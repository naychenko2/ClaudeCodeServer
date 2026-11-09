// QA «Общие проекты»: права редактора-участника над задачами общего проекта.
// Вопрос: участник с ролью editor может ли создать задачу в общем проекте и менять карточки?
// UI показывает кнопку «Задача», макет §4.7 обещает редактору менять задачи
// (отсутствие «+ Задача» оговорено только для читателя).
//
// Стенд: feat/shared-projects, порт 5557. Учётки admin (владелец), qa_b (editor), qa_e (reader).

import { test, expect, type Page } from '@playwright/test';
import { mkdirSync, writeFileSync } from 'node:fs';

const BASE = process.env.PLAYWRIGHT_BASE_URL || 'http://127.0.0.1:5557';
const SHOTS = '/home/an/Sources/ClaudeCodeServer-shared-projects/.cc-attachments/qa-shared-accept';
const RUN = process.env.QA_ACCEPT_RUN || Date.now().toString(36).slice(-6);

const report: string[] = [];
function note(s: string) {
  report.push(s);
  writeFileSync(`${SHOTS}/report-editor-rights.txt`, report.join('\n'));
  console.log(s);
}

const tokens: Record<string, string> = {};
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
  throw new Error(`login ${user}`);
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

test.describe.configure({ mode: 'serial' });
test.beforeAll(() => { mkdirSync(SHOTS, { recursive: true }); });
test.setTimeout(120_000);

test('API: права участника над задачами общего проекта', async () => {
  const a = await login('admin');
  const b = await login('qa_b');
  const e = await login('qa_e');
  const projects = (await api(a, 'GET', '/api/projects')).body as any[];
  const shared = projects.find(p => p.rootPath && p.rootPath.includes('shared-proj'));
  expect(shared, 'общий проект найден').toBeTruthy();
  const pid = shared.id as string;

  // Создание задачи в общем проекте участником-редактором.
  const create = await api(b, 'POST', `/api/projects/${pid}/tasks`, { title: `QA-acc права ${RUN}` });
  note(`API: редактор создаёт задачу в общем проекте → ${create.status} ${JSON.stringify(create.body).slice(0, 200)}`);
  const createReader = await api(e, 'POST', `/api/projects/${pid}/tasks`, { title: `QA-acc права reader ${RUN}` });
  note(`API: читатель создаёт задачу → ${createReader.status} ${JSON.stringify(createReader.body).slice(0, 160)}`);

  // Правка карточки, созданной владельцем, участником-редактором.
  const ownerCard = await api(a, 'POST', `/api/projects/${pid}/tasks`, { title: `QA-acc карточка владельца ${RUN}` });
  const ownerTaskId = ownerCard.body.id as string;
  const upd = await api(b, 'PUT', `/api/tasks/${ownerTaskId}`, { status: 'inProgress' });
  note(`API: редактор меняет карточку владельца → ${upd.status} ${JSON.stringify(upd.body).slice(0, 200)}`);

  await api(a, 'DELETE', `/api/tasks/${ownerTaskId}`);
});

test('UI: кнопка «Задача» у участника общего проекта и исход нажатия', async ({ page }) => {
  const a = await login('admin');
  const projects = (await api(a, 'GET', '/api/projects')).body as any[];
  const shared = projects.find(p => p.rootPath && p.rootPath.includes('shared-proj'));
  const pid = shared.id as string;

  await uiLogin(page, 'qa_b');
  await page.goto(`${BASE}/#/project/${pid}`);
  await page.reload();
  await page.waitForTimeout(2500);
  const tasksTab = page.locator('[title*="Задачи"], [aria-label*="Задачи"]').first();
  if (await tasksTab.count()) {
    await tasksTab.click().catch(() => undefined);
    await page.waitForTimeout(1500);
  }
  const newTaskBtn = page.getByRole('button', { name: 'Задача', exact: true }).first();
  const btnVisible = await newTaskBtn.isVisible().catch(() => false);
  note(`UI: кнопка «Задача» у qa_b (editor) видна — ${btnVisible}`);
  await page.screenshot({ path: `${SHOTS}/editor-tasks-panel-qa_b.png` });
  expect(btnVisible, 'кнопка «Задача» участнику-редактору видна').toBeTruthy();

  await newTaskBtn.click();
  await page.waitForTimeout(1200);
  const titleBox = page.getByPlaceholder('Что нужно сделать?').first();
  expect(await titleBox.isVisible().catch(() => false), 'диалог «Новая задача» открылся').toBeTruthy();
  await titleBox.fill(`QA-acc UI права ${RUN}`);
  const submit = page.getByRole('button', { name: 'Создать', exact: true }).first();
  expect(await submit.count(), 'кнопка «Создать» есть').toBeTruthy();
  await submit.click().catch((e: any) => note(`UI: клик по «Создать» не прошёл — ${String(e).slice(0, 120)}`));
  await page.waitForTimeout(2500);
  // Текст отказа ищем обходом DOM: модалка может быть вне #root.
  const refused = await page.evaluate(() => {
    const hits = Array.from(document.querySelectorAll('div,span,p'))
      .filter(e => (e.textContent || '').includes('Нет права на запись'))
      .map(e => ({ text: (e.textContent || '').trim().slice(0, 80), w: e.getBoundingClientRect().width, h: e.getBoundingClientRect().height }));
    return { count: hits.length, sample: hits.slice(-3) };
  });
  note(`UI: у qa_b после «Создать» — отказ в интерфейсе: ${JSON.stringify(refused)}`);
  await page.screenshot({ path: `${SHOTS}/editor-new-task-result-qa_b.png` });

  // Задача не создана (её нет в списке задач проекта).
  const tasks = (await api(await login('qa_b'), 'GET', `/api/projects/${pid}/tasks`)).body as any[];
  const created = tasks.some(t => t.title === `QA-acc UI права ${RUN}`);
  note(`UI: задача «QA-acc UI права ${RUN}» появилась в проекте = ${created}`);
  expect(created, 'задача участника в общем проекте не создалась (отказ сервера)').toBeFalsy();
  expect(refused.count, 'интерфейс показал отказ «Нет права на записи»').toBeGreaterThan(0);
});
