// Мини-хелпер: выполнить выражение в указанном CDP-таргете (по подстроке url).
// node cdp-eval.mjs <подстрока-url> <выражение> [без-await]
const [, , match, expr, noAwait] = process.argv;
const list = await (await fetch('http://127.0.0.1:9333/json/list')).json();
const t = list.find((x) => x.url.includes(match));
if (!t) { console.error('таргет не найден: ' + match); process.exit(1); }
const ws = new WebSocket(t.webSocketDebuggerUrl);
await new Promise((r, j) => { ws.onopen = r; ws.onerror = j; });
ws.send(JSON.stringify({ id: 1, method: 'Runtime.evaluate', params: { expression: expr, awaitPromise: !noAwait, returnByValue: true } }));
ws.onmessage = (ev) => {
  const m = JSON.parse(ev.data);
  if (m.id === 1) {
    if (m.result.exceptionDetails) console.error('EXC:', JSON.stringify(m.result.exceptionDetails.exception || m.result.exceptionDetails).slice(0, 800));
    else console.log(JSON.stringify(m.result.result.value, null, 1));
    ws.close(); process.exit(0);
  }
};
