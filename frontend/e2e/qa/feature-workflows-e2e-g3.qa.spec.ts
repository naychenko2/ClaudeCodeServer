import { test, expect } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { login, api, fullCycleTemplate, editFullCycleTemplate, alignEpicRoute, type Api } from './helpers';

// С8v3: правка шаблона МЕНЯЕТ СОСТАВ маршрута (сняли этап «Проверка») —
// карточка «шаблон изменился», решение человека, готовые шаги не переписываются.
//
// Ф-2.20 (дефект bc99a48f): фикстура общая и мутируется предыдущими прогонами (маршрут
// мог остаться без qa, шаблон — с выключенным qa), поэтому начальное состояние приводится
// сам: «Проверка» в шаблоне «on» + маршрут подтверждён с ним (qa в составе). Иначе правка
// «Нет» — не перемена состава, и карточки нет по делу. Переход на страницу фичи — внешний
// hashchange на живой странице (как вставка адреса): до правки App терял featureId и сам
// экран не открывался — «ни карточки, ни Сменить» были именно поэтому.

const PID = 'ce3e224a-d0f8-4828-9519-f51d4c3dbf6a';
const EPIC = 'f571aace-a780-4c3e-abca-05741154c8ad';
const SHOTS = '.cc-attachments/qa-wf-e2e';
const flat = (s: string) => s.replace(/\n/g, ' | ');

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

test('С8v3: изменение состава маршрута', async ({ context }) => {
  test.setTimeout(280_000);
  const page = await context.newPage();
  page.setViewportSize({ width: 1280, height: 900 });
  const errs: string[] = [];
  page.on('console', m => { if (m.type() === 'error') errs.push(m.text().slice(0, 200)); });
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);

  const stepsBefore = (await client.get<any[]>(`/api/projects/${PID}/tasks`)).filter(t => t.featureId === EPIC);
  const viewBefore = stepsBefore.map(t => ({ role: t.role, v: t.stage?.templateVersion }));
  console.log('С8v3 шаги до правки:', JSON.stringify(viewBefore));

  // Начальное состояние — своё, не унаследованное от предыдущего прогона:
  // 1) «Проверка» (qa) в шаблоне «on»; 2) маршрут подтверждён текущим шаблоном без снятия.
  const tplStart = await fullCycleTemplate(client);
  const qaStart = tplStart.stages.find((s: any) => s.role === 'qa')?.presence;
  if (qaStart !== 'on') {
    await editFullCycleTemplate(client, t => { t.stages.find((s: any) => s.role === 'qa').presence = 'on'; });
  }
  await client.post(`/api/tasks/${EPIC}/workflow/confirm`, { templateId: 'builtin-full-cycle', scope: 'global', skipped: [] });
  const wfStart = await client.get<any>(`/api/tasks/${EPIC}/workflow`);
  console.log('С8v3 начальное: confirmed v', wfStart.confirmed?.confirmedVersion, 'updateAvailable:', wfStart.updateAvailable);
  expect.soft(wfStart.updateAvailable ?? true, 'начальный маршрут совпадает с составом шаблона').toBeFalsy();

  try {
    const vBefore = (await fullCycleTemplate(client)).version;

    // Правка: этап «Проверка» → «Нет» в маршруте
    await page.goto('/#/specialties', { waitUntil: 'domcontentloaded' });
    await page.waitForTimeout(2500);
    await page.getByText('Процессы фичи', { exact: true }).click();
    await page.waitForTimeout(1500);
    await page.getByRole('button', { name: /Полный цикл/ }).first().click();
    await page.waitForTimeout(1200);
    await page.locator('[data-stage-index]').filter({ hasText: 'qa' }).first().click();
    await page.waitForTimeout(1000);
    await page.getByRole('button', { name: 'Нет', exact: true }).first().click({ timeout: 8000 }).catch(e => console.log('С8v3 presence:', String(e).slice(0, 100)));
    await page.waitForTimeout(800);
    await page.getByLabel('Закрыть настройку этапа').first().click({ timeout: 8000 }).catch(() => page.keyboard.press('Escape'));
    await page.waitForTimeout(500);
    await page.getByRole('button', { name: 'Сохранить', exact: true }).first().click({ timeout: 15000 }).catch(e => console.log('С8v3 сохранить:', String(e).slice(0, 100)));
    await page.waitForTimeout(2500);
    const tpls = await client.get<any[]>('/api/workflow-templates');
    const full = tpls.find(e => e.template.builtInKey === 'full-cycle');
    const vAfter = full?.template.version;
    console.log('С8v3 версия шаблона:', vAfter, 'presence qa:', full?.template.stages.find((s: any) => s.role === 'qa')?.presence);
    expect.soft(vAfter ?? 0, 'выключение этапа подняло версию шаблона').toBeGreaterThan(vBefore);
    expect.soft(full?.template.stages.find((s: any) => s.role === 'qa')?.presence, '«Проверка» стала «Нет»').toBe('off');

    // Бэк видит перемену состава: данные для карточки есть
    const wfMid = await client.get<any>(`/api/tasks/${EPIC}/workflow`);
    console.log('С8v3 после выключения: updateAvailable:', wfMid.updateAvailable, 'templateUpdate:', JSON.stringify(wfMid.templateUpdate ?? null));
    expect.soft(wfMid.updateAvailable, 'снятый этап — перемена состава: updateAvailable=true').toBeTruthy();

    // Страница фичи: карточка «шаблон изменился» (или «Сменить» для раскрытия блока)
    await page.goto(`/#/project/${PID}/feature/${EPIC}`, { waitUntil: 'domcontentloaded' });
    await page.waitForTimeout(5000);
    await page.getByTestId('feature-workflow-confirm-change').first().click({ timeout: 15000 }).catch(e => console.log('С8v3 «Сменить»:', String(e).slice(0, 100)));
    await page.waitForTimeout(2500);
    const upd = page.getByTestId('feature-workflow-confirm-update');
    const updText = await upd.innerText({ timeout: 3000 }).catch(() => '');
    console.log('С8v3 карточка:', await upd.count(), flat(updText).slice(0, 400));
    console.log('С8v3 кнопки apply/keep:', await page.getByTestId('feature-workflow-confirm-update-apply').count(), await page.getByTestId('feature-workflow-confirm-update-keep').count());
    await page.screenshot({ path: `${SHOTS}/С8v3-update-card.png`, fullPage: true });
    expect.soft(await upd.count(), 'карточка «шаблон изменился» показана').toBeGreaterThan(0);
    expect.soft(updText, 'карточка называет версии').toContain(`v${wfStart.confirmed?.confirmedVersion}`);

    await page.getByTestId('feature-workflow-confirm-update-apply').first().click({ timeout: 15000 }).catch(e => console.log('С8v3 apply:', String(e).slice(0, 100)));
    await page.waitForTimeout(4000);
    const wf = await client.get<any>(`/api/tasks/${EPIC}/workflow`);
    // stages — из payload страницы (run.epic.workflow.stages): ConfirmedDto ручки /workflow
    // этапов не несёт вовсе, проверять «не вошёл» на нём было бы пусто-истинно
    const pageData = await client.get<any>(`/api/tasks/features/${EPIC}`);
    const stages = ((pageData?.run?.epic?.workflow?.stages) ?? []).map((s: any) => s.id ?? s.role);
    console.log('С8v3 после «Обновить»: v', wf.confirmed?.confirmedVersion, 'stages:', JSON.stringify(stages));
    expect.soft(wf.confirmed?.confirmedVersion, 'маршрут обновился до новой версии шаблона').toBe(vAfter);
    expect.soft(stages.includes('qa'), 'снятый этап не вошёл в обновлённый маршрут').toBeFalsy();

    const stepsAfter = (await client.get<any[]>(`/api/projects/${PID}/tasks`)).filter(t => t.featureId === EPIC);
    const view = stepsAfter.map(t => ({ role: t.role, v: t.stage?.templateVersion }));
    console.log('С8v3 шаги после обновления:', JSON.stringify(view));
    expect.soft(view.every(s => s.v === 1), 'созданные ранее шаги остались на v1').toBeTruthy();

    await page.screenshot({ path: `${SHOTS}/С8v3-after.png`, fullPage: true });
  } finally {
    // Возвращаем фикстуру как было: она общая для остальных спеков (тот же приём, что у С8).
    const cur = await fullCycleTemplate(client).catch(() => null);
    if (cur && (cur.stages.find((s: any) => s.role === 'qa')?.presence ?? '') !== (qaStart ?? 'off')) {
      await editFullCycleTemplate(client, t => { t.stages.find((s: any) => s.role === 'qa').presence = qaStart ?? 'off'; })
        .catch(e => console.log('С8v3 откат шаблона:', String(e).slice(0, 120)));
    }
    await alignEpicRoute(client, EPIC).catch(e => console.log('С8v3 откат маршрута:', String(e).slice(0, 120)));
  }

  console.log('С8v3 errors:', JSON.stringify([...new Set(errs)]));
  await page.close();
});
