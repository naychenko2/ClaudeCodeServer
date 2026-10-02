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

const names = new Map<string, string>();
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
async function chat(page: Page, only?: 'image' | 'sound'): Promise<string> {
  const req = page.request;
  const created = await (await req.post(`/api/projects/${projectId}/sessions`, { headers: auth(), data: { name: `Режимы ${Date.now()}${Math.floor(Math.random() * 1000)}` } })).json() as { id: string; name: string };
  const sid = created.id;
  names.set(sid, created.name);
  if (only !== 'sound') {
  const img = await req.post(`/api/projects/${projectId}/image-editor/sessions/${sid}/threads`, { headers: auth(), data: { file: 'm/p.png', revision: 0 } });
  expect(img.ok(), `нить картинки: ${await img.text()}`).toBeTruthy();
  const imgState = await img.json() as { revision: number; focus: string | null; threads: { id: string }[] };
  if (!imgState.focus) {
    const f = await req.put(`/api/projects/${projectId}/image-editor/sessions/${sid}/threads/focus`, { headers: auth(), data: { threadId: imgState.threads[0].id, revision: imgState.revision } });
    expect(f.ok(), `фокус картинки: ${await f.text()}`).toBeTruthy();
  }
  }
  if (only !== 'image') {
  const snd = await req.post(`/api/projects/${projectId}/audio-editor/sessions/${sid}/threads`, { headers: auth(), data: { file: 'm/a.mp3', mode: 'music', revision: 0 } });
  expect(snd.ok(), `нить звука: ${await snd.text()}`).toBeTruthy();
  const sndState = await snd.json() as { revision: number; focus: string | null; threads: { id: string }[] };
  if (!sndState.focus) {
    const f = await req.put(`/api/projects/${projectId}/audio-editor/sessions/${sid}/threads/focus`, { headers: auth(), data: { threadId: sndState.threads[0].id, revision: sndState.revision } });
    expect(f.ok(), `фокус звука: ${await f.text()}`).toBeTruthy();
  }
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
  await page.getByRole('button', { name: new RegExp(`^${title}(\\s|$)`) }).last().click();
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

// На телефоне просьба режима открывает шторку панели поверх поля — её опускаем, как человек
async function dropSheet(page: Page) {
  const close = page.getByRole('button', { name: 'Закрыть панель — сводка останется в полосе' });
  if (await close.isVisible().catch(() => false)) await close.click();
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

// Возврат в чат не переключает поле само: просьба режима («Править», «Новый звук») срабатывает
// один раз, а при перемонтировании поля (уход в другой чат и обратно) её не повторяют.
// Ручной уход в «Чат» и черновик текста при возврате сохраняются. Проверяем и мелькание
// панели режима на возврате: итоговое «Чат» бывает и случайным (полоса мигнула чужой)
async function setFlag(page: Page, flag: string, enabled: boolean) {
  await page.request.put(`/api/feature-flags/${flag}`, { headers: auth(), data: { enabled } });
}

// Переход как у человека: кликом по чату в списке, а не новой загрузкой страницы
async function goChat(page: Page, sid: string) {
  await page.getByText(names.get(sid)!, { exact: false }).first().click();
  await expect(page.locator('[data-composer-strip]').first()).toBeVisible({ timeout: 30_000 });
}

const watchModeBar = (page: Page) => page.evaluate(() => {
  const w = window as unknown as { __bar: boolean };
  w.__bar = !!document.querySelector('[data-composer-mode-bar]');
  new MutationObserver(() => { if (document.querySelector('[data-composer-mode-bar]')) w.__bar = true; })
    .observe(document.body, { childList: true, subtree: true });
});
const barEverShown = (page: Page) => page.evaluate(() => (window as unknown as { __bar: boolean }).__bar);

async function roundTrip(page: Page, a: string, b: string) {
  await page.getByRole('textbox').last().fill('черновик А');
  await goChat(page, b);
  await watchModeBar(page);
  await goChat(page, a);
  await page.waitForTimeout(2000);
  expect(await barEverShown(page), 'панель режима не должна мелькать при возврате').toBe(false);
  await expect(page.locator('[data-composer-mode-bar]')).toHaveCount(0);
  await expect(page.getByRole('textbox').last()).toHaveValue('черновик А');
}

for (const v5 of [true, false]) {
  for (const width of [1440, 360]) {
    test(`${width}: «Картинка» (image-panel-v5=${v5}) — возврат в чат не включает режим сам`, async ({ page }) => {
      await setFlag(page, 'image-panel-v5', v5);
      try {
        const a = await chat(page);
        const b = await chat(page);
        await open(page, a, 'images', width);
        if (v5) await page.locator('[data-images-mode-switch] button').nth(1).click();
        else {
          // Без флага просьба — «Нарисовать новую», она есть, когда картинка не выбрана
          await page.locator('[data-composer-strip="images"]').getByText('×', { exact: true }).first().click();
          await page.locator('[data-composer-strip="images"]').getByText('Нарисовать новую').first().dispatchEvent('click');
        }
        await expect(page.locator('[data-composer-mode-bar]')).toBeVisible({ timeout: 10_000 });
        await dropSheet(page);
        await chatBtn(page).click();
        await expect(page.locator('[data-composer-mode-bar]')).toHaveCount(0);
        if (width > 500) await roundTrip(page, a, b);
        else {
          // На телефоне списка чатов рядом нет: уходим назад из чата и возвращаемся по адресу
          await page.getByRole('textbox').last().fill('черновик А');
          await page.goto(`/#/project/${projectId}`);
          await watchModeBar(page);
          await page.goto(`/#/project/${projectId}/chat/${a}`);
          await expect(page.locator('[data-composer-strip]').first()).toBeVisible({ timeout: 30_000 });
          await page.waitForTimeout(2000);
          expect(await barEverShown(page)).toBe(false);
          await expect(page.getByRole('textbox').last()).toHaveValue('черновик А');
        }
      } finally { await setFlag(page, 'image-panel-v5', true); }
    });
  }
}

for (const width of [1440, 360]) {
  test(`${width}: «Звук» — возврат в чат не включает режим сам`, async ({ page }) => {
    const a = await chat(page);
    const b = await chat(page);
    await open(page, a, 'sound', width);
    await page.locator('[data-composer-strip="sound"]').locator('[data-sound-chip="focus"]').getByText('×', { exact: true }).first().click();
    await pickStrip(page, 'sound');
    await page.locator('[data-composer-strip="sound"]').getByText('Новый звук').first().dispatchEvent('click');
    await expect(page.locator('[data-composer-mode-bar]')).toBeVisible({ timeout: 10_000 });
    await dropSheet(page);
    await chatBtn(page).click();
    await expect(page.locator('[data-composer-mode-bar]')).toHaveCount(0);
    if (width > 500) await roundTrip(page, a, b);
    else {
      await page.getByRole('textbox').last().fill('черновик А');
      await page.goto(`/#/project/${projectId}`);
      await watchModeBar(page);
      await page.goto(`/#/project/${projectId}/chat/${a}`);
      await expect(page.locator('[data-composer-strip]').first()).toBeVisible({ timeout: 30_000 });
      await page.waitForTimeout(2000);
      expect(await barEverShown(page)).toBe(false);
      await expect(page.getByRole('textbox').last()).toHaveValue('черновик А');
    }
  });
}

// Свёрнутая полоса: режим «Чат» и черновик переживают уход и возврат, панель режима не мелькает
test('1440: свёрнутая полоса — возврат в чат не включает «Картинка»', async ({ page }) => {
  const a = await chat(page);
  const b = await chat(page);
  await open(page, a, 'images', 1440);
  await page.locator('[data-images-mode-switch] button').nth(1).click();
  await expect(page.locator('[data-composer-mode-bar]')).toBeVisible({ timeout: 10_000 });
  await chatBtn(page).click();
  await page.evaluate(s => {
    for (const k of ['images', 'sound']) localStorage.setItem(`cc-composer-strip-collapsed:${s}:${k}`, '1');
  }, a);
  await page.getByRole('textbox').last().fill('черновик А');
  await goChat(page, b);
  await watchModeBar(page);
  await goChat(page, a);
  await page.waitForTimeout(2000);
  expect(await barEverShown(page)).toBe(false);
  await expect(page.getByRole('textbox').last()).toHaveValue('черновик А');
});
