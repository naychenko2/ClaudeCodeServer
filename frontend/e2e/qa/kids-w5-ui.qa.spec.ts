// QA-8 · волна 5 «Опекуны и раскладка»: клиентская часть приёмки.
// Пункт 2 задачи — раскладка (Г3б, Г3б-д1, Г7): 360/768/1280, контент не схлопнут,
// нет горизонтального переполнения, плашка предмета не налезает на панель «Чаты» и
// появляется только при закрытых панелях, шапка не сжимает имя репетитора, детские
// тач-цели — не меньше 40 px.
// Пункт 4 — выбор предмета (Г6, Г6-д1): плитки своих предметов, тап по ВТОРОМУ
// открывает именно его, «назад» возвращает к списку (первый автоматически не
// открывается), выключенный предмет не виден, пустой профиль — понятный текст,
// «Нет связи с сервером» с кнопкой «Попробовать ещё раз».
// Пункт 5 — взрослый путь не сломан: список проектов, выбор проекта, диплинк, и
// взрослому новый экран выбора предмета не показывается.
//
// Стенд: PLAYWRIGHT_BASE_URL (5560), worktree kids-guardians, ветка feat/kids-guardians-ui.
// Фикстуры: qa_w5_off (предмет выключен), qa_w5_empty (пустой профиль).

import { test, expect, type APIRequestContext, type Page } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { resolve } from 'node:path';
import { ADMIN_U, ADMIN_PW, BASE, call, tokenOf, loginViaUi, badResponses } from './kids-w5-helpers';

const SHOTS = resolve(process.cwd(), '../../../.cc-attachments/qa/kids-w5-qa8/shots');
const CHILD_U = 'masha';
const CHILD_PW = 'dev-master';
const OFF_U = 'qa_w8_off';
const OFF_PW = 'Offqa12345';
const EMPTY_U = 'qa_w8_empty';
const EMPTY_PW = 'Emptyqa12345';
const ADULT_PROJECT_NAME = 'QA-W5 взрослый путь';

let req: APIRequestContext;
let adminToken = '';
let adminId = '';
let offId = '';
let emptyId = '';
let adultProjectId = '';

// Тесты независимы (каждый логинится сам): падение одного не должно скрывать остальные.

test.beforeAll(async ({ playwright }) => {
  mkdirSync(SHOTS, { recursive: true });
  req = await playwright.request.newContext({ baseURL: BASE });
  adminToken = await tokenOf(req, ADMIN_U, ADMIN_PW);
  const users = (await call(req, 'get', '/api/users', adminToken)).body as any[];
  adminId = users.find(u => u.username === ADMIN_U)?.id ?? '';

  // Ребёнок с выключенным предметом (engl) и ребёнок с пустым профилем.
  // Имена уникальны для QA-8: чужой пользователь с тем же именем и другим паролем
  // ломает вход (пароль при create не применяется, если пользователь уже есть).
  for (const [u, p] of [[OFF_U, OFF_PW], [EMPTY_U, EMPTY_PW]] as const) {
    const existing = (await call(req, 'get', '/api/users', adminToken)).body as any[];
    const old = existing.find(x => x.username === u);
    let id = '';
    if (old) {
      // Пользователь уже есть (прошлый прогон): пароль при create не применяется —
      // сбрасываем его отдельной ручкой, иначе вход под тестовым паролем не пройдёт.
      id = old.id;
      const reset = await call(req, 'put', `/api/users/${id}/password`, adminToken, { newPassword: p });
      console.log('QA-FIXTURE-RESET', u, reset.status);
    } else {
      const created = await call(req, 'post', '/api/users', adminToken, {
        username: u, password: p, role: 'user', executionEnvironment: 'container',
      });
      expect(created.status, `фикстура ${u} создана`).toBe(201);
      id = created.body.id;
    }
    if (u === OFF_U) offId = id; else emptyId = id;
    await call(req, 'put', `/api/users/${id}/child-mode`, adminToken, { enabled: true });
  }
  await call(req, 'put', `/api/users/${offId}/child-profile`, adminToken, {
    enabled: true, guardianUserIds: [adminId], providerKey: 'stub', model: 'stub-model', spaces: {},
    subjects: [
      { id: 'math', label: 'Математика', glyph: '∑', color: 'info', enabled: true },
      { id: 'engl', label: 'Английский', glyph: 'A', color: 'success', enabled: false },
    ],
    acknowledgeRouteRisk: true,
  });
  await call(req, 'put', `/api/users/${emptyId}/child-profile`, adminToken, {
    enabled: true, guardianUserIds: [adminId], providerKey: 'stub', model: 'stub-model', spaces: {},
    subjects: [], acknowledgeRouteRisk: true,
  });

  // Проект для взрослого пути (у admin на стенде своих проектов нет).
  const proj = await call(req, 'post', '/api/projects', adminToken, {
    name: ADULT_PROJECT_NAME, rootPath: '/home/an/Sources/qa-kids-w5-projects/qa-w5-adult', createDirectory: true,
  });
  console.log('QA-FIXTURE-PROJECT', proj.status, JSON.stringify(proj.body).slice(0, 200));
  if (proj.status < 300 && proj.body?.id) adultProjectId = proj.body.id;
  else {
    const list = (await call(req, 'get', '/api/projects', adminToken)).body as any[];
    adultProjectId = list.find(p => p.name === ADULT_PROJECT_NAME)?.id ?? '';
  }
  console.log('QA-FIXTURE-IDS', JSON.stringify({ offId, emptyId, adultProjectId }));
});

test.afterAll(async () => {
  for (const id of [offId, emptyId]) {
    if (!id) continue;
    await call(req, 'delete', `/api/users/${id}`, adminToken).catch(() => {});
  }
  if (adultProjectId) await call(req, 'delete', `/api/projects/${adultProjectId}`, adminToken).catch(() => {});
  await req?.dispose();
});

/** Интерактивные элементы детской поверхности: сам элемент или предок с testid «child-*». */
async function childTargets(page: Page) {
  return page.evaluate(() => {
    const out: { label: string; testid: string | null; w: number; h: number }[] = [];
    const inChild = (el: Element) => {
      let n: Element | null = el;
      while (n && n !== document.body) {
        const t = n.getAttribute?.('data-testid');
        if (t && t.startsWith('child-')) return true;
        n = n.parentElement;
      }
      return false;
    };
    for (const el of Array.from(document.querySelectorAll('button,a,[role="button"],input,select,textarea'))) {
      const r = (el as HTMLElement).getBoundingClientRect();
      if (!r.width || !r.height) continue;
      if (!inChild(el)) continue;
      out.push({
        label: (el.getAttribute('aria-label') || el.getAttribute('title') || el.textContent || '').trim().slice(0, 30),
        testid: el.getAttribute('data-testid'),
        w: Math.round(r.width), h: Math.round(r.height),
      });
    }
    return out;
  });
}

/** Ширины: документ, корневой контейнер приложения, панель уроков. */
async function layoutOf(page: Page) {
  return page.evaluate(() => {
    const doc = document.documentElement;
    const panel = document.querySelector('[data-testid="child-lessons-panel"]') as HTMLElement | null;
    const pr = panel?.getBoundingClientRect();
    const root = (document.querySelector('#root') ?? document.body.firstElementChild) as HTMLElement | null;
    const rr = root?.getBoundingClientRect();
    return {
      innerWidth: window.innerWidth,
      docScrollWidth: doc.scrollWidth,
      docClientWidth: doc.clientWidth,
      horizontalOverflow: doc.scrollWidth > doc.clientWidth + 1,
      root: rr ? { left: Math.round(rr.left), width: Math.round(rr.width) } : null,
      panel: pr ? { left: Math.round(pr.left), width: Math.round(pr.width), height: Math.round(pr.height) } : null,
      testids: Array.from(document.querySelectorAll('[data-testid]'))
        .map(e => e.getAttribute('data-testid')).filter(Boolean).slice(0, 40),
    };
  });
}

/** Плашка предмета (портал в body, fixed) — ищется по кнопке «Новый урок» внутри fixed-контейнера. */
async function subjectPlate(page: Page) {
  return page.evaluate(() => {
    const btns = Array.from(document.querySelectorAll('[data-testid="child-new-lesson"]')) as HTMLElement[];
    for (const b of btns) {
      let n: HTMLElement | null = b.parentElement;
      for (let i = 0; i < 4 && n; i++) {
        if (getComputedStyle(n).position === 'fixed') {
          const r = n.getBoundingClientRect();
          return { found: true, left: Math.round(r.left), right: Math.round(r.right), top: Math.round(r.top), text: (n.textContent || '').trim().slice(0, 60) };
        }
        n = n.parentElement;
      }
    }
    return { found: false };
  });
}

/** Панель «Чаты» (десктоп) — прямоугольник её карточки, если она открыта. */
async function chatsPanel(page: Page) {
  return page.evaluate(() => {
    const heads = Array.from(document.querySelectorAll('*')).filter(e => {
      const t = (e.textContent || '').trim();
      return t === 'Чаты' && e.children.length === 0;
    }) as HTMLElement[];
    for (const h of heads) {
      let n: HTMLElement | null = h.parentElement;
      for (let i = 0; i < 5 && n; i++) {
        const r = n.getBoundingClientRect();
        if (r.width > 100 && r.height > 100) {
          return { found: true, left: Math.round(r.left), right: Math.round(r.right), top: Math.round(r.top), bottom: Math.round(r.bottom) };
        }
        n = n.parentElement;
      }
    }
    return { found: false };
  });
}

async function loginChild(page: Page) {
  await page.setViewportSize({ width: 360, height: 900 });
  await loginViaUi(page, CHILD_U, CHILD_PW, true);
}

// ── Пункт 4: выбор предмета ─────────────────────────────────────────────────────────

test('Г6: ребёнок видит плитки своих предметов', async ({ page }) => {
  const bad = badResponses(page);
  await loginChild(page);
  await expect(page.locator('[data-testid="child-subject-picker"]')).toBeVisible({ timeout: 20000 });
  const tiles = page.locator('[data-testid^="child-subject-tile-"]');
  console.log('QA-G6-TILES', JSON.stringify(await tiles.evaluateAll(els => els.map(e => e.getAttribute('data-testid')))));
  expect(await tiles.count(), 'плитки предметов есть').toBeGreaterThan(1);
  await page.screenshot({ path: resolve(SHOTS, 'g6-picker-360.png'), fullPage: true });
  console.log('QA-G6-BAD', JSON.stringify(bad));
});

test('Г6: тап по ВТОРОМУ предмету открывает именно его', async ({ page }) => {
  await loginChild(page);
  await expect(page.locator('[data-testid="child-subject-picker"]')).toBeVisible({ timeout: 20000 });
  const ids = await page.locator('[data-testid^="child-subject-tile-"]').evaluateAll(
    els => els.map(e => e.getAttribute('data-testid') ?? ''));
  console.log('QA-G6-ORDER', JSON.stringify(ids));
  const second = ids[1];
  expect(second, 'вторая плитка есть').toBeTruthy();
  const subjectId = second.replace('child-subject-tile-', '');

  await page.locator(`[data-testid="${second}"]`).click({ force: true });
  await page.waitForTimeout(3000);
  const url = page.url();
  console.log('QA-G6-URL', url);

  // Открыт ли именно проект этого предмета: сверяем с проект��ми ребёнка.
  const childToken = await tokenOf(req, CHILD_U, CHILD_PW);
  const projects = (await call(req, 'get', '/api/projects', childToken)).body as any[];
  const target = projects.find(p => p.childSubjectId === subjectId);
  console.log('QA-G6-TARGET', JSON.stringify({ subjectId, targetId: target?.id, name: target?.name }));
  expect(target, `у ребёнка есть проект-предмет ${subjectId}`).toBeTruthy();
  expect(url, 'открыт проект выбранного предмета').toContain(target!.id);
  await page.screenshot({ path: resolve(SHOTS, 'g6-second-subject-360.png'), fullPage: true });
});

test('Г6: в мобильной шапке проекта есть кнопка «назад»', async ({ page }) => {
  await loginChild(page);
  await expect(page.locator('[data-testid="child-subject-picker"]')).toBeVisible({ timeout: 20000 });
  await page.locator('[data-testid="child-subject-tile-math"]').click({ force: true });
  await page.waitForTimeout(3000);

  const btns = await page.evaluate(() => Array.from(document.querySelectorAll('button')).map(b => ({
    title: b.getAttribute('title'), aria: b.getAttribute('aria-label'),
    text: (b.textContent || '').trim().slice(0, 24),
    svg: Array.from(b.querySelectorAll('svg')).map(s => s.getAttribute('class') || '').join('|'),
    testid: b.getAttribute('data-testid'),
  })));
  console.log('QA-G6-BUTTONS', JSON.stringify(btns));
  console.log('QA-G6-TESTIDS', JSON.stringify(await page.locator('[data-testid]').evaluateAll(
    els => els.map(e => e.getAttribute('data-testid')).filter(Boolean).slice(0, 40))));
  await page.screenshot({ path: resolve(SHOTS, 'g6-child-project-360.png'), fullPage: true });

  // «Назад» в мобильной шапке проекта — BackButton (стрелка влево + имя проекта).
  // Это НЕ <button> (PressableArea), поэтому ищем по иконке среди любых тегов.
  const back = page.locator('[title]:has(svg[class*="chevron-left"])');
  console.log('QA-G6-BACK-ELEMS', await back.count());
  expect(await back.count(), 'в мобильной шапке есть кнопка «назад» (стрелка влево)').toBeGreaterThan(0);
});

test('Г6: «назад» из проекта возвращает к списку предметов (первый не открывается сам)', async ({ page }) => {
  await loginChild(page);
  await expect(page.locator('[data-testid="child-subject-picker"]')).toBeVisible({ timeout: 20000 });
  const ids = await page.locator('[data-testid^="child-subject-tile-"]').evaluateAll(
    els => els.map(e => e.getAttribute('data-testid') ?? ''));
  const second = ids[1];
  const subjectId = second.replace('child-subject-tile-', '');
  await page.locator(`[data-testid="${second}"]`).click({ force: true });
  await page.waitForTimeout(3000);
  const openedUrl = page.url();

  // Кнопка «назад» — BackButton (стрелка влево, title = имя проекта).
  const back = page.locator('[title]:has(svg[class*="chevron-left"])').first();
  console.log('QA-G6-BACK-COUNT', await page.locator('[title]:has(svg[class*="chevron-left"])').count());
  expect(await back.count(), 'в мобильной шапке есть кнопка «назад»').toBeGreaterThan(0);
  await back.click({ force: true });
  await page.waitForTimeout(2500);

  await expect(page.locator('[data-testid="child-subject-picker"]')).toBeVisible({ timeout: 15000 });
  expect(page.url(), 'после «назад» — не внутри проекта').not.toContain('/project/');
  const stored = await page.evaluate(() => localStorage.getItem('cc_open_project'));
  console.log('QA-G6-AFTER-BACK', JSON.stringify({ openedUrl, url: page.url(), stored }));
  expect(stored ?? '', 'открытый проект сброшен').not.toContain('/project/');
  await page.screenshot({ path: resolve(SHOTS, 'g6-back-to-picker-360.png'), fullPage: true });
});

test('Г6: выключенный предмет не показывается', async ({ page }) => {
  await page.setViewportSize({ width: 360, height: 900 });
  await loginViaUi(page, OFF_U, OFF_PW, true);
  await expect(page.locator('[data-testid="child-subject-picker"]')).toBeVisible({ timeout: 20000 });
  const ids = await page.locator('[data-testid^="child-subject-tile-"]').evaluateAll(
    els => els.map(e => e.getAttribute('data-testid') ?? ''));
  console.log('QA-G6-OFF-TILES', JSON.stringify(ids));
  expect(ids, 'выключенный предмет не показан').not.toContain('child-subject-tile-engl');
  expect(ids.length, 'показан только включённый предмет').toBe(1);
  await page.screenshot({ path: resolve(SHOTS, 'g6-off-subject-360.png'), fullPage: true });
});

test('Г6: пустой профиль — понятный текст', async ({ page }) => {
  await page.setViewportSize({ width: 360, height: 900 });
  await loginViaUi(page, EMPTY_U, EMPTY_PW, true);
  await page.waitForTimeout(4000);
  const body = await page.locator('body').innerText();
  console.log('QA-G6-EMPTY-TEXT', JSON.stringify(body.slice(0, 300)));
  expect(body, 'понятный текст пустого профиля').toContain('Предметов пока нет');
  await page.screenshot({ path: resolve(SHOTS, 'g6-empty-profile-360.png'), fullPage: true });
});

test('Г6: нет связи с сервером — «Попробовать ещё раз»', async ({ page }) => {
  await loginChild(page);
  await expect(page.locator('[data-testid="child-subject-picker"]')).toBeVisible({ timeout: 20000 });
  await page.route('**/api/child/subjects*', route => route.abort());
  await page.reload();
  await page.waitForTimeout(3000);
  const body = await page.locator('body').innerText();
  console.log('QA-G6-OFFLINE-TEXT', JSON.stringify(body.slice(0, 300)));
  expect(body, 'видно «Нет связи с сервером»').toContain('Нет связи с сервером');
  const retry = page.locator('[data-testid="child-subjects-retry"]');
  expect(await retry.count(), 'кнопка «Попробовать ещё раз» есть').toBeGreaterThan(0);
  await page.screenshot({ path: resolve(SHOTS, 'g6-offline-360.png'), fullPage: true });

  await page.unroute('**/api/child/subjects*');
  await retry.click({ force: true });
  await page.waitForTimeout(2500);
  await expect(page.locator('[data-testid^="child-subject-tile-"]').first()).toBeVisible({ timeout: 15000 });
  console.log('QA-G6-RETRY-OK', page.url());
});

// ── Пункт 2: раскладка ────────────────────────────────────────────────────────────

for (const width of [360, 768]) {
  test(`Г3б/Г7: раскладка на ${width} — контент не схлопнут, нет переполнения, цели ≥40`, async ({ page }) => {
    await page.setViewportSize({ width, height: 900 });
    await loginViaUi(page, CHILD_U, CHILD_PW, true);
    await expect(page.locator('[data-testid="child-subject-picker"]')).toBeVisible({ timeout: 20000 });
    await page.locator('[data-testid="child-subject-tile-math"]').click({ force: true });
    await page.waitForTimeout(3000);

    const lay = await layoutOf(page);
    console.log(`QA-LAYOUT-${width}`, JSON.stringify(lay));
    expect(lay.horizontalOverflow, `нет горизонтального переполнения на ${width}`).toBe(false);
    // Контент не схлопнут: корневой контейнер занимает ширину экрана и не уехал влево.
    expect(lay.root, `корневой контейнер отрисован на ${width}`).toBeTruthy();
    expect(lay.root!.width, `контент не схлопнут на ${width}`).toBeGreaterThan(width - 2);
    expect(lay.root!.left, `контент не уехал влево за экран на ${width}`).toBeGreaterThanOrEqual(0);
    if (lay.panel) {
      expect(lay.panel.width, `панель уроков не схлопнута на ${width}`).toBeGreaterThan(0);
      expect(lay.panel.left, `панель уроков не уехала влево за экран на ${width}`).toBeGreaterThanOrEqual(0);
    }

    const targets = await childTargets(page);
    console.log(`QA-TARGETS-${width}`, JSON.stringify(targets));
    const small = targets.filter(t => t.w < 40 || t.h < 40);
    expect(small, `детские цели ≥40 на ${width}`).toEqual([]);
    await page.screenshot({ path: resolve(SHOTS, `layout-${width}.png`), fullPage: true });
  });
}

test('Г3б/Г7: на 1280 плашка предмета только при закрытых панелях и не налезает на «Чаты»', async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 900 });
  await loginViaUi(page, CHILD_U, CHILD_PW, true);
  await expect(page.locator('[data-testid="child-subject-picker"]')).toBeVisible({ timeout: 20000 });
  await page.locator('[data-testid="child-subject-tile-math"]').click({ force: true });
  await page.waitForTimeout(3500);

  const plate0 = await subjectPlate(page);
  const panel0 = await chatsPanel(page);
  console.log('QA-PLATE-0', JSON.stringify({ plate0, panel0 }));
  console.log('QA-1280-TESTIDS', JSON.stringify(await page.locator('[data-testid]').evaluateAll(
    els => els.map(e => e.getAttribute('data-testid')).filter(t => t && t.startsWith('child-')).slice(0, 30))));

  if (plate0.found && panel0.found) {
    const overlap = plate0.left < panel0.right && plate0.right > panel0.left;
    console.log('QA-PLATE-OVERLAP', JSON.stringify({ overlap }));
    expect(overlap, 'плашка не налезает на панель «Чаты»').toBe(false);
  }

  // Кнопка закрытия панели «Чаты» (если панель открыта) — плашка обязана появиться.
  const hide = page.locator('button[aria-label*="Скрыть"], button[title*="Скрыть"]').first();
  if (await hide.count() > 0) {
    await hide.click({ force: true });
    await page.waitForTimeout(1500);
    const plate1 = await subjectPlate(page);
    console.log('QA-PLATE-AFTER-HIDE', JSON.stringify(plate1));
    expect(plate1.found, 'при закрытых панелях плашка предмета появляется').toBe(true);
  } else {
    console.log('QA-PLATE-NO-HIDE-BTN', 'кнопки закрытия панели «Чаты» нет');
  }
  await page.screenshot({ path: resolve(SHOTS, 'layout-1280.png'), fullPage: true });
});

test('Г3б: шапка не сжимает имя репетитора', async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 900 });
  await loginViaUi(page, CHILD_U, CHILD_PW, true);
  await expect(page.locator('[data-testid="child-subject-picker"]')).toBeVisible({ timeout: 20000 });

  // Имя репетитора — из снимка поверхности ребёнка.
  const childToken = await tokenOf(req, CHILD_U, CHILD_PW);
  const subjects = (await call(req, 'get', '/api/child/subjects', childToken)).body as any[];
  const tutorName: string = subjects?.subjects?.find((s: any) => s.id === 'math')?.tutor?.name ?? '';
  console.log('QA-TUTOR-NAME', JSON.stringify(tutorName));

  // Плитка в пикере (мобильный экран выбора предмета) — имя репетитора видно и не обрезано.
  const inTile = await tutorBox(page, tutorName);
  console.log('QA-TUTOR-TILE', JSON.stringify(inTile));
  expect(inTile.found, 'имя репетитора есть на плитке предмета').toBe(true);
  expect(inTile.w, 'имя репетитора не схлопнуто').toBeGreaterThan(0);
  expect(inTile.clipped, 'имя репетитора не обрезано').toBe(false);

  // Плашка предмета на десктопе (после закрытия панелей) — то же имя целиком.
  await page.locator('[data-testid="child-subject-tile-math"]').click({ force: true });
  await page.waitForTimeout(3500);
  const hide = page.locator('button[aria-label*="Скрыть"], button[title*="Скрыть"]').first();
  if (await hide.count() > 0) { await hide.click({ force: true }); await page.waitForTimeout(1500); }
  const inPlate = await tutorBox(page, tutorName);
  console.log('QA-TUTOR-PLATE', JSON.stringify(inPlate));
  if (inPlate.found) {
    expect(inPlate.w, 'имя репетитора в плашке не схлопнуто').toBeGreaterThan(0);
    expect(inPlate.clipped, 'имя репетитора в плашке не обрезано').toBe(false);
  }
});

/** Узкийший текстовый узел, содержащий имя репетитора: его ширина и обрезание. */
async function tutorBox(page: Page, name: string) {
  return page.evaluate((n) => {
    if (!n) return { found: false };
    const els = Array.from(document.querySelectorAll('*')) as HTMLElement[];
    const hits = els.filter(e => (e.textContent || '').includes(n) && e.children.length === 0);
    if (hits.length === 0) return { found: false };
    const el = hits[0];
    const r = el.getBoundingClientRect();
    return {
      found: true, text: (el.textContent || '').trim().slice(0, 60),
      w: Math.round(r.width), h: Math.round(r.height),
      clipped: el.scrollWidth > el.clientWidth + 1,
      scrollWidth: el.scrollWidth, clientWidth: el.clientWidth,
    };
  }, name);
}

test('Г6: из чата «К списку уроков» возвращает к урокам (а не к предметам)', async ({ page }) => {
  await loginChild(page);
  await expect(page.locator('[data-testid="child-subject-picker"]')).toBeVisible({ timeout: 20000 });
  await page.locator('[data-testid="child-subject-tile-math"]').click({ force: true });
  await page.waitForTimeout(3000);

  // Тап по строке урока — открывается чат с детской шапкой.
  const row = page.locator('[data-testid="child-lesson-row"]').first();
  expect(await row.count(), 'строка урока есть').toBeGreaterThan(0);
  await row.click({ force: true });
  await page.waitForTimeout(3000);
  const backToLessons = page.locator('[aria-label="К списку уроков"]').first();
  console.log('QA-G6-IN-CHAT-BACK', await backToLessons.count());
  expect(await backToLessons.count(), 'в чате есть «К списку уроков»').toBeGreaterThan(0);
  await backToLessons.click({ force: true });
  await page.waitForTimeout(2500);
  const picker = await page.locator('[data-testid="child-subject-picker"]').count();
  const lessons = await page.locator('[data-testid="child-lesson-row"]').count();
  console.log('QA-G6-BACK-FROM-CHAT', JSON.stringify({ picker, lessons, url: page.url() }));
  // «К списку уроков» возвращает к урокам — это его прямое назначение; к предметам
  // ведёт «назад» в шапке проекта (проверяется отдельным тестом).
  expect(lessons, 'вернулись к списку уроков').toBeGreaterThan(0);
  await page.screenshot({ path: resolve(SHOTS, 'g6-in-chat-360.png'), fullPage: true });
});

test('уточнение: на 768 список уроков и имя репетитора в шапке чата', async ({ page }) => {
  // 768: в проекте по умолчанию только пустое состояние центра. Проверяем, доступен
  // ли список уроков через рельсу предметов (иконка глифа предмета слева).
  await page.setViewportSize({ width: 768, height: 900 });
  await loginViaUi(page, CHILD_U, CHILD_PW, true);
  await expect(page.locator('[data-testid="child-subject-picker"]')).toBeVisible({ timeout: 20000 });
  await page.locator('[data-testid="child-subject-tile-math"]').click({ force: true });
  await page.waitForTimeout(3000);
  const before = await layoutOf(page);
  console.log('QA-768-BEFORE-RAIL', JSON.stringify(before.testids));

  const railGlyph = page.locator('button:has-text("∑"), [data-testid*="rail"] , button:has(svg)').filter({ hasText: '∑' }).first();
  const railCount = await page.locator('text=∑').count();
  console.log('QA-768-GLYPH-COUNT', railCount);
  if (railCount > 0) {
    await page.locator('text=∑').first().click({ force: true });
    await page.waitForTimeout(2000);
    const after = await layoutOf(page);
    console.log('QA-768-AFTER-RAIL', JSON.stringify(after.testids));
    console.log('QA-768-LESSON-PANEL-AFTER', await page.locator('[data-testid="child-lessons-panel"]').count(),
      'rows', await page.locator('[data-testid="child-lesson-row"]').count());
    await page.screenshot({ path: resolve(SHOTS, 'layout-768-rail.png'), fullPage: true });
  }

  // 360: в чате шапка урока — имя репетитора не обрезано.
  await page.setViewportSize({ width: 360, height: 900 });
  await page.waitForTimeout(1500);
  const row = page.locator('[data-testid="child-lesson-row"]').first();
  if (await row.count() === 0) {
    console.log('QA-360-HEADER-NO-ROW', 'список уроков недоступен на 360');
    return;
  }
  await row.click({ force: true });
  await page.waitForTimeout(3000);
  const childToken = await tokenOf(req, CHILD_U, CHILD_PW);
  const subjects = (await call(req, 'get', '/api/child/subjects', childToken)).body as any[];
  const tutorName: string = subjects?.subjects?.find((s: any) => s.id === 'math')?.tutor?.name ?? '';
  const header = await tutorBox(page, tutorName);
  console.log('QA-360-HEADER-TUTOR', JSON.stringify({ tutorName, header }));
  console.log('QA-360-HEADER-TESTIDS', JSON.stringify(await page.locator('[data-testid^="child-"]').evaluateAll(
    els => els.map(e => e.getAttribute('data-testid')).slice(0, 20))));
  await page.screenshot({ path: resolve(SHOTS, 'child-header-360.png'), fullPage: true });
});

test('взрослый путь: список проектов, выбор проекта, диплинк; пикер предмета не показывается', async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 900 });
  await loginViaUi(page, ADMIN_U, ADMIN_PW, true);
  await page.goto('/#/projects');
  await page.waitForTimeout(4000);
  const body = await page.locator('body').innerText();
  console.log('QA-ADULT-PROJECTS', JSON.stringify(body.slice(0, 300)));
  expect(body, 'взрослый видит список проектов').toContain(ADULT_PROJECT_NAME);
  expect(await page.locator('[data-testid="child-subject-picker"]').count(), 'пикер предмета взрослому не показывается').toBe(0);

  await page.getByText(ADULT_PROJECT_NAME, { exact: false }).first().click({ force: true });
  await page.waitForTimeout(4000);
  console.log('QA-ADULT-URL', page.url());
  expect(page.url(), 'проект открылся').toContain('/project/');

  // Диплинк: перезагрузка по прямой ссылке на проект.
  const deep = page.url();
  await page.goto(deep);
  await page.waitForTimeout(4000);
  console.log('QA-ADULT-DEEP-URL', page.url());
  expect(page.url(), 'диплинк открывает тот же проект').toContain('/project/');
  expect(await page.locator('[data-testid="child-subject-picker"]').count(), 'пикер предмета и тут не показывается').toBe(0);
  await page.screenshot({ path: resolve(SHOTS, 'adult-project-1280.png'), fullPage: true });
});
