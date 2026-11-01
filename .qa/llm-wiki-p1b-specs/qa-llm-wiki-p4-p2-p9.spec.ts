import { test, type Page } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';

// QA-П1b · LLM-wiki — п.2 (статус разбора, двойное нажатие), п.4 (перечитывание страницы
// после отката), п.9 (замеры 360/1280 по вики-экранам).
const SHOTS = process.env.QA_WIKI_SHOTS || path.join(process.cwd(), '..', '.cc-attachments', 'qa-llm-wiki-p1b');
const PID = '047a15f0-1bda-4567-8bbb-ed39f55c376e';
const CHAT_E = 'd09ee44e-7acb-4581-b1cd-121d93b4c075'; // QA-чат-E — уже откатан
const CHAT_D = 'd09ee44e-6acb-4581-b1cd-121d93b4c071'; // QA-чат-D — применённый пакет (цитаты)

async function shot(page: Page, name: string) {
  if (!SHOTS) return;
  fs.mkdirSync(SHOTS, { recursive: true });
  await page.screenshot({ path: path.join(SHOTS, name), fullPage: false });
}

async function loginToken(): Promise<string> {
  const r = await fetch(`${process.env.PLAYWRIGHT_BASE_URL}/api/auth/login`, {
    method: 'POST', headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ username: 'admin', password: '12345' }),
  });
  return (await r.json()).token as string;
}

test.use({ serviceWorkers: 'block' });

async function openWiki(page: Page) {
  await page.goto('/#/chats');
  await page.waitForTimeout(4000);
  const w = page.viewportSize()?.width ?? 1280;
  if (w <= 480) {
    // На узком экране верхнее меню сворачивается в «Ещё»
    const more = page.getByRole('button', { name: 'Ещё' }).first();
    if (await more.count()) {
      await more.click();
      await page.waitForTimeout(800);
      const notesItem = page.getByText('Заметки', { exact: true }).first();
      if (await notesItem.count()) { await notesItem.click(); await page.waitForTimeout(4000); }
    }
  } else {
    const notesTab = page.getByText('Заметки', { exact: true }).first();
    if (await notesTab.count()) { await notesTab.click(); await page.waitForTimeout(3500); }
  }
  const wikiTab = page.getByText('Вики', { exact: true }).first();
  if (await wikiTab.count()) { await wikiTab.click(); await page.waitForTimeout(3500); }
}

test('п.4 — перечитывание открытой страницы после отката: экран не остаётся с откатанным содержимым', async ({ page, context, playwright, baseURL }) => {
  const request = await playwright.request.newContext({ baseURL });
  const token = await loginToken();
  await request.dispose();
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);

  await page.setViewportSize({ width: 1280, height: 900 });
  await openWiki(page);

  // Раздел проекта → каталог → страница «Правила разбора»
  const sec = page.locator('[data-testid="wiki-section-row"]').filter({ hasText: 'QA-вики' }).first();
  if (await sec.count()) { await sec.click(); await page.waitForTimeout(2500); }
  const pageRow = page.locator('[data-testid="wiki-page-row"]').filter({ hasText: 'Правила разбора' }).first();
  console.log('[p4] строка страницы найдена:', await pageRow.count());
  if (!(await pageRow.count())) { console.log('[p4] каталог:', JSON.stringify((await page.locator('body').innerText()).slice(0, 400))); return; }
  await pageRow.click();
  await page.waitForTimeout(2500);
  await shot(page, 'p4-page-before-retract.png');
  const wikiPage = page.locator('[data-testid="wiki-page"]');
  const before = await wikiPage.innerText();
  console.log('[p4] страница ДО отката, длина:', before.length);
  console.log('[p4] есть утверждение чата D («не меньше восьми»):', before.includes('не меньше восьми'));
  console.log('[p4] есть утверждение чата D («QuoteMatcher»):', before.includes('QuoteMatcher'));

  // Откат по источнику — из журнала (тот же путь, что у человека)
  const journal = page.getByText('Журнал', { exact: true }).first();
  await journal.click();
  await page.waitForTimeout(3000);
  const row = page.locator('[data-testid="wiki-journal-row"]').filter({ hasText: 'QA-чат-D' }).first();
  console.log('[p4] строка журнала по QA-чат-D:', await row.count());
  if (await row.count()) {
    await row.click();
    await page.waitForTimeout(2000);
    const rb = page.locator('[data-testid="wiki-journal-retract"]');
    if (await rb.count()) {
      await rb.click();
      await page.waitForTimeout(1200);
      await page.locator('.cc-overlay button', { hasText: 'Откатить всё' }).last().click();
      await page.waitForTimeout(3000);
      await shot(page, 'p4-after-retract.png');
      console.log('[p4] тост отката:', JSON.stringify((await page.locator('body').innerText()).match(/Откат по источнику[^\n]*/)?.[0] ?? '(нет тоста)'));
    }
  }

  // Возвращаемся на страницу и смотрим содержимое
  await openWiki(page);
  const sec2 = page.locator('[data-testid="wiki-section-row"]').filter({ hasText: 'QA-вики' }).first();
  if (await sec2.count()) { await sec2.click(); await page.waitForTimeout(2500); }
  const pr2 = page.locator('[data-testid="wiki-page-row"]').filter({ hasText: 'Правила разбора' }).first();
  if (await pr2.count()) {
    await pr2.click();
    await page.waitForTimeout(2500);
    await shot(page, 'p4-page-after-retract.png');
    const after = await page.locator('[data-testid="wiki-page"]').innerText();
    console.log('[p4] страница ПОСЛЕ отката, длина:', after.length);
    console.log('[p4] осталось «не меньше восьми»:', after.includes('не меньше восьми'));
    console.log('[p4] осталось «QuoteMatcher»:', after.includes('QuoteMatcher'));
    console.log('[p4] длина до/после:', before.length, after.length);
  }
});

test('п.2 — двойное нажатие «Разобрать этот чат»: один разбор; статус «разбираю…»', async ({ page, context, playwright, baseURL }) => {
  const request = await playwright.request.newContext({ baseURL });
  const token = await loginToken();
  await request.dispose();
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);

  const posts: string[] = [];
  page.on('request', r => { if (r.url().includes('/analyze') && r.method() === 'POST') posts.push(r.url()); });

  await page.setViewportSize({ width: 1280, height: 900 });
  // Deeplink на чат без reload не срабатывает (Hash-SPA): открываем и перезагружаем
  await page.goto(`/#/project/${PID}/chat/${CHAT_E}`);
  await page.reload();
  await page.waitForTimeout(8000);
  console.log('[p2] URL:', page.url());
  const btn = page.locator('[data-testid="wiki-chat-analyze"]');
  console.log('[p2] кнопка «Разобрать этот чат»:', await btn.count());
  console.log('[p2] текст кнопки:', JSON.stringify(await btn.innerText()));
  if (!(await btn.count())) return;

  // Двойное нажатие подряд
  await btn.click({ force: true });
  await btn.click({ force: true }).catch(e => console.log('[p2] второе нажатие:', (e as Error).message.slice(0, 80)));
  await page.waitForTimeout(700);
  await shot(page, 'p2-analyze-running.png');
  const bodyNow = await page.locator('body').innerText();
  console.log('[p2] есть «разбираю»:', /разбираю/i.test(bodyNow));
  const m = bodyNow.match(/[^\n]*разбираю[^\n]*/i);
  console.log('[p2] строка статуса:', JSON.stringify(m?.[0] ?? '(нет)'));
  console.log('[p2] текст кнопки во время разбора:', JSON.stringify(await btn.innerText()));
  await page.waitForTimeout(3000);
  console.log('[p2] POST analyze за ~4 с:', posts.length, JSON.stringify(posts));
  await page.waitForTimeout(9000);
  console.log('[p2] POST analyze итог:', posts.length, JSON.stringify(posts));
  await shot(page, 'p2-analyze-done.png');
});

test('п.9 — замеры 360/1280 по вики-экранам (свой сценарий, как ui_probe)', async ({ page, context, playwright, baseURL }) => {
  const request = await playwright.request.newContext({ baseURL });
  const token = await loginToken();
  await request.dispose();
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);

  for (const w of [360, 1280]) {
    await page.setViewportSize({ width: w, height: w === 360 ? 780 : 900 });
    await openWiki(page);
    await page.waitForTimeout(1500);
    const m = await page.evaluate(() => {
      const out: any = { interactive: 0, small: [] as any[], overflow: [] as any[], clipped: 0, unnamed: [] as any[] };
      const els = Array.from(document.querySelectorAll('button,a[href],input,select,textarea,[role=button],[role=tab],[tabindex]')) as HTMLElement[];
      out.interactive = els.length;
      for (const el of els) {
        const r = el.getBoundingClientRect();
        if (r.width === 0 || r.height === 0) continue;
        if (r.width < 40 || r.height < 40) {
          const name = (el.getAttribute('aria-label') || el.getAttribute('title') || el.textContent || '').trim().slice(0, 40);
          out.small.push({ sel: el.tagName + '.' + (el.className || '').toString().slice(0, 30), w: +r.width.toFixed(1), h: +r.height.toFixed(1), name });
        }
        const acc = el.getAttribute('aria-label') || el.getAttribute('title') || el.textContent?.trim();
        if (!acc) out.unnamed.push(el.tagName + '.' + (el.className || '').toString().slice(0, 40));
      }
      out.docOverflow = document.documentElement.scrollWidth > document.documentElement.clientWidth
        ? { sw: document.documentElement.scrollWidth, cw: document.documentElement.clientWidth } : null;
      for (const el of Array.from(document.querySelectorAll('*')) as HTMLElement[]) {
        if (el.scrollWidth > el.clientWidth + 2 && el.clientWidth > 0) {
          const cs = getComputedStyle(el);
          if (cs.overflowX === 'visible' || cs.overflowX === 'hidden') continue;
          out.overflow.push({ sel: el.tagName + '.' + (el.className || '').toString().slice(0, 30), sw: el.scrollWidth, cw: el.clientWidth });
        }
      }
      for (const el of Array.from(document.querySelectorAll('*')) as HTMLElement[]) {
        if (el.children.length === 0 && el.scrollWidth > el.clientWidth + 2 && el.clientWidth > 0) {
          const cs = getComputedStyle(el);
          if (cs.textOverflow === 'ellipsis' || cs.overflow === 'hidden') continue;
          out.clipped++;
        }
      }
      return out;
    });
    console.log(`[probe ${w}] интерактивных: ${m.interactive}, мелких(<40): ${m.small.length}, переполнение: ${m.overflow.length}, обрезанных: ${m.clipped}, безымянных: ${m.unnamed.length}, документ: ${JSON.stringify(m.docOverflow)}`);
    console.log(`[probe ${w}] мелкие:`, JSON.stringify(m.small.slice(0, 12)));
    console.log(`[probe ${w}] переполнение:`, JSON.stringify(m.overflow.slice(0, 8)));
    console.log(`[probe ${w}] безымянные:`, JSON.stringify(m.unnamed.slice(0, 8)));
    await shot(page, `probe-wiki-${w}.png`);
  }
});
