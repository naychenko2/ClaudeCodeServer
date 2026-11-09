// QA-SHARED-PROJ-2 · волна 2: права на задачи, удаление, фича владельца.
//
// Стенд: ветка feat/shared-projects, порт 5557. Учётки: admin (владелец), qa_b (editor),
// qa_c (editor), qa_e (reader).
//
// Что проверяем (дефект e8f94e29 из круга 1 — «участник общего проекта не может управлять
// задачами, но интерфейс этого не сообщает»):
//   1. редактор создаёт задачу, правит её, перетаскивает по доске, назначает исполнителя,
//      меняет подзадачи — всё без 403;
//   2. у читателя нет «+ Задача» и «Изменить», есть подсказка «Менять задачи может редактор»,
//      карточка на доске не тянется за курсором, но клик открывает карточку;
//   3. корзина: владельцу на любой задаче, редактору только на своей (шапка карточки и форма правки);
//   4. фичевые поля и жизнь фичи — только владельцу проекта (роль, эпик, мерж-задача);
//      обычный дефект с заметкой проверки редактор сохраняет и закрывает.
//
// Один спек на группу сценариев (docs/operations/qa-stand.md). Каждая проверка создаёт своё
// и убирает за собой.

import { test, expect, type Page } from '@playwright/test';
import { mkdirSync, writeFileSync } from 'node:fs';

const BASE = process.env.PLAYWRIGHT_BASE_URL || 'http://127.0.0.1:5557';
const SHOTS = '/home/an/Sources/ClaudeCodeServer-shared-projects/.cc-attachments/qa-shared-w2';
const RUN = process.env.QA_ACCEPT_RUN || Date.now().toString(36).slice(-6);
const N = (s: string) => `QA-w2 ${RUN} ${s}`;

const report: string[] = [];
function note(s: string) {
  report.push(s);
  writeFileSync(`${SHOTS}/report-tasks.txt`, report.join('\n'));
  console.log(s);
}

const tokens: Record<string, string> = {};
const ids: Record<string, string> = {};

async function login(user: string): Promise<string> {
  if (tokens[user]) return tokens[user];
  for (let i = 0; i < 12; i++) {
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

// Доска общего проекта включается флагом в localStorage (без него диплинк уводит в центр с чатом).
// На мобиле доска живёт в режиме «Задачи» (leftTab) и открывается кнопкой «Открыть доску».
async function openBoard(page: Page, pid: string, mobile = false) {
  await page.addInitScript(({ p, m }) => {
    try {
      localStorage.setItem(`cc_proj_board_${p}`, '1');
      localStorage.setItem('cc_board_group', 'none');
      if (m) localStorage.setItem(`ws:${p}`, JSON.stringify({ leftTab: 'tasks' }));
    } catch { /* ignore */ }
  }, { p: pid, m: mobile });
  await page.goto(`${BASE}/#/project/${pid}`);
  await page.reload();
  await page.waitForTimeout(3500);
  const later = page.getByRole('button', { name: 'Позже' });
  if (await later.count()) { await later.first().click().catch(() => undefined); await page.waitForTimeout(1000); }
  const openBoardBtn = page.getByRole('button', { name: 'Открыть доску' }).first();
  if (await openBoardBtn.count()) {
    await openBoardBtn.click({ timeout: 8000 }).catch(() => undefined);
    await page.waitForTimeout(2500);
  }
  await page.locator('.cc-board-scroll').first().waitFor({ state: 'visible', timeout: 20000 });
}

// Клик по названию задачи: deepest-элемент с точным текстом + синтетический клик с
// всплытием. Обычный click() у Playwright ждёт «стабильности» карточки на доске (она
// перерисовывается), из-за чего виснет на таймауте — событие отправляем напрямую.
async function clickTitle(page: Page, title: string) {
  return page.evaluate((t) => {
    const nodes = Array.from(document.querySelectorAll('div,span,button,a'))
      .filter(e => (e.textContent || '').trim() === t);
    const el = nodes[nodes.length - 1] as HTMLElement | undefined;
    if (!el) return false;
    el.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true }));
    return true;
  }, title);
}

async function boardState(page: Page) {
  return page.evaluate(() => {
    const list = Array.from(document.querySelectorAll('[aria-roledescription="sortable"]'));
    return list.map(el => ({
      title: (el.textContent || '').trim().slice(0, 60),
      dragDisabled: el.getAttribute('aria-disabled') === 'true',
      cursor: getComputedStyle(el).cursor,
    }));
  });
}

test.describe.configure({ mode: 'serial' });
test.beforeAll(() => { mkdirSync(SHOTS, { recursive: true }); });
test.setTimeout(180_000);

// ─────────────────────────────────────────────────────────────────────────────
// 1 · Права редактора над задачами общего проекта (закрытие дефекта e8f94e29)
// ─────────────────────────────────────────────────────────────────────────────

test('1a редактор: создание, правка, назначение исполнителя и подзадачи — без 403', async () => {
  const a = await login('admin');
  const b = await login('qa_b');
  ids.admin = (await api(a, 'GET', '/api/auth/me')).body.userId;
  ids.b = (await api(b, 'GET', '/api/auth/me')).body.userId;
  const projects = (await api(a, 'GET', '/api/projects')).body as any[];
  // Именно общий проект приёмки: корень /home/an/qa-shared-projects/shared-proj.
  // Поиск по подстроке 'shared-proj' ловит и контейнеры ('/home/an/qa-shared-projects/…').
  const shared = projects.find(p => (p.rootPath || '').endsWith('/shared-proj'));
  expect(shared, 'общий проект найден').toBeTruthy();
  const pid = shared.id as string;

  // Создание задачи участником-редактором.
  const title = N('создана редактором');
  const created = await api(b, 'POST', `/api/projects/${pid}/tasks`, { title });
  note(`  редактор создаёт задачу в общем проекте → ${created.status}`);
  expect(created.status, 'редактор создаёт задачу в общем проекте').toBe(200);
  const taskId = created.body.id as string;
  expect(created.body.createdByUserId, 'автор задачи — редактор').toBe(ids.b);
  expect(created.body.ownerId, 'запись лежит под ключом арендатора (владельца проекта)').toBe(ids.admin);

  try {
    // Правка карточки владельца (обычные исполнительские поля).
    const upd = await api(b, 'PUT', `/api/tasks/${taskId}`, { status: 'inProgress', description: 'правка редактором' });
    note(`  редактор правит свою задачу → ${upd.status}`);
    expect(upd.status, 'редактор правит задачу').toBe(200);

    // Назначение исполнителя (участнику проекта).
    const assign = await api(b, 'PUT', `/api/tasks/${taskId}`, { assigneeUserId: ids.b });
    note(`  редактор назначает исполнителя → ${assign.status}`);
    expect(assign.status, 'редактор назначает исполнителя').toBe(200);

    // Подзадачи.
    const sub = await api(b, 'PUT', `/api/tasks/${taskId}`, {
      subtasks: [{ id: 's1', title: 'подшаг редактора', isDone: false }],
    });
    note(`  редактор меняет подзадачи → ${sub.status}`);
    expect(sub.status, 'редактор меняет подзадачи').toBe(200);

    // Перетаскивание по доске = смена колонки и порядка (та же дверь правки).
    const move = await api(b, 'PUT', `/api/tasks/${taskId}`, { columnId: 'inprogress', order: 42 });
    note(`  редактор двигает карточку по доске (колонка+порядок) → ${move.status}`);
    expect(move.status, 'редактор перетаскивает карточку по доске').toBe(200);
    expect(move.body.columnId, 'колонка применилась').toBe('inprogress');
    expect(move.body.order, 'порядок применился').toBe(42);

    // Карточка видна и владельцу, и автору.
    const listA = (await api(a, 'GET', `/api/projects/${pid}/tasks`)).body as any[];
    note(`  задача редактора видна владельцу — ${listA.some(t => t.id === taskId)}`);
    expect(listA.some(t => t.id === taskId), 'владелец видит задачу редактора').toBeTruthy();
  } finally {
    await api(a, 'DELETE', `/api/tasks/${taskId}`);
  }
});

test('1b читатель: правка задачи и создание — 403, интерфейс обещаний не даёт', async () => {
  const a = await login('admin');
  const e = await login('qa_e');
  const projects = (await api(a, 'GET', '/api/projects')).body as any[];
  // Именно общий проект приёмки: корень /home/an/qa-shared-projects/shared-proj.
  // Поиск по подстроке 'shared-proj' ловит и контейнеры ('/home/an/qa-shared-projects/…').
  const shared = projects.find(p => (p.rootPath || '').endsWith('/shared-proj'));
  const pid = shared.id as string;

  const ownerCard = await api(a, 'POST', `/api/projects/${pid}/tasks`, { title: N('карточка владельца') });
  const taskId = ownerCard.body.id as string;
  try {
    const upd = await api(e, 'PUT', `/api/tasks/${taskId}`, { status: 'inProgress' });
    note(`  читатель правит карточку → ${upd.status} ${JSON.stringify(upd.body).slice(0, 120)}`);
    expect(upd.status, 'читателю правка закрыта').toBe(403);
    expect(upd.body.code, 'код отказа project_scope').toBe('project_scope');

    const create = await api(e, 'POST', `/api/projects/${pid}/tasks`, { title: N('читатель') });
    note(`  читатель создаёт задачу → ${create.status}`);
    expect(create.status, 'читателю создание закрыто').toBe(403);

    // Запись не изменилась.
    const after = (await api(a, 'GET', `/api/tasks/${taskId}`)).body;
    expect(after.status, 'статус задачи не тронут').toBe('todo');
  } finally {
    await api(a, 'DELETE', `/api/tasks/${taskId}`);
  }
});

test('1c UI: у читателя нет «+ Задача» и «Изменить», карточка не тянется, но открывается', async ({ page }) => {
  const a = await login('admin');
  const e = await login('qa_e');
  const projects = (await api(a, 'GET', '/api/projects')).body as any[];
  // Именно общий проект приёмки: корень /home/an/qa-shared-projects/shared-proj.
  // Поиск по подстроке 'shared-proj' ловит и контейнеры ('/home/an/qa-shared-projects/…').
  const shared = projects.find(p => (p.rootPath || '').endsWith('/shared-proj'));
  const pid = shared.id as string;
  const title = N('карточка для читателя');
  const created = await api(a, 'POST', `/api/projects/${pid}/tasks`, { title });
  const taskId = created.body.id as string;

  try {
    const t0 = Date.now();
    await uiLogin(page, 'qa_e');
    note(`  вход читателя — ${Date.now() - t0} мс`);
    const t1 = Date.now();
    await openBoard(page, pid);
    note(`  доска открыта — ${Date.now() - t1} мс`);

    // 1. Входы создания скрыты: ни «Задача» в панели, ни «Добавить» в ячейках доски.
    const createBtn = page.getByRole('button', { name: 'Задача', exact: true }).first();
    const createVisible = await createBtn.isVisible().catch(() => false);
    note(`  читатель: кнопка «Задача» в панели — ${createVisible}`);
    expect(createVisible, 'у читателя нет входа создания задачи').toBeFalsy();

    const quickAdd = await page.getByRole('button', { name: 'Добавить', exact: true }).count();
    note(`  читатель: кнопок «Добавить» в ячейках доски — ${quickAdd}`);
    expect(quickAdd, 'у читателя нет быстрого добавления в ячейках').toBe(0);

    // 2. Карточка на доске есть, но бросок не активируется (запрет на уровне сенсора).
    const state = await boardState(page);
    const mine = state.find(c => c.title.includes(title));
    note(`  доска читателя: карточек ${state.length}, наша найдена — ${!!mine}`);
    expect(mine, 'карточка читателю видна').toBeTruthy();
    expect(mine!.dragDisabled, 'карточка читателя не перетаскивается (aria-disabled)').toBeTruthy();

    // 3. Подсказка «Менять задачи может редактор» — в панели деталей на выключенном статусе.
    const clicked = await clickTitle(page, title);
    note(`  клик по карточке читателя → ${clicked}`);
    await page.waitForTimeout(2500);
    const denied = await page.evaluate(() => {
      const hit = Array.from(document.querySelectorAll('button[title], [title]'))
        .find(e => (e.getAttribute('title') || '') === 'Менять задачи может редактор');
      return hit ? { found: true, tag: hit.tagName, disabled: hit.hasAttribute('disabled') } : { found: false };
    });
    note(`  читатель: подсказка «Менять задачи может редактор» — ${JSON.stringify(denied)}`);
    expect(denied.found, 'у читателя есть подсказка о праве редактора').toBeTruthy();
    expect(denied.disabled, 'подсказка стоит на выключенном контроле').toBeTruthy();

    const editBtn = await page.getByRole('button', { name: 'Изменить', exact: true }).count();
    note(`  читатель: кнопок «Изменить» — ${editBtn}`);
    expect(editBtn, 'у читателя нет кнопки «Изменить»').toBe(0);
    const trash = await page.locator('[title="Удалить задачу"]').count();
    note(`  читатель: корзин в панели — ${trash}`);
    expect(trash, 'у читателя нет корзины').toBe(0);
    await page.screenshot({ path: `${SHOTS}/1c-reader-details.png` });

    // 4. Клик по карточке открывает карточку (запрет броска клик не ломает):
    // в панели деталей есть сегмент статуса («К выполнению»/«В работе»/«Готово»).
    const statusSeg = await page.getByRole('button', { name: 'В работе', exact: true }).count();
    note(`  читатель: сегмент статуса в панели деталей — ${statusSeg}`);
    expect(statusSeg, 'клик по карточке открывает панель деталей').toBeGreaterThan(0);
  } finally {
    await api(a, 'DELETE', `/api/tasks/${taskId}`);
  }
});

test('1d UI: мобильная раскладка 360 px — доска читателя (без входов создания, карточка не тянется)', async ({ page }) => {
  const a = await login('admin');
  const e = await login('qa_e');
  const projects = (await api(a, 'GET', '/api/projects')).body as any[];
  // Именно общий проект приёмки: корень /home/an/qa-shared-projects/shared-proj.
  // Поиск по подстроке 'shared-proj' ловит и контейнеры ('/home/an/qa-shared-projects/…').
  const shared = projects.find(p => (p.rootPath || '').endsWith('/shared-proj'));
  const pid = shared.id as string;
  const title = N('карточка 360');
  const created = await api(a, 'POST', `/api/projects/${pid}/tasks`, { title });
  const taskId = created.body.id as string;

  try {
    await page.setViewportSize({ width: 360, height: 780 });
    await uiLogin(page, 'qa_e');
    await openBoard(page, pid, true);
    const state = await boardState(page);
    const mine = state.find(c => c.title.includes(title));
    note(`  360px: карточек ${state.length}, наша найдена — ${!!mine}, бросок выключен — ${mine?.dragDisabled}`);
    expect(mine, 'на 360px карточка читателю видна').toBeTruthy();
    expect(mine!.dragDisabled, 'на 360px карточка читателя не перетаскивается').toBeTruthy();
    const quickAdd = await page.getByRole('button', { name: 'Добавить', exact: true }).count();
    const newTask = await page.getByRole('button', { name: 'Новая задача', exact: true }).count();
    note(`  360px: «Добавить» — ${quickAdd}, «Новая задача» — ${newTask}`);
    expect(quickAdd + newTask, 'на 360px у читателя нет входов создания').toBe(0);
    await page.screenshot({ path: `${SHOTS}/1d-reader-360-board.png` });
  } finally {
    await api(a, 'DELETE', `/api/tasks/${taskId}`);
  }
});

// ─────────────────────────────────────────────────────────────────────────────
// 2 · Удаление задачи: корзина владельцу на любой, редактору только на своей
// ─────────────────────────────────────────────────────────────────────────────

test('2a корзина: владелец удаляет чужую, редактор — только свою; в шапке и в форме правки', async () => {
  const a = await login('admin');
  const b = await login('qa_b');
  const projects = (await api(a, 'GET', '/api/projects')).body as any[];
  // Именно общий проект приёмки: корень /home/an/qa-shared-projects/shared-proj.
  // Поиск по подстроке 'shared-proj' ловит и контейнеры ('/home/an/qa-shared-projects/…').
  const shared = projects.find(p => (p.rootPath || '').endsWith('/shared-proj'));
  const pid = shared.id as string;

  const ownerTask = await api(a, 'POST', `/api/projects/${pid}/tasks`, { title: N('владельческая') });
  const editorTask = await api(b, 'POST', `/api/projects/${pid}/tasks`, { title: N('редакторская') });
  const ownerId = ownerTask.body.id as string;
  const editorId = editorTask.body.id as string;

  try {
    // Владелец удаляет чужую (редакторскую) — можно.
    const delOwner = await api(a, 'DELETE', `/api/tasks/${editorId}`);
    note(`  владелец удаляет задачу редактора → ${delOwner.status}`);
    expect(delOwner.status, 'владелец удаляет любую задачу').toBe(204);

    // Редактор удаляет чужую (владельческую) — отказ, задача цела.
    const delEditor = await api(b, 'DELETE', `/api/tasks/${ownerId}`);
    note(`  редактор удаляет чужую задачу → ${delEditor.status} ${JSON.stringify(delEditor.body).slice(0, 140)}`);
    expect(delEditor.status, 'редактору чужую задачу удалить нельзя').toBe(403);
    expect(delEditor.body.code, 'код отказа not_task_author').toBe('not_task_author');
    const alive = await api(a, 'GET', `/api/tasks/${ownerId}`);
    expect(alive.status, 'задача осталась цела').toBe(200);

    // Редактор удаляет свою — можно (отдельная карточка: чужую уже удалил владелец).
    const ownTask = await api(b, 'POST', `/api/projects/${pid}/tasks`, { title: N('своя') });
    const delOwn = await api(b, 'DELETE', `/api/tasks/${ownTask.body.id}`);
    note(`  редактор удаляет свою задачу → ${delOwn.status}`);
    expect(delOwn.status, 'редактор удаляет свою задачу').toBe(204);
  } finally {
    await api(a, 'DELETE', `/api/tasks/${ownerId}`);
  }
});

test('2b UI: корзина в шапке карточки и в форме правки — владельцу всегда, редактору только на своей', async ({ page }) => {
  const a = await login('admin');
  const b = await login('qa_b');
  const projects = (await api(a, 'GET', '/api/projects')).body as any[];
  // Именно общий проект приёмки: корень /home/an/qa-shared-projects/shared-proj.
  // Поиск по подстроке 'shared-proj' ловит и контейнеры ('/home/an/qa-shared-projects/…').
  const shared = projects.find(p => (p.rootPath || '').endsWith('/shared-proj'));
  const pid = shared.id as string;

  const ownerTitle = N('владельческая карточка');
  const ownerTask = await api(a, 'POST', `/api/projects/${pid}/tasks`, { title: ownerTitle });
  const ownerId = ownerTask.body.id as string;

  try {
    // Владелец: корзина на чужой (созданной владельцем, но под редактором это другой случай) —
    // берём задачу редактора: у владельца корзина есть на любой.
    const editorTitle = N('карточка редактора');
    const editorTask = await api(b, 'POST', `/api/projects/${pid}/tasks`, { title: editorTitle });
    const editorId = editorTask.body.id as string;

    // Владелец открывает карточку редактора — корзина есть и в шапке, и в форме правки.
    await uiLogin(page, 'admin');
    await openBoard(page, pid);
    await clickTitle(page, editorTitle);
    await page.waitForTimeout(2500);
    const trashOwner = await page.locator('[title="Удалить задачу"]').count();
    note(`  владелец на чужой карточке: корзин в шапке — ${trashOwner}`);
    expect(trashOwner, 'у владельца корзина есть на любой задаче').toBeGreaterThan(0);
    await page.getByRole('button', { name: 'Изменить', exact: true }).first().click({ timeout: 8000 }).catch(() => undefined);
    await page.waitForTimeout(1200);
    const trashFormOwner = await page.locator('[title="Удалить задачу"]').count();
    note(`  владелец в форме правки: корзин — ${trashFormOwner}`);
    expect(trashFormOwner, 'у владельца корзина есть и в форме правки').toBeGreaterThan(0);
    await page.screenshot({ path: `${SHOTS}/2b-owner-trash.png` });

    // Редактор на чужой карточке — корзины нет ни в шапке, ни в форме правки.
    await uiLogin(page, 'qa_b');
    await openBoard(page, pid);
    await clickTitle(page, ownerTitle);
    await page.waitForTimeout(2500);
    const trashEditor = await page.locator('[title="Удалить задачу"]').count();
    note(`  редактор на чужой карточке: корзин в шапке — ${trashEditor}`);
    expect(trashEditor, 'у редактора на чужой задаче корзины нет').toBe(0);
    await page.getByRole('button', { name: 'Изменить', exact: true }).first().click({ timeout: 8000 }).catch(() => undefined);
    await page.waitForTimeout(1200);
    const trashFormEditor = await page.locator('[title="Удалить задачу"]').count();
    note(`  редактор в форме правки чужой карточки: корзин — ${trashFormEditor}`);
    expect(trashFormEditor, 'у редактора в форме правки чужой задаче корзины нет').toBe(0);
    await page.screenshot({ path: `${SHOTS}/2b-editor-no-trash.png` });

    // Редактор на своей — корзина есть.
    await uiLogin(page, 'qa_b');
    await openBoard(page, pid);
    await clickTitle(page, editorTitle);
    await page.waitForTimeout(2500);
    const trashOwn = await page.locator('[title="Удалить задачу"]').count();
    note(`  редактор на своей карточке: корзин в шапке — ${trashOwn}`);
    expect(trashOwn, 'у редактора на своей задаче корзина есть').toBeGreaterThan(0);

    await api(a, 'DELETE', `/api/tasks/${editorId}`);
  } finally {
    await api(a, 'DELETE', `/api/tasks/${ownerId}`);
  }
});

// ─────────────────────────────────────────────────────────────────────────────
// 3 · Фича владельца: роль/эпик/мерж — только владельцу; обычный дефект правит редактор
// ─────────────────────────────────────────────────────────────────────────────

test('3a редактор не может вести фичу: роль, эпик, мерж-задача — отказ, запись не тронута', async () => {
  const a = await login('admin');
  const b = await login('qa_b');
  const projects = (await api(a, 'GET', '/api/projects')).body as any[];
  // Именно общий проект приёмки: корень /home/an/qa-shared-projects/shared-proj.
  // Поиск по подстроке 'shared-proj' ловит и контейнеры ('/home/an/qa-shared-projects/…').
  const shared = projects.find(p => (p.rootPath || '').endsWith('/shared-proj'));
  const pid = shared.id as string;

  // Эпик фичи (владелец).
  const epic = await api(a, 'POST', `/api/projects/${pid}/tasks`, { title: N('эпик'), role: 'feature' });
  expect(epic.status, 'владелец создаёт эпик фичи').toBe(200);
  const epicId = epic.body.id as string;

  try {
    // Редактор ставит роль «финал» на обычной задаче — фичевое поле.
    const plain = await api(a, 'POST', `/api/projects/${pid}/tasks`, { title: N('обычная') });
    const plainId = plain.body.id as string;
    try {
      const role = await api(b, 'PUT', `/api/tasks/${plainId}`, { role: 'final' });
      note(`  редактор ставит роль «финал» → ${role.status} ${JSON.stringify(role.body).slice(0, 140)}`);
      expect(role.status, 'фичевая роль закрыта редактору').toBe(403);
      expect(role.body.code, 'код отказа feature_owner_only').toBe('feature_owner_only');
      const after = (await api(a, 'GET', `/api/tasks/${plainId}`)).body;
      expect(after.role, 'роль не проставлена').toBeNull();
    } finally {
      await api(a, 'DELETE', `/api/tasks/${plainId}`);
    }

    // Редактор закрывает эпик фичи.
    const closeEpic = await api(b, 'PUT', `/api/tasks/${epicId}`, { status: 'done' });
    note(`  редактор закрывает эпик фичи → ${closeEpic.status} ${JSON.stringify(closeEpic.body).slice(0, 140)}`);
    expect(closeEpic.status, 'закрытие эпика закрыто редактору').toBe(403);
    expect(closeEpic.body.code, 'код отказа feature_owner_only').toBe('feature_owner_only');
    const epicAfter = (await api(a, 'GET', `/api/tasks/${epicId}`)).body;
    expect(epicAfter.status, 'эпик не закрыт').not.toBe('done');

    // Мерж-задача фичи: редактор не может её закрыть.
    const finalTask = await api(a, 'POST', `/api/projects/${pid}/tasks`, {
      title: N('финал'), role: 'final', featureId: epicId,
    });
    expect(finalTask.status, 'владелец создаёт финальную задачу').toBe(200);
    await api(a, 'PUT', `/api/tasks/${finalTask.body.id}`, {
      status: 'done', verification: { verdict: 'accepted', notes: 'ok' },
    });
    const merge = await api(a, 'POST', `/api/projects/${pid}/tasks`, {
      title: N('мерж'), role: 'merge', featureId: epicId,
    });
    expect(merge.status, 'владелец создаёт мерж-задачу').toBe(200);
    const mergeId = merge.body.id as string;
    try {
      const closeMerge = await api(b, 'PUT', `/api/tasks/${mergeId}`, { status: 'done' });
      note(`  редактор закрывает мерж-задачу чужой фичи → ${closeMerge.status} ${JSON.stringify(closeMerge.body).slice(0, 140)}`);
      expect(closeMerge.status, 'закрытие мерж-задачи закрыто редактору').toBe(403);
      expect(closeMerge.body.code, 'код отказа feature_owner_only').toBe('feature_owner_only');
      const mergeAfter = (await api(a, 'GET', `/api/tasks/${mergeId}`)).body;
      expect(mergeAfter.status, 'мерж-задача не закрыта').not.toBe('done');

      // Контроль: владелец мерж закрывает.
      const ownerClose = await api(a, 'PUT', `/api/tasks/${mergeId}`, { status: 'done' });
      note(`  владелец закрывает мерж-задачу → ${ownerClose.status}`);
      expect(ownerClose.status, 'владелец мерж закрывает').toBe(200);
    } finally {
      await api(a, 'DELETE', `/api/tasks/${mergeId}`);
      await api(a, 'DELETE', `/api/tasks/${finalTask.body.id}`);
    }
  } finally {
    await api(a, 'DELETE', `/api/tasks/${epicId}`);
  }
});

test('3b редактор сохраняет и закрывает обычный дефект с заметкой проверки — без ошибки', async () => {
  const a = await login('admin');
  const b = await login('qa_b');
  const projects = (await api(a, 'GET', '/api/projects')).body as any[];
  // Именно общий проект приёмки: корень /home/an/qa-shared-projects/shared-proj.
  // Поиск по подстроке 'shared-proj' ловит и контейнеры ('/home/an/qa-shared-projects/…').
  const shared = projects.find(p => (p.rootPath || '').endsWith('/shared-proj'));
  const pid = shared.id as string;

  const defect = await api(a, 'POST', `/api/projects/${pid}/tasks`, {
    title: N('дефект'), kind: 'defect',
    repro: { steps: 'шаг', expected: 'ожидалось', actual: 'фактически' },
  });
  expect(defect.status, 'владелец создаёт дефект').toBe(200);
  const defectId = defect.body.id as string;
  try {
    const saved = await api(b, 'PUT', `/api/tasks/${defectId}`, {
      status: 'done',
      verification: { verdict: 'accepted_with_remarks', notes: 'проверено редактором' },
    });
    note(`  редактор сохраняет дефект с заметкой проверки → ${saved.status} ${JSON.stringify(saved.body).slice(0, 200)}`);
    expect(saved.status, 'редактор сохраняет обычный дефект').toBe(200);
    expect(saved.body.status, 'дефект закрыт').toBe('done');
    expect(saved.body.verification?.verdict, 'вердикт сохранён').toBe('accepted_with_remarks');
    expect(saved.body.verification?.notes, 'заметка проверки сохранена').toBe('проверено редактором');
  } finally {
    await api(a, 'DELETE', `/api/tasks/${defectId}`);
  }
});
