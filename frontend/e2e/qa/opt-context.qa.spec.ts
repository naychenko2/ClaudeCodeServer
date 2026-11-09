import { test, expect, type APIRequestContext, type Page } from '@playwright/test';
import { api, collectConsoleErrors, login, type Api } from './helpers';
import { readFileSync, writeFileSync, existsSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

// Приёмка фичи «Внутренняя оптимизация AI Home»: неактивная вертикаль в строке контекста
// (сценарий п. 2b карточки b39c753a).
//
// Две фазы, разведённые файлом `.phase-opt-context` рядом со спеком (как в components-admin):
// гейт подсистемы читается один раз при старте сервера, поэтому «выключенный раздел» видно
// ТОЛЬКО после резапуска стенда.
//
//   on  — вертикаль «Редактор картинок» включена: у картинки обычный чип строки, текста
//         причины нет, в панели «С чем» есть действие вида (кнопка редактора);
//   off — вертикаль выключена администратором (PUT /api/admin/subsystems/imageeditor): в строке
//         неактивная плашка «Раздел выключен администратором», имя картинки не показано,
//         действия вида в панели «С чем» нет.
//
// Засев (фаза on) кладёт проект, чат, нить картинки и основной объект контекста; его id
// сохраняются в `.seed-opt-context.json` — фаза off открывает тот же чат (вертикаль выключена,
// засевать её уже нечем: маршрутов редактора нет).

const PHASE_FILE = join(dirname(fileURLToPath(import.meta.url)), '.phase-opt-context');
const SEED_FILE = join(dirname(fileURLToPath(import.meta.url)), '.seed-opt-context.json');
const PHASE = (existsSync(PHASE_FILE) ? readFileSync(PHASE_FILE, 'utf8').trim() : '') || 'on';
const PREFIX = 'qa-optctx-';
const USER = process.env.E2E_USER || 'admin';
const PASS = process.env.E2E_PASS || '12345';
const TARGET = 'imageeditor';   // выключаемая вертикаль: владелец вида «image» в строке контекста
const DISABLED_TEXT = 'Раздел выключен администратором';

// Крошечный валидный PNG 1×1 — картинке достаточно существовать в проекте.
const PNG_1PX = Buffer.from(
  'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==',
  'base64',
);

interface SeedData {
  projectId: string;
  sessionId: string;
  threadId: string;
  fileName: string;
}

test.describe.configure({ mode: 'serial' });

let request: APIRequestContext;
let token: string;
let client: Api;

test.beforeAll(async ({ playwright, baseURL }) => {
  request = await playwright.request.newContext({ baseURL });
  token = await login(request);
  client = api(request, token);
});

test.afterAll(async () => {
  await request?.dispose();
});

// ─── helpers ─────────────────────────────────────────────────────────────────

// Вход через форму: роль «admin» фронт берёт из localStorage cc_role, который ставит форма.
async function loginViaUi(page: Page) {
  await page.goto('/');
  await page.getByPlaceholder('Имя пользователя').fill(USER);
  await page.getByPlaceholder('Пароль').fill(PASS);
  await page.getByRole('button', { name: 'Войти', exact: true }).click();
  await expect(page.getByRole('button', { name: /Меню пользователя/ })).toBeVisible({ timeout: 30_000 });
}

async function openChat(page: Page, s: SeedData) {
  await page.goto(`/#/project/${s.projectId}/chat/${s.sessionId}`);
  await expect(page.locator('#root')).not.toBeEmpty();
  // Строка контекста: чип основного объекта с подписью [data-chip-label].
  await expect(page.locator('[data-chip-label]').first()).toBeVisible({ timeout: 30_000 });
}

function primaryChip(page: Page) {
  return page.locator('[data-chip-label]').first();
}

// Панель «Контекст»: раздел «С чем» и кнопка-редактор вида (действие вида).
async function openContextPanel(page: Page) {
  await primaryChip(page).click();
  const withSection = page.locator('[data-ctx-section="with"]');
  await expect(withSection).toBeVisible({ timeout: 15_000 });
  return withSection;
}

// ─── фаза on: вертикаль включена ────────────────────────────────────────────

test('включённая вертикаль: у картинки обычный чип строки, текста причины нет', async ({ page }) => {
  test.skip(PHASE !== 'on', 'фаза on');
  const errors = collectConsoleErrors(page);
  await loginViaUi(page);

  const stamp = Date.now();
  const name = `${PREFIX}p-${stamp}`;
  const rootPath = join('/tmp', name);
  const fileName = 'photo.png';

  // Проект с картинкой внутри: картинка нужна нити редактора (источник — файл проекта).
  const project = await client.post<{ id: string }>('/api/projects', { name, rootPath, createDirectory: true });
  const session = await client.post<{ id: string }>(`/api/projects/${project.id}/sessions`, { name: `${PREFIX}chat-${stamp}` });
  writeFileSync(join(rootPath, fileName), PNG_1PX);

  // Нить картинки в чате (маршруты редактора есть только у включённой вертикали).
  const threads = await client.post<{ threads: Array<{ id: string }> }>(
    `/api/projects/${project.id}/image-editor/sessions/${session.id}/threads`,
    { file: fileName, revision: 0 },
  );
  const threadId = threads.threads[0]?.id;
  expect(threadId, 'нить картинки создана').toBeTruthy();

  // Основной объект контекста — эта картинка. Нить открывается «в работе», поэтому засев
  // уже мог поставить картинку в контекст — тогда PUT не нужен, а если нет — ставим с той
  // ревизией, которую отдал сервер (своя ревизия = 409 context_changed).
  const ctx = await client.get<{ revision: number; primary: { kind: string } | null }>(`/api/chats/${session.id}/context`);
  if (ctx.primary?.kind !== 'image') {
    await client.put(`/api/chats/${session.id}/context/primary`, { kind: 'image', ref: { threadId }, revision: ctx.revision });
  }

  const seed: SeedData = { projectId: project.id, sessionId: session.id, threadId: threadId!, fileName };
  writeFileSync(SEED_FILE, JSON.stringify(seed));

  await openChat(page, seed);

  // Чип обычный: имя картинки видно, текста причины нет.
  await expect(primaryChip(page)).toContainText(fileName, { timeout: 15_000 });
  await expect(page.getByText(DISABLED_TEXT, { exact: true })).toHaveCount(0);

  // Действие вида в панели «С чем» есть (кнопка редактора).
  const withSection = await openContextPanel(page);
  await expect(withSection.getByRole('button').filter({ hasText: /Править|Работать|Открыть/ }).first()).toBeVisible();

  expect(errors.filter(e => !/status of 503/.test(e)), 'в консоли не должно быть ошибок').toEqual([]);
});

// ─── фаза off: вертикаль выключена администратором ───────────────────────────

test('выключенная вертикаль: неактивная плашка в строке, имя картинки не показано, действия вида нет', async ({ page }) => {
  test.skip(PHASE !== 'off', 'фаза off');
  const errors = collectConsoleErrors(page);
  expect(existsSync(SEED_FILE), 'засев фазы on на месте').toBeTruthy();
  const seed = JSON.parse(readFileSync(SEED_FILE, 'utf8')) as SeedData;

  await loginViaUi(page);
  await openChat(page, seed);

  // Плашка причины на месте, строка жива.
  await expect(page.getByText(DISABLED_TEXT, { exact: true }).first()).toBeVisible({ timeout: 15_000 });
  // Имя картинки в строке не показано.
  await expect(primaryChip(page)).not.toContainText(seed.fileName);

  // Действий вида нет: в панели «С чем» нет кнопки-редактора.
  const withSection = await openContextPanel(page);
  await expect(withSection.getByRole('button').filter({ hasText: /Править|Работать|Открыть/ })).toHaveCount(0);

  expect(errors.filter(e => !/status of 503/.test(e)), 'в консоли не должно быть ошибок').toEqual([]);
});

// ─── переключение вертикали (админский REST, готовит фазу off) ───────────────

test('выключение вертикали администратором пишет в конфиг и требует рестарта', async () => {
  test.skip(PHASE !== 'off', 'фаза off');
  const list = await client.get<Array<{ key: string; enabled: boolean; active: boolean; restartRequired: boolean }>>('/api/admin/subsystems');
  const row = list.find(s => s.key === TARGET);
  expect(row, `вертикаль ${TARGET} есть в списке`).toBeTruthy();
  expect(row!.enabled, 'вертикаль выключена записью').toBe(false);
  // После рестарта гейт одноразовый: модуль не загружен — вертикаль не активна.
  expect(row!.active, 'после рестарта вертикаль не активна').toBe(false);
  expect(row!.restartRequired, 'расхождение записи и процесса снято рестартом').toBe(false);
});
