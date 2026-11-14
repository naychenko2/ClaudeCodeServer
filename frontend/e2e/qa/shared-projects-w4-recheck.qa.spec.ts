// QA-W4-2 · повтор по дефектам 8041ed7c и b505fa3d (общий проект без руководителя,
// создание чата владельцем). Стенд — голова feat/shared-projects-w4 (337c46277 + 99d04df65).
// Порядок: сначала пустое состояние (б505fa3d) — его предусловие «чатов нет»; затем
// «Новый» в тулбаре (8041ed7c). Контроль: редактор — прежний понятный отказ; личный
// проект — чат с личной персоной. Всё с префиксом qa-w4r-, уборка в afterAll.
import { test, expect, type Page } from '@playwright/test';
import { mkdirSync, writeFileSync } from 'node:fs';
import { resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import {
  EDITOR, OWNER, addMember, api, ensureFlags, ensureUsers,
  makeProject, openProject, tok, uiLogin, wait,
} from './shared-w4-helpers';
import { collectConsoleErrors } from './helpers';

const SHOTS = resolve(dirname(fileURLToPath(import.meta.url)), '../../../.cc-attachments/qa-w4r');
const RUN = process.env.QA_W4_RUN || Date.now().toString(36).slice(-6);
const N = (s: string) => `qa-w4r ${RUN} ${s}`;
const ROOT = `/home/an/qa-w4r/${RUN}`;
const GLOBAL_TOAST = 'не может быть собеседником';
const NO_LEAD = 'В проекте нет руководителя';

const report: string[] = [];
function note(s: string) {
  report.push(s);
  writeFileSync(`${SHOTS}/report-recheck.txt`, report.join('\n'));
  console.log(s);
}

let pidNoLead = '';    // общий проект без руководителя, с редактором-участником
let pidPersonal = '';  // личный проект владельца

test.describe.configure({ mode: 'serial' });
test.setTimeout(180_000);

test.beforeAll(async () => {
  mkdirSync(SHOTS, { recursive: true });
  await ensureUsers();
  await ensureFlags();
  pidNoLead = await makeProject(N('общий без руководителя'), ROOT);
  await addMember(pidNoLead, EDITOR, 'editor');
  pidPersonal = await makeProject(N('личный проект'), `${ROOT}-personal`);
  note(`фикстура: общий без руководителя ${pidNoLead} (участник-редактор ${EDITOR}), личный ${pidPersonal}`);
});

test.afterAll(async () => {
  const a = await tok(OWNER);
  for (const p of [pidNoLead, pidPersonal]) if (p) await api(a, 'DELETE', `/api/projects/${p}`).catch(() => undefined);
});

const newButton = (page: Page) => page.locator('[data-testid="chat-new-button"]').first();
const menuItem = (page: Page, label: string) => page.getByText(label, { exact: true }).first();

// Снимка собеседника чата: id → имя и зона (global/project). Без собеседника — «нет».
async function personaInfo(token: string, personaId?: string | null): Promise<string> {
  if (!personaId) return 'собеседника нет (personaId=null)';
  const p = (await api(token, 'GET', `/api/personas/${personaId}`)).body as any;
  return `${p?.name ?? '?'} id=${personaId} scope=${p?.scope ?? '?'}`;
}

// Общие проверки «создания чата» для пунктов 1–2: без тоста про глобальную персону,
// чат создан; собеседника — записать (не дефект, по постановке).
async function expectCreatedChat(page: Page, label: string, token: string, pid: string, beforeIds: string[]) {
  const globalToast = await page.getByText(GLOBAL_TOAST, { exact: false }).count();
  const noLead = await page.getByText(NO_LEAD, { exact: false }).count();
  const chats = (await api(token, 'GET', `/api/projects/${pid}/sessions`)).body as any[];
  const created = chats.filter(c => !beforeIds.includes(c.id));
  const hash = await page.evaluate(() => location.hash);
  const screen = (await page.locator('body').innerText()).replace(/\n+/g, ' | ');
  const info = created.length ? await personaInfo(token, created[0].personaId) : '—';
  note(`${label}: чатов было ${beforeIds.length}, стало ${chats.length}; тост «глобальная» — ${globalToast}; отказ «нет руководителя» — ${noLead}; hash ${hash}; собеседник: ${info}`);
  note(`${label}: тексты экрана: ${screen.slice(0, 500)}`);
  await page.screenshot({ path: `${SHOTS}/${label}.png` }).catch(() => undefined);
  expect(globalToast, `${label}: тоста про «глобальную персону» нет`).toBe(0);
  expect(created.length, `${label}: чат создан`).toBe(1);
  return created[0];
}

// ─────────────────────────────────────────────────────────────────────────────
// Пункт 2 (дефект b505fa3d): пустое состояние центра «С чего начнём?» → «Новый чат»
// ─────────────────────────────────────────────────────────────────────────────

test('R1 дефект b505fa3d: «Новый чат» в пустом состоянии — чат создаётся, тоста нет', async ({ page }) => {
  const errors = collectConsoleErrors(page);
  await uiLogin(page, OWNER);
  await openProject(page, pidNoLead);

  const before = (await api(await tok(OWNER), 'GET', `/api/projects/${pidNoLead}/sessions`)).body as any[];
  expect(before.length, 'предусловие: в проекте нет чатов').toBe(0);
  await expect(page.getByText('С чего начнём?', { exact: false }).first(), 'в центре — пустое состояние').toBeVisible({ timeout: 20000 });

  const btn = page.getByRole('button', { name: 'Новый чат', exact: true }).first();
  await expect(btn, 'кнопка «Новый чат» в пустом состоянии').toBeVisible();
  await btn.click();
  await wait(3500);

  await expectCreatedChat(page, 'r1-empty-state', await tok(OWNER), pidNoLead, []);
  const bad = errors.filter(e => !/favicon|WebSocket|SignalR/i.test(e));
  note(`R1: ошибок консоли — ${errors.length} (существенных ${bad.length}): ${bad.slice(0, 3).join(' || ')}`);
});

// ─────────────────────────────────────────────────────────────────────────────
// Пункт 1 (дефект 8041ed7c): «Новый» → «Обычный чат» в тулбаре того же проекта
// ─────────────────────────────────────────────────────────────────────────────

test('R2 дефект 8041ed7c: «Новый» → «Обычный чат» — чат создаётся и открывается, тоста нет', async ({ page }) => {
  await uiLogin(page, OWNER);
  await openProject(page, pidNoLead);
  const before = (await api(await tok(OWNER), 'GET', `/api/projects/${pidNoLead}/sessions`)).body as any[];

  const btn = newButton(page);
  await expect(btn, 'кнопка «Новый» в тулбаре').toBeVisible({ timeout: 20000 });
  await btn.click();
  await wait(500);
  await expect(menuItem(page, 'Обычный чат'), 'пункт «Обычный чат»').toBeVisible();
  await menuItem(page, 'Обычный чат').click();
  await wait(3500);

  const created = await expectCreatedChat(page, 'r2-toolbar', await tok(OWNER), pidNoLead, before.map(c => c.id));
  const hash = await page.evaluate(() => location.hash);
  expect(hash, 'созданный чат открылся').toContain(`/chat/${created.id}`);
});

// ─────────────────────────────────────────────────────────────────────────────
// Пункт 3а (контроль): редактор того же проекта — прежний понятный отказ
// ─────────────────────────────────────────────────────────────────────────────

test('R3 контроль: редактор общего проекта без руководителя — понятный отказ, чат не создан', async ({ page }) => {
  await uiLogin(page, EDITOR);
  await openProject(page, pidNoLead);
  const before = (await api(await tok(EDITOR), 'GET', `/api/projects/${pidNoLead}/sessions`)).body as any[];

  await newButton(page).click();
  await wait(500);
  await menuItem(page, 'Обычный чат').click();
  await wait(3000);

  const refusal = await page.getByText(NO_LEAD, { exact: false }).count();
  const after = (await api(await tok(EDITOR), 'GET', `/api/projects/${pidNoLead}/sessions`)).body as any[];
  note(`R3a: текст «${NO_LEAD}» — ${refusal}; чатов было ${before.length}, стало ${after.length}`);
  await page.screenshot({ path: `${SHOTS}/r3-editor-refusal.png` });
  expect(refusal, 'редактор видит текст отказа').toBeGreaterThan(0);
  expect(after.length, 'чат редактором не создан').toBe(before.length);
});

// ─────────────────────────────────────────────────────────────────────────────
// Пункт 3б (контроль): личный проект — чат создаётся как раньше, с личной персоной
// ─────────────────────────────────────────────────────────────────────────────

test('R4 контроль: личный проект — «Новый» в один клик, собеседник — личная (global) персона', async ({ page }) => {
  await uiLogin(page, OWNER);
  await openProject(page, pidPersonal);
  const token = await tok(OWNER);
  const before = (await api(token, 'GET', `/api/projects/${pidPersonal}/sessions`)).body as any[];

  await newButton(page).click();
  await wait(700);
  const menuCount = await page.getByText('Обычный чат', { exact: true }).count();
  note(`R4: в личном проекте пунктов меню «Обычный чат» — ${menuCount}`);
  expect(menuCount, 'в личном проекте меню нет').toBe(0);
  await wait(2500);

  const created = await expectCreatedChat(page, 'r4-personal', token, pidPersonal, before.map(c => c.id));
  const globals = (await api(token, 'GET', '/api/personas?scope=global')).body as any[];
  const isPersonal = Array.isArray(globals) && globals.some(p => p.id === created.personaId);
  const info = await personaInfo(token, created.personaId);
  note(`R4: собеседник чата в личном проекте — ${info}; в списке global-персон владельца — ${isPersonal}`);
  expect(isPersonal, 'собеседник — личная (global) персона владельца').toBeTruthy();
});
