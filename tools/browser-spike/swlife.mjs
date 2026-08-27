#!/usr/bin/env node
// Замер 5: живучесть service worker + native-канала под нагрузкой и в тишине.
// CfT (порт 9335): сценарий swlife крутится в SW (~2 мин ping/pong + лёгкие срезы),
// затем драйвер молчит 45 с (> 30-секундного таймаута SW) и проверяет:
// отвечает ли SW, поднялся ли порт заново, сколько было разрывов.

const PORT = 9335;
const EXT_PREFIX = 'chrome-extension://';
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

const list = await (await fetch(`http://127.0.0.1:${PORT}/json/list`)).json();
const sw = list.find((t) => t.type === 'service_worker' && t.url.includes('background.js'));
if (!sw) { console.error('SW не найден'); process.exit(1); }

function cdp(wsUrl) {
  const ws = new WebSocket(wsUrl);
  let seq = 0;
  const pending = new Map();
  ws.onmessage = (ev) => {
    const m = JSON.parse(ev.data);
    if (m.id && pending.has(m.id)) { pending.get(m.id)(m); pending.delete(m.id); }
  };
  const ready = new Promise((res, rej) => { ws.onopen = res; ws.onerror = rej; });
  return {
    async eval(expression, awaitPromise = true) {
      await ready;
      const id = ++seq;
      return new Promise((resolve) => {
        pending.set(id, resolve);
        ws.send(JSON.stringify({ id, method: 'Runtime.evaluate', params: { expression, awaitPromise, returnByValue: true } }));
      });
    },
  };
}

const c = cdp(sw.webSocketDebuggerUrl);
console.log('[1/3] нагрузка: swlife (~2 мин)…');
const t0 = Date.now();
const r = await c.eval('__runScenario("swlife")');
console.log('swlife:', JSON.stringify(r && r.ok ? 'ok' : r), Math.round((Date.now() - t0) / 1000) + ' c');

console.log('[2/3] тишина 45 с (> 30 с таймаута SW)…');
await sleep(45000);

console.log('[3/3] контроль: жив ли SW и канал');
const t1 = Date.now();
const ping = await c.eval('__natPing()').catch((e) => 'SW МЁРТВ: ' + e.message);
console.log('ping после тишины:', typeof ping === 'number' ? ping + ' мс' : ping, '(вызов', Date.now() - t1, 'мс)');
const stats = await c.eval('__nativeStats()', false).catch(() => null);
console.log('native stats:', JSON.stringify(stats && stats.result && stats.result.result ? stats.result.result.value : stats));
