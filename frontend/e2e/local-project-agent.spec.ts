import { test, expect, type APIRequestContext, type Page, type Route } from '@playwright/test';
import { createServer, type IncomingMessage, type Server } from 'node:http';

// E2E работы с локальным проектом через агента устройства (ADR-016, задача 4.4).
// Живого устройства в e2e нет, поэтому: серверный проект подменяется в ответе /api/projects
// на локальный (матрица host=device), билет выдаёт перехват маршрута, а на 127.0.0.1:47318
// слушает фейковый агент — настоящий HTTP-сервер с тем же периметром, что у агента:
// Origin только веб-морды, билет в X-Agent-Ticket, ответ на preflight.

const USER = process.env.E2E_USER || 'admin';
const PASS = process.env.E2E_PASS || '12345';
const RUN = Date.now().toString(36);
const PROJECT_NAME = `E2E агент ${RUN}`;
const AGENT_PORT = 47318;
const TICKET = 'fake-ticket';

async function login(request: APIRequestContext): Promise<string> {
  const r = await request.post('/api/auth/login', { data: { username: USER, password: PASS } });
  expect(r.ok(), 'логин должен пройти').toBeTruthy();
  return (await r.json()).token as string;
}

const auth = (token: string) => ({ Authorization: `Bearer ${token}` });

// ---------- фейковый агент ----------

interface SeenRequest { method: string; url: string; ticket?: string; authorization?: string; origin?: string }

function startFakeAgent(origin: string, seen: SeenRequest[]): Promise<Server> {
  const server = createServer((req: IncomingMessage, res) => {
    const reqOrigin = req.headers.origin;
    if (reqOrigin && reqOrigin !== origin) { res.writeHead(403).end(); return; }
    if (reqOrigin) { res.setHeader('Access-Control-Allow-Origin', reqOrigin); res.setHeader('Vary', 'Origin'); }
    if (req.method === 'OPTIONS') {
      res.writeHead(204, {
        'Access-Control-Allow-Methods': 'GET, POST, PUT, DELETE',
        'Access-Control-Allow-Headers': 'X-Agent-Ticket, Content-Type',
        'Access-Control-Allow-Private-Network': 'true',
      }).end();
      return;
    }
    seen.push({
      method: req.method ?? '', url: req.url ?? '',
      ticket: req.headers['x-agent-ticket'] as string | undefined,
      authorization: req.headers.authorization,
      origin: reqOrigin,
    });
    const json = (status: number, body: unknown) => {
      res.writeHead(status, { 'Content-Type': 'application/json' }).end(JSON.stringify(body));
    };
    if (req.headers['x-agent-ticket'] !== TICKET) { json(401, { error: 'Билет к агенту недействителен или истёк' }); return; }
    const path = (req.url ?? '').split('?')[0].replace(/^\/api\/projects\/[^/]+\//, '');
    const now = new Date().toISOString();
    if (req.method === 'GET' && (path === 'files' || path === 'files/tree')) {
      json(200, [{ name: 'agent-readme.md', path: 'agent-readme.md', isDirectory: false, size: 12, modified: now, isModified: false }]);
      return;
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
  // Серверная файловая подсистема локального проекта трогаться не должна: любой такой
  // запрос — провал маршрутизации «сервер или агент»
  await page.route(new RegExp(`^${escapeRe(serverOrigin)}/api/projects/${projectId}/(files|git)(/|\\?|$)`), (route: Route) =>
    route.fulfill({ status: 599, json: { error: 'запрос файлов локального проекта ушёл на сервер' } }));
}

async function openTab(page: Page, projectId: string, token: string, tab: 'Файлы' | 'Изменения') {
  await page.context().addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
  await page.goto(`/#/project/${projectId}`);
  // К имени кнопки рельсы дописывается расшифровка счётчика («Изменения: …»), как только
  // статус git от агента пришёл
  await page.getByRole('button', { name: new RegExp(`^${tab}(:|$)`) }).first().click();
}

test.describe('локальный проект через агента устройства (ADR-016, 4.4)', () => {
  let token: string;
  let projectId: string;
  let origin: string;
  let agent: Server | null = null;
  const seen: SeenRequest[] = [];

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
    agent = await startFakeAgent(origin, seen);
  });

  test.afterAll(async ({ playwright, baseURL }) => {
    await new Promise<void>(resolve => (agent ? agent.close(() => resolve()) : resolve()));
    const request = await playwright.request.newContext({ baseURL });
    // Токен из beforeAll: вход под rate-limit, лишний логин в хвосте прогона его выжигает
    if (projectId) await request.delete(`/api/projects/${projectId}`, { headers: auth(token) });
    await request.dispose();
  });

  test.beforeEach(() => { seen.length = 0; });
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

  test('агент не найден — понятное состояние и повтор', async ({ page }) => {
    // Билет указывает на порт, где никто не слушает: так выглядит машина без агента
    await asLocalProject(page, origin, projectId, { kind: 'ok', port: AGENT_PORT + 1 });
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
});
