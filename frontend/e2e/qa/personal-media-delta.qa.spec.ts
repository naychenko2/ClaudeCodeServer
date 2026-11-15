// Приёмка фичи «Доработка „Медиа в личных чатах“: панель Файлы и хвосты» на живом личном чате.
// Спека по рецепту docs/operations/qa-stand.md. Фикстуры (личный чат и проектный чат с картинкой
// в ленте) заведены ДО старта стенда: история чата лежит файлом, а сервер читает её только без
// живого аккумулятора хода — то есть после рестарта стенда. Поэтому id чатов — константы.
//
// Что проверяем:
//  1. панель «Файлы» личного чата открывается и показывает «Личное» (без вечного «Ищем агент AI Home»);
//  2. загрузка файла в текущую папку: файл виден в листинге и скачивается;
//  3. «Сохранить в Личное» у картинки: диалог с папкой → сохранение → файл в панели в images/;
//  4. та же кнопка при ЗАКРЫТОЙ панели «Файлы» (главный сценарий: раньше был «✗ Ошибка»);
//  5. панель «Звук из Личного» в личном чате;
//  6. CRUD личной панели: папка, файл с пробелом внутри сегмента, переименование, перенос, удаление;
//  7. регресс: в проекте кнопки «Сохранить в Личное» нет, панель «Файлы» проекта работает.

import { test, expect, type APIRequestContext, type Page } from '@playwright/test';
import { writeFileSync, mkdtempSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { api, login, type Api } from './helpers';

const PERSONAL_CHAT = '503635a7-6a85-447a-9ec8-f8ee84b466fa';
const PROJECT_ID = '44ef2bd3-f234-4f10-854c-5cb3ce021bb8';
const PROJECT_CHAT = '7961fc44-82cc-4367-8eb1-d467e21ea678';

let request: APIRequestContext;
let token: string;
let client: Api;

// Порядок тестов значим (тест 4 работает с файлами личной папки), но падение одного не должно
// отменять остальные — режим по умолчанию (не serial) сохраняет порядок и не пропускает хвост.
test.beforeAll(async ({ playwright, baseURL }) => {
  request = await playwright.request.newContext({ baseURL });
  token = await login(request);
  client = api(request, token);
});

test.afterAll(async () => {
  // Уборка личных файлов, заведённых прогоном (создание папки/файла сrefuses занятое имя —
  // без уборки повторный прогон спотыкается о «уже есть»)
  for (const p of ['qa-papka', 'c d.txt', 'a b.txt', 'qa-upload.txt', 'images', 'video', 'audio']) {
    await request.delete(`/api/personal/chats/${PERSONAL_CHAT}/files?path=${encodeURIComponent(p)}`,
      { headers: { Authorization: `Bearer ${token}` } }).catch(() => undefined);
  }
  await request?.dispose();
});

/** Снести личный файл/папку, оставшуюся от прошлого прогона. */
async function removePersonal(path: string): Promise<void> {
  const res = await request.delete(`/api/personal/chats/${PERSONAL_CHAT}/files?path=${encodeURIComponent(path)}`,
    { headers: { Authorization: `Bearer ${token}` } });
  expect([200, 204, 404, 400]).toContain(res.status());
}

/** Открыть чат под токеном и убрать системную плашку (новый CLI) — она перекрывает верх экрана. */
async function openChat(page: Page, chatId: string): Promise<void> {
  await page.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
  await page.goto(`/#/chats/${chatId}`);
  await page.waitForTimeout(2500);
  const close = page.getByRole('button', { name: 'Закрыть' });
  if (await close.count()) await close.first().click();
  await page.waitForTimeout(300);
}

/**
 * Открыть панель «Файлы» в рельсе справа. После клика курсор уводим в центр: плашка-подсказка
 * рельсы (RailFlyout) висит поверх и перехватывает клики по кнопкам панели.
 */
async function openFilesPanel(page: Page): Promise<void> {
  await page.locator('[aria-label="Файлы"]').first().click();
  await page.mouse.move(600, 400);
  await page.waitForTimeout(1200);
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

test('1. Панель «Файлы» личного чата открывается и показывает папку «Личного»', async ({ page }) => {
  await openChat(page, PERSONAL_CHAT);
  // Личный чат: у медиа в ленте кнопка «Сохранить в Личное» (в проекте — «Добавить в проект»)
  await expect(page.getByRole('button', { name: 'Сохранить в Личное' }).first()).toBeVisible();

  await openFilesPanel(page);
  await expect(page.getByText('Файлы', { exact: true }).first()).toBeVisible();
  // Прежний баг: панель личного чата вечно показывала «Ищем агент AI Home на этом компьютере»
  await expect(page.getByText('Ищем агент AI Home на этом компьютере')).toHaveCount(0);
  // Панель не висит в загрузке: либо корень пуст («Здесь пока пусто»), либо в нём есть строки
  const empty = page.getByText('Здесь пока пусто');
  const rows = page.locator('[data-cc-src*="FileExplorer"]');
  await expect(empty.or(rows).first()).toBeVisible();
});

test('2. Загрузка файла в панель «Файлы»: «Добавить» → файл в листинге, файл скачивается', async ({ page }) => {
  const dir = mkdtempSync(join(tmpdir(), 'qa-personal-'));
  const file = join(dir, 'qa-upload.txt');
  writeFileSync(file, 'приёмка фичи «Медиа в личных чатах»\n');

  await openChat(page, PERSONAL_CHAT);
  await openFilesPanel(page);

  const chooser = page.waitForEvent('filechooser');
  await openCreateMenu(page, 'Загрузить файлы');
  await (await chooser).setFiles(file);

  await expect(page.getByText('qa-upload.txt', { exact: true })).toBeVisible({ timeout: 15000 });

  // Файл скачивается тем же маршрутом, что и кнопка «Скачать» в строке
  const res = await request.get(
    `/api/personal/chats/${PERSONAL_CHAT}/files/download?path=qa-upload.txt`,
    { headers: { Authorization: `Bearer ${token}` } },
  );
  expect(res.status(), 'скачивание загруженного файла').toBe(200);
  expect(await res.text()).toContain('приёмка фичи');

  // И кнопкой «Скачать» в просмотрщике: открыть файл → скачать
  await page.getByText('qa-upload.txt', { exact: true }).first().click();
  await page.waitForTimeout(1200);
  const dl = page.waitForEvent('download', { timeout: 15000 });
  await page.getByTitle('Скачать').first().click();
  const download = await dl;
  expect(download.suggestedFilename()).toContain('qa-upload.txt');
});

test('3. «Сохранить в Личное»: у картинки, видео и звука — папка по типу медиа, файл в панели', async ({ page }) => {
  await openChat(page, PERSONAL_CHAT);

  // Кнопка сохранения в каждом блоке медиа (подпись меняется на «✓ Сохранено», поэтому
  // берём кнопку по источнику, а не по тексту — порядок ленты: картинка, видео, звук)
  const buttons = page.locator('button[data-cc-src*="MediaBlock"]');
  await expect(buttons).toHaveCount(3, { timeout: 15000 });

  const cases = [
    { idx: 0, dir: 'images', file: 'qa-image.png' },
    { idx: 1, dir: 'video', file: 'qa-video.mp4' },
    { idx: 2, dir: 'audio', file: 'qa-audio.mp3' },
  ];

  for (const c of cases) {
    const btn = buttons.nth(c.idx);
    // Панель «Файлы» ЗАКРЫТА — главный сценарий (раньше кнопка отказывала «✗ Ошибка»)
    await expect(btn).toBeVisible();
    await btn.click();

    await expect(page.getByText('Сохранить в Личное', { exact: true }).first()).toBeVisible();
    // Папка медиальная по типу медиа
    await expect(page.getByText(`Папка: ${c.dir}/`)).toBeVisible();
    await page.getByRole('button', { name: 'Сохранить', exact: true }).click();

    // Кнопка блока меняет подпись на «✓ Сохранено» (через 3 с возвращается в «Сохранить»)
    await expect(buttons.nth(c.idx)).toHaveText(/✓ Сохранено/, { timeout: 30000 });
    await expect(page.getByRole('button', { name: '✗ Ошибка' })).toHaveCount(0);
  }

  // Файлы появились в панели «Файлы» в своих папках
  await openFilesPanel(page);
  for (const c of cases) {
    await page.getByText(c.dir, { exact: true }).first().click();
    await page.waitForTimeout(1200);
    // Файл в листинге панели (строка дерева; то же имя есть и в ленте — берём по подсказке строки)
    await expect(page.getByTitle(c.file).first()).toBeVisible({ timeout: 15000 });
  }
});

test('4. CRUD личной панели: папка, файл с пробелом, переименование, перенос, удаление', async ({ page }) => {
  // Убрать следы прошлого прогона: «создать» отказывает занятым именем
  for (const p of ['qa-papka', 'c d.txt', 'a b.txt']) await removePersonal(p);

  await openChat(page, PERSONAL_CHAT);
  await openFilesPanel(page);

  // Папка
  await openCreateMenu(page, 'Папка');
  await page.getByPlaceholder('my-folder').first().fill('qa-papka');
  await page.getByRole('button', { name: 'Создать', exact: true }).click();
  await expect(page.getByText('qa-papka', { exact: true })).toBeVisible({ timeout: 15000 });

  // Файл с пробелом внутри сегмента
  await openCreateMenu(page, 'Файл');
  await page.getByPlaceholder('name.py').first().fill('a b.txt');
  await page.getByRole('button', { name: 'Создать', exact: true }).click();
  await expect(page.getByText('a b.txt', { exact: true })).toBeVisible({ timeout: 15000 });

  // Переименование: правый клик по строке → «Переименовать»
  await page.getByText('a b.txt', { exact: true }).first().click({ button: 'right' });
  await page.mouse.move(600, 400);
  await page.waitForTimeout(600);
  await page.getByText('Переименовать', { exact: true }).first().click();
  await page.waitForTimeout(600);
  // Поле переименования — инлайновое, в строке дерева (не поле модалки)
  const renameInput = page.locator('input[data-cc-src*="FileExplorer"]:visible').first();
  await renameInput.fill('c d.txt');
  await renameInput.press('Enter');
  await expect(page.getByText('c d.txt', { exact: true })).toBeVisible({ timeout: 15000 });
  await expect(page.getByText('a b.txt', { exact: true })).toHaveCount(0);

  // Перенос в папку: правый клик → «Переместить» → выбор целевой папки в списке
  await page.getByText('c d.txt', { exact: true }).first().click({ button: 'right' });
  await page.mouse.move(600, 400);
  await page.waitForTimeout(600);
  await page.getByText('Переместить в…', { exact: true }).first().click();
  await page.waitForTimeout(800);
  await page.getByText('qa-papka', { exact: true }).last().click();
  await page.waitForTimeout(1500);

  // Файл переехал: в корне его нет, в папке он есть
  await expect(page.getByText('c d.txt', { exact: true })).toHaveCount(0);
  await page.getByText('qa-papka', { exact: true }).first().click();
  await page.waitForTimeout(1200);
  await expect(page.getByText('c d.txt', { exact: true })).toBeVisible({ timeout: 15000 });

  // Удаление папки: правый клик → «Удалить» → подтверждение
  await page.getByText('qa-papka', { exact: true }).first().click({ button: 'right' });
  await page.mouse.move(600, 400);
  await page.waitForTimeout(600);
  await page.getByText('Удалить', { exact: true }).first().click();
  await page.waitForTimeout(600);
  const confirm = page.getByRole('button', { name: /Удалить|Да/ }).last();
  await confirm.click();
  await expect(page.getByText('qa-papka', { exact: true })).toHaveCount(0, { timeout: 15000 });
});

test('5. Панель «Звук из Личного» доступна в личном чате', async ({ page }) => {
  const moduleHits: string[] = [];
  page.on('response', r => {
    if (r.url().includes('audio-editor-remote')) moduleHits.push(`${r.status()} ${r.url()}`);
  });
  await openChat(page, PERSONAL_CHAT);

  // Модуль «Звук» грузится (иначе дефект другой — модуль не отдаётся)
  console.log('загрузка модуля звука:', JSON.stringify(moduleHits));

  // Кнопка панели в правой рельсе — по подписи «Звук из Личного»
  const rail = page.locator('[aria-label="Звук из Личного"], [title="Звук из Личного"]');
  await expect(rail.first()).toBeVisible({ timeout: 10000 });
  // Панель открывается и показывает звуковые файлы «Личного»
  await rail.first().click();
  await page.mouse.move(600, 400);
  await page.waitForTimeout(1500);
  await expect(page.getByText('Звуковых файлов нет').or(page.locator('[data-personal-sound-file]')).first())
    .toBeVisible({ timeout: 10000 });
});

test('7. Личная ссылка в строке контекста не гаснет («Раздел не загрузился»)', async ({ page }) => {
  // Файл «Личного» — его и кладёт в контекст личная ссылка
  await client.post(`/api/personal/chats/${PERSONAL_CHAT}/files/create`, { path: 'audio/qa-tone.wav', content: '' })
    .catch(() => undefined);

  // Основной объект — звук: нить-черновик личной области (без файла). Открытие нити само
  // зеркалится в контекст (primary=audio), поэтому ревизию берём живую
  const st = await client.get<any>(`/api/audio-editor/chats/${PERSONAL_CHAT}/threads`);
  let threadId: string | undefined = (st?.threads ?? []).find((t: any) => !t.file)?.id;
  if (!threadId) {
    const opened = await client.post<any>(`/api/audio-editor/chats/${PERSONAL_CHAT}/threads`,
      { draftFolder: '', revision: st?.revision ?? 0 });
    threadId = opened?.thread?.id ?? opened?.threads?.[0]?.id;
  }
  expect(threadId, 'нить звука личной области').toBeTruthy();

  const ctx = await client.get<any>(`/api/chats/${PERSONAL_CHAT}/context`);
  let revision: number = ctx?.revision ?? 0;
  if (ctx?.primary?.kind !== 'audio') {
    const res = await request.put(`/api/chats/${PERSONAL_CHAT}/context/primary`, {
      headers: { Authorization: `Bearer ${token}` },
      data: { kind: 'audio', ref: { threadId }, revision },
    });
    expect(res.status(), 'основной объект — звук').toBe(200);
    revision = (await res.json()).revision;
  }
  const refRes = await request.post(`/api/chats/${PERSONAL_CHAT}/context/refs`, {
    headers: { Authorization: `Bearer ${token}` },
    data: { kind: 'personal-file', ref: { path: 'audio/qa-tone.wav' }, role: 'piece', revision },
  });
  expect(refRes.status(), 'личная ссылка в контексте').toBe(200);

  await openChat(page, PERSONAL_CHAT);
  await page.waitForTimeout(1500);
  // Личная ссылка в строке контекста — живая: имя файла есть, а «Раздел не загрузился» — нет
  await expect(page.getByText('qa-tone.wav')).toBeVisible({ timeout: 15000 });
  await expect(page.getByText('Раздел не загрузился')).toHaveCount(0);
});

test('6. Регресс: в проекте кнопки «Сохранить в Личное» нет, панель «Файлы» проекта работает', async ({ page }) => {
  await page.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
  await page.goto(`/#/project/${PROJECT_ID}/chat/${PROJECT_CHAT}`);
  await page.waitForTimeout(2500);
  const close = page.getByRole('button', { name: 'Закрыть' });
  if (await close.count()) await close.first().click();

  await expect(page.getByRole('button', { name: 'Добавить в проект' })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Сохранить в Личное' })).toHaveCount(0);

  // Панель «Файлы» проекта открывается и показывает корень проекта
  await page.locator('[aria-label="Файлы"]').first().click();
  await page.mouse.move(600, 400);
  await page.waitForTimeout(1500);
  await expect(page.getByText('Файлы', { exact: true }).first()).toBeVisible();
  await expect(page.getByText('Ищем агент AI Home на этом компьютере')).toHaveCount(0);
});
