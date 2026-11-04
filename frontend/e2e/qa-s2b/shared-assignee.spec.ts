// QA-приёмка блока S2b «назначение исполнителя-участника» (фича «Общие проекты»).
// Стенд: ветка feat/shared-projects, порт 5557. Учётки: admin (владелец А), qa_b (редактор Б),
// qa_c (читатель В), qa_d (посторонний Г), qa_e (второй участник — фикстура дорожки
// «Без исполнителя» после отзыва).

import { test, expect, type Page, type APIRequestContext } from '@playwright/test';
import { readFileSync, writeFileSync } from 'node:fs';

const BASE = process.env.PLAYWRIGHT_BASE_URL || 'http://127.0.0.1:5557';
const PASS: Record<string, string> = { admin: '12345', qa_b: 'qa-pass-123', qa_c: 'qa-pass-123', qa_e: 'qa-pass-123', qa_d: 'qa-pass-123' };
const SHOTS = '/home/an/Sources/ClaudeCodeServer-shared-projects/.cc-attachments/qa-s2b';
const FX = JSON.parse(readFileSync(new URL('./fixture.json', import.meta.url).pathname, 'utf8'));
const PROJECT_ID: string = FX.projectId;
const ADMIN_ID: string = FX.adminId;
const QA_B_ID: string = FX.bId;
const QA_C_ID: string = FX.cId;
const QA_E_ID: string = FX.eId;
const T = FX.tasks;
const TODAY = '2026-11-01';

const report: string[] = [];
function note(s: string) { report.push(s); writeFileSync(`${SHOTS}/report.txt`, report.join('\n')); }

let adminToken = '';

async function adm(method: string, path: string, body?: unknown) {
  const r = await fetch(`${BASE}${path}`, {
    method,
    headers: { Authorization: `Bearer ${adminToken}`, 'Content-Type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  const txt = await r.text();
  let parsed: any = txt;
  try { parsed = JSON.parse(txt); } catch { /* текст */ }
  return { status: r.status, body: parsed };
}

async function login(page: Page, username: string) {
  await page.goto(`${BASE}/#/home`);
  await page.evaluate(async ({ u, p }) => {
    const r = await fetch('/api/auth/login', {
      method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ username: u, password: p }),
    });
    const raw = await r.text();
    if (!r.ok) throw new Error(`login ${u}: ${r.status} ${raw.slice(0, 200)}`);
    const j = JSON.parse(raw);
    sessionStorage.setItem('cc_token', j.token);
    const me = await fetch('/api/auth/me', { headers: { Authorization: `Bearer ${j.token}` } });
    const mraw = await me.text();
    const m = mraw ? JSON.parse(mraw) : {};
    sessionStorage.setItem('cc_user_id', m.userId || '');
    if (m.role) sessionStorage.setItem('cc_role', m.role);
  }, { u: username, p: PASS[username] });
  await page.reload();
  await page.waitForTimeout(600);
}

async function apiGet(page: Page, path: string) {
  return page.evaluate(async (p) => {
    const t = sessionStorage.getItem('cc_token');
    const r = await fetch(p, { headers: { Authorization: `Bearer ${t}` } });
    const txt = await r.text();
    try { return { status: r.status, body: JSON.parse(txt) }; } catch { return { status: r.status, body: txt }; }
  }, path);
}

async function apiSend(page: Page, path: string, method: string, body?: unknown) {
  return page.evaluate(async ({ p, m, b }) => {
    const t = sessionStorage.getItem('cc_token');
    const r = await fetch(p, {
      method: m, headers: { Authorization: `Bearer ${t}`, 'Content-Type': 'application/json' },
      body: b === undefined ? undefined : JSON.stringify(b),
    });
    const txt = await r.text();
    try { return { status: r.status, body: JSON.parse(txt) }; } catch { return { status: r.status, body: txt }; }
  }, { p: path, m: method, b: body });
}

async function taskState(page: Page, id: string) {
  const r = await apiGet(page, `/api/tasks/${id}`);
  const t = r.body;
  return { status: r.status, assignee: t.assignee, assigneeUserId: t.assigneeUserId ?? '', title: t.title };
}

// Раскладка доски: дорожки (swimlanes) и прямоугольники их ячеек.
async function boardLayout(page: Page) {
  return page.evaluate(() => {
    const grid = document.querySelector('.cc-board-scroll > div') as HTMLElement | null;
    if (!grid) return null;
    const lanes: { label: string; cells: { x: number; y: number; w: number; h: number }[] }[] = [];
    for (const wrap of Array.from(grid.children) as HTMLElement[]) {
      if ((wrap.style.display || '') !== 'contents') continue;
      const inner = Array.from(wrap.children) as HTMLElement[];
      if (inner.length < 2) continue;
      const spans = Array.from(inner[0].querySelectorAll('span')).map(s => (s.textContent || '').trim());
      const label = spans.find(s => s.length > 0 && !/^\d+$/.test(s)) || '';
      const cells = [];
      for (const cell of inner.slice(1)) {
        const r = cell.getBoundingClientRect();
        cells.push({ x: r.x + r.width / 2, y: r.y + r.height / 2, w: r.width, h: r.height });
      }
      lanes.push({ label, cells });
    }
    return { lanes };
  });
}

async function cardBox(page: Page, title: string) {
  const card = page.locator('[aria-roledescription="sortable"]').filter({ hasText: title }).first();
  await card.waitFor({ state: 'visible', timeout: 15000 });
  return await card.boundingBox();
}

// В какой дорожке лежит карточка (по геометрии ячеек)
async function laneOfCard(page: Page, title: string) {
  const box = await cardBox(page, title);
  const layout = await boardLayout(page);
  if (!layout) return null;
  const cx = box.x + box.width / 2, cy = box.y + box.height / 2;
  for (const lane of layout.lanes) {
    for (const cell of lane.cells) {
      if (cx > cell.x - cell.w / 2 && cx < cell.x + cell.w / 2 && cy > cell.y - cell.h / 2 && cy < cell.y + cell.h / 2) return lane.label;
    }
  }
  return null;
}

// Прокрутить доску так, чтобы дорожка была видна (иначе при перетаскивании
// автоскролл сбивает координаты цели)
async function scrollLaneIntoView(page: Page, laneLabel: string) {
  await page.evaluate((label) => {
    const grid = document.querySelector('.cc-board-scroll > div') as HTMLElement | null;
    if (!grid) return;
    let sc: HTMLElement | null = grid.parentElement;
    while (sc) {
      const st = getComputedStyle(sc);
      const scrolls = (st.overflowY === 'auto' || st.overflowY === 'scroll') && sc.scrollHeight > sc.clientHeight + 4;
      if (scrolls) break;
      sc = sc.parentElement;
    }
    if (!sc) return;
    for (const wrap of Array.from(grid.children) as HTMLElement[]) {
      if ((wrap.style.display || '') !== 'contents') continue;
      const header = wrap.children[0] as HTMLElement;
      const spans = Array.from(header.querySelectorAll('span')).map(s => (s.textContent || '').trim());
      const lbl = spans.find(s => s.length > 0 && !/^\d+$/.test(s)) || '';
      if (lbl === label) {
        const hr = header.getBoundingClientRect();
        const sr = sc.getBoundingClientRect();
        // дорожку ставим в верхнюю треть контейнера: у края dnd-kit автоскроллит
        sc.scrollTop += (hr.top - sr.top) - Math.max(40, sr.height / 3);
        return;
      }
    }
  }, laneLabel);
  await page.waitForTimeout(400);
}

function laneAtPoint(layout: { lanes: { label: string; cells: { x: number; y: number; w: number; h: number }[] }[] }, x: number, y: number) {
  for (const lane of layout.lanes) {
    for (const cell of lane.cells) {
      if (x > cell.x - cell.w / 2 && x < cell.x + cell.w / 2 && y > cell.y - cell.h / 2 && y < cell.y + cell.h / 2) return lane.label;
    }
  }
  return null;
}

// Ключ дорожки dnd-kit: участник — 'u:<userId>', владелец — тоже 'u:<id владельца>',
// AI — 'claude', «Без исполнителя» — 'none'.
const LANE_LABELS: Record<string, string> = {
  admin: `u:${ADMIN_ID}`,
  qa_b: `u:${QA_B_ID}`,
  qa_c: `u:${QA_C_ID}`,
  qa_e: `u:${QA_E_ID}`,
  'Без исполнителя': 'none',
  AI: 'claude',
};

async function announcedOver(page: Page): Promise<string | null> {
  const loc = page.locator('[role="status"]');
  const n = await loc.count();
  for (let i = 0; i < n; i++) {
    const t = (await loc.nth(i).innerText().catch(() => '')) || '';
    const m = t.match(/droppable area ([^\s]+)/);
    if (m) return m[1];
  }
  return null;
}

// Полный текст живой области (с ней dnd-kit объявляет цели): нужно, чтобы отличить
// свежее объявление от предыдущего (область помнит последний текст)
async function announcedText(page: Page): Promise<string> {
  const loc = page.locator('[role="status"]');
  const n = await loc.count();
  let all = '';
  for (let i = 0; i < n; i++) all += (await loc.nth(i).innerText().catch(() => '')) + '\n';
  return all;
}

// Прокрутить контейнер доски так, чтобы карточка с указанным текстом была в поле зрения
async function scrollCardIntoView(page: Page, title: string, bias = 0.3) {
  await page.evaluate(({ t, b }) => {
    const grid = document.querySelector('.cc-board-scroll > div') as HTMLElement | null;
    if (!grid) return;
    let sc: HTMLElement | null = grid.parentElement;
    while (sc) {
      const st = getComputedStyle(sc);
      if ((st.overflowY === 'auto' || st.overflowY === 'scroll') && sc.scrollHeight > sc.clientHeight + 4) break;
      sc = sc.parentElement;
    }
    if (!sc) return;
    const el = Array.from(document.querySelectorAll('[aria-roledescription="sortable"]'))
      .find(e => (e.textContent || '').includes(t));
    if (!el) return;
    const r = el.getBoundingClientRect();
    const sr = sc.getBoundingClientRect();
    sc.scrollTop += (r.top - sr.top) - sr.height * b;
  }, { t: title, b: bias });
  await page.waitForTimeout(350);
}

async function laneCellRect(page: Page, laneLabel: string, cellIndex: number) {
  return page.evaluate(({ label, idx }) => {
    const grid = document.querySelector('.cc-board-scroll > div') as HTMLElement | null;
    if (!grid) return null;
    for (const wrap of Array.from(grid.children) as HTMLElement[]) {
      if ((wrap.style.display || '') !== 'contents') continue;
      const inner = Array.from(wrap.children) as HTMLElement[];
      if (inner.length < 2) continue;
      const spans = Array.from(inner[0].querySelectorAll('span')).map(s => (s.textContent || '').trim());
      const lbl = spans.find(s => s.length > 0 && !/^\d+$/.test(s)) || '';
      if (lbl !== label) continue;
      const cell = inner[1 + idx];
      if (!cell) return null;
      const r = cell.getBoundingClientRect();
      return { top: r.top, bottom: r.bottom, left: r.left, right: r.right, x: r.x + r.width / 2, y: r.y + r.height / 2, w: r.width, h: r.height };
    }
    return null;
  }, { label: laneLabel, idx: cellIndex });
}

async function dragCardToCell(page: Page, title: string, laneLabel: string, cellIndex = 0) {
  // 1. источник в поле зрения
  await scrollCardIntoView(page, title, 0.3);
  const box = await cardBox(page, title);
  const layout0 = await boardLayout(page);
  if (!layout0) throw new Error('доска не найдена');
  if (!layout0.lanes.some(l => l.label === laneLabel)) {
    throw new Error(`дорожка «${laneLabel}» не найдена: ${layout0.lanes.map(l => l.label).join('|')}`);
  }
  await page.mouse.move(box.x + box.width / 2, box.y + 12);
  await page.mouse.down();
  await page.mouse.move(box.x + box.width / 2 + 12, box.y + 26, { steps: 6 });
  await page.waitForTimeout(200);

  // 2. цель в поле зрения (колесо — доска прокручивается под зажатой кнопкой)
  let rect = await laneCellRect(page, laneLabel, cellIndex);
  if (!rect) { await page.mouse.up(); throw new Error(`ячейка дорожки «${laneLabel}» не найдена`); }
  const vh = page.viewportSize()?.height ?? 720;
  if (rect.top < 80 || rect.bottom > vh - 20) {
    const delta = rect.top < 80 ? rect.top - 120 : rect.bottom - (vh - 60);
    await page.mouse.wheel(0, delta);
    await page.waitForTimeout(500);
  }
  // доска может доскроллиться под курсором (автоскролл dnd-kit) — наводимся, пока
  // прямоугольник цели не перестанет «уплывать», и только потом отпускаем
  let last = null as any;
  for (let i = 0; i < 6; i++) {
    rect = await laneCellRect(page, laneLabel, cellIndex);
    if (!rect) break;
    await page.mouse.move(rect.x, rect.y - rect.h / 2 + 20, { steps: 10 });
    await page.waitForTimeout(280);
    const now = await laneCellRect(page, laneLabel, cellIndex);
    if (!now) break;
    if (last && Math.abs(now.y - last.y) < 3) { last = now; break; }
    last = now;
  }
  note(`S1 бросок «${title}» → «${laneLabel}»: итог=${last ? `${Math.round(last.x)},${Math.round(last.y)}` : 'нет'}`);
  await page.mouse.up();
  await page.waitForTimeout(1400);
}

async function openBoard(page: Page, group = 'assignee') {
  await page.evaluate(({ pid, g }) => {
    localStorage.setItem('cc_board_group', g);
    localStorage.setItem(`cc_proj_board_${pid}`, '1');
  }, { pid: PROJECT_ID, g: group });
  await page.goto(`${BASE}/#/project/${PROJECT_ID}/board`);
  await page.reload();
  await page.waitForTimeout(3500);
  const later = page.getByRole('button', { name: 'Позже' });
  if (await later.count()) { await later.first().click().catch(() => {}); await page.waitForTimeout(1200); }
  if (await page.locator('.cc-board-scroll').count() === 0 || !(await page.locator('.cc-board-scroll').first().isVisible().catch(() => false))) {
    // мобильная раскладка: доска живёт во вкладке «Задачи»
    const tab = page.locator('[title*="Задачи"], [aria-label*="Задачи"]').first();
    if (await tab.count()) { await tab.click().catch(() => {}); await page.waitForTimeout(2000); }
  }
  await page.locator('.cc-board-scroll').first().waitFor({ state: 'visible', timeout: 15000 });
}

async function toastTexts(page: Page): Promise<string[]> {
  const out: string[] = [];
  for (const sel of ['[role="status"]', '[role="alert"]', '[class*="toast"]']) {
    const loc = page.locator(sel);
    const n = await loc.count();
    for (let i = 0; i < Math.min(n, 6); i++) {
      const t = (await loc.nth(i).innerText().catch(() => '')).trim();
      if (t) out.push(`${sel} :: ${t}`);
    }
  }
  return out;
}

// Каноническое состояние фикстур перед каждым тестом
async function resetFixtures() {
  await adm('PUT', `/api/projects/${PROJECT_ID}/members/${QA_C_ID}`, { role: 'editor' });
  const mem = await adm('GET', `/api/projects/${PROJECT_ID}/members`);
  const list = JSON.stringify(mem.body);
  if (!list.includes('qa_e')) await adm('POST', `/api/projects/${PROJECT_ID}/members`, { username: 'qa_e', role: 'editor' });
  if (!list.includes('qa_b')) await adm('POST', `/api/projects/${PROJECT_ID}/members`, { username: 'qa_b', role: 'editor' });
  else await adm('PUT', `/api/projects/${PROJECT_ID}/members/${QA_B_ID}`, { role: 'editor' });
  await adm('PUT', `/api/tasks/${T.owner}`, { assignee: 'me', assigneeUserId: '' });
  await adm('PUT', `/api/tasks/${T.ya}`, { assignee: 'me', assigneeUserId: '' });
  await adm('PUT', `/api/tasks/${T.b}`, { assignee: 'me', assigneeUserId: QA_B_ID });
  await adm('PUT', `/api/tasks/${T.claude}`, { assignee: 'claude', assigneeUserId: '', dueDate: TODAY });
  await adm('PUT', `/api/tasks/${T.c}`, { assignee: 'me', assigneeUserId: QA_C_ID });
  await adm('PUT', `/api/tasks/${T.e}`, { assignee: 'me', assigneeUserId: QA_E_ID });
}

test.describe.configure({ mode: 'serial' });

test.beforeAll(async ({ request }: APIRequestContext) => {
  const r = await request.post(`${BASE}/api/auth/login`, { data: { username: 'admin', password: PASS.admin } });
  const j = await r.json();
  adminToken = j.token;
  await resetFixtures();
});

test.beforeEach(async () => { await resetFixtures(); });

test.describe('S2b · назначение исполнителя-участника', () => {
  test('сценарий 1: бросок карточки на доске общего проекта', async ({ page }) => {
    const puts: string[] = [];
    page.on('request', r => { if (r.method() === 'PUT' && r.url().includes('/api/tasks/')) puts.push(r.postData() || ''); });
    await login(page, 'admin');
    await openBoard(page);
    const layout = await boardLayout(page);
    note(`S1 дорожки: ${layout!.lanes.map(l => l.label).join(', ')}`);
    await page.screenshot({ path: `${SHOTS}/01-board-lanes.png`, fullPage: true });

    // 1а. бросок карточки владельца на дорожку qa_b
    await dragCardToCell(page, 'Карточка владельца', 'qa_b');
    let st = await taskState(page, T.owner);
    note(`S1a броск на qa_b: assignee=${st.assignee} assigneeUserId=${st.assigneeUserId} (ожидалось ${QA_B_ID})`);
    expect(st.assigneeUserId).toBe(QA_B_ID);
    await page.screenshot({ path: `${SHOTS}/01a-drop-onto-qa_b.png`, fullPage: true });

    // 1б. бросок той же карточки на дорожку AI — участник снимается
    await dragCardToCell(page, 'Карточка владельца', 'AI');
    note(`S1b PUT-запросы: ${JSON.stringify(puts)}`);
    st = await taskState(page, T.owner);
    note(`S1b броск на AI: assignee=${st.assignee} assigneeUserId="${st.assigneeUserId}"`);
    expect(st.assignee).toBe('claude');
    expect(st.assigneeUserId).toBe('');
    await page.screenshot({ path: `${SHOTS}/01b-drop-onto-ai.png`, fullPage: true });

    // 1в. бросок карточки участника на дорожку владельца — снятие, без «отскока» на «Без исполнителя»
    await dragCardToCell(page, 'Карточка Б', 'admin');
    st = await taskState(page, T.b);
    const laneNow = await laneOfCard(page, 'Карточка Б');
    note(`S1c броск на дорожку владельца: assignee=${st.assignee} assigneeUserId="${st.assigneeUserId}"; дорожка карточки после броска: ${laneNow}`);
    expect(st.assigneeUserId).toBe('');
    expect(laneNow).toBe('admin');
    await page.screenshot({ path: `${SHOTS}/01c-drop-onto-owner.png`, fullPage: true });
  });

  test('сценарий 1 (продолжение): «Без исполнителя» и отказ двери', async ({ page }) => {
    await login(page, 'admin');
    // отзываем qa_e — его карточка проваливается в дорожку «Без исполнителя»
    const rm = await apiSend(page, `/api/projects/${PROJECT_ID}/members/${QA_E_ID}`, 'DELETE');
    note(`S1d отзыв qa_e (фикстура): ${rm.status}`);
    await openBoard(page);
    let layout = await boardLayout(page);
    note(`S1d дорожки после отзыва qa_e: ${layout!.lanes.map(l => l.label).join(', ')}`);
    expect(layout!.lanes.map(l => l.label)).toContain('Без исполнителя');
    await page.screenshot({ path: `${SHOTS}/01d-lane-none.png`, fullPage: true });

    // 1г. бросок карточки Б на «Без исполнителя» — исполнитель снят
    await dragCardToCell(page, 'Карточка Б', 'Без исполнителя');
    let st = await taskState(page, T.b);
    note(`S1e броск на «Без исполнителя»: assignee=${st.assignee} assigneeUserId="${st.assigneeUserId}"`);
    expect(st.assigneeUserId).toBe('');
    await page.screenshot({ path: `${SHOTS}/01e-drop-onto-none.png`, fullPage: true });

    // 1д. отказ двери: qa_c понижаем до читателя, его карточка остаётся на дорожке qa_c
    const demote = await apiSend(page, `/api/projects/${PROJECT_ID}/members/${QA_C_ID}`, 'PUT', { role: 'reader' });
    note(`S1f понижение qa_c до reader: ${demote.status} ${JSON.stringify(demote.body).slice(0, 200)}`);
    await openBoard(page);
    layout = await boardLayout(page);
    note(`S1f дорожки после понижения qa_c: ${layout!.lanes.map(l => l.label).join(', ')}`);

    const before = await taskState(page, T.owner);
    await dragCardToCell(page, 'Карточка владельца', 'qa_c');
    const after = await taskState(page, T.owner);
    const toasts = await toastTexts(page);
    const bodyAfter = await page.locator('body').innerText();
    note(`S1f отказ на читателе: было assigneeUserId="${before.assigneeUserId}" стало "${after.assigneeUserId}"; тосты: ${JSON.stringify(toasts)}`);
    note(`S1f текст тоста на странице: есть «Не удалось переместить карточку» = ${bodyAfter.includes('Не удалось переместить карточку')}, есть «Читателя нельзя назначить» = ${bodyAfter.includes('Читателя нельзя назначить')}`);
    const tail = bodyAfter.slice(0, 600);
    note(`S1f начало страницы после отказа: ${JSON.stringify(tail)}`);
    await page.screenshot({ path: `${SHOTS}/01f-refuse-reader.png`, fullPage: true });
    expect(after.assigneeUserId).toBe(before.assigneeUserId);
  });

  test('сценарий 2: форма правки задачи общего проекта', async ({ page }) => {
    await login(page, 'admin');
    await openBoard(page);
    const card = page.locator('[aria-roledescription="sortable"]').filter({ hasText: 'Карточка Claude' }).first();
    await card.click();
    await page.waitForTimeout(1500);
    const edit = page.getByRole('button', { name: 'Изменить' });
    if (await edit.count()) { await edit.first().click(); await page.waitForTimeout(1200); }
    await page.screenshot({ path: `${SHOTS}/02a-edit-form.png`, fullPage: true });

    const picker = page.locator('button', { hasText: 'Задача на вас' }).or(page.locator('button', { hasText: 'Выполнит AI' })).first();
    await picker.click();
    await page.waitForTimeout(800);
    const groupNames = await page.locator('div').evaluateAll(els =>
      els.map(e => (e.textContent || '').trim()).filter(t => ['Основные', 'Участники проекта', 'Команда проекта', 'Глобальные'].includes(t)));
    const memberItems = await page.locator('[data-testid^="member-"]').allInnerTexts();
    const hasReader = memberItems.some(x => x.includes('qa_c'));
    note(`S2 группы пикера: ${JSON.stringify([...new Set(groupNames)])}; участники: ${JSON.stringify(memberItems)}; читатель(qa_c) в списке: ${hasReader}`);
    await page.screenshot({ path: `${SHOTS}/02b-executor-picker.png`, fullPage: true });

    const member = page.locator(`[data-testid="member-${QA_B_ID}"]`);
    await member.click();
    await page.waitForTimeout(600);
    const save = page.getByRole('button', { name: /^(Готово|Сохранить)/ });
    if (await save.count()) { await save.first().click(); await page.waitForTimeout(1800); }
    else note('S2 кнопка сохранения не найдена');
    const st = await taskState(page, T.claude);
    note(`S2 после выбора участника и сохранения: assignee=${st.assignee} assigneeUserId=${st.assigneeUserId}`);
    await page.screenshot({ path: `${SHOTS}/02c-after-save.png`, fullPage: true });
    expect(st.assigneeUserId).toBe(QA_B_ID);
  });

  test('сценарий 2 (продолжение): «Я» снимает участника, в личном проекте группы нет', async ({ page }) => {
    await login(page, 'admin');
    // qa_c — читатель: в пикере его быть не должно
    await adm('PUT', `/api/projects/${PROJECT_ID}/members/${QA_C_ID}`, { role: 'reader' });
    await openBoard(page);
    const card = page.locator('[aria-roledescription="sortable"]').filter({ hasText: 'Карточка Claude' }).first();
    await card.click();
    await page.waitForTimeout(1500);
    const edit = page.getByRole('button', { name: 'Изменить' });
    if (await edit.count()) { await edit.first().click(); await page.waitForTimeout(1200); }
    const picker = page.locator('button', { hasText: 'Задача на вас' }).or(page.locator('button', { hasText: 'Выполнит AI' })).or(page.locator('button', { hasText: 'Задача на нём' })).first();
    await picker.click();
    await page.waitForTimeout(800);
    const memberItems = await page.locator('[data-testid^="member-"]').allInnerTexts();
    const groupNames = await page.locator('div').evaluateAll(els =>
      els.map(e => (e.textContent || '').trim()).filter(t => ['Основные', 'Участники проекта', 'Команда проекта', 'Глобальные'].includes(t)));
    note(`S2b участники в пикере (qa_c — читатель): ${JSON.stringify(memberItems)}; группы: ${JSON.stringify([...new Set(groupNames)])}`);
    await page.screenshot({ path: `${SHOTS}/02d-picker-no-reader.png`, fullPage: true });
    expect(memberItems.some(x => x.includes('qa_c'))).toBe(false);

    // «Я» — участник снимается
    await page.locator('[data-testid="opt-me"]').click();
    await page.waitForTimeout(600);
    const done = page.getByRole('button', { name: /^(Готово|Сохранить)/ });
    if (await done.count()) { await done.first().click(); await page.waitForTimeout(1500); }
    const st = await taskState(page, T.claude);
    note(`S2b после выбора «Я»: assignee=${st.assignee} assigneeUserId="${st.assigneeUserId}"`);
    expect(st.assigneeUserId).toBe('');

  });

  test('сценарий 2 (продолжение): в личном проекте группы «Участники проекта» нет', async ({ page }) => {
    await login(page, 'qa_b');
    await page.evaluate((pid) => localStorage.setItem(`cc_proj_board_${pid}`, '1'), FX.bPersonalProject);
    await page.goto(`${BASE}/#/project/${FX.bPersonalProject}/board`);
    await page.reload();
    await page.waitForTimeout(3500);
    const later = page.getByRole('button', { name: 'Позже' });
    if (await later.count()) { await later.first().click().catch(() => {}); await page.waitForTimeout(1000); }
    const pcard = page.locator('[aria-roledescription="sortable"]').filter({ hasText: 'Личная задача Б' }).first();
    expect(await pcard.count()).toBeGreaterThan(0);
    await pcard.click();
    await page.waitForTimeout(1500);
    const edit2 = page.getByRole('button', { name: 'Изменить' });
    if (await edit2.count()) { await edit2.first().click(); await page.waitForTimeout(1200); }
    const picker2 = page.locator('button', { hasText: 'Задача на вас' }).or(page.locator('button', { hasText: 'Выполнит AI' })).first();
    await picker2.click();
    await page.waitForTimeout(800);
    const g2 = await page.locator('div').evaluateAll(els =>
      els.map(e => (e.textContent || '').trim()).filter(t => ['Основные', 'Участники проекта', 'Команда проекта', 'Глобальные'].includes(t)));
    note(`S2c личный проект Б, группы пикера: ${JSON.stringify([...new Set(g2)])}`);
    await page.screenshot({ path: `${SHOTS}/02e-personal-project-picker.png`, fullPage: true });
    expect([...new Set(g2)]).not.toContain('Участники проекта');
  });



  test('сценарий 2 (продолжение): смена проекта в форме — список участников', async ({ page }) => {
    await login(page, 'admin');
    await openBoard(page);
    const card = page.locator('[aria-roledescription="sortable"]').filter({ hasText: 'Карточка Claude' }).first();
    await card.click();
    await page.waitForTimeout(1500);
    const edit = page.getByRole('button', { name: 'Изменить' });
    if (await edit.count()) { await edit.first().click(); await page.waitForTimeout(1200); }
    // переключаем проект на личный проект Б (не общий) — группа участников должна исчезнуть
    const projectRadio = page.getByText('Личное', { exact: false }).first();
    const before = await page.locator('[data-testid^="member-"]').count();
    note(`S2d участников в пикере до смены проекта: ${before}`);
    if (await projectRadio.count()) {
      await projectRadio.click().catch(() => {});
      await page.waitForTimeout(1500);
    }
    const picker = page.locator('button', { hasText: 'Задача на вас' }).or(page.locator('button', { hasText: 'Выполнит AI' })).or(page.locator('button', { hasText: 'Задача на нём' })).first();
    await picker.click().catch(() => {});
    await page.waitForTimeout(900);
    const after = await page.locator('[data-testid^="member-"]').count();
    const groups = await page.locator('div').evaluateAll(els =>
      els.map(e => (e.textContent || '').trim()).filter(t => ['Основные', 'Участники проекта', 'Команда проекта', 'Глобальные'].includes(t)));
    note(`S2d после смены проекта: участников в пикере=${after}; группы=${JSON.stringify([...new Set(groups)])}`);
    await page.screenshot({ path: `${SHOTS}/02f-project-switch.png`, fullPage: true });
  });

  test('сценарий 3: события доски — участник и читатель обновляются, посторонний нет', async ({ browser }) => {
    const ctxB = await browser.newContext();
    const ctxC = await browser.newContext();
    const ctxD = await browser.newContext();
    const pB = await ctxB.newPage();
    const frames: string[] = [];
    pB.on('websocket', ws => { ws.on('framereceived', (f: any) => { if (typeof f.payload === 'string') frames.push(f.payload); }); });
    const pC = await ctxC.newPage();
    const pD = await ctxD.newPage();
    await login(pB, 'qa_b');
    await login(pC, 'qa_c');
    await login(pD, 'qa_d');

    await openBoard(pB);
    await openBoard(pC);
    // посторонний: проект ему недоступен
    const projD = await apiGet(pD, `/api/projects/${PROJECT_ID}`);
    note(`S3 для постороннего (qa_d) проект: HTTP ${projD.status}`);
    expect(projD.status).toBe(404);

    const boardB0 = await pB.locator('body').innerText();
    const boardC0 = await pC.locator('body').innerText();
    note(`S3 до события: у Б на доске «Карточка владельца» = ${boardB0.includes('Карточка владельца')}; у В = ${boardC0.includes('Карточка владельца')}; у Б своя карточка = ${boardB0.includes('Карточка Б')}`);

    // А (владелец) назначает карточку участнику Б — событие доски
    await adm('PUT', `/api/tasks/${T.owner}`, { dueDate: TODAY });
    const upd = await adm('PUT', `/api/tasks/${T.owner}`, { assignee: 'me', assigneeUserId: QA_B_ID });
    note(`S3 А назначил карточку Б: ${upd.status}`);
    await pB.waitForTimeout(4000);
    await pC.waitForTimeout(1000);

    const boardB1 = await pB.locator('body').innerText();
    const boardC1 = await pC.locator('body').innerText();
    note(`S3 после события (без перезагрузки): у Б «Карточка владельца» = ${boardB1.includes('Карточка владельца')}; у В = ${boardC1.includes('Карточка владельца')}`);
    await pB.screenshot({ path: `${SHOTS}/03a-board-b-live.png`, fullPage: true });
    await pC.screenshot({ path: `${SHOTS}/03b-board-c-live.png`, fullPage: true });
    expect(boardB1.includes('Карточка владельца')).toBe(true);

    // состав события: у Б в кадре нет Description/ResultMarkdown, но есть listItem с превью
    const taskFrames = frames.filter(f => f.includes('Карточка владельца'));
    let payload = '';
    for (const f of taskFrames) payload += f;
    note(`S3 кадров с карточкой у Б: ${taskFrames.length}; есть description=${payload.includes('"description"')}, есть resultMarkdown=${payload.includes('"resultMarkdown"')}, есть listItem=${payload.includes('"listItem"')}, есть descriptionPreview=${payload.includes('"descriptionPreview"')}`);
    note(`S3 фрагмент кадра: ${JSON.stringify(payload.slice(0, 700))}`);

    // превью описания у участника не затёрлось: календарь (агенда) — тултип карточки
    await pB.goto(`${BASE}/#/calendar`);
    await pB.reload();
    await pB.waitForTimeout(3000);
    const ag = pB.getByRole('button', { name: 'Агенда' });
    if (await ag.count()) { await ag.first().click(); await pB.waitForTimeout(1500); }
    const acard = pB.locator('text=Карточка владельца').first();
    if (await acard.count()) {
      const ab = await acard.boundingBox();
      if (ab) { await pB.mouse.move(ab.x + ab.width / 2, ab.y + ab.height / 2); await pB.waitForTimeout(2500); }
    }
    const hoverText = await pB.locator('body').innerText();
    note(`S3 превью описания у Б в календаре: ${hoverText.includes('Описание карточки владельца')}`);
    await pB.screenshot({ path: `${SHOTS}/03c-hover-preview.png`, fullPage: true });
    await pB.screenshot({ path: `${SHOTS}/03c-hover-preview.png`, fullPage: true });

    await ctxB.close(); await ctxC.close(); await ctxD.close();
  });



  test('сценарий 7: раскладка 360/1280 (доска, форма правки) и известный minor', async ({ browser }) => {
    // известный minor: бросок на «Без исполнителя» для AI-задачи ничего не делает
    const ctx = await browser.newContext();
    const p = await ctx.newPage();
    await login(p, 'admin');
    // «Без исполнителя» появляется, когда у участника отозван (его карточка проваливается)
    await apiSend(p, `/api/projects/${PROJECT_ID}/members/${QA_E_ID}`, 'DELETE');
    await openBoard(p);
    const beforeAi = await taskState(p, T.claude);
    await dragCardToCell(p, 'Карточка Claude', 'Без исполнителя');
    const afterAi = await taskState(p, T.claude);
    note(`S7 minor (AI на «Без исполнителя»): было assignee=${beforeAi.assignee}/"${beforeAi.assigneeUserId}" стало ${afterAi.assignee}/"${afterAi.assigneeUserId}"`);

    // доска хаба (тот же компонент TaskBoard): 360 и 1280
    for (const w of [360, 1280]) {
      await p.setViewportSize({ width: w, height: 720 });
      await p.evaluate(() => localStorage.setItem('cc_cal_view', 'board'));
      await p.goto(`${BASE}/#/calendar/board`);
      await p.reload();
      await p.waitForTimeout(3000);
      const m = await p.evaluate(() => {
        const board = document.querySelector('.cc-board-scroll') as HTMLElement | null;
        if (!board) return null;
        const doc = document.documentElement;
        const small: string[] = [];
        board.querySelectorAll('button, [role="button"], a, input, textarea, select').forEach(el => {
          const r = el.getBoundingClientRect();
          if (r.width === 0 && r.height === 0) return;
          if (r.width < 40 || r.height < 40) small.push(`${(el.textContent || '').trim().slice(0, 20)} ${Math.round(r.width)}x${Math.round(r.height)}`);
        });
        return {
          docOverflow: doc.scrollWidth - doc.clientWidth,
          boardOverflowX: board.scrollWidth - board.clientWidth,
          small,
        };
      });
      note(`S7 доска ${w}: ${JSON.stringify(m)}`);
      await p.screenshot({ path: `${SHOTS}/07-board-${w}.png`, fullPage: true });
    }

    // форма правки + раскрытый пикер исполнителя: 360 и 1280
    for (const w of [360, 1280]) {
      await p.setViewportSize({ width: w, height: 720 });
      await p.goto(`${BASE}/#/project/${PROJECT_ID}/task/${T.claude}`);
      await p.reload();
      await p.waitForTimeout(3000);
      const later = p.getByRole('button', { name: 'Позже' });
      if (await later.count()) { await later.first().click().catch(() => {}); await p.waitForTimeout(1200); }
      const edit = p.getByRole('button', { name: 'Изменить' });
      if (await edit.count()) { await edit.first().click(); await p.waitForTimeout(1200); }
      const picker = p.locator('button', { hasText: 'Задача на вас' }).or(p.locator('button', { hasText: 'Выполнит AI' })).or(p.locator('button', { hasText: 'Задача на нём' })).first();
      await picker.click();
      await p.waitForTimeout(900);
      const m = await p.evaluate(() => {
        const items = Array.from(document.querySelectorAll('[data-testid^="member-"], [data-testid="opt-me"], [data-testid="opt-ai"]')) as HTMLElement[];
        const doc = document.documentElement;
        const small = items.map(el => {
          const r = el.getBoundingClientRect();
          return `${(el.textContent || '').trim().slice(0, 12)} ${Math.round(r.width)}x${Math.round(r.height)}`;
        });
        const wide = items.filter(el => { const r = el.getBoundingClientRect(); return r.right > doc.clientWidth + 1; }).length;
        return { docOverflow: doc.scrollWidth - doc.clientWidth, items: small, выходитЗаПределОкна: wide };
      });
      note(`S7 форма правки ${w}: ${JSON.stringify(m)}`);
      await p.screenshot({ path: `${SHOTS}/07-edit-${w}.png`, fullPage: true });
    }
    await ctx.close();
  });

  test('сценарий 6: MCP tasks_update — назначение участника по username и отказы', async ({ page }) => {
    await login(page, 'admin');
    const sess = await apiSend(page, `/api/projects/${PROJECT_ID}/sessions`, 'POST', { name: 'QA S2b MCP' });
    const sessionId = (sess.body as any).id;
    note(`S6 сессия для MCP: ${sess.status} ${sessionId}`);

    const call = async (args: Record<string, unknown>) => page.evaluate(async ({ sid, a }) => {
      const t = sessionStorage.getItem('cc_token');
      const r = await fetch(`/mcp/tasks/${sid}`, {
        method: 'POST',
        headers: { Authorization: `Bearer ${t}`, 'Content-Type': 'application/json' },
        body: JSON.stringify({ jsonrpc: '2.0', id: 1, method: 'tools/call', params: { name: 'tasks_update', arguments: a } }),
      });
      const txt = await r.text();
      let j: any = txt;
      try { j = JSON.parse(txt); } catch { /* текст */ }
      const res = j?.result;
      const text = res?.content?.[0]?.text ?? '';
      return { http: r.status, isError: !!res?.isError, text: String(text).slice(0, 400) };
    }, { sid: sessionId, a: args });

    // назначение участником по username
    let res = await call({ id: T.owner, assignee: 'qa_b' });
    let st = await taskState(page, T.owner);
    note(`S6 assignee=qa_b: isError=${res.isError} text=${JSON.stringify(res.text.slice(0, 120))}; состояние assigneeUserId=${st.assigneeUserId}`);
    expect(res.isError).toBe(false);
    expect(st.assigneeUserId).toBe(QA_B_ID);

    // 'me' — снимает участника
    res = await call({ id: T.owner, assignee: 'me' });
    st = await taskState(page, T.owner);
    note(`S6 assignee=me: isError=${res.isError}; assigneeUserId="${st.assigneeUserId}"`);
    expect(st.assigneeUserId).toBe('');

    // 'claude' — снимает участника
    await call({ id: T.owner, assignee: 'qa_b' });
    res = await call({ id: T.owner, assignee: 'claude' });
    st = await taskState(page, T.owner);
    note(`S6 assignee=claude: isError=${res.isError}; assignee=${st.assignee} assigneeUserId="${st.assigneeUserId}"`);
    expect(st.assigneeUserId).toBe('');

    // читатель — отказ (понижаем qa_c до читателя)
    await adm('PUT', `/api/projects/${PROJECT_ID}/members/${QA_C_ID}`, { role: 'reader' });
    res = await call({ id: T.owner, assignee: 'qa_c' });
    st = await taskState(page, T.owner);
    note(`S6 assignee=qa_c (читатель): isError=${res.isError} text=${JSON.stringify(res.text.slice(0, 160))}; assigneeUserId="${st.assigneeUserId}"`);
    expect(res.isError).toBe(true);

    // не участник — отказ
    await adm('PUT', `/api/projects/${PROJECT_ID}/members/${QA_C_ID}`, { role: 'editor' });
    res = await call({ id: T.owner, assignee: 'qa_d' });
    st = await taskState(page, T.owner);
    note(`S6 assignee=qa_d (не участник): isError=${res.isError} text=${JSON.stringify(res.text.slice(0, 160))}; assigneeUserId="${st.assigneeUserId}"`);
    expect(res.isError).toBe(true);

    // задача с персоной-исполнителем — отказ
    const me = await apiGet(page, '/api/auth/me');
    const personaId = (me.body as any).defaultPersonaId;
    const withPersona = await adm('PUT', `/api/tasks/${T.ya}`, { assignee: 'claude', personaId, assigneeUserId: '' });
    note(`S6 задача с персоной: ${withPersona.status}`);
    res = await call({ id: T.ya, assignee: 'qa_b' });
    const stYa = await taskState(page, T.ya);
    note(`S6 задача с персоной, assignee=qa_b: isError=${res.isError} text=${JSON.stringify(res.text.slice(0, 160))}; assigneeUserId="${stYa.assigneeUserId}"`);
    expect(res.isError).toBe(true);
    await adm('PUT', `/api/tasks/${T.ya}`, { personaId: '', assigneeUserId: '' });
  });

  test('сценарий 4: календарь участника — чип «Общие» и фильтры', async ({ page }) => {
    await adm('PUT', `/api/tasks/${T.claude}`, { assignee: 'me', assigneeUserId: QA_B_ID, dueDate: TODAY });
    await login(page, 'qa_b');
    const own = await apiSend(page, `/api/tasks/${FX.bPersonalTask}`, 'PUT', { assignee: 'me', dueDate: TODAY });
    note(`S4 срок личной задачи Б проставлен: ${own.status} ${JSON.stringify(own.body).slice(0,200)}`);
    await page.goto(`${BASE}/#/calendar`);
    await page.reload();
    await page.waitForTimeout(3000);
    const chips = await page.locator('button').evaluateAll(els => els.map(e => (e.textContent || '').trim()));
    const idxAll = chips.indexOf('Все');
    const idxShared = chips.findIndex(t => t === 'Общие');
    const idxGroup = chips.findIndex(t => t === 'Группа Б');
    note(`S4 чипы календаря: ${JSON.stringify(chips.filter(t => ['Все', 'Общие', 'Группа Б', 'Без группы'].includes(t)))}; индексы: Все=${idxAll} ГруппаБ=${idxGroup} Общие=${idxShared}`);
    await page.screenshot({ path: `${SHOTS}/04a-calendar-chips.png`, fullPage: true });
    expect(idxShared).toBeGreaterThan(-1);
    expect(idxShared).toBeGreaterThan(idxGroup);

    // задача общего проекта, назначенная Б, видна и помечена чипом «Общие»
    const bodyText = await page.locator('body').innerText();
    note(`S4 в календаре видна карточка Claude: ${bodyText.includes('Карточка Claude')}; чип «Общие» у карточки: ${/Общие/.test(bodyText)}`);
    await page.screenshot({ path: `${SHOTS}/04b-calendar-body.png`, fullPage: true });

    // клик по «Общие» — фильтрует
    await page.getByRole('button', { name: 'Общие', exact: true }).first().click();
    await page.waitForTimeout(1200);
    const afterShared = await page.locator('body').innerText();
    note(`S4 после клика «Общие»: личная задача видна=${afterShared.includes('Личная задача Б')}, общая видна=${afterShared.includes('Карточка Claude')}`);
    await page.screenshot({ path: `${SHOTS}/04c-filter-shared.png`, fullPage: true });

    // выбор группы снимает «Общие»
    await page.getByRole('button', { name: 'Группа Б', exact: true }).first().click();
    await page.waitForTimeout(1200);
    const afterGroup = await page.locator('body').innerText();
    const sharedActive = await page.getByRole('button', { name: 'Общие', exact: true }).first().evaluate(e => e.getAttribute('style') || '');
    note(`S4 после выбора группы: общая задача видна=${afterGroup.includes('Карточка Claude')}, личная видна=${afterGroup.includes('Личная задача Б')}; стиль чипа «Общие»: ${sharedActive.includes('accent') ? 'активен' : 'не активен'}`);
    await page.screenshot({ path: `${SHOTS}/04d-filter-group.png`, fullPage: true });
    expect(afterGroup.includes('Карточка Claude')).toBe(false);
  });


  test('сценарий 4 (напоминание): срок задачи в часовом поясе адресата', async ({ page }) => {
    test.setTimeout(420_000);
    // Б — в таймзоне Токио; хост в MSK (UTC+3). Разница 6 часов: если бы планировщик
    // считал срок в таймзоне владельца (MSK), напоминание пришло бы сразу.
    await login(page, 'qa_b');
    const tzSet = await apiSend(page, '/api/auth/timezone', 'PUT', { timeZone: 'Asia/Tokyo' });
    note(`S4 таймзона Б выставлена: ${tzSet.status}`);

    const fireAt = new Date(Date.now() + 3 * 60 * 1000);
    const jp = new Intl.DateTimeFormat('sv-SE', { timeZone: 'Asia/Tokyo', year: 'numeric', month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit', hour12: false })
      .format(fireAt);
    const dueDate = jp.slice(0, 10);
    const dueTime = jp.slice(11, 16);
    note(`S4 срок задачи (локально в Токио): ${dueDate} ${dueTime}; это ${fireAt.toISOString()} UTC`);

    const title = 'Напоминание по таймзоне ' + Date.now();
    const created = await adm('POST', `/api/projects/${PROJECT_ID}/tasks`, { title, dueDate, dueTime, reminderMinutes: 0 });
    const newId = (created.body as any).id;
    const assign = await adm('PUT', `/api/tasks/${newId}`, { assignee: 'me', assigneeUserId: QA_B_ID });
    note(`S4 задача создана: ${created.status}, назначена Б: ${assign.status}`);

    const t0 = Date.now();
    let elapsed = -1;
    for (let i = 0; i < 40; i++) {
      await page.waitForTimeout(10000);
      const n = await apiGet(page, '/api/notifications');
      const list = Array.isArray(n.body) ? n.body : (n.body?.items ?? []);
      const hit = list.find((x: any) => (x.title === 'Напоминание о задаче') && ((x.body || '').includes(title) || (x.text || '').includes(title)));
      if (hit) { elapsed = Math.round((Date.now() - t0) / 1000); note(`S4 напоминание пришло через ${elapsed} с: ${JSON.stringify(hit).slice(0, 300)}`); break; }
    }
    if (elapsed < 0) note('S4 напоминание не пришло за 400 с');
    await adm('DELETE', `/api/tasks/${newId}`);
    await apiSend(page, '/api/auth/timezone', 'PUT', { timeZone: 'Europe/Moscow' });
    // пришло по таймзоне адресата (≈180 с), а не сразу (≤60 с) — срок в таймзоне Б
    expect(elapsed).toBeGreaterThan(120);
    expect(elapsed).toBeLessThan(360);
  });

  test('сценарий 5: отзыв участника — задача исчезает из календаря и «моих задач»', async ({ page }) => {
    await adm('PUT', `/api/tasks/${T.claude}`, { assignee: 'me', assigneeUserId: QA_B_ID, dueDate: TODAY });
    await login(page, 'qa_b');
    await page.goto(`${BASE}/#/calendar`);
    await page.reload();
    await page.waitForTimeout(3000);
    const before = await page.locator('body').innerText();
    note(`S5 до отзыва: «Карточка Claude» в календаре Б = ${before.includes('Карточка Claude')}`);
    expect(before.includes('Карточка Claude')).toBe(true);

    // отзыв Б из проекта — владельцем (А)
    const rm = await adm('DELETE', `/api/projects/${PROJECT_ID}/members/${QA_B_ID}`);
    note(`S5 отзыв qa_b: ${rm.status} ${JSON.stringify(rm.body).slice(0, 200)}`);

    await page.reload();
    await page.waitForTimeout(3000);
    const after = await page.locator('body').innerText();
    const sharedChip = await page.getByRole('button', { name: 'Общие', exact: true }).count();
    note(`S5 после отзыва: «Карточка Claude» в календаре Б = ${after.includes('Карточка Claude')}; чип «Общие» = ${sharedChip}`);
    await page.screenshot({ path: `${SHOTS}/05a-after-revoke.png`, fullPage: true });
    expect(after.includes('Карточка Claude')).toBe(false);

    // «мои задачи» в проекте: Б больше не видит проект
    const proj = await apiGet(page, `/api/projects/${PROJECT_ID}`);
    note(`S5 проект для Б после отзыва: HTTP ${proj.status}`);
    expect(proj.status).toBe(404);
  });
});
