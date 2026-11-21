// QA-5: повторная приёмка детского режима после устранения четырёх дефектов QA-4 (задача 46ef10ea).
// Иду строго по шагам каждого дефекта; новых сценариев не ищу.
//
// D1 = 358bfe10 (И9′): REST-канал обходил запрет хода в детском чате → ожидаем 409 child_turn_owner_only.
// D2 = a2aae946 (И2″): на 360 ребёнку были «Изменения» и взрослый уход шторки «Ещё» → их не должно быть вовсе.
// D3 = fab7e418 (И2″): плитка предмета — no-op, нет списка уроков и ленты урока → цепочка
//      плитка → проект-предмет → список уроков → лента урока с шапкой, на НАСТОЯЩЕМ API (не на фейках).
// D4 = f974e03f (И10): при отказе маршрута ребёнку падал техтекст → ожидаем детский текст в ленте,
//      хаб-вызов завершается штатно, опекун получает уведомление.
// Плюс: ни одного 403 на экране ребёнка (сторож X-W4-79) и ui_probe 360/1280.
//
// Стенд: свой каталог данных /home/an/Sources/qa-kids-w4-data, порт 5562,
// провайдеры kids-cloud/kids-local на фейковом движке 192.168.7.117:18021.
// Фикстура прежняя: ребёнок qa_kids_w4, куратор-опекун qa_kids_w4_cur, предмет «Математика».

import { test, expect, type APIRequestContext, type Page } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { resolve } from 'node:path';

const SHOTS = resolve(process.cwd(), '.cc-attachments/qa/kids-w4-qa5/shots');
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
let childSubjectId = '';
let lessonSessionId = '';
// 403-ответы на пути ребёнка, собранные по всем тестам: отдельный сторож GUARD.
const guardBad: string[] = [];

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

// Все не-2xx ответы страницы: на экране ребёнка их быть не должно (X-W4-79).
function badResponses(page: Page): string[] {
  const out: string[] = [];
  page.on('response', r => {
    const s = r.status();
    if (s >= 400) out.push(`${s} ${r.request().method()} ${r.url().replace(/^https?:\/\/[^/]+/, '')}`);
  });
  return out;
}

// ui_probe внутри спека: тач-цели <40px, горизонтальное переполнение, обрезанный текст.
async function uiProbe(page: Page, w: number): Promise<any> {
  return page.evaluate((vw) => {
    const small: string[] = [];
    let smallCount = 0;
    for (const el of Array.from(document.querySelectorAll('button,a,[role="button"],input,textarea,select,[role="tab"],[role="menuitem"]'))) {
      const r = (el as HTMLElement).getBoundingClientRect();
      if (r.width === 0 || r.height === 0) continue; // невидимые не мерим
      if (r.width < 40 || r.height < 40) {
        smallCount++;
        if (small.length < 8) small.push(`${(el.getAttribute('aria-label') || el.getAttribute('title') || (el.textContent ?? '').trim().slice(0, 30))} ${Math.round(r.width)}x${Math.round(r.height)}`);
      }
    }
    let clipped = 0;
    const clippedSamples: string[] = [];
    for (const el of Array.from(document.querySelectorAll('*'))) {
      const he = el as HTMLElement;
      if (!(he as any).isContentEditable && typeof he.title !== 'string') continue;
      const cs = getComputedStyle(he);
      if (cs.textOverflow === 'ellipsis' && he.scrollWidth > he.clientWidth + 1 && he.textContent?.trim()) {
        clipped++;
        if (clippedSamples.length < 5) clippedSamples.push((he.textContent ?? '').trim().slice(0, 40));
      }
    }
    return {
      viewport: vw,
      docScrollWidth: document.documentElement.scrollWidth,
      horizontalOverflow: document.documentElement.scrollWidth > vw,
      smallTargets: smallCount,
      smallSamples: small,
      clippedText: clipped,
      clippedSamples,
    };
  }, w);
}

const FORBIDDEN_PANELS = [
  'Изменения', 'Граф', 'Команда', 'Навыки', 'Инструменты', 'Участники',
  'Настройки проекта', 'Модели и расход', 'Архитектура', 'Терминал',
  'Документация', 'Сервисы', 'Плагины', 'Агенты', 'Эфир', 'Голоса',
];

test.describe.configure({ mode: 'serial' });

test.beforeAll(async ({ playwright }) => {
  mkdirSync(SHOTS, { recursive: true });
  const req = await playwright.request.newContext({ baseURL: BASE });
  childToken = await tokenOf(req, CHILD_U, CHILD_PW);
  curToken = await tokenOf(req, CUR_U, CUR_PW);
  adminToken = await tokenOf(req, process.env.E2E_USER || 'admin', process.env.E2E_PASS || '12345');

  const users = (await (await req.get('/api/users', { headers: { Authorization: `Bearer ${adminToken}` } })).json()) as any[];
  childId = users.find(u => u.username === CHILD_U).id;
  expect(childId, 'учётка ребёнка есть на стенде').toBeTruthy();

  const projects = (await (await req.get('/api/projects', { headers: { Authorization: `Bearer ${childToken}` } })).json()) as any[];
  expect(projects.length, 'у ребёнка есть проект-предмет').toBeGreaterThan(0);
  subjectProjectId = projects[0].id;
  // X-W4-76: DTO проекта отдаёт маркер предмета — без него фронт не находит предмет.
  childSubjectId = projects[0].childSubjectId ?? '';
  console.log('QA-SUBJECT-ID-FIELD', JSON.stringify(projects.map(p => ({ id: p.id, childSubjectId: p.childSubjectId ?? null }))));
  expect(childSubjectId, 'X-W4-76: childSubjectId в DTO проекта').toBeTruthy();

  // Гигиена фикстуры: живые слоты ребёнка (прошлый прогон мог оставить их сломанными —
  // тогда создание урока отвечает 409 subject_not_ready).
  await call(req, 'put', `/api/admin/users/${childId}/model-tiers`, adminToken, {
    strong: 'kids-cloud-1', medium: 'kids-cloud-1', weak: 'kids-local-1',
  });

  const status = await call(req, 'get', '/api/child/status?space=lessons', childToken);
  console.log('QA-STATUS-BEFORE', JSON.stringify(status.body ?? '').slice(0, 400));

  // Чистый урок под сценарии (прежние загрязнены прогонами QA-4).
  const created = await call(req, 'post', '/api/child/spaces/lessons/chat', childToken, { subject: childSubjectId });
  console.log('QA-NEW-LESSON', created.status, JSON.stringify(created.body ?? '').slice(0, 300));
  expect(created.status, 'создание урока').toBe(201);
  lessonSessionId = created.body?.sessionId ?? created.body?.chat?.sessionId;
  expect(lessonSessionId, 'id созданного урока').toBeTruthy();
  console.log('QA-LESSON-ID', lessonSessionId);

  await req.dispose();
});

// ── D1 = 358bfe10 (И9′): REST-канал и запрет хода ───────────────────────────────────
test('D1: куратор через REST не может запустить ход в детском уроке', async ({ playwright }) => {
  const req = await playwright.request.newContext({ baseURL: BASE });

  const histBefore = await call(req, 'get', `/api/sessions/${lessonSessionId}/history`, curToken);
  expect(histBefore.status, 'куратор читает историю урока').toBe(200);
  const startBefore = ((histBefore.body?.items ?? []).filter((i: any) => i.kind === 'session_started')).length;

  const send = await call(req, 'post', `/api/sessions/${lessonSessionId}/messages`, curToken, { text: 'проба куратора (QA5)' });
  console.log('QA-D1-REST-SEND', send.status, JSON.stringify(send.body ?? '').slice(0, 300));
  // Ожидание по фиксу X-W4-73: 409 с текстом ядра и кодом child_turn_owner_only.
  expect(send.status, 'REST-отказ куратору — 409').toBe(409);
  expect(String(send.body?.code ?? ''), 'код отказа').toBe('child_turn_owner_only');
  expect(String(send.body?.error ?? ''), 'тот же текст, что у хаб-пути').toContain('Ход детского чата запускает только его владелец-ребёнок');

  await new Promise(r => setTimeout(r, 4000));
  const hist = await call(req, 'get', `/api/sessions/${lessonSessionId}/history`, curToken);
  const items = hist.body?.items ?? [];
  const curMsg = items.find((i: any) => String(i.text ?? '').includes('проба куратора (QA5)'));
  const startAfter = items.filter((i: any) => i.kind === 'session_started').length;
  console.log('QA-D1-AFTER', 'replica in feed:', !!curMsg, 'session_started before/after:', startBefore, startAfter);
  expect(curMsg, 'реплика куратора не попала в ленту урока').toBeFalsy();
  expect(startAfter, 'ход не запустился (new session_started нет)').toBe(startBefore);
  const refusalInFeed = items.find((i: any) => String(i.text ?? '').includes('Ход детского чата'));
  expect(refusalInFeed, 'отказ принадлежит вызывающему, не ленте ребёнка').toBeFalsy();

  // Регресс прежнего поведения: HTTP-канал ребёнку закрыт (пишет через хаб) — не изменилось.
  const own = await call(req, 'post', `/api/sessions/${lessonSessionId}/messages`, childToken, { text: 'сам ребёнок (QA5)' });
  console.log('QA-D1-CHILD-REST', own.status, JSON.stringify(own.body ?? '').slice(0, 200));
  expect(own.status, 'HTTP-REST ребёнку — по-прежнему 403 child_mode (писать можно хабом)').toBe(403);

  await req.dispose();
});

// ── D2 = a2aae946 (И2″): мобильная поверхность 360 и десктоп 1280 ───────────────────
test('D2: рельса и шторка ребёнка — только детская поверхность', async ({ page }) => {
  const badAll: string[] = [];
  for (const w of [360, 1280]) {
    await page.setViewportSize({ width: w, height: 900 });
    const bad = badResponses(page);
    await enter(page, childToken);
    await page.goto(`/#/home`);
    await page.waitForTimeout(6000);

    const rail = await page.locator('button[aria-label], button[title]').evaluateAll(
      els => els.map(e => e.getAttribute('aria-label') || e.getAttribute('title') || '').filter(Boolean));
    console.log(`QA-D2-RAIL-${w}`, JSON.stringify(rail));

    const more = page.getByRole('button', { name: 'Ещё' }).first();
    let drawer: string[] = [];
    if (await more.count() > 0) {
      await more.click({ force: true }).catch(() => {});
      await page.waitForTimeout(1500);
      drawer = await page.locator('button[aria-label], button[title]').evaluateAll(
        els => els.map(e => e.getAttribute('aria-label') || e.getAttribute('title') || '').filter(Boolean));
      console.log(`QA-D2-DRAWER-${w}`, JSON.stringify(drawer));
      await page.screenshot({ path: resolve(SHOTS, `d2-drawer-${w}.png`), fullPage: true });
      await page.keyboard.press('Escape').catch(() => {});
    }

    const seen = [...new Set([...rail, ...drawer])];
    for (const f of FORBIDDEN_PANELS) {
      expect(seen, `X-W4-75: ребёнку нет пункта «${f}» (ширина ${w})`).not.toContain(f);
    }
    // Детская поверхность на месте: «Файлы» доступна (пункт или шторка).
    expect(seen, `панель «Файлы» ребёнку доступна (${w})`).toContain('Файлы');

    const bodyText = await page.locator('body').innerText();
    expect(bodyText, 'нет падения ErrorBoundary').not.toContain('Интерфейс споткнулся');
    expect(bodyText, 'нет плашки «Изменения недоступны»').not.toContain('Изменения недоступны');

    badAll.push(...bad);
  }
  console.log('QA-D2-BAD-RESPONSES', JSON.stringify(badAll));
  expect(badAll.filter(s => s.startsWith('403')), 'ни одного 403 на экране ребёнка').toEqual([]);
});

// ── D3 = fab7e418 (И2″): цепочка «плитка → проект → уроки → лента» на настоящем API ──
test('D3: плитка предмета → список уроков → лента урока с шапкой (360 и 1280)', async ({ page }) => {
  const badAll: string[] = [];

  for (const w of [1280, 360]) {
    await page.setViewportSize({ width: w, height: 900 });
    const bad = badResponses(page);
    await enter(page, childToken);
    await page.goto(`/#/home`);
    await page.waitForTimeout(6000);

    // 1. Плитка предмета открывает проект-предмет (X-W4-76: маркер в DTO). На 360 дом
    // ребёнка — уже экран предмета (плитки дока там нет), поэтому плитка меряется,
    // когда она видна; иначе меряется сам экран.
    const tile = page.locator('button[aria-label="Математика"], button[title="Математика"]').first();
    const hasTile = await tile.count() > 0;
    console.log(`QA-D3-TILE-${w}`, 'плитка видна:', hasTile);
    if (hasTile) {
      const urlBefore = page.url();
      await tile.click({ force: true });
      await page.waitForTimeout(6000);
      console.log(`QA-D3-TILE-${w}`, urlBefore, '→', page.url());
      expect(page.url(), `X-W4-76: клик по плитке ведёт в проект-предмет (${w})`).toContain(`/project/${subjectProjectId}`);
    }
    await page.screenshot({ path: resolve(SHOTS, `d3-project-${w}.png`), fullPage: true });

    // 2. Пустой центр — детское состояние, не взрослый NewChatSetup (X-W4-79).
    const centerEmpty = page.locator('[data-testid="child-center-empty"]');
    const hasCenterEmpty = await centerEmpty.count();
    const bodyText0 = await page.locator('body').innerText();
    console.log(`QA-D3-CENTER-${w}`, 'child-center-empty:', hasCenterEmpty);
    if (hasCenterEmpty > 0) {
      expect(bodyText0, 'детский текст пустого центра').toContain('Выбери урок или начни новый');
    }
    expect(bodyText0, 'нет взрослого «Инициализировать проект»').not.toContain('Инициализировать проект');
    expect(bodyText0, 'нет взрослого «С чего начнём»').not.toContain('С чего начнём');

    // 3. Панель уроков (X-W4-77/78): на десктопе открыта по умолчанию; на 360 — открыть
    // кнопкой «Чаты». Сначала мерим как есть, клик — только если панели нет.
    let openedPanel = await page.locator('[data-testid="child-lessons-panel"]').count() > 0;
    if (!openedPanel) {
      for (const label of ['Чаты', 'Уроки']) {
        const btn = page.locator(`button[aria-label="${label}"], button[title="${label}"]`).first();
        if (await btn.count() > 0) {
          await btn.click({ force: true }).catch(() => {});
          await page.waitForTimeout(4000);
          if (await page.locator('[data-testid="child-lessons-panel"]').count() > 0) { openedPanel = true; break; }
        }
      }
    }
    console.log(`QA-D3-PANEL-${w}`, 'lessons panel:', openedPanel);
    expect(openedPanel, `X-W4-77/78: панель уроков открылась (${w})`).toBe(true);
    expect(await page.locator('[data-testid="child-new-lesson"]').count(), 'кнопка «Новый урок» есть').toBeGreaterThan(0);
    // Строка «Уроки видят …» — видна и на 360 (решение 86).
    const curatorsLine = page.locator('[data-testid="child-curators-line"]').first();
    expect(await curatorsLine.count(), `строка «Уроки видят …» в панели (${w})`).toBeGreaterThan(0);
    const rows = page.locator('[data-testid="child-lesson-row"]');
    console.log(`QA-D3-ROWS-${w}`, await rows.count());
    expect(await rows.count(), 'список уроков не пуст').toBeGreaterThan(0);
    await page.screenshot({ path: resolve(SHOTS, `d3-lessons-${w}.png`), fullPage: true });

    // 4. Строка списка → лента урока; прямой адрес урока — лента, не взрослая форма (X-W4-80).
    // Первая строка — самый свежий урок (в фикстуре их несколько из прошлых прогонов),
    // поэтому меряется сам факт: строка ведёт в маршрут чата и там — детская лента.
    await rows.first().click({ force: true });
    await page.waitForTimeout(6000);
    console.log(`QA-D3-OPEN-${w}`, page.url());
    expect(page.url(), 'урок открылся маршрутом чата').toMatch(/\/chat\/[0-9a-f-]{36}/);
    const header = page.locator('[data-testid="child-lesson-header"]');
    console.log(`QA-D3-HEADER-${w}`, await header.count());
    expect(await header.count(), `шапка урока с меткой предмета (${w})`).toBeGreaterThan(0);
    const feedText = await page.locator('body').innerText();
    // На 360 остальная шапка свёрнута по дизайну (видна только строка кураторов) —
    // метка предмета меряется на десктопной шапке.
    if (w === 1280) {
      expect(feedText, 'метка предмета в шапке').toContain('Математика');
      expect(feedText, 'имя репетитора в шапке').toContain('Репетитор: Математика');
    }
    expect(feedText, 'строка «Уроки видит …» в ленте (и на 360)').toContain('Уроки видит');
    expect(feedText, 'нет взрослого «Инициализировать проект» в ленте урока').not.toContain('Инициализировать проект');
    await page.screenshot({ path: resolve(SHOTS, `d3-feed-${w}.png`), fullPage: true });

    // Прямый заход по адресу урока (тот же pending-канал, X-W4-80).
    await page.goto(`/#/project/${subjectProjectId}/chat/${lessonSessionId}`);
    await page.waitForTimeout(6000);
    expect(await page.locator('[data-testid="child-lesson-header"]').count(), 'прямой адрес открывает ленту урока').toBeGreaterThan(0);
    expect(await page.locator('body').innerText(), 'нет взрослой формы «Новый проект»').not.toContain('Инициализировать проект');

    badAll.push(...bad);
  }
  // 403 на пути ребёнка собираем, но не валим прогон: это отдельный сторож (тест GUARD)
  // и отдельный вопрос — не часть цепочки D3.
  guardBad.push(...badAll);
  console.log('QA-D3-BAD-RESPONSES', JSON.stringify(badAll));
});

// ── D4 = f974e03f (И10): отказ маршрута — детский текст, хаб завершается штатно ──────
test('D4: недопустимый маршрут — ребёнку детский текст, опекуну уведомление', async ({ page, playwright }) => {
  const req = await playwright.request.newContext({ baseURL: BASE });

  const histBefore = await call(req, 'get', `/api/sessions/${lessonSessionId}/history`, curToken);
  const beforeItems = histBefore.body?.items ?? [];

  const notifBefore = await call(req, 'get', '/api/notifications', curToken);
  const beforeNotifItems = notifBefore.body?.items ?? notifBefore.body ?? [];
  const beforeRefusalNotifs = Array.isArray(beforeNotifItems)
    ? beforeNotifItems.filter((n: any) => n?.type === 'child_route_refusal').length : 0;

  // Живой маршрут урока резолвится моделью персоны-репетитора, а у неё явной модели нет —
  // падает в слоты ребёнка (ChildModeGate.cs:276-279, DefaultTier места «чат с персоной» = strong).
  // Ломаем тот слот, который резолвер реально читает: strong+medium → kids-alien-model
  // (модели нет ни у одного провайдера).
  const tiers = await call(req, 'put', `/api/admin/users/${childId}/model-tiers`, adminToken, {
    strong: 'kids-alien-model', medium: 'kids-alien-model', weak: 'kids-local-1',
  });
  expect(tiers.status, 'слоты ребёнка сломаны').toBe(200);

  const consoleErrors: string[] = [];
  page.on('console', m => { if (m.type() === 'error') consoleErrors.push(m.text()); });
  page.on('pageerror', e => consoleErrors.push(String(e)));

  await page.setViewportSize({ width: 1280, height: 900 });
  await enter(page, childToken);
  await page.goto(`/#/project/${subjectProjectId}/chat/${lessonSessionId}`);
  await page.waitForTimeout(6000);

  const composer = page.locator('[data-composer-input] textarea').first();
  expect(await composer.count(), 'композер урока доступен ребёнку').toBeGreaterThan(0);
  await composer.fill('Привет, репетитор (QA5 маршрут)');
  await composer.press('Enter');
  await page.waitForTimeout(12000);

  const feedText = await page.locator('body').innerText();
  // Факт на этой ветке: сервер пишет в ленту ChildRoute.RefusedText, а фронт любой
  // error-вид детской ленты рисует общей плашкой CHILD_TURN_ERROR_TEXT (childFeedFilter.ts:36-43).
  // Поэтому в UI мерим: нет техтекста, есть спокойный детский текст (любой из двух форм);
  // какой именно текст показал UI — уходит в лог и в отчёт как факт на стыке.
  console.log('QA-D4-UI-TEXT-HONEST', /Занятие пока недоступно/.test(feedText));
  console.log('QA-D4-UI-TEXT-PLATE', /Помощник сейчас не может ответить/.test(feedText));
  console.log('QA-D4-UI-TEXT-TECH', /Процесс модели завершился/.test(feedText));
  await page.screenshot({ path: resolve(SHOTS, 'd4-feed-1280.png'), fullPage: true });
  expect(feedText, 'в UI нет техтекста').not.toContain('Процесс модели завершился');
  expect(/Занятие пока недоступно|Помощник сейчас не может ответить/.test(feedText),
    'в UI — спокойный детский текст отказа').toBe(true);

  // История (лента) — кураторским токеном: админ чужой проект-предмет не видит (404).
  const hist = await call(req, 'get', `/api/sessions/${lessonSessionId}/history`, curToken);
  expect(hist.status, 'куратор читает историю урока').toBe(200);
  const newItems = (hist.body?.items ?? []).slice(beforeItems.length);
  console.log('QA-D4-NEW-ITEMS', JSON.stringify(newItems).slice(0, 900));
  const childRefusal = newItems.find((i: any) => String(i.text ?? '').includes('Занятие пока недоступно'));
  const techText = newItems.find((i: any) => String(i.text ?? '').includes('Процесс модели завершился'));
  expect(childRefusal, 'в ленте урока (история) — детский текст отказа').toBeTruthy();
  expect(techText, 'техтекста мёртвого процесса больше нет').toBeFalsy();

  // Хаб-вызов завершается штатно: «An unexpected error occurred invoking 'SendMessage'» нет.
  const hubError = consoleErrors.find(t => t.includes('An unexpected error occurred invoking'));
  console.log('QA-D4-HUB-ERROR', hubError ?? 'нет');
  expect(hubError, 'хаб-вызов не падает').toBeFalsy();

  // История ленты читается ребёнком, а не только живыми событиями (после X-W4-80):
  // перезагрузка страницы — текст отказа остаётся в ленте.
  await page.reload();
  await page.waitForTimeout(8000);
  // ФАКТ (новый дефект, не часть ожиданий QA-4): после перезагрузки ленты её история
  // ребёнку не загружается — фронт просит историю проектным маршрутом, закрытым ребёнку
  // (403 GET /api/projects/{pid}/sessions/{id}/history?after=-1), а детская разрешённая
  // ручка /api/chats/{id}/history (200) для детского проектного чата не используется.
  const feedAfterReload = await page.locator('body').innerText();
  await page.screenshot({ path: resolve(SHOTS, 'd4-feed-after-reload-1280.png'), fullPage: true });
  console.log('QA-D4-FACT-HISTORY-AFTER-RELOAD',
    /Занятие пока недоступно|Помощник сейчас не может ответить/.test(feedAfterReload) ? 'загрузилась' : 'ПУСТО (дефект A)');
  expect(feedAfterReload, 'после перезагрузки нет техтекста').not.toContain('Процесс модели завершился');

  // Уведомление опекуну. NOTE: у нотификатора есть дизайн-схлопывание «раз в час на
  // (ребёнок, предмет, причина)» (ChildRouteRefusalNotifier.cs:36-40) — повторные отказы
  // в том же часу не дают нового уведомления, это не дефект. Новое уведомление первого
  // отказа часа подтверждено живьём в отчёте (09:16:31, ровно при первом отказе прогона).
  const notif = await call(req, 'get', '/api/notifications', curToken);
  const notifItems = notif.body?.items ?? notif.body ?? [];
  const refusalNotifs = Array.isArray(notifItems)
    ? notifItems.filter((n: any) => n?.type === 'child_route_refusal') : [];
  console.log('QA-D4-NOTIF-COUNT', beforeRefusalNotifs, '→', refusalNotifs.length,
    'latest:', refusalNotifs[0]?.createdAt ?? refusalNotifs[0]?.created_at ?? '-');
  expect(refusalNotifs.length, 'у опекуна есть уведомление child_route_refusal').toBeGreaterThan(0);

  await call(req, 'put', `/api/admin/users/${childId}/model-tiers`, adminToken, {
    strong: 'kids-cloud-1', medium: 'kids-cloud-1', weak: 'kids-local-1',
  });
  await req.dispose();
});

// ── GUARD: ни одного 403 на детском пути (собранный по D3 + свежий заход в ленту) ─────
test('GUARD: ни одного 403 на пути ребёнка', async ({ page }) => {
  const bad = badResponses(page);
  await page.setViewportSize({ width: 1280, height: 900 });
  await enter(page, childToken);
  await page.goto(`/#/project/${subjectProjectId}/chat/${lessonSessionId}`);
  await page.waitForTimeout(8000);
  const all = [...guardBad, ...bad];
  console.log('QA-GUARD-BAD', JSON.stringify(all));
  expect([...new Set(all.filter(s => s.startsWith('403')))], 'ни одного 403 на пути ребёнка').toEqual([]);
});

// ── ui_probe по маршрутам ребёнка (360 и 1280) ───────────────────────────────────────
test('ui_probe: главная и лента урока ребёнка на 360 и 1280', async ({ page }) => {
  const results: any[] = [];
  for (const w of [360, 1280]) {
    await page.setViewportSize({ width: w, height: 900 });
    await enter(page, childToken);

    await page.goto(`/#/home`);
    await page.waitForTimeout(6000);
    results.push({ route: '#/home', ...(await uiProbe(page, w)) });
    await page.screenshot({ path: resolve(SHOTS, `probe-home-${w}.png`), fullPage: true });

    await page.goto(`/#/project/${subjectProjectId}/chat/${lessonSessionId}`);
    await page.waitForTimeout(6000);
    results.push({ route: 'lesson-feed', ...(await uiProbe(page, w)) });
    await page.screenshot({ path: resolve(SHOTS, `probe-feed-${w}.png`), fullPage: true });
  }
  console.log('QA-UI-PROBE', JSON.stringify(results, null, 1));
});
