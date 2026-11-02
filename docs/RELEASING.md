# การออก Release

ทุกขั้นตอนทำงานอัตโนมัติผ่าน GitHub Actions คนทำ release มีหน้าที่แค่ **push tag**

## ขั้นตอน

1. merge งานเข้า `main` และดูให้ CI เขียวก่อน
2. ตั้ง tag เวอร์ชันตาม [SemVer](https://semver.org/lang/th/) จาก `main`:

```bash
git checkout main
```

```bash
git pull
```

```bash
git tag -a v0.2.0 -m "SweetHide 0.2.0"
```

```bash
git push origin v0.2.0
```

3. รอ workflow **Release** (ใช้เวลาประมาณ 6–8 นาที) แล้ว release จะขึ้นที่ `https://github.com/redatipu/SweetHide/releases`

ถ้าเป็นเวอร์ชันทดลองให้ใช้ tag ที่มีส่วนต่อท้าย เช่น `v0.3.0-beta.1` ระบบจะติดป้าย **Pre-release** ให้เอง

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
| `SHA256SUMS.txt` | checksum ของทุกไฟล์ข้างบน |

## ถ้าต้องการยกเลิก release

ลบ release และ tag ทิ้ง แล้วออกเวอร์ชันใหม่ด้วยเลขถัดไป ไม่ควรใช้เลขเดิมซ้ำ เพราะเพื่อนที่ดาวน์โหลดไปแล้วจะมี checksum ไม่ตรงกับไฟล์ใหม่

```bash
git push origin :refs/tags/v0.2.0
```

## ยังไม่ได้ทำ (เลือกเพิ่มได้)

- **Code signing:** ใช้ Azure Trusted Signing ประมาณ $10 ต่อเดือน ช่วยให้ SmartScreen ไม่เตือน แต่ไม่จำเป็นถ้าใช้กันเองในกลุ่มเพื่อน
- **Build provenance:** ใช้ `actions/attest-build-provenance` เพื่อพิสูจน์ว่าไฟล์ถูก build จาก CI นี้จริง ใช้ได้ฟรีสำหรับ public repo
- **winget manifest:** ช่วยให้ติดตั้งด้วย `winget install SweetHide` ได้
