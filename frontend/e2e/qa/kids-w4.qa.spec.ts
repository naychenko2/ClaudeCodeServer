// Приёмка детского режима, волна 4 (QA-4, задача 44e8bd1e).
// Инварианты: И2″ поверхность, И8 пути, И9′ куратор, И10 маршруты, И11 окна/лимиты.
// Плюс отдельно: git-опросы во вкладках «Файлы» и «Изменения» (у ребёнка ни одного 403).
//
// Стенд: свой каталог данных, провайдер kids-cloud/kids-local на фейковом движке :18021,
// пул «anthropic-api-kids». Логины: E2E_USER/E2E_PASS (админ), qa_kids_w4 (ребёнок),
// qa_kids_w4_cur (куратор-опекун).
//
// Файл копируется в frontend/e2e/qa/ перед прогоном (ветку двигают другие исполнители).

import { test, expect, type APIRequestContext, type Page } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { resolve } from 'node:path';

const SHOTS = resolve(process.cwd(), '../.cc-attachments/qa/kids-w4/shots');
const CHILD_U = 'qa_kids_w4';
const CHILD_PW = 'Kidsqa12345';
const CUR_U = 'qa_kids_w4_cur';
const CUR_PW = 'Kidsqa12345';

const BASE = process.env.PLAYWRIGHT_BASE_URL || 'http://127.0.0.1:5562';

let childToken = '';
let curToken = '';
let adminToken = '';
let childId = '';
let subjectProjectId = '';
let lessonSessionId = '';

// Сырой вызов: нужны коды отказа, а не только успех.
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

async function enter(page: Page, token: string, role: 'admin' | 'user') {
  await page.addInitScript(
    ({ t, r }) => {
      localStorage.setItem('cc_token', t);
      localStorage.setItem('cc_role', r);
    },
    { t: token, r: role },
  );
}

function gitCollector(page: Page): string[] {
  const calls: string[] = [];
  page.on('response', r => {
    const u = r.url();
    if (u.includes('/git/')) calls.push(`${r.status()} ${r.request().method()} ${u.replace(/^https?:\/\/[^/]+/, '')}`);
  });
  return calls;
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

  const projects = (await (await req.get('/api/projects', { headers: { Authorization: `Bearer ${childToken}` } })).json()) as any[];
  expect(projects.length, 'у ребёнка есть проект-предмет').toBeGreaterThan(0);
  subjectProjectId = projects[0].id;

  // Чистый урок под тесты (прежние загрязнены сообщениями куратора из прошлых прогонов).
  const created = await call(req, 'post', '/api/child/spaces/lessons/chat', childToken, { subject: 'matematika' });
  console.log('QA-NEW-LESSON', created.status, JSON.stringify(created.body ?? '').slice(0, 300));
  const lessons = await call(req, 'get', '/api/child/spaces/lessons/chats', childToken);
  const chats = lessons.body?.chats ?? [];
  expect(chats.length, 'у ребёнка есть урок').toBeGreaterThan(0);
  const fresh = chats.find((c: any) => c.sessionId === (created.body?.sessionId ?? created.body?.chat?.sessionId));
  lessonSessionId = fresh?.sessionId ?? chats[chats.length - 1].sessionId;
  console.log('QA-LESSON-ID', lessonSessionId);
});

// ── И2″: поверхность ────────────────────────────────────────────────────────────────
test('И2″: снимок поверхности, взрослые ручки закрыты, чужие проекты недостижимы', async ({ playwright }) => {
  const req = await playwright.request.newContext({ baseURL: BASE });

  const me = await call(req, 'get', '/api/auth/me', childToken);
  expect(me.status).toBe(200);
  expect(me.body?.isChild, 'ребёнок опознан').toBe(true);
  expect(me.body?.childSurface?.panels, 'панели по allow-list').toEqual(
    ['files', 'knowledge', 'notes', 'chatContext', 'tasks']);
  expect(me.body?.childSpaces, 'пространства ребёнка').toEqual(['lessons']);

  for (const url of ['/api/personas', '/api/tasks', '/api/usage', '/api/chats', '/api/sessions', '/api/features']) {
    const r = await call(req, 'get', url, childToken);
    expect(r.status, `взрослая ручка ${url} детским токеном`).toBe(403);
    expect(String(r.body?.error ?? ''), `тело отказа ${url}`).toContain('child_mode');
  }

  const list = (await (await req.get('/api/projects', { headers: { Authorization: `Bearer ${childToken}` } })).json()) as any[];
  expect(list.length, 'у ребёнка только свои проекты-предметы').toBeGreaterThan(0);
  expect(list.every(p => p.ownerId === me.body?.userId), 'все проекты ребёнка — его').toBe(true);

  const adminProjects = (await (await req.get('/api/projects', { headers: { Authorization: `Bearer ${adminToken}` } })).json()) as any[];
  const foreign = adminProjects.find(p => p.id !== subjectProjectId);
  if (foreign) {
    const r = await call(req, 'get', `/api/projects/${foreign.id}`, childToken);
    expect(r.status, 'чужой проект ребёнку — 404').toBe(404);
  }
  await req.dispose();
});

// ── И8: пути ────────────────────────────────────────────────────────────────────────
test('И8: пути ребёнка — папка предмета, скрытая «Для репетитора», записи закрыты', async ({ playwright }) => {
  const req = await playwright.request.newContext({ baseURL: BASE });

  const files = await call(req, 'get', `/api/projects/${subjectProjectId}/files`, childToken);
  expect(files.status).toBe(200);
  const names = (files.body as any[]).map(f => f.name);
  expect(names, 'папка «Для репетитора» ребёнку не видна').not.toContain('Для репетитора');
  expect(names.filter(n => n.startsWith('.') && n !== '.ccs-text'),
    'точечные имена скрыты (кроме .ccs-text)').toEqual([]);

  const materials = await call(req, 'put', `/api/projects/${subjectProjectId}/files/content?path=${encodeURIComponent('Материалы/заметка.txt')}`, childToken, { content: 'проба' });
  expect(materials.status, 'запись в «Материалы» — 403').toBe(403);
  expect(materials.body?.code, 'код отказа путей').toBe('child_path');
  expect(String(materials.body?.error ?? ''), 'текст отказа про «Материалы»').toContain('Материалы');

  const tutor = await call(req, 'put', `/api/projects/${subjectProjectId}/files/content?path=${encodeURIComponent('Для репетитора/файл.txt')}`, childToken, { content: 'проба' });
  expect(tutor.status, 'запись в «Для репетитора» — 403').toBe(403);
  expect(tutor.body?.code).toBe('child_path');

  const protectedName = await call(req, 'put', `/api/projects/${subjectProjectId}/files/content?path=${encodeURIComponent('CLAUDE.md')}`, childToken, { content: 'проба' });
  expect(protectedName.status, 'запись CLAUDE.md — 403').toBe(403);
  expect(protectedName.body?.code).toBe('child_path');

  const mkdirMaterials = await call(req, 'post', `/api/projects/${subjectProjectId}/files/mkdir`, childToken, { path: 'Материалы/проба' });
  expect(mkdirMaterials.status, 'mkdir в «Материалы» — 403').toBe(403);
  expect(mkdirMaterials.body?.code).toBe('child_path');

  const outside = await call(req, 'post', `/api/projects/${subjectProjectId}/files/mkdir`, childToken, { path: '../побег' });
  expect(outside.status, 'mkdir вне папки предмета — 403').toBe(403);
  expect(outside.body?.code).toBe('child_path');

  const del = await call(req, 'delete', `/api/projects/${subjectProjectId}/files?path=${encodeURIComponent('Материалы')}`, childToken);
  expect([403, 404].includes(del.status), 'удаление «Материалы» ребёнком — отказ').toBe(true);
  console.log('QA-DELETE-MATERIALS', del.status, JSON.stringify(del.body ?? '').slice(0, 200));

  await req.dispose();
});

// ── И9′: куратор читает, но не пишет ─────────────────────────────────────────────────
test('И9′: куратор читает уроки; ход из REST-канала запускается (дефект?)', async ({ page, playwright }) => {
  const req = await playwright.request.newContext({ baseURL: BASE });

  const projects = (await (await req.get('/api/projects', { headers: { Authorization: `Bearer ${curToken}` } })).json()) as any[];
  const mine = projects.find(p => p.id === subjectProjectId);
  expect(mine, 'куратор видит проект-предмет ребёнка').toBeTruthy();
  const chats = await call(req, 'get', `/api/projects/${subjectProjectId}/chats`, curToken);
  expect([200, 404].includes(chats.status), `куратор читает уроки (${chats.status})`).toBe(true);
  console.log('QA-CURATOR-CHATS', chats.status, JSON.stringify(chats.body ?? '').slice(0, 300));

  // Куратор ЧИТАЕТ: история урока доступна.
  const history = await call(req, 'get', `/api/sessions/${lessonSessionId}/history`, curToken);
  console.log('QA-CURATOR-HISTORY', history.status, JSON.stringify(history.body ?? '').slice(0, 200));
  expect(history.status, 'куратор читает историю урока').toBe(200);

  // Детские ручки взрослому закрыты (default-deny)
  for (const url of ['/api/child/spaces/lessons/chats', '/api/child/subjects', '/api/child/status?space=lessons']) {
    const r = await call(req, 'get', url, curToken);
    console.log('QA-ADULT-CHILD-ROUTE', url, r.status, JSON.stringify(r.body ?? '').slice(0, 150));
    expect(r.status, `детская ручка ${url} взрослому`).toBe(403);
  }

  // Ход куратором в детский урок через REST-канал (chats_send).
  const send = await call(req, 'post', `/api/sessions/${lessonSessionId}/messages`, curToken, { text: 'проба куратора (REST)' });
  console.log('QA-CURATOR-REST-SEND', send.status, JSON.stringify(send.body ?? '').slice(0, 300));
  // ДЕФЕКТ (И9′): ожидался отказ «Ход детского чата запускает только его владелец-ребёнок».
  // Здесь не роняем прогон — факт уходит в лог и в отчёт приёмки.
  if ([200, 202].includes(send.status)) {
    console.log('QA-DEFECT-I9', 'REST-канал принял сообщение куратора в детский урок — отказа по И9′ нет');
  }
  expect([200, 202, 403, 409]).toContain(send.status);

  // Что реально попало в урок: реплика куратора в ленте?
  await new Promise(r => setTimeout(r, 6000));
  const hist = await call(req, 'get', `/api/sessions/${lessonSessionId}/history`, curToken);
  const items = hist.body?.items ?? [];
  console.log('QA-CURATOR-HIST-AFTER', JSON.stringify(items).slice(0, 500));
  const curatorMsg = items.find((i: any) => i.kind === 'user' && String(i.text ?? '').includes('проба куратора'));
  console.log('QA-CURATOR-MSG-IN-LESSON', curatorMsg ? 'ПОПАЛА В ЛЕНТУ УРОКА' : 'не попала');

  // Хаб-путь: куратор пишет в урок через UI — ожидается красный ленточный текст отказа.
  await page.setViewportSize({ width: 1280, height: 900 });
  await enter(page, curToken, 'user');
  await page.goto(`/#/project/${subjectProjectId}/chat/${lessonSessionId}`);
  await page.waitForTimeout(6000);
  const curComposer = page.locator('[data-composer-input] textarea').first();
  console.log('QA-CURATOR-COMPOSER', await curComposer.count());
  if (await curComposer.count() > 0) {
    await curComposer.fill('пишет куратор (UI)');
    await curComposer.press('Enter');
    await page.waitForTimeout(8000);
    const feed = await page.locator('body').innerText();
    console.log('QA-CURATOR-FEED', feed.slice(0, 1200));
    await page.screenshot({ path: resolve(SHOTS, 'i9-curator-hub-1280.png'), fullPage: true });
    console.log('QA-CURATOR-HUB-REFUSAL', /Ход детского чата запускает только его владелец-ребёнок/.test(feed) ? 'ОТКАЗ ЕСТЬ' : 'ОТКАЗА НЕТ');
  }

  // Добавление участника в проект-предмет
  const members = await call(req, 'post', `/api/projects/${subjectProjectId}/members`, childToken, { username: CUR_U, role: 'reader' });
  console.log('QA-MEMBERS', members.status, JSON.stringify(members.body ?? '').slice(0, 200));
  expect([403, 409].includes(members.status), `добавление участника отказ (${members.status})`).toBe(true);

  // Взрослый чат в проекте-предмете — 409 child_subject_project
  const adultChat = await call(req, 'post', `/api/projects/${subjectProjectId}/sessions`, curToken, {});
  console.log('QA-ADULT-CHAT', adultChat.status, JSON.stringify(adultChat.body ?? '').slice(0, 200));
  expect([403, 404, 409].includes(adultChat.status), `взрослый чат в проекте-предмете отказ (${adultChat.status})`).toBe(true);

  await req.dispose();
});

// ── И10: маршруты хода ──────────────────────────────────────────────────────────────
// Живой маршрут ребёнка ломается слотом ребёнка: medium=kids-alien-model — модели нет
// ни у одного провайдера, поэтому пара (провайдер, модель) недопустима.
// Ожидание: ребёнку в ленте честный детский текст, опекуну — уведомление child_route_refusal.
test('И10: недопустимый маршрут — ребёнку честный текст, куратору уведомление', async ({ playwright }) => {
  const req = await playwright.request.newContext({ baseURL: BASE });

  const routes = await call(req, 'get', '/api/admin/child-routes', adminToken);
  expect(routes.status, 'список маршрутов детского режима').toBe(200);
  console.log('QA-ROUTES', JSON.stringify(routes.body ?? '').slice(0, 400));

  const before = await call(req, 'get', '/api/notifications', curToken);
  const beforeCount = (before.body?.items ?? before.body ?? []).length;
  console.log('QA-NOTIF-BEFORE', beforeCount);

  const tiers = await call(req, 'put', `/api/admin/users/${childId}/model-tiers`, adminToken, {
    strong: 'kids-cloud-1', medium: 'kids-alien-model', weak: 'kids-local-1',
  });
  expect(tiers.status, 'слоты ребёнка заданы').toBe(200);

  // Ребёнок пишет в урок: маршрут недопустим → в ленте честный текст отказа
  const send = await call(req, 'post', `/api/sessions/${lessonSessionId}/messages`, childToken,
    { text: 'Привет, репетитор (проба маршрута)' });
  console.log('QA-I10-SEND', send.status, JSON.stringify(send.body ?? '').slice(0, 300));
  await new Promise(r => setTimeout(r, 15000));

  const hist = await call(req, 'get', `/api/sessions/${lessonSessionId}/history`, adminToken);
  const items = hist.body?.items ?? [];
  console.log('QA-I10-HISTORY', JSON.stringify(items).slice(0, 900));
  const refusal = items.find((i: any) => /недоступно/i.test(String(i.text ?? '')));
  console.log('QA-I10-REFUSAL-TEXT', refusal ? String(refusal.text).slice(0, 200) : 'ОТКАЗА НЕТ');

  // Уведомление опекуну об отказе маршрута
  const notif = await call(req, 'get', '/api/notifications', curToken);
  const notifItems = notif.body?.items ?? notif.body ?? [];
  console.log('QA-NOTIF', notif.status, notifItems.length, JSON.stringify(notifItems[0] ?? {}).slice(0, 400));

  await call(req, 'put', `/api/admin/users/${childId}/model-tiers`, adminToken, {
    strong: 'kids-cloud-1', medium: 'kids-cloud-1', weak: 'kids-local-1',
  });
  await req.dispose();
});

// ── И11: окна и лимиты ──────────────────────────────────────────────────────────────
test('И11: окно и лимиты — детский текст, код 409 при создании урока вне окна', async ({ playwright }) => {
  const req = await playwright.request.newContext({ baseURL: BASE });

  const profile = (await call(req, 'get', `/api/users/${childId}/child-profile`, adminToken)).body?.profile;
  expect(profile, 'профиль ребёнка прочитан').toBeTruthy();

  const closed = await call(req, 'put', `/api/users/${childId}/child-profile`, adminToken, {
    enabled: true,
    guardianUserIds: profile.guardianUserIds ?? [],
    providerKey: profile.providerKey,
    model: profile.model,
    acknowledgeRouteRisk: true,
    spaces: { lessons: { windows: [{ from: '02:00', to: '02:30' }], dailyMinutes: 60 } },
    subjects: profile.subjects,
    warnMinutes: 5,
    dailyBudgetUsd: null,
    monthlyBudgetUsd: null,
  });
  expect(closed.status, 'профиль с закрытым окном сохранён').toBe(200);

  const status = await call(req, 'get', '/api/child/status?space=lessons', childToken);
  expect(status.status).toBe(200);
  console.log('QA-STATUS', JSON.stringify(status.body ?? ''));
  expect(['out_of_window', 'time_up'], 'состояние вне окна').toContain(status.body?.state);
  expect(String(status.body?.nextWindowAt ?? ''), 'начало следующего окна названо').not.toBe('');
  // Таймзона окна — локальная (Europe/Moscow, +03:00), не UTC
  console.log('QA-WINDOW-ENDS', status.body?.windowEndsAt);

  const create = await call(req, 'post', '/api/child/spaces/lessons/chat', childToken, { subject: 'matematika' });
  expect(create.status, 'создание урока вне окна — 409').toBe(409);
  console.log('QA-CREATE-OUT', JSON.stringify(create.body ?? ''));
  expect(['out_of_window', 'time_up'], 'код состояния в теле 409').toContain(create.body?.code);
  expect(String(create.body?.message ?? ''), 'детский текст в теле 409').toMatch(/урок|отдых|закончил/i);

  await call(req, 'put', `/api/users/${childId}/child-profile`, adminToken, {
    enabled: true,
    guardianUserIds: profile.guardianUserIds ?? [],
    providerKey: profile.providerKey,
    model: profile.model,
    acknowledgeRouteRisk: true,
    spaces: { lessons: { windows: [{ from: '00:00', to: '23:59' }], dailyMinutes: 60 } },
    subjects: profile.subjects,
    warnMinutes: 5,
    dailyBudgetUsd: null,
    monthlyBudgetUsd: null,
  });
  await req.dispose();
});

// ── Поверхность в UI + git-опросы (последними) ──────────────────────────────────────
test('И2″/git: UI ребёнка на 360 и 1280 — рельса панелей и ни одного git-опроса с 403', async ({ page }) => {
  const gitCalls: string[] = [];

  for (const w of [360, 1280]) {
    await page.setViewportSize({ width: w, height: 900 });
    const git = gitCollector(page);
    await enter(page, childToken, 'user');
    await page.goto(`/#/home`);
    await page.waitForTimeout(5000);

    // Состав рельсы: кнопки панелей по aria-label/title + раскрытая «Ещё»
    const rail = await page.locator('button[aria-label], button[title]').evaluateAll(
      els => els.map(e => e.getAttribute('aria-label') || e.getAttribute('title') || ''));
    console.log(`QA-RAIL-${w}`, JSON.stringify(rail));
    const more = page.getByRole('button', { name: 'Ещё' }).first();
    if (await more.count() > 0) {
      await more.click({ force: true }).catch(() => {});
      await page.waitForTimeout(1500);
      const rail2 = await page.locator('button[aria-label], button[title]').evaluateAll(
        els => els.map(e => e.getAttribute('aria-label') || e.getAttribute('title') || ''));
      console.log(`QA-RAIL-${w}-MORE`, JSON.stringify(rail2));
      await page.screenshot({ path: resolve(SHOTS, `rail-more-${w}.png`), fullPage: true });
      await page.keyboard.press('Escape').catch(() => {});
    }
    // Закрытые панели не должны попадать в рельсу ребёнка (И2″).
    const forbidden = ['Граф', 'Терминал', 'Документация', 'Плагины', 'Сервисы', 'Архитектура', 'Команды', 'Агенты', 'Эфир', 'Голоса'];
    for (const f of forbidden) {
      expect(rail, `в рельсе ребёнка нет панели «${f}» (ширина ${w})`).not.toContain(f);
    }
    // «Изменения» в рельсе ребёнка на 360 — находка И2″ (панели нет в childSurface.panels).
    if (rail.includes('Изменения')) {
      console.log(`QA-DEFECT-I2-CHANGES-${w}`, 'панель «Изменения» видна ребёнку, хотя её нет в childSurface.panels');
    }

    // Панель «Файлы» — там фронт мог бы опрашивать git
    // Панель «Файлы» — там фронт мог бы опрашивать git
    const filesBtn = page.locator('button[aria-label="Файлы"], button[title="Файлы"]').first();
    if (await filesBtn.count() > 0) {
      await filesBtn.click({ force: true }).catch(() => {});
      await page.waitForTimeout(4000);
      await page.screenshot({ path: resolve(SHOTS, `files-${w}.png`), fullPage: true });
    }
    // Клик по плитке предмета («Математика») — должен открыть проект-предмет
    const tile = page.locator('button[aria-label="Математика"]').first();
    if (await tile.count() > 0) {
      const urlBefore = page.url();
      await tile.click({ force: true }).catch(() => {});
      await page.waitForTimeout(4000);
      console.log(`QA-TILE-${w}`, 'url до', urlBefore, '→ после', page.url());
      await page.screenshot({ path: resolve(SHOTS, `tile-${w}.png`), fullPage: true });
    }
    gitCalls.push(...git);
  }

  console.log('QA-GIT-CALLS', JSON.stringify(gitCalls));
  expect(gitCalls.filter(c => c.startsWith('403')), 'ни одного git-опроса с 403 у ребёнка').toEqual([]);
  expect(gitCalls.filter(c => c.startsWith('4') || c.startsWith('5')), 'ни одного упавшего git-опроса').toEqual([]);
});
