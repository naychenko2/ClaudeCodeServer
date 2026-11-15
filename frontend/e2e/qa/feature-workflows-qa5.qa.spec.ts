import { test, expect, type BrowserContext, type Page } from '@playwright/test';
import { createHash } from 'node:crypto';
import { mkdirSync, readFileSync } from 'node:fs';
import { login, api, collectConsoleErrors, type Api } from './helpers';

// QA-5 (задача c2d2cdb6, фича a149e308): приёмка двух правок про фиолетовые плашки
// страницы фичи. Точка 1 (Ф-2.41, коммит 6e517362f): proposed-узел графа — прозрачный фон
// по макету .node.prop; фиолетовыми остались пунктир рамки и цвет заголовка.
// Точка 2 (Ф-2.42, коммит 2c97f7d31): поясняющая строка — только в состоянии предложения
// (десктоп под шапкой блока; в мобильной шторке).
// Состояния — посев в данные стенда (два эпика в tasks.json, добавлены при остановленном
// сервере; удалены этим спеком через API): предложение и «ни предложения, ни маршрута».
// Подтверждённое состояние — через API внутри S2 (свой шаблон, confirm, закрытая qa-задача).

const PID = 'ce3e224a-d0f8-4828-9519-f51d4c3dbf6a';
const EPIC_PROPOSED = 'cedc2dcb-0612-4f9e-b9d3-35e9e9610a8e'; // посев: предложение без подтверждения
const EPIC_NOPROPOSAL = 'd5b0e005-11f5-4a2e-9c1f-a5b0c0d1e7f2'; // посев: ни предложения, ни маршрута
const SHOTS = '.cc-attachments/qa-wf-e2e';
const PLAN_HINT = 'Фиолетовый цвет означает: это предложение модели. Пока маршрут не подтверждён, предложенные этапы не выполняются.';
const ROLES = ['cut', 'design', 'plan', 'step', 'review', 'qa', 'final', 'merge'];

let client: Api;
let request: import('@playwright/test').APIRequestContext;
let token: string;
const tplIds: string[] = [];
const epicIds: string[] = [EPIC_PROPOSED, EPIC_NOPROPOSAL]; // посев убирается здесь же

test.beforeAll(async ({ playwright, baseURL }) => {
  mkdirSync(SHOTS, { recursive: true });
  request = await playwright.request.newContext({ baseURL });
  token = await login(request);
  client = api(request, token);
});

test.afterAll(async () => {
  for (const id of tplIds) await client.del(`/api/workflow-templates/${id}`).catch(() => undefined);
  // Ручка удаления задачи — /api/tasks/{id} (тот же путь, что у фронта);
  // /projects/{pid}/tasks/{id} даёт 404 и молча не чистит.
  for (const id of epicIds) await client.del(`/api/tasks/${id}`).catch(() => undefined);
  await request?.dispose();
});

async function openPage(ctx: BrowserContext, path: string, w: number, h: number) {
  const page = await ctx.newPage();
  const errs = collectConsoleErrors(page);
  await ctx.addInitScript(([tk]: string[]) => { localStorage.setItem('cc_token', tk); localStorage.setItem('theme-mode', 'light'); }, [token]);
  await page.setViewportSize({ width: w, height: h });
  await page.goto(path, { waitUntil: 'domcontentloaded' });
  await page.waitForTimeout(4000);
  return { page, errs };
}

// Вычисленные значения токенов темы через пробел в той же странице: сравнение computed
// style узлов с фактическими rgb, а не со строкой переменной.
async function cssVar(page: Page, name: string) {
  return page.evaluate((n: string) => {
    const el = document.createElement('span');
    el.style.borderColor = `var(${n})`;
    el.style.color = `var(${n})`;
    el.style.background = `var(${n})`;
    document.body.appendChild(el);
    const cs = getComputedStyle(el);
    const out = { border: cs.borderTopColor, color: cs.color, bg: cs.backgroundColor };
    el.remove();
    return out;
  }, name);
}

interface NodeMeasure {
  id: string; state: string | null; proposed: string | null; selected: boolean;
  bg: string; borderStyle: string; borderWidth: string; borderColor: string;
  titleColor: string | null; clamp: string | null;
  width: number; height: number; label: boolean;
}

async function readNodes(page: Page): Promise<NodeMeasure[]> {
  return page.locator('[data-testid^="pgraph-node-"]').evaluateAll((els: Element[]) => els.map((e) => {
    const el = e as HTMLElement;
    const cs = getComputedStyle(el);
    const id = (el.getAttribute('data-testid') || '').slice('pgraph-node-'.length);
    const title = el.querySelector(`[data-testid="pgraph-title-${id}"]`) as HTMLElement | null;
    const tcs = title ? getComputedStyle(title) : null;
    const box = el.getBoundingClientRect();
    return {
      id,
      state: el.getAttribute('data-state'),
      proposed: el.getAttribute('data-proposed'),
      selected: el.getAttribute('aria-pressed') === 'true',
      bg: cs.backgroundColor,
      borderStyle: cs.borderTopStyle, borderWidth: cs.borderTopWidth, borderColor: cs.borderTopColor,
      titleColor: tcs ? tcs.color : null,
      clamp: tcs ? tcs.getPropertyValue('-webkit-line-clamp') : null,
      width: Math.round(box.width * 10) / 10, height: Math.round(box.height * 10) / 10,
      label: !!el.querySelector(`[data-testid="pgraph-proposed-${id}"]`),
    };
  }));
}

test('S0: стенд раздаёт свежий frontend/dist (sha256 index.html + строка в бандле)', async () => {
  const served = await request.get('/');
  expect(served.ok(), `GET / → ${served.status()}`).toBeTruthy();
  const servedText = await served.text();
  const local = readFileSync('../../dist/index.html', 'utf8');
  const sha = (s: string) => createHash('sha256').update(s).digest('hex');
  expect(sha(servedText), 'sha256 раздаваемого index.html == sha256 frontend/dist/index.html').toBe(sha(local));
  const m = servedText.match(/\/assets\/(index-[\w-]+\.js)/);
  expect(m, 'бандл назван в index.html').not.toBeNull();
  const bundle = await request.get(`/assets/${m![1]}`);
  expect(bundle.ok(), `GET бандл → ${bundle.status()}`).toBeTruthy();
  const body = await bundle.text();
  expect(body.includes('Фиолетовый цвет означает'), 'поясняющая строка Ф-2.42 в бандле').toBeTruthy();
  console.log('S0 ok: бандл', m![1], 'sha256 совпал');
});

test('S1: предложение @1280 — прозрачный фон узлов, фиолетовый только пунктир и заголовок; пометка, легенда и строка Ф-2.42 на месте', async ({ browser }) => {
  test.setTimeout(120_000);
  const ctx = await browser.newContext();
  const { page, errs } = await openPage(ctx, `/#/project/${PID}/feature/${EPIC_PROPOSED}`, 1280, 900);
  await page.waitForSelector('[data-testid^="pgraph-node-"]', { timeout: 30_000 });
  const plan = await cssVar(page, '--c-plan');
  const planText = await cssVar(page, '--c-plan-text');
  const planLight = await cssVar(page, '--c-plan-light');
  const accent = await cssVar(page, '--c-accent');
  const nodes = await readNodes(page);
  console.log('S1 узлы @1280:', JSON.stringify(nodes));

  expect(nodes.length, 'граф предложения содержит узлы').toBeGreaterThan(0);
  for (const n of nodes) {
    expect.soft(n.proposed, `узел ${n.id}: признак proposed`).toBe('true');
    expect.soft(n.bg, `узел ${n.id}: фон прозрачный (не залитый)`).toBe('rgba(0, 0, 0, 0)');
    expect.soft(n.borderWidth, `узел ${n.id}: рамка 1px`).toBe('1px');
    expect.soft(n.titleColor, `узел ${n.id}: заголовок — --c-plan-text`).toBe(planText.color);
    expect.soft(n.label, `узел ${n.id}: пометка «предложено»`).toBe(true);
    expect.soft(n.clamp, `узел ${n.id}: clamp заголовка 3`).toBe('3');
    expect.soft(n.width, `узел ${n.id}: ширина ≥116 (не изменилась)`).toBeGreaterThanOrEqual(116);
    expect.soft(n.height, `узел ${n.id}: высота 88 (не изменилась)`).toBe(88);
    if (n.selected) {
      // Выбранный по умолчанию узел (defaultSelected — активная стадия): рамка подсветки
      // выбора — accent solid. Это состояние выбора, правкой Ф-2.41 не трогалось.
      expect.soft(n.borderStyle, `выбранный узел ${n.id}: рамка подсветки выбора solid`).toBe('solid');
      expect.soft(n.borderColor, `выбранный узел ${n.id}: цвет подсветки выбора — accent`).toBe(accent.border);
    } else {
      expect.soft(n.borderStyle, `узел ${n.id}: рамка пунктирная`).toBe('dashed');
      expect.soft(n.borderColor, `узел ${n.id}: цвет рамки — --c-plan`).toBe(plan.border);
    }
  }

  const legend = page.locator('[data-testid="pgraph-legend-proposed"]');
  await expect.soft(legend, 'строка легенды о proposed').toBeVisible();
  await expect.soft(legend.first()).toContainText('фиолетовый пунктир — предложено, не подтверждено');

  // Точка 2 (десктоп, F05): предложение — компактная строка шапки (тон plan);
  // «Посмотреть и подтвердить» раскрывает блок, в нём — поясняющая строка под шапкой.
  await page.waitForSelector('[data-testid="feature-page-route-proposal"]', { timeout: 30_000 })
    .catch(() => console.log('S1: компактная строка предложения не появилась за 30 с'));
  const row = page.locator('[data-testid="feature-page-route-proposal"]');
  await expect.soft(row, 'компактная строка предложения видна').toBeVisible();
  const rowBg = await row.evaluate((el: Element) => getComputedStyle(el).backgroundColor);
  expect.soft(rowBg, 'тон компактной строки — planLight').toBe(planLight.bg);
  await page.locator('[data-testid="feature-page-route-proposal-action"]').click();
  const hint = page.locator('[data-testid="feature-workflow-confirm-plan-hint"]');
  await expect.soft(hint, 'строка-пояснение видна в раскрытом блоке').toBeVisible();
  expect.soft((await hint.first().innerText()).trim(), 'текст строки — ровно Ф-2.42').toBe(PLAN_HINT);
  const cardBg = await page.locator('[data-testid="feature-workflow-confirm"]').evaluate((el: Element) => getComputedStyle(el).backgroundColor);
  expect.soft(cardBg, 'тон карточки предложения — planLight').toBe(planLight.bg);
  const submit = page.locator('[data-testid="feature-workflow-confirm-submit"]');
  await expect.soft(submit, 'кнопка «Подтвердить маршрут» в раскрытом блоке').toBeVisible();

  await page.screenshot({ path: `${SHOTS}/qa5-s1-proposed-1280.png`, fullPage: true });
  console.log('S1 console errors:', JSON.stringify(errs));
  await page.close(); await ctx.close();
});

test('S2: подтверждённый @1280 и @360 — строки нет; done/active-узлы белые, proposed-признака и легенды нет; ширина узлов и шеврон не изменились', async ({ browser }) => {
  test.setTimeout(180_000);
  const stages = ROLES.map((role, i) => ({ role, presence: 'on', ...(i ? { after: [ROLES[i - 1]] } : {}) }));
  const tpl = await client.post<any>('/api/workflow-templates', { name: 'qa-e2e-qa5-full', stages });
  const tplId = tpl?.id ?? tpl?.template?.id ?? null;
  expect(tplId, 'свой шаблон создан').not.toBeNull();
  tplIds.push(tplId!);
  const epic = await client.post<any>(`/api/projects/${PID}/tasks`, { title: 'qa-e2e-qa5-confirmed-feature', role: 'feature', assignee: 'me', description: 'Подтверждённый маршрут: контроль обычных узлов.' });
  expect(epic?.id, 'эпик создан').toBeTruthy();
  epicIds.push(epic.id);
  await client.post(`/api/tasks/${epic.id}/workflow/confirm`, { templateId: tplId, scope: 'global' });
  const qaTask = await client.post<any>(`/api/projects/${PID}/tasks`, { title: 'qa-e2e-qa5-qa-task', role: 'qa', featureId: epic.id, assignee: 'me' });
  await client.put(`/api/tasks/${qaTask.id}`, { status: 'done' });

  const ctx = await browser.newContext();

  // @1280: стиль обычных узлов и отсутствие строки/пометок
  {
    const { page, errs } = await openPage(ctx, `/#/project/${PID}/feature/${epic.id}`, 1280, 900);
    await page.waitForSelector('[data-testid^="pgraph-node-"]', { timeout: 30_000 });
    const white = await cssVar(page, '--c-bg-white');
    const plan = await cssVar(page, '--c-plan');
    const nodes = await readNodes(page);
    console.log('S2 узлы @1280:', JSON.stringify(nodes));
    expect(nodes.length, 'граф подтверждённого маршрута').toBeGreaterThanOrEqual(8);
    const done = nodes.find((n) => n.state === 'done');
    expect.soft(done, 'закрытая стадия — done-узел').toBeTruthy();
    if (done) {
      expect.soft(done.proposed, 'у done-узла нет proposed-признака').toBeNull();
      expect.soft(done.bg, 'done-узел белый (как был)').toBe(white.bg);
      expect.soft(done.label, 'у done-узла нет пометки «предложено»').toBe(false);
    }
    for (const n of nodes.filter((n) => n.state === 'active')) {
      expect.soft(n.bg, `active-узел ${n.id} белый (как был)`).toBe(white.bg);
    }
    for (const n of nodes.filter((n) => n.state === 'pending' || n.state === 'absent')) {
      expect.soft(n.bg, `узел ${n.id} (${n.state}): не залитый (было так и раньше)`).toBe('rgba(0, 0, 0, 0)');
      expect.soft(n.borderColor, `узел ${n.id}: рамка не фиолетовая`).not.toBe(plan.border);
    }
    expect(nodes.every((n) => !n.label), 'пометок «предложено» нет').toBe(true);
    expect(await page.locator('[data-testid="feature-workflow-confirm-plan-hint"]').count(), 'строки-пояснения в подтверждённом нет').toBe(0);
    expect(await page.locator('[data-testid="pgraph-legend-proposed"]').count(), 'легенды proposed нет').toBe(0);
    await page.screenshot({ path: `${SHOTS}/qa5-s2-confirmed-1280.png`, fullPage: true });
    console.log('S2 @1280 console errors:', JSON.stringify(errs));
    await page.close();
  }

  // @360: ширина узлов 104, шеврон широкой ленты, done-узел белый и на мобиле, строки нет
  {
    const { page, errs } = await openPage(ctx, `/#/project/${PID}/feature/${epic.id}`, 360, 800);
    await page.waitForSelector('[data-testid^="pgraph-node-"]', { timeout: 30_000 });
    const white = await cssVar(page, '--c-bg-white');
    const nodes = await readNodes(page);
    console.log('S2 узлы @360:', JSON.stringify(nodes.map((n) => ({ id: n.id, state: n.state, w: n.width, bg: n.bg }))));
    for (const n of nodes) {
      expect.soft(n.width, `ширина узла ${n.id} на мобиле = 104 (не изменилась)`).toBe(104);
      if (n.state === 'done') expect.soft(n.bg, `done-узел ${n.id} белый и на мобиле`).toBe(white.bg);
    }
    expect(await page.locator('[data-testid="pgraph-scroll-right"]').count(), 'шеврон широкой ленты (8 узлов > 360)').toBeGreaterThan(0);
    expect(await page.locator('[data-testid="feature-workflow-confirm-plan-hint"]').count(), 'строки-пояснения на мобиле нет').toBe(0);
    await page.screenshot({ path: `${SHOTS}/qa5-s2-confirmed-360.png` });
    console.log('S2 @360 console errors:', JSON.stringify(errs));
    await page.close();
  }
  await ctx.close();
});

test('S3: «предложения нет» @1280 — строки-пояснения и proposed-оформления нет', async ({ browser }) => {
  test.setTimeout(120_000);
  const page0 = await client.get<any>(`/api/tasks/features/${EPIC_NOPROPOSAL}`);
  const run = page0?.run;
  console.log('S3 состояние: wp=', Boolean(run?.epic?.workflowProposal), 'wf=', Boolean(run?.epic?.workflow));
  expect(run?.epic?.workflowProposal ?? null, 'посев без предложения не получил автопредложения').toBeNull();
  const ctx = await browser.newContext();
  const { page, errs } = await openPage(ctx, `/#/project/${PID}/feature/${EPIC_NOPROPOSAL}`, 1280, 900);
  const hintCount = await page.locator('[data-testid="feature-workflow-confirm-plan-hint"]').count();
  const nodes = await readNodes(page).catch(() => []);
  console.log('S3 plan-hint count:', hintCount, 'узлы:', JSON.stringify(nodes.map((n) => ({ id: n.id, proposed: n.proposed, bg: n.bg, bs: n.borderStyle, bc: n.borderColor }))));
  expect.soft(hintCount, 'строки-пояснения в состоянии «предложения нет»').toBe(0);
  expect(nodes.every((n) => n.proposed !== 'true'), 'у обычных legacy-узлов нет proposed-признака').toBe(true);
  expect(nodes.every((n) => !n.label), 'пометок «предложено» нет').toBe(true);
  expect(await page.locator('[data-testid="pgraph-legend-proposed"]').count(), 'легенды proposed нет').toBe(0);
  await page.screenshot({ path: `${SHOTS}/qa5-s3-noproposal-1280.png`, fullPage: true });
  console.log('S3 console errors:', JSON.stringify(errs));
  await page.close(); await ctx.close();
});

test('S4: предложение @360 — строка в шторке, переносится, карточка в экране, кнопка ≥40; ширина узлов не изменилась', async ({ browser }) => {
  test.setTimeout(180_000);
  const ctx = await browser.newContext();
  const { page, errs } = await openPage(ctx, `/#/project/${PID}/feature/${EPIC_PROPOSED}`, 360, 800);

  // Мобильная раскладка: фиолетовый ряд + шторка по «Посмотреть».
  // Ждём данных страницы дольше фиксированного wait: на гружёном фоне (auto-recommend
  // модели после S2) 4 с не хватает — это среда, не UI (flake второго прогона).
  await page.waitForSelector('[data-testid="feature-workflow-confirm-mobile-row"]', { timeout: 30_000 })
    .catch(() => console.log('S4: ряд предложения не появился за 30 с'));
  const row = page.locator('[data-testid="feature-workflow-confirm-mobile-row"]');
  await expect.soft(row, 'ряд предложения виден').toBeVisible();
  const rowBg = await row.evaluate((el: Element) => getComputedStyle(el).backgroundColor);
  await page.locator('[data-testid="feature-workflow-confirm-mobile-open"]').click();
  const sheet = page.locator('.cc-sheet-card');
  await expect.soft(sheet, 'шторка открылась').toBeVisible();

  const hint = sheet.locator('[data-testid="feature-workflow-confirm-plan-hint"]');
  await expect.soft(hint, 'строка-пояснение видна в шторке вместе с карточкой').toBeVisible();
  expect.soft((await hint.first().innerText()).trim(), 'текст строки в шторке').toBe(PLAN_HINT);

  // Перенос строки на 360 (minor 4 из 6828f6bd: «перенос на 360 не мерян — за Верой»)
  const lines = await hint.first().evaluate((el: Element) => {
    const tn = [...el.childNodes].find((n) => n.nodeType === 3) as Text | undefined;
    if (!tn) return 0;
    const rg = document.createRange();
    rg.selectNodeContents(tn);
    const tops = new Set([...rg.getClientRects()].filter((r) => r.width > 0 && r.height > 0).map((r) => Math.round(r.top)));
    return tops.size;
  });
  console.log('S4 строка в шторке: строк переноса =', lines);
  expect.soft(lines, 'строка переносится (≥2 строк)').toBeGreaterThanOrEqual(2);
  const hb = await hint.first().boundingBox();
  expect.soft(hb!.width, 'строка не шире экрана').toBeLessThanOrEqual(360);

  const sb = await sheet.boundingBox();
  expect.soft(sb!.x + sb!.width, 'карточка шторки не вылезает за экран').toBeLessThanOrEqual(361);
  const docW = await page.evaluate(() => document.documentElement.scrollWidth);
  expect.soft(docW, 'горизонтального переполнения документа нет').toBeLessThanOrEqual(361);

  const btn = sheet.locator('[data-testid="feature-workflow-confirm-submit"]');
  await expect.soft(btn, 'кнопка «Подтвердить маршрут» доступна').toBeVisible();
  const bb = await btn.boundingBox();
  expect.soft(bb!.height, 'тач-цель кнопки ≥40').toBeGreaterThanOrEqual(40);

  // Наблюдение для minor 4: цвета самой шторки и строки в ней (оценка — в отчёте)
  const sheetBg = await sheet.evaluate((el: Element) => getComputedStyle(el).backgroundColor);
  const hintColor = await hint.first().evaluate((el: Element) => getComputedStyle(el).color);
  console.log('S4 цвета: ряд', rowBg, 'шторка', sheetBg, 'строка', hintColor);

  // Регрессия точки 1 на мобиле: ширина узлов 104, фон прозрачный, рамка фиолетовая
  const plan = await cssVar(page, '--c-plan');
  const nodes = await readNodes(page);
  console.log('S4 узлы @360:', JSON.stringify(nodes.map((n) => ({ id: n.id, w: n.width, h: n.height, bg: n.bg, bs: n.borderStyle, bc: n.borderColor, sel: n.selected }))));
  for (const n of nodes) {
    expect.soft(n.width, `ширина узла ${n.id} на мобиле = 104`).toBe(104);
    expect.soft(n.bg, `фон узла ${n.id} прозрачный`).toBe('rgba(0, 0, 0, 0)');
    if (!n.selected) {
      expect.soft(n.borderStyle, `узел ${n.id}: рамка пунктирная`).toBe('dashed');
      expect.soft(n.borderColor, `узел ${n.id}: цвет рамки — --c-plan`).toBe(plan.border);
    }
  }

  await page.screenshot({ path: `${SHOTS}/qa5-s4-proposed-360-sheet.png` });
  console.log('S4 console errors:', JSON.stringify(errs));
  await page.close(); await ctx.close();
});
