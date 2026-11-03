// QA-приёмка «Общие проекты»: приватный чат, журнал правок человека, стыки.
// Стенд: ветка feat/shared-projects, порт 5557, юнит ccs-qa-shared.service.
// Учётки: admin (владелец общего проекта), qa_b (участник-редактор).
// Общий проект QA: 433434e7-12d2-4541-8101-ffd9c58bfed4, корень /home/an/qa-shared-projects/shared-proj.
//
// Один спек на группу сценариев (как требует рецепт стенда): логин с повтором при 429
// (лимит входа — 10 за окно), токены кэшируются на весь файл.

import { test, expect, type Page } from '@playwright/test';
import { execFileSync } from 'node:child_process';
import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';

const BASE = process.env.PLAYWRIGHT_BASE_URL || 'http://127.0.0.1:5557';
const PROJECT_ID = '433434e7-12d2-4541-8101-ffd9c58bfed4';
const PROJECT_ROOT = '/home/an/qa-shared-projects/shared-proj';
const DATA_DIR = '/home/an/Sources/ClaudeCodeServer-shared-projects/data-qa';
const SHOTS = '/home/an/Sources/ClaudeCodeServer-shared-projects/.cc-attachments/qa-private';
const PASS = '12345';

const RUN = Date.now().toString(36).slice(-6);
const NAME_A = `QA ${RUN} приватный A`;
const NAME_B = `QA ${RUN} приватный B`;
const NAME_C = `QA ${RUN} общий C`;

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
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ username: user, password: PASS }),
    });
    if (r.status === 200) {
      const j: any = await r.json();
      tokens[user] = j.token;
      return j.token;
    }
    if (r.status !== 429) throw new Error(`login ${user}: ${r.status} ${(await r.text()).slice(0, 200)}`);
    await new Promise(r => setTimeout(r, 12000));
  }
  throw new Error(`login ${user}: лимит входа не прошёл`);
}

async function api(token: string, method: string, path: string, body?: unknown, headers: Record<string, string> = {}) {
  const r = await fetch(`${BASE}${path}`, {
    method,
    headers: { Authorization: `Bearer ${token}`, 'Content-Type': 'application/json', ...headers },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  const txt = await r.text();
  let parsed: any = txt;
  try { parsed = JSON.parse(txt); } catch { /* текст */ }
  return { status: r.status, body: parsed };
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

test.describe.configure({ mode: 'serial' });

test.beforeAll(() => { mkdirSync(SHOTS, { recursive: true }); });
test.setTimeout(180_000);

test('подготовка: флаги, чаты в общем проекте', async () => {
  const admin = await login('admin');
  const b = await login('qa_b');
  for (const t of [admin, b]) {
    const r = await api(t, 'PUT', '/api/feature-flags/shared-projects', { enabled: true });
    expect(r.status, `флаг shared-projects: ${JSON.stringify(r.body)}`).toBe(200);
  }
  const meB = await api(b, 'GET', '/api/auth/me');
  ids.bUser = meB.body.userId;
  const meA = await api(admin, 'GET', '/api/auth/me');
  ids.aUser = meA.body.userId;

  // Персона проекта (нужна для чата) — берём первую проектную, иначе создаём.
  const personas = await api(b, 'GET', `/api/personas?projectId=${PROJECT_ID}`);
  let persona = (Array.isArray(personas.body) ? personas.body : []).find((p: any) => p.projectId === PROJECT_ID);
  if (!persona) {
    const created = await api(b, 'POST', '/api/personas', { name: 'Персона QA', projectId: PROJECT_ID });
    expect(created.status, `персона: ${JSON.stringify(created.body)}`).toBe(200);
    persona = created.body;
  }
  ids.persona = persona.id;

  // Три чата: A — под приватность, B — под живой ход (приватный), C — публичный (контроль).
  const mk = async (name: string, model?: string) => {
    const r = await api(b, 'POST', `/api/projects/${PROJECT_ID}/sessions`, { mode: 'default', name, model });
    expect(r.status, `чат ${name}: ${JSON.stringify(r.body)}`).toBe(201);
    return r.body.id as string;
  };
  ids.chatA = await mk(NAME_A);
  ids.chatB = await mk(NAME_B);
  ids.chatC = await mk(NAME_C);
  note(`подготовка: персона=${ids.persona} чатA=${ids.chatA} чатB=${ids.chatB} чатC=${ids.chatC}`);
});

test('сценарий 1: приватный чат скрыт от владельца (список, поиск, прямой URL → 404)', async () => {
  const admin = await login('admin');
  const b = await login('qa_b');

  const vis = await api(b, 'PUT', `/api/chats/${ids.chatA}/visibility`, { private: true });
  expect(vis.status, `включение приватности: ${JSON.stringify(vis.body)}`).toBe(200);
  expect(vis.body.privateToUserId, 'в ответе — автор приватности').toBe(ids.bUser);

  // Владелец проекта: список чатов проекта — чата нет.
  const listA = await api(admin, 'GET', `/api/projects/${PROJECT_ID}/sessions`);
  const listIds = (Array.isArray(listA.body) ? listA.body : []).map((c: any) => c.id);
  expect(listIds, 'владелец не видит приватный чат в списке').not.toContain(ids.chatA);
  expect(listIds, 'владелец видит общий чат').toContain(ids.chatC);

  // Прямой URL: 404, а не 403.
  const direct = await api(admin, 'GET', `/api/chats/${ids.chatA}`);
  expect(direct.status, `прямой URL /api/chats: ${JSON.stringify(direct.body)}`).toBe(404);
  const directProj = await api(admin, 'GET', `/api/projects/${PROJECT_ID}/sessions/${ids.chatA}`);
  expect(directProj.status, `прямой URL проекта: ${JSON.stringify(directProj.body)}`).toBe(404);

  // Автор видит свой чат.
  const own = await api(b, 'GET', `/api/chats/${ids.chatA}`);
  expect(own.status).toBe(200);
  expect(own.body.privateToUserId).toBe(ids.bUser);

  // Поиск: в поиске чатов владельца приватного чата тоже нет.
  const search = await api(admin, 'GET', `/api/projects/${PROJECT_ID}/sessions?search=${encodeURIComponent('QA приватный A')}`);
  const searchIds = (Array.isArray(search.body) ? search.body : []).map((c: any) => c.id);
  expect(searchIds, 'в поиске владельца приватного чата нет').not.toContain(ids.chatA);

  note(`сценарий 1: приватный чат скрыт от владельца (список, поиск, 404) — OK`);
});

test('сценарий 1 (UI): тумблер «Приватный чат» в шапке', async ({ page, browser }) => {
  const b = await login('qa_b');
  await uiLogin(page, 'qa_b');
  await page.goto(`${BASE}/#/project/${PROJECT_ID}/chat/${ids.chatC}`);
  await page.reload();   // диплинк на чат читается только при полной загрузке
  await page.waitForTimeout(2500);

  const lockBtn = page.locator('button[title="Сделать этот чат видимым только вам"]');
  await expect(lockBtn, 'тумблер приватности в шапке').toBeVisible({ timeout: 20000 });
  await lockBtn.click();
  const sw = page.locator('[role="switch"][aria-label="Приватный чат — видите только вы"]');
  await expect(sw, 'тумблер в поповере').toBeVisible({ timeout: 10000 });
  await page.screenshot({ path: `${SHOTS}/01-toggle-popover.png`, fullPage: false });
  await sw.click();
  await expect(sw).toHaveAttribute('aria-checked', 'true', { timeout: 15000 });
  await page.screenshot({ path: `${SHOTS}/02-private-on.png`, fullPage: false });

  // Сервер: чат стал приватным.
  const b2 = await login('qa_b');
  const st = await api(b2, 'GET', `/api/chats/${ids.chatC}`);
  expect(st.body.privateToUserId, 'чат C приватен после клика по тумблеру').toBe(ids.bUser);

  // Владелец в отдельной вкладке не видит чата.
  const admin = await login('admin');
  const ctx2 = await browser.newContext();
  const page2 = await ctx2.newPage();
  await page2.goto(`${BASE}/#/home`);
  await page2.evaluate(async (t) => {
    sessionStorage.setItem('cc_token', t);
    const me = await fetch('/api/auth/me', { headers: { Authorization: `Bearer ${t}` } });
    const m: any = await me.json();
    sessionStorage.setItem('cc_user_id', m.userId || '');
    if (m.role) sessionStorage.setItem('cc_role', m.role);
  }, admin);
  await page2.reload();
  await page2.waitForTimeout(1200);
  await page2.goto(`${BASE}/#/project/${PROJECT_ID}`);
  await page2.waitForTimeout(1500);
  const bodyText = (await page2.locator('body').innerText()).replace(/\s+/g, ' ');
  expect(bodyText, 'в списке чатов владельца нет приватного чата').not.toContain(NAME_C);
  await page2.screenshot({ path: `${SHOTS}/03-owner-list.png`, fullPage: false });

  // Возвращаем чат в общий режим (нужно дальнейшим сценариям).
  const back = await api(b2, 'PUT', `/api/chats/${ids.chatC}/visibility`, { private: false });
  expect(back.status, `возврат в общий: ${JSON.stringify(back.body)}`).toBe(200);
  expect(back.body.privateToUserId ?? null).toBeNull();
  note(`сценарий 1 (UI): тумблер работает, владелец чата не видит — OK`);
});

test('сценарий 2: запреты приватного чата (штаб, группа, фича, досье)', async () => {
  const b = await login('qa_b');
  const admin = await login('admin');

  // Делаем чат B приватным.
  const vis = await api(b, 'PUT', `/api/chats/${ids.chatB}/visibility`, { private: true });
  expect(vis.status, `приватность чата B: ${JSON.stringify(vis.body)}`).toBe(200);

  // Штаб («Командная реализация») и состав чата — режимы, которые настраивает ВЛАДЕЛЕЦ
  // проекта: у участника-редактора чата эта дверь закрыта раньше (403 owner_settings),
  // поэтому проверяем на чате владельца, сделанном приватным.
  const ownerChat = await api(admin, 'POST', `/api/projects/${PROJECT_ID}/sessions`, { mode: 'default', name: `QA ${RUN} штаб` });
  expect(ownerChat.status, `чат владельца: ${JSON.stringify(ownerChat.body)}`).toBe(201);
  const ownerPriv = await api(admin, 'PUT', `/api/chats/${ownerChat.body.id}/visibility`, { private: true });
  expect(ownerPriv.status, `приватность чата владельца: ${JSON.stringify(ownerPriv.body)}`).toBe(200);

  const team = await api(admin, 'PUT', `/api/chats/${ownerChat.body.id}/team-implement`, { enabled: true });
  expect(team.status, `штаб: ${JSON.stringify(team.body)}`).toBe(409);
  expect(team.body.code).toBe('private_chat_mode');
  expect(String(team.body.error)).toMatch(/приватн/i);

  // Групповой режим — состав чата. Ручка смотрит «владельца чата» (GetOwnedVisible), а чат
  // участника в общем проекте владельцем не имеет (ownerId пуст) — та же дверь проверяем
  // на чате владельца проекта, сделанном приватным.
  const parts = await api(admin, 'PUT', `/api/chats/${ownerChat.body.id}/participants`, { personaIds: [ids.persona] });
  expect(parts.status, `состав: ${JSON.stringify(parts.body)}`).toBe(409);
  expect(parts.body.code).toBe('private_chat_mode');

  // Обратное направление: уже групповой чат нельзя сделать приватным (409 private_chat_mode).
  // Групповой чат создаёт владелец проекта — автором группы становится он.
  const ap1 = await api(admin, 'POST', '/api/personas', { name: `Персона владельца 1 ${RUN}`, projectId: PROJECT_ID });
  const ap2 = await api(admin, 'POST', '/api/personas', { name: `Персона владельца 2 ${RUN}`, projectId: PROJECT_ID });
  expect(ap1.status, `персона владельца 1: ${JSON.stringify(ap1.body)}`).toBe(200);
  expect(ap2.status, `персона владельца 2: ${JSON.stringify(ap2.body)}`).toBe(200);
  const groupChat = await api(admin, 'POST', '/api/chats/group', { personaIds: [ap1.body.id, ap2.body.id], name: `QA ${RUN} группа` });
  expect(groupChat.status, `групповой чат: ${JSON.stringify(groupChat.body)}`).toBe(200);
  const groupPriv = await api(admin, 'PUT', `/api/chats/${groupChat.body.id}/visibility`, { private: true });
  // Наблюдение: у группового чата нет автора, поэтому гейт «автор чата» отдаёт 403 раньше
  // двери «приватный чат» (409 private_chat_mode) — см. дефект D3 в отчёте.
  expect(groupPriv.status, `приватность группового чата: ${JSON.stringify(groupPriv.body)}`).toBe(403);
  expect(groupPriv.body.code).toBe('not_chat_author');
  note(`  обратное направление (групповой → приватный): ${groupPriv.status} ${groupPriv.body.code}`);

  // Привязка к фиче: заголовок вызывающего чата = приватный чат.
  const task = await api(admin, 'POST', `/api/projects/${PROJECT_ID}/tasks`, { title: `QA фича-привязка ${RUN}`, role: 'feature' },
    { 'X-Caller-Session-Id': ownerChat.body.id });
  expect(task.status, `задача-фича: ${JSON.stringify(task.body)}`).toBe(409);
  expect(task.body.code).toBe('private_chat_mode');
  expect(String(task.body.error)).toMatch(/приватн/i);

  // Досье: «Не сохранять решения» нельзя выключить, пока чат приватный.
  const dossier = await api(admin, 'PUT', `/api/projects/${PROJECT_ID}/sessions/${ownerChat.body.id}`, { excludeFromDossiers: false });
  expect(dossier.status, `досье: ${JSON.stringify(dossier.body)}`).toBe(409);
  expect(dossier.body.code).toBe('private_chat_dossiers');
  expect(String(dossier.body.error)).toMatch(/приватн/i);

  // Тот же вызов на собственном приватном чате УЧАСТНИКА: доходит гейт «автор чата»
  // (ownerId у такого чата пуст) — текста про приватность человек не увидит (дефект D4).
  const dossierB = await api(b, 'PUT', `/api/projects/${PROJECT_ID}/sessions/${ids.chatB}`, { excludeFromDossiers: false });
  note(`  досье на чате участника: ${dossierB.status} ${dossierB.body.code}`);

  note(`сценарий 2: запреты отдают 409 с кодами private_chat_mode / private_chat_dossiers — OK`);
});

test('сценарий 1 (вложения): перенос вложений при смене видимости', async () => {
  const b = await login('qa_b');
  const admin = await login('admin');

  // Загрузка вложения идёт через гейт «владелец чата»: у чата УЧАСТНИКА общего проекта
  // ownerId пуст, поэтому загрузка отдаёт 404 — см. дефект D5 в отчёте.
  const formB = new FormData();
  formB.append('file', new Blob(['QA вложение участника'], { type: 'text/plain' }), 'qa-attachment-b.txt');
  const upB = await fetch(`${BASE}/api/chats/${ids.chatA}/files/upload`, {
    method: 'POST', headers: { Authorization: `Bearer ${b}` }, body: formB,
  });
  note(`  загрузка вложения участником в свой чат: ${upB.status}`);
  expect(upB.status, `загрузка вложения участником: ${upB.status}`).toBe(404);

  // Основной сценарий переноса — на чате владельца проекта.
  const ownerChat = await api(admin, 'POST', `/api/projects/${PROJECT_ID}/sessions`, { mode: 'default', name: `QA ${RUN} вложения` });
  expect(ownerChat.status).toBe(201);
  const chatId = ownerChat.body.id as string;

  const form = new FormData();
  form.append('file', new Blob(['QA вложение'], { type: 'text/plain' }), 'qa-attachment.txt');
  const up = await fetch(`${BASE}/api/chats/${chatId}/files/upload`, {
    method: 'POST', headers: { Authorization: `Bearer ${admin}` }, body: form,
  });
  const upBody = await up.json().catch(() => ({}));
  expect(up.status, `загрузка вложения: ${JSON.stringify(upBody)}`).toBe(200);
  const sharedPath0 = upBody.path as string;

  // Делаем приватным — ждём карту переноса.
  const toPriv = await api(admin, 'PUT', `/api/chats/${chatId}/visibility`, { private: true });
  expect(toPriv.status, `приватность: ${JSON.stringify(toPriv.body)}`).toBe(200);
  const moves2 = toPriv.body.attachmentMoves as Record<string, string> | undefined;
  expect(moves2 && Object.keys(moves2).length, `карта переноса: ${JSON.stringify(moves2)}`).toBeGreaterThan(0);
  const privPath = Object.values(moves2!)[0];
  expect(privPath, 'путь изменился').not.toBe(sharedPath0);

  // Файл читается по приватному пути (владельцем).
  const readPriv = await api(admin, 'GET', `/api/projects/${PROJECT_ID}/files/content?path=${encodeURIComponent(privPath)}`);
  expect(readPriv.status, `чтение по приватному пути: ${JSON.stringify(readPriv.body)}`).toBe(200);
  expect(String(readPriv.body.content ?? readPriv.body)).toContain('QA вложение');

  // Ссылка на вложение у участника проекта закрыта (чужое приватное).
  const foreign = await api(b, 'GET', `/api/projects/${PROJECT_ID}/files/content?path=${encodeURIComponent(privPath)}`);
  expect([403, 404], `чужое приватное вложение: ${foreign.status}`).toContain(foreign.status);

  // Возвращаем в общий — карта переноса обратно.
  const toShared = await api(admin, 'PUT', `/api/chats/${chatId}/visibility`, { private: false });
  expect(toShared.status, `снятие приватности: ${JSON.stringify(toShared.body)}`).toBe(200);
  const moves1 = toShared.body.attachmentMoves as Record<string, string> | undefined;
  expect(moves1 && Object.keys(moves1).length, `карта переноса назад: ${JSON.stringify(moves1)}`).toBeGreaterThan(0);
  expect(Object.values(moves1!)[0]).toBe(sharedPath0);
  const readShared = await api(admin, 'GET', `/api/projects/${PROJECT_ID}/files/content?path=${encodeURIComponent(sharedPath0)}`);
  expect(readShared.status, `чтение по общему пути: ${JSON.stringify(readShared.body)}`).toBe(200);
  expect(String(readShared.body.content ?? readShared.body)).toContain('QA вложение');

  note(`сценарий 1 (вложения): карта переноса есть в обе стороны, файл читается, чужие пути закрыты — OK`);
});

test('сценарий 6: регрессия — обычный общий чат и одиночный проект', async () => {
  const b = await login('qa_b');
  const admin = await login('admin');

  // Обычный общий чат виден владельцу.
  const listA = await api(admin, 'GET', `/api/projects/${PROJECT_ID}/sessions`);
  const idsA = (Array.isArray(listA.body) ? listA.body : []).map((c: any) => c.id);
  expect(idsA, 'общий чат виден владельцу').toContain(ids.chatC);

  // Одиночный (личный) проект: приватность неприменима.
  const projects = await api(b, 'GET', '/api/projects');
  const own = (Array.isArray(projects.body) ? projects.body : []).find((p: any) => p.members === undefined || p.members === null);
  if (!own) {
    note('сценарий 6: одиночный проект у участника не найден — проверка пропущена');
    return;
  }
  const ch = await api(b, 'POST', `/api/projects/${own.id}/sessions`, { mode: 'default', name: 'QA личный чат' });
  expect(ch.status, `чат одиночного проекта: ${JSON.stringify(ch.body)}`).toBe(201);
  const priv = await api(b, 'PUT', `/api/chats/${ch.body.id}/visibility`, { private: true });
  expect(priv.status, `приватность в одиночном проекте: ${JSON.stringify(priv.body)}`).toBe(409);
  expect(priv.body.code).toBe('not_shared_project');
  note('сценарий 6: обычный общий чат виден владельцу, одиночный проект отказывает (not_shared_project) — OK');
});


test('сценарий 3: ход приватного чата не попадает в журнал проекта и авто-память', async ({ page }) => {
  test.setTimeout(480_000);
  const b = await login('qa_b');
  const admin = await login('admin');

  // Два чата на фейковом движке (qa-fake-1): приватный и общий (контроль).
  const priv = await api(b, 'POST', `/api/projects/${PROJECT_ID}/sessions`, { mode: 'default', name: `QA ${RUN} ход приватный`, model: 'claude-sonnet-5-5' });
  const pub = await api(b, 'POST', `/api/projects/${PROJECT_ID}/sessions`, { mode: 'default', name: `QA ${RUN} ход общий`, model: 'claude-sonnet-5-5' });
  expect(priv.status, `приватный чат для хода: ${JSON.stringify(priv.body)}`).toBe(201);
  expect(pub.status, `общий чат для хода: ${JSON.stringify(pub.body)}`).toBe(201);
  const privId = priv.body.id as string;
  const pubId = pub.body.id as string;
  await api(b, 'PUT', `/api/chats/${privId}/visibility`, { private: true });

  const eventsOf = async () => {
    const r = await api(admin, 'GET', `/api/projects/${PROJECT_ID}/events?limit=200`);
    return Array.isArray(r.body) ? r.body : (r.body.events ?? []);
  };
  const memSnapshot = () => readData('persona-memory.json') + '|' + readData('team-memory.json');
  const evBefore = await eventsOf();
  const memBefore = memSnapshot();

  // Живой ход в ПРИВАТНОМ чате: сообщение из композера, ответ — от фейкового движка.
  // Чат открываем КЛИКОМ по карточке: диплинк #/project/{id}/chat/{chatId} не открывает нужный
  // чат (открывается последний активный) — см. дефект D2 в отчёте.
  await uiLogin(page, 'qa_b');
  await page.goto(`${BASE}/#/project/${PROJECT_ID}`);
  await page.reload();
  await page.waitForTimeout(2500);
  const privName = priv.body.name as string;
  const pubName = pub.body.name as string;
  await page.getByText(privName, { exact: false }).first().click();
  await page.waitForTimeout(2500);
  expect(await page.evaluate(() => location.hash), 'открыт приватный чат').toContain(privId);
  await page.screenshot({ path: `${SHOTS}/04-private-chat.png`, fullPage: false });

  const composer = page.locator('textarea.cc-composer-input');
  await composer.waitFor({ state: 'visible', timeout: 20000 });
  await composer.fill('Проверка приватности: секретный факт QA');
  await composer.press('Enter');
  await page.waitForTimeout(3000);
  await page.screenshot({ path: `${SHOTS}/04-private-turn.png`, fullPage: false });
  let replied = false;
  try {
    await page.getByText(/QA-фейк ответил/).first().waitFor({ timeout: 120000 });
    replied = true;
  } catch { replied = false; }
  note(`  ход в приватном чате: ответ движка получен = ${replied}`);

  // Живой ход в ОБЩЕМ чате — контроль: журнал проекта должен увидеть ход.
  await page.getByText(pubName, { exact: false }).first().click();
  await page.waitForTimeout(2500);
  expect(await page.evaluate(() => location.hash), 'открыт общий чат').toContain(pubId);
  const composer2 = page.locator('textarea.cc-composer-input');
  await composer2.waitFor({ state: 'visible', timeout: 20000 });
  await composer2.fill('Проверка: обычный ход в общем чате');
  await composer2.press('Enter');
  let replied2 = false;
  try {
    await page.getByText(/QA-фейк ответил/).first().waitFor({ timeout: 120000 });
    replied2 = true;
  } catch { replied2 = false; }
  note(`  ход в общем чате: ответ движка получен = ${replied2}`);

  const evAfter = await eventsOf();
  const newEvents = evAfter.slice(0, Math.max(0, evAfter.length - evBefore.length));
  const hasPriv = newEvents.some((e: any) => JSON.stringify(e).includes(privId));
  const hasPub = newEvents.some((e: any) => JSON.stringify(e).includes(pubId));
  note(`  новых событий журнала проекта: ${newEvents.length}; упоминание приватного чата: ${hasPriv}; упоминание общего: ${hasPub}`);
  expect(hasPriv, 'ход приватного чата не попадает в журнал проекта').toBe(false);
  if (replied2) expect(hasPub, 'ход общего чата попадает в журнал проекта (контроль)').toBe(true);

  // Авто-память: содержимое приватного чата в памяти не появилось.
  const memAfter = memSnapshot();
  expect(memAfter, 'память не изменилась после хода в приватном чате').toBe(memBefore);
  note('сценарий 3: ход приватного чата не попал в журнал проекта и не изменил авто-память');
});

test('сценарий 5: мобильная раскладка 360 px (тумблер и NewChatSetup)', async ({ browser }) => {
  const b = await login('qa_b');
  const ctx = await browser.newContext({ viewport: { width: 360, height: 780 }, isMobile: false });
  const page = await ctx.newPage();
  const token = b;
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

  // Пустой чат в общем проекте — на нём видна NewChatSetup (пилюли настроек).
  const empty = await api(b, 'POST', `/api/projects/${PROJECT_ID}/sessions`, { mode: 'default', name: `QA ${RUN} мобильный` });
  expect(empty.status, `мобильный чат: ${JSON.stringify(empty.body)}`).toBe(201);
  // Открываем чат КЛИКОМ по карточке: диплинк открывает последний активный чат (дефект D2),
  // а в нём висит карточка разрешения от CLI, которая перекрывает шапку.
  await page.goto(`${BASE}/#/project/${PROJECT_ID}`);
  await page.reload();
  await page.waitForTimeout(2500);
  await page.getByText(empty.body.name as string, { exact: false }).first().click();
  await page.waitForTimeout(2500);
  expect(await page.evaluate(() => location.hash), 'открыт нужный чат').toContain(empty.body.id as string);
  await page.screenshot({ path: `${SHOTS}/05-mobile-newchatsetup.png`, fullPage: false });
  const setup = page.getByText('История решений', { exact: false }).first();
  await expect(setup, 'NewChatSetup на 360 px').toBeVisible({ timeout: 20000 });

  // Тумблер приватности в шапке на 360 px.
  // Карточка разрешения от CLI («AI может помочь…») перекрывает шапку — гасим её.
  const later = page.getByRole('button', { name: /Позже/ });
  if (await later.isVisible().catch(() => false)) { await later.click(); await page.waitForTimeout(800); }

  const lock = page.locator('button[title="Сделать этот чат видимым только вам"]');
  await expect(lock, 'тумблер на 360 px').toBeVisible({ timeout: 20000 });
  const sw = page.locator('[role="switch"][aria-label="Приватный чат — видите только вы"]');
  await lock.click();
  await page.waitForTimeout(2000);
  await page.screenshot({ path: `${SHOTS}/06-mobile-toggle.png`, fullPage: false });
  const swCount = await sw.count();
  expect(swCount, 'поповер тумблера открылся на 360 px').toBeGreaterThan(0);
  const swBox = await sw.first().boundingBox();
  note(`  тумблер в поповере: ${JSON.stringify(swBox)}`);
  expect(swBox!.x, 'тумблер не вылезает за левый край').toBeGreaterThanOrEqual(0);
  expect(swBox!.x + swBox!.width, 'тумблер не вылезает за правый край 360 px').toBeLessThanOrEqual(361);

  // Горизонтального переполнения быть не должно.
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
  note(`  горизонтальное переполнение на 360 px: ${overflow}px`);
  expect(overflow, 'нет горизонтального переполнения на 360 px').toBeLessThanOrEqual(1);
  await ctx.close();
  note('сценарий 5: мобильная раскладка 360 px — тумблер и NewChatSetup на месте');
});

test('сценарий 4: журнал правок человека в заметках общего проекта', async () => {
  const b = await login('qa_b');
  const before = gitLog().length;

  const wait = async () => { await new Promise(r => setTimeout(r, 4000)); };
  const step = async (label: string) => {
    const n0 = gitLog().length;
    await wait();
    const log = gitLog();
    const added = log.length - n0;
    note(`  ${label}: новых коммитов = ${added}${added ? ' → ' + log.slice(0, added).join(' | ') : ''}`);
    return { added, log };
  };

  // Контроль: журнал вообще жив — правка обычного файла проекта даёт коммит с автором-человеком.
  const ctrlFile = `qa-journal-control-${RUN}.txt`;
  const ctrl = await api(b, 'PUT', `/api/projects/${PROJECT_ID}/files/content?path=${encodeURIComponent(ctrlFile)}`, { content: 'контроль' });
  expect(ctrl.status, `контрольная запись файла: ${JSON.stringify(ctrl.body)}`).toBe(200);
  const s0 = await step('КОНТРОЛЬ: правка обычного файла');
  expect(s0.added, 'журнал пишет коммит по обычному файлу').toBe(1);
  expect(s0.log[0], 'автор коммита — человек').toContain('qa_b');

  // 1. Создание заметки общего проекта.
  const created = await api(b, 'POST', '/api/notes', {
    title: `Журнал QA ${RUN}`, content: 'тело\n- [ ] задача QA', source: PROJECT_ID });
  expect(created.status, `создание заметки: ${JSON.stringify(created.body)}`).toBe(200);
  const noteId = created.body.id as string;
  const notePath = created.body.path as string;   // путь заметки внутри notes/ проекта
  const s1 = await step('1. создание заметки');
  expect.soft(s1.added, 'создание заметки пишет коммит в журнал').toBe(1);

  // 2. Аннотация (комментарий к документу проекта) — до переноса, пока путь заметки известен.
  const ann = await api(b, 'POST', '/api/notes/annotate', {
    doc: { scope: PROJECT_ID, path: `notes/${notePath}` },
    selection: { start: 0, end: 4, text: 'тело' },
    comment: `аннотация QA ${RUN}`,
  });
  expect(ann.status, `аннотация: ${JSON.stringify(ann.body)}`).toBe(200);
  const s2 = await step('2. аннотация');
  expect.soft(s2.added, 'аннотация пишет коммит').toBe(1);

  // 3. Правка.
  const upd = await api(b, 'PUT', `/api/notes/${noteId}`, { content: 'тело 2\n- [ ] задача QA' });
  expect(upd.status, `правка заметки: ${JSON.stringify(upd.body)}`).toBe(200);
  const s3 = await step('3. правка заметки');
  expect.soft(s3.added, 'правка заметки пишет коммит').toBe(1);

  // 4. Перенос в папку.
  const mv = await api(b, 'POST', `/api/notes/${noteId}/move`, { folder: 'Подпапка' });
  expect(mv.status, `перенос заметки: ${JSON.stringify(mv.body)}`).toBe(200);
  // перенос меняет id заметки (новый путь) — дальше работаем с ним
  const noteId2 = mv.body.id as string;
  const s4 = await step('4. перенос заметки');
  expect.soft(s4.added, 'перенос заметки пишет коммит').toBe(1);

  // 5. Чекбокс задачи в заметке. Синхронизация с задачами проекта доступна
  // владельцу проекта (у участника-редактора права Write нет — 403 project_scope).
  const admin0 = await login('admin');
  const toggle = await api(admin0, 'POST', `/api/notes/${noteId2}/tasks/toggle`, { line: 1, done: true });
  expect(toggle.status, `чекбокс задачи: ${JSON.stringify(toggle.body)}`).toBe(200);
  const s5 = await step('5. чекбокс задачи');
  expect.soft(s5.added, 'чекбокс задачи пишет коммит').toBe(1);

  // 6. Срок задачи в заметке.
  const due = await api(admin0, 'POST', `/api/notes/${noteId2}/tasks/set-due`, { line: 1, due: '2026-12-01' });
  expect(due.status, `срок задачи: ${JSON.stringify(due.body)}`).toBe(200);
  const s6 = await step('6. срок задачи');
  expect.soft(s6.added, 'срок задачи пишет коммит').toBe(1);

  // 7. Удаление.
  const del = await api(b, 'DELETE', `/api/notes/${noteId2}`);
  expect(del.status, `удаление заметки: ${JSON.stringify(del.body)}`).toBe(204);
  const s7 = await step('7. удаление заметки');
  expect.soft(s7.added, 'удаление заметки пишет коммит').toBe(1);

  // Личный vault: правка человека в личном vault в журнал проекта не пишет.
  const personalBefore = gitLog().length;
  const pc = await api(b, 'POST', '/api/notes', { title: `Личная заметка QA ${RUN}`, content: 'тело', source: 'personal' });
  expect(pc.status, `личная заметка: ${JSON.stringify(pc.body)}`).toBe(200);
  const pu = await api(b, 'PUT', `/api/notes/${pc.body.id}`, { content: 'тело 2' });
  expect(pu.status, `правка личной заметки: ${JSON.stringify(pu.body)}`).toBe(200);
  await wait();
  const personalAfter = gitLog().length;
  expect(personalAfter - personalBefore, 'личный vault не пишет в журнал проекта').toBe(0);

  // Причина: .gitignore проекта (его пишет сам продукт) исключает /notes/.
  const gi = existsSync(`${PROJECT_ROOT}/.gitignore`) ? readFileSync(`${PROJECT_ROOT}/.gitignore`, 'utf8') : '';
  note(`  .gitignore проекта исключает notes/: ${gi.split('\n').filter(l => l.includes('notes')).join(' | ')}`);
  note(`сценарий 4: всего коммитов за сценарий ${gitLog().length - before} (контрольный — 1)`);
});
