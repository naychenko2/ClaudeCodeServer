import { test, expect } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { login, api, type Api } from './helpers';

// QA-2 (задача 6c3cdc39), п.8: правка D1 (merge ddf1f91a9) на живом стенде.
// D1a: этап с привязанными файлами — «Файлы задачи» видны и без коммитов, и рядом с
//      файлами коммитов; итог виден без коммитов.
// D1b: этап без дерева задачи — текст «Коммиты недоступны: дерево задачи не найдено.»,
//      а не «Появятся с первым коммитом».

const PID = 'ce3e224a-d0f8-4828-9519-f51d4c3dbf6a';
const EPIC = 'f571aace-a780-4c3e-abca-05741154c8ad';
const SHOTS = '.cc-attachments/qa-wf-e2e';

let client: Api;
let request: import('@playwright/test').APIRequestContext;
let token: string;
let ownEpicId: string | null = null;
let ownTplId: string | null = null;

test.beforeAll(async ({ playwright, baseURL }) => {
  request = await playwright.request.newContext({ baseURL });
  token = await login(request);
  client = api(request, token);
});
test.afterAll(async () => {
  if (ownEpicId) await client.del(`/api/projects/${PID}/tasks/${ownEpicId}`).catch(() => undefined);
  if (ownTplId) await client.del(`/api/workflow-templates/${ownTplId}`).catch(() => undefined);
  await request?.dispose();
});

async function openStage(context: import('@playwright/test').BrowserContext, epicId: string, stageNode: string) {
  const page = await context.newPage();
  page.setViewportSize({ width: 1280, height: 900 });
  const errs: string[] = [];
  page.on('console', m => { if (m.type() === 'error') errs.push(m.text().slice(0, 200)); });
  page.on('pageerror', e => errs.push(String(e).slice(0, 200)));
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
  await page.goto(`/#/project/${PID}/feature/${epicId}`, { waitUntil: 'domcontentloaded' });
  await page.waitForTimeout(4000);
  await page.getByTestId(`pgraph-node-${stageNode}`).click({ timeout: 15000 });
  await page.waitForTimeout(1500);
  await page.getByTestId('feature-stage-area-open').click({ timeout: 15000 });
  await page.waitForTimeout(3000);
  await page.getByText('Артефакты', { exact: true }).first().click().catch(() => undefined);
  await page.waitForTimeout(2000);
  return { page, errs };
}

test('D1a: привязанные файлы и итог видны; с коммитами — рядом с файлами коммитов', async ({ context }) => {
  test.setTimeout(240_000);
  mkdirSync(SHOTS, { recursive: true });

  // Что реально отдаёт бэк по этапам сида — по этому выбираем экран для замера.
  const tasks = await client.get<any[]>(`/api/projects/${PID}/tasks`);
  const chain = tasks.filter(t => t.featureId === EPIC);
  const screens: Record<string, any> = {};
  for (const t of chain) {
    const s = await client.get<any>(`/api/tasks/${t.id}/stage`);
    screens[t.role] = s;
    console.log('D1a', t.role, JSON.stringify({
      linked: s?.artifacts?.linkedFiles ?? [], files: (s?.artifacts?.files ?? []).map((f: any) => f.path),
      commits: (s?.artifacts?.commits ?? []).length, available: s?.artifacts?.commitsAvailable,
      result: !!s?.artifacts?.resultMarkdown,
    }));
  }

  // Экран этапа с привязанными файлами (любой из сида): «Файлы задачи» обязаны быть.
  const withLinked = chain.find(t => (screens[t.role]?.artifacts?.linkedFiles ?? []).length > 0);
  expect(!!withLinked, 'в сиде есть этап с привязанными файлами').toBeTruthy();
  const role = withLinked!.role;
  const linkedFile = screens[role].artifacts.linkedFiles[0];
  const hasFiles = (screens[role].artifacts.files ?? []).length > 0;

  const { page, errs } = await openStage(context, EPIC, role);
  const linkedBlock = page.getByTestId('stage-linked-files');
  const linkedCount = await linkedBlock.count();
  const linkedText = linkedCount ? await linkedBlock.innerText() : '';
  console.log('D1a экран этапа', role, ': блок «Файлы задачи» =', linkedCount, '| содержит', linkedFile, ':', linkedText.includes(linkedFile));
  expect.soft(linkedCount, '«Файлы задачи» показаны').toBeGreaterThan(0);
  expect.soft(linkedText.includes(linkedFile), 'привязанный файл назван по имени').toBeTruthy();
  const resultVisible = await page.getByTestId('stage-result').count();
  console.log('D1a итог на экране:', resultVisible, '| файлы коммитов на экране:', hasFiles);
  if (screens[role].artifacts.resultMarkdown) {
    expect.soft(resultVisible, 'итог задачи показан').toBeGreaterThan(0);
  }
  if (hasFiles) {
    // ветка «с коммитами»: привязанный файл обязан быть именно рядом с файлами коммитов
    const filesPanel = await page.getByTestId('stage-files').count().catch(() => 0);
    const order = await page.evaluate(() => {
      const art = document.querySelector('[data-testid="stage-artifacts"]');
      if (!art) return null;
      const kids = [...art.querySelectorAll('[data-testid]')].map(e => e.getAttribute('data-testid'));
      return kids;
    });
    console.log('D1a порядок блоков артефактов:', JSON.stringify(order));
    expect.soft(filesPanel, 'панель файлов коммитов есть').toBeGreaterThan(0);
    const iFiles = (order ?? []).indexOf('stage-files');
    const iLinked = (order ?? []).indexOf('stage-linked-files');
    expect.soft(iLinked >= 0 && iLinked > iFiles, '«Файлы задачи» идут после панели файлов коммитов').toBeTruthy();
  }
  await page.screenshot({ path: `${SHOTS}/qa2-d1a-${role}.png`, fullPage: true });
  console.log('D1a ошибки консоли:', JSON.stringify([...new Set(errs)]));
  await page.close();
});

test('D1b: этап без дерева задачи — текст «Коммиты недоступны…», не обещание коммита', async ({ context }) => {
  test.setTimeout(240_000);

  // Свой эпик с простым маршрутом: ни одна задача не имеет дерева → дерево задачи недоступно.
  const tpl = await client.post<any>('/api/workflow-templates', {
    name: 'qa-e2e-qa2-notree', stages: [{ role: 'cut', presence: 'on' }, { role: 'review', presence: 'on' }],
  });
  ownTplId = tpl?.id ?? tpl?.template?.id ?? null;
  const epic = await client.post<any>(`/api/projects/${PID}/tasks`, {
    title: 'qa-e2e-qa2-notree-feature', role: 'feature', assignee: 'me', description: 'Проверка пустого состояния артефактов без дерева.',
  });
  ownEpicId = epic.id;
  await client.post(`/api/tasks/${ownEpicId}/workflow/confirm`, { templateId: ownTplId, scope: 'global' });
  const review = await client.post<any>(`/api/projects/${PID}/tasks`, {
    title: 'qa-e2e-qa2-notree-review', role: 'review', featureId: ownEpicId, assignee: 'claude',
    description: 'Ничего не делай.',
  });

  const s = await client.get<any>(`/api/tasks/${review.id}/stage`);
  console.log('D1b артефакты по API:', JSON.stringify({
    commits: s?.artifacts?.commits?.length, available: s?.artifacts?.commitsAvailable,
    linked: s?.artifacts?.linkedFiles, files: s?.artifacts?.files?.length,
  }));
  expect.soft(s?.artifacts?.commitsAvailable, 'бэк говорит: дерево задачи недоступно').toBe(false);

  const { page, errs } = await openStage(context, ownEpicId!, 'review');
  const body = (await page.locator('body').innerText()).replace(/\n/g, ' | ');
  const hasNoTree = body.includes('Коммиты недоступны: дерево задачи не найдено');
  const hasPromise = body.includes('Появятся с первым коммитом');
  console.log('D1b тексты пустого состояния: «Коммиты недоступны…» =', hasNoTree, '| обещание коммита =', hasPromise);
  expect.soft(hasNoTree, 'пустое состояние говорит про отсутствие дерева').toBeTruthy();
  expect.soft(hasPromise, 'обещание «Появятся с первым коммитом» не показано').toBeFalsy();
  await page.screenshot({ path: `${SHOTS}/qa2-d1b-notree.png`, fullPage: true });
  console.log('D1b ошибки консоли:', JSON.stringify([...new Set(errs)]));
  await page.close();
  await client.del(`/api/projects/${PID}/tasks/${review.id}`).catch(() => undefined);
});
