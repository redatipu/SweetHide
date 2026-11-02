# การออก Release

ทุกขั้นตอนทำงานอัตโนมัติผ่าน GitHub Actions คนทำ release มีหน้าที่แค่ **push tag**

## ขั้นตอน

ทำได้สองทาง เลือกทางที่สะดวก

**ทางที่ 1: กดบนเว็บ GitHub** (ง่ายที่สุด)

1. ไปที่ `Releases` แล้วกด `Draft a new release`
2. ช่อง tag พิมพ์เวอร์ชันใหม่ เช่น `v0.2.0` แล้วเลือก "Create new tag on publish" ส่วน target เลือก `main`
3. จะเขียน release notes เองหรือปล่อยว่างไว้ก็ได้ ถ้าปล่อยว่างระบบจะใส่ข้อความมาตรฐานให้ แล้วกด `Publish release`
4. ประมาณ 6–8 นาที workflow **Release** จะ build ไฟล์ทั้งหมดแล้วแนบเข้า release นั้นให้เอง

**ทางที่ 2: push tag จากเครื่อง**

```bash
git tag -a v0.2.0 -m "SweetHide 0.2.0"
```

```bash
git push origin v0.2.0
```

ถ้าเป็นเวอร์ชันทดลองให้ใช้ tag ที่มีส่วนต่อท้าย เช่น `v0.3.0-beta.1` ระบบจะติดป้าย **Pre-release** ให้เอง ถ้าเผลอทำทั้งสองทางหรือกด re-run ซ้ำก็ไม่เป็นไร workflow จะแนบไฟล์ทับของเดิมให้ ไม่สร้าง release ซ้ำ

## Workflow ทำอะไรบ้าง

```
tag v1.2.0 ──► version  (ตรวจรูปแบบ tag แล้วแยกได้ 1.2.0)
          ──► test     (build + format check + test บน Windows และ Ubuntu)      .github/workflows/test.yml
          ──► package  (ไฟล์แจกทั้งหมด + smoke test ของแอป + SHA256SUMS.txt)   .github/workflows/package.yml
          ──► release  (ตรวจ checksum แล้วสร้าง GitHub Release พร้อมแนบไฟล์)   .github/workflows/release.yml
```

- `test.yml` และ `package.yml` เป็น workflow ที่เรียกใช้ซ้ำได้ CI ของทุก PR ก็รันชุดเดียวกันนี้ (โดยตั้ง `version: dev`) ไฟล์ที่ได้จาก release จึงผ่านการทดสอบมาก่อนตั้งแต่ขั้น PR
- เลขเวอร์ชันจาก tag ถูกฝังลงใน binary ผ่าน `-p:Version=…` และไปแสดงในหน้าตั้งค่า → เกี่ยวกับ
- ข้อความ release มาจาก `.github/RELEASE_NOTES.md` (มีวิธีดาวน์โหลดและวิธีตรวจ checksum) ต่อด้วยรายการ PR ที่ GitHub สร้างให้อัตโนมัติ

## ไฟล์ที่แนบใน release

| ไฟล์ | เนื้อหา |
|---|---|
| `SweetHide-<v>-win-x64.zip`, `-win-arm64.zip` | แอป WinUI 3 แบบ self-contained มีโฟลเดอร์บนสุดหนึ่งโฟลเดอร์ |
| `sweethide-<v>-win-x64.zip`, `-win-arm64.zip` | CLI ไฟล์เดียว (`sweethide.exe`) |
| `sweethide-<v>-linux-x64.tar.gz`, `-linux-arm64.tar.gz` | CLI ไฟล์เดียว (`sweethide`) ที่ตั้งสิทธิ์รันไว้แล้ว |
| `*.sbom.cdx.json` | SBOM (CycloneDX) ของแต่ละไฟล์ บอกว่าข้างในมีไลบรารีเวอร์ชันไหนบ้าง |
| `winget-manifests-<v>.zip` | manifest สำหรับส่งเข้า winget (ดูหัวข้อข้างล่าง) |
| `SHA256SUMS.txt` | checksum ของทุกไฟล์ข้างบน |

## ถ้าต้องการยกเลิก release

ลบ release และ tag ทิ้ง แล้วออกเวอร์ชันใหม่ด้วยเลขถัดไป ไม่ควรใช้เลขเดิมซ้ำ เพราะเพื่อนที่ดาวน์โหลดไปแล้วจะมี checksum ไม่ตรงกับไฟล์ใหม่

```bash
git push origin :refs/tags/v0.2.0
```

## ส่งเข้า winget

ทุก release มีไฟล์ `winget-manifests-<v>.zip` แนบไว้ ข้างในเป็น manifest ของสองแพ็กเกจ คือ `redatipu.SweetHide` (แอป เรียกจาก terminal ด้วย `sweethide-app`) และ `redatipu.SweetHide.CLI` (คำสั่ง `sweethide`) ใส่ hash จาก release นั้นไว้ครบแล้ว

ครั้งแรกต้องส่งเองหนึ่งครั้ง โดยแตก zip แล้วใช้ [wingetcreate](https://github.com/microsoft/winget-create)

```powershell
wingetcreate submit .\manifests\r\redatipu\SweetHide\0.2.0
```

หรือเปิด PR เข้า `microsoft/winget-pkgs` เองก็ได้ พอผ่าน review แล้ว เพื่อนติดตั้งได้ด้วยคำสั่งนี้

```powershell
winget install redatipu.SweetHide
```

## Provenance และ SBOM

- **SBOM** สร้างทุกครั้งด้วย Syft บอกว่าในไฟล์แจกมีไลบรารีอะไรบ้าง เอาไปสแกนหาช่องโหว่ต่อได้
- **Build provenance** (`actions/attest-build-provenance`) ใช้ได้ฟรีเฉพาะ repo ที่เป็น public ถ้า repo เป็น public จะเปิดใช้เองอัตโนมัติ คนที่โหลดไปตรวจได้ว่าไฟล์ถูก build จาก CI นี้จริง

```bash
gh attestation verify SweetHide-0.2.0-win-x64.zip --repo redatipu/SweetHide
```

## Code signing และ MSIX (ยังไม่ได้ทำ ต้องใช้เงิน)

ถ้าวันหนึ่งไม่อยากให้ SmartScreen เตือนแล้ว ทางที่ถูกที่สุดตอนนี้คือ **Azure Trusted Signing** (ประมาณ $10 ต่อเดือน) ขั้นตอนคือ:

1. สมัครแล้วยืนยันตัวตนกับ Microsoft
2. สร้าง certificate profile
3. ใส่ secret `AZURE_CLIENT_ID`, `AZURE_TENANT_ID` และ `AZURE_CLIENT_SECRET` ใน repo
4. เพิ่ม step `azure/trusted-signing-action` ใน `package.yml` ก่อนขั้น zip ให้เซ็น `SweetHide.exe` กับ `sweethide.exe`

ส่วน MSIX ยังไม่แนะนำ เพราะต้องเซ็นด้วย certificate ที่เครื่องปลายทางเชื่อถืออยู่แล้ว ถ้าใช้ self-signed เพื่อนต้อง import cert เข้าเครื่องเองซึ่งยุ่งกว่า zip มาก
