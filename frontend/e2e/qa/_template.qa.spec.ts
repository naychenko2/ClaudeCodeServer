import { test, expect, type APIRequestContext } from '@playwright/test';
import { api, collectConsoleErrors, login, seed, type Api } from './helpers';

// Шаблон QA-спека: логин → засев → сценарий → проверки → уборка в afterAll.
// Копируй в `<фича>.qa.spec.ts`; запуск — target=frontend/e2e/qa (docs/operations/qa-stand.md).
// Всё засеянное — с префиксом `qa-tpl-`, чтобы уборку и проверку «ничего не осталось» делать по имени.

const PREFIX = 'qa-tpl-';

test.describe.configure({ mode: 'serial' });

let request: APIRequestContext;
let token: string;
let client: Api;
let seeded: ReturnType<typeof seed>;
let projectId: string;
let taskTitle: string;

test.beforeAll(async ({ playwright, baseURL }) => {
  request = await playwright.request.newContext({ baseURL });
  token = await login(request);
  client = api(request, token);
  seeded = seed(client);

  const stamp = Date.now();
  const project = await seeded.project(`${PREFIX}p-${stamp}`, `/tmp/${PREFIX}${stamp}`);
  projectId = project.id;
  await seeded.session(projectId, `${PREFIX}chat-${stamp}`);
  taskTitle = `${PREFIX}task-${stamp}`;
  await seeded.task(taskTitle, projectId);
});

test.afterAll(async () => {
  await seeded?.cleanup();
  await request?.dispose();
});

test('засеянные сущности видны в API и приложение открывается без ошибок консоли', async ({ page, context }) => {
  const errors = collectConsoleErrors(page);
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);

  // Сценарий: открыть приложение под токеном. Свою фичу проверяй здесь (page.goto('/#/…'))
  await page.goto('/');
  await expect(page.locator('#root')).not.toBeEmpty();

  // Проверки через API: то, что засеяли, действительно существует
  const projects = await client.get<Array<{ id: string; name: string }>>('/api/projects');
  expect(projects.some(p => p.id === projectId), 'засеянный проект виден в списке').toBeTruthy();
  const tasks = await client.get<Array<{ title: string }>>('/api/tasks');
  expect(tasks.some(t => t.title === taskTitle), 'засеянная задача видна в списке').toBeTruthy();

  expect(errors, 'в консоли не должно быть ошибок').toEqual([]);
});
