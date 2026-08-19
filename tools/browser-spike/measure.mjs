#!/usr/bin/env node
// Замеры спайка прямым CDP (драйверный Chrome, порт 9333, профиль не дефолтный —
// CDP разрешён). Команды те же, что пойдут через chrome.debugger из расширения:
// DOMSnapshot.captureSnapshot, Accessibility.getFullAXTree, Input.dispatchMouseEvent,
// Browser.setWindowBounds, Runtime.evaluate. Результаты — в results/raw/*.jsonl
// в том же формате, что пишет extension/background.js (анализирует analyze.mjs).
//
//   node measure.mjs sources <url> <label>
//   node measure.mjs pairs   <url> <label> <шаг,шаг,...>   шаги: control|scroll3|resize|tabswitch|rerender_soft|rerender_hard
//   node measure.mjs iframe  <url>

import { createWriteStream, mkdirSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { normalizeDomSnapshot } from './normalize.mjs';

const PORT = 9333;
const here = dirname(fileURLToPath(import.meta.url));
const rawDir = join(here, 'results', 'raw');
mkdirSync(rawDir, { recursive: true });
const out = createWriteStream(join(rawDir, `spike-cdp-${new Date().toISOString().replace(/[:.]/g, '-')}.jsonl`), { flags: 'a' });
const log = (o) => out.write(JSON.stringify(o) + '\n');
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

// ---------- CDP ----------

async function newTab(url) {
  const r = await fetch(`http://127.0.0.1:${PORT}/json/new?${encodeURIComponent(url)}`, { method: 'PUT' });
  return r.json();
}

function cdp(wsUrl) {
  const ws = new WebSocket(wsUrl);
  let seq = 0;
  const pending = new Map();
  const listeners = [];
  ws.onmessage = (ev) => {
    const m = JSON.parse(ev.data);
    if (m.id && pending.has(m.id)) { pending.get(m.id)(m); pending.delete(m.id); }
    else for (const l of listeners) l(m);
  };
  const ready = new Promise((res, rej) => { ws.onopen = res; ws.onerror = (e) => rej(new Error('ws')); });
  return {
    async send(method, params) {
      await ready;
      const id = ++seq;
      return new Promise((resolve, reject) => {
        pending.set(id, (m) => (m.error ? reject(new Error(m.error.message + ' @ ' + method)) : resolve(m.result)));
        ws.send(JSON.stringify({ id, method, params: params || {} }));
      });
    },
    onEvent: (l) => listeners.push(l),
    close: () => ws.close(),
  };
}

async function evalIn(c, expression) {
  const r = await c.send('Runtime.evaluate', { expression, returnByValue: true, awaitPromise: true });
  if (r.exceptionDetails) throw new Error('page: ' + (r.exceptionDetails.exception?.description || '').slice(0, 300));
  return r.result?.value;
}

// ---------- Источники среза (те же нормализации, что в background.js) ----------

async function snapDomSnapshot(c) {
  const t0 = performance.now();
  const raw = await c.send('DOMSnapshot.captureSnapshot', { computedStyles: [], includeDOMRects: true, includePaintOrder: false });
  const ms = Math.round(performance.now() - t0);
  const { nodes, docs } = normalizeDomSnapshot(raw);
  return { nodes, docs, ms, rawBytes: JSON.stringify(raw).length };
}

async function snapAx(c) {
  const t0 = performance.now();
  const raw = await c.send('Accessibility.getFullAXTree', {});
  const ms = Math.round(performance.now() - t0);
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
  return { byBid, ms, rawBytes: JSON.stringify(raw).length, axTotal: total, axIgnored: ignored };
}

// «Обычный DOM» из JS страницы (эквивалент content script: тот же обход, MAIN world).
const WALK_DOM = `(() => {
  const IT = new Set(['A','BUTTON','INPUT','SELECT','TEXTAREA','SUMMARY','OPTION','LABEL']);
  const out = [];
  const pathOf = (el) => { const parts=[]; let n=el, g=0;
    while (n && n.nodeType===1 && g++<64) { let i=1,s=n;
      while (s.previousElementSibling && s.previousElementSibling.tagName===n.tagName) { s=s.previousElementSibling; i++; }
      parts.unshift(n.tagName.toLowerCase()+':'+i); n=n.parentElement; }
    return parts.join('/'); };
  for (const el of document.body.querySelectorAll('*')) {
    const r = el.getBoundingClientRect();
    if (r.width===0 && r.height===0) continue;
    const tag = el.tagName.toLowerCase();
    const role = el.getAttribute('role') || (IT.has(el.tagName) ? tag : (['table','tr','td','th'].includes(tag) ? tag : null));
    const aria = el.getAttribute('aria-label') || el.getAttribute('alt') || el.getAttribute('title') || el.getAttribute('placeholder') || '';
    const ownText = Array.from(el.childNodes).filter(n=>n.nodeType===3).map(n=>n.textContent.trim()).filter(Boolean).join(' ').slice(0,80);
    const interactive = IT.has(el.tagName) || el.hasAttribute('role') || (el.hasAttribute('tabindex') && el.getAttribute('tabindex')!=='-1') || el.hasAttribute('onclick');
    out.push({ p: pathOf(el), tag, role, name: (aria || (interactive ? ownText : '')).slice(0,80), t: ownText || null,
      x: Math.round(r.x), y: Math.round(r.y), w: Math.round(r.width), h: Math.round(r.height), i: interactive?1:0,
      attrs: (el.id || el.className) ? Object.assign({}, el.id ? {id: el.id} : {}, el.className ? {class: String(el.className).slice(0,60)} : {}) : null });
  }
  return { url: location.href, vw: innerWidth, vh: innerHeight, nodes: out };
})()`;

async function snapDomContent(c) {
  const t0 = performance.now();
  const f = await evalIn(c, WALK_DOM);
  const ms = Math.round(performance.now() - t0);
  return { nodes: f.nodes, frames: [{ url: f.url, count: f.nodes.length, vw: f.vw, vh: f.vh }], ms, rawBytes: JSON.stringify(f.nodes).length };
}

async function snapUnified(c) {
  const ds = await snapDomSnapshot(c);
  const ax = await snapAx(c);
  for (const n of ds.nodes) {
    const a = ax.byBid.get(n.bid);
    n.role = a ? a.role : n.tag;
    n.name = a ? a.name : null;
  }
  return { nodes: ds.nodes, docs: ds.docs, ms: ds.ms, axMs: ax.ms, axTotal: ax.axTotal, axIgnored: ax.axIgnored, dsRawBytes: ds.rawBytes };
}

// ---------- Сценарии ----------

const [, , cmd, url, label, stepsArg] = process.argv;
const tab = await newTab(url);
// Активируем вкладку: у фоновой вкладки Blink не строит layout, DOMSnapshot пуст.
await fetch(`http://127.0.0.1:${PORT}/json/activate/${tab.id}`, { method: 'PUT' }).catch(() => {});
const c = cdp(tab.webSocketDebuggerUrl);
await c.send('Accessibility.enable', {});
// Синтетический ввод (Input.dispatchMouseEvent) доходит только в выведенное на
// передний план окно — разворачиваем окно и поднимаем вкладку.
try {
  const info = await c.send('Browser.getWindowForTarget', {});
  if (info.bounds.windowState !== 'maximized') await c.send('Browser.setWindowBounds', { windowId: info.windowId, bounds: { windowState: 'maximized' } });
} catch { /* не критично */ }
await c.send('Page.bringToFront').catch(() => {});
// Ждём завершения загрузки (и немного layout-фреймов).
for (let i = 0; i < 40; i++) {
  const st = await evalIn(c, 'document.readyState').catch(() => null);
  if (st === 'complete') break;
  await sleep(250);
}
await sleep(700);

if (cmd === 'sources') {
  log({ t: new Date().toISOString(), type: 'scenario_begin', scenario: 'sources', label, url });
  for (const source of ['ax', 'domsnapshot', 'content']) {
    for (let rep = 0; rep < 5; rep++) {
      let r;
      if (source === 'ax') r = await snapAx(c);
      else if (source === 'domsnapshot') r = await snapDomSnapshot(c);
      else r = await snapDomContent(c);
      const nodes = source === 'ax' ? null : r.nodes;
      log({ t: new Date().toISOString(), type: 'snap', scenario: 'sources', label, source, rep, ms: { call: r.ms, norm: 0 }, rawBytes: r.rawBytes,
        count: source === 'ax' ? r.axTotal : nodes.length, axIgnored: source === 'ax' ? r.axIgnored : undefined,
        docs: r.docs || r.frames || undefined, nodes: rep === 0 ? nodes : undefined });
      await sleep(200);
    }
  }
  log({ t: new Date().toISOString(), type: 'scenario_end', scenario: 'sources', label });
} else if (cmd === 'pairs') {
  log({ t: new Date().toISOString(), type: 'scenario_begin', scenario: 'pairs', label, url });
  const m = await c.send('Page.getLayoutMetrics', {});
  const v = m.cssVisualViewport || m.cssLayoutViewport;
  const cx = Math.round(v.clientWidth / 2), cy = Math.round(v.clientHeight / 2);
  for (const stepName of stepsArg.split(',')) {
    await c.send('Page.bringToFront').catch(() => {});
    const A = await snapUnified(c);
    log({ t: new Date().toISOString(), type: 'pair', label, step: stepName, snap: 'A', nodes: A.nodes, ms: A.ms, axMs: A.axMs, axTotal: A.axTotal, axIgnored: A.axIgnored, dsRawBytes: A.dsRawBytes });
    if (stepName.startsWith('scroll3')) {
      for (let i = 0; i < 3; i++) { await c.send('Input.dispatchMouseEvent', { type: 'mouseWheel', x: cx, y: cy, deltaX: 0, deltaY: 120 }); await sleep(80); }
    } else if (stepName === 'resize') {
      const info = await c.send('Browser.getWindowForTarget', {});
      await c.send('Browser.setWindowBounds', { windowId: info.windowId, bounds: { width: Math.round(info.bounds.width * 1.1), height: Math.round(info.bounds.height * 1.1) } });
    } else if (stepName === 'tabswitch') {
      const t2 = await newTab('about:blank');
      await sleep(500);
      await fetch(`http://127.0.0.1:${PORT}/json/activate/${t2.id}`, { method: 'PUT' }).catch(() => {});
      await sleep(400);
      await fetch(`http://127.0.0.1:${PORT}/json/activate/${tab.id}`, { method: 'PUT' }).catch(() => {});
      await sleep(500);
      await fetch(`http://127.0.0.1:${PORT}/json/close/${t2.id}`, { method: 'PUT' }).catch(() => {});
    } else if (stepName === 'rerender_soft') {
      await evalIn(c, `(() => { document.querySelectorAll('.row').forEach((r,i) => r.textContent = r.textContent + ' [upd ' + i + ']'); const h = document.querySelector('header'); if (h) h.textContent = h.textContent + ' (обновлено)'; return 1; })()`);
    } else if (stepName === 'rerender_hard') {
      await evalIn(c, `(() => { const c2 = document.getElementById('scroll'); const html = c2.innerHTML; c2.innerHTML=''; void c2.offsetHeight; c2.innerHTML = html; return 1; })()`);
    }
    await sleep(900);
    const B = await snapUnified(c);
    log({ t: new Date().toISOString(), type: 'pair', label, step: stepName, snap: 'B', nodes: B.nodes, ms: B.ms, axMs: B.axMs, axTotal: B.axTotal, axIgnored: B.axIgnored, dsRawBytes: B.dsRawBytes });
  }
  log({ t: new Date().toISOString(), type: 'scenario_end', scenario: 'pairs', label });
} else if (cmd === 'iframe') {
  log({ t: new Date().toISOString(), type: 'scenario_begin', scenario: 'iframe', url });
  const ds = await snapDomSnapshot(c);
  log({ t: new Date().toISOString(), type: 'iframe_probe', source: 'domsnapshot', docs: ds.docs, ms: ds.ms, rawBytes: ds.rawBytes,
    nodes: ds.nodes.filter((n) => n.text && (n.text.includes('ВНУТРЕННИЙ') || n.text.includes('INNER'))).slice(0, 20) });
  const ax = await snapAx(c);
  const axArr = Array.from(ax.byBid.entries()).map(([bid, v2]) => Object.assign({ bid }, v2));
  log({ t: new Date().toISOString(), type: 'iframe_probe', source: 'ax', ms: ax.ms, total: ax.axTotal,
    innerNodes: axArr.filter((n) => n.name && (n.name.includes('ВНУТРЕННИЙ') || n.name.toLowerCase().includes('inner'))).slice(0, 20) });
  log({ t: new Date().toISOString(), type: 'scenario_end', scenario: 'iframe' });
}

c.close();
out.end();
console.log('готово:', cmd, label || '');
