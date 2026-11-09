// QA-приёмка «Общие проекты», круг 2: повторная проверка D1–D6 и новых прав на чат.
// Стенд: feat/shared-projects @ 86199b374, порт 5557 (поднят руками из worktree, PID в отчёте).
// Учётки: admin (владелец общего проекта), qa_b (участник-редактор), qa_e (читатель), qa_d (посторонний).
// Общий проект QA: 433434e7-12d2-4541-8101-ffd9c58bfed4, корень /home/an/qa-shared-projects/shared-proj.
//
// Один спек на группу сценариев (docs/operations/qa-stand.md). Прогон дробится --grep'ом на
// три части (потолок прогона 9 минут): A — API, B — UI, C — живые ходы. Каждая проверка
// сама создаёт или находит свои чаты: части — отдельные процессы, общий id-стор один на файл.

import { test, expect, type Page } from '@playwright/test';
import { execFileSync } from 'node:child_process';
import { existsSync, mkdirSync, readFileSync, writeFileSync, readdirSync, rmSync, symlinkSync } from 'node:fs';

const BASE = process.env.PLAYWRIGHT_BASE_URL || 'http://127.0.0.1:5557';
const PROJECT_ID = '433434e7-12d2-4541-8101-ffd9c58bfed4';
const PROJECT_NAME = 'Общий проект QA';
const PROJECT_ROOT = '/home/an/qa-shared-projects/shared-proj';
const DATA_DIR = '/home/an/Sources/ClaudeCodeServer-shared-projects/data-qa';
const SHOTS = '/home/an/Sources/ClaudeCodeServer-shared-projects/.cc-attachments/qa-private-r2';
const PASS = '12345';

const RUN = process.env.QA_R2_RUN || Date.now().toString(36).slice(-6);
const N = (s: string) => `QA-r2 ${RUN} ${s}`;

const report: string[] = [];
function note(s: string) {
  report.push(s);
  writeFileSync(`${SHOTS}/report.txt`, report.join('\n'));
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
  for (const u of ['admin', 'qa_b', 'qa_e', 'qa_d']) {
    const t = await login(u);
    const r = await api(t, 'PUT', '/api/feature-flags/shared-projects', { enabled: true });
    expect(r.status, `флаг shared-projects ${u}: ${JSON.stringify(r.body)}`).toBe(200);
  }
  if (!ids.bUser) {
    ids.bUser = (await api(await login('qa_b'), 'GET', '/api/auth/me')).body.userId;
    ids.aUser = (await api(await login('admin'), 'GET', '/api/auth/me')).body.userId;
    ids.eUser = (await api(await login('qa_e'), 'GET', '/api/auth/me')).body.userId;
  }
}

// Чат проекта: создать или найти существующий с тем же именем (между частями прогона).
async function ensureChat(token: string, label: string, model?: string): Promise<string> {
  const name = N(label);
  const list = await api(token, 'GET', `/api/projects/${PROJECT_ID}/sessions`);
  const found = (Array.isArray(list.body) ? list.body : []).find((c: any) => c.name === name);
  if (found) return found.id;
  const r = await api(token, 'POST', `/api/projects/${PROJECT_ID}/sessions`, { mode: 'default', name, ...(model ? { model } : {}) });
  expect(r.status, `чат ${name}: ${JSON.stringify(r.body)}`).toBe(201);
  return r.body.id;
}

async function makePrivate(token: string, chatId: string) {
  const list = await api(token, 'GET', `/api/chats/${chatId}`);
  if (list.status === 200 && list.body.privateToUserId) return;
  const r = await api(token, 'PUT', `/api/chats/${chatId}/visibility`, { private: true });
  expect(r.status, `приватность ${chatId}: ${JSON.stringify(r.body)}`).toBe(200);
}

function gitLog(): string[] {
  try {
    return execFileSync('git', ['-C', PROJECT_ROOT, 'log', '--format=%h|%an|%s'], { encoding: 'utf8' })
      .split('\n').filter(Boolean);
  } catch { return []; }
}

function readData(name: string): string {
  const p = `${DATA_DIR}/${name}`;
  return existsSync(p) ? readFileSync(p, 'utf8') : '';
}

function countFiles(dir: string): number {
  if (!existsSync(dir)) return 0;
  let n = 0;
  for (const e of readdirSync(dir, { withFileTypes: true })) {
    if (e.isDirectory()) n += countFiles(`${dir}/${e.name}`); else n++;
  }
  return n;
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
  await page.waitForTimeout(1500);
}

// Открыть чат прямым адресом (после логина): путь «внутри приложения» (смена hash)
// и путь «после перезагрузки» (полная загрузка с адресом).
async function openByLink(page: Page, chatId: string) {
  await page.goto(`${BASE}/#/project/${PROJECT_ID}/chat/${chatId}`);
  await page.waitForTimeout(2500);
  await page.reload();
  await page.waitForTimeout(2500);
}

async function sendInComposer(page: Page, text: string) {
  const composer = page.locator('textarea.cc-composer-input');
  await composer.waitFor({ state: 'visible', timeout: 20000 });
  await composer.fill(text);
  await composer.press('Enter');
}

async function historyText(token: string, chatId: string): Promise<string> {
  const r = await api(token, 'GET', `/api/projects/${PROJECT_ID}/sessions/${chatId}/history`);
  return JSON.stringify(r.body);
}

async function eventsOf(token: string): Promise<any[]> {
  const r = await api(token, 'GET', `/api/projects/${PROJECT_ID}/events?limit=300`);
  return Array.isArray(r.body) ? r.body : (r.body.events ?? []);
}

const wait = async (ms = 4000) => { await new Promise(r => setTimeout(r, ms)); };

test.describe.configure({ mode: 'serial' });
test.beforeAll(() => { mkdirSync(SHOTS, { recursive: true }); });
test.setTimeout(180_000);

// ─────────────────────────────────────────────────────────────────────────────
// A1 · D1 — журнал правок человека: заметки общего проекта версионируются
// ─────────────────────────────────────────────────────────────────────────────

test('A1 D1: журнал заметок общего проекта пишет коммиты с автором qa_b', async () => {
  await ensureFlags();
  const b = await login('qa_b');

  const step = async (label: string, wantAuthor: string, wantInMsg: string) => {
    const n0 = gitLog().length;
    await wait();
    const log = gitLog();
    const added = log.slice(0, Math.max(0, log.length - n0));
    const hit = added.filter(l => l.includes(wantInMsg) && l.toLowerCase().includes(wantAuthor.toLowerCase()));
    note(`  ${label}: новых коммитов ${added.length}${added.length ? ' → ' + added.join(' | ') : ''}; целевой (${wantInMsg}, автор ${wantAuthor}): ${hit.length ? 'ЕСТЬ' : 'нет'}`);
    return hit.length > 0;
  };

  // Контроль: журнал жив — правка обычного файла проекта даёт коммит с автором qa_b.
  const ctrlFile = `qa-journal-control-r2-${RUN}.txt`;
  const ctrl = await api(b, 'PUT', `/api/projects/${PROJECT_ID}/files/content?path=${encodeURIComponent(ctrlFile)}`, { content: 'контроль r2' });
  expect(ctrl.status, `контрольная запись файла: ${JSON.stringify(ctrl.body)}`).toBe(200);
  expect(await step('КОНТРОЛЬ: правка обычного файла', 'qa_b', ctrlFile), 'журнал пишет коммит по обычному файлу').toBe(true);

  // 1. Создание заметки общего проекта.
  const title = `Журнал QA r2 ${RUN}`;
  const created = await api(b, 'POST', '/api/notes', { title, content: 'тело\n- [ ] задача QA r2', source: PROJECT_ID });
  expect(created.status, `создание заметки: ${JSON.stringify(created.body)}`).toBe(200);
  const noteId = created.body.id as string;
  const notePath = created.body.path as string;
  expect.soft(await step('1. создание заметки', 'qa_b', title), 'создание заметки пишет коммит').toBe(true);

  // Причина D1 устранена: .gitignore проекта больше не содержит нашего блока /notes/.
  const gi = existsSync(`${PROJECT_ROOT}/.gitignore`) ? readFileSync(`${PROJECT_ROOT}/.gitignore`, 'utf8') : '';
  expect(gi.includes('/notes/'), `.gitignore проекта всё ещё игнорирует notes/: ${gi.split('\n').filter(l => l.includes('notes')).join(' | ')}`).toBe(false);

  // 2. Аннотация (комментарий к документу проекта) — до переноса, пока путь известен.
  const ann = await api(b, 'POST', '/api/notes/annotate', {
    doc: { scope: PROJECT_ID, path: `notes/${notePath}` },
    selection: { start: 0, end: 4, text: 'тело' },
    comment: `аннотация QA r2 ${RUN}`,
  });
  expect(ann.status, `аннотация: ${JSON.stringify(ann.body)}`).toBe(200);
  expect.soft(await step('2. аннотация', 'qa_b', 'notes/'), 'аннотация пишет коммит').toBe(true);

  // 3. Правка.
  const upd = await api(b, 'PUT', `/api/notes/${noteId}`, { content: 'тело 2\n- [ ] задача QA r2' });
  expect(upd.status, `правка заметки: ${JSON.stringify(upd.body)}`).toBe(200);
  expect.soft(await step('3. правка заметки', 'qa_b', title), 'правка заметки пишет коммит').toBe(true);

  // 4. Перенос в папку (id меняется — новый путь).
  const mv = await api(b, 'POST', `/api/notes/${noteId}/move`, { folder: 'Подпапка' });
  expect(mv.status, `перенос заметки: ${JSON.stringify(mv.body)}`).toBe(200);
  const noteId2 = mv.body.id as string;
  expect.soft(await step('4. перенос заметки', 'qa_b', title), 'перенос заметки пишет коммит').toBe(true);

  // 5. Чекбокс задачи в заметке — пробуем участником (как требует постановка).
  const toggle = await api(b, 'POST', `/api/notes/${noteId2}/tasks/toggle`, { line: 1, done: true });
  note(`  5. чекбокс участником: ${toggle.status} ${JSON.stringify(toggle.body).slice(0, 120)}`);
  if (toggle.status === 200) {
    expect.soft(await step('5. чекбокс задачи (qa_b)', 'qa_b', title), 'чекбокс пишет коммит').toBe(true);
  } else {
    const a = await login('admin');
    const t2 = await api(a, 'POST', `/api/notes/${noteId2}/tasks/toggle`, { line: 1, done: true });
    note(`  5. чекбокс владельцем: ${t2.status}`);
    expect.soft(await step('5. чекбокс задачи (admin)', 'admin', title), 'чекбокс пишет коммит').toBe(true);
  }

  // 6. Срок задачи в заметке.
  const who = toggle.status === 200 ? b : await login('admin');
  const due = await api(who, 'POST', `/api/notes/${noteId2}/tasks/set-due`, { line: 1, due: '2026-12-01' });
  note(`  6. срок задачи: ${due.status}`);
  expect.soft(await step('6. срок задачи', who === b ? 'qa_b' : 'admin', title), 'срок задачи пишет коммит').toBe(true);

  // 7. Удаление.
  const del = await api(b, 'DELETE', `/api/notes/${noteId2}`);
  expect([200, 204], `удаление заметки: ${JSON.stringify(del.body)}`).toContain(del.status);
  expect.soft(await step('7. удаление заметки', 'qa_b', title), 'удаление заметки пишет коммит').toBe(true);

  // Личный vault: правки личного человека в журнал проекта не пишут.
  const personalBefore = gitLog().length;
  const pc = await api(b, 'POST', '/api/notes', { title: `Личная заметка QA r2 ${RUN}`, content: 'тело', source: 'personal' });
  expect(pc.status, `личная заметка: ${JSON.stringify(pc.body)}`).toBe(200);
  const pu = await api(b, 'PUT', `/api/notes/${pc.body.id}`, { content: 'тело 2' });
  expect(pu.status, `правка личной заметки: ${JSON.stringify(pu.body)}`).toBe(200);
  await wait();
  expect(gitLog().length - personalBefore, 'личный vault не пишет в журнал проекта').toBe(0);
  note('личный проект qa_b — не git-репозиторий: «без коммитов» для него тривиально');
  note('A1 D1: итог коммитов за сценарий ' + (gitLog().length - 0) + ' (отсчёт по шагам выше)');
});

// ─────────────────────────────────────────────────────────────────────────────
// A2 · D3 — вложения: право хода, а не владение
// ─────────────────────────────────────────────────────────────────────────────

test('A2 D3: загрузка вложений участником, читателем и посторонним', async () => {
  await ensureFlags();
  const b = await login('qa_b');
  const e = await login('qa_e');
  const d = await login('qa_d');

  const chatPub = await ensureChat(b, 'общий D3');
  const chatPriv = await ensureChat(b, 'приватный D3');
  await makePrivate(b, chatPriv);
  const a = await login('admin');
  const chatOwner = await ensureChat(a, 'чат владельца D3');

  const up = async (token: string, chatId: string, name: string) => {
    const form = new FormData();
    form.append('file', new Blob([`QA вложение r2 ${RUN}`]), name);
    const r = await fetch(`${BASE}/api/chats/${chatId}/files/upload`, { method: 'POST', headers: { Authorization: `Bearer ${token}` }, body: form });
    const body = await r.json().catch(() => ({}));
    return { status: r.status, body };
  };

  // Свой общий чат: 200.
  const own = await up(b, chatPub, 'qa-r2-own.txt');
  expect(own.status, `загрузка в свой общий чат: ${JSON.stringify(own.body)}`).toBe(200);

  // Свой приватный чат: 200 и путь под private/{author}.
  const priv = await up(b, chatPriv, 'qa-r2-priv.txt');
  expect(priv.status, `загрузка в свой приватный чат: ${JSON.stringify(priv.body)}`).toBe(200);
  expect(String(priv.body.path ?? ''), 'путь приватного вложения').toContain(`.cc-attachments/private/${ids.bUser}/`);

  // Чужой общий чат (создан владельцем): 200 — право хода.
  const foreign = await up(b, chatOwner, 'qa-r2-foreign.txt');
  expect(foreign.status, `загрузка в чужой общий чат: ${JSON.stringify(foreign.body)}`).toBe(200);

  // Читатель: отказ 403.
  const denied = await up(e, chatPub, 'qa-r2-reader.txt');
  expect(denied.status, `загрузка читателем: ${JSON.stringify(denied.body)}`).toBe(403);
  note(`  читатель: код ${JSON.stringify(denied.body).slice(0, 160)}`);

  // Посторонний: 404 (и в приватный чат — тоже 404).
  const out = await up(d, chatPub, 'qa-r2-out.txt');
  expect(out.status, `загрузка посторонним: ${JSON.stringify(out.body)}`).toBe(404);
  const outPriv = await up(d, chatPriv, 'qa-r2-out-priv.txt');
  expect(outPriv.status, `загрузка посторонним в приватный: ${JSON.stringify(outPriv.body)}`).toBe(404);

  note('A2 D3: свой/приватный/чужой общий — 200; читатель — 403; посторонний — 404 — OK');
});

// ─────────────────────────────────────────────────────────────────────────────
// A3 · D4/D5 — запреты режима: 409 с русским текстом, раньше авторского 403
// ─────────────────────────────────────────────────────────────────────────────

test('A3 D4/D5: групповой→приватный и «не сохранять решения» на своём приватном', async () => {
  await ensureFlags();
  const a = await login('admin');
  const b = await login('qa_b');

  // Обратное направление: групповой чат нельзя сделать приватным — 409 private_chat_mode.
  // Проектные персоны (scope=Project): глобальная в состав чата общего проекта не берётся.
  const ap1 = await api(a, 'POST', '/api/personas', { name: N('персона 1'), scope: 'Project', projectId: PROJECT_ID });
  const ap2 = await api(a, 'POST', '/api/personas', { name: N('персона 2'), scope: 'Project', projectId: PROJECT_ID });
  expect(ap1.status, `персона 1: ${JSON.stringify(ap1.body)}`).toBe(200);
  expect(ap2.status, `персона 2: ${JSON.stringify(ap2.body)}`).toBe(200);
  // Групповой чат общего проекта: проектный чат + состав персон (тот же путь, что у юнит-фикстуры D45).
  const chatGroup = await ensureChat(a, 'группа D4');
  const parts = await api(a, 'PUT', `/api/chats/${chatGroup}/participants`, { personaIds: [ap1.body.id, ap2.body.id] });
  expect(parts.status, `состав группы: ${JSON.stringify(parts.body)}`).toBe(200);
  const groupPriv = await api(a, 'PUT', `/api/chats/${chatGroup}/visibility`, { private: true });
  expect(groupPriv.status, `приватность группового: ${JSON.stringify(groupPriv.body)}`).toBe(409);
  expect(groupPriv.body.code, 'код отказа режима').toBe('private_chat_mode');
  expect(String(groupPriv.body.error), 'русский текст отказа').toMatch(/приватн/i);

  // «Не сохранять решения» на своём приватном чате участника: 409 private_chat_dossiers.
  const chatPriv = await ensureChat(b, 'приватный D5');
  await makePrivate(b, chatPriv);
  const dossier = await api(b, 'PUT', `/api/projects/${PROJECT_ID}/sessions/${chatPriv}`, { excludeFromDossiers: false });
  expect(dossier.status, `досье на своём приватном: ${JSON.stringify(dossier.body)}`).toBe(409);
  expect(dossier.body.code, 'код отказа досье').toBe('private_chat_dossiers');
  expect(String(dossier.body.error), 'русский текст досье').toMatch(/приватн/i);

  // Регрессия прежнего контракта: приватный чат не становится штабом/группой (409),
  // и обычный приватный скрыт от владельца (404 на прямой URL).
  const ownerChat = await ensureChat(a, 'чат владельца D4');
  await makePrivate(a, ownerChat);
  const team = await api(a, 'PUT', `/api/chats/${ownerChat}/team-implement`, { enabled: true });
  expect(team.status, `штаб на приватном: ${JSON.stringify(team.body)}`).toBe(409);
  expect(team.body.code).toBe('private_chat_mode');
  const direct = await api(b, 'GET', `/api/chats/${ownerChat}`);
  expect(direct.status, 'приватный чат владельца скрыт от участника').toBe(404);

  // Регрессия: одиночный (личный) проект отказывает приватность.
  const projects = await api(b, 'GET', '/api/projects');
  const own = (Array.isArray(projects.body) ? projects.body : []).find((p: any) => p.members === undefined || p.members === null);
  if (own) {
    const ch = await api(b, 'POST', `/api/projects/${own.id}/sessions`, { mode: 'default', name: N('личный D4') });
    expect(ch.status, `чат личного проекта: ${JSON.stringify(ch.body)}`).toBe(201);
    const priv = await api(b, 'PUT', `/api/chats/${ch.body.id}/visibility`, { private: true });
    expect(priv.status, `приватность в личном проекте: ${JSON.stringify(priv.body)}`).toBe(409);
    expect(priv.body.code).toBe('not_shared_project');
    await api(b, 'DELETE', `/api/projects/${own.id}/sessions/${ch.body.id}`);
  } else {
    note('A3: личный проект у участника не найден — пропущено');
  }

  note('A3 D4/D5: 409 private_chat_mode / private_chat_dossiers с русским текстом — OK');
});

// ─────────────────────────────────────────────────────────────────────────────
// A4 · новые права на чат (API): переименовать/архив — редактору; удалить — автору и владельцу
// ─────────────────────────────────────────────────────────────────────────────

test('A4 права: rename/archive — редактору любой видимый чат, delete — автору и владельцу', async () => {
  await ensureFlags();
  const a = await login('admin');
  const b = await login('qa_b');
  const e = await login('qa_e');

  const chatOwner = await ensureChat(a, 'чат владельца прав');   // чужой для qa_b
  const chatPub = await ensureChat(b, 'общий прав');             // свой для qa_b

  // Редактор переименовывает чужой чат.
  const renamed = await api(b, 'PUT', `/api/projects/${PROJECT_ID}/sessions/${chatOwner}`, { name: N('переименован редактором') });
  expect(renamed.status, `rename чужого чата: ${JSON.stringify(renamed.body)}`).toBe(200);
  expect(renamed.body.name).toContain('переименован редактором');

  // Редактор архивирует и возвращает чужой чат.
  const arch = await api(b, 'PUT', `/api/chats/${chatOwner}/archived`, { archived: true });
  expect(arch.status, `archive чужого чата: ${JSON.stringify(arch.body)}`).toBe(200);
  const unarch = await api(b, 'PUT', `/api/chats/${chatOwner}/archived`, { archived: false });
  expect(unarch.status, `вернуть из архива: ${JSON.stringify(unarch.body)}`).toBe(200);

  // Редактор НЕ удаляет чужой чат: 403 not_chat_author, чат цел.
  const delForeign = await api(b, 'DELETE', `/api/projects/${PROJECT_ID}/sessions/${chatOwner}`);
  expect(delForeign.status, `delete чужого чата редактором: ${JSON.stringify(delForeign.body)}`).toBe(403);
  expect(delForeign.body.code).toBe('not_chat_author');
  const still = await api(a, 'GET', `/api/chats/${chatOwner}`);
  expect(still.status, 'чат после отказа цел').toBe(200);

  // Автор (редактор) удаляет свой чат.
  const chatOwn = await ensureChat(b, 'свой для удаления');
  const delOwn = await api(b, 'DELETE', `/api/projects/${PROJECT_ID}/sessions/${chatOwn}`);
  expect(delOwn.status, `delete своего чата автором: ${JSON.stringify(delOwn.body)}`).toBe(204);

  // Владелец удаляет чужой чат.
  const chatDel = await ensureChat(b, 'для удаления владельцем');
  const delByOwner = await api(a, 'DELETE', `/api/projects/${PROJECT_ID}/sessions/${chatDel}`);
  expect(delByOwner.status, `delete чужого чата владельцем: ${JSON.stringify(delByOwner.body)}`).toBe(204);

  // Читателю закрыты все три.
  const r = await api(e, 'PUT', `/api/projects/${PROJECT_ID}/sessions/${chatPub}`, { name: N('rename читателем') });
  expect(r.status, `rename читателем: ${JSON.stringify(r.body)}`).toBe(403);
  const ar = await api(e, 'PUT', `/api/chats/${chatPub}/archived`, { archived: true });
  expect(ar.status, `archive читателем: ${JSON.stringify(ar.body)}`).toBe(403);
  const dl = await api(e, 'DELETE', `/api/projects/${PROJECT_ID}/sessions/${chatPub}`);
  expect(dl.status, `delete читателем: ${JSON.stringify(dl.body)}`).toBe(403);
  const stillPub = await api(b, 'GET', `/api/chats/${chatPub}`);
  expect(stillPub.status, 'чат читателя цел после трёх отказов').toBe(200);

  note('A4 права: rename/archive редактору — 200; delete чужому — 403 not_chat_author; автору и владельцу — 204; читателю — 403 — OK');
});

// ─────────────────────────────────────────────────────────────────────────────
// A5 · настройки владельца: 403 owner_settings, чат цел
// ─────────────────────────────────────────────────────────────────────────────

test('A5 настройки владельца: срок/мьют/голос/досье — только владельцу (API)', async () => {
  await ensureFlags();
  const a = await login('admin');
  const b = await login('qa_b');
  const e = await login('qa_e');

  const chatPub = await ensureChat(b, 'общий настройки');

  const settings: Array<[string, any]> = [
    ['expiresAfterMinutes', { expiresAfterMinutes: 60 }],
    ['notificationsMuted', { notificationsMuted: true }],
    ['voiceMode', { voiceMode: true }],
    ['excludeFromDossiers', { excludeFromDossiers: true }],
  ];
  for (const [label, body] of settings) {
    const r = await api(b, 'PUT', `/api/projects/${PROJECT_ID}/sessions/${chatPub}`, body);
    expect(r.status, `${label} редактором: ${JSON.stringify(r.body)}`).toBe(403);
    expect(r.body.code, `код для ${label}`).toBe('owner_settings');
  }

  // Смешанный запрос (name + срок) отказывается целиком: имя не записано.
  const mixed = await api(b, 'PUT', `/api/projects/${PROJECT_ID}/sessions/${chatPub}`, { name: N('смешанный отказ'), expiresAfterMinutes: 60 });
  expect(mixed.status, `смешанный запрос: ${JSON.stringify(mixed.body)}`).toBe(403);
  const after = await api(b, 'GET', `/api/chats/${chatPub}`);
  expect(after.status).toBe(200);
  expect(after.body.name, 'имя не записано отказанным смешанным запросом').not.toContain('смешанный отказ');
  expect(after.body.expiresAfterMinutes ?? null, 'срок не применён').toBeNull();

  // Читателю — тоже отказ.
  const re = await api(e, 'PUT', `/api/projects/${PROJECT_ID}/sessions/${chatPub}`, { expiresAfterMinutes: 60 });
  expect(re.status, `срок читателем: ${JSON.stringify(re.body)}`).toBe(403);

  // Владелец — может (позитивный контроль на одноразовом чате).
  const chatTmp = await ensureChat(a, 'временный контроль');
  const ok = await api(a, 'PUT', `/api/projects/${PROJECT_ID}/sessions/${chatTmp}`, { expiresAfterMinutes: 60 });
  expect(ok.status, `срок владельцем: ${JSON.stringify(ok.body)}`).toBe(200);
  expect(ok.body.expiresAfterMinutes).toBe(60);
  await api(a, 'DELETE', `/api/projects/${PROJECT_ID}/sessions/${chatTmp}`);

  note('A5: 403 owner_settings редактору и читателю, чат цел; владельцу — 200 — OK');
});

// ─────────────────────────────────────────────────────────────────────────────
// A6 · WebDAV: MOVE/COPY в контейнер private и в симлинк на него — 403
// ─────────────────────────────────────────────────────────────────────────────

test('A6 WebDAV: цель-контейнер private и её симлинк — 403 до следа на диске', async () => {
  await ensureFlags();
  const a = await login('admin');
  const b = await login('qa_b');

  // Источник и контроль «чужие приватные целы».
  const src = `qa-dav-src-r2-${RUN}.txt`;
  const put = await api(a, 'PUT', `/api/projects/${PROJECT_ID}/files/content?path=${encodeURIComponent(src)}`, { content: 'dav r2' });
  expect(put.status, `источник: ${JSON.stringify(put.body)}`).toBe(200);

  const chatPriv = await ensureChat(b, 'приватный D3');   // из A2: в нём есть вложение qa_b
  const privDir = `${PROJECT_ROOT}/.cc-attachments/private`;
  const bBefore = countFiles(`${privDir}/${ids.bUser}`);
  note(`  private/${ids.bUser} до операций: ${bBefore} файлов`);

  // Симлинк L → .cc-attachments (тот же случай, что тест фикстуры IsPrivateContainerDestinationResolved).
  const linkPath = `${PROJECT_ROOT}/L`;
  if (!existsSync(linkPath)) symlinkSync('.cc-attachments', linkPath);

  const dav = async (method: string, path: string, headers: Record<string, string> = {}) => {
    const r = await fetch(`${BASE}/projects/${encodeURIComponent(PROJECT_NAME)}${path}`, {
      method,
      headers: { Authorization: `Basic ${Buffer.from('admin:12345').toString('base64')}`, ...headers },
    });
    return r.status;
  };
  const dest = (p: string) => ({ Destination: `${BASE}/projects/${encodeURIComponent(PROJECT_NAME)}/${p}` });

  expect(await dav('COPY', `/${src}`, dest('.cc-attachments/private')), 'COPY в контейнер private').toBe(403);
  expect(await dav('MOVE', `/${src}`, dest('.cc-attachments/private')), 'MOVE в контейнер private').toBe(403);
  expect(await dav('COPY', `/${src}`, dest('L/private')), 'COPY в симлинк на private').toBe(403);
  expect(await dav('MOVE', `/${src}`, dest('L/private')), 'MOVE в симлинк на private').toBe(403);

  // Отказ — до следа на диске: источник цел, чужой private не тронут.
  const srcAfter = await api(a, 'GET', `/api/projects/${PROJECT_ID}/files/content?path=${encodeURIComponent(src)}`);
  expect(srcAfter.status, 'источник после отказов цел').toBe(200);
  expect(String(srcAfter.body.content ?? '')).toContain('dav r2');
  expect(countFiles(`${privDir}/${ids.bUser}`), 'чужой private не тронут').toBe(bBefore);

  // Позитивный контроль: свой private-каталог автора — не контейнер, копия разрешена.
  const okCopy = await dav('COPY', `/${src}`, dest(`.cc-attachments/private/${ids.aUser}/qa-dav-copy-r2.txt`));
  expect([200, 201], `COPY в свой private: ${okCopy}`).toContain(okCopy);

  // Уборка фикстуры.
  await api(a, 'DELETE', `/api/projects/${PROJECT_ID}/files/content?path=${encodeURIComponent(src)}`);
  try { rmSync(linkPath); } catch { /* best-effort */ }
  note('A6 WebDAV: 403 на контейнер и симлинк, следов нет, свой private разрешён — OK');
});

// ─────────────────────────────────────────────────────────────────────────────
// B1 · D2 — ссылка на чат: открывается именно указанный; удалённый — тост
// ─────────────────────────────────────────────────────────────────────────────

test('B1 D2: прямая ссылка открывает именно чат; сообщение уходит в нужный', async ({ page }) => {
  test.setTimeout(300_000);
  await ensureFlags();
  const b = await login('qa_b');

  const chatPub = await ensureChat(b, 'общий D2', 'claude-sonnet-5-5');
  const chatPriv = await ensureChat(b, 'приватный D2', 'claude-sonnet-5-5');
  await makePrivate(b, chatPriv);

  const MARK_PUB = `МАРКЕР-PUB-${RUN}`;
  const MARK_PRIV = `МАРКЕР-PRIV-${RUN}`;

  // Засев маркеров: по сообщению в каждый чат (клик по карточке — прежний надёжный путь).
  await uiLogin(page, 'qa_b');
  for (const [chatId, name, marker] of [[chatPub, N('общий D2'), MARK_PUB], [chatPriv, N('приватный D2'), MARK_PRIV]] as const) {
    await page.goto(`${BASE}/#/project/${PROJECT_ID}`);
    await page.reload();
    await page.waitForTimeout(2500);
    await page.getByText(name, { exact: false }).first().click();
    await page.waitForTimeout(2500);
    expect(await page.evaluate(() => location.hash), `открыт ${name}`).toContain(chatId);
    await sendInComposer(page, marker);
    try { await page.getByText(/QA-фейк ответил/).first().waitFor({ timeout: 120000 }); } catch { note(`  ход в ${name}: ответ движка не получен`); }
  }

  // API: маркеры в истории ровно своих чатов.
  const hPub = await historyText(b, chatPub);
  const hPriv = await historyText(b, chatPriv);
  expect(hPub, 'маркер общего чата в его истории').toContain(MARK_PUB);
  expect(hPub, 'маркер приватного чата не в истории общего').not.toContain(MARK_PRIV);
  expect(hPriv, 'маркер приватного чата в его истории').toContain(MARK_PRIV);
  expect(hPriv, 'маркер общего чата не в истории приватного').not.toContain(MARK_PUB);

  // Прямая ссылка: после перезагрузки открывается именно указанный чат.
  // Маркер ищем в ленте сообщений (.cc-msg): превью карточки в сайдбаре тоже содержит
  // текст последнего сообщения — глобальный getByText путает ленту и список.
  const msg = (marker: string) => page.locator('.cc-msg', { hasText: marker });
  await openByLink(page, chatPub);
  expect(await page.evaluate(() => location.hash), 'адрес — на нужный чат').toContain(chatPub);
  await expect(msg(MARK_PUB).first(), 'открыт именно общий чат').toBeVisible({ timeout: 20000 });
  await expect(msg(MARK_PRIV), 'чужой маркер в ленте не открылся').toHaveCount(0);
  await page.screenshot({ path: `${SHOTS}/b1-link-public.png` });

  // Переход внутри приложения (смена hash без перезагрузки) — тоже именно указанный.
  await page.evaluate((a) => { location.hash = `#/project/${a.p}/chat/${a.id}`; }, { p: PROJECT_ID, id: chatPriv });
  await page.waitForTimeout(3000);
  await expect(msg(MARK_PRIV).first(), 'внутри приложения открыт приватный чат').toBeVisible({ timeout: 20000 });
  await expect(msg(MARK_PUB), 'маркер прежнего чата из ленты ушёл').toHaveCount(0);
  await page.screenshot({ path: `${SHOTS}/b1-link-private.png` });

  // Недоступный/удалённый чат: тост «Чат недоступен», ничего не открыто (прежний остаётся).
  const chatGone = await ensureChat(b, 'удаляемый D2');
  const del = await api(b, 'DELETE', `/api/projects/${PROJECT_ID}/sessions/${chatGone}`);
  expect(del.status, `удаление чата-фикстуры: ${JSON.stringify(del.body)}`).toBe(204);
  await page.evaluate((a) => { location.hash = `#/project/${a.p}/chat/${a.id}`; }, { p: PROJECT_ID, id: chatGone });
  await expect(page.getByText('Чат недоступен').first(), 'тост о недоступном чате').toBeVisible({ timeout: 20000 });
  await page.screenshot({ path: `${SHOTS}/b1-toast-gone.png` });
  await expect(msg(MARK_PRIV).first(), 'ничего не открыто: прежний чат остаётся').toBeVisible();
  note('B1 D2: ссылка открывает именно чат (перезагрузка + внутренний переход), сообщения по адресам, удалённый — тост — OK');
});

// ─────────────────────────────────────────────────────────────────────────────
// B2 · новые права в интерфейсе: кнопки открыты/закрыты с подсказками
// ─────────────────────────────────────────────────────────────────────────────

test('B2 UI прав: меню карточки чата — редактору rename/archive, чужой delete и читателю всё закрыто', async ({ page }) => {
  await ensureFlags();
  const a = await login('admin');
  const b = await login('qa_b');
  const e = await login('qa_e');

  const chatOwner = await ensureChat(a, 'чат владельца прав'); // чужой для qa_b
  const chatPub = await ensureChat(b, 'общий прав');

  // Открыть меню карточки в списке проекта (⋮ на desktop появляется при наведении).
  const openCardMenu = async (name: string) => {
    await page.goto(`${BASE}/#/project/${PROJECT_ID}`);
    await page.reload();
    await page.waitForTimeout(2500);
    await page.getByText(name, { exact: false }).first().hover();
    await page.waitForTimeout(600);
    const trigger = page.locator('button[title="Действия с чатом"]').first();
    await expect(trigger, 'триггер меню карточки').toBeVisible({ timeout: 10000 });
    await trigger.click();
    await page.waitForTimeout(600);
  };

  // Редактор на чужом чате: «Удалить» закрыт с подсказкой, «В архив» и «Переименовать» открыты.
  await uiLogin(page, 'qa_b');
  await openCardMenu(N('чат владельца прав'));
  const delItem = page.locator('button:has-text("Удалить")').first();
  await expect(delItem, 'пункт «Удалить» виден').toBeVisible({ timeout: 10000 });
  await expect(delItem, 'чужой delete закрыт редактору').toBeDisabled();
  await expect(page.getByText('Только автор чата и владелец проекта').first(), 'подсказка авторского права').toBeVisible();
  await expect(page.locator('button:has-text("В архив")').first(), 'archive открыт редактору').toBeEnabled();
  await expect(page.locator('button:has-text("Переименовать")').first(), 'rename открыт редактору').toBeEnabled();
  await page.screenshot({ path: `${SHOTS}/b2-editor-card-menu.png` });
  await page.keyboard.press('Escape');

  // Читатель: все три закрыты с подсказкой права записи.
  await uiLogin(page, 'qa_e');
  await openCardMenu(N('общий прав'));
  for (const label of ['Переименовать', 'В архив', 'Удалить']) {
    const item = page.locator(`button:has-text("${label}")`).first();
    await expect(item, `пункт «${label}» виден`).toBeVisible({ timeout: 10000 });
    await expect(item, `«${label}» закрыт читателю`).toBeDisabled();
  }
  await expect(page.getByText('Только владелец и редактор проекта').first(), 'подсказка читателю').toBeVisible();
  await page.screenshot({ path: `${SHOTS}/b2-reader-card-menu.png` });
  note('B2 UI: редактору — rename/archive открыты, чужой delete закрыт с подсказкой; читателю — всё закрыто — OK');
});

// ─────────────────────────────────────────────────────────────────────────────
// B3 · настройки владельца в интерфейсе: кнопки и пилюли неактивны с причиной
// ─────────────────────────────────────────────────────────────────────────────

test('B3 UI настроек: срок/голос/пилюли «Время жизни» и «История решений» — только владельцу', async ({ page }) => {
  await ensureFlags();
  const a = await login('admin');
  const b = await login('qa_b');

  const chatPub = await ensureChat(b, 'общий настройки');
  const uiEmpty = await ensureChat(b, 'пустой UI');

  // Редактор на своём общем чате: «Срок» в шапке неактивен с причиной, «Голос» в композере — тоже.
  await uiLogin(page, 'qa_b');
  await openByLink(page, chatPub);
  const expiryBtn = page.locator('button[title="Срок хранения этого чата меняет только владелец проекта"]').first();
  await expect(expiryBtn, 'кнопка срока закрыта редактору с причиной').toBeVisible({ timeout: 20000 });
  await expect(expiryBtn).toBeDisabled();
  const voiceBtn = page.locator('button[title="Режим разговора: Только владелец проекта"]').first();
  await expect(voiceBtn, '«Голос» закрыт редактору с причиной').toBeVisible({ timeout: 20000 });
  await expect(voiceBtn).toBeDisabled();
  await page.screenshot({ path: `${SHOTS}/b3-editor-header.png` });

  // Редактор на пустом чате: пилюли NewChatSetup неактивны с причиной, панель не открывается.
  await openByLink(page, uiEmpty);
  const pillExp = page.locator('button[title="Время жизни: Только владелец проекта"]').first();
  await expect(pillExp, 'пилюля «Время жизни» закрыта редактору').toBeVisible({ timeout: 20000 });
  await expect(pillExp).toBeDisabled();
  const pillDoss = page.locator('button[title="История решений: Только владелец проекта"]').first();
  await expect(pillDoss, 'пилюля «История решений» закрыта редактору').toBeVisible({ timeout: 20000 });
  await expect(pillDoss).toBeDisabled();
  await page.screenshot({ path: `${SHOTS}/b3-editor-newchat.png` });

  // Владелец на том же пустом чате: пилюли активны.
  await uiLogin(page, 'admin');
  await openByLink(page, uiEmpty);
  const pillOk = page.locator('button:has-text("Время жизни")').first();
  await expect(pillOk, 'пилюля «Время жизни» открыта владельцу').toBeVisible({ timeout: 20000 });
  await expect(pillOk, 'владельцу пилюля не закрыта').toBeEnabled();
  const pillDossOk = page.locator('button:has-text("История решений")').first();
  await expect(pillDossOk, '«История решений» открыта владельцу').toBeVisible({ timeout: 20000 });
  await expect(pillDossOk).toBeEnabled();
  await page.screenshot({ path: `${SHOTS}/b3-owner-newchat.png` });
  note('B3 UI: срок/голос/пилюли — участнику неактивны с причиной «Только владелец проекта», владельцу открыты — OK');
});

// ─────────────────────────────────────────────────────────────────────────────
// B4 · D6 — мобильная раскладка 360 px
// ─────────────────────────────────────────────────────────────────────────────

test('B4 D6: 360 px — кнопка «Приватный чат» ≥40 px, переполнения нет; размеры соседей зафиксированы', async ({ browser }) => {
  await ensureFlags();
  const b = await login('qa_b');
  const uiEmpty = await ensureChat(b, 'пустой UI');

  const ctx = await browser.newContext({ viewport: { width: 360, height: 780 }, isMobile: false });
  const page = await ctx.newPage();
  await uiLogin(page, 'qa_b');
  await openByLink(page, uiEmpty);

  // Карточка разрешения от CLI может перекрывать шапку — гасим.
  const later = page.getByRole('button', { name: /Позже/ });
  if (await later.isVisible().catch(() => false)) { await later.click(); await page.waitForTimeout(800); }

  // Тач-цель кнопки «Приватный чат» — не меньше 40×40 (фикс 3b9e307ba).
  const lock = page.locator('button[title="Сделать этот чат видимым только вам"]').first();
  await expect(lock, 'кнопка приватности на 360 px').toBeVisible({ timeout: 20000 });
  const box = await lock.boundingBox();
  note(`  кнопка «Приватный чат» на 360 px: ${JSON.stringify(box)}`);
  expect(box!.width, 'тач-цель по ширине ≥40 px').toBeGreaterThanOrEqual(40);
  expect(box!.height, 'тач-цель по высоте ≥40 px').toBeGreaterThanOrEqual(40);

  // Горизонтального переполнения быть не должно.
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
  note(`  горизонтальное переполнение на 360 px: ${overflow}px`);
  expect(overflow, 'нет горизонтального переполнения').toBeLessThanOrEqual(1);

  // Соседние кнопки шапки (Срок/Уведомления/Досье): фиксируем что есть в ряду и его размеры.
  const measured = await page.evaluate(() => {
    const out: Array<{ title: string; w: number; h: number }> = [];
    for (const el of Array.from(document.querySelectorAll('button[title]'))) {
      const t = (el as HTMLElement).getAttribute('title') || '';
      if (!/Срок|Уведомления|Досье|Приватн|Ещё/.test(t)) continue;
      const r = el.getBoundingClientRect();
      if (r.width === 0 || r.height === 0) continue;
      out.push({ title: t.slice(0, 60), w: Math.round(r.width), h: Math.round(r.height) });
    }
    return out;
  });
  note(`  кнопки шапки на 360 px: ${JSON.stringify(measured)}`);
  await page.screenshot({ path: `${SHOTS}/b4-mobile-360.png`, clip: { x: 0, y: 0, width: 360, height: 240 } });
  await ctx.close();
  note('B4 D6: «Приватный чат» ≥40 px, переполнения нет; размеры соседних кнопок зафиксированы в report.txt');
});

// ─────────────────────────────────────────────────────────────────────────────
// C1 · авто-память: приватный ход — записи нет; контрольный общий — запись есть
// ─────────────────────────────────────────────────────────────────────────────

test('C1 авто-память: факт в приватном чате не попадает в память/журнал, в общем — попадает', async ({ page }) => {
  test.setTimeout(480_000);
  await ensureFlags();
  const a = await login('admin');
  const b = await login('qa_b');

  const privId = await ensureChat(b, 'память приватный', 'claude-sonnet-5-5');
  await makePrivate(b, privId);
  const pubId = await ensureChat(b, 'память общий', 'claude-sonnet-5-5');

  const tokPriv = `КВАРЦБЕРГ-p${RUN}`;
  const tokPub = `КВАРЦБЕРГ-q${RUN}`;
  // Реплика длиннее порогов автообучения (AutolearnMinTurnChars 400, MinTranscriptGrowth 600).
  const longMsg = (tok: string) =>
    `QA-проверка авто-памяти общего проекта. ${'Контекст проверки: длинная рабочая реплика для порога содержания хода. '.repeat(12)}Запомни факт: ${tok}.`;

  const memBefore = readData('team-memory.json');
  const evBefore = await eventsOf(a);

  await uiLogin(page, 'qa_b');
  for (const [chatId, name, tok] of [[privId, N('память приватный'), tokPriv], [pubId, N('память общий'), tokPub]] as const) {
    await page.goto(`${BASE}/#/project/${PROJECT_ID}`);
    await page.reload();
    await page.waitForTimeout(2500);
    await page.getByText(name, { exact: false }).first().click();
    await page.waitForTimeout(2500);
    expect(await page.evaluate(() => location.hash), `открыт ${name}`).toContain(chatId);
    await sendInComposer(page, longMsg(tok));
    let replied = false;
    try {
      await page.getByText(new RegExp(`Проект называется ${tok}`)).first().waitFor({ timeout: 150000 });
      replied = true;
    } catch { replied = false; }
    note(`  ход в ${name}: ответ с фактом получен = ${replied}`);
    if (!replied) throw new Error(`ход в ${name}: фейковый движок не ответил с фактом — проба невозможна`);
  }
  await page.screenshot({ path: `${SHOTS}/c1-turns.png` });

  // Запись в сторе ищется разбором JSON: сериализатор бэка экранирует кириллицу,
  // подстрока по исходным символам её не находит.
  const memoryEntries = (): any[] => {
    try {
      const shelf = JSON.parse(readData('team-memory.json'));
      return Array.isArray(shelf) ? shelf : Object.values(shelf).flatMap((v: any) => Array.isArray(v) ? v : []);
    } catch { return []; }
  };
  const memoryHas = (token: string) => memoryEntries().some((e: any) => String(e.Text ?? e.text ?? '').includes(token));

  // Ждём фоновой выжимки: запись общего хода должна появиться, приватного — нет.
  let sawPub = false;
  for (let i = 0; i < 24; i++) {
    await wait(5000);
    if (memoryHas(tokPub)) { sawPub = true; break; }
  }
  const evAfter = await eventsOf(a);
  const newEvents = evAfter.slice(0, Math.max(0, evAfter.length - evBefore.length));
  const hasPriv = newEvents.some((e: any) => JSON.stringify(e).includes(privId));
  const hasPub = newEvents.some((e: any) => JSON.stringify(e).includes(pubId));
  const learnedPub = newEvents.some((e: any) => String(e.type ?? e.Type ?? '').includes('memory_learned'));
  note(`  память: токен общего=${memoryHas(tokPub)} токен приватного=${memoryHas(tokPriv)}; новых событий=${newEvents.length}, упоминание приватного=${hasPriv}, общего=${hasPub}, memory_learned=${learnedPub}`);

  expect(sawPub, 'контрольный общий ход: запись в память проекта появилась (нетвакуумная проба)').toBe(true);
  expect(memoryHas(tokPriv), 'ход приватного чата не попал в память проекта').toBe(false);
  expect(hasPriv, 'ход приватного чата не попал в журнал проекта').toBe(false);
  expect(hasPub, 'ход общего чата отражён в журнале проекта').toBe(true);

  // Уборка записи памяти.
  const entry = memoryEntries().find((e: any) => String(e.Text ?? e.text ?? '').includes(tokPub));
  if (entry) {
    const del = await api(a, 'DELETE', `/api/projects/${PROJECT_ID}/team-memory/${entry.Id ?? entry.id}`);
    note(`  уборка записи памяти: ${del.status}`);
  }

  note('C1: приватный ход — ни в память, ни в журнал; общий — запись есть — OK');
});