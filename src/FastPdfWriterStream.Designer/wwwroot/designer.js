/*
 * FastPdf Report Designer — front-end
 * Pure vanilla ES2020, zero external dependencies (must run on offline intranets).
 *
 * Sections
 *   1. Constants & icons
 *   2. DOM / utility helpers
 *   3. Persistence helpers (localStorage, always guarded)
 *   4. Template model helpers (defaults, normalization, style resolution, samples)
 *   5. State & store (mutations, undo/redo, selection)
 *   6. Server API
 *   7. Fonts (WYSIWYG FontFace loading)
 *   8. Layout & canvas rendering
 *   9. Canvas interactions (select, move, resize, band/column resize, drop)
 *  10. Element commands (add, delete, clipboard, align, bands, columns)
 *  11. Side panel (toolbox & fields)
 *  12. Inspector (forms built from small field descriptors)
 *  13. Dialogs & toasts
 *  14. Document commands (new/open/save/import/export/preview, autosave)
 *  15. Toolbar, keyboard, init
 *
 * Security note: templates may come from other users, so every template-provided
 * string is inserted with textContent / createTextNode — never innerHTML.
 */
(() => {
  'use strict';

  // ════════════════════════════════════════════════════════════════════
  // 1. Constants & icons
  // ════════════════════════════════════════════════════════════════════

  const PAGE_SIZES = {
    A3: [841.89, 1190.55],
    A4: [595.28, 841.89],
    A5: [419.53, 595.28],
    Letter: [612, 792],
    Legal: [612, 1008],
  };
  const PX_PER_PT = 96 / 72;
  const ZOOM_STEPS = [0.25, 0.33, 0.5, 0.67, 0.75, 0.9, 1, 1.1, 1.25, 1.5, 1.75, 2, 2.5, 3, 4];
  const UNDO_LIMIT = 100;
  const LS_DRAFT = 'fastpdf.designer.draft.v1';
  const LS_PREFS = 'fastpdf.designer.prefs.v1';
  const DND_MIME = 'application/x-fastpdf-designer';

  const ELEMENT_BANDS = ['pageHeader', 'reportHeader', 'reportFooter', 'pageFooter'];
  const BAND_ORDER = ['pageHeader', 'reportHeader', 'table', 'reportFooter', 'pageFooter'];
  const BAND_LABELS = {
    pageHeader: 'سرصفحه',
    reportHeader: 'سرگزارش',
    table: 'جدول',
    reportFooter: 'پاورقی گزارش',
    pageFooter: 'پاصفحه',
  };
  const BAND_HINTS = {
    pageHeader: 'بالای همهٔ صفحات',
    reportHeader: 'فقط صفحهٔ اول، بعد از سرصفحه',
    table: 'سطر عنوان + ردیف‌های داده',
    reportFooter: 'بعد از پایان جدول',
    pageFooter: 'پایین همهٔ صفحات',
  };

  const TABLE_DEFAULTS = {
    headerHeight: 20, rowHeight: 16, repeatHeader: true, fitWidth: true,
    headerBackground: '#D9E1F2', altRowBackground: '#F5F7FA', totalsBackground: '#E7E6E6',
    borderColor: '#9E9E9E', borderWidth: 0.5, verticalLines: true, horizontalLines: true,
    showPageTotals: false, pageTotalsLabel: 'جمع صفحه',
    showGrandTotals: false, grandTotalsLabel: 'جمع کل',
    showCarryForward: false, carryForwardLabel: 'نقل از صفحه قبل',
    emptyText: 'داده‌ای برای نمایش وجود ندارد',
    groupHeaderBackground: '#EDEDED', showGroupTotals: false, groupTotalsLabel: 'جمع گروه',
  };
  const WATERMARK_DEFAULTS = { text: 'محرمانه', fontSize: 60, color: '#C00000', opacity: 0.12, angle: 45 };

  const SYSTEM_VARS = [
    { name: 'PageNumber', title: 'شمارهٔ صفحه' },
    { name: 'TotalPages', title: 'تعداد کل صفحات' },
    { name: 'RowNumber', title: 'شمارهٔ ردیف' },
    { name: 'Count', title: 'تعداد ردیف‌ها' },
    { name: 'Today', title: 'تاریخ امروز' },
    { name: 'Today:persian', title: 'تاریخ شمسی امروز' },
    { name: 'Now', title: 'تاریخ و ساعت' },
    { name: 'ReportName', title: 'نام گزارش' },
  ];
  const FORMATS = [
    ['N0', 'عدد صحیح با جداکننده'],
    ['N2', 'عدد با دو رقم اعشار'],
    ['#,0.##', 'عدد با اعشار اختیاری'],
    ['persian', 'تاریخ شمسی'],
    ['yyyy/MM/dd', 'تاریخ میلادی'],
    ['mask:####/##/##', 'ماسک'],
    ['HH:mm', 'ساعت'],
  ];

  const ALIGN_OPTS = [['Start', 'ابتدا'], ['End', 'انتها'], ['Right', 'راست'], ['Center', 'وسط'], ['Left', 'چپ']];
  const VALIGN_OPTS = [['Top', 'بالا'], ['Middle', 'وسط'], ['Bottom', 'پایین']];
  const OVERFLOW_OPTS = [['Shrink', 'کوچک‌کردن قلم'], ['Wrap', 'شکستن خط'], ['Clip', 'برش'], ['Ellipsis', 'سه‌نقطه'], ['Visible', 'بدون محدودیت']];
  const DIRECTION_OPTS = [['Auto', 'خودکار'], ['RightToLeft', 'راست‌به‌چپ'], ['LeftToRight', 'چپ‌به‌راست']];
  const DIGITS_OPTS = [['Persian', 'فارسی (۱۲۳)'], ['Arabic', 'عربی (١٢٣)'], ['Latin', 'لاتین (123)']];
  const AGG_OPTS = [['Sum', 'جمع'], ['Count', 'تعداد'], ['Avg', 'میانگین'], ['Min', 'کمینه'], ['Max', 'بیشینه']];
  const FIELD_TYPES = [['string', 'متن'], ['number', 'عدد'], ['date', 'تاریخ'], ['bool', 'بولی'], ['image', 'تصویر']];
  const SIZE_OPTS = [['A4', 'A4'], ['A3', 'A3'], ['A5', 'A5'], ['Letter', 'Letter'], ['Legal', 'Legal'], ['Custom', 'سفارشی']];
  const TYPE_LABELS = { Text: 'متن', Line: 'خط', Rect: 'مستطیل', Image: 'تصویر' };
  const FIELD_TYPE_LABELS = { string: 'متن', number: 'عدد', date: 'تاریخ', bool: 'بولی', image: 'تصویر' };

  // Element-level inline style keys (ElementDefinition) and column-level ones (ColumnDefinition).
  const STYLE_KEYS = ['font', 'fontSize', 'bold', 'underline', 'color', 'background', 'align', 'vAlign', 'overflow',
    'direction', 'digits', 'padding', 'minFontSize', 'lineSpacing', 'border'];
  const ELEMENT_STYLE_KEYS = ['font', 'fontSize', 'bold', 'underline', 'color', 'background', 'align', 'vAlign',
    'overflow', 'direction', 'digits', 'padding', 'border'];
  const COLUMN_STYLE_KEYS = ['font', 'fontSize', 'bold', 'color', 'align', 'overflow', 'direction'];

  // Canonical camelCase property names of the model, used to normalize imported JSON
  // (the engine reads case-insensitively, so foreign files may use PascalCase/lowercase).
  const KNOWN_KEYS = ['name', 'description', 'version', 'page', 'size', 'landscape', 'width', 'height', 'marginLeft',
    'marginTop', 'marginRight', 'marginBottom', 'rightToLeft', 'digits', 'fonts', 'path', 'boldPath', 'fallback',
    'defaultFont', 'defaultFontSize', 'styles', 'basedOn', 'font', 'fontSize', 'bold', 'underline', 'color',
    'background', 'align', 'vAlign', 'overflow', 'direction', 'padding', 'minFontSize', 'lineSpacing', 'border',
    'sides', 'dashed', 'radius', 'reportHeader', 'pageHeader', 'table', 'reportFooter', 'pageFooter', 'skipFirstPage',
    'elements', 'type', 'x', 'y', 'text', 'style', 'lineColor', 'lineWidth', 'cornerRadius', 'source',
    'keepAspectRatio', 'visibleWhen', 'columns', 'headerHeight', 'rowHeight', 'repeatHeader', 'fitWidth',
    'headerStyle', 'rowStyle', 'totalsStyle', 'headerBackground', 'altRowBackground', 'totalsBackground',
    'borderColor', 'borderWidth', 'verticalLines', 'horizontalLines', 'showPageTotals', 'pageTotalsLabel',
    'showGrandTotals', 'grandTotalsLabel', 'showCarryForward', 'carryForwardLabel', 'emptyText', 'groupBy',
    'groupHeaderText', 'groupHeaderBackground', 'showGroupTotals', 'groupTotalsLabel', 'header', 'field', 'format',
    'headerAlign', 'aggregate', 'negativeColor', 'visible', 'watermark', 'opacity', 'angle', 'parameters', 'fields',
    'title', 'sample', 'maxColumnsPerLine', 'line', 'spanLines', 'details', 'detailSeparator', 'label'];
  const KEY_MAP = new Map(KNOWN_KEYS.map((k) => [k.toLowerCase(), k]));
  const UNSAFE_KEYS = new Set(['__proto__', 'constructor', 'prototype']);

  // Stroke icons (24×24). Static, trusted path data.
  const ICONS = {
    file: 'M14 3H6a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V9zM14 3v6h6M12 12v6M9 15h6',
    folder: 'M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z',
    save: 'M5 3h11l5 5v11a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2zM7 3v5h8V3M7 21v-7h10v7',
    saveAs: 'M5 3h11l5 5v5M12 21H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2M7 3v5h8V3M7 21v-7h5M18 15v6M15 18h6',
    upload: 'M12 15V3M7 8l5-5 5 5M4 15v4a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2v-4',
    download: 'M12 3v12M7 10l5 5 5-5M4 15v4a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2v-4',
    undo: 'M9 14L4 9l5-5M4 9h11a5 5 0 0 1 0 10h-4',
    redo: 'M15 14l5-5-5-5M20 9H9a5 5 0 0 0 0 10h4',
    trash: 'M4 7h16M10 11v6M14 11v6M6 7l1 13h10l1-13M9 7V4h6v3',
    zoomIn: 'M11 4a7 7 0 1 0 0 14 7 7 0 0 0 0-14zM21 21l-5-5M8 11h6M11 8v6',
    zoomOut: 'M11 4a7 7 0 1 0 0 14 7 7 0 0 0 0-14zM21 21l-5-5M8 11h6',
    fit: 'M4 9V4h5M20 9V4h-5M4 15v5h5M20 15v5h-5',
    eye: 'M2 12s4-7 10-7 10 7 10 7-4 7-10 7S2 12 2 12zM12 9a3 3 0 1 0 0 6 3 3 0 0 0 0-6z',
    text: 'M5 7V4h14v3M12 4v16M9 20h6',
    line: 'M4 20L20 4',
    rect: 'M4 6h16v12H4z',
    image: 'M4 5h16v14H4zM4 16l5-5 4 4 3-3 4 4M15.5 8a1.5 1.5 0 1 0 0 3 1.5 1.5 0 0 0 0-3z',
    hash: 'M10 4L8 20M16 4l-2 16M5 9h15M4 15h15',
    calendar: 'M4 6h16v14H4zM4 10h16M8 3v4M16 3v4',
    copy: 'M9 9h11v11H9zM5 15H4V4h11v1',
    plus: 'M12 5v14M5 12h14',
    x: 'M6 6l12 12M18 6L6 18',
    alignLeft: 'M4 3v18M8 6h12v4H8zM8 14h7v4H8z',
    alignRight: 'M20 3v18M4 6h12v4H4zM9 14h7v4H9z',
    alignTop: 'M3 4h18M6 8h4v12H6zM14 8h4v7h-4z',
    alignBottom: 'M3 20h18M6 4h4v12H6zM14 9h4v7h-4z',
    alignHCenter: 'M12 3v18M6 6h12v4H6zM8 14h8v4H8z',
    alignVCenter: 'M3 12h18M6 6h4v12H6zM14 8h4v8h-4z',
    toFront: 'M8 8h12v12H8zM4 16V4h12',
    toBack: 'M4 4h12v12H4zM20 8v12H8',
    chevronRight: 'M9 6l6 6-6 6',
    chevronLeft: 'M15 6l-6 6 6 6',
    table: 'M3 5h18v14H3zM3 10h18M3 15h18M9 10v9M15 10v9',
    band: 'M3 6h18v5H3zM3 14h18v4H3z',
  };

  // ════════════════════════════════════════════════════════════════════
  // 2. DOM / utility helpers
  // ════════════════════════════════════════════════════════════════════

  const SVG_NS = 'http://www.w3.org/2000/svg';
  const $ = (id) => document.getElementById(id);

  /** Create an element. attrs: class, style (object|string), dataset, onXxx handlers, props, attributes. */
  function h(tag, attrs, ...children) {
    const el = document.createElement(tag);
    if (attrs) setAttrs(el, attrs);
    appendKids(el, children);
    return el;
  }

  function setAttrs(el, attrs) {
    for (const k of Object.keys(attrs)) {
      const v = attrs[k];
      if (v === undefined || v === null || v === false) continue;
      if (k === 'class') el.className = v;
      else if (k === 'style') {
        if (typeof v === 'string') el.style.cssText = v;
        else Object.assign(el.style, v);
      } else if (k === 'dataset') Object.assign(el.dataset, v);
      else if (k.startsWith('on') && typeof v === 'function') el.addEventListener(k.slice(2).toLowerCase(), v);
      else if (k === 'value' || k === 'checked' || k === 'selected' || k === 'disabled') el[k] = v;
      else el.setAttribute(k, v === true ? '' : String(v));
    }
  }

  function appendKids(el, kids) {
    for (const c of kids) {
      if (c === undefined || c === null || c === false) continue;
      if (Array.isArray(c)) appendKids(el, c);
      else el.append(c instanceof Node ? c : document.createTextNode(String(c)));
    }
  }

  function svgEl(tag, attrs) {
    const el = document.createElementNS(SVG_NS, tag);
    if (attrs) for (const k of Object.keys(attrs)) if (attrs[k] != null) el.setAttribute(k, String(attrs[k]));
    return el;
  }

  function icon(name) {
    const svg = svgEl('svg', { viewBox: '0 0 24 24', class: 'icon', 'aria-hidden': 'true' });
    svg.append(svgEl('path', { d: ICONS[name] || ICONS.rect }));
    return svg;
  }

  /** Icon + optional label button. */
  function btn(label, iconName, onClick, opts = {}) {
    return h('button', {
      type: 'button',
      class: 'btn ' + (opts.cls || 'small outline'),
      title: opts.title || label || undefined,
      'aria-label': opts.title || label || undefined,
      disabled: !!opts.disabled,
      onClick,
    }, iconName ? icon(iconName) : null, label ? h('span', null, label) : null);
  }

  /** <select> from [[value,label],...]; value set after options exist. */
  function selectEl(options, value, attrs) {
    const sel = h('select', attrs || null);
    for (const [v, label] of options) sel.append(h('option', { value: v }, label));
    if (value != null) sel.value = value;
    return sel;
  }

  const num = (v, def) => {
    if (typeof v === 'number') return Number.isFinite(v) ? v : def;
    if (typeof v === 'string' && v.trim() !== '') {
      const n = Number(v);
      return Number.isFinite(n) ? n : def;
    }
    return def;
  };
  const lower = (s) => String(s == null ? '' : s).toLowerCase();
  const round2 = (v) => Math.round(v * 100) / 100;
  const roundHalf = (v) => Math.round(v * 2) / 2;
  const clamp = (v, lo, hi) => Math.min(hi, Math.max(lo, v));
  const isObj = (v) => v !== null && typeof v === 'object' && !Array.isArray(v);
  const deepClone = (o) => (o == null ? o : JSON.parse(JSON.stringify(o)));
  const fmtPt = (v) => String(Math.round(num(v, 0) * 10) / 10);
  const arrOfObj = (v) => (Array.isArray(v) ? v.filter(isObj) : []);

  function normHex(v) {
    if (typeof v !== 'string') return null;
    let s = v.trim();
    if (!s) return null;
    if (s[0] !== '#') s = '#' + s;
    if (/^#[0-9a-f]{3}$/i.test(s)) s = '#' + s[1] + s[1] + s[2] + s[2] + s[3] + s[3];
    return /^#[0-9a-f]{6}$/i.test(s) ? s.toUpperCase() : null;
  }

  const DIGIT_SETS = { persian: '۰۱۲۳۴۵۶۷۸۹', arabic: '٠١٢٣٤٥٦٧٨٩' };
  function toDigits(str, style) {
    const set = DIGIT_SETS[lower(style)];
    if (!set) return str;
    return String(str).replace(/[0-9]/g, (d) => set[+d]);
  }

  function debounce(fn, ms) {
    let t = 0;
    const wrapped = (...args) => {
      clearTimeout(t);
      t = setTimeout(() => fn(...args), ms);
    };
    wrapped.flush = () => { clearTimeout(t); fn(); };
    return wrapped;
  }

  function cssFamily(name) {
    const clean = String(name || '').replace(/["\\]/g, '').trim();
    return clean ? `"${clean}", Tahoma, sans-serif` : 'Tahoma, sans-serif';
  }

  const fileBaseName = (p) => String(p || '').split(/[\\/]/).pop();

  function slugify(s) {
    const t = String(s || '').trim()
      .replace(/[\\/:*?"<>|#%&{}]+/g, '-')
      .replace(/\s+/g, '-')
      .replace(/-+/g, '-')
      .replace(/^-|-$/g, '');
    return t || 'report';
  }

  /** File name for downloads: some browsers drop non-ASCII download names, so keep it ASCII. */
  function downloadName(ext) {
    const cands = [state.name, state.suggestedName, state.tpl && state.tpl.name];
    for (const c of cands) {
      const ascii = slugify(String(c || '').replace(/[^\x20-\x7E]/g, ' '));
      if (c && ascii !== 'report' && /[A-Za-z0-9]/.test(ascii)) return ascii + ext;
    }
    return 'report-template' + ext;
  }

  function downloadUrl(url, fileName) {
    const a = h('a', { href: url, download: fileName, style: 'display:none' });
    document.body.append(a);
    a.click();
    a.remove();
  }

  function formatTime(ts) {
    try {
      return new Date(ts).toLocaleString('fa-IR');
    } catch (e) {
      return new Date(ts).toISOString();
    }
  }

  // ════════════════════════════════════════════════════════════════════
  // 3. Persistence helpers (every access guarded)
  // ════════════════════════════════════════════════════════════════════

  function lsGet(key) {
    try {
      const s = window.localStorage.getItem(key);
      return s ? JSON.parse(s) : null;
    } catch (e) {
      return null;
    }
  }
  function lsSet(key, val) {
    try {
      window.localStorage.setItem(key, JSON.stringify(val));
      return true;
    } catch (e) {
      return false;
    }
  }

  // ════════════════════════════════════════════════════════════════════
  // 4. Template model helpers
  // ════════════════════════════════════════════════════════════════════

  function newTemplate() {
    const cw = PAGE_SIZES.A4[0] - 40;
    return {
      name: 'گزارش جدید',
      version: 1,
      page: { size: 'A4', landscape: false, marginLeft: 20, marginTop: 20, marginRight: 20, marginBottom: 20 },
      rightToLeft: true,
      digits: 'Persian',
      fonts: [{ name: 'Vazirmatn', path: 'Vazirmatn-Regular.ttf', boldPath: 'Vazirmatn-Bold.ttf' }],
      defaultFont: 'Vazirmatn',
      defaultFontSize: 9,
      styles: {},
      pageHeader: {
        height: 44,
        elements: [{
          type: 'Text', name: 'title', x: 0, y: 8, width: round2(cw), height: 26,
          text: 'عنوان گزارش', fontSize: 15, bold: true, align: 'Center', vAlign: 'Middle',
        }],
      },
      table: {
        columns: [
          { header: 'ردیف', field: '{RowNumber}', width: 40, align: 'Center' },
          { header: 'شرح', field: 'Description', width: 300 },
          { header: 'مبلغ', field: 'Amount', width: 120, format: 'N0', aggregate: 'Sum', align: 'End' },
        ],
        headerHeight: 22,
        rowHeight: 18,
        showGrandTotals: true,
      },
      pageFooter: {
        height: 24,
        elements: [{
          type: 'Text', x: 0, y: 4, width: round2(cw), height: 16,
          text: 'صفحه {PageNumber} از {TotalPages}', align: 'Center', vAlign: 'Middle',
        }],
      },
      parameters: [],
      fields: [
        { name: 'Description', title: 'شرح', type: 'string', sample: 'خرید از فروشگاه' },
        { name: 'Amount', title: 'مبلغ', type: 'number', sample: '1250000' },
      ],
    };
  }

  function camelKey(k) {
    return KEY_MAP.get(k.toLowerCase()) || (k.charAt(0).toLowerCase() + k.slice(1));
  }

  /** Recursively camel-case keys (style dictionary keys are kept verbatim). Drops prototype-polluting keys. */
  function camelize(v, dictKeys) {
    if (Array.isArray(v)) return v.map((x) => camelize(x, false));
    if (!isObj(v)) return v;
    const out = {};
    for (const k of Object.keys(v)) {
      if (UNSAFE_KEYS.has(k)) continue;
      if (dictKeys) {
        out[k] = camelize(v[k], false);
        continue;
      }
      const ck = camelKey(k);
      out[ck] = camelize(v[k], ck === 'styles');
    }
    return out;
  }

  function normalizeTemplate(raw) {
    if (!isObj(raw)) throw new Error('ساختار JSON قالب معتبر نیست.');
    const t = camelize(raw, false);
    if (typeof t.name !== 'string') t.name = 'Report';
    if (!isObj(t.page)) t.page = {};
    t.fonts = arrOfObj(t.fonts);
    if (!isObj(t.styles)) t.styles = {};
    for (const k of Object.keys(t.styles)) if (!isObj(t.styles[k])) delete t.styles[k];
    t.parameters = arrOfObj(t.parameters);
    t.fields = arrOfObj(t.fields);
    for (const k of ELEMENT_BANDS) {
      if (!isObj(t[k])) {
        delete t[k];
        continue;
      }
      t[k].elements = arrOfObj(t[k].elements);
      if (num(t[k].height, null) == null) t[k].height = 40;
    }
    if (!isObj(t.table)) delete t.table;
    else t.table.columns = arrOfObj(t.table.columns);
    if (!isObj(t.watermark)) delete t.watermark;
    return t;
  }

  function defaultTable(t) {
    const columns = [{ header: 'ردیف', field: '{RowNumber}', width: 40, align: 'Center' }];
    for (const f of (t.fields || []).slice(0, 5)) columns.push(columnForField(f));
    return { columns, headerHeight: 20, rowHeight: 16 };
  }

  function columnForField(f) {
    const col = { header: f.title || f.name, field: f.name, width: 80 };
    if (lower(f.type) === 'number') {
      col.format = 'N0';
      col.align = 'End';
    } else if (lower(f.type) === 'date') {
      col.format = 'persian';
      col.align = 'Center';
    }
    return col;
  }

  const normType = (t) => {
    const s = lower(t);
    if (s === 'line') return 'Line';
    if (s === 'rect' || s === 'rectangle') return 'Rect';
    if (s === 'image') return 'Image';
    return 'Text';
  };

  const tplRtl = () => state.tpl.rightToLeft !== false;

  /** Table property with the engine's C# default when absent (explicit null stays null). */
  function tv(key) {
    const t = state.tpl.table;
    const v = t ? t[key] : undefined;
    return v === undefined ? TABLE_DEFAULTS[key] : v;
  }

  function pageInfo() {
    const p = state.tpl.page || {};
    let w;
    let hgt;
    const size = String(p.size || 'A4');
    if (lower(size) === 'custom' && num(p.width, 0) > 0 && num(p.height, 0) > 0) {
      w = num(p.width, 595.28);
      hgt = num(p.height, 841.89);
    } else {
      const key = Object.keys(PAGE_SIZES).find((k) => lower(k) === lower(size)) || 'A4';
      [w, hgt] = PAGE_SIZES[key];
      if (p.landscape) [w, hgt] = [hgt, w];
    }
    const ml = num(p.marginLeft, 20);
    const mt = num(p.marginTop, 20);
    const mr = num(p.marginRight, 20);
    const mb = num(p.marginBottom, 20);
    return { w, h: hgt, ml, mt, mr, mb, cw: Math.max(10, w - ml - mr) };
  }

  function findStyleKey(name) {
    if (!name) return null;
    const st = state.tpl.styles;
    if (Object.prototype.hasOwnProperty.call(st, name)) return name;
    const l = lower(name);
    return Object.keys(st).find((k) => lower(k) === l) || null;
  }
  const findStyle = (name) => {
    const k = findStyleKey(name);
    return k ? state.tpl.styles[k] : null;
  };
  const styleNames = () => Object.keys(state.tpl.styles);
  const fontNames = () => state.tpl.fonts.map((f) => f.name).filter(Boolean);

  function selectedDataSource() {
    return state.server.datasources.find((d) => d.name === state.dataSource) || null;
  }

  /** All known fields: template first, then (not yet imported) data-source fields. */
  function knownFields() {
    const out = state.tpl.fields.filter((f) => f.name).map((f) => ({ ...f, origin: 'tpl' }));
    const ds = selectedDataSource();
    if (ds && Array.isArray(ds.fields)) {
      for (const f of ds.fields) {
        if (!isObj(f) || !f.name) continue;
        if (!out.some((x) => lower(x.name) === lower(f.name))) out.push({ ...f, origin: 'ds' });
      }
    }
    return out;
  }
  function knownParams() {
    const out = state.tpl.parameters.filter((f) => f.name).map((f) => ({ ...f, origin: 'tpl' }));
    const ds = selectedDataSource();
    if (ds && Array.isArray(ds.parameters)) {
      for (const f of ds.parameters) {
        if (!isObj(f) || !f.name) continue;
        if (!out.some((x) => lower(x.name) === lower(f.name))) out.push({ ...f, origin: 'ds' });
      }
    }
    return out;
  }
  function findFieldInfo(name) {
    const l = lower(name);
    return knownFields().find((f) => lower(f.name) === l) || knownParams().find((f) => lower(f.name) === l) || null;
  }

  /**
   * Resolve a style like the engine's StyleResolver:
   * defaults ← named styles (with basedOn chains) ← inline overrides.
   */
  function resolveStyle(named, inline, inlineKeys, defaults) {
    const acc = Object.assign({}, defaults || {});
    for (const n of named) applyNamed(acc, n, 0);
    if (inline) for (const k of inlineKeys) if (inline[k] != null) acc[k] = inline[k];
    const fs = num(acc.fontSize, num(state.tpl.defaultFontSize, 9));
    return {
      font: acc.font || state.tpl.defaultFont || null,
      fontSize: fs,
      bold: acc.bold === true,
      underline: acc.underline === true,
      color: normHex(acc.color) || '#000000',
      background: normHex(acc.background),
      align: acc.align || 'Start',
      vAlign: acc.vAlign || 'Middle',
      overflow: acc.overflow || 'Shrink',
      direction: acc.direction || 'Auto',
      digits: acc.digits || state.tpl.digits || 'Persian',
      padding: num(acc.padding, 2),
      minFontSize: num(acc.minFontSize, Math.min(4, fs)),
      lineSpacing: num(acc.lineSpacing, 1),
      border: isObj(acc.border) ? acc.border : null,
    };
  }
  function applyNamed(acc, name, depth) {
    if (!name || depth > 10) return;
    const s = findStyle(name);
    if (!s) return;
    applyNamed(acc, s.basedOn, depth + 1);
    for (const k of STYLE_KEYS) if (s[k] != null) acc[k] = s[k];
  }

  /** Split "{a} text {{literal}}" into literal / expression parts (mirrors the engine's parser). */
  function splitExpr(text) {
    const out = [];
    let lit = '';
    let i = 0;
    while (i < text.length) {
      const c = text[i];
      if (c === '{' && text[i + 1] === '{') { lit += '{'; i += 2; continue; }
      if (c === '}' && text[i + 1] === '}') { lit += '}'; i += 2; continue; }
      if (c === '{') {
        const end = text.indexOf('}', i + 1);
        if (end < 0) { lit += text.slice(i); break; }
        if (lit) { out.push({ text: lit }); lit = ''; }
        out.push({ expr: true, text: text.slice(i, end + 1), inner: text.slice(i + 1, end).trim() });
        i = end + 1;
        continue;
      }
      lit += c;
      i++;
    }
    if (lit) out.push({ text: lit });
    return out;
  }

  /** Split "Name:format" at the first ':' outside parentheses. */
  function splitFormat(inner) {
    let depth = 0;
    for (let k = 0; k < inner.length; k++) {
      const c = inner[k];
      if (c === '(') depth++;
      else if (c === ')') depth--;
      else if (c === ':' && depth === 0) return { name: inner.slice(0, k).trim(), fmt: inner.slice(k + 1) };
    }
    return { name: inner.trim(), fmt: '' };
  }

  function parseNum(v) {
    if (typeof v === 'number') return Number.isFinite(v) ? v : null;
    const s = String(v == null ? '' : v).replace(/,/g, '').trim();
    if (!/^[-+]?\d+(\.\d+)?$/.test(s)) return null;
    return Number(s);
  }

  function groupNum(n, minD, maxD) {
    return n.toLocaleString('en-US', { minimumFractionDigits: minD, maximumFractionDigits: maxD });
  }

  /** Approximate the engine's formatting for designer samples. */
  function formatSample(v, fmt) {
    if (v == null) return null;
    const f = String(fmt || '').trim();
    if (!f) return String(v);
    const lf = f.toLowerCase();
    if (lf.startsWith('mask:')) {
      const mask = f.slice(5);
      const chars = String(v).replace(/[^0-9A-Za-z\u06F0-\u06F9]/g, '');
      let k = 0;
      let out = '';
      for (const ch of mask) {
        if (ch === '#') {
          if (k >= chars.length) break;
          out += chars[k++];
        } else out += ch;
      }
      return out;
    }
    const n = parseNum(v);
    if (n != null) {
      const m = /^n(\d*)$/i.exec(f);
      if (m) {
        const d = m[1] === '' ? 2 : Math.min(10, +m[1]);
        return groupNum(n, d, d);
      }
      if (lf === 'money') return groupNum(n, 0, 0);
      if (/^[#0,.]+$/.test(f)) {
        const dec = f.split('.')[1] || '';
        const minD = (dec.match(/0/g) || []).length;
        return f.includes(',') ? groupNum(n, minD, dec.length) : n.toFixed(dec.length);
      }
    }
    return String(v);
  }

  function sampleOf(name, rowIdx) {
    const l = lower(name);
    if (l === 'rownumber' || l === 'line' || l === '#') return String(rowIdx + 1);
    if (l === 'pagenumber' || l === 'page') return '1';
    if (l === 'totalpages' || l === 'pagecount' || l === 'totalpagecount') return '3';
    if (l === 'count') return '4';
    if (l === 'reportname') return state.tpl.name || '';
    if (l === 'today') return '1403/05/12';
    if (l === 'now') return '1403/05/12 10:30';
    const f = findFieldInfo(name);
    if (f && f.sample != null && f.sample !== '') return String(f.sample);
    return null;
  }

  /** Sample text for a data cell: bare field name or an expression with {..} tokens. */
  function cellSample(col, rowIdx) {
    if (Array.isArray(col.details) && col.details.length) {
      const sep = col.detailSeparator != null ? col.detailSeparator : '  •  ';
      return col.details.filter((d) => d && d.field).map((d) => {
        const v = sampleOf(d.field, rowIdx);
        return (d.label ? d.label + ': ' : '') + (v == null ? `{${d.field}}` : formatSample(v, d.format));
      }).join(sep);
    }
    const f = String(col.field || '').trim();
    if (!f) return '';
    if (!f.includes('{')) {
      const v = sampleOf(f, rowIdx);
      return v == null ? `{${f}}` : formatSample(v, col.format);
    }
    return f.replace(/\{([^{}]*)\}/g, (m, inner) => {
      const { name, fmt } = splitFormat(inner);
      const v = sampleOf(name, rowIdx);
      return v == null ? m : formatSample(v, fmt || col.format);
    });
  }

  function aggregateSample(col, agg) {
    const l = lower(agg);
    if (l === 'count') return '4';
    const f = String(col.field || '').trim();
    const v = f.includes('{') ? null : sampleOf(f, 0);
    const n = parseNum(v);
    if (n == null) return AGG_OPTS.find(([k]) => lower(k) === l)?.[1] || agg;
    return formatSample(l === 'sum' ? n * 4 : n, col.format || 'N0');
  }

  // ════════════════════════════════════════════════════════════════════
  // 5. State & store
  // ════════════════════════════════════════════════════════════════════

  const state = {
    tpl: null,
    name: null, // name of the template on the server (null = never saved there)
    suggestedName: null,
    dirty: false,
    // Selection: null (document) | {kind:'els', items:[{band,index}]} | {kind:'band', band}
    //            | {kind:'table'} | {kind:'col', index}
    sel: null,
    scale: PX_PER_PT,
    snap: 2,
    showGrid: true,
    undo: [],
    redo: [],
    coalesce: null,
    gestureSnap: null,
    server: { checked: false, online: false, readOnly: false, version: null, fonts: [], datasources: [] },
    dataSource: '',
    previewRows: 50,
    sideTab: 'tools',
    clipboard: null,
    dragPayload: null,
    styleSel: null,
    secOpen: new Map(), // remembered open/closed state of inspector sections
    lastInspKey: null,
    imageTarget: null,
  };

  function snapshot() {
    return JSON.stringify(state.tpl);
  }

  function pushUndo(snap) {
    state.undo.push(snap);
    if (state.undo.length > UNDO_LIMIT) state.undo.shift();
    state.redo.length = 0;
  }

  /**
   * Apply a change to the template. Records an undo snapshot when something actually changed.
   * opts.inspector: re-render the inspector (structural changes); opts.coalesce: merge rapid edits
   * of the same field (typing) into one undo step.
   */
  function mutate(fn, opts = {}) {
    const before = snapshot();
    fn(state.tpl);
    if (snapshot() === before) return false;
    const now = Date.now();
    const co = opts.coalesce;
    if (co && state.coalesce && state.coalesce.key === co && now - state.coalesce.t < 1500) {
      state.coalesce.t = now;
    } else {
      pushUndo(before);
      state.coalesce = co ? { key: co, t: now } : null;
    }
    changed(opts);
    return true;
  }

  function changed(opts = {}) {
    state.dirty = true;
    validateSelection();
    renderCanvas();
    if (opts.inspector) renderInspector();
    renderSidePanelSoon();
    updateChrome();
    scheduleAutosave();
    ensureFontsSoon();
  }

  /** Start a pointer gesture (drag/resize): live edits, one undo step at the end. */
  function beginGesture() {
    state.gestureSnap = snapshot();
  }
  function endGesture() {
    const before = state.gestureSnap;
    state.gestureSnap = null;
    if (before == null) return;
    if (before === snapshot()) {
      renderCanvas();
      return;
    }
    pushUndo(before);
    state.coalesce = null;
    changed({ inspector: true });
  }

  function undo() {
    if (!state.undo.length) return;
    state.redo.push(snapshot());
    state.tpl = JSON.parse(state.undo.pop());
    afterHistory();
  }
  function redo() {
    if (!state.redo.length) return;
    state.undo.push(snapshot());
    state.tpl = JSON.parse(state.redo.pop());
    afterHistory();
  }
  function afterHistory() {
    state.coalesce = null;
    state.dirty = true;
    validateSelection();
    ensureFonts();
    renderAll();
    scheduleAutosave();
  }

  /** Replace the whole document (new/open/import/restore). */
  function setTemplate(tpl, name, dirty) {
    state.tpl = tpl;
    state.name = name || null;
    state.dirty = !!dirty;
    state.undo = [];
    state.redo = [];
    state.coalesce = null;
    state.sel = null;
    state.styleSel = null;
    ensureFonts();
    renderAll();
    scheduleAutosave();
  }

  function select(sel) {
    state.sel = sel;
    renderCanvas();
    renderInspector();
    updateChrome();
  }

  function validateSelection() {
    const s = state.sel;
    if (!s) return;
    const t = state.tpl;
    if (s.kind === 'band' && !t[s.band]) state.sel = null;
    else if (s.kind === 'els') {
      s.items = s.items.filter((it) => t[it.band] && t[it.band].elements && t[it.band].elements[it.index]);
      if (!s.items.length) state.sel = null;
    } else if (s.kind === 'table' && !t.table) state.sel = null;
    else if (s.kind === 'col' && !(t.table && t.table.columns[s.index])) state.sel = t.table ? { kind: 'table' } : null;
  }

  function selectedElements() {
    if (!state.sel || state.sel.kind !== 'els') return [];
    return state.sel.items
      .map((it) => ({ band: it.band, index: it.index, el: state.tpl[it.band] && state.tpl[it.band].elements[it.index] }))
      .filter((x) => x.el);
  }

  function isElSelected(band, index) {
    return !!(state.sel && state.sel.kind === 'els' && state.sel.items.some((it) => it.band === band && it.index === index));
  }

  function selectionKey() {
    const s = state.sel;
    if (!s) return 'doc';
    if (s.kind === 'els') return 'els:' + s.items.map((i) => i.band + i.index).join(',');
    if (s.kind === 'band') return 'band:' + s.band;
    if (s.kind === 'col') return 'col:' + s.index;
    return s.kind;
  }

  // ════════════════════════════════════════════════════════════════════
  // 6. Server API
  // ════════════════════════════════════════════════════════════════════

  /** Base path = directory the page is served from, e.g. "/pdf-designer/". */
  const BASE = (() => {
    const p = window.location.pathname || '/';
    return p.endsWith('/') ? p : p.slice(0, p.lastIndexOf('/') + 1);
  })();

  class ApiError extends Error {
    constructor(status, message) {
      super(message);
      this.status = status;
    }
  }

  async function errorText(res) {
    let txt = '';
    try {
      txt = await res.text();
    } catch (e) { /* ignore */ }
    try {
      const j = JSON.parse(txt);
      if (isObj(j)) {
        const parts = [j.title, j.detail, j.error, j.message].filter((x) => typeof x === 'string' && x);
        if (isObj(j.errors)) {
          for (const v of Object.values(j.errors)) parts.push(Array.isArray(v) ? v.join(' ') : String(v));
        }
        if (parts.length) txt = parts.join('\n');
      }
    } catch (e) { /* not JSON */ }
    txt = String(txt || '').trim();
    if (/^\s*<(!doctype|html)/i.test(txt)) txt = '';
    if (txt.length > 1200) txt = txt.slice(0, 1200) + '…';
    return `خطای سرور (${res.status}): ${txt || res.statusText || 'نامشخص'}`;
  }

  async function api(path, opts = {}) {
    let res;
    try {
      res = await fetch(BASE + path, {
        method: opts.method || 'GET',
        headers: opts.body !== undefined ? { 'Content-Type': 'application/json' } : undefined,
        body: opts.body,
        cache: 'no-store',
        credentials: 'same-origin',
      });
    } catch (e) {
      throw new ApiError(0, 'ارتباط با سرور برقرار نشد.');
    }
    if (!res.ok) throw new ApiError(res.status, await errorText(res));
    if (opts.raw) return res;
    if (res.status === 204) return null;
    const ct = res.headers.get('content-type') || '';
    if (ct.includes('json')) return res.json();
    const txt = await res.text();
    if (opts.json) {
      try {
        return JSON.parse(txt);
      } catch (e) {
        throw new ApiError(res.status, 'پاسخ سرور JSON معتبر نیست.');
      }
    }
    return txt;
  }

  const tplPath = (name) => 'api/templates/' + encodeURIComponent(name);

  async function connectServer() {
    const srv = state.server;
    try {
      const info = await api('api/info', { json: true });
      if (!isObj(info)) throw new Error('bad info');
      srv.online = true;
      srv.readOnly = !!info.readOnly;
      srv.version = info.version || null;
    } catch (e) {
      srv.online = false;
    }
    srv.checked = true;
    if (srv.online) {
      const [fonts, ds] = await Promise.allSettled([api('api/fonts', { json: true }), api('api/datasources', { json: true })]);
      srv.fonts = fonts.status === 'fulfilled' && Array.isArray(fonts.value)
        ? fonts.value.filter((f) => isObj(f) && typeof f.file === 'string' && f.file) : [];
      srv.datasources = ds.status === 'fulfilled' && Array.isArray(ds.value)
        ? ds.value.filter((d) => isObj(d) && typeof d.name === 'string' && d.name) : [];
      if (fonts.status === 'rejected') toast('فهرست فونت‌های سرور دریافت نشد.\n' + fonts.reason.message, 'error');
      if (ds.status === 'rejected') toast('فهرست منابع داده دریافت نشد.\n' + ds.reason.message, 'error');
      loadUiFont();
    }
    if (!srv.datasources.some((d) => d.name === state.dataSource)) {
      state.dataSource = srv.datasources.length ? srv.datasources[0].name : '';
    }
    ensureFonts();
    renderAll();
  }

  // ════════════════════════════════════════════════════════════════════
  // 7. Fonts — load template fonts with FontFace so the canvas is WYSIWYG
  // ════════════════════════════════════════════════════════════════════

  const loadedFaces = new Map();
  let fontErrorShown = false;

  function fontUrl(path) {
    if (!path || !state.server.online) return null;
    const file = lower(fileBaseName(path));
    const m = state.server.fonts.find((f) => lower(f.file) === file);
    return m ? BASE + 'api/fonts/' + encodeURIComponent(m.file) : null;
  }

  function loadFace(family, path, weight) {
    if (typeof FontFace === 'undefined') return;
    const url = fontUrl(path);
    if (!url) return;
    const fam = String(family).replace(/["\\]/g, '').trim();
    if (!fam) return;
    const key = fam + '|' + weight + '|' + url;
    if (loadedFaces.has(key)) return;
    let face;
    try {
      face = new FontFace(fam, `url("${url}")`, { weight, style: 'normal' });
    } catch (e) {
      return;
    }
    loadedFaces.set(key, face);
    face.load().then((f) => {
      document.fonts.add(f);
      scheduleCanvasRender();
    }).catch(() => {
      if (!fontErrorShown) {
        fontErrorShown = true;
        toast(`بارگذاری فونت «${fam}» ناموفق بود.`, 'error');
      }
    });
  }

  function ensureFonts() {
    if (!state.tpl) return;
    for (const f of state.tpl.fonts) {
      if (!f || !f.name) continue;
      loadFace(f.name, f.path, '400');
      if (f.boldPath) loadFace(f.name, f.boldPath, '700');
    }
  }
  const ensureFontsSoon = debounce(ensureFonts, 600);

  /** UI font = first (regular) font offered by the server. */
  function loadUiFont() {
    const list = state.server.fonts;
    if (!list.length || typeof FontFace === 'undefined') return;
    const regular = list.find((f) => !/bold/i.test(f.file)) || list[0];
    const bold = list.find((f) => f !== regular && /bold/i.test(f.file) && (!regular.family || f.family === regular.family));
    const mk = (f, weight) => new FontFace('DesignerUI', `url("${BASE}api/fonts/${encodeURIComponent(f.file)}")`, { weight });
    const faces = [mk(regular, '400')];
    if (bold) faces.push(mk(bold, '700'));
    Promise.allSettled(faces.map((f) => f.load())).then((results) => {
      let ok = false;
      for (const r of results) {
        if (r.status === 'fulfilled') {
          document.fonts.add(r.value);
          ok = true;
        }
      }
      if (ok) document.documentElement.style.setProperty('--ui-font', '"DesignerUI", system-ui, Tahoma, sans-serif');
    });
  }

  // ════════════════════════════════════════════════════════════════════
  // 8. Layout & canvas rendering
  // ════════════════════════════════════════════════════════════════════

  const px = (pt) => (pt * state.scale) + 'px';

  /**
   * Table geometry, mirroring the engine (ReportRenderer): visible columns may be split into
   * several sub-lines per record (explicit column.line, or table.maxColumnsPerLine); each line
   * is laid out from the start side (right in RTL). Positions are pt relative to the band.
   */
  function tableGeometry(cw) {
    const t = state.tpl.table;
    const rtl = tplRtl();
    const vis = t.columns.map((c, i) => ({ c, i })).filter((x) => x.c.visible !== false);
    const lineOf = vis.map(() => 0);
    let span = vis.map((x) => !!x.c.spanLines);
    if (vis.some((x) => num(x.c.line, 0) > 0)) {
      vis.forEach((x, k) => { lineOf[k] = span[k] ? 0 : Math.max(0, Math.floor(num(x.c.line, 0))); });
    } else {
      const mx = Math.floor(num(tv('maxColumnsPerLine'), 0));
      if (mx > 0 && vis.length > mx) {
        const flow = vis.map((x, k) => k).filter((k) => !span[k]);
        const nSpan = vis.length - flow.length;
        const lines = Math.ceil(flow.length / Math.max(1, mx - nSpan));
        const per = Math.ceil(flow.length / Math.max(1, lines));
        flow.forEach((k, j) => { lineOf[k] = Math.floor(j / per); });
      }
    }
    const lineCount = vis.length ? Math.max(...lineOf) + 1 : 1;
    if (lineCount === 1) span = span.map(() => false);
    // Shared slot grid (as in the engine): every line's columns sit under the same slots;
    // spanLines columns take a slot for the full record height; a short line's last column stretches.
    const spanIdx = vis.map((x, k) => k).filter((k) => span[k]);
    const perLine = [];
    for (let l = 0; l < lineCount; l++) perLine.push(vis.map((x, k) => k).filter((k) => !span[k] && lineOf[k] === l));
    const flowSlots = Math.max(0, ...perLine.map((a) => a.length));
    const slotW = [];
    spanIdx.forEach((k) => slotW.push(Math.max(1, num(vis[k].c.width, 60))));
    for (let j = 0; j < flowSlots; j++) {
      slotW.push(Math.max(...perLine.filter((a) => j < a.length).map((a) => Math.max(1, num(vis[a[j]].c.width, 60)))));
    }
    const sum = Math.max(1, slotW.reduce((a, b) => a + b, 0));
    const fit = !!tv('fitWidth');
    const tableW = fit ? cw : Math.min(cw, sum);
    const scale = tableW / sum;
    const tableLeft = rtl ? cw - tableW : 0;
    const slotX = [];
    let cur = rtl ? tableLeft + tableW : tableLeft;
    for (let j = 0; j < slotW.length; j++) {
      slotW[j] *= scale;
      if (rtl) { cur -= slotW[j]; slotX.push(cur); } else { slotX.push(cur); cur += slotW[j]; }
    }
    const geoOf = new Array(vis.length);
    spanIdx.forEach((k, j) => { geoOf[k] = [j, 1]; });
    perLine.forEach((a) => a.forEach((k, j) => { geoOf[k] = [spanIdx.length + j, j === a.length - 1 ? flowSlots - j : 1]; }));
    const cols = vis.map((x, k) => {
      const [slot, count] = geoOf[k];
      let x1 = Infinity;
      let x2 = -Infinity;
      for (let j = slot; j < slot + count; j++) { x1 = Math.min(x1, slotX[j]); x2 = Math.max(x2, slotX[j] + slotW[j]); }
      return { c: x.c, i: x.i, line: lineOf[k], span: span[k], width: x2 - x1, left: x1, scale };
    });
    // Horizontal range of non-spanning columns (for the separators between sub-lines).
    let fx1 = tableLeft;
    let fx2 = tableLeft + tableW;
    cols.forEach((c) => { if (c.span) { if (rtl) fx2 = Math.min(fx2, c.left); else fx1 = Math.max(fx1, c.left + c.width); } });
    return { cols, lineCount, tableLeft, tableW, flowX1: fx1, flowX2: fx2 };
  }

  /** Preview rows: header + optional carry/group rows + 4 sample records + enabled totals rows. */
  function tableRows(geo) {
    const lc = geo.lineCount;
    const rh = Math.max(1, num(tv('rowHeight'), 16));
    const hh = Math.max(1, num(tv('headerHeight'), 20));
    const tot = Math.max(rh, 14); // engine: TotalsHeight = max(rowHeight, 14)
    const rows = [{ kind: 'header', sub: hh, h: hh * lc }];
    if (tv('showCarryForward')) rows.push({ kind: 'carry', sub: tot, h: tot * lc });
    const grouped = !!tv('groupBy');
    if (grouped) rows.push({ kind: 'group', sub: tot, h: tot, full: true });
    for (let i = 0; i < 4; i++) rows.push({ kind: 'data', sub: rh, h: rh * lc, i });
    if (grouped && tv('showGroupTotals')) rows.push({ kind: 'groupTotal', sub: tot, h: tot * lc });
    if (tv('showPageTotals')) rows.push({ kind: 'pageTotal', sub: tot, h: tot * lc });
    if (tv('showGrandTotals')) rows.push({ kind: 'grandTotal', sub: tot, h: tot * lc });
    return rows;
  }

  /**
   * Vertical stacking of the bands (pt). Missing bands get a small placeholder strip so they
   * can be added in place. The page footer sits at the bottom of the page unless pushed down.
   */
  function computeLayout() {
    const P = pageInfo();
    const phPt = 22 / state.scale;
    const bands = {};
    const list = [];
    let y = P.mt;
    for (const key of ['pageHeader', 'reportHeader', 'table', 'reportFooter']) {
      const b = state.tpl[key];
      let height;
      if (!b) height = phPt;
      else if (key === 'table') height = tableRows(tableGeometry(P.cw)).reduce((a, r) => a + r.h, 0);
      else height = Math.max(1, num(b.height, 40));
      const info = { key, top: y, height, missing: !b };
      bands[key] = info;
      list.push(info);
      y += height;
    }
    const pf = state.tpl.pageFooter;
    const fh = pf ? Math.max(1, num(pf.height, 40)) : phPt;
    const footerTop = Math.max(P.h - P.mb - fh, y);
    const info = { key: 'pageFooter', top: footerTop, height: fh, missing: !pf };
    bands.pageFooter = info;
    list.push(info);
    const canvasH = Math.max(P.h, footerTop + fh + P.mb);
    return { P, bands, list, canvasH, overflow: canvasH > P.h + 0.01 };
  }

  /** Band exactly under page-y (pt), or null. */
  function bandHit(L, y) {
    for (const b of L.list) if (y >= b.top && y < b.top + b.height) return b;
    return null;
  }

  /** Existing element band under y, or the nearest one. */
  function elementBandAt(L, y) {
    let best = null;
    let bestD = Infinity;
    for (const b of L.list) {
      if (b.missing || !ELEMENT_BANDS.includes(b.key)) continue;
      if (y >= b.top && y < b.top + b.height) return b.key;
      const d = y < b.top ? b.top - y : y - (b.top + b.height);
      if (d < bestD) {
        bestD = d;
        best = b.key;
      }
    }
    return best;
  }

  let canvasRaf = 0;
  function scheduleCanvasRender() {
    if (canvasRaf) return;
    canvasRaf = requestAnimationFrame(() => {
      canvasRaf = 0;
      renderCanvas();
    });
  }

  function renderCanvas() {
    if (!state.tpl) return;
    const page = $('page');
    const s = state.scale;
    const L = computeLayout();
    const P = L.P;
    page.replaceChildren();
    page.style.width = px(P.w);
    page.style.height = px(L.canvasH);

    page.append(h('div', {
      class: 'margin-guide',
      style: { left: px(P.ml), top: px(P.mt), width: px(P.cw), height: px(Math.max(0, P.h - P.mt - P.mb)) },
    }));
    if (L.overflow) {
      page.append(h('div', { class: 'page-overflow', style: { top: px(P.h), height: px(L.canvasH - P.h) } }));
      page.append(h('div', { class: 'page-end', style: { top: px(P.h) } },
        h('span', null, 'پایان صفحه — محتوای ثابت از ارتفاع صفحه بیشتر است')));
    }
    renderWatermark(page, P);

    let lastTabBottom = -Infinity;
    for (const b of L.list) {
      if (b.missing) {
        page.append(renderPlaceholder(b, P));
        continue;
      }
      page.append(renderBand(b, P));
      const top = Math.max(b.top * s, lastTabBottom + 2);
      page.append(renderBandTab(b, P, top));
      lastTabBottom = top + 22;
    }
    applyShrink(page);
    renderStatusPage();
  }

  function renderWatermark(page, P) {
    const wm = state.tpl.watermark;
    if (!isObj(wm) || !wm.text) return;
    const s = state.scale;
    const d = h('div', { class: 'watermark', dir: 'auto' }, String(wm.text));
    Object.assign(d.style, {
      left: px(P.w / 2),
      top: px(P.h / 2),
      fontFamily: cssFamily(wm.font || state.tpl.defaultFont),
      fontSize: (num(wm.fontSize, 60) * s) + 'px',
      color: normHex(wm.color) || '#C00000',
      opacity: String(clamp(num(wm.opacity, 0.12), 0, 1)),
      transform: `translate(-50%, -50%) rotate(${-num(wm.angle, 45)}deg)`,
    });
    page.append(d);
  }

  function renderPlaceholder(b, P) {
    const s = state.scale;
    return h('div', {
      class: 'band-placeholder',
      dataset: { band: b.key, missing: '1' },
      style: { left: px(P.ml), top: (b.top * s + 2) + 'px', width: px(P.cw), height: Math.max(4, b.height * s - 4) + 'px' },
    }, h('button', {
      type: 'button',
      title: BAND_HINTS[b.key],
      onClick: () => addBand(b.key),
    }, '+ افزودن ' + BAND_LABELS[b.key]));
  }

  function isBandSelected(key) {
    const s = state.sel;
    if (!s) return false;
    if (key === 'table') return s.kind === 'table' || s.kind === 'col';
    return s.kind === 'band' && s.band === key;
  }

  function renderBand(b, P) {
    const key = b.key;
    const band = state.tpl[key];
    const div = h('div', {
      class: 'band' + (isBandSelected(key) ? ' selected' : '') + (key !== 'table' && state.showGrid ? ' grid' : ''),
      dataset: { band: key },
      style: { left: px(P.ml), top: px(b.top), width: px(P.cw), height: px(b.height) },
    });
    if (key !== 'table') {
      const bg = normHex(band.background);
      if (bg) div.style.backgroundColor = bg;
      if (state.showGrid) applyGrid(div);
    }
    div.addEventListener('pointerdown', (e) => onBandPointerDown(e, key));
    if (key === 'table') {
      renderTable(div, P);
    } else {
      band.elements.forEach((el, i) => div.append(renderElement(el, key, i)));
      const rz = h('div', {
        class: 'band-resize',
        title: 'برای تغییر ارتفاع باند بکشید',
        style: key === 'pageFooter' ? { top: '-4px' } : { bottom: '-4px' },
      });
      rz.addEventListener('pointerdown', (e) => startBandResize(e, key));
      div.append(rz);
    }
    return div;
  }

  function applyGrid(div) {
    let step = state.snap > 0 ? state.snap : 10;
    let g = step * state.scale;
    while (g < 7) {
      step *= 2;
      g = step * state.scale;
    }
    div.style.backgroundImage = 'radial-gradient(circle, rgba(110,122,145,.42) 0.75px, transparent 1.1px)';
    div.style.backgroundSize = `${g}px ${g}px`;
    div.style.backgroundPosition = `${-g / 2}px ${-g / 2}px`;
  }

  function renderBandTab(b, P, topPx) {
    const key = b.key;
    const label = BAND_LABELS[key];
    const dim = key === 'table' ? null : fmtPt(num(state.tpl[key].height, 40));
    const tab = h('div', {
      class: 'band-tab' + (isBandSelected(key) ? ' active' : ''),
      title: label + ' — ' + BAND_HINTS[key],
      dataset: { tab: key },
      style: { left: (P.w * state.scale + 6) + 'px', top: topPx + 'px' },
    },
    h('span', null, label),
    dim ? h('span', { class: 'tab-dim' }, dim) : null,
    h('button', {
      type: 'button',
      class: 'tab-x',
      title: 'حذف ' + label,
      'aria-label': 'حذف ' + label,
      onClick: (e) => {
        e.stopPropagation();
        removeBand(key);
      },
    }, '×'));
    tab.addEventListener('pointerdown', (e) => e.stopPropagation());
    tab.addEventListener('click', () => select(key === 'table' ? { kind: 'table' } : { kind: 'band', band: key }));
    return tab;
  }

  function renderElement(el, bandKey, index) {
    const s = state.scale;
    const type = normType(el.type);
    const x = num(el.x, 0);
    const y = num(el.y, 0);
    const w = Math.max(0, num(el.width, 100));
    const hh = Math.max(0, num(el.height, 16));
    const selected = isElSelected(bandKey, index);
    const multi = selected && state.sel.items.length > 1;
    const div = h('div', {
      class: `el ${type.toLowerCase()}` + (selected ? ' selected' : '') + (el.visibleWhen ? ' conditional' : ''),
      dataset: { band: bandKey, index: String(index) },
      title: (el.name ? el.name + ' — ' : '') + TYPE_LABELS[type] + (el.visibleWhen ? ' (شرطی: ' + el.visibleWhen + ')' : ''),
    });
    let left = x * s;
    let top = y * s;
    let W = w * s;
    let H = hh * s;
    let handles = ['nw', 'n', 'ne', 'e', 'se', 's', 'sw', 'w'];

    if (type === 'Line') {
      const HIT = 8;
      let ox = 0;
      let oy = 0;
      if (H < HIT) { oy = (HIT - H) / 2; top -= oy; H = HIT; }
      if (W < HIT) { ox = (HIT - W) / 2; left -= ox; W = HIT; }
      if (hh === 0 && w > 0) handles = ['w', 'e'];
      else if (w === 0 && hh > 0) handles = ['n', 's'];
      const lw = num(el.lineWidth, 0.75);
      const sw = Math.max(lw * s, 0.75);
      const svg = svgEl('svg', { class: 'shape', width: W, height: H });
      const ln = svgEl('line', {
        x1: ox, y1: oy, x2: ox + w * s, y2: oy + hh * s,
        stroke: normHex(el.lineColor) || '#000000', 'stroke-width': sw,
      });
      if (el.dashed) ln.setAttribute('stroke-dasharray', `${Math.max(2, sw * 3)} ${Math.max(2, sw * 2)}`);
      svg.append(ln);
      div.append(svg);
    } else if (type === 'Rect') {
      const lw = num(el.lineWidth, 0.75);
      const shape = h('div', { style: { position: 'absolute', inset: '0', boxSizing: 'border-box', pointerEvents: 'none' } });
      if (lw > 0) shape.style.border = `${Math.max(lw * s, 0.75)}px ${el.dashed ? 'dashed' : 'solid'} ${normHex(el.lineColor) || '#000000'}`;
      const bg = normHex(el.background);
      if (bg) shape.style.background = bg;
      const r = num(el.cornerRadius, 0);
      if (r > 0) shape.style.borderRadius = px(r);
      div.append(shape);
    } else if (type === 'Image') {
      const src = String(el.source || '');
      if (/^data:image\//i.test(src)) {
        div.append(h('img', {
          src, alt: '', draggable: 'false',
          style: { objectFit: el.keepAspectRatio === false ? 'fill' : 'contain' },
        }));
      } else {
        div.append(h('div', { class: 'img-ph' }, icon('image'), h('span', null, src || 'بدون تصویر')));
      }
      if (isObj(el.border)) applyBorder(div, el.border, s);
    } else {
      // Inner box so overflow clipping never hides the resize handles.
      const box = h('div', { style: { position: 'absolute', inset: '0' } });
      buildTextBox(box, el.text == null ? '' : String(el.text), resolveStyle([el.style], el, ELEMENT_STYLE_KEYS));
      div.append(box);
    }

    Object.assign(div.style, { left: left + 'px', top: top + 'px', width: W + 'px', height: H + 'px' });
    if (selected && !multi) {
      for (const d of handles) {
        const hd = h('div', { class: 'handle ' + d, dataset: { dir: d } });
        hd.addEventListener('pointerdown', (e) => startResize(e, bandKey, index, d));
        div.append(hd);
      }
    }
    div.addEventListener('pointerdown', (e) => startElementDrag(e, bandKey, index));
    div.addEventListener('dblclick', (e) => {
      e.stopPropagation();
      focusInspectorText();
    });
    return div;
  }

  function cssTextAlign(align, rtl) {
    switch (lower(align)) {
      case 'left': return 'left';
      case 'right': return 'right';
      case 'center': case 'centre': return 'center';
      case 'end': return rtl ? 'left' : 'right';
      default: return rtl ? 'right' : 'left';
    }
  }

  function dirAttr(direction) {
    const d = lower(direction);
    if (d === 'righttoleft' || d === 'rtl') return 'rtl';
    if (d === 'lefttoright' || d === 'ltr') return 'ltr';
    return 'auto';
  }

  function applyBorder(node, b, s) {
    if (!isObj(b)) return;
    const w = num(b.width, 0.5);
    if (w <= 0) return;
    const sides = b.sides ? String(b.sides).toUpperCase() : 'LTRB';
    const css = `${Math.max(w * s, 0.75)}px ${b.dashed ? 'dashed' : 'solid'} ${normHex(b.color) || '#000000'}`;
    if (sides.includes('L')) node.style.borderLeft = css;
    if (sides.includes('T')) node.style.borderTop = css;
    if (sides.includes('R')) node.style.borderRight = css;
    if (sides.includes('B')) node.style.borderBottom = css;
    const r = num(b.radius, 0);
    if (r > 0) node.style.borderRadius = (r * s) + 'px';
  }

  /** Fill an absolutely-positioned box with styled text (used by Text elements and table cells). */
  function buildTextBox(box, text, st) {
    const s = state.scale;
    const rtl = tplRtl();
    const va = lower(st.vAlign);
    Object.assign(box.style, {
      display: 'flex',
      flexDirection: 'column',
      justifyContent: va === 'top' ? 'flex-start' : va === 'bottom' ? 'flex-end' : 'center',
      boxSizing: 'border-box',
      padding: `${Math.min(st.padding, 1) * s}px ${st.padding * s}px`,
    });
    if (st.background) box.style.backgroundColor = st.background;
    applyBorder(box, st.border, s);
    const ov = lower(st.overflow);
    box.style.overflow = ov === 'visible' ? 'visible' : 'hidden';

    const inner = h('div', { class: 'text-inner', dir: dirAttr(st.direction) });
    Object.assign(inner.style, {
      fontFamily: cssFamily(st.font),
      fontSize: (st.fontSize * s) + 'px',
      fontWeight: st.bold ? '700' : '400',
      textDecoration: st.underline ? 'underline' : 'none',
      color: st.color,
      lineHeight: String(1.25 * st.lineSpacing),
      textAlign: cssTextAlign(st.align, rtl),
    });
    if (ov === 'wrap') {
      inner.style.whiteSpace = 'pre-wrap';
      inner.style.overflowWrap = 'anywhere';
    } else {
      inner.style.whiteSpace = 'pre';
      if (ov !== 'visible') inner.style.overflow = 'hidden';
      if (ov === 'ellipsis') inner.style.textOverflow = 'ellipsis';
      if (ov === 'shrink') inner.dataset.shrink = String(st.minFontSize * s);
    }
    for (const p of splitExpr(text)) {
      if (p.expr) inner.append(h('span', { class: 'expr' }, p.text));
      else inner.append(document.createTextNode(toDigits(p.text, st.digits)));
    }
    box.append(inner);
  }

  /** "Shrink" overflow: reduce the font size so a single line fits (batched reads, then writes). */
  function applyShrink(root) {
    const nodes = root.querySelectorAll('[data-shrink]');
    const m = [];
    for (const n of nodes) m.push([n, n.scrollWidth, n.clientWidth]);
    for (const [n, sw, cw] of m) {
      if (cw > 0 && sw > cw + 0.5) {
        const fs = parseFloat(n.style.fontSize);
        const min = parseFloat(n.dataset.shrink) || 1;
        n.style.fontSize = Math.max(min, (fs * cw) / sw * 0.97) + 'px';
      }
    }
  }

  function renderTable(div, P) {
    const t = state.tpl.table;
    const s = state.scale;
    const rtl = tplRtl();
    const geo = tableGeometry(P.cw);
    const cols = geo.cols;
    const rows = tableRows(geo);
    const selCol = state.sel && state.sel.kind === 'col' ? state.sel.index : -1;

    if (!cols.length) {
      div.append(h('div', { class: 'table-empty' },
        t.columns.length ? 'همهٔ ستون‌ها مخفی هستند' : 'جدول ستونی ندارد — یک فیلد را اینجا رها کنید'));
      return;
    }
    const labels = {
      carry: tv('carryForwardLabel'), groupTotal: tv('groupTotalsLabel'),
      pageTotal: tv('pageTotalsLabel'), grandTotal: tv('grandTotalsLabel'),
    };
    const isAgg = (c) => !!c.c.aggregate && lower(c.c.aggregate) !== 'none';
    const byLine = [];
    for (let l = 0; l < geo.lineCount; l++) byLine.push(cols.filter((c) => c.line === l));
    const selectTable = (e) => {
      if (e.button !== 0) return;
      e.stopPropagation();
      blurActive();
      select({ kind: 'table' });
    };
    // Table-level backgrounds take precedence over style backgrounds (as in the engine).
    const cell = (left, top, width, height, text, st, bg, cls, title) => {
      const d = h('div', {
        class: 'tcell' + (cls ? ' ' + cls : ''),
        style: { left: px(left), top: px(top), width: px(width), height: px(height) },
        title: title || null,
      });
      buildTextBox(d, text, st);
      if (bg) d.style.backgroundColor = bg;
      div.append(d);
      return d;
    };

    let y = 0;
    for (const r of rows) {
      if (r.kind === 'group') {
        const st = resolveStyle([t.rowStyle], null, [], { bold: true });
        const d = cell(geo.tableLeft, y, geo.tableW, r.h, tv('groupHeaderText') || `{${tv('groupBy')}}`, st,
          normHex(tv('groupHeaderBackground')), null, 'عنوان گروه (' + tv('groupBy') + ')');
        d.addEventListener('pointerdown', selectTable);
        y += r.h;
        continue;
      }
      const isTotals = r.kind !== 'header' && r.kind !== 'data';
      for (let l = 0; l < geo.lineCount; l++) {
        const lineCols = byLine[l];
        const top = y + l * r.sub;
        let first = 0;
        if (isTotals) {
          // Leading non-aggregate columns merge into one label cell (label only on line 0).
          let span = 0;
          while (span < lineCols.length && !isAgg(lineCols[span])) span++;
          if (span > 0) {
            const part = lineCols.slice(0, span);
            const x1 = Math.min(...part.map((c) => c.left));
            const x2 = Math.max(...part.map((c) => c.left + c.width));
            const st = resolveStyle([t.totalsStyle], null, [], { bold: true });
            st.align = 'Center';
            const d = cell(x1, top, x2 - x1, r.sub, l === 0 ? (labels[r.kind] || '') : '', st,
              normHex(tv('totalsBackground')), null, 'ردیف جمع');
            d.addEventListener('pointerdown', selectTable);
          }
          first = span;
        }
        for (let k = first; k < lineCols.length; k++) {
          const c = lineCols[k];
          let text = '';
          let st;
          let bg = null;
          if (r.kind === 'header') {
            text = c.c.header || '';
            st = resolveStyle([t.headerStyle], { align: c.c.headerAlign }, ['align'], { bold: true, align: 'Center' });
            bg = normHex(tv('headerBackground'));
          } else if (r.kind === 'data') {
            text = cellSample(c.c, r.i);
            st = resolveStyle([t.rowStyle, c.c.style], c.c, COLUMN_STYLE_KEYS);
            const neg = normHex(c.c.negativeColor);
            const n = parseNum(text);
            if (neg && n != null && n < 0) st.color = neg;
            if (r.i % 2 === 1) bg = normHex(tv('altRowBackground'));
          } else {
            if (isAgg(c)) text = aggregateSample(c.c, c.c.aggregate);
            st = resolveStyle([t.totalsStyle], null, [], { bold: true, align: c.c.align });
            bg = normHex(tv('totalsBackground'));
          }
          const full = c.span && !isTotals;
          const d = cell(c.left, full ? y : top, c.width, full ? r.h : r.sub, text, st, bg,
            (r.kind === 'header' ? 'header' : '') + (c.i === selCol ? ' col-sel' : ''),
            r.kind === 'header' ? 'ستون: ' + (c.c.header || c.c.field || '') : String(c.c.field || ''));
          d.addEventListener('pointerdown', (e) => {
            if (e.button !== 0) return;
            e.stopPropagation();
            blurActive();
            select({ kind: 'col', index: c.i });
          });
        }
      }
      y += r.h;
    }

    const lines = tableLinesSvg(geo, byLine, rows);
    if (lines) div.append(lines);

    // Column resize grips at each column's end-side edge (left edge in RTL), per header sub-line.
    const hsub = rows[0].sub;
    for (const lineCols of byLine) {
      lineCols.forEach((c, k) => {
        const edge = rtl ? c.left : c.left + c.width;
        const next = lineCols[k + 1];
        const grip = h('div', {
          class: 'col-grip',
          title: 'برای تغییر پهنای ستون بکشید',
          style: { left: px(edge), top: px(c.line * hsub), height: px(hsub) },
        });
        grip.addEventListener('pointerdown', (e) => startColumnResize(e, c.i, next ? next.i : null, c.scale));
        div.append(grip);
      });
    }
  }

  function tableLinesSvg(geo, byLine, rows) {
    const s = state.scale;
    const bw = num(tv('borderWidth'), 0.5);
    if (bw <= 0) return null;
    const color = normHex(tv('borderColor')) || '#9E9E9E';
    const rtl = tplRtl();
    const L0 = geo.tableLeft;
    const R0 = geo.tableLeft + geo.tableW;
    const totalH = rows.reduce((a, r) => a + r.h, 0);
    const sw = Math.max(bw * s, 0.75);
    const svg = svgEl('svg', { class: 't-lines', width: R0 * s + 2, height: totalH * s + 2 });
    const line = (x1, y1, x2, y2) => svg.append(svgEl('line', {
      x1: x1 * s, y1: y1 * s, x2: x2 * s, y2: y2 * s, stroke: color, 'stroke-width': sw,
    }));
    svg.append(svgEl('rect', { x: L0 * s, y: 0, width: geo.tableW * s, height: totalH * s, fill: 'none', stroke: color, 'stroke-width': sw }));
    const hLines = tv('horizontalLines');
    const vLines = tv('verticalLines');
    const isAgg = (c) => !!c.c.aggregate && lower(c.c.aggregate) !== 'none';
    let y = 0;
    rows.forEach((r, i) => {
      const y0 = y;
      y += r.h;
      if (r.kind !== 'group') {
        const isTotals = r.kind !== 'header' && r.kind !== 'data';
        for (let l = 0; l < geo.lineCount; l++) {
          const top = y0 + l * r.sub;
          if (l > 0) {
            if (r.kind === 'data') {
              svg.append(svgEl('line', {
                x1: geo.flowX1 * s, y1: top * s, x2: geo.flowX2 * s, y2: top * s, stroke: color,
                'stroke-width': sw * 0.6, 'stroke-dasharray': '3 2', opacity: 0.45,
              }));
            } else if (r.kind === 'header') line(geo.flowX1, top, geo.flowX2, top);
            else if (hLines) line(L0, top, R0, top);
          }
          const lineCols = byLine[l];
          let span = 0;
          if (isTotals) while (span < lineCols.length && !isAgg(lineCols[span])) span++;
          lineCols.forEach((c, k) => {
            if (k < span - 1) return; // inside the merged label cell
            if (!vLines && !(isTotals && k === span - 1)) return;
            const x = rtl ? c.left : c.left + c.width;
            if (Math.abs(x - L0) < 0.01 || Math.abs(x - R0) < 0.01) return;
            if (c.span && !isTotals) { if (l === 0) line(x, y0, x, y0 + r.h); return; }
            line(x, top, x, top + r.sub);
          });
        }
      }
      if (i === rows.length - 1) return;
      const next = rows[i + 1];
      if (i === 0 || hLines || r.kind !== 'data' || next.kind !== 'data') line(L0, y, R0, y);
    });
    return svg;
  }

  // ════════════════════════════════════════════════════════════════════
  // 9. Canvas interactions
  // ════════════════════════════════════════════════════════════════════

  function blurActive() {
    const a = document.activeElement;
    if (a && a !== document.body && typeof a.blur === 'function') a.blur();
  }

  function clientToPage(e) {
    const r = $('page').getBoundingClientRect();
    return { x: (e.clientX - r.left) / state.scale, y: (e.clientY - r.top) / state.scale };
  }

  /** Snap to the grid (Alt disables snapping temporarily). */
  function snapV(v, e) {
    if ((e && e.altKey) || !(state.snap > 0)) return round2(v);
    return round2(Math.round(v / state.snap) * state.snap);
  }

  function trackPointer(onMove, onUp) {
    const move = (ev) => onMove(ev);
    const up = (ev) => {
      window.removeEventListener('pointermove', move);
      window.removeEventListener('pointerup', up);
      window.removeEventListener('pointercancel', up);
      onUp(ev);
    };
    window.addEventListener('pointermove', move);
    window.addEventListener('pointerup', up);
    window.addEventListener('pointercancel', up);
  }

  function onBandPointerDown(e, key) {
    if (e.button !== 0) return;
    blurActive();
    select(key === 'table' ? { kind: 'table' } : { kind: 'band', band: key });
  }

  function startElementDrag(e, bandKey, index) {
    if (e.button !== 0) return;
    e.stopPropagation();
    e.preventDefault();
    blurActive();
    const wasSelected = isElSelected(bandKey, index);
    if (e.shiftKey || e.ctrlKey || e.metaKey) {
      const items = state.sel && state.sel.kind === 'els' ? state.sel.items.slice() : [];
      const i = items.findIndex((it) => it.band === bandKey && it.index === index);
      if (i >= 0) items.splice(i, 1);
      else items.push({ band: bandKey, index });
      select(items.length ? { kind: 'els', items } : null);
      return;
    }
    if (!wasSelected) select({ kind: 'els', items: [{ band: bandKey, index }] });

    const L = computeLayout();
    const sx = e.clientX;
    const sy = e.clientY;
    const items = selectedElements().map((x) => ({
      band: x.band, index: x.index,
      x0: num(x.el.x, 0), y0: num(x.el.y, 0),
      absY0: L.bands[x.band].top + num(x.el.y, 0),
    }));
    const single = items.length === 1;
    const origBand = bandKey;
    let moved = false;

    trackPointer((ev) => {
      if (!moved) {
        if (Math.hypot(ev.clientX - sx, ev.clientY - sy) < 3) return;
        moved = true;
        beginGesture();
      }
      const dx = (ev.clientX - sx) / state.scale;
      const dy = (ev.clientY - sy) / state.scale;
      const t = state.tpl;
      if (single) {
        // A single element can be dragged into another band: re-parent and recompute y.
        const it = items[0];
        const target = elementBandAt(L, clientToPage(ev).y) || it.band;
        if (target !== it.band && t[target]) {
          const [moving] = t[it.band].elements.splice(it.index, 1);
          t[target].elements.push(moving);
          it.band = target;
          it.index = t[target].elements.length - 1;
          state.sel = { kind: 'els', items: [{ band: target, index: it.index }] };
        }
        const el = t[it.band].elements[it.index];
        el.x = snapV(it.x0 + dx, ev);
        el.y = snapV(it.absY0 + dy - L.bands[it.band].top, ev);
        if (it.band !== origBand) {
          // Entering another band: keep the element inside it.
          el.y = round2(clamp(el.y, 0, Math.max(0, num(t[it.band].height, 40) - num(el.height, 16))));
        }
        setStatusPos(`${BAND_LABELS[it.band]} — x: ${fmtPt(el.x)}  y: ${fmtPt(el.y)} pt`);
      } else {
        for (const it of items) {
          const el = t[it.band].elements[it.index];
          el.x = snapV(it.x0 + dx, ev);
          el.y = snapV(it.y0 + dy, ev);
        }
        setStatusPos(`Δx: ${fmtPt(dx)}  Δy: ${fmtPt(dy)} pt`);
      }
      renderCanvas();
    }, () => {
      if (moved) endGesture();
      else if (wasSelected && state.sel && state.sel.kind === 'els' && state.sel.items.length > 1) {
        select({ kind: 'els', items: [{ band: bandKey, index }] });
      }
    });
  }

  function startResize(e, bandKey, index, dir) {
    if (e.button !== 0) return;
    e.stopPropagation();
    e.preventDefault();
    blurActive();
    const el0 = state.tpl[bandKey].elements[index];
    const isLine = normType(el0.type) === 'Line';
    const o = { x: num(el0.x, 0), y: num(el0.y, 0), w: Math.max(0, num(el0.width, 100)), h: Math.max(0, num(el0.height, 16)) };
    const min = isLine ? 0 : 2;
    const sx = e.clientX;
    const sy = e.clientY;
    beginGesture();
    trackPointer((ev) => {
      const dx = (ev.clientX - sx) / state.scale;
      const dy = (ev.clientY - sy) / state.scale;
      let x1 = o.x;
      let y1 = o.y;
      let x2 = o.x + o.w;
      let y2 = o.y + o.h;
      if (dir.includes('w')) x1 = Math.min(snapV(o.x + dx, ev), x2 - min);
      if (dir.includes('e')) x2 = Math.max(snapV(o.x + o.w + dx, ev), x1 + min);
      if (dir.includes('n')) y1 = Math.min(snapV(o.y + dy, ev), y2 - min);
      if (dir.includes('s')) y2 = Math.max(snapV(o.y + o.h + dy, ev), y1 + min);
      const el = state.tpl[bandKey].elements[index];
      el.x = round2(x1);
      el.y = round2(y1);
      el.width = round2(x2 - x1);
      el.height = round2(y2 - y1);
      setStatusPos(`${fmtPt(el.width)} × ${fmtPt(el.height)} pt`);
      renderCanvas();
    }, () => endGesture());
  }

  function startBandResize(e, key) {
    if (e.button !== 0) return;
    e.stopPropagation();
    e.preventDefault();
    blurActive();
    if (!isBandSelected(key)) select({ kind: 'band', band: key });
    const h0 = num(state.tpl[key].height, 40);
    const sy = e.clientY;
    beginGesture();
    trackPointer((ev) => {
      const dy = (ev.clientY - sy) / state.scale;
      const nh = key === 'pageFooter' ? h0 - dy : h0 + dy;
      state.tpl[key].height = Math.max(4, snapV(nh, ev));
      setStatusPos(`ارتفاع ${BAND_LABELS[key]}: ${fmtPt(state.tpl[key].height)} pt`);
      renderCanvas();
    }, () => endGesture());
  }

  /**
   * Column border drag. Widths are stored as absolute numbers; with fitWidth the engine scales
   * them proportionally, so we trade width with the neighbouring column to keep the scale stable.
   */
  function startColumnResize(e, idx, nextIdx, scale) {
    if (e.button !== 0) return;
    e.stopPropagation();
    e.preventDefault();
    blurActive();
    const cols = state.tpl.table.columns;
    const w0 = Math.max(1, num(cols[idx].width, 60));
    const n0 = nextIdx != null ? Math.max(1, num(cols[nextIdx].width, 60)) : 0;
    const fit = !!tv('fitWidth');
    const k = scale > 0 ? scale : 1;
    const rtl = tplRtl();
    const sx = e.clientX;
    state.sel = { kind: 'col', index: idx };
    renderInspector();
    beginGesture();
    trackPointer((ev) => {
      let d = (ev.clientX - sx) / state.scale;
      if (rtl) d = -d;
      let dAbs = d / k;
      const c = state.tpl.table.columns;
      if (fit && nextIdx != null) {
        // Trade width with the neighbour on the same line so the fit-width scale stays constant.
        dAbs = clamp(dAbs, Math.min(0, 4 - w0), Math.max(0, n0 - 4));
        c[idx].width = roundHalf(w0 + dAbs);
        c[nextIdx].width = roundHalf(n0 - dAbs);
      } else {
        c[idx].width = Math.max(4, roundHalf(w0 + dAbs));
      }
      setStatusPos(`پهنای ستون: ${fmtPt(c[idx].width)}`);
      renderCanvas();
    }, () => endGesture());
  }

  // ── drag & drop from the side panel ──

  function setDragPayload(e, payload) {
    state.dragPayload = payload;
    try {
      e.dataTransfer.setData(DND_MIME, JSON.stringify(payload));
      e.dataTransfer.setData('text/plain', payload.text || (payload.name ? `{${payload.name}}` : ''));
      e.dataTransfer.effectAllowed = 'copy';
    } catch (err) { /* ignore */ }
  }

  function clearDrag() {
    state.dragPayload = null;
    clearDropHighlight();
  }

  let dropKey = null;
  function highlightDrop(pt) {
    const L = computeLayout();
    const hit = bandHit(L, pt.y);
    const key = hit && !hit.missing ? hit.key : elementBandAt(L, pt.y);
    if (key === dropKey) return;
    clearDropHighlight();
    dropKey = key;
    const el = key && $('page').querySelector(`.band[data-band="${key}"]`);
    if (el) el.classList.add('drop-target');
  }
  function clearDropHighlight() {
    dropKey = null;
    for (const el of $('page').querySelectorAll('.drop-target')) el.classList.remove('drop-target');
  }

  function makeElementFromPayload(p) {
    if (!p) return null;
    if (p.kind === 'tool') {
      const tool = TOOLS.find((x) => x.id === p.tool);
      return tool ? tool.make() : null;
    }
    if (p.kind === 'field' || p.kind === 'param') return { type: 'Text', width: 110, height: 16, text: `{${p.name}}` };
    if (p.kind === 'sys') return { type: 'Text', width: 110, height: 16, text: p.text };
    return null;
  }

  function handleDrop(p, pt) {
    const L = computeLayout();
    const hit = bandHit(L, pt.y);
    if (hit && hit.key === 'table') {
      if (p.kind === 'field') {
        addColumnForField(p);
        return;
      }
      if (!hit.missing) {
        toast('این مورد را نمی‌توان داخل جدول گذاشت؛ آن را روی یکی از باندهای دیگر رها کنید.');
        return;
      }
    }
    let key = null;
    let fresh = false;
    if (hit && ELEMENT_BANDS.includes(hit.key)) {
      key = hit.key;
      fresh = hit.missing;
    } else {
      key = elementBandAt(L, pt.y);
    }
    if (!key) {
      toast('ابتدا یک باند (مثلاً سرصفحه) اضافه کنید.');
      return;
    }
    const el = makeElementFromPayload(p);
    if (!el) return;
    const top = fresh ? 0 : L.bands[key].top;
    insertElement(key, el, pt.x - L.P.ml, fresh ? 2 : pt.y - top, p);
  }

  // ════════════════════════════════════════════════════════════════════
  // 10. Element / band / column commands
  // ════════════════════════════════════════════════════════════════════

  const TOOLS = [
    { id: 'text', label: 'متن', icon: 'text', make: () => ({ type: 'Text', width: 120, height: 18, text: 'متن' }) },
    { id: 'line', label: 'خط', icon: 'line', make: () => ({ type: 'Line', width: 150, height: 0, lineColor: '#000000', lineWidth: 0.75 }) },
    { id: 'rect', label: 'مستطیل', icon: 'rect', make: () => ({ type: 'Rect', width: 120, height: 40, lineColor: '#000000', lineWidth: 0.5 }) },
    { id: 'image', label: 'تصویر', icon: 'image', make: () => ({ type: 'Image', width: 60, height: 60 }) },
    {
      id: 'pageNumber', label: 'شماره صفحه', icon: 'hash',
      make: () => ({ type: 'Text', width: 120, height: 16, text: 'صفحه {PageNumber} از {TotalPages}', align: 'Center' }),
    },
    { id: 'date', label: 'تاریخ', icon: 'calendar', make: () => ({ type: 'Text', width: 90, height: 16, text: '{Today:persian}' }) },
  ];

  function newBand(key) {
    return { height: key === 'pageFooter' || key === 'reportFooter' ? 30 : 40, elements: [] };
  }

  function cleanFieldInfo(f) {
    const o = { name: String(f.name) };
    if (f.title) o.title = String(f.title);
    o.type = FIELD_TYPE_LABELS[lower(f.type)] ? lower(f.type) : 'string';
    if (f.sample != null && f.sample !== '') o.sample = String(f.sample);
    return o;
  }

  /** A data-source field dropped on the canvas is also added to template.fields/parameters. */
  function ensureTemplateField(t, p) {
    if (!p || p.origin !== 'ds' || (p.kind !== 'field' && p.kind !== 'param')) return;
    const list = p.kind === 'param' ? t.parameters : t.fields;
    if (!list.some((x) => lower(x.name) === lower(p.name))) list.push(cleanFieldInfo(p));
  }

  function insertElement(key, el, x, y, payload) {
    let index = -1;
    mutate((t) => {
      if (!t[key]) t[key] = newBand(key);
      ensureTemplateField(t, payload);
      const band = t[key];
      const P = pageInfo();
      const w = num(el.width, 100);
      const hh = num(el.height, 16);
      el.x = snapV(clamp(x, 0, Math.max(0, P.cw - w)));
      el.y = snapV(clamp(y, 0, Math.max(0, num(band.height, 40) - hh)));
      band.elements.push(el);
      index = band.elements.length - 1;
    });
    if (index >= 0) select({ kind: 'els', items: [{ band: key, index }] });
  }

  function targetBandForAdd() {
    const s = state.sel;
    if (s && s.kind === 'band' && ELEMENT_BANDS.includes(s.band)) return s.band;
    if (s && s.kind === 'els' && s.items.length) return s.items[0].band;
    return ELEMENT_BANDS.find((k) => state.tpl[k]) || null;
  }

  /** Click on a toolbox item / field chip: add to the selected (or first) band with a cascade offset. */
  function addAtDefault(el, payload) {
    if (!el) return;
    const key = targetBandForAdd() || 'reportHeader';
    const band = state.tpl[key];
    const n = band ? band.elements.length : 0;
    const off = (n % 6) * 8;
    const P = pageInfo();
    const w = num(el.width, 100);
    const x = tplRtl() ? P.cw - w - 4 - off : 4 + off;
    insertElement(key, el, x, 2 + (n % 6) * 4, payload);
  }

  function addColumnForField(p) {
    let idx = -1;
    mutate((t) => {
      if (!t.table) t.table = { columns: [], headerHeight: 20, rowHeight: 16 };
      ensureTemplateField(t, p);
      t.table.columns.push(columnForField(p));
      idx = t.table.columns.length - 1;
    });
    if (idx >= 0) select({ kind: 'col', index: idx });
  }

  function deleteSelection() {
    const sel = state.sel;
    if (!sel) return;
    if (sel.kind === 'els') {
      const items = sel.items.slice().sort((a, b) => b.index - a.index);
      state.sel = null;
      mutate((t) => {
        for (const it of items) if (t[it.band]) t[it.band].elements.splice(it.index, 1);
      });
      select(null);
    } else if (sel.kind === 'col') deleteColumn(sel.index);
    else if (sel.kind === 'band') removeBand(sel.band);
    else if (sel.kind === 'table') removeBand('table');
  }

  function addBand(key) {
    if (!state.tpl[key]) {
      mutate((t) => {
        t[key] = key === 'table' ? defaultTable(t) : newBand(key);
      });
    }
    select(key === 'table' ? { kind: 'table' } : { kind: 'band', band: key });
  }

  function removeBand(key) {
    if (!state.tpl[key]) return;
    const s = state.sel;
    if (isBandSelected(key) || (s && s.kind === 'els' && s.items.some((i) => i.band === key))) state.sel = null;
    mutate((t) => {
      delete t[key];
    }, { inspector: true });
    toast(`«${BAND_LABELS[key]}» حذف شد — برای بازگردانی Ctrl+Z را بزنید.`);
  }

  function copySelection() {
    const els = selectedElements();
    if (!els.length) return false;
    state.clipboard = els.map((x) => ({ band: x.band, el: deepClone(x.el) }));
    return true;
  }

  function pasteClipboard(offset) {
    const clip = state.clipboard;
    if (!clip || !clip.length) return;
    const s = state.sel;
    const target = s && s.kind === 'band' && ELEMENT_BANDS.includes(s.band) ? s.band : null;
    const items = [];
    mutate((t) => {
      const P = pageInfo();
      for (const c of clip) {
        const key = target || (t[c.band] ? c.band : ELEMENT_BANDS.find((k) => t[k]));
        if (!key) continue;
        const el = deepClone(c.el);
        el.x = round2(clamp(num(el.x, 0) + offset, 0, Math.max(0, P.cw - num(el.width, 100))));
        el.y = round2(clamp(num(el.y, 0) + offset, 0, Math.max(0, num(t[key].height, 40) - num(el.height, 16))));
        c.el.x = el.x; // cascade repeated pastes
        c.el.y = el.y;
        t[key].elements.push(el);
        items.push({ band: key, index: t[key].elements.length - 1 });
      }
    });
    if (items.length) select({ kind: 'els', items });
    else toast('باندی برای چسباندن وجود ندارد.');
  }

  function duplicateSelection() {
    const saved = state.clipboard;
    if (copySelection()) pasteClipboard(10);
    state.clipboard = saved;
  }

  function nudge(dx, dy) {
    const els = selectedElements();
    if (!els.length) return false;
    mutate((t) => {
      for (const x of els) {
        const el = t[x.band].elements[x.index];
        el.x = round2(num(el.x, 0) + dx);
        el.y = round2(num(el.y, 0) + dy);
      }
    }, { coalesce: 'nudge', inspector: true });
    return true;
  }

  function alignSelection(mode) {
    const els = selectedElements();
    if (els.length < 2) return;
    const L = computeLayout();
    const boxes = els.map((x) => ({
      x, ax: num(x.el.x, 0), ay: L.bands[x.band].top + num(x.el.y, 0),
      w: num(x.el.width, 100), h: num(x.el.height, 16),
    }));
    const minX = Math.min(...boxes.map((b) => b.ax));
    const maxX = Math.max(...boxes.map((b) => b.ax + b.w));
    const minY = Math.min(...boxes.map((b) => b.ay));
    const maxY = Math.max(...boxes.map((b) => b.ay + b.h));
    const ref = boxes[0];
    mutate((t) => {
      for (const b of boxes) {
        const el = t[b.x.band].elements[b.x.index];
        const top = L.bands[b.x.band].top;
        switch (mode) {
          case 'left': el.x = round2(minX); break;
          case 'right': el.x = round2(maxX - b.w); break;
          case 'hcenter': el.x = round2((minX + maxX) / 2 - b.w / 2); break;
          case 'top': el.y = round2(minY - top); break;
          case 'bottom': el.y = round2(maxY - b.h - top); break;
          case 'vcenter': el.y = round2((minY + maxY) / 2 - b.h / 2 - top); break;
          case 'sameWidth': el.width = ref.w; break;
          case 'sameHeight': el.height = ref.h; break;
          default: break;
        }
      }
    }, { inspector: true });
  }

  function reorderElement(band, index, toFront) {
    let ni = index;
    mutate((t) => {
      const arr = t[band].elements;
      const [el] = arr.splice(index, 1);
      if (toFront) arr.push(el);
      else arr.unshift(el);
      ni = toFront ? arr.length - 1 : 0;
    });
    select({ kind: 'els', items: [{ band, index: ni }] });
  }

  function moveElementToBand(band, index, target) {
    if (band === target || !state.tpl[target]) return;
    let ni = -1;
    mutate((t) => {
      const [el] = t[band].elements.splice(index, 1);
      const maxY = Math.max(0, num(t[target].height, 40) - num(el.height, 16));
      el.y = round2(clamp(num(el.y, 0), 0, maxY));
      t[target].elements.push(el);
      ni = t[target].elements.length - 1;
    });
    if (ni >= 0) select({ kind: 'els', items: [{ band: target, index: ni }] });
  }

  function addColumn(after) {
    let idx = -1;
    mutate((t) => {
      if (!t.table) t.table = { columns: [], headerHeight: 20, rowHeight: 16 };
      const cols = t.table.columns;
      const at = after == null ? cols.length : after + 1;
      cols.splice(at, 0, { header: 'ستون جدید', field: '', width: 60 });
      idx = at;
    });
    if (idx >= 0) select({ kind: 'col', index: idx });
  }

  function deleteColumn(i) {
    if (!state.tpl.table) return;
    state.sel = { kind: 'table' };
    mutate((t) => {
      t.table.columns.splice(i, 1);
    }, { inspector: true });
    renderInspector();
  }

  function moveColumn(i, delta) {
    const cols = state.tpl.table ? state.tpl.table.columns : [];
    const j = i + delta;
    if (j < 0 || j >= cols.length) return;
    mutate((t) => {
      const c = t.table.columns;
      [c[i], c[j]] = [c[j], c[i]];
    });
    select({ kind: 'col', index: j });
  }

  // ════════════════════════════════════════════════════════════════════
  // 11. Side panel — toolbox & fields
  // ════════════════════════════════════════════════════════════════════

  function renderSidePanel() {
    if (!state.tpl) return;
    renderToolsPane();
    renderFieldsPane();
  }
  const renderSidePanelSoon = debounce(() => {
    renderSidePanel();
    refreshDatalists();
  }, 250);

  function toolTile(tool) {
    return h('button', {
      type: 'button',
      class: 'tool',
      draggable: 'true',
      title: `${tool.label} — بکشید روی باند یا کلیک کنید`,
      dataset: { tool: tool.id },
      onClick: () => addAtDefault(tool.make()),
      onDragstart: (e) => setDragPayload(e, { kind: 'tool', tool: tool.id }),
      onDragend: clearDrag,
    }, icon(tool.icon), h('span', null, tool.label));
  }

  function renderToolsPane() {
    const bandBtns = BAND_ORDER.map((key) => {
      const present = !!state.tpl[key];
      return h('button', {
        type: 'button',
        class: 'btn small' + (present ? '' : ' outline'),
        title: BAND_HINTS[key],
        onClick: () => (present ? select(key === 'table' ? { kind: 'table' } : { kind: 'band', band: key }) : addBand(key)),
      }, icon(present ? (key === 'table' ? 'table' : 'band') : 'plus'), h('span', null, (present ? '' : 'افزودن ') + BAND_LABELS[key]));
    });
    const shortcuts = [
      'Delete — حذف انتخاب',
      'Ctrl+C / Ctrl+V — کپی / چسباندن',
      'Ctrl+D — تکثیر',
      'Ctrl+Z / Ctrl+Y — واگرد / ازنو',
      'کلیدهای جهت — جابه‌جایی ۱pt (با Shift ده pt)',
      'Shift+کلیک — انتخاب چندتایی',
      'Alt هنگام کشیدن — بدون چسبیدن به شبکه',
      'Esc — لغو انتخاب',
    ];
    $('toolsPane').replaceChildren(
      h('div', { class: 'side-title' }, 'عناصر'),
      h('div', { class: 'tool-grid' }, TOOLS.map(toolTile)),
      h('div', { class: 'hint' }, 'عنصر را روی یک باند بکشید، یا کلیک کنید تا به باند انتخاب‌شده اضافه شود.'),
      h('div', { class: 'side-title' }, 'باندها'),
      h('div', { class: 'band-buttons' }, bandBtns),
      h('div', { class: 'side-title' }, 'میانبرها'),
      h('div', { class: 'hint' }, shortcuts.map((s) => h('div', null, s))),
    );
  }

  function chip(info, kind) {
    const payload = kind === 'sys'
      ? { kind: 'sys', name: info.name, text: info.text }
      : { kind, name: info.name, title: info.title, type: info.type, sample: info.sample, origin: info.origin };
    const typeLabel = kind === 'sys' ? (info.badge || 'سیستمی')
      : kind === 'param' ? 'پارامتر' : (FIELD_TYPE_LABELS[lower(info.type)] || 'متن');
    return h('button', {
      type: 'button',
      class: 'chip' + (kind === 'param' ? ' param' : kind === 'sys' ? ' sys' : '') + (info.origin === 'ds' ? ' ds' : ''),
      draggable: 'true',
      title: (kind === 'sys' ? info.text : `{${info.name}}`) + (info.sample ? ` — نمونه: ${info.sample}` : '')
        + (info.origin === 'ds' ? ' (از منبع داده؛ با استفاده به قالب افزوده می‌شود)' : ''),
      onClick: () => onChipClick(payload),
      onDragstart: (e) => setDragPayload(e, payload),
      onDragend: clearDrag,
    },
    h('span', { class: 'c-title' }, info.title || info.name),
    h('span', { class: 'c-name' }, kind === 'sys' ? info.text : info.name),
    h('span', { class: 'c-type' }, typeLabel));
  }

  function onChipClick(p) {
    const s = state.sel;
    if (p.kind === 'field' && s && (s.kind === 'table' || s.kind === 'col')) {
      addColumnForField(p);
      return;
    }
    addAtDefault(makeElementFromPayload(p), p);
  }

  function renderFieldsPane() {
    const kids = [];
    kids.push(h('div', { class: 'side-title' }, 'منبع داده'));
    const dsList = state.server.datasources;
    if (dsList.length) {
      const sel = selectEl(dsList.map((d) => [d.name, d.name]), state.dataSource, { 'aria-label': 'منبع داده' });
      sel.addEventListener('change', () => setDataSource(sel.value));
      const ds = selectedDataSource();
      const cnt = ds ? `${arrOfObj(ds.fields).length} فیلد، ${arrOfObj(ds.parameters).length} پارامتر` : '';
      kids.push(h('div', { class: 'ds-box' }, sel,
        h('div', { class: 'hint', style: 'margin:0' }, cnt),
        btn('افزودن فیلدها و پارامترها به قالب', 'download', () => importDataSource(state.dataSource), { cls: 'small outline', disabled: !ds })));
    } else {
      kids.push(h('div', { class: 'empty-note' }, state.server.online
        ? 'منبع داده‌ای روی سرور تعریف نشده است.'
        : 'سرور در دسترس نیست؛ فقط فیلدهای خود قالب نمایش داده می‌شوند.'));
    }

    const fields = knownFields();
    const params = knownParams();
    const tplFields = fields.filter((f) => f.origin === 'tpl');
    const dsFields = fields.filter((f) => f.origin === 'ds');
    const tplParams = params.filter((f) => f.origin === 'tpl');
    const dsParams = params.filter((f) => f.origin === 'ds');
    const list = (items, kind, empty) => (items.length
      ? h('div', { class: 'chip-list' }, items.map((f) => chip(f, kind)))
      : h('div', { class: 'empty-note' }, empty));

    kids.push(h('div', { class: 'side-title' }, 'فیلدهای قالب'));
    kids.push(list(tplFields, 'field', 'فیلدی تعریف نشده است (از ویژگی‌های سند یا منبع داده اضافه کنید).'));
    kids.push(h('div', { class: 'side-title' }, 'پارامترها'));
    kids.push(list(tplParams, 'param', 'پارامتری تعریف نشده است.'));
    if (dsFields.length || dsParams.length) {
      kids.push(h('div', { class: 'side-title' }, 'از منبع داده (هنوز در قالب نیست)'));
      kids.push(h('div', { class: 'chip-list' }, dsFields.map((f) => chip(f, 'field')), dsParams.map((f) => chip(f, 'param'))));
    }
    kids.push(h('div', { class: 'side-title' }, 'متغیرهای سیستمی'));
    kids.push(h('div', { class: 'chip-list' }, SYSTEM_VARS.map((v) => chip({ name: v.name, title: v.title, text: `{${v.name}}` }, 'sys'))));

    const numeric = fields.filter((f) => lower(f.type) === 'number');
    if (numeric.length) {
      kids.push(h('div', { class: 'side-title' }, 'تجمیعی'));
      kids.push(h('div', { class: 'chip-list' }, numeric.map((f) => chip({
        name: f.name, title: 'جمع ' + (f.title || f.name), text: `{Sum(${f.name})}`, badge: 'جمع',
      }, 'sys'))));
    }
    kids.push(h('div', { class: 'hint' }, 'فیلد را روی جدول رها کنید تا ستون شود؛ روی باندهای دیگر یک متن ساخته می‌شود.'));
    $('fieldsPane').replaceChildren(...kids);
  }

  function setDataSource(name) {
    state.dataSource = name;
    savePrefs();
    const tb = $('dsSelect');
    if (tb.value !== name) tb.value = name;
    renderSidePanel();
    refreshDatalists();
    renderCanvas();
  }

  function importDataSource(name) {
    const ds = state.server.datasources.find((d) => d.name === name);
    if (!ds) return;
    let added = 0;
    const merge = (target, src) => {
      for (const f of arrOfObj(src)) {
        if (!f.name) continue;
        const ex = target.find((x) => lower(x.name) === lower(f.name));
        if (!ex) {
          target.push(cleanFieldInfo(f));
          added++;
        } else {
          if (!ex.title && f.title) ex.title = String(f.title);
          if ((ex.sample == null || ex.sample === '') && f.sample != null && f.sample !== '') ex.sample = String(f.sample);
        }
      }
    };
    const ok = mutate((t) => {
      merge(t.fields, ds.fields);
      merge(t.parameters, ds.parameters);
    }, { inspector: true });
    toast(ok ? `${added} مورد از «${name}» به قالب افزوده شد.` : 'همهٔ فیلدهای این منبع داده در قالب وجود دارند.', ok ? 'success' : 'info');
  }

  function refreshDatalists() {
    const fill = (id, items) => {
      $(id).replaceChildren(...items.map(([v, label]) => h('option', { value: v, label: label || undefined })));
    };
    const seen = new Set();
    const fl = [];
    for (const f of [...knownFields(), ...knownParams()]) {
      if (seen.has(lower(f.name))) continue;
      seen.add(lower(f.name));
      fl.push([f.name, f.title || '']);
    }
    fill('dl-fields', fl);
    fill('dl-formats', FORMATS);
    fill('dl-fontfiles', state.server.fonts.map((f) => [f.file, f.family || '']));
  }

  // ════════════════════════════════════════════════════════════════════
  // 12. Inspector — forms built from small field descriptors
  // ════════════════════════════════════════════════════════════════════
  //
  // Field descriptor:
  //   k        property key            label   row label
  //   t        'text' | 'num' | 'bool' | 'tri' | 'select' | 'font' | 'style' | 'color' | 'textarea' | 'expr'
  //   def      value displayed when the property is absent (the engine's C# default)
  //   required never removes the property (empty input ignored, color not clearable)
  //   nullable clearing writes an explicit null (C# props whose default is non-null, e.g. headerBackground)
  //   keepEmpty an empty string is a legal value (e.g. column header)
  //   opts / emptyLabel / emptyAlias for selects; list (datalist id); ltr; step/min/max; ph; rerender

  const cssEscape = (s) => (window.CSS && CSS.escape ? CSS.escape(s) : String(s).replace(/["\\]/g, '\\$&'));

  function sec(id, title, kids, closedByDefault) {
    const d = h('details', { class: 'sec', dataset: { sec: id } });
    d.open = state.secOpen.has(id) ? state.secOpen.get(id) : !closedByDefault;
    d.addEventListener('toggle', () => state.secOpen.set(id, d.open));
    d.append(h('summary', null, title), h('div', { class: 'sec-body' }, kids));
    return d;
  }

  function row(label, ctl, cls) {
    return h('div', { class: 'row' + (cls ? ' ' + cls : '') },
      h('span', { class: 'lbl', title: label }, label),
      h('div', { class: 'ctl' }, ctl));
  }

  function writeProp(getObj, f, v, coalesceKey, rerender) {
    mutate(() => {
      const o = getObj();
      if (!o) return;
      if (v === undefined) {
        if (f.nullable) o[f.k] = null;
        else delete o[f.k];
      } else o[f.k] = v;
      if (f.after) f.after(o);
    }, { inspector: !!rerender, coalesce: coalesceKey || undefined });
  }

  function control(getObj, f, scope) {
    const fkey = scope + '.' + f.k;
    const obj = getObj();
    const cur = obj ? obj[f.k] : undefined;
    let val = cur === undefined ? f.def : cur;
    const commit = (v, coalesce, rerender) => writeProp(getObj, f, v, coalesce ? fkey : null, rerender);

    switch (f.t) {
      case 'num': {
        const inp = h('input', {
          type: 'number', step: f.step != null ? f.step : 'any', min: f.min, max: f.max,
          value: val == null ? '' : String(val),
          placeholder: f.ph != null ? f.ph : (f.def != null ? String(f.def) : ''),
          dataset: { fkey },
        });
        inp.addEventListener('input', () => {
          if (inp.value === '') {
            if (!f.required) commit(undefined, true);
            return;
          }
          const n = Number(inp.value);
          if (!Number.isFinite(n) || (f.int && !Number.isInteger(n))) return;
          if (f.min != null && n < f.min) return;
          if (f.max != null && n > f.max) return;
          commit(n, true);
        });
        inp.addEventListener('change', () => {
          if (inp.value === '' && f.required) {
            const o = getObj();
            inp.value = o && o[f.k] != null ? String(o[f.k]) : '';
          }
          if (f.rerender) renderInspector();
        });
        return inp;
      }
      case 'bool': {
        const cb = h('input', { type: 'checkbox', checked: !!val, dataset: { fkey } });
        cb.addEventListener('change', () => commit(cb.checked, false, f.rerender));
        return cb;
      }
      case 'tri': {
        const sel = selectEl([['', f.emptyLabel || '(ارث)'], ['true', 'بله'], ['false', 'خیر']],
          val === true ? 'true' : val === false ? 'false' : '', { dataset: { fkey } });
        sel.addEventListener('change', () => commit(sel.value === '' ? undefined : sel.value === 'true', false, f.rerender));
        return sel;
      }
      case 'select':
      case 'font':
      case 'style': {
        let opts;
        if (f.t === 'font') opts = fontNames().map((n) => [n, n]);
        else if (f.t === 'style') {
          const ex = f.exclude ? lower(f.exclude()) : null;
          opts = styleNames().filter((n) => lower(n) !== ex).map((n) => [n, n]);
        } else opts = f.opts.slice();
        if (f.emptyAlias && lower(val) === lower(f.emptyAlias)) val = '';
        let selVal = '';
        if (val != null && val !== '') {
          const m = opts.find(([v]) => lower(v) === lower(val));
          if (m) selVal = m[0];
          else {
            opts.push([String(val), String(val) + ' (نامعتبر)']);
            selVal = String(val);
          }
        }
        if (!f.required) opts.unshift(['', f.emptyLabel || '(ارث)']);
        else if (!selVal && opts.length) selVal = String(f.def != null ? f.def : opts[0][0]);
        const sel = selectEl(opts, selVal, { dataset: { fkey } });
        sel.addEventListener('change', () => commit(sel.value === '' ? undefined : sel.value, false, f.rerender));
        return sel;
      }
      case 'color':
        return colorControl(val, (v) => commit(v, true, f.rerender), !f.required, fkey);
      case 'textarea':
      case 'expr': {
        const ta = h('textarea', { rows: f.rows || 3, dir: 'auto', dataset: { fkey }, spellcheck: 'false', placeholder: f.ph || '' });
        ta.value = val == null ? '' : String(val);
        ta.addEventListener('input', () => {
          const v = ta.value;
          if (v === '' && !f.keepEmpty) commit(undefined, true);
          else commit(v, true);
        });
        if (f.t === 'textarea') return ta;
        const ins = exprSelect();
        ins.addEventListener('change', () => {
          const v = ins.value;
          ins.value = '';
          if (!v) return;
          insertAtCaret(ta, v);
          ta.dispatchEvent(new Event('input'));
          ta.focus();
        });
        return h('div', { style: 'display:flex;flex-direction:column;gap:4px;width:100%' }, ta, h('div', { class: 'expr-tools' }, ins));
      }
      default: { // text
        const inp = h('input', {
          type: 'text', value: val == null ? '' : String(val), placeholder: f.ph || '',
          list: f.list, class: f.ltr ? 'ltr' : null, dataset: { fkey }, spellcheck: 'false', autocomplete: 'off',
        });
        inp.addEventListener('input', () => {
          const raw = inp.value;
          if (raw === '' && !f.keepEmpty) {
            if (f.required) return;
            commit(undefined, true);
          } else commit(raw, true);
        });
        if (f.rerender) inp.addEventListener('change', () => renderInspector());
        return inp;
      }
    }
  }

  function formRows(getObj, defs, scope) {
    return defs.map((f) => {
      const ctl = control(getObj, f, scope);
      const cls = f.t === 'expr' || f.t === 'textarea' ? 'stack' : f.t === 'bool' ? 'check' : null;
      return row(f.label, ctl, cls);
    });
  }

  function colorControl(value, onSet, clearable, fkey) {
    const hex = normHex(value);
    const wrap = h('div', { class: 'color-ctl' + (hex ? '' : ' empty') });
    const picker = h('input', { type: 'color', value: hex ? hex.toLowerCase() : '#000000', title: 'انتخاب رنگ', dataset: { fkey: fkey + '.pick' } });
    const txt = h('input', {
      type: 'text', class: 'hex', value: hex || '', maxlength: '7', spellcheck: 'false', autocomplete: 'off',
      placeholder: clearable ? 'بدون رنگ' : '#000000', dataset: { fkey },
    });
    picker.addEventListener('input', () => {
      const v = picker.value.toUpperCase();
      txt.value = v;
      txt.classList.remove('invalid');
      wrap.classList.remove('empty');
      onSet(v);
    });
    txt.addEventListener('change', () => {
      const v = txt.value.trim();
      if (!v) {
        if (clearable) {
          wrap.classList.add('empty');
          onSet(undefined);
        } else txt.value = hex || '';
        return;
      }
      const n = normHex(v);
      if (!n) {
        txt.classList.add('invalid');
        toast('رنگ باید به شکل ‎#RRGGBB‎ باشد.', 'error');
        return;
      }
      txt.classList.remove('invalid');
      txt.value = n;
      picker.value = n.toLowerCase();
      wrap.classList.remove('empty');
      onSet(n);
    });
    wrap.append(picker, txt);
    if (clearable) {
      wrap.append(h('button', {
        type: 'button', class: 'btn tiny clear', title: 'بدون رنگ (حذف مقدار)', 'aria-label': 'حذف رنگ',
        onClick: () => {
          txt.value = '';
          txt.classList.remove('invalid');
          wrap.classList.add('empty');
          onSet(undefined);
        },
      }, icon('x')));
    }
    return wrap;
  }

  function exprSelect() {
    const sel = h('select', { title: 'درج عبارت در محل مکان‌نما', 'aria-label': 'درج عبارت' });
    sel.append(h('option', { value: '' }, '＋ درج عبارت…'));
    const group = (label, items) => {
      if (!items.length) return;
      const g = h('optgroup', { label });
      for (const [v, text] of items) g.append(h('option', { value: v }, text));
      sel.append(g);
    };
    const fields = knownFields();
    const params = knownParams();
    group('فیلدها', fields.map((f) => [`{${f.name}}`, `${f.title || f.name} — {${f.name}}`]));
    group('پارامترها', params.map((f) => [`{${f.name}}`, `${f.title || f.name} — {${f.name}}`]));
    group('سیستمی', SYSTEM_VARS.map((v) => [`{${v.name}}`, `${v.title} — {${v.name}}`]));
    const aggs = [];
    for (const f of fields.filter((x) => lower(x.type) === 'number')) {
      for (const [a, l] of [['Sum', 'جمع'], ['Avg', 'میانگین'], ['Min', 'کمینه'], ['Max', 'بیشینه'], ['PageSum', 'جمع صفحه'], ['GroupSum', 'جمع گروه']]) {
        aggs.push([`{${a}(${f.name})}`, `${l} ${f.title || f.name} — {${a}(${f.name})}`]);
      }
    }
    aggs.push(['{Count}', 'تعداد ردیف‌ها — {Count}']);
    group('تجمیعی', aggs);
    const fmts = [];
    for (const f of fields) {
      const t = lower(f.type);
      if (t === 'number') fmts.push([`{${f.name}:N0}`, `${f.title || f.name} با جداکننده — {${f.name}:N0}`]);
      else if (t === 'date') fmts.push([`{${f.name}:persian}`, `${f.title || f.name} شمسی — {${f.name}:persian}`]);
    }
    group('با قالب‌بندی', fmts);
    group('نویسهٔ آکولاد', [['{{', 'آکولاد باز — {{'], ['}}', 'آکولاد بسته — }}']]);
    return sel;
  }

  function insertAtCaret(ta, text) {
    const s = ta.selectionStart != null ? ta.selectionStart : ta.value.length;
    const e = ta.selectionEnd != null ? ta.selectionEnd : s;
    ta.value = ta.value.slice(0, s) + text + ta.value.slice(e);
    ta.selectionStart = ta.selectionEnd = s + text.length;
  }

  function styleFieldDefs(includeStyleOnly) {
    const d = [
      { k: 'font', label: 'فونت', t: 'font' },
      { k: 'fontSize', label: 'اندازهٔ قلم', t: 'num', step: 0.5, min: 1 },
      { k: 'bold', label: 'ضخیم', t: 'tri' },
      { k: 'underline', label: 'زیرخط', t: 'tri' },
      { k: 'color', label: 'رنگ متن', t: 'color' },
      { k: 'background', label: 'پس‌زمینه', t: 'color' },
      { k: 'align', label: 'تراز افقی', t: 'select', opts: ALIGN_OPTS },
      { k: 'vAlign', label: 'تراز عمودی', t: 'select', opts: VALIGN_OPTS },
      { k: 'overflow', label: 'سرریز متن', t: 'select', opts: OVERFLOW_OPTS },
      { k: 'direction', label: 'جهت متن', t: 'select', opts: DIRECTION_OPTS },
      { k: 'digits', label: 'ارقام', t: 'select', opts: DIGITS_OPTS },
      { k: 'padding', label: 'فاصلهٔ داخلی', t: 'num', step: 0.5, min: 0 },
    ];
    if (includeStyleOnly) {
      d.push({ k: 'minFontSize', label: 'حداقل قلم', t: 'num', step: 0.5, min: 1 });
      d.push({ k: 'lineSpacing', label: 'فاصلهٔ سطرها', t: 'num', step: 0.05, min: 0.5 });
    }
    return d;
  }

  function borderEditor(getOwner, scope) {
    const owner = getOwner();
    const b = owner ? owner.border : null;
    const enable = h('input', { type: 'checkbox', checked: isObj(b), dataset: { fkey: scope + '.on' } });
    enable.addEventListener('change', () => mutate(() => {
      const o = getOwner();
      if (!o) return;
      if (enable.checked) o.border = { color: '#000000', width: 0.5 };
      else delete o.border;
    }, { inspector: true }));
    const out = [row('حاشیه دارد', enable, 'check')];
    if (!isObj(b)) return out;
    const getB = () => {
      const o = getOwner();
      return o && isObj(o.border) ? o.border : null;
    };
    out.push(...formRows(getB, [
      { k: 'color', label: 'رنگ', t: 'color', required: true, def: '#000000' },
      { k: 'width', label: 'ضخامت', t: 'num', step: 0.25, min: 0, def: 0.5, required: true },
    ], scope));
    const cur = b.sides ? String(b.sides).toUpperCase() : 'LTRB';
    const boxes = [];
    for (const [k, label] of [['T', 'بالا'], ['B', 'پایین'], ['R', 'راست'], ['L', 'چپ']]) {
      const cb = h('input', { type: 'checkbox', checked: cur.includes(k), dataset: { fkey: scope + '.side' + k } });
      cb.addEventListener('change', () => {
        const on = new Set(boxes.filter((x) => x.cb.checked).map((x) => x.k));
        const s = 'LTRB'.split('').filter((c) => on.has(c)).join('');
        mutate(() => {
          const bb = getB();
          if (!bb) return;
          if (s.length === 0 || s.length === 4) delete bb.sides;
          else bb.sides = s;
        }, { inspector: true });
      });
      boxes.push({ k, cb, el: h('label', null, cb, label) });
    }
    out.push(row('اضلاع', h('div', { class: 'sides' }, boxes.map((x) => x.el))));
    out.push(...formRows(getB, [
      { k: 'dashed', label: 'خط‌چین', t: 'bool' },
      { k: 'radius', label: 'گردی گوشه', t: 'num', step: 0.5, min: 0, def: 0 },
    ], scope));
    return out;
  }

  function headBlock(head, iconName, title, sub, actions) {
    head.append(icon(iconName),
      h('div', { class: 'grow' }, h('span', { class: 'kind' }, title), sub ? h('span', { class: 'sub' }, sub) : null));
    if (actions) head.append(actions);
  }

  function renderInspector() {
    if (!state.tpl) return;
    const head = $('inspHead');
    const body = $('inspBody');
    const key = selectionKey();
    const same = key === state.lastInspKey;
    const scroll = body.scrollTop;
    const active = document.activeElement;
    const fk = active && body.contains(active) && active.dataset ? active.dataset.fkey : null;
    head.replaceChildren();
    body.replaceChildren();
    const s = state.sel;
    if (!s) inspectDocument(head, body);
    else if (s.kind === 'els') {
      if (s.items.length > 1) inspectMulti(head, body);
      else inspectElement(head, body, s.items[0]);
    } else if (s.kind === 'band') inspectBand(head, body, s.band);
    else if (s.kind === 'table') inspectTable(head, body);
    else if (s.kind === 'col') inspectColumn(head, body, s.index);
    state.lastInspKey = key;
    body.scrollTop = same ? scroll : 0;
    if (fk) {
      const el = body.querySelector(`[data-fkey="${cssEscape(fk)}"]`);
      if (el) el.focus();
    }
  }

  function focusInspectorText() {
    const ta = $('inspBody').querySelector('[data-fkey="el.text"]');
    if (ta) {
      ta.focus();
      ta.select();
    }
  }

  // ── document ──

  function inspectDocument(head, body) {
    const T = () => state.tpl;
    const Pg = () => state.tpl.page;
    const P = pageInfo();
    headBlock(head, 'file', 'ویژگی‌های سند', state.tpl.name || '');
    const custom = lower(state.tpl.page.size) === 'custom';
    body.append(
      sec('doc-main', 'سند', formRows(T, [
        { k: 'name', label: 'نام گزارش', t: 'text', keepEmpty: true },
        { k: 'description', label: 'توضیحات', t: 'textarea', rows: 2 },
        { k: 'rightToLeft', label: 'راست‌به‌چپ', t: 'bool', def: true },
        { k: 'digits', label: 'ارقام', t: 'select', opts: DIGITS_OPTS, required: true, def: 'Persian' },
        { k: 'defaultFont', label: 'فونت پیش‌فرض', t: 'font', emptyLabel: '(بدون)' },
        { k: 'defaultFontSize', label: 'اندازهٔ قلم پیش‌فرض', t: 'num', def: 9, min: 1, step: 0.5 },
      ], 'doc')),
      sec('doc-page', 'صفحه', [
        ...formRows(Pg, [{
          k: 'size', label: 'اندازه', t: 'select', opts: SIZE_OPTS, required: true, def: 'A4', rerender: true,
          after: (o) => {
            if (lower(o.size) === 'custom' && !(num(o.width, 0) > 0 && num(o.height, 0) > 0)) {
              o.width = round2(P.w);
              o.height = round2(P.h);
            }
          },
        }], 'page'),
        custom ? formRows(Pg, [
          { k: 'width', label: 'عرض (pt)', t: 'num', min: 10, step: 0.5 },
          { k: 'height', label: 'ارتفاع (pt)', t: 'num', min: 10, step: 0.5 },
        ], 'page') : null,
        ...formRows(Pg, [{ k: 'landscape', label: custom ? 'افقی (برای سفارشی بی‌اثر)' : 'افقی', t: 'bool', def: false }], 'page'),
        h('div', { class: 'side-title', style: 'margin:6px 0 0' }, 'حاشیه‌ها (pt)'),
        h('div', { class: 'grid4' }, formRows(Pg, [
          { k: 'marginRight', label: 'راست', t: 'num', def: 20, min: 0, step: 0.5 },
          { k: 'marginLeft', label: 'چپ', t: 'num', def: 20, min: 0, step: 0.5 },
          { k: 'marginTop', label: 'بالا', t: 'num', def: 20, min: 0, step: 0.5 },
          { k: 'marginBottom', label: 'پایین', t: 'num', def: 20, min: 0, step: 0.5 },
        ], 'page')),
        h('div', { class: 'hint' }, `صفحه \u2066${fmtPt(P.w)} × ${fmtPt(P.h)} pt\u2069 — عرض ناحیهٔ محتوا \u2066${fmtPt(P.cw)} pt\u2069`),
      ]),
      sec('doc-wm', 'واترمارک', watermarkEditor(), !state.tpl.watermark),
      sec('doc-fonts', `فونت‌ها (${state.tpl.fonts.length})`, fontsEditor(), true),
      sec('doc-styles', `سبک‌های نام‌دار (${styleNames().length})`, stylesEditor(), true),
      sec('doc-fields', `فیلدها (${state.tpl.fields.length})`, fieldListEditor('fields'), true),
      sec('doc-params', `پارامترها (${state.tpl.parameters.length})`, fieldListEditor('parameters'), true),
    );
  }

  function watermarkEditor() {
    const wm = state.tpl.watermark;
    const cb = h('input', { type: 'checkbox', checked: isObj(wm), dataset: { fkey: 'wm.on' } });
    cb.addEventListener('change', () => mutate((t) => {
      if (cb.checked) t.watermark = Object.assign({}, WATERMARK_DEFAULTS);
      else delete t.watermark;
    }, { inspector: true }));
    const out = [row('فعال', cb, 'check')];
    if (isObj(wm)) {
      out.push(...formRows(() => state.tpl.watermark, [
        { k: 'text', label: 'متن', t: 'text', keepEmpty: true },
        { k: 'font', label: 'فونت', t: 'font' },
        { k: 'fontSize', label: 'اندازه', t: 'num', def: 60, min: 1, step: 1 },
        { k: 'color', label: 'رنگ', t: 'color', required: true, def: '#C00000' },
        { k: 'opacity', label: 'شفافیت (۰ تا ۱)', t: 'num', def: 0.12, min: 0, max: 1, step: 0.01 },
        { k: 'angle', label: 'زاویه (درجه)', t: 'num', def: 45, step: 1 },
      ], 'wm'));
    }
    return out;
  }

  function fontsEditor() {
    const out = [];
    state.tpl.fonts.forEach((f, i) => {
      const get = () => state.tpl.fonts[i];
      const others = fontNames().filter((n) => n !== f.name).map((n) => [n, n]);
      const missing = state.server.online && f.path && !fontUrl(f.path);
      out.push(h('div', { class: 'list-card' },
        h('div', { class: 'card-head' },
          h('span', { class: 'grow' }, f.name || 'فونت بی‌نام'),
          btn(null, 'trash', () => mutate((t) => { t.fonts.splice(i, 1); }, { inspector: true }), { cls: 'tiny danger', title: 'حذف فونت' })),
        formRows(get, [
          { k: 'name', label: 'نام', t: 'text', keepEmpty: true },
          { k: 'path', label: 'فایل', t: 'text', keepEmpty: true, list: 'dl-fontfiles', ltr: true, rerender: true },
          { k: 'boldPath', label: 'فایل ضخیم', t: 'text', list: 'dl-fontfiles', ltr: true },
          { k: 'fallback', label: 'جایگزین', t: 'select', opts: others, emptyLabel: '(هیچ)' },
        ], 'font' + i),
        missing ? h('div', { class: 'notice warn' }, `فایل «${fileBaseName(f.path)}» در فهرست فونت‌های سرور نیست؛ روی بوم با فونت جایگزین نمایش داده می‌شود.`) : null));
    });
    out.push(h('div', { class: 'btn-row' }, btn('افزودن فونت', 'plus', () => mutate((t) => {
      const sf = state.server.fonts.find((x) => !/bold/i.test(x.file)) || state.server.fonts[0];
      t.fonts.push({
        name: sf ? (sf.family || sf.file.replace(/\.[^.]+$/, '')) : 'Font' + (t.fonts.length + 1),
        path: sf ? sf.file : '',
      });
    }, { inspector: true }))));
    if (!state.server.online) out.push(h('div', { class: 'hint' }, 'سرور در دسترس نیست؛ فهرست فایل‌های فونت سرور نمایش داده نمی‌شود.'));
    return out;
  }

  function stylesEditor() {
    const names = styleNames();
    state.styleSel = state.styleSel ? findStyleKey(state.styleSel) : null;
    if (!state.styleSel) state.styleSel = names[0] || null;
    const out = [];
    const bar = h('div', { class: 'btn-row' });
    if (names.length) {
      const sel = selectEl(names.map((n) => [n, n]), state.styleSel, { dataset: { fkey: 'styles.sel' }, style: 'flex:1;min-width:0' });
      sel.addEventListener('change', () => {
        state.styleSel = sel.value;
        renderInspector();
      });
      bar.append(sel);
    }
    bar.append(btn(names.length ? null : 'سبک جدید', 'plus', addStyle, { cls: 'tiny outline', title: 'سبک جدید' }));
    if (state.styleSel) {
      const cur = state.styleSel;
      bar.append(
        btn('تغییر نام', null, () => renameStyle(cur), { cls: 'tiny outline' }),
        btn(null, 'trash', () => deleteStyle(cur), { cls: 'tiny danger', title: 'حذف سبک' }));
    }
    out.push(bar);
    if (!state.styleSel) {
      out.push(h('div', { class: 'empty-note' }, 'سبکی تعریف نشده است. سبک‌ها را می‌توان به عناصر، ستون‌ها و بخش‌های جدول نسبت داد.'));
      return out;
    }
    const key = state.styleSel;
    const get = () => state.tpl.styles[key];
    out.push(...formRows(get, [{ k: 'basedOn', label: 'بر پایهٔ', t: 'style', exclude: () => key, emptyLabel: '(هیچ)' }, ...styleFieldDefs(true)], 'style:' + key));
    out.push(...borderEditor(get, 'style:' + key + '.border'));
    return out;
  }

  function validStyleName(v, current) {
    const s = v.trim();
    if (!s) return 'نام نمی‌تواند خالی باشد.';
    if (UNSAFE_KEYS.has(s)) return 'این نام مجاز نیست.';
    const ex = findStyleKey(s);
    if (ex && ex !== current) return 'سبکی با این نام وجود دارد.';
    return null;
  }

  async function addStyle() {
    const name = await promptDialog('سبک جدید', 'نام سبک', 'Style' + (styleNames().length + 1), (v) => validStyleName(v, null));
    if (!name) return;
    const n = name.trim();
    state.styleSel = n;
    state.secOpen.set('doc-styles', true);
    mutate((t) => {
      t.styles[n] = {};
    }, { inspector: true });
  }

  function forEachStyleRef(t, fn) {
    for (const k of Object.keys(t.styles)) fn(t.styles[k], 'basedOn');
    for (const k of ELEMENT_BANDS) if (t[k]) for (const el of t[k].elements) fn(el, 'style');
    if (t.table) {
      fn(t.table, 'headerStyle');
      fn(t.table, 'rowStyle');
      fn(t.table, 'totalsStyle');
      for (const c of t.table.columns) fn(c, 'style');
    }
  }

  async function renameStyle(old) {
    const nn = await promptDialog('تغییر نام سبک', 'نام جدید', old, (v) => validStyleName(v, old));
    if (!nn || nn.trim() === old) return;
    const n = nn.trim();
    state.styleSel = n;
    mutate((t) => {
      const entries = Object.keys(t.styles).map((k) => [k === old ? n : k, t.styles[k]]);
      t.styles = {};
      for (const [k, v] of entries) t.styles[k] = v;
      forEachStyleRef(t, (o, prop) => {
        if (o && lower(o[prop]) === lower(old)) o[prop] = n;
      });
    }, { inspector: true });
  }

  function deleteStyle(name) {
    state.styleSel = null;
    mutate((t) => {
      delete t.styles[name];
      forEachStyleRef(t, (o, prop) => {
        if (o && lower(o[prop]) === lower(name)) delete o[prop];
      });
    }, { inspector: true });
    toast(`سبک «${name}» حذف شد و ارجاع‌هایش پاک شد.`);
  }

  function fieldListEditor(listKey) {
    const isFields = listKey === 'fields';
    const out = [];
    state.tpl[listKey].forEach((f, i) => {
      const get = () => state.tpl[listKey][i];
      out.push(h('div', { class: 'list-card' },
        h('div', { class: 'card-head' },
          h('span', { class: 'grow' }, f.name || '(بی‌نام)'),
          btn(null, 'trash', () => mutate((t) => { t[listKey].splice(i, 1); }, { inspector: true }), { cls: 'tiny danger', title: 'حذف' })),
        formRows(get, [
          { k: 'name', label: 'نام', t: 'text', keepEmpty: true, ltr: true },
          { k: 'title', label: 'عنوان', t: 'text' },
          { k: 'type', label: 'نوع', t: 'select', opts: FIELD_TYPES, required: true, def: 'string' },
          { k: 'sample', label: 'نمونه', t: 'text' },
        ], listKey + i)));
    });
    if (!out.length) out.push(h('div', { class: 'empty-note' }, isFields ? 'فیلدی تعریف نشده است.' : 'پارامتری تعریف نشده است.'));
    out.push(h('div', { class: 'btn-row' }, btn(isFields ? 'افزودن فیلد' : 'افزودن پارامتر', 'plus', () => {
      state.secOpen.set(isFields ? 'doc-fields' : 'doc-params', true);
      mutate((t) => {
        t[listKey].push({ name: (isFields ? 'Field' : 'Param') + (t[listKey].length + 1), type: 'string' });
      }, { inspector: true });
    })));
    return out;
  }

  // ── elements ──

  function elSummary(el) {
    const type = normType(el.type);
    if (type === 'Text') {
      const s = String(el.text || '').replace(/\s+/g, ' ');
      return s.length > 40 ? s.slice(0, 40) + '…' : (s || 'متن خالی');
    }
    if (type === 'Image') return el.source && !/^data:/i.test(el.source) ? fileBaseName(el.source) : 'تصویر';
    return TYPE_LABELS[type];
  }

  const TYPE_ICONS = { Text: 'text', Line: 'line', Rect: 'rect', Image: 'image' };

  function inspectElement(head, body, item) {
    const { band, index } = item;
    const get = () => (state.tpl[band] ? state.tpl[band].elements[index] : null);
    const el = get();
    if (!el) return;
    const type = normType(el.type);
    const scope = 'el';
    headBlock(head, TYPE_ICONS[type], TYPE_LABELS[type], 'در ' + BAND_LABELS[band] + (el.name ? ' — ' + el.name : ''),
      h('div', { class: 'btn-row' },
        btn(null, 'copy', duplicateSelection, { cls: 'tiny', title: 'تکثیر (Ctrl+D)' }),
        btn(null, 'trash', deleteSelection, { cls: 'tiny danger', title: 'حذف (Delete)' })));

    const bandSel = selectEl(ELEMENT_BANDS.filter((k) => state.tpl[k]).map((k) => [k, BAND_LABELS[k]]), band, { dataset: { fkey: 'el.band' } });
    bandSel.addEventListener('change', () => moveElementToBand(band, index, bandSel.value));

    body.append(sec('el-gen', 'عمومی', [
      ...formRows(get, [{ k: 'name', label: 'نام', t: 'text', ltr: true }], scope),
      row('باند', bandSel),
      ...formRows(get, [{ k: 'visibleWhen', label: 'نمایش به شرط', t: 'text', ltr: true, ph: '{ShowLogo}' }], scope),
    ]));
    body.append(sec('el-geo', 'موقعیت و اندازه (pt)', [
      h('div', { class: 'grid2' }, formRows(get, [
        { k: 'x', label: 'X', t: 'num', required: true, step: 0.5 },
        { k: 'y', label: 'Y', t: 'num', required: true, step: 0.5 },
        { k: 'width', label: 'عرض', t: 'num', required: true, min: 0, step: 0.5 },
        { k: 'height', label: 'ارتفاع', t: 'num', required: true, min: 0, step: 0.5 },
      ], scope)),
      h('div', { class: 'btn-row' },
        btn('جلو', 'toFront', () => reorderElement(band, index, true), { cls: 'tiny outline', title: 'آوردن به رو' }),
        btn('عقب', 'toBack', () => reorderElement(band, index, false), { cls: 'tiny outline', title: 'بردن به زیر' })),
    ]));

    if (type === 'Text') {
      body.append(sec('el-text', 'متن', formRows(get, [
        { k: 'text', label: 'متن / عبارت', t: 'expr', ph: 'مثال: مبلغ کل: {Sum(Amount):N0}' },
        { k: 'style', label: 'سبک نام‌دار', t: 'style', emptyLabel: '(بدون)' },
      ], scope)));
      body.append(sec('el-fmt', 'قالب‌بندی درون‌خطی', formRows(get, styleFieldDefs(false), scope)));
      body.append(sec('el-border', 'حاشیه', borderEditor(get, 'el.border'), !isObj(el.border)));
    } else if (type === 'Line') {
      body.append(sec('el-line', 'خط', [
        ...formRows(get, [
          { k: 'lineColor', label: 'رنگ خط', t: 'color' },
          { k: 'lineWidth', label: 'ضخامت', t: 'num', min: 0, step: 0.25 },
          { k: 'dashed', label: 'خط‌چین', t: 'bool' },
        ], scope),
        h('div', { class: 'hint' }, 'خط از (X,Y) تا (X+عرض, Y+ارتفاع) کشیده می‌شود؛ برای خط افقی ارتفاع ۰ و برای عمودی عرض ۰.'),
      ]));
    } else if (type === 'Rect') {
      body.append(sec('el-rect', 'مستطیل', formRows(get, [
        { k: 'lineColor', label: 'رنگ خط', t: 'color' },
        { k: 'lineWidth', label: 'ضخامت خط', t: 'num', min: 0, step: 0.25 },
        { k: 'dashed', label: 'خط‌چین', t: 'bool' },
        { k: 'cornerRadius', label: 'گردی گوشه', t: 'num', min: 0, step: 0.5, def: 0 },
        { k: 'background', label: 'پس‌زمینه', t: 'color' },
      ], scope)));
    } else if (type === 'Image') {
      const src = String(el.source || '');
      const isData = /^data:/i.test(src);
      const rows = [];
      if (isData) {
        rows.push(row('منبع', h('span', { class: 'hint', style: 'margin:0' }, `تصویر جاسازی‌شده (${Math.round((src.length * 0.75) / 1024)} KB)`)));
      } else {
        rows.push(...formRows(get, [{ k: 'source', label: 'مسیر یا پارامتر', t: 'text', ltr: true, ph: 'logo.png یا {Logo}', rerender: true }], scope));
      }
      rows.push(h('div', { class: 'btn-row' },
        btn('بارگذاری فایل…', 'upload', () => pickImage(get), { cls: 'small outline', title: 'جاسازی تصویر PNG/JPEG به صورت data URI' }),
        isData ? btn('حذف تصویر', 'x', () => mutate(() => { const o = get(); if (o) delete o.source; }, { inspector: true }), { cls: 'small danger' }) : null));
      rows.push(...formRows(get, [{ k: 'keepAspectRatio', label: 'حفظ تناسب', t: 'bool', def: true }], scope));
      body.append(sec('el-img', 'تصویر', rows));
      body.append(sec('el-border', 'حاشیه', borderEditor(get, 'el.border'), !isObj(el.border)));
    }
  }

  function pickImage(get) {
    state.imageTarget = get;
    const inp = $('imageFile');
    inp.value = '';
    inp.click();
  }

  function onImageFile() {
    const inp = $('imageFile');
    const file = inp.files && inp.files[0];
    const get = state.imageTarget;
    if (!file || !get) return;
    if (!/^image\/(png|jpeg)$/i.test(file.type)) {
      toast('فقط تصاویر PNG یا JPEG پشتیبانی می‌شوند.', 'error');
      return;
    }
    if (file.size > 1024 * 1024) toast('حجم تصویر بیش از ۱ مگابایت است و قالب را سنگین می‌کند.', 'error');
    const r = new FileReader();
    r.onload = () => mutate(() => {
      const o = get();
      if (o) o.source = String(r.result);
    }, { inspector: true });
    r.onerror = () => toast('خواندن فایل تصویر ناموفق بود.', 'error');
    r.readAsDataURL(file);
  }

  function inspectMulti(head, body) {
    const els = selectedElements();
    headBlock(head, 'copy', `${els.length} عنصر انتخاب شده`, 'Shift+کلیک برای افزودن یا حذف از انتخاب',
      h('div', { class: 'btn-row' },
        btn(null, 'copy', duplicateSelection, { cls: 'tiny', title: 'تکثیر (Ctrl+D)' }),
        btn(null, 'trash', deleteSelection, { cls: 'tiny danger', title: 'حذف (Delete)' })));
    const al = [
      ['right', 'alignRight', 'راست'], ['hcenter', 'alignHCenter', 'وسط افقی'], ['left', 'alignLeft', 'چپ'],
      ['top', 'alignTop', 'بالا'], ['vcenter', 'alignVCenter', 'وسط عمودی'], ['bottom', 'alignBottom', 'پایین'],
    ];
    body.append(sec('multi-align', 'هم‌ترازی', [
      h('div', { class: 'btn-row' }, al.map(([m, ic, l]) => btn(null, ic, () => alignSelection(m), { cls: 'small outline', title: 'تراز ' + l }))),
      h('div', { class: 'btn-row' },
        btn('هم‌عرض', null, () => alignSelection('sameWidth'), { cls: 'small outline', title: 'عرض همه برابر با نخستین انتخاب' }),
        btn('هم‌ارتفاع', null, () => alignSelection('sameHeight'), { cls: 'small outline', title: 'ارتفاع همه برابر با نخستین انتخاب' })),
      h('div', { class: 'hint' }, 'هم‌ترازی بالا/پایین بر اساس موقعیت روی صفحه است (حتی بین باندها).'),
    ]));
    body.append(sec('multi-list', 'عناصر انتخاب‌شده', h('div', { class: 'mini-list' }, els.map((x) => h('div', {
      class: 'item', role: 'button', tabindex: '0',
      onClick: () => select({ kind: 'els', items: [{ band: x.band, index: x.index }] }),
    }, icon(TYPE_ICONS[normType(x.el.type)]), h('span', { class: 'grow' }, x.el.name || elSummary(x.el)),
    h('span', { class: 'meta' }, BAND_LABELS[x.band]))))));
  }

  // ── bands ──

  function inspectBand(head, body, key) {
    const get = () => state.tpl[key];
    const band = get();
    headBlock(head, 'band', 'باند ' + BAND_LABELS[key], BAND_HINTS[key],
      btn(null, 'trash', () => removeBand(key), { cls: 'tiny danger', title: 'حذف باند' }));
    const defs = [
      { k: 'height', label: 'ارتفاع (pt)', t: 'num', required: true, min: 1, step: 0.5 },
      { k: 'background', label: 'پس‌زمینه', t: 'color' },
    ];
    if (key === 'pageHeader') defs.push({ k: 'skipFirstPage', label: 'در صفحهٔ اول چاپ نشود', t: 'bool', def: false });
    body.append(sec('band-props', 'ویژگی‌ها', formRows(get, defs, 'band:' + key)));
    const els = band.elements;
    body.append(sec('band-els', `عناصر (${els.length})`, els.length
      ? h('div', { class: 'mini-list' }, els.map((el, i) => h('div', {
        class: 'item', role: 'button', tabindex: '0',
        onClick: () => select({ kind: 'els', items: [{ band: key, index: i }] }),
      }, icon(TYPE_ICONS[normType(el.type)]), h('span', { class: 'grow' }, el.name || elSummary(el)),
      h('span', { class: 'meta' }, `${fmtPt(el.x)}, ${fmtPt(el.y)}`))))
      : h('div', { class: 'empty-note' }, 'عنصری ندارد. از «ابزارها» یا «فیلدها» بکشید.')));
  }

  // ── table & columns ──

  function inspectTable(head, body) {
    const t = state.tpl.table;
    const get = () => state.tpl.table;
    headBlock(head, 'table', 'جدول', `${t.columns.length} ستون`,
      btn(null, 'trash', () => removeBand('table'), { cls: 'tiny danger', title: 'حذف جدول' }));
    const cols = t.columns;
    body.append(sec('tbl-cols', `ستون‌ها (${cols.length})`, [
      cols.length
        ? h('div', { class: 'mini-list' }, cols.map((c, i) => h('div', {
          class: 'item' + (c.visible === false ? ' hidden-col' : ''), role: 'button', tabindex: '0',
          title: c.visible === false ? 'ستون مخفی' : '',
          onClick: () => select({ kind: 'col', index: i }),
        }, h('span', { class: 'meta' }, String(i + 1)), h('span', { class: 'grow' }, c.header || '(بدون عنوان)'),
        h('span', { class: 'meta' }, String(c.field || '')))))
        : h('div', { class: 'empty-note' }, 'ستونی ندارد.'),
      h('div', { class: 'btn-row' }, btn('افزودن ستون', 'plus', () => addColumn(null))),
      h('div', { class: 'hint' }, 'روی سرستون کلیک کنید تا انتخاب شود؛ مرز سرستون‌ها را برای تغییر پهنا بکشید. فیلدها را از پنل «فیلدها» روی جدول رها کنید.'),
    ]));
    body.append(sec('tbl-dim', 'ابعاد و چیدمان', [...formRows(get, [
      { k: 'headerHeight', label: 'ارتفاع سرستون', t: 'num', def: 20, min: 1, step: 0.5 },
      { k: 'rowHeight', label: 'ارتفاع ردیف', t: 'num', def: 16, min: 1, step: 0.5 },
      { k: 'repeatHeader', label: 'تکرار سرستون در هر صفحه', t: 'bool', def: true },
      { k: 'fitWidth', label: 'کشیدن تا پهنای صفحه', t: 'bool', def: true },
      { k: 'maxColumnsPerLine', label: 'حداکثر ستون در هر سطر', t: 'num', def: 0, min: 0, step: 1, int: true },
    ], 'tbl'),
    h('div', { class: 'hint' }, 'اگر تعداد ستون‌های قابل‌مشاهده بیشتر از «حداکثر ستون در هر سطر» باشد، هر رکورد در چند سطر چیده می‌شود (۰ = همیشه یک سطر). با تعیین «سطر» برای ستون‌ها می‌توان چیدمان را دستی مشخص کرد.')]));
    body.append(sec('tbl-look', 'ظاهر', formRows(get, [
      { k: 'headerStyle', label: 'سبک سرستون', t: 'style', emptyLabel: '(بدون)' },
      { k: 'rowStyle', label: 'سبک ردیف‌ها', t: 'style', emptyLabel: '(بدون)' },
      { k: 'totalsStyle', label: 'سبک جمع‌ها', t: 'style', emptyLabel: '(بدون)' },
      { k: 'headerBackground', label: 'زمینهٔ سرستون', t: 'color', def: TABLE_DEFAULTS.headerBackground, nullable: true },
      { k: 'altRowBackground', label: 'زمینهٔ یک‌درمیان', t: 'color', def: TABLE_DEFAULTS.altRowBackground, nullable: true },
      { k: 'totalsBackground', label: 'زمینهٔ جمع‌ها', t: 'color', def: TABLE_DEFAULTS.totalsBackground, nullable: true },
      { k: 'borderColor', label: 'رنگ خطوط', t: 'color', def: TABLE_DEFAULTS.borderColor, required: true },
      { k: 'borderWidth', label: 'ضخامت خطوط', t: 'num', def: 0.5, min: 0, step: 0.25 },
      { k: 'verticalLines', label: 'خطوط عمودی', t: 'bool', def: true },
      { k: 'horizontalLines', label: 'خطوط افقی', t: 'bool', def: true },
    ], 'tbl')));
    body.append(sec('tbl-totals', 'جمع‌ها', formRows(get, [
      { k: 'showGrandTotals', label: 'ردیف جمع کل', t: 'bool', def: false },
      { k: 'grandTotalsLabel', label: 'عنوان جمع کل', t: 'text', def: TABLE_DEFAULTS.grandTotalsLabel },
      { k: 'showPageTotals', label: 'جمع هر صفحه', t: 'bool', def: false },
      { k: 'pageTotalsLabel', label: 'عنوان جمع صفحه', t: 'text', def: TABLE_DEFAULTS.pageTotalsLabel },
      { k: 'showCarryForward', label: 'نقل از صفحهٔ قبل', t: 'bool', def: false },
      { k: 'carryForwardLabel', label: 'عنوان نقل از قبل', t: 'text', def: TABLE_DEFAULTS.carryForwardLabel },
    ], 'tbl')));
    body.append(sec('tbl-group', 'گروه‌بندی', [
      ...formRows(get, [
        { k: 'groupBy', label: 'فیلد گروه', t: 'text', list: 'dl-fields', ltr: true, ph: 'BranchName' },
        { k: 'groupHeaderText', label: 'عنوان گروه', t: 'expr', ph: 'شعبه: {BranchName}' },
        { k: 'groupHeaderBackground', label: 'زمینهٔ عنوان گروه', t: 'color', def: TABLE_DEFAULTS.groupHeaderBackground, nullable: true },
        { k: 'showGroupTotals', label: 'جمع هر گروه', t: 'bool', def: false },
        { k: 'groupTotalsLabel', label: 'عنوان جمع گروه', t: 'text', def: TABLE_DEFAULTS.groupTotalsLabel },
      ], 'tbl'),
      h('div', { class: 'hint' }, 'داده باید بر اساس فیلد گروه مرتب باشد.'),
    ], !t.groupBy));
    body.append(sec('tbl-misc', 'سایر', formRows(get, [
      { k: 'emptyText', label: 'متن جدول خالی', t: 'text', def: TABLE_DEFAULTS.emptyText, nullable: true },
    ], 'tbl'), true));
  }

  function inspectColumn(head, body, i) {
    const t = state.tpl.table;
    const c = t.columns[i];
    const get = () => (state.tpl.table ? state.tpl.table.columns[i] : null);
    const rtl = tplRtl();
    headBlock(head, 'table', 'ستون «' + (c.header || c.field || i + 1) + '»', `ستون ${i + 1} از ${t.columns.length}`,
      btn(null, 'trash', () => deleteColumn(i), { cls: 'tiny danger', title: 'حذف ستون' }));
    body.append(h('div', { class: 'sec-body', style: 'padding-top:10px;border-bottom:1px solid var(--border)' }, h('div', { class: 'btn-row' },
      btn('به ابتدا', rtl ? 'chevronRight' : 'chevronLeft', () => moveColumn(i, -1), { disabled: i === 0, title: 'انتقال یک خانه به سمت ابتدای جدول' }),
      btn('به انتها', rtl ? 'chevronLeft' : 'chevronRight', () => moveColumn(i, 1), { disabled: i === t.columns.length - 1, title: 'انتقال یک خانه به سمت انتهای جدول' }),
      btn('ستون جدید', 'plus', () => addColumn(i), { title: 'افزودن ستون بعد از این ستون' }),
      btn('جدول', 'table', () => select({ kind: 'table' }), { cls: 'small', title: 'ویژگی‌های جدول' }))));
    body.append(sec('col-main', 'ستون', formRows(get, [
      { k: 'header', label: 'عنوان', t: 'text', keepEmpty: true },
      { k: 'field', label: 'فیلد یا عبارت', t: 'text', keepEmpty: true, list: 'dl-fields', ltr: true, ph: 'Amount یا {A} - {B}' },
      { k: 'width', label: 'پهنا', t: 'num', required: true, min: 1, step: 0.5 },
      { k: 'format', label: 'قالب', t: 'text', list: 'dl-formats', ltr: true, ph: 'N0' },
      { k: 'visible', label: 'نمایش', t: 'bool', def: true },
      { k: 'line', label: 'سطر (چندسطری)', t: 'num', min: 0, step: 1, int: true, ph: 'خودکار' },
      { k: 'spanLines', label: 'تمام ارتفاع رکورد (چندسطری)', t: 'bool', def: false },
    ], 'col')));
    body.append(sec('col-details', 'ستون جزئیات', [detailsEditor(i)]));
    body.append(sec('col-align', 'تراز و تجمیع', formRows(get, [
      { k: 'align', label: 'تراز داده', t: 'select', opts: ALIGN_OPTS },
      { k: 'headerAlign', label: 'تراز سرستون', t: 'select', opts: ALIGN_OPTS },
      { k: 'aggregate', label: 'تجمیع', t: 'select', opts: AGG_OPTS, emptyLabel: 'هیچ', emptyAlias: 'None' },
    ], 'col')));
    body.append(sec('col-fmt', 'قالب‌بندی', formRows(get, [
      { k: 'style', label: 'سبک نام‌دار', t: 'style', emptyLabel: '(بدون)' },
      { k: 'font', label: 'فونت', t: 'font' },
      { k: 'fontSize', label: 'اندازهٔ قلم', t: 'num', min: 1, step: 0.5 },
      { k: 'bold', label: 'ضخیم', t: 'tri' },
      { k: 'color', label: 'رنگ متن', t: 'color' },
      { k: 'negativeColor', label: 'رنگ اعداد منفی', t: 'color' },
      { k: 'overflow', label: 'سرریز متن', t: 'select', opts: OVERFLOW_OPTS },
      { k: 'direction', label: 'جهت متن', t: 'select', opts: DIRECTION_OPTS },
    ], 'col')));
  }

  /**
   * Editor for column.details: one item per line as «عنوان=فیلد» or «عنوان=فیلد|قالب».
   * Only non-empty fields are printed (label: value), which keeps sparse fields compact.
   */
  function detailsEditor(i) {
    const col = state.tpl.table.columns[i];
    const toText = (arr) => (arr || []).map((d) => (d.label || '') + '=' + (d.field || '') + (d.format ? '|' + d.format : '')).join('\n');
    const ta = h('textarea', { rows: 5, dir: 'auto', spellcheck: 'false', placeholder: 'شماره شبا=Sheba\nکارت مبدا=SourceCardNo\nمبلغ=Amount|N0', dataset: { fkey: 'col.details' } });
    ta.value = toText(col.details);
    ta.addEventListener('input', () => {
      const items = ta.value.split('\n').map((ln) => ln.trim()).filter(Boolean).map((ln) => {
        const eq = ln.indexOf('=');
        const label = eq >= 0 ? ln.slice(0, eq).trim() : '';
        let rest = eq >= 0 ? ln.slice(eq + 1).trim() : ln;
        let format;
        const bar = rest.indexOf('|');
        if (bar >= 0) { format = rest.slice(bar + 1).trim() || undefined; rest = rest.slice(0, bar).trim(); }
        const o = { label, field: rest };
        if (format) o.format = format;
        return o;
      }).filter((d) => d.field);
      mutate((tpl) => {
        const c = tpl.table && tpl.table.columns[i];
        if (!c) return;
        if (items.length) c.details = items; else delete c.details;
      }, { coalesce: 'col.details.' + i });
    });
    const sepInp = h('input', { type: 'text', value: col.detailSeparator == null ? '' : col.detailSeparator, placeholder: '  •  ', dataset: { fkey: 'col.detailSeparator' } });
    sepInp.addEventListener('input', () => mutate((tpl) => {
      const c = tpl.table && tpl.table.columns[i];
      if (!c) return;
      if (sepInp.value === '') delete c.detailSeparator; else c.detailSeparator = sepInp.value;
    }, { coalesce: 'col.sep.' + i }));
    return h('div', { class: 'sec-body' },
      h('div', { class: 'hint' }, 'هر خط: عنوان=فیلد (اختیاری: |قالب). فقط مقادیر پر چاپ می‌شوند؛ مناسب شبا، کارت، پیگیری و ... به‌جای ستون‌های خالی. معمولا همراه با «سطر» = ۱ برای یک سطر تمام‌عرض زیر هر تراکنش.'),
      row('آیتم‌ها', ta, 'stack'), row('جداکننده', sepInp));
  }

  // ════════════════════════════════════════════════════════════════════
  // 13. Dialogs & toasts
  // ════════════════════════════════════════════════════════════════════

  function toast(msg, type = 'info', ms) {
    const box = $('toasts');
    const tEl = h('div', { class: 'toast ' + type, role: type === 'error' ? 'alert' : 'status' },
      h('span', { class: 'msg' }, String(msg)),
      h('button', { type: 'button', 'aria-label': 'بستن', onClick: () => tEl.remove() }, '×'));
    box.append(tEl);
    setTimeout(() => tEl.remove(), ms || (type === 'error' ? 9000 : 3500));
    while (box.children.length > 5) box.firstChild.remove();
  }

  const modalStack = [];

  /** Generic modal. buttons: [{label, primary, danger, onClick}] — onClick returning false keeps it open. */
  function openModal({ title, body, buttons = [], wide = false, onClose }) {
    const prevFocus = document.activeElement;
    const backdrop = h('div', { class: 'modal-backdrop' });
    const modal = h('div', { class: 'modal' + (wide ? ' wide' : ''), role: 'dialog', 'aria-modal': 'true', 'aria-label': title });
    let closed = false;
    const api = {
      el: modal,
      close() {
        if (closed) return;
        closed = true;
        backdrop.remove();
        const i = modalStack.indexOf(api);
        if (i >= 0) modalStack.splice(i, 1);
        if (onClose) onClose();
        if (prevFocus && prevFocus.isConnected && typeof prevFocus.focus === 'function') prevFocus.focus();
      },
    };
    const foot = buttons.length ? h('div', { class: 'modal-foot' }, buttons.map((b) => h('button', {
      type: 'button',
      class: 'btn ' + (b.primary ? 'primary' : b.danger ? 'outline danger' : 'outline'),
      dataset: b.primary ? { primary: '1' } : undefined,
      onClick: () => {
        if (b.onClick && b.onClick(api) === false) return;
        api.close();
      },
    }, b.label))) : null;
    modal.append(
      h('div', { class: 'modal-head' }, h('h2', null, title),
        h('button', { type: 'button', class: 'btn icon-only', 'aria-label': 'بستن', title: 'بستن', onClick: () => api.close() }, icon('x'))),
      h('div', { class: 'modal-body' }, body),
      foot);
    backdrop.append(modal);
    backdrop.addEventListener('pointerdown', (e) => {
      if (e.target === backdrop) api.close();
    });
    $('modalRoot').append(backdrop);
    modalStack.push(api);
    const focusTarget = modal.querySelector('input, textarea, select') || modal.querySelector('[data-primary]') || modal.querySelector('.modal-foot button');
    if (focusTarget) focusTarget.focus();
    return api;
  }

  function confirmDialog(message, okLabel = 'تأیید', danger = false) {
    return new Promise((resolve) => {
      let result = false;
      openModal({
        title: 'تأیید',
        body: h('p', { style: 'margin:0' }, message),
        buttons: [
          { label: okLabel, primary: !danger, danger, onClick: () => { result = true; } },
          { label: 'انصراف' },
        ],
        onClose: () => resolve(result),
      });
    });
  }

  function promptDialog(title, label, value, validate) {
    return new Promise((resolve) => {
      let result = null;
      const inp = h('input', { type: 'text', value: value || '', spellcheck: 'false', autocomplete: 'off' });
      const err = h('div', { class: 'hint', style: 'color:var(--danger);margin:0;min-height:1em' });
      const tryOk = () => {
        const v = inp.value;
        const msg = validate ? validate(v) : null;
        if (msg) {
          err.textContent = msg;
          inp.classList.add('invalid');
          return false;
        }
        result = v.trim();
        return true;
      };
      const m = openModal({
        title,
        body: [h('label', { class: 'field' }, h('span', null, label), inp), err],
        buttons: [{ label: 'تأیید', primary: true, onClick: tryOk }, { label: 'انصراف' }],
        onClose: () => resolve(result),
      });
      inp.addEventListener('keydown', (e) => {
        if (e.key === 'Enter') {
          e.preventDefault();
          if (tryOk()) m.close();
        }
      });
      inp.select();
    });
  }

  // ════════════════════════════════════════════════════════════════════
  // 14. Document commands
  // ════════════════════════════════════════════════════════════════════

  const serialize = () => JSON.stringify(state.tpl, null, 2);

  function setBusy(on) {
    document.body.classList.toggle('busy', !!on);
  }

  async function ensureOnline() {
    if (state.server.online) return true;
    await connectServer();
    if (!state.server.online) {
      toast('سرور در دسترس نیست. می‌توانید با «ورود JSON» و «خروجی JSON» به‌صورت آفلاین کار کنید.', 'error');
      return false;
    }
    return true;
  }

  async function confirmDiscard() {
    if (!state.dirty) return true;
    return confirmDialog('تغییرات ذخیره‌نشده از بین می‌رود. ادامه می‌دهید؟', 'ادامه', true);
  }

  async function cmdNew() {
    if (!(await confirmDiscard())) return;
    setTemplate(newTemplate(), null, false);
    state.suggestedName = null;
    updateChrome();
    zoomFit();
    toast('قالب جدید ایجاد شد.');
  }

  async function loadFromServer(name) {
    if (!(await confirmDiscard())) return false;
    setBusy(true);
    try {
      const raw = await api(tplPath(name), { json: true });
      const t = normalizeTemplate(raw);
      setTemplate(t, name, false);
      zoomFit();
      toast(`قالب «${name}» باز شد.`, 'success');
      return true;
    } catch (e) {
      toast(e.message, 'error');
      return false;
    } finally {
      setBusy(false);
    }
  }

  async function cmdOpen() {
    if (!(await ensureOnline())) return;
    let names;
    try {
      names = await api('api/templates', { json: true });
    } catch (e) {
      toast(e.message, 'error');
      return;
    }
    names = (Array.isArray(names) ? names : [])
      .map((n) => (typeof n === 'string' ? n : isObj(n) && typeof n.name === 'string' ? n.name : null))
      .filter(Boolean);
    const filter = h('input', { type: 'text', placeholder: 'جست‌وجو…', 'aria-label': 'جست‌وجو', autocomplete: 'off' });
    const list = h('div', { class: 'tpl-list' });
    let modal = null;
    const openItem = async (n) => {
      if (await loadFromServer(n)) modal.close();
    };
    const deleteItem = async (n) => {
      if (!(await confirmDialog(`قالب «${n}» از سرور حذف شود؟ این کار برگشت‌پذیر نیست.`, 'حذف', true))) return;
      try {
        await api(tplPath(n), { method: 'DELETE' });
        names = names.filter((x) => x !== n);
        if (state.name === n) {
          state.name = null;
          state.dirty = true;
          updateChrome();
        }
        draw();
        toast(`«${n}» از سرور حذف شد.`, 'success');
      } catch (e) {
        toast(e.message, 'error');
      }
    };
    const draw = () => {
      const q = lower(filter.value.trim());
      const items = names.filter((n) => !q || lower(n).includes(q));
      list.replaceChildren(...(items.length ? items.map((n) => h('div', { class: 'item' + (n === state.name ? ' current' : '') },
        h('span', {
          class: 'name', role: 'button', tabindex: '0', title: 'باز کردن',
          onClick: () => openItem(n),
          onKeydown: (e) => { if (e.key === 'Enter') openItem(n); },
        }, n),
        btn(null, 'folder', () => openItem(n), { cls: 'tiny', title: 'باز کردن' }),
        btn(null, 'trash', () => deleteItem(n), { cls: 'tiny danger', title: 'حذف از سرور', disabled: state.server.readOnly })))
        : [h('div', { class: 'empty-note', style: 'padding:12px' }, names.length ? 'موردی یافت نشد.' : 'قالبی روی سرور وجود ندارد.')]));
    };
    filter.addEventListener('input', draw);
    draw();
    modal = openModal({ title: 'باز کردن قالب از سرور', body: [filter, list], buttons: [{ label: 'بستن' }] });
  }

  function validateTemplateName(v) {
    const s = v.trim();
    if (!s) return 'نام نمی‌تواند خالی باشد.';
    if (/[\\/]/.test(s) || s.includes('..')) return 'نام نباید شامل / یا \\ یا .. باشد.';
    if (s.length > 100) return 'نام بیش از حد طولانی است.';
    return null;
  }

  async function putTemplate(name) {
    if (!(await ensureOnline())) return false;
    if (state.server.readOnly) {
      toast('سرور در حالت فقط‌خواندنی است؛ از «خروجی JSON» استفاده کنید.', 'error');
      return false;
    }
    setBusy(true);
    try {
      await api(tplPath(name), { method: 'PUT', body: serialize() });
      state.name = name;
      state.dirty = false;
      updateChrome();
      scheduleAutosave();
      toast(`ذخیره شد: ${name}`, 'success');
      return true;
    } catch (e) {
      toast(e.message, 'error');
      return false;
    } finally {
      setBusy(false);
    }
  }

  async function cmdSave() {
    if (!state.name) return cmdSaveAs();
    return putTemplate(state.name);
  }

  async function cmdSaveAs() {
    if (!(await ensureOnline())) return false;
    const suggestion = state.name || state.suggestedName || slugify(state.tpl.name);
    const name = await promptDialog('ذخیره با نام', 'نام قالب روی سرور', suggestion, validateTemplateName);
    if (!name) return false;
    if (name !== state.name) {
      try {
        const existing = await api('api/templates', { json: true });
        if (Array.isArray(existing) && existing.some((n) => n === name || (isObj(n) && n.name === name))) {
          if (!(await confirmDialog(`قالبی با نام «${name}» روی سرور وجود دارد. جایگزین شود؟`, 'جایگزینی', true))) return false;
        }
      } catch (e) { /* listing failed — let the PUT report errors */ }
    }
    return putTemplate(name);
  }

  function cmdExport() {
    const blob = new Blob([serialize()], { type: 'application/json' });
    const url = URL.createObjectURL(blob);
    downloadUrl(url, downloadName('.json'));
    setTimeout(() => URL.revokeObjectURL(url), 10000);
    toast('فایل JSON قالب دریافت شد.', 'success');
  }

  function cmdImport() {
    const inp = $('importFile');
    inp.value = '';
    inp.click();
  }

  /** JSON.parse that also tolerates comments and trailing commas (the engine's reader allows both). */
  function parseJsonLenient(text) {
    const src = String(text).replace(/^﻿/, '');
    try {
      return JSON.parse(src);
    } catch (first) {
      let out = '';
      let i = 0;
      let inStr = false;
      while (i < src.length) {
        const c = src[i];
        if (inStr) {
          out += c;
          if (c === '\\') {
            out += src[i + 1] || '';
            i += 2;
            continue;
          }
          if (c === '"') inStr = false;
          i++;
          continue;
        }
        if (c === '"') { inStr = true; out += c; i++; continue; }
        if (c === '/' && src[i + 1] === '/') { while (i < src.length && src[i] !== '\n') i++; continue; }
        if (c === '/' && src[i + 1] === '*') {
          const end = src.indexOf('*/', i + 2);
          i = end < 0 ? src.length : end + 2;
          continue;
        }
        out += c;
        i++;
      }
      try {
        return JSON.parse(out.replace(/,(\s*[}\]])/g, '$1'));
      } catch (e) {
        throw first;
      }
    }
  }

  async function onImportFile() {
    const file = $('importFile').files && $('importFile').files[0];
    if (!file) return;
    let t;
    try {
      t = normalizeTemplate(parseJsonLenient(await file.text()));
    } catch (e) {
      toast('فایل JSON معتبر نیست:\n' + e.message, 'error');
      return;
    }
    if (!(await confirmDiscard())) return;
    setTemplate(t, null, true);
    state.suggestedName = file.name.replace(/\.json$/i, '');
    updateChrome();
    zoomFit();
    toast(`«${file.name}» وارد شد. برای نگهداری روی سرور «ذخیره» را بزنید.`, 'success');
  }

  async function cmdPreview() {
    if (!(await ensureOnline())) return;
    const rows = clamp(parseInt($('rowsInput').value, 10) || 0, 0, 100000);
    const q = new URLSearchParams();
    if (state.dataSource) q.set('dataSource', state.dataSource);
    q.set('rows', String(rows));
    const b = $('btnPreview');
    b.disabled = true;
    setBusy(true);
    try {
      const res = await api('api/preview?' + q.toString(), { method: 'POST', body: serialize(), raw: true });
      const blob = await res.blob();
      const pdf = blob.type === 'application/pdf' ? blob : new Blob([blob], { type: 'application/pdf' });
      const url = URL.createObjectURL(pdf);
      const fname = downloadName('.pdf');
      openModal({
        title: 'پیش‌نمایش PDF' + (state.dataSource ? ' — ' + state.dataSource : '') + ` (${rows} ردیف)`,
        body: h('iframe', { src: url, title: 'پیش‌نمایش PDF' }),
        wide: true,
        onClose: () => URL.revokeObjectURL(url),
        buttons: [
          { label: 'دریافت فایل PDF', onClick: () => { downloadUrl(url, fname); return false; } },
          { label: 'بستن' },
        ],
      });
    } catch (e) {
      toast(e.message, 'error');
    } finally {
      b.disabled = false;
      setBusy(false);
    }
  }

  // ── autosave / draft restore ──

  let autosaveWarned = false;
  const scheduleAutosave = debounce(() => {
    if (!state.tpl) return;
    const ok = lsSet(LS_DRAFT, {
      tpl: state.tpl, name: state.name, suggestedName: state.suggestedName, dirty: state.dirty, savedAt: Date.now(),
    });
    if (!ok && !autosaveWarned) {
      autosaveWarned = true;
      toast('ذخیرهٔ خودکار پیش‌نویس در مرورگر ممکن نشد (فضا پر یا غیرفعال است). از «خروجی JSON» استفاده کنید.', 'error');
    }
  }, 700);

  function offerDraftRestore() {
    const d = lsGet(LS_DRAFT);
    if (!d || !isObj(d.tpl)) return;
    let t;
    try {
      t = normalizeTemplate(d.tpl);
    } catch (e) {
      return;
    }
    if (!d.dirty && !d.name && JSON.stringify(t) === JSON.stringify(normalizeTemplate(newTemplate()))) return;
    const title = d.name || t.name || 'بدون نام';
    const when = d.savedAt ? ' (' + formatTime(d.savedAt) + ')' : '';
    openModal({
      title: 'بازیابی پیش‌نویس',
      body: [
        h('p', { style: 'margin:0' }, `پیش‌نویس «${title}»${when} در این مرورگر پیدا شد${d.dirty ? ' که تغییرات ذخیره‌نشده دارد' : ''}.`),
        h('p', { style: 'margin:0;color:var(--muted)' }, 'آیا بازیابی شود؟'),
      ],
      buttons: [
        {
          label: 'بازیابی',
          primary: true,
          onClick: () => {
            setTemplate(t, typeof d.name === 'string' ? d.name : null, !!d.dirty);
            state.suggestedName = typeof d.suggestedName === 'string' ? d.suggestedName : null;
            updateChrome();
            zoomFit();
          },
        },
        { label: 'شروع با قالب تازه' },
      ],
    });
  }

  // ════════════════════════════════════════════════════════════════════
  // 15. Toolbar, chrome, keyboard, init
  // ════════════════════════════════════════════════════════════════════

  function loadPrefs() {
    const p = lsGet(LS_PREFS);
    if (!isObj(p)) return;
    if (num(p.snap, -1) >= 0) state.snap = num(p.snap, 2);
    if (typeof p.showGrid === 'boolean') state.showGrid = p.showGrid;
    if (typeof p.dataSource === 'string') state.dataSource = p.dataSource;
    if (num(p.previewRows, -1) >= 0) state.previewRows = num(p.previewRows, 50);
    if (p.sideTab === 'tools' || p.sideTab === 'fields') state.sideTab = p.sideTab;
  }
  function savePrefs() {
    lsSet(LS_PREFS, {
      snap: state.snap, showGrid: state.showGrid, dataSource: state.dataSource,
      previewRows: state.previewRows, sideTab: state.sideTab,
    });
  }

  function setSideTab(tab) {
    state.sideTab = tab;
    for (const b of document.querySelectorAll('.tab')) {
      const on = b.dataset.tab === tab;
      b.classList.toggle('active', on);
      b.setAttribute('aria-selected', on ? 'true' : 'false');
    }
    $('toolsPane').hidden = tab !== 'tools';
    $('fieldsPane').hidden = tab !== 'fields';
    savePrefs();
  }

  function renderAll() {
    if (!state.tpl) return;
    renderCanvas();
    renderInspector();
    renderSidePanel();
    refreshDatalists();
    updateChrome();
  }

  let dsKey = null;
  function updateChrome() {
    if (!state.tpl) return;
    const srv = state.server;
    const title = state.name || state.tpl.name || 'بدون نام';
    $('docTitle').textContent = title;
    $('docSubtitle').textContent = state.name
      ? (state.tpl.name && state.tpl.name !== state.name ? state.tpl.name : 'روی سرور')
      : 'هنوز روی سرور ذخیره نشده';
    $('dirtyMark').classList.toggle('on', state.dirty);
    document.title = (state.dirty ? '● ' : '') + title + ' — طراح گزارش PDF';
    $('btnUndo').disabled = !state.undo.length;
    $('btnRedo').disabled = !state.redo.length;
    $('btnDelete').disabled = !state.sel;
    $('btnSave').disabled = srv.readOnly;
    $('btnSaveAs').disabled = srv.readOnly;

    const badge = $('serverBadge');
    badge.className = 'server-badge' + (srv.checked ? (srv.online ? ' online' : ' offline') : '');
    badge.textContent = !srv.checked ? 'در حال اتصال…'
      : srv.online ? (srv.readOnly ? 'متصل — فقط‌خواندنی' : 'متصل به سرور') + (srv.version ? ' · ' + srv.version : '')
        : 'آفلاین';
    badge.title = srv.online ? 'سرور در دسترس است' : 'سرور در دسترس نیست؛ کار با ورود/خروجی JSON ممکن است';

    const sel = $('dsSelect');
    const key = srv.datasources.map((d) => d.name).join('\u0001');
    if (key !== dsKey) {
      dsKey = key;
      const opts = srv.datasources.length ? srv.datasources.map((d) => [d.name, d.name]) : [['', '(بدون منبع داده)']];
      sel.replaceChildren(...opts.map(([v, l]) => h('option', { value: v }, l)));
    }
    if (sel.value !== state.dataSource) sel.value = state.dataSource;
    renderStatusSel();
  }

  function setStatusPos(text) {
    $('stPos').textContent = text;
  }

  function renderStatusPage() {
    const p = state.tpl.page || {};
    const P = pageInfo();
    const pct = Math.round((state.scale / PX_PER_PT) * 100);
    // \u2066…\u2069 = LTR isolate, so "W×H pt" is not reordered inside the RTL status bar.
    $('stPage').textContent = `${p.size || 'A4'}${lower(p.size) === 'custom' ? '' : p.landscape ? ' افقی' : ' عمودی'} · \u2066${fmtPt(P.w)} × ${fmtPt(P.h)} pt\u2069 · بزرگ‌نمایی ${pct}%`;
    $('zoomLabel').textContent = pct + '%';
  }

  function renderStatusSel() {
    const s = state.sel;
    let txt = 'سند';
    if (s && s.kind === 'els') {
      if (s.items.length > 1) txt = `${s.items.length} عنصر`;
      else {
        const x = selectedElements()[0];
        if (x) txt = `${TYPE_LABELS[normType(x.el.type)]} در ${BAND_LABELS[x.band]} — \u2066${fmtPt(x.el.x)}, ${fmtPt(x.el.y)} · ${fmtPt(x.el.width)} × ${fmtPt(x.el.height)} pt\u2069`;
      }
    } else if (s && s.kind === 'band') txt = 'باند ' + BAND_LABELS[s.band];
    else if (s && s.kind === 'table') txt = 'جدول';
    else if (s && s.kind === 'col') txt = 'ستون ' + (s.index + 1);
    $('stSel').textContent = 'انتخاب: ' + txt;
  }

  // ── zoom ──

  function setScale(sc) {
    const scroll = $('canvasScroll');
    const old = state.scale;
    const next = clamp(sc, 0.2 * PX_PER_PT, 5 * PX_PER_PT);
    if (Math.abs(next - old) < 1e-6) {
      renderStatusPage();
      return;
    }
    const cx = scroll.scrollLeft + scroll.clientWidth / 2;
    const cy = scroll.scrollTop + scroll.clientHeight / 2;
    state.scale = next;
    renderCanvas();
    const r = next / old;
    scroll.scrollLeft = cx * r - scroll.clientWidth / 2;
    scroll.scrollTop = cy * r - scroll.clientHeight / 2;
  }

  function zoomStep(dir) {
    const pct = state.scale / PX_PER_PT;
    const next = dir > 0
      ? ZOOM_STEPS.find((z) => z > pct + 0.001)
      : ZOOM_STEPS.slice().reverse().find((z) => z < pct - 0.001);
    if (next) setScale(next * PX_PER_PT);
  }

  function zoomFit() {
    const scroll = $('canvasScroll');
    const P = pageInfo();
    const avail = scroll.clientWidth - 60 - 150 - 20;
    if (avail < 80) return;
    setScale(clamp(avail / P.w, 0.25 * PX_PER_PT, 1.5 * PX_PER_PT));
  }

  // ── wiring ──

  function bindToolbar() {
    for (const b of document.querySelectorAll('[data-icon]')) b.prepend(icon(b.dataset.icon));
    $('btnNew').addEventListener('click', cmdNew);
    $('btnOpen').addEventListener('click', cmdOpen);
    $('btnSave').addEventListener('click', cmdSave);
    $('btnSaveAs').addEventListener('click', cmdSaveAs);
    $('btnImport').addEventListener('click', cmdImport);
    $('btnExport').addEventListener('click', cmdExport);
    $('btnUndo').addEventListener('click', undo);
    $('btnRedo').addEventListener('click', redo);
    $('btnDelete').addEventListener('click', deleteSelection);
    $('btnZoomIn').addEventListener('click', () => zoomStep(1));
    $('btnZoomOut').addEventListener('click', () => zoomStep(-1));
    $('btnZoomFit').addEventListener('click', zoomFit);
    $('zoomLabel').addEventListener('click', () => setScale(PX_PER_PT));
    $('btnPreview').addEventListener('click', cmdPreview);

    const snap = $('snapInput');
    snap.value = String(state.snap);
    snap.addEventListener('change', () => {
      const v = Number(snap.value);
      state.snap = Number.isFinite(v) && v >= 0 ? v : 2;
      snap.value = String(state.snap);
      savePrefs();
      renderCanvas();
    });
    const grid = $('gridToggle');
    grid.checked = state.showGrid;
    grid.addEventListener('change', () => {
      state.showGrid = grid.checked;
      savePrefs();
      renderCanvas();
    });
    const rows = $('rowsInput');
    rows.value = String(state.previewRows);
    rows.addEventListener('change', () => {
      state.previewRows = clamp(parseInt(rows.value, 10) || 0, 0, 100000);
      rows.value = String(state.previewRows);
      savePrefs();
    });
    $('dsSelect').addEventListener('change', () => setDataSource($('dsSelect').value));
    for (const tab of document.querySelectorAll('.tab')) tab.addEventListener('click', () => setSideTab(tab.dataset.tab));
    $('importFile').addEventListener('change', onImportFile);
    $('imageFile').addEventListener('change', onImageFile);
  }

  function bindCanvas() {
    const page = $('page');
    const scroll = $('canvasScroll');
    scroll.addEventListener('pointerdown', (e) => {
      if (e.button !== 0) return;
      if (e.target.closest('.band, .band-tab, .band-placeholder, .el')) return;
      blurActive();
      if (state.sel) select(null);
    });
    page.addEventListener('pointermove', (e) => {
      if (e.buttons) return;
      const pt = clientToPage(e);
      const L = computeLayout();
      const b = bandHit(L, pt.y);
      setStatusPos(b && !b.missing
        ? `${BAND_LABELS[b.key]} — x: ${fmtPt(pt.x - L.P.ml)}  y: ${fmtPt(pt.y - b.top)} pt`
        : `x: ${fmtPt(pt.x)}  y: ${fmtPt(pt.y)} pt`);
    });
    page.addEventListener('pointerleave', () => setStatusPos(''));
    page.addEventListener('dragover', (e) => {
      if (!state.dragPayload) return;
      e.preventDefault();
      e.dataTransfer.dropEffect = 'copy';
      highlightDrop(clientToPage(e));
    });
    page.addEventListener('dragleave', (e) => {
      if (!e.relatedTarget || !page.contains(e.relatedTarget)) clearDropHighlight();
    });
    page.addEventListener('drop', (e) => {
      e.preventDefault();
      let p = state.dragPayload;
      if (!p) {
        try {
          p = JSON.parse(e.dataTransfer.getData(DND_MIME));
        } catch (err) {
          p = null;
        }
      }
      clearDrag();
      if (isObj(p)) handleDrop(p, clientToPage(e));
    });
    scroll.addEventListener('wheel', (e) => {
      if (!(e.ctrlKey || e.metaKey)) return;
      e.preventDefault();
      zoomStep(e.deltaY < 0 ? 1 : -1);
    }, { passive: false });
  }

  function selectAll() {
    const s = state.sel;
    const bands = s && s.kind === 'band' ? [s.band] : ELEMENT_BANDS.filter((k) => state.tpl[k]);
    const items = [];
    for (const k of bands) {
      if (!state.tpl[k]) continue;
      state.tpl[k].elements.forEach((_, i) => items.push({ band: k, index: i }));
    }
    if (items.length) select({ kind: 'els', items });
  }

  function onKeyDown(e) {
    const mod = e.ctrlKey || e.metaKey;
    if (modalStack.length) {
      if (e.key === 'Escape') {
        e.preventDefault();
        modalStack[modalStack.length - 1].close();
      }
      return;
    }
    // e.code (physical key) keeps shortcuts working with a Persian keyboard layout.
    if (mod && e.code === 'KeyS') {
      e.preventDefault();
      if (e.shiftKey) cmdSaveAs();
      else cmdSave();
      return;
    }
    if (mod && e.code === 'KeyO') {
      e.preventDefault();
      cmdOpen();
      return;
    }
    const t = e.target;
    const editing = t && (t.tagName === 'INPUT' || t.tagName === 'TEXTAREA' || t.tagName === 'SELECT' || t.isContentEditable);
    if (editing) {
      if (e.key === 'Escape' && typeof t.blur === 'function') t.blur();
      return;
    }
    if (mod && e.code === 'KeyZ') {
      e.preventDefault();
      if (e.shiftKey) redo();
      else undo();
      return;
    }
    if (mod && e.code === 'KeyY') { e.preventDefault(); redo(); return; }
    if (mod && e.code === 'KeyC') { if (copySelection()) e.preventDefault(); return; }
    if (mod && e.code === 'KeyV') {
      if (state.clipboard) {
        e.preventDefault();
        pasteClipboard(10);
      }
      return;
    }
    if (mod && e.code === 'KeyD') { e.preventDefault(); duplicateSelection(); return; }
    if (mod && e.code === 'KeyA') { e.preventDefault(); selectAll(); return; }
    if (mod && (e.code === 'Equal' || e.code === 'NumpadAdd')) { e.preventDefault(); zoomStep(1); return; }
    if (mod && (e.code === 'Minus' || e.code === 'NumpadSubtract')) { e.preventDefault(); zoomStep(-1); return; }
    if (mod && (e.code === 'Digit0' || e.code === 'Numpad0')) { e.preventDefault(); zoomFit(); return; }
    if (e.key === 'Delete' || e.key === 'Backspace') {
      if (state.sel) {
        e.preventDefault();
        deleteSelection();
      }
      return;
    }
    if (e.key === 'Escape') {
      if (state.sel) select(null);
      return;
    }
    const arrows = { ArrowLeft: [-1, 0], ArrowRight: [1, 0], ArrowUp: [0, -1], ArrowDown: [0, 1] };
    if (arrows[e.key] && !mod) {
      const step = e.shiftKey ? 10 : 1;
      const [dx, dy] = arrows[e.key];
      if (nudge(dx * step, dy * step)) e.preventDefault();
    }
  }

  function init() {
    loadPrefs();
    state.tpl = newTemplate(); // not autosaved until the user changes something (keeps any old draft)
    bindToolbar();
    bindCanvas();
    document.addEventListener('keydown', onKeyDown);
    window.addEventListener('beforeunload', (e) => {
      if (state.dirty) {
        e.preventDefault();
        e.returnValue = '';
      }
    });
    setSideTab(state.sideTab || 'tools');
    renderAll();
    requestAnimationFrame(zoomFit);
    const want = new URLSearchParams(window.location.search).get('template');
    connectServer().then(() => {
      if (want && state.server.online) loadFromServer(want);
    });
    if (!want) offerDraftRestore();
  }

  // Exposed for debugging / automated tests only (read-only view of the current template).
  window.FastPdfDesigner = Object.freeze({
    getTemplate: () => deepClone(state.tpl),
    version: '1.0.0',
  });

  init();
})();
