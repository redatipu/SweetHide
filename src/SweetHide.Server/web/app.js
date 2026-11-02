// SweetHide web UI. Plain JavaScript, no dependencies; talks to the same server's /api endpoints.
'use strict';

(() => {
  const storageKeys = { lang: 'sweethide.lang', theme: 'sweethide.theme' };
  const storage = {
    get(key) {
      try { return window.localStorage.getItem(key); } catch { return null; }
    },
    set(key, value) {
      try { window.localStorage.setItem(key, value); } catch { /* private mode: not remembered */ }
    },
  };

  // Theme and language are applied before the body renders, so there is no flash of the wrong one.
  const root = document.documentElement;
  root.dataset.theme = storage.get(storageKeys.theme) === 'light' ? 'light' : 'dark';
  const savedLang = storage.get(storageKeys.lang);
  if (savedLang === 'th' || savedLang === 'en') {
    root.lang = savedLang;
  } else if (root.lang !== 'th') {
    root.lang = 'en'; // the server picked it from Accept-Language
  }

  const strings = {
    en: {
      tagline: 'Hide text and files in images',
      switchLanguage: 'Switch language',
      switchTheme: 'Switch between dark and light theme',
      tabsLabel: 'Mode',
      tabHide: 'Hide',
      tabReveal: 'Reveal',
      tabAnalyze: 'Analyze',
      coverTitle: 'Cover image',
      dropImage: 'Drop an image here',
      dropImageHint: 'PNG, JPEG, BMP, GIF or WebP · or paste with Ctrl+V',
      dropStegoHint: 'The PNG you received as a file',
      chooseImage: 'Choose image…',
      changeImage: 'Change',
      previewAlt: 'Preview of the chosen image',
      imageInfo: '{0} × {1} · {2}',
      measuring: 'Measuring…',
      capacityTitle: 'Capacity',
      capacityEmpty: 'Holds up to {0} at {1} bit(s) per channel',
      capacityUsage: 'About {0} of {1} ({2})',
      capacityWarn: 'More than half full: easier to detect statistically. A larger or more detailed photo helps.',
      capacityOver: 'Probably doesn\'t fit. Text compresses, so it may still work; otherwise use a larger image, hide less, or raise bits per channel under Advanced.',
      secretTitle: 'What to hide',
      messageLabel: 'Message',
      messagePlaceholder: 'Type a secret message (optional if you attach files)',
      filesLabel: 'Files',
      addFiles: 'Add files…',
      noFiles: 'No files yet. Drop files here or choose Add files.',
      removeFile: 'Remove {0}',
      passwordTitle: 'Password',
      passwordLabel: 'Password',
      confirmLabel: 'Confirm password',
      showPassword: 'Show password',
      passwordHint: 'A forgotten password cannot be recovered. A long passphrase is best.',
      strength: 'Strength: {0}',
      strength1: 'weak',
      strength2: 'fair',
      strength3: 'good',
      strength4: 'strong',
      advanced: 'Advanced',
      bitsLabel: 'Bits per colour channel',
      bits1: 'invisible (recommended)',
      bits2: 'twice the room, still invisible to the eye',
      bits3: 'may show in smooth areas such as sky',
      bits4: 'visible noise',
      hideButton: 'Hide & download',
      hiding: 'Hiding…',
      hideDoneTitle: 'Done! {0} downloaded',
      hideDoneText: 'Send the PNG as a file or document. Chat apps re-compress photos, which destroys the hidden data.',
      revealImageTitle: 'Image with a secret',
      revealButton: 'Reveal',
      revealing: 'Revealing…',
      foundItems: 'Found {0} item(s)',
      messageN: 'Message {0}',
      copy: 'Copy',
      copied: 'Copied',
      copyFailed: 'Couldn\'t copy. Select the text and copy it yourself.',
      download: 'Download {0}',
      downloadZip: 'Download all (.zip)',
      fileSafetyNote: 'Files come from whoever made the image and are never opened automatically.',
      analyzeTitle: 'Analyze an image',
      analyzeIntro: 'Simple steganalysis: LSB statistics and the chi-square attack. Try it on images from friends.',
      analyzing: 'Analyzing…',
      colChannel: 'Channel',
      colOnes: 'LSB = 1',
      colChi: 'Chi-square',
      colDof: 'dof',
      colP: 'p (LSB replacement)',
      analyzeExplanation: 'p close to 1 on every channel suggests heavy LSB replacement. SweetHide uses LSB matching, which this test is not designed to catch.',
      bitPlaneLabel: 'Bit plane',
      bitPlaneHint: '0 = least significant bit. Hidden data looks like noise; natural photos usually show shapes.',
      bitPlaneAlt: 'Bit plane {0} of the image',
      footerPrivacy: 'Everything happens in this server\'s memory and nothing is stored. Your password travels to the server, so only use it on a network you trust or over HTTPS.',
      sendAsFileShort: 'Always send the PNG as a file: chat apps re-compress photos.',
      errorTitle: 'Something went wrong',
      errNoCover: 'Choose a cover image first.',
      errNoImage: 'Choose an image first.',
      errNothing: 'Type a message or add at least one file.',
      errNoPassword: 'Enter a password.',
      errMismatch: 'Passwords don\'t match.',
      errTooBigLocal: 'That\'s {0} in total, more than this server accepts ({1}).',
      errNotImage: '{0} doesn\'t look like an image.',
      errNotFoundTitle: 'Nothing found',
      errNotFound: 'Nothing found, or the password is wrong.',
      errCorrupted: 'The password is right, but the hidden data is damaged (the image was edited or re-compressed).',
      errCapacity: 'Doesn\'t fit: needs {0}, but the image holds {1} at {2} bit(s) per channel.',
      errCapacityBits: 'It would fit with {0} bits per channel.',
      useBits: 'Use {0} bits',
      errCapacityBigger: 'Use a larger image or hide less.',
      errInvalidImage: 'Couldn\'t read this image.',
      errTooLarge: 'The upload is larger than this server accepts ({0}).',
      errRateLimited: 'Too many requests. Wait a minute and try again.',
      errBusy: 'The server is busy. Try again in a moment.',
      errTimeout: 'That took too long. Try a smaller image or try again.',
      errUnauthorized: 'This server needs an access token. Reload the page to sign in.',
      errNetwork: 'Couldn\'t reach the server. Check the connection and try again.',
      errUnknown: 'The server answered with an error ({0}).',
      version: 'SweetHide {0}',
    },
    th: {
      tagline: 'ซ่อนข้อความและไฟล์ในรูปภาพ',
      switchLanguage: 'เปลี่ยนภาษา',
      switchTheme: 'สลับธีมมืดและสว่าง',
      tabsLabel: 'โหมด',
      tabHide: 'ซ่อน',
      tabReveal: 'ถอด',
      tabAnalyze: 'วิเคราะห์',
      coverTitle: 'รูปปก',
      dropImage: 'ลากรูปมาวางที่นี่',
      dropImageHint: 'PNG, JPEG, BMP, GIF หรือ WebP · หรือวางด้วย Ctrl+V',
      dropStegoHint: 'ไฟล์ PNG ที่ได้รับมาแบบไฟล์',
      chooseImage: 'เลือกรูป…',
      changeImage: 'เปลี่ยนรูป',
      previewAlt: 'ตัวอย่างรูปที่เลือก',
      imageInfo: '{0} × {1} · {2}',
      measuring: 'กำลังคำนวณ…',
      capacityTitle: 'ความจุ',
      capacityEmpty: 'จุได้สูงสุด {0} ที่ {1} บิตต่อ channel',
      capacityUsage: 'ใช้ประมาณ {0} จาก {1} ({2})',
      capacityWarn: 'ใช้เกินครึ่งของความจุ ตรวจจับทางสถิติได้ง่ายขึ้น ใช้รูปที่ใหญ่หรือมีรายละเอียดมากขึ้นจะช่วยได้',
      capacityOver: 'น่าจะใส่ไม่พอ แต่ข้อความถูกบีบอัดได้ จึงอาจยังใส่ได้ ถ้าไม่พอให้ใช้รูปที่ใหญ่ขึ้น ลดข้อมูลลง หรือเพิ่มจำนวนบิตในตั้งค่าขั้นสูง',
      secretTitle: 'สิ่งที่จะซ่อน',
      messageLabel: 'ข้อความ',
      messagePlaceholder: 'พิมพ์ข้อความลับ (เว้นว่างได้ถ้าแนบไฟล์)',
      filesLabel: 'ไฟล์แนบ',
      addFiles: 'เพิ่มไฟล์…',
      noFiles: 'ยังไม่มีไฟล์แนบ ลากไฟล์มาวางที่นี่หรือกดเพิ่มไฟล์',
      removeFile: 'นำ {0} ออก',
      passwordTitle: 'รหัสผ่าน',
      passwordLabel: 'รหัสผ่าน',
      confirmLabel: 'ยืนยันรหัสผ่าน',
      showPassword: 'แสดงรหัสผ่าน',
      passwordHint: 'ถ้าลืมรหัสผ่านจะกู้ข้อมูลคืนไม่ได้ ใช้วลียาว ๆ จะปลอดภัยที่สุด',
      strength: 'ความแข็งแรง: {0}',
      strength1: 'อ่อน',
      strength2: 'พอใช้',
      strength3: 'ดี',
      strength4: 'ดีมาก',
      advanced: 'ตั้งค่าขั้นสูง',
      bitsLabel: 'จำนวนบิตต่อ channel สี',
      bits1: 'มองไม่เห็น (แนะนำ)',
      bits2: 'จุได้ 2 เท่า ยังมองด้วยตาไม่เห็น',
      bits3: 'อาจเห็น noise ในพื้นที่เรียบ เช่น ท้องฟ้า',
      bits4: 'เห็น noise ชัดเจน',
      hideButton: 'ซ่อนและดาวน์โหลด',
      hiding: 'กำลังซ่อน…',
      hideDoneTitle: 'เรียบร้อย! ดาวน์โหลด {0} แล้ว',
      hideDoneText: 'ส่ง PNG เป็นไฟล์หรือเอกสารเท่านั้น เพราะแอปแชตจะบีบอัดรูปใหม่และข้อมูลที่ซ่อนจะหาย',
      revealImageTitle: 'รูปที่มีข้อมูลซ่อนอยู่',
      revealButton: 'ถอดข้อมูล',
      revealing: 'กำลังถอด…',
      foundItems: 'พบ {0} รายการ',
      messageN: 'ข้อความ {0}',
      copy: 'คัดลอก',
      copied: 'คัดลอกแล้ว',
      copyFailed: 'คัดลอกไม่ได้ ลองเลือกข้อความแล้วคัดลอกเอง',
      download: 'ดาวน์โหลด {0}',
      downloadZip: 'ดาวน์โหลดทั้งหมด (.zip)',
      fileSafetyNote: 'ไฟล์มาจากคนที่สร้างรูป ระบบจะไม่เปิดไฟล์ให้อัตโนมัติ',
      analyzeTitle: 'วิเคราะห์รูป',
      analyzeIntro: 'Steganalysis เบื้องต้น: สถิติ LSB และ chi-square attack ลองใช้กับรูปที่เพื่อนส่งมาได้',
      analyzing: 'กำลังวิเคราะห์…',
      colChannel: 'Channel',
      colOnes: 'LSB = 1',
      colChi: 'Chi-square',
      colDof: 'dof',
      colP: 'p (LSB replacement)',
      analyzeExplanation: 'ถ้า p เข้าใกล้ 1 ทุก channel แสดงว่าน่าจะมีการฝังแบบ LSB replacement จำนวนมาก SweetHide ใช้ LSB matching ซึ่งการทดสอบนี้ไม่ได้ออกแบบมาให้จับได้',
      bitPlaneLabel: 'Bit plane',
      bitPlaneHint: '0 = บิตต่ำสุด ข้อมูลที่ซ่อนจะดูเหมือน noise ส่วนรูปถ่ายทั่วไปมักยังเห็นเป็นรูปร่าง',
      bitPlaneAlt: 'Bit plane {0} ของรูป',
      footerPrivacy: 'ทุกอย่างทำงานในหน่วยความจำของเซิร์ฟเวอร์นี้และไม่มีการเก็บอะไรไว้ แต่รหัสผ่านจะถูกส่งไปที่เซิร์ฟเวอร์ ใช้เฉพาะในเครือข่ายที่ไว้ใจได้หรือผ่าน HTTPS เท่านั้น',
      sendAsFileShort: 'ส่ง PNG เป็นไฟล์เสมอ เพราะแอปแชตจะบีบอัดรูปใหม่',
      errorTitle: 'เกิดข้อผิดพลาด',
      errNoCover: 'เลือกรูปปกก่อน',
      errNoImage: 'เลือกรูปก่อน',
      errNothing: 'พิมพ์ข้อความหรือแนบไฟล์อย่างน้อยหนึ่งไฟล์',
      errNoPassword: 'ใส่รหัสผ่าน',
      errMismatch: 'รหัสผ่านไม่ตรงกัน',
      errTooBigLocal: 'รวมแล้ว {0} เกินกว่าที่เซิร์ฟเวอร์นี้รับได้ ({1})',
      errNotImage: '{0} ดูเหมือนไม่ใช่ไฟล์รูป',
      errNotFoundTitle: 'ไม่พบข้อมูล',
      errNotFound: 'ไม่พบข้อมูล หรือรหัสผ่านไม่ถูกต้อง',
      errCorrupted: 'รหัสผ่านถูกต้อง แต่ข้อมูลเสียหาย (รูปถูกแก้ไขหรือบีบอัดใหม่)',
      errCapacity: 'ใส่ไม่พอ ต้องใช้ {0} แต่รูปนี้จุได้ {1} ที่ {2} บิตต่อ channel',
      errCapacityBits: 'ถ้าใช้ {0} บิตต่อ channel จะใส่ได้',
      useBits: 'ใช้ {0} บิต',
      errCapacityBigger: 'ลองใช้รูปที่ใหญ่ขึ้นหรือลดข้อมูลลง',
      errInvalidImage: 'อ่านรูปนี้ไม่ได้',
      errTooLarge: 'ไฟล์ที่ส่งใหญ่เกินกว่าที่เซิร์ฟเวอร์นี้รับได้ ({0})',
      errRateLimited: 'ส่งคำขอถี่เกินไป รอสักครู่แล้วลองใหม่',
      errBusy: 'เซิร์ฟเวอร์กำลังยุ่ง ลองใหม่อีกครั้งในอีกสักครู่',
      errTimeout: 'ใช้เวลานานเกินไป ลองใช้รูปที่เล็กลงหรือลองใหม่',
      errUnauthorized: 'เซิร์ฟเวอร์นี้ต้องใช้ access token ให้โหลดหน้าใหม่เพื่อล็อกอิน',
      errNetwork: 'ติดต่อเซิร์ฟเวอร์ไม่ได้ ตรวจการเชื่อมต่อแล้วลองใหม่',
      errUnknown: 'เซิร์ฟเวอร์ตอบกลับด้วยข้อผิดพลาด ({0})',
      version: 'SweetHide {0}',
    },
  };

  const lang = () => (root.lang === 'th' ? 'th' : 'en');

  function t(key, ...args) {
    const template = strings[lang()][key] ?? strings.en[key] ?? key;
    return args.reduce((text, arg, i) => text.replaceAll(`{${i}}`, String(arg)), template);
  }

  const languageListeners = [];
  const onLanguageChange = (listener) => languageListeners.push(listener);

  function applyLanguage() {
    for (const el of document.querySelectorAll('[data-i18n]')) el.textContent = t(el.dataset.i18n);
    for (const el of document.querySelectorAll('[data-i18n-placeholder]')) el.placeholder = t(el.dataset.i18nPlaceholder);
    for (const el of document.querySelectorAll('[data-i18n-aria]')) el.setAttribute('aria-label', t(el.dataset.i18nAria));
    for (const el of document.querySelectorAll('[data-i18n-alt]')) el.alt = t(el.dataset.i18nAlt);
    document.title = `SweetHide · ${t('tagline')}`;
    for (const listener of languageListeners) listener();
  }

  // ---------- helpers ----------

  const $ = (id) => document.getElementById(id);

  const binaryUnits = ['B', 'KiB', 'MiB', 'GiB', 'TiB'];
  function formatBytes(bytes) {
    let value = bytes;
    let unit = 0;
    while (Math.abs(value) >= 1024 && unit < binaryUnits.length - 1) {
      value /= 1024;
      unit++;
    }
    return unit === 0 ? `${bytes.toLocaleString('en-US')} B` : `${value.toFixed(1)} ${binaryUnits[unit]}`;
  }

  const formatPercent = (ratio) => `${(ratio * 100).toFixed(ratio < 0.1 ? 1 : 0)}%`;
  const formatNumber = (value, digits) => (value === null || value === undefined ? '–' : value.toFixed(digits));
  const utf8Length = (text) => new TextEncoder().encode(text).length;

  function icon(name) {
    const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
    svg.setAttribute('class', 'icon');
    svg.setAttribute('aria-hidden', 'true');
    const use = document.createElementNS('http://www.w3.org/2000/svg', 'use');
    use.setAttribute('href', `#i-${name}`);
    svg.append(use);
    return svg;
  }

  function element(tag, className, text) {
    const el = document.createElement(tag);
    if (className) el.className = className;
    if (text !== undefined) el.textContent = text;
    return el;
  }

  function download(blob, name) {
    const url = URL.createObjectURL(blob);
    const link = document.createElement('a');
    link.href = url;
    link.download = name;
    document.body.append(link);
    link.click();
    link.remove();
    setTimeout(() => URL.revokeObjectURL(url), 60_000);
  }

  function fileNameFrom(response, fallback) {
    const header = response.headers.get('Content-Disposition') || '';
    const encoded = /filename\*\s*=\s*UTF-8''([^;]+)/i.exec(header);
    if (encoded) {
      try { return decodeURIComponent(encoded[1]); } catch { /* fall through */ }
    }
    const plain = /filename\s*=\s*"?([^";]+)"?/i.exec(header);
    return plain ? plain[1] : fallback;
  }

  function base64ToBlob(base64) {
    const binary = atob(base64);
    const bytes = new Uint8Array(binary.length);
    for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
    return new Blob([bytes], { type: 'application/octet-stream' });
  }

  async function copyText(text) {
    if (navigator.clipboard && window.isSecureContext) {
      try {
        await navigator.clipboard.writeText(text);
        return true;
      } catch { /* fall back below */ }
    }
    // Plain-HTTP LAN pages have no Clipboard API; the old way still works there.
    const area = element('textarea', 'offscreen');
    area.value = text;
    area.setAttribute('readonly', '');
    document.body.append(area);
    area.select();
    let ok = false;
    try { ok = document.execCommand('copy'); } catch { ok = false; }
    area.remove();
    return ok;
  }

  const looksLikeImage = (file) => file.type.startsWith('image/') || /\.(png|jpe?g|bmp|gif|webp)$/i.test(file.name);

  class ApiError extends Error {
    constructor(code, problem = {}, status = 0) {
      super(problem.title || code);
      this.code = code;
      this.problem = problem;
      this.status = status;
    }
  }

  async function api(path, form) {
    let response;
    try {
      response = await fetch(path, { method: 'POST', body: form, credentials: 'same-origin', cache: 'no-store' });
    } catch {
      throw new ApiError('network');
    }
    if (!response.ok) {
      let problem = {};
      try { problem = await response.json(); } catch { /* not JSON */ }
      throw new ApiError(problem.code || 'http', problem, response.status);
    }
    return response;
  }

  /** Turns an API error into { title, text, action? } in the current language. */
  function describeError(error) {
    const p = error.problem || {};
    switch (error.code) {
      case 'not-found-or-wrong-password': return { title: t('errNotFoundTitle'), text: t('errNotFound') };
      case 'corrupted': return { title: t('errorTitle'), text: t('errCorrupted') };
      case 'insufficient-capacity': {
        const text = t('errCapacity', formatBytes(p.neededBytes ?? 0), formatBytes(p.capacityBytes ?? 0), p.bitsPerChannel ?? 1);
        return p.suggestedBitsPerChannel
          ? { title: t('errorTitle'), text: `${text} ${t('errCapacityBits', p.suggestedBitsPerChannel)}`, suggestedBits: p.suggestedBitsPerChannel }
          : { title: t('errorTitle'), text: `${text} ${t('errCapacityBigger')}` };
      }
      case 'invalid-image': return { title: t('errInvalidImage'), text: p.detail || '' };
      case 'request-too-large': return { title: t('errorTitle'), text: t('errTooLarge', formatBytes(p.limitBytes ?? maxRequestBytes)) };
      case 'rate-limited': return { title: t('errorTitle'), text: t('errRateLimited') };
      case 'busy': return { title: t('errorTitle'), text: t('errBusy') };
      case 'timeout': return { title: t('errorTitle'), text: t('errTimeout') };
      case 'unauthorized': return { title: t('errorTitle'), text: t('errUnauthorized') };
      case 'network': return { title: t('errorTitle'), text: t('errNetwork') };
      default: {
        const text = [p.title, p.detail].filter(Boolean).join(' ');
        return { title: t('errorTitle'), text: text || t('errUnknown', error.status || '?') };
      }
    }
  }

  function showAlert(box, kind, title, text, action) {
    box.replaceChildren();
    box.dataset.kind = kind;
    box.setAttribute('role', kind === 'error' ? 'alert' : 'status');
    box.append(icon(kind === 'success' ? 'check' : 'alert'));
    const body = element('div', 'alert-body');
    body.append(element('span', 'alert-title', title));
    if (text) body.append(element('span', 'alert-text', text));
    if (action) {
      const button = element('button', 'button secondary small alert-action', action.label);
      button.type = 'button';
      button.addEventListener('click', action.run);
      body.append(button);
    }
    box.append(body);
    box.hidden = false;
  }

  const hideAlert = (box) => { box.hidden = true; box.replaceChildren(); };

  function setBusy(button, busy, labelKey, busyKey) {
    button.disabled = busy;
    button.setAttribute('aria-busy', String(busy));
    const label = button.querySelector('[data-i18n]');
    label.dataset.i18n = busy ? busyKey : labelKey;
    label.textContent = t(label.dataset.i18n);
  }

  // ---------- drop zones and file pickers ----------

  /** Wires a drop target (+ its hidden input) to `onFiles(FileList)`. */
  function wireFileTarget(zone, input, onFiles) {
    let depth = 0;
    zone.addEventListener('dragenter', (event) => {
      if (!event.dataTransfer?.types.includes('Files')) return;
      event.preventDefault();
      depth++;
      zone.classList.add('is-dragging');
    });
    zone.addEventListener('dragover', (event) => {
      if (!event.dataTransfer?.types.includes('Files')) return;
      event.preventDefault();
      event.dataTransfer.dropEffect = 'copy';
    });
    zone.addEventListener('dragleave', () => {
      depth = Math.max(0, depth - 1);
      if (depth === 0) zone.classList.remove('is-dragging');
    });
    zone.addEventListener('drop', (event) => {
      event.preventDefault();
      depth = 0;
      zone.classList.remove('is-dragging');
      if (event.dataTransfer?.files.length) onFiles(event.dataTransfer.files);
    });
    input.addEventListener('change', () => {
      if (input.files.length) onFiles(input.files);
      input.value = '';
    });
  }

  /** An image drop zone with a preview. Returns { file, set(file), clear(), setInfo(text) }. */
  function imageZone(zoneId, inputId, onChange) {
    const zone = $(zoneId);
    const empty = zone.querySelector('.dropzone-empty');
    const filled = zone.querySelector('.dropzone-filled');
    const preview = zone.querySelector('.preview');
    const name = zone.querySelector('.preview-name');
    const info = zone.querySelector('.preview-info');
    let objectUrl = null;
    const state = {
      file: null,
      set(file) {
        if (objectUrl) URL.revokeObjectURL(objectUrl);
        objectUrl = URL.createObjectURL(file);
        state.file = file;
        preview.src = objectUrl;
        name.textContent = file.name || 'image';
        info.textContent = formatBytes(file.size);
        empty.hidden = true;
        filled.hidden = false;
        onChange(file);
      },
      setInfo(text) { info.textContent = text; },
    };
    wireFileTarget(zone, $(inputId), (files) => {
      const file = files[0];
      if (!looksLikeImage(file)) {
        onChange(null, t('errNotImage', file.name));
        return;
      }
      state.set(file);
    });
    return state;
  }

  // ---------- tabs ----------

  function initTabs() {
    const tabs = [...document.querySelectorAll('[role="tab"]')];
    const select = (tab, focus) => {
      for (const other of tabs) {
        const selected = other === tab;
        other.setAttribute('aria-selected', String(selected));
        other.tabIndex = selected ? 0 : -1;
        $(other.getAttribute('aria-controls')).hidden = !selected;
      }
      if (focus) tab.focus();
      activeTab = tab.dataset.tab;
    };
    for (const tab of tabs) {
      tab.addEventListener('click', () => select(tab, false));
      tab.addEventListener('keydown', (event) => {
        const index = tabs.indexOf(tab);
        const next = { ArrowRight: 1, ArrowLeft: -1 }[event.key];
        if (next) {
          event.preventDefault();
          select(tabs[(index + next + tabs.length) % tabs.length], true);
        }
      });
    }
  }

  let activeTab = 'hide';
  const pasteTargets = {};

  function initPaste() {
    // Ctrl+V with an image anywhere outside a text field uses it on the current tab.
    document.addEventListener('paste', (event) => {
      const target = event.target;
      if (target instanceof HTMLInputElement || target instanceof HTMLTextAreaElement) return;
      const file = [...(event.clipboardData?.files || [])].find(looksLikeImage);
      if (!file || !pasteTargets[activeTab]) return;
      event.preventDefault();
      const named = file.name && file.name !== 'image.png' ? file : new File([file], 'clipboard.png', { type: file.type });
      pasteTargets[activeTab](named);
    });
  }

  function initPasswordToggles() {
    for (const button of document.querySelectorAll('[data-toggle-password]')) {
      const inputs = button.dataset.togglePassword.split(' ').map($);
      button.addEventListener('click', () => {
        const show = button.getAttribute('aria-pressed') !== 'true';
        button.setAttribute('aria-pressed', String(show));
        for (const input of inputs) input.type = show ? 'text' : 'password';
      });
    }
  }

  // ---------- password strength (same rules as the desktop app) ----------

  const commonPasswords = ['password', '123456', '12345678', '123456789', 'qwerty', 'abc123', '111111', 'iloveyou', 'admin',
    'letmein', 'welcome', 'sweethide', 'football', 'monkey', 'dragon'];

  function graphemeCount(text) {
    if (typeof Intl !== 'undefined' && Intl.Segmenter) {
      return [...new Intl.Segmenter(undefined, { granularity: 'grapheme' }).segment(text)].length;
    }
    return [...text].length;
  }

  function passwordStrength(password) {
    if (!password) return 0;
    const length = graphemeCount(password); // Thai vowels and tone marks don't inflate the count
    const lower = password.toLowerCase();
    if ((length < 16 && commonPasswords.some((common) => lower.includes(common))) || new Set(password).size <= 2) return 1;
    let classes = 0;
    if (/[a-z]/.test(password)) classes++;
    if (/[A-Z]/.test(password)) classes++;
    if (/[0-9]/.test(password)) classes++;
    if (/[\p{P}\p{S}\s]/u.test(password)) classes++;
    if ([...password].some((c) => c.codePointAt(0) > 127 && /\p{L}/u.test(c))) classes++;
    if (length < 8) return 1;
    if (length < 12) return classes >= 3 ? 2 : 1;
    if (length < 16) return classes >= 2 ? 3 : 2;
    return classes >= 2 ? 4 : 3;
  }

  // ---------- hide ----------

  const containerOverhead = 36; // AES-GCM header + tags (StegoEngine.ContainerOverhead)
  let maxRequestBytes = 64 * 1024 * 1024;

  function initHide() {
    const form = $('hide-form');
    const alertBox = $('hide-alert');
    const text = $('hide-text');
    const password = $('hide-password');
    const confirm = $('hide-password-confirm');
    const submit = $('hide-submit');
    const capacityBox = $('hide-capacity');
    const capacityText = $('hide-capacity-text');
    const capacityHint = $('hide-capacity-hint');
    const meter = $('hide-meter');
    const fileList = $('hide-file-list');
    const filesEmpty = $('hide-files-empty');
    const strength = $('hide-strength');
    const strengthText = $('hide-strength-text');
    const files = [];
    let capacity = null; // /api/capacity response for the current cover
    let measuring = false;
    let coverRequest = 0;

    const bits = () => Number(form.querySelector('input[name="bits"]:checked').value);
    const setBits = (value) => {
      form.querySelector(`input[name="bits"][value="${value}"]`).checked = true;
      form.querySelector('.advanced').open = true;
      renderCapacity();
    };

    /** Upper bound for what the payload will take (before compression), mirroring docs/FORMAT.md. */
    function estimatedBytes() {
      const entries = [];
      if (text.value) entries.push(utf8Length(text.value));
      for (const file of files) entries.push(utf8Length(file.name) + file.size);
      if (entries.length === 0) return 0;
      return containerOverhead + 5 + 2 + entries.reduce((sum, size) => sum + 1 + 2 + 4 + size, 0);
    }

    function renderCapacity() {
      if (!cover.file) {
        capacityBox.hidden = true;
        return;
      }
      capacityBox.hidden = !measuring && !capacity; // stays hidden when the server couldn't read the image
      capacityHint.hidden = true;
      meter.dataset.state = 'ok';
      if (!capacity) {
        capacityText.textContent = t('measuring');
        meter.style.setProperty('--fill', '0%');
        return;
      }
      const level = capacity.levels.find((l) => l.bitsPerChannel === bits()) || capacity.levels[0];
      const needed = estimatedBytes();
      if (needed === 0) {
        capacityText.textContent = t('capacityEmpty', formatBytes(level.payloadBytes), level.bitsPerChannel);
        meter.style.setProperty('--fill', '0%');
        return;
      }
      const ratio = needed / level.containerBytes;
      capacityText.textContent = t('capacityUsage', formatBytes(needed), formatBytes(level.containerBytes), formatPercent(ratio));
      meter.style.setProperty('--fill', `${Math.min(100, Math.max(1.5, ratio * 100))}%`);
      if (ratio > 1) {
        meter.dataset.state = 'over';
        capacityHint.dataset.state = 'over';
        capacityHint.textContent = t('capacityOver');
        capacityHint.hidden = false;
      } else if (ratio > 0.5) {
        meter.dataset.state = 'warn';
        capacityHint.dataset.state = 'warn';
        capacityHint.textContent = t('capacityWarn');
        capacityHint.hidden = false;
      }
    }

    async function measure(file) {
      const request = ++coverRequest;
      capacity = null;
      measuring = true;
      renderCapacity();
      const body = new FormData();
      body.append('image', file, file.name || 'image');
      try {
        const response = await api('api/capacity', body);
        const result = await response.json();
        if (request !== coverRequest) return;
        capacity = result;
        cover.setInfo(t('imageInfo', result.width, result.height, formatBytes(file.size)));
      } catch (error) {
        if (request !== coverRequest) return;
        const { title, text: message } = describeError(error);
        showAlert(alertBox, 'error', title, message);
      } finally {
        if (request === coverRequest) {
          measuring = false;
          renderCapacity();
        }
      }
    }

    const cover = imageZone('hide-cover-zone', 'hide-cover-input', (file, error) => {
      if (!file) {
        showAlert(alertBox, 'error', t('errorTitle'), error);
        return;
      }
      hideAlert(alertBox);
      measure(file);
    });
    pasteTargets.hide = (file) => cover.set(file);

    function renderFiles() {
      fileList.replaceChildren(...files.map((file, index) => {
        const item = element('li', 'file-item');
        item.append(icon('file'), element('span', 'file-name', file.name), element('span', 'file-size', formatBytes(file.size)));
        const remove = element('button', 'icon-only');
        remove.type = 'button';
        remove.setAttribute('aria-label', t('removeFile', file.name));
        remove.append(icon('x'));
        remove.addEventListener('click', () => {
          files.splice(index, 1);
          renderFiles();
        });
        item.append(remove);
        return item;
      }));
      filesEmpty.hidden = files.length > 0;
      renderCapacity();
    }

    wireFileTarget($('hide-files-zone'), $('hide-files-input'), (list) => {
      files.push(...list);
      renderFiles();
    });

    function renderStrength() {
      const level = passwordStrength(password.value);
      strength.dataset.level = String(level);
      strengthText.textContent = level ? t('strength', t(`strength${level}`)) : '';
      confirm.setAttribute('aria-invalid', String(confirm.value.length > 0 && confirm.value !== password.value));
    }

    text.addEventListener('input', renderCapacity);
    password.addEventListener('input', renderStrength);
    confirm.addEventListener('input', renderStrength);
    for (const radio of form.querySelectorAll('input[name="bits"]')) radio.addEventListener('change', renderCapacity);
    onLanguageChange(() => {
      renderCapacity();
      renderStrength();
      renderFiles();
      if (cover.file && capacity) cover.setInfo(t('imageInfo', capacity.width, capacity.height, formatBytes(cover.file.size)));
    });

    form.addEventListener('submit', async (event) => {
      event.preventDefault();
      hideAlert(alertBox);
      const fail = (message, focus) => {
        showAlert(alertBox, 'error', t('errorTitle'), message);
        focus?.focus();
      };
      if (!cover.file) return fail(t('errNoCover'));
      if (!text.value && files.length === 0) return fail(t('errNothing'), text);
      if (!password.value) return fail(t('errNoPassword'), password);
      if (password.value !== confirm.value) return fail(t('errMismatch'), confirm);
      const uploadBytes = cover.file.size + files.reduce((sum, file) => sum + file.size, 0) + utf8Length(text.value) + 2048;
      if (uploadBytes > maxRequestBytes) return fail(t('errTooBigLocal', formatBytes(uploadBytes), formatBytes(maxRequestBytes)));

      const body = new FormData();
      body.append('cover', cover.file, cover.file.name || 'image');
      if (text.value) body.append('text', text.value);
      for (const file of files) body.append('file', file, file.name);
      body.append('password', password.value);
      body.append('bits', String(bits()));

      setBusy(submit, true, 'hideButton', 'hiding');
      try {
        const response = await api('api/hide', body);
        const name = fileNameFrom(response, 'image.png');
        download(await response.blob(), name);
        password.value = '';
        confirm.value = '';
        renderStrength();
        showAlert(alertBox, 'success', t('hideDoneTitle', name), t('hideDoneText'));
      } catch (error) {
        const described = describeError(error);
        const action = described.suggestedBits
          ? { label: t('useBits', described.suggestedBits), run: () => { setBits(described.suggestedBits); hideAlert(alertBox); } }
          : undefined;
        showAlert(alertBox, 'error', described.title, described.text, action);
      } finally {
        setBusy(submit, false, 'hideButton', 'hiding');
      }
    });

    renderFiles();
    renderStrength();
  }

  // ---------- reveal ----------

  function initReveal() {
    const form = $('reveal-form');
    const alertBox = $('reveal-alert');
    const password = $('reveal-password');
    const submit = $('reveal-submit');
    const results = $('reveal-results');
    const resultsTitle = $('reveal-results-title');
    const messages = $('reveal-messages');
    const fileList = $('reveal-files');
    const fileNote = $('reveal-file-note');
    const zipButton = $('reveal-zip');
    let revealed = null;

    const clearResults = () => {
      revealed = null;
      results.hidden = true;
      messages.replaceChildren();
      fileList.replaceChildren();
    };

    const image = imageZone('reveal-zone', 'reveal-input', (file, error) => {
      clearResults();
      if (!file) {
        showAlert(alertBox, 'error', t('errorTitle'), error);
        return;
      }
      hideAlert(alertBox);
      password.focus();
    });
    pasteTargets.reveal = (file) => image.set(file);

    function renderResults() {
      if (!revealed) return;
      const count = revealed.messages.length + revealed.files.length;
      resultsTitle.textContent = t('foundItems', count);
      messages.replaceChildren(...revealed.messages.map((message, index) => {
        const box = element('div', 'message');
        const head = element('div', 'message-head');
        head.append(element('span', '', t('messageN', index + 1)));
        const copy = element('button', 'button secondary small');
        copy.type = 'button';
        copy.append(icon('copy'), element('span', '', t('copy')));
        copy.addEventListener('click', async () => {
          const ok = await copyText(message);
          copy.lastChild.textContent = ok ? t('copied') : t('copy');
          if (!ok) showAlert(alertBox, 'warn', t('copyFailed'));
          setTimeout(() => { copy.lastChild.textContent = t('copy'); }, 2000);
        });
        head.append(copy);
        box.append(head, element('p', 'message-text', message));
        return box;
      }));
      fileList.replaceChildren(...revealed.files.map((file) => {
        const item = element('li', 'file-item');
        item.append(icon('file'), element('span', 'file-name', file.name), element('span', 'file-size', formatBytes(file.size)));
        const save = element('button', 'icon-only');
        save.type = 'button';
        save.setAttribute('aria-label', t('download', file.name));
        save.append(icon('download'));
        save.addEventListener('click', () => download(base64ToBlob(file.data), file.name));
        item.append(save);
        return item;
      }));
      fileNote.hidden = revealed.files.length === 0;
      zipButton.hidden = revealed.files.length === 0;
      results.hidden = false;
    }

    function revealForm() {
      const body = new FormData();
      body.append('image', image.file, image.file.name || 'image.png');
      body.append('password', password.value);
      return body;
    }

    form.addEventListener('submit', async (event) => {
      event.preventDefault();
      hideAlert(alertBox);
      clearResults();
      if (!image.file) return showAlert(alertBox, 'error', t('errorTitle'), t('errNoImage'));
      if (!password.value) {
        showAlert(alertBox, 'error', t('errorTitle'), t('errNoPassword'));
        return password.focus();
      }
      if (image.file.size > maxRequestBytes) {
        return showAlert(alertBox, 'error', t('errorTitle'), t('errTooBigLocal', formatBytes(image.file.size), formatBytes(maxRequestBytes)));
      }
      setBusy(submit, true, 'revealButton', 'revealing');
      try {
        const response = await api('api/reveal', revealForm());
        revealed = await response.json();
        renderResults();
        results.scrollIntoView({ behavior: 'smooth', block: 'nearest' });
      } catch (error) {
        const { title, text } = describeError(error);
        showAlert(alertBox, 'error', title, text);
      } finally {
        setBusy(submit, false, 'revealButton', 'revealing');
      }
    });

    zipButton.addEventListener('click', async () => {
      if (!image.file || !password.value) return;
      zipButton.disabled = true;
      try {
        const response = await api('api/reveal?format=zip', revealForm());
        download(await response.blob(), fileNameFrom(response, 'files.zip'));
      } catch (error) {
        const { title, text } = describeError(error);
        showAlert(alertBox, 'error', title, text);
      } finally {
        zipButton.disabled = false;
      }
    });

    onLanguageChange(renderResults);
  }

  // ---------- analyze ----------

  function initAnalyze() {
    const alertBox = $('analyze-alert');
    const results = $('analyze-results');
    const rows = $('analyze-rows');
    const bitSelect = $('analyze-bit');
    const plane = $('analyze-plane');
    let planeUrl = null;
    let report = null;
    let request = 0;

    async function analyze() {
      if (!image.file) return;
      const current = ++request;
      hideAlert(alertBox);
      image.setInfo(t('analyzing'));
      const body = new FormData();
      body.append('image', image.file, image.file.name || 'image');
      body.append('bitPlane', bitSelect.value);
      try {
        const response = await api('api/analyze', body);
        const result = await response.json();
        if (current !== request) return;
        report = result;
        if (planeUrl) URL.revokeObjectURL(planeUrl);
        planeUrl = result.bitPlane ? URL.createObjectURL(new Blob([base64ToBlob(result.bitPlane.png)], { type: 'image/png' })) : null;
        render();
      } catch (error) {
        if (current !== request) return;
        results.hidden = true;
        const { title, text } = describeError(error);
        showAlert(alertBox, 'error', title, text);
        image.setInfo(formatBytes(image.file.size));
      }
    }

    function render() {
      if (!report) return;
      image.setInfo(t('imageInfo', report.width, report.height, formatBytes(image.file.size)));
      rows.replaceChildren(...report.channels.map((channel) => {
        const row = document.createElement('tr');
        const name = element('td');
        const dot = element('span', 'channel-dot');
        dot.dataset.channel = channel.channel;
        name.append(dot, document.createTextNode(channel.channel));
        const p = element('td', channel.embeddingProbability > 0.95 ? 'p-high' : '', formatNumber(channel.embeddingProbability, 4));
        row.append(name, element('td', '', formatNumber(channel.onesRatio, 4)), element('td', '', formatNumber(channel.chiSquare, 1)),
          element('td', '', String(channel.degreesOfFreedom)), p);
        return row;
      }));
      if (planeUrl) plane.src = planeUrl;
      plane.alt = t('bitPlaneAlt', bitSelect.value);
      results.hidden = false;
    }

    const image = imageZone('analyze-zone', 'analyze-input', (file, error) => {
      if (!file) {
        showAlert(alertBox, 'error', t('errorTitle'), error);
        return;
      }
      results.hidden = true;
      analyze();
    });
    pasteTargets.analyze = (file) => image.set(file);
    bitSelect.addEventListener('change', analyze);
    onLanguageChange(render);
  }

  // ---------- page ----------

  function initToolbar() {
    $('lang-toggle').addEventListener('click', () => {
      root.lang = lang() === 'th' ? 'en' : 'th';
      storage.set(storageKeys.lang, root.lang);
      applyLanguage();
    });
    const themeColor = document.querySelector('meta[name="theme-color"]');
    const syncThemeColor = () => themeColor.setAttribute('content', root.dataset.theme === 'light' ? '#fcf6fb' : '#16101f');
    $('theme-toggle').addEventListener('click', () => {
      root.dataset.theme = root.dataset.theme === 'light' ? 'dark' : 'light';
      storage.set(storageKeys.theme, root.dataset.theme);
      syncThemeColor();
    });
    syncThemeColor();
  }

  function init() {
    const body = document.body;
    maxRequestBytes = Number(body.dataset.maxRequestBytes) || maxRequestBytes;
    const version = document.querySelector('.footer-version');
    onLanguageChange(() => { version.textContent = t('version', body.dataset.version || ''); });

    initToolbar();
    initTabs();
    initPaste();
    initPasswordToggles();
    initHide();
    initReveal();
    initAnalyze();
    applyLanguage();

    // Dropping a file outside a drop zone would make the browser navigate away from the page.
    for (const type of ['dragover', 'drop']) {
      window.addEventListener(type, (event) => {
        if (event.dataTransfer?.types.includes('Files')) event.preventDefault();
      });
    }
  }

  document.addEventListener('DOMContentLoaded', init);
})();
