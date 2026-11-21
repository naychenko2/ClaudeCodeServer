// QA-4 · приёмка на стенде: ночь со слияниями без самоблокировки и живая база K1
// (задача a39e9339, ветка feat/wiki-merge-gate).
//
// Сценарии:
//   q5 — карточка merge-пакета во «Входящих» («Решения»): бейдж «слить страницы» и строка «A → B»;
//   q6 — регресс: блок «По теме» на странице показывает соседей, и они кликабельны.
//
// Данные стенда — свои (data-qa вики владельца admin): пакет merge b9bf9a78 висит
// в «Решениях» (судья его ещё не решала). Код продукта не трогается.

import { test, expect, type APIRequestContext, type Page } from '@playwright/test';
import { existsSync, mkdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { createHash, randomUUID } from 'node:crypto';
import path from 'node:path';
import { api, collectConsoleErrors, login, type Api } from './helpers';

const BASE = process.env.PLAYWRIGHT_BASE_URL || 'http://127.0.0.1:5562';
const ROOT = path.resolve(new URL('../../..', import.meta.url).pathname);
const SHOTS = path.join(ROOT, '.cc-attachments', 'wiki-qa-merge-gate');
mkdirSync(SHOTS, { recursive: true });

// Вики стенда QA-4 (worktree feat/wiki-merge-gate, свой каталог данных data-qa).
const WIKI_ROOT = '/home/an/Sources/ClaudeCodeServer-wiki-merge/data-qa/wiki';
const SECTION = 'projects/9f8bc22f-1092-44db-81a4-e212517ed6a1';
const PAGE_A = 'Проекты/9f8bc22f-1092-44db-81a4-e212517ed6a1/Регламент футоскопии документов каталога.md';
const PAGE_B = 'Проекты/9f8bc22f-1092-44db-81a4-e212517ed6a1/Регламент футоскопии сводок каталога.md';
const seededPackages: string[] = [];

test.describe.configure({ mode: 'serial' });
test.setTimeout(120_000);

let request: APIRequestContext;
let token: string;
let client: Api;

function railButton(page: Page, key: string) {
  return page.locator(`[data-rail-item="${key}"] button`);
}

function panelCard(page: Page, title: string) {
  return page.locator(
    `xpath=//span[contains(@style,"font-weight: 600") and normalize-space()="${title}"]` +
    `/ancestor::div[contains(@style,"flex-direction: column")][1]`);
}

function uiErrors(sink: string[]): string[] {
  return sink.filter(t => !t.includes('net::ERR_') && !t.includes('Failed to load resource')
    && !t.includes('404 (Not Found)') && !t.includes('Failed to fetch dynamically imported module'));
}

async function closeToasts(page: Page): Promise<void> {
  const closes = page.locator('button[title="Закрыть"]');
  for (let i = await closes.count(); i > 0; i--) {
    try { await closes.first().click({ timeout: 1000 }); } catch { break; }
  }
}

async function openApp(page: Page): Promise<void> {
  await page.goto(BASE + '/#/notes');
  await page.reload();
  await page.waitForTimeout(1500);
  await closeToasts(page);
  const inSection = await page.getByRole('button', { name: 'Вики', exact: true }).count()
    + await page.getByTestId('unified-filter').count();
  if (inSection === 0) {
    const nav = page.getByRole('button', { name: 'Заметки', exact: true }).first();
    if (await nav.count()) { await nav.click(); await page.waitForTimeout(1500); }
  }
}

async function openPanelTab(page: Page, key: string): Promise<void> {
  const card = panelCard(page, key === 'wikiInbox' ? 'Решения' : 'Журнал');
  if (await card.count() > 0 && await card.first().isVisible()) return;
  await railButton(page, key).click();
  try {
    await expect(card.first()).toBeVisible({ timeout: 8000 });
  } catch {
    await railButton(page, key).click();
    await expect(card.first()).toBeVisible({ timeout: 20000 });
  }
}

test.beforeAll(async ({ playwright, baseURL }) => {
  request = await playwright.request.newContext({ baseURL: baseURL || BASE });
  token = await login(request);
  client = api(request, token);
});

test.afterAll(async () => {
  // Уборка своих данных: пакет — свой, файл удаляем.
  for (const id of seededPackages) {
    const dir = path.join(WIKI_ROOT, ownerId, 'keeper', 'packages');
    if (existsSync(dir)) rmSync(path.join(dir, `${id}.json`), { force: true });
  }
  await request?.dispose();
});

// Владелец стендовой вики — из токена (sub).
let ownerId = '';
function owner(): string {
  if (!ownerId) {
    ownerId = JSON.parse(Buffer.from(token.split('.')[1], 'base64url').toString()).sub as string;
  }
  return ownerId;
}

// Пакет merge «во Входящих»: операция решения inbox, кода merge_proposal, без вердикта судьи.
// Посев файлом — так же, как в прецеденте wiki-judge-c3 (docs/operations/qa-stand.md).
function seedMergePackage(): string {
  const dir = path.join(WIKI_ROOT, owner(), 'keeper', 'packages');
  mkdirSync(dir, { recursive: true });
  const fileA = path.join(WIKI_ROOT, owner(), 'pages', PAGE_A);
  const fileB = path.join(WIKI_ROOT, owner(), 'pages', PAGE_B);
  const hash = (f: string) => createHash('sha256').update(readFileSync(f)).digest('hex');
  const now = new Date().toISOString();
  const id = randomUUID();
  const pkg = {
    v: 1, id, author: 'ai', action: null, section: SECTION,
    sources: [{ origin: 'session', ref: `merge:${PAGE_A}` }],
    operations: [{
      op: 'merge', page: PAGE_A, pageSection: null, target: PAGE_B, text: null, claim: null,
      quotes: null, kind: null, asOf: null, class: 'session×merge', decision: 'inbox',
      reason: `предложение слить «${PAGE_A}» в «${PAGE_B}»: дубль по заголовкам или мелкая страница по теме`,
      reasonCode: 'merge_proposal', strippedLinks: null,
      baseHashes: { [PAGE_A]: hash(fileA), [PAGE_B]: hash(fileB) },
      inputTokens: null, outputTokens: null, humanDecision: null, humanReason: null,
      judgeDecision: null, judgeReason: null, judgeModel: null, judgeAt: null, judgeCostUsd: null,
      judgeAttempts: 0,
    }],
    status: 'proposed', statusReason: null, createdAt: now, updatedAt: now,
  };
  writeFileSync(path.join(dir, `${id}.json`), JSON.stringify(pkg, null, 1), 'utf-8');
  seededPackages.push(id);
  return id;
}

// Панель «Решения» показывает пакеты ТЕКУЩЕГО раздела: открываем страницу раздела «Общее»
// (merge-пакет ночи лежит в common), иначе панель смотрит в проект.
async function openCommonPage(page: Page, title: string): Promise<void> {
  const row = page.getByTestId('unified-row').filter({ hasText: title }).first();
  await expect(row, `в дереве вики есть страница «${title}»`).toBeVisible({ timeout: 20000 });
  await row.click();
  await page.waitForTimeout(1800);
}

test('q5 · «Решения»: карточка merge-пакета показывает «слить страницы» и «A → B»', async ({ page, context }) => {
  const errors = collectConsoleErrors(page);
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);

  // Ручной режим вики (тумблер «Применять неспорное само» выключен): в нём очередь
  // показывает карточку пакета с операциями — в режиме судьи панель показывает только
  // вердикты, а нерешённого merge-пакета среди них нет.
  await client.put('/api/wiki/settings', { autoApplyEnabled: false });
  try {
    // Свой пакет merge во «Входящих» (в ручном режиме судья не решает — пакет ждёт человека).
    seedMergePackage();
    await openApp(page);
    await openCommonPage(page, 'Регламент футоскопии сводок каталога');

    await openPanelTab(page, 'wikiInbox');
    const card = panelCard(page, 'Решения');
    await expect(card.first(), 'панель «Решения» открылась').toBeVisible({ timeout: 20000 });

    // Строка пакета ночи (merge) в очереди решений.
    const pkgRow = card.first().getByTestId('wiki-pkg-row')
      .or(card.first().getByTestId('wiki-judge-row'))
      .filter({ hasText: 'футоскопии' }).first();
    await expect(pkgRow, 'в «Решениях» есть строка merge-пакета').toBeVisible({ timeout: 20000 });
    await pkgRow.click();
    await page.waitForTimeout(1000);

    // Merge-пакет ночи: бейдж операции и строка «A → B».
    const badge = card.first().getByText('слить страницы', { exact: true });
    await expect(badge, 'в «Решениях» есть операция merge с бейджем «слить страницы»').toBeVisible({ timeout: 20000 });

    const mergeRow = card.first().locator('[data-testid="wiki-op-merge"]').first();
    await expect(mergeRow, 'в карточке пакета есть строка операции merge').toBeVisible({ timeout: 20000 });
    const mergeText = (await mergeRow.innerText()).replace(/\s+/g, ' ').trim();
    console.log('QA4 merge-строка:', mergeText);
    expect(mergeText, 'строка merge содержит «A → B» со стрелкой').toContain('→');
    expect(mergeText, 'в строке merge видно обе стороны (A и B)').toMatch(/Регламент футоскопии документов каталога.*Регламент футоскопии сводок каталога/);

    await page.screenshot({ path: `${SHOTS}/q5-inbox-merge-card.png`, fullPage: false });
  } finally {
    await client.put('/api/wiki/settings', { autoApplyEnabled: true });
  }
  expect(uiErrors(errors), 'в консоли нет ошибок').toEqual([]);
});

test('q6 · «По теме» на странице: соседи показаны и кликабельны', async ({ page, context }) => {
  const errors = collectConsoleErrors(page);
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
  await openApp(page);

  await openCommonPage(page, 'Регламент футоскопии документов каталога');

  const topicHeading = page.getByText('По теме', { exact: true }).first();
  await expect(topicHeading, 'на странице есть блок «По теме»').toBeVisible({ timeout: 20000 });

  const items = page.getByTestId('wiki-related-item');
  const count = await items.count();
  console.log('QA4 «По теме»: соседей =', count);
  expect(count, 'в блоке «По теме» есть соседи').toBeGreaterThan(0);

  const first = items.first();
  await expect(first, 'сосед из блока «По теме» виден').toBeVisible();
  await expect(first, 'сосед кликабелен (aria-disabled=false)').toHaveAttribute('aria-disabled', 'false');
  const neighbourName = (await first.innerText()).split('\n')[0].trim();
  console.log('QA4 сосед по теме:', neighbourName);
  await page.screenshot({ path: `${SHOTS}/q6-topic-related.png`, fullPage: false });
  await first.click();
  await page.waitForTimeout(2000);

  // Клик открыл соседнюю страницу: заголовок страницы (h2) сменился.
  const heading = page.getByRole('heading', { level: 2 }).first();
  await expect(heading, 'после клика открыта страница соседа').toBeVisible({ timeout: 20000 });
  const headingText = (await heading.innerText()).trim();
  console.log('QA4 открытая страница:', headingText);
  expect(headingText, 'клик по соседу «По теме» открыл именно страницу соседа').toBe(neighbourName);

  expect(uiErrors(errors), 'в консоли нет ошибок').toEqual([]);
});
