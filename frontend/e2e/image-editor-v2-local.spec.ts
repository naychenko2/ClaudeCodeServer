import { test, expect, type APIRequestContext, type Page } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';

// Поставщик «Локальные модели» (ADR-018 §11): строка «Бесплатно · …» вместо цены, выбор
// поставщика и модели, «Улучшить лица» и живая правка через ComfyUI. Гоняется против стенда
// на ВРЕМЕННОЙ data с LocalMedia__Enabled=true и живым ComfyUI; проект — папка
// IE_PROJECT_ROOT с photo.png. Последний тест — на моке с тремя поставщиками: подсказка
// «Улучшить лица» у тех, кто так не умеет.
//
//   IE_PROJECT_ROOT=/tmp/ie-local/proj E2E_PASS=… PLAYWRIGHT_BASE_URL=http://127.0.0.1:5077 \
//     IE_SHOTS_DIR=../.cc-attachments/image-editor-v2/local npx playwright test e2e/image-editor-v2-local.spec.ts

const USER = process.env.E2E_USER || 'admin';
const PASS = process.env.E2E_PASS || '12345';
const ROOT = process.env.IE_PROJECT_ROOT || '';
const SHOTS = process.env.IE_SHOTS_DIR || '';
const FILE = 'photo.png';

async function login(request: APIRequestContext): Promise<string> {
  const r = await request.post('/api/auth/login', { data: { username: USER, password: PASS } });
  expect(r.ok(), 'логин должен пройти').toBeTruthy();
  return (await r.json()).token as string;
}

async function ensureProject(request: APIRequestContext, token: string): Promise<{ id: string }> {
  const headers = { Authorization: `Bearer ${token}` };
  const list = (await (await request.get('/api/projects', { headers })).json()) as { id: string; rootPath: string }[];
  const found = list.find(p => p.rootPath === ROOT);
  if (found) return found;
  const r = await request.post('/api/projects', { headers, data: { name: 'Локальные модели', rootPath: ROOT } });
  expect(r.ok(), `проект должен создаться: ${r.status()} ${await r.text()}`).toBeTruthy();
  return (await r.json()) as { id: string };
}

async function openEditor(page: Page, width: number, height: number, mock: 'all' | null = null) {
  const request = await page.context().request;
  const token = await login(request);
  const project = await ensureProject(request, token);
  // Без cc_user_id страница не вступает в группу владельца и не слышит события задачи
  const me = (await (await request.get('/api/auth/me', { headers: { Authorization: `Bearer ${token}` } })).json()) as { userId: string };
  await page.setViewportSize({ width, height });
  await page.addInitScript(([tk, m, uid]) => {
    localStorage.setItem('cc_token', tk as string);
    localStorage.setItem('cc_user_id', uid as string);
    if (m) localStorage.setItem('cc-image-editor-mock', m as string);
    else localStorage.removeItem('cc-image-editor-mock');
  }, [token, mock, me.userId]);
  await page.goto(`/#/project/${project.id}/file/${encodeURIComponent(FILE)}`);
  await page.getByRole('button', { name: 'Редактировать' }).first().click();
  await expect(page.getByText(FILE).first()).toBeVisible();
}

const shot = (page: Page, name: string) => (SHOTS ? page.screenshot({ path: path.join(SHOTS, name) }) : Promise.resolve());

// Секция панели раскрывается кликом по заголовку, если закрыта
async function openSection(page: Page, title: string, probe: string) {
  if (!(await page.getByRole('button', { name: probe }).first().isVisible())) {
    await page.getByRole('button', { name: new RegExp(title) }).first().click();
  }
}

test.beforeAll(() => {
  expect(ROOT, 'нужен IE_PROJECT_ROOT — папка тестового проекта на временной data').not.toBe('');
  expect(fs.existsSync(path.join(ROOT, FILE))).toBeTruthy();
});

test('1440: поставщик «Локальные модели», строка «Бесплатно · …» и живая правка', async ({ page }) => {
  test.setTimeout(10 * 60_000);
  await openEditor(page, 1440, 900);

  // Вместо денег — «Бесплатно», время и (если не пусто) очередь
  const price = page.locator('[data-prompt-card] b', { hasText: /^Бесплатно · (≈ \d+ (с|мин)|время уточняется)/ });
  await expect(price).toBeVisible({ timeout: 20_000 });
  await expect(page.locator('[data-prompt-card]')).not.toContainText('$');

  // «Поставщик → Модель»: выбираем «Локальные модели» и Qwen-Image 2.1
  await openSection(page, 'Чем рисовать', 'Поставщик');
  await page.getByRole('button', { name: /^Поставщик:/ }).click();
  const localItem = page.getByRole('menuitem', { name: /Локальные модели/ }).or(page.getByText('Локальные модели').last());
  await expect(page.getByText('бесплатно, на своей видеокарте')).toBeVisible();
  await shot(page, '1440-provider-menu.png');
  await localItem.first().click();
  await page.getByRole('button', { name: /^Модель:/ }).click();
  await expect(page.getByText('по тексту и правка')).toBeVisible();
  await expect(page.getByText('Запускается кнопкой «Улучшить лица» в быстрых действиях')).toBeVisible();
  await shot(page, '1440-model-menu.png');
  await page.getByText('Qwen-Image 2.1').last().click();
  await expect(page.getByRole('button', { name: /Модель: Qwen-Image 2\.1/ })).toBeVisible();
  await expect(price).toBeVisible({ timeout: 20_000 });

  // «Улучшить лица» доступно у локальных моделей даже при явной Qwen-Image;
  // чего поставщик не умеет — выключено с подсказкой
  await openSection(page, 'Быстрые действия', 'Улучшить лица');
  await expect(page.getByRole('button', { name: 'Улучшить лица' })).toBeEnabled();
  await expect(page.getByRole('button', { name: 'Убрать фон' })).toBeDisabled();
  await expect(page.getByRole('button', { name: 'Убрать фон' }))
    .toHaveAttribute('title', 'Этот поставщик так не умеет — возьмите другого в «Чем рисовать»');
  await shot(page, '1440-editor-local.png');

  // Живая правка одним вариантом
  await page.locator('[data-prompt-card]').getByRole('button', { name: '1', exact: true }).click();
  await page.locator('[data-prompt-card] textarea').fill('сделай небо закатным, оранжевым');
  await expect(page.getByRole('button', { name: 'Сгенерировать' })).toBeEnabled({ timeout: 20_000 });
  await page.getByRole('button', { name: 'Сгенерировать' }).click();
  await expect(page.getByText(/Рисуем 1 вариант/).first()).toBeVisible();
  await shot(page, '1440-running.png');
  await expect(page.getByText('Готово: 1 вариант')).toBeVisible({ timeout: 8 * 60_000 });
  // Бесплатный запуск не пишет «Списано»
  await expect(page.getByText(/Списано/)).toHaveCount(0);
  await shot(page, '1440-edit-done.png');
});

test('1440: «Улучшить лица» на живом ComfyUI', async ({ page }) => {
  test.setTimeout(10 * 60_000);
  await openEditor(page, 1440, 900);
  await openSection(page, 'Быстрые действия', 'Улучшить лица');
  await expect(page.getByRole('button', { name: 'Улучшить лица' })).toBeEnabled({ timeout: 20_000 });
  await page.getByRole('button', { name: 'Улучшить лица' }).click();
  await expect(page.getByText('Готово: 1 вариант')).toBeVisible({ timeout: 8 * 60_000 });
  await shot(page, '1440-faces-done.png');
});

test('390: «Чем рисовать» и строка цены на телефоне', async ({ page }) => {
  await openEditor(page, 390, 844);
  const price = page.locator('[data-prompt-card] b', { hasText: /^Бесплатно · / });
  await expect(price).toBeVisible({ timeout: 20_000 });
  // Строка цены не вылезает за экран
  const box = await price.boundingBox();
  expect(box && box.x + box.width).toBeLessThanOrEqual(390);
  await shot(page, '390-editor.png');

  await page.getByRole('button', { name: 'Инструменты' }).click();
  await openSection(page, 'Чем рисовать', 'Локальные модели');
  await page.getByRole('button', { name: /Локальные модели · / }).click();
  await expect(page.getByText('бесплатно, на своей видеокарте')).toBeVisible();
  await expect(page.getByText(/^Бесплатно · /).last()).toBeVisible();
  await shot(page, '390-provider-sheet.png');
  await page.getByRole('button', { name: 'Готово' }).click();

  await openSection(page, 'Быстрые действия', 'Улучшить лица');
  await expect(page.getByRole('button', { name: 'Улучшить лица' })).toBeEnabled();
  await shot(page, '390-quick-actions.png');
});

test('мок: у fal «Улучшить лица» выключено с подсказкой', async ({ page }) => {
  await openEditor(page, 1440, 900, 'all');
  await openSection(page, 'Чем рисовать', 'Поставщик');
  await page.getByRole('button', { name: /^Поставщик:/ }).click();
  await page.getByText('оплата в долларах').click();
  await openSection(page, 'Быстрые действия', 'Улучшить лица');
  const faces = page.getByRole('button', { name: 'Улучшить лица' });
  await expect(faces).toBeDisabled();
  await expect(faces).toHaveAttribute('title', 'Есть только у «Локальных моделей» — выберите их в «Чем рисовать»');
  await expect(page.locator('[data-prompt-card]')).toContainText('$');
  await shot(page, '1440-mock-fal-faces-off.png');
});
