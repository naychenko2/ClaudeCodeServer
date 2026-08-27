#!/usr/bin/env node
// Пульт драйверного браузера для замера 5. Branded Chrome не подходит: автоматической
// установки unpacked-расширения в нём нет, а --load-extension игнорируется с Chrome 137.
// Поэтому драйвер — Chrome for Testing (движок и MV3 те же): в нём --load-extension жив.
//
//   node drive.mjs launch <путь-к-chrome.exe>   — CfT: профиль, CDP :9335, --no-sandbox (sandbox не стартует из папки репо), расширение
//   node drive.mjs wait-sw [мин]                — ждать service worker расширения
//   node drive.mjs stats                        — __nativeStats из SW
//   node drive.mjs kill                         — закрыть драйверный браузер
//
// Сценарии замеров и тишина — swlife.mjs; срезы — measure.mjs.

import { spawn } from 'node:child_process';
import { mkdirSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

const PORT = 9335;
const PROFILE = join(tmpdir(), 'ccs-cft-profile');
const EXT_PATH = new URL('./extension/', import.meta.url).pathname.replace(/^\/([A-Za-z]:)/, '$1');

const log = (...a) => console.log('[drive]', ...a);
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

async function targets() {
  const res = await fetch(`http://127.0.0.1:${PORT}/json/list`);
  return res.json();
}

async function evalInSw(expression, awaitPromise = true) {
  const t = (await targets()).find((x) => x.type === 'service_worker' && x.url.includes('background.js'));
  if (!t) throw new Error('service worker расширения не найден');
  const ws = new WebSocket(t.webSocketDebuggerUrl);
  await new Promise((res, rej) => { ws.onopen = res; ws.onerror = rej; });
  const r = await new Promise((resolve) => {
    const id = 1;
    ws.addEventListener('message', function h(ev) {
      const m = JSON.parse(ev.data);
      if (m.id === id) { ws.removeEventListener('message', h); resolve(m); }
    });
    ws.send(JSON.stringify({ id, method: 'Runtime.evaluate', params: { expression, awaitPromise, returnByValue: true } }));
  });
  ws.close();
  return r;
}

const cmd = process.argv[2];

if (cmd === 'launch') {
  const exe = process.argv[3];
  if (!exe) { console.error('укажите путь к chrome.exe (Chrome for Testing)'); process.exit(1); }
  mkdirSync(PROFILE, { recursive: true });
  const ch = spawn(exe, [
    `--user-data-dir=${PROFILE}`, `--remote-debugging-port=${PORT}`,
    '--no-first-run', '--no-default-browser-check', '--no-sandbox', '--window-size=1200,800',
    `--load-extension=${EXT_PATH}`,
    'about:blank'], { detached: true, stdio: 'ignore' });
  ch.unref();
  log('браузер запущен, профиль:', PROFILE, 'CDP:', PORT);
} else if (cmd === 'wait-sw') {
  const deadline = Date.now() + Number(process.argv[3] || 1) * 60000;
  while (Date.now() < deadline) {
    const t = await targets().catch(() => []);
    if (t.some((x) => x.type === 'service_worker' && x.url.includes('background.js'))) { log('SW на связи'); process.exit(0); }
    await sleep(500);
  }
  log('SW НЕ найден'); process.exit(1);
} else if (cmd === 'stats') {
  const r = await evalInSw('__nativeStats()', false);
  log(JSON.stringify(r.result && r.result.result ? r.result.result.value : r));
} else if (cmd === 'kill') {
  for (const t of await targets().catch(() => [])) {
    if (t.type === 'page') {
      const ws = new WebSocket(t.webSocketDebuggerUrl);
      await new Promise((r) => { ws.onopen = r; });
      ws.send(JSON.stringify({ id: 1, method: 'Browser.close' }));
      await sleep(500);
      ws.close();
      break;
    }
  }
  log('браузер закрыт');
} else {
  console.log('команды: launch <chrome.exe> | wait-sw [мин] | stats | kill');
}
