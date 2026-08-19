#!/usr/bin/env node
// Native messaging host спайка браузерного канала. Chrome запускает этот файл
// (через host.bat) как дочерний процесс при connectNative из расширения.
// Протокол: stdin/stdout, 32-битная длина LE + UTF-8 JSON. Лимиты Chrome:
// сообщение к хосту — 64 МиБ, от хоста — 1 МБ. Срезы едут к нам (большой
// лимит), от нас только pong — как в продукте.
//
// Хост пишет всё, что шлёт расширение, в results/raw/<timestamp>.jsonl
// и отвечает на ping pong'ом (для замера RTT в сценарии swlife).

const fs = require('fs');
const path = require('path');

const rawDir = path.join(__dirname, '..', 'results', 'raw');
fs.mkdirSync(rawDir, { recursive: true });
const stamp = new Date().toISOString().replace(/[:.]/g, '-');
const outFile = path.join(rawDir, 'spike-' + stamp + '.jsonl');
const out = fs.createWriteStream(outFile, { flags: 'a' });

process.stderr.write('[browser-spike host] лог: ' + outFile + '\n');

let buf = Buffer.alloc(0);
process.stdin.on('data', (d) => {
  buf = Buffer.concat([buf, d]);
  for (;;) {
    if (buf.length < 4) break;
    const len = buf.readUInt32LE(0);
    if (buf.length < 4 + len) break;
    const body = buf.subarray(4, 4 + len);
    buf = buf.subarray(4 + len);
    let msg;
    try { msg = JSON.parse(body.toString('utf8')); } catch (e) { process.stderr.write('[host] битый JSON: ' + e.message + '\n'); continue; }
    handle(msg);
  }
});

function send(obj) {
  const b = Buffer.from(JSON.stringify(obj), 'utf8');
  const h = Buffer.alloc(4);
  h.writeUInt32LE(b.length, 0);
  process.stdout.write(h);
  process.stdout.write(b);
}

function handle(msg) {
  out.write(JSON.stringify(msg) + '\n');
  if (msg.type === 'ping') send({ type: 'pong', id: msg.id });
  if (msg.type === 'run_done' || msg.type === 'run_error') out.flush?.();
}

process.stdin.on('end', () => {
  process.stderr.write('[browser-spike host] stdin закрыт, выходим\n');
  out.end();
});
