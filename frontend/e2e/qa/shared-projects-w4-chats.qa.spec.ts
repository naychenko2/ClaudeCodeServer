// QA-приёмка волны 4 «Общие проекты», блок Б: удаление чатов.
// Решение Андрея 12.11: общий чат удаляет только владелец проекта; свой приватный —
// его автор; читателю — ничего.
import { test, expect, type Page } from '@playwright/test';
import { mkdirSync, writeFileSync } from 'node:fs';
import { resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import {
  EDITOR, OWNER, READER, addMember, api, createChat, createPersona, ensureFlags, ensureUsers,
  makeLead, makeProject, openChatCardMenu, openProject, tok, uiLogin, wait,
} from './shared-w4-helpers';

const SHOTS = resolve(dirname(fileURLToPath(import.meta.url)), '../../../.cc-attachments/qa-w4');
const RUN = process.env.QA_W4_RUN || Date.now().toString(36).slice(-6);
const N = (s: string) => `qa-w4 ${RUN} ${s}`;
const ROOT = `/home/an/qa-w4/chats-${RUN}`;
const DENIED = 'Общий чат удаляет только владелец проекта';

const report: string[] = [];
function note(s: string) {
  report.push(s);
  writeFileSync(`${SHOTS}/report-chats.txt`, report.join('\n'));
  console.log(s);
}

let pid = '';
let editorChat = '';   // общий чат редактора
let editorPrivate = ''; // приватный чат редактора
let ownerChat = '';    // общий чат владельца

test.describe.configure({ mode: 'serial' });
test.setTimeout(180_000);

test.beforeAll(async () => {
  mkdirSync(SHOTS, { recursive: true });
  await ensureUsers();
  await ensureFlags();
  pid = await makeProject(N('проект чатов'), ROOT);
  await addMember(pid, EDITOR, 'editor');
  await addMember(pid, READER, 'reader');
  // Руководитель проекта обязателен для создания чата редактором (400 no_project_persona)
  const lead = await createPersona(pid, N('руководитель'));
  await makeLead(lead);

  const c1 = await createChat(pid, N('общий чат редактора'), { user: EDITOR });
  expect(c1.status, `чат редактора: ${JSON.stringify(c1.body)}`).toBe(201);
  editorChat = c1.body.id;
  const c2 = await createChat(pid, N('приватный чат редактора'), { user: EDITOR, private: true });
  expect(c2.status, `приватный чат редактора: ${JSON.stringify(c2.body)}`).toBe(201);
  editorPrivate = c2.body.id;
  const c3 = await createChat(pid, N('общий чат владельца'), { user: OWNER });
  expect(c3.status, `чат владельца: ${JSON.stringify(c3.body)}`).toBe(201);
  ownerChat = c3.body.id;
  note(`фикстура: проект ${pid}; чаты редактора ${editorChat} / приватный ${editorPrivate}; чат владельца ${ownerChat}`);
});

test.afterAll(async () => {
  if (pid) await api(await tok(OWNER), 'DELETE', `/api/projects/${pid}`).catch(() => undefined);
});

// Пункт «Удалить» в открытом меню карточки чата
const deleteItem = (page: Page) => page.locator('button:has-text("Удалить")').first();

// ─────────────────────────────────────────────────────────────────────────────

test('Б5 редактор: у своего общего чата «Удалить» неактивен с подсказкой владельца', async ({ page }) => {
  await uiLogin(page, EDITOR);
  await openProject(page, pid);
  await openChatCardMenu(page, N('общий чат редактора'));

  const del = deleteItem(page);
  await expect(del, 'пункт «Удалить» виден в меню').toBeVisible({ timeout: 10000 });
  await expect(del, 'удаление общего чата редактору закрыто').toBeDisabled();
  await expect(page.getByText(DENIED).first(), 'подсказка «Общий чат удаляет только владелец проекта»').toBeVisible();
  // Прочие действия записи редактору открыты (переименование/архив)
  await expect(page.locator('button:has-text("Переименовать")').first(), 'переименование открыто').toBeEnabled();
  note('Б5: редактор — «Удалить» общего чата неактивен, подсказка владельца видна — OK');
  await page.screenshot({ path: `${SHOTS}/b5-editor-public-menu.png` });
});

test('Б6 редактор: свой приватный чат видит «Удалить», удаление работает', async ({ page }) => {
  await uiLogin(page, EDITOR);
  await openProject(page, pid);
  await openChatCardMenu(page, N('приватный чат редактора'));

  const del = deleteItem(page);
  await expect(del, 'пункт «Удалить» у приватного чата виден').toBeVisible({ timeout: 10000 });
  await expect(del, 'удаление своего приватного чата редактору открыто').toBeEnabled();
  await del.click();
  await wait(800);

  await expect(page.getByText('Удалить чат?', { exact: false }).first(), 'диалог подтверждения открылся').toBeVisible();
  await page.locator('.cc-overlay').getByRole('button', { name: 'Удалить', exact: true }).first().click();
  await wait(2500);

  const chats = (await api(await tok(EDITOR), 'GET', `/api/projects/${pid}/sessions`)).body as any[];
  const stillThere = chats.some(c => c.id === editorPrivate);
  note(`Б6: приватный чат редактора после удаления в списке — ${stillThere}; в UI имя — ${await page.getByText(N('приватный чат редактора')).count()}`);
  expect(stillThere, 'приватный чат редактора удалён').toBeFalsy();
  await page.screenshot({ path: `${SHOTS}/b6-editor-private-deleted.png` });
});

test('Б7 владелец: удаляет общий чат редактора', async ({ page }) => {
  await uiLogin(page, OWNER);
  await openProject(page, pid);
  await openChatCardMenu(page, N('общий чат редактора'));

  const del = deleteItem(page);
  await expect(del, 'удаление владельцу открыто').toBeEnabled();
  await del.click();
  await wait(800);
  await expect(page.getByText('Удалить чат?', { exact: false }).first(), 'диалог подтверждения').toBeVisible();
  await page.locator('.cc-overlay').getByRole('button', { name: 'Удалить', exact: true }).first().click();
  await wait(2500);

  const chats = (await api(await tok(OWNER), 'GET', `/api/projects/${pid}/sessions`)).body as any[];
  const stillThere = chats.some(c => c.id === editorChat);
  note(`Б7: общий чат редактора после удаления владельцем в списке — ${stillThere}`);
  expect(stillThere, 'общий чат редактора удалён владельцем').toBeFalsy();
  await page.screenshot({ path: `${SHOTS}/b7-owner-deleted-editor-chat.png` });
});

test('Б8 читатель: удалить не может ничего', async ({ page }) => {
  await uiLogin(page, READER);
  await openProject(page, pid);
  await openChatCardMenu(page, N('общий чат владельца'));

  const del = deleteItem(page);
  await expect(del, 'пункт «Удалить» виден читателю').toBeVisible({ timeout: 10000 });
  await expect(del, 'удаление читателю закрыто').toBeDisabled();
  await expect(page.getByText(DENIED).first(), 'подсказка читателю').toBeVisible();
  await page.screenshot({ path: `${SHOTS}/b8-reader-menu.png` });
  note('Б8: читателю удаление закрыто подсказкой — OK');
});
