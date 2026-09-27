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
      whenDomReady(startApp);
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
