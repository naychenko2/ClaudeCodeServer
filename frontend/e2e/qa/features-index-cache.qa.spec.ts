// QA-приёмка фичи «Кэш среза индекса фич» (FI-1/FI-2/FI-3).
// Проверяем поведение, а не код: при переключении проектов вкладка чатов в режиме
// «Фичи» не должна показывать кадр «все чаты в Без фичи», а возврат в проект
// должен рисовать группы с первого кадра.
//
// Стенд — боевые данные (проекты «AI Home» и «Русская Strava»), стенд поднят из
// ветки fix/features-index-cache (docs/operations/qa-stand.md).
//
// Доказательство «ни одного кадра»: в страницу вживлена выборка DOM раз в 16 мс
// (initScript до загрузки приложения). Кадр считается плохим, если в режиме фич
// НЕТ ни одной группы фичи, НЕТ строки «Загружаем фичи…» и при этом видны карточки
// чатов — то есть все чаты висят в «Без фичи» (исходный баг Андрея).

import { test, expect, type APIRequestContext, type BrowserContext, type Page } from '@playwright/test';
import { api, collectConsoleErrors, login, type Api } from './helpers';

const A_ID = 'fedfe8c3-eaab-48ae-a5bd-1661bb68f7cc';   // «AI Home»
const A_NAME = 'AI Home';
const B_ID = '69b68a50-e030-4981-ab8a-c3edac4ffeeb';   // «Русская Strava»
const B_NAME = 'Русская Strava';

type Sample = {
  t: number;
  groups: number;
  cards: number;
  loading: boolean;
  empty: boolean;
  hash: string;
  first: string;
};

// Выборка состояния списка чатов. Ставится через addInitScript — до кода приложения,
// поэтому успевает зафиксировать самый первый кадр.
const SAMPLER = `
(() => {
  const samples = [];
  window.__qaSamples = samples;
  const t0 = performance.now();
  const tick = () => {
    const groups = document.querySelectorAll('[data-testid="feature-group-header"]').length;
    const cards = document.querySelectorAll('.cc-card-shadow').length;
    let loading = false, empty = false;
    if (groups === 0) {
      const txt = document.body ? (document.body.textContent || '') : '';
      loading = txt.includes('Загружаем фичи');
      empty = txt.includes('Фич пока нет');
    }
    samples.push({ t: Math.round(performance.now() - t0), groups, cards, loading, empty,
      hash: location.hash, first: (document.querySelector('[data-testid="feature-group-header"]')?.textContent || '').slice(0, 40) });
  };
  tick();
  window.__qaTimer = setInterval(tick, 16);
})();
`;

// 404 по статике/иконкам — не про эту фичу; в отчёт идут отдельно.
function realErrors(errors: string[]): string[] {
  return errors.filter(e => !/Failed to load resource/.test(e));
}

let request: APIRequestContext;
let token: string;
let client: Api;

async function readSamples(page: Page): Promise<Sample[]> {
  return page.evaluate(() => (window as unknown as { __qaSamples: Sample[] }).__qaSamples || []);
}

// Плохой кадр: режим фич, групп нет, строки загрузки нет, карточки чатов на месте.
function badFrames(samples: Sample[]): Sample[] {
  return samples.filter(s => s.groups === 0 && !s.loading && s.cards > 0);
}

async function setupPage(_page: Page, context: BrowserContext) {
  await context.addInitScript(
    ([tk, a, b]) => {
      localStorage.setItem('cc_token', tk as string);
      localStorage.setItem('cc_chat_filters:' + a, JSON.stringify({ groupBy: 'features', sortOrder: 'newest' }));
      localStorage.setItem('cc_chat_filters:' + b, JSON.stringify({ groupBy: 'features', sortOrder: 'newest' }));
    },
    [token, A_ID, B_ID] as const,
  );
  await context.addInitScript(SAMPLER);
}

// Переключение проекта через рельс → палитра «Перейти к проекту…».
async function switchProject(page: Page, name: string) {
  await page.getByRole('button', { name: /Перейти к проекту/ }).first().click();
  const input = page.getByPlaceholder('Перейти к проекту…');
  await input.waitFor({ state: 'visible', timeout: 15000 });
  await input.fill(name);
  await page.waitForTimeout(300);
  const opt = page.locator('div[role="button"]').filter({ hasText: name }).first();
  await opt.waitFor({ state: 'visible', timeout: 15000 });
  await opt.click();
}

async function waitGroups(page: Page, timeout = 20000) {
  await page.waitForFunction(
    () => document.querySelectorAll('[data-testid="feature-group-header"]').length > 0,
    undefined,
    { timeout },
  );
}

test.describe.configure({ mode: 'serial' });

test.beforeEach(async ({ page }) => {
  page.setDefaultTimeout(20000);
});

test.beforeAll(async ({ playwright, baseURL }) => {
  request = await playwright.request.newContext({ baseURL });
  token = await login(request);
  client = api(request, token);
});

test.afterAll(async () => {
  await request?.dispose();
});

test('сценарий 1: холодный вход в проект A — ни одного кадра «все чаты в Без фичи», группы сразу', async ({ page, context }) => {
  const errors = collectConsoleErrors(page);
  await setupPage(page, context);

  await page.goto(`/#/project/${A_ID}`);
  await waitGroups(page);
  // Дай дописаться выборке после появления групп
  await page.waitForTimeout(700);
  const samples = await readSamples(page);

  const bad = badFrames(samples);
  const firstGroups = samples.find(s => s.groups > 0);
  const sawLoading = samples.some(s => s.loading);
  const sawEmpty = samples.some(s => s.empty);

  console.log('СЦЕНАРИЙ 1: всего кадров', samples.length,
    '| первый кадр с группами на t=', firstGroups?.t, 'мс',
    '| кадров с «Загружаем фичи…»:', samples.filter(s => s.loading).length,
    '| плохих кадров:', bad.length,
    '| максимум групп в кадре:', Math.max(...samples.map(s => s.groups)));

  expect(bad, 'ни одного кадра, где все чаты висят в «Без фичи»').toEqual([]);
  expect(firstGroups, 'группы фич появились').toBeTruthy();
  expect(sawEmpty, 'не было кадра «Фич пока нет» в непустом проекте').toBe(false);
  // Строка загрузки допустима, но не должна «мигать» дольше секунды
  if (sawLoading) {
    const lastLoading = [...samples].reverse().find(s => s.loading);
    const after = samples.find(s => s.t > (lastLoading?.t ?? 0) && s.groups > 0);
    expect((after?.t ?? 0) - (lastLoading?.t ?? 0), 'строка загрузки живёт недолго').toBeLessThan(1500);
  }
  expect(realErrors(errors), 'ошибок консоли нет (кроме 404 статики)').toEqual([]);
});

test('сценарий 7: замер времени до групп на большом проекте (A)', async ({ page, context }) => {
  await setupPage(page, context);
  await page.goto(`/#/project/${A_ID}`);
  await waitGroups(page);
  const samples = await readSamples(page);
  const firstGroups = samples.find(s => s.groups > 0);
  console.log('СЦЕНАРИЙ 7: время до первых групп (холодная загрузка страницы), мс =', firstGroups?.t);
  expect(firstGroups?.t).toBeGreaterThan(0);
});

test.setTimeout(240_000);
test('сценарии 2–3: A→B→A и быстрая серия A→B→A→B — группы с первого кадра, итог — последний проект', async ({ page, context }) => {
  const errors = collectConsoleErrors(page);
  await setupPage(page, context);
  await page.goto(`/#/project/${A_ID}`);
  await waitGroups(page);

  // ── Сценарий 2: A → B → A ────────────────────────────────────────────────────
  await switchProject(page, B_NAME);
  await waitGroups(page);
  // A покинут — срез кэшируется. Возвращаемся в A.
  const beforeSwitch = (await readSamples(page)).length;
  await switchProject(page, A_NAME);
  const samplesA = (await readSamples(page)).slice(beforeSwitch);
  const idxA = samplesA.findIndex(s => s.hash.includes(A_ID));
  const afterA = idxA >= 0 ? samplesA.slice(idxA) : [];
  const firstBack = afterA.find(s => s.groups > 0);
  const badBack = badFrames(afterA);
  const emptyAfterSwitch = afterA.filter(s => s.groups === 0);
  console.log('СЦЕНАРИЙ 2: первый кадр после возврата в A — t=', firstBack?.t, 'мс, групп =', firstBack?.groups,
    '| строк загрузки после возврата:', samplesA.filter(s => s.loading).length,
    '| плохих кадров:', badBack.length);
  console.log('СЦЕНАРИЙ 2 (кадры после возврата):',
    samplesA.slice(0, 40).map(s => `${s.t}ms g=${s.groups} c=${s.cards} l=${s.loading ? 1 : 0} ${s.hash} «${s.first.replace(/\s+/g, ' ')}»`).join('\n  '));
  expect(idxA, 'переключение на проект A состоялось').toBeGreaterThanOrEqual(0);
  expect(badBack, 'при возврате в A не было кадра «все чаты в Без фичи»').toEqual([]);
  expect(firstBack, 'после возврата в A группы появились').toBeTruthy();
  expect(firstBack!.groups, 'возврат в A рисует группы с первого кадра (кэш среза)').toBeGreaterThan(0);
  expect(emptyAfterSwitch.length, 'после переключения на A список не оставался пустым ни одного кадра').toBe(0);
  expect(afterA.filter(s => s.loading).length, 'строка «Загружаем фичи…» при возврате в A не показывалась').toBe(0);

  // ── Сценарий 3: быстрая серия A→B→A→B ───────────────────────────────────────
  await switchProject(page, B_NAME);
  await switchProject(page, A_NAME);
  await switchProject(page, B_NAME);
  await waitGroups(page);
  await page.waitForTimeout(500);

  const indexB = await client.get<{ features: Array<{ title: string }> }>(`/api/tasks/features/index?projectId=${B_ID}`);
  const indexA = await client.get<{ features: Array<{ title: string }> }>(`/api/tasks/features/index?projectId=${A_ID}`);
  const titlesB = new Set(indexB.features.map(f => f.title));
  const titlesA = new Set(indexA.features.map(f => f.title));
  const onlyA = [...titlesA].filter(t => !titlesB.has(t));

  const shownTitles = await page.locator('[data-testid="feature-group-header"]').allInnerTexts();
  const foreign = shownTitles.filter(t => onlyA.some(a => t.includes(a)));
  console.log('СЦЕНАРИЙ 3: показано групп:', shownTitles.length, '| чужих (только из A):', foreign.length,
    '| первые:', shownTitles.slice(0, 3).map(t => t.split('\n')[0]).join(' | '));

  expect(foreign, 'после серии переключений в группах нет фич чужого проекта').toEqual([]);
  expect(shownTitles.length, 'в списке есть группы фич последнего проекта').toBeGreaterThan(0);
  expect(realErrors(errors), 'ошибок консоли нет (кроме 404 статики)').toEqual([]);
});

test('сценарий 4: живое обновление (task_changed) — группы обновляются без мерцания', async ({ page, context }) => {
  const errors = collectConsoleErrors(page);
  await setupPage(page, context);
  await page.goto(`/#/project/${A_ID}`);
  await waitGroups(page);

  const index = await client.get<{ features: Array<{ featureId: string; title: string }> }>(
    `/api/tasks/features/index?projectId=${A_ID}`);
  const target = index.features[0];
  const newTitle = `QA-обновление ${Date.now()}`;
  console.log('СЦЕНАРИЙ 4: меняем название фичи', target.title, '→', newTitle);

  const mark = (await readSamples(page)).length;
  await client.put(`/api/tasks/${target.featureId}`, { title: newTitle });

  await page.waitForFunction(
    (t) => Array.from(document.querySelectorAll('[data-testid="feature-group-header"]'))
      .some(el => (el.textContent || '').includes(t)),
    newTitle,
    { timeout: 15000 },
  );
  await page.waitForTimeout(400);
  const samples = await readSamples(page);
  const tail = samples.slice(mark);
  const bad = badFrames(tail);
  console.log('СЦЕНАРИЙ 4: кадров после правки:', tail.length, '| плохих:', bad.length,
    '| групп в последнем кадре:', tail[tail.length - 1]?.groups);
  expect(bad, 'при живом обновлении не было кадра «все чаты в Без фичи»').toEqual([]);
  expect(realErrors(errors), 'ошибок консоли нет (кроме 404 статики)').toEqual([]);
});

test('сценарий 5: выход и вход другим пользователем без перезагрузки — данных прежнего владельца нет', async ({ page, context, playwright, baseURL }) => {
  const errors = collectConsoleErrors(page);
  await setupPage(page, context);
  await page.goto(`/#/project/${A_ID}`);
  await page.waitForTimeout(3000);
  const adminPhase = await page.evaluate(() => ({
    groups: document.querySelectorAll('[data-testid="feature-group-header"]').length,
    cards: document.querySelectorAll('.cc-card-shadow').length,
    text: (document.body.textContent || '').slice(0, 400),
  }));
  console.log('СЦЕНАРИЙ 5 (фаза владельца): групп =', adminPhase.groups, '| карточек =', adminPhase.cards,
    '| текст:', adminPhase.text.replace(/\s+/g, ' ').slice(0, 200));

  // Вторая учётка — участник проекта A (иначе проект ей не виден).
  await client.put('/api/feature-flags/shared-projects', { enabled: true });
  const members = await client.get<{ members?: Array<{ username: string }> }>(`/api/projects/${A_ID}/members`);
  const already = (members?.members ?? []).some(m => m.username === 'qa2');
  if (!already) {
    await client.post(`/api/projects/${A_ID}/members`, { username: 'qa2', role: 'reader' });
  }
  const qa2Req = await playwright.request.newContext({ baseURL });
  const qa2Login = await qa2Req.post('/api/auth/login', { data: { username: 'qa2', password: 'qapass12345' } });
  expect(qa2Login.ok(), `логин qa2 (${qa2Login.status()})`).toBeTruthy();
  const qa2Token = (await qa2Login.json()).token as string;
  await api(qa2Req, qa2Token).put('/api/feature-flags/shared-projects', { enabled: true });

  // Названия фич прежнего владельца — эталон утечки.
  const adminIndex = await client.get<{ features: Array<{ title: string }> }>(
    `/api/tasks/features/index?projectId=${A_ID}`);
  const adminTitles = adminIndex.features.map(f => f.title);

  // Выход из приложения (без перезагрузки страницы).
  await page.getByRole('button', { name: /Меню пользователя/ }).first().click();
  await page.getByRole('button', { name: 'Выйти' }).click();
  await page.getByPlaceholder('Имя пользователя').waitFor({ state: 'visible', timeout: 15000 });
  console.log('СЦЕНАРИЙ 5: экран входа показан');

  // Вход второй учёткой в той же вкладке (без перезагрузки).
  await page.getByPlaceholder('Имя пользователя').fill('qa2');
  await page.getByPlaceholder('Пароль').fill('qapass12345');
  await page.getByRole('button', { name: 'Войти' }).click();
  await page.waitForTimeout(2500);
  const afterLogin = await page.evaluate(() => ({
    userMenu: !!document.querySelector('[aria-label^="Меню пользователя"]'),
    text: (document.body.textContent || '').replace(/\s+/g, ' ').slice(0, 300),
  }));
  console.log('СЦЕНАРИЙ 5: после входа меню пользователя =', afterLogin.userMenu, '| текст:', afterLogin.text);

  // Второй владелец открывает тот же проект: у qa2 в хабе нет рельсы — проект открывают из списка.
  await page.getByText(A_NAME, { exact: true }).first().click();
  await page.waitForFunction(id => location.hash.includes(id), A_ID, { timeout: 20000 });
  await page.waitForTimeout(3000);

  const headers = await page.locator('[data-testid="feature-group-header"]').allInnerTexts();
  const leaked = headers.filter(h => adminTitles.some(t => h.includes(t)));
  console.log('СЦЕНАРИЙ 5: групп после входа вторым пользователем =', headers.length,
    '| групп с фичами прежнего владельца =', leaked.length);
  expect(leaked, 'данные прежнего владельца не показываются').toEqual([]);
  expect(realErrors(errors), 'ошибок консоли нет (кроме 404 статики)').toEqual([]);
});

test('снимки экрана: список чатов в режиме «Фичи» на 1280 и 360', async ({ page, context }) => {
  await setupPage(page, context);
  await page.setViewportSize({ width: 1280, height: 900 });
  await page.goto(`/#/project/${A_ID}`);
  await waitGroups(page);
  await page.waitForTimeout(1200);
  await page.screenshot({ path: '../../.cc-attachments/qa-features-index/branch-1280-features.png', fullPage: false });

  await page.setViewportSize({ width: 360, height: 760 });
  await page.waitForTimeout(1200);
  await page.screenshot({ path: '../../.cc-attachments/qa-features-index/branch-360-features.png', fullPage: false });
});
