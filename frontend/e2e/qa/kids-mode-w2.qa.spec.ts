// Приёмка волны 2 «Предметы и репетиторы» (ADR-041) на стенде из ветки feat/kids-tutors.
// Сценарии 1–9 и мобильная ширина 360 — по постановке QA-1 (задача eb91954d).
// Скриншоты — в .cc-attachments/qa/kids-w2/shots.
//
// Стенд настроен так: пул «anthropic-api-kids» (фейковый ключ), облачный kids-cloud и локальный
// kids-local (фейковый Anthropic-совместимый движок на :5599), фейковый Dify на :8081 с двумя базами.
// Живой ход тьютора идёт по локальному маршруту ребёнка → в фейковый движок.
//
// Админский сценарий идёт в ОДНОЙ вкладке: модалка «Пользователи» держит живой профиль ребёнка
// в памяти (отдельной GET-ручки профиля нет — ADR-041, ручка №10, только PUT), поэтому после
// перезагрузки страницы редактор снова пустой.

import { test, expect, type APIRequestContext, type Page } from '@playwright/test';
import { api, collectConsoleErrors, type Api } from './helpers';
import { mkdirSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const HERE = dirname(fileURLToPath(import.meta.url));
const SHOTS = resolve(HERE, '../../../.cc-attachments/qa/kids-w2/shots');
const FIXTURES = resolve(HERE, '../../../.cc-attachments/qa/kids-w2/fixtures');

const CHILD_PW = 'Kidsqa12345';
const CHILD_U = 'qa_kids_w2';
const ADMIN_U = process.env.E2E_USER || 'admin';
const ADMIN_P = process.env.E2E_PASS || '12345';
const PROVIDER = 'anthropic-api-kids';
const SUBJECT_LABEL = 'Математика';
const SUBJECT_GLYPH = 'Мат';
const ACK_LABEL = 'Понимаю риск и разрешаю этот маршрут';

const LESSONS_OK = { from: '00:00', to: '23:59' };

function profileBody(over: Record<string, unknown> = {}) {
  return {
    enabled: true,
    guardianUserIds: [],
    providerKey: PROVIDER,
    model: 'sonnet',
    spaces: { lessons: { windows: [LESSONS_OK], dailyMinutes: 30 } },
    subjects: [],
    dailyBudgetUsd: null,
    monthlyBudgetUsd: null,
    warnMinutes: 5,
    ...over,
  };
}

// Сырой вызов: нужны и коды отказа (400/403/404/409/415/422/503), а не только успех.
async function call(
  req: APIRequestContext, method: string, url: string, token: string, data?: unknown,
): Promise<{ status: number; body: any }> {
  const r = await (req as any)[method](url, {
    headers: { Authorization: `Bearer ${token}` },
    ...(data === undefined ? {} : { data }),
  });
  const text = await r.text();
  let body: any = undefined;
  try { body = text ? JSON.parse(text) : undefined; } catch { body = text; }
  return { status: r.status(), body };
}

async function tokenOf(req: APIRequestContext, username: string, password: string): Promise<string> {
  const r = await req.post('/api/auth/login', { data: { username, password } });
  expect(r.ok(), `логин ${username} (${r.status()})`).toBeTruthy();
  return (await r.json()).token as string;
}

test.describe.configure({ mode: 'serial' });

let adminReq: APIRequestContext;
let childReq: APIRequestContext;
let admin: Api;
let adminToken: string;
let childToken: string;
let childId: string;
let subjectId = '';
let subjectPersonaId = '';
let adminPage: Page;

// Учётки, профиль и админская вкладка — до всех тестов: сценарии идут по порядку.
test.beforeAll(async ({ playwright, browser, baseURL }) => {
  mkdirSync(SHOTS, { recursive: true });
  adminReq = await playwright.request.newContext({ baseURL });
  childReq = await playwright.request.newContext({ baseURL });

  adminToken = await tokenOf(adminReq, ADMIN_U, ADMIN_P);
  admin = api(adminReq, adminToken);

  // Флаг прячет только интерфейс включения у админа — включаем его.
  await admin.put('/api/feature-flags/child-mode', { enabled: true });

  const users = await admin.get<Array<{ id: string; username: string }>>('/api/users');
  const existing = users.find(u => u.username === CHILD_U);
  childId = existing?.id ?? (await admin.post<{ id: string }>('/api/users', {
    username: CHILD_U, password: CHILD_PW, role: 'user', executionEnvironment: 'container',
  })).id;

  // Ребёнок с профилем, но без предметов: предмет создаёт админ в интерфейсе (сценарий 1).
  await admin.put(`/api/users/${childId}/child-profile`, profileBody());
  // Детский клиент не строим: все детские ручки идут через call(childReq, …, childToken)
  childToken = await tokenOf(childReq, CHILD_U, CHILD_PW);

  adminPage = await browser.newPage();
  await adminPage.setViewportSize({ width: 1280, height: 900 });
  await enter(adminPage, adminToken, 'admin', ADMIN_U);
  adminPage.on('request', r => {
    if (r.url().includes('/child-profile') && r.method() === 'PUT') {
      console.log('QA-PUT-BODY', (r.postData() ?? '').slice(0, 1200));
    }
  });
  await adminPage.goto('/');
  await openEditor(adminPage);
  await fillMode(adminPage);
});

test.afterAll(async () => {
  await adminPage?.close();
  await adminReq?.dispose();
  await childReq?.dispose();
});

// Вход в приложение под токеном пользователя. Роль приложение читает из cc_role (localStorage).
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

// Шум стенда: упавшие загрузки ресурсов. Ошибки JS остаются в проверке.
function noise(errors: string[]): string[] {
  return errors.filter(e => !e.includes('Failed to load resource'));
}

// Детская лента дёргает взрослую ручку /api/personas — сервер отвечает 403 child_mode (дефект волны 1).
const KNOWN_CHILD_403 = ['/api/personas', '/api/yandex/account'];
function onlyKnownChildMode(bad: string[]): string[] {
  return bad.filter(b => !KNOWN_CHILD_403.some(k => b.includes(k)));
}
function badCollector(page: Page): string[] {
  const bad: string[] = [];
  page.on('response', r => { if (r.status() >= 400) bad.push(`${r.status()} ${r.request().method()} ${r.url()}`); });
  return bad;
}

// Модалка «Пользователи» → раздел «Детский режим» ребёнка → редактор профиля.
async function openEditor(page: Page): Promise<void> {
  // Всплывающие уведомления перекрывают меню аватара — снимаем их, если есть
  for (const t of await page.locator('[data-testid="toast-layer"] button[title="Закрыть"]').all()) {
    await t.click({ timeout: 2000 }).catch(() => {});
  }
  // force: тосты перекрывают меню, а попадать по ним нельзя — жмём сам элемент
  await page.locator('[role="button"][aria-label^="Меню пользователя"]').click({ force: true });
  await page.getByRole('button', { name: 'Пользователи' }).click();
  await expect(page.getByText('Пользователи').first()).toBeVisible();
  const row = page.locator(`[data-testid="user-row-${childId}"]`);
  await expect(row).toBeVisible();
  const toggle = row.locator('[data-testid="child-section-toggle"]');
  if (await toggle.isVisible()) await toggle.click();
  await expect(page.locator('[data-testid="child-profile-editor"]')).toBeVisible({ timeout: 20000 });
}

// Вкладка «Режим и время»: включить режим, окно «Уроков», минут в день.
async function fillMode(page: Page): Promise<void> {
  const editor = page.locator('[data-testid="child-profile-editor"]');
  await editor.locator('[data-testid="profile-tabs"]').getByRole('tab', { name: 'Режим и время' }).click();
  const cb = editor.getByRole('checkbox', { name: 'Детский режим' }).first();
  if (!(await cb.isChecked())) await cb.click();
  if (await editor.locator('[data-testid="time-window"]').count() === 0) {
    await editor.locator('[data-testid="add-window"]').click();
  }
  const win = editor.locator('[data-testid="time-window"]').first();
  await win.locator('input[placeholder="09:00"]').fill('00:00');
  await win.locator('input[placeholder="20:00"]').fill('23:59');
  await editor.locator('input[placeholder="30"]').fill('30');
}

// Вкладка «Предметы»: маршрут помощника (профиля) — запись пула.
async function subjectsTab(page: Page): Promise<void> {
  const editor = page.locator('[data-testid="child-profile-editor"]');
  await editor.locator('[data-testid="profile-tabs"]').getByRole('tab', { name: 'Предметы' }).click();
  await expect(page.locator('[data-testid="child-subjects-editor"]')).toBeVisible({ timeout: 20000 });
  const assistant = page.locator('[data-testid="assistant-route"] [data-testid="child-route-picker"]');
  await assistant.locator('select').first().selectOption(PROVIDER);
  await assistant.locator('select').nth(1).selectOption('sonnet');
}


// Сохранение профиля: ждём ответ ручки, иначе проверка состояния уходит вперёд запроса.
async function saveProfile(page: Page): Promise<void> {
  const done = page.waitForResponse(r => r.url().includes('/child-profile') && r.request().method() === 'PUT');
  await page.locator('[data-testid="save-profile"]').click();
  const resp = await done;
  expect(resp.ok(), `сохранение профиля → ${resp.status()}`).toBeTruthy();
  await expect(page.getByText('Сохранить не удалось')).toHaveCount(0, { timeout: 20000 });
}

// Строка предмета в редакторе (по названию).
function subjectRow(page: Page, label = SUBJECT_LABEL) {
  return page.locator('[data-testid="subject-row"]').filter({ hasText: label }).first();
}

// ── Сценарий 1: предмет ────────────────────────────────────────────────────────────────
test('С1: предмет — добавление, сохранение, репетитор создан, файлы только после сохранения', async () => {
  const page = adminPage;
  const errors = collectConsoleErrors(page);
  const bad = badCollector(page);
  await subjectsTab(page);

  // Пустой список предметов: админ в свежей модалке профиля не видит живых предметов
  await expect(page.getByText('Предметов нет')).toBeVisible();

  // Добавить предмет: название, глиф, цвет
  await page.locator('[data-testid="add-subject"]').click();
  // Строка берётся по номеру: после ввода названия поиск по тексту перестал бы её находить
  const row = page.locator('[data-testid="subject-row"]').first();
  // Пока названия нет — строка помечена «новый»
  await expect(row.getByText('новый')).toBeVisible();
  await row.locator('input[placeholder="Математика"]').fill(SUBJECT_LABEL);
  await row.locator('input[placeholder="Мат"]').fill(SUBJECT_GLYPH);
  await row.locator('[data-testid="subject-color-rose"]').click();
  await page.screenshot({ path: resolve(SHOTS, 's1-new-subject.png') });

  // У несохранённого предмета вместо блока файлов — строка про сохранение
  await row.locator('[data-testid="subject-open"]').click();
  await expect(page.locator('[data-testid="subject-materials-unsaved"]')).toBeVisible();
  await expect(page.getByText('Файлы можно добавить после сохранения профиля.')).toBeVisible();
  await expect(page.locator('[data-testid="child-subject-materials"]')).toHaveCount(0);
  // Карточка репетитора без персоны говорит прямо
  await expect(page.getByText('Репетитор появится после сохранения профиля')).toBeVisible();

  // Сохранить
  await saveProfile(page);

  // Предмет жив на сервере: ребёнок видит его в своих предметах
  const subs = await call(childReq, 'get', '/api/child/subjects', childToken);
  expect(subs.status, 'список предметов ребёнка').toBe(200);
  const s = (subs.body?.subjects ?? []).find((x: any) => x.label === SUBJECT_LABEL);
  expect(s, 'предмет сохранён и виден ребёнку').toBeTruthy();
  subjectId = s.id;
  expect(s.glyph).toBe(SUBJECT_GLYPH);
  expect(s.color).toBe('rose');
  // У предмета создался репетитор
  expect(s.tutor?.personaId, 'у предмета есть репетитор').toBeTruthy();
  subjectPersonaId = s.tutor.personaId;

  await page.screenshot({ path: resolve(SHOTS, 's1-subject-saved.png') });
  expect(noise(errors)).toEqual([]);
  expect(bad.filter(b => !b.includes('404') && !b.includes('avatar/generate') && !b.includes('400 PUT') && !b.includes('child-profile') && !(b.includes('503') && b.includes('/materials')))).toEqual([]);
});

// ── Сценарий 2: карточка репетитора ─────────────────────────────────────────────────────
test('С2: карточка репетитора — имя, характер, голос без «по умолчанию», аватар, правила >12', async () => {
  const page = adminPage;
  const errors = collectConsoleErrors(page);
  const bad = badCollector(page);
  await subjectsTab(page);
  await subjectRow(page).locator('[data-testid="subject-open"]').click();

  const card = page.locator('[data-testid="child-tutor-card"]');
  await expect(card).toBeVisible();
  await expect(card.getByText('Репетитор', { exact: true })).toBeVisible();

  // Имя и характер — правятся
  await card.locator('input[placeholder="Сова Ульяна"]').fill('Лисик Ульяна');
  await card.locator('textarea[aria-label="Характер"]').fill('Добрый лесовичок, объясняет на примерах из леса.');

  // Голос: в СПИСКЕ голосов нет строки «Голос по умолчанию» (в поле показана подпись текущего
  // голоса, а выбираемой строки нет — снять голос этой карточкой нечем)
  await expect(card.getByText('голос, который задан на сервере')).toHaveCount(0);
  await card.locator('button').filter({ hasText: 'Голос по умолчанию' }).first().click();
  await expect(card.getByText('голос, который задан на сервере')).toHaveCount(0);
  await page.keyboard.press('Escape');

  // Правила: больше 12 строк — счётчик красный и поле в состоянии ошибки
  const mustDo = card.locator('textarea[aria-label="Обязательно"]');
  const thirteen = Array.from({ length: 13 }, (_, i) => `Правило номер ${i + 1}`).join('\n');
  await mustDo.fill(thirteen);
  await expect(card.getByText('13/12')).toBeVisible();
  await expect(mustDo).toHaveAttribute('aria-invalid', 'true');
  await page.screenshot({ path: resolve(SHOTS, 's2-rules-over-limit.png') });
  // Возвращаем в допустимое состояние, чтобы сохранение прошло
  await mustDo.fill('Хвали за попытку, даже если ответ неверный');

  // Аватар: генерация (на стенде генератор выключен — ждём отказ), затем загрузка файла
  await card.getByRole('button', { name: 'Сгенерировать' }).click();
  await expect(card.getByText('Сохранить не удалось')).toBeVisible({ timeout: 60000 });
  const genError = await card.getByText('Сохранить не удалось').locator('..').innerText();
  expect(genError.length, 'отказ генерации аватара с текстом').toBeGreaterThan(0);
  await page.screenshot({ path: resolve(SHOTS, 's2-avatar-generate-refused.png') });

  const chooser = page.waitForEvent('filechooser');
  await card.getByRole('button', { name: 'Загрузить' }).click();
  (await chooser).setFiles(resolve(FIXTURES, 'страница.png'));
  await expect(page.getByText('Аватар репетитора')).toBeVisible();
  await page.screenshot({ path: resolve(SHOTS, 's2-avatar-crop.png') });
  await page.getByRole('button', { name: /^Применить/ }).first().click();
  await expect(page.getByText('Аватар репетитора')).toHaveCount(0, { timeout: 30000 });

  // Сохранить карточку
  const tutorPut = page.waitForResponse(r => r.url().includes('/child-tutors/') && r.request().method() === 'PUT');
  await card.getByRole('button', { name: 'Сохранить' }).click();
  const tutorResp = await tutorPut;
  expect(tutorResp.ok(), `правка репетитора → ${tutorResp.status()}`).toBeTruthy();
  await expect(card.getByText('Сохранить не удалось')).toHaveCount(0, { timeout: 30000 });

  // Имя репетитора доехало до ребёнка
  const subs = await call(childReq, 'get', '/api/child/subjects', childToken);
  const s = (subs.body?.subjects ?? []).find((x: any) => x.label === SUBJECT_LABEL);
  expect(s?.tutor?.name, 'имя репетитора сохранено').toBe('Лисик Ульяна');

  // Аватар репетитора отдаётся админу
  const av = await call(adminReq, 'get', `/api/users/${childId}/child-tutors/${subjectPersonaId}/avatar`, adminToken);
  expect(av.status, 'аватар репетитора отдаётся').toBe(200);

  await page.screenshot({ path: resolve(SHOTS, 's2-tutor-card.png') });
  expect(noise(errors)).toEqual([]);
  expect(bad.filter(b => !b.includes('404') && !b.includes('avatar/generate') && !b.includes('400 PUT') && !b.includes('child-profile') && !(b.includes('503') && b.includes('/materials')))).toEqual([]);
});

// ── Сценарий 3: маршрут модели ─────────────────────────────────────────────────────────
test('С3: маршрут модели — пул, облако с согласием, локальный без бюджета, отзыв согласия', async () => {
  const page = adminPage;
  const errors = collectConsoleErrors(page);
  const bad = badCollector(page);
  await subjectsTab(page);
  await subjectRow(page).locator('[data-testid="subject-open"]').click();

  const row = subjectRow(page);
  const picker = row.locator('[data-testid="child-route-picker"]');
  const provider = picker.locator('select').first();
  const model = picker.locator('select').nth(1);

  // В списке поставщиков есть все три вида
  const opts = await provider.locator('option').allTextContents();
  expect(opts.join('|'), 'в списке есть запись пула').toContain('Детский ключ Anthropic');
  expect(opts.join('|'), 'в списке есть облачный поставщик').toContain('Фейковое облако');
  expect(opts.join('|'), 'в списке есть локальная модель').toContain('Фейковая локальная');
  const groups = await provider.locator('optgroup').evaluateAll((els: any[]) => els.map(e => e.label ?? ''));
  expect(groups.join('|')).toContain('Anthropic · записи пула с API-ключом');
  expect(groups.join('|')).toContain('Облачные поставщики');
  expect(groups.join('|')).toContain('Локальные модели');

  // Пул: предупреждения и согласия нет
  await provider.selectOption(PROVIDER);
  await expect(picker.getByText('Anthropic по API-ключу')).toBeVisible();
  await expect(picker.getByText(ACK_LABEL)).toHaveCount(0);

  // Облачный поставщик: предупреждение и галка согласия
  await provider.selectOption('kids-cloud');
  await expect(picker.getByText(/Переписка уходит поставщику/)).toBeVisible();
  await expect(picker.getByText(ACK_LABEL)).toBeVisible();

  // Без галки сохранение отказывает
  const refused = page.waitForResponse(r => r.url().includes('/child-profile') && r.request().method() === 'PUT');
  await page.locator('[data-testid="save-profile"]').click();
  expect((await refused).status(), 'без галки сохранение отказано').toBe(400);
  await expect(page.getByText('Сохранить не удалось')).toBeVisible({ timeout: 20000 });
  await expect(page.getByText(/требует согласия|нужно явное согласие администратора/).first()).toBeVisible();
  await page.screenshot({ path: resolve(SHOTS, 's3-no-ack.png') });

  // С галкой — сохраняется, и появляется строка «Разрешено …»
  await picker.getByRole('checkbox', { name: ACK_LABEL }).click();
  await saveProfile(page);
  await expect(row.locator('[data-testid="subject-ack-at"]')).toBeVisible({ timeout: 20000 });

  // Локальный маршрут: «бюджет не тратится»
  await provider.selectOption('kids-local');
  await expect(picker.getByText('бюджет не тратится')).toBeVisible();
  await expect(picker.getByText('Ограничивают только окна и минуты в день.')).toBeVisible();
  await picker.getByRole('checkbox', { name: ACK_LABEL }).click();
  await saveProfile(page);
  await expect(row.locator('[data-testid="subject-ack-at"]')).toBeVisible({ timeout: 20000 });
  await page.screenshot({ path: resolve(SHOTS, 's3-local-route.png') });

  // Снятие галки у сохранённого предмета отзывает согласие
  await picker.getByRole('checkbox', { name: ACK_LABEL }).click();
  await saveProfile(page);
  await expect(row.locator('[data-testid="subject-ack-at"]')).toHaveCount(0, { timeout: 20000 });
  await page.screenshot({ path: resolve(SHOTS, 's3-ack-revoked.png') });

  // Возвращаем локальный маршрут с согласием — на нём пойдёт живой ход ребёнка (сценарий 8)
  await provider.selectOption('kids-local');
  await picker.getByRole('checkbox', { name: ACK_LABEL }).click();
  await expect(model).toHaveValue('kids-local-1');
  await saveProfile(page);
  await expect(row.locator('[data-testid="subject-ack-at"]')).toBeVisible({ timeout: 20000 });

  expect(noise(errors)).toEqual([]);
  expect(bad.filter(b => !b.includes('404') && !b.includes('avatar/generate') && !b.includes('400 PUT') && !b.includes('child-profile') && !(b.includes('503') && b.includes('/materials')))).toEqual([]);
});

// ── Сценарий 4: база знаний ────────────────────────────────────────────────────────────
test('С4: база знаний — выбор, отвязка («Без базы»), ошибка привязки', async () => {
  const page = adminPage;
  const errors = collectConsoleErrors(page);
  const bad = badCollector(page);
  await subjectsTab(page);
  await subjectRow(page).locator('[data-testid="subject-open"]').click();

  const picker = page.locator('[data-testid="child-knowledge-picker"]');
  await expect(picker).toBeVisible();
  const select = picker.locator('select');
  const opts = await select.locator('option').allTextContents();
  expect(opts[0], 'первым пунктом идёт «Без базы»').toBe('Без базы');
  expect(opts.join('|')).toContain('Математика QA');

  // Выбрать базу и сохранить
  await select.selectOption('qa-ds-math');
  await saveProfile(page);

  // Отвязать: «Без базы»
  await page.locator('[data-testid="child-knowledge-picker"]').locator('select').selectOption('');
  await saveProfile(page);

  // Ошибка привязки: неизвестная база — отказ сервера
  const prof = profileBody({ subjects: [{ id: subjectId, label: SUBJECT_LABEL, glyph: SUBJECT_GLYPH, color: 'rose', providerKey: PROVIDER, model: 'sonnet', enabled: true, knowledge: { datasetId: 'нет-такой-базы' } }] });
  const r = await call(adminReq, 'put', `/api/users/${childId}/child-profile`, adminToken, prof);
  expect(r.status, 'неизвестная база знаний — отказ').toBe(400);
  expect(String(r.body?.code ?? '')).toContain('dataset_not_attachable');

  await page.screenshot({ path: resolve(SHOTS, 's4-knowledge.png') });
  expect(noise(errors)).toEqual([]);
  expect(bad.filter(b => !b.includes('404') && !b.includes('avatar/generate') && !b.includes('400 PUT') && !b.includes('child-profile') && !(b.includes('503') && b.includes('/materials')))).toEqual([]);
});

// ── Сценарий 5: файлы предмета ─────────────────────────────────────────────────────────
test('С5: файлы предмета — загрузка pdf/docx/фото, удаление, отказ по фото', async () => {
  const page = adminPage;
  const errors = collectConsoleErrors(page);
  const bad = badCollector(page);
  await subjectsTab(page);
  await subjectRow(page).locator('[data-testid="subject-open"]').click();

  const materials = page.locator('[data-testid="child-subject-materials"]');
  await expect(materials).toBeVisible();
  // Опись файлов переживает пересоздание предмета (каталог по id предмета) — считаем от текущей
  const base = (await call(adminReq, 'get', `/api/users/${childId}/child-subjects/${subjectId}/materials`, adminToken)).body;
  const baseCount = (Array.isArray(base) ? base : (base?.materials ?? base?.items ?? [])).length;

  // Текстовый файл — принимается
  const chooser = page.waitForEvent('filechooser');
  await materials.locator('[data-testid="material-upload"]').click();
  (await chooser).setFiles(resolve(FIXTURES, 'задачи.txt'));
  await expect(page.locator('[data-testid="material-row"]')).toHaveCount(baseCount + 1, { timeout: 30000 });

  // PDF — принимается, у файла есть предпросмотр текста
  const chooser2 = page.waitForEvent('filechooser');
  await materials.locator('[data-testid="material-upload"]').click();
  (await chooser2).setFiles(resolve(FIXTURES, 'уроки.pdf'));
  await expect(page.locator('[data-testid="material-row"]')).toHaveCount(baseCount + 2, { timeout: 30000 });
  // Превью «так репетитор прочтёт эту страницу» — блок под строкой загруженного файла
  await expect(page.locator('[data-testid="material-preview"]')).toBeVisible({ timeout: 20000 });
  await expect(page.locator('[data-testid="material-preview-text"]')).toContainText(/5\+5=10|Task five/);
  await page.locator('[data-testid="preview-toggle"]').click();
  await expect(page.locator('[data-testid="material-preview-text"]')).toHaveCount(0);
  await page.locator('[data-testid="preview-toggle"]').click();
  await expect(page.locator('[data-testid="material-preview-text"]')).toBeVisible();

  // DOCX — принимается
  const chooser3 = page.waitForEvent('filechooser');
  await materials.locator('[data-testid="material-upload"]').click();
  (await chooser3).setFiles(resolve(FIXTURES, 'сочинение.docx'));
  await expect(page.locator('[data-testid="material-row"]')).toHaveCount(baseCount + 3, { timeout: 30000 });

  // Фото страницы: OCR на стенде недоступен — ждём отказ с текстом
  const chooser4 = page.waitForEvent('filechooser');
  await materials.locator('[data-testid="material-upload"]').click();
  (await chooser4).setFiles(resolve(FIXTURES, 'страница.png'));
  await expect(page.locator('[data-testid="material-reject"]')).toBeVisible({ timeout: 60000 });
  const rejectText = await page.locator('[data-testid="material-reject"]').innerText();
  expect(rejectText.length, 'в отказе есть текст').toBeGreaterThan(0);
  await page.screenshot({ path: resolve(SHOTS, 's5-materials.png') });

  // Список на сервере совпадает с тем, что видит админ
  const list = await call(adminReq, 'get', `/api/users/${childId}/child-subjects/${subjectId}/materials`, adminToken);
  expect(list.status, 'список файлов на сервере').toBe(200);
  const files = Array.isArray(list.body) ? list.body : (list.body?.materials ?? list.body?.items ?? []);
  expect(files.length, 'на сервере столько же файлов, сколько принято интерфейсом').toBe(baseCount + 3);
  expect(files.filter((x: any) => /\.png$/i.test(x.name)).length, 'отклонённое фото не попало в опись').toBe(0);

  // Удаление файла
  await page.locator('[data-testid="material-row"] button[aria-label^="Удалить файл"]').first().click();
  await expect(page.locator('[data-testid="material-confirm"]')).toBeVisible();
  await page.locator('[data-testid="material-remove-yes"]').first().click();
  await expect(page.locator('[data-testid="material-row"]')).toHaveCount(baseCount + 2, { timeout: 20000 });
  const list2 = await call(adminReq, 'get', `/api/users/${childId}/child-subjects/${subjectId}/materials`, adminToken);
  const files2 = Array.isArray(list2.body) ? list2.body : (list2.body?.materials ?? list2.body?.items ?? []);
  expect(files2.length, 'файл удалён и на сервере').toBe(files.length - 1);

  expect(noise(errors)).toEqual([]);
  expect(bad.filter(b => !b.includes('404') && !b.includes('avatar/generate') && !b.includes('400 PUT') && !b.includes('child-profile') && !(b.includes('503') && b.includes('/materials')))).toEqual([]);
});

// ── Сценарий 7: ребёнок видит предметы ──────────────────────────────────────────────────
test('С7: ребёнок — предметы на главной в своих цветах, в шапке урока репетитор с аватаром', async ({ page }) => {
  const errors = collectConsoleErrors(page);
  const bad = badCollector(page);
  await page.setViewportSize({ width: 1280, height: 900 });
  await enter(page, childToken, 'user', CHILD_U);
  await page.goto('/');

  // Первый вход: онбординг правил — ребёнок с админом договариваются о правилах
  const start = page.getByRole('button', { name: 'Начать вместе с папой' });
  const tileEarly = page.getByRole('button', { name: new RegExp(SUBJECT_LABEL) }).first();
  await expect(start.or(tileEarly)).toBeVisible({ timeout: 30000 });
  if (await start.isVisible()) {
    await start.click();
    await page.waitForTimeout(2500);
    console.log('QA-BAD', JSON.stringify(bad));
    // Шаги договора: кнопка следующего шага называется «Дальше» или «Нам подходит» — зависит от режима
    for (let i = 0; i < 8; i++) {
      if (await page.getByRole('button', { name: 'Договорились' }).count() > 0) break;
      const next = page.locator('button').filter({ hasText: /^(Дальше|Нам подходит)$/ }).first();
      await page.waitForTimeout(1500);
      await page.screenshot({ path: resolve(SHOTS, 's7-onboarding-step.png'), fullPage: true });
      await next.waitFor({ state: 'visible', timeout: 15000 });
      await next.click();
      await page.waitForTimeout(700);
    }
    await expect(page.getByRole('button', { name: 'Договорились' })).toBeVisible({ timeout: 20000 });
    await page.getByRole('checkbox', { name: /Я, .* согласн/ }).click();
    await page.getByRole('checkbox', { name: 'Папа рядом и тоже согласен' }).click();
    await page.getByRole('button', { name: 'Договорились' }).click();
    await page.waitForTimeout(1500);
  }

  // Предмет на главной
  const tile = page.getByRole('button', { name: new RegExp(SUBJECT_LABEL) }).first();
  await expect(tile).toBeVisible({ timeout: 30000 });
  await page.screenshot({ path: resolve(SHOTS, 's7-child-home.png') });

  // Цвет предмета — свой (rose), а не серая плитка: подложка задана
  const bg = await tile.evaluate((el) => getComputedStyle(el).backgroundColor);
  expect(bg, 'у плитки предмета своя подложка').not.toBe('rgba(0, 0, 0, 0)');

  // Урок по предмету: в шапке репетитор
  await tile.click();
  const startLesson = page.getByRole('button', { name: 'Начать урок' });
  if (await startLesson.count() > 0) await startLesson.click();
  await expect(page.locator('[data-child-lesson]')).toBeVisible({ timeout: 30000 });
  await expect(page.getByText(SUBJECT_LABEL).first()).toBeVisible();
  await page.screenshot({ path: resolve(SHOTS, 's7-lesson-header.png') });

  expect(onlyKnownChildMode(bad), 'детский UI не должен дёргать взрослые ручки').toEqual([]);
  expect(noise(errors).filter(e => !e.includes('401') && !e.includes('Invalid API key') && !e.includes('child_mode'))).toEqual([]);
});

// ── Сценарий 8: ответ тьютора ──────────────────────────────────────────────────────────
test('С8: ответ тьютора — виджеты и mermaid работают, внешних ссылок и картинок нет', async ({ page }) => {
  const errors = collectConsoleErrors(page);
  await page.setViewportSize({ width: 1280, height: 900 });
  await enter(page, childToken, 'user', CHILD_U);
  await page.goto('/');

  await page.getByRole('button', { name: new RegExp(SUBJECT_LABEL) }).first().click();
  const startLesson = page.getByRole('button', { name: 'Начать урок' });
  if (await startLesson.count() > 0) await startLesson.click();
  await expect(page.locator('[data-child-lesson]')).toBeVisible({ timeout: 30000 });

  const feed = page.locator('[data-child-feed]');
  // Поле ввода урока живёт рядом с лентой, а не внутри неё
  const composer = page.locator('[data-child-lesson] textarea').first();
  await expect(composer).toBeVisible({ timeout: 20000 });
  await composer.fill('Посчитай, пожалуйста, 2+2');
  await composer.press('Enter');

  // Ждём текст ответа в ленте (фейковый движок отвечает сразу, CLI поднимается дольше)
  await expect(feed).toContainText(/Смотри, как считать|graph TD/, { timeout: 240000 });
  await page.screenshot({ path: resolve(SHOTS, 's8-tutor-answer.png'), fullPage: true });

  // Внешних ссылок и картинок в детской ленте нет
  const links = await feed.locator('a[href^="http"]').count();
  const imgs = await feed.locator('img[src^="http"]').count();
  expect(links, 'внешних ссылок в детской ленте нет').toBe(0);
  expect(imgs, 'внешних картинок в детской ленте нет').toBe(0);

  // Mermaid отрисовался (svg в ленте)
  expect(await feed.locator('svg').count(), 'в ленте есть отрисованная диаграмма').toBeGreaterThan(0);

  expect(noise(errors).filter(e => !e.includes('401') && !e.includes('Invalid API key') && !e.includes('child_mode'))).toEqual([]);
});

// ── Сценарий 9: закрытый предмет ──────────────────────────────────────────────────────
test('С9: закрытый предмет — «Вчера остановились тут», урок только для чтения', async ({ page }) => {
  const errors = collectConsoleErrors(page);

  // Выключаем предмет у админа
  await subjectsTab(adminPage);
  await subjectRow(adminPage).locator('[role="switch"]').first().click();
  await saveProfile(adminPage);
  await expect(subjectRow(adminPage).getByText('выключен')).toBeVisible({ timeout: 20000 });

  // У ребёнка: карточка «Вчера остановились тут» и урок только для чтения
  await page.setViewportSize({ width: 1280, height: 900 });
  await enter(page, childToken, 'user', CHILD_U);
  await page.goto('/');
  await expect(page.getByText('Вчера остановились тут')).toBeVisible({ timeout: 30000 });
  await page.screenshot({ path: resolve(SHOTS, 's9-child-home-closed.png') });

  await page.getByRole('button', { name: 'Продолжить' }).first().click();
  await expect(page.locator('[data-child-lesson]')).toBeVisible({ timeout: 30000 });
  await expect(page.getByText('Этот предмет закрыт')).toBeVisible();
  await expect(page.getByText('Переписку можно перечитать, а новые сообщения сюда не уйдут.')).toBeVisible();
  await page.screenshot({ path: resolve(SHOTS, 's9-closed-lesson.png') });

  expect(noise(errors).filter(e => !e.includes('401') && !e.includes('Invalid API key') && !e.includes('child_mode'))).toEqual([]);
});

// ── Сценарий 6: удаление предмета ──────────────────────────────────────────────────────
test('С6: удаление предмета — репетитор исчезает, чаты предмета остаются только для чтения', async ({ page }) => {
  const errors = collectConsoleErrors(page);

  // У ребёнка чат по предмету должен существовать ДО удаления
  const chatsBefore = await call(childReq, 'get', '/api/child/spaces/lessons/chats', childToken);
  expect(chatsBefore.status, 'список чатов ребёнка').toBe(200);
  const before = (chatsBefore.body?.chats ?? []).filter((c: any) => c.subject === subjectId);
  expect(before.length, 'по предмету есть чат').toBeGreaterThan(0);

  // Админ удаляет предмет
  await subjectsTab(adminPage);
  await subjectRow(adminPage).getByRole('button', { name: /^Удалить предмет/ }).click();
  await saveProfile(adminPage);
  await expect(subjectRow(adminPage)).toHaveCount(0, { timeout: 20000 });

  // У ребёнка предмета больше нет
  const subs = await call(childReq, 'get', '/api/child/subjects', childToken);
  expect(subs.status).toBe(200);
  expect((subs.body?.subjects ?? []).some((x: any) => x.id === subjectId), 'предмета у ребёнка нет').toBe(false);

  // Чаты остались и помечены как закрытые (только для чтения)
  const chatsAfter = await call(childReq, 'get', '/api/child/spaces/lessons/chats', childToken);
  expect(chatsAfter.status).toBe(200);
  const after = (chatsAfter.body?.chats ?? []).filter((c: any) => c.subject === subjectId);
  expect(after.length, 'чаты предмета остались у ребёнка').toBe(before.length);
  expect(after.every((c: any) => c.subjectClosed === true), 'чаты предмета только для чтения').toBe(true);

  // В интерфейсе ребёнка урок только для чтения
  await page.setViewportSize({ width: 1280, height: 900 });
  await enter(page, childToken, 'user', CHILD_U);
  await page.goto('/');
  await page.waitForTimeout(2000);
  await page.screenshot({ path: resolve(SHOTS, 's6-child-home-after-delete.png'), fullPage: true });
  console.log('QA-CHILD-BODY', (await page.locator('body').innerText()).slice(0, 500));
  const cont = page.getByRole('button', { name: 'Продолжить' });
  if (await cont.count() > 0) await cont.first().click();
  else await page.getByRole('button', { name: /Новый урок|Математика/ }).first().click();
  await expect(page.getByText('Этот предмет закрыт')).toBeVisible({ timeout: 30000 });
  await page.screenshot({ path: resolve(SHOTS, 's6-deleted-subject-lesson.png') });

  expect(noise(errors).filter(e => !e.includes('401') && !e.includes('Invalid API key') && !e.includes('child_mode'))).toEqual([]);
});

// ── Мобильная ширина 360 ───────────────────────────────────────────────────────────────
test('М360: вкладка «Предметы» и карточка репетитора помещаются по ширине', async ({ page }) => {
  await page.setViewportSize({ width: 360, height: 800 });
  await enter(page, adminToken, 'admin', ADMIN_U);
  await page.goto('/');
  await page.screenshot({ path: resolve(SHOTS, 'm360-home.png'), fullPage: true });
  await openEditor(page);
  await page.screenshot({ path: resolve(SHOTS, 'm360-menu.png'), fullPage: true });
  await subjectsTab(page);

  const editor = page.locator('[data-testid="child-profile-editor"]');
  await expect(editor).toBeVisible();

  // Горизонтального переполнения нет: ни у редактора, ни у документа
  const overflow = await editor.evaluate((el) => {
    const d = document.documentElement;
    return { editor: el.scrollWidth - el.clientWidth, doc: d.scrollWidth - d.clientWidth };
  });
  expect(overflow.editor, 'редактор профиля не переполнен по ширине').toBeLessThanOrEqual(1);
  expect(overflow.doc, 'страница не переполнена по ширине').toBeLessThanOrEqual(1);
  await page.screenshot({ path: resolve(SHOTS, 'm360-subjects.png'), fullPage: true });
});

// ── Дефект: редактор профиля не читает живой профиль ребёнка ────────────────────────────
test('Д1: свежая модалка профиля не видит живые предметы — сохранение затирает их и выключает режим', async ({ page }) => {
  // Живой профиль: два предмета
  const two = profileBody({
    subjects: [
      { id: 'matematika', label: SUBJECT_LABEL, glyph: SUBJECT_GLYPH, color: 'rose', providerKey: PROVIDER, model: 'sonnet', enabled: true },
      { id: 'russkij', label: 'Русский язык', glyph: 'Рус', color: 'info', providerKey: PROVIDER, model: 'sonnet', enabled: true },
    ],
  });
  const put1 = await call(adminReq, 'put', `/api/users/${childId}/child-profile`, adminToken, two);
  expect(put1.status, 'профиль с двумя предметами сохранён').toBe(200);
  const live0 = await call(childReq, 'get', '/api/child/subjects', childToken);
  expect((live0.body?.subjects ?? []).length, 'у ребёнка два предмета').toBe(2);

  // Свежая модалка: редактор стартует с пустого черновика — живых предметов не видит
  await page.setViewportSize({ width: 1280, height: 900 });
  await enter(page, adminToken, 'admin', ADMIN_U);
  await page.goto('/');
  await openEditor(page);
  await subjectsTab(page);
  await expect(page.getByText('Предметов нет')).toBeVisible({ timeout: 20000 });
  await page.screenshot({ path: resolve(SHOTS, 'd1-editor-empty.png'), fullPage: true });

  // Админ добавляет третий предмет и сохраняет
  await page.locator('[data-testid="add-subject"]').click();
  const row = page.locator('[data-testid="subject-row"]').first();
  await row.locator('input[placeholder="Математика"]').fill('История');
  await row.locator('input[placeholder="Мат"]').fill('Ист');
  await page.locator('[data-testid="save-profile"]').click();
  await page.waitForTimeout(2500);
  await page.screenshot({ path: resolve(SHOTS, 'd1-after-save.png'), fullPage: true });

  // Что стало с профилем: предметы и режим
  const after = await call(childReq, 'get', '/api/child/subjects', childToken);
  console.log('QA-D1-SUBJECTS', after.status, JSON.stringify(after.body).slice(0, 400));
  expect.soft(after.status, 'после сохранения предмета ребёнок должен остаться ребёнком').toBe(200);
  // Интерфейс ошибку не показал — сохранение выглядело успешным
  await expect(page.getByText('Сохранить не удалось')).toHaveCount(0);
  await page.screenshot({ path: resolve(SHOTS, 'd1-after-save.png'), fullPage: true });

  // Вторая половина дефекта: админ в свежей модалке заново настраивает профиль
  // (включает режим, повторяет маршрут и окна) и сохраняет предмет — живые предметы при этом затираются.
  const page2 = await page.context().newPage();
  await page2.setViewportSize({ width: 1280, height: 900 });
  await enter(page2, adminToken, 'admin', ADMIN_U);
  await page2.goto('/');
  await openEditor(page2);
  await fillMode(page2);
  await subjectsTab(page2);
  await page2.locator('[data-testid="add-subject"]').click();
  const row2 = page2.locator('[data-testid="subject-row"]').first();
  await row2.locator('input[placeholder="Математика"]').fill('История');
  await row2.locator('input[placeholder="Мат"]').fill('Ист');
  await page2.locator('[data-testid="save-profile"]').click();
  await page2.waitForTimeout(2500);
  const after2 = await call(childReq, 'get', '/api/child/subjects', childToken);
  const labels = (after2.body?.subjects ?? []).map((x: any) => x.label);
  console.log('QA-D1-LABELS', after2.status, JSON.stringify(labels));
  expect.soft(labels, 'живые предметы не должны затираться добавлением нового').toContain('Математика');
  expect.soft(labels, 'живые предметы не должны затираться добавлением нового').toContain('Русский язык');
  await page2.screenshot({ path: resolve(SHOTS, 'd1-wipe.png'), fullPage: true });
  await page2.close();
});

