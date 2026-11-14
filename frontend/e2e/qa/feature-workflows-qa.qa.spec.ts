import { test, expect, type BrowserContext, type Page } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { login } from './helpers';

// Сквозная QA-проверка фичи «Процессы фичи» на живом стенде (задача 63edb792).
// Сид подготовлен вручную (не спеком): проект с подтверждённым параллельным маршрутом,
// легаси-эпик, эпик с предложением модели, эпик с длинным именем этапа, персона-тестер
// со специальностью и выключенной специальностью секцией промпта.
const SHOTS = '.cc-attachments/qa-wf';

const FEATURE = { pid: '92470dad-8265-4925-8f0f-381376d3019d', fid: '39cdc87c-8739-46d0-813d-e5fe387e4f7a' };
const LEGACY = { pid: '9959a26e-a236-4850-838b-e82f25a1a85b', fid: 'fae420d7-bb85-428e-817a-175ab17b39a6' };
const PROPOSAL = { pid: '2e8a5e7d-3a45-4b13-a782-edee0a2a6eee', fid: '6f88115f-29eb-4036-82e4-c1c86c4d12d8' };
const LONG = { pid: '6409776b-000b-4204-9768-b5a3eae396a6', fid: '6d5e5412-6624-4f3b-ad3d-26f85a33954b' };
const PERSONA = '68665fdc-10eb-4f2d-a052-f79665f9e0ca';
const STEP_BACK = 'e949e184-d216-4ad8-a355-4382fd378fe5';

test.describe.configure({ mode: 'serial' });

let token: string;
test.beforeAll(async ({ playwright, baseURL }) => {
  const req = await playwright.request.newContext({ baseURL });
  token = await login(req);
  await req.dispose();
  mkdirSync(SHOTS, { recursive: true });
});

async function openPage(context: BrowserContext, url: string): Promise<Page> {
  const page = await context.newPage();
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
  await page.goto(url, { waitUntil: 'domcontentloaded' });
  return page;
}

// ── 1. Редактор шаблонов процесса в настройках проекта ────────────────────────────────
test('редактор шаблонов: список, копия глобального пресета в проект, правка и сохранение', async ({ context }) => {
  const page = await openPage(context, `/#/project/${FEATURE.pid}`);
  await page.getByTestId('feature-page').count(); // прогрев
  await page.waitForTimeout(2500);

  await page.getByRole('button', { name: 'Настройки проекта' }).first().click();
  await expect(page.getByText('Редактировать проект')).toBeVisible({ timeout: 15000 });
  await page.getByText('Шаблоны процесса', { exact: true }).click();
  await page.waitForTimeout(2000);
  await page.screenshot({ path: `${SHOTS}/tpl-editor-list.png`, fullPage: true });

  // Модалка ui-кита без role=dialog — держим область по заголовку секции
  const dlg = page.locator('div').filter({ has: page.getByText('Шаблоны процесса', { exact: true }) }).last();
  const dlgText = await dlg.innerText();
  expect(dlgText, 'в проектных шаблонах виден свой шаблон фичи A').toContain('qa-wf-parallel');
  expect(dlgText, 'глобальные показаны с пометкой «встроенный»').toContain('встроенный');

  // Открываем глобальный «Параллельный цикл»: он только для чтения
  await page.getByText('Параллельный цикл', { exact: true }).first().click();
  await page.waitForTimeout(1500);
  await page.screenshot({ path: `${SHOTS}/tpl-editor-global.png`, fullPage: true });
  const globalText = await page.locator('body').innerText();
  expect(globalText, 'глобальный шаблон открыт в режиме чтения').toContain('Глобальный шаблон правит администратор');
  console.log('GLOBAL EDITOR TEXT:\n' + globalText.slice(globalText.indexOf('Шаблоны процесса'), globalText.indexOf('Шаблоны процесса') + 2500));
  // Имена параллельных этапов (Title) в редакторе — факт, а не ожидание: проверяем отдельным тестом.

  // «Создать проектный» — копия с basedOn
  await page.getByRole('button', { name: 'Создать проектный' }).click();
  await page.waitForTimeout(1200);
  const nameLabel = page.locator('label', { hasText: 'Название' }).first();
  const nameInput = nameLabel.locator('xpath=..').locator('input').first();
  await expect(nameInput).toBeVisible({ timeout: 10000 });
  console.log('name input value:', await nameInput.inputValue());
  await nameInput.fill('qa-wf-copy-parallel');
  await page.getByRole('button', { name: /^Сохранить$/ }).first().click();
  await page.waitForTimeout(2000);
  await page.screenshot({ path: `${SHOTS}/tpl-editor-saved.png`, fullPage: true });

  const after = await page.locator('body').innerText();
  expect(after, 'копия пресета сохранена в проектные').toContain('qa-wf-copy-parallel');
  console.log('EDITOR TEXT AFTER SAVE:\n' + after.slice(after.indexOf('Шаблоны процесса'), after.indexOf('Шаблоны процесса') + 2000));
  await page.close();
});

// ── 2. Подтверждение маршрута: предложение, смена шаблона, снятие, ложная строка ──────
test('подтверждение маршрута: предложение с обоснованием, смена шаблона, снятие этапа, подтверждение', async ({ context }) => {
  const page = await openPage(context, `/#/project/${PROPOSAL.pid}/feature/${PROPOSAL.fid}`);
  await expect(page.getByTestId('feature-page')).toBeVisible({ timeout: 30000 });

  const block = page.getByTestId('feature-workflow-confirm');
  await expect(block, 'блок подтверждения виден у эпика с предложением').toBeVisible({ timeout: 20000 });
  const text = await block.innerText();
  console.log('CONFIRM BLOCK:\n' + text);
  expect(text).toContain('Предложение модели');
  expect(text).toContain('qa-model-stub');
  expect(text).toContain('Задача меняет только тексты интерфейса');

  // Смена шаблона не должна давать строку «Шаблон изменился»
  const select = block.locator('select[title="Шаблон маршрута"]');
  const options = await select.locator('option').allInnerTexts();
  console.log('TEMPLATE OPTIONS:', JSON.stringify(options));
  const other = options.filter(o => !o.startsWith('Полный цикл'))[0];
  expect(other, 'в каталоге есть второй шаблон').toBeTruthy();
  await select.selectOption({ label: other });
  await page.waitForTimeout(800);
  const afterSwitch = await block.innerText();
  expect(afterSwitch, 'ложной строки «Шаблон изменился» при смене шаблона нет').not.toContain('изменился');

  // Возвращаем предложенный шаблон и снимаем необязательный этап (QA по выбору)
  await select.selectOption({ label: options.find(o => o.startsWith('Полный цикл'))! });
  await page.waitForTimeout(800);
  const boxes = block.getByRole('checkbox');
  console.log('CHECKBOXES:', await boxes.count());
  // design снят предложением (skipped), qa включён — снимаем qa
  const qaBox = boxes.nth((await boxes.count()) - 1);
  console.log('qa checked before:', await qaBox.isChecked());
  if (await qaBox.isChecked()) await qaBox.click();
  await page.waitForTimeout(500);
  await page.screenshot({ path: `${SHOTS}/confirm-before-submit.png`, fullPage: true });

  await page.getByTestId('feature-workflow-confirm-submit').click();
  await page.waitForTimeout(3000);
  await page.screenshot({ path: `${SHOTS}/confirm-after-submit.png`, fullPage: true });
  // После подтверждения блок в тихом режиме исчезает, появляется граф
  await expect(page.getByTestId('feature-process-graph'), 'после подтверждения появился граф').toBeVisible({ timeout: 15000 });
  await expect(page.getByTestId('feature-workflow-confirm'), 'подтверждённый маршрут не держит блок в шапке').toHaveCount(0);
  const pageText = await page.getByTestId('feature-page').innerText();
  expect(pageText, 'после подтверждения нет строки «Шаблон изменился»').not.toContain('Шаблон изменился');
  await page.close();
});

// ── 3. Граф процесса и экран этапа ────────────────────────────────────────────────────
test('граф: колонки, параллельные ветки, различие двух проверок, выбор этапа', async ({ context }) => {
  const page = await openPage(context, `/#/project/${FEATURE.pid}/feature/${FEATURE.fid}`);
  await expect(page.getByTestId('feature-page')).toBeVisible({ timeout: 30000 });
  const graph = page.getByTestId('feature-process-graph');
  await expect(graph).toBeVisible({ timeout: 20000 });
  await page.waitForTimeout(1500);
  await page.screenshot({ path: `${SHOTS}/graph-desktop.png`, fullPage: true });

  const nodes = page.locator('[data-testid^="pgraph-node-"]');
  expect(await nodes.count(), 'в графе все 8 этапов маршрута').toBe(8);
  const labels = await nodes.evaluateAll(els => els.map(e => e.getAttribute('aria-label')));
  console.log('NODE LABELS:', JSON.stringify(labels));
  expect(labels.join(' | '), 'два этапа проверки различимы по имени').toContain('Проверка плана');
  expect(labels.join(' | ')).toContain('Проверка:');

  // Параллельные ветки одной роли стоят в одной колонке: одинаковый left, разные top
  const boxBack = await page.getByTestId('pgraph-node-step-back').boundingBox();
  const boxFront = await page.getByTestId('pgraph-node-step-front').boundingBox();
  expect(boxBack && boxFront).toBeTruthy();
  expect(Math.round(boxBack!.x), 'step-back и step-front в одной колонке').toBe(Math.round(boxFront!.x));
  expect(Math.abs(boxBack!.y - boxFront!.y), 'ветки друг под другом').toBeGreaterThan(10);

  // Клик выбирает этап и подсвечивает его
  await page.getByTestId('pgraph-node-qa-plan').click();
  await page.waitForTimeout(1200);
  expect(await page.getByTestId('pgraph-node-qa-plan').getAttribute('aria-pressed'), 'выбранный узел подсвечен').toBe('true');
  expect(await page.getByTestId('pgraph-node-cut').getAttribute('aria-pressed')).toBe('false');
  await expect(page.getByTestId('feature-stage-area-title')).toHaveText('Проверка плана');
  await page.screenshot({ path: `${SHOTS}/graph-selected.png`, fullPage: true });
  await page.close();
});

test('экран этапа: выбор задачи этапа, блоки, ошибка диффа не гасит соседние', async ({ context }) => {
  const page = await openPage(context, `/#/project/${FEATURE.pid}/feature/${FEATURE.fid}`);
  await expect(page.getByTestId('feature-page')).toBeVisible({ timeout: 30000 });
  await expect(page.getByTestId('feature-process-graph')).toBeVisible({ timeout: 20000 });
  await page.getByTestId('pgraph-node-step-back').click();
  await page.waitForTimeout(1200);

  // Этап «Производство · бэк»: две задачи — есть выбор
  await expect(page.getByTestId('stage-screen-picker')).toBeVisible();
  const picks = page.locator('[data-testid^="stage-screen-pick-"]');
  expect(await picks.count(), 'две задачи этапа — две кнопки выбора').toBe(2);
  const body = page.getByTestId('stage-screen-body');
  await expect(body).toBeVisible({ timeout: 15000 });
  const firstTask = await body.getAttribute('data-task-id');
  await picks.nth(1).click();
  await page.waitForTimeout(1200);
  expect(await body.getAttribute('data-task-id'), 'выбор второй задачи меняет экран').not.toBe(firstTask);

  // Блоки экрана на месте
  await expect(page.getByTestId('stage-artifacts')).toBeVisible();
  await expect(page.getByTestId('stage-input')).toBeVisible();
  await expect(page.getByTestId('stage-review')).toBeVisible();
  await expect(page.getByTestId('stage-metrics')).toBeVisible();
  // Слепок этапа отдельно от постановки: слепок есть, транскрипта нет
  await expect(page.getByTestId('stage-snapshot')).toBeVisible();
  await expect(page.getByTestId('stage-transcript-empty')).toBeVisible();
  await page.screenshot({ path: `${SHOTS}/stage-screen.png`, fullPage: true });

  // Ошибка диффа не гасит соседние блоки: фикстура с коммитом, дифф отвечает 500
  const fixture = {
    taskId: STEP_BACK, featureId: FEATURE.fid, role: 'step', stepRef: 'B1',
    title: 'qa-wf-step-b1', status: 'inProgress',
    stage: {
      templateId: 'tpl-fixture', templateVersion: 1, role: 'step', stageId: 'step-back', lane: 'back',
      after: ['plan'], specialty: null, personaId: null, prompt: 'ФИКСТУРА-СЛЕПОК: проверь контракт API',
      tierStrong: null, tierMedium: null, tierWeak: null, defaultTier: 'medium',
      disallowedTools: ['web'], mcpAllow: null, stallMinutes: 30, stallPolicy: 'alert',
    },
    artifacts: {
      commits: [{ sha: 'deadbeef', shortSha: 'deadbee', subject: 'qa: фикстурный коммит', at: null, files: ['a.ts'], taskId: STEP_BACK, role: 'step', version: 1 }],
      commitsAvailable: true, linkedFiles: [], resultMarkdown: null,
    },
    input: {
      stage: {
        templateId: 'tpl-fixture', templateVersion: 1, role: 'step', stageId: 'step-back', lane: 'back',
        after: ['plan'], specialty: null, personaId: null, prompt: 'ФИКСТУРА-СЛЕПОК: проверь контракт API',
        tierStrong: null, tierMedium: null, tierWeak: null, defaultTier: 'medium',
        disallowedTools: ['web'], mcpAllow: null, stallMinutes: 30, stallPolicy: 'alert',
      },
      sessionId: 'sess-fixture',
      transcriptAvailable: true,
      transcript: {
        prompt: 'ФИКСТУРА-ПОСТАНОВКА: сделай шаг B1',
        filesRead: ['src/a.ts'],
        toolCalls: [{ name: 'Read', summary: 'src/a.ts', at: null, isError: false }],
        toolCallsTotal: 1,
        truncated: false,
      },
    },
    review: { own: null, reviews: [], awaitingHuman: null },
    metrics: null,
  };
  // Переписываем и input, и stage — смоук: слепок и постановка приходят из разных мест
  await page.route(`**/api/tasks/${STEP_BACK}/stage`, route => {
    console.log('FIXTURE STAGE →', route.request().url());
    return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(fixture) });
  });
  await page.route(`**/api/tasks/${STEP_BACK}/stage/commits/deadbeef/diff`, route => {
    console.log('FIXTURE DIFF 500 →', route.request().url());
    return route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ error: 'git недоступен' }) });
  });
  // Перезаход на страницу: первый же запрос экрана этапа пойдёт через фикстуру
  await page.reload({ waitUntil: 'domcontentloaded' });
  await expect(page.getByTestId('feature-process-graph')).toBeVisible({ timeout: 20000 });
  await page.getByTestId('pgraph-node-step-back').click();
  await page.waitForTimeout(2000);
  await expect(page.getByTestId('stage-commit-deadbeef'), 'фикстура коммита доехала до экрана').toBeVisible({ timeout: 15000 });
  await page.getByTestId('stage-commit-toggle-deadbeef').click();
  await page.waitForTimeout(1500);
  await expect(page.getByTestId('stage-commit-error-deadbeef'), 'ошибка диффа показана под коммитом').toBeVisible({ timeout: 10000 });
  await expect(page.getByTestId('stage-input'), 'соседние блоки живы после ошибки диффа').toBeVisible();
  await expect(page.getByTestId('stage-review')).toBeVisible();
  // Слепок этапа и постановка исполнителю — раздельные блоки из разных источников ответа
  await page.getByTestId('stage-snapshot-prompt-toggle').click();
  await page.getByTestId('stage-prompt-toggle').click();
  await page.waitForTimeout(600);
  await expect(page.getByTestId('stage-snapshot-prompt'), 'промпт этапа из слепка').toContainText('ФИКСТУРА-СЛЕПОК');
  await expect(page.getByTestId('stage-prompt'), 'постановка исполнителю из транскрипта').toContainText('ФИКСТУРА-ПОСТАНОВКА');
  await page.screenshot({ path: `${SHOTS}/stage-diff-error.png`, fullPage: true });
  await page.unrouteAll({ behavior: 'ignoreErrors' });
  await page.close();
});

// ── 4. Мобила 360: вертикальный граф без переполнения ────────────────────────────────
test('360px: граф вертикальным списком, документ не переполняется', async ({ context }) => {
  const page = await openPage(context, `/#/project/${FEATURE.pid}/feature/${FEATURE.fid}`);
  await page.setViewportSize({ width: 360, height: 800 });
  await expect(page.getByTestId('feature-page')).toBeVisible({ timeout: 30000 });
  await expect(page.getByTestId('feature-process-graph')).toBeVisible({ timeout: 20000 });
  await page.waitForTimeout(1500);
  await page.screenshot({ path: `${SHOTS}/graph-mobile.png`, fullPage: true });

  const sizes = await page.evaluate(() => ({
    scrollW: document.documentElement.scrollWidth,
    clientW: document.documentElement.clientWidth,
  }));
  expect(sizes.scrollW, 'документ не переполняется по горизонтали').toBeLessThanOrEqual(sizes.clientW + 1);

  // Вертикальный список: узлы образуют колонку (x совпадает), следующий ниже предыдущего
  const cut = await page.getByTestId('pgraph-node-cut').boundingBox();
  const plan = await page.getByTestId('pgraph-node-plan').boundingBox();
  expect(cut && plan).toBeTruthy();
  expect(plan!.y, 'на 360 этапы идут вниз, а не вправо').toBeGreaterThan(cut!.y + 10);
  await page.close();
});

// ── 5. Длинное имя этапа не вылезает за узел графа ───────────────────────────────────
test('длинное имя этапа помещается в узел графа', async ({ context }) => {
  const page = await openPage(context, `/#/project/${LONG.pid}/feature/${LONG.fid}`);
  await expect(page.getByTestId('feature-process-graph')).toBeVisible({ timeout: 30000 });
  await page.waitForTimeout(1200);
  await page.screenshot({ path: `${SHOTS}/graph-longtitle.png`, fullPage: true });
  const node = page.getByTestId('pgraph-node-qa-long');
  await expect(node).toBeVisible();
  const m = await node.evaluate(el => {
    const label = el.querySelector('span:last-child') as HTMLElement | null;
    const r = el.getBoundingClientRect();
    const lr = label?.getBoundingClientRect();
    return {
      label: label?.textContent ?? '',
      node: { w: r.width, h: r.height, scrollW: el.scrollWidth, scrollH: el.scrollHeight },
      labelBox: lr ? { x: lr.x, y: lr.y, w: lr.width, h: lr.height } : null,
    };
  });
  console.log('LONG NODE:', JSON.stringify(m));
  expect(m.label.length).toBeGreaterThan(20);
  // Текст не выходит за рамки узла по горизонтали и вертикали
  expect(m.node.scrollW, 'текст не расширяет узел').toBeLessThanOrEqual(m.node.w + 1);
  expect(m.node.scrollH, 'текст не вылезает по высоте').toBeLessThanOrEqual(m.node.h + 1);
  await page.close();
});

// ── 6. Легаси-фича без маршрута ──────────────────────────────────────────────────────
test('легаси-фича: нет графа и блока подтверждения, каталог шаблонов не грузится', async ({ context }) => {
  const page = await openPage(context, `/#/project/${LEGACY.pid}/feature/${LEGACY.fid}`);
  const templateCalls: string[] = [];
  page.on('request', r => { if (r.url().includes('/api/workflow-templates')) templateCalls.push(r.url()); });
  await expect(page.getByTestId('feature-page')).toBeVisible({ timeout: 30000 });
  await page.waitForTimeout(3000);
  await page.screenshot({ path: `${SHOTS}/legacy.png`, fullPage: true });

  await expect(page.getByTestId('feature-process-graph'), 'у легаси-фичи графа нет').toHaveCount(0);
  await expect(page.getByTestId('feature-workflow-confirm'), 'блока подтверждения нет').toHaveCount(0);
  const tabs = page.getByTestId('feature-page-tabs');
  await expect(tabs).toBeVisible();
  const tabText = await tabs.innerText();
  expect(tabText).toContain('Ход');
  expect(tabText).toContain('Материалы');
  expect(tabText).toContain('Разбор');
  expect(templateCalls, 'каталог шаблонов для легаси не запрашивается').toEqual([]);
  await page.close();
});

// ── 7. Секции промпта персоны ────────────────────────────────────────────────────────
test('секции промпта персоны: «выключено специальностью», своё с текстом и счётчиком', async ({ context }) => {
  const page = await openPage(context, `/#/personas/${PERSONA}`);
  await page.waitForTimeout(3000);
  await page.getByRole('button', { name: 'Редактировать' }).first().click();
  await page.waitForTimeout(1500);
  await page.getByText('Промпт', { exact: true }).first().click();
  await page.waitForTimeout(1500);
  await page.screenshot({ path: `${SHOTS}/persona-prompt.png`, fullPage: true });

  const section = page.getByTestId('prompt-section-history');
  await expect(section, 'секция «История решений» видна в форме').toBeVisible({ timeout: 15000 });
  await expect(section, 'секция, выключенная специальностью, помечена').toContainText('выключено специальностью');

  // Своё: текст подставляется, счётчик символов есть
  await section.getByText('Своё', { exact: true }).click();
  await page.waitForTimeout(600);
  const counter = await section.innerText();
  console.log('SECTION TEXT AFTER OWN:\n' + counter.slice(0, 800));
  expect(counter, 'счётчик символов показан').toMatch(/\d+ \/ 1024/);
  const area = section.locator('textarea');
  await expect(area).toBeVisible();

  // Персональное «выключить» отличается от «выключено специальностью»
  await section.getByText('Выключено', { exact: true }).click();
  await page.waitForTimeout(600);
  const offText = await section.innerText();
  expect(offText, 'личное выключение объясняется словами').toContain('Секция не попадает в промпт этой персоны');
  expect(offText, 'личное «выключено» уже не показывается как выключенное специальностью').not.toContain('выключено специальностью');
  await page.screenshot({ path: `${SHOTS}/persona-prompt-off.png`, fullPage: true });
  await page.close();
});
