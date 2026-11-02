# SweetHide Server (ใช้ผ่านเบราว์เซอร์)

เว็บเซิร์ฟเวอร์เล็ก ๆ ที่ทำให้ใช้ SweetHide จากเบราว์เซอร์ได้ ไม่ว่าจะเป็นมือถือ แท็บเล็ต หรือคอมเครื่องไหนในบ้าน ไม่ต้องลงแอปเพิ่ม เหมาะกับคนที่มี homelab อยู่แล้ว จะเป็น Raspberry Pi, VM บน Proxmox หรือ NAS ที่รัน Docker ได้ก็ใช้ได้หมด

ข้างในใช้ engine ตัวเดียวกับแอป Windows และ CLI รูปที่ซ่อนจากเว็บจึงเอาไปถอดในแอปได้ และกลับกันก็ได้เหมือนกัน

หน้าเว็บมี 3 แท็บ คือ **ซ่อน**, **ถอด** และ **วิเคราะห์** ลากรูปมาวางได้เลย หรือวางจากคลิปบอร์ดด้วย Ctrl+V เลือกรูปปกแล้วจะเห็นทันทีว่าจุข้อมูลได้เท่าไร มีทั้งภาษาไทยและอังกฤษ (เลือกตามภาษาของเบราว์เซอร์ก่อน แล้วสลับเองได้) เปิดมาเป็นธีมมืด สลับเป็นธีมสว่างได้ และใช้บนมือถือได้สบาย

> ⚠️ **อ่านก่อนเปิดใช้:** เวลาซ่อนหรือถอด รหัสผ่าน ข้อความ และไฟล์ทั้งหมดจะถูกส่งไปที่เซิร์ฟเวอร์ ไม่ได้เข้ารหัสอยู่ในเบราว์เซอร์ เพราะฉะนั้นให้ใช้กับเซิร์ฟเวอร์ที่เราเป็นคนดูแลเอง ในเครือข่ายที่ไว้ใจได้เท่านั้น ถ้าต้องผ่าน Wi-Fi สาธารณะหรืออินเทอร์เน็ต ต้องใช้ HTTPS (ดูหัวข้อ [HTTPS ด้วย reverse proxy](#https-ด้วย-reverse-proxy)) และอย่า forward port ออกอินเทอร์เน็ตตรง ๆ ถ้าอยากใช้จากนอกบ้านให้เข้าผ่าน VPN อย่าง WireGuard หรือ Tailscale จะปลอดภัยกว่า

## เริ่มใช้งานด้วย Docker

```bash
docker run -d --name sweethide --restart unless-stopped \
  -p 8080:8080 \
  --read-only --cap-drop ALL --security-opt no-new-privileges \
  ghcr.io/redatipu/sweethide:latest
```

แล้วเปิด `http://<IP ของเครื่อง>:8080` จากเครื่องไหนในบ้านก็ได้

- image มีทั้ง `linux/amd64` และ `linux/arm64` Docker จะเลือกให้ตรงกับเครื่องเอง Raspberry Pi 3, 4, 5 ใช้ได้ แต่ต้องเป็น OS แบบ 64 บิต
- tag `latest` คือ release ล่าสุด ถ้าอยากล็อกเวอร์ชันให้ใช้เลขเวอร์ชันแทน เช่น `ghcr.io/redatipu/sweethide:0.2.0` (tag ของ pre-release เช่น `0.3.0-beta.1` จะไม่ไปทับ `latest`)
- ตัวเลือก `--read-only --cap-drop ALL --security-opt no-new-privileges` ใส่ได้เลยไม่มีผลข้างเคียง เพราะเซิร์ฟเวอร์ไม่เขียนอะไรลงดิสก์ และรันเป็น user ธรรมดา (UID 1654) อยู่แล้ว
- ถ้า repo บน GitHub เป็น private, image บน ghcr.io ก็จะเป็น private ตามไปด้วย เพื่อนต้อง `docker login ghcr.io` ด้วย token ที่มีสิทธิ์ `read:packages` ก่อน หรือเจ้าของ repo ไปเปลี่ยน package ให้เป็น public ในหน้า Packages ของ GitHub

### ล็อกด้วย access token

ถ้าในบ้านมีคนอื่นใช้เน็ตด้วย หรือเปิดให้เพื่อนเข้ามาใช้ ควรตั้ง token ไว้

```bash
docker run -d --name sweethide --restart unless-stopped \
  -p 8080:8080 \
  --read-only --cap-drop ALL --security-opt no-new-privileges \
  -e SWEETHIDE__ACCESSTOKEN="$(openssl rand -base64 24)" \
  ghcr.io/redatipu/sweethide:latest
```

```bash
docker inspect sweethide --format '{{range .Config.Env}}{{println .}}{{end}}' | grep ACCESSTOKEN
```

คำสั่งที่สองเอาไว้ดู token ที่สุ่มได้ เปิดเว็บแล้วเบราว์เซอร์จะถามชื่อผู้ใช้กับรหัสผ่าน ชื่อผู้ใช้ใส่อะไรก็ได้ ส่วนรหัสผ่านคือ token นี้ ถ้าเรียกจากสคริปต์ให้ส่ง header `Authorization: Bearer <token>` หรือใช้ `curl -u x:<token>` ก็ได้ ส่วน `/healthz` เปิดไว้ให้ health check เสมอ

token นี้แค่กันคนอื่นใน LAN ไม่ให้ใช้เซิร์ฟเวอร์ ไม่ได้ช่วยเรื่องการดักฟัง ถ้ายังเป็น HTTP ธรรมดา token ก็วิ่งผ่านเครือข่ายแบบไม่เข้ารหัสเหมือนรหัสผ่านนั่นแหละ

### docker compose

ใน repo มี [`docker-compose.yml`](../docker-compose.yml) ที่ตั้งค่าความปลอดภัยไว้ครบแล้ว (read-only, ตัด capability ทิ้งหมด, จำกัดหน่วยความจำและจำนวน process) ถ้าจะใช้ token ให้สร้างไฟล์ `.env` ไว้ข้าง ๆ

```bash
echo "SWEETHIDE_ACCESSTOKEN=$(openssl rand -base64 24)" > .env
```

```bash
docker compose up -d
```

```bash
docker compose logs -f sweethide
```

### build image เอง

```bash
docker build -t sweethide .
```

```bash
docker buildx build --platform linux/amd64,linux/arm64 -t ghcr.io/<ชื่อคุณ>/sweethide:dev --push .
```

ขั้น build จะรันบนสถาปัตยกรรมของเครื่องที่ build แล้ว cross-compile ไปอีกแบบ จึง build image arm64 บนเครื่อง x64 ได้โดยไม่ต้องลง QEMU ส่วน image สุดท้ายเป็น ASP.NET runtime แบบ chiseled (distroless ของ Ubuntu) ไม่มี shell ไม่มี package manager และเป็นรุ่น `-extra` ที่มี ICU มาด้วย ตรงนี้สำคัญ เพราะรหัสผ่านต้องถูก normalize เป็น Unicode NFC ก่อนสร้างกุญแจ (ดู [FORMAT.md](FORMAT.md)) ถ้าไม่มี ICU รหัสผ่านภาษาไทยบางแบบจะเปิดในแอปอื่นไม่ได้ เซิร์ฟเวอร์จึงไม่ยอมเริ่มทำงานถ้าเจอกรณีนี้

### ไม่ใช้ Docker

ต้องมี .NET 10 SDK ตอน build ส่วนเครื่องที่รันไม่ต้องลง .NET แต่ต้องมี ICU (`libicu`) ซึ่ง Raspberry Pi OS และ Ubuntu ส่วนใหญ่มีอยู่แล้ว

```bash
dotnet publish src/SweetHide.Server -c Release -r linux-arm64 --self-contained -o publish/server
```

```bash
cd publish/server && ./SweetHide.Server
```

เปลี่ยน `linux-arm64` เป็น `linux-x64` ได้ตามเครื่อง ค่าเริ่มต้นจะฟังที่พอร์ต 8080 ทุก interface ถ้าจะเปลี่ยนให้ใส่ `--urls http://0.0.0.0:9000` หรือตั้ง `ASPNETCORE_HTTP_PORTS=9000` การตั้งค่าอื่นแก้ใน `appsettings.json` ข้างไฟล์โปรแกรมได้ หรือจะใช้ตัวแปรแวดล้อมแบบเดียวกับ Docker ก็ได้ ถ้าจะให้ systemd รันให้ อย่าลืมตั้ง `WorkingDirectory` เป็นโฟลเดอร์นั้น ไม่อย่างนั้นโปรแกรมจะหา `appsettings.json` ไม่เจอ

## ตั้งค่า

ตั้งผ่านตัวแปรแวดล้อม (หรือ section `SweetHide` ใน `appsettings.json`) ไม่ต้องตั้งอะไรเลยก็ใช้ได้

| ตัวแปร | ค่าเริ่มต้น | ความหมาย |
|---|---|---|
| `SWEETHIDE__ACCESSTOKEN` | ว่าง (ไม่ล็อก) | token ที่ต้องใช้ทุกครั้ง ยกเว้น `/healthz` ควรยาวอย่างน้อย 16 ตัวอักษรและสุ่มขึ้นมา |
| `SWEETHIDE__MAXREQUESTBYTES` | `67108864` (64 MiB) | ขนาด request ใหญ่สุด รวมรูปปกและไฟล์แนบทั้งหมด ตั้งได้ตั้งแต่ 1 KiB ถึง 1 GiB |
| `SWEETHIDE__MAXIMAGEPIXELS` | `67108864` (64 ล้านพิกเซล) | รูปที่ใหญ่กว่านี้จะถูกปฏิเสธ กันรูปที่ตั้งใจทำให้หน่วยความจำระเบิด |
| `SWEETHIDE__MAXCONCURRENTJOBS` | `2` | จำนวนงาน (ถอดรหัสรูป, Argon2, ฝังข้อมูล) ที่ทำพร้อมกันได้ |
| `SWEETHIDE__MAXQUEUEDJOBS` | `16` | คำขอที่รอคิวได้ รวมที่กำลังอัปโหลด ถ้าเกินจะได้ 503 |
| `SWEETHIDE__RATELIMITPERMINUTE` | `120` | จำนวนคำขอต่อนาทีต่อ IP (ไม่นับ `/healthz`) ถ้าเกินจะได้ 429 |
| `SWEETHIDE__REQUESTTIMEOUTSECONDS` | `120` | เวลาสูงสุดของหนึ่งคำขอ นับรวมเวลาอัปโหลดและรอคิว |
| `SWEETHIDE__TRUSTEDPROXIES__0`, `__1`, ... | ว่าง | IP หรือช่วง CIDR ของ reverse proxy ที่เชื่อ `X-Forwarded-For` ได้ (ดูหัวข้อถัดไป) |
| `ASPNETCORE_HTTP_PORTS` | `8080` | พอร์ตที่ฟัง |
| `AllowedHosts` | `*` | ชื่อโฮสต์ที่ยอมรับ คั่นด้วย `;` เช่น `sweethide.home.lan;192.168.1.20` ช่วยกันการโจมตีแบบ DNS rebinding |

**เรื่องหน่วยความจำ:** ตอนทำงาน แต่ละงานใช้หน่วยความจำประมาณ 30 ไบต์ต่อพิกเซล (วัดจากรูป 16 ล้านพิกเซลได้ราว 0.5 GiB) รูปจากมือถือ 12 ล้านพิกเซลจึงใช้ราว 0.4 GiB ส่วนรูปใหญ่สุดที่ค่าเริ่มต้นยอมรับ (64 ล้านพิกเซล) ใช้ราว 2 GiB ถ้าเป็น Pi ที่มี RAM 2 GB แนะนำให้ตั้ง `SWEETHIDE__MAXIMAGEPIXELS=24000000` กับ `SWEETHIDE__MAXCONCURRENTJOBS=1` ถ้าหน่วยความจำไม่พอจริง ๆ เซิร์ฟเวอร์จะตอบ 503 พร้อมบอกให้ลองรูปที่เล็กลง แต่ตัวเซิร์ฟเวอร์ไม่ล้ม

## HTTPS ด้วย reverse proxy

ตัวเซิร์ฟเวอร์พูด HTTP อย่างเดียว ถ้าต้องการ HTTPS ให้วาง reverse proxy ไว้ข้างหน้า มีเรื่องที่ต้องตั้งให้ถูกอยู่ 3 อย่าง

1. **ให้ proxy รับไฟล์ใหญ่ได้** อย่างน้อยเท่ากับ `MAXREQUESTBYTES` และ timeout นานพอสำหรับการอัปโหลดและ Argon2
2. **ตั้ง `TRUSTEDPROXIES`** ให้เป็น IP ของ proxy ไม่อย่างนั้นเซิร์ฟเวอร์จะเห็นทุกคนมาจาก IP เดียวกัน (IP ของ proxy) แล้ว rate limit จะนับรวมกันหมด ถ้า proxy อยู่ใน Docker network เดียวกันและไม่รู้ IP แน่ ๆ ใส่ช่วง `172.16.0.0/12` ไปได้
3. **อย่าเปิดพอร์ต 8080 ให้คนอื่นเข้าตรง ๆ** เมื่อเชื่อ `X-Forwarded-For` แล้ว ใครที่เข้าพอร์ตนี้ตรง ๆ ได้ก็ปลอม IP หลบ rate limit ได้ ถ้า proxy อยู่บนเครื่องเดียวกันให้ผูกพอร์ตไว้ที่ localhost เช่น `-p 127.0.0.1:8080:8080` หรือไม่ต้อง publish พอร์ตเลยถ้าอยู่ใน Docker network เดียวกัน

ถ้า proxy ส่ง header `Strict-Transport-Security` (HSTS) ได้ก็ควรเปิดไว้ ส่วน header ความปลอดภัยอื่น ๆ (CSP, nosniff, ห้ามฝังใน iframe ฯลฯ) เซิร์ฟเวอร์ใส่มาให้ครบแล้ว

### Caddy

Caddy ขอ certificate ให้เองถ้าเป็นโดเมนจริง ถ้าเป็นชื่อในบ้าน (เช่น `.lan`) ให้ใส่ `tls internal` แล้วติดตั้ง root CA ของ Caddy ในเครื่องที่ใช้งาน

```caddyfile
sweethide.home.example.com {
    reverse_proxy sweethide:8080
}
```

Caddy ไม่จำกัดขนาด request และส่ง `X-Forwarded-For` ให้อยู่แล้ว ตั้งแค่ `SWEETHIDE__TRUSTEDPROXIES__0` เป็น IP ของ Caddy ก็พอ

### Traefik

```yaml
services:
  sweethide:
    image: ghcr.io/redatipu/sweethide:latest
    restart: unless-stopped
    read_only: true
    cap_drop: [ALL]
    security_opt: ["no-new-privileges:true"]
    environment:
      SWEETHIDE__ACCESSTOKEN: "${SWEETHIDE_ACCESSTOKEN:-}"
      SWEETHIDE__TRUSTEDPROXIES__0: "172.16.0.0/12"
    labels:
      - traefik.enable=true
      - traefik.http.routers.sweethide.rule=Host(`sweethide.home.example.com`)
      - traefik.http.routers.sweethide.entrypoints=websecure
      - traefik.http.routers.sweethide.tls.certresolver=letsencrypt
      - traefik.http.services.sweethide.loadbalancer.server.port=8080
```

ตัวอย่างนี้ไม่ได้ publish พอร์ต 8080 ออกมา ทุกอย่างวิ่งผ่าน Traefik อย่างเดียว ส่วน `websecure` กับ `letsencrypt` ให้เปลี่ยนเป็นชื่อ entrypoint และ certificate resolver ที่ตั้งไว้ใน Traefik ของคุณ

### Nginx

```nginx
server {
    listen 443 ssl;
    http2 on;
    server_name sweethide.home.example.com;

    ssl_certificate     /etc/ssl/sweethide/fullchain.pem;
    ssl_certificate_key /etc/ssl/sweethide/privkey.pem;
    add_header Strict-Transport-Security "max-age=31536000" always;

    client_max_body_size 64m;
    proxy_request_buffering off;
    proxy_read_timeout 180s;
    proxy_send_timeout 180s;

    location / {
        proxy_pass http://127.0.0.1:8080;
        proxy_set_header Host $host;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
    }
}
```

`proxy_request_buffering off` สำคัญ เพราะปกติ Nginx จะพักไฟล์ที่อัปโหลดขนาดใหญ่ลงไฟล์ชั่วคราวในดิสก์ก่อน ซึ่งขัดกับความตั้งใจที่จะไม่ให้ข้อมูลลับแตะดิสก์ ถ้ารัน SweetHide ด้วย `-p 127.0.0.1:8080:8080` คำขอจาก Nginx บนเครื่องเดียวกันจะเข้ามาจาก gateway ของ Docker bridge (ปกติคือ `172.17.0.1`) ให้ใส่ค่านี้ใน `SWEETHIDE__TRUSTEDPROXIES__0`

## API สำหรับสคริปต์

ทุก endpoint รับ `multipart/form-data` เหมือนที่หน้าเว็บใช้

| Endpoint | ฟิลด์ | ผลลัพธ์ |
|---|---|---|
| `POST /api/hide` | `cover` (รูป), `text` (ซ้ำได้), `file` (ซ้ำได้), `password`, `bits` (1–4, ค่าเริ่มต้น 1) | ไฟล์ `image/png` ชื่อตามรูปปก เช่น `beach.jpg` → `beach.png` |
| `POST /api/reveal` | `image`, `password` | JSON ที่มี `messages` และ `files` (ชื่อไฟล์ที่ทำความสะอาดแล้ว + เนื้อไฟล์เป็น base64) |
| `POST /api/reveal?format=zip` | `image`, `password` | zip ของไฟล์ทั้งหมด และ `messages.txt` ถ้ามีข้อความ |
| `POST /api/capacity` | `image` | ขนาดรูปและความจุของแต่ละระดับบิต |
| `POST /api/analyze` | `image`, `bitPlane` (0–7, ไม่ใส่ก็ได้) | สถิติ LSB, chi-square ของแต่ละ channel และภาพ bit plane เป็น PNG แบบ base64 ถ้าขอ |
| `GET /healthz` | | `Healthy` (ตรวจด้วยว่า SkiaSharp โหลดได้จริง) |

```bash
curl -F cover=@photo.jpg -F 'text=พบกันที่ร้านเดิม 2 ทุ่ม' -F file=@plan.pdf -F 'password=<-' -o secret.png http://pi.lan:8080/api/hide < password.txt
```

```bash
curl -F image=@secret.png -F 'password=<-' http://pi.lan:8080/api/reveal < password.txt | jq
```

```bash
curl -F image=@secret.png -F 'password=<-' -o files.zip 'http://pi.lan:8080/api/reveal?format=zip' < password.txt
```

```bash
curl -F image=@photo.jpg http://pi.lan:8080/api/capacity
```

`-F 'password=<-'` ให้ curl อ่านรหัสผ่านจาก stdin (ในตัวอย่างคือไฟล์ `password.txt` ที่ไม่มีขึ้นบรรทัดใหม่ต่อท้าย) รหัสผ่านจะได้ไม่ค้างใน shell history และไม่โผล่ในรายการ process ถ้าจะพิมพ์ข้อความที่ขึ้นต้นด้วย `@` หรือ `<` ให้ใช้ `--form-string` แทน `-F` ไม่อย่างนั้น curl จะคิดว่าเป็นชื่อไฟล์

ผลของ `/api/reveal` หน้าตาประมาณนี้

```json
{
  "messages": ["พบกันที่ร้านเดิม 2 ทุ่ม"],
  "files": [{ "name": "plan.pdf", "size": 48213, "data": "JVBERi0xLjcK..." }]
}
```

### error

ทุก error ตอบเป็น `application/problem+json` ไม่มี stack trace และมีฟิลด์ `code` ไว้ให้สคริปต์เช็ก

| Status | `code` | ความหมาย |
|---|---|---|
| 400 | `missing-field`, `invalid-field`, `nothing-to-hide`, `bad-request` | ฟิลด์ขาดหรือผิด (มี `field` บอกว่าเป็นฟิลด์ไหน) |
| 401 | `unauthorized` | ต้องใช้ access token |
| 403 | `cross-site-request` | เบราว์เซอร์บอกว่าคำขอมาจากเว็บอื่น |
| 413 | `request-too-large` | ใหญ่เกิน `MAXREQUESTBYTES` |
| 415 | `unsupported-media-type` | ไม่ได้ส่งเป็น `multipart/form-data` |
| 422 | `not-found-or-wrong-password` | ไม่พบข้อมูล **หรือ** รหัสผ่านผิด ข้อความเหมือนกันทุกครั้ง แยกไม่ได้โดยตั้งใจ |
| 422 | `corrupted` | รหัสผ่านถูก แต่ข้อมูลเสียหาย (รูปถูกแก้หรือบีบอัดใหม่) |
| 422 | `insufficient-capacity` | รูปเล็กเกินไป มี `neededBytes`, `capacityBytes` และ `suggestedBitsPerChannel` ถ้าเพิ่มบิตแล้วใส่ได้ |
| 422 | `invalid-image` | อ่านรูปไม่ได้ หรือพิกเซลเกิน `MAXIMAGEPIXELS` |
| 429 | `rate-limited` | ถี่เกินไป ดู header `Retry-After` |
| 503 | `busy`, `out-of-memory` | คิวเต็ม หรือหน่วยความจำไม่พอสำหรับรูปนี้ |
| 504 | `timeout` | เกิน `REQUESTTIMEOUTSECONDS` |

## ความปลอดภัย

- **ไม่มีอะไรถูกเก็บ** รูป ข้อความ ไฟล์ และรหัสผ่านอยู่ในหน่วยความจำแค่ระหว่างที่ทำงาน ไม่มีฐานข้อมูล ไม่มีไฟล์ชั่วคราว (ตั้งให้ ASP.NET พักไฟล์อัปโหลดไว้ในหน่วยความจำทั้งหมด) และไม่ log รหัสผ่านหรือเนื้อหาใด ๆ log มีแค่ตอนเริ่มทำงานกับ error ที่ไม่คาดคิด ถ้าเครื่องมี swap ข้อมูลในหน่วยความจำก็อาจถูกเขียนลง swap ได้ ถ้าซีเรียสเรื่องนี้ให้ปิด swap หรือใช้ swap ที่เข้ารหัส
- **รหัสผ่านไปถึงเซิร์ฟเวอร์** ใครที่คุมเซิร์ฟเวอร์ได้ก็เห็นทุกอย่างได้ ให้ใช้กับเซิร์ฟเวอร์ที่ตัวเองหรือเพื่อนที่ไว้ใจดูแลเท่านั้น และถ้าไม่ใช่ LAN ของตัวเองต้องใช้ HTTPS
- **รหัสผ่านอยู่ใน body เท่านั้น** ไม่เคยอยู่ใน URL จึงไม่ไปโผล่ใน access log ของ reverse proxy ส่วน response ของ `/api` มี `Cache-Control: no-store` เบราว์เซอร์และ proxy จะไม่เก็บไว้
- **ป้องกันการใช้งานเกินตัว** มีทั้งจำกัดขนาด request จำกัดจำนวนพิกเซล rate limit ต่อ IP จำกัดงาน Argon2 ที่ทำพร้อมกัน และ timeout
- **หน้าเว็บ** ไม่โหลดอะไรจากข้างนอกเลย ไม่มี CDN ไม่มีฟอนต์จากเว็บอื่น ไม่มี analytics ใช้ Content Security Policy แบบเข้มที่ไม่อนุญาต inline script/style ห้ามฝังหน้าเว็บใน iframe และไม่ส่ง Referer
- **API กันเว็บอื่นเรียก** คำขอที่เบราว์เซอร์ระบุว่ามาจากเว็บอื่น (`Sec-Fetch-Site: cross-site`) จะถูกปฏิเสธ เว็บแปลก ๆ ที่เปิดอยู่จึงสั่งให้เซิร์ฟเวอร์ในบ้านทำงานแทนไม่ได้ ถ้าอยากกันเพิ่มให้ตั้ง `AllowedHosts`
- **ไฟล์ที่ถอดออกมา** ชื่อไฟล์ถูกทำความสะอาด (ตัด path ทิ้ง กันชื่ออุปกรณ์ของ Windows) และหน้าเว็บไม่เคยเปิดไฟล์ให้เอง มีแค่ปุ่มดาวน์โหลด
- **ส่งรูปเป็นไฟล์เสมอ** เหมือนแอปอื่น ๆ ถ้าส่งผ่าน LINE หรือ Messenger แบบรูปภาพ ข้อมูลจะหาย บนมือถือให้แชร์เป็นไฟล์หรือเอกสาร

## แก้ปัญหา

| อาการ | วิธีแก้ |
|---|---|
| อัปโหลดรูปใหญ่แล้วได้ 413 | เพิ่ม `SWEETHIDE__MAXREQUESTBYTES` และขนาด request ที่ reverse proxy รับได้ (เช่น `client_max_body_size` ของ Nginx) |
| ได้ 503 บ่อย ๆ ตอนหลายคนใช้พร้อมกัน | ถ้า RAM เหลือ เพิ่ม `SWEETHIDE__MAXCONCURRENTJOBS` |
| container ปิดตัวพร้อมข้อความ `Unicode normalization is not available` | ใช้ base image ที่ไม่มี ICU อยู่ ให้ใช้ image ของโปรเจกต์ หรือ base `aspnet:10.0-noble-chiseled-extra` |
| rate limit ทำงานเหมือนทุกคนเป็นคนเดียวกัน | ยังไม่ได้ตั้ง `SWEETHIDE__TRUSTEDPROXIES__0` ให้ตรงกับ IP ของ reverse proxy |
| Raspberry Pi ดึง image ไม่ได้ (`no matching manifest`) | ใช้ Raspberry Pi OS แบบ 64 บิต image ไม่มีรุ่น 32 บิต |
| คัดลอกข้อความในหน้าถอดไม่ได้บนมือถือ | เบราว์เซอร์บางตัวยอมให้ใช้คลิปบอร์ดเฉพาะ HTTPS ถ้าปุ่มคัดลอกไม่ทำงาน ให้กดค้างที่ข้อความแล้วคัดลอกเอง |
