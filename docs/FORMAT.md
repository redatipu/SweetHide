# รูปแบบข้อมูลในภาพ (SweetHide format v2 และ v1)

เอกสารนี้อธิบายว่า SweetHide เก็บข้อมูลไว้ในภาพอย่างไร ใครอยากเขียนตัวถอดเอง หรือลองหาจุดอ่อนในระบบ ใช้เอกสารนี้อ้างอิงได้
ความปลอดภัยของระบบขึ้นอยู่กับ **กุญแจ** (รหัสผ่าน, key file หรือ private key) อย่างเดียว ไม่ได้อาศัยการปกปิดวิธีการ (ตามหลักของ Kerckhoffs)

- เวอร์ชันใหม่ **เขียนเป็น v2 เสมอ** และยังถอดภาพ v1 ที่ทำด้วย v0.1.0 ได้ (ส่วน v1 อยู่ท้ายเอกสาร)
- v0.1.0 **ถอดภาพ v2 ไม่ได้** จะรายงานว่า "ไม่พบข้อมูลหรือรหัสผ่านผิด" เพราะ v2 ไม่มีอะไรที่ v1 รู้จักเลย
- เหมือน v1 คือ ทุกบิตที่อ่านได้โดยไม่มีกุญแจต้องดูเป็นค่าสุ่ม ไม่มี magic bytes, ไม่มีเลขเวอร์ชัน และไม่มีค่าตั้งค่าใดเขียนไว้แบบ plaintext

---

# Format v2

## 1. Carrier และ slot

ส่วน container ไม่ได้อ่านเขียนพิกเซลตรง ๆ แต่ทำงานผ่าน `IStegoCarrier` ซึ่งเป็นรายการ slot ที่มีหมายเลขกำกับ และแต่ละ slot เก็บบิตล่างได้ไม่กี่บิต

```
SlotCount            จำนวน slot
MaxBitsPerSlot       จำนวนบิตล่างสูงสุดที่ slot เดียวเก็บได้
Read(slot, b)        คืนค่า b บิตล่างของ slot
Write(slot, v, b)    เปลี่ยน slot ให้น้อยที่สุดเพื่อให้ b บิตล่างเท่ากับ v
```

carrier ทุกตัว (รวมถึง JPEG-coefficient และ palette ที่จะเพิ่มทีหลัง) ต้องรักษาเงื่อนไขเหล่านี้ ไม่อย่างนั้นจะถอดไม่ได้

1. **หา slot ชุดเดิมได้จากภาพผลลัพธ์** จำนวน slot และ sample ที่แต่ละหมายเลขชี้ไป ต้องขึ้นกับสิ่งที่ `Write` ไม่เคยแก้เท่านั้น
2. **อ่านกลับได้ตรง** หลัง `Write(slot, v, b)` แล้ว `Read(slot, b) == v` และ slot อื่นไม่เปลี่ยน
3. **เปลี่ยนน้อยที่สุด** ใช้ LSB matching (ข้อ 13) และใช้ค่าสุ่มเฉพาะตอนต้องเลือกระหว่างสองทางที่ห่างเท่ากัน
4. **อ่านเขียน slot คนละตัวพร้อมกันได้** เพื่อให้ทำงานหลาย core ได้

carrier ที่รองรับ adaptive embedding (`IAdaptiveCarrier`) ต้องบอก texture ของแต่ละ slot ได้ด้วย (0–255 หรือ -1 สำหรับ **reference slot**) โดย texture ต้องคำนวณจาก reference slot และสิ่งที่ `Write` ไม่เคยแก้เท่านั้น

### RGB carrier (`RgbLsbCarrier`)

- รูปจะถูก decode เป็น RGBA 8 บิตแบบ unpremultiplied และไม่มีการแปลง color space
- พิกเซลที่ alpha = 0 จะถูกข้ามทั้งหมด ส่วนพิกเซลที่เหลือให้ 3 slot ตามลำดับ R, G, B
  - slot `s` → พิกเซลที่ใช้ได้ลำดับที่ `s / 3` (นับตาม raster order), channel `s % 3`
- ค่า alpha ไม่ถูกแก้ไข จึงหา slot ชุดเดิมได้เสมอตอนถอด
- `MaxBitsPerSlot` = 4
- **texture แบบตารางหมากรุก:** พิกเซลที่ `x + y` เป็นเลขคี่เป็น reference ส่วนพิกเซลที่ `x + y` เป็นเลขคู่มี texture ต่อ channel เท่ากับ `max − min` ของค่าใน channel เดียวกันของเพื่อนบ้านบน ล่าง ซ้าย ขวาที่ alpha ≠ 0 (ซึ่งเป็น reference ทั้งหมด) ถ้ามีเพื่อนบ้านน้อยกว่า 2 ตัวให้ texture = 0
  - เหตุผลที่ไม่ใช้ "บิตที่อยู่เหนือบิตที่ฝัง": LSB matching เปลี่ยน 128 เป็น 127 ได้พอ ๆ กับเป็น 129 ซึ่งทำให้บิตสูงทุกบิตเปลี่ยน ตัวถอดจึงคำนวณ texture เดิมจากภาพผลลัพธ์ไม่ได้

### Palette carrier (`PaletteCarrier`)

ใช้กับ PNG แบบ palette (color type 3) ซึ่งอ่านและเขียนด้วย `IndexedPng` ของเราเอง เพื่อให้ได้ index ของแต่ละพิกเซลตรงตามไฟล์ เพราะ SkiaSharp จะแปลง palette เป็น RGBA ทิ้งไป

- **palette ไม่ถูกแก้เลย** เปลี่ยนแค่ว่าแต่ละพิกเซลชี้ไปที่สีไหน ไฟล์ที่ได้จึงยังเป็น palette PNG ที่มีสีชุดเดิมทุกสี
- **สายสี (chain):** เอาเฉพาะสีใน palette ที่ alpha = 255 มาเรียงเป็นสาย เริ่มจากสีที่มืดที่สุด (luma `299R + 587G + 114B` ต่ำสุด ถ้าเท่ากันเอา index น้อยกว่า) แล้วต่อด้วยสีที่ใกล้สีล่าสุดที่สุดซึ่งยังไม่ได้ใช้ (ระยะ Euclidean ใน RGB ถ้าเท่ากันเอา index น้อยกว่า)
- **บิตของพิกเซล** = ตำแหน่งของสีพิกเซลนั้นในสาย `& 1`
- **พิกเซลที่ใช้ได้:** สีของพิกเซลต้องมีเพื่อนบ้านในสาย (ก่อนหรือหลัง) ที่ห่างไม่เกิน 48 ใน RGB พิกเซลที่สีโปร่งแสงหรือไม่มีสีใกล้เคียงจะถูกข้าม เงื่อนไขนี้ขึ้นกับ palette อย่างเดียว และการเขียนจะย้ายไปหาเพื่อนบ้านที่ผ่านเงื่อนไขนี้เท่านั้น (ซึ่งมีสีเดิมเป็นเพื่อนบ้านใกล้ของมันเช่นกัน) ตัวถอดจึงหา slot ชุดเดิมเจอเสมอ
- slot `s` = พิกเซลที่ใช้ได้ลำดับที่ `s` ตาม raster order, `MaxBitsPerSlot` = 1
- **การเขียน:** ถ้า parity ไม่ตรง ให้ขยับหนึ่งตำแหน่งในสายไปทางเพื่อนบ้านที่สีใกล้กว่า (ถ้าใกล้เท่ากันใช้ coin) และต้องอยู่ในระยะ 48
- ใช้ได้เฉพาะโหมดรหัสผ่าน (มี key file และเลือก KDF profile ได้) 1 บิตต่อพิกเซล ไม่มี adaptive, deniable, public key หรือ Shamir และไม่มี v1
- **ความจุ** ราว 1/8 ไบต์ต่อพิกเซลที่ใช้ได้ หักส่วน public region, header และ tag ไปไม่ถึง 120 ไบต์ ภาพโลโก้ที่มีไม่กี่สีและแต่ละสีต่างกันมากจะเก็บอะไรไม่ได้เลย ซึ่งตั้งใจไว้แบบนั้น เพราะการขยับสีในภาพแบบนี้จะเห็นด้วยตาเปล่า

## 2. ลำดับสาธารณะ (public order) และพื้นที่ในภาพ

ให้ N = จำนวน slot และ `P_pub` คือ Feistel permutation ของ `[0, N)` (ข้อ 2 ของ v1) ที่ใช้ key = `SHA-512("sweethide/v1/salt-slots")` ซึ่งเป็น key เดียวกับตำแหน่ง salt ของ v1
slot ที่ **rank** `r` คือ `P_pub(r)` ดังนั้น rank 0..127 ตรงกับตำแหน่ง salt ของ v1 พอดี

```
rank 0 .. 255                     public region: 32 ไบต์, 1 บิตต่อ slot
rank 256 .. 256 + 392n − 1        ตาราง recipient (เฉพาะโหมด public key, n คน)
rank dataStart .. N − 1           data region   (dataStart = 256 หรือ 256 + 392n)
```

ลำดับสาธารณะแค่กระจายส่วนที่ตำแหน่งตายตัวไปทั่วภาพ ส่วน container แต่ละอันจะเดินในลำดับของตัวเองที่ขึ้นกับกุญแจ (ข้อ 7) คนที่ไม่มีกุญแจจึงไม่รู้ว่าใช้ slot ไหนบ้าง

## 3. Public region (32 ไบต์)

เก็บ 1 บิตต่อ slot ใน rank 0..255 เรียงจาก MSB ไป LSB ของแต่ละไบต์

| โหมด | ไบต์ 0–15 | ไบต์ 16–31 |
|---|---|---|
| รหัสผ่าน (รวม deniable และ Shamir) | `salt` ที่เลือก KDF profile (ข้อ 4) | ค่าสุ่ม |
| public key | ephemeral X25519 public key 32 ไบต์ (little-endian u-coordinate) โดยสุ่มบิตบนสุด (bit 255) ทุกครั้ง | (ต่อ) |

X25519 ไม่สนใจ bit 255 ตาม RFC 7748 ตัวถอดจึงเคลียร์บิตนี้ก่อนใช้ ที่สุ่มไว้เพราะ public key จริงมี bit 255 เป็น 0 เสมอ ถ้าไม่สุ่มจะเป็นจุดสังเกตง่าย ๆ

## 4. KDF profile

| profile | Argon2id | ค่าใช้จ่ายเทียบกับ Normal |
|---|---|---|
| Fast | m = 16 MiB, t = 2, p = 4 | ~1/6 |
| Normal | m = 64 MiB, t = 3, p = 4 (เท่ากับ v1) | 1 |
| Paranoid | m = 256 MiB, t = 4, p = 4 | ~5 เท่า (ใช้หน่วยความจำ 4 เท่า) |

profile ไม่ได้เขียนไว้ตรง ๆ แต่ได้มาจาก salt

```
code    = SHA-256("sweethide/v2/kdf-profile" ‖ salt)[0] & 3
profile = { 0: Fast, 1: Normal, 2: Paranoid, 3: Normal }[code]
```

ตอนซ่อนจะสุ่ม salt ใหม่ไปเรื่อย ๆ จนกว่า code จะตรงกับ profile ที่เลือก (เฉลี่ย 2–4 ครั้ง) ตอนถอดจึงรัน Argon2id แค่ครั้งเดียวด้วย profile ที่ถูก

- salt ที่เก็บไว้ยังเป็นค่าสุ่มสม่ำเสมอในกลุ่ม salt ที่ให้ code นั้น และทุก code ใช้ได้ จึงไม่มี salt ไหนที่บอกได้ว่า "ภาพนี้ไม่ใช่ SweetHide"
- ข้อจำกัดดูข้อ 15

## 5. กุญแจ

### โหมดรหัสผ่าน

```
master = Argon2id(password = UTF-8(NFC(password)), salt, profile, len = 32)
kf     = มี key file ? HMAC-SHA256(key = "sweethide/v2/key-file", data = เนื้อไฟล์ทั้งไฟล์) : 0x00 × 32
root   = HMAC-SHA256(key = kf, data = master)          (คือ HKDF-Extract(salt = kf, IKM = master))
```

- key file คือไฟล์อะไรก็ได้ เปลี่ยนแม้แต่ไบต์เดียวก็เป็น key file คนละอัน
- key file ถูกผสม **หลัง** Argon2id จึงเพิ่มปัจจัยขนาด 256 บิตโดยไม่เปลี่ยนต้นทุนการเดารหัสผ่าน คนที่ได้ key file ไปแล้วยังต้องจ่าย Argon2id ทุกครั้งที่เดา และคนที่ไม่มี key file เดาไม่ได้เลย
- เมื่อ profile ของภาพใช้พารามิเตอร์เดียวกับ v1 (Normal) ค่า `master` จะเท่ากับ master ของ v1 ตัวถอดจึงใช้ Argon2id ครั้งเดียวลองได้ทั้งสอง format

### โหมด public key

```
root = file key (32 ไบต์สุ่มใหม่ทุกภาพ)
```

### กุญแจของ container (ทั้งสองโหมด)

```
aeadKey = HKDF-SHA256-Expand(root, info = "sweethide/v2/aead-key",         len = 32)
permKey = HKDF-SHA256-Expand(root, info = "sweethide/v2/slot-permutation", len = 64)
```

## 6. ตาราง recipient (โหมด public key)

ออกแบบตาม age: file key เข้ารหัส container และตารางเก็บ file key ที่ห่อ (wrap) แยกให้ recipient แต่ละคน

```
e, E    = ephemeral X25519 key pair หนึ่งคู่ต่อภาพ ใช้ร่วมกันทุก recipient
shared  = X25519(e, R)                                   R = public key ของ recipient
wrapKey = HKDF-SHA256(IKM = shared, salt = E ‖ R, info = "sweethide/v2/x25519", len = 32)
entry   = AES-256-GCM(wrapKey, nonce = 0 × 12, aad = "sweethide/v2/stanza",
                      plaintext = fileKey (32) ‖ u8 n)   → 49 ไบต์ = 392 slot
```

- entry ที่ `j` (0..n−1) อยู่ที่ rank `256 + 392j` ถึง `256 + 392(j+1) − 1` แบบ 1 บิตต่อ slot
- เรียงลำดับ recipient แบบสุ่ม ไม่มี ID หรือชื่อใด ๆ ตัวถอดลอง entry j = 0..15 ทีละอันจนกว่าอันใดจะถอดผ่าน แล้วอ่าน n จากในนั้นเพื่อรู้ว่า data region เริ่มที่ `256 + 392n`
- รองรับสูงสุด 16 คนต่อภาพ, key ซ้ำจะถูกตัดออก, key ที่เป็น low-order point จะถูกปฏิเสธ

### Public key และไฟล์ private key

- **public key:** Bech32 (BIP-173) โดยมี HRP = `sweethide` เช่น `sweethide1euce7lq9…` ยาว 68 ตัวอักษร checksum 6 ตัวท้ายจับการพิมพ์ผิดได้ถึง 4 ตัวอักษร รับได้ทั้งตัวเล็กล้วนหรือตัวใหญ่ล้วน
- **ไฟล์ private key** เข้ารหัสด้วย passphrase:

```
-----BEGIN SWEETHIDE PRIVATE KEY-----
Public-Key: sweethide1…                       (ให้คนอ่าน ไม่ได้ใช้ตอนถอด)
base64 ของ: "SHKY" | u8 version = 1 | u32 memoryKiB | u32 iterations | u32 parallelism
            | salt (16) | public key (32) | AES-256-GCM(private key) (32) | tag (16)
-----END SWEETHIDE PRIVATE KEY-----

key   = HKDF-SHA256-Expand(Argon2id(NFC(passphrase), salt, m, t, p), "sweethide/identity/v1", 32)
nonce = 0 × 12   (salt ใหม่ทุกครั้งที่ export)
aad   = ทุกไบต์ก่อน ciphertext
```

ตอน import จะจำกัดพารามิเตอร์ Argon2id (m ≤ 4 GiB, t ≤ 64, p ≤ 64) เพื่อไม่ให้ไฟล์ที่ถูกแก้สั่งให้ใช้หน่วยความจำหรือเวลามหาศาล และตรวจว่า public key ที่คำนวณได้ตรงกับที่เก็บไว้

## 7. Data region และลำดับของ container

ให้ `D = N − dataStart` แล้วแบ่ง data region เป็นสองครึ่งตาม rank: ครึ่งแรก `⌊D/2⌋` rank และครึ่งหลังที่เหลือ

| placement | ใช้เมื่อ | ช่วง rank |
|---|---|---|
| 0 = whole | โหมดรหัสผ่านปกติ, public key, Shamir | ทั้ง data region |
| 1 = first half | deniable | ครึ่งแรก |
| 2 = second half | deniable | ครึ่งหลัง |

ลำดับของ container (slot sequence) สำหรับช่วง rank `[base, base + L)`:

```
element k → slot P_pub(base + P_permKey(k)),     P_permKey = Feistel permutation ของ [0, L) ที่ใช้ key = permKey
```

ทั้งสองชั้นเป็น Feistel แบบ random access จึงคำนวณ slot ของแต่ละ element แยกกันได้ และอ่านเขียนพร้อมกันหลาย core ได้ ผลลัพธ์ไม่ขึ้นกับจำนวน core เลย (มี test ยืนยันว่าภาพที่ได้ตรงกันทุกไบต์ทั้งแบบ 1 core และ 4 core)

## 8. Container

### ตำแหน่ง

- **header** ใช้ 216 element แรกของ sequence ที่ slot **ไม่ใช่** reference slot (สำหรับ RGB คือพิกเซลที่ `x + y` เป็นเลขคู่) แบบ 1 บิตต่อ slot ทำให้ header ไม่เคยไปเปลี่ยน texture ให้ adaptive mode ให้ `headerEnd` = element ถัดจาก element สุดท้ายของ header
- **body** ใช้ element ที่เหลือตามลำดับ แบบ b บิตต่อ slot (MSB ก่อน และเติมบิตสุ่มในช่องว่างของ slot สุดท้าย)
  - ปกติ: element ที่ถูกข้ามไปตอนหา header (reference slot ที่ k < headerEnd) ก่อน แล้วตามด้วยทุก element ตั้งแต่ headerEnd
  - adaptive: เฉพาะ element ตั้งแต่ headerEnd ที่ texture ≥ τ

### เนื้อหา

```
header plaintext (11 ไบต์)
  u8  flags          bit 0–1 = b − 1, bit 2 = adaptive, bit 3–7 = 0 (สงวนไว้)
  u8  τ              threshold ของ texture (0 ถ้าไม่ใช่ adaptive)
  u8  kind           1 = payload, 2 = Shamir share
  u32 bodyLength     ความยาว body plaintext (LE)
  u32 contentLength  ส่วนที่เป็นเนื้อหาจริง (LE, ≤ bodyLength) ที่เหลือเป็นศูนย์ (padding ของโหมด deniable)

header = AES-256-GCM(aeadKey, nonce = 00…01, aad = "sweethide/v2/header" ‖ u8 placement, header plaintext)   → 27 ไบต์ = 216 slot
body   = AES-256-GCM(aeadKey, nonce = 00…02, aad = "sweethide/v2/body" ‖ u8 placement ‖ header plaintext,
                     plaintext = content ‖ 0 × (bodyLength − contentLength))
```

- nonce เป็นค่าคงที่ได้ เพราะ root ไม่ซ้ำกันในแต่ละภาพ (salt ใหม่ หรือ file key ใหม่) และในโหมด deniable ถ้าสองส่วนได้ root เดียวกันจะปฏิเสธไม่ยอมซ่อน
- ค่า b ไม่ต้องเดาอีกต่อไปเหมือน v1 เพราะอยู่ใน header ที่เข้ารหัสแล้ว ตัวถอดลองแค่ 3 placement
- ถ้า header ผ่านแต่ flag ที่สงวนไว้ไม่เป็นศูนย์หรือ kind ไม่รู้จัก จะรายงาน `UnsupportedFormat` (ทำด้วยเวอร์ชันที่ใหม่กว่า) ถ้า header ผ่านแต่ body ไม่ผ่าน แปลว่ากุญแจถูกแต่ข้อมูลเสียหาย (`Corrupted`)
- ก่อนจองหน่วยความจำให้ body จะตรวจก่อนว่า `bodyLength` ใส่ในภาพนี้ได้จริง ความยาวที่เสียหายจึงขอหน่วยความจำเกินขนาดภาพไม่ได้

### ความจุ

```
ContainerBytes = 27 + ⌊bodySlots × b / 8⌋        overhead ทั้งหมด = 27 + 16 = 43 ไบต์
bodySlots      = (จำนวน rank ของ placement) − 216                   ปกติ
               = (จำนวน slot ที่ไม่ใช่ reference ใน data region) − 216   adaptive (τ = 0)
```

## 9. Adaptive embedding

เลือกฝังเฉพาะบริเวณที่มี texture สูง ซึ่งการเปลี่ยน ±1 กลืนไปกับ noise เดิม

- ใช้เฉพาะ slot ที่ไม่ใช่ reference (ครึ่งหนึ่งของพิกเซลแบบตารางหมากรุก) จึงจุได้ไม่ถึงครึ่งของโหมดปกติ
- texture ถูกคำนวณ **หลัง** เขียน public region และตาราง recipient ซึ่งเป็นสิ่งเดียวที่อาจไปโดน reference slot หลังจากนั้นจะไม่มีอะไรเขียนลง reference slot อีก ตัวถอดจึงคำนวณ texture ชุดเดียวกันได้จากภาพผลลัพธ์
- การเลือก τ: ให้ `fit` = τ สูงสุดที่ slot ทั้งหมดยังจุ body ได้ และ `half` = τ สูงสุดที่ body ใช้ไม่เกิน 50% ของ slot แล้วใช้ `τ = max(half, ⌈fit / 2⌉)`
  - หมายความว่าจะกระจายออกไปถ้ายังมี slot ที่ texture สูงเหลือ แต่ไม่ลงไปใช้ slot ที่เรียบกว่าครึ่งหนึ่งของที่จำเป็น เพราะ LSB matching แบบไม่มี coding เปลี่ยนประมาณครึ่งหนึ่งของ group เสมอไม่ว่าจะมี slot ให้กี่ช่อง การกระจายจึงคุ้มก็ต่อเมื่อยังอยู่ในบริเวณที่มี texture

## 10. Deniable mode

ซ่อน payload สองชุดในภาพเดียว แต่ละชุดมีรหัสผ่าน (และ key file) ของตัวเอง

- ทั้งสองชุดใช้ salt เดียวกัน (จึงใช้ KDF profile เดียวกัน) แต่ root ต่างกัน
- แต่ละชุดถูกใส่ลงครึ่งที่ **สุ่ม** เลือก จึงไม่มีครึ่งไหนที่แปลว่า "ตัวหลอก"
- แต่ละชุด **เติม padding จนเต็มครึ่งของตัวเอง** (`bodyLength` = ความจุของครึ่งนั้น)
- ถ้ามี payload ชุดเดียว อีกครึ่งจะถูกเติมด้วย container ที่ใช้ root สุ่มซึ่งไม่มีใครรู้ (chaff) กระบวนการเหมือนชุดจริงทุกอย่าง

เหตุผล: ถ้าภาพ deniable ปล่อยให้ครึ่งใดครึ่งหนึ่งว่างบางส่วน คนที่ไม่มีรหัสผ่านก็เปรียบเทียบสถิติของสองครึ่ง (ซึ่งเป็นพิกเซลสลับกันในภาพเดียวกัน) ได้ทันที และคนที่บังคับให้เราเปิดรหัสผ่านหนึ่งก็จะเห็นว่าอีกครึ่งว่างหรือเต็ม การเติมเต็มทั้งสองครึ่งทำให้ภาพที่มี payload สองชุดกับภาพที่มีชุดเดียวบวก chaff มีรูปแบบเหมือนกันทุกอย่าง

**ข้อแลกเปลี่ยนเรื่องความจุ:**
- payload แต่ละชุดใช้ได้ไม่เกินครึ่งหนึ่งของความจุ (`GetDeniableCapacity`)
- ภาพถูกใช้เต็มความจุเสมอ (ที่ 1 บิตต่อ channel ค่าสีประมาณครึ่งหนึ่งจะเปลี่ยน) จึง **ตรวจจับได้ง่ายกว่า** ว่ามีอะไรซ่อนอยู่ เมื่อเทียบกับการซ่อนข้อความสั้น ๆ แบบปกติ deniable mode ปกป้องว่า "มีชุดที่สองหรือไม่" ไม่ได้ปกป้องว่า "มีอะไรซ่อนอยู่หรือไม่"
- ใช้ร่วมกับ adaptive ไม่ได้ (เพราะต้องใช้เต็มภาพอยู่แล้ว)

## 11. Shamir k-of-n

แบ่ง payload ไปไว้ในภาพ n ภาพ ภาพใดก็ได้ k ภาพ (พร้อมรหัสผ่าน) ประกอบกลับได้ ถ้ามีน้อยกว่า k ภาพจะไม่รู้อะไรเกี่ยวกับเนื้อหาเลย

```
secret = macKey (32 ไบต์สุ่ม) ‖ serialized payload (ข้อ 12)
แต่ละไบต์ของ secret = ค่าคงที่ของพหุนามสุ่มดีกรี k − 1 บน GF(2^8) (polynomial x^8 + x^4 + x^3 + x + 1)
share ที่ x = 1..n เก็บค่าพหุนามที่ x
tag    = HMAC-SHA256(macKey, "sweethide/v2/shamir" ‖ splitId ‖ u8 k ‖ u8 n ‖ payload)

เนื้อหาของ container (kind = 2):
  u8 version = 1 | u8 k | u8 n | u8 x | splitId (16) | tag (32) | y (32 + ความยาว payload)
```

- แต่ละภาพใช้ salt ของตัวเอง จึงไม่มี AES-GCM key ซ้ำกัน
- Shamir แบบธรรมดาแก้ไขได้ (malleable): คนที่คุม share หนึ่งอันเลื่อนค่า secret ที่ประกอบได้ตามต้องการ tag ที่ใช้ macKey ซึ่งอยู่ใน secret เองจะจับได้ และเมื่อมี share ไม่ถึง k ก็ไม่รู้ macKey จึงไม่รู้อะไรจาก tag
- การคูณใน GF(2^8) เป็นแบบ shift-and-add ไม่มี branch และไม่เปิดตาราง จึงใช้เวลาเท่ากันไม่ว่าข้อมูลลับเป็นอะไร

## 12. Payload (เนื้อหา kind = 1)

เหมือน v1 ข้อ 6 ทุกอย่าง

## 13. การฝัง (LSB matching)

เหมือน v1 ข้อ 7 ทุกอย่าง

## 14. ขั้นตอนการถอด

1. สร้าง carrier แล้วอ่าน public region 32 ไบต์ (ถ้า N > 256) ถ้าภาพเล็กเกินกว่าจะมี v2 แต่ยังมีที่สำหรับ v1 ให้อ่าน salt ของ v1 แทน
2. **รหัสผ่าน:** หา profile จาก salt → `master` → `root` → ลอง header ของ placement 0, 1, 2
   ถ้าไม่ผ่าน และไม่ได้ใช้ key file และภาพมีที่สำหรับ v1: ใช้ master ด้วยพารามิเตอร์ Normal (ใช้ค่าเดิมซ้ำถ้า profile ของภาพเป็น Normal) แล้วลอง v1 ด้วย b = 1..4
3. **private key:** เอา public region มาเคลียร์ bit 255 → X25519 → wrapKey → ลอง entry 0..15 → ได้ file key และ n → เปิด container ที่ `dataStart = 256 + 392n`
4. ถ้าไม่มีอะไรผ่านเลย รายงาน `NotFoundOrWrongPassword`

ต้นทุนต่อหนึ่งรหัสผ่านคือ Argon2id หนึ่งครั้ง (สองครั้งถ้า profile ของภาพไม่ใช่ Normal และไม่พบ v2) `PasswordProbe` ทำขั้นตอนเดียวกันนี้แต่ตรวจแค่ header สำหรับใช้ทดสอบรหัสผ่านของภาพตัวเองกับ wordlist ซึ่งก็คือสิ่งที่ผู้โจมตีที่ได้ภาพไปจะทำนั่นเอง

## 15. หมายเหตุด้านความปลอดภัย และสิ่งที่ยังแยกแยะได้

- **KDF profile เป็นข้อมูลสาธารณะ:** ใครก็คำนวณ code จาก salt ของภาพไหนก็ได้ สำหรับภาพทั่วไป code กระจายสม่ำเสมอ แต่สำหรับภาพ SweetHide จะเป็นไปตาม profile ที่คนเลือกใช้ เพราะ Normal ได้ไป 2 ใน 4 code ภาพ Normal จึงให้หลักฐานกับผู้สังเกตไม่เกิน 1 บิต (likelihood ratio ≤ 2) ภาพ Fast หรือ Paranoid ไม่เกิน 2 บิต ทางเลือกอื่นคือให้ตัวถอดลองทุก profile ซึ่งไม่รั่วอะไรเลย แต่ทุกครั้งที่ใส่รหัสผ่านผิดต้องจ่ายค่า Fast + Normal + Paranoid รวมกัน (ประมาณ 6.5 เท่าของ Normal) ส่วนการใส่ hint ที่ต้องใช้รหัสผ่านถึงจะคำนวณได้ จะทำให้ผู้โจมตีตัดรหัสผิดส่วนใหญ่ได้ด้วยต้นทุนของ profile ที่ถูกกว่า ทำให้ Paranoid ไร้ความหมาย จึงไม่เลือกทั้งสองแบบ
- **ephemeral key ในโหมด public key เป็นจุดบนเส้นโค้ง:** สุ่ม bit 255 แล้ว และใช้ key เดียวต่อภาพ (ไม่ใช่หนึ่ง key ต่อ recipient แบบ age) จึงมีจุดบนเส้นโค้งแค่จุดเดียว แต่ string 255 บิตแบบสุ่มเป็นจุดบนเส้นโค้งที่ใช้ได้แค่ประมาณครึ่งเดียว (อีกครึ่งอยู่บน twist) ผู้สังเกตที่สงสัยโหมดนี้จึงได้หลักฐาน 1 บิต การแก้ให้หมดต้องใช้ Elligator 2 ร่วมกับ "dirty" key (บวกจุด low-order แบบสุ่ม) ซึ่งต้องใช้ point arithmetic บน Edwards curve ที่ libsodium/NSec ไม่มีให้ จึงยังไม่ได้ทำ
- **deniable mode** ปกป้องในเชิง cryptography: ด้วยรหัสผ่านหนึ่ง สิ่งที่เห็นจะเหมือนกันทุกอย่างไม่ว่าอีกครึ่งจะเป็น payload จริงหรือ chaff แต่คนที่ได้รหัสผ่านหนึ่งไปจะรู้ว่าภาพนี้ทำด้วย deniable mode (ซึ่งตรงกับกรณีที่ใช้ chaff ด้วย) และภาพถูกใช้เต็มความจุ ดูข้อ 10
- **adaptive mode:** texture คำนวณจากพิกเซลที่ใครก็เห็น ผู้วิเคราะห์จึงรู้ว่า slot ไหนมีโอกาสถูกใช้มากกว่า (selection channel) และ steganalysis ที่ใช้ข้อมูลนี้ทำได้ดีกว่าแบบไม่ใช้ adaptive mode ช่วยได้จริงกับตัวตรวจทั่วไป แต่ไม่ใช่การรับประกัน
- **Shamir:** คนที่มี share น้อยกว่า k ภาพแต่มีรหัสผ่าน จะรู้ k, n, หมายเลข share, splitId และขนาด payload แต่ไม่รู้เนื้อหาเลย
- **key file:** ต้องเป็นไฟล์ที่ผู้โจมตีเดาไม่ได้ (ไม่ใช่ไฟล์ที่ดาวน์โหลดได้ทั่วไป) และต้องเก็บไว้ไม่ให้แก้แม้แต่ไบต์เดียว
- **ภาพที่เคยเป็น JPEG:** ถ้า cover เป็น JPEG ที่ decode แล้ว การแก้บิตล่างจะทำให้โครงสร้าง block 8×8 ของ JPEG เสีย ซึ่ง JPEG compatibility attack ตรวจจับได้แม้ซ่อนข้อมูลนิดเดียว `CoverAnalyzer` จะเตือนเมื่อเห็นขอบ block ชัด แต่ JPEG คุณภาพสูงอาจตรวจไม่เจอ ควรใช้ภาพต้นฉบับที่เป็น PNG หรือ RAW
- **ภาพที่ไม่ได้มี noise:** ภาพเรียบ ภาพหน้าจอ หรือบริเวณที่ค่าเป็น 0/255 ทำให้การเปลี่ยน ±1 เด่นชัด `CoverAnalyzer` ให้คะแนน 0–100 จาก flat area, ค่าที่ saturate, texture และร่องรอย JPEG
- การใช้พื้นที่เกิน 50% หรือใช้ b ≥ 3 ยังทำให้ steganalysis เชิงสถิติขั้นสูง (เช่น SPA, RS หรือ ML-based detector) ตรวจจับได้ง่ายขึ้นเหมือนเดิม

## 16. ข้อจำกัดที่รู้อยู่แล้ว

- ภาพต้องเป็น lossless ตลอดทาง ถ้าถูก re-encode เป็น JPEG, ย่อขนาด, ตัดขอบ หรือผ่านแอปแชตที่บีบอัดรูป ข้อมูลจะหาย
- ตาราง recipient รองรับได้ 16 คนต่อภาพ
- Shamir รองรับ n ≤ 255 และใช้ได้กับโหมดรหัสผ่านเท่านั้น
- ภาพที่มี slot น้อยกว่า 256 + 216 × 2 โดยประมาณจะไม่มีที่สำหรับ v2

---

# Format v1 (v0.1.0)

ส่วนนี้คงไว้เพื่อให้ถอดภาพเก่าได้ เวอร์ชันใหม่ไม่เขียน v1 อีกแล้ว แต่ยังอ่านได้เสมอ (มีภาพ fixture จาก v0.1.0 จริงใน test)

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

## 8. ข้อจำกัดของ v1 (แก้แล้วใน v2)

- พารามิเตอร์ Argon2 ถูกกำหนดตายตัว (v2 มี KDF profile)
- ตัวถอดต้องเดา b (v2 เก็บไว้ใน header ที่เข้ารหัส)
