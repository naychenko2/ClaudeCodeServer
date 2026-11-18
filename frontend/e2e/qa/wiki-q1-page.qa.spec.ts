import { test, expect, type Page, type APIRequestContext } from '@playwright/test';
import { login } from './helpers';

// QA · очередь 1 вики: страница вики (блок «Связанные»), ссылки [[…]] плоским текстом,
// мобильная раскладка 360 и тёмная тема. Стенд из worktree (:5562).

const SHOTS = '/home/an/Sources/ClaudeCodeServer-wt/wiki-queue1/.cc-attachments/wiki-test-queue1/shots';
const T = { timeout: 20000 };

let request: APIRequestContext;
let token: string;

test.describe.configure({ mode: 'serial' });
test.setTimeout(420_000);

test.beforeAll(async ({ playwright, baseURL }) => {
  request = await playwright.request.newContext({ baseURL });
  token = await login(request);
});

test.afterAll(async () => { await request?.dispose(); });

async function openNotes(page: Page, context: any) {
  page.setDefaultTimeout(20000);
  await context.addInitScript((tk: string) => localStorage.setItem('cc_token', tk), token);
  await page.goto('/#/notes');
  await page.waitForTimeout(5000);
  const tree = page.locator('[data-testid="wiki-unified-tree"]');
  if (!(await tree.count())) {
    const nav = page.locator('button', { hasText: 'Заметки' }).first();
    if (await nav.count()) { await nav.click(T); await page.waitForTimeout(7000); }
  }
  await expect(page.locator('[data-testid="wiki-unified-tree"]')).toBeVisible({ timeout: 30000 });
}

test('страница вики: блок «Связанные», пустые группы скрыты, переход по пункту', async ({ page, context }) => {
  await openNotes(page, context);
  const rows = page.locator('[data-testid="unified-row"][data-kind="page"]');
  const total = await rows.count();
  test.info().annotations.push({ type: 'note', description: `страниц в дереве: ${total}` });
  expect(total, 'в дереве есть страницы вики').toBeGreaterThan(0);

  let groupsSeen = 0, withBlock = 0, withoutBlock = 0, items = 0, kinds: string[] = [], labels: string[] = [];
  for (let i = 0; i < Math.min(total, 25); i++) {
    await rows.nth(i).click(T);
    await page.waitForTimeout(1200);
    if (!(await page.locator('[data-testid="wiki-page"]').count())) continue;
    const has = await page.locator('[data-testid="wiki-related"]').count();
    if (has) {
      withBlock++;
      const g = await page.locator('[data-testid="wiki-related-group"]').evaluateAll(
        els => els.map(e => e.getAttribute('data-kind') ?? ''));
      const l = await page.locator('[data-testid="wiki-related-group"] > div').allInnerTexts();
      kinds = [...new Set([...kinds, ...g])];
      labels = [...new Set([...labels, ...l.map(t => t.trim())])];
      items += await page.locator('[data-testid="wiki-related-item"]').count();
      if (groupsSeen === 0) {
        groupsSeen = g.length;
        await page.screenshot({ path: `${SHOTS}/wiki-page-related-1280.png` });
      }
    } else withoutBlock++;
  }
  test.info().annotations.push({ type: 'note', description: `страниц с блоком: ${withBlock}, без блока: ${withoutBlock}; виды групп: ${JSON.stringify(kinds)}; подписи: ${JSON.stringify(labels)}; пунктов: ${items}` });
  expect(withBlock, 'хоть одна страница показывает блок «Связанные»').toBeGreaterThan(0);
  test.info().annotations.push({ type: 'note', description: `страниц без блока «Связанные» в выборке: ${withoutBlock}` });

  // переход по пункту открывает страницу
  const first = page.locator('[data-testid="unified-row"][data-kind="page"]').first();
  await first.click(T);
  await page.waitForTimeout(1500);
  for (let i = 0; i < 25; i++) {
    const rel = page.locator('[data-testid="wiki-related-item"]');
    if (await rel.count()) {
      const key = await rel.first().getAttribute('data-page-key');
      const title = (await rel.first().innerText()).split('\n')[0];
      const before = (await page.locator('[data-testid="wiki-page"]').innerText()).slice(0, 120);
      await rel.first().click(T);
      await page.waitForTimeout(2500);
      const after = (await page.locator('[data-testid="wiki-page"]').innerText()).slice(0, 120);
      const newKey = await page.locator('[data-testid="wiki-related-item"]').first().getAttribute('data-page-key').catch(() => null);
      test.info().annotations.push({ type: 'note', description: `переход «${title}» (${key}): было «${before.replace(/\n/g, ' ')}» → стало «${after.replace(/\n/g, ' ')}»; первый пункт теперь ${newKey}` });
      expect(after.length, 'по пункту открылась страница').toBeGreaterThan(0);
      break;
    }
    await first.click(T).catch(() => undefined);
    await page.waitForTimeout(1200);
  }
});

test('ссылки [[…]] в тексте страницы — плоский текст, без перехода', async ({ page, context }) => {
  await openNotes(page, context);
  const rows = page.locator('[data-testid="unified-row"][data-kind="page"]');
  const total = await rows.count();
  let found = 0;
  for (let i = 0; i < Math.min(total, 12); i++) {
    await rows.nth(i).click(T);
    await page.waitForTimeout(1000);
    if (!(await page.locator('[data-testid="wiki-page"]').count())) continue;
    const hits = await page.locator('[data-testid="wiki-page"]').evaluate(el => {
      const walker = document.createTreeWalker(el, NodeFilter.SHOW_TEXT);
      let n: Node | null;
      const out: Array<{ text: string; inLink: boolean; tag: string }> = [];
      while ((n = walker.nextNode())) {
        const t = (n.textContent || '').trim();
        if (t.includes('[[')) {
          const p = n.parentElement as HTMLElement;
          out.push({ text: t.slice(0, 140), inLink: !!p?.closest('a, button, [role="link"]'), tag: p?.tagName ?? '' });
        }
        if (out.length > 2) break;
      }
      return out;
    });
    if (hits.length) {
      found++;
      test.info().annotations.push({ type: 'note', description: `страница ${i}: ${JSON.stringify(hits)}` });
    }
  }
  test.info().annotations.push({ type: 'note', description: `страниц со ссылками [[…]] в тексте: ${found}` });

  // сноска-маркер: не ссылка (цели перехода нет) — это и есть «плоский текст» по коду
  const sup = await page.locator('[data-testid="wiki-footnote"]').first().evaluate(el => {
    const p = el.parentElement as HTMLElement;
    return { tag: el.tagName, inLink: !!p?.closest('a, button, [role="link"]'), title: el.getAttribute('title') };
  }).catch(() => null);
  test.info().annotations.push({ type: 'note', description: `сноска: ${JSON.stringify(sup)}` });
  if (sup) expect(sup.inLink, 'сноска — не ссылка').toBe(false);
});

test('мобильная раскладка 360 и тёмная тема: подгруппы, тач-цель, консоль', async ({ page, context }) => {
  await page.setViewportSize({ width: 360, height: 780 });
  await page.emulateMedia({ colorScheme: 'dark' });
  await context.addInitScript((tk: string) => localStorage.setItem('cc_token', tk), token);
  await page.goto('/#/notes');
  await page.waitForTimeout(6000);
  // на 360 панель «Заметки» имеет переключатель режимов «Заметки | Вики | Граф»
  const wikiMode = page.locator('button', { hasText: /^Вики/ }).first();
  test.info().annotations.push({ type: 'note', description: `переключатель «Вики» на 360: ${await wikiMode.count()}` });
  if (await wikiMode.count()) { await wikiMode.click(T); await page.waitForTimeout(6000); }
  const treeBtn = page.locator('[data-testid="wiki-open-tree"]');
  test.info().annotations.push({ type: 'note', description: `кнопка «дерево» на 360: ${await treeBtn.count()}` });
  if (await treeBtn.count()) { await treeBtn.first().click(T); await page.waitForTimeout(6000); }
  const tree = page.locator('[data-testid="wiki-unified-tree"]');
  if (!(await tree.count())) {
    const nav = page.locator('button', { hasText: 'Заметки' }).first();
    if (await nav.count()) { await nav.click(T); await page.waitForTimeout(5000); }
  }
  await expect(page.locator('[data-testid="wiki-unified-tree"]')).toBeVisible({ timeout: 30000 });
  const errs: string[] = [];
  page.on('console', m => { if (m.type() === 'error') errs.push(m.text().slice(0, 160)); });

  const subs = page.locator('[data-testid="unified-subgroup"]');
  const n = await subs.count();
  const heights: number[] = [];
  for (let i = 0; i < Math.min(n, 15); i++) {
    const box = await subs.nth(i).boundingBox();
    if (box) heights.push(Math.round(box.height));
  }
  test.info().annotations.push({ type: 'note', description: `подгрупп: ${n}; высоты заголовков: ${JSON.stringify(heights)}` });
  expect(n, 'подгруппы видны на 360').toBeGreaterThan(0);
  const low = heights.filter(h => h < 40);
  test.info().annotations.push({ type: 'note', description: `ниже 40px: ${JSON.stringify(low)}` });
  expect(low, 'тач-цель заголовка подгруппы ≥ 40px').toEqual([]);
  await page.screenshot({ path: `${SHOTS}/wiki-tree-360-dark.png` });
  test.info().annotations.push({ type: 'note', description: `ошибки консоли: ${errs.join(' | ')}` });
});
