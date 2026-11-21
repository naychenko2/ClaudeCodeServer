// Мок-движок для QA-7 (детский режим, kids-w4): Anthropic-совместимый /v1/messages на :18111,
// нужен одной успешной веткой сценария «отказ → успешный ход → новый сбой» (К9g-д2, обратная
// сторона инварианта): успешный ход разрывает блок отказа, и сбой следующего хода получает
// общую плашку. Настоящий движок (:18021) в момент приёмки опущен, поднимать прод-движок —
// не решение QA. Управление: POST /mock/off | /mock/on — после /off мок отвечает на
// /v1/messages HTTP 400 (CLI не ретраит 4xx — ход падает сразу, это и есть «любой сбой»).
// Скрипт служебный (для прогона), в продукт не идёт; лежит рядом со спеком.

import http from 'node:http';

const PORT = Number(process.env.QA7_MOCK_PORT || 18111);
let off = false;

function sse(res, model, text) {
  res.writeHead(200, {
    'Content-Type': 'text/event-stream',
    'Cache-Control': 'no-cache',
    'Transfer-Encoding': 'chunked',
  });
  const send = (event, data) => res.write(`event: ${event}\ndata: ${JSON.stringify(data)}\n\n`);
  const id = `mock-${Date.now()}`;
  send('message_start', {
    type: 'message_start',
    message: { id, type: 'message', role: 'assistant', model, usage: { input_tokens: 120, cache_creation_input_tokens: 0, cache_read_input_tokens: 0, output_tokens: 12 } },
  });
  send('content_block_start', { type: 'content_block_start', index: 0, content_block: { type: 'text', text: '' } });
  send('content_block_delta', { type: 'content_block_delta', index: 0, delta: { type: 'text_delta', text } });
  send('content_block_stop', { type: 'content_block_stop', index: 0 });
  send('message_delta', { type: 'message_delta', delta: { stop_reason: 'end_turn', stop_sequence: null }, usage: { output_tokens: 12 } });
  send('message_stop', { type: 'message_stop' });
  res.end();
}

const server = http.createServer((req, res) => {
  const url = new URL(req.url, `http://127.0.0.1:${PORT}`);
  const path = url.pathname;
  console.log(`QA7-MOCK ${req.method} ${req.url}`);

  if (req.method === 'POST' && (path === '/mock/off' || path === '/mock/on')) {
    off = path === '/mock/off';
    res.writeHead(200, { 'Content-Type': 'application/json' });
    res.end(JSON.stringify({ off }));
    return;
  }
  if (req.method === 'GET' && path === '/mock/state') {
    res.writeHead(200, { 'Content-Type': 'application/json' });
    res.end(JSON.stringify({ off }));
    return;
  }

  if (req.method === 'POST' && path === '/v1/messages/count_tokens') {
    res.writeHead(200, { 'Content-Type': 'application/json' });
    res.end(JSON.stringify({ input_tokens: 120 }));
    return;
  }

  if (req.method === 'POST' && path === '/v1/messages') {
    let body = '';
    req.on('data', (c) => { body += c; });
    req.on('end', () => {
      let model = 'kids-cloud-1';
      try { model = JSON.parse(body || '{}').model || model; } catch { /* тело не важно для мока */ }
      if (off) {
        res.writeHead(400, { 'Content-Type': 'application/json' });
        res.end(JSON.stringify({ type: 'error', error: { type: 'invalid_request_error', message: 'mock engine is down (QA-7 switch)' } }));
        return;
      }
      sse(res, model, 'Считаем вместе. 2+2=4.');
    });
    return;
  }

  if (req.method === 'GET' && path === '/v1/models') {
    res.writeHead(200, { 'Content-Type': 'application/json' });
    res.end(JSON.stringify({ data: [{ id: 'kids-cloud-1', display_name: 'Мок QA-7', type: 'model' }] }));
    return;
  }

  res.writeHead(404, { 'Content-Type': 'application/json' });
  res.end(JSON.stringify({ error: 'not found' }));
});

// 0.0.0.0: ход ребёнка живёт в песочнице (cc-sandbox, bridge) — 127.0.0.1 контейнера это сам
// контейнер; достать мока CLI может только по адресу хоста (192.168.7.117 или 172.17.0.1).
server.listen(PORT, '0.0.0.0', () => console.log(`QA7-MOCK up on :${PORT}`));
process.on('SIGTERM', () => { server.close(); process.exit(0); });
