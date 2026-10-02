import { test, expect, type Page } from '@playwright/test';
import { execFileSync } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';

// Режимы поля ввода следуют за полосой над ним: «Картинки» — «Чат | Картинка», «Звук» —
// «Чат | Звук»; смена полосы меняет набор и уводит поле из чужого режима в «Чат».
// Нужен стенд на ВРЕМЕННОЙ data и проект в AE_PROJECT_ROOT; у владельца включён audio-editor.
//
//   AE_PROJECT_ROOT=/tmp/cm-stand/proj PLAYWRIGHT_BASE_URL=http://127.0.0.1:5311 \
//     CM_SHOTS_DIR=../.cc-attachments/composer-modes npx playwright test e2e/composer-modes-follow-strip.spec.ts

const USER = process.env.E2E_USER || 'admin';
const PASS = process.env.E2E_PASS || '12345';
const ROOT = process.env.AE_PROJECT_ROOT || '';
const SHOTS = process.env.CM_SHOTS_DIR || '';

let token = '';
let projectId = '';
const auth = () => ({ Authorization: `Bearer ${token}` });

test.use({ serviceWorkers: 'block' });

test.beforeAll(async ({ playwright, baseURL }) => {
  expect(ROOT, 'нужен AE_PROJECT_ROOT стенда на временной data').toBeTruthy();
  const request = await playwright.request.newContext({ baseURL });
  const r = await request.post('/api/auth/login', { data: { username: USER, password: PASS } });
  expect(r.ok(), 'логин должен пройти').toBeTruthy();
  token = (await r.json()).token as string;
  for (const f of ['audio-editor', 'image-editor', 'image-panel-v5']) await request.put(`/api/feature-flags/${f}`, { headers: auth(), data: { enabled: true } });
  fs.mkdirSync(path.join(ROOT, 'm'), { recursive: true });
  if (!fs.existsSync(path.join(ROOT, 'm/a.mp3'))) execFileSync('ffmpeg', ['-loglevel', 'error', '-y', '-f', 'lavfi', '-i', 'sine=frequency=300:duration=2', path.join(ROOT, 'm/a.mp3')]);
  if (!fs.existsSync(path.join(ROOT, 'm/p.png'))) execFileSync('ffmpeg', ['-loglevel', 'error', '-y', '-f', 'lavfi', '-i', 'color=c=red:s=64x64', '-frames:v', '1', path.join(ROOT, 'm/p.png')]);
  let projects = (await (await request.get('/api/projects', { headers: auth() })).json()) as { id: string; rootPath: string }[];
  if (!projects.some(p => p.rootPath === ROOT)) {
    await request.post('/api/projects', { headers: auth(), data: { name: 'Режимы', rootPath: ROOT } });
    projects = (await (await request.get('/api/projects', { headers: auth() })).json()) as { id: string; rootPath: string }[];
  }
  projectId = projects.find(p => p.rootPath === ROOT)!.id;
  await request.dispose();
});

// Чат, где выбраны и картинка, и звук: обе полосы доступны
async function chat(page: Page): Promise<string> {
  const req = page.request;
  const sid = (await (await req.post(`/api/projects/${projectId}/sessions`, { headers: auth(), data: { name: `Режимы ${Date.now()}` } })).json()).id as string;
  const img = await req.post(`/api/projects/${projectId}/image-editor/sessions/${sid}/threads`, { headers: auth(), data: { file: 'm/p.png', revision: 0 } });
  expect(img.ok(), `нить картинки: ${await img.text()}`).toBeTruthy();
  const imgState = await img.json() as { revision: number; focus: string | null; threads: { id: string }[] };
  if (!imgState.focus) {
    const f = await req.put(`/api/projects/${projectId}/image-editor/sessions/${sid}/threads/focus`, { headers: auth(), data: { threadId: imgState.threads[0].id, revision: imgState.revision } });
    expect(f.ok(), `фокус картинки: ${await f.text()}`).toBeTruthy();
  }
  const snd = await req.post(`/api/projects/${projectId}/audio-editor/sessions/${sid}/threads`, { headers: auth(), data: { file: 'm/a.mp3', mode: 'music', revision: 0 } });
  expect(snd.ok(), `нить звука: ${await snd.text()}`).toBeTruthy();
  const sndState = await snd.json() as { revision: number; focus: string | null; threads: { id: string }[] };
  if (!sndState.focus) {
    const f = await req.put(`/api/projects/${projectId}/audio-editor/sessions/${sid}/threads/focus`, { headers: auth(), data: { threadId: sndState.threads[0].id, revision: sndState.revision } });
    expect(f.ok(), `фокус звука: ${await f.text()}`).toBeTruthy();
  }
  return sid;
}

// Полосу берёт последний запросивший (выбор звука/картинки), поэтому нужную выбираем меню — как человек
async function pickStrip(page: Page, stripId: 'images' | 'sound') {
  const target = page.locator(`[data-composer-strip="${stripId}"]`);
  await page.waitForTimeout(1500);
  if (await target.isVisible()) return;
  const sw = page.locator('[data-composer-strip-switcher] button').first();
  await sw.click();
  const title = stripId === 'images' ? 'Картинки' : 'Звук';
  // Пункт меню — кнопка с подписью и строкой состояния; меню рисуется последним в body
  await page.getByRole('button', { name: new RegExp(`^${title} Работаем с`) }).last().click();
  await expect(target).toBeVisible({ timeout: 10_000 });
}

async function open(page: Page, sid: string, stripId: 'images' | 'sound', width: number) {
  await page.setViewportSize({ width, height: width < 500 ? 780 : 900 });
  await page.addInitScript(([tk, s]) => {
    localStorage.setItem('cc_token', tk);
    localStorage.setItem(`cc-composer-strip-collapsed:${s}:images`, '0');
    localStorage.setItem(`cc-composer-strip-collapsed:${s}:sound`, '0');
  }, [token, sid] as const);
  await page.goto(`/#/project/${projectId}/chat/${sid}`);
  await expect(page.locator('[data-composer-strip]').first()).toBeVisible({ timeout: 30_000 });
  await pickStrip(page, stripId);
}

const modes = (page: Page) => page.locator('[data-composer-modes]');
const imageBtn = (page: Page) => page.getByRole('button', { name: 'Режим «Картинка»' });
const soundBtn = (page: Page) => page.getByRole('button', { name: 'Режим «Звук»' });
const chatBtn = (page: Page) => page.getByRole('button', { name: 'Режим «Чат»' });

const shot = async (page: Page, name: string) => {
  if (!SHOTS) return;
  fs.mkdirSync(SHOTS, { recursive: true });
  await page.screenshot({ path: path.join(SHOTS, `${name}.png`) });
};

for (const width of [1440, 360]) {
  test(`${width}: набор режимов следует за полосой, смена полосы сбрасывает чужой режим`, async ({ page }) => {
    const sid = await chat(page);
    await open(page, sid, 'images', width);
    await expect(chatBtn(page)).toBeVisible();
    await expect(imageBtn(page)).toBeVisible();
    await expect(soundBtn(page)).toHaveCount(0);
    await shot(page, `${width}-images-strip`);

    // Полоса «Картинки», поле в режиме «Картинка»; уходим на «Звук» — поле в «Чате», «Картинки» нет
    await imageBtn(page).click();
    await expect(page.locator('[data-composer-mode-bar]')).toBeVisible();
    await expect(soundBtn(page)).toHaveCount(0);
    await shot(page, `${width}-images-mode`);
    await pickStrip(page, 'sound');
    await expect(page.locator('[data-composer-mode-bar]')).toHaveCount(0);
    await expect(chatBtn(page)).toBeVisible();
    await expect(soundBtn(page)).toBeVisible();
    await expect(imageBtn(page)).toHaveCount(0);
    await shot(page, `${width}-sound-strip`);
  });
}
