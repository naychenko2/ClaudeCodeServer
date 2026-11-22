// QA-8 · волна 5 «Опекуны и раскладка»: админская часть приёмки.
// Пункт 1 задачи — редактор опекунов (Г2, Г2-д1б): добавление/удаление опекуна и
// инвариант согласия (правка списка НЕ создаёт согласие и НЕ стирает явный отзыв).
// Пункт 3 задачи — окно настроек на телефоне (Г7, L4): разделы списком карточек,
// тап ведёт в экран раздела с «←», «←» возвращает к списку; на десктопе вкладки.
//
// Стенд: http://127.0.0.1:5500 (worktree kids-guardians, ветка feat/kids-guardians-ui).
// Фикстура (засевается в beforeAll, убирается в afterAll):
//   qa_w5_guard  — взрослый, кандидат в опекуны;
//   qa_w5_child  — ребёнок, профиль БЕЗ согласия (проверка «правка опекунов не создаёт согласие»);
//   qa_w5_child2 — ребёнок с ПУСТЫМ профилем (проверка пустого состояния — в детском спеке).

import { test, expect, type APIRequestContext } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { resolve } from 'node:path';
import {
  ADMIN_U, ADMIN_PW, CHILD_U, GUARD_U, GUARD_PW, CHILD2_U, CHILD2_PW, CHILD3_U, CHILD3_PW,
  BASE, call, tokenOf, enter, loginViaUi, openUserManagement, openChildSettings, uiProbe,
} from './kids-w5-helpers';

const SHOTS = resolve(process.cwd(), '.cc-attachments/qa/kids-w5-qa8/shots');
const ACK_LABEL = 'Я понимаю риски и беру их на себя';

let req: APIRequestContext;
let adminToken = '';
let guardId = '';
let child2Id = '';
let child3Id = '';
let mashaId = '';
let adminId = '';

test.describe.configure({ mode: 'serial' });

test.beforeAll(async ({ playwright }) => {
  mkdirSync(SHOTS, { recursive: true });
  req = await playwright.request.newContext({ baseURL: BASE });
  adminToken = await tokenOf(req, ADMIN_U, ADMIN_PW);

  const allUsers = (await call(req, 'get', '/api/users', adminToken)).body as any[];
  mashaId = allUsers.find(u => u.username === CHILD_U)?.id ?? '';
  adminId = allUsers.find(u => u.username === ADMIN_U)?.id ?? '';
  // Чистим профили от опекунов-пользователей, которых уже нет (следы прошлых прогонов):
  // такой id в списке блокирует любую правку профиля (guardian_not_found).
  const liveIds = new Set(allUsers.map(u => u.id));
  for (const pid of [mashaId]) {
    const pr = (await call(req, 'get', `/api/users/${pid}/child-profile`, adminToken)).body?.profile;
    if (!pr) continue;
    const alive = (pr.guardianUserIds ?? []).filter((g: string) => liveIds.has(g));
    if (alive.length === (pr.guardianUserIds ?? []).length) continue;
    await call(req, 'put', `/api/users/${pid}/child-profile`, adminToken, {
      enabled: pr.enabled, guardianUserIds: alive, providerKey: pr.providerKey, model: pr.model,
      spaces: pr.spaces, subjects: pr.subjects, acknowledgeRouteRisk: pr.acknowledgeRouteRisk,
    });
    console.log('QA-FIXTURE-CLEAN', pid, 'опекуны очищены');
  }
  expect(mashaId, 'ребёнок masha есть на стенде').toBeTruthy();

  // Кандидат-опекун (взрослый, local): его нет в профилях детей.
  const guardRes = await call(req, 'post', '/api/users', adminToken, {
    username: GUARD_U, password: GUARD_PW, role: 'user', executionEnvironment: 'local',
  });
  if (guardRes.status === 201) guardId = guardRes.body.id;
  else {
    const users = (await call(req, 'get', '/api/users', adminToken)).body as any[];
    guardId = users.find(u => u.username === GUARD_U)?.id ?? '';
  }

  // Ребёнок без согласия (маршрут пустой — согласие не требуется и не выдаётся).
  const c2 = await call(req, 'post', '/api/users', adminToken, {
    username: CHILD2_U, password: CHILD2_PW, role: 'user', executionEnvironment: 'container',
  });
  if (c2.status === 201) child2Id = c2.body.id;
  else {
    const users = (await call(req, 'get', '/api/users', adminToken)).body as any[];
    child2Id = users.find(u => u.username === CHILD2_U)?.id ?? '';
  }
  await call(req, 'put', `/api/users/${child2Id}/child-mode`, adminToken, { enabled: true });
  // Сначала профиль со СОГЛАСИЕМ (маршрут stub), потом — явный ОТЗЫВ согласия
  // вместе с маршрутом: так проверяется «правка опекунов не стирает явный отзыв».
  await call(req, 'put', `/api/users/${child2Id}/child-profile`, adminToken, {
    enabled: true, guardianUserIds: [], providerKey: 'stub', model: 'stub-model', spaces: {},
    subjects: [{ id: 'math', label: 'Математика', glyph: '∑', color: 'info', enabled: true }],
    acknowledgeRouteRisk: true,
  });
  await call(req, 'put', `/api/users/${child2Id}/child-profile`, adminToken, {
    enabled: true, guardianUserIds: [], providerKey: '', model: '', spaces: {},
    subjects: [{ id: 'math', label: 'Математика', glyph: '∑', color: 'info', enabled: true }],
    acknowledgeRouteRisk: false,
  });

  // Ребёнок с пустым профилем (без предметов).
  const c3 = await call(req, 'post', '/api/users', adminToken, {
    username: CHILD3_U, password: CHILD3_PW, role: 'user', executionEnvironment: 'container',
  });
  if (c3.status === 201) child3Id = c3.body.id;
  else {
    const users = (await call(req, 'get', '/api/users', adminToken)).body as any[];
    child3Id = users.find(u => u.username === CHILD3_U)?.id ?? '';
  }
  await call(req, 'put', `/api/users/${child3Id}/child-mode`, adminToken, { enabled: true });
});

test.afterAll(async () => {
  // Уборка: сначала убираем опекуна из профилей (иначе в профиле останется мёртвый id,
  // который блокирует следующие правки), потом удаляем засеянных пользователей.
  if (guardId) {
    const pr = (await call(req, 'get', `/api/users/${mashaId}/child-profile`, adminToken)).body?.profile;
    if (pr) {
      const alive = (pr.guardianUserIds ?? []).filter((g: string) => g !== guardId);
      if (alive.length !== (pr.guardianUserIds ?? []).length) {
        await call(req, 'put', `/api/users/${mashaId}/child-profile`, adminToken, {
          enabled: pr.enabled, guardianUserIds: alive, providerKey: pr.providerKey, model: pr.model,
          spaces: pr.spaces, subjects: pr.subjects, acknowledgeRouteRisk: pr.acknowledgeRouteRisk,
        });
      }
    }
  }
  for (const id of [guardId, child2Id, child3Id]) {
    if (!id) continue;
    await call(req, 'delete', `/api/users/${id}`, adminToken).catch(() => {});
  }
  await req?.dispose();
});

/** Профиль ребёнка из API (источник правды после правки окна). */
async function profileOf(childId: string) {
  const r = await call(req, 'get', `/api/users/${childId}/child-profile`, adminToken);
  return r.body?.profile ?? null;
}

/** Открыть раздел «Безопасность и согласие» в окне (десктоп — вкладка, мобиль — карточка). */
async function openSafetySection(page: any, mobile: boolean) {
  if (mobile) {
    await page.locator('[data-testid="child-settings-nav-safety"]').click({ force: true });
    await page.waitForTimeout(800);
  } else {
    await page.getByRole('tab', { name: 'Безопасность и согласие' }).click({ force: true });
    await page.waitForTimeout(800);
  }
  await expect(page.locator('[data-testid="child-safety-section"]')).toBeVisible({ timeout: 15000 });
}

// ── Пункт 1: редактор опекунов, десктоп ────────────────────────────────────────────
test('Г2: добавление и удаление опекуна в окне настроек (1280)', async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 900 });
  await loginViaUi(page, ADMIN_U, ADMIN_PW);
  await page.waitForTimeout(3000);

  await openUserManagement(page);
  await openChildSettings(page, CHILD_U);

  const before = await profileOf(mashaId);
  console.log('QA-G2-BEFORE', JSON.stringify({
    ack: before?.acknowledgeRouteRisk, ackAt: before?.routeRiskAcknowledgedAt,
    guardians: before?.guardianUserIds,
  }));

  await openSafetySection(page, false);

  // Кандидаты: qa_w5_guard (новый взрослый) — он в списке выбора.
  const addBox = page.locator('[data-testid="guardian-add"]');
  expect(await addBox.count(), 'блок добавления опекуна есть').toBeGreaterThan(0);
  await page.screenshot({ path: resolve(SHOTS, 'g2-safety-1280.png'), fullPage: true });

  // Выбираем кандидата в Select и жмём «Добавить».
  const select = addBox.locator('select, [role="combobox"]').first();
  const selects = await addBox.locator('select').count();
  console.log('QA-G2-SELECT-KIND', selects);
  if (selects > 0) {
    const opts = await select.locator('option').allTextContents();
    console.log('QA-G2-OPTIONS', JSON.stringify(opts));
    const opt = opts.find(o => o.includes(GUARD_U));
    expect(opt, `кандидат ${GUARD_U} есть в списке`).toBeTruthy();
    await select.selectOption({ label: opt! });
  } else {
    // кастомный Select: раскрыть и выбрать по тексту
    await select.click({ force: true });
    await page.waitForTimeout(600);
    await page.getByText(GUARD_U, { exact: false }).first().click({ force: true });
  }
  await page.waitForTimeout(400);
  await page.locator('[data-testid="guardian-add-btn"]').click({ force: true });
  await page.waitForTimeout(600);
  const rowAfterAdd = page.locator(`[data-testid="guardian-${guardId}"]`);
  expect(await rowAfterAdd.count(), 'строка опекуна появилась в черновике').toBeGreaterThan(0);
  await page.screenshot({ path: resolve(SHOTS, 'g2-added-1280.png'), fullPage: true });

  // Сохраняем и сверяем живой профиль.
  await page.locator('[data-testid="child-settings-save"]').click({ force: true });
  await page.waitForTimeout(2500);
  const after = await profileOf(mashaId);
  console.log('QA-G2-PROFILE-AFTER-ADD', JSON.stringify({
    guardians: after?.guardianUserIds, ack: after?.acknowledgeRouteRisk,
    ackAt: after?.routeRiskAcknowledgedAt,
  }));
  expect(after?.guardianUserIds ?? [], 'опекун сохранён в живом профиле').toContain(guardId);
  // Инвариант согласия: правка списка опекунов согласия не трогает (ни создаёт, ни снимает).
  expect(after?.acknowledgeRouteRisk, 'согласие осталось как было (не создано правкой списка)').toBe(before?.acknowledgeRouteRisk);
  expect(after?.routeRiskAcknowledgedAt ?? null, 'момент согласия не сдвинут').toBe(before?.routeRiskAcknowledgedAt ?? null);
});

// ── Пункт 1: инвариант согласия (Г2-д1б) ───────────────────────────────────────────
test('Г2-д1б: правка опекунов не создаёт согласие и не стирает явный отзыв', async ({ page }) => {
  // Профиль ребёнка с ЯВНЫМ ОТЗЫВОМ (согласие было — отозвали в фикстуре):
  // правка опекунов не должна вернуть согласие.
  const before = await profileOf(child2Id);
  console.log('QA-G2B-BEFORE', JSON.stringify({
    ack: before?.acknowledgeRouteRisk, ackAt: before?.routeRiskAcknowledgedAt,
    guardians: before?.guardianUserIds,
  }));
  expect(before?.acknowledgeRouteRisk, 'согласие отозвано').toBe(false);
  expect(before?.routeRiskAcknowledgedAt ?? null, 'после отзыва момент согласия обнулён').toBeNull();

  await page.setViewportSize({ width: 1280, height: 900 });
  await loginViaUi(page, ADMIN_U, ADMIN_PW);
  await page.goto('/#/home');
  await page.waitForTimeout(6000);
  console.log('QA-DEBUG-LS', JSON.stringify(await page.evaluate(() => Object.keys(localStorage))));
  await openUserManagement(page);
  await openChildSettings(page, CHILD2_U);
  await openSafetySection(page, false);

  // Шаг 1: добавить опекуна и сохранить — правка списка НЕ должна выдать согласие.
  const addBox = page.locator('[data-testid="guardian-add"]');
  expect(await addBox.count(), 'блок добавления опекуна есть').toBeGreaterThan(0);
  const select = addBox.locator('select').first();
  if (await addBox.locator('select').count() > 0) {
    const opts = await select.locator('option').allTextContents();
    const opt = opts.find(o => o.includes(GUARD_U));
    expect(opt, `кандидат ${GUARD_U} есть в списке`).toBeTruthy();
    await select.selectOption({ label: opt! });
  } else {
    await select.click({ force: true });
    await page.waitForTimeout(600);
    await page.getByText(GUARD_U, { exact: false }).first().click({ force: true });
  }
  await page.locator('[data-testid="guardian-add-btn"]').click({ force: true });
  await page.waitForTimeout(600);
  await page.locator('[data-testid="child-settings-save"]').click({ force: true });
  await page.waitForTimeout(2500);

  const mid = await profileOf(child2Id);
  console.log('QA-G2B-AFTER-ADD', JSON.stringify({
    ack: mid?.acknowledgeRouteRisk, ackAt: mid?.routeRiskAcknowledgedAt,
    guardians: mid?.guardianUserIds,
  }));
  expect(mid?.guardianUserIds ?? [], 'опекун добавлен').toContain(guardId);
  expect(mid?.acknowledgeRouteRisk, 'правка опекунов НЕ создала согласие').toBe(false);
  expect(mid?.routeRiskAcknowledgedAt ?? null, 'момент согласия не появился').toBeNull();

  // Шаг 2: убрать опекуна — согласие не должно появиться и тут.
  const removeBtn = page.locator(`[data-testid="guardian-remove-${guardId}"]`);
  expect(await removeBtn.count(), 'кнопка «Убрать» есть у строки опекуна').toBeGreaterThan(0);
  await removeBtn.click({ force: true });
  await page.waitForTimeout(400);
  await page.locator('[data-testid="child-settings-save"]').click({ force: true });
  await page.waitForTimeout(2500);

  const after = await profileOf(child2Id);
  console.log('QA-G2B-AFTER', JSON.stringify({
    ack: after?.acknowledgeRouteRisk, ackAt: after?.routeRiskAcknowledgedAt,
    guardians: after?.guardianUserIds,
  }));
  expect(after?.guardianUserIds ?? [], 'опекун убран из живого профиля').not.toContain(guardId);
  expect(after?.acknowledgeRouteRisk, 'явный отзыв согласия не стирается правкой списка').toBe(false);
  expect(after?.routeRiskAcknowledgedAt ?? null, 'момент согласия не появился').toBeNull();
});

// ── Пункт 3: окно настроек на телефоне (разделы списком) ───────────────────────────
test('Г7/L4: на 360 разделы окна — списком карточек, «←» возвращает к списку', async ({ page }) => {
  await page.setViewportSize({ width: 360, height: 900 });
  await loginViaUi(page, ADMIN_U, ADMIN_PW);
  await page.goto('/#/home');
  await page.waitForTimeout(6000);
  console.log('QA-DEBUG-LS', JSON.stringify(await page.evaluate(() => Object.keys(localStorage))));
  await openUserManagement(page);
  await openChildSettings(page, CHILD_U);

  // Список разделов, а не вкладки.
  const navList = page.locator('[data-testid="child-settings-nav-list"]');
  expect(await navList.count(), 'на мобиле окно открывается списком разделов').toBeGreaterThan(0);
  expect(await page.getByRole('tab').count(), 'на мобиле вкладок разделов нет').toBe(0);
  await page.screenshot({ path: resolve(SHOTS, 'l4-navlist-360.png'), fullPage: true });

  // Тач-цели карточек — не меньше 40 px.
  const navSizes = await page.locator('[data-testid^="child-settings-nav-"]').evaluateAll(
    els => els.map(e => { const r = e.getBoundingClientRect(); return { id: e.getAttribute('data-testid'), w: Math.round(r.width), h: Math.round(r.height) }; }));
  console.log('QA-L4-NAV-SIZES', JSON.stringify(navSizes));
  for (const s of navSizes) expect(s.h, `карточка ${s.id} — высота`).toBeGreaterThanOrEqual(40);

  // Тап по разделу → экран раздела с кнопкой «←».
  await page.locator('[data-testid="child-settings-nav-safety"]').click({ force: true });
  await page.waitForTimeout(900);
  expect(await page.locator('[data-testid="child-safety-section"]').count(), 'открылся экран раздела').toBeGreaterThan(0);
  const backBar = page.locator('[data-testid="child-settings-back-bar"]');
  expect(await backBar.count(), 'в экране раздела есть полоса «←»').toBeGreaterThan(0);
  await page.screenshot({ path: resolve(SHOTS, 'l4-section-360.png'), fullPage: true });

  // «←» возвращает к списку разделов.
  await page.locator('[aria-label="К разделам"]').click({ force: true });
  await page.waitForTimeout(900);
  expect(await page.locator('[data-testid="child-settings-nav-list"]').count(), '«←» вернул к списку разделов').toBeGreaterThan(0);
  expect(await page.locator('[data-testid="child-safety-section"]').count(), 'экран раздела закрыт').toBe(0);
  await page.screenshot({ path: resolve(SHOTS, 'l4-back-360.png'), fullPage: true });
});

// ── Пункт 3: десктоп остался на вкладках ───────────────────────────────────────────
test('Г7: на 1280 разделы окна остались вкладками', async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 900 });
  await loginViaUi(page, ADMIN_U, ADMIN_PW);
  await page.goto('/#/home');
  await page.waitForTimeout(6000);
  console.log('QA-DEBUG-LS', JSON.stringify(await page.evaluate(() => Object.keys(localStorage))));
  await openUserManagement(page);
  await openChildSettings(page, CHILD_U);
  const tabs = page.getByRole('tab');
  expect(await tabs.count(), 'на десктопе вкладки разделов есть').toBeGreaterThan(0);
  expect(await page.locator('[data-testid="child-settings-nav-list"]').count(), 'списка разделов на десктопе нет').toBe(0);
  await page.screenshot({ path: resolve(SHOTS, 'g7-tabs-1280.png'), fullPage: true });
});

// ── Пункт 3 (хвост): отказ загрузки профиля на мобильном — не пустой экран ──────────
test('L4-хвост: отказ чтения профиля на мобиле виден (не пустой экран)', async ({ page }) => {
  await page.setViewportSize({ width: 360, height: 900 });
  await loginViaUi(page, ADMIN_U, ADMIN_PW);
  // Список пользователей и его «Настроить занятия» грузятся БЕЗ мока: снимок детского
  // режима нужен, чтобы кнопка входа в окно появилась.
  await openUserManagement(page);
  // Теперь роняем чтение профиля — окно должно показать отказ, а не пустой экран.
  await page.route('**/api/users/*/child-profile', route => route.fulfill({
    status: 500, contentType: 'application/json', body: JSON.stringify({ message: 'стенд: профиль недоступен' }),
  }));
  await openChildSettings(page, CHILD_U);
  await page.waitForTimeout(2500);
  const text = await page.locator('.cc-modal-card, .cc-sheet-card').last().innerText().catch(() => '');
  console.log('QA-L4-FAIL-TEXT', JSON.stringify(text.slice(0, 300)));
  expect(text.trim().length, 'экран не пустой: виден текст отказа').toBeGreaterThan(10);
  await page.screenshot({ path: resolve(SHOTS, 'l4-loadfail-360.png'), fullPage: true });
});
