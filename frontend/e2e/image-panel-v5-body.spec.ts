import { test, expect, type Page, type Route } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';

// Тело панели «Картинки» v5 (флаг image-panel-v5, макет docs/mockups/image-panel-v5.html,
// вариант 1): сценарии 3–6 со счётом кликов и переходов между зонами, первый экран шторки
// на 360 и кадры обеих тем. Бэкенд не нужен: собранный dist раздаётся статикой, /api/** и
// хаб — моки. Запуск задачи ловится по телу multipart: маска уходит полем mask.
//
//   (cd dist && python3 -m http.server 5231) &
//   PLAYWRIGHT_BASE_URL=http://127.0.0.1:5231 I3_SHOTS_DIR=../.cc-attachments/image-i3 \
//     npx playwright test e2e/image-panel-v5-body.spec.ts

const SHOTS = process.env.I3_SHOTS_DIR || '';
const TRACE = !!process.env.I3_TRACE;
const P = 'proj-i3';
const S = 'chat-i3';
const H = 'thread-hero';
const D = 'thread-draft';
const now = new Date('2026-10-02T10:00:00Z').toISOString();
const PNG = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==', 'base64');
// hero.png 320×240 (небо, солнце, земля): по ней кисть рисует маску, у PNG 1×1 места под мазок нет
const HERO = Buffer.from(
  'iVBORw0KGgoAAAANSUhEUgAAAUAAAADwCAIAAAD+Tyo8AAACy0lEQVR42u3TUQkAIBBEQXOaxBCG8NskhrgQRrCECCcDk2DZV9oMuG7H4IHiaghYwCBgASNgBIyABQwCFjACRsAIGAEjYAGDgAWMgBEwAhYwCFjAIGABI2AEjIAFDAIWMAJGwAgYASNgAYOABYyAETACFjAIWMAIWF0CRsAIGAELGAQsYASMgBEwAkbAAgYBCxgBI2AELGAQsIARMAJGwAgYAQsYBCxgBIyAEbCAvQ0BCxgELGAEjIARsIBBwAJGwAgYASNgBCxgELCAETACRsACBgELGAQsYASMgBGwgEHAAkbACBgBI2AELGAQsIARMAJGwAIGAQsYASNgBIyAEbCAQcACRsAIGAEjYAQsYBCwgBEwAkbAAgYBCxgBI2AEjIARsIBBwAJGwAgYAQsYBCxgELCAETACRsACBgELGAEjYASMgBGwgEHAAkbACBgBCxgELGAQsIARMAJGwAIGAQsYASNgBIyAEbCAQcACRsAIGAELGAQsYASMgBEwAkbAAgYBCxgBI2AEjIARsIBBwAJGwAgYAQsYBCxgBIyAETACRsACBgELGAEjYAQsYBCwgEHAAkbACBgBCxgELGAEjIARMAJGwAIGAQsYASNgBCxgELCAQVoCRsAIGAELGAQsYASMgBEwAkbAAgYBCxgBI2AELGAQsIARMAJGwAgYAQsYBCxgBIyAEbCAvQ0B5w149QokJWAQMCBgQMAgYEDAgIABAYOAAQEDAgYBAwIGBAwIGAQMCBgQMCBgEDAgYEDAIGBAwICAAQGDgAEBAwIGBAwCBgQMCBgEDAgYEDAgYBAwIGBAwCBgK4CAAQEDAgYBAwIGBAwIGAQMCBgQMAgYEDAgYEDAIGBAwICAAQGDgAEBAwIGAQMCBgQMCBgEDAgYEDAgYBAwIGBAwCBgQMCAgAEBg4ABAQMCBgEDAgYEDAgYBAwIGBAwIGD4xwF2D3CwePmopAAAAABJRU5ErkJggg==', 'base64');

const caps = (ops: string[], mask = 'none', maxReferences = 4) => ({ ops, mask, maxReferences, maxCount: 4, faceByReferences: false });
const CATALOG = {
  default: { provider: 'local', model: 'auto' },
  providers: [
    { key: 'local', label: 'Локальные модели', priceUnit: 'free', available: true, models: [
      { id: 'auto', label: 'Авто' },
      { id: 'qwen-image-2.1', label: 'Qwen-Image 2.1', caps: caps(['generate', 'edit', 'inpaint', 'outpaint', 'removeBackground', 'upscale'], 'asReference', 3), priceHint: { amount: 0, unit: 'free', per: 'image' } },
      { id: 'face-detailer', label: 'Улучшить лица', caps: { ...caps(['enhanceFaces'], 'none', 0), maxCount: 1 }, priceHint: { amount: 0, unit: 'free', per: 'image' } },
    ] },
    { key: 'fal', label: 'fal', priceUnit: 'usd', models: [
      { id: 'auto', label: 'Авто' },
      { id: 'fal-ai/flux-pro/kontext', label: 'FLUX Kontext', caps: caps(['generate', 'edit']), priceHint: { amount: 0.04, unit: 'usd', per: 'image' } },
      { id: 'fal-ai/birefnet', label: 'BiRefNet', caps: caps(['removeBackground', 'upscale'], 'none', 0), priceHint: { amount: 0.01, unit: 'usd', per: 'image' } },
    ] },
    { key: 'higgsfield', label: 'Higgsfield', priceUnit: 'credits', models: [
      { id: 'soul_2', label: 'Soul', caps: caps(['generate']), priceHint: { amount: 2, unit: 'credits', per: 'image' } },
    ] },
  ],
  limits: { maxFileMb: 20, maxReferences: 6, maxCount: 4 }, reason: null,
};

const origin = { id: 'origin', number: 0, jobId: null, variant: null, baseVersionId: null, baseStepId: null, steps: [], currentStepId: null, createdAt: now };
const heroThread = () => ({
  id: H, file: 'images/hero.png', lineage: [], draftFolder: null, stacks: [], currentStackId: null, currentStepId: null,
  settings: null, pendingJobId: null, createdAt: now, versions: [origin], currentVersionId: 'origin', launches: [],
});
const draftThread = () => ({
  id: D, file: null, lineage: [], draftFolder: 'images', stacks: [], currentStackId: null, currentStepId: null,
  settings: null, pendingJobId: null, createdAt: now, versions: [{ ...origin }], currentVersionId: 'origin', launches: [],
});

interface World { focus: string | null; revision: number; threads: ReturnType<typeof heroThread>[] }
let world: World;
let jobs: { op: string | null; fields: string[]; aspectRatio: string | null }[];
let quotes: Record<string, unknown>[];

const SESSION = { id: S, projectId: P, mode: 'default', status: 'finished', messageCount: 1, createdAt: now, updatedAt: now, name: 'Картинки для сайта', ownerId: 'u1' };
const PROJECT = { id: P, name: 'Сайт студии', rootPath: '/tmp/i3', createdAt: now, updatedAt: now, ownerId: 'u1' };
const PREFS = {
  provider: null, model: null, count: 2, matchSourceSize: true, characterSlug: null,
  create: { provider: null, model: null, count: 2 },
  edit: { provider: null, model: null, count: 2, op: 'edit', editMode: null, ratio: null },
};

async function mockApi(page: Page) {
  await page.route('**/hubs/**', async (r: Route) => {
    if (r.request().url().includes('negotiate')) {
      return r.fulfill({ json: { negotiateVersion: 1, connectionId: 'c', connectionToken: 'c', availableTransports: [{ transport: 'WebSockets', transferFormats: ['Text'] }] } });
    }
    return r.fulfill({ status: 404, body: '' });
  });
  await page.routeWebSocket(/\/hubs\//, ws => {
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
    // Картинки
    const tb = `${ie}/sessions/${S}/threads`;
    if (p === tb && method === 'GET') return json(world);
    if (p === tb && method === 'POST') {
      // Черновик «Новая картинка»
      if (!world.threads.some(t => t.id === D)) world.threads.push(draftThread());
      world.focus = D;
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
    if (p === `${ie}/characters`) {
      return json([
        { slug: 'anya', name: 'Аня', path: 'characters/anya', photos: [{ file: '1.png' }, { file: '2.png' }], createdAt: now },
        { slug: 'petya', name: 'Петя', path: 'characters/petya', photos: [{ file: '1.png' }], createdAt: now },
      ]);
    }
    if (p.startsWith(`${ie}/characters/`)) return r.fulfill({ contentType: 'image/png', body: PNG });
    if (p === `${ie}/quote`) {
      const req = r.request().postDataJSON() as { provider: string; model: string; count: number; op: string };
      quotes.push(req);
      const free = req.provider === 'local';
      return json({
        quoteId: `q${quotes.length}`, provider: req.provider, model: req.model,
        estimate: free ? { amount: 0, unit: 'free', approx: false, source: 'provider', etaSeconds: 40, queueLength: 0 }
          : { amount: 0.04 * req.count, unit: 'usd', approx: true, source: 'catalog' },
        expiresAt: new Date(Date.now() + 600_000).toISOString(), expectedSeconds: 40,
      });
    }
    if (p === `${ie}/jobs` && method === 'POST') {
      const body = r.request().postDataBuffer()?.toString('latin1') ?? '';
      const fields = [...body.matchAll(/name="([^"]+)"/g)].map(m => m[1]);
      const field = (n: string) => new RegExp(`name="${n}"\\r\\n\\r\\n([^\\r]*)`).exec(body)?.[1] ?? null;
      jobs.push({ op: quotes.at(-1)?.op as string ?? null, fields, aspectRatio: field('aspectRatio') });
      return json({ jobId: `job${jobs.length}` });
    }
    if (/\/image-editor\/jobs\/[^/]+$/.test(p)) return json({ jobId: 'job1', status: 'running', phase: 'run', count: 1, variants: [] });
    if (p.includes('/files/stream') || p.includes('/steps/')) return r.fulfill({ contentType: 'image/png', body: HERO });
    if (p === `/projects/${P}/files/tree`) {
      const f = (fp: string) => ({ name: fp.split('/').pop(), path: fp, isDirectory: false, modified: now, isModified: false });
      return json([
        { name: 'assets', path: 'assets', isDirectory: true, modified: now, isModified: false },
        f('assets/palette.png'), f('assets/logo.png'), f('images/hero.png'),
      ]);
    }
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

async function open(page: Page, vp: { width: number; height: number }, theme: 'light' | 'dark', focus: string | null, mode: 'edit' | 'create') {
  world = { focus, revision: 1, threads: [heroThread()] };
  jobs = [];
  quotes = [];
  await page.setViewportSize(vp);
  await page.addInitScript(([th, m, s]) => {
    localStorage.setItem('cc_token', 'e2e-token');
    localStorage.setItem('theme-mode', th as string);
    localStorage.setItem(`cc-image-mode:${s}`, m as string);
  }, [theme, mode, S]);
  await mockApi(page);
  if (TRACE) page.on('console', m => { if (m.type() === 'error') console.log('CONSOLE', m.text().slice(0, 3000)); });
  await page.goto(`/#/project/${P}/chat/${S}`);
}

// Счёт как в макете: клик — нажатие на контрол, ввод текста не в счёт; переход — смена зоны
// «лента / полоса и поле ввода (b) / панель (p) / редактор (e)», старт у поля ввода
class Counter {
  clicks = 0;
  moves = 0;
  zone = 'b';
  async click(z: string, l: ReturnType<Page['locator']>) {
    await l.click();
    this.step(z);
  }
  async select(z: string, l: ReturnType<Page['locator']>, value: string) {
    // Открыть список и выбрать пункт — два нажатия
    await l.selectOption(value);
    this.clicks += 1;
    this.step(z);
  }
  step(z: string) {
    this.clicks += 1;
    if (z !== this.zone) { this.moves += 1; this.zone = z; }
  }
}

const panel = (page: Page) => page.getByRole('complementary', { name: 'Картинки' });
const summary = (page: Page) => page.locator('[data-images-settings-toggle] button');
const input = (page: Page) => page.locator('textarea').last();
// Кнопка поля ввода — с ценой через точку; у низа панели цена отдельной строкой
const sendBtn = (page: Page) => page.getByRole('button', { name: /^(Изменить|Сгенерировать)( отмеченное)? · / });

// Поле ввода в режим «Картинка» — подготовка сценария, в счёт не входит
async function imageComposer(page: Page) {
  await expect(page.locator('[data-images-strip="full"]')).toBeVisible({ timeout: 30_000 });
  const toggle = page.getByRole('button', { name: 'Режим «Картинка»' });
  if (await toggle.isVisible()) await toggle.click();
  await expect(input(page)).toHaveAttribute('placeholder', /Что изменить|Опишите новую/);
}

// Панель «Картинки»: на 360 полоса свёрнута в строку — её сводка открывает шторку
async function openPanel(page: Page) {
  await expect(page.locator('[data-composer-strip="images"]')).toBeVisible({ timeout: 30_000 });
  const mini = page.locator('[data-images-strip="mini"] [data-images-summary]');
  if (await mini.isVisible()) await mini.click();
  else await summary(page).click();
}

async function shot(page: Page, name: string) {
  if (!SHOTS) return;
  fs.mkdirSync(SHOTS, { recursive: true });
  await page.screenshot({ path: path.join(SHOTS, `${name}.png`) });
}

test.afterEach(async ({ page }, info) => {
  if (info.status !== info.expectedStatus) await page.screenshot({ path: `/tmp/i3/fail-${info.title.slice(0, 2)}.png` }).catch(() => {});
});

test.describe('сценарии v5, 1440', () => {
  test.beforeEach(async ({ page }) => { await page.setViewportSize({ width: 1440, height: 900 }); });

  test('3. убрать лишнее по отмеченному — 4 / 2', async ({ page }) => {
    await open(page, { width: 1440, height: 900 }, 'light', H, 'edit');
    await imageComposer(page);
    const c = new Counter();
    await c.click('e', page.locator('[data-image-brush]'));
    await expect(page.getByRole('heading', { name: 'Редактор · hero.png' })).toBeVisible();
    const pic = page.locator('svg[viewBox="0 0 320 240"]');
    await expect(pic).toBeVisible();
    const box = (await pic.boundingBox())!;
    await page.mouse.move(box.x + box.width * 0.6, box.y + box.height * 0.4);
    await page.mouse.down();
    await page.mouse.move(box.x + box.width * 0.75, box.y + box.height * 0.6, { steps: 6 });
    await page.mouse.up();
    c.step('e');
    await c.click('b', page.getByRole('button', { name: 'Готово' }));
    await expect(page.getByRole('heading', { name: 'Редактор · hero.png' })).toHaveCount(0);
    await input(page).fill('убери человека справа');
    await c.click('b', sendBtn(page));
    await expect.poll(() => jobs.length).toBe(1);
    expect(quotes.at(-1)).toMatchObject({ op: 'inpaint', hasMask: true });
    expect(jobs[0].fields).toContain('mask');
    expect([c.clicks, c.moves]).toEqual([4, 2]);
  });

  test('4. дорисовать за края до 9:16 — 5 / 1', async ({ page }) => {
    await open(page, { width: 1440, height: 900 }, 'light', H, 'edit');
    await imageComposer(page);
    const c = new Counter();
    await c.click('p', summary(page));
    await c.select('p', panel(page).locator('[data-image-op-select] select'), 'outpaint');
    await c.click('p', panel(page).getByRole('button', { name: '9:16', exact: true }));
    await c.click('p', panel(page).getByRole('button', { name: /^Дорисовать/ }));
    await expect.poll(() => jobs.length).toBe(1);
    expect(jobs[0]).toMatchObject({ op: 'outpaint', aspectRatio: '9:16' });
    expect([c.clicks, c.moves]).toEqual([5, 1]);
  });

  test('5. убрать фон, потом улучшить — 7 / 1', async ({ page }) => {
    await open(page, { width: 1440, height: 900 }, 'light', H, 'edit');
    await imageComposer(page);
    const c = new Counter();
    await c.click('p', summary(page));
    const op = panel(page).locator('[data-image-op-select] select');
    await c.select('p', op, 'removeBackground');
    await expect(panel(page).locator('[data-image-no-samples]')).toBeVisible();
    await c.click('p', panel(page).getByRole('button', { name: /^Убрать фон/ }));
    await expect.poll(() => jobs.length).toBe(1);
    await c.select('p', op, 'upscale');
    await c.click('p', panel(page).getByRole('button', { name: /^Улучшить/ }));
    await expect.poll(() => jobs.length).toBe(2);
    expect(jobs.map(j => j.op)).toEqual(['removeBackground', 'upscale']);
    expect([c.clicks, c.moves]).toEqual([7, 1]);
  });

  test('6. Аня в стиле palette.png — 8 / 2', async ({ page }) => {
    await open(page, { width: 1440, height: 900 }, 'light', null, 'edit');
    await expect(page.locator('[data-images-strip="full"]')).toBeVisible({ timeout: 30_000 });
    const c = new Counter();
    // «Создать» в полосе — шаг И2; до него ту же роль играет «Нарисовать новую»
    await c.click('b', page.locator('[data-images-strip="full"]').getByText('Нарисовать новую'));
    await c.click('p', summary(page));
    await expect(panel(page).locator('[data-image-body="create"]')).toBeVisible();
    await c.click('p', panel(page).locator('[data-image-character-pick]'));
    await c.click('p', page.getByRole('button', { name: /^Аня/ }));
    await c.click('p', panel(page).getByText('Образец', { exact: true }));
    await c.click('p', page.getByRole('button', { name: /^Из файлов проекта/ }));
    await c.click('p', page.getByRole('button', { name: /palette\.png/ }));
    await expect(panel(page).locator('[data-sample="palette.png"]')).toBeVisible();
    await expect(panel(page).locator('[data-image-character-pick]')).toContainText('Аня');
    await input(page).fill('Аня у окна с чашкой какао');
    await c.click('b', sendBtn(page));
    await expect.poll(() => jobs.length).toBe(1);
    expect(jobs[0].op).toBe('generate');
    expect(jobs[0].fields).toEqual(expect.arrayContaining(['characterSlug', 'referencePaths']));
    expect([c.clicks, c.moves]).toEqual([8, 2]);
  });
});

// Шторка 360: поля «Создать» и «Изменить» и строка «Чем» — в первом экране, без прокрутки
for (const theme of ['light', 'dark'] as const) {
  test(`шторка 360 и колонка 1440, тема ${theme}`, async ({ page }) => {
    for (const [mode, focus] of [['edit', H], ['create', null]] as const) {
      await open(page, { width: 360, height: 780 }, theme, focus, mode);
      await openPanel(page);
      const sheet = page.locator('[data-gen-sheet="sheet"]');
      await expect(sheet).toBeVisible();
      const body = sheet.locator(`[data-image-body="${mode}"]`);
      await expect(body).toBeVisible();
      const exec = body.locator('[data-image-executor]');
      const sb = (await sheet.boundingBox())!;
      const eb = (await exec.boundingBox())!;
      expect(eb.y + eb.height, `${mode}: «Чем» в первом экране шторки`).toBeLessThanOrEqual(sb.y + sb.height);
      await shot(page, `m360-${theme}-${mode}`);
      if (mode === 'edit') {
        await expect(body.locator('[data-image-where]')).toContainText('отметок на телефоне нет');
        await expect(body.getByRole('button', { name: 'Отметить в редакторе' })).toHaveCount(0);
      }
      await page.setViewportSize({ width: 1440, height: 900 });
      await page.goto('about:blank');
      await open(page, { width: 1440, height: 900 }, theme, focus, mode);
      await openPanel(page);
      await expect(panel(page).locator(`[data-image-body="${mode}"]`)).toBeVisible();
      await panel(page).locator('[data-image-executor] button').first().click();
      await panel(page).getByRole('button', { name: /Ещё настройки/ }).click();
      await shot(page, `w1440-${theme}-${mode}`);
      await page.goto('about:blank');
    }
  });
}
