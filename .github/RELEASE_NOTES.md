แตกไฟล์แล้วเปิดใช้ได้เลย ไม่ต้องติดตั้ง .NET หรืออะไรเพิ่ม

**เลือกไฟล์ให้ถูก**

- `SweetHide-{version}-win-x64.zip` แอปสำหรับ Windows 10/11 เครื่องทั่วไป
- `SweetHide-{version}-win-arm64.zip` แอปสำหรับ Windows บนชิป ARM
- `sweethide-{version}-…` ตัว CLI มีทั้ง Windows และ Linux (arm64 ใช้กับ Raspberry Pi ได้)
- `SHA256SUMS.txt` ไว้เช็กว่าไฟล์ที่โหลดมาไม่ถูกแก้ไข

แอปยังไม่ได้เซ็นโค้ด เปิดครั้งแรก Windows จะขึ้นหน้าเตือน แนะนำให้เช็ก checksum ก่อน ถ้าตรงกับใน `SHA256SUMS.txt` ค่อยกด More info แล้ว Run anyway

```powershell
Get-FileHash .\SweetHide-{version}-win-x64.zip
```

อีกเรื่องที่ต้องระวัง: เวลาส่งรูปที่ซ่อนข้อมูลให้เพื่อน ต้องส่งเป็นไฟล์ ถ้าส่งเป็นรูปใน LINE หรือ Messenger รูปจะถูกบีบอัดใหม่และข้อมูลที่ซ่อนไว้จะหายหมด
