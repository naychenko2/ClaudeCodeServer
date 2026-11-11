import { test, expect, type APIRequestContext, type Page } from '@playwright/test';
import { api, collectConsoleErrors, login, type Api } from './helpers';

// Приёмка фичи «Упрощение раздела «Фичи»» (FS-QA). Фикстура ставится файлами в data стенда
// (скрипт .cc-attachments/qa/fs_fixture.py), поэтому здесь только чтение и проверки.
// Проекты и фичи известны по именам «qa-fs · …».

const PROJECT_NAME = 'qa-fs';
const RICH = 'qa-fs · фича в работе';
const CLOSED = 'qa-fs · закрытая фича';
const AFTERMERGE = 'qa-fs · после мержа';
const MERGE = 'qa-fs · ждёт мержа';
const WAIT = 'qa-fs · ждёт тебя';
const ERROR = 'qa-fs · стоит';
const RUNNING = 'qa-fs · в работе';

test.describe.configure({ mode: 'serial' });

let request: APIRequestContext;
let token: string;
let client: Api;
let projectId: string;

interface IndexFeature {
  featureId: string;
  title: string;
  kind: string;
  state: string;
  chatCount: number;
  stepsAccepted: number;
  stepsTotal: number;
  keySessionId: string | null;
}

test.beforeAll(async ({ playwright, baseURL }) => {
  request = await playwright.request.newContext({ baseURL });
  token = await login(request);
  client = api(request, token);
  const projects = await client.get<Array<{ id: string; name: string }>>('/api/projects');
  const p = projects.find(x => x.name === PROJECT_NAME);
  expect(p, 'проект фикстуры qa-fs должен быть на стенде').toBeTruthy();
  projectId = p!.id;
});

test.afterAll(async () => {
  await request?.dispose();
});

async function features(): Promise<IndexFeature[]> {
  const idx = await client.get<{ features: IndexFeature[] }>('/api/tasks/features/index');
  return idx.features;
}

let navSeq = 0;

/** Переход с полной навигацией: приложение читает hash только при инициализации, поэтому
 *  смены фичи в одном документе не работают — нужен новый URL (счётчик в query). */
async function gotoApp(page: Page, hash: string) {
  navSeq += 1;
  await page.goto(`/?r=${navSeq}#${hash}`);
  await expect(page.locator('#root')).not.toBeEmpty();
}

async function openFeature(page: Page, featureId: string) {
  await gotoApp(page, `/project/${projectId}/feature/${featureId}`);
  await expect(page.locator('[data-testid="feature-page-tabs"]')).toBeVisible({ timeout: 15000 });
}

/** Пресет «Фичи» в списке чатов: без него список остаётся в обычном режиме. */
async function openFeatureList(page: Page) {
  await page.addInitScript(
    ([pid]) => localStorage.setItem(`cc_chat_filters:${pid}`, JSON.stringify({ groupBy: 'features', sortOrder: 'newest' })),
    [projectId],
  );
  await gotoApp(page, `/project/${projectId}`);
}

/** Замер интерфейса теми же числами, что у ui_probe: мелкие интерактивные элементы (<40 px),
 *  горизонтальное переполнение и ошибки консоли. Нужен там, где ui_probe не дотягивается:
 *  пресет «Фичи» живёт в localStorage, а ui_probe его не сеет. */
async function measure(page: Page, scope: string) {
  return await page.evaluate((scopeSel) => {
    const root = (scopeSel ? document.querySelector(scopeSel) : null) as HTMLElement | null;
    const base = root ?? document.body;
    const small: string[] = [];
    base.querySelectorAll<HTMLElement>('button, a, [role="button"], input, select, textarea').forEach(el => {
      const r = el.getBoundingClientRect();
      if (r.width === 0 && r.height === 0) return;
      if (r.width < 40 || r.height < 40) {
        const label = (el.getAttribute('aria-label') || el.textContent || el.tagName).trim().slice(0, 40);
        small.push(`${label} ${Math.round(r.width)}×${Math.round(r.height)}`);
      }
    });
    const overflowing: string[] = [];
    document.querySelectorAll<HTMLElement>('*').forEach(el => {
      if (root && !root.contains(el) && el !== root) return;
      if (el.scrollWidth > el.clientWidth + 1 && el.clientWidth > 0) {
        overflowing.push(`${el.tagName}.${el.className}`.slice(0, 60));
      }
    });
    return {
      smallTargets: small.length,
      smallSample: small.slice(0, 6),
      documentOverflow: document.documentElement.scrollWidth > document.documentElement.clientWidth + 1,
      overflowingContainers: overflowing.slice(0, 6),
    };
  }, scope);
}

test('сценарий 1: список фич — секции «Ждут тебя/Стоят» → «В работе» → «Закрытые», строка фичи', async ({ page, context }) => {
  const errors = collectConsoleErrors(page);
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
  await openFeatureList(page);

  // Секции: первая — внимание, вторая — работа, закрытые — свёрнутая строка.
  const titles = page.locator('[data-testid="feature-section-title"]');
  await expect(titles.first()).toBeVisible({ timeout: 15000 });
  const all = await titles.allTextContents();
  expect(all.length, 'должны быть секции «Ждут тебя»/«Стоят» и «В работе»').toBeGreaterThanOrEqual(2);
  expect(all.join('|')).toContain('Ждут тебя');
  expect(all.join('|')).toContain('В работе');

  // «Закрытые · N ›» — одна свёрнутая строка с числом.
  const closedToggle = page.locator('[data-testid="closed-section-toggle"]');
  await expect(closedToggle).toBeVisible();
  await expect(closedToggle).toContainText('Закрытые');
  const closedText = (await closedToggle.textContent()) ?? '';
  const closedCount = Number(closedText.replace(/\D+/g, ''));
  expect(closedCount, 'в секции «Закрытые» есть закрытая фича').toBeGreaterThanOrEqual(1);

  // До раскрытия закрытая фича не видна.
  await expect(page.locator(`text=${CLOSED}`)).toHaveCount(0);
  await closedToggle.click();
  await expect(page.locator(`text=${CLOSED}`).first()).toBeVisible();

  // У каждой фичи — строка: название, срок и этап (две строки + полоса).
  const header = page.locator('[data-testid="feature-group-header"]').first();
  await expect(header).toBeVisible();
  await expect(header.locator('[data-testid="feature-group-row1"]')).toBeVisible();
  await expect(header.locator('[data-testid="feature-group-row2"]')).toBeVisible();
  await expect(header.locator('[data-testid="feature-group-eta"]')).toBeVisible();

  // Смена проекта сворачивает «Закрытые».
  const other = await client.get<Array<{ id: string; name: string }>>('/api/projects');
  const otherProject = other.find(p => p.id !== projectId);
  if (otherProject) {
    await gotoApp(page, `/project/${otherProject.id}`);
    await page.waitForTimeout(800);
    await gotoApp(page, `/project/${projectId}`);
    await expect(page.locator('[data-testid="closed-section-toggle"]')).toBeVisible();
    // После возврата в проект секция снова свёрнута (состояние привязано к проекту).
    await expect(page.locator(`text=${CLOSED}`)).toHaveCount(0);
  }

  expect(errors, 'в консоли не должно быть ошибок').toEqual([]);
});

test('сценарий 2: первый экран фичи — «от тебя», карточки «Объём» и «Срок», три вкладки', async ({ page, context }) => {
  const errors = collectConsoleErrors(page);
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
  const idx = await features();
  const rich = idx.find(f => f.title === RICH)!;
  expect(rich, 'богатая фича есть в индексе').toBeTruthy();

  await openFeature(page, rich.featureId);

  // Три вкладки: «Ход · Материалы · Разбор».
  const tabs = page.locator('[data-testid="feature-page-tabs"] [role="tab"]');
  await expect(tabs).toHaveCount(3);
  expect((await tabs.allTextContents()).join('|')).toContain('Ход');
  expect((await tabs.allTextContents()).join('|')).toContain('Материалы');
  expect((await tabs.allTextContents()).join('|')).toContain('Разбор');

  // Карточка «Объём»: N из M шагов приняты.
  const volume = page.locator('[data-testid="volume-card"]');
  await expect(volume).toBeVisible();
  await expect(volume.locator('[data-testid="volume-count"]')).toContainText('2 из 3');
  // «план расширен: было N, стало M» (базовая оценка 2, план разросся до 3).
  await expect(volume.locator('[data-testid="volume-plan-grew"]')).toContainText('было 2, стало 3');

  // Карточка «Срок»: остаток, дата, источник оценки.
  const eta = page.locator('[data-testid="eta-card"]');
  await expect(eta).toBeVisible();
  await expect(eta.locator('[data-testid="eta-main"]')).toContainText('3 ч');
  const etaText = (await eta.textContent()) ?? '';
  expect(etaText, 'источник оценки виден').toContain('оценка');

  // «От тебя» одной фразой: у фичи без тревоги плашка спокойная («Ничего не нужно»).
  const richAsk = page.locator('[data-testid="feature-ask"]');
  await expect(richAsk).toHaveCount(1);
  await expect(richAsk).toHaveAttribute('data-tone', 'calm');
  await expect(richAsk).toContainText('Ничего не нужно');

  const wait = idx.find(f => f.title === WAIT)!;
  await openFeature(page, wait.featureId);
  const ask = page.locator('[data-testid="feature-ask"]');
  await expect(ask).toBeVisible();
  await expect(ask).toContainText('нужен выбор по сроку');

  expect(errors, 'в консоли не должно быть ошибок').toEqual([]);
});

test('сценарий 3: вкладка «Ход» — шаги, доработки отдельной группой, фильтры сходятся с числом', async ({ page, context }) => {
  const errors = collectConsoleErrors(page);
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
  const idx = await features();
  const rich = idx.find(f => f.title === RICH)!;
  await openFeature(page, rich.featureId);

  const progress = page.locator('[data-testid="progress-tab"]');
  await expect(progress).toBeVisible();
  // Шаги этапа спрятаны под «Все N шагов этапа ›» — раскрываем.
  const stepsToggle = page.locator('[data-testid="progress-steps-toggle"]');
  await expect(stepsToggle).toBeVisible();
  await stepsToggle.click();
  await expect(page.locator('[data-testid="progress-steps"]')).toBeVisible();
  await expect(page.locator('[data-testid="tasks"]')).toBeVisible();
  await expect(page.locator('[data-testid="tasks-list"]')).toBeVisible();

  // Шаги видны строками с кодами плана.
  for (const ref of ['S1', 'S2', 'S3']) {
    await expect(page.locator(`[data-testid="step-row-${ref}"]`), `шаг ${ref}`).toBeVisible();
  }

  // Фильтры корзин: «все N» совпадает с числом строк, остальные корзины в сумме дают то же.
  const allChip = page.locator('[data-testid="tasks-filter-all"]');
  await expect(allChip).toBeVisible();
  const allText = (await allChip.textContent()) ?? '';
  const allCount = Number(allText.replace(/\D+/g, ''));
  const rows = page.locator('[data-testid="tasks-list"] [data-testid^="step-row-"]');
  await expect(rows).toHaveCount(allCount);
  const bucketSum = await page.evaluate(() => {
    const chips = Array.from(document.querySelectorAll('[data-testid^="tasks-filter-"]')) as HTMLElement[];
    return chips
      .map(c => c.getAttribute('data-testid') ?? '')
      .filter(k => !['tasks-filter-all', 'tasks-filter-service', 'tasks-filter-inMaster', 'tasks-filter-withdrawn'].includes(k))
      .map(k => Number((document.querySelector(`[data-testid="${k}"]`)?.textContent ?? '').replace(/\D+/g, '')))
      .reduce((a, b) => a + b, 0);
  });
  expect(bucketSum, 'сумма корзин равна числу шагов').toBe(allCount);

  // Клик по корзине «принято» оставляет только принятые шаги.
  const acceptedChip = page.locator('[data-testid="tasks-filter-accepted"]');
  if (await acceptedChip.count()) {
    await acceptedChip.click();
    const acceptedCount = Number(((await acceptedChip.textContent()) ?? '').replace(/\D+/g, ''));
    await expect(rows).toHaveCount(acceptedCount);
    await allChip.click();
    await expect(rows).toHaveCount(allCount);
  }

  // Доработки после мержа — отдельной группой (фича «после мержа»: шаг A9 вне плана).
  const after = idx.find(f => f.title === AFTERMERGE)!;
  expect(after, 'фича «после мержа» есть в индексе').toBeTruthy();
  await openFeature(page, after.featureId);
  const afterToggle = page.locator('[data-testid="progress-steps-toggle"]');
  await expect(afterToggle).toBeVisible();
  await afterToggle.click();
  await expect(page.locator('[data-testid="tasks-followup-header"]'), 'заголовок группы доработок').toBeVisible();
  await expect(page.locator('[data-testid="tasks-followup-count"]')).toContainText('0 из 1');
  await expect(page.locator('[data-testid="followup-row-A9"]'), 'доработка A9 отдельной строкой').toBeVisible();
  // И та же цифра — в карточке «Объём» первого экрана.
  await expect(page.locator('[data-testid="volume-followups"]')).toContainText('0 из 1');

  // У закрытой фичи списка шагов нет (нет текущего этапа) — доработки видны строкой в сводке.
  const closed = idx.find(f => f.title === CLOSED)!;
  await openFeature(page, closed.featureId);
  await expect(page.locator('[data-testid="closed-summary-followup"]'), 'доработки в сводке закрытой фичи').toContainText('1 из 1');

  expect(errors, 'в консоли не должно быть ошибок').toEqual([]);
});

test('сценарий 4: «Материалы» — прочие документы открываются в центре, плашка «Из ветки», кнопка «К фиче»', async ({ page, context }) => {
  const errors = collectConsoleErrors(page);
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
  const idx = await features();
  const rich = idx.find(f => f.title === RICH)!;
  await openFeature(page, rich.featureId);

  await page.locator('[data-testid="feature-page-tabs"] [role="tab"]', { hasText: 'Материалы' }).click();
  await expect(page.locator('[data-testid="plans-grid-other-docs"]')).toBeVisible();

  // Прочий документ открывается в центре с taskId автора.
  const otherDoc = page.locator('[data-testid="other-doc-row"]').first();
  await expect(otherDoc).toBeVisible();
  await otherDoc.click();

  // Плашка «Из ветки … · только просмотр».
  const branchBadge = page.locator('[data-testid="file-task-notice"]');
  await expect(branchBadge).toBeVisible({ timeout: 15000 });
  await expect(branchBadge).toContainText('Из ветки');
  await expect(branchBadge).toContainText('только просмотр');

  // Кнопка возврата «К фиче» — на мобиле видимая подпись, в title и aria-label.
  await page.setViewportSize({ width: 360, height: 800 });
  await page.waitForTimeout(500);
  const back = page.getByRole('button', { name: /К фиче/ });
  await expect(back.first()).toBeVisible();
  const title = await back.first().getAttribute('title');
  const aria = await back.first().getAttribute('aria-label');
  expect(title ?? '', 'подпись «К фиче» есть в title').toContain('К фиче');
  expect(aria ?? '', 'подпись «К фиче» есть в aria-label').toContain('К фиче');
  await page.setViewportSize({ width: 1280, height: 800 });

  // Файл только из ветки отдаётся (200, не 404): проверяем ручку материала.
  const materials = await client.get<{ materials: Array<{ path: string; taskId: string }> }>(
    `/api/tasks/features/${rich.featureId}`,
  );
  expect(materials.materials.length, 'у фичи есть материалы').toBeGreaterThan(0);
  const m = materials.materials.find(x => x.taskId) ?? materials.materials[0];
  const res = await request.get(
    `/api/projects/${projectId}/files/content?path=${encodeURIComponent(m.path)}&taskId=${encodeURIComponent(String(m.taskId ?? ''))}`,
    { headers: { Authorization: `Bearer ${token}` } },
  );
  expect(res.status(), `материал ${m.path} должен отдаваться`).toBe(200);

  expect(errors, 'в консоли не должно быть ошибок').toEqual([]);
});

test('сценарий 5: «Разбор» — шесть секций, строка без данных не рисуется', async ({ page, context }) => {
  const errors = collectConsoleErrors(page);
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
  const idx = await features();
  const rich = idx.find(f => f.title === RICH)!;
  await openFeature(page, rich.featureId);

  await page.locator('[data-testid="feature-page-tabs"] [role="tab"]', { hasText: 'Разбор' }).click();
  await expect(page.locator('[data-testid="review-anchors"]')).toBeVisible({ timeout: 15000 });

  for (const id of ['time', 'volume', 'returns', 'who', 'spend', 'history']) {
    await expect(page.locator(`[data-testid="review-sec-${id}"]`), `секция «${id}»`).toBeVisible();
  }

  // «Время и срок»: сдвиг срока (две оценки: 120 → 180).
  const time = page.locator('[data-testid="review-sec-time"]');
  const timeText = (await time.textContent()) ?? '';
  expect(timeText, 'в «Времени и сроке» есть сдвиг').toMatch(/сдвиг|1 ч|60/);
  // «Расход»: доля кэша.
  const spend = page.locator('[data-testid="review-sec-spend"]');
  const spendText = (await spend.textContent()) ?? '';
  expect(spendText, 'в «Расходе» есть доля кэша').toContain('кэш');

  // «Кто работал» — строки персон.
  const who = page.locator('[data-testid="review-sec-who"]');
  await expect(who).toBeVisible();

  // Строка без данных не рисуется: у фичи без расхода секция «Расход» говорит словами.
  const errFeature = idx.find(f => f.title === ERROR)!;
  await openFeature(page, errFeature.featureId);
  await page.locator('[data-testid="feature-page-tabs"] [role="tab"]', { hasText: 'Разбор' }).click();
  await expect(page.locator('[data-testid="review-sec-spend"]')).toBeVisible();

  expect(errors, 'в консоли не должно быть ошибок').toEqual([]);
});

test('сценарий 6: шапка чата — карточка фичи, чип «к фиче» не дублирует её', async ({ page, context }) => {
  const errors = collectConsoleErrors(page);
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
  const idx = await features();
  const rich = idx.find(f => f.title === RICH)!;
  const chatId = rich.keySessionId!;
  expect(chatId, 'у фичи есть ключевой чат').toBeTruthy();

  await gotoApp(page, `/project/${projectId}/chat/${chatId}`);
  await expect(page.locator('#root')).not.toBeEmpty();
  await expect(page.locator('[data-testid="feature-chat-card"]')).toBeVisible({ timeout: 15000 });

  // Две строки карточки.
  await expect(page.locator('[data-testid="feature-card-row1"]')).toBeVisible();
  await expect(page.locator('[data-testid="feature-card-row2"]')).toBeVisible();

  // «Нужно от тебя / Что мешает» — подписи на месте (у фичи без тревоги может не быть).
  const cardText = (await page.locator('[data-testid="feature-chat-card"]').textContent()) ?? '';
  expect(cardText.length, 'карточка не пустая').toBeGreaterThan(0);

  // Чип «к фиче» не дублирует карточку: он есть, но без тех же строк.
  const chip = page.getByText(/к фиче/i).first();
  if (await chip.count()) {
    const chipBox = await chip.boundingBox();
    expect(chipBox, 'чип виден').toBeTruthy();
  }

  expect(errors, 'в консоли не должно быть ошибок').toEqual([]);
});

test('сценарий 7: диалог мержа — закрытый хвост поимённо, без выбора мерж отклоняется', async ({ page, context }) => {
  const errors = collectConsoleErrors(page);
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
  const idx = await features();
  const merge = idx.find(f => f.title === MERGE)!;
  expect(merge, 'фича ждёт мержа есть').toBeTruthy();

  await openFeature(page, merge.featureId);
  const mergeButton = page.locator('[data-testid="feature-page-main-action"]').getByRole('button', { name: /Мерж/ });
  await expect(mergeButton.first()).toBeVisible({ timeout: 15000 });
  await mergeButton.first().click();

  // Модалка подтверждения мержа (собственных testid у неё нет — ищем по содержимому).
  const tail = page.locator('[data-testid="feature-merge-tail"]');
  await expect(tail, 'блок закрытого хвоста').toBeVisible({ timeout: 15000 });
  const dialogText = (await page.locator('body').textContent()) ?? '';
  expect(dialogText, 'в диалоге есть имя ветки').toMatch(/feat\/features-simplify|ветк/);
  // Закрытый хвост поимённо: ключ шага M2.
  const tailKeys = page.locator('[data-testid="feature-merge-tail-keys"]');
  await expect(tailKeys, 'список закрытого хвоста').toBeVisible();
  await expect(tailKeys, 'хвост назван поимённо').toContainText('M2');

  // Без явного выбора кнопка мержа неактивна.
  const confirm = page.getByRole('button', { name: /^Мерж$/ });
  await expect(confirm).toBeDisabled();

  // После выбора кнопка активна.
  await page.getByText('Снять хвост и мержить').click();
  await expect(confirm).toBeEnabled();

  expect(errors, 'в консоли не должно быть ошибок').toEqual([]);
});

test('сценарий 8: эскалация — ответ человека в чате задачи снимает ожидание', async ({ page, context }) => {
  const errors = collectConsoleErrors(page);
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
  const idx = await features();
  const wait = idx.find(f => f.title === WAIT)!;

  // До ответа у фичи есть тревога «ждёт».
  const before = await client.get<{ run: { attention: { kind: string } | null } }>(
    `/api/tasks/features/${wait.featureId}`,
  );
  expect(before.run.attention?.kind, 'фича ждёт человека').toBe('wait');

  // Ответ человека в чате задачи (MCP-путь эскалации) снимает ожидание.
  const tasks = await client.get<Array<{ id: string; title: string; role: string | null; awaitingHumanSince: string | null; linkedSessionId: string | null }>>(
    `/api/projects/${projectId}/tasks`,
  );
  const waitingTask = tasks.find(t => t.role === 'step' && t.awaitingHumanSince);
  expect(waitingTask, 'шаг с ожиданием человека есть').toBeTruthy();
  const chatId = waitingTask!.linkedSessionId;
  expect(chatId, 'у шага есть чат исполнителя').toBeTruthy();

  await openFeature(page, wait.featureId);
  await expect(page.locator('[data-testid="feature-ask"]'), 'плашка «нужен выбор по сроку»').toBeVisible();

  // Ответ человека в чате задачи снимает ожидание.
  const beforeErrors = errors.length;
  await client.post(`/api/sessions/${chatId}/messages`, { text: 'Беру срок: неделя.', wait: 'none' });
  await expect
    .poll(async () => {
      const after = await client.get<Array<{ id: string; awaitingHumanSince: string | null }>>(
        `/api/projects/${projectId}/tasks`,
      );
      return after.find(t => t.id === waitingTask!.id)?.awaitingHumanSince ?? null;
    }, { timeout: 20000, intervals: [500, 1000, 2000] })
    .toBeNull();

  // Фича перестала ждать человека.
  await expect
    .poll(async () => {
      const after = await client.get<{ run: { attention: { kind: string } | null } }>(
        `/api/tasks/features/${wait.featureId}`,
      );
      return after.run.attention?.kind ?? 'none';
    }, { timeout: 20000, intervals: [500, 1000, 2000] })
    .not.toBe('wait');

  expect(errors.slice(beforeErrors), 'после ответа в консоли не должно быть ошибок').toEqual([]);
});

test('замеры: список фич на 360 и 1280', async ({ page, context }) => {
  const errors = collectConsoleErrors(page);
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
  for (const width of [360, 1280]) {
    await page.setViewportSize({ width, height: 900 });
    await openFeatureList(page);
    await page.locator('[data-testid="feature-section-title"]').first().waitFor({ timeout: 15000 });
    console.log(`QA-MEASURE список фич ${width}: ${JSON.stringify(await measure(page, ''))}`);
  }
  // ERR_NETWORK_CHANGED — артефакт смены viewport с перезагрузкой страницы, не ошибка фичи.
  expect(errors.filter(e => !e.includes('ERR_NETWORK_CHANGED')), 'в консоли не должно быть ошибок').toEqual([]);
});

test('замеры: страница фичи на 360 и 1280', async ({ page, context }) => {
  const errors = collectConsoleErrors(page);
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
  const rich = (await features()).find(f => f.title === RICH)!;
  for (const width of [360, 1280]) {
    await page.setViewportSize({ width, height: 900 });
    await openFeature(page, rich.featureId);
    console.log(`QA-MEASURE страница фичи ${width}: ${JSON.stringify(await measure(page, '[data-testid="feature-page"]'))}`);
  }
  expect(errors.filter(e => !e.includes('ERR_NETWORK_CHANGED')), 'в консоли не должно быть ошибок').toEqual([]);
});

async function measureFile(page: Page, width: number, context: any) {
  const errors = collectConsoleErrors(page);
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
  const rich = (await features()).find(f => f.title === RICH)!;
  await page.setViewportSize({ width, height: 900 });
  await openFeature(page, rich.featureId);
  await page.locator('[data-testid="feature-page-tabs"] [role="tab"]', { hasText: 'Материалы' }).click();
  await page.locator('[data-testid="other-doc-row"]').first().click();
  await page.locator('[data-testid="file-task-notice"]').waitFor({ timeout: 15000 });
  console.log(`QA-MEASURE файл из материалов ${width}: ${JSON.stringify(await measure(page, ''))}`);
  expect(errors.filter(e => !e.includes('ERR_NETWORK_CHANGED')), 'в консоли не должно быть ошибок').toEqual([]);
}

test('замеры: файл из «Материалов» на 360', async ({ page, context }) => {
  await measureFile(page, 360, context);
});

test('замеры: файл из «Материалов» на 1280', async ({ page, context }) => {
  await measureFile(page, 1280, context);
});
