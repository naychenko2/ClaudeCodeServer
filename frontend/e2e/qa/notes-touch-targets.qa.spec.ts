// Замер тач-целей кнопок «Новая» и «Дневниковая заметка» на мобиле (360) и на десктопе (1280).
// Правка — коммит 7537d322c: на мобиле обе кнопки не меньше TB.iconHitMobile (40 px),
// на десктопе размеры прежние. Проверка — живой стенд master (docs/operations/qa-stand.md).
//
// Точки замера:
//   1. мобильный список заметок (режим «Заметки») — сайдбар;
//   2. шторка дерева (режим «Вики» + единое дерево) — кнопки внутри wiki-drawer-tree;
//   3. десктоп 1280 — левая панель — размеры должны остаться прежними.

import { test, expect, type APIRequestContext, type Locator, type Page } from '@playwright/test';
import { api, login, type Api } from './helpers';

const BASE = process.env.PLAYWRIGHT_BASE_URL || 'http://127.0.0.1:5555';
const MIN = 40; // TB.iconHitMobile

let request: APIRequestContext;
let token: string;
let client: Api;

async function setFlags(unified: boolean, wiki = true): Promise<void> {
  await client.put('/api/feature-flags/llm-wiki', { enabled: wiki });
  await client.put('/api/feature-flags/notes-wiki-unified', { enabled: unified });
}

async function closeToasts(page: Page): Promise<void> {
  const closes = page.locator('button[title="Закрыть"]');
  for (let i = await closes.count(); i > 0; i--) {
    try { await closes.first().click({ timeout: 1000 }); } catch { break; }
  }
}

// Кнопки внутри области: «Новая» — по подписи, иконочная дневниковая — по title.
function buttonsIn(scope: Page | Locator) {
  const root = scope;
  return {
    newBtn: root.getByRole('button', { name: 'Новая', exact: true }),
    dailyBtn: root.locator('button[title="Дневниковая заметка на сегодня"]'),
  };
}

// Размеры внешнего бокса обеих кнопок в области.
async function measure(scope: Page | Locator, where: string): Promise<Array<{ who: string; w: number; h: number }>> {
  const { newBtn, dailyBtn } = buttonsIn(scope);
  const out: Array<{ who: string; w: number; h: number }> = [];
  for (const [who, loc] of [['Новая', newBtn], ['Дневниковая заметка', dailyBtn]] as const) {
    await expect(loc.first(), `${where}: кнопка «${who}» есть`).toBeVisible({ timeout: 20000 });
    const box = await loc.first().boundingBox();
    out.push({ who, w: Math.round(box!.width * 10) / 10, h: Math.round(box!.height * 10) / 10 });
  }
  return out;
}

// Мобильный список заметок: раздел «Заметки», режим по умолчанию.
async function openNotesList(page: Page): Promise<void> {
  await page.goto(BASE + '/#/notes');
  await page.reload();
  await page.waitForTimeout(1500);
  await closeToasts(page);
  const nav = page.getByRole('button', { name: 'Заметки', exact: true }).first();
  if (await nav.count()) { await nav.click(); await page.waitForTimeout(1200); }
  await closeToasts(page);
}

// Мобильная шторка дерева: режим «Вики» → кнопка дерева.
async function openDrawerTree(page: Page): Promise<void> {
  await page.getByRole('button', { name: 'Вики', exact: true }).first().click();
  await page.waitForTimeout(600);
  await page.getByTestId('wiki-open-tree').click();
  // id шторки — wiki-drawer-tree; testid у неё wiki-drawer-left
  await expect(page.locator('#wiki-drawer-tree')).toBeVisible({ timeout: 20000 });
}

test.describe.configure({ mode: 'serial' });

test.beforeAll(async ({ playwright }) => {
  request = await playwright.request.newContext({ baseURL: BASE });
  token = await login(request);
  client = api(request, token);
  await setFlags(true);
});

test.afterAll(async () => { await request?.dispose(); });

for (const theme of ['light', 'dark'] as const) {
  test(`360, тема «${theme}»: обе кнопки ≥ 40 px в списке заметок и в шторке дерева`, async ({ browser }) => {
    const ctx = await browser.newContext({ baseURL: BASE, viewport: { width: 360, height: 800 } });
    await ctx.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
    await ctx.addInitScript(t => localStorage.setItem('theme-mode', t as string), theme);
    const page = await ctx.newPage();

    await openNotesList(page);
    expect(await page.evaluate(() => document.documentElement.dataset.theme), `тема «${theme}» применена`).toBe(theme);

    // 1. Мобильный список заметок — сайдбар раздела (шторки на экране нет)
    const list = await measure(page, `360/«${theme}»/список`);

    // 2. Шторка дерева (режим «Вики») — кнопки внутри шторки
    await openDrawerTree(page);
    const drawer = await measure(page.locator('#wiki-drawer-tree'), `360/«${theme}»/шторка`);

    const bad = [...list, ...drawer]
      .filter(m => m.w < MIN || m.h < MIN)
      .map(m => `${m.who}: ${m.w}×${m.h} px`);
    expect(bad, `360/«${theme}»: тач-цели меньше ${MIN} px`).toEqual([]);

    console.log(`MEASURE 360 ${theme} список: ` + JSON.stringify(list));
    console.log(`MEASURE 360 ${theme} шторка: ` + JSON.stringify(drawer));

    await ctx.close();
  });
}

test('1280: размеры кнопок прежние (мобильные значения не применяются)', async ({ browser }) => {
  const ctx = await browser.newContext({ baseURL: BASE, viewport: { width: 1280, height: 900 } });
  await ctx.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
  const page = await ctx.newPage();

  await page.goto(BASE + '/#/notes');
  await page.reload();
  await page.waitForTimeout(1800);
  await closeToasts(page);

  const { newBtn, dailyBtn } = buttonsIn(page);
  await expect(newBtn.first()).toBeVisible({ timeout: 20000 });
  const nb = await newBtn.first().boundingBox();
  const db = await dailyBtn.first().boundingBox();
  const res = {
    Новая: { w: Math.round(nb!.width * 10) / 10, h: Math.round(nb!.height * 10) / 10 },
    Дневниковая: { w: Math.round(db!.width * 10) / 10, h: Math.round(db!.height * 10) / 10 },
  };
  console.log('MEASURE 1280 ' + JSON.stringify(res));
  // База десктопа выводится из стилей newBtn: высота 32 (паддинг 7×2 + строка), иконочная кнопка
  // 16 (иконка) + 12×2 (паддинги) + 2 (рамка) = 42 в ширину. Мобильные 40 не применяются.
  expect(res.Новая.h, 'на 1280 высота «Новой» прежняя 32').toBe(32);
  expect(res.Дневниковая.h, 'на 1280 высота иконочной прежняя 32').toBe(32);
  expect(res.Дневниковая.w, 'на 1280 ширина иконочной прежняя 42').toBe(42);

  await ctx.close();
});
