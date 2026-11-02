# รูปแบบข้อมูลในภาพ (SweetHide format v1)

เอกสารนี้อธิบายว่า SweetHide เก็บข้อมูลไว้ในภาพอย่างไร ใครอยากเขียนตัวถอดเอง หรือลองหาจุดอ่อนในระบบ ใช้เอกสารนี้อ้างอิงได้
ความปลอดภัยของระบบขึ้นอยู่กับ **รหัสผ่าน** อย่างเดียว ไม่ได้อาศัยการปกปิดวิธีการ (ตามหลักของ Kerckhoffs)

## 1. Slot

- รูปจะถูก decode เป็น RGBA 8 บิตแบบ unpremultiplied และไม่มีการแปลง color space
- พิกเซลที่ alpha = 0 จะถูกข้ามทั้งหมด ส่วนพิกเซลที่เหลือให้ 3 slot ตามลำดับ R, G, B
  - slot `s` → พิกเซลที่ใช้ได้ลำดับที่ `s / 3` (นับตาม raster order), channel `s % 3`
- ค่า alpha ไม่ถูกแก้ไข จึงหา slot ชุดเดิมได้เสมอตอนถอด
- N = จำนวน slot ทั้งหมด

## 2. การเรียง slot (Feistel permutation)

`P_key : [0, N) → [0, N)` เป็น Feistel network แบบ balanced จำนวน 8 รอบ บนโดเมน `2^(2h)` โดยเลือก `2h` เป็นจำนวนคู่ที่เล็กที่สุดซึ่ง `≥ ⌈log2 N⌉` และอย่างน้อย 2
ถ้าผลลัพธ์ ≥ N จะวนซ้ำ (cycle-walking) จนได้ค่าที่อยู่ในช่วง

```
round key k[r] = uint64 little-endian ของ key[8r .. 8r+8)
F(r, R)       = splitmix64_finalizer(R XOR k[r]) & mask(h)
(L, R)        → (R, L XOR F(r, R))      ทำซ้ำ r = 0..7
```

## 3. Salt

- `salt` = 16 ไบต์สุ่มใหม่ทุกครั้งที่ซ่อน
- เก็บไว้ใน slot `P_pub(0), P_pub(1), …, P_pub(127)` โดย `P_pub` ใช้ key = `SHA-512("sweethide/v1/salt-slots")` ซึ่งเป็นค่าคงที่ที่เปิดเผย
- เก็บ 1 บิตต่อ slot เรียงจาก MSB ไป LSB ของแต่ละไบต์

## 4. กุญแจ

```
master  = Argon2id(password = UTF-8(NFC(password)), salt, m = 64 MiB, t = 3, p = 4, len = 32)
aeadKey = HKDF-SHA256-Expand(master, info = "sweethide/v1/aead-key",        len = 32)
permKey = HKDF-SHA256-Expand(master, info = "sweethide/v1/slot-permutation", len = 64)
```

## 5. Container

data slot คือลำดับ `P_permKey(0), P_permKey(1), …` โดยข้าม slot ที่ใช้เก็บ salt ไปแล้ว
แต่ละ slot เก็บ `b` บิต (b = 1–4) เรียงจาก MSB ก่อน ช่องว่างที่เหลือใน slot สุดท้ายจะเติมด้วยบิตสุ่ม

```
[ header ciphertext (4) | header tag (16) | body ciphertext (n) | body tag (16) ]

header = AES-256-GCM(aeadKey, nonce = 00…01, aad = "sweethide/v1/header" ‖ b, plaintext = uint32 LE n)
body   = AES-256-GCM(aeadKey, nonce = 00…02, aad = "sweethide/v1/body"   ‖ b, plaintext = payload)
```

- nonce เป็นค่าคงที่ได้ เพราะ salt ใหม่ทุกครั้งทำให้ `aeadKey` ไม่ซ้ำกันเลยในแต่ละภาพ
- ค่า `b` ไม่ได้เก็บไว้ในภาพ ตัวถอดจะลอง b = 1, 2, 3, 4 ทีละค่าจนกว่า header tag จะผ่าน
- ถ้า header ผ่านแต่ body ไม่ผ่าน แปลว่ารหัสผ่านถูกแต่ข้อมูลเสียหาย

## 6. Payload (plaintext ของ body)

```
u8  compression   0 = ไม่บีบอัด, 1 = Brotli (เลือกเฉพาะเมื่อได้ขนาดเล็กลงจริง)
u32 rawLength     ขนาดหลังคลายการบีบอัด (LE, ไม่เกิน 256 MiB)
... entry list:
    u16 count                     (1..1024)
    count × {
        u8  kind                  1 = ข้อความ, 2 = ไฟล์
        u16 nameLength            (≤ 1024)
        ... name                  UTF-8; ข้อความใช้ "" ส่วนไฟล์ใช้ชื่อไฟล์อย่างเดียวโดยไม่มี path
        u32 contentLength
        ... content               ข้อความต้องเป็น UTF-8 ที่ถูกต้อง
    }
```

ชื่อไฟล์ที่ถอดออกมาถือว่าเป็น **ข้อมูลที่เชื่อไม่ได้** ตัวถอดต้องทำความสะอาดชื่อก่อนเขียนไฟล์ (ดู `SafeFileName`) คือตัด path ทิ้ง, ห้ามใช้ชื่อ `..`, ห้ามชื่ออุปกรณ์สงวนของ Windows และห้ามตัวอักษรต้องห้าม

## 7. การฝัง (LSB matching)

ถ้า `v mod 2^b ≠ target` จะเลือกค่า `v'` ในช่วง [0, 255] ที่ใกล้ `v` ที่สุดและมี `v' mod 2^b = target`
ถ้าใกล้เท่ากันทั้งสองทาง จะสุ่มเลือกทางใดทางหนึ่ง กรณี b = 1 ก็คือการบวกหรือลบ 1 แบบสุ่ม ซึ่งต่างจาก LSB replacement ตรงที่ไม่ทิ้งร่องรอยใน histogram แบบ pairs-of-values ให้ chi-square attack จับได้

## 8. ข้อจำกัดที่รู้อยู่แล้ว

- ภาพต้องเป็น lossless ตลอดทาง ถ้าถูก re-encode เป็น JPEG, ย่อขนาด หรือผ่านแอปแชตที่บีบอัดรูป ข้อมูลจะหาย
- พารามิเตอร์ Argon2 ถูกกำหนดตายตัวใน v1 ถ้าจะเปลี่ยนต้องออก format version ใหม่
- การใช้พื้นที่เกิน 50% หรือใช้ b ≥ 3 ทำให้ steganalysis เชิงสถิติขั้นสูง (เช่น SPA, RS, หรือ ML-based detector) ตรวจจับได้ง่ายขึ้น
