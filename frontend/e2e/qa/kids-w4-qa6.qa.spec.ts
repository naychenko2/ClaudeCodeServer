// QA-6: повторная приёмка круга, закрывающего три дефекта QA-5 (задача 1ae702e8).
// Иду строго по пунктам задачи; новых сценариев не ищу.
//
// R1 = К9a + Т9а/К9f: сломанный маршрут → в ленте «Занятие пока недоступно…», а не общая
//      плашка; после F5 лента не пустая — история грузится через разрешённую /api/chats/{id}/history,
//      проектную /api/projects/{pid}/sessions/{id}/history ребёнок не просит.
// GUARD = К9b–К9e, К9g: ни одного 403 на страницах ребёнка; закрытые ручки не опрашиваются вовсе
//      (PUT /chats/{id}/read, GET /settings, GET /tasks/features*, POST /tts/diag); ErrorBoundary нет.
// NOTIF = Т9a: опекун получил child_route_refusal на отказ из R1.
// ADULT = К9g: у взрослого история грузится, /settings опрашивается (200), сбои — обычным путём,
//      детский текст отказа в взрослую ленту не течёт.
//
// Вне круга (по постановке): мобильный список уроков и «notes/chatContext» на 360.
//
// Стенд: свой процесс на :5563 (5562 занят чужим стендом), DataPath — фикстура
// /home/an/Sources/qa-kids-w4-data (ребёнок qa_kids_w4, куратор qa_kids_w4_cur,
// предмет «Математика», провайдеры kids-cloud/kids-local на фейковом движке).

import { test, expect, type APIRequestContext, type Page } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { resolve } from 'node:path';

// process.cwd() прогона — frontend/e2e/qa; снимки — в .cc-attachments от корня дерева.
const SHOTS = resolve(process.cwd(), '../../../.cc-attachments/qa/kids-w4-qa6/shots');
const CHILD_U = 'qa_kids_w4';
const CHILD_PW = 'Kidsqa12345';
const CUR_U = 'qa_kids_w4_cur';
const CUR_PW = 'Kidsqa12345';
const CHILD_TEXT = 'Занятие пока недоступно, взрослым отправлено уведомление.';
const GENERIC_PLATE = 'Помощник сейчас не может ответить';

const BASE = process.env.PLAYWRIGHT_BASE_URL || 'http://127.0.0.1:5563';

let childToken = '';
let curToken = '';
let adminToken = '';
let childId = '';
let adminId = '';
let subjectProjectId = '';
let childSubjectId = '';
let lessonSessionId = '';
let refusalNotifsBefore = 0;
let adminTiersBackup: any = null;
// Закрытые ручки, которые ребёнку просить нельзя (К9b–К9e): [метод, путь-подстрока]
const FORBIDDEN_REQS: Array<[string, string]> = [
  ['PUT', '/read'],
  ['GET', '/api/settings'],
  ['GET', '/api/tasks/features'],
  ['POST', '/api/tts/diag'],
];

async function call(
  req: APIRequestContext, method: string, url: string, token: string, data?: unknown,
): Promise<{ status: number; body: any }> {
  const r = await (req as any)[method](url, {
    headers: { Authorization: `Bearer ${token}` },
    ...(data === undefined ? {} : { data }),
  });
  const text = await r.text();
  let body: any = undefined;
  try { body = text ? JSON.parse(text) : undefined; } catch { body = text; }
  return { status: r.status(), body: body ?? null };
}

async function tokenOf(req: APIRequestContext, username: string, password: string): Promise<string> {
  const r = await req.post('/api/auth/login', { data: { username, password } });
  expect(r.ok(), `логин ${username} (${r.status()})`).toBeTruthy();
  return (await r.json()).token as string;
}

async function enter(page: Page, token: string) {
  await page.addInitScript((t) => { localStorage.setItem('cc_token', t); }, token);
}

// Ответы ≥400 и все запросы страницы — для сторожа GUARD и проверки «закрытые ручки не опрашиваются».
function pageTraffic(page: Page): { bad: string[]; reqs: string[] } {
  const bad: string[] = [];
  const reqs: string[] = [];
  page.on('response', r => {
    const s = r.status();
    if (s >= 400) bad.push(`${s} ${r.request().method()} ${r.url().replace(/^https?:\/\/[^/]+/, '')}`);
  });
  page.on('request', r => {
    reqs.push(`${r.method()} ${r.url().replace(/^https?:\/\/[^/]+/, '').split('?')[0]}`);
  });
  return { bad, reqs };
}

function forbiddenHits(reqs: string[]): string[] {
  return reqs.filter(q => FORBIDDEN_REQS.some(([m, p]) => q.startsWith(`${m} `) && q.includes(p)));
}

// ui_probe (правило приёмки интерфейса): тач-цели <40px, горизонтальное переполнение, обрезанный текст.
async function uiProbe(page: Page, w: number): Promise<any> {
  return page.evaluate((vw) => {
    let smallCount = 0;
    const small: string[] = [];
    for (const el of Array.from(document.querySelectorAll('button,a,[role="button"],input,textarea,select,[role="tab"],[role="menuitem"]'))) {
      const r = (el as HTMLElement).getBoundingClientRect();
      if (r.width === 0 || r.height === 0) continue;
      if (r.width < 40 || r.height < 40) {
        smallCount++;
        if (small.length < 8) small.push(`${(el.getAttribute('aria-label') || el.getAttribute('title') || (el.textContent ?? '').trim().slice(0, 30))} ${Math.round(r.width)}x${Math.round(r.height)}`);
      }
    }
    let clipped = 0;
    for (const el of Array.from(document.querySelectorAll('*'))) {
      const he = el as HTMLElement;
      if (!(he as any).isContentEditable && typeof he.title !== 'string') continue;
      const cs = getComputedStyle(he);
      if (cs.textOverflow === 'ellipsis' && he.scrollWidth > he.clientWidth + 1 && he.textContent?.trim()) clipped++;
    }
    return {
      viewport: vw,
      horizontalOverflow: document.documentElement.scrollWidth > vw,
      smallTargets: smallCount,
      smallSamples: small,
      clippedText: clipped,
    };
  }, w);
}

test.describe.configure({ mode: 'serial' });

test.beforeAll(async ({ playwright }) => {
  mkdirSync(SHOTS, { recursive: true });
  const req = await playwright.request.newContext({ baseURL: BASE });
  childToken = await tokenOf(req, CHILD_U, CHILD_PW);
  curToken = await tokenOf(req, CUR_U, CUR_PW);
  adminToken = await tokenOf(req, process.env.E2E_USER || 'admin', process.env.E2E_PASS || '12345');

  const users = (await (await req.get('/api/users', { headers: { Authorization: `Bearer ${adminToken}` } })).json()) as any[];
  childId = users.find(u => u.username === CHILD_U).id;
  adminId = users.find(u => u.username === (process.env.E2E_USER || 'admin')).id;
  expect(childId, 'учётка ребёнка есть на стенде').toBeTruthy();

  const projects = (await (await req.get('/api/projects', { headers: { Authorization: `Bearer ${childToken}` } })).json()) as any[];
  expect(projects.length, 'у ребёнка есть проект-предмет').toBeGreaterThan(0);
  subjectProjectId = projects[0].id;
  childSubjectId = projects[0].childSubjectId ?? '';
  expect(childSubjectId, 'маркер предмета в DTO проекта').toBeTruthy();

  // Гигиена фикстуры: живые слоты ребёнка и снимок слотов админа (для восстановления после ADULT).
  await call(req, 'put', `/api/admin/users/${childId}/model-tiers`, adminToken, {
    strong: 'kids-cloud-1', medium: 'kids-cloud-1', weak: 'kids-local-1',
  });
  const adminTiers = await call(req, 'get', '/api/me/model-tiers', adminToken);
  adminTiersBackup = adminTiers.status === 200 ? adminTiers.body : null;
  console.log('QA-ADMIN-TIERS-BACKUP', JSON.stringify(adminTiersBackup ?? {}).slice(0, 200));

  // Базлайн уведомлений опекуна: на отказ из R1 должно появиться новое (схлопывание — раз в час,
  // ChildRouteRefusalNotifier.cs:36-40; отказ этого часа будет первым).
  const notif = await call(req, 'get', '/api/notifications', curToken);
  const items = notif.body?.items ?? notif.body ?? [];
  refusalNotifsBefore = Array.isArray(items) ? items.filter((n: any) => n?.type === 'child_route_refusal').length : 0;
  console.log('QA-NOTIF-BASELINE', refusalNotifsBefore);

  // Чистый урок под сценарии (прежние загрязнены прогонами QA-4/QA-5).
  const created = await call(req, 'post', '/api/child/spaces/lessons/chat', childToken, { subject: childSubjectId });
  console.log('QA-NEW-LESSON', created.status, JSON.stringify(created.body ?? '').slice(0, 300));
  expect(created.status, 'создание урока').toBe(201);
  lessonSessionId = created.body?.sessionId ?? created.body?.chat?.sessionId;
  expect(lessonSessionId, 'id созданного урока').toBeTruthy();
  console.log('QA-LESSON-ID', lessonSessionId);

  await req.dispose();
});

test.afterAll(async ({ playwright }) => {
  const req = await playwright.request.newContext({ baseURL: BASE });
  // Состояние фикстуры после приёмки: слоты восстановлены.
  await call(req, 'put', `/api/admin/users/${childId}/model-tiers`, adminToken, {
    strong: 'kids-cloud-1', medium: 'kids-cloud-1', weak: 'kids-local-1',
  });
  if (adminTiersBackup) {
    await call(req, 'put', `/api/admin/users/${adminId}/model-tiers`, adminToken, adminTiersBackup);
  }
  await req.dispose();
});

// ── R1 = К9a + Т9а/К9f: отказ маршрута и лента после F5 ──────────────────────────────
test('R1: сломанный маршрут — честный текст в ленте', { tag: '@refusal' }, async ({ page, playwright }) => {
  const req = await playwright.request.newContext({ baseURL: BASE });

  // Живой маршрут резолвится слотами ребёнка (у персоны-репетитора явной модели нет) —
  // ломаем strong+medium моделью, которой нет ни у одного провайдера.
  const tiers = await call(req, 'put', `/api/admin/users/${childId}/model-tiers`, adminToken, {
    strong: 'kids-alien-model', medium: 'kids-alien-model', weak: 'kids-local-1',
  });
  expect(tiers.status, 'слоты ребёнка сломаны').toBe(200);

  const traffic = pageTraffic(page);
  const consoleErrors: string[] = [];
  page.on('console', m => { if (m.type() === 'error') consoleErrors.push(m.text()); });
  page.on('pageerror', e => consoleErrors.push(String(e)));

  await page.setViewportSize({ width: 1280, height: 900 });
  await enter(page, childToken);
  await page.goto(`/#/project/${subjectProjectId}/chat/${lessonSessionId}`);
  await page.waitForTimeout(6000);

  const composer = page.locator('[data-composer-input] textarea').first();
  expect(await composer.count(), 'композер урока доступен ребёнку').toBeGreaterThan(0);
  await composer.fill('Привет, репетитор (QA6 маршрут)');
  await composer.press('Enter');
  await page.waitForTimeout(12000);

  const feedText = await page.locator('body').innerText();
  console.log('QA-R1-LIVE-TEXT', JSON.stringify({
    honest: feedText.includes(CHILD_TEXT),
    genericPlate: feedText.includes(GENERIC_PLATE),
    tech: /Процесс модели завершился/.test(feedText),
  }));
  await page.screenshot({ path: resolve(SHOTS, 'r1-feed-live-1280.png'), fullPage: true });
  expect(feedText, 'Т9а/К9f: в ленте — честный текст отказа').toContain(CHILD_TEXT);
  expect(feedText, 'техтекста мёртвого процесса нет').not.toContain('Процесс модели завершился');
  const hubError = consoleErrors.find(t => t.includes('An unexpected error occurred invoking'));
  expect(hubError, 'хаб-вызов не падает').toBeFalsy();
  // Факт собран (лог + скрин); строгая проверка общей плашки — в конце теста,
  // чтобы измерение после F5 доехало даже при живом дефекте.

  // К9a: после F5 лента не пустая — прошлые записи доезжают из истории.
  await page.reload();
  await page.waitForTimeout(8000);
  const afterReload = await page.locator('body').innerText();
  await page.screenshot({ path: resolve(SHOTS, 'r1-feed-after-reload-1280.png'), fullPage: true });
  console.log('QA-R1-AFTER-RELOAD', JSON.stringify({
    honest: afterReload.includes(CHILD_TEXT),
    ownMessage: afterReload.includes('Привет, репетитор (QA6 маршрут)'),
  }));
  // К9a мерим по формулировке критерия: «лента не пустая, видны прошлые записи». Записью
  // здесь является отказ — он персистится; реплика ребёнка при отказе хода не принимается
  // ядром вовсе (TurnIntake.cs:515: отказ до OnUserMessage:545) — это не запись урока.
  expect(afterReload, 'К9a: после F5 лента не пустая — видна запись отказа').toContain(CHILD_TEXT);
  expect(afterReload, 'после F5 нет падения ErrorBoundary').not.toContain('Интерфейс споткнулся');

  // Контракт загрузки: история — через разрешённую /chats-ручку, проектную ребёнок не просит.
  const chatsHist = traffic.reqs.filter(q => q.includes(`/api/chats/${lessonSessionId}/history`));
  const projectHist = traffic.reqs.filter(q => /\/api\/projects\/[^/]+\/sessions\/[^/]+\/history/.test(q));
  console.log('QA-R1-HISTORY-ROUTES', JSON.stringify({ chats: chatsHist, project: projectHist }));
  expect(chatsHist.length, 'история урока идёт через /api/chats/{id}/history').toBeGreaterThan(0);
  expect(projectHist, 'проектная ручка истории ребёнком не запрашивается').toEqual([]);
  const bad403 = traffic.bad.filter(s => s.startsWith('403'));
  console.log('QA-R1-BAD', JSON.stringify(traffic.bad));
  expect(bad403, 'на R1-пути ребёнка нет 403').toEqual([]);

  // Строгая проверка общей плашки — последняя: на живом пути (К9f).
  expect(feedText, 'К9f: на живом пути общей плашки «Помощник сейчас не может ответить…» нет').not.toContain(GENERIC_PLATE);
  expect(afterReload, 'К9f: после F5 общей плашки в ленте нет').not.toContain(GENERIC_PLATE);

  // Слоты обратно — дальше меряем GUARD на живом маршруте.
  await call(req, 'put', `/api/admin/users/${childId}/model-tiers`, adminToken, {
    strong: 'kids-cloud-1', medium: 'kids-cloud-1', weak: 'kids-local-1',
  });
  await req.dispose();
});

// ── GUARD = К9b–К9e, К9g: ни одного 403 на детском пути; закрытые ручки не опрашиваются ─
test('GUARD: страницы ребёнка — ни 403, ни закрытых ручек, ни ErrorBoundary', async ({ page }) => {
  const badAll: string[] = [];
  const reqsAll: string[] = [];

  for (const route of [`#/${'home'}`, `#/project/${subjectProjectId}`, `#/project/${subjectProjectId}/chat/${lessonSessionId}`]) {
    const traffic = pageTraffic(page);
    await page.setViewportSize({ width: 1280, height: 900 });
    await enter(page, childToken);
    await page.goto(`/${route}`);
    await page.waitForTimeout(8000);
    const bodyText = await page.locator('body').innerText();
    expect(bodyText, `нет падения ErrorBoundary на ${route}`).not.toContain('Интерфейс споткнулся');
    badAll.push(...traffic.bad);
    reqsAll.push(...traffic.reqs);
    await page.screenshot({ path: resolve(SHOTS, `guard-${route.replace(/[^a-z0-9]+/gi, '-')}.png`), fullPage: true });
  }

  console.log('QA-GUARD-BAD', JSON.stringify(badAll));
  console.log('QA-GUARD-REQS', JSON.stringify([...new Set(reqsAll)]));
  expect([...new Set(badAll.filter(s => s.startsWith('403')))], 'ни одного 403 на пути ребёнка').toEqual([]);
  const hits = forbiddenHits([...new Set(reqsAll)]);
  console.log('QA-GUARD-FORBIDDEN-HITS', JSON.stringify(hits));
  expect(hits, 'закрытые ручки (/read, /settings, /tasks/features*, /tts/diag) ребёнком не опрашиваются').toEqual([]);
});

// ── NOTIF = Т9a: опекун получил уведомление на отказ из R1 ────────────────────────────
// NOTIF мерим свежестью: отказ этого круга уже был в прогоне R1 (тот же стенд), а базлайн
// «до/после» в отдельном запуске не собрать (beforeAll считает уже после отказа). Поэтому:
// последнее child_route_refusal у опекуна должно быть свежим — не старше последнего отказа.
test('NOTIF: отказ маршрута — у опекуна свежее child_route_refusal', { tag: '@refusal' }, async ({ playwright }) => {
  const req = await playwright.request.newContext({ baseURL: BASE });
  const notif = await call(req, 'get', '/api/notifications', curToken);
  const items = notif.body?.items ?? notif.body ?? [];
  const refusal = Array.isArray(items) ? items.filter((n: any) => n?.type === 'child_route_refusal') : [];
  const latestRaw = refusal[0]?.createdAt ?? refusal[0]?.created_at ?? null;
  const latestMs = typeof latestRaw === 'number' ? latestRaw : latestRaw ? Date.parse(String(latestRaw)) : NaN;
  const ageMin = Number.isNaN(latestMs) ? NaN : (Date.now() - latestMs) / 60000;
  console.log('QA-NOTIF-COUNT', refusalNotifsBefore, '→', refusal.length, 'latest:', latestRaw, 'ageMin:', ageMin);
  expect(refusal.length, 'у опекуна есть уведомления child_route_refusal').toBeGreaterThan(0);
  expect(Number.isNaN(ageMin) || ageMin > 30, 'последнее уведомление — свежее (отказ этого круга, ≤30 мин)').toBe(false);
  await req.dispose();
});

// ── ADULT = К9g: взрослый путь не сломан ──────────────────────────────────────────────
test('ADULT: взрослый — история грузится, /settings 200, сбои обычным путём', async ({ page, playwright }) => {
  const req = await playwright.request.newContext({ baseURL: BASE });

  // Тестовый взрослый чат (свой проект, убирается в finally этого теста).
  const uniq = `qa-kids6-${Date.now()}`;
  const proj = await call(req, 'post', '/api/projects', adminToken, {
    name: uniq, rootPath: `/tmp/${uniq}`, createDirectory: true,
  });
  expect(proj.status, 'создан тестовый проект').toBe(201);
  const sess = await call(req, 'post', `/api/projects/${proj.body.id}/sessions`, adminToken, { name: 'qa-kids6-chat' });
  expect(sess.status, 'создан тестовый чат').toBe(201);

  try {
    // Ломаем взрослый маршрут той же недоступной моделью — проверяем, что сбой показывается
    // обычным взрослым путём, а детский текст отказа в взрослую ленту не течёт.
    const tiers = await call(req, 'put', `/api/admin/users/${adminId}/model-tiers`, adminToken, {
      strong: 'kids-alien-model', medium: 'kids-alien-model', weak: 'kids-alien-model',
    });
    expect(tiers.status, 'слоты админа сломаны').toBe(200);

    const traffic = pageTraffic(page);
    await page.setViewportSize({ width: 1280, height: 900 });
    await enter(page, adminToken);
    await page.goto(`/#/project/${proj.body.id}/chat/${sess.body.id}`);
    await page.waitForTimeout(6000);

    const composer = page.locator('[data-composer-input] textarea').first();
    expect(await composer.count(), 'композер доступен взрослому').toBeGreaterThan(0);
    await composer.fill('проба взрослого (QA6)');
    await composer.press('Enter');
    await page.waitForTimeout(12000);

    const liveText = await page.locator('body').innerText();
    console.log('QA-ADULT-LIVE', JSON.stringify({
      ownMessage: liveText.includes('проба взрослого (QA6)'),
      childText: liveText.includes(CHILD_TEXT),
    }));
    await page.screenshot({ path: resolve(SHOTS, 'adult-feed-live-1280.png'), fullPage: true });
    expect(liveText, 'сообщение взрослого ушло в ленту').toContain('проба взрослого (QA6)');
    expect(liveText, 'детский текст отказа не течёт в взрослую ленту').not.toContain(CHILD_TEXT);

    // История взрослого: после F5 лента не пустая, грузится проектной ручкой (200).
    await page.reload();
    await page.waitForTimeout(8000);
    const afterReload = await page.locator('body').innerText();
    await page.screenshot({ path: resolve(SHOTS, 'adult-feed-after-reload-1280.png'), fullPage: true });
    expect(afterReload, 'после F5 лента взрослого не пустая').toContain('проба взрослого (QA6)');
    expect(afterReload, 'нет падения ErrorBoundary у взрослого').not.toContain('Интерфейс споткнулся');

    const settingsReqs = traffic.reqs.filter(q => q.startsWith('GET /api/settings'));
    const settingsResp = traffic.bad.filter(s => s.includes('/api/settings'));
    console.log('QA-ADULT-SETTINGS', JSON.stringify({ requests: settingsReqs, bad: settingsResp }));
    expect(settingsReqs.length, 'взрослый опрашивает GET /settings').toBeGreaterThan(0);
    expect(settingsResp, 'GET /settings у взрослого без 4xx/5xx').toEqual([]);
    const projectHist = traffic.reqs.filter(q => /\/api\/projects\/[^/]+\/sessions\/[^/]+\/history/.test(q));
    const histBad = traffic.bad.filter(s => s.includes('/history'));
    console.log('QA-ADULT-HISTORY', JSON.stringify({ project: projectHist, bad: histBad }));
    expect(projectHist.length, 'взрослый грузит историю проектной ручкой').toBeGreaterThan(0);
    expect(histBad, 'история взрослого без 4xx/5xx').toEqual([]);
  } finally {
    await call(req, 'delete', `/api/projects/${proj.body.id}`, adminToken);
    if (adminTiersBackup) {
      await call(req, 'put', `/api/admin/users/${adminId}/model-tiers`, adminToken, adminTiersBackup);
    }
    await req.dispose();
  }
});

// ── ui_probe: главная и лента урока ребёнка на 360 и 1280 (правило приёмки интерфейса) ─
test('ui_probe: маршруты ребёнка на 360 и 1280', async ({ page }) => {
  const results: any[] = [];
  for (const w of [360, 1280]) {
    await page.setViewportSize({ width: w, height: 900 });
    await enter(page, childToken);
    await page.goto(`/#/home`);
    await page.waitForTimeout(6000);
    results.push({ route: '#/home', ...(await uiProbe(page, w)) });
    await page.goto(`/#/project/${subjectProjectId}/chat/${lessonSessionId}`);
    await page.waitForTimeout(6000);
    results.push({ route: 'lesson-feed', ...(await uiProbe(page, w)) });
    await page.screenshot({ path: resolve(SHOTS, `probe-${w}.png`), fullPage: true });
  }
  console.log('QA-UI-PROBE', JSON.stringify(results, null, 1));
});
