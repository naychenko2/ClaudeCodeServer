import { test, expect, type APIRequestContext, type Page, type Route } from '@playwright/test';
import { createServer, type IncomingMessage, type Server } from 'node:http';
import ws, { type WebSocket } from 'ws';

// ws — CommonJS-пакет: именованный экспорт из ESM недоступен
const WebSocketServer = ws.Server;

// E2E работы с локальным проектом через агента устройства (ADR-016, задача 4.4).
// Живого устройства в e2e нет, поэтому: серверный проект подменяется в ответе /api/projects
// на локальный (матрица host=device), билет выдаёт перехват маршрута, а на 127.0.0.1:47318
// слушает фейковый агент — настоящий HTTP-сервер с тем же периметром, что у агента:
// Origin только веб-морды, билет в X-Agent-Ticket, ответ на preflight. С задачи 4.4б у него
// есть хаб /hubs/agent (SignalR JSON поверх WebSocket, вход по узкому билету хаба) и отдельный
// порт превью с кукой, как у PreviewGate агента.
//
// Порт фейкового агента — E2E_AGENT_PORT (по умолчанию 47318): на машине разработчика там может
// жить настоящий агент, а порт всё равно едет фронту в билете.

const USER = process.env.E2E_USER || 'admin';
const PASS = process.env.E2E_PASS || '12345';
const RUN = Date.now().toString(36);
const PROJECT_NAME = `E2E агент ${RUN}`;
const AGENT_PORT = Number(process.env.E2E_AGENT_PORT || 47318);
const PREVIEW_PORT = AGENT_PORT + 1;
const TICKET = 'fake-ticket';
const HUB_TICKET = 'fake-hub-ticket';
const PREVIEW_TICKET = 'fake-preview-ticket';
const PREVIEW_COOKIE = 'cc_agent_preview';

async function login(request: APIRequestContext): Promise<string> {
  const r = await request.post('/api/auth/login', { data: { username: USER, password: PASS } });
  expect(r.ok(), 'логин должен пройти').toBeTruthy();
  return (await r.json()).token as string;
}

const auth = (token: string) => ({ Authorization: `Bearer ${token}` });

// ---------- фейковый агент ----------

// Узкий билет потока фейкового агента: свой на каждый путь
const streamTicketFor = (path: string) => `narrow-${Buffer.from(path).toString('hex')}`;
const PNG_1X1 = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==', 'base64');
const STREAM_FILES: Record<string, { mime: string; bytes: Buffer }> = {
  'logo.png': { mime: 'image/png', bytes: PNG_1X1 },
  'clip.mp4': { mime: 'video/mp4', bytes: Buffer.alloc(64) },
};
const README = '# Локальный README\n\n![Логотип](logo.png)\n';

interface SeenRequest { method: string; url: string; ticket?: string; authorization?: string; origin?: string }
interface HubCall { target: string; args: unknown[] }
interface PreviewHit { url: string; cookie?: string }

const SERVICE = {
  id: 'web', name: 'agent-web', source: 'npm', command: 'npm', args: ['run', 'dev'], cwd: null,
  suggestedPort: 5199, autoPort: false, saved: false, status: 'started', runningPort: 5199, error: null,
};
const TERMINAL_OUTPUT = 'hello-from-agent-terminal';
const PREVIEW_LOG = 'log-from-agent-dev-server';

// Хаб агента: SignalR JSON-протокол поверх WebSocket, ровно столько, сколько зовёт фронт
function attachFakeHub(server: Server, origin: string, calls: HubCall[]): void {
  const wss = new WebSocketServer({ noServer: true });
  server.on('upgrade', (req, socket, head) => {
    const url = new URL(req.url ?? '/', 'http://agent');
    // WebSocket заголовок не ставит: узкий билет хаба едет в access_token, основной не годится
    if (url.pathname !== '/hubs/agent' || url.searchParams.get('access_token') !== HUB_TICKET
      || (req.headers.origin && req.headers.origin !== origin)) {
      socket.end('HTTP/1.1 401 Unauthorized\r\n\r\n');
      return;
    }
    wss.handleUpgrade(req, socket, head, ws => serveHub(ws, calls));
  });
}

function serveHub(ws: WebSocket, calls: HubCall[]): void {
  const send = (m: unknown) => ws.send(JSON.stringify(m) + '\x1e');
  const push = (msg: unknown) => send({ type: 1, target: 'message', arguments: [msg] });
  let handshaken = false;
  const terminal = (projectId: string) => ({ id: 'term-1', projectId, name: 'agent-term', status: 'running', shell: 'bash' });
  let projectOfTerminal = '';
  ws.on('message', raw => {
    for (const frame of raw.toString().split('\x1e').filter(Boolean)) {
      const m = JSON.parse(frame) as { type?: number; invocationId?: string; target?: string; arguments?: unknown[] };
      if (!handshaken) { handshaken = true; ws.send('{}\x1e'); continue; }
      if (m.type !== 1 || !m.target) continue;
      const args = m.arguments ?? [];
      calls.push({ target: m.target, args });
      const done = (result?: unknown) => { if (m.invocationId) send({ type: 3, invocationId: m.invocationId, result: result ?? null }); };
      switch (m.target) {
        case 'ListTerminals': done(projectOfTerminal ? [terminal(projectOfTerminal)] : []); break;
        case 'CreateTerminal': projectOfTerminal = args[0] as string; done(terminal(projectOfTerminal)); break;
        case 'ConnectTerminal':
          done(terminal(projectOfTerminal));
          push({ type: 'terminal_output', terminalId: 'term-1', data: TERMINAL_OUTPUT + '\r\n' });
          break;
        case 'JoinPreviewLog': done(PREVIEW_LOG + '\r\n'); break;
        default: done();
      }
    }
  });
}

// Порт превью: билет в адресе → секционированная кука на путь проекта, дальше по куке
function startFakePreview(hits: PreviewHit[]): Promise<Server> {
  const server = createServer((req, res) => {
    const url = new URL(req.url ?? '/', 'http://preview');
    hits.push({ url: url.pathname + url.search, cookie: req.headers.cookie });
    const m = /^\/preview\/([^/]+)\//.exec(url.pathname);
    if (!m) { res.writeHead(404).end(); return; }
    if (url.searchParams.get('previewTicket') === PREVIEW_TICKET) {
      res.setHeader('Set-Cookie', `${PREVIEW_COOKIE}=${PREVIEW_TICKET}; Path=/preview/${m[1]}/; Max-Age=28800; HttpOnly; Secure; SameSite=None; Partitioned`);
      res.writeHead(200, { 'Content-Type': 'text/html; charset=utf-8' })
        .end('<!doctype html><h1>preview-from-agent</h1><img id="dev-asset" src="asset.png" alt="asset">');
      return;
    }
    if (!(req.headers.cookie ?? '').includes(`${PREVIEW_COOKIE}=${PREVIEW_TICKET}`)) {
      res.writeHead(401, { 'Content-Type': 'text/plain' }).end('no preview cookie');
      return;
    }
    if (url.pathname.endsWith('/asset.png')) { res.writeHead(200, { 'Content-Type': 'image/png' }).end(PNG_1X1); return; }
    res.writeHead(200, { 'Content-Type': 'text/html; charset=utf-8' }).end('<!doctype html><h1>preview-by-cookie</h1>');
  });
  return new Promise((resolve, reject) => {
    server.once('error', reject);
    server.listen(PREVIEW_PORT, '127.0.0.1', () => resolve(server));
  });
}

function startFakeAgent(origin: string, seen: SeenRequest[], calls: HubCall[]): Promise<Server> {
  const server = createServer((req: IncomingMessage, res) => {
    const reqOrigin = req.headers.origin;
    if (reqOrigin && reqOrigin !== origin) { res.writeHead(403).end(); return; }
    if (reqOrigin) { res.setHeader('Access-Control-Allow-Origin', reqOrigin); res.setHeader('Vary', 'Origin'); }
    if (req.method === 'OPTIONS') {
      res.writeHead(204, {
        'Access-Control-Allow-Methods': 'GET, POST, PUT, DELETE',
        'Access-Control-Allow-Headers': 'X-Agent-Ticket, Content-Type, Authorization, X-Requested-With, X-SignalR-User-Agent',
        'Access-Control-Allow-Private-Network': 'true',
      }).end();
      return;
    }
    const json = (status: number, body: unknown) => {
      res.writeHead(status, { 'Content-Type': 'application/json' }).end(JSON.stringify(body));
    };
    seen.push({
      method: req.method ?? '', url: req.url ?? '',
      ticket: req.headers['x-agent-ticket'] as string | undefined,
      authorization: req.headers.authorization,
      origin: reqOrigin,
    });
    const url = new URL(req.url ?? '/', 'http://agent');
    // negotiate хаба: узкий билет в Authorization, основной сюда не годится
    if (url.pathname === '/hubs/agent/negotiate') {
      if (req.headers.authorization !== `Bearer ${HUB_TICKET}`) { json(401, { error: 'Билет хаба недействителен или истёк' }); return; }
      json(200, { negotiateVersion: 1, connectionId: 'c1', connectionToken: 'ct1',
        availableTransports: [{ transport: 'WebSockets', transferFormats: ['Text', 'Binary'] }] });
      return;
    }
    const path = url.pathname.replace(/^\/api\/projects\/[^/]+\//, '');
    const projectId = /^\/api\/projects\/([^/]+)\//.exec(url.pathname)?.[1] ?? '';
    // Поток — как у агента: без заголовка, только по узкому билету на тот же путь; основной
    // билет в URL отвергается
    if (req.method === 'GET' && path === 'files/stream' && !req.headers['x-agent-ticket']) {
      const file = url.searchParams.get('path') ?? '';
      if (url.searchParams.get('streamTicket') !== streamTicketFor(file)) { json(401, { error: 'Билет к агенту недействителен или истёк' }); return; }
      const body = STREAM_FILES[file];
      if (!body) { json(404, { error: 'Не найдено' }); return; }
      res.writeHead(200, { 'Content-Type': body.mime, 'Content-Length': body.bytes.length }).end(body.bytes);
      return;
    }
    if (req.headers['x-agent-ticket'] !== TICKET) { json(401, { error: 'Билет к агенту недействителен или истёк' }); return; }
    const now = new Date().toISOString();
    if (req.method === 'POST' && path === 'agent/stream-ticket') {
      const chunks: Buffer[] = [];
      req.on('data', c => chunks.push(c as Buffer));
      req.on('end', () => {
        const file = (JSON.parse(Buffer.concat(chunks).toString() || '{}') as { path?: string }).path ?? '';
        if (file.startsWith('..')) { json(403, { error: 'Путь вне проекта' }); return; }
        json(200, { streamTicket: streamTicketFor(file), expiresAt: new Date(Date.now() + 60_000).toISOString() });
      });
      return;
    }
    if (req.method === 'POST' && path === 'agent/hub-ticket') {
      json(200, { hubTicket: HUB_TICKET, expiresAt: new Date(Date.now() + 60_000).toISOString() });
      return;
    }
    if (req.method === 'POST' && path === 'agent/preview-ticket') {
      json(200, { previewTicket: PREVIEW_TICKET, expiresAt: new Date(Date.now() + 8 * 3600_000).toISOString(),
        url: `http://127.0.0.1:${PREVIEW_PORT}/preview/${projectId}/?previewTicket=${PREVIEW_TICKET}` });
      return;
    }
    if (req.method === 'GET' && path === 'services') { json(200, { services: [SERVICE], activeServiceId: null }); return; }
    if (req.method === 'GET' && path === 'preview/status') { json(200, { running: [], activeServiceId: null }); return; }
    if (req.method === 'POST' && path === 'preview/active') { json(200, { activeServiceId: SERVICE.id }); return; }
    if (req.method === 'GET' && path === 'skills') {
      json(200, { skills: [], projectSkills: [{ name: 'agent-skill', description: 'навык с машины проекта', filePath: '.claude/skills/agent-skill/SKILL.md' }], agents: [] });
      return;
    }
    if (req.method === 'POST' && path === 'agent/attachments') {
      req.resume();
      req.on('end', () => json(200, { path: '.cc-attachments/g1/note.txt' }));
      return;
    }
    if (req.method === 'GET' && (path === 'files' || path === 'files/tree')) {
      json(200, [
        { name: 'agent-readme.md', path: 'agent-readme.md', isDirectory: false, size: 12, modified: now, isModified: false },
        { name: 'clip.mp4', path: 'clip.mp4', isDirectory: false, size: STREAM_FILES['clip.mp4'].bytes.length, modified: now, isModified: false },
      ]);
      return;
    }
    if (req.method === 'GET' && path === 'files/content') {
      const file = url.searchParams.get('path');
      if (file === 'agent-readme.md') { json(200, { content: README, isBinary: false, isImage: false }); return; }
      if (file === 'clip.mp4') { json(200, { content: null, isBinary: true, isImage: false, isVideo: true, mimeType: 'video/mp4', fileSize: STREAM_FILES['clip.mp4'].bytes.length }); return; }
    }
    if (req.method === 'GET' && path === 'git/status') {
      json(200, {
        isRepo: true, branch: 'main', upstream: null, ahead: 0, behind: 0, detached: false, isWorktree: false,
        staged: [], unstaged: [{ path: 'agent-changed.ts', status: 'M', added: 1, deleted: 0 }], untracked: [],
      });
      return;
    }
    json(404, { error: 'Не найдено' });
  });
  attachFakeHub(server, origin, calls);
  return new Promise((resolve, reject) => {
    server.once('error', reject);
    server.listen(AGENT_PORT, '127.0.0.1', () => resolve(server));
  });
}

// ---------- подмена проекта и билета ----------

const escapeRe = (v: string) => v.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');

type TicketMode = { kind: 'ok'; port?: number } | { kind: 'refused'; status: number; error: string };

async function asLocalProject(page: Page, serverOrigin: string, projectId: string, ticket: TicketMode) {
  await page.route('**/api/projects', async (route: Route) => {
    if (route.request().method() !== 'GET') return route.fallback();
    const response = await route.fetch();
    const list = (await response.json()) as Array<Record<string, unknown>>;
    const patched = list.map(p => p.id !== projectId ? p : {
      ...p,
      deviceId: 'fake-device',
      device: { id: 'fake-device', name: 'Фейковый ПК', online: true, platform: 'linux', agentVersion: '0.0.0', harnessReady: true, harnessProblem: null },
      capabilities: {
        host: 'device', deviceId: 'fake-device',
        files: { host: 'device', available: true, reason: null, features: ['files', 'diff', 'git', 'fileWatcher', 'terminal', 'devServers', 'skills', 'attachments'] },
        platform: { host: 'server', available: true, reason: null, features: ['chat', 'history', 'tasks', 'memory', 'personas', 'notes', 'costs', 'tts'] },
        serverContent: { host: 'off', available: false, reason: 'Нужен контент проекта на сервере — у локального проекта недоступно', features: ['knowledge', 'codeGraph', 'dossiers', 'docs', 'mapHygiene'] },
        exec: { available: true, reason: null },
      },
    });
    await route.fulfill({ response, json: patched });
  });
  await page.route(`**/api/projects/${projectId}/device-agent/ticket`, (route: Route) =>
    ticket.kind === 'ok'
      ? route.fulfill({ json: {
          ticket: TICKET, deviceId: 'fake-device', expiresAt: new Date(Date.now() + 5 * 60_000).toISOString(),
          port: ticket.port ?? AGENT_PORT, header: 'X-Agent-Ticket',
        } })
      : route.fulfill({ status: ticket.status, json: { error: ticket.error } }));
  // Серверные рабочие подсистемы локального проекта трогаться не должны: любой такой
  // запрос — провал маршрутизации «сервер или агент»
  await page.route(new RegExp(`^${escapeRe(serverOrigin)}/api/projects/${projectId}/(files|git|services|preview|launch-config|skills|agents)(/|\\?|$)`), (route: Route) =>
    route.fulfill({ status: 599, json: { error: 'запрос рабочей подсистемы локального проекта ушёл на сервер' } }));
  await page.route(new RegExp(`^${escapeRe(serverOrigin)}/api/chats/[^/]+/files/upload`), (route: Route) =>
    route.fulfill({ status: 599, json: { error: 'вложение локального проекта ушло на сервер' } }));
}

async function openTab(page: Page, projectId: string, token: string, tab: 'Файлы' | 'Изменения' | 'Терминал' | 'Сервисы' | 'Навыки') {
  await page.context().addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
  await page.goto(`/#/project/${projectId}`);
  // К имени кнопки рельсы дописывается расшифровка счётчика («Изменения: …»), как только
  // статус git от агента пришёл
  const target = page.getByRole('button', { name: new RegExp(`^${tab}(:|$)`) }).first();
  if (tab !== 'Терминал' && tab !== 'Сервисы' && tab !== 'Навыки') { await target.click(); return; }
  // Терминал, сервисы и навыки по умолчанию убраны в ящик рельсы «Ещё». Рельса дорисовывается
  // по мере загрузки проекта: ждём её, а ящик, открытый слишком рано, открываем заново
  await expect(page.getByRole('button', { name: /^Файлы(:|$)/ }).first()).toBeVisible();
  await expect(async () => {
    // «Ещё» есть и у шапки открытого чата — ящик рельсы идёт в разметке последним
    if (!(await target.isVisible())) await page.getByRole('button', { name: /^Ещё/ }).last().click();
    await target.click({ timeout: 2_000 });
  }).toPass({ timeout: 30_000 });
}

test.describe('локальный проект через агента устройства (ADR-016, 4.4)', () => {
  let token: string;
  let projectId: string;
  let origin: string;
  let agent: Server | null = null;
  let preview: Server | null = null;
  const seen: SeenRequest[] = [];
  const hubCalls: HubCall[] = [];
  const previewHits: PreviewHit[] = [];

  test.beforeAll(async ({ playwright, baseURL }) => {
    origin = new URL(baseURL!).origin;
    const request = await playwright.request.newContext({ baseURL });
    token = await login(request);
    const r = await request.post('/api/projects', {
      headers: auth(token),
      data: { name: PROJECT_NAME, rootPath: `/tmp/e2e-agent-${RUN}`, createDirectory: true },
    });
    expect(r.ok(), `проект создаётся: ${r.status()} ${await r.text()}`).toBeTruthy();
    projectId = (await r.json()).id as string;
    await request.dispose();
    agent = await startFakeAgent(origin, seen, hubCalls);
    preview = await startFakePreview(previewHits);
  });

  test.afterAll(async ({ playwright, baseURL }) => {
    agent?.closeAllConnections();
    await new Promise<void>(resolve => (agent ? agent.close(() => resolve()) : resolve()));
    await new Promise<void>(resolve => (preview ? preview.close(() => resolve()) : resolve()));
    const request = await playwright.request.newContext({ baseURL });
    // Токен из beforeAll: вход под rate-limit, лишний логин в хвосте прогона его выжигает
    if (projectId) await request.delete(`/api/projects/${projectId}`, { headers: auth(token) });
    await request.dispose();
  });

  test.beforeEach(() => { seen.length = 0; hubCalls.length = 0; previewHits.length = 0; });
  // Перехват /api/projects может быть в полёте к концу теста — не роняем на нём прогон
  test.afterEach(async ({ page }) => { await page.unrouteAll({ behavior: 'ignoreErrors' }); });

  test('дерево файлов приходит от агента с билетом, без JWT сервера', async ({ page }) => {
    await asLocalProject(page, origin, projectId, { kind: 'ok' });
    await openTab(page, projectId, token, 'Файлы');

    await expect(page.getByText('agent-readme.md')).toBeVisible();
    const filesCalls = seen.filter(s => s.url.startsWith(`/api/projects/${projectId}/files`));
    expect(filesCalls.length).toBeGreaterThan(0);
    for (const s of filesCalls) {
      expect(s.ticket).toBe(TICKET);
      expect(s.authorization).toBeUndefined();
      expect(s.origin).toBe(origin);
    }
  });

  test('панель изменений берёт git-статус у агента', async ({ page }) => {
    await asLocalProject(page, origin, projectId, { kind: 'ok' });
    await openTab(page, projectId, token, 'Изменения');

    await expect(page.getByText('agent-changed.ts')).toBeVisible();
    expect(seen.some(s => s.url.startsWith(`/api/projects/${projectId}/git/status`))).toBe(true);
    // Публикации у агента нет — кнопка скрыта, а не падает при нажатии
    await expect(page.getByRole('button', { name: 'Опубликовать' })).toHaveCount(0);
  });

  test('картинка markdown и видео идут потоком по узкому билету, основной в URL не попадает', async ({ page }) => {
    await asLocalProject(page, origin, projectId, { kind: 'ok' });
    await openTab(page, projectId, token, 'Файлы');

    await page.getByText('agent-readme.md').click();
    const img = page.getByRole('img', { name: 'Логотип' });
    await expect(img).toHaveAttribute('src', new RegExp(`^http://127\\.0\\.0\\.1:${AGENT_PORT}/api/projects/${projectId}/files/stream\\?`));
    await expect.poll(() => img.evaluate(el => (el as HTMLImageElement).naturalWidth)).toBe(1);

    await page.getByText('clip.mp4').click();
    await expect(page.locator('video source')).toHaveAttribute('src', /streamTicket=narrow-/);
    await expect.poll(() => seen.some(s => s.url.includes('files/stream?path=clip.mp4'))).toBe(true);

    const streams = seen.filter(s => s.url.includes('/files/stream'));
    expect(streams.map(s => new URL(s.url, 'http://agent').searchParams.get('path')).sort()).toEqual(expect.arrayContaining(['clip.mp4', 'logo.png']));
    for (const s of streams) {
      expect(s.url).not.toContain(TICKET);
      expect(s.ticket).toBeUndefined();
      expect(s.authorization).toBeUndefined();
    }
    // Узкие билеты выданы агентом по основному билету в заголовке
    const issued = seen.filter(s => s.method === 'POST' && s.url.endsWith('/agent/stream-ticket'));
    expect(issued.length).toBeGreaterThanOrEqual(2);
    for (const s of issued) expect(s.ticket).toBe(TICKET);
  });

  test('агент не найден — понятное состояние и повтор', async ({ page }) => {
    // Билет указывает на порт, где никто не слушает: так выглядит машина без агента
    await asLocalProject(page, origin, projectId, { kind: 'ok', port: AGENT_PORT + 2 });
    await openTab(page, projectId, token, 'Файлы');

    const state = page.locator('[data-device-agent-state="unreachable"]');
    await expect(state).toBeVisible();
    await expect(state.getByText('Агент устройства не найден')).toBeVisible();
    await expect(state.getByRole('button', { name: 'Проверить снова' })).toBeVisible();
  });

  test('сервер не выдал билет — причина сервера вместо пустой панели', async ({ page }) => {
    await asLocalProject(page, origin, projectId, { kind: 'refused', status: 409, error: 'Устройство офлайн' });
    await openTab(page, projectId, token, 'Файлы');

    const state = page.locator('[data-device-agent-state="refused"]');
    await expect(state).toBeVisible();
    await expect(state.getByText('Устройство офлайн')).toBeVisible();
    expect(seen).toHaveLength(0);
  });

  // ---------- 4.4б: терминал, сервисы и превью, навыки, вложения ----------

  test('терминал идёт в хаб агента по узкому билету хаба, основной билет в URL не попадает', async ({ page }) => {
    await asLocalProject(page, origin, projectId, { kind: 'ok' });
    await openTab(page, projectId, token, 'Терминал');

    await page.getByRole('button', { name: 'Новый терминал' }).first().click();
    await expect(page.locator('.xterm-rows')).toContainText(TERMINAL_OUTPUT);
    expect(hubCalls.map(c => c.target)).toEqual(expect.arrayContaining(['ListTerminals', 'CreateTerminal', 'ConnectTerminal']));
    expect(hubCalls.find(c => c.target === 'CreateTerminal')?.args[0]).toBe(projectId);

    await page.locator('.xterm-helper-textarea').first().pressSequentially('ls');
    await expect.poll(() => hubCalls.filter(c => c.target === 'TerminalInput').map(c => c.args[1]).join('')).toContain('ls');

    // Билет хаба выдал агент по основному в заголовке; негоциация — с узким, без JWT сервера
    const issued = seen.filter(s => s.method === 'POST' && s.url.endsWith('/agent/hub-ticket'));
    expect(issued.length).toBeGreaterThan(0);
    for (const s of issued) expect(s.ticket).toBe(TICKET);
    const negotiate = seen.filter(s => s.url.startsWith('/hubs/agent/negotiate'));
    expect(negotiate.length).toBeGreaterThan(0);
    for (const s of negotiate) expect(s.authorization).toBe(`Bearer ${HUB_TICKET}`);
    expect(seen.some(s => s.url.includes(TICKET) && !s.url.includes(HUB_TICKET))).toBe(false);
  });

  test('сервисы — у агента; iframe превью грузится с порта агента, кука Secure; Partitioned принимается', async ({ page }) => {
    await asLocalProject(page, origin, projectId, { kind: 'ok' });
    await openTab(page, projectId, token, 'Сервисы');

    await page.getByText(SERVICE.name).first().click();
    const iframe = page.locator(`iframe[src^="http://127.0.0.1:${PREVIEW_PORT}/preview/${projectId}/"]`);
    await expect(iframe).toHaveAttribute('src', new RegExp(`previewTicket=${PREVIEW_TICKET}`));
    const frame = page.frameLocator(`iframe[src^="http://127.0.0.1:${PREVIEW_PORT}/"]`);
    await expect(frame.getByText('preview-from-agent')).toBeVisible();

    // Подресурс дев-сайта пришёл уже по куке — значит, браузер её принял
    await expect.poll(() => previewHits.find(h => h.url.endsWith('/asset.png'))?.cookie ?? '').toContain(`${PREVIEW_COOKIE}=${PREVIEW_TICKET}`);
    await expect.poll(() => frame.locator('#dev-asset').evaluate(el => (el as HTMLImageElement).naturalWidth)).toBe(1);
    // Переход внутри iframe без билета в адресе — тоже по куке
    await frame.locator('body').evaluate((_, pid) => { location.href = `/preview/${pid}/second`; }, projectId);
    await expect(frame.getByText('preview-by-cookie')).toBeVisible();

    expect(seen.some(s => s.url === `/api/projects/${projectId}/services`)).toBe(true);
    expect(seen.some(s => s.method === 'POST' && s.url.endsWith('/agent/preview-ticket'))).toBe(true);
    // Основной билет на порт превью не уходит никогда
    for (const h of previewHits) expect(h.url).not.toContain(`=${TICKET}`);

    // Лог дев-сервера — из хаба агента
    await page.getByRole('radio', { name: 'Логи' }).or(page.getByRole('button', { name: 'Логи' })).first().click();
    await expect(page.locator('.xterm-rows').last()).toContainText(PREVIEW_LOG);
    expect(hubCalls.some(c => c.target === 'JoinPreviewLog' && c.args[0] === projectId && c.args[1] === SERVICE.id)).toBe(true);
  });

  test('навыки проекта читаются у агента', async ({ page }) => {
    await asLocalProject(page, origin, projectId, { kind: 'ok' });
    await openTab(page, projectId, token, 'Навыки');

    await expect(page.getByText('agent-skill').first()).toBeVisible();
    expect(seen.some(s => s.url === `/api/projects/${projectId}/skills` && s.ticket === TICKET)).toBe(true);
  });

  test('вложение чата ложится на машину проекта через агента', async ({ page, playwright, baseURL }) => {
    const request = await playwright.request.newContext({ baseURL });
    const r = await request.post(`/api/projects/${projectId}/sessions`, { headers: auth(token), data: { mode: 'acceptEdits' } });
    expect(r.ok(), `чат создаётся: ${r.status()}`).toBeTruthy();
    const chatId = (await r.json()).id as string;
    await request.dispose();

    await asLocalProject(page, origin, projectId, { kind: 'ok' });
    await page.context().addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
    await page.goto(`/#/project/${projectId}/chat/${chatId}`);
    await page.locator('input[type="file"][multiple]').first()
      .setInputFiles({ name: 'note.txt', mimeType: 'text/plain', buffer: Buffer.from('заметка') });

    await expect.poll(() => seen.filter(s => s.method === 'POST' && s.url === `/api/projects/${projectId}/agent/attachments`).length).toBe(1);
    const upload = seen.find(s => s.url.endsWith('/agent/attachments'))!;
    expect(upload.ticket).toBe(TICKET);
    expect(upload.authorization).toBeUndefined();
    await expect(page.getByText('note.txt').first()).toBeVisible();
  });

  test('агент не найден — терминал показывает понятное состояние', async ({ page }) => {
    await asLocalProject(page, origin, projectId, { kind: 'ok', port: AGENT_PORT + 2 });
    await openTab(page, projectId, token, 'Терминал');

    const state = page.locator('[data-device-agent-state="unreachable"]');
    await expect(state).toBeVisible();
    await expect(state.getByText('Агент устройства не найден')).toBeVisible();
  });
});
