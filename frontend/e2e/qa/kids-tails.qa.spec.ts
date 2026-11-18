// Приёмка волны «Хвосты» детского режима (ADR-041, ХВ5–ХВ12) на стенде из ветки feat/kids-tails.
// Сценарии — по постановке QA-1 (задача 54827999): мобильный и планшетный список уроков,
// статус ребёнка в «Управлении пользователями», тач-цели шапок SidebarSection.
//
// Стенд: свой каталог данных, пул подписки anthropic-api-kids (живых ходов тьютора тут нет —
// проверяем интерфейс, а не модель). Скриншоты — в .cc-attachments/qa/kids-tails/shots.

import { test, expect, type APIRequestContext, type Page } from '@playwright/test';
import { api, collectConsoleErrors, type Api } from './helpers';
import { mkdirSync, writeFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const HERE = dirname(fileURLToPath(import.meta.url));
const SHOTS = resolve(HERE, '../../../.cc-attachments/qa/kids-tails/shots');

const ADMIN_U = process.env.E2E_USER || 'admin';
const ADMIN_P = process.env.E2E_PASS || '12345';
const CHILD_U = 'qa_kids_w3';
const CHILD_PW = 'Kidsqa12345';
const PENDING_U = 'qa_kids_w3p';
const PENDING_PW = 'Kidsqa12345';
const PROVIDER = 'anthropic-api-kids';
const MODEL = 'sonnet';

const MATH = { id: 'math', label: 'Математика', glyph: 'Мат', color: 'rose' };
const PHYS = { id: 'phys', label: 'Физика', glyph: 'Физ', color: 'info' };

test.describe.configure({ mode: 'serial' });

let adminReq: APIRequestContext;
let childReq: APIRequestContext;
let admin: Api;
let adminToken = '';
let childToken = '';
let pendingToken = '';
let childId = '';
let pendingId = '';
let projectId = '';

// ── засев ───────────────────────────────────────────────────────────────────────────────
function profileBody(over: Record<string, unknown> = {}) {
  return {
    enabled: true,
    guardianUserIds: [],
    providerKey: PROVIDER,
    model: MODEL,
    spaces: { lessons: { windows: [{ from: '00:00', to: '23:59' }], dailyMinutes: 30 } },
    subjects: [
      { ...MATH, providerKey: PROVIDER, model: MODEL, enabled: true },
      { ...PHYS, providerKey: PROVIDER, model: MODEL, enabled: true },
    ],
    dailyBudgetUsd: null,
    monthlyBudgetUsd: null,
    warnMinutes: 5,
    ...over,
  };
}

async function tokenOf(req: APIRequestContext, username: string, password: string): Promise<string> {
  const r = await req.post('/api/auth/login', { data: { username, password } });
  expect(r.ok(), `логин ${username} (${r.status()})`).toBeTruthy();
  return (await r.json()).token as string;
}

// Учётка заводится с нуля: иначе уроки прошлого прогона остаются в списке и счётчики врют.
async function freshUser(adminApi: Api, username: string, password: string): Promise<string> {
  const users = await adminApi.get<Array<{ id: string; username: string }>>('/api/users');
  const found = users.find(u => u.username === username);
  if (found) await adminApi.del(`/api/users/${found.id}`);
  const created = await adminApi.post<{ id: string }>('/api/users', {
    username, password, role: 'user', executionEnvironment: 'container',
  });
  return created.id;
}

test.beforeAll(async ({ playwright, baseURL }) => {
  mkdirSync(SHOTS, { recursive: true });
  adminReq = await playwright.request.newContext({ baseURL });
  childReq = await playwright.request.newContext({ baseURL });

  adminToken = await tokenOf(adminReq, ADMIN_U, ADMIN_P);
  admin = api(adminReq, adminToken);

  // Флаг прячет только интерфейс включения у админа — включаем его.
  await admin.put('/api/feature-flags/child-mode', { enabled: true });

  childId = await freshUser(admin, CHILD_U, CHILD_PW);
  pendingId = await freshUser(admin, PENDING_U, PENDING_PW);

  await admin.put(`/api/users/${childId}/child-profile`, profileBody());
  await admin.put(`/api/users/${pendingId}/child-profile`, profileBody());

  childToken = await tokenOf(childReq, CHILD_U, CHILD_PW);
  pendingToken = await tokenOf(childReq, PENDING_U, PENDING_PW);

  // Правила согласованы ручками (без живого хода тьютора): состояние ребёнка становится «ok».
  const rules = await childReq.post('/api/child/rules/start', { headers: { Authorization: `Bearer ${childToken}` } });
  expect(rules.status(), 'засев: чат правил').toBeLessThan(300);
  const rulesBody = await rules.json();
  const agreed = await childReq.post('/api/child/rules/agree', {
    headers: { Authorization: `Bearer ${childToken}` },
    data: { sessionId: rulesBody.sessionId, summary: 'Слушаю репетитора, стараюсь, прошу помощи, когда непонятно.' },
  });
  expect(agreed.status(), 'засев: правила согласованы').toBeLessThan(300);

  // Два урока: по математике (открытый предмет) и по физике (потом предмет выключим —
  // урок уедет в свёрнутую группу «Старые уроки»).
  for (const subject of [MATH.id, PHYS.id]) {
    const chat = await childReq.post('/api/child/spaces/lessons/chat', {
      headers: { Authorization: `Bearer ${childToken}` }, data: { subject },
    });
    expect(chat.status(), `засев: урок по предмету ${subject}`).toBe(201);
  }

  // Физику выключаем: её урок остаётся в списке, но как закрытый.
  const physOff = { ...PHYS, providerKey: PROVIDER, model: MODEL, enabled: false };
  await admin.put(`/api/users/${childId}/child-profile`, profileBody({ subjects: [
    { ...MATH, providerKey: PROVIDER, model: MODEL, enabled: true }, physOff,
  ] }));

  // Проект для панели «Свойства» документа (тач-цель шапки SidebarSection).
  const root = `/tmp/qa-kids-tails-docs-${Date.now()}`;
  const proj = await admin.post<{ id: string }>('/api/projects', {
    name: `qa-kids-tails-docs-${Date.now()}`,
    rootPath: root,
    createDirectory: true,
  });
  projectId = proj.id;
  mkdirSync(resolve(root, 'docs/qa'), { recursive: true });
  writeFileSync(resolve(root, '.docs'), JSON.stringify({
    folders: ['docs'],
    rootFiles: [],
    types: ['markdown'],
    docTypes: [{
      id: 'qa',
      title: 'Заметка QA',
      folders: ['docs/qa'],
      properties: [{ key: 'статус', kind: 'text', title: 'Статус' }],
    }],
  }), 'utf-8');
  writeFileSync(resolve(root, 'docs/qa/заметка-qa.md'), '# Заметка QA\n\nТело документа для проверки панели свойств.\n', 'utf-8');
});

test.afterAll(async () => {
  for (const id of [childId, pendingId]) {
    if (id) await admin?.del(`/api/users/${id}`).catch(() => {});
  }
  if (projectId) await admin?.del(`/api/projects/${projectId}`).catch(() => {});
  await adminReq?.dispose();
  await childReq?.dispose();
});

// Вход в приложение под токеном: роль приложение читает из cc_role.
async function enter(page: Page, token: string, role: 'admin' | 'user' = 'user', username = '') {
  await page.addInitScript(
    ({ t, r, u }) => {
      localStorage.setItem('cc_token', t);
      localStorage.setItem('cc_role', r);
      if (u) localStorage.setItem('cc_username', u);
    },
    { t: token, r: role, u: username },
  );
}

// Шум стенда: упавшие загрузки ресурсов.
function noise(errors: string[]): string[] {
  return errors.filter(e => !e.includes('Failed to load resource'));
}

async function isCoarse(page: Page): Promise<boolean> {
  return page.evaluate(() => matchMedia('(pointer: coarse)').matches);
}


// Плитка предмета на главной: в сетке сразу за подписью «Предметы» (не строка урока в острове —
// у той в имени сначала «Новый урок», потом предмет).
function subjectTile(page: Page, label: string) {
  return page.getByText('Предметы', { exact: true })
    .locator('xpath=following-sibling::div[1]')
    .getByRole('button')
    .filter({ hasText: label })
    .first();
}

function island(page: Page) {
  return page.locator('[data-testid="child-mobile-lessons-list"]');
}

// ── Сценарий 1: ребёнок на 360 ───────────────────────────────────────────────────────────
test('С1 (360): остров «Мои уроки», старые уроки в свёрнутой группе', async ({ page }) => {
  const errors = collectConsoleErrors(page);
  await page.setViewportSize({ width: 360, height: 800 });
  await enter(page, childToken, 'user', CHILD_U);
  await page.goto('/');

  const list = island(page);
  await expect(list, 'на 360 под главной есть остров «Мои уроки»').toBeVisible({ timeout: 30000 });
  await expect(list.getByText('Мои уроки')).toBeVisible();
  await page.screenshot({ path: resolve(SHOTS, 's1-360-home.png'), fullPage: true });

  // Открытый урок — в общем списке, без группы
  await expect(list.getByRole('button', { name: /Новый урок/ })).toHaveCount(1);

  // Закрытый урок — в свёрнутой группе «Старые уроки»: сама группа видна, урок внутри — нет
  const group = list.getByRole('button', { name: /Старые уроки/ });
  await expect(group, 'в списке есть свёрнутая группа «Старые уроки»').toBeVisible();
  await expect(list.getByRole('button', { name: /Новый урок/ }), 'до раскрытия урок в группе не видно')
    .toHaveCount(1);

  await group.click();
  await expect(list.getByRole('button', { name: /Новый урок/ }), 'после раскрытия урок из группы виден')
    .toHaveCount(2);
  await page.screenshot({ path: resolve(SHOTS, 's1-360-old-lessons-open.png'), fullPage: true });

  // Урок открывается из списка (не только через «Продолжить»)
  await list.getByRole('button', { name: /Новый урок/ }).last().click();
  await expect(page.locator('[data-child-lesson]'), 'урок открылся из списка').toBeVisible({ timeout: 30000 });
  await expect(page.getByText('Этот предмет закрыт')).toBeVisible();
  await page.screenshot({ path: resolve(SHOTS, 's1-360-closed-lesson.png'), fullPage: true });

  expect(noise(errors).filter(e => !e.includes('401') && !e.includes('child_mode'))).toEqual([]);
});

// ── Сценарий 2: ребёнок на 768 (планшет) ────────────────────────────────────────────────
test('С2 (768): остров есть, боковой панели нет, подпись предмета новая', async ({ page }) => {
  const errors = collectConsoleErrors(page);
  await page.setViewportSize({ width: 768, height: 1024 });
  await enter(page, childToken, 'user', CHILD_U);
  await page.goto('/');

  await expect(island(page), 'на 768 остров уроков есть').toBeVisible({ timeout: 30000 });
  await expect(island(page).getByText('Мои уроки')).toBeVisible();
  await page.screenshot({ path: resolve(SHOTS, 's2-768-home.png'), fullPage: true });

  // Внутри предмета: подпись зовёт прошлые уроки на главную
  await subjectTile(page, 'Математика').click();
  await expect(page.getByText('Начни новый урок. Прошлые уроки — на главной, в списке «Мои уроки».'))
    .toBeVisible({ timeout: 20000 });
  // Боковой панели на 768 нет: списка уроков рядом с предметом нет
  await expect(page.getByText('Старые уроки'), 'на 768 боковой панели нет').toHaveCount(0);
  await page.screenshot({ path: resolve(SHOTS, 's2-768-subject.png'), fullPage: true });

  expect(noise(errors).filter(e => !e.includes('401') && !e.includes('child_mode'))).toEqual([]);
});

// ── Сценарий 3: ребёнок на 1280 ──────────────────────────────────────────────────────────
test('С3 (1280): острова нет, боковая панель есть, подпись прежняя', async ({ page }) => {
  const errors = collectConsoleErrors(page);
  await page.setViewportSize({ width: 1280, height: 900 });
  await enter(page, childToken, 'user', CHILD_U);
  await page.goto('/');

  await expect(island(page), 'на 1280 острова уроков нет').toHaveCount(0);
  await expect(page.getByText('Мои уроки')).toHaveCount(0);
  await page.screenshot({ path: resolve(SHOTS, 's3-1280-home.png'), fullPage: true });

  await subjectTile(page, 'Математика').click();
  await expect(page.getByText('Выбери урок слева или начни новый.')).toBeVisible({ timeout: 20000 });
  // Боковая панель со списком уроков на месте
  await expect(page.getByRole('button', { name: /Старые уроки/ }), 'на 1280 боковая панель со списком уроков')
    .toBeVisible();
  await page.screenshot({ path: resolve(SHOTS, 's3-1280-subject.png'), fullPage: true });

  expect(noise(errors).filter(e => !e.includes('401') && !e.includes('child_mode'))).toEqual([]);
});

// ── Сценарий 4: правила не приняты — острова нет ────────────────────────────────────────
test('С4 (768, rules_pending): острова уроков нет', async ({ page }) => {
  await page.setViewportSize({ width: 768, height: 1024 });
  await enter(page, pendingToken, 'user', PENDING_U);
  await page.goto('/');

  await expect(page.getByText('Сначала договоримся о правилах')).toBeVisible({ timeout: 30000 });
  await expect(island(page), 'при rules_pending острова уроков нет').toHaveCount(0);
  await expect(page.getByText('Мои уроки')).toHaveCount(0);
  await page.screenshot({ path: resolve(SHOTS, 's4-768-rules-pending.png'), fullPage: true });
});

// ── Сценарий 5: тач-цели шапок SidebarSection ───────────────────────────────────────────
test('С5: тач-цель шапки SidebarSection у ребёнка', async ({ browser }) => {
  // Ребёнок, 360, сенсорная эмуляция: hasTouch даёт (pointer: coarse)
  const ctx = await browser.newContext({ hasTouch: true, viewport: { width: 360, height: 800 } });
  const page = await ctx.newPage();
  await enter(page, childToken, 'user', CHILD_U);
  await page.goto('/');
  expect(await isCoarse(page), 'сенсорная эмуляция включилась (pointer: coarse)').toBe(true);

  const list = island(page);
  await expect(list).toBeVisible({ timeout: 30000 });
  const header = list.getByRole('button', { name: /Старые уроки/ });
  await expect(header).toBeVisible();
  const boxTouch = await header.boundingBox();
  expect(boxTouch?.height ?? 0, 'шапка «Старые уроки» на тач-экране не меньше 40 px')
    .toBeGreaterThanOrEqual(40);
  await page.screenshot({ path: resolve(SHOTS, 's5-touch-child.png'), fullPage: true });

  await ctx.close();

  // Тот же экран мышью: плотность прежняя
  const ctxMouse = await browser.newContext({ viewport: { width: 360, height: 800 } });
  const pageMouse = await ctxMouse.newPage();
  await enter(pageMouse, childToken, 'user', CHILD_U);
  await pageMouse.goto('/');
  expect(await isCoarse(pageMouse), 'мышиная эмуляция включилась (pointer: fine)').toBe(false);
  const headerMouse = island(pageMouse).getByRole('button', { name: /Старые уроки/ });
  await expect(headerMouse).toBeVisible({ timeout: 30000 });
  const boxMouse = await headerMouse.boundingBox();
  expect(boxMouse?.height ?? 0, 'на десктопе шапка осталась прежней плотности')
    .toBeLessThan(40);
  await pageMouse.screenshot({ path: resolve(SHOTS, 's5-mouse-child.png'), fullPage: true });
  await ctxMouse.close();
});

// ── Сценарий 5б: тач-цель шапки «Свойства» в панели документа ───────────────────────────
test('С5б: тач-цель шапки «Свойства» документа', async ({ browser }) => {
  const ctx = await browser.newContext({ hasTouch: true, viewport: { width: 1280, height: 900 } });
  const page = await ctx.newPage();
  await enter(page, adminToken, 'admin', ADMIN_U);
  await page.goto(`/#/project/${projectId}`);
  expect(await isCoarse(page), 'сенсорная эмуляция включилась (pointer: coarse)').toBe(true);

  // Панель «Документация» рельсы проекта
  const rail = page.getByRole('button', { name: /Документация/ }).first();
  await expect(rail, 'кнопка панели «Документация» есть').toBeVisible({ timeout: 30000 });
  await rail.click();
  const doc = page.getByText('Заметка QA').first();
  await expect(doc, 'документ виден в панели').toBeVisible({ timeout: 30000 });
  // Панель «Документация»: двойной клик открывает превью, «Развернуть в центре» — полный просмотр
  await doc.dblclick({ force: true });
  await page.getByTitle('Развернуть в центре').first().click({ force: true });

  const props = page.getByRole('button', { name: /Свойства/ }).first();
  await expect(props, 'панель «Свойства» открылась').toBeVisible({ timeout: 30000 });
  const boxTouch = await props.boundingBox();
  expect(boxTouch?.height ?? 0, 'шапка «Свойства» на тач-экране не меньше 40 px')
    .toBeGreaterThanOrEqual(40);
  await page.screenshot({ path: resolve(SHOTS, 's5b-touch-doc-props.png'), fullPage: true });

  await ctx.close();

  // Мышь: плотность прежняя
  const ctxMouse = await browser.newContext({ viewport: { width: 1280, height: 900 } });
  const pageMouse = await ctxMouse.newPage();
  await enter(pageMouse, adminToken, 'admin', ADMIN_U);
  await pageMouse.goto(`/#/project/${projectId}`);
  expect(await isCoarse(pageMouse)).toBe(false);
  await pageMouse.getByRole('button', { name: /Документация/ }).first().click();
  await pageMouse.getByText('Заметка QA').first().dblclick({ force: true });
  await pageMouse.getByTitle('Развернуть в центре').first().click({ force: true });
  const propsMouse = pageMouse.getByRole('button', { name: /Свойства/ }).first();
  await expect(propsMouse).toBeVisible({ timeout: 30000 });
  const boxMouse = await propsMouse.boundingBox();
  expect(boxMouse?.height ?? 0, 'на десктопе шапка «Свойства» осталась прежней плотности')
    .toBeLessThan(40);
  await pageMouse.screenshot({ path: resolve(SHOTS, 's5b-mouse-doc-props.png'), fullPage: true });
  await ctxMouse.close();
});

// ── Сценарий 6: админ → управление пользователями ──────────────────────────────────────
test('С6: статус ребёнка, отказ с «Повторить», повторная загрузка, тьютор из ответа', async ({ page }) => {
  const errors = collectConsoleErrors(page);
  await page.setViewportSize({ width: 1280, height: 900 });
  await enter(page, adminToken, 'admin', ADMIN_U);
  await page.goto('/');

  // Модалка «Пользователи»
  for (const t of await page.locator('[data-testid="toast-layer"] button[title="Закрыть"]').all()) {
    await t.click({ timeout: 2000 }).catch(() => {});
  }
  await page.locator('[role="button"][aria-label^="Меню пользователя"]').click({ force: true });
  await page.getByRole('button', { name: 'Пользователи' }).click();
  const row = page.locator(`[data-testid="user-row-${childId}"]`);
  await expect(row).toBeVisible({ timeout: 20000 });

  // Строка статуса ДО снимка профиля: нейтральная, ничего не утверждает
  const toggle = row.locator('[data-testid="child-section-toggle"]');
  await expect(toggle, 'строка статуса не утверждает «выключен» без снимка')
    .not.toHaveText(/выключен/);
  await expect(toggle).toHaveText(/Детский режим/);
  await page.screenshot({ path: resolve(SHOTS, 's6-status-before.png'), fullPage: true });

  // Отказ загрузки профиля → Notice с «Повторить» ВНУТРИ
  await page.route('**/api/users/*/child-profile', route => route.fulfill({
    status: 500, contentType: 'application/json',
    body: JSON.stringify({ code: 'boom', message: 'Сбой стенда' }),
  }));
  await toggle.click();
  const notice = page.locator('[data-testid="child-profile-error"]');
  await expect(notice, 'при отказе показана Notice').toBeVisible({ timeout: 20000 });
  await expect(notice.getByRole('button', { name: 'Повторить' }), '«Повторить» внутри Notice').toBeVisible();
  await expect(notice).toContainText('Сбой стенда');
  await page.screenshot({ path: resolve(SHOTS, 's6-error-notice.png'), fullPage: true });

  // «Повторить» после снятия обрыва — профиль загрузился
  await page.unroute('**/api/users/*/child-profile');
  await notice.getByRole('button', { name: 'Повторить' }).click();
  await expect(page.locator('[data-testid="child-profile-editor"]'), 'после «Повторить» профиль загружен')
    .toBeVisible({ timeout: 20000 });
  await expect(toggle, 'строка статуса после снимка — «включён»').toHaveText(/включён/);

  // Повторное раскрытие пользователя грузит профиль заново
  let gets = 0;
  page.on('request', r => {
    if (r.method() === 'GET' && r.url().includes(`/api/users/${childId}/child-profile`)) gets++;
  });
  await toggle.click(); // свернуть
  await expect(page.locator('[data-testid="child-profile-editor"]')).toHaveCount(0);
  await toggle.click(); // раскрыть снова
  await expect(page.locator('[data-testid="child-profile-editor"]')).toBeVisible({ timeout: 20000 });
  expect(gets, 'повторное раскрытие снова грузит профиль').toBeGreaterThan(0);

  // Репетитор нового предмета — из ответа сервера
  const editor = page.locator('[data-testid="child-profile-editor"]');
  await editor.locator('[data-testid="profile-tabs"]').getByRole('tab', { name: 'Предметы' }).click();
  await expect(page.locator('[data-testid="child-subjects-editor"]')).toBeVisible({ timeout: 20000 });
  await page.locator('[data-testid="add-subject"]').click();
  const newRow = page.locator('[data-testid="subject-row"]').last();
  await newRow.locator('input[placeholder="Математика"]').fill('История');
  await newRow.locator('input[placeholder="Мат"]').fill('Ист');
  await newRow.locator('[data-testid="subject-color-rose"]').click();
  const picker = newRow.locator('[data-testid="child-route-picker"]');
  if (await picker.locator('select').count() > 0) {
    await picker.locator('select').first().selectOption(PROVIDER);
    await picker.locator('select').nth(1).selectOption(MODEL);
  }
  const put = page.waitForResponse(r => r.url().includes('/child-profile') && r.request().method() === 'PUT');
  await page.locator('[data-testid="save-profile"]').click();
  const putResp = await put;
  expect(putResp.ok(), `сохранение профиля → ${putResp.status()}`).toBeTruthy();
  const putBody = await putResp.json() as { tutorPersonaId?: string; profile?: any };

  // «Персона-тьютор» в редакторе — ровно тот id, что вернул сервер (не «ещё нет» и не копия клиента)
  await editor.locator('[data-testid="profile-tabs"]').getByRole('tab', { name: 'Режим и время' }).click();
  const tutorRow = page.locator('[data-testid="tutor-state"]');
  await expect(tutorRow, 'строка «Персона-тьютор» есть').toBeVisible({ timeout: 20000 });
  await expect(tutorRow).not.toHaveText(/ещё нет/);
  const shownTutor = (await tutorRow.innerText()).trim();
  expect(putBody.tutorPersonaId, 'ответ сервера содержит id тьютора').toBeTruthy();
  expect(shownTutor, 'в редакторе показан id тьютора из ответа сервера').toContain(putBody.tutorPersonaId!);
  await page.screenshot({ path: resolve(SHOTS, 's6-tutor-from-response.png'), fullPage: true });

  expect(noise(errors).filter(e => !e.includes('401'))).toEqual([]);
});
