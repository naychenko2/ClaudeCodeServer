import { test, expect, type APIRequestContext, type Page } from '@playwright/test';
import { mkdirSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

// Регрессия выноса раздела «Архитектура» (Viaduct 10): бэкенд — динамический модуль
// (ModuleLoader, dll в bin/modules/architecture), фронт — MF-remote (/architecture-remote/).
// Ломается это молча: remote не загрузился — кнопки рельсы и документа просто нет, dll не
// доехала — /api/projects/{id}/architecture/* уходит в SPA-фолбэк. Поэтому гоняем по живому
// стенду с собранным Viaduct (scripts/build-viaduct.ps1): модуль в списке, API отвечает,
// панель и документ с редактором в iframe открываются.

const USER = process.env.E2E_USER || 'admin';
const PASS = process.env.E2E_PASS || '12345';

let token = '';
let projectId = '';
let root = '';

async function login(request: APIRequestContext): Promise<string> {
  const r = await request.post('/api/auth/login', { data: { username: USER, password: PASS } });
  expect(r.ok(), 'логин должен пройти').toBeTruthy();
  return (await r.json()).token as string;
}

const auth = () => ({ Authorization: `Bearer ${token}` });

function collectConsole(page: Page, sink: string[]) {
  page.on('console', m => { if (m.type() === 'error' || m.type() === 'warning') sink.push(`[${m.type()}] ${m.text()}`); });
  page.on('pageerror', e => sink.push(`[pageerror] ${e}`));
}

async function openProject(page: Page) {
  await page.goto('/');
  await page.waitForTimeout(5000); // remotes ждутся до 2 с — кнопку рельсы до этого не ассертим
  await page.evaluate(id => { location.hash = `#/project/${id}`; }, projectId);
  await page.waitForTimeout(4000);
}

test.beforeAll(async ({ playwright, baseURL }) => {
  const request = await playwright.request.newContext({ baseURL });
  token = await login(request);
  // Фич-флага у раздела нет: гейт — только загрузка модуля (DynamicModules + Subsystems)
  const me = await (await request.get('/api/auth/me', { headers: auth() })).json() as { subsystems: string[]; featureFlags: Record<string, boolean> };
  expect(me.subsystems, 'модуль architecture должен быть загружен').toContain('architecture');
  expect(me.featureFlags, 'флага architecture больше нет').not.toHaveProperty('architecture');

  root = join(tmpdir(), `ccs-arch-e2e-${Date.now()}`);
  mkdirSync(root, { recursive: true });
  const r = await request.post('/api/projects', { headers: auth(), data: { name: `arch-e2e-${Date.now()}`, rootPath: root } });
  expect(r.ok(), `проект должен создаться: ${r.status()}`).toBeTruthy();
  projectId = (await r.json()).id;
  await request.dispose();
});

test.afterAll(async ({ playwright, baseURL }) => {
  const request = await playwright.request.newContext({ baseURL });
  if (projectId) await request.delete(`/api/projects/${projectId}`, { headers: auth() });
  await request.dispose();
  if (root) rmSync(root, { recursive: true, force: true });
});

test('модуль «Архитектура» загружен: бэкенд из dll, remote и Viaduct раздаются', async ({ playwright, baseURL }) => {
  const request = await playwright.request.newContext({ baseURL });
  const mods = await (await request.get('/api/subsystem-modules', { headers: auth() })).json() as { items: { id: string; remoteUrl?: string }[] };
  expect(mods.items.map(m => m.id)).toContain('architecture');

  const remote = await request.get('/architecture-remote/remoteEntry.js');
  expect(remote.status()).toBe(200);
  expect(remote.headers()['content-type']).toContain('javascript');

  const editor = await request.get('/modules/viaduct/');
  expect(editor.status(), 'Viaduct должен быть собран (scripts/build-viaduct.ps1)').toBe(200);

  // Контроллер живёт в dll модуля: не загрузился — ответ был бы SPA-фолбэком (text/html)
  const model = await request.get(`/api/projects/${projectId}/architecture/model`, { headers: auth() });
  expect(model.status()).toBe(200);
  expect(model.headers()['content-type']).toContain('application/json');
  expect((await model.json()).exists).toBe(false);
  await request.dispose();
});

test('модель: запись, конфликт версии, отказ на не-объект', async ({ playwright, baseURL }) => {
  const request = await playwright.request.newContext({ baseURL });
  const url = `/api/projects/${projectId}/architecture/model`;
  const content = JSON.stringify({ name: 'e2e', elements: [] });

  const first = await request.put(url, { headers: auth(), data: { content, baseVersion: null } });
  expect(first.status()).toBe(200);
  const v1 = (await first.json()).version as string;
  expect(v1).toBeTruthy();

  // Вторая «вкладка» шла от «модели нет» — её запись не должна затереть первую
  const stale = await request.put(url, { headers: auth(), data: { content: '{"x":1}', baseVersion: null } });
  expect(stale.status()).toBe(409);
  expect((await stale.json()).code).toBe('version_conflict');

  const notObject = await request.put(url, { headers: auth(), data: { content: '[1,2]', baseVersion: v1 } });
  expect(notObject.status()).toBe(400);

  const after = await (await request.get(url, { headers: auth() })).json();
  expect(after.version).toBe(v1);
  expect(after.content).toBe(content);
  await request.dispose();
});

test('панель рельсы и документ с редактором Viaduct открываются', async ({ page, context }) => {
  const logs: string[] = [];
  collectConsole(page, logs);
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
  await openProject(page);

  // Кнопка рельсы открывает панель (вклад слота workspace-panel), холст — отдельный
  // вклад workspace-center-doc: гейтятся оба, поэтому проверяем оба
  await page.getByRole('button', { name: 'Архитектура' }).first().click();
  await page.getByRole('button', { name: 'Открыть холст' }).click();
  await page.waitForTimeout(2500);

  const moduleFailed = logs.filter(l => l.includes('[subsystems]'));
  expect(moduleFailed, 'remote подсистемы должен загрузиться без предупреждений').toEqual([]);

  const frame = page.locator('iframe[src*="/modules/viaduct"]');
  await expect(frame, 'документ должен поднять редактор Viaduct в iframe').toHaveCount(1, { timeout: 15_000 });
});
