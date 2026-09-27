/*
 * Шим Viaduct для iframe раздела «Архитектура» CCS. Инжектится первым скриптом <head>
 * скриптом scripts/build-viaduct.ps1; module-скрипт бандла сборка дезактивирует
 * (type="text/x-viaduct-app" data-src=...), запускает его этот шим, когда модель
 * приехала от хоста. Код Viaduct не трогаем. План — docs/research/viaduct-embed-plan.md.
 *
 * Iframe открыт с sandbox="allow-scripts allow-downloads" без allow-same-origin: origin
 * фрейма непрозрачный, родные localStorage/sessionStorage бросают SecurityError. Шим
 * подменяет оба in-memory-хранилищами и мостит ключ модели к хосту через postMessage.
 * Никаких токенов и адресов API внутри фрейма нет: хранит модель только хост.
 *
 * Monaco (редакторы документов, sequence, контрактов) Viaduct штатно тянет с CDN, а CSP
 * фрейма пускает только 'self'. Сборка кладёт AMD-Monaco рядом (monaco/vs) и описывает его
 * в <script type="application/json" id="viaduct-monaco">; шим поднимает его до старта
 * бандла — @monaco-editor/loader видит готовый window.monaco и на CDN не идёт. Воркеры
 * стартуют из blob по тексту файла: из непрозрачного origin new Worker(url) бросает
 * SecurityError (скрипт воркера «чужой»), а blob, созданный в самом фрейме, — свой.
 *
 * Экспорт «Export → SVG/PNG»: в Community пункты зовут серверный рендер, которого нет. Шим
 * перехватывает клик и снимает холст на месте (порт обвязки gpb-event-vscode): копия холста
 * со вычисленными стилями в foreignObject + встроенные data:-шрифты; PNG — через canvas.
 * Файл сохраняет хост (сообщение download): скачивание из песочницы без allow-same-origin
 * зависит от активации пользователя, а к концу асинхронного снимка её уже может не быть.
 *
 * Протокол (версия v=1), все сообщения — объекты с полями source и v:
 *   фрейм → хост (source: 'viaduct-shim'):
 *     ready                         — шим встал, жду init (повторяется, пока init не придёт);
 *     started                       — бандл вставлен в документ;
 *     dirty                         — редактор записал модель, сохранение ждёт дебаунса;
 *     save   { value }              — persist-обёртка модели (строка ключа стора) после дебаунса;
 *     prefs  { prefs }              — все прочие ключи localStorage (настройки вида), с дебаунсом;
 *     download { name, data }       — готовая картинка экспорта (data — ArrayBuffer, передаётся
 *                                     transfer'ом); сохраняет файл хост, см. «Экспорт» ниже;
 *   хост → фрейм (source: 'ccs-host'):
 *     init   { model, prefs, theme, focus } — модель (строка или null), настройки, тема, фокус;
 *     focus  { elementId }          — показать элемент (параметр ?focus= редактора Viaduct);
 *     flush                         — немедленно отправить отложенное сохранение.
 */
(function () {
  'use strict';

  var PROTOCOL = 1;
  var MODEL_KEY = 'c4modelizer_flat_store';
  var COLOR_MODE_KEY = 'c4-color-mode';
  var ONBOARDING_KEY = 'c4-onboarding-v1';
  // Экскурсия по интерфейсу Viaduct «Quick tour 1/10»: во встраивании она про чужую
  // оболочку (лендинг, экспорт/импорт файлом) и перекрывает холст — глушим всегда
  var TIPS_TOUR_KEY = 'c4-tips-tour-v1';
  var SAVE_DEBOUNCE_MS = 1200;
  var PREFS_DEBOUNCE_MS = 1500;
  var READY_RETRY_MS = 500;
  var MONACO_TIMEOUT_MS = 15000;
  var EXPORT_TIMEOUT_MS = 30000;

  var parentWin = window.parent;
  if (!parentWin || parentWin === window) return; // открыт не во фрейме — шиму делать нечего

  function post(type, payload) {
    var msg = { source: 'viaduct-shim', v: PROTOCOL, type: type };
    if (payload) for (var k in payload) if (Object.prototype.hasOwnProperty.call(payload, k)) msg[k] = payload[k];
    // Непрозрачный origin не адресуется иначе, чем '*'; в сообщениях только модель и настройки
    parentWin.postMessage(msg, '*');
  }

  var saveTimer = null;
  var prefsTimer = null;
  var pendingModel = null;

  function flushSave() {
    if (saveTimer) { clearTimeout(saveTimer); saveTimer = null; }
    if (pendingModel === null) return;
    var value = pendingModel;
    pendingModel = null;
    post('save', { value: value });
  }

  function schedulePrefs(store) {
    if (prefsTimer) clearTimeout(prefsTimer);
    prefsTimer = setTimeout(function () {
      prefsTimer = null;
      post('prefs', { prefs: store.snapshot(MODEL_KEY) });
    }, PREFS_DEBOUNCE_MS);
  }

  // In-memory Storage с API как у настоящего; onWrite — хук записи ключа
  function MemoryStorage(onWrite) {
    var data = Object.create(null);
    var api = {
      getItem: function (k) { k = String(k); return k in data ? data[k] : null; },
      setItem: function (k, v) { k = String(k); v = String(v); data[k] = v; if (onWrite) onWrite(k, v); },
      removeItem: function (k) { k = String(k); if (k in data) { delete data[k]; if (onWrite) onWrite(k, null); } },
      clear: function () { var keys = Object.keys(data); data = Object.create(null); if (onWrite) keys.forEach(function (k) { onWrite(k, null); }); },
      key: function (i) { var keys = Object.keys(data); return i >= 0 && i < keys.length ? keys[i] : null; },
      // служебное, не часть Storage API: наполнение без хуков и снимок настроек
      load: function (k, v) { data[String(k)] = String(v); },
      snapshot: function (exceptKey) {
        var out = {};
        Object.keys(data).forEach(function (k) { if (k !== exceptKey) out[k] = data[k]; });
        return out;
      },
    };
    Object.defineProperty(api, 'length', { get: function () { return Object.keys(data).length; } });
    return api;
  }

  var local = MemoryStorage(function (key, value) {
    if (key === MODEL_KEY) {
      if (value === null) return; // стор модель не удаляет; стирать файл по такому поводу не будем
      pendingModel = value;
      if (!saveTimer) post('dirty');
      else clearTimeout(saveTimer);
      saveTimer = setTimeout(flushSave, SAVE_DEBOUNCE_MS);
    } else {
      schedulePrefs(local);
    }
  });
  var session = MemoryStorage(null);

  function shim(name, value) {
    try {
      Object.defineProperty(window, name, { value: value, configurable: true, enumerable: true, writable: false });
    } catch (e) {
      console.error('[viaduct-shim] не удалось подменить ' + name, e);
    }
  }
  shim('localStorage', local);
  shim('sessionStorage', session);

  function editorUrl(focus) {
    return '/editor' + (focus ? '?focus=' + encodeURIComponent(focus) : '');
  }

  // Роутер Viaduct — BrowserRouter без basename: до старта бандла ставим ему /editor,
  // иначе чужой путь /modules/viaduct/index.html уведёт его на лендинг/редирект.
  try { history.replaceState(null, '', editorUrl(null)); } catch (e) { /* не критично */ }

  var initialized = false;
  var readyTimer = null;

  function startApp() {
    var holder = document.querySelector('script[type="text/x-viaduct-app"]');
    if (!holder) { console.error('[viaduct-shim] не найден дезактивированный скрипт бандла'); return; }
    var s = document.createElement('script');
    s.type = 'module';
    s.crossOrigin = 'anonymous';
    s.src = holder.getAttribute('data-src');
    holder.parentNode.insertBefore(s, holder.nextSibling);
    post('started');
  }

  function whenDomReady(fn) {
    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', fn, { once: true });
    else fn();
  }

  // Объект хоста для бандла: autoHandles — перевыбор сторон связей после переноса карточки
  // (сборка без этой правки флаг просто не читает). Остальное — диагностика для проверок и
  // DevTools: встал ли локальный Monaco, сколько воркеров поднято
  var diag = window.__viaductHost = { autoHandles: true, monaco: false, workers: 0 };

  // Локальный Monaco. done() зовётся ровно один раз — и при успехе, и при провале: без
  // Monaco бандл всё равно стартует (холст работает, редакторы — нет), лучше, чем пустой фрейм
  function bootMonaco(done) {
    var el = document.getElementById('viaduct-monaco');
    if (!el) { done(); return; } // сборка без Monaco (старый скрипт) — прежнее поведение
    var cfg;
    try { cfg = JSON.parse(el.textContent); } catch (e) { console.error('[viaduct-shim] конфиг Monaco', e); done(); return; }
    var amd = window.require;
    if (typeof amd !== 'function' || typeof amd.config !== 'function') {
      console.error('[viaduct-shim] AMD-загрузчик Monaco не встал');
      done();
      return;
    }
    var workers = cfg.workers || {};
    // Ставится ПОСЛЕ editor.main: AMD-сборка при загрузке перетирает MonacoEnvironment
    // своим getWorker (new Worker(url) — из песочницы падает, и Monaco молча уводит воркеры
    // в главный поток)
    var environment = {
      getWorker: function (_, label) {
        var url = label === 'json' && workers.json ? workers.json : workers.editor;
        return fetch(url)
          .then(function (r) {
            if (!r.ok) throw new Error('воркер Monaco ' + url + ': HTTP ' + r.status);
            return r.text();
          })
          .then(function (text) {
            diag.workers++;
            return new Worker(URL.createObjectURL(new Blob([text], { type: 'text/javascript' })), { name: label });
          });
      },
    };
    amd.config({ paths: { vs: cfg.vs } });
    // У AMD-загрузчика нет таймаута: повисший запрос editor.main держал бы фрейм пустым
    // навсегда. Повторный done() безопасен — его отсекает вызывающий
    setTimeout(function () {
      if (!diag.monaco) { console.error('[viaduct-shim] Monaco не загрузился за ' + MONACO_TIMEOUT_MS + ' мс'); done(); }
    }, MONACO_TIMEOUT_MS);
    amd(['vs/editor/editor.main'], function () {
      window.MonacoEnvironment = environment;
      diag.monaco = !!(window.monaco && window.monaco.editor);
      done();
    }, function (err) {
      console.error('[viaduct-shim] Monaco не загрузился', err);
      done();
    });
  }

  // Бандл стартует, когда есть и модель от хоста (init), и Monaco — в любом порядке
  var monacoSettled = false;
  var appRequested = false;
  var appStarted = false;
  function maybeStartApp() {
    if (appStarted || !monacoSettled || !appRequested) return;
    appStarted = true;
    startApp();
  }
  whenDomReady(function () {
    bootMonaco(function () {
      if (monacoSettled) return;
      monacoSettled = true;
      maybeStartApp();
    });
  });

  function focusElement(id) {
    if (!id) return;
    try {
      // Viaduct читает ?focus= из URL и сам снимает параметр; popstate будит роутер
      history.pushState(null, '', editorUrl(String(id)));
      window.dispatchEvent(new PopStateEvent('popstate', { state: null }));
    } catch (e) { console.error('[viaduct-shim] focus', e); }
  }

  // --- Экспорт диаграммы в SVG/PNG на месте (порт обвязки gpb-event-vscode) ---
  // Картинка SVG живёт без стилей страницы, поэтому стили переносятся в копию холста явно
  // (computed style). Все ~350 свойств на каждый элемент раздули бы SVG до мегабайт, так что
  // свойство пропускается, только если его значение совпадает и с начальным по CSS (all:
  // initial), и с родительским: тогда в копии оно и так унаследуется или встанет начальным.
  // Корень холста везёт всё. Для тегов, у которых браузер держит свои умолчания (заголовки,
  // кнопки, поля, списки), и для точки трансформации (у вьюпорта React Flow она 0 0, а по
  // умолчанию центр) нужные свойства идут всегда.
  var SVG_NS = 'http://www.w3.org/2000/svg';
  var initials = {};
  var probeBox = null;
  function initialStyle(el) {
    var key = el.namespaceURI + ' ' + el.localName;
    if (initials[key]) return initials[key];
    if (!probeBox) {
      probeBox = document.createElement('div');
      probeBox.style.cssText = 'position:absolute;left:-10000px;top:0;width:0;height:0;overflow:hidden';
      probeBox.appendChild(document.createElementNS(SVG_NS, 'svg'));
      document.body.appendChild(probeBox);
    }
    var probe = document.createElementNS(el.namespaceURI, el.localName);
    probe.setAttribute('style', 'all: initial');
    (el.namespaceURI === SVG_NS ? probeBox.firstChild : probeBox).appendChild(probe);
    var cs = getComputedStyle(probe), map = {};
    for (var i = 0; i < cs.length; i++) map[cs[i]] = cs.getPropertyValue(cs[i]);
    probe.remove();
    return initials[key] = map;
  }
  // Ширины рамок — тоже всегда: сброс Chakra ставит всем border-style: solid при ширине 0,
  // а у образца (all: initial, style none) вычисленная ширина тоже 0 — без явного свойства
  // копия получила бы solid с шириной medium, и каждый блок оброс бы рамками. overflow —
  // вложенному <svg> браузер ставит hidden, а линии связей React Flow рисует в svg нулевого
  // размера с overflow: visible: совпадение с начальным стоило бы картинке всех стрелок
  var GEOMETRY = {
    'transform-origin': 1, 'perspective-origin': 1,
    'border-top-width': 1, 'border-right-width': 1, 'border-bottom-width': 1, 'border-left-width': 1,
    'outline-width': 1, 'column-rule-width': 1, 'overflow-x': 1, 'overflow-y': 1,
  };
  var UA_TAGS = /^(h[1-6]|p|ul|ol|li|dl|dd|button|input|textarea|select|option|a|b|strong|em|i|code|pre|kbd|small|label|fieldset|legend|table|th|td|blockquote|hr|img|sub|sup|mark|figure)$/;
  var UA_PROPS = /^(display|margin-|padding-|font-|line-height|color|text-align|vertical-align|list-style-type|text-decoration-line|box-sizing|border-(top|right|bottom|left)-(style|width|color)|background-color|cursor|white-space)/;
  function copyStyles(src, dst, parent) {
    var cs = getComputedStyle(src), base = initialStyle(src), own = {}, text = '';
    var ua = src.namespaceURI !== SVG_NS && UA_TAGS.test(src.localName);
    for (var i = 0; i < cs.length; i++) {
      var p = cs[i], v = cs.getPropertyValue(p);
      own[p] = v;
      if (!parent || v !== base[p] || v !== parent[p] || GEOMETRY[p] || (ua && UA_PROPS.test(p))) text += p + ':' + v + ';';
    }
    dst.setAttribute('style', text);
    for (var j = 0; j < src.children.length; j++) copyStyles(src.children[j], dst.children[j], own);
  }

  function toDataUrl(blob) {
    return new Promise(function (ok, fail) {
      var fr = new FileReader();
      fr.onload = function () { ok(fr.result); };
      fr.onerror = function () { fail(fr.error); };
      fr.readAsDataURL(blob);
    });
  }

  // Правила @font-face страницы. Отличие от gpb: у фрейма непрозрачный origin, и fonts.css
  // (свой адрес сервера) для него чужой — cssRules бросает SecurityError. Такой лист
  // перечитываем текстом (раздача отдаёт ACAO: *) и разбираем конструируемым листом
  function fontFaceRules() {
    return Promise.all(Array.from(document.styleSheets).map(function (sheet) {
      var list = null;
      try { list = sheet.cssRules; } catch (e) { /* чужой лист — ниже */ }
      if (list) return Array.from(list).map(function (rule) { return { rule: rule, base: sheet.href || location.href }; });
      if (!sheet.href) return [];
      return fetch(sheet.href)
        .then(function (r) { if (!r.ok) throw new Error('HTTP ' + r.status); return r.text(); })
        .then(function (text) {
          var parsed = new CSSStyleSheet();
          parsed.replaceSync(text);
          return Array.from(parsed.cssRules).map(function (rule) { return { rule: rule, base: sheet.href }; });
        })
        .catch(function (e) { console.error('[viaduct-shim] лист стилей ' + sheet.href, e); return []; });
    })).then(function (lists) {
      var out = [];
      lists.forEach(function (l) { l.forEach(function (x) { if (x.rule instanceof CSSFontFaceRule) out.push(x); }); });
      return out;
    });
  }

  // Шрифты страницы в картинке SVG недоступны — встраиваем data: только те начертания, что
  // браузер реально загрузил (обычно два-три файла по 20 КБ, а не все подмножества). Кириллица
  // приезжает своим подмножеством unicode-range — если она на холсте, её начертание загружено
  function embedFonts() {
    var loaded = Array.from(document.fonts).filter(function (f) { return f.status === 'loaded'; });
    var norm = function (s) { return String(s || '').replace(/["']/g, '').replace(/\s+/g, '').toUpperCase(); };
    return fontFaceRules().then(function (rules) {
      return Promise.all(rules.filter(function (x) {
        var st = x.rule.style;
        return loaded.some(function (f) {
          return norm(f.family) === norm(st.getPropertyValue('font-family')) &&
            norm(f.style) === norm(st.getPropertyValue('font-style') || 'normal') &&
            norm(f.unicodeRange) === norm(st.getPropertyValue('unicode-range') || 'U+0-10FFFF');
        });
      }).map(function (x) {
        var match = /url\((['"]?)([^'")]+)\1\)/.exec(x.rule.style.getPropertyValue('src'));
        if (!match) return '';
        return fetch(new URL(match[2], x.base).href)
          .then(function (r) { if (!r.ok) throw new Error('HTTP ' + r.status); return r.blob(); })
          .then(toDataUrl)
          .then(function (dataUrl) { return x.rule.cssText.replace(match[0], 'url(' + dataUrl + ')'); })
          .catch(function (e) { console.error('[viaduct-shim] шрифт ' + match[2], e); return ''; });
      }));
    }).then(function (parts) { return parts.join('\n'); });
  }

  function inlineImages(root) {
    return Promise.all(Array.from(root.querySelectorAll('img')).map(function (img) {
      var src = img.getAttribute('src') || '';
      if (!src || src.indexOf('data:') === 0) return null;
      return fetch(img.src)
        .then(function (r) { return r.blob(); })
        .then(toDataUrl)
        .then(function (dataUrl) { img.setAttribute('src', dataUrl); })
        .catch(function () { img.removeAttribute('src'); });
    }));
  }

  function diagramName() {
    try {
      var model = JSON.parse(local.getItem(MODEL_KEY)).state.model;
      return 'architecture-' + String(model.viewLevel || 'system').replace(/[^A-Za-z0-9_-]/g, '');
    } catch (e) { return 'architecture'; }
  }

  // Короткая плашка внутри фрейма: ошибка экспорта не должна теряться в консоли
  var noticeEl = null;
  var noticeTimer = null;
  function notice(text) {
    console.error('[viaduct-shim] ' + text);
    if (!document.body) return;
    if (!noticeEl) {
      noticeEl = document.createElement('div');
      noticeEl.setAttribute('role', 'status');
      noticeEl.style.cssText = 'position:fixed;left:50%;bottom:16px;transform:translateX(-50%);z-index:2147483647;' +
        'max-width:90vw;padding:6px 14px;border-radius:8px;font:13px/1.4 sans-serif;color:#fff;background:rgba(40,40,40,.92);pointer-events:none';
    }
    noticeEl.textContent = text;
    document.body.appendChild(noticeEl);
    if (noticeTimer) clearTimeout(noticeTimer);
    noticeTimer = setTimeout(function () { noticeEl.remove(); }, 6000);
  }

  function sendDownload(name, blob) {
    return blob.arrayBuffer().then(function (buffer) {
      parentWin.postMessage({ source: 'viaduct-shim', v: PROTOCOL, type: 'download', name: name, data: buffer }, '*', [buffer]);
    });
  }

  var exporting = false;
  function exportImage(format) {
    if (exporting) return Promise.resolve();
    var canvasRoot = document.querySelector('.react-flow');
    if (!canvasRoot) {
      notice('Холста диаграммы на экране нет — откройте диаграмму и повторите экспорт.');
      return Promise.resolve();
    }
    exporting = true;
    var rect = canvasRoot.getBoundingClientRect(), width = Math.ceil(rect.width), height = Math.ceil(rect.height);
    var clone = canvasRoot.cloneNode(true);
    copyStyles(canvasRoot, clone, null);
    // Панели поверх холста (масштаб, мини-карта, подпись) в картинку не идут
    Array.from(clone.querySelectorAll('.react-flow__panel, .react-flow__controls, .react-flow__minimap, .react-flow__attribution'))
      .forEach(function (el) { el.remove(); });
    clone.style.margin = '0';
    var background = getComputedStyle(document.body).backgroundColor;
    return Promise.all([inlineImages(clone), embedFonts()]).then(function (done) {
      if (done[1]) {
        var style = document.createElement('style');
        style.textContent = done[1];
        clone.insertBefore(style, clone.firstChild);
      }
      var body = new XMLSerializer().serializeToString(clone);
      var svg = '<svg xmlns="http://www.w3.org/2000/svg" width="' + width + '" height="' + height + '" viewBox="0 0 ' + width + ' ' + height + '">' +
        '<rect width="100%" height="100%" fill="' + background + '"/>' +
        '<foreignObject x="0" y="0" width="' + width + '" height="' + height + '">' + body + '</foreignObject></svg>';
      var name = diagramName();
      if (format === 'svg') return sendDownload(name + '.svg', new Blob([svg], { type: 'image/svg+xml' }));
      return new Promise(function (ok, fail) {
        // Декодер может не позвать ни onload, ни onerror — без таймаута флаг exporting
        // залип бы до перезагрузки фрейма
        var guard = setTimeout(function () { fail(new Error('SVG холста не нарисовался за ' + EXPORT_TIMEOUT_MS + ' мс')); }, EXPORT_TIMEOUT_MS);
        ok = (function (done) { return function (v) { clearTimeout(guard); done(v); }; })(ok);
        fail = (function (done) { return function (e) { clearTimeout(guard); done(e); }; })(fail);
        var img = new Image();
        img.onload = function () {
          var scale = 2, canvas = document.createElement('canvas');
          canvas.width = width * scale; canvas.height = height * scale;
          var ctx = canvas.getContext('2d');
          ctx.scale(scale, scale);
          ctx.drawImage(img, 0, 0);
          canvas.toBlob(function (png) { png ? ok(sendDownload(name + '.png', png)) : fail(new Error('canvas не отдал PNG')); }, 'image/png');
        };
        img.onerror = function () { fail(new Error('SVG холста не нарисовался')); };
        img.src = 'data:image/svg+xml;charset=utf-8,' + encodeURIComponent(svg);
      });
    }).catch(function (e) {
      notice('Экспорт ' + format.toUpperCase() + ' не удался: ' + (e && e.message || e));
    }).then(function () { exporting = false; });
  }
  diag.exportImage = exportImage;

  // Клик по пунктам «Export → SVG/PNG» перехватывается раньше React (фаза захвата на
  // document) и уходит в экспорт на месте; меню закрывается Escape, снимок — после того,
  // как оно исчезнет с холста
  document.addEventListener('click', function (e) {
    var item = e.target && e.target.closest && e.target.closest('[data-testid="toolbar-export-svg"], [data-testid="toolbar-export-png"]');
    if (!item) return;
    e.preventDefault();
    e.stopImmediatePropagation();
    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    var format = item.getAttribute('data-testid') === 'toolbar-export-svg' ? 'svg' : 'png';
    setTimeout(function () { exportImage(format); }, 150);
  }, true);

  window.addEventListener('message', function (e) {
    if (e.source !== parentWin) return; // слушаем только своего хоста
    var msg = e.data;
    if (!msg || msg.source !== 'ccs-host' || msg.v !== PROTOCOL) return;

    if (msg.type === 'init') {
      if (initialized) return;
      initialized = true;
      if (readyTimer) { clearInterval(readyTimer); readyTimer = null; }
      var prefs = msg.prefs && typeof msg.prefs === 'object' ? msg.prefs : {};
      Object.keys(prefs).forEach(function (k) {
        if (k !== MODEL_KEY && typeof prefs[k] === 'string') local.load(k, prefs[k]);
      });
      if (msg.theme === 'light' || msg.theme === 'dark') local.load(COLOR_MODE_KEY, msg.theme);
      local.load(TIPS_TOUR_KEY, '1');
      if (typeof msg.model === 'string') {
        local.load(MODEL_KEY, msg.model);
        // Модель проекта уже есть — приветственный диалог Viaduct не нужен и опасен:
        // «Start with a sample» подменил бы модель проекта демкой и сохранил её в файл
        local.load(ONBOARDING_KEY, '1');
      }
      if (msg.focus) { try { history.replaceState(null, '', editorUrl(String(msg.focus))); } catch (err) { /* без фокуса */ } }
      whenDomReady(function () { appRequested = true; maybeStartApp(); });
    } else if (msg.type === 'focus') {
      focusElement(msg.elementId);
    } else if (msg.type === 'flush') {
      flushSave();
    }
  });

  // Уходим (хост перезагружает или закрывает фрейм) — не теряем отложенную правку
  window.addEventListener('pagehide', flushSave);

  post('ready');
  readyTimer = setInterval(function () {
    if (initialized) { clearInterval(readyTimer); readyTimer = null; return; }
    post('ready');
  }, READY_RETRY_MS);
})();
