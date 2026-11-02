#!/usr/bin/env bash
# Writes winget manifests for the app and the CLI of one release.
# usage: make-manifests.sh <version> <tag> <owner/repo> <SHA256SUMS.txt> <output dir>
# The result can be submitted with `wingetcreate submit <dir>` or as a PR to microsoft/winget-pkgs.
set -euo pipefail

version="$1"; tag="$2"; repo="$3"; sums="$4"; out="$5"
owner="${repo%%/*}"
base="https://github.com/$repo/releases/download/$tag"
schema="1.6.0"

hash_of() {
  local file="$1" line
  line="$(grep -E "^[0-9a-f]{64}  \*?$file\$" "$sums" || true)"
  [[ -n "$line" ]] || { echo "missing $file in $sums" >&2; exit 1; }
  echo "${line%% *}" | tr '[:lower:]' '[:upper:]'
}

write_package() {
  local id="$1" name="$2" description="$3" prefix="$4" exe="$5" alias="$6"
  local dir="$out/manifests/${owner:0:1}/${id//.//}/$version"
  mkdir -p "$dir"

  cat > "$dir/$id.yaml" <<YAML
PackageIdentifier: $id
PackageVersion: $version
DefaultLocale: en-US
ManifestType: version
ManifestVersion: $schema
YAML

  cat > "$dir/$id.locale.en-US.yaml" <<YAML
PackageIdentifier: $id
PackageVersion: $version
PackageLocale: en-US
Publisher: $owner
PublisherUrl: https://github.com/$owner
PackageName: $name
PackageUrl: https://github.com/$repo
License: MIT
LicenseUrl: https://github.com/$repo/blob/$tag/LICENSE
ShortDescription: $description
Tags:
  - steganography
  - encryption
  - privacy
ReleaseNotesUrl: https://github.com/$repo/releases/tag/$tag
ManifestType: defaultLocale
ManifestVersion: $schema
YAML

  {
    echo "PackageIdentifier: $id"
    echo "PackageVersion: $version"
    echo "InstallerType: zip"
    echo "NestedInstallerType: portable"
    echo "NestedInstallerFiles:"
    echo "  - RelativeFilePath: $prefix-$version-win-x64\\$exe"
    echo "    PortableCommandAlias: $alias"
    echo "Installers:"
    for arch in x64 arm64; do
      local file="$prefix-$version-win-$arch.zip"
      echo "  - Architecture: $arch"
      echo "    InstallerUrl: $base/$file"
      echo "    InstallerSha256: $(hash_of "$file")"
      if [[ "$arch" == arm64 ]]; then
        echo "    NestedInstallerFiles:"
        echo "      - RelativeFilePath: $prefix-$version-win-arm64\\$exe"
        echo "        PortableCommandAlias: $alias"
      fi
    done
    echo "ManifestType: installer"
    echo "ManifestVersion: $schema"
  } > "$dir/$id.installer.yaml"
}

write_package "$owner.SweetHide" "SweetHide" "Hide encrypted text and files inside images (Windows app)." "SweetHide" "SweetHide.exe" "sweethide-app"
write_package "$owner.SweetHide.CLI" "SweetHide CLI" "Hide encrypted text and files inside images (command line)." "sweethide" "sweethide.exe" "sweethide"
echo "winget manifests written to $out"
