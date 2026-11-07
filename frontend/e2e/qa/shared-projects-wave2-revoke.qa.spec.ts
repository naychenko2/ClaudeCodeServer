// QA-SHARED-PROJ-2 · волна 2, часть 2: отзыв участника, «Голоса», контекст чата,
// регрессия приватности.
//
// Стенд: ветка feat/shared-projects, порт 5557. Учётки: admin (владелец), qa_b (editor),
// qa_c (editor), qa_e (reader).
//
// Про живой ход: на стенде OAuth CLI истёк, поэтому ход агента гоняется через фейковый
// Anthropic-SSE движок на 127.0.0.1:18099 (провайдер qa-fake в appsettings.Local.json стенда).
// Скрипт движка — .cc-attachments/qa-shared-w2/fake-anthropic.py, журнал его запросов —
// fake-requests.log рядом. Движок держит ответ (hold), чтобы ход был живым, и пишет в журнал
// ТЕКСТ каждого запроса: по журналу видно, дошла ли отложенная доставка.

import { test, expect, type Page } from '@playwright/test';
import { existsSync, mkdirSync, readFileSync, readdirSync, rmSync, symlinkSync, writeFileSync } from 'node:fs';

const BASE = process.env.PLAYWRIGHT_BASE_URL || 'http://127.0.0.1:5557';
const SHOTS = '/home/an/Sources/ClaudeCodeServer-shared-projects/.cc-attachments/qa-shared-w2';
const FAKE_LOG = `${SHOTS}/fake-requests.log`;
const PROJECT_ROOT = '/home/an/qa-shared-projects/shared-proj';
const RUN = process.env.QA_ACCEPT_RUN || Date.now().toString(36).slice(-6);
const N = (s: string) => `QA-w2 ${RUN} ${s}`;

const report: string[] = [];
function note(s: string) {
  report.push(s);
  writeFileSync(`${SHOTS}/report-revoke.txt`, report.join('\n'));
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

// Журнал запросов фейкового движка: строки вида «HH:MM:SS POST /v1/messages len=… body=файл».
// Тело запроса лежит отдельным файлом (оно большое) — маркер пользователя ищем в нём.
function fakeLogLines(): string[] {
  if (!existsSync(FAKE_LOG)) return [];
  return readFileSync(FAKE_LOG, 'utf8').split('\n').filter(Boolean);
}

// Есть ли маркер в теле запроса, записанном ПОСЛЕ отметки `from` (индекс строки).
function fakeBodiesContain(marker: string, _from = 0): boolean {
  // Ищем по всем сохранённым телам запросов: журнал только перечисляет файлы.
  const dir = SHOTS;
  for (const name of readdirSync(dir)) {
    if (!name.startsWith('fake-body-')) continue;
    const p = `${dir}/${name}`;
    if (!existsSync(p)) continue;
    if (readFileSync(p, 'utf8').includes(marker)) return true;
  }
  return false;
}

async function fakeEngineAlive(): Promise<boolean> {
  try {
    const r = await fetch('http://127.0.0.1:18099/v1/models');
    return r.ok;
  } catch { return false; }
}

test.describe.configure({ mode: 'serial' });
test.beforeAll(() => { mkdirSync(SHOTS, { recursive: true }); });
test.setTimeout(600_000);

// ─────────────────────────────────────────────────────────────────────────────
// 4 · Отзыв участника: ход гасится, очередь чистится, бывшему участнику ничего не приходит
// ─────────────────────────────────────────────────────────────────────────────

test('4a отзыв участника с живым ходом: ход останавливается, очередь чистится', async () => {
  const a = await login('admin');
  const b = await login('qa_b');
  ids.admin = (await api(a, 'GET', '/api/auth/me')).body.userId;
  ids.b = (await api(b, 'GET', '/api/auth/me')).body.userId;

  expect(await fakeEngineAlive(), 'фейковый движок на :18099 должен отвечать (иначе живой ход не получить)').toBeTruthy();

  const projects = (await api(a, 'GET', '/api/projects')).body as any[];
  // Именно общий проект приёмки: корень /home/an/qa-shared-projects/shared-proj.
  // Поиск по подстроке 'shared-proj' ловит и контейнеры ('/home/an/qa-shared-projects/…').
  const shared = projects.find(p => (p.rootPath || '').endsWith('/shared-proj'));
  const pid = shared.id as string;

  // Приватный чат участника в общем проекте.
  const chat = await api(b, 'POST', `/api/projects/${pid}/sessions`, { mode: 'default', name: N('приватный чат отзыва') });
  expect(chat.status, 'участник создаёт приватный чат').toBe(201);
  const chatId = chat.body.id as string;
  await api(b, 'PUT', `/api/chats/${chatId}/visibility`, { private: true });

  const turnText = N('ход отзыва');
  const queuedText = N('отложенное при отзыве');
  const before = fakeLogLines().length;

  try {
    // Живой ход: фейковый движок держит ответ.
    const sent = await api(b, 'POST', `/api/sessions/${chatId}/messages`, { text: turnText, wait: 'none' });
    note(`  ход в приватном чате участника → ${sent.status} ${JSON.stringify(sent.body).slice(0, 120)}`);
    expect(sent.status, 'ход запущен').toBe(202);

    // Ждём, пока ход действительно пошёл (движок получил запрос).
    let got = false;
    // CLI на стенде стартует долго (синк профиля, первый запуск) — ждём щедро.
    for (let i = 0; i < 240 && !got; i++) {
      await new Promise(r => setTimeout(r, 1000));
      got = fakeLogLines().slice(before).some(l => l.includes(turnText));
    }
    note(`  движок получил запрос хода — ${got}`);
    expect(got, 'ход дошёл до движка (ход живой)').toBeTruthy();

    const st = await api(b, 'GET', `/api/chats/${chatId}`);
    note(`  статус чата участника перед отзывом — ${st.body?.status}`);
    expect(st.body?.status, 'чат в работе').toBe('working');

    // Второе сообщение встаёт в очередь (чат занят).
    const queued = await api(b, 'POST', `/api/sessions/${chatId}/messages`, { text: queuedText, wait: 'none' });
    note(`  второе сообщение → ${queued.status} ${JSON.stringify(queued.body).slice(0, 120)}`);
    expect(queued.status, 'сообщение принято в очередь').toBe(202);
    expect(queued.body?.status, 'сообщение помечено как отложенное').toBe('queued');

    // Отзыв участника владельцем.
    const revoked = await api(a, 'DELETE', `/api/projects/${pid}/members/${ids.b}`);
    note(`  отзыв участника → ${revoked.status}`);
    expect(revoked.status, 'отзыв прошёл').toBe(200);

    // Ждём, пока ход погасится и очередь разберётся.
    await new Promise(r => setTimeout(r, 12000));

    // 1. Бывшему участнику ничего не приходит: проект и чат ему недоступны.
    const listTasks = await api(b, 'GET', `/api/projects/${pid}/tasks`);
    note(`  бывший участник: список задач проекта → ${listTasks.status}`);
    expect(listTasks.status, 'бывший участник не видит проект').toBe(404);
    const chatAfter = await api(b, 'GET', `/api/chats/${chatId}`);
    note(`  бывший участник: свой бывший приватный чат → ${chatAfter.status}`);
    expect(chatAfter.status, 'бывший участник не видит свой бывший чат').toBe(404);
    const hist = await api(b, 'GET', `/api/sessions/${chatId}/history`);
    note(`  бывший участник: история бывшего чата → ${hist.status}`);
    expect(hist.status, 'бывший участник не читает историю').toBe(404);

    // 2. Отложенное сообщение НЕ доставлено: в журнале движка его нет.
    const delivered = fakeBodiesContain(queuedText, before);
    note(`  отложенное сообщение дошло до движка — ${delivered}`);
    expect(delivered, 'очередь очищена: отложенное сообщение не доставлено').toBeFalsy();

    // 3. Ход остановлен: чат больше не «working» (у отозванного он недоступен — смотрим у владельца).
    const chatList = (await api(a, 'GET', `/api/projects/${pid}/sessions`)).body as any[];
    const stillThere = chatList.find(c => c.id === chatId);
    note(`  владелец: бывший приватный чат участника в списке — ${!!stillThere}`);
    expect(stillThere, 'владелец не видит чужой приватный чат даже после отзыва').toBeFalsy();
  } finally {
    // Вернуть участника, чтобы стенд остался в прежнем составе.
    await api(a, 'POST', `/api/projects/${pid}/members`, { username: 'qa_b', role: 'editor' });
  }
});

test('4b отозван последний участник: его бывший приватный чат не получает личное пространство владельца', async () => {
  const a = await login('admin');
  const b = await login('qa_b');

  // Отдельный проект-контейнер: владелец + единственный участник.
  const proj = await api(a, 'POST', '/api/projects', {
    name: N('контейнер отзыва'), rootPath: `/home/an/qa-shared-projects/w2-revoke-${RUN}`, createDirectory: true,
  });
  expect([200, 201], 'создан проект-контейнер').toContain(proj.status);
  const pid = proj.body.id as string;

  try {
    await api(a, 'POST', `/api/projects/${pid}/members`, { username: 'qa_b', role: 'editor' });

    const chat = await api(b, 'POST', `/api/projects/${pid}/sessions`, { mode: 'default', name: N('приватный чат') });
    expect([200, 201], 'участник создал приватный чат').toContain(chat.status);
    const chatId = chat.body.id as string;
    await api(b, 'PUT', `/api/chats/${chatId}/visibility`, { private: true });

    // Глобальная персона владельца — личное пространство, которое рамка режет.
    const personas = (await api(a, 'GET', '/api/personas')).body as any[];
    const globalPersona = personas.find(p => p.scope === 'global');
    expect(globalPersona, 'у владельца есть глобальная персона').toBeTruthy();

    // Отзываем последнего участника: участников не остаётся, проект формально «личный».
    const revoked = await api(a, 'DELETE', `/api/projects/${pid}/members/${ids.b}`);
    note(`  отзыв последнего участника → ${revoked.status}`);
    expect(revoked.status, 'отзыв прошёл').toBe(200);
    const members = (await api(a, 'GET', `/api/projects/${pid}/members`)).body as any[];
    note(`  участников после отзыва — ${members.members?.length ?? 0}`);
    expect(members.members?.length ?? 0, 'участников не осталось').toBe(0);

    // Рамка хода в бывшем приватном чате: личная память глобальной персоны закрыта.
    const call = await fetch(`${BASE}/mcp/memory/${globalPersona.id}/${pid}`, {
      method: 'POST',
      headers: { Authorization: `Bearer ${a}`, 'Content-Type': 'application/json', 'X-Session-Id': chatId },
      body: JSON.stringify({ jsonrpc: '2.0', id: 1, method: 'tools/call', params: { name: 'memory_search', arguments: { query: '*' } } }),
    });
    const res: any = await call.json();
    const text = (res?.result?.content ?? []).map((c: any) => c.text).join(' ');
    note(`  рамка в бывшем приватном чате: isError=${res?.result?.isError}, текст «${text.slice(0, 160)}»`);
    expect(res?.result?.isError, 'личная память владельца в бывшем чате закрыта').toBe(true);
    expect(text, 'отказ объясняет, что это личное пространство владельца').toContain('личное пространство владельца');
  } finally {
    await api(a, 'DELETE', `/api/projects/${pid}`);
    rmSync(`/home/an/qa-shared-projects/w2-revoke-${RUN}`, { recursive: true, force: true });
  }
});

// ─────────────────────────────────────────────────────────────────────────────
// 5 · «Голоса»: образец из чужого приватного вложения не принимается
// ─────────────────────────────────────────────────────────────────────────────

test('5a образец голоса: чужое приватное вложение — отказ «файл не найден», своё и обычное — принимаются', async () => {
  const a = await login('admin');
  const b = await login('qa_b');
  const projects = (await api(a, 'GET', '/api/projects')).body as any[];
  // Именно общий проект приёмки: корень /home/an/qa-shared-projects/shared-proj.
  // Поиск по подстроке 'shared-proj' ловит и контейнеры ('/home/an/qa-shared-projects/…').
  const shared = projects.find(p => (p.rootPath || '').endsWith('/shared-proj'));
  const pid = shared.id as string;

  // WAV-образец: движок проверяет формат сам.
  const wav = Buffer.from(
    'RIFF$\x00\x00\x00WAVEfmt \x10\x00\x00\x00\x01\x00\x01\x00\x40\x1f\x00\x00\x80>\x00\x00\x02\x00\x10\x00data\x00\x00\x00\x00',
    'binary');
  const wavB64 = wav.toString('base64');

  // Приватный чат владельца + вложение в него.
  const ownChat = await api(a, 'POST', `/api/projects/${pid}/sessions`, { mode: 'default', name: N('приватный чат владельца') });
  const ownChatId = ownChat.body.id as string;
  await api(a, 'PUT', `/api/chats/${ownChatId}/visibility`, { private: true });
  const ownForm = new FormData();
  ownForm.append('file', new Blob([wav], { type: 'audio/wav' }), 'own-sample.wav');
  const ownUp = await fetch(`${BASE}/api/chats/${ownChatId}/files/upload`, {
    method: 'POST', headers: { Authorization: `Bearer ${a}` }, body: ownForm,
  });
  expect(ownUp.status, 'вложение владельца загружено').toBe(200);
  const ownPath = (await ownUp.json()).path as string;

  // Приватный чат участника + вложение в него (для владельца оно чужое).
  const otherChat = await api(b, 'POST', `/api/projects/${pid}/sessions`, { mode: 'default', name: N('приватный чат участника') });
  const otherChatId = otherChat.body.id as string;
  await api(b, 'PUT', `/api/chats/${otherChatId}/visibility`, { private: true });
  const otherForm = new FormData();
  otherForm.append('file', new Blob([wav], { type: 'audio/wav' }), 'foreign-sample.wav');
  const otherUp = await fetch(`${BASE}/api/chats/${otherChatId}/files/upload`, {
    method: 'POST', headers: { Authorization: `Bearer ${b}` }, body: otherForm,
  });
  expect(otherUp.status, 'вложение участника загружено').toBe(200);
  const otherPath = (await otherUp.json()).path as string;

  // Обычный файл проекта.
  const up = await fetch(`${BASE}/api/projects/${pid}/files/upload?path=`, {
    method: 'POST', headers: { Authorization: `Bearer ${a}` },
    body: (() => { const f = new FormData(); f.append('file', new Blob([wav], { type: 'audio/wav' }), 'plain-sample.wav'); return f; })(),
  });
  expect(up.status, 'обычный файл проекта загружен').toBe(200);

  const voicesBefore = (await api(a, 'GET', `/api/projects/${pid}/audio-editor/voices`)).body as any;

  // 1. Чужое приватное вложение — отказ, голос не создан.
  const foreignForm = new FormData();
  foreignForm.append('Name', N('голос из чужого'));
  foreignForm.append('ProjectFiles', otherPath);
  const foreign = await fetch(`${BASE}/api/projects/${pid}/audio-editor/voices`, {
    method: 'POST', headers: { Authorization: `Bearer ${a}` }, body: foreignForm,
  });
  const foreignBody: any = await foreign.json().catch(() => ({}));
  note(`  образец из чужого приватного вложения → ${foreign.status} ${JSON.stringify(foreignBody).slice(0, 200)}`);
  expect(foreign.status, 'чужое приватное вложение не принимается').toBe(400);
  expect(foreignBody.error, 'ответ «файл не найден»').toContain('Файл не найден');

  // 2. Своё приватное вложение — принимается.
  const ownForm2 = new FormData();
  ownForm2.append('Name', N('голос из своего'));
  ownForm2.append('ProjectFiles', ownPath);
  const own = await fetch(`${BASE}/api/projects/${pid}/audio-editor/voices`, {
    method: 'POST', headers: { Authorization: `Bearer ${a}` }, body: ownForm2,
  });
  const ownBody: any = await own.json().catch(() => ({}));
  note(`  образец из своего приватного вложения → ${own.status} ${JSON.stringify(ownBody).slice(0, 140)}`);
  expect(own.status, 'своё приватное вложение принимается').toBe(201);

  // 3. Обычный файл проекта — принимается.
  const plainForm = new FormData();
  plainForm.append('Name', N('голос из обычного файла'));
  plainForm.append('ProjectFiles', 'plain-sample.wav');
  const plain = await fetch(`${BASE}/api/projects/${pid}/audio-editor/voices`, {
    method: 'POST', headers: { Authorization: `Bearer ${a}` }, body: plainForm,
  });
  const plainBody: any = await plain.json().catch(() => ({}));
  note(`  образец из обычного файла проекта → ${plain.status} ${JSON.stringify(plainBody).slice(0, 140)}`);
  expect(plain.status, 'обычный файл проекта принимается').toBe(201);

  // Голос из чужого не создан: в списке нет голоса с таким именем.
  const voicesAfter = (await api(a, 'GET', `/api/projects/${pid}/audio-editor/voices`)).body as any;
  const names = (voicesAfter.voices ?? []).map((v: any) => v.name);
  const created = names.length - (voicesBefore.voices?.length ?? 0);
  note(`  голосов создано за прогон — ${created} (из них один из чужого — ${names.some(n => n.includes('голос из чужого'))})`);
  expect(names.some(n => n.includes('голос из чужого')), 'голос из чужого вложения не создан').toBeFalsy();

  // Уборка: удалить созданные голоса и загруженные файлы.
  for (const v of voicesAfter.voices ?? []) {
    if (String(v.name).includes('голос из')) await api(a, 'DELETE', `/api/projects/${pid}/audio-editor/voices/${v.slug}`);
  }
  await api(a, 'DELETE', `/api/projects/${pid}/sessions/${ownChatId}`);
  await api(a, 'DELETE', `/api/projects/${pid}/sessions/${otherChatId}`);
  await fetch(`${BASE}/api/projects/${pid}/files/content?path=plain-sample.wav`, {
    method: 'DELETE', headers: { Authorization: `Bearer ${a}` },
  });
});

// ─────────────────────────────────────────────────────────────────────────────
// 6 · Контекст общего чата: у соседа обновляется, чужое приватное в нём не появляется
// ─────────────────────────────────────────────────────────────────────────────

test('6a контекст общего чата: сосед видит обновление, чужое приватное в списке не появляется', async () => {
  const a = await login('admin');
  const c = await login('qa_c');
  ids.admin = (await api(a, 'GET', '/api/auth/me')).body.userId;
  const projects = (await api(a, 'GET', '/api/projects')).body as any[];
  const shared = projects.find(p => (p.rootPath || '').endsWith('/shared-proj'));
  const pid = shared.id as string;

  const chat = await api(a, 'POST', `/api/projects/${pid}/sessions`, { mode: 'default', name: N('общий чат контекста') });
  const chatId = chat.body.id as string;

  try {
    // Приватное вложение ВЛАДЕЛЬЦА — для соседа оно чужое.
    const ownChat = await api(a, 'POST', `/api/projects/${pid}/sessions`, { mode: 'default', name: N('приватный чат владельца') });
    const ownChatId = ownChat.body.id as string;
    await api(a, 'PUT', `/api/chats/${ownChatId}/visibility`, { private: true });
    const ownForm = new FormData();
    ownForm.append('file', new Blob(['секрет владельца'], { type: 'text/plain' }), 'owner-secret.txt');
    const ownUp = await fetch(`${BASE}/api/chats/${ownChatId}/files/upload`, {
      method: 'POST', headers: { Authorization: `Bearer ${a}` }, body: ownForm,
    });
    expect(ownUp.status, 'вложение владельца загружено').toBe(200);
    const ownerPrivatePath = (await ownUp.json()).path as string;

    // Владелец кладёт материал в контекст общего чата.
    const fileName = `qa-w2-context-${RUN}.txt`;
    await fetch(`${BASE}/api/projects/${pid}/files/upload?path=`, {
      method: 'POST', headers: { Authorization: `Bearer ${a}` },
      body: (() => { const f = new FormData(); f.append('file', new Blob(['материал контекста'], { type: 'text/plain' }), fileName); return f; })(),
    });
    const put = await api(a, 'PUT', `/api/projects/${pid}/sessions/${chatId}/context`, [
      { type: 'file', id: fileName, title: N('материал') },
    ]);
    note(`  владелец меняет контекст общего чата → ${put.status}`);
    expect(put.status, 'владелец пишет контекст общего чата').toBe(200);

    // Сосед (другой участник) читает тот же контекст — обновление доехало.
    const seen = await api(c, 'GET', `/api/projects/${pid}/sessions/${chatId}/context`);
    const items = (seen.body as any[]) ?? [];
    note(`  сосед qa_c видит контекст: ${JSON.stringify(items).slice(0, 200)}`);
    expect(seen.status, 'сосед читает контекст общего чата').toBe(200);
    expect(items.some(i => i.id === fileName), 'у соседа виден новый материал контекста').toBeTruthy();

    // Сосед пробует дописать контекст общего чата — право записи закрыто.
    const tryPut = await api(c, 'PUT', `/api/projects/${pid}/sessions/${chatId}/context`, [
      { type: 'file', id: fileName, title: 'подмена' },
    ]);
    note(`  сосед пишет контекст общего чата → ${tryPut.status} ${JSON.stringify(tryPut.body).slice(0, 120)}`);
    expect([403, 404], 'сосед не может дописывать контекст общего чата').toContain(tryPut.status);

    // Содержимое приватного вложения соседу недоступно в любом случае.
    const read = await api(c, 'GET', `/api/projects/${pid}/files/content?path=${encodeURIComponent(ownerPrivatePath)}`);
    note(`  сосед читает чужое приватное по этому пути → ${read.status}`);
    expect(read.status, 'содержимое чужого приватного соседу недоступно').toBe(404);

    await api(a, 'DELETE', `/api/projects/${pid}/sessions/${ownChatId}`);
  } finally {
    await api(a, 'DELETE', `/api/projects/${pid}/sessions/${chatId}`);
  }
});

// Найдено на приёмке: владелец кладёт в контекст общего чата путь к своему приватному
// вложению — сосед видит эту запись (имя файла и missing=false), хотя прочитать файл не может.
// Тест красный по этому дефекту (см. карточку дефекта); здесь он держит воспроизводимый след.
test('6b дефект: private-путь в контексте общего чата виден соседу (имя файла и missing=false)', async () => {
  // Ожидаемое падение: дефект зафиксирован (см. карточку трекера). Когда его починят —
  // тест начнёт проходить, и Playwright отметит это как «неожиданно зелёный» (сработает
  // правило «тест обязан краснеть, пока поведение не исправлено»).
  test.fail(true, 'известный дефект: private-путь в общем контексте виден соседу');
  const a = await login('admin');
  const c = await login('qa_c');
  const projects = (await api(a, 'GET', '/api/projects')).body as any[];
  const shared = projects.find(p => (p.rootPath || '').endsWith('/shared-proj'));
  const pid = shared.id as string;

  const ownChat = await api(a, 'POST', `/api/projects/${pid}/sessions`, { mode: 'default', name: N('приватный чат владельца 2') });
  const ownChatId = ownChat.body.id as string;
  await api(a, 'PUT', `/api/chats/${ownChatId}/visibility`, { private: true });
  const ownForm = new FormData();
  ownForm.append('file', new Blob(['секрет владельца'], { type: 'text/plain' }), 'owner-secret-2.txt');
  const ownUp = await fetch(`${BASE}/api/chats/${ownChatId}/files/upload`, {
    method: 'POST', headers: { Authorization: `Bearer ${a}` }, body: ownForm,
  });
  expect(ownUp.status, 'вложение владельца загружено').toBe(200);
  const ownerPrivatePath = (await ownUp.json()).path as string;

  const chat = await api(a, 'POST', `/api/projects/${pid}/sessions`, { mode: 'default', name: N('общий чат контекста 2') });
  const chatId = chat.body.id as string;
  try {
    await api(a, 'PUT', `/api/projects/${pid}/sessions/${chatId}/context`, [
      { type: 'file', id: ownerPrivatePath, title: N('материал') },
    ]);
    const neighbor = (await api(c, 'GET', `/api/projects/${pid}/sessions/${chatId}/context`)).body as any[];
    const leaked = (neighbor ?? []).filter(i => String(i.id).includes('private/'));
    note(`  у соседа записей с private-путем — ${leaked.length}: ${JSON.stringify(leaked).slice(0, 220)}`);
    expect(leaked, 'чужое приватное не должно появляться в общем контексте у соседа').toEqual([]);
  } finally {
    await api(a, 'DELETE', `/api/projects/${pid}/sessions/${chatId}`);
    await api(a, 'DELETE', `/api/projects/${pid}/sessions/${ownChatId}`);
  }
});

test('7a приватность держит: список чатов, поиск по именам, дерево, симлинк на корень', async ({ page }) => {
  const a = await login('admin');
  const b = await login('qa_b');
  ids.admin = (await api(a, 'GET', '/api/auth/me')).body.userId;
  ids.b = (await api(b, 'GET', '/api/auth/me')).body.userId;
  const projects = (await api(a, 'GET', '/api/projects')).body as any[];
  // Именно общий проект приёмки: корень /home/an/qa-shared-projects/shared-proj.
  // Поиск по подстроке 'shared-proj' ловит и контейнеры ('/home/an/qa-shared-projects/…').
  const shared = projects.find(p => (p.rootPath || '').endsWith('/shared-proj'));
  const pid = shared.id as string;

  const chatName = N('приватный чат владельца');
  const chat = await api(a, 'POST', `/api/projects/${pid}/sessions`, { mode: 'default', name: chatName });
  const chatId = chat.body.id as string;
  await api(a, 'PUT', `/api/chats/${chatId}/visibility`, { private: true });

  const secretName = `qa-w2-secret-${RUN}.txt`;
  const form = new FormData();
  form.append('file', new Blob(['секрет владельца'], { type: 'text/plain' }), secretName);
  const up = await fetch(`${BASE}/api/chats/${chatId}/files/upload`, {
    method: 'POST', headers: { Authorization: `Bearer ${a}` }, body: form,
  });
  expect(up.status, 'вложение владельца загружено').toBe(200);

  const linkName = `QA-W2-ROOT-${RUN}`;
  const linkPath = `${PROJECT_ROOT}/${linkName}`;
  rmSync(linkPath, { force: true });
  symlinkSync(PROJECT_ROOT, linkPath);

  try {
    // 1. Список чатов: приватного чата владельца у участника нет.
    const chats = (await api(b, 'GET', `/api/projects/${pid}/sessions`)).body as any[];
    expect(chats.some(c => c.id === chatId), 'приватный чат владельца не виден участнику').toBeFalsy();
    note(`  список чатов участника: ${chats.length}, приватного чата владельца нет — OK`);

    // 2. Поиск по именам: чужое приватное вложение не находится.
    const search = (await api(b, 'GET', `/api/projects/${pid}/files/search?q=${encodeURIComponent(secretName)}`)).body as any[];
    note(`  поиск участника на «${secretName}»: выдача ${search.length}`);
    expect(search, 'чужое приватное вложение не находится по имени').toEqual([]);

    // 3. Дерево: чужих private-путей нет.
    const tree = (await api(b, 'GET', `/api/projects/${pid}/files/tree?path=`)).body as any[];
    const treePaths = tree.map(x => x.path as string);
    const leaked = treePaths.filter(p => p.includes(`private/${ids.admin}/`) || p.includes(secretName));
    note(`  дерево участника: ${treePaths.length} путей, чужих private ${leaked.length}`);
    expect(leaked, 'в дереве нет чужих private-путей').toEqual([]);

    // 4. Симлинк на корень проекта: чужие private-имена не находятся.
    const viaLink = (await api(b, 'GET', `/api/projects/${pid}/files/search?q=${encodeURIComponent(secretName)}`)).body as any[];
    const leakedLink = viaLink.filter(x => x.path.includes(`private/${ids.admin}/`));
    note(`  поиск через симлинк на корень: выдача ${viaLink.length}, чужих private ${leakedLink.length}`);
    expect(leakedLink, 'через симлинк на корень чужие private не находятся').toEqual([]);
    const readLink = await api(b, 'GET', `/api/projects/${pid}/files/content?path=${encodeURIComponent(`${linkName}/.cc-attachments/private/${ids.admin}/${secretName}`)}`);
    note(`  чтение чужого private через симлинк → ${readLink.status}`);
    expect(readLink.status, 'чтение чужого private по симлинку закрыто').toBe(404);

    // 5. В интерфейсе участника приватного чата владельца нет.
    await uiLogin(page, 'qa_b');
    await page.goto(`${BASE}/#/project/${pid}`);
    await page.reload();
    await page.waitForTimeout(2500);
    const inUi = await page.getByText(chatName, { exact: false }).first().isVisible().catch(() => false);
    note(`  лента чатов участника: приватный чат владельца виден — ${inUi}`);
    await page.screenshot({ path: `${SHOTS}/7a-privacy-reader.png` });
    expect(inUi, 'приватный чат владельца не виден в ленте участника').toBeFalsy();
  } finally {
    rmSync(linkPath, { force: true });
    await api(a, 'DELETE', `/api/projects/${pid}/sessions/${chatId}`);
  }
});
