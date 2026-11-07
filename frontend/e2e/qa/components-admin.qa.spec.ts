import { test, expect, type APIRequestContext, type Page } from '@playwright/test';
import { api, collectConsoleErrors, login, type Api } from './helpers';
import { readFileSync, existsSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

// Приёмка панели «Компоненты» (шаг 2c «Внутренней оптимизации») — целиком в браузере.
//
// Фазы одной проверки, разведённые файлом `.phase-components` рядом со спеком:
// гейт читается один раз при старте процесса, поэтому «выключенный компонент
// перестал работать» видно ТОЛЬКО после перезапуска сервера. Спек умеет и то, что
// видно сразу (панель, список, запись тумблера, отказ записи, роли), и то, что
// видно после рестарта. Файл, а не переменная окружения: run_tests пробрасывает
// только PLAYWRIGHT_BASE_URL/E2E_USER/E2E_PASS.
//
//   ui        — панель, состояние из бэка, персистентность переключения, отказ
//               записи и загрузки, роли (обычный пользователь);
//   off       — выключаю «Заметки» тумблером в панели (запись уходит в бек);
//   off-effect— ПОСЛЕ рестарта сервера: «Заметки» действительно выключены;
//   on        — включаю «Заметки» обратно тумблером;
//   on-effect — ПОСЛЕ рестарта: поведение вернулось.
//
// Ключ под эффектом — notes: из отключаемых он единственный гейтит интерфейс
// (его API /api/notes и ключ в составе активных подсистем), и его отключение
// ничего не ломает в остальном продукте.

const PHASE_FILE = join(dirname(fileURLToPath(import.meta.url)), '.phase-components');
const PHASE = (existsSync(PHASE_FILE) ? readFileSync(PHASE_FILE, 'utf8').trim() : '') || 'ui';
const PREFIX = 'qa-comp-';
const USER = process.env.E2E_USER || 'admin';
const PASS = process.env.E2E_PASS || '12345';
const TARGET = 'notes';          // ключ под эффектом
const TOGGLE_KEY = 'changelog';  // ключ для проверки персистентности записи

interface SubsystemRow {
  key: string;
  title: string;
  enabled: boolean;
  active: boolean;
  restartRequired: boolean;
  canDisable: boolean;
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

async function openPanel(page: Page) {
  // dispatchEvent: тосты уведомлений висят поверх аватара и перехватывают клик;
  // событие доставляем прямо элементу (React слушает на корне — обработчик сработает).
  await page.getByRole('button', { name: /Меню пользователя/ }).dispatchEvent('click');
  await page.getByRole('button', { name: 'Компоненты', exact: true }).dispatchEvent('click');
  const panel = page.locator('.cc-modal-card');
  await expect(panel).toBeVisible();
  await expect(panel.getByText('Компоненты', { exact: true })).toBeVisible();
  return panel;
}

function row(page: Page, key: string) {
  return page.locator(`[data-testid="components-row-${key}"]`);
}

async function openApp(page: Page) {
  await page.goto('/');
  await expect(page.locator('#root')).not.toBeEmpty();
}

// Вход через форму, а не подстановкой токена: роль («admin») фронт берёт из
// localStorage cc_role, который ставит именно форма входа. Токен без роли даёт
// интерфейс обычного пользователя (пункта «Компоненты» нет).
async function loginViaUi(page: Page, username: string, password: string) {
  await page.goto('/');
  await page.getByPlaceholder('Имя пользователя').fill(username);
  await page.getByPlaceholder('Пароль').fill(password);
  await page.getByRole('button', { name: 'Войти', exact: true }).click();
  await expect(page.getByRole('button', { name: /Меню пользователя/ })).toBeVisible({ timeout: 30_000 });
}

async function subsystem(client: Api, key: string): Promise<SubsystemRow> {
  const list = await client.get<SubsystemRow[]>('/api/admin/subsystems');
  const row = list.find(s => s.key === key);
  expect(row, `подсистема ${key} есть в списке`).toBeTruthy();
  return row!;
}

// Тумблер строки: role=switch с aria-checked.
function switchOf(page: Page, key: string) {
  return row(page, key).getByRole('switch');
}

// Диалог подтверждения может появиться (если по компоненту ходили за сутки) —
// отвечаем «Выключить», он есть не на каждом стенде.
async function confirmIfShown(page: Page) {
  const dialog = page.locator('.cc-modal-card').getByRole('button', { name: 'Выключить', exact: true });
  if (await dialog.count()) await dialog.first().click();
}

// ─── ui: панель и её состояние ──────────────────────────────────────────────

test('панель открывается из админки, список приходит с бэка, у отключаемых — тумблер, у ядра — нет', async ({ page, context }) => {
  test.skip(PHASE !== 'ui', 'фаза ui');
  const errors = collectConsoleErrors(page);
  await loginViaUi(page, USER, PASS);

  const list = await client.get<SubsystemRow[]>('/api/admin/subsystems');
  const disableable = list.filter(s => s.canDisable);
  const core = list.filter(s => !s.canDisable);
  expect(disableable.length, 'на стенде есть отключаемые подсистемы').toBeGreaterThan(0);
  expect(core.length, 'на стенде есть строки ядра').toBeGreaterThan(0);

  const panel = await openPanel(page);

  // Каждая отключаемая подсистема — своей строкой с тумблером; строки ядра — без тумблера.
  await expect(panel.locator('[data-testid^="components-row-"]')).toHaveCount(disableable.length);
  for (const s of disableable) {
    await expect(row(page, s.key)).toBeVisible();
    await expect(switchOf(page, s.key)).toHaveCount(1);
  }
  await expect(panel.getByTestId('components-core')).toBeVisible();
  await expect(panel.getByTestId('components-core').getByText('Часть ядра').first()).toBeVisible();
  await expect(panel.getByTestId('components-core').getByRole('switch')).toHaveCount(0);

  // Состояние строки — то, что ответил сервер, а не локальное состояние.
  const notes = await subsystem(client, TARGET);
  expect(notes.enabled, 'целевая подсистема включена на старте проверки').toBe(true);
  await expect(switchOf(page, TARGET)).toHaveAttribute('aria-checked', 'true');
  await expect(row(page, TARGET)).toContainText('Работает');

  // 503 от /api/yandex/account — стенд без ключей Яндекс-биллинга: это ожидаемо
  // и к панели отношения не имеет. Остальных ошибок быть не должно.
  const unexpected = errors.filter(e => !/status of 503/.test(e));
  expect(unexpected, 'в консоли не должно быть ошибок').toEqual([]);
});

test('переключение доезжает до бэка и переживает перезагрузку страницы', async ({ page, context }) => {
  test.skip(PHASE !== 'ui', 'фаза ui');
  await loginViaUi(page, USER, PASS);
  await openPanel(page);

  const before = await subsystem(client, TOGGLE_KEY);
  expect(before.enabled, 'стартовое состояние — включено').toBe(true);

  // Выключаем тумблером.
  await switchOf(page, TOGGLE_KEY).click();
  await confirmIfShown(page);
  await expect(row(page, TOGGLE_KEY)).toContainText('Выключится');
  await expect(switchOf(page, TOGGLE_KEY)).toHaveAttribute('aria-checked', 'false');

  // Запись доехала до бэка. GET отвечает из конфигурации, а она перечитывается
  // с файла с задержкой (reloadOnChange) — ждём значения, а не одного ответа.
  await expect.poll(async () => (await subsystem(client, TOGGLE_KEY)).enabled, { timeout: 15_000 })
    .toBe(false);
  const off = await subsystem(client, TOGGLE_KEY);
  expect(off.active, 'в процессе компонент ещё работает').toBe(true);
  expect(off.restartRequired, 'бек честно просит рестарт').toBe(true);

  // Перезагрузка страницы не должна сбрасывать показанное: состояние приходит с бэка.
  await page.reload();
  await expect(page.locator('#root')).not.toBeEmpty();
  await openPanel(page);
  await expect(switchOf(page, TOGGLE_KEY)).toHaveAttribute('aria-checked', 'false');
  await expect(row(page, TOGGLE_KEY)).toContainText('Выключится');

  // Включаем обратно — и снова проверяем персистентность.
  // Возврат: процесс всё это время работал (рестарта не было), поэтому после
  // включения расхождения нет — строка снова «Работает».
  await switchOf(page, TOGGLE_KEY).click();
  await expect(row(page, TOGGLE_KEY)).toContainText('Работает');
  await expect.poll(async () => (await subsystem(client, TOGGLE_KEY)).enabled, { timeout: 15_000 })
    .toBe(true);

  await page.reload();
  await expect(page.locator('#root')).not.toBeEmpty();
  await openPanel(page);
  await expect(switchOf(page, TOGGLE_KEY)).toHaveAttribute('aria-checked', 'true');
});

test('диалог подтверждения честен, а «Вернуть» гасит запись без рестарта', async ({ page, context }) => {
  test.skip(PHASE !== 'ui', 'фаза ui');
  await loginViaUi(page, USER, PASS);
  await openPanel(page);

  const before = await subsystem(client, TARGET);
  expect(before.enabled, 'стартовое состояние — включено').toBe(true);

  // Выключение используемого компонента спрашивает подтверждение и объясняет,
  // чем это грозит и что данные на диске останутся.
  await switchOf(page, TARGET).click();
  const dialog = page.getByRole('button', { name: 'Выключить', exact: true });
  await expect(dialog.first()).toBeVisible();
  await expect(page.getByText('Им пользовались', { exact: false })).toBeVisible();
  await expect(page.getByText('Файлы на диске останутся', { exact: false })).toBeVisible();
  await dialog.first().click();

  await expect(row(page, TARGET)).toContainText('Выключится');
  await expect.poll(async () => (await subsystem(client, TARGET)).enabled, { timeout: 15_000 })
    .toBe(false);

  // «Вернуть» в расходящейся строке — обратный запрос: он гасит изменение.
  await row(page, TARGET).getByRole('button', { name: 'Вернуть', exact: true }).click();
  await expect(row(page, TARGET)).toContainText('Работает');
  await expect.poll(async () => (await subsystem(client, TARGET)).enabled, { timeout: 15_000 })
    .toBe(true);
  const after = await subsystem(client, TARGET);
  expect(after.active, 'компонент и в процессе работает').toBe(true);
  expect(after.restartRequired, 'расхождения не осталось').toBe(false);
});

test('отказ записи: панель не показывает «выключено» молча и объясняет, что ничего не изменилось', async ({ page, context }) => {
  test.skip(PHASE !== 'ui', 'фаза ui');
  await loginViaUi(page, USER, PASS);

  // Рвём только запись: GET списка продолжает работать.
  await page.route('**/api/admin/subsystems/**', route =>
    route.request().method() === 'PUT'
      ? route.fulfill({
          status: 500,
          contentType: 'application/json',
          body: JSON.stringify({ code: 'write_failed', message: 'Тестовая ошибка записи' }),
        })
      : route.continue());

  await openPanel(page);
  const before = await subsystem(client, TOGGLE_KEY);
  await switchOf(page, TOGGLE_KEY).click();
  await confirmIfShown(page);

  const r = row(page, TOGGLE_KEY);
  await expect(r.getByText('Не удалось записать настройку')).toBeVisible();
  await expect(r.getByText('Тестовая ошибка записи')).toBeVisible();
  await expect(r.getByText('Положение не изменилось')).toBeVisible();
  // Тумблер не «уехал» в выключенное: строка осталась той, что прислал сервер.
  await expect(switchOf(page, TOGGLE_KEY)).toHaveAttribute('aria-checked', String(before.enabled));

  const after = await subsystem(client, TOGGLE_KEY);
  expect(after.enabled, 'бек не записал сорванный запрос').toBe(before.enabled);
  const file = await client.get<SubsystemRow[]>('/api/admin/subsystems');
  expect(file.find(s => s.key === TOGGLE_KEY)!.enabled, 'строка в беке не сдвинулась').toBe(before.enabled);
});

test('недоступный бэк: панель честно говорит, что список не загрузился, и умеет повторить', async ({ page, context }) => {
  test.skip(PHASE !== 'ui', 'фаза ui');
  await loginViaUi(page, USER, PASS);

  await page.route('**/api/admin/subsystems', route =>
    route.request().method() === 'GET'
      ? route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ code: 'boom', message: 'Стенд упал' }) })
      : route.continue());

  const panel = await openPanel(page);
  await expect(panel.getByText('Не удалось загрузить список')).toBeVisible();
  await expect(panel.getByText('Повторить')).toBeVisible();
  // Список не показан — значит, панель не рисует «включено» по умолчанию.
  await expect(panel.locator('[data-testid^="components-row-"]')).toHaveCount(0);

  // Сеть починилась — «Повторить» поднимает список.
  await page.unroute('**/api/admin/subsystems');
  await panel.getByRole('button', { name: 'Повторить' }).click();
  await expect(panel.locator('[data-testid^="components-row-"]').first()).toBeVisible();
});

test('роли: обычному пользователю панель недоступна (403) и в меню её нет', async ({ page, context }) => {
  test.skip(PHASE !== 'ui', 'фаза ui');
  const stamp = Date.now();
  const username = `${PREFIX}user-${stamp}`;
  const password = 'qa-pass-123';
  await client.post('/api/users', { username, password, role: 'user' });

  try {
    // API: 403 обычному пользователю.
    const loginRes = await request.post('/api/auth/login', { data: { username, password } });
    expect(loginRes.ok(), `логин ${username} должен пройти (${loginRes.status()})`).toBeTruthy();
    const userToken = (await loginRes.json()).token as string;

    const listRes = await request.get('/api/admin/subsystems', { headers: { Authorization: `Bearer ${userToken}` } });
    expect(listRes.status(), 'обычный пользователь получает 403 на список подсистем').toBe(403);

    const putRes = await request.put('/api/admin/subsystems/notes', {
      headers: { Authorization: `Bearer ${userToken}` },
      data: { enabled: false },
    });
    expect(putRes.status(), 'обычный пользователь не может записать тумблер').toBe(403);

    // UI: пункта «Компоненты» в меню аватара нет.
    const userContext = await context.browser()!.newContext();
    const userPage = await userContext.newPage();
    await loginViaUi(userPage, username, password);
    await userPage.getByRole('button', { name: /Меню пользователя/ }).dispatchEvent('click');
    await expect(userPage.getByRole('button', { name: 'Компоненты', exact: true })).toHaveCount(0);
    await userContext.close();
  } finally {
    const users = await client.get<Array<{ id: string; username: string }>>('/api/users');
    const created = users.find(u => u.username === username);
    if (created) await client.del(`/api/users/${created.id}`);
  }
});

// ─── off: выключаю «Заметки» тумблером (запись в бек) ───────────────────────

test('выключаю «Заметки» тумблером — запись уходит в бек', async ({ page, context }) => {
  test.skip(PHASE !== 'off', 'фаза off');
  await loginViaUi(page, USER, PASS);
  await openPanel(page);

  await switchOf(page, TARGET).click();
  await confirmIfShown(page);
  await expect(row(page, TARGET)).toContainText('Выключится');

  await expect.poll(async () => (await subsystem(client, TARGET)).enabled, { timeout: 15_000 })
    .toBe(false);
  const s = await subsystem(client, TARGET);
  expect(s.enabled, 'бек помнит выключение «Заметок»').toBe(false);
  expect(s.active, 'в процессе вертикаль ещё поднята').toBe(true);
  expect(s.restartRequired, 'бек просит рестарт').toBe(true);
});

// ─── off-effect: ПОСЛЕ рестарта сервера ─────────────────────────────────────

test('после рестарта «Заметки» действительно выключены', async ({ page, context }) => {
  test.skip(PHASE !== 'off-effect', 'фаза off-effect');
  await loginViaUi(page, USER, PASS);

  const s = await subsystem(client, TARGET);
  expect(s.enabled, 'запись выключения на месте').toBe(false);
  expect(s.active, 'после рестарта вертикаль НЕ поднята').toBe(false);
  expect(s.restartRequired, 'расхождения больше нет').toBe(false);

  // API раздела отказывает (модуль не загружен) — не 200.
  const res = await request.get('/api/notes', { headers: { Authorization: `Bearer ${token}` } });
  expect(res.status(), `GET /api/notes должен отказывать (${res.status()})`).not.toBe(200);

  // Фронт берёт гейт из /api/auth/me — ключа «notes» там больше нет.
  const me = await client.get<{ subsystems?: string[] }>('/api/auth/me');
  expect(me.subsystems ?? [], 'в составе активных подсистем notes больше нет').not.toContain(TARGET);

  // Бэк не отдаёт и фронтовый remote модуля — интерфейсу нечего грузить.
  const modules = await client.get<{ items?: Array<{ id: string }> }>('/api/subsystem-modules');
  expect((modules.items ?? []).map(m => m.id), 'remote выключенного модуля не раздаётся').not.toContain(TARGET);

  // В интерфейсе раздел заметок не показывается: remote не грузится, значит и
  // вкладки «Заметки» в хабе нет. Ждём загрузки приложения и убеждаемся, что
  // запроса к remote модуля не было.
  const remoteRequests: string[] = [];
  page.on('request', r => { if (r.url().includes('/notes-remote/')) remoteRequests.push(r.url()); });
  await openApp(page);
  await page.waitForTimeout(8000);
  expect(remoteRequests, 'фронт не грузит remote выключенного модуля').toEqual([]);
  await expect(page.getByRole('button', { name: 'Заметки', exact: true })).toHaveCount(0);

  // В панели строка честно показывает «Выключен».
  await openPanel(page);
  await expect(row(page, TARGET)).toContainText('Выключен');
  await expect(switchOf(page, TARGET)).toHaveAttribute('aria-checked', 'false');
});

// ─── on: включаю обратно ────────────────────────────────────────────────────

test('включаю «Заметки» обратно тумблером', async ({ page, context }) => {
  test.skip(PHASE !== 'on', 'фаза on');
  await loginViaUi(page, USER, PASS);
  await openPanel(page);

  await switchOf(page, TARGET).click();
  await expect(row(page, TARGET)).toContainText('Включится');

  await expect.poll(async () => (await subsystem(client, TARGET)).enabled, { timeout: 15_000 })
    .toBe(true);
  const s = await subsystem(client, TARGET);
  expect(s.enabled, 'бек помнит включение').toBe(true);
  expect(s.active, 'в процессе вертикаль ещё не поднята').toBe(false);
  expect(s.restartRequired, 'бек просит рестарт').toBe(true);
});

// ─── on-effect: ПОСЛЕ рестарта ───────────────────────────────────────────────

test('после рестарта поведение «Заметок» вернулось', async ({ page, context }) => {
  test.skip(PHASE !== 'on-effect', 'фаза on-effect');
  await loginViaUi(page, USER, PASS);

  const s = await subsystem(client, TARGET);
  expect(s.enabled, 'запись включения на месте').toBe(true);
  expect(s.active, 'после рестарта вертикаль поднята').toBe(true);

  const res = await request.get('/api/notes', { headers: { Authorization: `Bearer ${token}` } });
  expect(res.status(), `GET /api/notes должен отвечать (${res.status()})`).toBe(200);

  const me = await client.get<{ subsystems?: string[] }>('/api/auth/me');
  expect(me.subsystems ?? [], 'в составе активных подсистем notes снова есть').toContain(TARGET);

  // Положительный контроль эффекта: remote модуля снова раздаётся, фронт его
  // грузит, и в хабе возвращается вкладка «Заметки».
  const remoteRequests: string[] = [];
  page.on('request', r => { if (r.url().includes('/notes-remote/')) remoteRequests.push(r.url()); });
  await openApp(page);
  await expect(page.getByRole('button', { name: 'Заметки', exact: true }).first())
    .toBeVisible({ timeout: 30_000 });
  expect(remoteRequests.length, 'фронт грузит remote включённого модуля').toBeGreaterThan(0);
});
