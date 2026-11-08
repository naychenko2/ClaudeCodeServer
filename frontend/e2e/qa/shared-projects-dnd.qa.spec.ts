// QA-приёмка «Общие проекты», круг 3, добавка 2: перетаскивание карточки на доске общего
// проекта (рецепт dnd-kit из frontend/e2e/qa-s2b/shared-assignee.spec.ts) и проверка,
// что потолок подмен у участника считается по владельцу, а не по пустому владельцу.
//
// Стенд: feat/shared-projects, порт 5557 (юнит ccs-qa-shared). Учётки admin/qa_b.

import { test, expect, type Page } from '@playwright/test';
import { mkdirSync, writeFileSync } from 'node:fs';

const BASE = process.env.PLAYWRIGHT_BASE_URL || 'http://127.0.0.1:5557';
const SHOTS = '/home/an/Sources/ClaudeCodeServer-shared-projects/.cc-attachments/qa-shared-accept';
const RUN = process.env.QA_ACCEPT_RUN || Date.now().toString(36).slice(-6);

const report: string[] = [];
function note(s: string) {
  report.push(s);
  writeFileSync(`${SHOTS}/report-dnd.txt`, report.join('\n'));
  console.log(s);
}

const tokens: Record<string, string> = {};
const ids: Record<string, string> = {};

async function login(user: string): Promise<string> {
  if (tokens[user]) return tokens[user];
  for (let i = 0; i < 10; i++) {
    const r = await fetch(`${BASE}/api/auth/login`, {
      method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ username: user, password: '12345' }),
    });
    if (r.status === 200) { tokens[user] = (await r.json()).token; return tokens[user]; }
    if (r.status !== 429) throw new Error(`login ${user}: ${r.status}`);
    await new Promise(r => setTimeout(r, 12000));
  }
  throw new Error(`login ${user}: лимит входа`);
}

async function api(token: string, method: string, path: string, body?: unknown) {
  const r = await fetch(`${BASE}${path}`, {
    method, headers: { Authorization: `Bearer ${token}`, 'Content-Type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  const txt = await r.text();
  let parsed: any = txt;
  try { parsed = JSON.parse(txt); } catch { /* текст */ }
  return { status: r.status, body: parsed };
}

async function uiLogin(page: Page, user: string) {
  const token = await login(user);
  await page.goto(`${BASE}/#/home`);
  await page.evaluate(async (t) => {
    sessionStorage.setItem('cc_token', t);
    const me = await fetch('/api/auth/me', { headers: { Authorization: `Bearer ${t}` } });
    const m: any = await me.json();
    sessionStorage.setItem('cc_user_id', m.userId || '');
    if (m.role) sessionStorage.setItem('cc_role', m.role);
  }, token);
  await page.reload();
  await page.waitForTimeout(1800);
}

// Прямоугольник карточки по тексту (рецепт S2b).
async function cardBox(page: Page, title: string) {
  return page.evaluate((t) => {
    const el = Array.from(document.querySelectorAll('[aria-roledescription="sortable"]'))
      .find(e => (e.textContent || '').includes(t));
    if (!el) return null;
    const r = el.getBoundingClientRect();
    return { x: r.x, y: r.y, width: r.width, height: r.height };
  }, title);
}

// Прокрутить контейнер доски так, чтобы карточка была в поле зрения.
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

// Прямоугольник ячейки дорожки: дорожки — wrap с display:contents, первая строка — заголовок.
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

async function boardLanes(page: Page): Promise<string[]> {
  return page.evaluate(() => {
    const grid = document.querySelector('.cc-board-scroll > div') as HTMLElement | null;
    if (!grid) return [];
    const out: string[] = [];
    for (const wrap of Array.from(grid.children) as HTMLElement[]) {
      if ((wrap.style.display || '') !== 'contents') continue;
      const inner = Array.from(wrap.children) as HTMLElement[];
      if (inner.length < 2) continue;
      const spans = Array.from(inner[0].querySelectorAll('span')).map(s => (s.textContent || '').trim());
      const lbl = spans.find(s => s.length > 0 && !/^\d+$/.test(s)) || '';
      if (lbl) out.push(lbl);
    }
    return out;
  });
}

async function dragCardToCell(page: Page, title: string, laneLabel: string, cellIndex = 0) {
  await scrollCardIntoView(page, title, 0.3);
  const box = await cardBox(page, title);
  if (!box) throw new Error(`карточка «${title}» не найдена`);
  await page.mouse.move(box.x + box.width / 2, box.y + 12);
  await page.mouse.down();
  await page.mouse.move(box.x + box.width / 2 + 12, box.y + 26, { steps: 6 });
  await page.waitForTimeout(200);

  let rect = await laneCellRect(page, laneLabel, cellIndex);
  if (!rect) { await page.mouse.up(); throw new Error(`ячейка дорожки «${laneLabel}» не найдена`); }
  const vh = page.viewportSize()?.height ?? 720;
  if (rect.top < 80 || rect.bottom > vh - 20) {
    const delta = rect.top < 80 ? rect.top - 120 : rect.bottom - (vh - 60);
    await page.mouse.wheel(0, delta);
    await page.waitForTimeout(500);
  }
  let last: any = null;
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
  note(`  бросок «${title}» → «${laneLabel}»: итог=${last ? `${Math.round(last.x)},${Math.round(last.y)}` : 'нет'}`);
  await page.mouse.up();
  await page.waitForTimeout(1600);
}

test.describe.configure({ mode: 'serial' });
test.beforeAll(() => { mkdirSync(SHOTS, { recursive: true }); });
test.setTimeout(180_000);

test('доп2: перетаскивание карточки на доске общего проекта и потолок подмен по владельцу', async ({ page }) => {
  const a = await login('admin');
  const b = await login('qa_b');
  ids.admin = (await api(a, 'GET', '/api/auth/me')).body.userId;
  ids.b = (await api(b, 'GET', '/api/auth/me')).body.userId;
  const projects = (await api(a, 'GET', '/api/projects')).body as any[];
  const shared = projects.find(p => p.rootPath && p.rootPath.includes('shared-proj'));
  expect(shared, 'общий проект найден').toBeTruthy();
  const pid = shared.id as string;

  // Потолок подмен: у участника значение не «пустое».
  const setB = (await api(b, 'GET', '/api/specialties/settings')).body;
  const setA = (await api(a, 'GET', '/api/specialties/settings')).body;
  note(`  потолок подмен: у admin ${setA.maxSubstitutions}, у qa_b ${setB.maxSubstitutions}`);
  expect(setB.maxSubstitutions, 'у участника потолок подмен не пустой').toBeGreaterThan(0);

  const title = `QA-acc dnd ${RUN}`;
  const created = await api(a, 'POST', `/api/projects/${pid}/tasks`, { title });
  expect(created.status, `создание карточки: ${JSON.stringify(created.body)}`).toBe(200);
  const taskId = created.body.id as string;

  try {
    // Владелец переносит карточку на дорожку участника — так назначается исполнитель.
    await uiLogin(page, 'admin');
    await page.evaluate((p) => {
      localStorage.setItem('cc_board_group', 'assignee');
      localStorage.setItem(`cc_proj_board_${p}`, '1');
    }, pid);
    await page.goto(`${BASE}/#/project/${pid}/board`);
    await page.reload();
    await page.waitForTimeout(3500);
    const later = page.getByRole('button', { name: 'Позже' });
    if (await later.count()) { await later.first().click().catch(() => undefined); await page.waitForTimeout(1200); }
    await page.locator('.cc-board-scroll').first().waitFor({ state: 'visible', timeout: 15000 });
    await page.screenshot({ path: `${SHOTS}/dnd-before.png` });

    const lanes = await boardLanes(page);
    note(`  дорожки доски: ${JSON.stringify(lanes)}`);
    const targetLane = lanes.find(l => l === 'qa_b') ?? lanes.find(l => l.includes('qa_b'));
    expect(targetLane, `дорожка qa_b есть среди ${JSON.stringify(lanes)}`).toBeTruthy();

    await dragCardToCell(page, title, targetLane!, 0);
    await page.screenshot({ path: `${SHOTS}/dnd-after.png` });

    const after = (await api(a, 'GET', `/api/tasks/${taskId}`)).body;
    note(`  после броска: assigneeUserId=${after.assigneeUserId}, columnId=${after.columnId}, status=${after.status}`);
    expect(after.assigneeUserId, `после броска на дорожку «${targetLane}» исполнитель — участник qa_b`).toBe(ids.b);
  } finally {
    await api(a, 'DELETE', `/api/tasks/${taskId}`);
  }
});
