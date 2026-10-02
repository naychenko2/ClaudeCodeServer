import { test, expect, type Locator, type Page } from '@playwright/test';
import { execFileSync } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';

// Зеркало «Голос / Музыка / Обработка» в полосе и панели и меню «Что обработать?» (план «Звук»,
// шаг K2; макет audio-panel-v4-proposal.md, вариант 3). Сценарии 1, 3, 5, 7 и «снять выбор»
// проходят тем числом кликов и переходов между зонами, что посчитан в макете. Стенд — на
// ВРЕМЕННОЙ data, каталог и котировка — фикстурой, запуск задачи перехвачен.
//
//   AE_PROJECT_ROOT=/tmp/k2-stand/proj PLAYWRIGHT_BASE_URL=http://127.0.0.1:5000 \
//     AE_SHOTS_DIR=../.cc-attachments/audio-k2 npx playwright test e2e/audio-mode-switch.spec.ts

const USER = process.env.E2E_USER || 'admin';
const PASS = process.env.E2E_PASS || '12345';
const ROOT = process.env.AE_PROJECT_ROOT || '';
const SHOTS = process.env.AE_SHOTS_DIR || '';

let token = '';
let projectId = '';

// Иначе запросы к API уходят через service worker прод-сборки мимо перехватов page.route
test.use({ serviceWorkers: 'block' });

const caps = (ops: string[], extra: Record<string, unknown> = {}) => ({
  ops, languages: ['ru', 'en'], voiceKinds: ['preset'], producesFiles: ['audio'], license: { label: 'MIT', kind: 'permissive' },
  priceUnit: 'free', maxTextChars: 5000, minDurationSec: 10, maxDurationSec: 240, ...extra,
});
const CATALOG = {
  autoModelId: 'auto', maxCount: 4, autoProviders: ['local'],
  providers: [{
    key: 'local', label: 'Локально', priceUnit: 'free', available: true, reason: null,
    models: [
      { id: 'qwen3-tts', label: 'Qwen3-TTS', caps: caps(['speak']) },
      { id: 'ace-step-1.5-xl', label: 'ACE-Step 1.5 XL', caps: caps(['song']) },
      { id: 'demucs', label: 'HTDemucs', caps: caps(['separate'], { languages: [], languageNeutral: true }) },
    ],
  }],
};

const FILES = {
  song: ['music/ёжик-v2.mp3', 'music/ёжик-v3.mp3'],
  lines: ['lines/реплика-1.mp3', 'lines/реплика-2.mp3', 'lines/реплика-3.mp3'],
};

const auth = () => ({ Authorization: `Bearer ${token}` });

test.beforeAll(async ({ playwright, baseURL }) => {
  expect(ROOT, 'нужен AE_PROJECT_ROOT стенда на временной data').toBeTruthy();
  const request = await playwright.request.newContext({ baseURL });
  const r = await request.post('/api/auth/login', { data: { username: USER, password: PASS } });
  expect(r.ok(), 'логин должен пройти').toBeTruthy();
  token = (await r.json()).token as string;
  await request.put('/api/feature-flags/audio-editor', { headers: auth(), data: { enabled: true } });
  for (const [i, f] of [...FILES.song, ...FILES.lines].entries()) {
    const out = path.join(ROOT, f);
    if (fs.existsSync(out)) continue;
    fs.mkdirSync(path.dirname(out), { recursive: true });
    execFileSync('ffmpeg', ['-loglevel', 'error', '-y', '-f', 'lavfi', '-i', `sine=frequency=${220 + i * 40}:duration=2`, out]);
  }
  let projects = (await (await request.get('/api/projects', { headers: auth() })).json()) as { id: string; rootPath: string }[];
  if (!projects.some(p => p.rootPath === ROOT)) {
    await request.post('/api/projects', { headers: auth(), data: { name: 'Звук', rootPath: ROOT } });
    projects = (await (await request.get('/api/projects', { headers: auth() })).json()) as { id: string; rootPath: string }[];
  }
  projectId = projects.find(p => p.rootPath === ROOT)!.id;
  await request.dispose();
});

interface Sent { quote: Record<string, unknown>[]; jobs: number; concat: Record<string, unknown>[] }

async function fixtures(page: Page): Promise<Sent> {
  const sent: Sent = { quote: [], jobs: 0, concat: [] };
  await page.route('**/audio-editor/**/catalog', r => r.fulfill({ json: CATALOG }));
  await page.route('**/audio-editor/**/state', async r => {
    const res = await r.fetch();
    await r.fulfill({ response: res, json: { ...(await res.json()), catalog: CATALOG } });
  });
  await page.route('**/audio-editor/**/quote', async r => {
    const body = r.request().postDataJSON() as Record<string, unknown>;
    sent.quote.push(body);
    await r.fulfill({ json: {
      quoteId: 'q1', mode: body.mode, op: body.mode === 'process' ? 'separate' : 'speak', provider: 'local', model: 'auto', count: 1, voiceKind: null,
      price: { amount: 0, unit: 'free', approx: false, source: 'catalog', eta: 30, queueLength: 0 }, license: 'MIT', heavy: false,
      expiresAt: new Date(Date.now() + 600_000).toISOString(),
    } });
  });
  // Задачу не запускаем: стенду нечем считать, а сценарию важен сам запуск
  await page.route(/\/audio-editor\/(.*\/)?jobs$/, async r => {
    if (r.request().method() !== 'POST') return r.fallback();
    sent.jobs += 1;
    await r.fulfill({ status: 500, json: { error: 'e2e: запуск перехвачен' } });
  });
  await page.route('**/audio-editor/**/concat', async r => {
    sent.concat.push(r.request().postDataJSON() as Record<string, unknown>);
    await r.fallback();
  });
  return sent;
}

const chatBase = (sid: string) => `/api/projects/${projectId}/audio-editor/sessions/${sid}`;

// Чат со звуками из files; выбор снят, префы режимов — с чистого листа
async function chat(page: Page, files: string[]): Promise<string> {
  const req = page.request;
  const sid = (await (await req.post(`/api/projects/${projectId}/sessions`, { headers: auth(), data: { name: `Зеркало ${Date.now()}` } })).json()).id as string;
  const empty = { operation: null, provider: null, model: null, count: null, fields: null, inputs: null };
  for (const m of ['voice', 'music', 'process']) await req.put(`/api/projects/${projectId}/audio-editor/prefs/${m}`, { headers: auth(), data: empty });
  let revision = 0;
  for (const file of files) {
    const opened = await req.post(`${chatBase(sid)}/threads`, { headers: auth(), data: { file, mode: 'music', revision } });
    expect(opened.ok(), `нить должна открыться: ${await opened.text()}`).toBeTruthy();
    revision = (await opened.json()).revision as number;
  }
  if (files.length) {
    const f = await req.put(`${chatBase(sid)}/threads/focus`, { headers: auth(), data: { threadId: null, revision } });
    expect(f.ok(), `выбор должен сняться: ${await f.text()}`).toBeTruthy();
  }
  return sid;
}

// Старт: полоса «Звук» развёрнута над полем ввода, панель закрыта
async function open(page: Page, sid: string, width: number, theme: 'light' | 'dark' = 'light') {
  await page.setViewportSize({ width, height: width < 500 ? 780 : 900 });
  await page.addInitScript(([tk, s, th]) => {
    localStorage.setItem('cc_token', tk);
    localStorage.setItem('theme-mode', th);
    localStorage.setItem(`cc-composer-strip:${s}`, 'sound');
    localStorage.setItem(`cc-composer-strip-collapsed:${s}:sound`, '0');
  }, [token, sid, theme] as const);
  await page.goto(`/#/project/${projectId}/chat/${sid}`);
  await expect(page.locator('[data-sound-strip="full"]')).toBeVisible({ timeout: 30_000 });
  await closeToasts(page);
}

async function closeToasts(page: Page) {
  const close = page.locator('[data-cc-src*="NotificationToasts"] [title="Закрыть"]');
  for (let i = 0; i < 5 && await close.count(); i++) await close.first().click().catch(() => {});
}

const shot = async (page: Page, name: string) => {
  if (!SHOTS) return;
  fs.mkdirSync(SHOTS, { recursive: true });
  await page.screenshot({ path: path.join(SHOTS, `${name}.png`), fullPage: false });
};

// Счётчик макета: клики по контролам и переходы между зонами (b — полоса и поле ввода,
// p — панель, f — лента); старт — у поля ввода, ввод текста не считается
function counter() {
  let clicks = 0;
  let moves = 0;
  let zone = 'b';
  const at = (z: 'b' | 'p' | 'f', n: number) => {
    clicks += n;
    if (z !== zone) moves += 1;
    zone = z;
  };
  return {
    click: async (z: 'b' | 'p' | 'f', l: Locator) => { await l.click(); at(z, 1); },
    // Нативный список: открыть и выбрать — два клика, как в макете
    select: async (z: 'b' | 'p' | 'f', l: Locator, label: string) => { await l.selectOption({ label }); at(z, 2); },
    get: () => `${clicks}/${moves}`,
  };
}

const strip = (page: Page) => page.locator('[data-sound-strip="full"]');
// По месту, а не по имени: на телефоне у иконки имя — подсказка, у приглушённой «Обработки» она своя
const SEG = { Голос: 0, Музыка: 1, Обработка: 2 } as const;
const stripMode = (page: Page, name: keyof typeof SEG) => strip(page).locator('[data-sound-mode-switch] button').nth(SEG[name]);
const send = (page: Page) => page.locator('[data-composer-mode-bar] [data-composer-send] button');
const menu = (page: Page) => page.getByText('Звуки этого чата, свежие сверху', { exact: true });
const pressed = (l: Locator) => expect(l).toHaveAttribute('aria-pressed', 'true');

test('сценарий 1 · озвучить абзац: 3 клика, 0 переходов', async ({ page }) => {
  const sent = await fixtures(page);
  const sid = await chat(page, []);
  await open(page, sid, 1440);
  await stripMode(page, 'Музыка').click();
  const c = counter();
  await c.click('b', strip(page).locator('[data-sound-chip="new"]'));
  await expect(strip(page).locator('[data-sound-chip="focus"]')).toBeVisible();
  await c.click('b', stripMode(page, 'Голос'));
  await page.locator('[data-composer-input] textarea').fill('Жил-был ёжик, и любил он яблоки.');
  await c.click('b', send(page));
  await expect.poll(() => sent.jobs).toBe(1);
  expect(sent.quote.at(-1)).toMatchObject({ mode: 'voice' });
  expect(c.get()).toBe('3/0');
});

for (const [name, width] of [['3 · стемы из ленты', 1440], ['7 · телефон 360', 360]] as const) {
  for (const theme of ['light', 'dark'] as const) {
    test(`сценарий ${name} (${theme}): 3 клика, 0 переходов`, async ({ page }) => {
      const sent = await fixtures(page);
      const sid = await chat(page, FILES.song);
      await open(page, sid, width, theme);
      const mobile = width < 500;
      if (!mobile) await stripMode(page, 'Музыка').click();
      const c = counter();
      // Звук не выбран: «Обработка» приглушена и спрашивает, что обработать
      await expect(stripMode(page, 'Обработка')).toHaveAttribute('title', 'Выбрать звук этого чата для обработки');
      await c.click('b', stripMode(page, 'Обработка'));
      await expect(menu(page)).toBeVisible();
      const rows = page.getByRole('button', { name: /ёжик-v\d\.mp3/ });
      await expect(rows).toHaveCount(2);
      // Свежие сверху; меню — над полосой, на телефоне во всю ширину
      await expect(rows.first()).toContainText('ёжик-v3.mp3');
      const card = page.locator('div', { has: menu(page) }).filter({ has: page.getByText('Склеить несколько…') }).last();
      const box = (await card.boundingBox())!;
      const bar = (await strip(page).boundingBox())!;
      expect(box.y + box.height).toBeLessThanOrEqual(bar.y + 1);
      if (mobile) {
        expect(box.width).toBeGreaterThanOrEqual(width - 17);
        for (const row of await page.getByRole('button', { name: /ёжик-v\d\.mp3|Склеить несколько/ }).all()) {
          expect((await row.boundingBox())!.height).toBeGreaterThanOrEqual(40);
        }
      }
      await shot(page, `menu-${width}-${theme}`);
      await c.click('b', rows.first());
      await expect(strip(page).locator('[data-sound-chip="focus"]')).toContainText('ёжик-v3.mp3');
      await pressed(stripMode(page, 'Обработка'));
      // Панель была закрыта и закрытой осталась: выбор в меню её не открывает
      await expect(page.locator('[data-sound-settings]')).toHaveCount(0);
      await page.locator('[data-composer-input] textarea').fill('вокал');
      await shot(page, `picked-${width}-${theme}`);
      await c.click('b', send(page));
      await expect.poll(() => sent.jobs).toBe(1);
      expect(sent.quote.at(-1)).toMatchObject({ mode: 'process' });
      expect(c.get()).toBe('3/0');
    });
  }
}

test('сценарий 5 · склеить три реплики: 9 кликов, 1 переход', async ({ page }) => {
  const sent = await fixtures(page);
  const sid = await chat(page, FILES.lines);
  await open(page, sid, 1440);
  const c = counter();
  await c.click('b', stripMode(page, 'Обработка'));
  await c.click('b', page.getByRole('button', { name: /Склеить несколько/ }));
  const p = page.locator('[data-sound-settings]');
  await expect(p).toBeVisible();
  await expect(p.locator('[data-opt="op:concat"]')).toHaveAttribute('data-on', 'true');
  // Выбор звука не нужен: чипа «Работаем с» нет
  await expect(strip(page).locator('[data-sound-chip="focus"]')).toHaveCount(0);
  const add = p.locator('select[title="Звук этого чата"]');
  for (const n of [1, 2, 3]) await c.select('p', add, `реплика-${n}.mp3 · исходник`);
  await expect(p.locator('[data-piece]')).toHaveCount(3);
  // Запуск в низу панели; первая «Склеить» — сама операция в списке
  await c.click('p', page.getByRole('button', { name: 'Склеить', exact: true }).last());
  await expect.poll(() => sent.concat.length).toBe(1);
  expect(c.get()).toBe('9/1');
});

for (const width of [1440, 360]) {
  for (const theme of ['light', 'dark'] as const) {
    test(`снять выбор в «Обработке» ${width} (${theme}): режим создания и «Вернуть»`, async ({ page }) => {
      await fixtures(page);
      const sid = await chat(page, FILES.song);
      await open(page, sid, width, theme);
      await stripMode(page, 'Музыка').click();
      await stripMode(page, 'Обработка').click();
      await page.getByRole('button', { name: /ёжик-v3\.mp3/ }).click();
      await pressed(stripMode(page, 'Обработка'));
      const c = counter();
      await c.click('b', strip(page).locator('[data-sound-chip="focus"]').getByText('×', { exact: true }));
      const notice = page.locator('[data-sound-release]');
      await expect(notice).toContainText('Звук снят — вернулись к «Музыке»');
      await pressed(stripMode(page, 'Музыка'));
      // «Обработка» на месте и снова будет спрашивать
      await expect(stripMode(page, 'Обработка')).toHaveAttribute('title', 'Выбрать звук этого чата для обработки');
      await shot(page, `release-${width}-${theme}`);
      expect(c.get()).toBe('1/0');
      await notice.getByRole('button', { name: 'Вернуть' }).click();
      await expect(strip(page).locator('[data-sound-chip="focus"]')).toContainText('ёжик-v3.mp3');
      await pressed(stripMode(page, 'Обработка'));
      await expect(notice).toHaveCount(0);
    });
  }
}
