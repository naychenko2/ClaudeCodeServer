import { test, expect, type APIRequestContext, type Page } from '@playwright/test';
import { api, collectConsoleErrors, login, seed, type Api } from './helpers';

// QA · меню «Вики» в шапке, на стене и в карточке чата (доработки 05.11, ветка fix/wiki-nightly-create).
// Стенд поднят из worktree вручную (systemd-run ccs-qa-wiki.service, :5560); учётка — admin/12345.

const PREFIX = 'qa-wikimenu-';

test.describe.configure({ mode: 'serial' });

let request: APIRequestContext;
let token: string;
let client: Api;
let seeded: ReturnType<typeof seed>;
let projectId: string;
let sessionId: string;

async function setFlag(on: boolean) {
  await client.put('/api/feature-flags/llm-wiki', { enabled: on });
}

// на стенде висит системное уведомление «Доступна новая версия claude CLI» —
// оно перекрывает шапку и глушит клик по кнопке, поэтому гасим его
async function dismissToast(page: Page) {
  // «Закрыть» уведомления — последняя кнопка в дереве (у панелей своих нет на этом экране)
  const close = page.locator('button[title="Закрыть"]').last();
  if (await close.count()) {
    await close.click({ timeout: 3000 }).catch(() => undefined);
    await page.waitForTimeout(500);
  }
}

async function openChat(page: Page) {
  await page.goto(`/#/project/${projectId}/chat/${sessionId}`);
  await page.waitForLoadState('domcontentloaded');
  await page.waitForTimeout(1500);
  await dismissToast(page);
}

const wikiTrigger = (page: Page) =>
  page.locator('[data-testid="wiki-chat-menu"] button[title="Действия «Вики» в этом чате"]');

// Открыть меню «Вики»: клик по триггеру, пока меню не появилось.
// Клик иногда уходит в перекрывающее уведомление — тогда повторяем.
async function openWikiMenu(page: Page) {
  // подпись пункта меняется после отказа: «Разобрать этот чат» / «Разобрать ещё раз»
  const item = page.getByRole('menuitem', { name: /Разобрать/ });
  for (let i = 0; i < 3; i++) {
    await wikiTrigger(page).first().click({ timeout: 10000 }).catch(() => undefined);
    await page.waitForTimeout(900);
    if (await item.count()) return true;
    await dismissToast(page);
  }
  return (await item.count()) > 0;
}

test.beforeAll(async ({ playwright, baseURL }) => {
  request = await playwright.request.newContext({ baseURL });
  token = await login(request);
  client = api(request, token);
  seeded = seed(client);

  const stamp = Date.now();
  const project = await seeded.project(`${PREFIX}p-${stamp}`, `/tmp/${PREFIX}${stamp}`);
  projectId = project.id;
  const s = await seeded.session(projectId, `${PREFIX}chat-${stamp}`);
  sessionId = s.id;

  await setFlag(true);
  await client.post('/api/wiki/initialize', {}).catch(() => undefined);
  // чат на стену — иначе стена пуста и в ней нечего проверять
  await client.put('/api/me/wall', { chatIds: [sessionId] }).catch(() => undefined);
});

test.afterAll(async () => {
  try { await setFlag(false); } catch { /* best-effort */ }
  await seeded?.cleanup();
  await request?.dispose();
});

test('шапка чата: кнопка «Вики» открывает меню действий', async ({ page, context }) => {
  const errors = collectConsoleErrors(page);
  const badResponses: string[] = [];
  page.on('response', r => { if (r.status() >= 400) badResponses.push(`${r.status()} ${r.url()}`); });
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
  await openChat(page);

  const trigger = wikiTrigger(page);
  await expect(trigger, 'в шапке есть кнопка «Вики»').toBeVisible({ timeout: 20000 });
  await trigger.click();

  await expect(page.getByRole('menuitem', { name: /Разобрать этот чат/ }), 'пункт «Разобрать этот чат»').toBeVisible({ timeout: 10000 });
  await expect(page.getByRole('menuitem', { name: /^Важный/ }), 'пункт «Важный»').toBeVisible();
  await expect(page.getByRole('menuitem', { name: /^Не разбирать/ }), 'пункт «Не разбирать»').toBeVisible();
  await expect(page.getByRole('menuitem', { name: /^Откатить/ }), 'пункт «Откатить всё из этого чата»').toBeVisible();
  await expect(page.getByRole('menuitem', { name: /^Открыть хронику/ }), 'пункт «Открыть хронику» (без разбора его быть не должно)').toHaveCount(0);

  test.info().annotations.push({ type: 'note', description: `ответы ≥400: ${badResponses.join(' | ') || 'нет'}` });
  // шум стенда: у локального провайдера нет ручки баланса
  const apiErrors = badResponses.filter(r => r.includes('/api/') && !r.includes('/api/providers/'));
  expect(apiErrors, 'ни один запрос /api/ не упал').toEqual([]);
  expect(errors.filter(e => !e.includes('Failed to load resource')), 'в консоли нет ошибок (кроме шума загрузки ресурсов)').toEqual([]);
});

test('«⋯» в шапке: пункт «Вики» открывает то же меню', async ({ page, context }) => {
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
  await openChat(page);

  const more = page.locator('button[aria-label="Ещё действия"]').first();
  await expect(more, 'в шапке есть кнопка «⋯»').toBeVisible({ timeout: 20000 });
  await more.click();

  const wikiItem = page.getByRole('menuitem', { name: /^Вики$/ });
  await expect(wikiItem, 'в меню «⋯» есть пункт «Вики»').toBeVisible({ timeout: 10000 });
  await wikiItem.click();

  await expect(page.getByRole('menuitem', { name: /Разобрать этот чат/ }), 'меню «Вики» открылось').toBeVisible({ timeout: 10000 });
});

test('правый клик по шапке: пункт «Вики» открывает меню', async ({ page, context }) => {
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
  await openChat(page);

  const trigger = wikiTrigger(page).first();
  await expect(trigger).toBeVisible({ timeout: 20000 });
  await trigger.click({ button: 'right' });

  const wikiItem = page.getByRole('button', { name: 'Вики', exact: true }).last();
  await expect(wikiItem, 'в меню правого клика есть пункт «Вики»').toBeVisible({ timeout: 10000 });
  await wikiItem.click();

  await expect(page.getByRole('menuitem', { name: /Разобрать этот чат/ }), 'меню «Вики» открылось').toBeVisible({ timeout: 10000 });
});

test('стена: в шапке колонки есть кнопка «Вики»', async ({ page, context }) => {
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
  await page.goto('/#/wall');
  await page.waitForLoadState('domcontentloaded');
  await page.waitForTimeout(3000);

  const count = await wikiTrigger(page).count();
  test.info().annotations.push({ type: 'note', description: `кнопок «Вики» на стене: ${count}` });
  expect(count, 'на стене есть кнопка «Вики»').toBeGreaterThan(0);
});

test('карточка чата: пункт «Вики» в меню «⋮» и подменю с пунктами', async ({ page, context }) => {
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
  await page.goto(`/#/project/${projectId}`);
  await page.waitForLoadState('domcontentloaded');
  await page.waitForTimeout(3000);

  await expect(page.getByText(new RegExp(`${PREFIX}chat-`)).first(), 'чат виден в списке').toBeVisible({ timeout: 20000 });

  // карточка (а не дерево) — с группировкой «без группировки»
  await page.evaluate(pid => localStorage.setItem(`cc_chat_filters:${pid}`, JSON.stringify({ groupBy: 'none', hierarchy: false })), projectId);
  await page.reload();
  await page.waitForTimeout(2500);
  const groupBtn = page.locator('button[title^="Группировка:"]').first();
  await expect(groupBtn).toHaveAttribute('title', /без группировки/);

  await page.getByText(new RegExp(`${PREFIX}chat-`)).first().hover();
  await page.waitForTimeout(600);
  const more = page.locator('button[title="Действия с чатом"]').first();
  await expect(more, 'у карточки есть кнопка «⋮»').toBeVisible({ timeout: 10000 });
  await more.click();

  // по умолчанию «Вики» не быстрой кнопкой: в ряду карточки её нет
  const quick = page.locator('button[title="Вики"]');
  const quickBefore = await quick.count();
  test.info().annotations.push({ type: 'note', description: `быстрых кнопок «Вики» в карточке: ${quickBefore}` });
  expect(quickBefore, 'по умолчанию быстрой кнопки «Вики» в карточке нет').toBe(0);

  const wikiRow = page.getByRole('button', { name: /^Вики/ }).last();
  await expect(wikiRow, 'в меню карточки есть пункт «Вики»').toBeVisible({ timeout: 10000 });
  await wikiRow.click();

  await expect(page.getByRole('menuitem', { name: /Разобрать этот чат/ }), 'подменю «Вики» открылось').toBeVisible({ timeout: 10000 });
});

test('карточка чата: «Откатить всё» просит подтверждение, «Отмена» ничего не откатывает', async ({ page, context }) => {
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
  await page.goto(`/#/project/${projectId}`);
  await page.waitForLoadState('domcontentloaded');
  await page.waitForTimeout(3000);

  await page.evaluate(pid => localStorage.setItem(`cc_chat_filters:${pid}`, JSON.stringify({ groupBy: 'none', hierarchy: false })), projectId);
  await page.reload();
  await page.waitForTimeout(2500);

  await page.getByText(new RegExp(`${PREFIX}chat-`)).first().hover();
  await page.waitForTimeout(600);
  const more = page.locator('button[title="Действия с чатом"]').first();
  await expect(more, 'у карточки есть кнопка «⋮»').toBeVisible({ timeout: 10000 });
  await more.click();
  const wikiRow = page.getByRole('button', { name: /^Вики/ }).last();
  await wikiRow.click();

  const retract = page.getByRole('menuitem', { name: /^Откатить/ });
  await expect(retract, 'пункт отката есть').toBeVisible({ timeout: 10000 });
  await retract.click();

  const confirm = page.getByRole('button', { name: /^Откатить всё$/ });
  await expect(confirm, 'диалог подтверждения показан').toBeVisible({ timeout: 10000 });
  const cancel = page.getByRole('button', { name: /^Отмена$/ });
  await expect(cancel, 'в диалоге есть «Отмена»').toBeVisible();
  await cancel.click();

  // «Отмена» ничего не откатывает: хроники у чата не появилось, пометки «не разбирать» нет
  const list = await client.get<{ items: Array<{ sessionId: string; chroniclePath: string | null; mark: string | null }> }>('/api/wiki/chats');
  const rec = list.items.find(i => i.sessionId === sessionId);
  test.info().annotations.push({ type: 'note', description: `запись чата в списке отбора: ${JSON.stringify(rec ?? null)}` });
  expect(rec?.chroniclePath ?? null, 'после «Отмены» хроники нет').toBeNull();
  expect(rec?.mark ?? null, 'после «Отмены» пометки «не разбирать» нет').toBeNull();
});

test('после отказа пункт называется «Разобрать ещё раз», на кнопке горит точка', async ({ page, context }) => {
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);

  // разбор чата без истории — отказ без кода: исход rejected
  const refused = await client.post<{ outcome?: string; reason?: string }>(`/api/wiki/chats/${sessionId}/analyze`);
  test.info().annotations.push({ type: 'note', description: `ответ разбора: ${JSON.stringify(refused)}` });
  expect(refused.outcome, 'разбор отказал').toBe('rejected');

  await openChat(page);
  await page.reload();
  await page.waitForTimeout(2500);

  // точка горит, пока человек итог не увидел (меню ещё не открывали)
  const dotsBefore = await wikiTrigger(page).locator('span[style*="position: absolute"]').count();
  test.info().annotations.push({ type: 'note', description: `точек-индикаторов на кнопке до открытия меню: ${dotsBefore}` });
  expect(dotsBefore, 'на кнопке горит точка с итогом').toBeGreaterThan(0);

  await wikiTrigger(page).click();

  const again = page.getByRole('menuitem', { name: /Разобрать ещё раз/ });
  await expect(again, 'после отказа пункт называется «Разобрать ещё раз»').toBeVisible({ timeout: 15000 });
  const againText = await again.innerText();
  test.info().annotations.push({ type: 'note', description: `строка пункта: ${againText}` });
  expect(againText, 'причина отказа — человеческим текстом, без «разбор окна m…»').not.toMatch(/разбор окна m\d/);

  // точка погасла, когда меню открыли
  const dotsAfter = await wikiTrigger(page).locator('span[style*="position: absolute"]').count();
  test.info().annotations.push({ type: 'note', description: `точек-индикаторов на кнопке после открытия меню: ${dotsAfter}` });
  expect(dotsAfter, 'точка погасла, когда меню открыли').toBe(0);
});

test('флаг llm-wiki выключен: кнопки «Вики» нет', async ({ page, context }) => {
  await setFlag(false);
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
  await openChat(page);
  await page.reload();
  await page.waitForTimeout(3000);

  await expect(wikiTrigger(page), 'при выключенном флаге кнопки «Вики» в шапке нет').toHaveCount(0);

  await page.goto(`/#/project/${projectId}`);
  await page.waitForTimeout(2500);
  {
    const more = page.locator('button[title="Действия с чатом"]').first();
    if (await more.count()) {
      await more.click();
      await expect(page.getByRole('button', { name: /^Вики/ }), 'при выключенном флаге пункта «Вики» в карточке нет').toHaveCount(0);
    }
  }

  await setFlag(true);
});

test('вики не заведена: пункта «Вики» нет', async ({ playwright, baseURL }) => {
  // второй пользователь: его вики не заведена
  const stamp = Date.now();
  const user = `qa_wiki_${stamp}`;
  const pass = 'qa-wiki-pass-123';
  const created = await request.post('/api/users', {
    headers: { Authorization: `Bearer ${token}` },
    data: { username: user, password: pass, role: 'user' },
  });
  test.info().annotations.push({ type: 'note', description: `создание пользователя: ${created.status()}` });

  const req2 = await playwright.request.newContext({ baseURL });
  const lr = await req2.post('/api/auth/login', { data: { username: user, password: pass } });
  const token2 = (await lr.json()).token as string;
  const client2 = api(req2, token2);
  await client2.put('/api/feature-flags/llm-wiki', { enabled: true });

  const proj2 = await client2.post<{ id: string }>('/api/projects', { name: `${PREFIX}p2-${stamp}`, rootPath: `/tmp/${PREFIX}p2-${stamp}`, createDirectory: true });
  await client2.post(`/api/projects/${proj2.id}/sessions`, { name: `${PREFIX}chat2-${stamp}` });

  const browser2 = await playwright.chromium.launch();
  const bctx = await browser2.newContext();
  await bctx.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token2);
  const page = await bctx.newPage();
  await page.goto(`/#/project/${proj2.id}`);
  await page.waitForTimeout(5000);

  const count = await page.locator('[data-testid="wiki-chat-menu"] button[title="Действия «Вики» в этом чате"]').count();
  test.info().annotations.push({ type: 'note', description: `кнопок «Вики» у второго пользователя (вики не заведена): ${count}` });
  expect(count, 'у пользователя без заведённой вики пункта «Вики» нет').toBe(0);

  await browser2.close();
  await req2.dispose();
});

test('тёмная тема: меню «Вики» читается, подпись на месте', async ({ browser }) => {
  const ctx = await browser.newContext({ colorScheme: 'dark', viewport: { width: 1280, height: 800 } });
  await ctx.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
  const page = await ctx.newPage();
  await openChat(page);

  const trigger = wikiTrigger(page).first();
  await expect(trigger, 'в тёмной теме кнопка «Вики» есть').toBeVisible({ timeout: 20000 });
  await dismissToast(page);
  const opened = await openWikiMenu(page);
  test.info().annotations.push({ type: 'note', description: `меню открылось: ${opened}` });
  await expect(page.getByRole('menuitem', { name: /Разобрать/ }), 'пункт разбора виден в тёмной теме').toBeVisible({ timeout: 10000 });
  await expect(page.getByRole('menuitem', { name: /^Важный/ }), 'пункт «Важный» виден').toBeVisible();
  await expect(page.getByRole('menuitem', { name: /^Не разбирать/ }), 'пункт «Не разбирать» виден').toBeVisible();
  await expect(page.getByRole('menuitem', { name: /^Откатить/ }), 'пункт «Откатить всё из этого чата» виден').toBeVisible();

  // подпись кнопки не обрезана и в тёмной теме
  const m = await trigger.evaluate(el => {
    const span = el.querySelector('span');
    return { text: span?.textContent ?? '', scrollW: span?.scrollWidth ?? 0, clientW: span?.clientWidth ?? 0 };
  });
  test.info().annotations.push({ type: 'note', description: `замер кнопки в тёмной теме: ${JSON.stringify(m)}` });
  expect(m.text.trim(), 'подпись «Вики» на месте').toBe('Вики');
  expect(m.scrollW, 'текст не обрезан').toBeLessThanOrEqual(m.clientW + 1);

  await ctx.close();
});

test('360: подпись «Вики» на кнопке не обрезана', async ({ browser }) => {
  const ctx = await browser.newContext({ viewport: { width: 360, height: 780 } });
  await ctx.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
  const page = await ctx.newPage();
  await openChat(page);

  const trigger = wikiTrigger(page).first();
  await expect(trigger, 'на 360 кнопка «Вики» есть').toBeVisible({ timeout: 20000 });

  const m = await trigger.evaluate(el => {
    const span = el.querySelector('span');
    const r = el.getBoundingClientRect();
    return {
      h: r.height, w: r.width,
      text: span?.textContent ?? '',
      scrollW: span?.scrollWidth ?? 0,
      clientW: span?.clientWidth ?? 0,
      overflowX: el.scrollWidth - el.clientWidth,
      docOverflow: document.documentElement.scrollWidth - document.documentElement.clientWidth,
    };
  });
  test.info().annotations.push({ type: 'note', description: `замер кнопки на 360: ${JSON.stringify(m)}` });
  expect(m.text.trim(), 'подпись «Вики» на месте').toBe('Вики');
  expect(m.scrollW, 'текст не обрезан').toBeLessThanOrEqual(m.clientW + 1);
  expect(m.h, 'высота кнопки ≥ 40 px').toBeGreaterThanOrEqual(40);
  expect(m.overflowX, 'нет горизонтального переполнения кнопки').toBeLessThanOrEqual(1);

  await ctx.close();
});
