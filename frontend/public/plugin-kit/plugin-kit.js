/* plugin-kit: шим моста v1 для панели плагина (ADR-031 §3).
 *
 * Хост встраивает этот скрипт в srcdoc ПЕРЕД html панели. Панель — недоверенный
 * код: кадр — sandbox без allow-same-origin, поэтому единственный канал к хосту —
 * window.ccsPlugin (call/resize/on) и сообщения хоста (init/result/theme/context).
 * DOM, хранилища и JWT хоста панель не видит; сеть из кадра закрыта CSP
 * (connect-src 'none'), весь выход наружу — только мостом.
 *
 * API:
 *   ccsPlugin.ready()             — шим загрузился (сам шлёт его при загрузке)
 *   ccsPlugin.call(op, args)     — Promise: данные на успех, {code, message} на сбой
 *   ccsPlugin.resize(height)     — желаемая высота панели (px)
 *   ccsPlugin.on(type, cb)       — 'init' | 'theme' | 'context'; возвращает отписку
 *   ccsPlugin.state              — текущее {plugin, slot, context, theme, locale, granted}
 *
 * Шим дополнительно диспатчит DOM-события ccs:init / ccs:theme / ccs:context на
 * window (detail — те же данные): панель, которая не использует on(), может
 * слушать их через addEventListener.
 */
(function () {
  'use strict';
  if (window.ccsPlugin) return; // шим встраивается один раз

  var SOURCE = 'ccs-plugin';
  var HOST_SOURCE = 'ccs-host';
  var VERSION = 1;

  var state = {
    plugin: null,    // {id, version} — из init
    slot: '',        // имя слота
    context: {},     // {projectId, objectId?}
    theme: 'light',  // 'light' | 'dark'
    locale: '',
    granted: []      // копия выданных прав
  };

  var pending = {};  // id вызова -> {resolve, reject}
  var seq = 0;
  var inited = false;
  var subs = { init: [], theme: [], context: [] };

  function send(msg) {
    msg.source = SOURCE;
    msg.v = VERSION;
    try {
      // Адресат — window.top (окно хоста), а не parent: после оболочки (ADR-031 §3.6)
      // parent панели — оболочка без скриптов, и сообщения в неё теряются. window.parent
      // — запасной адресат, если панель смонтирована без оболочки (один уровень вложенности).
      (window.top || window.parent).postMessage(msg, '*');
    } catch (e) { /* хоста больше нет — мост мёртв, панель просто не получит ответа */ }
  }

  function applyTheme() {
    var el = document.documentElement;
    if (state.theme === 'dark') el.setAttribute('data-cc-theme', 'dark');
    else el.removeAttribute('data-cc-theme');
  }

  function snapshot() {
    return {
      plugin: state.plugin,
      slot: state.slot,
      context: state.context,
      theme: state.theme,
      locale: state.locale,
      granted: state.granted.slice()
    };
  }

  function notify(type, detail) {
    subs[type].slice().forEach(function (cb) {
      try { cb(detail); } catch (e) { /* обработчик панели не должен убить шим */ }
    });
    try { window.dispatchEvent(new CustomEvent('ccs:' + type, { detail: detail })); } catch (e) {}
  }

  window.addEventListener('message', function (e) {
    var d = e.data;
    if (!d || typeof d !== 'object') return;
    if (d.source !== HOST_SOURCE || d.v !== VERSION) return;
    // Источник — окно хоста (window.top || window.parent): хост шлёт в окно панели
    // напрямую (frames[0]), сообщения из других окон (включая оболочку) отбрасываем.
    if (e.source !== (window.top || window.parent)) return;
    if (d.type === 'init') {
      state.plugin = d.plugin || null;
      state.slot = typeof d.slot === 'string' ? d.slot : '';
      state.context = d.context || {};
      state.theme = d.theme === 'dark' ? 'dark' : 'light';
      state.locale = typeof d.locale === 'string' ? d.locale : '';
      state.granted = Array.isArray(d.granted) ? d.granted : [];
      applyTheme();
      if (!inited) {
        inited = true;
        notify('init', snapshot());
      }
    } else if (d.type === 'theme') {
      state.theme = d.theme === 'dark' ? 'dark' : 'light';
      applyTheme();
      notify('theme', { theme: state.theme });
    } else if (d.type === 'context') {
      state.context = d.context || {};
      notify('context', { context: state.context });
    } else if (d.type === 'result') {
      var p = pending[d.id];
      if (!p) return;
      delete pending[d.id];
      if (d.ok) p.resolve(d.data);
      else p.reject({ code: d.error && d.error.code, message: d.error && d.error.message });
    }
  });

  window.ccsPlugin = {
    // Шим загрузился — хост отвечает init. Шим шлёт его сам при загрузке.
    ready: function () { send({ type: 'ready' }); },
    // Вызов операции хоста: op — {область}.{глагол} (notes.read, tasks.list, net.fetch),
    // args — объект. Promise резолвится данными или реджит {code, message}
    // (code: denied/not_found/invalid/unavailable/upstream/timeout/limit, ADR-031 §3.4).
    call: function (op, args) {
      var id = 'c' + (++seq);
      return new Promise(function (resolve, reject) {
        pending[id] = { resolve: resolve, reject: reject };
        send({ type: 'call', id: id, op: op, args: args || {} });
      });
    },
    // Желаемая высота панели (px); хост зажимает её в пределы слота.
    resize: function (height) {
      if (typeof height === 'number' && isFinite(height) && height >= 0)
        send({ type: 'resize', height: height });
    },
    // Подписка на сообщения хоста: 'init' | 'theme' | 'context'. Возвращает отписку.
    on: function (type, cb) {
      if (subs[type] && typeof cb === 'function') {
        subs[type].push(cb);
        return function () {
          var i = subs[type].indexOf(cb);
          if (i >= 0) subs[type].splice(i, 1);
        };
      }
      return function () {};
    },
    // Текущее состояние (снимок): читать можно в любой момент, не только из on().
    get state() { return snapshot(); }
  };

  // Авто-высота: хосту не видно DOM фрейма (sandbox), единственный канал — postMessage.
  // ResizeObserver шлёт высоту body при каждом изменении; хост зажимает её в пределы
  // слота. Меряем body (реальная высота контента), а не documentElement: его
  // scrollHeight не бывает меньше высоты iframe, и мелкая панель не смогла бы ужаться.
  // Шим живёт в <head> — observer вешаем, когда body существует (DOMContentLoaded).
  function reportHeight() {
    var h = Math.ceil(document.body ? document.body.scrollHeight : 0);
    if (h > 0) send({ type: 'resize', height: h });
  }
  function setupAutoResize() {
    if (!document.body) return;
    if (typeof ResizeObserver === 'function') {
      new ResizeObserver(reportHeight).observe(document.body);
    }
    reportHeight();
  }
  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', setupAutoResize);
  } else {
    setupAutoResize();
  }
  window.addEventListener('load', reportHeight);

  // Шим загрузился — хост ответит init (тема, права, контекст слота).
  send({ type: 'ready' });
})();
