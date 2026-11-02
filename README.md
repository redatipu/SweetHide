# SweetHide

โปรแกรมเล็ก ๆ สำหรับซ่อนข้อความและไฟล์ไว้ในรูปภาพ ใส่รหัสผ่านไว้ด้วย รูปที่ได้ออกมาเป็น PNG ที่ดูด้วยตาแล้วเหมือนรูปเดิมทุกอย่าง
ทำไว้ใช้เองกับเพื่อน ๆ เอาไว้ลองของใน homelab กัน

ข้อมูลที่ซ่อนจะถูกเข้ารหัสด้วย AES-256-GCM โดยใช้กุญแจที่สร้างจากรหัสผ่านผ่าน Argon2id แล้วกระจายลงในบิตต่ำสุดของพิกเซลตามลำดับที่สุ่มจากรหัสผ่าน ไม่มีส่วนไหนของรูปที่บอกได้ว่ามีอะไรซ่อนอยู่ ถ้าอยากรู้รายละเอียดดูได้ที่ [docs/FORMAT.md](docs/FORMAT.md)

มีให้ใช้สองแบบ คือแอปบน Windows 10/11 และ CLI ที่ใช้ได้ทั้ง Windows และ Linux

> ⚠️ ส่งรูปที่ซ่อนข้อมูลแล้วเป็น **ไฟล์** เสมอ เพราะ LINE, Messenger และแอปแชตอื่น ๆ จะบีบอัดรูปใหม่ และข้อมูลจะหายหมด

## ดาวน์โหลด

โหลดได้จากหน้า [Releases](https://github.com/redatipu/SweetHide/releases) แตกไฟล์แล้วใช้ได้เลยโดยไม่ต้องติดตั้งอะไร ถ้าจะลองเวอร์ชันที่ยังพัฒนาอยู่ ให้โหลด artifact `release-assets` จาก CI ของ PR

ไฟล์ยังไม่ได้เซ็นโค้ด เปิดครั้งแรก Windows จะเตือน ให้เช็ก checksum กับ `SHA256SUMS.txt` ก่อน ถ้าตรงกันค่อยกด More info แล้ว Run anyway

```powershell
Get-FileHash .\SweetHide-0.1.0-win-x64.zip
```

## แอป Windows (GUI)

เปิดมาจะเป็นธีมมืด ถ้าอยากได้ธีมสว่างหรือให้ตาม Windows ไปเปลี่ยนได้ในหน้าตั้งค่า

| หน้า | ใช้ทำอะไร |
|---|---|
| ซ่อน | เลือกหรือลากรูปปกมาวาง พิมพ์ข้อความ แนบไฟล์ ดูแถบความจุ ใส่รหัสผ่าน 2 ครั้ง แล้วกด **ซ่อนและบันทึก…** |
| ถอด | เลือกรูป ใส่รหัสผ่าน แล้วกด Enter จะเห็นข้อความ (มีปุ่มคัดลอก) และไฟล์ (บันทึกทีละไฟล์หรือทั้งหมดก็ได้) |
| วิเคราะห์ | ดูสถิติ LSB, chi-square และภาพ bit plane ของรูปใดก็ได้ |
| ตั้งค่า | เลือกภาษา (ไทย/อังกฤษ/ตาม Windows) และธีม (สว่าง/มืด/ตาม Windows) |

build เองบน Windows (ต้องมี .NET 10 SDK):

```bash
dotnet publish src/SweetHide.App -c Release -r win-x64 -p:Platform=x64 -o publish/app
```

## CLI

ดาวน์โหลด `sweethide` / `sweethide.exe` ได้จาก artifact ของ GitHub Actions หรือ build เองด้วยคำสั่งนี้ (ต้องมี .NET 10 SDK):

```bash
dotnet publish src/SweetHide.Cli -c Release -r win-x64 -o publish
```

เปลี่ยน `win-x64` เป็น `linux-x64` หรือ `linux-arm64` ได้ตามเครื่อง ผลลัพธ์เป็นไฟล์เดียวที่รันได้ทันทีโดยไม่ต้องติดตั้ง .NET

### ซ่อน

```bash
sweethide hide -i photo.jpg -o secret.png -t "พบกันที่ร้านเดิม 2 ทุ่ม" -f plan.pdf -f keys.txt
```

- `-t` ใส่ข้อความตรง ๆ, `--text-file` อ่านข้อความจากไฟล์ UTF-8, `-f` แนบไฟล์ (ทุก option ใช้ซ้ำได้)
- `-b 2` ใช้ 2 บิตต่อ channel ได้พื้นที่เพิ่มเป็น 2 เท่าและยังมองไม่เห็นด้วยตา ส่วน 3–4 บิตอาจเห็น noise
- ไฟล์ output ต้องเป็น `.png` เท่านั้น ส่วน input จะเป็น JPEG, PNG, BMP, GIF หรือ WebP ก็ได้

### ถอด

```bash
sweethide reveal -i secret.png -o ./revealed
```

ข้อความจะพิมพ์ออกทาง stdout ส่วนไฟล์จะถูกเขียนลง `./revealed` โดยไม่ทับไฟล์เดิม (ถ้าชื่อซ้ำจะตั้งชื่อใหม่เป็น `name (1).ext`) ใช้ `--list` ถ้าต้องการดูรายการอย่างเดียวโดยไม่เขียนไฟล์

### รหัสผ่าน

ถ้าไม่ระบุอะไร โปรแกรมจะถามรหัสผ่านแบบซ่อนตัวอักษร เวลาซ่อนจะถามสองครั้ง ถ้าใช้ในสคริปต์ให้ส่งรหัสผ่านทางใดทางหนึ่งต่อไปนี้:

```bash
export SWEETHIDE_PASSWORD='...'
```

```bash
echo '...' | sweethide reveal -i secret.png --password-stdin
```

ไม่มี option `--password` เพราะรหัสผ่านจะไปค้างใน shell history และโผล่ในรายการ process

### เครื่องมืออื่น

```bash
sweethide capacity -i photo.jpg
```

```bash
sweethide analyze -i suspect.png --bit-plane plane.png
```

`capacity` บอกว่ารูปนี้จุข้อมูลได้เท่าไรในแต่ละระดับบิต ส่วน `analyze` ทำ steganalysis คือคำนวณสถิติ LSB, chi-square attack และสร้างภาพ bit plane เอาไว้ลองตรวจรูปที่เพื่อนส่งมา

### ใช้ในสคริปต์และ homelab

ทุกคำสั่งใส่ `-i` ได้หลายครั้ง ตอนซ่อนหลายรูปพร้อมกันให้ใช้ `--output-dir` ระบบจะถามรหัสผ่านแค่ครั้งเดียว ตอนถอดหลายรูป ไฟล์ของแต่ละรูปจะแยกไปอยู่ในโฟลเดอร์ของตัวเอง

```bash
sweethide hide -i a.jpg -i b.jpg --output-dir out -t "ข้อความเดียวกันทุกรูป"
```

ถ้า `--file` ชี้ไปที่โฟลเดอร์ โปรแกรมจะ zip ทั้งโฟลเดอร์ให้เองก่อนซ่อน ส่วนชื่อไฟล์ `-` หมายถึง stdin/stdout เอาไว้ต่อ pipe กับคำสั่งอื่น

```bash
cat photo.png | sweethide hide -i - -o - -t "ผ่าน pipe" > secret.png
```

```bash
journalctl -n 50 | sweethide hide -i photo.jpg -o logs.png --text-file -
```

ใส่ `--json` แล้วผลจะออกมาเป็น JSON (array ตามจำนวนรูปเสมอ) เหมาะเอาไปต่อกับ `jq`

```bash
sweethide capacity -i photo.jpg --json | jq '.[0].capacity[0].payloadBytes'
```

เปิด tab completion ได้ด้วย `sweethide completion bash|zsh|powershell` ดูวิธีติดตั้งได้ที่ `sweethide completion --help`

| Exit code | ความหมาย |
|---|---|
| 0 | สำเร็จ |
| 1 | error ทั่วไป (ไฟล์, argument, รูปเสีย) |
| 2 | ไม่พบข้อมูล **หรือ** รหัสผ่านผิด (แยกไม่ได้โดยตั้งใจ) |
| 3 | รูปเล็กเกินไปสำหรับข้อมูลที่จะซ่อน |
| 4 | รหัสผ่านถูก แต่ข้อมูลเสียหาย (รูปถูกแก้ไขหรือบีบอัดใหม่) |

## พัฒนา

`SweetHide.slnx` รวมทุกโปรเจกต์ (เปิดด้วย Visual Studio บน Windows) ส่วน `SweetHide.Portable.slnf` รวมทุกอย่างยกเว้นแอป WinUI ใช้ได้ทั้ง Linux และ macOS

```bash
dotnet build SweetHide.Portable.slnf
```

```bash
dotnet test --solution SweetHide.Portable.slnf
```

```bash
dotnet format SweetHide.Portable.slnf --verify-no-changes
```

```bash
dotnet build src/SweetHide.App -p:Platform=x64
```

คำสั่งสุดท้ายใช้ได้บน Windows เท่านั้น

| Path | หน้าที่ |
|---|---|
| `src/SweetHide.Core` | Engine: ซ่อนและถอดข้อมูล, crypto, payload format, codec ภาพ (SkiaSharp), steganalysis |
| `src/SweetHide.Cli` | คำสั่ง `sweethide` |
| `src/SweetHide.Presentation` | ViewModel (MVVM), ข้อความ UI ภาษาไทย/อังกฤษ (`Resources/Strings*.resx`), interface ของ service ทั้งหมดอยู่ที่นี่ ไม่ผูกกับ WinUI จึงเทสต์บน Linux ได้ |
| `src/SweetHide.App` | แอป WinUI 3: XAML, file picker, clipboard, drag & drop |
| `tests/` | xUnit v3 + FsCheck (property-based) |
| `docs/PLAN.md` | แผนงานและ tech stack |
| `.github/workflows/` | `test.yml` และ `package.yml` (เรียกใช้ซ้ำได้), `ci.yml` (PR), `release.yml` (tag `v*`) |
| `docs/FORMAT.md` | สเปกรูปแบบข้อมูลในภาพ (เวอร์ชัน v1) |
| `docs/RELEASING.md` | วิธีออก release (push tag แล้ว GitHub Actions ทำที่เหลือให้) |

## License

ใช้ [MIT License](LICENSE) นำไปใช้ แก้ไข หรือแจกต่อได้ตามสบาย ขอแค่เก็บข้อความลิขสิทธิ์ไว้ ส่วนรายชื่อซอฟต์แวร์อื่นที่ SweetHide ใช้และ license ของแต่ละตัวอยู่ใน [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt) ไฟล์ที่แจกใน release มีทั้งสองไฟล์นี้แนบไปด้วย
