import { test, expect, type APIRequestContext, type Page } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';

// Шаг 9 редактора картинок v2: диалог «Сохранить как…» на живых ручках save / save/check.
// Гоняется против стенда на ВРЕМЕННОЙ data (не боевой): проект — папка IE_PROJECT_ROOT с
// images/hero.png, images/generated/hero.v2.png и пустой images/blog/. Шаг истории
// получаем правкой без ИИ (поворот), генерация не нужна.
//
//   IE_PROJECT_ROOT=/tmp/ie9-stand/proj PLAYWRIGHT_BASE_URL=http://127.0.0.1:5099 \
//     npx playwright test e2e/image-editor-v2-save.spec.ts

const USER = process.env.E2E_USER || 'admin';
const PASS = process.env.E2E_PASS || '12345';
const ROOT = process.env.IE_PROJECT_ROOT || '';
const SHOTS = process.env.IE_SHOTS_DIR || '';

async function login(request: APIRequestContext): Promise<string> {
  const r = await request.post('/api/auth/login', { data: { username: USER, password: PASS } });
  expect(r.ok(), 'логин должен пройти').toBeTruthy();
  return (await r.json()).token as string;
}

// Проект на временной папке: заводим, если его ещё нет
async function ensureProject(request: APIRequestContext, token: string): Promise<{ id: string; name: string }> {
  const headers = { Authorization: `Bearer ${token}` };
  const list = (await (await request.get('/api/projects', { headers })).json()) as { id: string; name: string; rootPath: string }[];
  const found = list.find(p => p.rootPath === ROOT);
  if (found) return found;
  const r = await request.post('/api/projects', { headers, data: { name: 'Сайт студии', rootPath: ROOT } });
  expect(r.ok(), `проект должен создаться: ${r.status()} ${await r.text()}`).toBeTruthy();
  return (await r.json()) as { id: string; name: string };
}

async function openEditor(page: Page, projectId: string, token: string, width: number, height: number) {
  await page.setViewportSize({ width, height });
  await page.addInitScript(tk => {
    localStorage.setItem('cc_token', tk as string);
    localStorage.removeItem('cc-image-editor-mock');
  }, token);
  await page.goto(`/#/project/${projectId}/file/${encodeURIComponent('images/hero.png')}`);
  await page.getByRole('button', { name: 'Редактировать' }).first().click();
  await expect(page.getByText('hero.png').first()).toBeVisible();
}

// Правка без ИИ даёт шаг истории — в шапке появляются «Сохранить как…» и «Сохранить»
async function makeStep(page: Page, mobile: boolean) {
  if (mobile) await page.getByRole('button', { name: 'Инструменты' }).click();
  const rotate = page.getByRole('button', { name: 'Повернуть вправо' });
  if (!(await rotate.isVisible())) await page.getByRole('button', { name: /Правка без ИИ/ }).first().click();
  await rotate.click();
  if (mobile) await page.keyboard.press('Escape');
  await expect(page.getByRole('button', { name: 'Сохранить', exact: true })).toBeEnabled({ timeout: 20_000 });
}

test.beforeAll(() => {
  expect(ROOT, 'нужен IE_PROJECT_ROOT — папка тестового проекта на временной data').not.toBe('');
  expect(fs.existsSync(path.join(ROOT, 'images/hero.png'))).toBeTruthy();
});

test('«Сохранить как…»: новое имя в другую папку, тост и «Показать в дереве»', async ({ page, playwright, baseURL }) => {
  const request = await playwright.request.newContext({ baseURL });
  const token = await login(request);
  const project = await ensureProject(request, token);
  await request.dispose();
  fs.rmSync(path.join(ROOT, 'images/blog/cover.png'), { force: true });

  await openEditor(page, project.id, token, 1440, 900);
  await makeStep(page, false);
  await page.getByRole('button', { name: 'Сохранить как…' }).click();

  const dialog = page.locator('[data-save-as]');
  await expect(dialog).toBeVisible();
  const name = page.getByRole('textbox').last();
  await expect(name).toHaveValue('hero.v2');
  await expect(page.getByTitle('Расширение — по формату результата')).toHaveText('.png');
  await expect(page.getByRole('button', { name: /images\/.*здесь сейчас/ })).toBeVisible();

  await page.getByRole('button', { name: /^images\/blog\// }).click();
  await name.fill('cover.png');
  await expect(page.getByText('Файл ляжет сюда: images/blog/cover.png')).toBeVisible();
  if (SHOTS) await page.screenshot({ path: path.join(SHOTS, 'save-as-1440.png') });

  await page.getByRole('button', { name: 'Сохранить', exact: true }).last().click();
  await expect(page.getByText('Сохранено в проект: images/blog/cover.png').first()).toBeVisible();
  // .png не удвоился, файл на диске, исходник на месте
  expect(fs.existsSync(path.join(ROOT, 'images/blog/cover.png'))).toBeTruthy();
  expect(fs.existsSync(path.join(ROOT, 'images/blog/cover.png.png'))).toBeFalsy();
  expect(fs.existsSync(path.join(ROOT, 'images/hero.png'))).toBeTruthy();
  // Редактор перешёл на новый файл
  await expect(page.getByText('cover.png').first()).toBeVisible();
  if (SHOTS) await page.screenshot({ path: path.join(SHOTS, 'saved-toast-1440.png') });

  await page.getByRole('button', { name: 'Показать в дереве' }).click();
  await expect(page).toHaveURL(/file\/images%2Fblog%2Fcover\.png/);
  await expect(page.locator('[data-save-as]')).toHaveCount(0);
  // Панель файлов открылась, папка images/blog раскрыта: имя файла и в просмотре, и в дереве
  await expect(page.getByText('cover.png', { exact: true })).toHaveCount(2);
  if (SHOTS) await page.screenshot({ path: path.join(SHOTS, 'tree-1440.png') });
});

test('«Сохранить как…»: занятое имя блокирует кнопку, «Взять …» даёт свободное', async ({ page, playwright, baseURL }) => {
  const request = await playwright.request.newContext({ baseURL });
  const token = await login(request);
  const project = await ensureProject(request, token);
  await request.dispose();
  fs.rmSync(path.join(ROOT, 'images/generated/hero.v3.png'), { force: true });
  const before = fs.readFileSync(path.join(ROOT, 'images/generated/hero.v2.png'));

  await openEditor(page, project.id, token, 390, 844);
  await makeStep(page, true);
  await page.getByRole('button', { name: 'Сохранить как…' }).click();
  await expect(page.locator('[data-save-as]')).toBeVisible();

  await page.getByRole('button', { name: /^images\/generated\// }).click();
  await expect(page.locator('[data-save-warn]')).toContainText('Такой файл уже есть: images/generated/hero.v2.png');
  const save = page.getByRole('button', { name: 'Сохранить', exact: true }).last();
  await expect(save).toBeDisabled();
  // Диалог помещается в 390: кнопка сохранения не выходит за ширину экрана
  const box = await save.boundingBox();
  expect(box && box.x + box.width).toBeLessThanOrEqual(390);
  if (SHOTS) await page.screenshot({ path: path.join(SHOTS, 'save-as-taken-390.png') });

  await page.getByRole('button', { name: 'Взять «hero.v3.png»' }).click();
  await expect(page.getByRole('textbox').last()).toHaveValue('hero.v3');
  await expect(page.getByText('Файл ляжет сюда: images/generated/hero.v3.png')).toBeVisible();
  await expect(save).toBeEnabled();
  await save.click();
  await expect(page.getByText('Сохранено в проект: images/generated/hero.v3.png').first()).toBeVisible();
  // Перезаписи нет: занятый файл не изменился
  expect(fs.readFileSync(path.join(ROOT, 'images/generated/hero.v2.png')).equals(before)).toBeTruthy();
  expect(fs.existsSync(path.join(ROOT, 'images/generated/hero.v3.png'))).toBeTruthy();
});
