import { normalizeDomSnapshot } from './normalize.mjs';

// Спайк браузерного канала (ADR-008). Драйвер сценариев — этот service worker:
// снимает срезы через chrome.debugger (CDP), вносит действия (скролл/ресайз) и
// отправляет сырые срезы в native messaging host, который пишет их на диск.
// Хост молчит (кроме pong) — весь протокол инициирует расширение, как в продукте.

const HOST_NAME = 'com.ccs.browser_spike';
const BASE = 'http://127.0.0.1:8801';

let port = null;
let nativeStats = { connectedAt: 0, disconnects: 0, reconnects: 0, sent: 0, sentBytes: 0, pings: 0, pongs: 0, pongMaxMs: 0 };
const pendingPongs = new Map(); // id -> {t0, resolve}

function ensurePort() {
  if (port) return port;
  port = chrome.runtime.connectNative(HOST_NAME);
  nativeStats.connectedAt = Date.now();
  port.onMessage.addListener((msg) => {
    if (msg && msg.type === 'pong') {
      const p = pendingPongs.get(msg.id);
      if (p) { pendingPongs.delete(msg.id); const ms = Date.now() - p.t0; nativeStats.pongMaxMs = Math.max(nativeStats.pongMaxMs, ms); nativeStats.pongs++; p.resolve(ms); }
    }
  });
  port.onDisconnect.addListener((p) => {
    nativeStats.disconnects++;
    const err = p.error ? String(p.error && p.error.message || p.error) : chrome.runtime.lastError ? String(chrome.runtime.lastError.message) : 'unknown';
    port = null;
    // Автопереподключение — как кнопка «переподключить» в продукте, но автоматом.
    try { ensurePort(); nativeStats.reconnects++; natLog({ type: 'event', kind: 'native_reconnect', after: err }); } catch (e) { /* SW умирает — увидим в логе */ }
  });
  return port;
}

function natSend(obj) {
  const p = ensurePort();
  const s = JSON.stringify(obj);
  nativeStats.sent++; nativeStats.sentBytes += s.length;
  p.postMessage(obj);
}

function natLog(obj) { natSend(Object.assign({ t: new Date().toISOString() }, obj)); }

function natPing() {
  return new Promise((resolve) => {
    const id = String(Math.random());
    const t0 = Date.now();
    pendingPongs.set(id, { t0, resolve });
    nativeStats.pings++;
    ensurePort().postMessage({ type: 'ping', id });
    setTimeout(() => { if (pendingPongs.has(id)) { pendingPongs.delete(id); resolve(-1); } }, 5000);
  });
}

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

// ---------- CDP ----------

async function cdp(tabId, method, params) {
  return new Promise((resolve, reject) => {
    chrome.debugger.sendCommand({ tabId }, method, params || {}, (res) => {
      const err = chrome.runtime.lastError;
      if (err) reject(new Error(err.message + ' @ ' + method)); else resolve(res);
    });
  });
}

async function attach(tabId) {
  await new Promise((resolve, reject) => {
    chrome.debugger.attach({ tabId }, '1.3', () => {
      const err = chrome.runtime.lastError;
      // «Already attached» от нашего же расширения — не ошибка для повторных сценариев.
      if (err && /already.*(debug|attach)/i.test(err.message)) resolve();
      else if (err) reject(new Error(err.message)); else resolve();
    });
  });
  await cdp(tabId, 'Accessibility.enable', {});
}

async function detach(tabId) {
  await new Promise((resolve) => chrome.debugger.detach({ tabId }, () => resolve(chrome.runtime.lastError)));
}

// ---------- Источники среза ----------

// DOMSnapshot.captureSnapshot: нормализация из общего модуля (формат Chrome 128+).
async function snapDomSnapshot(tabId) {
  const t0 = performance.now();
  const raw = await cdp(tabId, 'DOMSnapshot.captureSnapshot', { computedStyles: [], includeDOMRects: true, includePaintOrder: false });
  const tCall = performance.now() - t0;
  const t1 = performance.now();
  const { nodes, docs } = normalizeDomSnapshot(raw);
  return { nodes, docs, ms: { call: Math.round(tCall), norm: Math.round(performance.now() - t1) }, rawBytes: JSON.stringify(raw).length };
}

// Accessibility.getFullAXTree: роль + имя по bid (для join с DOMSnapshot).
async function snapAx(tabId) {
  const t0 = performance.now();
  const raw = await cdp(tabId, 'Accessibility.getFullAXTree', {});
  const tCall = performance.now() - t0;
  const t1 = performance.now();
  const byBid = new Map();
  let total = 0, ignored = 0;
  for (const n of raw.nodes) {
    total++;
    if (n.ignored) { ignored++; continue; }
    if (!n.backendDOMNodeId) continue;
    byBid.set(n.backendDOMNodeId, {
      role: n.role && n.role.value ? String(n.role.value) : null,
      name: n.name && n.name.value ? String(n.name.value).slice(0, 120) : '',
    });
  }
  const tNorm = performance.now() - t1;
  return { byBid, ms: { call: Math.round(tCall), norm: Math.round(tNorm) }, rawBytes: JSON.stringify(raw).length, axTotal: total, axIgnored: ignored };
}

// Обычный DOM из content script (ISOLATED world, активная вкладка целиком, все фреймы).
async function snapDomContent(tabId) {
  const t0 = performance.now();
  const res = await chrome.scripting.executeScript({
    target: { tabId, allFrames: true },
    func: () => {
      const INTERACTIVE_TAGS = new Set(['A', 'BUTTON', 'INPUT', 'SELECT', 'TEXTAREA', 'SUMMARY', 'OPTION', 'LABEL']);
      const out = [];
      const pathOf = (el) => {
        const parts = []; let n = el, guard = 0;
        while (n && n.nodeType === 1 && guard++ < 64) {
          let i = 1, s = n;
          while (s.previousElementSibling && s.previousElementSibling.tagName === n.tagName) { s = s.previousElementSibling; i++; }
          parts.unshift(n.tagName.toLowerCase() + ':' + i);
          n = n.parentElement;
          if (n && n.host) { parts.unshift('#shadow'); n = n.host; }
        }
        return parts.join('/');
      };
      const walk = (root) => {
        for (const el of root.querySelectorAll('*')) {
          const r = el.getBoundingClientRect();
          if (r.width === 0 && r.height === 0) continue;
          const tag = el.tagName.toLowerCase();
          const role = el.getAttribute('role') || (INTERACTIVE_TAGS.has(el.tagName) ? tag : (tag === 'table' || tag === 'tr' || tag === 'td' || tag === 'th' ? tag : null));
          const aria = el.getAttribute('aria-label') || el.getAttribute('alt') || el.getAttribute('title') || el.getAttribute('placeholder') || '';
          const ownText = Array.from(el.childNodes).filter((n) => n.nodeType === 3).map((n) => n.textContent.trim()).filter(Boolean).join(' ').slice(0, 80);
          const interactive = INTERACTIVE_TAGS.has(el.tagName) || el.hasAttribute('role') || (el.hasAttribute('tabindex') && el.getAttribute('tabindex') !== '-1') || el.hasAttribute('onclick');
          const id = el.id || null, cls = (el.classList && el.classList.length) ? Array.from(el.classList).slice(0, 4).join(' ') : null;
          out.push({ p: pathOf(el), tag, role, name: (aria || (interactive ? ownText : '')).slice(0, 80), t: ownText || null, x: Math.round(r.x), y: Math.round(r.y), w: Math.round(r.width), h: Math.round(r.height), i: interactive ? 1 : 0, attrs: id || cls ? Object.assign({}, id ? { id } : {}, cls ? { class: cls } : {}) : null });
        }
      };
      if (document.body) walk(document.body);
      return { url: location.href, frame: window.top === window ? 'main' : (location.href || 'iframe'), vw: innerWidth, vh: innerHeight, nodes: out };
    },
  });
  const tCall = performance.now() - t0;
  const frames = (res || []).map((r) => r.result).filter(Boolean);
  const nodes = [];
  for (const f of frames) for (const n of f.nodes) nodes.push(Object.assign({ frame: f.frame }, n));
  return { nodes, frames: frames.map((f) => ({ url: f.url, count: f.nodes.length, vw: f.vw, vh: f.vh })), ms: { call: Math.round(tCall), norm: 0 }, rawBytes: JSON.stringify(nodes).length };
}

// Единая модель: DOMSnapshot (bid/rect/path/attrs) + AX (role/name) по bid.
// Это и есть «срез для модели» из двух CDP-источников; метрики сравнимы с UIA.
async function snapUnified(tabId) {
  const ds = await snapDomSnapshot(tabId);
  const ax = await snapAx(tabId);
  for (const n of ds.nodes) {
    const a = ax.byBid.get(n.bid);
    n.role = a ? a.role : n.tag;
    n.name = a ? a.name : null;
  }
  return { nodes: ds.nodes, docs: ds.docs, ms: ds.ms, axMs: ax.ms, axTotal: ax.axTotal, axIgnored: ax.axIgnored, dsRawBytes: ds.rawBytes, axRawBytes: ax.rawBytes };
}

// ---------- Действия ----------

async function viewportCenter(tabId) {
  const m = await cdp(tabId, 'Page.getLayoutMetrics', {});
  const v = m.cssVisualViewport || m.cssLayoutViewport;
  return { x: Math.round(v.clientWidth / 2), y: Math.round(v.clientHeight / 2) };
}

async function scrollTicks(tabId, ticks) {
  const c = await viewportCenter(tabId);
  for (let i = 0; i < ticks; i++) {
    await cdp(tabId, 'Input.dispatchMouseEvent', { type: 'mouseWheel', x: c.x, y: c.y, deltaX: 0, deltaY: 120 });
    await sleep(60);
  }
}

async function waitLoad(tabId, ms = 10000) {
  await new Promise((resolve) => {
    const to = setTimeout(done, ms);
    function done() { clearTimeout(to); chrome.tabs.onUpdated.removeListener(l); resolve(); }
    function l(id, info) { if (id === tabId && info.status === 'complete') done(); }
    chrome.tabs.onUpdated.addListener(l);
  });
  await sleep(300);
}

// ---------- Сценарии ----------

const FIXTURES = {
  simple: BASE + '/uia/page-simple.html',
  vlist: BASE + '/uia/page-vlist.html',
  windowed: BASE + '/page-vlist-windowed.html',
  iframe: BASE + '/page-iframe.html',
};

// 1/3/4. Три источника на одной странице: время вызова, нормализации, объём, состав.
async function scenarioSources(url, label) {
  const tab = await chrome.tabs.create({ url, active: true });
  await waitLoad(tab.id);
  await attach(tab.id);
  natLog({ type: 'scenario_begin', scenario: 'sources', label, url });
  const reps = 5;
  for (const source of ['ax', 'domsnapshot', 'content']) {
    for (let rep = 0; rep < reps; rep++) {
      let r;
      if (source === 'ax') r = await snapAx(tab.id);
      else if (source === 'domsnapshot') r = await snapDomSnapshot(tab.id);
      else r = await snapDomContent(tab.id);
      const nodes = source === 'ax' ? null : r.nodes;
      natLog({
        type: 'snap', scenario: 'sources', label, source, rep,
        ms: r.ms, rawBytes: r.rawBytes,
        count: source === 'ax' ? r.axTotal : nodes ? nodes.length : 0,
        axIgnored: source === 'ax' ? r.axIgnored : undefined,
        docs: r.docs || r.frames || undefined,
        nodes: rep === 0 ? nodes : undefined,
        axSample: source === 'ax' && rep === 0 ? Array.from(r.byBid.entries()).slice(0, 400).map(([bid, v]) => Object.assign({ bid }, v)) : undefined,
      });
      await sleep(200);
    }
  }
  await detach(tab.id);
  await chrome.tabs.remove(tab.id).catch(() => {});
  natLog({ type: 'scenario_end', scenario: 'sources', label });
}

// Центральный замер: срез A -> действие -> срез B. Классификация офлайн в analyze.mjs.
async function scenarioPairs(url, label, steps) {
  const tab = await chrome.tabs.create({ url, active: true });
  await waitLoad(tab.id);
  await attach(tab.id);
  natLog({ type: 'scenario_begin', scenario: 'pairs', label, url });
  for (const step of steps) {
    const A = await snapUnified(tab.id);
    natLog({ type: 'pair', label, step: step.name, snap: 'A', nodes: A.nodes, ms: A.ms, axMs: A.axMs, axTotal: A.axTotal, axIgnored: A.axIgnored, dsRawBytes: A.dsRawBytes });
    await step.run(tab);
    await sleep(step.settleMs || 800);
    const B = await snapUnified(tab.id);
    natLog({ type: 'pair', label, step: step.name, snap: 'B', nodes: B.nodes, ms: B.ms, axMs: B.axMs, axTotal: B.axTotal, axIgnored: B.axIgnored, dsRawBytes: B.dsRawBytes });
  }
  await detach(tab.id);
  await chrome.tabs.remove(tab.id).catch(() => {});
  natLog({ type: 'scenario_end', scenario: 'pairs', label });
}

// Перерисовка содержимого страницы из content script.
async function rerender(tabId, mode) {
  await chrome.scripting.executeScript({
    target: { tabId },
    func: (m) => {
      const rows = document.querySelectorAll('.row');
      if (m === 'soft') {
        // Тот же DOM: обновляем только текст узлов.
        rows.forEach((r, i) => { r.textContent = r.textContent + ' [upd ' + i + ']'; });
        const h = document.querySelector('header'); if (h) h.textContent = h.textContent + ' (обновлено)';
      } else {
        // Жёсткая перерисовка: контейнер пересоздаётся целиком (как ре-маунт React без сохранения узлов).
        const c = document.getElementById('scroll');
        const html = c.innerHTML;
        c.innerHTML = '';
        void c.offsetHeight;
        c.innerHTML = html;
      }
    },
    args: [mode],
  });
}

// 5. Живучесть service worker + native-канала под нагрузкой и в тишине.
async function scenarioSwlife() {
  natLog({ type: 'scenario_begin', scenario: 'swlife' });
  const tab = await chrome.tabs.create({ url: FIXTURES.simple, active: true });
  await waitLoad(tab.id);
  await attach(tab.id);
  const t0 = Date.now();
  let i = 0;
  // ~2 минуты нагрузки: ping/pong каждые 250 мс, лёгкий срез каждые 10 итераций.
  while (Date.now() - t0 < 120000) {
    const ms = await natPing();
    if (ms < 0) natLog({ type: 'event', kind: 'pong_timeout', iter: i });
    if (i % 10 === 0) {
      const r = await snapDomSnapshot(tab.id);
      natLog({ type: 'snap_lite', iter: i, count: r.nodes.length, ms: r.ms });
    }
    i++;
    await sleep(250);
  }
  await detach(tab.id);
  await chrome.tabs.remove(tab.id).catch(() => {});
  natLog({ type: 'swlife_stats', phase: 'load', iters: i, stats: nativeStats });
  // Тишину держит внешний драйвер (drive.mjs): пауза > 30 с, затем ping через CDP.
  globalThis.__swlifeReady = true;
}

// 6. Cross-origin iframe: что видит каждый источник.
async function scenarioIframe() {
  const tab = await chrome.tabs.create({ url: FIXTURES.iframe, active: true });
  await waitLoad(tab.id);
  await attach(tab.id);
  natLog({ type: 'scenario_begin', scenario: 'iframe', url: FIXTURES.iframe });

  const ds = await snapDomSnapshot(tab.id);
  natLog({ type: 'iframe_probe', source: 'domsnapshot', docs: ds.docs, ms: ds.ms, rawBytes: ds.rawBytes,
    nodes: ds.nodes.filter((n) => n.text && (n.text.includes('ВНУТРЕННИЙ') || n.text.includes('inner'))).slice(0, 20) });

  const ax = await snapAx(tab.id);
  const axArr = Array.from(ax.byBid.entries()).map(([bid, v]) => Object.assign({ bid }, v));
  natLog({ type: 'iframe_probe', source: 'ax', ms: ax.ms, total: ax.axTotal,
    innerNodes: axArr.filter((n) => n.name && (n.name.includes('ВНУТРЕННИЙ') || n.name.includes('inner'))).slice(0, 20) });

  const dom = await snapDomContent(tab.id);
  natLog({ type: 'iframe_probe', source: 'content', frames: dom.frames, ms: dom.ms,
    innerNodes: dom.nodes.filter((n) => (n.t && n.t.includes('ВНУТРЕННИЙ')) || (n.name && n.name.includes('ВНУТРЕННИЙ'))).slice(0, 20) });

  await detach(tab.id);
  await chrome.tabs.remove(tab.id).catch(() => {});
  natLog({ type: 'scenario_end', scenario: 'iframe' });
}

// ---------- Управление ----------

const scenarios = {
  // Замеры источников на фикстурах части 1 + windowed + реальная страница.
  async sources_simple() { return scenarioSources(FIXTURES.simple, 'simple'); },
  async sources_vlist() { return scenarioSources(FIXTURES.vlist, 'vlist'); },
  async sources_windowed() { return scenarioSources(FIXTURES.windowed, 'windowed'); },
  async sources_real(url) { return scenarioSources(url || globalThis.__realUrl, 'real'); },

  // 1. Тихая подмена под скроллом: те же фикстуры, серии по 3 тика (как часть 2 UIA).
  async scroll_vlist() {
    return scenarioPairs(FIXTURES.vlist, 'vlist', [
      { name: 'control' },
      { name: 'scroll3_s1', run: (t) => scrollTicks(t, 3) },
      { name: 'scroll3_s2', run: (t) => scrollTicks(t, 3) },
      { name: 'scroll3_s3', run: (t) => scrollTicks(t, 3) },
    ]);
  },
  async scroll_windowed() {
    return scenarioPairs(FIXTURES.windowed, 'windowed', [
      { name: 'control' },
      { name: 'scroll3_s1', run: (t) => scrollTicks(t, 3) },
      { name: 'scroll3_s2', run: (t) => scrollTicks(t, 3) },
      { name: 'scroll3_s3', run: (t) => scrollTicks(t, 3) },
    ]);
  },

  // 2. Стабильность ключа: смена вкладки, ресайз, перерисовка (soft/hard).
  async stab() {
    const simpleSteps = [
      { name: 'control' },
      { name: 'tabswitch', run: async (t) => { const t2 = await chrome.tabs.create({ url: 'about:blank', active: true }); await sleep(700); await chrome.tabs.update(t, { active: true }); await sleep(500); await chrome.tabs.remove(t2).catch(() => {}); }, settleMs: 500 },
      { name: 'resize', run: async (t) => { const win = await chrome.windows.getLastFocused(); const w = Math.round(win.width * 1.1), h = Math.round(win.height * 1.1); await chrome.windows.update(win.id, { width: w, height: h }); await sleep(400); }, settleMs: 400 },
    ];
    await scenarioPairs(FIXTURES.simple, 'simple', simpleSteps);
    const vlistSteps = [
      { name: 'control' },
      { name: 'rerender_soft', run: (t) => rerender(t, 'soft'), settleMs: 400 },
      { name: 'rerender_hard', run: (t) => rerender(t, 'hard'), settleMs: 400 },
    ];
    await scenarioPairs(FIXTURES.vlist, 'vlist_render', vlistSteps);
  },

  async swlife() { return scenarioSwlife(); },
  async iframe() { return scenarioIframe(); },
};

async function runScenario(name, arg) {
  const fn = scenarios[name];
  if (!fn) return { error: 'no such scenario: ' + name };
  try {
    natLog({ type: 'run', scenario: name });
    await fn(arg);
    natLog({ type: 'run_done', scenario: name, stats: nativeStats });
    return { ok: true, stats: nativeStats };
  } catch (e) {
    natLog({ type: 'run_error', scenario: name, error: String(e && e.stack || e) });
    return { error: String(e && e.message || e) };
  }
}

// Точка входа для драйвера (CDP Runtime.evaluate в service worker) и для popup.
globalThis.__runScenario = (name, arg) => runScenario(name, arg);
globalThis.__natPing = () => natPing();
globalThis.__nativeStats = () => nativeStats;

chrome.runtime.onMessage.addListener((msg, sender, sendResponse) => {
  if (msg && msg.scenario) {
    runScenario(msg.scenario, msg.arg).then(sendResponse);
    return true; // async response
  }
});

// Стартовое подключение к хосту — сразу при пробуждении SW, чтобы порт жил.
ensurePort();
