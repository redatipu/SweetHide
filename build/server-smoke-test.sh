#!/usr/bin/env bash
# Smoke test for a running SweetHide server (for example the Docker image): health check, UI, a hide -> reveal round
# trip through the HTTP API, the zip download and a wrong password. Needs curl, jq and python3.
#
#   build/server-smoke-test.sh [base-url]          default http://127.0.0.1:8080
#   SWEETHIDE_TOKEN=... build/server-smoke-test.sh  when the server has an access token
set -euo pipefail

base="${1:-http://127.0.0.1:8080}"
token="${SWEETHIDE_TOKEN:-}"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

auth=()
if [[ -n "$token" ]]; then
  auth=(-H "Authorization: Bearer $token")
fi

fail() {
  echo "FAIL: $*" >&2
  exit 1
}

echo "Waiting for $base/healthz"
for _ in $(seq 1 60); do
  if curl -fsS -o /dev/null "$base/healthz" 2>/dev/null; then
    break
  fi
  sleep 1
done
[[ "$(curl -fsS "$base/healthz")" == "Healthy" ]] || fail "health check is not Healthy"
echo "ok - health check"

if [[ -n "$token" ]]; then
  status="$(curl -sS -o /dev/null -w '%{http_code}' "$base/")"
  [[ "$status" == 401 ]] || fail "expected 401 without the token, got $status"
  echo "ok - token required"
fi

# Responses are captured before grepping: grep -q stops reading early, which would break the pipe under pipefail.
page="$(curl -fsS "${auth[@]}" -H 'Accept-Language: th-TH,th;q=0.9' "$base/")"
grep -q '<html lang="th"' <<<"$page" || fail "UI not served in Thai"
headers="$(curl -fsS "${auth[@]}" -D - -o /dev/null "$base/app.js")"
grep -qi "^content-security-policy: default-src 'none'" <<<"$headers" || fail "no CSP header"
grep -qi '^x-content-type-options: nosniff' <<<"$headers" || fail "no nosniff header"
echo "ok - UI and security headers"

# A 256x192 noisy gradient, written as PNG with nothing but the Python standard library.
python3 - "$work/cover.png" <<'PY'
import random, struct, sys, zlib
width, height, rng = 256, 192, random.Random(1)
rows = b"".join(
    b"\0" + b"".join(bytes(((x + rng.randint(-6, 6)) & 255, (y + rng.randint(-6, 6)) & 255, (128 + rng.randint(-40, 40)) & 255))
                     for x in range(width))
    for y in range(height))
def chunk(kind, data):
    return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data))
with open(sys.argv[1], "wb") as f:
    f.write(b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 2, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(rows, 9)) + chunk(b"IEND", b""))
PY

message='ทดสอบ smoke test 🍬'
password='smoke test password ไทย'
printf 'attached file\nบรรทัดที่สอง\n' > "$work/secret.txt"

status="$(curl -sS "${auth[@]}" -o "$work/stego.png" -D "$work/hide.headers" -w '%{http_code}' \
  -F "cover=@$work/cover.png;filename=holiday.png" \
  --form-string "text=$message" \
  -F "file=@$work/secret.txt" \
  --form-string "password=$password" \
  "$base/api/hide")"
[[ "$status" == 200 ]] || fail "hide returned $status: $(cat "$work/stego.png")"
[[ "$(head -c 8 "$work/stego.png" | od -An -tx1 | tr -d ' \n')" == "89504e470d0a1a0a" ]] || fail "hide did not return a PNG"
grep -qi 'filename=holiday.png' "$work/hide.headers" || fail "download not named after the cover"
echo "ok - hide ($(wc -c <"$work/stego.png") bytes)"

curl -fsS "${auth[@]}" -F "image=@$work/stego.png" --form-string "password=$password" "$base/api/reveal" >"$work/reveal.json"
jq -e --arg m "$message" '.messages == [$m]' "$work/reveal.json" >/dev/null || fail "message did not round-trip: $(cat "$work/reveal.json")"
[[ "$(jq -r '.files[0].name' "$work/reveal.json")" == "secret.txt" ]] || fail "file name did not round-trip"
jq -r '.files[0].data' "$work/reveal.json" | base64 -d | cmp -s - "$work/secret.txt" || fail "file content did not round-trip"
echo "ok - reveal"

curl -fsS "${auth[@]}" -o "$work/revealed.zip" -F "image=@$work/stego.png" --form-string "password=$password" "$base/api/reveal?format=zip"
listing="$(python3 -m zipfile -l "$work/revealed.zip")"
grep -q 'secret.txt' <<<"$listing" || fail "zip has no secret.txt"
grep -q 'messages.txt' <<<"$listing" || fail "zip has no messages.txt"
echo "ok - reveal as zip"

status="$(curl -sS "${auth[@]}" -o "$work/wrong.json" -w '%{http_code}' -F "image=@$work/stego.png" --form-string "password=not it" "$base/api/reveal")"
[[ "$status" == 422 ]] || fail "wrong password returned $status"
jq -e '.code == "not-found-or-wrong-password"' "$work/wrong.json" >/dev/null || fail "unexpected problem: $(cat "$work/wrong.json")"
echo "ok - wrong password is 422"

echo "Smoke test passed"
