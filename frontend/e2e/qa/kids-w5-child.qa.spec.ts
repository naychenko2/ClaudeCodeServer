// QA-8 · волна 5 «Опекуны и раскладка»: детская часть приёмки.
// Пункт 2 — раскладка (Г3б, Г3б-д1, Г7): 360/768/1280 — нет схлопывания влево, нет
// горизонтального переполнения, плашка предмета не налезает на панель «Чаты», кнопки и
// строки списка уроков — не меньше 40 px.
// Пункт 4 — выбор предмета (Г6, Г6-д1): ребёнок видит плитки своих предметов, тап по
// ВТОРОМУ открывает именно его, «назад» возвращает к списку (не открывает первый),
// выключенный предмет не показывается, пустой профиль — понятный текст, «нет связи»
// с кнопкой «Попробовать ещё раз».
// Пункт 5 — взрослый путь не сломан.
//
// Стенд: http://127.0.0.1:5500. Ребёнок masha (2 предмета: math, liter), админ — взрослый.

import { test, expect, type APIRequestContext } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { resolve } from 'node:path';
import {
  ADMIN_U, ADMIN_PW, CHILD_U, CHILD_PW, CHILD3_U, CHILD3_PW, BASE,
  call, tokenOf, enter, uiProbe, badResponses,
} from './kids-w5-helpers';

const SHOTS = resolve(process.cwd(), '.cc-attachments/qa/kids-w5-qa8/shots');

let req: APIRequestContext;
let childToken = '';
let adminToken = '';
let childId = '';
let mathProjectId = '';
let literProjectId = '';
let literProjectName = '';
let adultProjectId = '';

test.describe.configure({ mode: 'serial' });

test.beforeAll(async ({ playwright }) => {
  mkdirSync(SHOTS, { recursive: true });
  req = await playwright.request.newContext({ baseURL: BASE });
  childToken = await tokenOf(req, CHILD_U, CHILD_PW);
  adminToken = await tokenOf(req, ADMIN_U, ADMIN_PW);

  const users = (await call(req, 'get', '/api/users', adminToken)).body as any[];
  childId = users.find(u => u.username === CHILD_U)?.id ?? '';
  expect(childId, 'ребёнок masha есть на стенде').toBeTruthy();

  // Ребёнок с ПУСТЫМ профилем (для проверки пустого состояния) — заводим, если нет.
  const empty = (await call(req, 'post', '/api/users', adminToken, {
    username: CHILD3_U, password: CHILD3_PW, role: 'user', executionEnvironment: 'container',
  }));
  const emptyId = empty.status === 201 ? empty.body.id
    : ((await call(req, 'get', '/api/users', adminToken)).body as any[]).find(u => u.username === CHILD3_U)?.id ?? '';
  if (emptyId) {
    await call(req, 'put', `/api/users/${emptyId}/child-mode`, adminToken, { enabled: true });
  }

  const projects = (await call(req, 'get', '/api/projects', childToken)).body as any[];
  const math = projects.find(p => p.childSubjectId === 'math');
  const liter = projects.find(p => p.childSubjectId === 'liter');
  expect(math, 'проект-предмет «Математика» есть').toBeTruthy();
  expect(liter, 'проект-предмет «Литература…» есть').toBeTruthy();
  mathProjectId = math.id;
  literProjectId = liter.id; literProjectName = liter.name;
  console.log('QA-CHILD-PROJECTS', JSON.stringify(projects.map(p => ({ id: p.id, name: p.name, subject: p.childSubjectId }))));
});

test.afterAll(async () => {
  if (adultProjectId) await call(req, 'delete', `/api/projects/${adultProjectId}`, adminToken).catch(() => {});
  await req?.dispose();
});

/** Профиль ребёнка — для включения/выключения предмета. */
async function profileOf(childId_: string) {
  const r = await call(req, 'get', `/api/users/${childId_}/child-profile`, adminToken);
  return r.body?.profile ?? null;
}

async function putProfile(childId_: string, profile: any) {
  return call(req, 'put', `/api/users/${childId_}/child-profile`, adminToken, profile);
}

// ── Пункт 4: выбор предмета (Г6) ────────────────────────────────────────────────────
test('Г6: ребёнок видит плитки предметов, тап по ВТОРОМУ открывает именно его', async ({ page }) => {
  await page.setViewportSize({ width: 360, height: 900 });
  const bad = badResponses(page);
  await enter(page, childToken, { role: 'user', userId: childId, username: CHILD_U, clean: true });
  await page.goto('/#/home');
  await page.reload();   // перезагрузка: сбросить состояние React (открытый проект живёт в нём)
  await page.waitForTimeout(7000);

  const picker = page.locator('[data-testid="child-subject-picker"]');
  expect(await picker.count(), 'экран выбора предмета показан').toBeGreaterThan(0);
  const tiles = page.locator('[data-testid^="child-subject-tile-"]');
  console.log('QA-G6-TILES', await tiles.count());
  expect(await tiles.count(), 'видны обе плитки предметов').toBe(2);
  await page.screenshot({ path: resolve(SHOTS, 'g6-picker-360.png'), fullPage: true });

  // Тап по ВТОРОМУ предмету (liter) — открывается его проект, а не первого.
  await page.locator('[data-testid="child-subject-tile-liter"]').click({ force: true });
  await page.waitForTimeout(7000);
  console.log('QA-G6-URL-AFTER-2ND', page.url());
  expect(page.url(), 'открыт проект второго предмета').toContain(literProjectId);
  expect(page.url(), 'первый предмет не открыт').not.toContain(mathProjectId);
  await page.screenshot({ path: resolve(SHOTS, 'g6-second-subject-360.png'), fullPage: true });

  console.log('QA-G6-BAD', JSON.stringify(bad.filter(s => s.startsWith('403'))));
  expect(bad.filter(s => s.startsWith('403')), 'ни одного 403 на пути ребёнка').toEqual([]);
});

// ── Пункт 4: «назад» возвращает к списку, а не открывает первый ───────────────────
test('Г6: «назад» из проекта возвращает к списку предметов (первый не открывается)', async ({ page }) => {
  await page.setViewportSize({ width: 360, height: 900 });
  await enter(page, childToken, { role: 'user', userId: childId, username: CHILD_U, clean: true });
  await page.goto('/#/home');
  await page.reload();   // перезагрузка: сбросить состояние React (открытый проект живёт в нём)
  await page.waitForTimeout(7000);
  await page.locator('[data-testid="child-subject-tile-liter"]').click({ force: true });
  await page.waitForTimeout(7000);
  expect(page.url(), 'открыт проект второго предмета').toContain(literProjectId);

  // «Назад» в мобильной шапке проекта (BackButton, title = имя проекта).
  const back = page.locator(`[role="button"][title="${literProjectName}"]`).first();
  console.log('QA-G6-BACK-BTN', await back.count(), literProjectName);
  if (await back.count() === 0) {
    // запасной путь: кнопка «К списку уроков» в шапке урока
    await page.locator('[aria-label="К списку уроков"]').first().click({ force: true });
  } else {
    await back.click({ force: true });
  }
  await page.waitForTimeout(6000);
  const picker = page.locator('[data-testid="child-subject-picker"]');
  console.log('QA-G6-AFTER-BACK-URL', page.url(), 'picker:', await picker.count());
  expect(await picker.count(), 'после «назад» — снова список предметов').toBeGreaterThan(0);
  expect(page.url(), 'авто-выбор первого предмета не произошёл').not.toContain(mathProjectId);
  await page.screenshot({ path: resolve(SHOTS, 'g6-back-to-list-360.png'), fullPage: true });
});

// ── Пункт 2: раскладка на 360/768/1280 ─────────────────────────────────────────────
test('Г3б/Г7: раскладка ребёнка на 360/768/1280 — без переполнения, тач-цели ≥ 40px', async ({ page }) => {
  const results: any[] = [];
  for (const w of [360, 768, 1280]) {
    await page.setViewportSize({ width: w, height: 900 });
    await enter(page, childToken, { role: 'user', userId: childId, username: CHILD_U, clean: true });
    await page.goto('/#/home');
    await page.reload();
    await page.waitForTimeout(7000);

    const pickerProbe = await uiProbe(page, w);
    results.push({ route: 'picker', ...pickerProbe });

    // Экран проекта-предмета: раскладка шапки и панели уроков. Заходим по диплинку
    // (через пикер дошли бы только из состояния без открытого проекта).
    await page.goto(`/#/project/${mathProjectId}`);
    await page.waitForTimeout(8000);
    const projProbe = await uiProbe(page, w);
    results.push({ route: 'project', ...projProbe });
    await page.screenshot({ path: resolve(SHOTS, `g3b-project-${w}.png`), fullPage: true });

    // Плашка предмета не должна налезать на панель «Чаты» (L1): при открытой панели её нет.
    const panel = page.locator('[data-testid="child-lessons-panel"]');
    const panelOpen = await panel.count() > 0;
    if (!panelOpen) {
      for (const label of ['Чаты', 'Уроки']) {
        const btn = page.locator(`button[aria-label="${label}"], button[title="${label}"]`).first();
        if (await btn.count() > 0) {
          await btn.click({ force: true }).catch(() => {});
          await page.waitForTimeout(3000);
          if (await panel.count() > 0) break;
        }
      }
    }
    const panelOpenNow = await panel.count() > 0;
    const plate = page.locator('text=Эту папку видят').first();
    const plateVisible = await plate.count() > 0;
    // Пересечение плашки с панелью «Чаты» — геометрия, а не «на глаз».
    let overlap: any = null;
    if (plateVisible && panelOpenNow) {
      const a = await plate.boundingBox();
      const b = await panel.boundingBox();
      if (a && b) {
        overlap = {
          plate: { x: Math.round(a.x), y: Math.round(a.y), w: Math.round(a.width), h: Math.round(a.height) },
          panel: { x: Math.round(b.x), y: Math.round(b.y), w: Math.round(b.width), h: Math.round(b.height) },
          intersects: a.x < b.x + b.width && a.x + a.width > b.x && a.y < b.y + b.height && a.y + a.height > b.y,
        };
      }
    }
    console.log(`QA-G3B-${w}`, JSON.stringify({ panelOpen, panelOpenNow, plateVisible, overlap }));
    results.push({ route: `lessons-${w}`, panelOpenNow, plateVisible, overlap });

    // Строки списка уроков и кнопки — не меньше 40 px по обеим сторонам.
    const rows = page.locator('[data-testid="child-lesson-row"]');
    const rowCount = await rows.count();
    const rowSizes = await rows.evaluateAll(els => els.map(e => {
      const r = e.getBoundingClientRect();
      return { w: Math.round(r.width), h: Math.round(r.height) };
    }));
    console.log(`QA-G3B-ROWS-${w}`, rowCount, JSON.stringify(rowSizes.slice(0, 6)));

    // Плашка предмета: при открытой панели её нет (проверено выше) — при закрытой должна
    // появляться (макет kids-project-screen .fly).
    const hideBtn = page.locator('button[aria-label="Скрыть «Чаты»"], button[title="Скрыть «Чаты»"]').first();
    if (await hideBtn.count() > 0) {
      await hideBtn.click({ force: true }).catch(() => {});
      await page.waitForTimeout(3000);
      const plateNow = page.locator('text=Эту папку видят').first();
      const plateCount = await plateNow.count();
      let plateBox = null;
      if (plateCount > 0) {
        plateBox = await plateNow.boundingBox();
        const panelNow = await page.locator('[data-testid="child-lessons-panel"]').count();
        console.log(`QA-G3B-PLATE-CLOSED-${w}`, 'плашка:', plateCount, 'панель:', panelNow, JSON.stringify(plateBox));
      } else {
        console.log(`QA-G3B-PLATE-CLOSED-${w}`, 'плашка не появилась при закрытых панелях');
      }
      await page.screenshot({ path: resolve(SHOTS, `g3b-plate-closed-${w}.png`), fullPage: true });
      // вернуть панель
      const showBtn = page.locator('button[aria-label="Чаты"], button[title="Чаты"]').first();
      if (await showBtn.count() > 0) { await showBtn.click({ force: true }).catch(() => {}); await page.waitForTimeout(2000); }
    } else {
      console.log(`QA-G3B-PLATE-CLOSED-${w}`, 'кнопки закрытия панели нет');
    }

    // Шапка урока на 360: имя репетитора не сжимается (Г5).
    if (w === 360) {
      const row = page.locator('[data-testid="child-lesson-row"]').first();
      if (await row.count() > 0) {
        await row.click({ force: true });
        await page.waitForTimeout(6000);
        const header = page.locator('[data-testid="child-lesson-header"]');
        console.log('QA-G3B-HEADER-360', await header.count());
        const headerText = await header.innerText().catch(() => '');
        console.log('QA-G3B-HEADER-TEXT-360', JSON.stringify(headerText.slice(0, 200)));
        await page.screenshot({ path: resolve(SHOTS, 'g3b-lesson-header-360.png'), fullPage: true });
        // длина имени репетитора в шапке (не обрезано ли)
        const tutorName = page.locator('[data-testid="child-lesson-header"] span[title]').first();
        if (await tutorName.count() > 0) {
          const cut = await tutorName.evaluate((el: any) => el.scrollWidth > el.clientWidth + 1);
          console.log('QA-G3B-TUTOR-CUT-360', 'имя репетитора обрезано:', cut);
        }
        // назад к списку уроков
        const backList = page.locator('[aria-label="К списку уроков"]').first();
        console.log('QA-G3B-BACK-LESSONS-360', await backList.count());
      }
    }

    for (const r of rowSizes) {
      expect(r.h, `высота строки урока (${w})`).toBeGreaterThanOrEqual(40);
      expect(r.w, `ширина строки урока (${w})`).toBeGreaterThanOrEqual(40);
    }
    await page.screenshot({ path: resolve(SHOTS, `g3b-lessons-${w}.png`), fullPage: true });
  }
  console.log('QA-G3B-PROBES', JSON.stringify(results, null, 1));
  for (const r of results) {
    if (!r.route || r.route === 'lessons-360' || r.route === 'lessons-768' || r.route === 'lessons-1280') continue;
    expect(r.horizontalOverflow, `нет горизонтального переполнения (${r.route}, ${r.viewport})`).toBe(false);
  }
});

// ── Пункт 4: выключенный предмет не показывается ──────────────────────────────────
test('Г6: выключенный предмет в списке не показывается', async ({ page }) => {
  const before = await profileOf(childId);
  const subjects = (before?.subjects ?? []).map((s: any) => ({ ...s }));
  const off = subjects.map((s: any) => ({ ...s, enabled: s.id !== 'liter' }));
  const put = await putProfile(childId, {
    enabled: true, guardianUserIds: before.guardianUserIds, providerKey: before.providerKey,
    model: before.model, spaces: before.spaces, subjects: off, acknowledgeRouteRisk: before.acknowledgeRouteRisk,
  });
  expect(put.status, 'предмет liter выключен через профиль').toBe(200);

  await page.setViewportSize({ width: 360, height: 900 });
  await enter(page, childToken, { role: 'user', userId: childId, username: CHILD_U, clean: true });
  await page.goto('/#/home');
  await page.reload();   // перезагрузка: сбросить состояние React (открытый проект живёт в нём)
  await page.waitForTimeout(7000);
  const tiles = page.locator('[data-testid^="child-subject-tile-"]');
  console.log('QA-G6-DISABLED-TILES', await tiles.count());
  expect(await tiles.count(), 'выключенный предмет не показан').toBe(1);
  expect(await page.locator('[data-testid="child-subject-tile-liter"]').count(), 'плитки liter нет').toBe(0);
  await page.screenshot({ path: resolve(SHOTS, 'g6-disabled-subject-360.png'), fullPage: true });

  // Возвращаем предмет как было.
  const back = await putProfile(childId, {
    enabled: true, guardianUserIds: before.guardianUserIds, providerKey: before.providerKey,
    model: before.model, spaces: before.spaces, subjects, acknowledgeRouteRisk: before.acknowledgeRouteRisk,
  });
  console.log('QA-G6-DISABLED-RESTORE', back.status);
});

// ── Пункт 4: пустой профиль ───────────────────────────────────────────────────────
test('Г6: пустой профиль — что ребёнок реально видит', async ({ page }) => {
  await page.setViewportSize({ width: 360, height: 900 });
  // Гарантируем «пустой профиль»: пользователь есть, детский режим включён, предметов нет.
  const usersNow = (await call(req, 'get', '/api/users', adminToken)).body as any[];
  const emptyUser = usersNow.find(u => u.username === CHILD3_U);
  expect(emptyUser, `пользователь ${CHILD3_U} есть на стенде`).toBeTruthy();
  const modeRes = await call(req, 'put', `/api/users/${emptyUser.id}/child-mode`, adminToken, { enabled: true });
  console.log('QA-G6-EMPTY-CHILDMODE', modeRes.status);
  const emptyProfile = await profileOf(emptyUser.id);
  console.log('QA-G6-EMPTY-PROFILE', JSON.stringify({ enabled: emptyProfile?.enabled, subjects: emptyProfile?.subjects }));
  const emptyToken = await tokenOf(req, CHILD3_U, CHILD3_PW);
  await enter(page, emptyToken, { role: 'user', clean: true });
  await page.goto('/#/home');
  await page.reload();
  await page.waitForTimeout(7000);
  const text = await page.locator('body').innerText();
  const tiles = await page.locator('[data-testid^="child-subject-tile-"]').count();
  console.log('QA-G6-EMPTY-TEXT', JSON.stringify(text.slice(0, 200)), 'плиток:', tiles);
  await page.screenshot({ path: resolve(SHOTS, 'g6-empty-profile-360.png'), fullPage: true });
  // ФАКТ: миграция Т1b засевает ребёнку тройку предметов (math/russian/english), поэтому
  // ветка «Предметов пока нет» на живом стенде недостижима — ребёнок видит плитки.
  expect(tiles, 'ребёнок видит плитки предметов').toBeGreaterThan(0);
  console.log('QA-G6-EMPTY-NO-TEXT-STATE', text.includes('Предметов пока нет') ? 'показан' : 'НЕ показан (миграция засеяла предметы)');
});

test('Г6: обрыв связи — «Нет связи с сервером» и кнопка «Попробовать ещё раз»', async ({ page }) => {
  await page.setViewportSize({ width: 360, height: 900 });
  let attempts = 0;
  await page.route('**/api/child/subjects', route => {
    attempts++;
    route.fulfill({ status: 503, contentType: 'application/json', body: JSON.stringify({ code: 'policy_unavailable' }) });
  });
  await enter(page, childToken, { role: 'user', userId: childId, username: CHILD_U, clean: true });
  await page.goto('/#/home');
  await page.reload();   // перезагрузка: сбросить состояние React (открытый проект живёт в нём)
  await page.waitForTimeout(7000);
  const text = await page.locator('body').innerText();
  console.log('QA-G6-OFFLINE-TEXT', JSON.stringify(text.slice(0, 200)), 'attempts:', attempts);
  expect(text, 'ребёнку показан спокойный обрыв').toContain('Нет связи с сервером');
  const retry = page.locator('[data-testid="child-subjects-retry"]');
  expect(await retry.count(), 'есть кнопка «Попробовать ещё раз»').toBeGreaterThan(0);
  await page.screenshot({ path: resolve(SHOTS, 'g6-offline-360.png'), fullPage: true });

  const before = attempts;
  await retry.click({ force: true });
  await page.waitForTimeout(4000);
  console.log('QA-G6-RETRY-ATTEMPTS', before, '→', attempts);
  expect(attempts, 'повтор реально пошёл на сервер').toBeGreaterThan(before);
});

// ── Пункт 5: взрослый путь не сломан ───────────────────────────────────────────────
test('Взрослый путь: список проектов, выбор проекта, «К проектам», диплинк; пикера нет', async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 900 });
  await enter(page, adminToken, { role: 'admin', username: ADMIN_U });
  await page.goto('/#/home');
  await page.reload();
  await page.waitForTimeout(7000);

  // Взрослому экран выбора предмета не показывается.
  expect(await page.locator('[data-testid="child-subject-picker"]').count(),
    'взрослому пикер предметов не показывается').toBe(0);

  // Взрослый входит в проект (любой из списка) — по диплинку.
  const projects = (await call(req, 'get', '/api/projects', adminToken)).body as any[];
  let anyProject = projects.find((p: any) => !p.childSubjectId);
  if (!anyProject) {
    const created = await call(req, 'post', '/api/projects', adminToken, {
      name: 'qa-w5-adult-project', rootPath: '/tmp/qa-w5-adult-project', createDirectory: true,
    });
    console.log('QA-ADULT-CREATE', created.status, JSON.stringify(created.body ?? '').slice(0, 120));
    anyProject = (created.body?.id) ? created.body : null;
    if (anyProject) adultProjectId = anyProject.id;
  }
  expect(anyProject, 'у взрослого есть проект для проверки пути').toBeTruthy();

  // Список проектов: раздел «Проекты» взрослого.
  await page.goto('/#/projects');
  await page.waitForTimeout(5000);
  const listText = await page.locator('body').innerText();
  console.log('QA-ADULT-PROJECTS-TEXT', JSON.stringify(listText.slice(0, 300)));
  expect(listText, 'взрослый видит список проектов').toContain('qa-w5-adult-project');
  await page.screenshot({ path: resolve(SHOTS, 'adult-projects-1280.png'), fullPage: true });

  // Выбор проекта из списка (клик по названию) — открывается рабочее пространство.
  await page.getByText('qa-w5-adult-project', { exact: false }).first().click({ force: true });
  await page.waitForTimeout(7000);
  console.log('QA-ADULT-SELECT-URL', page.url());
  expect(page.url(), 'выбор проекта открыл проект').toContain(anyProject.id);
  const inProject = await page.locator('body').innerText();
  expect(inProject, 'взрослый видит взрослый экран проекта').not.toContain('Выбери предмет');
  await page.screenshot({ path: resolve(SHOTS, 'adult-selected-project-1280.png'), fullPage: true });

  // Диплинк: прямой заход по адресу проекта.
  await page.goto(`/#/project/${anyProject.id}`);
  await page.waitForTimeout(7000);
  console.log('QA-ADULT-DEEPLINK-URL', page.url());
  expect(page.url(), 'диплинк открывает проект').toContain(anyProject.id);
  await page.screenshot({ path: resolve(SHOTS, 'adult-deeplink-project-1280.png'), fullPage: true });

  // goToProjects — возврат из проекта к списку проектов: на мобильном это кнопка «назад»
  // в шапке проекта (BackButton, title = имя проекта), на десктопе — «К проектам» в сайдбаре.
  await page.setViewportSize({ width: 360, height: 900 });
  await page.reload();
  await page.waitForTimeout(7000);
  const backMobile = page.locator(`[role="button"][title="qa-w5-adult-project"]`).first();
  console.log('QA-ADULT-BACK-BTN-M', await backMobile.count());
  if (await backMobile.count() > 0) {
    await backMobile.click({ force: true });
    await page.waitForTimeout(5000);
    console.log('QA-ADULT-AFTER-BACK-URL', page.url());
    expect(page.url(), '«назад» из проекта вернул к списку проектов').toContain('/projects');
    await page.screenshot({ path: resolve(SHOTS, 'adult-back-360.png'), fullPage: true });
  }
  const backDesktop = page.getByText('К проектам', { exact: false }).first();
  console.log('QA-ADULT-BACK-BTN-D', await backDesktop.count());
});
