import { test, expect, type APIRequestContext, type Page } from '@playwright/test';

// Регрессия пилота динамических модулей: подсистема «Заметки» приезжает MF-remote'ом,
// её код берёт сторы и компоненты каркаса из кита хоста (`aihome_shell/kit`).
// Ломается это молча — раздел открывается, но список пуст (или раздела нет вовсе),
// поэтому проверяем по живому стенду: список в разделе и в панели проекта.

const USER = process.env.E2E_USER || 'admin';
const PASS = process.env.E2E_PASS || '12345';

type Note = { id: string; title: string; source: string };

async function login(request: APIRequestContext): Promise<string> {
  const r = await request.post('/api/auth/login', { data: { username: USER, password: PASS } });
  expect(r.ok(), 'логин должен пройти').toBeTruthy();
  return (await r.json()).token as string;
}

// Предупреждения тоже собираем: провал загрузки модуля виден только как console.warn
// из loadSubsystemRemotes — без него раздел исчезает без единого следа.
function collectConsole(page: Page, sink: string[]) {
  page.on('console', m => { if (m.type() === 'error' || m.type() === 'warning') sink.push(`[${m.type()}] ${m.text()}`); });
  page.on('pageerror', e => sink.push(`[pageerror] ${e}`));
}

test('раздел «Заметки» открывается по диплинку и показывает список', async ({ page, context, playwright, baseURL }) => {
  const request = await playwright.request.newContext({ baseURL });
  const token = await login(request);
  const notes = (await (await request.get('/api/notes', { headers: { Authorization: `Bearer ${token}` } })).json()) as Note[];
  await request.dispose();
  expect(notes.length, 'для проверки нужны заметки у пользователя').toBeGreaterThan(0);

  const logs: string[] = [];
  const notesCalls: string[] = [];
  collectConsole(page, logs);
  page.on('request', r => { if (r.url().includes('/api/notes')) notesCalls.push(r.url()); });

  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
  await page.goto('/#/notes');
  await page.waitForTimeout(8000);

  const moduleFailed = logs.filter(l => l.includes('[subsystems]'));
  expect(moduleFailed, 'модуль подсистемы должен загрузиться без предупреждений').toEqual([]);
  expect(notesCalls.length, 'раздел обязан сходить за списком заметок').toBeGreaterThan(0);

  const text = await page.locator('body').innerText();
  const shown = notes.filter(n => text.includes(n.title)).length;
  console.log('[notes] заголовков из API видно на экране:', shown, 'из', notes.length);
  expect(shown, 'на экране должны быть заголовки заметок из API').toBeGreaterThan(0);
});

test('панель «Заметки» проекта показывает заметки этого проекта', async ({ page, context, playwright, baseURL }) => {
  const request = await playwright.request.newContext({ baseURL });
  const token = await login(request);
  const notes = (await (await request.get('/api/notes', { headers: { Authorization: `Bearer ${token}` } })).json()) as Note[];
  await request.dispose();

  // Проект с наибольшим числом заметок — на нём проверка нагляднее всего
  const bySource = new Map<string, number>();
  for (const n of notes) if (n.source !== 'personal') bySource.set(n.source, (bySource.get(n.source) ?? 0) + 1);
  const [projectId, count] = [...bySource.entries()].sort((a, b) => b[1] - a[1])[0] ?? ['', 0];
  expect(count, 'для проверки нужен проект с заметками').toBeGreaterThan(0);

  const logs: string[] = [];
  collectConsole(page, logs);
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
  await page.goto('/');
  await page.waitForTimeout(6000);
  await page.evaluate(id => { location.hash = `#/project/${id}`; }, projectId);
  await page.waitForTimeout(6000);

  await page.getByText('Заметки', { exact: true }).first().click();
  await page.waitForTimeout(2500);

  const text = await page.locator('body').innerText();
  const shown = notes.filter(n => n.source === projectId && text.includes(n.title)).length;
  console.log('[notes] заметок проекта видно:', shown, 'из', count);
  expect(shown, 'панель проекта должна показывать его заметки').toBeGreaterThan(0);
});
