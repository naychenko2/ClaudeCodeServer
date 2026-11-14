import { test, expect, type APIRequestContext, type BrowserContext, type Locator, type Page } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { login, api, type Api } from './helpers';

// Сквозной прогон «Процессы фичи» (задача a06c5c09), часть A:
// С1 шаблоны (список, редактор, свой пресет) → С2 запуск по шаблону (рекомендация,
// отсутствие запуска без подтверждения, подтверждение) → С3 граф.
// Поля редактора не имеют aria-связи (Field рендерит подпись отдельным блоком) —
// поэтому поиск по подписи и переход к контролу через following-sibling.

const PID = 'ce3e224a-d0f8-4828-9519-f51d4c3dbf6a';
const FEAT = 'qa-e2e-wf-feature';
const TPL = 'qa-e2e-wf-tpl';
const SHOTS = '.cc-attachments/qa-wf-e2e';

let token: string;
let client: Api;
let request: APIRequestContext;
const errors: string[] = [];

test.beforeAll(async ({ playwright, baseURL }) => {
  mkdirSync(SHOTS, { recursive: true });
  request = await playwright.request.newContext({ baseURL });
  token = await login(request);
  client = api(request, token);
});
test.afterAll(async () => { console.log('CONSOLE ERRORS A:', JSON.stringify([...new Set(errors)].slice(0, 12))); await request?.dispose(); });

async function open(context: BrowserContext, path: string, w = 1280): Promise<Page> {
  const page = await context.newPage();
  page.setViewportSize({ width: w, height: w < 500 ? 800 : 900 });
  page.on('console', m => { if (m.type() === 'error') errors.push(`[error] ${path} ${m.text()}`.slice(0, 300)); });
  page.on('pageerror', e => errors.push(`[pageerror] ${path} ${e}`.slice(0, 300)));
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
  await page.goto(path, { waitUntil: 'domcontentloaded' });
  await page.waitForTimeout(2000);
  return page;
}
const flat = (s: string) => s.replace(/\n/g, ' | ');
// Контрол поля по его подписи (Field: подпись и контрол — соседи в одном блоке)
const fieldControl = (root: Page | Locator, label: string) => root.locator([
  `label:has-text("${label}") + div input`, `label:has-text("${label}") + div textarea`,
  `label:has-text("${label}") + div select`, `label:has-text("${label}") + input`,
  `label:has-text("${label}") + textarea`, `label:has-text("${label}") + select`,
].join(', ')).first();

async function openTemplates(page: Page) {
  await page.goto('/#/specialties', { waitUntil: 'domcontentloaded' });
  await page.waitForTimeout(2000);
  await page.getByText('Процессы фичи', { exact: true }).click();
  await page.waitForTimeout(1500);
}

test('С1а: список шаблонов и настройка этапа встроенного', async ({ context }) => {
  const page = await open(context, '/#/specialties');
  await openTemplates(page);

  for (const name of ['Полный цикл', 'Быстрый трек', 'Параллельный цикл']) {
    expect.soft(await page.getByRole('button', { name: new RegExp(name) }).count(), `в списке есть «${name}»`).toBeGreaterThan(0);
  }
  console.log('С1а бейджей «встроенный»:', await page.getByText('встроенный', { exact: true }).count());
  await page.screenshot({ path: `${SHOTS}/s1a-list-1280.png`, fullPage: true });

  await page.getByRole('button', { name: /Быстрый трек/ }).first().click();
  await page.waitForTimeout(1200);
  const rows = page.locator('[data-stage-index]');
  const n = await rows.count();
  console.log('С1а строк этапов «Быстрого трека»:', n);
  expect.soft(n).toBeGreaterThanOrEqual(4);
  await page.locator('[data-stage-index]').filter({ hasText: 'step' }).first().click();
  await page.waitForTimeout(800);
  const settings = page.locator('[data-stage-settings]');
  expect.soft(await settings.count(), 'открылась настройка этапа').toBeGreaterThan(0);
  const st = (await settings.innerText()).toUpperCase();
  console.log('С1а поля настройки:', flat(st).slice(0, 900));
  for (const label of ['ИСПОЛНИТЕЛЬ', 'ПРОМПТ ЭТАПА', 'МОДЕЛИ', 'ЕСТЬ В МАРШРУТЕ', 'ЗАПРЕЩЕНО НА ЭТАПЕ']) {
    expect.soft(st.includes(label), `в настройке этапа есть «${label}»`).toBeTruthy();
  }
  // Таймаут и политика при зависании
  console.log('С1а политика зависания: «если исполнитель молчит»:', /ЕСЛИ ИСПОЛНИТЕЛЬ МОЛЧИТ/i.test(st), '«порог молчания»:', /ПОРОГ МОЛЧАНИЯ/i.test(st), '«что делать»:', /ЧТО ДЕЛАТЬ/i.test(st));
  await page.screenshot({ path: `${SHOTS}/s1a-builtin-settings.png`, fullPage: true });
  await page.close();
});

test('С1б: свой шаблон через редактор', async ({ context }) => {
  test.setTimeout(240_000);
  const page = await open(context, '/#/specialties');
  await openTemplates(page);

  await page.getByTestId('new-template').click();
  await page.waitForTimeout(1000);
  expect.soft(await page.getByText('не сохранён', { exact: true }).count(), 'черновик помечен «не сохранён»').toBeGreaterThan(0);

  await fieldControl(page, 'Название').fill(TPL, { timeout: 15000 }).catch(e => console.log('С1б название:', String(e).slice(0, 120)));
  const rows = page.locator('[data-stage-index]');
  console.log('С1б этапов в свежем черновике:', await rows.count());

  await page.locator('[data-stage-index]').filter({ hasText: 'design' }).first().click();
  await page.waitForTimeout(1000);
  const settings = page.locator('[data-stage-settings]');
  expect.soft(await settings.count(), 'открылась настройка этапа design').toBeGreaterThan(0);

  await fieldControl(settings, 'Промпт этапа').fill('Оформи вариант решения и опиши, что сознательно не трогаем.', { timeout: 15000 }).catch(e => console.log('С1б промпт:', String(e).slice(0, 120)));

  const specSel = settings.locator('select[title="Специальность этапа"]').first();
  if (await specSel.count()) {
    const opts = await specSel.locator('option').allInnerTexts();
    const pick = opts.find(o => /Аналитик/i.test(o));
    if (pick) { await specSel.selectOption({ label: pick }); console.log('С1б исполнитель-специальность:', pick); }
  } else console.log('С1б: селект специальности не найден');

  // «Есть в маршруте» — сегмент-контрол, не select
  await page.getByRole('button', { name: 'По решению', exact: true }).first().click({ timeout: 8000 })
    .then(() => console.log('С1б presence → по решению')).catch(e => console.log('С1б presence:', String(e).slice(0, 100)));

  // Слот модели strong — RoutePicker в [data-tier-cell]
  const cell = settings.locator('[data-tier-cell="strong"]');
  if (await cell.count()) {
    await cell.locator('button').first().click({ timeout: 8000 }).catch(e => console.log('С1б открыть пикер модели:', String(e).slice(0, 100)));
    await page.waitForTimeout(800);
    const opts = await page.getByRole('button').allInnerTexts();
    const cards = opts.filter(o => /Flash-Next|Локальная модель|qwen/i.test(o));
    console.log('С1б карточек модели в пикере:', JSON.stringify(cards.slice(0, 6)));
    if (cards.length) await page.getByRole('button').filter({ hasText: /Flash-Next|Локальная модель/ }).first().click().catch(() => undefined);
    await page.waitForTimeout(500);
    console.log('С1б подпись слота после выбора:', await cell.innerText({ timeout: 3000 }).catch(() => '').then(s => flat(s).slice(0, 120)));
  } else console.log('С1б: ячеек модели нет');

  // Перекрывает ли шторка настройки кнопку «Сохранить»
  const saveBtn = page.getByRole('button', { name: 'Сохранить', exact: true });
  console.log('С1б Сохранить есть:', await saveBtn.count(), 'disabled:', await saveBtn.first().isDisabled().catch(() => 'нет'));
  await saveBtn.first().scrollIntoViewIfNeeded().catch(() => undefined);
  await page.waitForTimeout(300);
  const covered = await saveBtn.first().evaluate((el: Element) => {
    const r = el.getBoundingClientRect();
    const top = document.elementFromPoint(r.x + r.width / 2, r.y + Math.min(r.height / 2, 10));
    return { inViewport: r.top >= 0 && r.bottom <= innerHeight, coveredBy: top ? (top.closest('[data-stage-settings]') ? 'шторка настройки этапа' : top.tagName) : 'нет точки' };
  }).catch(e => String(e).slice(0, 120));
  console.log('С1б «Сохранить» при открытой шторке:', JSON.stringify(covered));
  await page.screenshot({ path: `${SHOTS}/s1b-edit-stage.png`, fullPage: false });

  // Закрыть шторку и сохранить
  await page.getByLabel('Закрыть настройку этапа').first().click({ timeout: 8000 }).catch(() => page.keyboard.press('Escape'));
  await page.waitForTimeout(800);
  await saveBtn.first().click({ timeout: 15000 }).catch(e => console.log('С1б клик Сохранить не прошёл:', String(e).slice(0, 120)));
  await page.waitForTimeout(3000);
  console.log('С1б после сохранения:', flat(await page.locator('body').innerText()).slice(0, 400));

  const tpls = await client.get<any[]>('/api/workflow-templates');
  const mine = tpls.find(e => e.template.name === TPL);
  expect.soft(!!mine, 'свой шаблон появился в сторе').toBeTruthy();
  if (mine) {
    const d = mine.template.stages.find((s: any) => s.role === 'design');
    console.log('С1б сохранённый design-этап:', JSON.stringify({
      id: d?.id, presence: d?.presence, prompt: d?.prompt, specialty: d?.specialty, models: d?.models,
    }));
    expect.soft(d?.presence, 'presence стал optional').toBe('optional');
    expect.soft(d?.prompt ?? '', 'свой промпт этапа сохранён').toContain('Оформи вариант решения');
  }
  await page.close();
});

test('С2: рекомендация, запуск только после подтверждения, подтверждение через UI', async ({ context }) => {
  test.setTimeout(300_000);
  const tasks = await client.get<any[]>(`/api/projects/${PID}/tasks`);
  const epic = tasks.find(t => t.title === FEAT && t.role === 'feature');
  expect(epic, 'эпик засечён').toBeTruthy();
  const epicId = epic!.id;

  const page = await open(context, `/#/project/${PID}/feature/${epicId}`);
  await expect(page.getByTestId('feature-page')).toBeVisible({ timeout: 30000 });

  // Строка «ждёт вас»: предложение модели, без подтверждения исполнители не стартуют
  const row = page.getByTestId('feature-page-workflow-confirm');
  expect.soft(await row.count(), 'строка предложения маршрута видена').toBeGreaterThan(0);
  const rowText = await row.innerText({ timeout: 3000 }).catch(() => '');
  console.log('С2 строка предложения:', flat(rowText).slice(0, 400));
  expect.soft(rowText, 'строка называет предложенный шаблон').toContain('Модель предлагает');
  expect.soft(rowText, 'строка говорит, что без подтверждения исполнители не стартуют').toContain('не подтвердите');
  await page.screenshot({ path: `${SHOTS}/s2a-proposal-row.png`, fullPage: true });

  // До подтверждения — ни одного шага фичи
  const before = (await client.get<any[]>(`/api/projects/${PID}/tasks`)).filter(t => t.featureId === epicId);
  console.log('С2 задач фичи до подтверждения:', before.length);
  expect.soft(before.length, 'без подтверждения шагов нет').toBe(0);

  // Раскрыть блок и посмотреть предложение
  await page.getByRole('button', { name: 'Посмотреть и подтвердить' }).first().click({ timeout: 15000 })
    .catch(e => console.log('С2 клик «Посмотреть и подтвердить»:', String(e).slice(0, 120)));
  await page.waitForTimeout(1500);
  const block = page.getByTestId('feature-workflow-confirm');
  expect.soft(await block.count(), 'открылся блок подтверждения маршрута').toBeGreaterThan(0);
  console.log('С2 блок:', flat(await block.innerText({ timeout: 3000 }).catch(() => '')).slice(0, 1200));
  const rationale = await page.getByTestId('feature-workflow-confirm-rationale').innerText({ timeout: 3000 }).catch(() => '');
  console.log('С2 обоснование модели:', flat(rationale).slice(0, 400));
  expect.soft(!!rationale, 'блок показывает обоснование предложения').toBeTruthy();
  const boxes = block.getByRole('checkbox');
  const nb = await boxes.count();
  const checked = await Promise.all(Array.from({ length: nb }, (_, i) => boxes.nth(i).isChecked().catch(() => null)));
  console.log('С2 чекбоксов пропуска:', nb, 'отмечено:', JSON.stringify(checked));
  await page.screenshot({ path: `${SHOTS}/s2b-confirm-block.png`, fullPage: true });

  // Подтвердить как предложено (пропуски модели остаются её решением)
  await page.getByTestId('feature-workflow-confirm-submit').first().click({ timeout: 15000 })
    .catch(e => console.log('С2 клик «Подтвердить»:', String(e).slice(0, 120)));
  await page.waitForTimeout(3000);
  const conf = await client.get<any>(`/api/tasks/${epicId}/workflow`);
  console.log('С2 подтверждено:', JSON.stringify({
    templateId: conf.confirmed?.templateId, scope: conf.confirmed?.scope, skipped: conf.confirmed?.skipped,
    stages: conf.confirmed?.stages?.map((s: any) => s.id ?? s.role), confirmedByUser: conf.confirmed?.confirmedByUser,
    autoConfirmed: conf.confirmed?.autoConfirmed,
  }));
  expect.soft(!!conf.confirmed, 'маршрут подтверждён').toBeTruthy();
  expect.soft(conf.confirmed?.confirmedByUser, 'подтвердил человек').toBeTruthy();

  // С3: граф — только фактический маршрут
  await page.reload({ waitUntil: 'domcontentloaded' });
  await page.waitForTimeout(3500);
  const labels = await page.locator('[data-testid^="pgraph-node-"]').evaluateAll(els => els.map(e => `${e.getAttribute('data-testid')}=${e.getAttribute('aria-label')}`));
  console.log('С3 узлы графа:', JSON.stringify(labels));
  expect.soft(labels.length, 'граф построен').toBeGreaterThan(0);
  const skipped = conf.confirmed?.skipped?.[0];
  if (skipped) expect.soft(labels.some(l => l.includes(skipped)), `пропущенный «${skipped}» не показан`).toBeFalsy();
  await page.screenshot({ path: `${SHOTS}/s3-graph-1280.png`, fullPage: true });
  await page.close();
});
