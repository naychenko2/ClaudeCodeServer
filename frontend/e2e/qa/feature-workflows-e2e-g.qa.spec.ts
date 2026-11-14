import { test, expect, type Page } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { login, api, fullCycleTemplate, editFullCycleTemplate, alignEpicRoute, type Api } from './helpers';

// Часть G: правка шаблона у идущей фичи («шаблон изменился» — про состав маршрута, не про версию:
// переименование этапа предупреждения не даёт, добавленный этап — даёт; готовые шаги не
// переписываются) и кастомизация на уровне проекта поверх админского пресета.

const PID = 'ce3e224a-d0f8-4828-9519-f51d4c3dbf6a';
const EPIC = 'f571aace-a780-4c3e-abca-05741154c8ad';
const SHOTS = '.cc-attachments/qa-wf-e2e';
const flat = (s: string) => s.replace(/\n/g, ' | ');

let client: Api;
let request: import('@playwright/test').APIRequestContext;

test.beforeAll(async ({ playwright, baseURL }) => {
  mkdirSync(SHOTS, { recursive: true });
  request = await playwright.request.newContext({ baseURL });
  const token = await login(request);
  client = api(request, token);
});
test.afterAll(async () => { await request?.dispose(); });

async function editFullCycle(page: Page, title: string) {
  await page.goto('/#/specialties', { waitUntil: 'domcontentloaded' });
  await page.waitForTimeout(2500);
  await page.getByText('Процессы фичи', { exact: true }).click();
  await page.waitForTimeout(1500);
  await page.getByRole('button', { name: /Полный цикл/ }).first().click();
  await page.waitForTimeout(1200);
  await page.locator('[data-stage-index]').filter({ hasText: 'design' }).first().click();
  await page.waitForTimeout(1000);
  const nameInput = page.locator('[data-stage-settings]').locator('input').first();
  await nameInput.fill(title);
  await page.getByLabel('Закрыть настройку этапа').first().click({ timeout: 8000 }).catch(() => page.keyboard.press('Escape'));
  await page.waitForTimeout(500);
  await page.getByRole('button', { name: 'Сохранить', exact: true }).first().click({ timeout: 15000 });
  await page.waitForTimeout(2500);
}

// Карточка «шаблон изменился» достижима через «Сменить»: открываем фичу, раскрываем маршрут
// и возвращаем число карточек.
async function updateCardCount(page: Page): Promise<number> {
  await page.goto(`/#/project/${PID}/feature/${EPIC}`, { waitUntil: 'domcontentloaded' });
  // тот же hash-URL перезагрузки не даёт: без неё экран держит шаблон, загруженный до правки
  await page.reload({ waitUntil: 'domcontentloaded' });
  await page.waitForTimeout(4000);
  if (await page.getByTestId('feature-workflow-confirm-update').count() === 0) {
    const change = page.getByTestId('feature-workflow-confirm-change').first();
    if (await change.count()) { await change.click({ timeout: 15000 }); await page.waitForTimeout(1500); }
  }
  return page.getByTestId('feature-workflow-confirm-update').count();
}

test('С8: переименование этапа — не «шаблон изменился»; добавленный этап — «шаблон изменился»', async ({ context, playwright, baseURL }) => {
  test.setTimeout(280_000);
  const req = await playwright.request.newContext({ baseURL });
  const token = await login(req);
  await req.dispose();
  const page = await context.newPage();
  page.setViewportSize({ width: 1280, height: 900 });
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);

  const tplStart = await fullCycleTemplate(client);
  const titleStart = tplStart.stages.find((s: any) => s.role === 'design')?.title ?? null;
  const qaStart = tplStart.stages.find((s: any) => s.role === 'qa')?.presence;
  try {
    const before = await alignEpicRoute(client, EPIC);
    const stepsBefore = (await client.get<any[]>(`/api/projects/${PID}/tasks`)).filter(t => t.featureId === EPIC);
    const viewBefore = stepsBefore.map(t => ({ role: t.role, v: t.stage?.templateVersion }));

    // 1. Переименование этапа через экран шаблонов: версия шаблона растёт, состав не меняется.
    const renamed = `Дизайн · проверка ${Date.now() % 100000}`;
    await editFullCycle(page, renamed);
    const tplRenamed = await fullCycleTemplate(client);
    expect(tplRenamed.stages.find((s: any) => s.role === 'design')?.title, 'название этапа сохранилось').toBe(renamed);
    expect(tplRenamed.version, 'версия шаблона выросла').toBeGreaterThan(tplStart.version);

    const wfRenamed = await client.get<any>(`/api/tasks/${EPIC}/workflow`);
    expect(wfRenamed.updateAvailable, 'переименование состав не меняет: updateAvailable=false').toBe(false);
    expect(wfRenamed.templateUpdate, 'переименование состав не меняет: templateUpdate=null').toBeNull();
    expect(await updateCardCount(page), 'карточки «шаблон изменился» нет').toBe(0);
    await page.screenshot({ path: `${SHOTS}/С8-rename-no-card.png`, fullPage: true });

    // 2. Добавленный этап (qa из «выключен» в «обязателен») — состав меняется.
    await editFullCycleTemplate(client, t => { t.stages.find((s: any) => s.role === 'qa').presence = 'on'; });
    const tplAdded = await fullCycleTemplate(client);
    const wfAdded = await client.get<any>(`/api/tasks/${EPIC}/workflow`);
    expect(wfAdded.updateAvailable, 'добавлен этап: updateAvailable=true').toBe(true);
    expect(wfAdded.templateUpdate?.toVersion, 'templateUpdate указывает на новую версию').toBe(tplAdded.version);
    expect(await updateCardCount(page), 'карточка «шаблон изменился» показана').toBeGreaterThan(0);
    await page.screenshot({ path: `${SHOTS}/С8-added-card.png`, fullPage: true });

    const stepsAdded = (await client.get<any[]>(`/api/projects/${PID}/tasks`)).filter(t => t.featureId === EPIC);
    expect(stepsAdded.map(t => ({ role: t.role, v: t.stage?.templateVersion })), 'созданные шаги остались на своей версии шаблона').toEqual(viewBefore);

    // 3. «Обновить»: маршрут получает новый этап и версию шаблона.
    await page.getByTestId('feature-workflow-confirm-update-apply').first().click({ timeout: 15000 });
    await expect.poll(async () => (await client.get<any>(`/api/tasks/${EPIC}/workflow`)).confirmed?.confirmedVersion, { timeout: 15000 }).toBe(tplAdded.version);
    const after = await client.get<any>(`/api/tasks/${EPIC}/workflow`);
    expect(after.updateAvailable, 'после «Обновить» предупреждения нет').toBe(false);
    expect(after.confirmed.confirmedVersion, 'маршрут обновился до новой версии').toBeGreaterThan(before.confirmed.confirmedVersion);
    const stepsAfter = (await client.get<any[]>(`/api/projects/${PID}/tasks`)).filter(t => t.featureId === EPIC);
    expect(stepsAfter.map(t => ({ role: t.role, v: t.stage?.templateVersion })), 'готовые шаги не переписаны').toEqual(viewBefore);
    await page.screenshot({ path: `${SHOTS}/С8-after-apply.png`, fullPage: true });
  } finally {
    // Возвращаем шаблон и маршрут как были: фикстура общая для остальных спеков.
    await editFullCycleTemplate(client, t => {
      const d = t.stages.find((s: any) => s.role === 'design'); if (titleStart === null) delete d.title; else d.title = titleStart;
      if (qaStart) t.stages.find((s: any) => s.role === 'qa').presence = qaStart;
    }).catch(e => console.log('С8 откат шаблона:', String(e).slice(0, 120)));
    await alignEpicRoute(client, EPIC).catch(e => console.log('С8 откат маршрута:', String(e).slice(0, 120)));
    await page.close();
  }
});

test('С9: проектная копия глобального пресета', async ({ context, playwright, baseURL }) => {
  test.setTimeout(240_000);
  const page = await context.newPage();
  page.setViewportSize({ width: 1280, height: 900 });
  const req = await playwright.request.newContext({ baseURL });
  const token = await login(req);
  await req.dispose();
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
  await page.goto(`/#/project/${PID}/feature/${EPIC}`, { waitUntil: 'domcontentloaded' });
  await page.waitForTimeout(4000);
  await page.getByRole('button', { name: /Настройки проекта/ }).first().click({ timeout: 15000 }).catch(e => console.log('С9 настройки проекта:', String(e).slice(0, 100)));
  await page.waitForTimeout(2000);
  await page.getByText('Процессы', { exact: true }).first().click({ timeout: 10000 }).catch(e => console.log('С9 секция Процессы:', String(e).slice(0, 100)));
  await page.waitForTimeout(2000);
  await page.getByRole('button', { name: /Полный цикл/ }).first().click().catch(e => console.log('С9 открыть глобальный:', String(e).slice(0, 100)));
  await page.waitForTimeout(1500);
  const body = await page.locator('body').innerText();
  console.log('С9 виден ли глобальный на чтение:', /правит администратор/i.test(flat(body).slice(-2000)) ? 'да' : 'нет', '| кнопка «Создать проектный»:', await page.getByRole('button', { name: 'Создать проектный' }).count());
  await page.getByRole('button', { name: 'Создать проектный' }).first().click({ timeout: 10000 }).catch(() => undefined);
  await page.waitForTimeout(1500);
  await page.getByRole('button', { name: 'Сохранить', exact: true }).first().click({ timeout: 15000 }).catch(e => console.log('С9 сохранить:', String(e).slice(0, 100)));
  await page.waitForTimeout(2500);
  const tpls = await client.get<any[]>(`/api/workflow-templates?projectId=${PID}`);
  const proj = tpls.filter(e => e.scope === 'project');
  console.log('С9 проектных шаблонов:', JSON.stringify(proj.map(e => ({ id: e.template.id, name: e.template.name, basedOn: e.basedOn?.name, diff: e.diffFromPreset }))));
  expect.soft(proj.length, 'проектный шаблон появился').toBeGreaterThan(0);
  await page.screenshot({ path: `${SHOTS}/С9-project-copy.png`, fullPage: true });
  await page.close();
});
