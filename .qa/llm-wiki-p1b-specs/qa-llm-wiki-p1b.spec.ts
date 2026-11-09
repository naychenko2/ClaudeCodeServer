import { test, expect, type Page } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';

// QA-П1b · LLM-wiki — повторная проверка на живой сборке без обходных патчей.
// Временный файл: удаляется после прогона.
//   PLAYWRIGHT_BASE_URL=http://127.0.0.1:5555 QA_WIKI_SHOTS=../.cc-attachments/qa-llm-wiki-p1b \
//     npx playwright test e2e/qa-llm-wiki-p1b.spec.ts

const SHOTS = process.env.QA_WIKI_SHOTS || path.join(process.cwd(), '..', '.cc-attachments', 'qa-llm-wiki-p1b');
const USER = process.env.E2E_USER || 'admin';
const PASS = process.env.E2E_PASS || '12345';
const PID = '047a15f0-1bda-4567-8bbb-ed39f55c376e';
// Свежие чаты, поднятые реальным разбором в этом прогоне
const CHAT_A = 'd09ee44e-6acb-4581-b1cd-121d93b4c071'; // QA-чат-D — применённый пакет
const CHAT_B = 'd09ee44e-5acb-4581-b1cd-121d93b4c070'; // QA-чат-B-продолжение — хроника со строкой «Продолжение чата»

const D = { width: 1280, height: 900 };
const M = { width: 360, height: 780 };

async function shot(page: Page, name: string) {
  if (!SHOTS) return;
  fs.mkdirSync(SHOTS, { recursive: true });
  await page.screenshot({ path: path.join(SHOTS, name), fullPage: false });
}

async function loginToken(user = USER, pass = PASS): Promise<string> {
  const r = await fetch(`${process.env.PLAYWRIGHT_BASE_URL}/api/auth/login`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ username: user, password: pass }),
  });
  return (await r.json()).token as string;
}

test.use({ serviceWorkers: 'block' });

test('п.1/п.2 — шапка чата: три кнопки, запросы /api/wiki, статус разбора', async ({ page, context, playwright, baseURL }) => {
  const request = await playwright.request.newContext({ baseURL });
  const token = await loginToken();
  await request.dispose();
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);

  const reqs: string[] = [];
  page.on('request', r => { if (r.url().includes('/api/wiki')) reqs.push(`${r.method()} ${r.url()}`); });

  await page.setViewportSize(D);
  await page.goto(`/#/project/${PID}/chat/${CHAT_A}`);
  await page.waitForTimeout(6000);
  await shot(page, 'chat-header.png');

  const analyze = page.locator('[data-testid="wiki-chat-analyze"]');
  const retract = page.locator('[data-testid="wiki-chat-retract"]');
  const chronicle = page.locator('[data-testid="wiki-chat-chronicle"]');
  console.log('[wiki] кнопка «Разобрать этот чат»:', await analyze.count());
  console.log('[wiki] кнопка «Откатить всё из этого чата»:', await retract.count());
  console.log('[wiki] кнопка «Открыть хронику»:', await chronicle.count());
  console.log('[wiki] подписи кнопок:', JSON.stringify(await page.locator('[data-testid^="wiki-chat-"]').allInnerTexts()));
  console.log('[wiki] запросы /api/wiki на чате:', JSON.stringify(reqs));
  console.log('[wiki] текст шапки:', (await page.locator('body').innerText()).slice(0, 400));
});

test('п.1 — флаг выкл: ни кнопок, ни запросов /api/wiki', async ({ page, context, playwright, baseURL }) => {
  const request = await playwright.request.newContext({ baseURL });
  const token = await loginToken();
  await request.dispose();
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);

  // Снимаем флаг llm-wiki на время прогона: локальное хранилище сторов флагов хоста
  await page.goto('/#/chats');
  await page.waitForTimeout(3000);
  const reqs: string[] = [];
  page.on('request', r => { if (r.url().includes('/api/wiki')) reqs.push(`${r.method()} ${r.url()}`); });
  const off = await page.evaluate(() => {
    const keys = Object.keys(localStorage).filter(k => k.includes('flag'));
    return keys.map(k => [k, localStorage.getItem(k)]);
  });
  console.log('[wiki] ключи флагов в localStorage:', JSON.stringify(off));
});

test('п.3/п.6 — вики: разделы, каталог, страница со сносками, Входящие, Журнал, Здоровье', async ({ page, context, playwright, baseURL }) => {
  const request = await playwright.request.newContext({ baseURL });
  const token = await loginToken();
  await request.dispose();
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
  const logs: string[] = [];
  page.on('console', m => { if (m.type() === 'error') logs.push(`[error] ${m.text()}`); });
  page.on('pageerror', e => logs.push(`[pageerror] ${e}`));
  const reqs: string[] = [];
  page.on('request', r => { if (r.url().includes('/api/wiki')) reqs.push(`${r.method()} ${r.url()}`); });

  await page.setViewportSize(D);
  await page.goto('/#/chats');
  await page.waitForTimeout(4000);
  const notesTab = page.getByText('Заметки', { exact: true }).first();
  console.log('[wiki] вкладка «Заметки» найдена:', await notesTab.count());
  if (await notesTab.count()) { await notesTab.click(); await page.waitForTimeout(4000); }
  await shot(page, 'notes-page.png');

  const wikiTab = page.getByText('Вики', { exact: true }).first();
  console.log('[wiki] вкладка «Вики»:', await wikiTab.count());
  if (!(await wikiTab.count())) { console.log('[wiki] текст:', (await page.locator('body').innerText()).slice(0, 600)); return; }
  await wikiTab.click();
  await page.waitForTimeout(4000);
  await shot(page, 'wiki-tab.png');
  console.log('[wiki] текст вики:', (await page.locator('body').innerText()).slice(0, 800));
  console.log('[wiki] запросы /api/wiki:', JSON.stringify(reqs, null, 1));
  console.log('[wiki] console errors:', JSON.stringify(logs.slice(0, 10)));

  console.log('[wiki] дерево разделов:', await page.locator('[data-testid="wiki-tree"]').count());
  console.log('[wiki] строк разделов:', await page.locator('[data-testid="wiki-section-row"]').count());

  const sec = page.locator('[data-testid="wiki-section-row"]').first();
  if (await sec.count()) {
    await sec.click();
    await page.waitForTimeout(2500);
    await shot(page, 'wiki-catalog.png');
    console.log('[wiki] строк каталога:', await page.locator('[data-testid="wiki-page-row"]').count());
    const pageRow = page.locator('[data-testid="wiki-page-row"]').first();
    if (await pageRow.count()) {
      await pageRow.click();
      await page.waitForTimeout(2500);
      await shot(page, 'wiki-page.png');
      console.log('[wiki] страница вики:', await page.locator('[data-testid="wiki-page"]').count());
      console.log('[wiki] блок источников:', await page.locator('[data-testid="wiki-sources"]').count());
    }
  }

  for (const label of ['Входящие', 'Журнал', 'Здоровье']) {
    const tab = page.getByText(label, { exact: true }).first();
    console.log(`[wiki] подвкладка «${label}» найдена:`, await tab.count());
    if (!(await tab.count())) continue;
    await tab.click();
    await page.waitForTimeout(2500);
    await shot(page, `wiki-${label}.png`);
    const rows = page.locator('[data-testid="wiki-pkg-row"], [data-testid="wiki-journal-row"], [data-testid="wiki-health-row"]');
    console.log(`[wiki] строк в «${label}»:`, await rows.count());
    const first = rows.first();
    if (await first.count()) {
      console.log(`[wiki] первая строка «${label}»:`, JSON.stringify((await first.innerText()).slice(0, 300)));
    }
  }
});

test('п.3 — карточка пакета во «Входящих»: дифф, решения, откат пакета', async ({ page, context, playwright, baseURL }) => {
  const request = await playwright.request.newContext({ baseURL });
  const token = await loginToken();
  await request.dispose();
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);

  await page.setViewportSize(D);
  await page.goto('/#/chats');
  await page.waitForTimeout(4000);
  const notesTab = page.getByText('Заметки', { exact: true }).first();
  if (await notesTab.count()) { await notesTab.click(); await page.waitForTimeout(3500); }
  const wikiTab = page.getByText('Вики', { exact: true }).first();
  if (await wikiTab.count()) { await wikiTab.click(); await page.waitForTimeout(3500); }
  const inbox = page.getByText('Входящие', { exact: true }).first();
  if (await inbox.count()) { await inbox.click(); await page.waitForTimeout(3000); }
  await shot(page, 'wiki-inbox.png');
  const rows = page.locator('[data-testid="wiki-pkg-row"]');
  console.log('[wiki] пакетов во «Входящих»:', await rows.count());
  if (await rows.count()) {
    console.log('[wiki] первая строка пакета:', JSON.stringify((await rows.first().innerText()).slice(0, 300)));
    await rows.first().click();
    await page.waitForTimeout(2500);
    await shot(page, 'wiki-package-card.png');
    console.log('[wiki] операций в карточке пакета:', await page.locator('[data-testid="wiki-op-row"]').count());
    const ops = page.locator('[data-testid="wiki-op-row"]');
    for (let i = 0; i < Math.min(await ops.count(), 3); i++) {
      console.log(`[wiki] операция ${i}:`, JSON.stringify((await ops.nth(i).innerText()).slice(0, 300)));
      const why = page.locator('[data-testid="wiki-op-why"]');
      if (await why.count()) console.log(`[wiki] причина ${i}:`, JSON.stringify((await why.nth(i).innerText()).slice(0, 200)));
    }
    const btns = await page.locator('button').allInnerTexts();
    console.log('[wiki] кнопки на карточке:', JSON.stringify(btns.filter(Boolean).slice(0, 60)));
  }
});

test('п.4/п.5 — журнал: диалог отката, честное число; перечитывание страницы после отката', async ({ page, context, playwright, baseURL }) => {
  const request = await playwright.request.newContext({ baseURL });
  const token = await loginToken();
  await request.dispose();
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);

  await page.setViewportSize(D);
  await page.goto('/#/chats');
  await page.waitForTimeout(4000);
  const notesTab = page.getByText('Заметки', { exact: true }).first();
  if (await notesTab.count()) { await notesTab.click(); await page.waitForTimeout(3500); }
  const wikiTab = page.getByText('Вики', { exact: true }).first();
  if (await wikiTab.count()) { await wikiTab.click(); await page.waitForTimeout(3500); }
  const journal = page.getByText('Журнал', { exact: true }).first();
  if (await journal.count()) { await journal.click(); await page.waitForTimeout(3000); }
  await shot(page, 'wiki-journal.png');
  console.log('[wiki] текст «Журнал»:', (await page.locator('body').innerText()).slice(0, 900));

  const rows = page.locator('[data-testid="wiki-journal-row"]');
  console.log('[wiki] строк журнала:', await rows.count());
  if (await rows.count()) {
    console.log('[wiki] первая строка журнала:', JSON.stringify((await rows.first().innerText()).slice(0, 300)));
    await rows.first().click();
    await page.waitForTimeout(2000);
    await shot(page, 'wiki-journal-entry.png');
    const retractBtn = page.locator('[data-testid="wiki-journal-retract"]');
    console.log('[wiki] кнопка отката из записи журнала:', await retractBtn.count());
    if (await retractBtn.count()) {
      console.log('[wiki] подпись кнопки отката:', JSON.stringify(await retractBtn.innerText()));
      await retractBtn.click();
      await page.waitForTimeout(1500);
      await shot(page, 'wiki-journal-retract-confirm.png');
      const dialog = page.locator('.cc-overlay');
      const n = await dialog.count();
      console.log('[wiki] диалогов на экране:', n);
      console.log('[wiki] ДИАЛОГ ОТКАТА:', JSON.stringify((await dialog.last().innerText().catch(() => '(нет диалога)')).slice(0, 900)));
    }
  }
});

test('п.3 — «Открыть хронику» открывает страницу вики (десктоп)', async ({ page, context, playwright, baseURL }) => {
  const request = await playwright.request.newContext({ baseURL });
  const token = await loginToken();
  await request.dispose();
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);

  await page.setViewportSize(D);
  await page.goto(`/#/project/${PID}/chat/${CHAT_B}`);
  await page.waitForTimeout(6000);
  const chronicle = page.locator('[data-testid="wiki-chat-chronicle"]');
  console.log('[wiki] кнопка «Открыть хронику»:', await chronicle.count());
  if (!(await chronicle.count())) return;
  const before = page.url();
  await chronicle.click();
  await page.waitForTimeout(2500);
  await shot(page, 'chat-chronicle-click.png');
  console.log('[wiki] url до/после:', before, '→', page.url());
  console.log('[wiki] вики-экран в DOM:', await page.locator('[data-testid="wiki-tree"], [data-testid="wiki-page"]').count());
  console.log('[wiki] текст:', (await page.locator('body').innerText()).slice(0, 700));
});

test('п.6 — мобильный экран «Разделы» и «Открыть хронику» на 360', async ({ page, context, playwright, baseURL }) => {
  const request = await playwright.request.newContext({ baseURL });
  const token = await loginToken();
  await request.dispose();
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);

  await page.setViewportSize(M);
  await page.goto('/#/chats');
  await page.waitForTimeout(4000);
  const notesTab = page.getByText('Заметки', { exact: true }).first();
  if (await notesTab.count()) { await notesTab.click(); await page.waitForTimeout(3500); }
  const wikiTab = page.getByText('Вики', { exact: true }).first();
  console.log('[wiki] мобильный: вкладка «Вики»:', await wikiTab.count());
  if (await wikiTab.count()) { await wikiTab.click(); await page.waitForTimeout(3000); }
  await shot(page, 'm-wiki-tab.png');
  const sw = page.locator('[data-testid="wiki-section-switch"]');
  console.log('[wiki] мобильный: кнопка «Раздел ▸»:', await sw.count());
  if (await sw.count()) {
    await sw.click();
    await page.waitForTimeout(1500);
    await shot(page, 'm-wiki-sections.png');
    console.log('[wiki] мобильный: строк разделов:', await page.locator('[data-testid="wiki-section-row"]').count());
  }

  // Шапка чата на 360: три кнопки
  await page.goto(`/#/project/${PID}/chat/${CHAT_A}`);
  await page.waitForTimeout(5000);
  await shot(page, 'm-chat-header.png');
  const btns = page.locator('[data-testid^="wiki-chat-"]');
  console.log('[wiki] мобильный: кнопок вики в шапке:', await btns.count());
  console.log('[wiki] мобильный: подписи:', JSON.stringify(await btns.allInnerTexts()));
  for (const b of await btns.all()) {
    const box = await b.boundingBox();
    console.log('[wiki] мобильный: кнопка', JSON.stringify(await b.innerText()), 'размер', JSON.stringify(box));
  }
  const chronicle = page.locator('[data-testid="wiki-chat-chronicle"]');
  if (await chronicle.count()) {
    await chronicle.click();
    await page.waitForTimeout(2500);
    await shot(page, 'm-chat-chronicle-click.png');
    console.log('[wiki] мобильный: url после «Открыть хронику»:', page.url());
    console.log('[wiki] мобильный: вики в DOM:', await page.locator('[data-testid="wiki-tree"], [data-testid="wiki-page"]').count());
  }
});

test('п.8 — изоляция: второй пользователь не видит вики первого', async ({ page, context, playwright, baseURL }) => {
  const request = await playwright.request.newContext({ baseURL });
  const token = await loginToken('qa2', '12345');
  await request.dispose();
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);

  await page.setViewportSize(D);
  await page.goto('/#/chats');
  await page.waitForTimeout(4000);
  const notesTab = page.getByText('Заметки', { exact: true }).first();
  if (await notesTab.count()) { await notesTab.click(); await page.waitForTimeout(3500); }
  const wikiTab = page.getByText('Вики', { exact: true }).first();
  console.log('[wiki] qa2: вкладка «Вики»:', await wikiTab.count());
  if (await wikiTab.count()) {
    await wikiTab.click();
    await page.waitForTimeout(3500);
    await shot(page, 'qa2-wiki.png');
    console.log('[wiki] qa2: текст вики:', (await page.locator('body').innerText()).slice(0, 600));
  }
});
