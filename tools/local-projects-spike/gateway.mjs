// Спайк ADR-016: прототип серверного LLM-шлюза. Одноразовый код, в продукт не встраивать.
//
// Секреты живут ТОЛЬКО в этом процессе («сервер»): токен подписки, ключ стороннего
// провайдера и JWT бэкенда. Клиент приходит с capability-токеном хода; шлюз по нему сам
// выбирает upstream и подставляет настоящую авторизацию.
//
//   /llm/*          → Anthropic Messages API (подписка) или DeepSeek (Anthropic-совместимый)
//   /mcp/*          → MCP-over-HTTP дев-стенда, с JWT владельца и X-Caller-Session-Id
//   POST /admin/turn → выпуск токена хода (только с 127.0.0.1; в продукте — внутренний вызов)
import http from 'node:http';
import https from 'node:https';
import crypto from 'node:crypto';
import fs from 'node:fs';

const PORT = +(process.env.GW_PORT ?? 18701);
const BACKEND = process.env.GW_BACKEND ?? 'http://127.0.0.1:5000';
const LOG = process.env.GW_LOG ?? '/tmp/ccs-spike/gateway.log';

// Секреты читаем из файлов на старте — ни одного секрета в argv.
const oauthToken = JSON.parse(fs.readFileSync(process.env.GW_OAUTH_FILE, 'utf8')).claudeAiOauth.accessToken;
const providersCfg = JSON.parse(fs.readFileSync(process.env.GW_PROVIDERS_FILE, 'utf8')).LlmProviders;
const deepseekKey = providersCfg.deepseek.ApiKey;
const minimaxKey = providersCfg.minimax.ApiKey;
const backendJwt = fs.readFileSync(process.env.GW_BACKEND_JWT_FILE, 'utf8').trim();

const PROVIDERS = {
  subscription: {
    base: 'https://api.anthropic.com',
    auth: h => { h['authorization'] = `Bearer ${oauthToken}`; addBeta(h, 'oauth-2025-04-20'); },
  },
  deepseek: {
    base: 'https://api.deepseek.com/anthropic',
    auth: h => { h['x-api-key'] = deepseekKey; },
    // Фоновые вызовы CLI (заголовки, сводки) идут на haiku — у DeepSeek такой модели нет
    model: m => (m?.startsWith('deepseek') ? m : 'deepseek-v4-flash'),
  },
  minimax: {
    base: 'https://api.minimax.io/anthropic',
    auth: h => { h['authorization'] = `Bearer ${minimaxKey}`; },
    model: m => (m?.startsWith('MiniMax') ? m : 'MiniMax-M2.7-highspeed'),
  },
};

function addBeta(h, beta) {
  const cur = (h['anthropic-beta'] ?? '').split(',').map(s => s.trim()).filter(Boolean);
  if (!cur.includes(beta)) cur.push(beta);
  h['anthropic-beta'] = cur.join(',');
}

const turns = new Map(); // токен хода → { provider, sessionId, expires }

function log(obj) {
  fs.appendFileSync(LOG, JSON.stringify({ t: new Date().toISOString(), ...obj }) + '\n');
}

function resolveTurn(req) {
  const tok = req.headers['x-turn-token'];
  const turn = tok && turns.get(tok);
  if (!turn || turn.expires < Date.now()) return null;
  return turn;
}

const HOP = new Set(['host', 'connection', 'content-length', 'transfer-encoding', 'keep-alive',
  'authorization', 'x-api-key', 'x-turn-token', 'proxy-authorization']);

function cleanHeaders(src) {
  const h = {};
  for (const [k, v] of Object.entries(src)) if (!HOP.has(k)) h[k] = v;
  return h;
}

async function readBody(req) {
  const chunks = [];
  for await (const c of req) chunks.push(c);
  return Buffer.concat(chunks);
}

function forward(res, targetUrl, method, headers, body, onResponse) {
  const u = new URL(targetUrl);
  const mod = u.protocol === 'https:' ? https : http;
  const up = mod.request(u, { method, headers: { ...headers, 'content-length': body.length } }, upRes => {
    onResponse?.(upRes);
    const out = cleanHeaders(upRes.headers);
    res.writeHead(upRes.statusCode, out);
    // Стрим без буферизации: каждый чанк SSE уходит клиенту сразу
    let chunks = 0, bytes = 0;
    const t0 = Date.now(); let tFirst = null;
    upRes.on('data', c => { chunks++; bytes += c.length; tFirst ??= Date.now() - t0; res.write(c); });
    upRes.on('end', () => {
      res.end();
      log({ ev: 'upstream-done', url: u.pathname, status: upRes.statusCode, chunks, bytes, firstChunkMs: tFirst, totalMs: Date.now() - t0 });
    });
  });
  up.on('error', e => { log({ ev: 'upstream-error', err: String(e) }); if (!res.headersSent) res.writeHead(502); res.end(); });
  up.end(body);
}

http.createServer(async (req, res) => {
  const url = new URL(req.url, 'http://x');
  try {
    if (url.pathname === '/admin/turn' && req.method === 'POST') {
      if (req.socket.remoteAddress !== '127.0.0.1') { res.writeHead(403); return res.end(); }
      const { provider, sessionId, ttlSec = 900 } = JSON.parse(await readBody(req));
      if (!PROVIDERS[provider]) { res.writeHead(400); return res.end('unknown provider'); }
      const token = 'turn_' + crypto.randomBytes(24).toString('base64url');
      turns.set(token, { provider, sessionId, expires: Date.now() + ttlSec * 1000 });
      log({ ev: 'turn-issued', provider, sessionId, ttlSec });
      res.writeHead(200, { 'content-type': 'application/json' });
      return res.end(JSON.stringify({ token }));
    }

    const turn = resolveTurn(req);
    if (!turn) { log({ ev: 'deny', path: url.pathname }); res.writeHead(401); return res.end('bad turn token'); }

    if (url.pathname.startsWith('/llm/')) {
      const p = PROVIDERS[turn.provider];
      let body = await readBody(req);
      const h = cleanHeaders(req.headers);
      h['accept-encoding'] = 'identity';
      let model;
      if (body.length && (req.headers['content-type'] ?? '').includes('json')) {
        const j = JSON.parse(body);
        model = j.model;
        if (p.model && j.model) { j.model = p.model(j.model); body = Buffer.from(JSON.stringify(j)); }
      }
      p.auth(h);
      const target = p.base + url.pathname.slice('/llm'.length) + url.search;
      log({ ev: 'llm-request', provider: turn.provider, path: url.pathname, model, clientAuthStripped: !!(req.headers.authorization || req.headers['x-api-key']) });
      return forward(res, target, req.method, h, body, upRes => {
        const rl = Object.fromEntries(Object.entries(upRes.headers).filter(([k]) => k.includes('ratelimit')));
        log({ ev: 'llm-response', provider: turn.provider, status: upRes.statusCode, contentType: upRes.headers['content-type'], ratelimit: rl });
      });
    }

    if (url.pathname.startsWith('/mcp/')) {
      // Хвост tasks/notes/watch — id сессии: чужой чат токеном хода не открыть
      const [, , server, tail] = url.pathname.split('/');
      if (['tasks', 'notes', 'watch'].includes(server) && tail !== turn.sessionId) {
        log({ ev: 'mcp-deny-tail', server, tail }); res.writeHead(403); return res.end();
      }
      const body = await readBody(req);
      const h = cleanHeaders(req.headers);
      h['authorization'] = `Bearer ${backendJwt}`;
      h['x-caller-session-id'] = turn.sessionId;
      let method;
      try { method = JSON.parse(body).method; } catch { }
      log({ ev: 'mcp-request', path: url.pathname, httpMethod: req.method, rpc: method });
      return forward(res, BACKEND + url.pathname + url.search, req.method, h, body);
    }

    res.writeHead(404); res.end();
  } catch (e) {
    log({ ev: 'error', err: String(e?.stack ?? e) });
    if (!res.headersSent) res.writeHead(500);
    res.end();
  }
}).listen(PORT, '127.0.0.1', () => log({ ev: 'gateway-up', port: PORT }));
