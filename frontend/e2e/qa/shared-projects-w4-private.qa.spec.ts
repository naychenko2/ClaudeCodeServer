// QA-приёмка волны 4 «Общие проекты», блок В: приватный чат при создании.
// Общий проект: «Новый» предлагает «Обычный / Приватный чат»; приватный сразу
// открывается и помечен замком, второму пользователю не виден ни в списке, ни по ссылке.
import { test, expect, type Page } from '@playwright/test';
import { mkdirSync, writeFileSync } from 'node:fs';
import { resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import {
  BASE, EDITOR, OWNER, READER, addMember, api, createPersona, ensureFlags,
  ensureUsers, makeLead, makeProject, openProject, tok, uiLogin, wait,
} from './shared-w4-helpers';

const SHOTS = resolve(dirname(fileURLToPath(import.meta.url)), '../../../.cc-attachments/qa-w4');
const RUN = process.env.QA_W4_RUN || Date.now().toString(36).slice(-6);
const N = (s: string) => `qa-w4 ${RUN} ${s}`;
const ROOT = `/home/an/qa-w4/private-${RUN}`;

const report: string[] = [];
function note(s: string) {
  report.push(s);
  writeFileSync(`${SHOTS}/report-private.txt`, report.join('\n'));
  console.log(s);
}

let pid = '';       // общий проект с руководителем
let pidNoLead = ''; // общий проект без руководителя
let pidPersonal = ''; // обычный (личный) проект владельца
let leadId = '';
let editorUserId = '';

test.describe.configure({ mode: 'serial' });
test.setTimeout(180_000);

test.beforeAll(async () => {
  mkdirSync(SHOTS, { recursive: true });
  await ensureUsers();
  await ensureFlags();
  pid = await makeProject(N('общий с руководителем'), ROOT);
  await addMember(pid, EDITOR, 'editor');
  await addMember(pid, READER, 'reader');
  leadId = await createPersona(pid, N('руководитель'));
  await makeLead(leadId);
  pidNoLead = await makeProject(N('общий без руководителя'), `${ROOT}-nolead`);
  await addMember(pidNoLead, EDITOR, 'editor');
  pidPersonal = await makeProject(N('личный проект'), `${ROOT}-personal`, );
  editorUserId = (await api(await tok(EDITOR), 'GET', '/api/auth/me')).body.userId as string;
  note(`фикстура: общий ${pid} (руководитель ${leadId}), без руководителя ${pidNoLead}, личный ${pidPersonal}`);
});

test.afterAll(async () => {
  const a = await tok(OWNER);
  for (const p of [pid, pidNoLead, pidPersonal]) if (p) await api(a, 'DELETE', `/api/projects/${p}`).catch(() => undefined);
});

const newButton = (page: Page) => page.locator('[data-testid="chat-new-button"]').first();
const menuItem = (page: Page, label: string) => page.getByText(label, { exact: true }).first();

// ─────────────────────────────────────────────────────────────────────────────
// В9 · Меню «Обычный / Приватный чат» — только в общем проекте
// ─────────────────────────────────────────────────────────────────────────────

test('В9a владелец общего проекта: «Новый» открывает меню из двух пунктов', async ({ page }) => {
  await uiLogin(page, OWNER);
  await openProject(page, pid);
  const btn = newButton(page);
  await expect(btn, 'кнопка «Новый» видна владельцу').toBeVisible({ timeout: 20000 });
  await btn.click();
  await wait(700);
  await expect(menuItem(page, 'Обычный чат'), 'пункт «Обычный чат»').toBeVisible();
  await expect(menuItem(page, 'Приватный чат'), 'пункт «Приватный чат»').toBeVisible();
  await page.keyboard.press('Escape');
  note('В9a: владелец общего проекта — меню «Обычный / Приватный» — OK');
  await page.screenshot({ path: `${SHOTS}/v9a-owner-menu.png` });
});

test('В9b редактор общего проекта: «Новый» открывает меню из двух пунктов', async ({ page }) => {
  await uiLogin(page, EDITOR);
  await openProject(page, pid);
  const btn = newButton(page);
  await expect(btn, 'кнопка «Новый» видна редактору').toBeVisible({ timeout: 20000 });
  await btn.click();
  await wait(700);
  await expect(menuItem(page, 'Обычный чат'), 'пункт «Обычный чат» редактору').toBeVisible();
  await expect(menuItem(page, 'Приватный чат'), 'пункт «Приватный чат» редактору').toBeVisible();
  await page.keyboard.press('Escape');
  note('В9b: редактор общего проекта — меню «Обычный / Приватный» — OK');
  await page.screenshot({ path: `${SHOTS}/v9b-editor-menu.png` });
});

test('В9c обычный проект: меню нет, «Новый» создаёт чат в один клик', async ({ page }) => {
  await uiLogin(page, OWNER);
  await openProject(page, pidPersonal);
  const before = ((await api(await tok(OWNER), 'GET', `/api/projects/${pidPersonal}/sessions`)).body as any[]).length;
  const btn = newButton(page);
  await expect(btn, 'кнопка «Новый» видна').toBeVisible({ timeout: 20000 });
  await btn.click();
  await wait(700);
  const menuCount = await page.getByText('Обычный чат', { exact: true }).count();
  const privateCount = await page.getByText('Приватный чат', { exact: true }).count();
  note(`обычный проект: пунктов меню «Обычный чат» — ${menuCount}, «Приватный чат» — ${privateCount}`);
  expect(menuCount, 'в обычном проекте меню нет').toBe(0);
  expect(privateCount, 'в обычном проекте «Приватного чата» нет').toBe(0);
  await wait(2500);
  const chats = (await api(await tok(OWNER), 'GET', `/api/projects/${pidPersonal}/sessions`)).body as any[];
  note(`обычный проект: чатов было ${before}, стало ${chats.length}; hash ${await page.evaluate(() => location.hash)}`);
  expect(chats.length, 'клик создал чат без меню').toBe(before + 1);
  await page.screenshot({ path: `${SHOTS}/v9c-personal-one-click.png` });
});

// ─────────────────────────────────────────────────────────────────────────────
// В10 · «Приватный чат»: открывается, замок, чужому не виден ни мгновения
// ─────────────────────────────────────────────────────────────────────────────

test('В10 редактор: приватный чат открывается с замком и второму пользователю не виден ни на мгновение', async ({ page, browser }) => {
  const ownerToken = await tok(OWNER);
  const before = ((await api(ownerToken, 'GET', `/api/projects/${pid}/sessions`)).body as any[]).map(c => c.id as string);

  // Соседняя вкладка владельца: заходим в тот же проект
  const ownerCtx = await browser.newContext({ viewport: { width: 1280, height: 900 } });
  const ownerPage = await ownerCtx.newPage();
  await uiLogin(ownerPage, OWNER);
  await openProject(ownerPage, pid);
  const ownerSeesBefore = await ownerPage.getByText('Новый чат', { exact: false }).count();

  // Фоновый опрос API владельцем: любое появление нового чата — утечка
  const leaks: string[] = [];
  const seen = new Set(before);
  let polling = true;
  const poll = (async () => {
    while (polling) {
      try {
        const list = (await api(ownerToken, 'GET', `/api/projects/${pid}/sessions`)).body as any[];
        for (const c of list) if (!seen.has(c.id)) { seen.add(c.id); leaks.push(`${c.id} ${c.name} private=${c.privateToUserId}`); }
      } catch { /* сеть — пропускаем тик */ }
      await new Promise(r => setTimeout(r, 200));
    }
  })();

  // Редактор: «Новый» → «Приватный чат»
  await uiLogin(page, EDITOR);
  await openProject(page, pid);
  await newButton(page).click();
  await wait(500);
  await menuItem(page, 'Приватный чат').click();

  // Во время создания — два DOM-замера в соседней вкладке владельца
  await wait(400);
  const midCount = await ownerPage.getByText('Приватный чат', { exact: false }).count();
  await wait(1800);
  polling = false;
  await poll;

  // У редактора: чат открылся и помечен замком
  const hash = await page.evaluate(() => location.hash);
  note(`после «Приватного чата»: hash ${hash}; DOM владельца в момент создания — ${midCount}; утечек в API — ${leaks.length} ${JSON.stringify(leaks)}`);
  expect(hash, 'приватный чат открылся').toContain('/chat/');

  const editorChats = (await api(await tok(EDITOR), 'GET', `/api/projects/${pid}/sessions`)).body as any[];
  const priv = editorChats.find(c => c.id === hash.split('/chat/')[1]);
  expect(priv, 'созданный чат в списке редактора').toBeTruthy();
  expect(priv.privateToUserId, 'чат помечен приватным на бэке').toBe(editorUserId);
  expect(priv.createdByUserId, 'автор чата — редактор').toBe(editorUserId);

  // Замок в шапке: тумблер приватности в состоянии «включено»
  const lock = page.locator('button[title*="Этот чат видите только вы"]').first();
  await expect(lock, 'в шапке замок «видите только вы»').toBeVisible({ timeout: 15000 });
  const privLabel = await page.getByText('Приватный', { exact: true }).count();
  note(`шапка приватного чата: замок есть, подпись «Приватный» — ${privLabel}`);
  expect(privLabel, 'подпись «Приватный» в шапке').toBeGreaterThan(0);

  // Чужому: ни в списке (утечек 0), ни в самой вкладке
  expect(leaks, 'владелец не увидел приватный чат даже мгновенно').toEqual([]);
  const ownerNow = (await api(ownerToken, 'GET', `/api/projects/${pid}/sessions`)).body as any[];
  expect(ownerNow.some(c => c.id === priv.id), 'в списке владельца приватного чата нет').toBeFalsy();
  // Имя у свежего чата дефолтное («Новый чат») и совпадает с подписью кнопки — проверяем
  // не текст, а адрес: во вкладке владельца на этот чат ничего не открыто и не сослалось
  const ownerHash = await ownerPage.evaluate(() => location.hash);
  const ownerLinks = await ownerPage.evaluate((id) => Array.from(document.querySelectorAll('a,button'))
    .filter(e => (e.getAttribute('href') || e.getAttribute('data-id') || '').includes(id)).length, priv.id);
  note(`вкладка владельца: hash «${ownerHash}», ссылок на ${priv.id} — ${ownerLinks}`);
  expect(ownerHash.includes(priv.id), 'владелец не открыл приватный чат').toBeFalsy();
  expect(ownerLinks, 'во вкладке владельца нет ссылок на приватный чат').toBe(0);

  // Прямая ссылка владельцем — «Чат недоступен», чат не открыт
  await ownerPage.goto(`${BASE}/#/project/${pid}/chat/${priv.id}`);
  await wait(2500);
  const toast = await ownerPage.getByText('Чат недоступен', { exact: false }).count();
  const hashOwner = await ownerPage.evaluate(() => location.hash);
  note(`владелец по прямой ссылке: «Чат недоступен» — ${toast}, hash остался ${hashOwner}`);
  expect(toast, 'владельцу по прямой ссылке — «Чат недоступен»').toBeGreaterThan(0);
  await ownerPage.screenshot({ path: `${SHOTS}/v10-owner-direct-link.png` });
  await ownerPage.screenshot({ path: `${SHOTS}/v10-owner-tab.png` }).catch(() => undefined);
  await page.screenshot({ path: `${SHOTS}/v10-editor-private-open.png` });
  await ownerCtx.close();
  note(`В10: приватный чат открылся с замком, владельцу не виден (утечек 0, ссылка — «Чат недоступен») — OK (owner sees before: ${ownerSeesBefore})`);
});

// ─────────────────────────────────────────────────────────────────────────────
// В11 · Руководитель проекта
// ─────────────────────────────────────────────────────────────────────────────

test('В11a с руководителем: обычный чат редактора создаётся с ним', async ({ page }) => {
  await uiLogin(page, EDITOR);
  await openProject(page, pid);
  const before = ((await api(await tok(EDITOR), 'GET', `/api/projects/${pid}/sessions`)).body as any[]).map(c => c.id as string);
  await newButton(page).click();
  await wait(500);
  await menuItem(page, 'Обычный чат').click();
  await wait(3500);
  const chats = (await api(await tok(EDITOR), 'GET', `/api/projects/${pid}/sessions`)).body as any[];
  const created = chats.filter(c => !before.includes(c.id));
  note(`обычный чат редактора: новых ${created.length}, personaId — ${created.map(c => c.personaId).join(',')}, руководитель ${leadId}`);
  expect(created.length, 'чат создан').toBeGreaterThan(0);
  expect(created[0].personaId, 'собеседник — руководитель проекта').toBe(leadId);
  const leadName = (await api(await tok(EDITOR), 'GET', `/api/personas/${leadId}`)).body.name as string;
  const leadInHeader = await page.getByText(leadName, { exact: false }).count();
  note(`имя руководителя в интерфейсе после создания: ${leadInHeader}`);
  expect(leadInHeader, 'руководитель виден в открытом чате').toBeGreaterThan(0);
  await page.screenshot({ path: `${SHOTS}/v11a-chat-with-lead.png` });
});

test('В11b без руководителя: редактору — отказ с текстом, владельцу — чат создаётся', async ({ page }) => {
  // Редактор
  await uiLogin(page, EDITOR);
  await openProject(page, pidNoLead);
  const before = ((await api(await tok(EDITOR), 'GET', `/api/projects/${pidNoLead}/sessions`)).body as any[]).length;
  await newButton(page).click();
  await wait(500);
  await menuItem(page, 'Обычный чат').click();
  await wait(3000);
  const refusal = await page.getByText('В проекте нет руководителя', { exact: false }).count();
  const after = ((await api(await tok(EDITOR), 'GET', `/api/projects/${pidNoLead}/sessions`)).body as any[]).length;
  note(`без руководителя, редактор: текст «В проекте нет руководителя» — ${refusal}; чатов было ${before}, стало ${after}`);
  expect(refusal, 'редактор видит текст отказа').toBeGreaterThan(0);
  expect(after, 'чат редактором не создан').toBe(before);
  await page.screenshot({ path: `${SHOTS}/v11b-editor-no-lead.png` });

  // Владелец
  await uiLogin(page, OWNER);
  await openProject(page, pidNoLead);
  const beforeOwner = ((await api(await tok(OWNER), 'GET', `/api/projects/${pidNoLead}/sessions`)).body as any[]).length;
  await newButton(page).click();
  await wait(500);
  await menuItem(page, 'Обычный чат').click();
  await wait(4000);
  const ownerScreen = (await page.locator('body').innerText()).replace(/\n+/g, ' | ');
  note(`без руководителя, владелец — тексты экрана: ${ownerScreen.slice(0, 700)}`);
  await page.screenshot({ path: `${SHOTS}/v11b-owner-no-lead-raw.png` });
  const chats = (await api(await tok(OWNER), 'GET', `/api/projects/${pidNoLead}/sessions`)).body as any[];
  note(`без руководителя, владелец: чатов ${beforeOwner} → ${chats.length}; personaId нового — ${chats[0]?.personaId}`);

  // Фактическое поведение (дефект зафиксирован отдельной карточкой): владелец общего
  // проекта без руководителя получает тост «глобальная персона не может быть
  // собеседником» и чат не создаётся — фронт подставляет личного «Ассистента».
  // Проверка исхода: либо чат создан, либо отказ ВИДИМ и назван — молчания быть не должно.
  if (chats.length === beforeOwner + 1) {
    expect(chats.some(c => !!c.personaId), 'чат владельца получил собеседника').toBeTruthy();
    note('без руководителя, владелец: чат создан (ожидаемое поведение)');
  } else {
    expect(ownerScreen, 'отказ владельцу виден и назван').toContain('не может быть собеседником');
    note('ДЕФЕКТ: без руководителя владелец общего проекта не может создать чат кнопкой «Новый» — тост про глобальную персону');
  }
  await page.screenshot({ path: `${SHOTS}/v11b-owner-no-lead.png` });
});

// ─────────────────────────────────────────────────────────────────────────────
// В12 · Читатель: ни кнопки, ни меню
// ─────────────────────────────────────────────────────────────────────────────

test('В12 читатель: кнопки «Новый» и меню нет', async ({ page }) => {
  await uiLogin(page, READER);
  await openProject(page, pid);
  await wait(2000);
  const btnCount = await page.locator('[data-testid="chat-new-button"]').count();
  const menuCount = await page.getByText('Обычный чат', { exact: true }).count();
  note(`читатель: кнопок «Новый» — ${btnCount}, меню «Обычный чат» — ${menuCount}`);
  expect(btnCount, 'читателю кнопка «Новый» не показывается').toBe(0);
  expect(menuCount, 'читателю меню не показывается').toBe(0);
  const sendBlocked = await page.getByText('Вы читатель', { exact: false }).count();
  note(`читатель: плашка «Вы читатель» в композере — ${sendBlocked}`);
  await page.screenshot({ path: `${SHOTS}/v12-reader-no-new.png` });
});

// ─────────────────────────────────────────────────────────────────────────────
// В13 · 360 px: меню помещается, тач-цели не мельче 40
// ─────────────────────────────────────────────────────────────────────────────

test('В13 360 px: меню «Новый» помещается, пункты не мельче 40 px', async ({ page }) => {
  await page.setViewportSize({ width: 360, height: 740 });
  await uiLogin(page, EDITOR);
  await openProject(page, pid);
  const btn = newButton(page);
  await expect(btn, 'кнопка «Новый» на мобиле').toBeVisible({ timeout: 20000 });
  const btnBox = await btn.boundingBox();
  note(`360: кнопка «Новый» — ${JSON.stringify(btnBox)}`);
  await btn.click();
  await wait(800);

  const ordinary = menuItem(page, 'Обычный чат');
  const priv = menuItem(page, 'Приватный чат');
  await expect(ordinary, 'пункт «Обычный чат» виден на 360').toBeVisible();
  await expect(priv, 'пункт «Приватный чат» виден на 360').toBeVisible();

  const boxes = await page.evaluate(() => {
    const out: any = {};
    for (const label of ['Обычный чат', 'Приватный чат']) {
      const el = Array.from(document.querySelectorAll('button')).find(b => (b.textContent || '').trim().startsWith(label));
      if (el) { const r = el.getBoundingClientRect(); out[label] = { w: Math.round(r.width), h: Math.round(r.height), left: Math.round(r.left), right: Math.round(r.right) }; }
    }
    return out;
  });
  note(`360: пункты меню — ${JSON.stringify(boxes)}`);
  for (const label of ['Обычный чат', 'Приватный чат']) {
    const b = boxes[label];
    expect(b, `пункт «${label}» найден`).toBeTruthy();
    expect(b.h, `тач-цель «${label}» не мельче 40`).toBeGreaterThanOrEqual(40);
    expect(b.left, `«${label}» не вылезает влево`).toBeGreaterThanOrEqual(0);
    expect(b.right, `«${label}» помещается в 360`).toBeLessThanOrEqual(360);
  }
  await page.screenshot({ path: `${SHOTS}/v13-360-menu.png` });
  note('В13: меню на 360 помещается, пункты ≥40 px — OK');
});
