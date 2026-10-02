import { test, expect, type Locator, type Page } from '@playwright/test';
import { execFileSync } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';

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

for (const width of [1440, 360]) {
  for (const theme of ['light', 'dark'] as const) {
    test(`${width} ${theme}: озвучка — «Новый звук», «Чем: Авто · локально», список «Исполнитель»`, async ({ page }) => {
      await fixtures(page);
      const p = await openPanel(page, await chat(page, 'voice', false), width, theme);
      await expect(op(p)).toHaveValue('speak');
      // Без звука группа «С выбранным звуком» серая с подсказкой
      await expect(op(p).locator('optgroup[label="С выбранным звуком · выберите звук в ленте"]')).toHaveAttribute('disabled', '');
      await expect(executorRow(p)).toContainText('Авто · локально · Qwen3-TTS');
      await expect(p.getByRole('button', { name: /^Ещё настройки/ })).toContainText('всё по умолчанию');
      await shot(page, `speak-${width}-${theme}`);
      if (width < 500) await inFirstScreen(executorRow(p));

      await executorRow(p).click();
      const list = p.getByRole('radiogroup', { name: 'Исполнитель' });
      await expect(list.getByRole('group', { name: 'Бесплатно на своей видеокарте' })).toBeVisible();
      await fullyInView(list);
      await expect(list.getByRole('radio').filter({ hasText: 'SpeechKit' })).toBeDisabled();
      await shot(page, `speak-executor-${width}-${theme}`);
      await list.getByRole('radio').filter({ hasText: 'MiniMax Speech 2.6' }).click();
      await expect(executorRow(p)).toContainText('fal · MiniMax Speech 2.6');
    });

    test(`${width} ${theme}: песня — лицензия YuE2 под «Чем»`, async ({ page }) => {
      await fixtures(page);
      const p = await openPanel(page, await chat(page, 'music', false), width, theme);
      await p.getByRole('button', { name: 'Музыка', exact: true }).click();
      await expect(op(p)).toHaveValue('song');
      await expect(executorRow(p)).toContainText('Авто · локально · ACE-Step 1.5 XL');
      await shot(page, `song-${width}-${theme}`);
      if (width < 500) await inFirstScreen(executorRow(p));
      await executorRow(p).click();
      await p.getByRole('radio').filter({ hasText: 'YuE2-3B' }).click();
      await expect(p.locator('[data-sound-license]')).toContainText('CC BY-NC 4.0');
    });

    test(`${width} ${theme}: стемы над выбранным звуком — «Что получить» подставляет модель`, async ({ page }) => {
      await fixtures(page);
      const p = await openPanel(page, await chat(page, 'process', true), width, theme);
      await p.getByRole('button', { name: 'Обработка', exact: true }).click();
      await expect(op(p)).toHaveValue('separate');
      await expect(op(p).locator('option[value="separate"]')).toHaveText('Стемы · над выбранным звуком');
      const seg = p.locator('[data-field="stems"]');
      await expect(executorRow(p)).toContainText('Авто · локально · BS-RoFormer');
      await shot(page, `stems-${width}-${theme}`);
      if (width < 500) await inFirstScreen(executorRow(p));
      await seg.getByRole('button', { name: '4', exact: true }).click();
      await expect(executorRow(p)).toContainText('HTDemucs · 4 стема · локально');
      // У fal только 6 дорожек: остальное серое с причиной
      await executorRow(p).click();
      await p.getByRole('radio').filter({ hasText: 'Demucs · стемы' }).filter({ hasText: 'fal' }).click();
      await expect(seg.getByRole('button', { name: 'Караоке', exact: true })).toBeDisabled();
      await expect(seg.getByRole('button', { name: 'Караоке', exact: true })).toHaveAttribute('title', 'У «fal» нет: караоке');
      await shot(page, `stems-fal-${width}-${theme}`);
    });
  }
}

// Дизайн-проверка Майи на 360: имя звука в чипе читается (≥ 96 px видимой ширины), «▴»
// и переключатель полос — тач-цели 40×40 внутри рамки полосы, сегменты по 40, строка «Ещё настройки» не ниже 40
type Rect = { x: number; y: number; width: number; height: number };
const rect = (l: Locator) => l.evaluate(n => { const r = n.getBoundingClientRect(); return { x: r.x, y: r.y, width: r.width, height: r.height }; }) as Promise<Rect>;
const inside = (a: Rect, b: Rect) => a.x >= b.x - 0.5 && a.x + a.width <= b.x + b.width + 0.5 && a.y >= b.y - 0.5 && a.y + a.height <= b.y + b.height + 0.5;

async function stripGeometry(page: Page) {
  const strip = await rect(page.locator('[data-sound-strip="full"]'));
  const toggle = await rect(page.locator('[data-sound-settings-toggle] button'));
  expect(toggle.width, '«▴» шириной 40').toBeGreaterThanOrEqual(40);
  expect(toggle.height, '«▴» высотой 40').toBeGreaterThanOrEqual(40);
  expect(inside(toggle, strip), `«▴» внутри полосы: ${JSON.stringify({ toggle, strip })}`).toBeTruthy();
  // Переключатель полос (общий хост полос над полем ввода) — тоже тач-цель 40×40 внутри полосы
  const sw = await rect(page.locator('[data-sound-strip="full"] [data-composer-strip-switcher] button'));
  expect(sw.width, 'переключатель полос шириной 40').toBeGreaterThanOrEqual(40);
  expect(sw.height, 'переключатель полос высотой 40').toBeGreaterThanOrEqual(40);
  expect(inside(sw, strip), `переключатель полос внутри полосы: ${JSON.stringify({ sw, strip })}`).toBeTruthy();
  const segs = page.locator('[data-sound-mode-switch] button');
  await expect(segs).toHaveCount(3);
  for (let i = 0; i < 3; i++) {
    const s = await rect(segs.nth(i));
    expect(s.width, 'сегмент шириной 40').toBeGreaterThanOrEqual(40);
    expect(s.height, 'сегмент высотой 40').toBeGreaterThanOrEqual(40);
    expect(inside(s, strip), 'сегмент внутри полосы').toBeTruthy();
  }
  return strip;
}

for (const theme of ['light', 'dark'] as const) {
  test(`360 ${theme}: полоса без звука — «▴» 40×40 внутри полосы`, async ({ page }) => {
    await fixtures(page);
    await openStrip(page, await chat(page, 'voice', false), 360, theme);
    await expect(page.locator('[data-sound-chip="new"]')).toBeVisible({ timeout: 30_000 });
    const strip = await stripGeometry(page);
    expect(inside(await rect(page.locator('[data-sound-chip="new"]')), strip), 'чип «Новый звук» внутри полосы').toBeTruthy();
    await shot(page, `strip-new-360-${theme}`);
  });

  test(`360 ${theme}: выбранный звук — имя в чипе читается, «Ещё настройки» не ниже 40`, async ({ page }) => {
    await fixtures(page);
    const sid = await chat(page, 'process', true);
    await openStrip(page, sid, 360, theme);
    await expect(page.locator('[data-sound-chip="focus"]')).toBeVisible({ timeout: 30_000 });
    const strip = await stripGeometry(page);
    const name = page.locator('[data-sound-chip="focus"] b');
    await expect(name).toContainText('песня-2.mp3');
    // Видимая часть имени: <b> внутри обрезки многоточием, считаем по рамке обрезки
    const visible = await name.evaluate(n => {
      const b = n.getBoundingClientRect();
      const clip = n.parentElement!.getBoundingClientRect();
      return Math.min(b.right, clip.right) - b.left;
    });
    expect(visible, 'видимая ширина имени в чипе').toBeGreaterThanOrEqual(96);
    expect(inside(await rect(page.locator('[data-sound-chip="focus"]')), strip), 'чип внутри полосы').toBeTruthy();
    await shot(page, `strip-picked-360-${theme}`);

    await page.locator('[data-sound-settings-toggle] button').click();
    const p = page.locator('[data-sound-settings]');
    await expect(p).toBeVisible();
    const adv = p.getByRole('button', { name: /^Ещё настройки/ });
    await expect(adv).toBeVisible();
    expect((await rect(adv)).height, 'строка «Ещё настройки» — тач-цель').toBeGreaterThanOrEqual(40);
    await shot(page, `panel-advanced-360-${theme}`);
  });

  // Свёрнутая строка на 360: сводка открывает панель, а развернуть полосу можно «⌄» — тач-цель 40×40
  test(`360 ${theme}: свёрнутая полоса — «⌄» 40×40, сводка по-прежнему открывает панель`, async ({ page }) => {
    await fixtures(page);
    await openStrip(page, await chat(page, 'process', true), 360, theme, true);
    const mini = page.locator('[data-sound-strip="mini"]');
    const expand = mini.locator('[data-sound-mini-expand]');
    const e = await rect(expand);
    expect(e.width, '«⌄» шириной 40').toBeGreaterThanOrEqual(40);
    expect(e.height, '«⌄» высотой 40').toBeGreaterThanOrEqual(40);
    expect(inside(e, await rect(mini)), `«⌄» внутри строки: ${JSON.stringify(e)}`).toBeTruthy();
    // Сводке остаётся место — она по-прежнему читается
    expect((await rect(mini.locator('[data-sound-summary]'))).width, 'ширина сводки').toBeGreaterThanOrEqual(96);
    await shot(page, `strip-collapsed-360-${theme}`);
    await expand.click();
    await expect(page.locator('[data-sound-strip="full"]')).toBeVisible();

    await page.reload();
    await expect(mini).toBeVisible({ timeout: 30_000 });
    await mini.locator('[data-sound-summary]').click();
    await expect(page.locator('[data-sound-settings]')).toBeVisible();
    await expect(page.locator('[data-sound-strip="full"]')).toHaveCount(0);
  });
}
