import { test, expect, type Page } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';

// QA-проверка фичи LLM-wiki на живом стенде (QA-П1). Файл временный: удаляется после прогона.
//   PLAYWRIGHT_BASE_URL=http://127.0.0.1:5555 QA_WIKI_SHOTS=../.cc-attachments/qa-llm-wiki \
//     npx playwright test e2e/qa-llm-wiki.spec.ts

const SHOTS = process.env.QA_WIKI_SHOTS || path.join(process.cwd(), '..', '.cc-attachments', 'qa-llm-wiki');
const USER = process.env.E2E_USER || 'admin';
const PASS = process.env.E2E_PASS || '12345';

const D = { width: 1280, height: 900 };
const M = { width: 360, height: 780 };

async function shot(page: Page, name: string) {
  if (!SHOTS) return;
  fs.mkdirSync(SHOTS, { recursive: true });
  await page.screenshot({ path: path.join(SHOTS, name), fullPage: false });
}

async function loginToken(): Promise<string> {
  const r = await fetch(`${process.env.PLAYWRIGHT_BASE_URL}/api/auth/login`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ username: USER, password: PASS }),
  });
  return (await r.json()).token as string;
}

test.use({ serviceWorkers: 'block' });

test('шапка чата: три действия вики, разбор и откат', async ({ page, context, playwright, baseURL }) => {
  const request = await playwright.request.newContext({ baseURL });
  const token = await loginToken();
  await request.dispose();
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);

  // Чат проекта: открываем первый чат из списка проекта
  await page.setViewportSize(D);
  await page.goto('/#/chats');
  await page.waitForTimeout(4000);
  const pid = '047a15f0-1bda-4567-8bbb-ed39f55c376e';
  const cid = '6765a994-77cc-4a22-8045-0b2c9a2cf279';
  if (pid && cid) {
    await page.goto(`/#/project/${pid}/chat/${cid}`);
    await page.waitForTimeout(5000);
  } else {
    const chatLink = page.locator('a,button').filter({ hasText: 'QA-чат' }).first();
    const found = await chatLink.count();
    console.log('[wiki] кандидатов чатов QA на экране:', found);
    if (!found) {
      console.log('[wiki] текст списка чатов:', (await page.locator('body').innerText()).slice(0, 600));
      await shot(page, 'chat-list.png');
      return;
    }
    await chatLink.click();
    await page.waitForTimeout(3000);
  }
  await shot(page, 'chat-header.png');

  const analyze = page.locator('[data-testid="wiki-chat-analyze"]');
  const retract = page.locator('[data-testid="wiki-chat-retract"]');
  console.log('[wiki] кнопка «Разобрать этот чат»:', await analyze.count());
  console.log('[wiki] кнопка «Откатить всё из этого чата»:', await retract.count());
  console.log('[wiki] кнопка «Открыть хронику»:', await page.locator('[data-testid="wiki-chat-chronicle"]').count());

  // Клик «Открыть хронику»: событие cc-open-wiki-page — проверяем, есть ли слушатель в проде
  const chronicle = page.locator('[data-testid="wiki-chat-chronicle"]');
  if (await chronicle.count()) {
    const before = page.url();
    await chronicle.click();
    await page.waitForTimeout(1500);
    console.log('[wiki] url до/после «Открыть хронику»:', before, '→', page.url());
    console.log('[wiki] вики-страница открылась в DOM:',
      await page.locator('[data-testid="wiki-page"], [data-testid="wiki-tree"]').count());
    await shot(page, 'chat-chronicle-click.png');
  }
});

test('заметки → вкладка «Вики»: разделы, каталог, страница со сносками', async ({ page, context, playwright, baseURL }) => {
  const request = await playwright.request.newContext({ baseURL });
  const token = await loginToken();
  await request.dispose();
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
  const logs: string[] = [];
  page.on('console', m => { if (m.type() === 'error' || m.type() === 'warning') logs.push(`[${m.type()}] ${m.text()}`); });
  page.on('pageerror', e => logs.push(`[pageerror] ${e}`));
  const reqs: string[] = [];
  page.on('request', r => { if (r.url().includes('/api/wiki')) reqs.push(`${r.method()} ${r.url()} ${r.failure()?.errorText ?? ''}`); });

  await page.setViewportSize(D);
  await page.goto('/#/notes');
  await page.waitForTimeout(6000);
  await shot(page, 'notes-page.png');

  const wikiTab = page.getByText('Вики', { exact: true }).first();
  console.log('[wiki] вкладка «Вики» на странице заметок:', await wikiTab.count());
  if (!(await wikiTab.count())) { console.log('[wiki] текст страницы:', (await page.locator('body').innerText()).slice(0, 600)); return; }
  await wikiTab.click();
  await page.waitForTimeout(4000);
  console.log('[wiki] текст после клика «Вики»:', (await page.locator('body').innerText()).slice(0, 800));
  console.log('[wiki] запросы /api/wiki:', JSON.stringify(reqs, null, 1));
  console.log('[wiki] console:', JSON.stringify(logs.slice(0, 20), null, 1));
  await shot(page, 'wiki-tab.png');

  console.log('[wiki] дерево разделов:', await page.locator('[data-testid="wiki-tree"]').count());
  console.log('[wiki] строк разделов:', await page.locator('[data-testid="wiki-section-row"]').count());

  // Каталог раздела: первая строка раздела → страницы
  const sec = page.locator('[data-testid="wiki-section-row"]').first();
  if (await sec.count()) {
    await sec.click();
    await page.waitForTimeout(2000);
    await shot(page, 'wiki-catalog.png');
    console.log('[wiki] строк каталога:', await page.locator('[data-testid="wiki-page-row"]').count());

    const pageRow = page.locator('[data-testid="wiki-page-row"]').first();
    if (await pageRow.count()) {
      await pageRow.click();
      await page.waitForTimeout(2000);
      await shot(page, 'wiki-page.png');
      console.log('[wiki] страница вики открыта:', await page.locator('[data-testid="wiki-page"]').count());
      console.log('[wiki] блок источников:', await page.locator('[data-testid="wiki-sources"]').count());
    }
  }

  // Подвкладки: Входящие / Журнал / Здоровье
  for (const label of ['Входящие', 'Журнал', 'Здоровье']) {
    const tab = page.getByText(label, { exact: true }).first();
    const n = await tab.count();
    console.log(`[wiki] подвкладка «${label}» найдена:`, n);
    if (!n) continue;
    await tab.click();
    await page.waitForTimeout(2000);
    await shot(page, `wiki-${label}.png`);
    const rows = await page.locator('[data-testid="wiki-pkg-row"], [data-testid="wiki-journal-row"], [data-testid="wiki-health-row"]').count();
    console.log(`[wiki] строк в «${label}»:`, rows);
  }
});

test('мобильный экран «Разделы» вики (360)', async ({ page, context, playwright, baseURL }) => {
  const request = await playwright.request.newContext({ baseURL });
  const token = await loginToken();
  await request.dispose();
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);

  await page.setViewportSize(M);
  await page.goto('/#/notes');
  await page.waitForTimeout(6000);
  const wikiTab = page.getByText('Вики', { exact: true }).first();
  if (!(await wikiTab.count())) { console.log('[wiki] мобильный: вкладки «Вики» нет'); return; }
  await wikiTab.click();
  await page.waitForTimeout(2000);
  await shot(page, 'm-wiki-tab.png');
  const sw = page.locator('[data-testid="wiki-section-switch"]');
  console.log('[wiki] мобильный: кнопка «Раздел ▸»:', await sw.count());
  if (await sw.count()) {
    await sw.click();
    await page.waitForTimeout(1500);
    await shot(page, 'm-wiki-sections.png');
    console.log('[wiki] мобильный: строк разделов:', await page.locator('[data-testid="wiki-section-row"]').count());
  }
});
