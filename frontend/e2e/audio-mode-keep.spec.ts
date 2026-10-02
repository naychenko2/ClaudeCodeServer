import { test, expect, type Page } from '@playwright/test';
import { execFileSync } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';

// Режимы панели «Звук» помнят свой выбор: Голос — local + Qwen, Музыка — fal + ElevenLabs,
// переключение туда-обратно (и перезагрузка) возвращает каждому режиму его поставщика и модель,
// префы ушедшего режима не затираются пустыми. Стенд — на ВРЕМЕННОЙ data, каталог — фикстурой.
//
//   AE_PROJECT_ROOT=/tmp/amk-stand/proj PLAYWRIGHT_BASE_URL=http://127.0.0.1:5096 \
//     AE_SHOTS_DIR=../.cc-attachments/audio-mode-keep npx playwright test e2e/audio-mode-keep.spec.ts

const USER = process.env.E2E_USER || 'admin';
const PASS = process.env.E2E_PASS || '12345';
const ROOT = process.env.AE_PROJECT_ROOT || '';
const SHOTS = process.env.AE_SHOTS_DIR || '';
const FILE = 'music/song.mp3';

let token = '';
let projectId = '';

const caps = (ops: string[], extra: Record<string, unknown> = {}) => ({
  ops, languages: ['ru', 'en'], voiceKinds: ['preset'], producesFiles: ['audio'], license: { label: 'MIT', kind: 'permissive' },
  priceUnit: 'free', maxTextChars: 5000, minDurationSec: 10, maxDurationSec: 240, ...extra,
});
const CATALOG = {
  autoModelId: 'auto', maxCount: 4,
  providers: [
    {
      key: 'local', label: 'Локально', priceUnit: 'free', available: true, reason: null,
      models: [
        { id: 'qwen3-tts', label: 'Qwen3-TTS', caps: caps(['speak', 'designVoice', 'cloneVoice']) },
        { id: 'moss-tts', label: 'MOSS-TTS', caps: caps(['speak', 'cloneVoice']) },
        { id: 'ace-step-1.5-xl', label: 'ACE-Step 1.5 XL', caps: caps(['song', 'cover', 'repaint']) },
        { id: 'demucs', label: 'HTDemucs', caps: caps(['separate'], { languages: [], languageNeutral: true }) },
      ],
    },
    {
      key: 'fal', label: 'fal', priceUnit: 'usd', available: true, reason: null,
      models: [
        { id: 'fal-ai/minimax/speech', label: 'MiniMax Speech', caps: caps(['speak'], { priceUnit: 'chars' }), priceHint: { amount: 0.0001, unit: 'chars', per: 'char' } },
        { id: 'fal-ai/elevenlabs/music', label: 'ElevenLabs Music v2.5', caps: caps(['song'], { languages: [], priceUnit: 'min' }), priceHint: { amount: 0.6, unit: 'min', per: 'min' } },
      ],
    },
  ],
};

async function fixtures(page: Page) {
  await page.route('**/audio-editor/**/catalog', r => r.fulfill({ json: CATALOG }));
  await page.route('**/audio-editor/**/state', async r => {
    const res = await r.fetch();
    await r.fulfill({ response: res, json: { ...(await res.json()), catalog: CATALOG } });
  });
  await page.route('**/audio-editor/**/quote', async r => {
    const body = r.request().postDataJSON() as { mode: string; operation: string; model: string; provider: string };
    await r.fulfill({ json: {
      quoteId: 'q1', mode: body.mode, op: body.operation, provider: body.provider ?? 'local', model: body.model, count: 1, voiceKind: null,
      price: { amount: 0, unit: 'free', approx: false, source: 'catalog', eta: 30, queueLength: 0 }, license: 'MIT', heavy: false,
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
    execFileSync('ffmpeg', ['-loglevel', 'error', '-y', '-f', 'lavfi', '-i', 'sine=frequency=262:duration=6', out]);
  }
  let projects = (await (await request.get('/api/projects', { headers })).json()) as { id: string; rootPath: string }[];
  if (!projects.some(p => p.rootPath === ROOT)) {
    await request.post('/api/projects', { headers, data: { name: 'Звук', rootPath: ROOT } });
    projects = (await (await request.get('/api/projects', { headers })).json()) as { id: string; rootPath: string }[];
  }
  projectId = projects.find(p => p.rootPath === ROOT)!.id;
  await request.dispose();
});

async function chatWithThread(page: Page): Promise<string> {
  const headers = { Authorization: `Bearer ${token}` };
  const req = page.request;
  const sid = (await (await req.post(`/api/projects/${projectId}/sessions`, { headers, data: { name: `Режимы ${Date.now()}` } })).json()).id as string;
  // Префы режимов — с чистого листа: иначе выбор прошлого прогона прошёл бы за «запомненный»
  const empty = { operation: null, provider: null, model: null, count: null, fields: null, inputs: null };
  for (const m of ['voice', 'music', 'process']) await req.put(`/api/projects/${projectId}/audio-editor/prefs/${m}`, { headers, data: empty });
  const opened = await req.post(`/api/projects/${projectId}/audio-editor/sessions/${sid}/threads`, { headers, data: { file: FILE, mode: 'voice', revision: 0 } });
  expect(opened.ok(), `нить должна открыться: ${await opened.text()}`).toBeTruthy();
  return sid;
}

async function open(page: Page, sid: string, width: number) {
  await page.setViewportSize({ width, height: width < 500 ? 780 : 900 });
  await page.addInitScript(tk => { localStorage.setItem('cc_token', tk as string); }, token);
  await page.goto(`/#/project/${projectId}/chat/${sid}`);
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

async function openPanel(page: Page, mobile: boolean) {
  const strip = page.locator('[data-composer-strip="sound"]');
  await expect(strip).toBeVisible({ timeout: 30_000 });
  await closeToasts(page);
  // На телефоне панель открывает сводка полосы: клик по заголовку там — меню смены полосы
  if (mobile) await page.locator('[data-sound-summary]').first().click();
  else {
    if (await strip.getAttribute('data-sound-strip') === 'mini') await strip.click({ position: { x: 12, y: 15 } });
    await page.locator('[data-sound-settings-toggle] button').click();
  }
  const p = page.locator('[data-sound-settings]');
  await expect(p).toBeVisible();
  return p;
}

const on = (p: ReturnType<Page['locator']>, opt: string) => expect(p.locator(`[data-opt="${opt}"]`)).toHaveAttribute('data-on', 'true');

for (const width of [1440, 360]) {
  test(`${width} px: Голос и Музыка помнят своего поставщика и модель`, async ({ page }) => {
    await fixtures(page);
    const sid = await chatWithThread(page);
    await open(page, sid, width);
    const p = await openPanel(page, width < 500);
    const mode = (name: string) => p.getByRole('button', { name, exact: true }).click();
    const pick = (opt: string) => p.locator(`[data-opt="${opt}"] button`).click();

    await mode('Голос');
    await pick('provider:local');
    await pick('model:moss-tts');
    await on(p, 'model:moss-tts');

    await mode('Музыка');
    await on(p, 'op:song');
    await pick('provider:fal');
    await pick('model:fal-ai/elevenlabs/music');
    await on(p, 'model:fal-ai/elevenlabs/music');
    await shot(page, `music-${width}`);

    await mode('Голос');
    await on(p, 'provider:local');
    await on(p, 'model:moss-tts');
    await shot(page, `voice-back-${width}`);

    await mode('Обработка');
    await mode('Музыка');
    await on(p, 'provider:fal');
    await on(p, 'model:fal-ai/elevenlabs/music');

    // Префы ушедшего режима на сервере — выбор человека, а не пустота
    await expect.poll(async () => {
      const res = await page.request.get(`/api/projects/${projectId}/audio-editor/prefs`, { headers: { Authorization: `Bearer ${token}` } });
      const prefs = await res.json() as { voice: { provider: string | null; model: string | null } | null };
      return `${prefs.voice?.provider}/${prefs.voice?.model}`;
    }).toBe('local/moss-tts');

    // После перезагрузки: нить держит Музыку, Голос поднимается из своих префов
    await page.reload();
    const p2 = await openPanel(page, width < 500);
    await on(p2, 'provider:fal');
    await p2.getByRole('button', { name: 'Голос', exact: true }).click();
    await on(p2, 'provider:local');
    await on(p2, 'model:moss-tts');

    const box = (await p2.boundingBox())!;
    expect(box.x + box.width).toBeLessThanOrEqual(width);
  });
}
