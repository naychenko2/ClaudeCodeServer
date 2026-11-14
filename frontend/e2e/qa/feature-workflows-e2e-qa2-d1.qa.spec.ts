import { test, expect } from '@playwright/test';
import { execSync } from 'node:child_process';
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
const epicIds: string[] = [];

test.beforeAll(async ({ playwright, baseURL }) => {
  request = await playwright.request.newContext({ baseURL });
  token = await login(request);
  client = api(request, token);
});
test.afterAll(async () => {
  if (ownEpicId) await client.del(`/api/projects/${PID}/tasks/${ownEpicId}`).catch(() => undefined);
  if (ownTplId) await client.del(`/api/workflow-templates/${ownTplId}`).catch(() => undefined);
  for (const id of epicIds) await client.del(`/api/projects/${PID}/tasks/${id}`).catch(() => undefined);
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
  // Первая вкладка экрана этапа — «Артефакты» по умолчанию; клик только если она не открыта.
  if (await page.getByTestId('stage-artifacts').count() === 0) {
    // Первая вкладка — «Ревью и вопросы»; табы с счётчиками («Артефакты 1») — кликаем по подстроке.
    await page.getByText(/Артефакты/).first().click({ timeout: 8000 }).catch(() => undefined);
    await page.waitForTimeout(2000);
  }
  console.log('DBG stage area открыт:', await page.getByTestId('stage-artifacts').count(),
    '| тела страницы (фрагмент):', JSON.stringify((await page.locator('body').innerText()).replace(/\n/g, ' | ').slice(0, 300)));
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
    screens[t.id] = s;
    console.log('D1a', t.id.slice(0, 8), t.role, JSON.stringify({
      linked: s?.artifacts?.linkedFiles ?? [], files: (s?.artifacts?.files ?? []).map((f: any) => f.path),
      commits: (s?.artifacts?.commits ?? []).length, available: s?.artifacts?.commitsAvailable,
      result: !!s?.artifacts?.resultMarkdown,
    }));
  }

  // Экран этапа с привязанными файлами: сначала ищем этап С коммитами (проверка
  // «рядом с файлами коммитов»), иначе — любой с привязанными файлами.
  const withCommits = chain.find(t => (screens[t.id]?.artifacts?.commits ?? []).length > 0
    && (screens[t.id]?.artifacts?.linkedFiles ?? []).length > 0);
  const withLinked = withCommits ?? chain.find(t => (screens[t.id]?.artifacts?.linkedFiles ?? []).length > 0);
  expect(!!withLinked, 'в сиде есть этап с привязанными файлами').toBeTruthy();
  const linkedFile = screens[withLinked.id].artifacts.linkedFiles[0];
  const hasFiles = (screens[withLinked.id].artifacts.files ?? []).length > 0;
  console.log('D1a выбран этап:', withLinked.id.slice(0, 8), withLinked.role, 'с коммитами:', !!withCommits);

  const { page, errs } = await openStage(context, EPIC, withLinked.role);
  const linkedBlock = page.getByTestId('stage-linked-files');
  const linkedCount = await linkedBlock.count();
  const linkedText = linkedCount ? await linkedBlock.innerText() : '';
  console.log('D1a экран этапа', withLinked.role, ': блок «Файлы задачи» =', linkedCount, '| содержит', linkedFile, ':', linkedText.includes(linkedFile));
  expect.soft(linkedCount, '«Файлы задачи» показаны').toBeGreaterThan(0);
  expect.soft(linkedText.includes(linkedFile), 'привязанный файл назван по имени').toBeTruthy();
  const resultVisible = await page.getByTestId('stage-result').count();
  console.log('D1a итог на экране:', resultVisible, '| файлы коммитов на экране:', hasFiles);
  if (screens[withLinked.id].artifacts.resultMarkdown) {
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
  await page.screenshot({ path: `${SHOTS}/qa2-d1a-${withLinked.role}.png`, fullPage: true });
  console.log('D1a ошибки консоли:', JSON.stringify([...new Set(errs)]));
  await page.close();
});

test('D1c: этап с коммитом — привязанный файл виден рядом с файлами коммитов', async ({ context }) => {
  test.setTimeout(560_000);
  // Дерево задачи — настоящий worktree проекта фикстуры: isolated-репозиторий исполнение
  // не принимает (проверено: исполнитель работал в RootPath проекта, не в /tmp/cff-wt2).
  const WT = '/tmp/cff-wt3';
  execSync(`git -C /tmp/qa-wf-e2e-proj worktree remove --force ${WT} 2>/dev/null || true`);
  execSync(`git -C /tmp/qa-wf-e2e-proj worktree prune && git -C /tmp/qa-wf-e2e-proj worktree add -f ${WT} -B qa2-wt3 >/dev/null`);

  // Свой эпик с маршрутом из одного производственного этапа.
  const tpl = await client.post<any>('/api/workflow-templates', {
    name: 'qa-e2e-qa2-commit', stages: [{ role: 'step', presence: 'on', lane: 'other', title: 'Производство' }],
  });
  const tplId = tpl?.id ?? tpl?.template?.id ?? null;
  const epic = await client.post<any>(`/api/projects/${PID}/tasks`, {
    title: 'qa-e2e-qa2-commit-feature', role: 'feature', assignee: 'me', description: 'Проверка артефактов этапа с коммитом.',
  });
  epicIds.push(epic.id);
  await client.post(`/api/tasks/${epic.id}/workflow/confirm`, { templateId: tplId, scope: 'global' });

  const step = await client.post<any>(`/api/projects/${PID}/tasks`, {
    title: 'qa-e2e-qa2-commit-step', role: 'step', featureId: epic.id, assignee: 'claude', worktreePath: WT,
    description: 'В своём рабочем дереве создай файл qa/step2.md с одной строкой. Закоммить его: git add qa/step2.md && git commit -m "feat(qa): шаг" — и добавь в конец сообщения коммита строку-трейлер «CCS-Task: ' + '' + '». После этого заверши задачу через tasks_complete, указав linkedFiles qa/step2.md.',
  });
  // Трейлер с реальным id задачи.
  await client.put(`/api/tasks/${step.id}`, {
    description: `В своём рабочем дереве создай файл qa/step2.md с одной строкой. Закоммить его: git add qa/step2.md && git commit -m "feat(qa): шаг" — и добавь в конец сообщения коммита строку-трейлер «CCS-Task: ${step.id}». После этого заверши задачу через tasks_complete.`,
  });
  const ex = await request.post(`/api/tasks/${step.id}/execute`, { headers: { Authorization: `Bearer ${token}` } });
  console.log('D1c execute:', ex.status());
  let stage: any = null;
  for (let i = 0; i < 90; i++) {
    await new Promise(r => setTimeout(r, 4000));
    const t = await client.get<any>(`/api/tasks/${step.id}`);
    stage = await client.get<any>(`/api/tasks/${step.id}/stage`);
    if (i % 5 === 0) console.log('D1c poll', i, 'status:', t?.status, 'stop:', t?.executorStopReason);
    if (t?.status === 'done') break;
  }
  console.log('D1c артефакты по API:', JSON.stringify({
    linked: stage?.artifacts?.linkedFiles ?? [], files: (stage?.artifacts?.files ?? []).map((f: any) => f.path),
    commits: (stage?.artifacts?.commits ?? []).length, available: stage?.artifacts?.commitsAvailable,
  }));
  expect.soft((stage?.artifacts?.commits ?? []).length, 'этап получил коммит с меткой задачи').toBeGreaterThan(0);
  expect.soft((stage?.artifacts?.linkedFiles ?? []).length, 'задача привязала файл').toBeGreaterThan(0);

  const { page, errs } = await openStage(context, epic.id, 'step');
  const order = await page.evaluate(() => {
    const art = document.querySelector('[data-testid="stage-artifacts"]');
    if (!art) return null;
    return [...art.querySelectorAll('[data-testid]')].map(e => e.getAttribute('data-testid'));
  });
  console.log('D1c порядок блоков артефактов:', JSON.stringify(order));
  // Панель файлов коммита имеет testid stage-file-<путь>; блок коммитов — stage-commits.
  const iFiles = (order ?? []).findIndex(t => t === 'stage-commits' || (t ?? '').startsWith('stage-file-'));
  const iLinked = (order ?? []).indexOf('stage-linked-files');
  expect.soft(iLinked, '«Файлы задачи» показаны на этапе с коммитами').toBeGreaterThanOrEqual(0);
  expect.soft(iLinked >= 0 && iFiles >= 0 && iLinked > iFiles, 'привязанный файл идёт рядом с файлами коммитов').toBeTruthy();
  await page.screenshot({ path: `${SHOTS}/qa2-d1c-with-commits.png`, fullPage: true });
  console.log('D1c ошибки консоли:', JSON.stringify([...new Set(errs)]));
  await page.close();
  await client.del(`/api/tasks/${step.id}`).catch(() => undefined);
  if (tplId) await client.del(`/api/workflow-templates/${tplId}`).catch(() => undefined);
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
