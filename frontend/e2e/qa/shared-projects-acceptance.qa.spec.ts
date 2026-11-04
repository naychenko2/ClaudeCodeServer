// QA-приёмка фичи «Общие проекты» целиком (круг 3): совместная работа, приватность, пределы, пустые состояния.
// Стенд: feat/shared-projects, порт 5557 (юнит ccs-qa-shared), учётки admin/qa_b/qa_c/qa_e/qa_fresh.
//
// Один спек на группу сценариев (docs/operations/qa-stand.md). Каждая проверка сама создаёт нужные
// сущности и убирает их за собой; после прогона в корне общего проекта не должно остаться симлинков.

import { test, expect, type Page } from '@playwright/test';
import { execFileSync } from 'node:child_process';
import { existsSync, mkdirSync, readdirSync, readFileSync, rmSync, symlinkSync, writeFileSync } from 'node:fs';

const BASE = process.env.PLAYWRIGHT_BASE_URL || 'http://127.0.0.1:5557';
const PROJECT_ID = '433434e7-12d2-4541-8101-ffd9c58bfed4';
const PROJECT_ROOT = '/home/an/qa-shared-projects/shared-proj';
const SHOTS = '/home/an/Sources/ClaudeCodeServer-shared-projects/.cc-attachments/qa-shared-accept';
const PASS = '12345';

const RUN = process.env.QA_ACCEPT_RUN || Date.now().toString(36).slice(-6);
const N = (s: string) => `QA-acc ${RUN} ${s}`;

const report: string[] = [];
function note(s: string) {
  report.push(s);
  writeFileSync(`${SHOTS}/report.txt`, report.join('\n'));
  console.log(s);
}

const tokens: Record<string, string> = {};
const ids: Record<string, string> = {};

async function login(user: string): Promise<string> {
  if (tokens[user]) return tokens[user];
  for (let i = 0; i < 10; i++) {
    const r = await fetch(`${BASE}/api/auth/login`, {
      method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ username: user, password: PASS }),
    });
    if (r.status === 200) { tokens[user] = (await r.json()).token; return tokens[user]; }
    if (r.status !== 429) throw new Error(`login ${user}: ${r.status} ${(await r.text()).slice(0, 200)}`);
    await new Promise(r => setTimeout(r, 12000));
  }
  throw new Error(`login ${user}: лимит входа не прошёл`);
}

async function api(token: string, method: string, path: string, body?: unknown, headers: Record<string, string> = {}) {
  const r = await fetch(`${BASE}${path}`, {
    method, headers: { Authorization: `Bearer ${token}`, 'Content-Type': 'application/json', ...headers },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  const txt = await r.text();
  let parsed: any = txt;
  try { parsed = JSON.parse(txt); } catch { /* текст */ }
  return { status: r.status, body: parsed };
}

async function ensureFlags() {
  for (const u of ['admin', 'qa_b', 'qa_c', 'qa_e', 'qa_fresh']) {
    const t = await login(u);
    for (const f of ['shared-projects', 'llm-wiki']) {
      const r = await api(t, 'PUT', `/api/feature-flags/${f}`, { enabled: true });
      expect(r.status, `флаг ${f} у ${u}: ${JSON.stringify(r.body)}`).toBe(200);
    }
  }
  if (!ids.admin) {
    ids.admin = (await api(await login('admin'), 'GET', '/api/auth/me')).body.userId;
    ids.b = (await api(await login('qa_b'), 'GET', '/api/auth/me')).body.userId;
    ids.c = (await api(await login('qa_c'), 'GET', '/api/auth/me')).body.userId;
    ids.fresh = (await api(await login('qa_fresh'), 'GET', '/api/auth/me')).body.userId;
  }
}

// Чат проекта: создать с уникальным именем (части прогона — отдельные процессы).
async function ensureChat(token: string, label: string): Promise<string> {
  const name = N(label);
  const list = await api(token, 'GET', `/api/projects/${PROJECT_ID}/sessions`);
  const found = (Array.isArray(list.body) ? list.body : []).find((c: any) => c.name === name);
  if (found) return found.id;
  const r = await api(token, 'POST', `/api/projects/${PROJECT_ID}/sessions`, { mode: 'default', name });
  expect(r.status, `чат ${name}: ${JSON.stringify(r.body)}`).toBe(201);
  return r.body.id;
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

async function openProject(page: Page) {
  await page.goto(`${BASE}/#/project/${PROJECT_ID}`);
  await page.reload();
  await page.waitForTimeout(2500);
}

// Доска общего проекта включается флагом в localStorage (WorkspacePage: projectBoard) —
// диплинк /board без него открывает центр с чатом.
async function openBoard(page: Page) {
  await page.addInitScript((pid) => {
    try { localStorage.setItem(`cc_proj_board_${pid}`, '1'); } catch { /* ignore */ }
  }, PROJECT_ID);
  await page.goto(`${BASE}/#/project/${PROJECT_ID}`);
  await page.reload();
  await page.waitForTimeout(3500);
}

const wait = async (ms = 1500) => { await new Promise(r => setTimeout(r, ms)); };

test.describe.configure({ mode: 'serial' });
test.beforeAll(() => { mkdirSync(SHOTS, { recursive: true }); });
test.setTimeout(180_000);

// ─────────────────────────────────────────────────────────────────────────────
// 1 · Совместная работа: состав участников, общие файлы, общие задачи, заметки
// ─────────────────────────────────────────────────────────────────────────────

test('1a состав участников виден обоим: владельцу и участнику', async ({ page }) => {
  await ensureFlags();
  const a = await login('admin');
  const b = await login('qa_b');

  // Владелец: шестерёнка «Настройки проекта» в крошке → секция «Участники».
  await uiLogin(page, 'admin');
  await openProject(page);
  const gear = page.locator('button[aria-label="Настройки проекта"]').first();
  expect(await gear.isVisible().catch(() => false), 'кнопка настроек проекта видна владельцу').toBeTruthy();
  await gear.click();
  await page.waitForTimeout(1800);
  // Секция «Участники» — свёрнутый аккордеон: раскрываем.
  const acc = page.getByText('Участники', { exact: true }).first();
  expect(await acc.isVisible().catch(() => false), 'секция «Участники» есть в настройках').toBeTruthy();
  await acc.click();
  await page.waitForTimeout(1500);
  const membersHeading = page.getByText('Участники', { exact: false }).first();
  expect(await membersHeading.isVisible().catch(() => false), 'секция «Участники» открыта владельцу').toBeTruthy();
  const rowB = page.getByText('qa_b', { exact: false }).first();
  const rowBVisible = await rowB.isVisible().catch(() => false);
  note(`  владелец: «qa_b» в списке участников виден — ${rowBVisible}`);
  const roleEditor = page.getByText('Редактор', { exact: false }).first();
  note(`  владелец: подпись роли «Редактор» видна — ${await roleEditor.isVisible().catch(() => false)}`);
  await page.screenshot({ path: `${SHOTS}/1a-members-owner.png` });
  expect(rowBVisible, 'владелец видит участника qa_b в списке').toBeTruthy();

  // Участник: клик по стопке аватаров в крошке → тот же список (режим участника).
  await uiLogin(page, 'qa_b');
  await openProject(page);
  const stack = page.locator('[data-testid="members-stack"]').first();
  const stackVisible = await stack.isVisible().catch(() => false);
  note(`  участник qa_b: стопка аватаров в крошке видна — ${stackVisible}`);
  expect(stackVisible, 'участник видит стопку участников в шапке').toBeTruthy();
  await stack.click();
  await page.waitForTimeout(1800);
  const membersHeadingB = page.getByText('Участники', { exact: false }).first();
  const headingBVisible = await membersHeadingB.isVisible().catch(() => false);
  note(`  участник qa_b: раздел «Участники» виден — ${headingBVisible}`);
  const rowB2 = page.getByText('qa_b', { exact: false }).first();
  note(`  участник qa_b: «qa_b» в списке виден — ${await rowB2.isVisible().catch(() => false)}`);
  const addBtn = page.getByRole('button', { name: 'Добавить', exact: true }).first();
  const addVisible = await addBtn.isVisible().catch(() => false);
  note(`  участник qa_b: форма добавления («Добавить») видна — ${addVisible} (участнику не полагается)`);
  await page.screenshot({ path: `${SHOTS}/1a-members-participant.png` });
  expect(headingBVisible, 'участник видит состав участников').toBeTruthy();
  expect(addVisible, 'участнику не показывается форма приглашения').toBeFalsy();

  // API-контроль: состав одинаков с точки зрения владельца и участника.
  const viewA = (await api(a, 'GET', `/api/projects/${PROJECT_ID}/members`)).body;
  const viewB = (await api(b, 'GET', `/api/projects/${PROJECT_ID}/members`)).body;
  note(`  состав: владелец видит ${viewA.members.length} участников (myRole=${viewA.myRole}), участник — ${viewB.members.length} (myRole=${viewB.myRole})`);
  expect(viewB.members.map((m: any) => m.username).sort()).toEqual(viewA.members.map((m: any) => m.username).sort());
  expect(viewB.myRole, 'участник видит свою роль').toBe('editor');
});

test('1b общая задача: назначается участнику и видна обоим', async ({ page }) => {
  await ensureFlags();
  const a = await login('admin');
  const b = await login('qa_b');
  const c = await login('qa_c');

  const title = N('общая задача');
  const created = await api(a, 'POST', `/api/projects/${PROJECT_ID}/tasks`, { title });
  expect(created.status, `создание задачи: ${JSON.stringify(created.body)}`).toBe(200);
  const taskId = created.body.id as string;

  const assigned = await api(a, 'PUT', `/api/tasks/${taskId}`, { assigneeUserId: ids.b });
  expect(assigned.status, `назначение участнику: ${JSON.stringify(assigned.body)}`).toBe(200);
  expect(assigned.body.assigneeUserId, 'исполнитель — участник qa_b').toBe(ids.b);

  // Видна обоим.
  const listB = (await api(b, 'GET', `/api/projects/${PROJECT_ID}/tasks`)).body as any[];
  const listC = (await api(c, 'GET', `/api/projects/${PROJECT_ID}/tasks`)).body as any[];
  const seenB = listB.find(t => t.id === taskId);
  const seenC = listC.find(t => t.id === taskId);
  note(`  задача видна qa_b: ${!!seenB}, qa_c: ${!!seenC}; исполнитель у qa_b: ${seenB?.assigneeUserId === ids.b}`);
  expect(seenB, 'участник видит задачу').toBeTruthy();
  expect(seenC, 'другой участник видит задачу').toBeTruthy();
  expect(seenB.assigneeUserId, 'исполнитель виден участнику').toBe(ids.b);

  // В интерфейсе: карточка на доске общего проекта под дорожкой участника.
  await uiLogin(page, 'qa_b');
  await openBoard(page);
  const card = page.getByText(title, { exact: false }).first();
  const visible = await card.isVisible().catch(() => false);
  note(`  доска: карточка «${title}» видна участнику — ${visible}`);
  await page.screenshot({ path: `${SHOTS}/1b-board-participant.png` });
  expect(visible, 'карточка общей задачи видна участнику на доске').toBeTruthy();

  // Посторонний (qa_d) — не участник: 404.
  const d = await login('qa_d');
  const listD = await api(d, 'GET', `/api/projects/${PROJECT_ID}/tasks`);
  note(`  посторонний qa_d получает ${listD.status} на список задач проекта`);
  expect(listD.status, 'постороннему список задач проекта недоступен').toBe(404);

  await api(a, 'DELETE', `/api/tasks/${taskId}`);
});

test('1c общие файлы, чаты и заметки видны обоим участникам', async () => {
  await ensureFlags();
  const a = await login('admin');
  const b = await login('qa_b');

  // Общий файл проекта.
  const fileName = `qa-acc-shared-${RUN}.txt`;
  const put = await api(a, 'PUT', `/api/projects/${PROJECT_ID}/files/content?path=${encodeURIComponent(fileName)}`, { content: 'общий файл' });
  expect(put.status, `запись общего файла: ${JSON.stringify(put.body)}`).toBe(200);

  const treeB = (await api(b, 'GET', `/api/projects/${PROJECT_ID}/files/tree?path=`)).body as any[];
  const treeNames = treeB.map(x => x.name);
  note(`  дерево у qa_b: файл «${fileName}» виден — ${treeNames.includes(fileName)}`);
  expect(treeNames, 'общий файл виден участнику').toContain(fileName);

  // Общий чат проекта виден участнику.
  const chatName = N('общий чат');
  const chat = await api(a, 'POST', `/api/projects/${PROJECT_ID}/sessions`, { mode: 'default', name: chatName });
  expect(chat.status, `создание общего чата: ${JSON.stringify(chat.body)}`).toBe(201);
  const chatsB = (await api(b, 'GET', `/api/projects/${PROJECT_ID}/sessions`)).body as any[];
  note(`  общий чат «${chatName}» виден qa_b — ${chatsB.some(c => c.id === chat.body.id)}`);
  expect(chatsB.some(c => c.id === chat.body.id), 'общий чат виден участнику').toBeTruthy();

  // Заметка проекта видна участнику.
  const noteTitle = N('общая заметка');
  const created = await api(a, 'POST', '/api/notes', { title: noteTitle, content: 'общая заметка', source: PROJECT_ID });
  expect(created.status, `создание заметки проекта: ${JSON.stringify(created.body)}`).toBe(200);
  const notesB = (await api(b, 'GET', `/api/notes?source=${PROJECT_ID}`)).body as any[];
  note(`  заметка «${noteTitle}» видна qa_b — ${notesB.some(n => n.title === noteTitle)}`);
  expect(notesB.some(n => n.title === noteTitle), 'заметка проекта видна участнику').toBeTruthy();

  // Уборка.
  await api(a, 'DELETE', `/api/notes/${created.body.id}`);
  await api(a, 'DELETE', `/api/projects/${PROJECT_ID}/sessions/${chat.body.id}`);
  await api(a, 'DELETE', `/api/projects/${PROJECT_ID}/files/content?path=${encodeURIComponent(fileName)}`);
});

// ─────────────────────────────────────────────────────────────────────────────
// 2 · Приватность: чужое личное не течёт (главное)
// ─────────────────────────────────────────────────────────────────────────────

test('2a приватный чат и вложение владельца не видны участнику: список, прямой адрес, поиск, дерево', async () => {
  await ensureFlags();
  const a = await login('admin');
  const b = await login('qa_b');

  // Приватный чат владельца с вложением.
  const chatName = N('приватный чат владельца');
  const chat = await api(a, 'POST', `/api/projects/${PROJECT_ID}/sessions`, { mode: 'default', name: chatName });
  expect(chat.status, `создание чата: ${JSON.stringify(chat.body)}`).toBe(201);
  const chatId = chat.body.id as string;
  const priv = await api(a, 'PUT', `/api/chats/${chatId}/visibility`, { private: true });
  expect(priv.status, `перевод в приватный: ${JSON.stringify(priv.body)}`).toBe(200);

  // Вложение в приватный чат владельца (уходит в private-каталог автора).
  const form = new FormData();
  form.append('file', new Blob(['секрет admin'], { type: 'text/plain' }), `qa-acc-secret-${RUN}.txt`);
  const up = await fetch(`${BASE}/api/chats/${chatId}/files/upload`, {
    method: 'POST', headers: { Authorization: `Bearer ${a}` }, body: form,
  });
  expect(up.status, `загрузка вложения в приватный чат: ${up.status} ${(await up.text()).slice(0, 200)}`).toBe(200);
  const upBody = await up.json().catch(() => ({} as any));
  note(`  вложение владельца: ${JSON.stringify(upBody)}`);
  const secretName = `qa-acc-secret-${RUN}.txt`;

  // 1. Список чатов: приватного чата владельца у участника нет.
  const chatsB = (await api(b, 'GET', `/api/projects/${PROJECT_ID}/sessions`)).body as any[];
  expect(chatsB.some(c => c.id === chatId), 'приватный чат владельца не виден участнику в списке').toBeFalsy();
  note(`  список чатов qa_b: приватного чата владельца нет — OK (всего ${chatsB.length})`);

  // 2. Прямой адрес приватного чата.
  const direct = await api(b, 'GET', `/api/chats/${chatId}`);
  note(`  прямой GET приватного чата от qa_b: ${direct.status}`);
  expect([403, 404], 'прямой доступ к чужому приватному чату закрыт').toContain(direct.status);
  const hist = await api(b, 'GET', `/api/projects/${PROJECT_ID}/sessions/${chatId}/history`);
  expect([403, 404], 'история чужого приватного чата закрыта').toContain(hist.status);

  // 3. Поиск по файлам: имени чужого приватного вложения в выдаче нет.
  const search = (await api(b, 'GET', `/api/projects/${PROJECT_ID}/files/search?q=${encodeURIComponent(secretName)}`)).body as any[];
  note(`  поиск по файлам (qa_b) на «${secretName}»: выдача ${search.length}`);
  expect(search, 'имя чужого приватного вложения не попадает в поиск').toEqual([]);

  // 4. Дерево: чужих private-файлов нет (свои private автора в дереве не показываются вовсе).
  const tree = (await api(b, 'GET', `/api/projects/${PROJECT_ID}/files/tree?path=`)).body as any[];
  const treePaths = tree.map(x => x.path as string);
  const leaked = treePaths.filter(p => p.includes(`private/${ids.admin}/`) || p.includes(secretName));
  note(`  дерево (qa_b): чужих private-путей ${leaked.length} из ${treePaths.length}`);
  expect(leaked, 'в дереве нет чужих private-путей').toEqual([]);

  // 5. Прямое чтение чужого private-файла — 404.
  const directRead = await api(b, 'GET', `/api/projects/${PROJECT_ID}/files/content?path=${encodeURIComponent(`.cc-attachments/private/${ids.admin}/${secretName}`)}`);
  note(`  прямое чтение чужого private-файла от qa_b: ${directRead.status}`);
  expect(directRead.status, 'чтение чужого приватного файла закрыто').toBe(404);

  // Уборка: чат каскадно уносит вложение.
  await api(a, 'DELETE', `/api/projects/${PROJECT_ID}/sessions/${chatId}`);
});

test('2b ссылка на корень проекта не открывает чужие приватные (дерево, список, поиск, чтение)', async () => {
  await ensureFlags();
  const a = await login('admin');
  const b = await login('qa_b');

  // В корне общего проекта — симлинк на корень того же проекта (в нём .cc-attachments).
  const linkName = `QA-ACC-ROOT-${RUN}`;
  const linkPath = `${PROJECT_ROOT}/${linkName}`;
  rmSync(linkPath, { force: true });
  symlinkSync(PROJECT_ROOT, linkPath);
  try {
    // Поиск: чужие private-имена через ссылку на корень не выходят
    // (свои private участника в выдаче быть могут — он их автор).
    const search = (await api(b, 'GET', `/api/projects/${PROJECT_ID}/files/search?q=${encodeURIComponent('qa-r2-priv')}`)).body as any[];
    const leaked = search.filter(x => x.path.includes(`private/${ids.admin}/`));
    const own = search.filter(x => x.path.includes(`private/${ids.b}/`));
    note(`  поиск через ссылку на корень: чужих private-имён ${leaked.length}, своих ${own.length}`);
    expect(leaked, 'через ссылку на корень чужие private не находятся').toEqual([]);

    // Список private-контейнера через ссылку: каталог чужого автора не отдаётся.
    const listPriv = await api(b, 'GET', `/api/projects/${PROJECT_ID}/files?path=${encodeURIComponent(`${linkName}/.cc-attachments/private`)}`);
    const names = (listPriv.body as any[]).map(x => x.name);
    note(`  список private через ссылку (qa_b): ${JSON.stringify(names)}`);
    expect(names, 'каталог чужого автора private не виден участнику').not.toContain(ids.admin);
    expect(names, 'свой каталог private виден').toContain(ids.b);

    // Список напрямую в каталог чужого автора — 404.
    const listForeign = await api(b, 'GET', `/api/projects/${PROJECT_ID}/files?path=${encodeURIComponent(`${linkName}/.cc-attachments/private/${ids.admin}`)}`);
    note(`  список каталога чужого автора через ссылку: ${listForeign.status}`);
    expect(listForeign.status, 'каталог чужого автора private закрыт').toBe(404);

    // Чтение чужого приватного файла через ссылку — 404.
    const read = await api(b, 'GET', `/api/projects/${PROJECT_ID}/files/content?path=${encodeURIComponent(`${linkName}/.cc-attachments/private/${ids.admin}/qa-dav-copy-r2.txt`)}`);
    note(`  чтение чужого private через ссылку: ${read.status}`);
    expect(read.status, 'чтение чужого приватного файла через ссылку закрыто').toBe(404);

    // Дерево через ссылку: контейнер .cc-attachments не перечисляет чужое.
    const tree = (await api(b, 'GET', `/api/projects/${PROJECT_ID}/files/tree?path=${encodeURIComponent(linkName)}`)).body as any[];
    const paths = tree.map(x => x.path as string);
    const privatePaths = paths.filter(p => p.includes(`/private/${ids.admin}/`) || p.includes('/private/'));
    note(`  дерево через ссылку на корень: private-путей ${privatePaths.length} из ${paths.length}`);
    expect(privatePaths, 'в дереве через ссылку чужих private нет').toEqual([]);
  } finally {
    rmSync(linkPath, { force: true });
  }
});

test('2c приватность на уровне знаний (поиск по содержимому) и заметок', async () => {
  await ensureFlags();
  const a = await login('admin');
  const b = await login('qa_b');
  const fresh = await login('qa_fresh');

  // Личная заметка владельца не видна участнику.
  const title = N('личная заметка владельца');
  const created = await api(a, 'POST', '/api/notes', { title, content: 'секретная личная заметка', source: 'personal' });
  expect(created.status, `создание личной заметки: ${JSON.stringify(created.body)}`).toBe(200);
  const personalB = (await api(b, 'GET', '/api/notes?source=personal')).body as any[];
  note(`  личные заметки qa_b: ${personalB.length}, наша — ${personalB.some(n => n.title === title)}`);
  expect(personalB.some(n => n.title === title), 'личная заметка владельца не видна участнику').toBeFalsy();
  const personalFresh = (await api(fresh, 'GET', '/api/notes?source=personal')).body as any[];
  expect(personalFresh.some(n => n.title === title), 'личная заметка владельца не видна новому участнику').toBeFalsy();

  // Знания проекта: индекс не показывает чужие приватные (пусто — нечего показывать).
  const know = await api(fresh, 'GET', `/api/projects/${PROJECT_ID}/knowledge/search?q=${encodeURIComponent(title)}`);
  note(`  поиск по знаниям проекта (qa_fresh): ${know.status}, ${JSON.stringify(know.body).slice(0, 160)}`);
  const items = (know.body as any)?.items ?? [];
  expect(items, 'в знаниях проекта чужая личная заметка не находится').toEqual([]);

  await api(a, 'DELETE', `/api/notes/${created.body.id}`);
});

// ─────────────────────────────────────────────────────────────────────────────
// 3 · Пределы: вики, превью модели, лимит владельца
// ─────────────────────────────────────────────────────────────────────────────

test('3a приватный чат не принимается вики: пересчёт и выдержка', async () => {
  await ensureFlags();
  const a = await login('admin');
  const b = await login('qa_b');

  const chatName = N('приватный чат вики');
  const chat = await api(a, 'POST', `/api/projects/${PROJECT_ID}/sessions`, { mode: 'default', name: chatName });
  const chatId = chat.body.id as string;
  const priv = await api(a, 'PUT', `/api/chats/${chatId}/visibility`, { private: true });
  expect(priv.status, 'перевод в приватный').toBe(200);

  // Пересчёт (POST /api/wiki/chats/recalculate) — отказ не приходит, но приватный чат в счёт не идёт.
  const recalcB = await api(b, 'POST', '/api/wiki/chats/recalculate', {});
  note(`  пересчёт от qa_b: ${recalcB.status} ${JSON.stringify(recalcB.body)}`);
  expect(recalcB.status, 'пересчёт доступен участнику').toBe(200);

  // Разбор приватного чата (анализ) — 404 «чат не существует».
  const analyzeB = await api(b, 'POST', `/api/wiki/chats/${chatId}/analyze`, {});
  note(`  разбор чужого приватного чата (qa_b): ${analyzeB.status} ${JSON.stringify(analyzeB.body)}`);
  expect(analyzeB.status, 'разбор чужого приватного чата закрыт').toBe(404);
  expect((analyzeB.body as any)?.code, 'код отказа not_found').toBe('not_found');

  // Даже владелец своего приватного чата: разбор не запускается (чат скрыт и от него).
  const analyzeA = await api(a, 'POST', `/api/wiki/chats/${chatId}/analyze`, {});
  note(`  разбор СВОЕГО приватного чата (admin): ${analyzeA.status} ${JSON.stringify(analyzeA.body)}`);
  expect(analyzeA.status, 'приватный чат вики не принимается и у автора').toBe(404);

  // Список чатов для вики: приватных нет ни у кого.
  const listB = await api(b, 'GET', '/api/wiki/chats');
  const listed = (listB.body as any)?.items ?? [];
  note(`  список вики-чатов (qa_b): ${listed.length}; нашего нет — ${!listed.some((x: any) => x.sessionId === chatId)}`);
  expect(listed.some((x: any) => x.sessionId === chatId), 'приватный чат в списке вики не значится').toBeFalsy();

  await api(a, 'DELETE', `/api/projects/${PROJECT_ID}/sessions/${chatId}`);
});

test('3b выдержка вики (MCP wiki_ingest) отказывает по чужому приватному чату', async () => {
  await ensureFlags();
  const a = await login('admin');
  const b = await login('qa_b');

  const chatName = N('приватный чат выдержки');
  const chat = await api(a, 'POST', `/api/projects/${PROJECT_ID}/sessions`, { mode: 'default', name: chatName });
  const chatId = chat.body.id as string;
  await api(a, 'PUT', `/api/chats/${chatId}/visibility`, { private: true });

  // Сессия-вызыватель: любой чат владельца (MCP-маршрут несёт id вызывающего).
  const caller = (await api(b, 'GET', `/api/projects/${PROJECT_ID}/sessions`)).body as any[];
  const callerChat = caller.find(c => !c.privateToUserId);
  expect(callerChat, 'у qa_b есть общий чат проекта').toBeTruthy();

  const call = await fetch(`${BASE}/mcp/notes/${callerChat.id}`, {
    method: 'POST',
    headers: { Authorization: `Bearer ${b}`, 'Content-Type': 'application/json' },
    body: JSON.stringify({ jsonrpc: '2.0', id: 1, method: 'tools/call', params: { name: 'wiki_ingest', arguments: { source: `session:${chatId}` } } }),
  });
  const res = await call.json();
  const text = (res?.result?.content ?? []).map((c: any) => c.text).join(' ');
  note(`  wiki_ingest по чужому приватному чату: isError=${res?.result?.isError}, текст «${text}»`);
  expect(res?.result?.isError, 'выдержка чужого приватного чата отказана').toBe(true);
  expect(text, 'отказ не выдаёт существование чата').toContain('не найден');

  await api(a, 'DELETE', `/api/projects/${PROJECT_ID}/sessions/${chatId}`);
});

test('3c превью модели не раскрывает чужой приватный чат', async () => {
  await ensureFlags();
  const a = await login('admin');
  const b = await login('qa_b');
  const fresh = await login('qa_fresh');

  const chatName = N('приватный чат превью');
  const chat = await api(a, 'POST', `/api/projects/${PROJECT_ID}/sessions`, { mode: 'default', name: chatName });
  const chatId = chat.body.id as string;
  await api(a, 'PUT', `/api/chats/${chatId}/visibility`, { private: true });

  const prevB = await api(b, 'GET', `/api/models/preview?sessionId=${chatId}`);
  note(`  превью чужого приватного чата (qa_b): ${prevB.status} ${JSON.stringify(prevB.body)}`);
  expect(prevB.status, 'превью чужого приватного чата закрыто').toBe(404);
  expect((prevB.body as any)?.error, 'ответ неотличим от «нет такого чата»').toBe('Сессия не найдена');

  const prevFresh = await api(fresh, 'GET', `/api/models/preview?sessionId=${chatId}`);
  expect(prevFresh.status, 'превью чужого приватного чата закрыто и для нового участника').toBe(404);

  await api(a, 'DELETE', `/api/projects/${PROJECT_ID}/sessions/${chatId}`);
});

test('3d «Модели и расход»: участнику чужого общего проекта показан лимит владельца проекта', async ({ page }) => {
  await ensureFlags();
  const b = await login('qa_b');

  await uiLogin(page, 'qa_b');
  await page.goto(`${BASE}/#/models`);
  await page.reload();
  await page.waitForTimeout(3000);

  const modal = page.getByText('Модели и расход', { exact: false }).first();
  note(`  модалка «Модели и расход» открылась — ${await modal.isVisible().catch(() => false)}`);

  // Вкладка «Цепочки» — блок «Подмен за один ход» и подсказка о лимите владельца.
  const chainsTab = page.getByText('Цепочки', { exact: true }).first();
  if (await chainsTab.isVisible().catch(() => false)) {
    await chainsTab.click();
    await page.waitForTimeout(2000);
  }
  const budget = page.getByText('Подмен за один ход', { exact: false }).first();
  const budgetVisible = await budget.isVisible().catch(() => false);
  note(`  блок «Подмен за один ход» виден участнику — ${budgetVisible}`);

  const ownerHint = page.getByText('В общих проектах других владельцев действует лимит владельца проекта', { exact: false }).first();
  const hintVisible = await ownerHint.isVisible().catch(() => false);
  note(`  подсказка о лимите владельца проекта видна участнику — ${hintVisible}`);
  await page.screenshot({ path: `${SHOTS}/3d-chains-participant.png` });

  expect(budgetVisible, 'блок бюджета подмен виден участнику').toBeTruthy();
  expect(hintVisible, 'участнику чужого общего проекта показана подсказка о лимите владельца').toBeTruthy();
});

// ─────────────────────────────────────────────────────────────────────────────
// 4 · Пустые состояния нового участника
// ─────────────────────────────────────────────────────────────────────────────

test('4a новый участник без личных данных: пустые состояния и понятные подсказки', async ({ page }) => {
  await ensureFlags();
  const fresh = await login('qa_fresh');

  // Личных задач нет — в списке только проектные.
  const tasks = (await api(fresh, 'GET', '/api/tasks')).body as any[];
  const personal = tasks.filter(t => !t.projectId);
  note(`  личных задач у qa_fresh: ${personal.length} (всего в /api/tasks: ${tasks.length})`);
  expect(personal, 'личных задач у нового участника нет').toEqual([]);

  // Личных заметок нет.
  const personalNotes = (await api(fresh, 'GET', '/api/notes?source=personal')).body as any[];
  note(`  личных заметок у qa_fresh: ${personalNotes.length}`);
  expect(personalNotes, 'личных заметок нет').toEqual([]);

  // Приватных чатов нет.
  const chats = (await api(fresh, 'GET', `/api/projects/${PROJECT_ID}/sessions`)).body as any[];
  const privChats = chats.filter(c => c.privateToUserId);
  note(`  чатов проекта у qa_fresh: ${chats.length}, приватных ${privChats.length}`);
  expect(privChats, 'приватных чатов у нового участника нет').toEqual([]);

  // В интерфейсе: раздел «Чаты» — своих чатов у нового участника нет,
  // пустое состояние с понятной подсказкой и кнопкой «Новый чат».
  await uiLogin(page, 'qa_fresh');
  await page.goto(`${BASE}/#/chats`);
  await page.reload();
  await page.waitForTimeout(3000);
  const emptyChats = page.getByText('О чём поговорим?', { exact: false }).first();
  const emptyChatsVisible = await emptyChats.isVisible().catch(() => false);
  const newChatBtn = page.getByRole('button', { name: 'Новый чат', exact: false }).first();
  const newChatVisible = await newChatBtn.isVisible().catch(() => false);
  note(`  пустое состояние своих чатов — ${emptyChatsVisible}; кнопка «Новый чат» — ${newChatVisible}`);
  await page.screenshot({ path: `${SHOTS}/4a-chats-empty.png` });
  expect(emptyChatsVisible, 'у нового участника понятное пустое состояние своих чатов').toBeTruthy();
  expect(newChatVisible, 'в пустом состоянии есть кнопка «Новый чат»').toBeTruthy();

  // Раздел «Заметки»: личных заметок нет — в списке только проектный источник.
  // Диплинк #/notes при полной перезагрузке уводит в «Чаты» (стор подсистем пуст до /api/auth/me),
  // поэтому открываем вкладку кликом по хабу.
  await page.goto(`${BASE}/#/home`);
  await page.reload();
  await page.waitForTimeout(2500);
  const notesTab = page.getByText('Заметки', { exact: true }).first();
  if (await notesTab.isVisible().catch(() => false)) {
    await notesTab.click();
    await page.waitForTimeout(2500);
  }
  const personalGroup = page.getByText('Личный', { exact: true }).first();
  const personalVisible = await personalGroup.isVisible().catch(() => false);
  note(`  раздел «Заметки»: группа «Личный» видна — ${personalVisible} (у нового участника личных заметок нет)`);
  await page.screenshot({ path: `${SHOTS}/4a-notes-empty.png` });
  expect(personalVisible, 'личных заметок у нового участника в разделе «Заметки» нет').toBeFalsy();

  // Доск общего проекта не пустая (общие задачи есть) и заголовок есть.
  await openBoard(page);
  const board = page.locator('.cc-board-scroll').first();
  note(`  доска общего проекта видна новому участнику — ${await board.isVisible().catch(() => false)}`);
  await page.screenshot({ path: `${SHOTS}/4a-board-fresh.png` });
  expect(await board.isVisible().catch(() => false), 'доска общего проекта доступна новому участнику').toBeTruthy();
});

test('4b уборка: в корне общего проекта не осталось симлинков приёмки', async () => {
  const left = readdirSync(PROJECT_ROOT).filter(n => n.startsWith('QA-ACC-'));
  note(`  остатки в корне проекта: ${JSON.stringify(left)}`);
  expect(left, 'симлинки приёмки убраны').toEqual([]);
});
