import { test, expect } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { login, api, fullCycleTemplate, editFullCycleTemplate, alignEpicRoute, type Api } from './helpers';

// С8v2: «шаблон изменился» на идущей фиче — про состав маршрута, а не про версию шаблона.
// Переименование этапа карточки не даёт; добавленный этап — даёт, карточка достижима через
// «Сменить», решение человека («Оставить»), готовые шаги не переписываются.

const PID = 'ce3e224a-d0f8-4828-9519-f51d4c3dbf6a';
const EPIC = 'f571aace-a780-4c3e-abca-05741154c8ad';
const SHOTS = '.cc-attachments/qa-wf-e2e';

let client: Api;
let token: string;
let request: import('@playwright/test').APIRequestContext;

test.beforeAll(async ({ playwright, baseURL }) => {
  mkdirSync(SHOTS, { recursive: true });
  request = await playwright.request.newContext({ baseURL });
  token = await login(request);
  client = api(request, token);
});
test.afterAll(async () => { await request?.dispose(); });

test('С8v2: карточка обновления маршрута — только при смене состава', async ({ context }) => {
  test.setTimeout(220_000);
  const page = await context.newPage();
  page.setViewportSize({ width: 1280, height: 900 });
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);

  const open = async () => {
    await page.goto(`/#/project/${PID}/feature/${EPIC}`, { waitUntil: 'domcontentloaded' });
    // тот же hash-URL перезагрузки не даёт: без неё экран держит шаблон, загруженный до правки
    await page.reload({ waitUntil: 'domcontentloaded' });
    await page.waitForTimeout(4000);
  };
  const cardCount = async () => {
    const change = page.getByTestId('feature-workflow-confirm-change').first();
    if (await page.getByTestId('feature-workflow-confirm-update').count() === 0 && await change.count()) {
      await change.click({ timeout: 15000 });
      await page.waitForTimeout(1500);
    }
    return page.getByTestId('feature-workflow-confirm-update').count();
  };

  const tplStart = await fullCycleTemplate(client);
  const titleStart = tplStart.stages.find((s: any) => s.role === 'design')?.title ?? null;
  const qaStart = tplStart.stages.find((s: any) => s.role === 'qa')?.presence;
  try {
    const before = await alignEpicRoute(client, EPIC);
    const versionBefore = before.confirmed.confirmedVersion;
    const stepsBefore = (await client.get<any[]>(`/api/projects/${PID}/tasks`)).filter(t => t.featureId === EPIC);
    const viewBefore = stepsBefore.map(t => ({ role: t.role, v: t.stage?.templateVersion }));

    // 1. Переименование этапа: версия шаблона растёт, состав прежний — карточки нет.
    await editFullCycleTemplate(client, t => { t.stages.find((s: any) => s.role === 'design').title = `Дизайн · v2 ${Date.now() % 100000}`; });
    const wfRenamed = await client.get<any>(`/api/tasks/${EPIC}/workflow`);
    expect(wfRenamed.updateAvailable, 'переименование: updateAvailable=false').toBe(false);
    expect(wfRenamed.templateUpdate, 'переименование: templateUpdate=null').toBeNull();
    await open();
    expect(await cardCount(), 'переименование: карточки «шаблон изменился» нет').toBe(0);
    await page.screenshot({ path: `${SHOTS}/С8v2-rename-no-card.png`, fullPage: true });

    // 2. Этап стал обязательным — состав изменился, карточка есть.
    await editFullCycleTemplate(client, t => { t.stages.find((s: any) => s.role === 'qa').presence = 'on'; });
    const tplAdded = await fullCycleTemplate(client);
    const wfAdded = await client.get<any>(`/api/tasks/${EPIC}/workflow`);
    expect(wfAdded.updateAvailable, 'добавлен этап: updateAvailable=true').toBe(true);
    expect(wfAdded.templateUpdate?.toVersion, 'templateUpdate указывает на новую версию').toBe(tplAdded.version);
    await open();
    expect(await cardCount(), 'добавлен этап: карточка «шаблон изменился» показана').toBeGreaterThan(0);
    expect(await page.getByTestId('feature-workflow-confirm-update-apply').count(), 'кнопка «Обновить»').toBeGreaterThan(0);
    expect(await page.getByTestId('feature-workflow-confirm-update-keep').count(), 'кнопка «Оставить»').toBeGreaterThan(0);
    await page.screenshot({ path: `${SHOTS}/С8v2-update-card.png`, fullPage: true });

    // 3. «Оставить» — предупреждение снимается, маршрут не меняется.
    await page.getByTestId('feature-workflow-confirm-update-keep').first().click({ timeout: 15000 });
    await expect.poll(async () => (await client.get<any>(`/api/tasks/${EPIC}/workflow`)).confirmed?.dismissedUpdateVersion, { timeout: 15000 }).toBe(tplAdded.version);
    const wfKept = await client.get<any>(`/api/tasks/${EPIC}/workflow`);
    expect(wfKept.confirmed.confirmedVersion, 'маршрут остался на своей версии').toBe(versionBefore);
    expect(wfKept.updateAvailable, 'предупреждение снято до следующей версии').toBe(false);
    expect(wfKept.templateUpdate, 'templateUpdate после «Оставить» — null').toBeNull();

    const stepsAfter = (await client.get<any[]>(`/api/projects/${PID}/tasks`)).filter(t => t.featureId === EPIC);
    expect(stepsAfter.map(t => ({ role: t.role, v: t.stage?.templateVersion })), 'созданные шаги не переписаны').toEqual(viewBefore);
  } finally {
    // Возвращаем шаблон и маршрут как были: фикстура общая для остальных спеков.
    await editFullCycleTemplate(client, t => {
      const d = t.stages.find((s: any) => s.role === 'design'); if (titleStart === null) delete d.title; else d.title = titleStart;
      if (qaStart) t.stages.find((s: any) => s.role === 'qa').presence = qaStart;
    }).catch(e => console.log('С8v2 откат шаблона:', String(e).slice(0, 120)));
    await alignEpicRoute(client, EPIC).catch(e => console.log('С8v2 откат маршрута:', String(e).slice(0, 120)));
    await page.close();
  }
});
