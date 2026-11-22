// QA-9 · повторная приёмка двух дефектов раскладки, найденных QA-8 и починенных в Г5-д1.
// c7082105 — на 360 шапка урока сжимала имя репетитора (фикс 481ec90cc: перенос вместо ellipsis).
// 68812ebd — на 768 у ребёнка не было списка уроков (фикс a0d13e51d: дефолтный дровер 'chats').
// Плюс регрессия: взрослый на 768 — никакая панель не открывается сама.
//
// Стенд: тот же, что у QA-8 (:5500, worktree kids-guardians, ветка feat/kids-guardians-ui).
// Фикстуры: masha (предметы math/liter, в math есть урок) — живые данные стенда;
// взрослый путь — проект «QA-W5 взрослый путь» (создаётся, если его нет).

import { test, expect, type APIRequestContext, type Page } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { resolve } from 'node:path';
import { ADMIN_U, ADMIN_PW, BASE, call, tokenOf, loginViaUi } from './kids-w5-helpers';

const SHOTS = resolve(process.cwd(), '../../../.cc-attachments/qa/kids-w5-qa9/shots');
const CHILD_U = 'masha';
const CHILD_PW = 'dev-master';
const ADULT_PROJECT_NAME = 'QA-W5 взрослый путь';

let req: APIRequestContext;
let adminToken = '';
let adultProjectId = '';
let createdAdultProject = false;

test.beforeAll(async ({ playwright }) => {
  mkdirSync(SHOTS, { recursive: true });
  req = await playwright.request.newContext({ baseURL: BASE });
  adminToken = await tokenOf(req, ADMIN_U, ADMIN_PW);
  // Проект для взрослого пути: берём живой (тот же, что у QA-8), создаём, если его снесли.
  const list = (await call(req, 'get', '/api/projects', adminToken)).body as any[];
  const existing = list.find(p => p.name === ADULT_PROJECT_NAME);
  if (existing) {
    adultProjectId = existing.id;
  } else {
    const proj = await call(req, 'post', '/api/projects', adminToken, {
      name: ADULT_PROJECT_NAME, rootPath: '/home/an/Sources/qa-kids-w5-projects/qa-w5-adult', createDirectory: true,
    });
    expect(proj.status, 'проект взрослого пути создан').toBeLessThan(300);
    adultProjectId = proj.body.id;
    createdAdultProject = true;
  }
  console.log('QA-QA9-ADULT-PROJECT', JSON.stringify({ adultProjectId, createdAdultProject }));
});

test.afterAll(async () => {
  if (createdAdultProject && adultProjectId) {
    await call(req, 'delete', `/api/projects/${adultProjectId}`, adminToken).catch(() => {});
  }
  await req?.dispose();
});

/** Открыть проект math у ребёнка на данной ширине (чистый вход → пикер → плитка). */
async function openMathProject(page: Page, width: number) {
  await page.setViewportSize({ width, height: 900 });
  await loginViaUi(page, CHILD_U, CHILD_PW, true);
  await expect(page.locator('[data-testid="child-subject-picker"]')).toBeVisible({ timeout: 20000 });
  await page.locator('[data-testid="child-subject-tile-math"]').click({ force: true });
  await page.waitForTimeout(3500);
}

/**
 * Живой замер имени репетитора в шапке урока (c7082105): узкийший текстовый узел с именем —
 * его clientWidth/scrollWidth (обрезание), его прямоугольник против шапки,
 * и размеры ряда шапки (не схлопнута/не вылезла ли).
 */
async function measureTutorName(page: Page, name: string) {
  return page.evaluate((n) => {
    const header = document.querySelector('[data-testid="child-lesson-header"]') as HTMLElement | null;
    if (!header) return { found: false, reason: 'шапки урока нет' };
    const hr = header.getBoundingClientRect();
    const els = Array.from(header.querySelectorAll('*')) as HTMLElement[];
    const hits = els.filter(e => (e.textContent || '').includes(n) && e.children.length === 0);
    if (hits.length === 0) return { found: false, reason: 'имени в шапке нет' };
    const el = hits[0];
    const r = el.getBoundingClientRect();
    // Ряд шапки — верхний flex-контейнер header'а (первый элемент-ребёнок).
    const row = header.firstElementChild as HTMLElement | null;
    const rr = row?.getBoundingClientRect();
    return {
      found: true,
      text: (el.textContent || '').trim().slice(0, 80),
      clientWidth: el.clientWidth, scrollWidth: el.scrollWidth,
      clipped: el.scrollWidth > el.clientWidth + 1,
      elLeft: Math.round(r.left), elRight: Math.round(r.right), elTop: Math.round(r.top), elBottom: Math.round(r.bottom),
      headerLeft: Math.round(hr.left), headerRight: Math.round(hr.right), headerWidth: Math.round(hr.width), headerHeight: Math.round(hr.height),
      insideHeader: r.left >= hr.left - 1 && r.right <= hr.right + 1 && r.top >= hr.top - 1 && r.bottom <= hr.bottom + 1,
      rowHeight: rr ? Math.round(rr.height) : null, rowWidth: rr ? Math.round(rr.width) : null,
    };
  }, name);
}

/** Состояние панелей компактной зоны: открытый дровер (карточка с «Скрыть панель») и детская панель. */
async function panelsState(page: Page) {
  return page.evaluate(() => {
    const closeBtns = Array.from(document.querySelectorAll('[title="Скрыть панель"]')) as HTMLElement[];
    const visible = closeBtns.filter(b => {
      const r = b.getBoundingClientRect();
      return r.width > 0 && r.height > 0;
    });
    const panel = document.querySelector('[data-testid="child-lessons-panel"]') as HTMLElement | null;
    const pr = panel?.getBoundingClientRect();
    return {
      openPanels: visible.length,
      childPanel: pr ? { left: Math.round(pr.left), width: Math.round(pr.width), height: Math.round(pr.height) } : null,
      childPanelTestid: panel ? 'child-lessons-panel' : null,
    };
  });
}

// ── Дефект c7082105: 360, шапка урока и имя репетитора ─────────────────────────────

test('c7082105: на 360 имя репетитора в шапке урока видно целиком, ряд целой', async ({ page }) => {
  const bad = badResponsesOf(page);
  await openMathProject(page, 360);
  const row = page.locator('[data-testid="child-lesson-row"]').first();
  expect(await row.count(), 'список уроков на 360 есть').toBeGreaterThan(0);
  await row.click({ force: true });
  await page.waitForTimeout(3000);

  const header = page.locator('[data-testid="child-lesson-header"]');
  await expect(header, 'шапка урока отрисована').toBeVisible({ timeout: 15000 });

  const m = await measureTutorName(page, 'Репетитор: Математика');
  console.log('QA-C7082105-MEASURE', JSON.stringify(m));
  expect(m.found, 'имя репетитора есть в шапке').toBe(true);
  expect(m.clipped, `обрезания нет (clientWidth ${m.clientWidth} >= scrollWidth ${m.scrollWidth})`).toBe(false);
  expect(m.text, 'текст имени полный').toContain('Репетитор: Математика');
  expect(m.insideHeader, 'имя не вылезает за пределы шапки').toBe(true);

  // Ряд шапки целой: рядом «назад», шеврон, стоп-сигнал; сам ряд на ширину экрана.
  expect(await page.locator('[aria-label="К списку уроков"]').count(), '«назад» на месте').toBeGreaterThan(0);
  expect(await page.locator('[aria-label="Мне неприятно — позвать взрослого"]').count(), 'стоп-сигнал на месте').toBeGreaterThan(0);
  expect(m.headerWidth, 'шапка на ширину экрана').toBeGreaterThanOrEqual(358);
  expect(m.headerWidth, 'шапка не вылезла за экран').toBeLessThanOrEqual(360);
  console.log('QA-C7082105-BAD', JSON.stringify(bad));
  await page.screenshot({ path: resolve(SHOTS, 'c7082105-header-360.png'), fullPage: true });

  // Мутация: вернуть элементу прежний стиль (nowrap + ellipsis) — замер обязан покраснеть
  // (имя снова режется). Без этого тест ничего не проверяет.
  const mutated = await page.evaluate((n) => {
    const header = document.querySelector('[data-testid="child-lesson-header"]') as HTMLElement;
    const el = (Array.from(header.querySelectorAll('*')) as HTMLElement[])
      .find(e => (e.textContent || '').includes(n) && e.children.length === 0)!;
    el.style.whiteSpace = 'nowrap'; el.style.overflow = 'hidden'; el.style.textOverflow = 'ellipsis';
    return { clientWidth: el.clientWidth, scrollWidth: el.scrollWidth, clipped: el.scrollWidth > el.clientWidth + 1 };
  }, 'Репетитор: Математика');
  console.log('QA-C7082105-MUTATION', JSON.stringify(mutated));
  expect(mutated.clipped, 'на прежнем стиле имя снова режется — замер чувствителен').toBe(true);
});

function badResponsesOf(page: Page): string[] {
  const out: string[] = [];
  page.on('response', r => { if (r.status() >= 400) out.push(`${r.status()} ${r.url()}`); });
  return out;
}

// ── Дефект 68812ebd: 768, список уроков у ребёнка ─────────────────────────────────

test('68812ebd: на 768 список уроков виден при входе в проект', async ({ page }) => {
  await openMathProject(page, 768);

  const panel = page.locator('[data-testid="child-lessons-panel"]');
  await expect(panel, 'панель уроков открыта при входе в проект на 768').toBeVisible({ timeout: 15000 });
  const rows = await page.locator('[data-testid="child-lesson-row"]').count();
  const st = await panelsState(page);
  console.log('QA-68812EBD-768', JSON.stringify({ rows, ...st }));
  expect(rows, 'строки уроков видны').toBeGreaterThan(0);
  expect(st.openPanels, 'дровер открыт (его карточка с „Скрыть панель“)').toBeGreaterThan(0);
  await page.screenshot({ path: resolve(SHOTS, '68812ebd-768-enter.png'), fullPage: true });

  // Формальная проверка фразы «клик по глифу открывает список» в штатном состоянии:
  // список уже открыт — клик по глифу активного предмета не должен его ломать.
  const glyph = page.locator('[data-child-subject="math"]').first();
  expect(await glyph.count(), 'глиф предмета в рельсе есть').toBeGreaterThan(0);
  await glyph.evaluate((el: HTMLElement) => el.click());
  await page.waitForTimeout(2500);
  const afterGlyph = await panelsState(page);
  console.log('QA-68812EBD-768-GLYPH-OPEN', JSON.stringify(afterGlyph));
  expect(afterGlyph.childPanel, 'клик по глифу не сломал открытый список').not.toBeNull();
});

test('68812ebd: на 768 закрытый дровер открывается кнопкой «Чаты» в рельсе', async ({ page }) => {
  await openMathProject(page, 768);
  await expect(page.locator('[data-testid="child-lessons-panel"]')).toBeVisible({ timeout: 15000 });

  // Закрыть дровер человеком — потом открыть его штатным путём: кнопкой панели в рельсе.
  const closeBtn = page.locator('[title="Скрыть панель"]').first();
  expect(await closeBtn.count(), 'кнопка закрытия панели есть').toBeGreaterThan(0);
  await closeBtn.evaluate((el: HTMLElement) => el.click());
  await page.waitForTimeout(1500);
  const closed = await panelsState(page);
  console.log('QA-68812EBD-768-CLOSED', JSON.stringify(closed));
  expect(closed.childPanel, 'после закрытия панели нет').toBeNull();

  // Наблюдение (не утверждение): клик по глифу АКТИВНОГО предмета — открывает ли список?
  // Глиф по назначению открывает предмет (проект), а не список; фиксируем факт.
  const glyph = page.locator('[data-child-subject="math"]');
  expect(await glyph.count(), 'глиф предмета в рельсе есть').toBeGreaterThan(0);
  await glyph.first().evaluate((el: HTMLElement) => el.click());
  await page.waitForTimeout(2500);
  const afterGlyph = await panelsState(page);
  console.log('QA-68812EBD-768-GLYPH', JSON.stringify(afterGlyph));

  // Штатный путь возврата к списку: кнопка панели «Чаты» в рельсе.
  const chatsBtn = page.locator('[aria-label="Чаты"]').first();
  expect(await chatsBtn.count(), 'кнопка «Чаты» в рельсе есть').toBeGreaterThan(0);
  await chatsBtn.evaluate((el: HTMLElement) => el.click());
  await page.waitForTimeout(2500);
  const after = await panelsState(page);
  console.log('QA-68812EBD-768-CHATS-BTN', JSON.stringify(after));
  await page.screenshot({ path: resolve(SHOTS, '68812ebd-768-after-glyph.png'), fullPage: true });
  expect(after.childPanel, 'кнопка «Чаты» открыла список уроков').not.toBeNull();
});

test('68812ebd: на 360 список уроков не сломан (телефон)', async ({ page }) => {
  await openMathProject(page, 360);
  const panel = page.locator('[data-testid="child-lessons-panel"]');
  await expect(panel, 'панель уроков видна на 360').toBeVisible({ timeout: 15000 });
  const rows = await page.locator('[data-testid="child-lesson-row"]').count();
  console.log('QA-68812EBD-360-ROWS', rows);
  expect(rows, 'строки уроков видны на 360').toBeGreaterThan(0);
  await page.screenshot({ path: resolve(SHOTS, '68812ebd-360.png'), fullPage: true });
});

test('68812ebd: на 1280 список уроков не сломан (десктоп)', async ({ page }) => {
  await openMathProject(page, 1280);
  const panel = page.locator('[data-testid="child-lessons-panel"]');
  await expect(panel, 'панель уроков видна на 1280').toBeVisible({ timeout: 15000 });
  const rows = await page.locator('[data-testid="child-lesson-row"]').count();
  console.log('QA-68812EBD-1280-ROWS', rows);
  expect(rows, 'строки уроков видны на 1280').toBeGreaterThan(0);
  await page.screenshot({ path: resolve(SHOTS, '68812ebd-1280.png'), fullPage: true });
});

// ── Регрессия: взрослый на 768 ─────────────────────────────────────────────────────

test('регрессия: взрослый на 768 — никакая панель не открывается сама', async ({ page }) => {
  await page.setViewportSize({ width: 768, height: 900 });
  await loginViaUi(page, ADMIN_U, ADMIN_PW, true);
  await page.goto('/#/projects');
  await page.waitForTimeout(4000);
  await page.getByText(ADULT_PROJECT_NAME, { exact: false }).first().click({ force: true });
  await page.waitForTimeout(4000);
  expect(page.url(), 'проект открылся').toContain('/project/');

  const st = await panelsState(page);
  console.log('QA-ADULT-768', JSON.stringify(st));
  expect(st.childPanel, 'детской панели взрослому нет').toBeNull();
  expect(st.openPanels, 'ни одна панель не открыта сама').toBe(0);
  await page.screenshot({ path: resolve(SHOTS, 'adult-768.png'), fullPage: true });
});
