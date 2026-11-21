// QA-7: финальная приёмка круга закрытия трёх дефектов QA-5 (задача ed6b4dfe; QA-6 1ae702e8
// вернула: двойная плашка ab12ca2d — закрыта доработками К9g-д/К9g-д2). Иду строго по пунктам
// задачи; новых сценариев не ищу.
//
// REFUSE-1..3 = КЛЮЧЕВОЕ (c3, дефект ab12ca2d): сломать живой маршрут, ребёнок пишет в урок —
//   в ленте РОВНО ОДНА плашка с честным текстом, второй общей нет. Три независимых захода
//   (флейк в QA-6 был 2/3). После F5 — тоже одна. История — через /chats-ручку, 403 нет (К9a).
// INV-A/INV-B = обратная сторона инварианта (c4) + лента после F5 с несколькими записями (c1):
//   отказ (блок отказа) → УСПЕШНЫЙ ход (мок-движок) разрывает блок → новый ход падает сбоем —
//   общая плашка нового хода видна, вторая (их ровно две: честная + общая). После F5 состояние
//   то же: честная, общая, текст репетитора и реплики ребёнка — лента не пустая.
// GUARD = c2 (К9б–К9е, К9г): #/home, проект, лента — ни одного ≥400; закрытые ручки
//   (PUT /read, GET /settings, GET /tasks/features*, POST /tts/diag) не опрашиваются; ErrorBoundary нет.
// ADULT = c5 (К9g): у взрослого история грузится, /settings 200, сбои — общей плашкой,
//   детский текст в взрослую ленту не течёт.
// ui_probe: маршруты ребёнка на 360 и 1280 (правило приёмки интерфейса).
//
// Вне круга (по постановке): мобильный список уроков и «notes/chatContext» на 360.
//
// Стенд: свой процесс на :5563 (ASPNETCORE_ENVIRONMENT=Development, cwd backend/ClaudeHomeServer,
// лог /home/an/Sources/qa-kids-w4-stand-qa7.log), DataPath — фикстура /home/an/Sources/qa-kids-w4-data
// (ребёнок qa_kids_w4, куратор qa_kids_w4_cur, предмет «Математика»). Провайдер kids-cloud в
// appsettings.Local.json переведён на мок-движок mock-engine-qa7.mjs (:18111): настоящий фейковый
// движок :18021 в момент приёмки опущен, а сценарию INV нужен управляемый успех и управляемый
// сбой. Мок включается/гасится POST /mock/on|/mock/off (выключенный отвечает 400 — ход падает).

import { test, expect, type APIRequestContext, type Page } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { resolve } from 'node:path';

// process.cwd() прогона — frontend/e2e/qa; снимки — в .cc-attachments от корня дерева.
const SHOTS = resolve(process.cwd(), '../../../.cc-attachments/qa/kids-w4-qa7/shots');
const CHILD_U = 'qa_kids_w4';
const CHILD_PW = 'Kidsqa12345';
const CHILD_TEXT = 'Занятие пока недоступно, взрослым отправлено уведомление.';
const GENERIC_PLATE = 'Помощник сейчас не может ответить';
const MOCK_TEXT = 'Считаем вместе';
const WORKING_TIERS = { strong: 'kids-cloud-1', medium: 'kids-cloud-1', weak: 'kids-local-1' };
const BROKEN_TIERS = { strong: 'kids-alien-model', medium: 'kids-alien-model', weak: 'kids-local-1' };

const BASE = process.env.PLAYWRIGHT_BASE_URL || 'http://127.0.0.1:5563';
const MOCK = process.env.QA7_MOCK_URL || 'http://127.0.0.1:18111';

let childToken = '';
let adminToken = '';
let childId = '';
let adminId = '';
let subjectProjectId = '';
let childSubjectId = '';
let invLessonId = '';
let adminTiersBackup: any = null;
// Закрытые ручки, которые ребёнку просить нельзя (К9б–К9е): [метод, путь-подстрока]
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

// Плашки детской ленты — по их настоящему маркеру (ChatPanel.tsx:2283 data-child-turn-error).
async function plateTexts(page: Page): Promise<string[]> {
  return await page.locator('[data-child-turn-error]').allInnerTexts();
}

async function setTiers(req: APIRequestContext, tiers: object): Promise<void> {
  const r = await call(req, 'put', `/api/admin/users/${childId}/model-tiers`, adminToken, tiers);
  expect(r.status, 'слоты ребёнка выставлены').toBe(200);
}

async function newLesson(req: APIRequestContext): Promise<string> {
  const created = await call(req, 'post', '/api/child/spaces/lessons/chat', childToken, { subject: childSubjectId });
  expect(created.status, 'создание урока').toBe(201);
  const id = created.body?.sessionId ?? created.body?.chat?.sessionId;
  expect(id, 'id созданного урока').toBeTruthy();
  return id;
}

async function mockSwitch(req: APIRequestContext, off: boolean): Promise<void> {
  const r = await req.post(`${MOCK}/mock/${off ? 'off' : 'on'}`);
  expect(r.ok(), `мок ${off ? 'выключен' : 'включён'} (${r.status()})`).toBeTruthy();
}

// Ждать появления текста в ленте (живой ход может длиться минуты; ждём с опросом, не слепой паузой).
async function waitForText(page: Page, needle: string, timeoutMs: number): Promise<boolean> {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    if ((await page.locator('body').innerText()).includes(needle)) return true;
    await page.waitForTimeout(2000);
  }
  return false;
}

async function writeInLesson(page: Page, text: string): Promise<void> {
  const composer = page.locator('[data-composer-input] textarea').first();
  expect(await composer.count(), 'композер урока доступен ребёнку').toBeGreaterThan(0);
  await composer.fill(text);
  await composer.press('Enter');
}

// Диагностика при провале: сырые item'ы истории (kind/subtype/action) — чем мерить, если лента не та.
async function historyDigest(req: APIRequestContext, sessionId: string): Promise<string> {
  const h = await call(req, 'get', `/api/chats/${sessionId}/history`, adminToken);
  const items = Array.isArray(h.body) ? h.body : (h.body?.items ?? []);
  return JSON.stringify((items as any[]).map(it => [it.kind, it.subtype ?? null, it.action ?? null, String(it.text ?? '').slice(0, 40)]));
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
  await setTiers(req, WORKING_TIERS);
  const adminTiers = await call(req, 'get', '/api/me/model-tiers', adminToken);
  adminTiersBackup = adminTiers.status === 200 ? adminTiers.body : null;
  console.log('QA-ADMIN-TIERS-BACKUP', JSON.stringify(adminTiersBackup ?? {}).slice(0, 200));

  // Урок для сценария INV (отказ → успех → сбой) — чистый, создаётся один на весь прогон.
  invLessonId = await newLesson(req);
  console.log('QA-INV-LESSON', invLessonId);

  // Мок должен стоять включённым (его гасит/включает сам спек).
  await mockSwitch(req, false);
  await req.dispose();
});

test.afterAll(async ({ playwright }) => {
  const req = await playwright.request.newContext({ baseURL: BASE });
  await setTiers(req, WORKING_TIERS);
  if (adminTiersBackup) {
    await call(req, 'put', `/api/admin/users/${adminId}/model-tiers`, adminToken, adminTiersBackup);
  }
  try { await mockSwitch(req, false); } catch { /* мок гасится внешним процессом */ }
  await req.dispose();
});

// ── REFUSE-1..3 = КЛЮЧЕВОЕ (c3, ab12ca2d): отказ маршрута — ровно одна честная плашка, ×3 ─
for (const attempt of [1, 2, 3]) {
  test(`REFUSE-${attempt}: отказ маршрута — ровно одна плашка, живой путь и после F5`, { tag: '@refusal' }, async ({ page, playwright }) => {
    const req = await playwright.request.newContext({ baseURL: BASE });

    // Урок создаётся на ЖИВОМ маршруте (создание урока гейтится маршрутом: на сломанных
    // слотах — 409). Затем ломаем слоты моделью, которой нет ни у одного провайдера.
    const lessonId = await newLesson(req);
    await setTiers(req, BROKEN_TIERS);
    console.log(`QA-REFUSE-${attempt}-LESSON`, lessonId);

    const traffic = pageTraffic(page);
    const consoleErrors: string[] = [];
    page.on('console', m => { if (m.type() === 'error') consoleErrors.push(m.text()); });
    page.on('pageerror', e => consoleErrors.push(String(e)));

    await page.setViewportSize({ width: 1280, height: 900 });
    await enter(page, childToken);
    await page.goto(`/#/project/${subjectProjectId}/chat/${lessonId}`);
    await page.waitForTimeout(6000);

    await writeInLesson(page, `отказ QA7 #${attempt}`);
    await page.waitForTimeout(12000);

    // Живой путь: ровно одна плашка — с честным текстом; общей нет (К9f, ab12ca2d).
    const livePlates = await plateTexts(page);
    const liveBody = await page.locator('body').innerText();
    console.log(`QA-REFUSE-${attempt}-LIVE`, JSON.stringify({
      plates: livePlates.length,
      texts: livePlates.map(t => t.slice(0, 60)),
      genericInBody: liveBody.includes(GENERIC_PLATE),
    }));
    await page.screenshot({ path: resolve(SHOTS, `refuse${attempt}-live-1280.png`), fullPage: true });
    expect(livePlates.length, `REFUSE-${attempt}: плашек живого пути не ровно одна (${JSON.stringify(livePlates)})`).toBe(1);
    expect(livePlates[0], 'плашка не с честным текстом отказа').toContain(CHILD_TEXT);
    expect(liveBody, 'К9f/ab12ca2d: рядом с честной — общая плашка').not.toContain(GENERIC_PLATE);
    const hubError = consoleErrors.find(t => t.includes('An unexpected error occurred invoking'));
    expect(hubError, 'хаб-вызов не падает').toBeFalsy();

    // После F5 — тоже одна (К9a: лента не пустая, запись отказа доезжает; ab12ca2d: состояние чистое).
    await page.reload();
    await page.waitForTimeout(8000);
    const afterPlates = await plateTexts(page);
    const afterBody = await page.locator('body').innerText();
    console.log(`QA-REFUSE-${attempt}-AFTER-RELOAD`, JSON.stringify({
      plates: afterPlates.length,
      texts: afterPlates.map(t => t.slice(0, 60)),
      genericInBody: afterBody.includes(GENERIC_PLATE),
    }));
    await page.screenshot({ path: resolve(SHOTS, `refuse${attempt}-after-reload-1280.png`), fullPage: true });
    expect(afterPlates.length, `REFUSE-${attempt}: после F5 плашек не ровно одна`).toBe(1);
    expect(afterPlates[0], 'после F5 плашка не с честным текстом').toContain(CHILD_TEXT);
    expect(afterBody, 'после F5 общей плашки нет').not.toContain(GENERIC_PLATE);
    expect(afterBody, 'после F5 нет падения ErrorBoundary').not.toContain('Интерфейс споткнулся');

    // Контракт загрузки (К9a): история — через разрешённую /chats-ручку, проектную ребёнок не просит; 403 нет.
    const chatsHist = traffic.reqs.filter(q => q.includes(`/api/chats/${lessonId}/history`));
    const projectHist = traffic.reqs.filter(q => /\/api\/projects\/[^/]+\/sessions\/[^/]+\/history/.test(q));
    console.log(`QA-REFUSE-${attempt}-HISTORY-ROUTES`, JSON.stringify({ chats: chatsHist, project: projectHist }));
    expect(chatsHist.length, 'история урока идёт через /api/chats/{id}/history').toBeGreaterThan(0);
    expect(projectHist, 'проектная ручка истории ребёнком не запрашивается').toEqual([]);
    const bad403 = traffic.bad.filter(s => s.startsWith('403'));
    console.log(`QA-REFUSE-${attempt}-BAD`, JSON.stringify(traffic.bad));
    expect(bad403, 'на пути ребёнка нет 403').toEqual([]);

    // Живой маршрут обратно — следующий заход тоже «ломает живой маршрут».
    await setTiers(req, WORKING_TIERS);
    await req.dispose();
  });
}

// ── INV-A (c4 фаза A+B): отказ в уроке INV → успешный ход (мок) разрывает блок отказа ────
test('INV-A: отказ — одна честная; затем успешный ход через мок разрывает блок', { tag: '@refusal' }, async ({ page, playwright }) => {
  test.setTimeout(240_000);
  const req = await playwright.request.newContext({ baseURL: BASE });

  await setTiers(req, BROKEN_TIERS);
  await page.setViewportSize({ width: 1280, height: 900 });
  await enter(page, childToken);
  await page.goto(`/#/project/${subjectProjectId}/chat/${invLessonId}`);
  await page.waitForTimeout(6000);

  await writeInLesson(page, 'отказ INV QA7');
  await page.waitForTimeout(12000);
  const platesA = await plateTexts(page);
  const bodyA = await page.locator('body').innerText();
  console.log('QA-INV-A-REFUSE', JSON.stringify({ plates: platesA.length, texts: platesA.map(t => t.slice(0, 60)) }));
  await page.screenshot({ path: resolve(SHOTS, 'inv-a-refusal-1280.png'), fullPage: true });
  expect(platesA.length, 'в фазе отказа плашек не ровно одна').toBe(1);
  expect(platesA[0], 'фаза отказа — не честный текст').toContain(CHILD_TEXT);
  expect(bodyA, 'в фазе отказа общей плашки нет').not.toContain(GENERIC_PLATE);

  // Живой маршрут обратно, мок включён — успешный ход должен разорвать блок отказа.
  await setTiers(req, WORKING_TIERS);
  await mockSwitch(req, false);
  await writeInLesson(page, 'реши 2+2 (INV успех QA7)');
  const ok = await waitForText(page, MOCK_TEXT, 150_000);
  console.log('QA-INV-B-SUCCESS', JSON.stringify({ mockTextSeen: ok, digest: ok ? null : await historyDigest(req, invLessonId) }));
  await page.screenshot({ path: resolve(SHOTS, 'inv-b-success-1280.png'), fullPage: true });
  expect(ok, 'успешный ход через мок-движок не дошёл до ленты (см. QA-INV-B-SUCCESS digest и лог стенда)').toBe(true);

  await req.dispose();
});

// ── INV-B (c4 + c1): новый ход падает сбоем — общая плашка жива; после F5 лента с записями ─
test('INV-B: сбой нового хода — общая плашка видна; после F5 лента не пустая', { tag: '@refusal' }, async ({ page, playwright }) => {
  test.setTimeout(180_000);
  const req = await playwright.request.newContext({ baseURL: BASE });

  // Мок выключен: живой маршрут цел, но ход падает на движке (любой сбой — result(error)).
  await mockSwitch(req, true);
  await page.setViewportSize({ width: 1280, height: 900 });
  await enter(page, childToken);
  await page.goto(`/#/project/${subjectProjectId}/chat/${invLessonId}`);
  await page.waitForTimeout(6000);

  await writeInLesson(page, 'реши ещё раз (INV сбой QA7)');
  const failed = await waitForText(page, GENERIC_PLATE, 120_000);
  const platesC = await plateTexts(page);
  console.log('QA-INV-C-FAILURE', JSON.stringify({
    genericSeen: failed,
    plates: platesC.length,
    texts: platesC.map(t => t.slice(0, 60)),
    digest: failed ? null : await historyDigest(req, invLessonId),
  }));
  await page.screenshot({ path: resolve(SHOTS, 'inv-c-failure-1280.png'), fullPage: true });
  // Обратная сторона инварианта (К9g-д2): гашение только в блоке отказанного хода —
  // сбой другого хода получает свою общую плашку; их ровно две: честная + общая.
  expect(failed, 'сбой нового хода не дал общей плашки (см. QA-INV-C-FAILURE)').toBe(true);
  expect(platesC.length, `плашек в ленте не ровно две (${JSON.stringify(platesC)})`).toBe(2);
  expect(platesC[0], 'первая плашка — не честный текст отказа').toContain(CHILD_TEXT);
  expect(platesC[1], 'вторая плашка — не общая текста сбоя').toContain(GENERIC_PLATE);

  // c1: после F5 лента не пустая — несколько записей подряд: отказ, реплики ребёнка, текст
  // репетитора, общая плашка нового сбоя. Состояние то же, что живом пути.
  await page.reload();
  await page.waitForTimeout(8000);
  const afterPlates = await plateTexts(page);
  const afterBody = await page.locator('body').innerText();
  const entries = {
    refusal: afterBody.includes(CHILD_TEXT),
    successText: afterBody.includes(MOCK_TEXT),
    childMsgB: afterBody.includes('реши 2+2 (INV успех QA7)'),
    childMsgC: afterBody.includes('реши ещё раз (INV сбой QA7)'),
  };
  console.log('QA-INV-AFTER-RELOAD', JSON.stringify({ plates: afterPlates.length, texts: afterPlates.map(t => t.slice(0, 60)), entries }));
  await page.screenshot({ path: resolve(SHOTS, 'inv-after-reload-1280.png'), fullPage: true });
  // «не пустая, несколько записей» мерим наличием самих записей, а не отсутствием empty-state.
  expect(entries.refusal, 'после F5 записи отказа нет').toBe(true);
  expect(entries.successText, 'после F5 текста репетитора нет').toBe(true);
  expect(entries.childMsgB, 'после F5 реплики ребёнка (успешный ход) нет').toBe(true);
  expect(afterPlates.length, 'после F5 плашек не ровно две').toBe(2);
  expect(afterPlates[0], 'после F5 первая плашка не честная').toContain(CHILD_TEXT);
  expect(afterPlates[1], 'после F5 вторая плашка не общая').toContain(GENERIC_PLATE);
  expect(afterBody, 'после F5 нет падения ErrorBoundary').not.toContain('Интерфейс споткнулся');

  await mockSwitch(req, false);
  await req.dispose();
});

// ── GUARD = c2 (К9б–К9е, К9г): ни одного 403 на детском пути; закрытые ручки не опрашиваются ─
test('GUARD: страницы ребёнка — ни 403, ни закрытых ручек, ни ErrorBoundary', async ({ page }) => {
  const badAll: string[] = [];
  const reqsAll: string[] = [];

  for (const route of [`#/${'home'}`, `#/project/${subjectProjectId}`, `#/project/${subjectProjectId}/chat/${invLessonId}`]) {
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

// ── ADULT = c5 (К9g): взрослый путь не сломан ────────────────────────────────────────────
test('ADULT: взрослый — история грузится, /settings 200, сбой общей плашкой без детского текста', async ({ page, playwright }) => {
  const req = await playwright.request.newContext({ baseURL: BASE });

  // Тестовый взрослый чат (свой проект, убирается в finally этого теста).
  const uniq = `qa-kids7-${Date.now()}`;
  const proj = await call(req, 'post', '/api/projects', adminToken, {
    name: uniq, rootPath: `/tmp/${uniq}`, createDirectory: true,
  });
  expect(proj.status, 'создан тестовый проект').toBe(201);
  const sess = await call(req, 'post', `/api/projects/${proj.body.id}/sessions`, adminToken, { name: 'qa-kids7-chat' });
  expect(sess.status, 'создан тестовый чат').toBe(201);

  try {
    // Ломаем взрослый маршрут той же недоступной моделью — сбой показывается обычным
    // взрослым путём, детский текст отказа в взрослую ленту не течёт.
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
    await composer.fill('проба взрослого (QA7)');
    await composer.press('Enter');
    await page.waitForTimeout(12000);

    const liveText = await page.locator('body').innerText();
    console.log('QA-ADULT-LIVE', JSON.stringify({
      ownMessage: liveText.includes('проба взрослого (QA7)'),
      childText: liveText.includes(CHILD_TEXT),
    }));
    await page.screenshot({ path: resolve(SHOTS, 'adult-feed-live-1280.png'), fullPage: true });
    expect(liveText, 'сообщение взрослого ушло в ленту').toContain('проба взрослого (QA7)');
    expect(liveText, 'детский текст отказа не течёт в взрослую ленту').not.toContain(CHILD_TEXT);

    // История взрослого: после F5 лента не пустая, грузится проектной ручкой (200).
    await page.reload();
    await page.waitForTimeout(8000);
    const afterReload = await page.locator('body').innerText();
    await page.screenshot({ path: resolve(SHOTS, 'adult-feed-after-reload-1280.png'), fullPage: true });
    expect(afterReload, 'после F5 лента взрослого не пустая').toContain('проба взрослого (QA7)');
    expect(afterReload, 'нет падения ErrorBoundary у взрослого').not.toContain('Интерфейс споткнулся');

    const settingsReqs = traffic.reqs.filter(q => q.startsWith('GET /api/settings'));
    const settingsBad = traffic.bad.filter(s => s.includes('/api/settings'));
    console.log('QA-ADULT-SETTINGS', JSON.stringify({ requests: settingsReqs, bad: settingsBad }));
    expect(settingsReqs.length, 'взрослый опрашивает GET /settings').toBeGreaterThan(0);
    expect(settingsBad, 'GET /settings у взрослого без 4xx/5xx').toEqual([]);
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

// ── ui_probe: главная и лента урока ребёнка на 360 и 1280 (правило приёмки интерфейса) ────
test('ui_probe: маршруты ребёнка на 360 и 1280', async ({ page }) => {
  const results: any[] = [];
  for (const w of [360, 1280]) {
    await page.setViewportSize({ width: w, height: 900 });
    await enter(page, childToken);
    await page.goto(`/#/home`);
    await page.waitForTimeout(6000);
    results.push({ route: '#/home', ...(await uiProbe(page, w)) });
    await page.goto(`/#/project/${subjectProjectId}/chat/${invLessonId}`);
    await page.waitForTimeout(6000);
    results.push({ route: 'lesson-feed', ...(await uiProbe(page, w)) });
    await page.screenshot({ path: resolve(SHOTS, `probe-${w}.png`), fullPage: true });
  }
  console.log('QA-UI-PROBE', JSON.stringify(results, null, 1));
});
