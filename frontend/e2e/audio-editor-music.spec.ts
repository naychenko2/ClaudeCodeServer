import { test, expect, type Page } from '@playwright/test';
import { execFileSync } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';

// Режим «Музыка» панели «Звук» и связь «Кусок ⇄ волна» (шаг 2.9б): «Перегенерировать кусок» из
// карточки открывает панель на нужной операции, выделение на волне и поле «Кусок» — одно значение,
// поля песни/кавера/дорожек по модели, лицензия YuE2 и «тяжёлая» операция — до запуска. Стенд —
// на ВРЕМЕННОЙ data; генерации на стенде нет, поэтому каталог поставщиков и котировка — фикстурой
// (по образцу AudioCatalog бэкенда), схема параметров модели — настоящая.
//
//   AE_PROJECT_ROOT=/tmp/music-stand/proj PLAYWRIGHT_BASE_URL=http://127.0.0.1:5097 \
//     AE_SHOTS_DIR=../.cc-attachments/audio-music npx playwright test e2e/audio-editor-music.spec.ts

const USER = process.env.E2E_USER || 'admin';
const PASS = process.env.E2E_PASS || '12345';
const ROOT = process.env.AE_PROJECT_ROOT || '';
const SHOTS = process.env.AE_SHOTS_DIR || '';
const FILE = 'music/song.mp3';

let token = '';
let projectId = '';

const caps = (ops: string[], extra: Record<string, unknown> = {}) => ({
  ops, languages: ['ru', 'en'], voiceKinds: [], producesFiles: ['audio'], license: { label: 'MIT', kind: 'permissive' },
  priceUnit: 'free', maxTextChars: 5000, minDurationSec: 10, maxDurationSec: 240, ...extra,
});
const CATALOG = {
  autoModelId: 'auto', maxCount: 4,
  providers: [
    {
      key: 'local', label: 'Локально', priceUnit: 'free', available: true, reason: null,
      models: [
        { id: 'ace-step-1.5-xl', label: 'ACE-Step 1.5 XL', caps: caps(['song', 'cover', 'repaint', 'extract', 'lego', 'complete'], { languages: ['ru', 'en', 'de', 'ja'], heavyOps: ['extract', 'lego', 'complete'] }) },
        { id: 'yue2-3b', label: 'YuE2-3B', caps: caps(['song', 'cover'], { license: { label: 'CC BY-NC 4.0', kind: 'nonCommercial' }, producesFiles: ['audio', 'score'] }) },
        { id: 'minimax-music-3', label: 'MiniMax Music 3', caps: caps(['song'], { license: { label: 'лицензия не указана', kind: 'unknown' } }) },
      ],
    },
    {
      key: 'fal', label: 'fal', priceUnit: 'usd', available: true, reason: null,
      models: [{ id: 'fal-ai/elevenlabs/music', label: 'ElevenLabs Music v2.5', caps: caps(['song'], { languages: [], priceUnit: 'min', minDurationSec: 3, maxDurationSec: 600 }), priceHint: { amount: 0.6, unit: 'min', per: 'min' } }],
    },
  ],
};

async function fixtures(page: Page) {
  await page.route('**/audio-editor/**/catalog', r => r.fulfill({ json: CATALOG }));
  // Каталог едет и в общем состоянии чата: нити — настоящие, подменяем только поставщиков
  await page.route('**/audio-editor/**/state', async r => {
    const res = await r.fetch();
    await r.fulfill({ response: res, json: { ...(await res.json()), catalog: CATALOG } });
  });
  await page.route('**/audio-editor/**/quote', async r => {
    const body = r.request().postDataJSON() as { mode: string; operation: string; model: string; provider: string };
    await r.fulfill({ json: {
      quoteId: 'q1', mode: body.mode, op: body.operation, provider: body.provider ?? 'local', model: body.model, count: 1, voiceKind: null,
      price: { amount: 0, unit: 'free', approx: false, source: 'catalog', eta: 90, queueLength: 1 }, license: 'MIT', heavy: false,
      expiresAt: new Date(Date.now() + 600_000).toISOString(),
    } });
  });
}

test.beforeAll(async ({ playwright, baseURL }) => {
  expect(ROOT, 'нужен AE_PROJECT_ROOT стенда на временной data').toBeTruthy();
  const request = await playwright.request.newContext({ baseURL });
  const r = await request.post('/api/auth/login', { data: { username: USER, password: PASS } });
  expect(r.ok(), 'логин должен пройти').toBeTruthy();
  token = (await r.json()).token as string;
  const headers = { Authorization: `Bearer ${token}` };
  await request.put('/api/feature-flags/audio-editor', { headers, data: { enabled: true } });
  const out = path.join(ROOT, FILE);
  if (!fs.existsSync(out)) {
    fs.mkdirSync(path.dirname(out), { recursive: true });
    execFileSync('ffmpeg', ['-loglevel', 'error', '-y', '-f', 'lavfi', '-i', 'sine=frequency=262:duration=12',
      '-af', "volume='0.2+0.8*abs(sin(t*1.3))':eval=frame", out]);
  }
  const projects = (await (await request.get('/api/projects', { headers })).json()) as { id: string; rootPath: string }[];
  projectId = projects.find(p => p.rootPath === ROOT)!.id;
  await request.dispose();
});

// Свежий чат с нитью песни в работе
async function chatWithSong(page: Page): Promise<string> {
  const headers = { Authorization: `Bearer ${token}` };
  const req = page.request;
  const sid = (await (await req.post(`/api/projects/${projectId}/sessions`, { headers, data: { name: `Музыка ${Date.now()}` } })).json()).id as string;
  const opened = await req.post(`/api/projects/${projectId}/audio-editor/sessions/${sid}/threads`, { headers, data: { file: FILE, mode: 'voice', revision: 0 } });
  expect(opened.ok(), `нить должна открыться: ${await opened.text()}`).toBeTruthy();
  return sid;
}

async function open(page: Page, sid: string, width: number, theme: 'light' | 'dark') {
  await page.setViewportSize({ width, height: width < 500 ? 780 : 900 });
  await page.addInitScript(([tk, th]) => {
    localStorage.setItem('cc_token', tk as string);
    localStorage.setItem('theme-mode', th as string);
  }, [token, theme]);
  await page.goto(`/#/project/${projectId}/chat/${sid}`);
}

async function closeToasts(page: Page) {
  const close = page.locator('[data-cc-src*="NotificationToasts"] [title="Закрыть"]');
  for (let i = 0; i < 5 && await close.count(); i++) await close.first().click().catch(() => {});
}

async function dragOn(page: Page, el: ReturnType<Page['locator']>, from: number, to: number) {
  await el.scrollIntoViewIfNeeded();
  const box = (await el.boundingBox())!;
  await page.mouse.move(box.x + box.width * from, box.y + box.height / 2);
  await page.mouse.down();
  await page.mouse.move(box.x + box.width * to, box.y + box.height / 2, { steps: 6 });
  await page.mouse.up();
}

const shot = async (page: Page, name: string) => {
  if (!SHOTS) return;
  fs.mkdirSync(SHOTS, { recursive: true });
  await page.screenshot({ path: path.join(SHOTS, `${name}.png`), fullPage: false });
};

const panel = (page: Page) => page.locator('[data-sound-settings]');

for (const [width, theme] of [[1280, 'light'], [1280, 'dark'], [360, 'light'], [360, 'dark']] as const) {
  test(`${width} px, ${theme}: «Перегенерировать кусок» из карточки, кусок ⇄ волна, поля музыки`, async ({ page }) => {
    await fixtures(page);
    const sid = await chatWithSong(page);
    await open(page, sid, width, theme);
    const card = page.locator('[data-audio-card]').first();
    await expect(card).toBeVisible({ timeout: 20_000 });
    await closeToasts(page);

    // Волна → кнопка под выделением → панель на «Музыка · Перегенерировать кусок»
    const wave = card.getByRole('slider', { name: /^Волна/ }).first();
    await expect.poll(async () => (await wave.getAttribute('aria-valuetext')) ?? '', { timeout: 20_000 }).toMatch(/из 0:12/);
    await dragOn(page, wave, 0.25, 0.5);
    await expect(card.getByText(/^Выделено /)).toBeVisible();
    await card.getByRole('button', { name: 'Перегенерировать кусок' }).click();
    const p = panel(page);
    await expect(p).toBeVisible({ timeout: 10_000 });
    await expect(p.locator('[data-opt="op:repaint"]')).toHaveAttribute('data-on', 'true');
    // Поле «Кусок» = выделение на волне; карточка знает, что связана с панелью
    const piece = p.locator('[data-field="piece"]');
    const start = piece.getByRole('textbox').first();
    await expect(start).toHaveValue(/^[23](\.\d)?$/);
    await expect(card.locator('[data-piece-linked]')).toHaveText('= «Кусок» в панели');
    await shot(page, `repaint-${width}-${theme}`);

    // Поле → волна: вписали начало и конец — выделение на волне сдвинулось
    await start.fill('1');
    await piece.getByRole('textbox').nth(1).fill('4');
    await expect(card.getByText(/^Выделено 0:01\.0 – 0:04\.0/)).toBeVisible();

    // Обрезка без ИИ — тот же кусок. На мобиле шторка закрывает ленту: опускаем её до цены, как человек
    const mobile = width < 500;
    if (mobile) await page.getByTitle('Опустить до цены — лента станет доступна').click();
    await card.getByRole('button', { name: 'Обрезать' }).click();
    if (mobile && await page.getByRole('button', { name: 'Поднять шторку' }).first().isVisible()) {
      await page.getByRole('button', { name: 'Поднять шторку' }).first().click();
    }
    await expect(p.locator('[data-opt="op:trim"]')).toHaveAttribute('data-on', 'true');
    await expect(p.locator('[data-field="piece"]').getByRole('textbox').first()).toHaveValue('1');

    // Песня: YuE2 — CC BY-NC видна до запуска, «Инструментал» серый с причиной
    await p.getByRole('button', { name: 'Музыка', exact: true }).click();
    await p.locator('[data-opt="op:song"] button').click();
    await p.locator('[data-opt="provider:local"] button').click();
    await p.locator('[data-opt="model:yue2-3b"] button').click();
    await expect(p.locator('[data-sound-license]')).toContainText('CC BY-NC 4.0: только некоммерческое');
    await expect(p.locator('[data-opt="lyrics:off"]')).toHaveAttribute('data-disabled', 'true');
    await expect(p.locator('[data-param="abc"]')).toBeVisible();
    await expect(page.getByText('YuE2 поёт по словам — напишите слова песни')).toBeVisible();
    await p.locator('[data-field="lyrics"] textarea').fill('Припев про море');
    await p.getByRole('button', { name: '[Chorus]' }).click();
    await expect(p.locator('[data-field="lyrics"] textarea')).toHaveValue('Припев про море\n\n[Chorus]\n');
    await shot(page, `song-yue2-${width}-${theme}`);

    // ACE: язык вокала, темп и тональность; «Вытащить дорожку» — тяжёлая, 12 дорожек
    await p.locator('[data-opt="model:ace-step-1.5-xl"] button').click();
    await expect(p.locator('[data-field="language"]')).toBeVisible();
    await expect(p.locator('[data-param="bpm"]')).toBeVisible();
    await expect(p.locator('[data-param="key"]')).toBeVisible();
    await p.locator('[data-opt="op:extract"] button').click();
    await p.locator('[data-opt="model:ace-step-1.5-xl"] button').click();
    await expect(p.locator('[data-sound-heavy]')).toContainText('одна за раз');
    await expect(p.locator('[data-field="track"] [data-opt^="track:"]')).toHaveCount(12);
    await p.locator('[data-opt="track:drums"] button').click();
    await expect(p.locator('[data-opt="track:drums"]')).toHaveAttribute('data-on', 'true');
    await shot(page, `extract-${width}-${theme}`);

    // Панель не шире экрана
    const box = (await p.boundingBox())!;
    expect(box.x + box.width).toBeLessThanOrEqual(width);
  });
}
