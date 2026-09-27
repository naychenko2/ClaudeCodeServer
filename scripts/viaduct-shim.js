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
 * Протокол (версия v=1), все сообщения — объекты с полями source и v:
 *   фрейм → хост (source: 'viaduct-shim'):
 *     ready                         — шим встал, жду init (повторяется, пока init не придёт);
 *     started                       — бандл вставлен в документ;
 *     dirty                         — редактор записал модель, сохранение ждёт дебаунса;
 *     save   { value }              — persist-обёртка модели (строка ключа стора) после дебаунса;
 *     prefs  { prefs }              — все прочие ключи localStorage (настройки вида), с дебаунсом;
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
