// QA-3: повторная приёмка (круг 3) фичи «Доработка „Медиа в личных чатах“: панель Файлы и хвосты».
// Идём строго по пунктам карточки: «Личное пусто» достижимо и «Заметки» в личном корне нет (после Х-9),
// при маунте личной панели нет запросов на проектные маршруты (после Х-13), регрессия Ф-1/Ф-2/Ф-8,
// контроль кнопки создания/загрузки в личной панели. Новых сценариев не ищем.
// Фикстуры (личный и проектный чаты) заведены ДО старта стенда — те же id, что в
// personal-media-delta.qa.spec.ts.

import { test, expect, type APIRequestContext, type Page } from '@playwright/test';
import { writeFileSync, mkdtempSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { api, login, collectConsoleErrors, type Api } from './helpers';

const PERSONAL_CHAT = '503635a7-6a85-447a-9ec8-f8ee84b466fa';
const SOUND_FILE = 'audio/qa-open.wav';
const IMG_FILE = 'images/qa-probe.png';
const MEDIA = [
  { idx: 0, dir: 'images', file: 'qa-image.png' },
  { idx: 1, dir: 'video', file: 'qa-video.mp4' },
  { idx: 2, dir: 'audio', file: 'qa-audio.mp3' },
];

let request: APIRequestContext;
let token: string;
let client: Api;
let auth: Record<string, string>;

const filesUrl = (q = '') => `/api/personal/chats/${PERSONAL_CHAT}/files${q}`;
const enc = (p: string) => encodeURIComponent(p);

test.beforeAll(async ({ playwright, baseURL }) => {
  request = await playwright.request.newContext({ baseURL });
  token = await login(request);
  client = api(request, token);
  auth = { Authorization: `Bearer ${token}` };
});

test.afterAll(async () => {
  // Уборка за прогоном: личные файлы и нить картинки, заведённые этой спекой
  for (const p of ['images', 'video', 'audio', 'qa-3-created.txt', 'qa-3-upload.txt']) {
    await request.delete(filesUrl(`?path=${enc(p)}`), { headers: auth }).catch(() => undefined);
  }
  const st = await request.get(`/api/image-editor/chats/${PERSONAL_CHAT}/threads`, { headers: auth })
    .then(r => (r.ok() ? r.json() : null)).catch(() => null);
  for (const t of (st?.threads ?? []).filter((t: any) => typeof t.file === 'string' && t.file.startsWith('images/'))) {
    await request.delete(`/api/image-editor/chats/${PERSONAL_CHAT}/threads/${t.id}?revision=${st.revision}`,
      { headers: auth }).catch(() => undefined);
  }
  const ctx = await request.get(`/api/chats/${PERSONAL_CHAT}/context`, { headers: auth })
    .then(r => (r.ok() ? r.json() : null)).catch(() => null);
  for (const r of (ctx?.refs ?? []).filter((r: any) => r.kind === 'personal-file')) {
    await request.delete(`/api/chats/${PERSONAL_CHAT}/context/refs/${r.id}`, { headers: auth }).catch(() => undefined);
  }
  await request?.dispose();
});

/** Открыть чат под токеном и убрать системную плашку (новый CLI) — она перекрывает верх экрана. */
async function openChat(page: Page, chatId: string): Promise<void> {
  await page.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
  await page.goto(`/#/chats/${chatId}`);
  await page.waitForTimeout(2500);
  const close = page.getByRole('button', { name: 'Закрыть' });
  if (await close.count()) await close.first().click();
  await page.waitForTimeout(300);
}

/** Открыть панель правой рельсы; после клика курсор уводим — плашка рельсы перехватывает клики. */
async function openRailPanel(page: Page, name: string): Promise<void> {
  await page.locator(`[aria-label="${name}"], [title="${name}"]`).first().click();
  await page.mouse.move(600, 400);
  await page.waitForTimeout(1500);
}

/** Снести всё содержимое папки «Личного» (сначала файлы, потом саму папку). */
async function wipePersonal(dir = ''): Promise<void> {
  const entries = await client.get<any[]>(filesUrl(`?path=${enc(dir)}`)).catch(() => []);
  for (const e of entries ?? []) {
    if (e.isDirectory) await wipePersonal(e.path);
    await request.delete(filesUrl(`?path=${enc(e.path)}`), { headers: auth }).catch(() => undefined);
  }
}

/** Пункт меню «Новый» в панели «Файлы». */
async function openCreateMenu(page: Page, item: string): Promise<void> {
  await page.getByRole('button', { name: 'Новый', exact: true }).first().click();
  await page.mouse.move(600, 400);
  await page.waitForTimeout(500);
  await page.getByText(item, { exact: true }).first().click();
  await page.mouse.move(600, 400);
  await page.waitForTimeout(500);
}

test('1. «Личное пусто» достижимо: пустой корень, папки «Заметки» в нём нет', async ({ page }) => {
  const consoleErrors = collectConsoleErrors(page);
  await wipePersonal();

  // Уровень API: корень пустой и виртуальной папки notes в нём не появляется
  const root = await client.get<any[]>(filesUrl('?path='));
  expect((root ?? []).filter((e: any) => e.name === 'Заметки'), '«Заметки» в листинге личного корня').toHaveLength(0);
  console.log('листинг личного корня:', JSON.stringify(root ?? []));

  await openChat(page, PERSONAL_CHAT);
  await openRailPanel(page, 'Файлы');
  await expect(page.getByText('Файлы', { exact: true }).first()).toBeVisible();

  // Пустое состояние называет «Личное», а не проект
  await expect(page.getByText('Личное пусто').first()).toBeVisible({ timeout: 15000 });
  await expect(page.getByText('Проект пуст')).toHaveCount(0);

  // Клика по «Заметки» нет ни как узла дерева, ни как подписи
  await expect(page.locator('[title*="Заметки"]')).toHaveCount(0);
  await expect(page.locator('[data-cc-src*="FileExplorer"]').filter({ hasText: 'Заметки' })).toHaveCount(0);
  console.log('консоль теста 1:', JSON.stringify(consoleErrors));
});

test('2. Маунт личной панели: запросов на проектные маршруты и лишних 404 нет', async ({ page }) => {
  // Сиротские нити звука прошлых прогонов (их файлы снесены) дают свои 404 — их чистим до замера
  const at = await client.get<any>(`/api/audio-editor/chats/${PERSONAL_CHAT}/threads`).catch(() => null);
  for (const t of at?.threads ?? []) {
    await request.delete(`/api/audio-editor/chats/${PERSONAL_CHAT}/threads/${t.id}?revision=${at.revision}`,
      { headers: auth }).catch(() => undefined);
  }

  const hits: string[] = [];
  page.on('response', r => {
    const u = r.url();
    if (u.includes('projects/personal-files')) hits.push(`PROJECT-ROUTE ${r.status()} ${u}`);
    else if (r.status() === 404 && u.includes('/api/')) hits.push(`404 ${u}`);
  });
  await openChat(page, PERSONAL_CHAT);
  await openFilesPanelMount(page);
  await page.waitForTimeout(3000);
  console.log('проектные маршруты и 404 при маунте:', JSON.stringify(hits));
  expect(hits.filter(h => h.startsWith('PROJECT-ROUTE')), 'личная панель ходит на проектный маршрут').toHaveLength(0);
  expect(hits.filter(h => h.includes('/api/personal/')), 'лишние 404 личных файловых маршрутов').toHaveLength(0);
});

/** Открыть панель «Файлы» и дождаться её дерева. */
async function openFilesPanelMount(page: Page): Promise<void> {
  await openRailPanel(page, 'Файлы');
  await expect(page.getByText('Файлы', { exact: true }).first()).toBeVisible();
  await page.waitForTimeout(1500);
}

test('3. Регрессия Ф-8: «Сохранить в Личное» кладёт файл в папку по типу медиа, файл виден в панели', async ({ page }) => {
  const consoleErrors = collectConsoleErrors(page);
  // Цель сохранения должна быть свободной: занятое имя даёт 409 (Х-10)
  for (const c of MEDIA) await request.delete(filesUrl(`?path=${enc(`${c.dir}/${c.file}`)}`), { headers: auth }).catch(() => undefined);

  await openChat(page, PERSONAL_CHAT);
  const buttons = page.locator('button[data-cc-src*="MediaBlock"]');
  await expect(buttons).toHaveCount(3, { timeout: 15000 });

  for (const c of MEDIA) {
    const btn = buttons.nth(c.idx);
    await expect(btn).toBeVisible();
    await btn.click();
    await expect(page.getByText('Сохранить в Личное', { exact: true }).first()).toBeVisible();
    await expect(page.getByText(`Папка: ${c.dir}/`)).toBeVisible();
    await page.getByRole('button', { name: 'Сохранить', exact: true }).click();
    await expect(buttons.nth(c.idx)).toHaveText(/✓ Сохранено/, { timeout: 30000 });
    await expect(page.getByRole('button', { name: '✗ Ошибка' })).toHaveCount(0);
  }

  // Файлы появились в панели «Файлы» в своих папках
  await openFilesPanelMount(page);
  for (const c of MEDIA) {
    await page.getByText(c.dir, { exact: true }).first().click();
    await page.waitForTimeout(1200);
    await expect(page.getByTitle(c.file).first()).toBeVisible({ timeout: 15000 });
  }
  console.log('консоль теста 3:', JSON.stringify(consoleErrors));
});

test('4. Регрессия Ф-2: «В контекст» даёт личную ссылку с ролью «кусок склейки», нить из файла заводится', async ({ page }) => {
  const consoleErrors = collectConsoleErrors(page);
  const created = await request.post(filesUrl('/create'), { headers: auth, data: { path: SOUND_FILE, content: '' } });
  expect([200, 201, 409], `создание ${SOUND_FILE}: ${await created.text()}`).toContain(created.status());

  // Идемпотентность: ссылки этого файла с прошлых прогонов
  const ctx0 = await client.get<any>(`/api/chats/${PERSONAL_CHAT}/context`);
  for (const r of (ctx0?.refs ?? []).filter((r: any) => r.kind === 'personal-file' && r.ref?.path === SOUND_FILE)) {
    await client.del(`/api/chats/${PERSONAL_CHAT}/context/refs/${r.id}`);
  }

  await openChat(page, PERSONAL_CHAT);
  const rail = page.locator('[aria-label="Звук из Личного"], [title="Звук из Личного"]');
  await expect(rail.first()).toBeVisible({ timeout: 10000 });
  await openRailPanel(page, 'Звук из Личного');

  const row = page.locator(`[data-personal-sound-file="${SOUND_FILE}"]`);
  await expect(row.first()).toBeVisible({ timeout: 15000 });
  await row.getByRole('button', { name: 'Работать с этим' }).first().click();
  await page.waitForTimeout(2500);

  // Нить звука из файла «Личного» заведена, и она — основной объект
  const ctx = await client.get<any>(`/api/chats/${PERSONAL_CHAT}/context`);
  expect(ctx?.primary?.kind, 'основной объект — звук').toBe('audio');
  const st = await client.get<any>(`/api/audio-editor/chats/${PERSONAL_CHAT}/threads`);
  expect((st?.threads ?? []).find((t: any) => t.file === SOUND_FILE), `нить звука по файлу ${SOUND_FILE}`).toBeTruthy();

  // «В контекст» → личная ссылка, роль одна — «кусок склейки» (piece)
  if (!(await row.first().isVisible().catch(() => false))) await openRailPanel(page, 'Звук из Личного');
  const addBtn = row.getByRole('button', { name: /^В контекст/ }).first();
  await expect(addBtn).toBeVisible({ timeout: 10000 });
  await addBtn.click();
  await page.waitForTimeout(1500);
  const ctx2 = await client.get<any>(`/api/chats/${PERSONAL_CHAT}/context`);
  const ref = (ctx2?.refs ?? []).find((r: any) => r.kind === 'personal-file' && r.ref?.path === SOUND_FILE);
  expect(ref, 'личная ссылка в контексте').toBeTruthy();
  expect(ref.role, 'роль личной ссылки').toBe('piece');
  console.log('личная ссылка:', JSON.stringify(ref));
  console.log('консоль теста 4:', JSON.stringify(consoleErrors));
});

test('5. Границы Ф-1: «Заметок» в личном корне нет, не-медиальный путь сохранения отвергнут', async ({ page }) => {
  const consoleErrors = collectConsoleErrors(page);

  // Путь notes/ в «Личном» отказан на 404 (Х-9), а не маппится куда-то молча
  const notes = await request.get(`/api/personal/chats/${PERSONAL_CHAT}/files/content?path=${enc('notes/qa.md')}`, { headers: auth });
  console.log('notes/ в «Личном»:', notes.status(), (await notes.text()).slice(0, 160));
  expect(notes.status(), 'путь notes/ в личном корне').toBe(404);

  // Сохранение по URL принимает только медиальные папки — то, чем ограничен и список диалога
  const bad = await request.post(`/api/personal/chats/${PERSONAL_CHAT}/files/save-from-url`, {
    headers: auth, data: { url: 'https://example.com/x.png', path: 'notes/qa.png' },
  });
  const badBody = await bad.text();
  console.log('save-from-url в notes/:', bad.status(), badBody.slice(0, 160));
  expect(bad.status(), `сохранение в не-медиальную папку: ${badBody.slice(0, 160)}`).toBe(400);

  // Диалог «Сохранить как…» в редакторе личной области: фиксируем состояние.
  // Нить, взятая из файла, шага не имеет — кнопка сохранения в её редакторе недоступна;
  // диалог открывается только нити с нарисованной версией (на стенде нет поставщика рисования).
  await request.post(filesUrl('/create'), { headers: auth, data: { path: IMG_FILE, content: '' } }).catch(() => undefined);
  let st = await client.get<any>(`/api/image-editor/chats/${PERSONAL_CHAT}/threads`);
  let thread = (st?.threads ?? []).find((t: any) => t.file === IMG_FILE);
  if (!thread) {
    await client.post(`/api/image-editor/chats/${PERSONAL_CHAT}/threads`, { file: IMG_FILE, revision: st?.revision ?? 0 });
    st = await client.get<any>(`/api/image-editor/chats/${PERSONAL_CHAT}/threads`);
    thread = (st?.threads ?? []).find((t: any) => t.file === IMG_FILE);
  }
  expect(thread, `нить картинки по файлу ${IMG_FILE}`).toBeTruthy();
  const ctx = await client.get<any>(`/api/chats/${PERSONAL_CHAT}/context`);
  const put = await request.put(`/api/chats/${PERSONAL_CHAT}/context/primary`, {
    headers: auth, data: { kind: 'image', ref: { threadId: thread.id }, revision: ctx?.revision ?? 0 },
  });
  expect(put.status(), `основной объект — картинка: ${await put.text()}`).toBe(200);

  await openChat(page, PERSONAL_CHAT);
  await openRailPanel(page, 'Контекст');
  const markBtn = page.getByRole('button', { name: 'Отметить', exact: true }).first();
  await expect(markBtn, 'действия, открывающего редактор, нет в панели «Контекст»').toBeVisible({ timeout: 20000 });
  await markBtn.click();
  await page.waitForTimeout(2000);
  const saveAs = page.getByRole('button', { name: 'Сохранить как…', exact: true }).first();
  console.log('«Сохранить как…» в редакторе нити, взятой из файла:',
    await saveAs.count(), 'доступна:', (await saveAs.count()) ? await saveAs.isEnabled() : false);
  console.log('консоль теста 5:', JSON.stringify(consoleErrors));
});

test('6. Контроль: «Новый» в личной панели даёт и создание файла, и загрузку с диска', async ({ page }) => {
  const consoleErrors = collectConsoleErrors(page);
  for (const p of ['qa-3-created.txt', 'qa-3-upload.txt']) {
    await request.delete(filesUrl(`?path=${enc(p)}`), { headers: auth }).catch(() => undefined);
  }

  await openChat(page, PERSONAL_CHAT);
  await openFilesPanelMount(page);

  // Создание файла
  await openCreateMenu(page, 'Файл');
  await page.getByPlaceholder('name.py').first().fill('qa-3-created.txt');
  await page.getByRole('button', { name: 'Создать', exact: true }).click();
  await expect(page.getByText('qa-3-created.txt', { exact: true })).toBeVisible({ timeout: 15000 });

  // Загрузка файла с диска
  const dir = mkdtempSync(join(tmpdir(), 'qa-personal-3-'));
  const file = join(dir, 'qa-3-upload.txt');
  writeFileSync(file, 'круг 3 приёмки «Медиа в личных чатах»\n');
  const chooser = page.waitForEvent('filechooser');
  await openCreateMenu(page, 'Загрузить файлы');
  await (await chooser).setFiles(file);
  await expect(page.getByText('qa-3-upload.txt', { exact: true })).toBeVisible({ timeout: 15000 });

  const res = await request.get(filesUrl(`?path=${enc('qa-3-upload.txt')}`), { headers: auth });
  expect([200, 404], `чтение загруженного файла: ${await res.text()}`).toContain(res.status());
  console.log('консоль теста 6:', JSON.stringify(consoleErrors));
});

test('7. Вход «Редактировать» из просмотрщика личного файла: что пользователь видит в редакторе', async ({ page }) => {
  const consoleErrors = collectConsoleErrors(page);
  await request.post(filesUrl('/create'), { headers: auth, data: { path: IMG_FILE, content: '' } }).catch(() => undefined);

  await openChat(page, PERSONAL_CHAT);
  await openFilesPanelMount(page);
  await page.getByText('images', { exact: true }).first().click();
  await page.waitForTimeout(1500);
  await page.getByTitle('qa-probe.png').first().click();
  await page.waitForTimeout(2000);

  const edit = page.getByRole('button', { name: 'Редактировать', exact: true });
  const n = await edit.count();
  console.log('«Редактировать» в просмотрщике личного файла:', n);
  if (n) {
    await edit.first().click();
    await page.waitForTimeout(3000);
    const saveAs = page.getByRole('button', { name: 'Сохранить как…', exact: true }).first();
    console.log('«Сохранить как…» после этого входа:', await saveAs.count(),
      'доступна:', (await saveAs.count()) ? await saveAs.isEnabled() : false);
  }
  console.log('консоль теста 7:', JSON.stringify(consoleErrors));
});
