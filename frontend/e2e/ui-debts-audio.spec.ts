import { test, expect, type Locator, type Page } from '@playwright/test';
import { execFileSync } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';

// Долги после релиза 3 (тач-цели звука и каркаса шторки) — копия каркаса audio-panel-a.spec.ts.
// Панель «Звук», вариант А (план «Звук», шаг K3; макет audio-panel-v3-proposal.md): три сценария
// v3 — озвучка, песня, стемы над выбранным звуком. Операция одним списком с группами, «Чем» со
// списком «Исполнитель», «Ещё настройки», «Что получить» в стемах. На шторке 360 всё до строки «Чем»
// включительно — в первом экране, без прокрутки. Стенд — на ВРЕМЕННОЙ data, каталог — фикстурой.
//
//   AE_PROJECT_ROOT=/tmp/k3s/proj PLAYWRIGHT_BASE_URL=http://127.0.0.1:5000 \
//     AE_SHOTS_DIR=../.cc-attachments/audio-k3 npx playwright test e2e/audio-panel-a.spec.ts

const USER = process.env.E2E_USER || 'admin';
const PASS = process.env.E2E_PASS || '12345';
const ROOT = process.env.AE_PROJECT_ROOT || '';
const SHOTS = process.env.AE_SHOTS_DIR || '';
const FILE = 'music/песня-2.mp3';

let token = '';
let projectId = '';

test.use({ serviceWorkers: 'block' });

const caps = (ops: string[], extra: Record<string, unknown> = {}) => ({
  ops, languages: ['ru', 'en'], voiceKinds: ['preset'], producesFiles: ['audio'], license: { label: 'Apache-2.0', kind: 'permissive' },
  priceUnit: 'free', maxTextChars: 5000, minDurationSec: 10, maxDurationSec: 240, ...extra,
});
const stems = (set: string) => caps(['separate'], { languages: [], languageNeutral: true, stemSet: set, license: { label: 'MIT', kind: 'permissive' } });
const CATALOG = {
  autoModelId: 'auto', maxCount: 4, autoProviders: ['local', 'fal'],
  providers: [
    {
      key: 'local', label: 'Локальные модели', priceUnit: 'free', available: true, reason: null,
      models: [
        { id: 'qwen3-tts', label: 'Qwen3-TTS', caps: caps(['speak', 'designVoice', 'cloneVoice']) },
        { id: 'moss-tts', label: 'MOSS-TTS v1.5', caps: caps(['speak', 'cloneVoice']) },
        { id: 'ace-step-1.5-xl', label: 'ACE-Step 1.5 XL', caps: caps(['song']) },
        { id: 'yue2-3b', label: 'YuE2-3B', caps: caps(['song'], { license: { label: 'CC BY-NC 4.0', kind: 'nonCommercial' } }) },
        { id: 'bs-roformer', label: 'BS-RoFormer', caps: stems('vocals') },
        { id: 'htdemucs-ft-4stems', label: 'HTDemucs · 4 стема', caps: stems('4') },
        { id: 'htdemucs-6stems', label: 'HTDemucs · 6 стемов', caps: stems('6') },
        { id: 'mel-roformer-karaoke', label: 'Караоке', caps: stems('karaoke') },
      ],
    },
    {
      key: 'fal', label: 'fal', priceUnit: 'usd', available: true, reason: null,
      models: [
        { id: 'fal-ai/minimax/speech-2.6-hd', label: 'MiniMax Speech 2.6', caps: caps(['speak'], { priceUnit: 'chars' }), priceHint: { amount: 0.0001, unit: 'chars', per: 'char' } },
        { id: 'fal-ai/elevenlabs/music', label: 'ElevenLabs Music v2.5', caps: caps(['song'], { languages: [], priceUnit: 'min' }), priceHint: { amount: 0.6, unit: 'min', per: 'min' } },
        { id: 'fal-ai/demucs', label: 'Demucs · стемы', caps: { ...stems('6'), priceUnit: 'sec' }, priceHint: { amount: 0.0007, unit: 'sec', per: 'sec' } },
      ],
    },
    { key: 'yandex', label: 'Яндекс', priceUnit: 'rub', available: false, reason: 'Ключ SpeechKit не задан', models: [
      { id: 'speechkit', label: 'SpeechKit', caps: caps(['speak'], { priceUnit: 'chars' }), priceHint: { amount: 0.0002, unit: 'chars', per: 'char' } },
    ] },
  ],
};

const auth = () => ({ Authorization: `Bearer ${token}` });

test.beforeAll(async ({ playwright, baseURL }) => {
  expect(ROOT, 'нужен AE_PROJECT_ROOT стенда на временной data').toBeTruthy();
  const request = await playwright.request.newContext({ baseURL });
  const r = await request.post('/api/auth/login', { data: { username: USER, password: PASS } });
  expect(r.ok(), 'логин должен пройти').toBeTruthy();
  token = (await r.json()).token as string;
  await request.put('/api/feature-flags/audio-editor', { headers: auth(), data: { enabled: true } });
  const out = path.join(ROOT, FILE);
  if (!fs.existsSync(out)) {
    fs.mkdirSync(path.dirname(out), { recursive: true });
    execFileSync('ffmpeg', ['-loglevel', 'error', '-y', '-f', 'lavfi', '-i', 'sine=frequency=330:duration=2', out]);
  }
  let projects = (await (await request.get('/api/projects', { headers: auth() })).json()) as { id: string; rootPath: string }[];
  if (!projects.some(p => p.rootPath === ROOT)) {
    await request.post('/api/projects', { headers: auth(), data: { name: 'Звук', rootPath: ROOT } });
    projects = (await (await request.get('/api/projects', { headers: auth() })).json()) as { id: string; rootPath: string }[];
  }
  projectId = projects.find(p => p.rootPath === ROOT)!.id;
  await request.dispose();
});

async function fixtures(page: Page) {
  await page.route('**/audio-editor/**/catalog', r => r.fulfill({ json: CATALOG }));
  await page.route('**/audio-editor/**/state', async r => {
    const res = await r.fetch();
    await r.fulfill({ response: res, json: { ...(await res.json()), catalog: CATALOG } });
  });
  await page.route('**/audio-editor/**/quote', async r => {
    const body = r.request().postDataJSON() as Record<string, unknown>;
    await r.fulfill({ json: {
      quoteId: 'q1', mode: body.mode, op: body.operation, provider: 'local', model: 'auto', count: 1, voiceKind: null,
      price: { amount: 0, unit: 'free', approx: false, source: 'catalog', eta: 40, queueLength: 0 }, license: 'MIT', heavy: false,
      expiresAt: new Date(Date.now() + 600_000).toISOString(),
    } });
  });
}

const chatBase = (sid: string) => `/api/projects/${projectId}/audio-editor/sessions/${sid}`;

// Чат со звуком в работе (withFile) или без него; префы режимов — с чистого листа
async function chat(page: Page, mode: 'voice' | 'music' | 'process', withFile: boolean): Promise<string> {
  const req = page.request;
  const sid = (await (await req.post(`/api/projects/${projectId}/sessions`, { headers: auth(), data: { name: `Панель А ${Date.now()}` } })).json()).id as string;
  const empty = { operation: null, provider: null, model: null, count: null, fields: null, inputs: null };
  for (const m of ['voice', 'music', 'process']) await req.put(`/api/projects/${projectId}/audio-editor/prefs/${m}`, { headers: auth(), data: empty });
  if (withFile) {
    const opened = await req.post(`${chatBase(sid)}/threads`, { headers: auth(), data: { file: FILE, mode, revision: 0 } });
    expect(opened.ok(), `нить должна открыться: ${await opened.text()}`).toBeTruthy();
  }
  return sid;
}

async function openStrip(page: Page, sid: string, width: number, theme: 'light' | 'dark', collapsed = false) {
  const mobile = width < 500;
  await page.setViewportSize({ width, height: mobile ? 780 : 900 });
  await page.addInitScript(([tk, s, th, c]) => {
    localStorage.setItem('cc_token', tk);
    localStorage.setItem('theme-mode', th);
    localStorage.setItem(`cc-composer-strip:${s}`, 'sound');
    localStorage.setItem(`cc-composer-strip-collapsed:${s}:sound`, c);
  }, [token, sid, theme, collapsed ? '1' : '0'] as const);
  await page.goto(`/#/project/${projectId}/chat/${sid}`);
  await expect(page.locator(`[data-sound-strip="${collapsed ? 'mini' : 'full'}"]`)).toBeVisible({ timeout: 30_000 });
  const close = page.locator('[data-cc-src*="NotificationToasts"] [title="Закрыть"]');
  for (let i = 0; i < 5 && await close.count(); i++) await close.first().click().catch(() => {});
}

async function openPanel(page: Page, sid: string, width: number, theme: 'light' | 'dark') {
  await openStrip(page, sid, width, theme);
  await page.locator('[data-sound-settings-toggle] button').click();
  const p = page.locator('[data-sound-settings]');
  await expect(p).toBeVisible();
  return p;
}

const shot = async (page: Page, name: string) => {
  if (!SHOTS) return;
  fs.mkdirSync(SHOTS, { recursive: true });
  await page.screenshot({ path: path.join(SHOTS, `${name}.png`), fullPage: false });
};

// Низ элемента не ниже видимой части прокручиваемого тела панели (прокрутка — в самом начале)
async function inFirstScreen(el: Locator) {
  const gap = await el.evaluate(node => {
    let box: HTMLElement | null = node.parentElement;
    while (box && !/(auto|scroll)/.test(getComputedStyle(box).overflowY)) box = box.parentElement;
    if (!box) return 0;
    box.scrollTop = 0;
    return box.getBoundingClientRect().bottom - node.getBoundingClientRect().bottom;
  });
  expect(gap, 'строка «Чем» видна без прокрутки').toBeGreaterThanOrEqual(0);
}

// Раскрытый элемент целиком в видимой части прокручиваемого тела панели — без ручной прокрутки
async function fullyInView(el: Locator) {
  await expect.poll(() => el.evaluate(node => {
    let box: HTMLElement | null = node.parentElement;
    while (box && !/(auto|scroll)/.test(getComputedStyle(box).overflowY)) box = box.parentElement;
    const r = node.getBoundingClientRect();
    const b = box ? box.getBoundingClientRect() : { top: 0, bottom: innerHeight };
    return r.top >= b.top - 0.5 && r.bottom <= b.bottom + 0.5;
  }), 'список «Исполнитель» виден целиком сразу после раскрытия').toBe(true);
}

const op = (p: Locator) => p.locator('[data-sound-op] select');
const executorRow = (p: Locator) => p.locator('[data-sound-executor] button[aria-expanded]');


// Тач-цели на 360: каждая видимая цель из списка — не ниже 40 по высоте
const MIN = 40;
async function heights(scope: Locator, names: (string | RegExp)[]) {
  const out: Record<string, number> = {};
  for (const n of names) {
    const el = scope.getByRole('button', { name: n }).first();
    if (!(await el.count())) continue;
    await el.scrollIntoViewIfNeeded();
    out[String(n)] = (await el.boundingBox())!.height;
  }
  return out;
}

for (const theme of ['light', 'dark'] as const) {
  test(`360 ${theme}: поля звука — тач-цели не ниже 40`, async ({ page }) => {
    await fixtures(page);
    const p = await openPanel(page, await chat(page, 'voice', false), 360, theme);
    // Голос: «Готовый диктор» — карточка-опция операции
    for (const n of ['Готовый диктор', 'По описанию', 'Образец']) {
      const b = p.getByRole('button', { name: n, exact: true });
      await expect(b, n).toBeVisible();
      expect((await b.boundingBox())!.height, n).toBeGreaterThanOrEqual(MIN);
    }
    const voice = await heights(p, ['В текст →', /Выбрать в «Голосах»/]);
    expect(voice['В текст →'], 'В текст →').toBeGreaterThanOrEqual(MIN);
    expect(voice[String(/Выбрать в «Голосах»/)], 'Выбрать в «Голосах»').toBeGreaterThanOrEqual(MIN);
    await p.getByRole('button', { name: /^Ещё настройки/ }).click();
    const reset = await heights(p, ['Сбросить']);
    expect(reset['Сбросить'], 'Сбросить').toBeGreaterThanOrEqual(MIN);
    await shot(page, `fields-voice-360-${theme}`);

    await p.getByRole('button', { name: 'Музыка', exact: true }).click();
    await expect(op(p)).toHaveValue('song');
    const lyrics = p.locator('[data-opt^="lyrics:"]');
    await expect(lyrics.first()).toBeVisible();
    for (const o of await lyrics.all()) expect((await o.boundingBox())!.height, 'Со словами / Инструментал').toBeGreaterThanOrEqual(MIN);
    const sec = await heights(p, ['[Verse]']);
    expect(sec['[Verse]'], '[Verse]').toBeGreaterThanOrEqual(MIN);
    await shot(page, `fields-music-360-${theme}`);
  });

  test(`360 ${theme}: каркас шторки — кнопки ≥ 40, подпись очереди не режется`, async ({ page }) => {
    await fixtures(page);
    const p = await openPanel(page, await chat(page, 'voice', false), 360, theme);
    const panel = page;
    for (const t of ['Меньше', 'Больше', 'Опустить до цены — лента станет доступна', 'Закрыть панель — сводка останется в полосе']) {
      const b = panel.locator(`button[title="${t}"], button[aria-label="${t}"]`).first();
      await expect(b, t).toBeVisible();
      const box = (await b.boundingBox())!;
      expect(box.height, t).toBeGreaterThanOrEqual(MIN);
      expect(box.width, t).toBeGreaterThanOrEqual(MIN);
    }
    const run = panel.locator('button').filter({ hasText: /Озвучить|Сгенерировать|Запустить|Создать/ }).last();
    expect((await run.boundingBox())!.height, 'кнопка запуска').toBeGreaterThanOrEqual(MIN);
    // Котировка пришла: вторая строка цены — «~40 с · очередь GPU: 0» (с ней строка и резалась)
    const queue = panel.locator('span', { hasText: /~40 с · очередь GPU: 0/ }).last();
    await expect(queue).toBeVisible();
    const cut = await queue.evaluate(n => { const e = n as HTMLElement; return e.scrollWidth > e.clientWidth + 1; });
    expect(cut, 'подпись очереди не обрезана многоточием').toBe(false);
    await shot(page, `sheet-360-${theme}`);
  });
}

test('1440: каркас колонки не раздут — кнопки остались плотными', async ({ page }) => {
  await fixtures(page);
  const p = await openPanel(page, await chat(page, 'voice', false), 1440, 'light');
  void p;
  const b = page.locator('button[title="Меньше"]').first();
  expect((await b.boundingBox())!.height).toBeLessThan(MIN);
});

const raise = (page: Page) => page.evaluate(() => document.documentElement.style.getPropertyValue('--cc-fab-raise'));

// Escape закрывает меню «Что обработать?» и возвращает фокус на «Обработку»
for (const theme of ['light', 'dark'] as const) {
  test(`1440 ${theme}: Escape закрывает меню «Что обработать?», фокус — на якорь`, async ({ page }) => {
    await fixtures(page);
    await openStrip(page, await chat(page, 'voice', false), 1440, theme);
    const anchor = page.locator('[data-sound-mode-switch]').getByRole('button', { name: 'Обработка' });
    await anchor.focus();
    await anchor.click();
    const menu = page.getByText('Что обработать?');
    await expect(menu).toBeVisible();
    await page.keyboard.press('Escape');
    await expect(menu).toHaveCount(0);
    await expect.poll(() => page.evaluate(() => !!document.activeElement?.closest('[data-sound-mode-switch]'))).toBe(true);
    await shot(page, `menu-escape-1440-${theme}`);
  });

  // Круг AI поднимается над «Вернуть» звука (как у картинок) и садится обратно
  test(`360 ${theme}: плашка «Вернуть» звука поднимает круг AI`, async ({ page }) => {
    await fixtures(page);
    const sid = await chat(page, 'process', true);
    await openStrip(page, sid, 360, theme);
    expect(await raise(page)).toBe('');
    await page.locator('[data-sound-chip="focus"]').getByText('×', { exact: true }).click();
    const notice = page.locator('[data-sound-release]');
    await expect(notice).toBeVisible();
    await expect.poll(async () => parseFloat(await raise(page) || '0')).toBeGreaterThan(20);
    await shot(page, `sound-release-fab-360-${theme}`);
    await notice.getByRole('button', { name: 'Вернуть' }).click();
    await expect.poll(() => raise(page)).toBe('');
  });
}

// Две плашки с подъёмом одновременно: уход одной не сбрасывает подъём, пока видна вторая
// Витрина кита живёт только в dev-сборке: KIT_URL — адрес vite dev (npm run dev), без бэкенда
const KIT = process.env.KIT_URL || '';
test('360: две плашки «Вернуть» — подъём круга AI живёт, пока видна любая', async ({ page }) => {
  test.skip(!KIT, 'нужен KIT_URL — vite dev со страницей #/ui-kit');
  await page.setViewportSize({ width: 360, height: 780 });
  await page.goto(`${KIT}/#/ui-kit`);
  const block = page.locator('text=Общий слой панелей генерации').first();
  await expect(block).toBeVisible({ timeout: 30_000 });
  await page.getByRole('button', { name: 'Править' }).first().click();
  await page.getByRole('button', { name: /hero\.png/ }).first().click();
  await page.locator('[title="Снять выбор (человек)"]').click();
  const live = page.getByText('Картинка снята — дальше рисуем новую');
  await expect(live).toBeVisible();
  await page.getByText('вторая плашка с подъёмом круга AI').locator('..').getByRole('switch').click();
  await expect(page.locator('[data-kit-second-notice]')).toBeVisible();
  await expect.poll(async () => parseFloat(await raise(page) || '0')).toBeGreaterThan(20);
  await shot(page, 'two-notices-360');
  // Живая плашка уходит сама через 4 с — вторая остаётся и держит подъём
  await expect(live).toHaveCount(0, { timeout: 8000 });
  expect(parseFloat(await raise(page) || '0')).toBeGreaterThan(20);
  await page.getByText('вторая плашка с подъёмом круга AI').locator('..').getByRole('switch').click();
  await expect.poll(() => raise(page)).toBe('');
});
