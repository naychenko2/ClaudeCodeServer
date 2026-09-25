// Спайк ADR-016: агент «устройства». Одноразовый код, в продукт не встраивать.
//
// Две роли в одном процессе:
//  1. Ретранслятор stdio (TCP loopback вместо канала устройства ADR-008): по спеке от сервера
//     запускает claude в каталоге «устройства» с ЧИСТЫМ env и гонит stdin/stdout/stderr кадрами.
//     Kill — по TurnId отдельным подключением (как `docker exec … kill` у DockerProcessRunner).
//  2. Сайдкар на 127.0.0.1: CLI ходит сюда за LLM (ANTHROPIC_BASE_URL) и MCP (http). Сайдкар
//     подменяет авторизацию CLI на токен хода и туннелирует в шлюз.
//
// Токен хода живёт только в памяти этого процесса — в env, argv и файлы CLI он не попадает.
import net from 'node:net';
import http from 'node:http';
import fs from 'node:fs';
import path from 'node:path';
import { spawn } from 'node:child_process';
import { FrameReader, frame, CH } from './frames.mjs';

const RELAY_PORT = +(process.env.AGENT_RELAY_PORT ?? 18711);
const SIDECAR_PORT = +(process.env.AGENT_SIDECAR_PORT ?? 18712);
const GATEWAY = process.env.AGENT_GATEWAY ?? 'http://127.0.0.1:18701';
const DEVICE = process.env.AGENT_DEVICE_DIR ?? '/tmp/ccs-spike/device';
const CLAUDE = process.env.AGENT_CLAUDE_BIN ?? '/home/an/.local/bin/claude';
const LOG = process.env.AGENT_LOG ?? '/tmp/ccs-spike/agent.log';

const turns = new Map(); // turnId → { token, child }
const log = o => fs.appendFileSync(LOG, JSON.stringify({ t: new Date().toISOString(), ...o }) + '\n');

// Env процесса CLI собирается С НУЛЯ, ничего не наследуется от агента
function cliEnv(turnId) {
  const home = path.join(DEVICE, 'home');
  return {
    PATH: '/usr/local/bin:/usr/bin:/bin',
    HOME: home,
    CLAUDE_CONFIG_DIR: path.join(home, '.claude'),
    ANTHROPIC_BASE_URL: `http://127.0.0.1:${SIDECAR_PORT}/t/${turnId}/llm`,
    // Заглушка: без какой-то авторизации CLI не пошлёт запрос. Сайдкар её выбрасывает.
    ANTHROPIC_AUTH_TOKEN: 'device-placeholder',
    NO_PROXY: '127.0.0.1,localhost',
    DISABLE_AUTOUPDATER: '1',
    LANG: 'C.UTF-8',
  };
}

function handleSpawn(sock, spec, reader) {
  const { turnId, args, turnToken, mcpConfig, cwd } = spec;
  const runDir = path.join(DEVICE, 'run', turnId);
  fs.mkdirSync(runDir, { recursive: true });
  fs.mkdirSync(path.join(DEVICE, 'home', '.claude'), { recursive: true });
  const finalArgs = [...args];
  if (mcpConfig) {
    // Материализация файла, который сервер передаёт путём (ADR-016 §3): адреса — на сайдкар
    const text = JSON.stringify(mcpConfig).replaceAll('sidecar:', `http://127.0.0.1:${SIDECAR_PORT}/t/${turnId}`);
    const p = path.join(runDir, 'mcp.json');
    fs.writeFileSync(p, text);
    finalArgs.push('--mcp-config', p);
  }
  turns.set(turnId, { token: turnToken });
  const child = spawn(CLAUDE, finalArgs, {
    cwd: cwd ?? path.join(DEVICE, 'project'), env: cliEnv(turnId), detached: true, stdio: ['pipe', 'pipe', 'pipe'],
  });
  turns.get(turnId).child = child;
  log({ ev: 'spawn', turnId, pid: child.pid, args: finalArgs.filter(a => !a.startsWith('{')) });
  sock.write(frame(CH.INFO, JSON.stringify({ pid: child.pid })));
  child.stdout.on('data', d => sock.write(frame(CH.STDOUT, d)));
  child.stderr.on('data', d => sock.write(frame(CH.STDERR, d)));
  child.on('exit', (code, signal) => {
    log({ ev: 'exit', turnId, code, signal });
    turns.delete(turnId);
    sock.end(frame(CH.EXIT, JSON.stringify({ code, signal })));
  });
  reader.on('frame', (ch, data) => {
    if (ch === CH.STDIN) child.stdin.write(data);
    else if (ch === CH.STDIN_EOF) child.stdin.end();
  });
  // Обрыв трубы от сервера — в спайке просто гасим CLI (буферизация до реконнекта — этап 2)
  sock.on('close', () => { if (child.exitCode === null) { log({ ev: 'pipe-closed', turnId }); } });
}

function handleKill(sock, { turnId }) {
  const t = turns.get(turnId);
  let killed = false;
  if (t?.child?.pid) {
    try { process.kill(-t.child.pid, 'SIGKILL'); killed = true; } catch (e) { log({ ev: 'kill-error', err: String(e) }); }
  }
  log({ ev: 'kill', turnId, killed });
  sock.end(frame(CH.INFO, JSON.stringify({ killed })));
}

net.createServer(sock => {
  const reader = new FrameReader();
  let started = false;
  sock.on('data', d => reader.push(d));
  sock.on('error', () => { });
  reader.on('frame', (ch, data) => {
    if (started || ch !== CH.CONTROL) return;
    started = true;
    const spec = JSON.parse(data);
    if (spec.op === 'spawn') handleSpawn(sock, spec, reader);
    else if (spec.op === 'kill') handleKill(sock, spec);
  });
}).listen(RELAY_PORT, '127.0.0.1', () => log({ ev: 'relay-up', port: RELAY_PORT }));

// Сайдкар: /t/{turnId}/{llm|mcp}/… → шлюз /{llm|mcp}/… + X-Turn-Token
http.createServer((req, res) => {
  const m = req.url.match(/^\/t\/([A-Za-z0-9_-]+)(\/(?:llm|mcp)\/.*)$/);
  const turn = m && turns.get(m[1]);
  if (!turn) { res.writeHead(404); return res.end(); }
  const headers = { ...req.headers };
  delete headers.authorization; delete headers['x-api-key']; delete headers.host;
  headers['x-turn-token'] = turn.token;
  log({ ev: 'sidecar', turnId: m[1], method: req.method, path: m[2].split('?')[0], clientAuth: req.headers.authorization ? 'bearer-placeholder' : req.headers['x-api-key'] ? 'x-api-key' : 'none' });
  const up = http.request(GATEWAY + m[2], { method: req.method, headers }, upRes => {
    res.writeHead(upRes.statusCode, upRes.headers);
    upRes.pipe(res);
  });
  up.on('error', e => { log({ ev: 'sidecar-error', err: String(e) }); if (!res.headersSent) res.writeHead(502); res.end(); });
  req.pipe(up);
}).listen(SIDECAR_PORT, '127.0.0.1', () => log({ ev: 'sidecar-up', port: SIDECAR_PORT }));
