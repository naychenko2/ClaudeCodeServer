// QA-приёмка волны 4 «Общие проекты», блок А: проектные персоны у редактора.
// Решение Андрея 12.11: редактору — только просмотр и чат; карточка, память,
// привязки, автоматика, ИИ-подбор, «Сформировать команду» и создание персоны — владелец.
//
// Стенд — PLAYWRIGHT_BASE_URL, фикстура своя: админ (владелец), qa_w4_editor (редактор),
// qa_w4_reader (читатель), проект с руководителем. Уборка — afterAll.
import { test, expect, type Page } from '@playwright/test';
import { mkdirSync, writeFileSync } from 'node:fs';
import { resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import {
  OWNER, EDITOR, READER, addMember, api, createPersona, ensureFlags,
  ensureUsers, login, makeLead, makeProject, openProject, tok, uiLogin, wait,
} from './shared-w4-helpers';

const SHOTS = resolve(dirname(fileURLToPath(import.meta.url)), '../../../.cc-attachments/qa-w4');
const RUN = process.env.QA_W4_RUN || Date.now().toString(36).slice(-6);
const N = (s: string) => `qa-w4 ${RUN} ${s}`;
const ROOT = `/home/an/qa-w4/personas-${RUN}`;

const report: string[] = [];
function note(s: string) {
  report.push(s);
  writeFileSync(`${SHOTS}/report-personas.txt`, report.join('\n'));
  console.log(s);
}

let pid = '';
let leadId = '';
let secondId = '';
let editorUserId = '';

test.describe.configure({ mode: 'serial' });
test.setTimeout(180_000);

test.beforeAll(async () => {
  mkdirSync(SHOTS, { recursive: true });
  await ensureUsers();
  await ensureFlags();
  pid = await makeProject(N('проект персон'), ROOT);
  await addMember(pid, EDITOR, 'editor');
  await addMember(pid, READER, 'reader');
  leadId = await createPersona(pid, N('руководитель'));
  await makeLead(leadId);
  secondId = await createPersona(pid, N('аналитик'));
  editorUserId = (await api(await tok(EDITOR), 'GET', '/api/auth/me')).body.userId as string;
  note(`фикстура: проект ${pid}, руководитель ${leadId}, вторая ${secondId}, редактор ${editorUserId}`);
});

test.afterAll(async () => {
  const a = await login(OWNER);
  for (const id of [leadId, secondId]) if (id) await api(a, 'DELETE', `/api/personas/${id}`).catch(() => undefined);
  if (pid) await api(a, 'DELETE', `/api/projects/${pid}`).catch(() => undefined);
});

// Открыть панель «Команда» и выбрать персону по имени
async function openPersona(page: Page, name: string) {
  const teamPanel = page.locator('[aria-label="Команда"]').first();
  await expect(teamPanel, 'панель «Команда» доступна').toBeVisible({ timeout: 20000 });
  await teamPanel.click();
  await wait(2000);
  const row = page.locator('[role="option"]', { hasText: name }).first();
  await expect(row, `строка персоны «${name}» в панели`).toBeVisible({ timeout: 15000 });
  await row.click();
  await wait(2000);
}

// ─────────────────────────────────────────────────────────────────────────────
// А1 · Редактор: просмотр и чат, кнопок правки нет
// ─────────────────────────────────────────────────────────────────────────────

test('А1 редактор: карточка, память, привязки и автоматика только на чтение; «Поговорить» есть', async ({ page }) => {
  await uiLogin(page, EDITOR);
  await openProject(page, pid);
  await openPersona(page, N('руководитель'));

  // Карточка: «Редактировать» и ⋯-меню (удаление/руководитель) отсутствуют
  const edit = page.getByTitle('Редактировать');
  const more = page.getByTitle('Ещё');
  const editCount = await edit.count();
  const moreCount = await more.count();
  note(`редактор: кнопок «Редактировать» — ${editCount}, ⋯ «Ещё» — ${moreCount}`);
  expect(editCount, 'кнопки правки карточки у редактора нет').toBe(0);
  expect(moreCount, '⋯-меню персоны у редактора нет').toBe(0);

  // «Поговорить» — единственное открытое действие
  const talk = page.getByRole('button', { name: 'Поговорить', exact: false }).first();
  await expect(talk, 'кнопка «Поговорить» у редактора есть').toBeVisible({ timeout: 10000 });

  // Сайдбар: создания персоны нет
  const newPersona = page.getByTitle('Новая персона');
  note(`редактор: кнопок «Новая персона» — ${await newPersona.count()}`);
  expect(await newPersona.count(), 'создать персону редактор не может').toBe(0);

  // «Умения»: привязки видны, кнопок записи и подбора нет
  await page.getByText('Умения', { exact: true }).first().click();
  await wait(2500);
  // Привязки видны (Read), но ни записи, ни ИИ-подбора: право на запись — владельца
  await expect(page.getByText('УМЕНИЯ И ПРАВИЛА', { exact: false }), 'вкладка «Умения» открыта').toBeVisible({ timeout: 10000 });
  const ownershipHint = page.getByText('за владельцем проекта', { exact: false });
  note(`редактор, «Умения»: пояснение «за владельцем проекта» — ${await ownershipHint.count()}`);
  expect(await ownershipHint.count(), 'редактор видит пояснение, что назначение привязок — за владельцем').toBeGreaterThan(0);
  for (const label of [/Подобрать автоматически/, /Создать привязку по описанию/, /Добавить привязку/, /Создать привязку/]) {
    const c = await page.getByText(label).count();
    note(`редактор, «Умения»: «${label}» — ${c}`);
    expect(c, `в «Умениях» редактору нет «${label}»`).toBe(0);
  }
  await page.screenshot({ path: `${SHOTS}/a1-editor-bindings.png` });

  // «Память»: нет ручного добавления
  await page.getByText('Память', { exact: true }).first().click();
  await wait(1500);
  for (const label of ['Добавить вручную', 'Запомнить']) {
    const c = await page.getByText(label, { exact: true }).count();
    note(`редактор, «Память»: «${label}» — ${c}`);
    expect(c, `в «Памяти» редактору нет «${label}»`).toBe(0);
  }
  await page.screenshot({ path: `${SHOTS}/a1-editor-memory.png` });

  // «Проактивность»: ни создания правила, ни подбора, ни генерации
  await page.getByText('Проактивность', { exact: true }).first().click();
  await wait(1500);
  for (const label of [/Создать правило/, /Подобрать автоматически/, /Создать по промпту/, /Добавить правило/]) {
    const c = await page.getByText(label).count();
    note(`редактор, «Проактивность»: «${label}» — ${c}`);
    expect(c, `в «Проактивности» редактору нет «${label}»`).toBe(0);
  }
  await page.screenshot({ path: `${SHOTS}/a1-editor-automation.png` });

  // Расхождений с бэком нет: привязки и память читаются (Read)
  const b = await tok(EDITOR);
  expect((await api(b, 'GET', `/api/personas/${leadId}/bindings`)).status, 'редактор читает привязки').toBe(200);
  expect((await api(b, 'GET', `/api/personas/${leadId}/memory`)).status, 'редактор читает память').toBe(200);
  note('А1: карточка/память/привязки/автоматика редактору — только чтение; «Поговорить» есть — OK');
});

test('А1b редактор: «Поговорить» создаёт чат с персоной', async ({ page }) => {
  await uiLogin(page, EDITOR);
  await openProject(page, pid);
  await openPersona(page, N('руководитель'));
  const before = ((await api(await tok(EDITOR), 'GET', `/api/projects/${pid}/sessions`)).body as any[]).length;

  await page.getByRole('button', { name: 'Поговорить', exact: false }).first().click();
  await wait(4000);

  const chats = (await api(await tok(EDITOR), 'GET', `/api/projects/${pid}/sessions`)).body as any[];
  const mine = chats.filter(c => c.personaId === leadId && c.createdByUserId === editorUserId);
  note(`«Поговорить» редактора: чатов было ${before}, с руководителем стало ${mine.length}`);
  expect(mine.length, 'чат с руководителем создан редактором').toBeGreaterThan(0);
  const hash = await page.evaluate(() => location.hash);
  note(`адрес после «Поговорить»: ${hash}`);
  expect(hash, 'приложение открыло созданный чат').toContain('/chat/');
  await page.screenshot({ path: `${SHOTS}/a1b-editor-talk.png` });
});

test('А2 редактор: создание персоны, ИИ-подбор, команда, руководитель, память и автоматика — 403', async () => {
  const b = await tok(EDITOR);
  const cases: Array<[string, string, string, unknown]> = [
    ['создание проектной персоны', 'POST', '/api/personas', { name: N('попытка редактора'), scope: 'project', projectId: pid, voice: {} }],
    ['правка карточки', 'PUT', `/api/personas/${leadId}`, { name: N('переименование') }],
    ['ИИ-подбор привязок', 'POST', `/api/personas/${leadId}/bindings/suggest`, {}],
    ['ИИ-сборка команды', 'POST', '/api/personas/ai/team', { projectId: pid, prompt: 'собери команду' }],
    ['назначить руководителем', 'POST', `/api/personas/${leadId}/make-default`, undefined],
    ['запись в память', 'POST', `/api/personas/${leadId}/memory`, { type: 'semantic', text: 'редактор пишет в память' }],
    ['создание правила автоматики', 'POST', `/api/personas/${leadId}/automation`, { title: 'правило редактора' }],
  ];
  for (const [label, m, path, body] of cases) {
    const r = await api(b, m, path, body);
    note(`редактор ${label}: ${r.status} ${JSON.stringify(r.body).slice(0, 120)}`);
    expect(r.status, `редактору закрыто: ${label}`).toBe(403);
  }
  // Чат с персоной — открыт (решение Андрея: чат редактору можно)
  const chat = await api(b, 'POST', `/api/personas/${leadId}/chats`, { projectId: pid });
  note(`редактор создаёт чат с персоной: ${chat.status}`);
  expect(chat.status, 'чат с персоной редактору открыт').toBe(200);

  // Сайдбар/командный центр: ИИ-сборки и создания нет
  // Чат редактора в общем проекте удаляет только владелец (W4-C1) — уборка от его лица
  expect((await api(await tok(OWNER), 'DELETE', `/api/projects/${pid}/sessions/${chat.body.id}`)).status, 'уборка чата фикстуры владельцем').toBe(204);
  note('А2: все правки персоны редактору отказывают 403, чат открыт — OK');
});

test('А2b редактор: «Сформировать команду» и «Новая персона» в командном центре отсутствуют', async ({ page }) => {
  await uiLogin(page, EDITOR);
  await openProject(page, pid);
  const teamPanel = page.locator('[aria-label="Команда"]').first();
  await expect(teamPanel, 'панель «Команда» доступна').toBeVisible({ timeout: 20000 });
  await teamPanel.click();
  await wait(2000);
  const tcc = page.getByText('Командный центр', { exact: false }).first();
  await expect(tcc, 'командный центр виден редактору').toBeVisible({ timeout: 15000 });
  await tcc.click();
  await wait(2500);

  // Пункты ИИ-сборки и создания персоны живут в ⋯-меню командного центра —
  // открываем его и смотрим состав: «Новая задача» есть всегда (меню открылось)
  const more = page.getByTitle('Ещё').first();
  await expect(more, '⋯-меню командного центра').toBeVisible({ timeout: 10000 });
  await more.click();
  await wait(800);
  const formTeam = await page.getByText('Сформировать команду', { exact: false }).count();
  const newPersona = await page.getByText('Новая персона', { exact: false }).count();
  const newTask = await page.getByText('Новая задача', { exact: false }).count();
  const convoke = await page.getByText('Созвать', { exact: false }).count();
  note(`редактор в ⋯-меню командного центра: «Сформировать команду» — ${formTeam}, «Новая персона» — ${newPersona}, «Новая задача» — ${newTask}, «Созвать» — ${convoke}`);
  expect(formTeam, 'ИИ-сборки команды редактору нет').toBe(0);
  expect(newPersona, 'создания персоны в командном центре редактору нет').toBe(0);
  expect(newTask, '⋯-меню открылось (есть «Новая задача»)').toBeGreaterThan(0);
  expect(convoke, 'групповой чат (RunTurn) редактору открыт').toBeGreaterThan(0);
  await page.screenshot({ path: `${SHOTS}/a2b-editor-team-center.png` });
});

// ─────────────────────────────────────────────────────────────────────────────
// А3 · Владелец: всё как раньше
// ─────────────────────────────────────────────────────────────────────────────

test('А3 владелец: правка карточки, создание персоны, ИИ-сборка и подбор — открыты', async ({ page }) => {
  const a = await tok(OWNER);
  // API: права владельца проходят (ИИ-места могут ответить 502 — LLM стенда недоступен,
  // это не отказ права: 403 от бэка отличаем от 502 провайдера)
  const list = (await api(a, 'GET', `/api/personas?scope=project&projectId=${pid}`)).body as any[];
  expect(list.map(p => p.id)).toContain(leadId);

  const put = await api(a, 'PUT', `/api/personas/${secondId}`, { description: 'QA-W4 описание владельца' });
  note(`владелец правит карточку: ${put.status}`);
  expect(put.status, 'правка карточки владельцу открыта').toBe(200);
  const third = await createPersona(pid, N('третья персона'));
  note(`владелец создаёт персону: ${third}`);
  const mem = await api(a, 'POST', `/api/personas/${leadId}/memory`, { type: 'semantic', text: 'QA-W4 запись владельца' });
  note(`владелец пишет в память: ${mem.status}`);
  expect(mem.status, 'память владельцу открыта').toBe(200);
  const auto = await api(a, 'POST', `/api/personas/${leadId}/automation`, { title: 'QA-W4 правило владельца', event: 'timer' });
  note(`владелец создаёт правило: ${auto.status} ${JSON.stringify(auto.body).slice(0, 100)}`);
  expect([200, 201], 'автоматика владельцу открыта').toContain(auto.status);

  // ИИ-места: право пройдено (не 403); 502 = LLM стенда недоступен
  const suggest = await api(a, 'POST', `/api/personas/${leadId}/bindings/suggest`, {});
  const team = await api(a, 'POST', '/api/personas/ai/team', { projectId: pid, prompt: 'собери команду' });
  note(`владелец ИИ-подбор привязок: ${suggest.status}; ИИ-сборка команды: ${team.status}`);
  expect(suggest.status, 'ИИ-подбор привязок — не отказ права').not.toBe(403);
  expect(team.status, 'ИИ-сборка команды — не отказ права').not.toBe(403);

  // UI: «Редактировать», «Новая персона», подбор и создание правил на месте
  await uiLogin(page, OWNER);
  await openProject(page, pid);
  await openPersona(page, N('руководитель'));
  await expect(page.getByTitle('Редактировать').first(), 'владельцу «Редактировать» есть').toBeVisible({ timeout: 10000 });
  await expect(page.getByTitle('Ещё').first(), 'владельцу ⋯-меню есть').toBeVisible({ timeout: 10000 });
  expect(await page.getByTitle('Новая персона').count(), 'владельцу создание персоны есть').toBeGreaterThan(0);

  await page.getByText('Умения', { exact: true }).first().click();
  await wait(1800);
  const suggestUi = await page.getByText(/Подобрать автоматически/).count();
  note(`владелец, «Умения»: кнопок подбора — ${suggestUi}`);
  expect(suggestUi, 'владельцу ИИ-подбор привязок в UI есть').toBeGreaterThan(0);
  await page.screenshot({ path: `${SHOTS}/a3-owner-bindings.png` });

  await page.getByText('Проактивность', { exact: true }).first().click();
  await wait(1500);
  const createRule = await page.getByText(/Создать правило|Добавить правило/).count();
  note(`владелец, «Проактивность»: кнопок создания правила — ${createRule}`);
  expect(createRule, 'владельцу создание правил в UI есть').toBeGreaterThan(0);
  await page.screenshot({ path: `${SHOTS}/a3-owner-automation.png` });

  // Командный центр: ИИ-сборка на месте (пункт ⋯-меню). Свежая загрузка проекта —
  // после вкладок персоны рельса панелей может быть не смонтирована, поэтому ссылку
  // «Командный центр» ищем в открытой панели «Команда», а рельс — лишь если её нет
  await openProject(page, pid);
  let tcc = page.getByText('Командный центр', { exact: false }).first();
  if (!(await tcc.isVisible().catch(() => false))) {
    const teamPanel = page.locator('[aria-label="Команда"]').first();
    await expect(teamPanel, 'панель «Команда» доступна').toBeVisible({ timeout: 20000 });
    await teamPanel.click();
    await wait(1500);
    tcc = page.getByText('Командный центр', { exact: false }).first();
  }
  await expect(tcc, 'командный центр владельцу виден').toBeVisible({ timeout: 15000 });
  await tcc.click();
  await wait(2500);
  await page.getByTitle('Ещё').first().click();
  await wait(800);
  const formTeam = await page.getByText('Сформировать команду', { exact: false }).count();
  const newPersonaMenu = await page.getByText('Новая персона', { exact: false }).count();
  note(`владелец в ⋯-меню командного центра: «Сформировать команду» — ${formTeam}, «Новая персона» — ${newPersonaMenu}`);
  expect(formTeam, 'владельцу ИИ-сборка команды в UI есть').toBeGreaterThan(0);
  expect(newPersonaMenu, 'владельцу создание персоны в командном центре есть').toBeGreaterThan(0);
  await page.screenshot({ path: `${SHOTS}/a3-owner-team-center.png` });
  note('А3: владелец правит карточку, создаёт персону, подбор и сборку — OK');
});
