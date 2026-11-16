// QA-2: повторная приёмка фичи «Медиа в личных чатах» после правок по первому раунду.
// Проверяем только прежние дефекты: панель «Звук из Личного» и «Работать с этим»
// (файл «Личного» создаётся в том же тесте — в прошлом раунде проба шла при удалённом
// afterAll файле), тексты личной панели «Файлы» (не должны говорить про проект),
// и сброс кэша персонажей при выходе по «Выйти».
// Фикстуры (личный чат админа и проектный чат) заведены ДО старта стенда — те же id,
// что в personal-media-delta.qa.spec.ts.

import { test, expect, type APIRequestContext, type Page } from '@playwright/test';
import { api, login, collectConsoleErrors, type Api } from './helpers';

const PERSONAL_CHAT = '503635a7-6a85-447a-9ec8-f8ee84b466fa';
const PROJECT_ID = '44ef2bd3-f234-4f10-854c-5cb3ce021bb8';
const PROJECT_CHAT = '7961fc44-82cc-4367-8eb1-d467e21ea678';
const SOUND_FILE = 'audio/qa-open.wav';

let request: APIRequestContext;
let token: string;
let client: Api;

test.beforeAll(async ({ playwright, baseURL }) => {
  request = await playwright.request.newContext({ baseURL });
  token = await login(request);
  client = api(request, token);
});

test.afterAll(async () => {
  // Уборка за прогоном: личные файлы и персонаж, заведённые этой спекой
  for (const p of ['audio', 'characters']) {
    await request.delete(`/api/personal/chats/${PERSONAL_CHAT}/files?path=${encodeURIComponent(p)}`,
      { headers: { Authorization: `Bearer ${token}` } }).catch(() => undefined);
  }
  await request?.dispose();
});

/** Открыть чат под токеном и убрать системную плашку (новый CLI) — она перекрывает верх экрана. */
async function openChat(page: Page, chatId: string, withToken = true): Promise<void> {
  if (withToken) await page.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
  await page.goto(`/#/chats/${chatId}`);
  await page.waitForTimeout(2500);
  const close = page.getByRole('button', { name: 'Закрыть' });
  if (await close.count()) await close.first().click();
  await page.waitForTimeout(300);
}

/** Снести личный файл/папку, оставшуюся от прошлого прогона. */
async function removePersonal(path: string): Promise<void> {
  const res = await request.delete(`/api/personal/chats/${PERSONAL_CHAT}/files?path=${encodeURIComponent(path)}`,
    { headers: { Authorization: `Bearer ${token}` } });
  expect([200, 204, 404, 400]).toContain(res.status());
}

/** Открыть панель по кнопке правой рельсы; после клика курсор уводим — плашка рельсы перехватывает клики. */
async function openRailPanel(page: Page, name: string): Promise<void> {
  await page.locator(`[aria-label="${name}"], [title="${name}"]`).first().click();
  await page.mouse.move(600, 400);
  await page.waitForTimeout(1500);
}

test('1. Личная панель «Файлы» не говорит про проект: корень — «Личное», проектных слов нет', async ({ page }) => {
  const consoleErrors = collectConsoleErrors(page);
  // Корень проверяем при пустой папке: сносим то, что могли оставить прошлые прогоны
  for (const p of ['audio', 'characters']) await removePersonal(p);

  await openChat(page, PERSONAL_CHAT);
  await openRailPanel(page, 'Файлы');
  await expect(page.getByText('Файлы', { exact: true }).first()).toBeVisible();

  // Проектных слов в личном чате быть не должно (прежний дефект)
  await expect(page.getByText('Проект пуст')).toHaveCount(0);
  await expect(page.getByText('Здесь пока нет файлов')).toHaveCount(0);

  // Подвал меню «Новый»: корень назван «Личное», а не «корень проекта»
  await page.getByRole('button', { name: 'Новый', exact: true }).first().click();
  await page.waitForTimeout(500);
  await expect(page.getByText('Личное', { exact: true }).last()).toBeVisible();
  await expect(page.getByText('корень проекта')).toHaveCount(0);

  // Фактическое состояние пустого корня (фиксируем для отчёта): при пустой папке вместо
  // пустого состояния в дереве стоит виртуальная папка «Заметки» — механизм корня проекта
  console.log('виртуальная «Заметки» в личном корне:',
    await page.locator('[title="Заметки (папка notes)"]').count(),
    'пустое состояние «Личное пусто»:', await page.getByText('Личное пусто').count());
  console.log('консоль теста 1:', JSON.stringify(consoleErrors));
});

test('2. «Работать с этим» в личной области: файл создаётся в этом же тесте, нить заведена, файл основной', async ({ page }) => {
  const consoleErrors = collectConsoleErrors(page);

  // Файл «Личного» создаём здесь же (прежний дефект: проба шла при отсутствующем файле)
  const created = await request.post(`/api/personal/chats/${PERSONAL_CHAT}/files/create`, {
    headers: { Authorization: `Bearer ${token}` },
    data: { path: SOUND_FILE, content: '' },
  });
  expect([200, 201, 409], `создание ${SOUND_FILE}: ${await created.text()}`).toContain(created.status());

  // Идемпотентность теста: убираем ссылки этого файла, оставшиеся в контексте с прошлых прогонов
  const ctx0 = await client.get<any>(`/api/chats/${PERSONAL_CHAT}/context`);
  for (const r of (ctx0?.refs ?? []).filter((r: any) => r.kind === 'personal-file' && r.ref?.path === SOUND_FILE)) {
    await client.del(`/api/chats/${PERSONAL_CHAT}/context/refs/${r.id}`);
  }

  await openChat(page, PERSONAL_CHAT);
  const rail = page.locator('[aria-label="Звук из Личного"], [title="Звук из Личного"]');
  await expect(rail.first()).toBeVisible({ timeout: 10000 });
  await openRailPanel(page, 'Звук из Личного');

  // Файл виден в панели с кнопкой «Работать с этим»
  const row = page.locator(`[data-personal-sound-file="${SOUND_FILE}"]`);
  await expect(row.first()).toBeVisible({ timeout: 15000 });
  await expect(row.getByRole('button', { name: 'Работать с этим' }).first()).toBeVisible();

  await row.getByRole('button', { name: 'Работать с этим' }).first().click();
  await page.waitForTimeout(2500);

  // Нить файла заведена, и она — основной объект (проверка по API сторов)
  const ctx = await client.get<any>(`/api/chats/${PERSONAL_CHAT}/context`);
  expect(ctx?.primary?.kind, 'основной объект — звук').toBe('audio');
  const st = await client.get<any>(`/api/audio-editor/chats/${PERSONAL_CHAT}/threads`);
  const thread = (st?.threads ?? []).find((t: any) => t.file === SOUND_FILE);
  expect(thread, `нить звука по файлу ${SOUND_FILE}`).toBeTruthy();

  // Что видит человек: в строке контекста основной — этот файл
  await expect(page.getByText('qa-open.wav').first()).toBeVisible({ timeout: 15000 });

  // «В контекст» появляется на строке, когда основной — звук. «Работать с этим» открывает
  // панель «Контекст» — если она закрыла панель звука, открываем панель звука снова
  if (!(await row.first().isVisible().catch(() => false))) await openRailPanel(page, 'Звук из Личного');
  const addBtn = row.getByRole('button', { name: /^В контекст/ }).first();
  await expect(addBtn).toBeVisible({ timeout: 10000 });
  await addBtn.click();
  await page.waitForTimeout(1500);
  await expect(row.locator('[data-context-add="in"]').first()).toBeVisible({ timeout: 10000 });
  const ctx2 = await client.get<any>(`/api/chats/${PERSONAL_CHAT}/context`);
  expect((ctx2?.refs ?? []).some((r: any) => r.kind === 'personal-file' && r.ref?.path === SOUND_FILE),
    'файл «Личного» в контексте').toBe(true);
  console.log('консоль теста 2:', JSON.stringify(consoleErrors));
});

test('3. Регресс: в проекте кнопки «Звук из Личного» нет', async ({ page }) => {
  const consoleErrors = collectConsoleErrors(page);
  await page.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
  await page.goto(`/#/project/${PROJECT_ID}/chat/${PROJECT_CHAT}`);
  await page.waitForTimeout(2500);
  const close = page.getByRole('button', { name: 'Закрыть' });
  if (await close.count()) await close.first().click();

  await expect(page.locator('[aria-label="Звук из Личного"], [title="Звук из Личного"]')).toHaveCount(0);
  console.log('консоль теста 3:', JSON.stringify(consoleErrors));
});

test('4. После «Выйти» следующий пользователь без перезагрузки не видит персонажей прежнего', async ({ page }) => {
  const consoleErrors = collectConsoleErrors(page);
  const auth = { Authorization: `Bearer ${token}` };

  // Второй пользователь (для «следующего») и его личный чат
  let u = await request.post('/api/users', { headers: auth, data: { username: 'qa-user-b', password: 'qa-pass-2', role: 'user' } });
  expect([201, 409], `создание qa-user-b: ${await u.text()}`).toContain(u.status());
  let userId: string;
  if (u.status() === 409) {
    const all = await (await request.get('/api/users', { headers: auth })).json();
    userId = all.find((x: any) => x.username === 'qa-user-b').id;
  } else {
    userId = (await u.json()).id;
  }
  const uTok = (await (await request.post('/api/auth/login', { data: { username: 'qa-user-b', password: 'qa-pass-2' } })).json()).token;
  const chatB = (await (await request.post('/api/chats', {
    headers: { Authorization: `Bearer ${uTok}` }, data: { name: 'QA-чат B' },
  })).json()).id;
  expect(chatB, 'личного чата пользователя B').toBeTruthy();

  // Персонаж прежнего пользователя: сносим старые и заводём заново (имя уникально для прогона)
  const chars = await (await request.get(`/api/image-editor/chats/${PERSONAL_CHAT}/characters`, { headers: auth })).json();
  for (const c of chars.filter((c: any) => c.name === 'QA-Прежний')) {
    await request.delete(`/api/image-editor/chats/${PERSONAL_CHAT}/characters/${c.slug}`, { headers: auth });
  }
  // Фото персонажа (бэк требует 3–10): multipart собираем телом вручную — options.multipart
  // не принимает список файлов на одно поле
  const png = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==', 'base64');
  const b = `----qa${Date.now()}`;
  const part = (name: string, value: Buffer, filename?: string) => Buffer.concat([
    Buffer.from(`--${b}\r\nContent-Disposition: form-data; name="${name}"${filename ? `; filename="${filename}"` : ''}\r\n${filename ? 'Content-Type: image/png\r\n' : ''}\r\n`),
    value, Buffer.from('\r\n'),
  ]);
  const body = Buffer.concat([
    part('name', Buffer.from('QA-Прежний')),
    ...[1, 2, 3].map(i => part('photos', png, `p${i}.png`)),
    Buffer.from(`--${b}--\r\n`),
  ]);
  const ch = await request.post(`/api/image-editor/chats/${PERSONAL_CHAT}/characters`, {
    headers: { ...auth, 'Content-Type': `multipart/form-data; boundary=${b}` }, data: body,
  });
  expect(ch.status(), `создание персонажа: ${await ch.text()}`).toBe(201);

  // Прежний пользователь: персонаж виден в панели (кэш заполняется его списком)
  await openChat(page, PERSONAL_CHAT);
  await openRailPanel(page, 'Персонажи');
  await expect(page.getByText('QA-Прежний').first()).toBeVisible({ timeout: 15000 });

  // Выход по кнопке «Выйти» — без перезагрузки страницы
  await page.locator('[aria-label^="Меню пользователя"]').first().click();
  await page.waitForTimeout(500);
  await page.getByRole('button', { name: 'Выйти', exact: true }).click();
  await expect(page.getByPlaceholder('Имя пользователя')).toBeVisible({ timeout: 15000 });
  await page.getByPlaceholder('Имя пользователя').fill('qa-user-b');
  await page.getByPlaceholder('Пароль').fill('qa-pass-2');
  await page.getByRole('button', { name: 'Войти', exact: true }).click();
  await page.waitForTimeout(2500);

  // Следующий пользователь (тот же документ, без перезагрузки): персонажей прежнего нет
  await openChat(page, chatB, false);
  await openRailPanel(page, 'Персонажи');
  await expect(page.getByText('QA-Прежний')).toHaveCount(0);

  // Уборка: чат и пользователь B
  await request.delete(`/api/chats/${chatB}`, { headers: { Authorization: `Bearer ${uTok}` } }).catch(() => undefined);
  await request.delete(`/api/users/${userId}`, { headers: auth }).catch(() => undefined);
  console.log('консоль теста 4:', JSON.stringify(consoleErrors));
});
