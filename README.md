# SweetHide

ซ่อน **ข้อความและไฟล์** ไว้ในรูปภาพ พร้อมเข้ารหัสด้วยรหัสผ่าน ผลลัพธ์เป็นไฟล์ PNG ที่ดูด้วยตาแล้วเหมือนรูปเดิมทุกอย่าง
ทำขึ้นเพื่อใช้กันเองและทดลองกับเพื่อนใน homelab

- เข้ารหัสด้วย **AES-256-GCM** และสร้างกุญแจจากรหัสผ่านด้วย **Argon2id**
- ใช้ **LSB matching (±1)** และกระจายตำแหน่งบิตแบบสุ่มตามรหัสผ่าน ไม่มี signature หรือ magic bytes ให้สแกนหา
- ใส่ข้อความหลายข้อความและไฟล์หลายไฟล์ไว้ในรูปเดียวกันได้ บีบอัดด้วย Brotli อัตโนมัติ
- **แอป Windows 11 (WinUI 3)** มีหน้าซ่อน ถอด และวิเคราะห์ รองรับลากไฟล์มาวางหรือกด Ctrl+V และสลับภาษาไทย/อังกฤษได้
- **CLI** ใช้ได้ทั้ง Windows และ Linux (x64/arm64)

> ⚠️ **ต้องส่งรูปแบบ "ไฟล์" เสมอ** แอปแชต (LINE, Messenger, Facebook) จะบีบอัดรูปใหม่ และข้อมูลที่ซ่อนไว้จะหายหมด

## แอป Windows (GUI)

ดาวน์โหลด `SweetHide-win-x64` (หรือ `SweetHide-win-arm64`) จาก artifact ของ GitHub Actions แล้วแตกไฟล์ไว้ที่ไหนก็ได้ จากนั้นเปิด `SweetHide.exe` ได้ทันที ไม่ต้องติดตั้ง .NET หรือ Windows App SDK เพิ่ม
ไฟล์ยังไม่ได้เซ็นด้วย code signing ครั้งแรกที่เปิด SmartScreen จะเตือน ให้กด **More info → Run anyway**

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
| `docs/FORMAT.md` | สเปกรูปแบบข้อมูลในภาพ (เวอร์ชัน v1) |
