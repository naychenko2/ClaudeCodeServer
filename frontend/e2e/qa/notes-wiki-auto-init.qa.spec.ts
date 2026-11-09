// QA-LI · вики создаётся сама — проверка на уровне REST (волна LI-1, LI-2, регрессы).
//
// Проверяем контракт волны «вики заводится сама»:
//   LI-1  ночная служба заводит вики сама (владелец с включённой ночью и без вики);
//   LI-2  человеческие точки (перенос заметки, разбор чата, правка/новая страница,
//         включение ночного разбора) заводят вику — прежнего 409 wiki_not_initialized нет;
//   регресс: чтение (GET дерева и настроек) и PUT только классов вику не заводят;
//            сервисный токен (typ=svc) и MCP не заводят;
//            уже заведённая вика с правкой шаблона не затирается.
//
// Каждому сценарию — свой владелец: вики заводится один раз на всю жизнь, поэтому
// «незаведённая вики» у одного и того же владельца получить нельзя. Учётки создаёт
// админ через POST /api/users; свой пароль у каждой (≥8 символов). Мусор убирается
// в afterAll: учётки удаляются, каталоги вики сносятся с диска.
//
// Рецепт стенда — docs/operations/qa-stand.md.

import { test, expect, type APIRequestContext } from '@playwright/test';
import { createHmac } from 'node:crypto';
import { existsSync, mkdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { api, login, type Api } from './helpers';

const BASE = process.env.PLAYWRIGHT_BASE_URL || 'http://127.0.0.1:5500';
const USER_PASS = 'qa-lI-pass-2026';

test.describe.configure({ mode: 'serial' });

// ─── Хелперы владельцев ───

interface Owner {
  name: string;
  id: string;
  token: string;
  client: Api;
}

let request: APIRequestContext;
let admin: Api;
const owners: Owner[] = [];

function repoRoot(): string {
  return path.resolve(process.cwd(), '../../..');
}

// Каталог данных стенда — из его appsettings.Local.json (DataPath указывает на ФАЙЛ
// projects.json; его каталог и есть корень data).
function dataDir(): string {
  const cfgPath = path.join(repoRoot(), 'backend/ClaudeHomeServer/appsettings.Local.json');
  expect(existsSync(cfgPath), 'у стенда должен быть appsettings.Local.json с DataPath').toBeTruthy();
  const cfg = JSON.parse(readFileSync(cfgPath, 'utf8')) as { DataPath?: string };
  expect(cfg.DataPath, 'в appsettings.Local.json стенда должен быть DataPath').toBeTruthy();
  return path.dirname(path.resolve(cfg.DataPath!));
}

function wikiRoot(ownerId: string): string {
  return path.join(dataDir(), 'wiki', ownerId);
}

// Секрет JWT стенда — из его каталога данных: им подписывается сервисный токен (typ=svc).
function jwtSecret(): string {
  const p = path.join(dataDir(), 'jwt-secret.txt');
  expect(existsSync(p), 'у стенда должен быть jwt-secret.txt').toBeTruthy();
  return readFileSync(p, 'utf8').trim();
}

function b64url(data: string | Buffer): string {
  return Buffer.from(data as never).toString('base64url');
}

// Сервисный токен хода (claim typ=svc): сервер сам себе выдаёт такие для MCP.
// Подписываем тем же секретом, что и сервер (HS256, iss/aud ClaudeHomeServer).
function serviceToken(ownerId: string): string {
  const now = Math.floor(Date.now() / 1000);
  const header = b64url(JSON.stringify({ alg: 'HS256', typ: 'JWT' }));
  const payload = b64url(JSON.stringify({
    sub: ownerId, name: 'mcp-tasks', role: 'user', typ: 'svc',
    iss: 'ClaudeHomeServer', aud: 'ClaudeHomeServer',
    iat: now, nbf: now - 10, exp: now + 3600,
  }));
  const sig = createHmac('sha256', jwtSecret()).update(`${header}.${payload}`).digest('base64url');
  return `${header}.${payload}.${sig}`;
}

// Новый владелец: учётка, вход, оба флага вики (llm-wiki и «Заметки и Вики»).
async function makeOwner(baseName: string): Promise<Owner> {
  // Имя уникально на прогон: учётка переживает прогон, а вики заводится навсегда.
  const name = `${baseName}_${Date.now().toString(36)}`.slice(0, 32);
  const created = await admin.post<{ id: string; username: string }>('/api/users', {
    username: name, password: USER_PASS, role: 'user',
  });
  expect(created?.id, `POST /api/users должен был создать ${name}`).toBeTruthy();
  const token = await loginAs(name);
  const client = api(request, token);
  // Учётку регистрируем ДО настройки флагов: упал флаг — учётку всё равно надо убрать.
  const owner: Owner = { name, id: created.id, token, client };
  owners.push(owner);
  await client.put('/api/feature-flags/llm-wiki', { enabled: true });
  await client.put('/api/feature-flags/notes-wiki-unified', { enabled: true });
  return owner;
}

// Логин ТОЛЬКО что созданного владельца: свой пароль, свой токен.
async function loginAs(name: string): Promise<string> {
  const r = await request.post('/api/auth/login', { data: { username: name, password: USER_PASS } });
  expect(r.ok(), `логин ${name} должен пройти (${r.status()})`).toBeTruthy();
  const body = await r.json() as { token: string };
  return body.token;
}

// Заведена ли вики: признак — pages/_schema.md (ADR-035, решение 1).
function wikiInitialized(ownerId: string): boolean {
  return existsSync(path.join(wikiRoot(ownerId), 'pages', '_schema.md'));
}

// ─── Жизненный цикл ───

test.beforeAll(async ({ playwright }) => {
  request = await playwright.request.newContext({ baseURL: BASE });
  const adminToken = await login(request);
  admin = api(request, adminToken);
});

test.afterAll(async () => {
  for (const o of owners) {
    try { await admin.del(`/api/users/${o.id}`); } catch { /* best-effort */ }
    rmSync(wikiRoot(o.id), { recursive: true, force: true });
  }
  await request?.dispose();
});

// ─── 1. Регресс: чтение и PUT только классов вику не заводят ───

test('LI регресс: GET дерева и настроек, PUT только классов — вики не заводят', async () => {
  const o = await makeOwner('qa_li_read');

  // GET дерева: читающий — ничего не создаёт
  const tree = await o.client.get<{ initialized: boolean }>('/api/wiki/tree');
  expect(tree.initialized, 'до всяких действий вики не заведена').toBeFalsy();
  expect(wikiInitialized(o.id), 'GET дерева не создало pages/_schema.md').toBeFalsy();

  // GET настроек: тоже читающий
  const settings = await o.client.get<{ nightly: { enabled: boolean } }>('/api/wiki/settings');
  expect(settings?.nightly).toBeTruthy();
  expect(wikiInitialized(o.id), 'GET настроек не создало pages/_schema.md').toBeFalsy();

  // PUT только автоприменения (ночь выключена) — вику не заводит
  await o.client.put('/api/wiki/settings', { autoApplyEnabled: true });
  expect(wikiInitialized(o.id), 'PUT только автоприменения не должен заводить вики').toBeFalsy();

  // PUT только класса (выключение класса) — тоже не заводит
  await o.client.put('/api/wiki/settings', { class: 'note×create', enable: false });
  expect(wikiInitialized(o.id), 'PUT только класса не должен заводить вики').toBeFalsy();

  const tree2 = await o.client.get<{ initialized: boolean }>('/api/wiki/tree');
  expect(tree2.initialized, 'вики всё ещё не заведена').toBeFalsy();
});

// ─── 2. Регресс: сервисный токен не заводит вики ───

test('LI регресс: сервисный токен (typ=svc) — 403 на человеческих ручках, вики не заведена', async () => {
  const o = await makeOwner('qa_li_svc');
  const svc = serviceToken(o.id);

  // Включение ночного разбора сервисным токеном — 403, вики не заведена
  const put = await request.put('/api/wiki/settings', {
    headers: { Authorization: `Bearer ${svc}` },
    data: { nightly: { enabled: true } },
  });
  expect(put.status(), 'сервисный токен не должен управлять настройками вики').toBe(403);
  expect(wikiInitialized(o.id), 'сервисный токен не должен заводить вики').toBeFalsy();

  // Разбор чата сервисным токеном — 403
  const analyze = await request.post('/api/wiki/chats/does-not-matter/analyze', {
    headers: { Authorization: `Bearer ${svc}` },
  });
  expect(analyze.status(), 'сервисный токен не должен запускать разбор').toBe(403);
  expect(wikiInitialized(o.id), 'сервисный токен не должен заводить вики').toBeFalsy();

  // Перенос заметки сервисным токеном — 403
  const note = await o.client.post<{ id: string }>('/api/notes', {
    title: 'Заметка для svc-проверки', source: 'personal', content: 'Текст заметки для svc-проверки.',
  });
  const transfer = await request.post('/api/wiki/ingest/note', {
    headers: { Authorization: `Bearer ${svc}` },
    data: { noteId: note.id, transfer: true },
  });
  expect(transfer.status(), 'сервисный токен не должен переносить заметку в вики').toBe(403);
  expect(wikiInitialized(o.id), 'сервисный токен не должен заводить вики').toBeFalsy();
  await o.client.del(`/api/notes/${note.id}`);
});

// ─── 3. Регресс: MCP не заводит вики ───

test('LI регресс: MCP-инструмент wiki_ingest не заводит вики', async () => {
  const o = await makeOwner('qa_li_mcp');

  // Свой чат: MCP-вызов идёт от его имени (хвост маршрута — id чата).
  const root = path.join(tmpdir(), `qa_li_mcp_${Date.now()}`);
  mkdirSync(root, { recursive: true });
  const project = await o.client.post<{ id: string }>('/api/projects', {
    name: 'QA-LI-MCP', rootPath: root, createDirectory: true,
  });
  const session = await o.client.post<{ id: string }>(`/api/projects/${project.id}/sessions`, { name: 'Чат для MCP' });
  const note = await o.client.post<{ id: string }>('/api/notes', {
    title: 'Заметка для MCP-проверки', source: 'personal', content: 'Текст заметки для MCP-проверки.',
  });

  const svc = serviceToken(o.id);
  const res = await request.post(`/mcp/notes/${session.id}`, {
    // Заголовок вызывающей сессии шлёт и живой клиент (ClaudeSession, конфиг сервера notes).
    // Без него рамка хода считается Narrow (fail-closed) и отказ приходит РАНЬШЕ проверки
    // вики — «Недоступно в общем проекте» вместо «вики не заведена».
    headers: { Authorization: `Bearer ${svc}`, 'X-Caller-Session-Id': session.id },
    data: {
      jsonrpc: '2.0', id: 1, method: 'tools/call',
      params: { name: 'wiki_ingest', arguments: { source: `note:${note.id}` } },
    },
  });
  const body = await res.text();
  expect(res.ok(), `MCP-вызов должен был дойти до сервера: ${res.status()} ${body}`).toBeTruthy();
  // Текст приезжает JSON-экранированным (\uXXXX) — разбираем JSON, а не ищем подстроку в сыром теле.
  const rpc = JSON.parse(body) as { result?: { content?: { text?: string }[] } };
  expect(rpc.result?.content?.[0]?.text ?? '', 'MCP-вызов wiki_ingest обязан быть отказом: вики не заведена')
    .toContain('не заведена');
  expect(wikiInitialized(o.id), 'MCP-инструмент не должен заводить вики').toBeFalsy();

  await o.client.del(`/api/notes/${note.id}`);
  await o.client.del(`/api/projects/${project.id}/sessions/${session.id}`);
  await o.client.del(`/api/projects/${project.id}`);
  rmSync(root, { recursive: true, force: true });
});

// ─── 4. LI-2: «Перенести в вики» заводит вику ───

test('LI-2: перенос заметки в вики заводит вику — 409 «не заведена» не видно', async () => {
  const o = await makeOwner('qa_li_move');
  const note = await o.client.post<{ id: string }>('/api/notes', {
    title: 'Заметка для переноса в вики', source: 'personal',
    content: 'Текст заметки, которую переносим в вики.',
  });
  expect(wikiInitialized(o.id), 'до переноса вики не заведена').toBeFalsy();

  const res = await request.post('/api/wiki/ingest/note', {
    headers: { Authorization: `Bearer ${o.token}` },
    data: { noteId: note.id, transfer: true },
  });
  const body = await res.text();
  expect(res.status(), `перенос должен пройти, а не отказать: ${res.status()} ${body}`).toBe(200);
  expect(body, 'прежнего кода wiki_not_initialized быть не должно').not.toContain('wiki_not_initialized');
  const dto = JSON.parse(body) as { outcome: string; section: string; page: string };
  expect(dto.outcome).toBe('transferred');

  // Вики заведена, страница перенесена на диск
  expect(wikiInitialized(o.id), 'перенос заметки должен был завести вики').toBeTruthy();
  const tree = await o.client.get<{ initialized: boolean; sections: { key: string }[] }>('/api/wiki/tree');
  expect(tree.initialized, 'GET дерева подтверждает: вики заведена').toBeTruthy();
  // dto.page — путь страницы внутри раздела (с папкой «Личное/» и расширением .md)
  const pageFile = path.join(wikiRoot(o.id), 'pages', dto.page.endsWith('.md') ? dto.page : `${dto.page}.md`);
  expect(existsSync(pageFile), `страница переноса должна лежать на диске: ${pageFile}`).toBeTruthy();
});

// ─── 5. LI-2: правка/новая страница заводит вику ───

const NEW_PAGE = `---
type: topic
aliases: []
owns: []
sources: []
status: active
---

# Новая страница QA-LI

## Сводка

## Хронология

## Противоречия

## История
`;

test('LI-2: правка (новая страница) заводит вику — 409 «не заведена» не видно', async () => {
  const o = await makeOwner('qa_li_edit');
  expect(wikiInitialized(o.id), 'до правки вики не заведена').toBeFalsy();

  const res = await request.post('/api/wiki/pages/edit', {
    headers: { Authorization: `Bearer ${o.token}` },
    data: { section: 'personal', page: 'Личное/Новая страница QA-LI.md', baseHash: null, content: NEW_PAGE },
  });
  const body = await res.text();
  expect(res.status(), `правка должна пройти: ${res.status()} ${body}`).toBe(200);
  expect(body, 'прежнего кода wiki_not_initialized быть не должно').not.toContain('wiki_not_initialized');
  expect((JSON.parse(body) as { status: string }).status).toBe('applied');

  expect(wikiInitialized(o.id), 'правка страницы должна была завести вики').toBeTruthy();
  expect(existsSync(path.join(wikiRoot(o.id), 'pages', 'Личное', 'Новая страница QA-LI.md')),
    'страница правки должна лежать на диске').toBeTruthy();
});

// ─── 6. LI-2: «Разобрать этот чат» заводит вику ───

test('LI-2: разбор чата заводит вику — 409 «не заведена» не видно', async () => {
  const o = await makeOwner('qa_li_chat');
  const root = path.join(tmpdir(), `qa_li_chat_${Date.now()}`);
  mkdirSync(root, { recursive: true });
  const project = await o.client.post<{ id: string }>('/api/projects', {
    name: 'QA-LI-CHAT', rootPath: root, createDirectory: true,
  });
  const session = await o.client.post<{ id: string }>(`/api/projects/${project.id}/sessions`, { name: 'Чат для разбора' });
  expect(wikiInitialized(o.id), 'до разбора вики не заведена').toBeFalsy();

  const res = await request.post(`/api/wiki/chats/${session.id}/analyze`, {
    headers: { Authorization: `Bearer ${o.token}` },
  });
  const body = await res.text();
  expect(res.status(), `разбор не должен отказать кодом 409 wiki_not_initialized: ${res.status()} ${body}`)
    .not.toBe(409);
  expect(body, 'прежнего кода wiki_not_initialized быть не должно').not.toContain('wiki_not_initialized');
  expect(wikiInitialized(o.id), 'разбор чата должен был завести вики').toBeTruthy();

  await o.client.del(`/api/projects/${project.id}/sessions/${session.id}`);
  await o.client.del(`/api/projects/${project.id}`);
  rmSync(root, { recursive: true, force: true });
});

// ─── 7. LI-1: ночная служба заводит вики сама ───

test('LI-1: ночная служба заводит вики сама (владелец с включённой ночью, вики нет)', async () => {
  const o = await makeOwner('qa_li_night');
  expect(wikiInitialized(o.id), 'до ночи вики не заведена').toBeFalsy();

  // Настройки ночи — файлом в вики владельца: так состояние «ночь включена, а вики нет»
  // достижимо (обычный PUT завёл бы вику сам — это отдельный сценарий).
  const now = new Date();
  const start = now.getHours();
  const end = (start + 2) % 24;
  const keeper = path.join(wikiRoot(o.id), 'keeper');
  mkdirSync(keeper, { recursive: true });
  writeFileSync(path.join(keeper, 'settings.json'), JSON.stringify({
    v: 1, autoApplyEnabled: true, classes: [],
    nightly: {
      enabled: true, windowStartHour: start, windowEndHour: end,
      maxPackagesPerNight: 20, maxPackagesPerSection: 6, maxChatsPerSection: 10, inputTokenBudget: 700000,
    },
  }), 'utf8');

  // Ждём тик ночной службы (интервал проверки — 1 минута на стенде).
  const deadline = Date.now() + 180000;
  while (Date.now() < deadline && !wikiInitialized(o.id)) {
    await new Promise(r => setTimeout(r, 3000));
  }
  expect(wikiInitialized(o.id),
    'ночная служба должна была завести вики сама (тик раз в минуту, окно — текущие два часа)').toBeTruthy();
  // След ночного прохода: итог ночи записан в keeper/nightly-last.json — вики завела именно служба.
  expect(existsSync(path.join(wikiRoot(o.id), 'keeper', 'nightly-last.json')),
    'ночной проход должен был записать итог ночи').toBeTruthy();
});

// ─── 8. Регресс: уже заведённая вика с правкой шаблона не затирается ───

test('LI регресс: правка шаблона вики не затирается повторной точкой заведения', async () => {
  const o = await makeOwner('qa_li_keep');
  // Первое заведение — включением ночи
  await o.client.put('/api/wiki/settings', { nightly: { enabled: true } });
  expect(wikiInitialized(o.id), 'включение ночи должно было завести вики').toBeTruthy();

  // Человек правит шаблон
  const schema = path.join(wikiRoot(o.id), 'pages', '_schema.md');
  const patched = readFileSync(schema, 'utf8') + '\nПравка человека QA-LI.\n';
  writeFileSync(schema, patched, 'utf8');

  // Повторная точка заведения (та же ручка) — шаблон не трогает
  await o.client.put('/api/wiki/settings', { nightly: { enabled: true } });
  expect(readFileSync(schema, 'utf8'), 'правка человека в _schema.md должна уцелеть').toBe(patched);

  // И перенос заметки (ещё одна точка заведения) — тоже не трогает
  const note = await o.client.post<{ id: string }>('/api/notes', {
    title: 'Заметка для проверки шаблона', source: 'personal', content: 'Текст заметки для проверки шаблона.',
  });
  const res = await request.post('/api/wiki/ingest/note', {
    headers: { Authorization: `Bearer ${o.token}` },
    data: { noteId: note.id, transfer: true },
  });
  expect(res.status(), 'перенос должен пройти').toBe(200);
  expect(readFileSync(schema, 'utf8'), 'правка человека в _schema.md должна уцелеть и после переноса').toBe(patched);
  await o.client.del(`/api/notes/${note.id}`);
});
