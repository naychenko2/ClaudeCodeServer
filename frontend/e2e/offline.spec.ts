import { test, expect, type Page, type APIRequestContext } from '@playwright/test';

// E2E офлайн-режима задач и заметок. Симулируем офлайн через context.setOffline
// (браузерный уровень — как реальная потеря связи: fetch/WS падают, SW отдаёт кэш).
// Проверяем то, что не ловят юниты: SW-персистентность при перезагрузке, дренаж
// очередей при возврате связи, отсутствие дублей, и дренаж заметок НЕ со страницы
// «Заметки» (регрессия на баг App-level drain).

const USER = process.env.E2E_USER || 'admin';
const PASS = process.env.E2E_PASS || '12345';

// Уникальный суффикс — чтобы прогоны не сталкивались и легко чистить за собой
const RUN = Date.now().toString(36);
const TASK_TITLE = `E2E задача ${RUN}`;
const NOTE_TITLE = `E2E заметка ${RUN}`;

async function login(request: APIRequestContext): Promise<string> {
  const r = await request.post('/api/auth/login', { data: { username: USER, password: PASS } });
  expect(r.ok(), 'логин должен пройти').toBeTruthy();
  return (await r.json()).token as string;
}

const auth = (token: string) => ({ Authorization: `Bearer ${token}` });

async function enableFlag(request: APIRequestContext, token: string, key: string) {
  const r = await request.put(`/api/feature-flags/${key}`, { headers: auth(token), data: { enabled: true } });
  expect(r.ok(), `флаг ${key} должен включиться`).toBeTruthy();
}

// Счётчик записей в сторе IndexedDB (outbox / notesOutbox / noteContent)
async function idbCount(page: Page, store: string): Promise<number> {
  return page.evaluate(async (s) => {
    const db: IDBDatabase = await new Promise((res, rej) => {
      const req = indexedDB.open('ccs-offline');
      req.onsuccess = () => res(req.result);
      req.onerror = () => rej(req.error);
    });
    return await new Promise<number>((res) => {
      const t = db.transaction(s, 'readonly').objectStore(s).count();
      t.onsuccess = () => res(t.result);
      t.onerror = () => res(-1);
    });
  }, store);
}

test.describe('офлайн-режим', () => {
  let token: string;

  test.beforeAll(async ({ playwright, baseURL }) => {
    const request = await playwright.request.newContext({ baseURL });
    token = await login(request);
    await enableFlag(request, token, 'tasks-offline');
    await enableFlag(request, token, 'notes-offline');
    await request.dispose();
  });

  // Чистим тестовые данные с сервера после прогона
  test.afterAll(async ({ playwright, baseURL }) => {
    const request = await playwright.request.newContext({ baseURL });
    const t = await login(request);
    const tasks = await (await request.get('/api/tasks', { headers: auth(t) })).json();
    for (const task of tasks.filter((x: any) => x.title === TASK_TITLE))
      await request.delete(`/api/tasks/${task.id}`, { headers: auth(t) });
    const notes = await (await request.get('/api/notes', { headers: auth(t) })).json();
    for (const n of notes.filter((x: any) => x.title === NOTE_TITLE))
      await request.delete(`/api/notes/${encodeURIComponent(n.id)}`, { headers: auth(t) });
    await request.dispose();
  });

  test('создание офлайн переживает перезагрузку и синхронизируется без дублей', async ({ page, context, request }) => {
    // Токен до загрузки приложения → сразу авторизованы
    await context.addInitScript((tk) => localStorage.setItem('cc_token', tk as string), token);

    await page.goto('/#/calendar');
    await expect(page.getByRole('button', { name: 'Задача' })).toBeVisible();
    // Дать снапшоту/флагам подгрузиться
    await page.waitForTimeout(1500);

    // --- Уходим офлайн ---
    await context.setOffline(true);

    // Создать задачу офлайн (Календарь → + Задача → срок сегодня → Создать)
    await page.getByRole('button', { name: 'Задача' }).first().click();
    await page.getByPlaceholder('Что нужно сделать?').fill(TASK_TITLE);
    await page.getByRole('button', { name: 'Сегодня', exact: true }).click();
    await page.getByRole('button', { name: 'Создать', exact: true }).click();
    await expect(page.getByText(TASK_TITLE).first()).toBeVisible();

    // Создать заметку офлайн (Заметки → Новая → Создать), затем вернуться на Календарь —
    // проверяем дренаж заметок НЕ со страницы «Заметки» (тот самый баг).
    await page.getByRole('button', { name: 'Заметки' }).click();
    await page.getByRole('button', { name: 'Новая', exact: true }).click();
    await page.getByPlaceholder('Название заметки').fill(NOTE_TITLE);
    await page.getByRole('button', { name: 'Создать', exact: true }).click();
    // Заголовок встречается дважды (тулбар + H1 из markdown-контента) — берём первый
    await expect(page.getByRole('heading', { name: NOTE_TITLE }).first()).toBeVisible();

    // Очереди наполнились
    expect(await idbCount(page, 'outbox')).toBeGreaterThanOrEqual(1);
    expect(await idbCount(page, 'notesOutbox')).toBeGreaterThanOrEqual(1);

    // Уходим на Календарь (заметки-подписка не смонтирована) и перезагружаемся ОФЛАЙН
    await page.getByRole('button', { name: 'Календарь' }).click();
    await page.reload();

    // Пережили перезагрузку (SW отдал приложение, стор гидрировался из IndexedDB)
    await expect(page.getByText(TASK_TITLE).first()).toBeVisible();
    expect(await idbCount(page, 'outbox')).toBeGreaterThanOrEqual(1);
    expect(await idbCount(page, 'notesOutbox')).toBeGreaterThanOrEqual(1);

    // --- Возвращаем связь ---
    await context.setOffline(false);

    // Дренаж должен опустошить обе очереди (в т.ч. заметку — со страницы Календаря)
    await expect.poll(() => idbCount(page, 'outbox'), { timeout: 30_000 }).toBe(0);
    await expect.poll(() => idbCount(page, 'notesOutbox'), { timeout: 30_000 }).toBe(0);

    // Сервер получил ровно по одному — без дублей
    const serverTasks = await (await request.get('/api/tasks', { headers: auth(token) })).json();
    expect(serverTasks.filter((t: any) => t.title === TASK_TITLE)).toHaveLength(1);

    const serverNotes = await (await request.get('/api/notes', { headers: auth(token) })).json();
    expect(serverNotes.filter((n: any) => n.title === NOTE_TITLE)).toHaveLength(1);
  });
});
