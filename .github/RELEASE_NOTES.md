## ดาวน์โหลด

| ไฟล์ | ใช้กับ |
|---|---|
| `SweetHide-{version}-win-x64.zip` | **แอป Windows 10/11** (เครื่องทั่วไป) แตกไฟล์แล้วเปิด `SweetHide.exe` |
| `SweetHide-{version}-win-arm64.zip` | แอป Windows บนเครื่อง ARM (เช่น Snapdragon) |
| `sweethide-{version}-win-x64.zip` / `-win-arm64.zip` | CLI สำหรับ Windows |
| `sweethide-{version}-linux-x64.tar.gz` / `-linux-arm64.tar.gz` | CLI สำหรับ Linux / Raspberry Pi |
| `SHA256SUMS.txt` | checksum ของทุกไฟล์ |

ไม่ต้องติดตั้งอะไรเพิ่ม เพราะทุกไฟล์รวม .NET และ Windows App SDK มาในตัวแล้ว

### ตรวจไฟล์ก่อนเปิด

ไฟล์ไม่ได้เซ็นด้วย code signing ตอนเปิดครั้งแรก Windows SmartScreen จะเตือน ให้ตรวจ checksum ให้ตรงกับ `SHA256SUMS.txt` ก่อน แล้วค่อยกด **More info → Run anyway**

```powershell
Get-FileHash .\SweetHide-{version}-win-x64.zip -Algorithm SHA256
```

```bash
sha256sum --check --ignore-missing SHA256SUMS.txt
```

> ⚠️ ต้องส่งรูปที่ซ่อนข้อมูลแล้วเป็น **ไฟล์** เสมอ เพราะแอปแชตจะบีบอัดรูปใหม่และข้อมูลจะหาย
