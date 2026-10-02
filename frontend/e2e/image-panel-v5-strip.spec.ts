import { test, expect, type Page, type Route, type WebSocketRoute } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';

// Полоса «Картинки» v5 (флаг image-panel-v5, макет docs/mockups/image-panel-v5.html, вариант 1,
// шаг И2): зеркало «Создать / Править», меню «Что править?», плашка «Вернуть», отправка без
// выбранной картинки и «↻ Ещё N». Сценарии 1, 2, 7, 8 и «снять выбор» со счётом кликов и
// переходов, кадры 360 и 1440 обеих тем. Бэкенд не нужен: собранный dist раздаётся статикой,
// /api/** и хаб — моки; состояние нитей после запуска приходит событием хаба, как на бою.
//
//   (cd dist && python3 -m http.server 5232) &
//   PLAYWRIGHT_BASE_URL=http://127.0.0.1:5232 I2_SHOTS_DIR=../.cc-attachments/image-i2 \
//     npx playwright test e2e/image-panel-v5-strip.spec.ts

const SHOTS = process.env.I2_SHOTS_DIR || '';
const TRACE = !!process.env.I2_TRACE;
const P = 'proj-i2';
const S = 'chat-i2';
const H = 'thread-hero';
const D = 'thread-draft';
const CAT = 'thread-cat';
const now = new Date('2026-10-02T10:00:00Z').toISOString();
const PNG = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==', 'base64');

const caps = (ops: string[], mask = 'none', maxReferences = 4) => ({ ops, mask, maxReferences, maxCount: 4, faceByReferences: false });
const CATALOG = {
  default: { provider: 'local', model: 'auto' },
  providers: [
    { key: 'local', label: 'Локальные модели', priceUnit: 'free', available: true, models: [
      { id: 'auto', label: 'Авто' },
      { id: 'qwen-image-2.1', label: 'Qwen-Image 2.1', caps: caps(['generate', 'edit', 'inpaint', 'outpaint', 'removeBackground', 'upscale'], 'asReference', 3), priceHint: { amount: 0, unit: 'free', per: 'image' } },
    ] },
    { key: 'fal', label: 'fal', priceUnit: 'usd', models: [
      { id: 'auto', label: 'Авто' },
      { id: 'fal-ai/flux-pro/kontext', label: 'FLUX Kontext', caps: caps(['generate', 'edit']), priceHint: { amount: 0.04, unit: 'usd', per: 'image' } },
    ] },
  ],
  limits: { maxFileMb: 20, maxReferences: 6, maxCount: 4 }, reason: null,
};

type Launch = { jobId: string; baseVersionId: string | null; baseStepId: string | null; at: string; status: string; initiator: 'human' | 'agent'; prompt: string | null };
type Version = { id: string; number: number; jobId: string | null; variant: number | null; baseVersionId: string | null; baseStepId: string | null; steps: string[]; currentStepId: string | null; createdAt: string };
interface Thread {
  id: string; file: string | null; lineage: string[]; draftFolder: string | null; stacks: never[]; currentStackId: null;
  currentStepId: string | null; settings: null; pendingJobId: null; createdAt: string; versions: Version[]; currentVersionId: string; launches: Launch[];
}
const origin: Version = { id: 'origin', number: 0, jobId: null, variant: null, baseVersionId: null, baseStepId: null, steps: [], currentStepId: null, createdAt: now };
const base = (id: string, file: string | null, at = now): Thread => ({
  id, file, lineage: [], draftFolder: file ? null : 'images', stacks: [], currentStackId: null, currentStepId: null,
  settings: null, pendingJobId: null, createdAt: at, versions: [{ ...origin }], currentVersionId: 'origin', launches: [],
});
const heroThread = () => base(H, 'images/hero.png', '2026-10-02T09:00:00Z');
// Кот, которого нарисовал агент: версия 1 с шагом
const catThread = (): Thread => ({
  ...base(CAT, null, '2026-10-02T09:30:00Z'),
  versions: [{ ...origin }, { id: 'v1', number: 1, jobId: 'job-cat', variant: 0, baseVersionId: null, baseStepId: null, steps: ['st-cat'], currentStepId: 'st-cat', createdAt: '2026-10-02T09:31:00Z' }],
  currentVersionId: 'v1',
  launches: [{ jobId: 'job-cat', baseVersionId: null, baseStepId: null, at: '2026-10-02T09:30:30Z', status: 'done', initiator: 'agent', prompt: 'рыжий кот на подоконнике' }],
});

interface World { focus: string | null; revision: number; threads: Thread[] }
let world: World;
let jobs: { op: string | null; prompt: string | null; threadId: string | null }[];
let quotes: Record<string, unknown>[];
let hub: WebSocketRoute | null = null;

const SESSION = { id: S, projectId: P, mode: 'default', status: 'finished', messageCount: 1, createdAt: now, updatedAt: now, name: 'Картинки для сайта', ownerId: 'u1' };
const PROJECT = { id: P, name: 'Сайт студии', rootPath: '/tmp/i2', createdAt: now, updatedAt: now, ownerId: 'u1' };
const PREFS = {
  provider: null, model: null, count: 2, matchSourceSize: true, characterSlug: null,
  create: { provider: null, model: null, count: 2 },
  edit: { provider: null, model: null, count: 2, op: 'edit', editMode: null, ratio: null },
};

// Событие хаба image_thread_changed — так сервер сообщает о записи нити (запуск, версии)
function pushThreads() {
  hub?.send(JSON.stringify({ type: 1, target: 'message', arguments: [{ type: 'image_thread_changed', sessionId: S, projectId: P, revision: world.revision, state: world }] }) + '\u001e');
}

async function mockApi(page: Page) {
  await page.route('**/hubs/**', async (r: Route) => {
    if (r.request().url().includes('negotiate')) {
      return r.fulfill({ json: { negotiateVersion: 1, connectionId: 'c', connectionToken: 'c', availableTransports: [{ transport: 'WebSockets', transferFormats: ['Text'] }] } });
    }
    return r.fulfill({ status: 404, body: '' });
  });
  await page.routeWebSocket(/\/hubs\//, ws => {
    if (ws.url().includes('/hubs/session')) hub = ws;
    ws.onMessage(m => {
      if (typeof m === 'string' && m.includes('"protocol"')) ws.send('{}\u001e');
      if (typeof m === 'string') {
        for (const rec of m.split('\u001e')) {
          const id = /"invocationId":"([^"]+)"/.exec(rec)?.[1];
          if (id) ws.send(JSON.stringify({ type: 3, invocationId: id, result: null }) + '\u001e');
        }
      }
    });
  });
  await page.route('**/api/**', async (r: Route) => {
    const url = new URL(r.request().url());
    const p = url.pathname.replace(/^\/api/, '');
    const method = r.request().method();
    const json = (body: unknown) => r.fulfill({ json: body });
    const ie = `/projects/${P}/image-editor`;
    if (p === '/auth/me') {
      return json({
        id: 'u1', username: 'admin', displayName: 'Андрей', role: 'admin', executionEnvironment: 'local',
        featureFlags: { 'image-editor': true, 'image-panel-v5': true }, subsystems: ['imageeditor'],
      });
    }
    if (p === '/subsystem-modules') {
      return json({ items: [{ id: 'imageeditor', remoteUrl: '/image-editor-remote/remoteEntry.js', exposedModule: './subsystem' }] });
    }
    if (p === '/projects' && method === 'GET') return json([PROJECT]);
    if (p === `/projects/${P}`) return json(PROJECT);
    if (p === `/chats/${S}/history`) return json([{ kind: 'user_message', text: 'Нужны картинки для сайта', timestamp: Date.parse(now) }]);
    if (p === `/chats/${S}`) return json(SESSION);
    if (p === '/chats' && method === 'GET') return json([SESSION]);
    if (p === `/projects/${P}/sessions` || p === `/projects/${P}/chats`) return json([SESSION]);
    const tb = `${ie}/sessions/${S}/threads`;
    if (p === tb && method === 'GET') return json(world);
    if (p === tb && method === 'POST') {
      const file = (r.request().postDataJSON() as { file?: string | null }).file ?? null;
      if (file) {
        // Файл в работу: его нить найдётся или заведётся заново
        let t = world.threads.find(x => x.file === file);
        if (!t) { t = base(`thread-file-${world.revision}`, file); world.threads.push(t); }
        world.focus = t.id;
      } else {
        // Черновик «Новая картинка»
        if (!world.threads.some(t => t.id === D)) world.threads.push(base(D, null, '2026-10-02T10:00:00Z'));
        world.focus = D;
      }
      world.revision++;
      return json(world);
    }
    // Нить без правок уходит из ленты целиком (✕ на чипе)
    if (method === 'DELETE' && p.startsWith(`${tb}/`)) {
      const id = decodeURIComponent(p.slice(tb.length + 1).split('/')[0]);
      world.threads = world.threads.filter(t => t.id !== id);
      if (world.focus === id) world.focus = null;
      world.revision++;
      return json(world);
    }
    if (p === `${tb}/focus`) {
      world.focus = (r.request().postDataJSON() as { threadId: string | null }).threadId;
      world.revision++;
      return json(world);
    }
    if (p.startsWith(tb)) { world.revision++; return json(world); }
    if (p === `${ie}/catalog`) return json(CATALOG);
    if (p === `${ie}/prefs`) return json(method === 'GET' ? PREFS : { ...PREFS, ...(r.request().postDataJSON() as object) });
    if (p === `${ie}/characters`) return json([]);
    if (p === `${ie}/quote`) {
      const req = r.request().postDataJSON() as { provider: string; model: string; count: number; op: string };
      quotes.push(req);
      return json({
        quoteId: `q${quotes.length}`, provider: req.provider, model: req.model,
        estimate: { amount: 0, unit: 'free', approx: false, source: 'provider', etaSeconds: 40, queueLength: 0 },
        expiresAt: new Date(Date.now() + 600_000).toISOString(), expectedSeconds: 40,
      });
    }
    if (p === `${ie}/jobs` && method === 'POST') {
      const body = r.request().postDataBuffer()?.toString('utf8') ?? '';
      const field = (n: string) => new RegExp(`name="${n}"\\r\\n\\r\\n([^\\r]*)`).exec(body)?.[1] ?? null;
      const job = { op: quotes.at(-1)?.op as string ?? null, prompt: field('prompt'), threadId: field('threadId') };
      jobs.push(job);
      const jobId = `job${jobs.length}`;
      // Запуск ложится в нить; новая версия — с шагом, как после генерации
      const t = world.threads.find(x => x.id === job.threadId);
      if (t) {
        const at = new Date(Date.parse(now) + jobs.length * 60_000).toISOString();
        t.launches.push({ jobId, baseVersionId: null, baseStepId: null, at, status: 'done', initiator: 'human', prompt: job.prompt });
        const n = t.versions.length;
        t.versions.push({ id: `v${n}`, number: n, jobId, variant: 0, baseVersionId: null, baseStepId: null, steps: [`st-${jobId}`], currentStepId: `st-${jobId}`, createdAt: at });
        t.currentVersionId = `v${n}`;
        world.revision++;
        setTimeout(pushThreads, 50);
      }
      return json({ jobId });
    }
    if (/\/image-editor\/jobs\/[^/]+$/.test(p)) return json({ jobId: 'job1', status: 'done', phase: 'done', count: 1, variants: [] });
    if (p.includes('/files/stream') || p.includes('/steps/')) return r.fulfill({ contentType: 'image/png', body: PNG });
    if (p === `/projects/${P}/files/tree`) return json([]);
    const OBJ: Record<string, unknown> = {
      '/modules': { items: [] }, '/models': { models: [] }, '/settings': {}, '/usage': { snapshots: [] }, '/home/summary': { active: [], recent: [] },
      '/chats/agents-presence': { agents: [], commands: [] }, '/watchdogs': { sessions: [], projects: [] },
      '/notifications/unread-count': { count: 0 },
      [`/projects/${P}/git/status`]: { isRepo: false, branch: null, upstream: null, ahead: 0, behind: 0, detached: false, staged: [], unstaged: [], untracked: [], isWorktree: false },
    };
    if (p in OBJ) return json(OBJ[p]);
    if (TRACE) console.log('FALLBACK', method, p);
    return method === 'GET' ? json([]) : json({});
  });
}

async function open(page: Page, vp: { width: number; height: number }, theme: 'light' | 'dark', focus: string | null, mode: 'edit' | 'create', threads: Thread[]) {
  world = { focus, revision: 1, threads };
  jobs = [];
  quotes = [];
  hub = null;
  await page.setViewportSize(vp);
  await page.addInitScript(([th, m, s]) => {
    localStorage.setItem('cc_token', 'e2e-token');
    localStorage.setItem('theme-mode', th as string);
    localStorage.setItem(`cc-image-mode:${s}`, m as string);
    // Полоса развёрнута: на телефоне по умолчанию она строкой, макет считает от развёрнутой
    localStorage.setItem(`cc-composer-strip-collapsed:${s}:images`, '0');
  }, [theme, mode, S]);
  await mockApi(page);
  if (TRACE) page.on('console', m => { if (m.type() === 'error') console.log('CONSOLE', m.text().slice(0, 3000)); });
  await page.goto(`/#/project/${P}/chat/${S}`);
}

// Счёт как в макете: клик — нажатие на контрол, ввод текста не в счёт; переход — смена зоны
// «лента / полоса и поле ввода (b) / панель (p)», старт у поля ввода
class Counter {
  clicks = 0;
  moves = 0;
  zone = 'b';
  async click(z: string, l: ReturnType<Page['locator']>) {
    await l.click();
    this.clicks += 1;
    if (z !== this.zone) { this.moves += 1; this.zone = z; }
  }
}

const strip = (page: Page) => page.locator('[data-images-strip="full"]');
const seg = (page: Page, i: 0 | 1) => strip(page).locator('[data-images-mode-switch] button').nth(i);
const input = (page: Page) => page.locator('textarea').last();
const sendBtn = (page: Page) => page.getByRole('button', { name: /^(Изменить|Сгенерировать) · / });
const imageToggle = (page: Page) => page.getByRole('button', { name: 'Режим «Картинка»' });
const sheet = (page: Page) => page.locator('[data-gen-sheet="sheet"]');
// ✕ на чипе «Работаем с»
const chipX = (page: Page) => strip(page).getByText('×', { exact: true }).first();

// Поле ввода в режим «Картинка» — подготовка сценария, в счёт не входит
async function imageComposer(page: Page) {
  await expect(strip(page)).toBeVisible({ timeout: 30_000 });
  if (await imageToggle(page).isVisible()) await imageToggle(page).click();
  await expect(input(page)).toHaveAttribute('placeholder', /Что изменить|Опишите новую/);
}

async function shot(page: Page, name: string) {
  if (!SHOTS) return;
  fs.mkdirSync(SHOTS, { recursive: true });
  await page.screenshot({ path: path.join(SHOTS, `${name}.png`) });
}

test.afterEach(async ({ page }, info) => {
  if (info.status !== info.expectedStatus) {
    fs.mkdirSync('/tmp/i2', { recursive: true });
    await page.screenshot({ path: `/tmp/i2/fail-${info.title.slice(0, 2)}.png` }).catch(() => {});
  }
});

test('1. нарисовать новую и ещё вариант — 3 / 0', async ({ page }) => {
  await open(page, { width: 1440, height: 900 }, 'light', H, 'edit', [heroThread()]);
  await imageComposer(page);
  const c = new Counter();
  await c.click('b', seg(page, 0));
  // Черновик «Новая картинка» в чипе, hero.png остаётся в ленте
  await expect(strip(page)).toContainText('Новая картинка');
  expect(world.threads.map(t => t.id)).toEqual([H, D]);
  await input(page).fill('маяк на закате, акварель');
  await c.click('b', sendBtn(page));
  await expect.poll(() => jobs.length).toBe(1);
  expect(jobs[0]).toMatchObject({ op: 'generate', threadId: D, prompt: 'маяк на закате, акварель' });
  // Режим остался «Создать», поле пустое — кнопка «↻ Ещё 2»
  await expect(input(page)).toHaveValue('');
  const again = page.locator('[data-composer-send="empty"] button');
  await expect(again).toContainText(/Ещё 2/);
  await c.click('b', again);
  await expect.poll(() => jobs.length).toBe(2);
  expect(jobs[1]).toMatchObject({ op: 'generate', threadId: D, prompt: 'маяк на закате, акварель' });
  expect([c.clicks, c.moves]).toEqual([3, 0]);
  // Та же кнопка — в низу панели
  await page.locator('[data-images-settings-toggle] button').click();
  await expect(page.getByRole('complementary', { name: 'Картинки' }).getByRole('button', { name: /^Ещё 2/ })).toBeVisible();
  await shot(page, 'w1440-light-again');
});

test('2. поправить картинку из ленты — 3 / 0', async ({ page }) => {
  await open(page, { width: 1440, height: 900 }, 'light', null, 'create', [heroThread(), catThread()]);
  await expect(strip(page)).toBeVisible({ timeout: 30_000 });
  const c = new Counter();
  // «Править» без картинки приглушён и спрашивает «Что править?»
  await c.click('b', seg(page, 1));
  await expect(page.getByText('Что править?')).toBeVisible();
  await expect(page.getByText('Или «Работать с этой» на карточке в ленте')).toBeVisible();
  await expect(page.getByRole('button', { name: /^С компьютера/ })).toBeVisible();
  await shot(page, 'w1440-light-pick');
  await c.click('b', page.getByRole('button', { name: /^hero\.png/ }));
  await expect(strip(page)).toContainText('Работаем с:');
  await input(page).fill('вечер, тёплый свет из окна');
  await c.click('b', sendBtn(page));
  await expect.poll(() => jobs.length).toBe(1);
  expect(jobs[0]).toMatchObject({ op: 'edit', threadId: H });
  expect([c.clicks, c.moves]).toEqual([3, 0]);
});

test('7. агент нарисовал — правлю — 2 / 0', async ({ page }) => {
  await open(page, { width: 1440, height: 900 }, 'light', CAT, 'create', [heroThread(), catThread()]);
  await expect(strip(page)).toBeVisible({ timeout: 30_000 });
  const c = new Counter();
  // Кот уже в чипе — «Править» включается без меню, поле ввода — «Картинка»
  await c.click('b', seg(page, 1));
  await expect(page.getByText('Что править?')).toHaveCount(0);
  await input(page).fill('сделай вечер');
  await c.click('b', sendBtn(page));
  await expect.poll(() => jobs.length).toBe(1);
  expect(jobs[0]).toMatchObject({ op: 'edit', threadId: CAT });
  expect([c.clicks, c.moves]).toEqual([2, 0]);
});

test('снять выбор — 1 клик: «Создать», поле остаётся «Картинкой», «Вернуть» возвращает', async ({ page }) => {
  await open(page, { width: 1440, height: 900 }, 'light', H, 'edit', [heroThread(), catThread()]);
  await imageComposer(page);
  const c = new Counter();
  await c.click('b', chipX(page));
  await expect(page.locator('[data-images-release]')).toContainText('Картинка снята — дальше рисуем новую');
  await expect(page.getByText('Картинка больше не выбрана')).toHaveCount(0);
  await expect(input(page)).toHaveAttribute('placeholder', /Опишите новую/);
  await expect(strip(page)).toContainText('Новая картинка');
  expect(c.clicks).toBe(1);
  await shot(page, 'w1440-light-release');
  await page.locator('[data-images-release]').getByRole('button', { name: 'Вернуть' }).click();
  await expect(strip(page)).toContainText('Работаем с:');
  await expect(input(page)).toHaveAttribute('placeholder', /Что изменить/);
});

test('8. телефон 360: правка из ленты — 3 / 0, шторка не поднимается', async ({ page }) => {
  await open(page, { width: 360, height: 780 }, 'light', null, 'create', [heroThread(), catThread()]);
  await expect(strip(page)).toBeVisible({ timeout: 30_000 });
  const c = new Counter();
  await c.click('b', seg(page, 1));
  await expect(page.getByText('Что править?')).toBeVisible();
  await shot(page, 'm360-light-pick');
  await c.click('b', page.getByRole('button', { name: /^hero\.png/ }));
  await expect(strip(page).locator('[data-images-mode-switch] button').nth(1)).toBeVisible();
  await page.waitForTimeout(300);
  await expect(sheet(page)).toHaveCount(0);
  await input(page).fill('вечер, тёплый свет из окна');
  await c.click('b', sendBtn(page));
  await expect.poll(() => jobs.length).toBe(1);
  expect(jobs[0]).toMatchObject({ op: 'edit', threadId: H });
  expect([c.clicks, c.moves]).toEqual([3, 0]);
});

// Кадры полосы обеих тем: 1440 — подписи и сводка, 360 — иконки и «▴»
for (const theme of ['light', 'dark'] as const) {
  test(`кадры полосы, тема ${theme}`, async ({ page }) => {
    for (const vp of [{ width: 1440, height: 900, tag: 'w1440' }, { width: 360, height: 780, tag: 'm360' }]) {
      await open(page, vp, theme, H, 'edit', [heroThread(), catThread()]);
      await imageComposer(page);
      await expect(strip(page).locator('[data-images-mode-switch]')).toBeVisible();
      if (vp.width === 360) await expect(strip(page).locator('[data-images-settings-toggle] button')).toHaveAttribute('aria-label', /Открыть настройки/);
      await shot(page, `${vp.tag}-${theme}-edit`);
      await chipX(page).click();
      await expect(page.locator('[data-images-release]')).toBeVisible();
      await shot(page, `${vp.tag}-${theme}-release`);
      await page.goto('about:blank');
    }
  });
}
