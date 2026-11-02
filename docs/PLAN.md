# Magi: แผนงานและ Tech Stack

## ขอบเขต (ยืนยันกับเจ้าของโปรเจกต์แล้ว)

- **ผู้ใช้:** ใช้เองและใช้กับเพื่อนเพื่อทดสอบความสามารถของ homelab กันเอง ไม่ได้แจกจ่ายสาธารณะ
- **สิ่งที่ซ่อนได้:** ทั้งข้อความและไฟล์ หลายรายการในรูปเดียว
- **ช่องทางส่งรูป:** ส่งเป็นไฟล์ (lossless) ยังไม่รองรับการส่งผ่านแอปแชตที่บีบอัดรูปใหม่ ถ้าต้องการให้รอดการบีบอัด ดู Phase 4

## Tech Stack

| ส่วน | เลือกใช้ | เหตุผล |
|---|---|---|
| Runtime | .NET 10 (LTS) | ใช้ภาษาเดียวทั้ง Core, CLI และ GUI และ CLI รันบน Linux homelab ได้ด้วย |
| อ่าน/เขียนภาพ | SkiaSharp (MIT) | ImageSharp 3.2+/4.x ต้องใช้ license key ตอน build แบบ Release จึงไม่เลือก |
| สร้างกุญแจ | Argon2id (Konscious, MIT) | กันการเดารหัสผ่านด้วย GPU |
| เข้ารหัส | AES-256-GCM (มีใน .NET) | เข้ารหัสและตรวจจับการแก้ไขข้อมูลได้ในขั้นตอนเดียว |
| CLI | System.CommandLine 2.0 | |
| GUI (Phase 2) | WinUI 3 + CommunityToolkit.Mvvm | หน้าตา Fluent ของ Windows 11 |
| Test | xUnit v3 + FsCheck บน Microsoft.Testing.Platform | |
| CI | GitHub Actions (Windows + Ubuntu) | ทดสอบได้ทั้งสองแพลตฟอร์ม และสร้างไฟล์รันสำหรับ win-x64/arm64 และ linux-x64/arm64 |

## Phase

### ✅ Phase 0: Setup
- solution (`Magi.slnx`), `Directory.Build.props`, `Directory.Packages.props` (Central Package Management)
- เปิด analyzer ระดับ Recommended และให้ warning นับเป็น error, `.editorconfig`, CI, Dependabot

### ✅ Phase 1: Core + CLI
- Engine: เข้ารหัส → กระจายตำแหน่งด้วย Feistel permutation → ฝังด้วย LSB matching (ดู [FORMAT.md](FORMAT.md))
- Payload: ข้อความและไฟล์หลายรายการ, Brotli, ป้องกัน decompression bomb, ทำความสะอาดชื่อไฟล์กัน path traversal
- CLI: `hide`, `reveal`, `capacity`, `analyze` (steganalysis สำหรับทดลองกับเพื่อน)
- Test: property-based roundtrip, tamper detection, PNG roundtrip ต้องได้ค่าพิกเซลตรงทุกบิต, known-answer test ของ permutation, CLI end-to-end

### ⏭️ Phase 2: GUI สำหรับ Windows 11 (WinUI 3)
- มีสองหน้าหลักคือ **ซ่อน** และ **ถอด** รองรับ drag & drop และวางรูปด้วย Ctrl+V
- แถบแสดงความจุแบบ real-time (ใช้ `StegoEngine.GetCapacity` / `GetContainerSize`)
- รายการไฟล์แนบและช่องข้อความหลายช่อง, ตัวเลือกบิตต่อ channel อยู่ในส่วน Advanced
- หน้าตาแบบ Windows 11: Mica, Dark/Light ตามระบบ, ฟอนต์ Leelawadee UI สำหรับภาษาไทย, รองรับไทย/อังกฤษผ่าน `.resw`
- เรียก Core บน background thread เพราะ Argon2 ใช้เวลาประมาณ 0.5–1 วินาที และต้องมี progress หรือ busy state ให้ผู้ใช้เห็น
- ต้อง build บน Windows (CI `windows-latest` ทำได้)

### ⏭️ Phase 3: Release สำหรับใช้กับเพื่อน
- push tag `v*` แล้ว GitHub Actions จะสร้าง GitHub Release ที่มี `magi.exe`, GUI แบบ portable zip และ SHA-256
- **ไม่ซื้อ code signing** เพราะใช้กันเอง ครั้งแรกที่เปิด Windows SmartScreen จะเตือน ให้กด More info → Run anyway และแจก checksum ให้เพื่อนตรวจสอบไฟล์
- GUI เลือกแบบ unpackaged + self-contained (`WindowsAppSDKSelfContained`) แทน MSIX เพราะ MSIX ต้องใช้ cert ที่เครื่องเพื่อนเชื่อถือ

### 💡 Phase 4: ทำเพิ่มถ้าสนใจ
- **Robust mode** ให้ข้อมูลรอดการส่งผ่าน LINE หรือ Facebook: ฝังใน DCT coefficient ร่วมกับ Reed–Solomon ECC แต่จะเก็บข้อมูลได้น้อยลงมาก
- Adaptive embedding (เช่น HUGO/S-UNIWARD) ให้ฝังเฉพาะบริเวณที่มี texture เพื่อให้ตรวจจับยากขึ้น
- เมนูคลิกขวาใน Explorer ("Reveal with Magi"), batch mode
- เพิ่ม analyzer แบบ RS analysis และ SPA เพื่อแข่งกันซ่อนและตรวจจับในกลุ่ม

## ความเสี่ยง

| ความเสี่ยง | วิธีรับมือ |
|---|---|
| รูปถูกบีบอัดใหม่แล้วข้อมูลหาย | เตือนทั้งใน CLI และ GUI ให้ส่งเป็นไฟล์ และมี robust mode ใน Phase 4 |
| ลืมรหัสผ่าน | กู้คืนไม่ได้โดยการออกแบบ ตอนซ่อนจึงให้ยืนยันรหัสผ่านสองครั้ง |
| ไฟล์จากเพื่อนมีชื่อหรือเนื้อหาอันตราย | ทำความสะอาดชื่อไฟล์, ไม่เขียนทับไฟล์เดิม, ไม่เปิดหรือรันไฟล์ให้อัตโนมัติ, จำกัดขนาดข้อมูล |
| Antivirus หรือ SmartScreen เตือน | แจก checksum และ build จาก CI ที่ตรวจสอบย้อนหลังได้ |
| CodeQL | ใช้ได้ฟรีเฉพาะ public repo ถ้า repo เป็น private ต้องมี GitHub Advanced Security จึงยังไม่ได้ใส่ไว้ใน CI |
