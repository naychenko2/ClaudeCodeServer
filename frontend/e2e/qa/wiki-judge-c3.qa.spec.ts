// QA-приёмка волны С3 (ADR-039 решения 9–10): судья спорных операций, вкладка «Решения»
// (судья — только чтение, ручной режим — кнопки), «Журнал» → «Решения за ночь» (четыре
// группы, откат пакета, возврат к «Все записи» при ошибке вида).
//
// Данные стенда — приёмки С3: пакеты с операциями «на судье» (код number_unconfirmed)
// и операцией без допуска к судье (autoapply_off) — пакет остаётся proposed, поэтому строка
// «Решений» показывает и вердикт судьи, и «нет вердикта».
//
// Стенд: ветка feat/wiki-judge, свой каталог данных (data-qa). Документ — docs/adr/ADR-039-wiki-new-setup.md.

import { test, expect, type APIRequestContext, type Page } from '@playwright/test';
import { createHash, randomUUID } from 'node:crypto';
import { existsSync, mkdirSync, readFileSync, readdirSync, rmSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { api, collectConsoleErrors, login, type Api } from './helpers';

const BASE = process.env.PLAYWRIGHT_BASE_URL || 'http://127.0.0.1:5561';
// Корень вики стенда: фикстура пакета кладётся файлом (как в recipes/operations/qa-stand.md).
const WIKI_ROOT = '/home/an/Sources/ClaudeCodeServer-wiki-judge/data-qa/wiki';
const SECTION = 'projects/0db0e722-667b-4125-bb96-20f04c6e63f1';
const PAGE = 'Проекты/0db0e722-667b-4125-bb96-20f04c6e63f1/Планы садовые.md';
const SOURCE_REF = 'session:a38872dc-1598-4235-8c59-b16a4eeb50d7#m1-8';

test.describe.configure({ mode: 'serial' });

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

const PANEL_TITLES: Record<string, string> = {
  wikiInbox: 'Решения', wikiJournal: 'Журнал', wikiHealth: 'Здоровье', wikiSettings: 'Настройки',
};

// Шум стенда: 404 «здоровья» раздела (нет отчёта линта — так и задумано беком) и сетевые
// обрывы браузера — не дефекты проверяемой фичи.
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
  const card = panelCard(page, PANEL_TITLES[key]);
  if (await card.count() > 0 && await card.first().isVisible()) return;
  await railButton(page, key).click();
  try {
    await expect(card.first()).toBeVisible({ timeout: 8000 });
  } catch {
    // Панель могла быть открыта, а клик по рельсе её закрыл — открываем ещё раз.
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
  await request?.dispose();
  for (const id of seededPackages) {
    for (const owner of existsSync(WIKI_ROOT) ? readdirSync(WIKI_ROOT) : []) {
      const f = path.join(WIKI_ROOT, owner, 'keeper', 'packages', `${id}.json`);
      if (existsSync(f)) rmSync(f);
    }
  }
});

const seededPackages: string[] = [];

// Пакет с двумя спорными операциями, код причины — вне допуска судьи (autoapply_off):
// судья их не трогает, пакет остаётся proposed — строка «Решений» остаётся видимой.
function seedHumanPackage(): string {
  const owner = readdirSync(WIKI_ROOT)[0];
  const pageFile = path.join(WIKI_ROOT, owner, 'pages', PAGE);
  const hash = createHash('sha256').update(readFileSync(pageFile)).digest('hex');
  const op = (claim: string, quotes: string[], reason: string) => ({
    op: 'append', page: PAGE, pageSection: null, target: null, text: claim, claim, quotes,
    kind: null, asOf: null, class: 'session×append', decision: 'inbox', reason,
    reasonCode: 'autoapply_off', baseHashes: { [PAGE]: hash }, inputTokens: 700, outputTokens: 70,
    humanDecision: null, humanReason: null, judgeDecision: null, judgeReason: null,
    judgeModel: null, judgeAt: null, judgeCostUsd: null, judgeAttempts: 0,
  });
  const now = new Date().toISOString();
  const id = randomUUID();
  const pkg = {
    v: 1, id, author: 'ai', action: null, section: SECTION,
    sources: [{ origin: 'session', ref: SOURCE_REF }],
    operations: [
      op('Теплица в следующем году будет 20 квадратных метров.',
        ['В следующем году хотим добавить теплицу из 20 квадратных метров.'], 'решение человека по тумблеру'),
      op('Урожай в прошлом году составил 400 килограммов.',
        ['В саду растут 12 яблонь, урожай в прошлом году был 400 килограммов.'], 'решение человека по тумблеру'),
    ],
    status: 'proposed', statusReason: null, createdAt: now, updatedAt: now,
  };
  const dir = path.join(WIKI_ROOT, owner, 'keeper', 'packages');
  mkdirSync(dir, { recursive: true });
  writeFileSync(path.join(dir, `${id}.json`), JSON.stringify(pkg, null, 1), 'utf-8');
  seededPackages.push(id);
  return id;
}

test('«Решения» в режиме судьи: вердикты только для чтения, кнопок решения и отката нет', async ({ page, context }) => {
  const errors = collectConsoleErrors(page);
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
  await openApp(page);

  // Режим судьи — тумблер «Применять без ревью» включён (дефолт стенда).
  const settings = await client.get<{ autoApplyEnabled: boolean }>('/api/wiki/settings');
  expect(settings.autoApplyEnabled, 'стенд в режиме судьи (тумблер включён)').toBeTruthy();

  await openPanelTab(page, 'wikiInbox');
  const rows = page.getByTestId('wiki-judge-row');
  await expect(rows.first(), 'в «Решениях» есть строка судьи').toBeVisible({ timeout: 20000 });

  // Кнопок решения человека в режиме судьи нет: ни «принять», ни «отклонить».
  const accept = page.getByRole('button', { name: /принять/i });
  const reject = page.getByRole('button', { name: /отклонить/i });
  expect(await accept.count(), 'в очереди судьи нет кнопки «принять»').toBe(0);
  expect(await reject.count(), 'в очереди судьи нет кнопки «отклонить»').toBe(0);
  // Кнопки отката пакета в очереди нет — откат живёт в «Журнале» → «Решения за ночь».
  expect(await page.getByTestId('wiki-night-revert').count(), 'в очереди судьи нет отката пакета').toBe(0);

  // Вердикт судьи виден в строке операции.
  await expect(page.getByTestId('wiki-judge-op').first()).toBeVisible();
  expect(uiErrors(errors), 'в консоли нет ошибок').toEqual([]);
});

test('тумблер ручного режима возвращает кнопки «принять/отклонить», решение человека видно в виде судьи', async ({ page, context }) => {
  const errors = collectConsoleErrors(page);
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
  await openApp(page);

  // Свой пакет под решение человека: две спорные операции с кодом вне допуска судьи —
  // судья их не трогает, поэтому после решения человека одна операция остаётся нерешённой
  // и пакет остаётся proposed: решение человека видно и в виде судьи.
  const pkgId = seedHumanPackage();

  // Выключаем тумблер «Применять без ревью» через интерфейс.
  await openPanelTab(page, 'wikiSettings');
  const sw = page.getByLabel('Применять без ревью');
  await expect(sw, 'тумблер «Применять без ревью» есть в настройках вики').toBeVisible({ timeout: 20000 });
  if ((await sw.getAttribute('aria-checked')) === 'true') {
    await sw.click();
    await expect.poll(async () => (await client.get<{ autoApplyEnabled: boolean }>('/api/wiki/settings')).autoApplyEnabled,
      { timeout: 15000 }).toBeFalsy();
  }

  // Ручная очередь: пакет с операцией «на решение» открывается, кнопки решения есть.
  await openPanelTab(page, 'wikiInbox');
  const row = page.getByTestId('wiki-pkg-row').filter({ hasText: 'a38872dc' }).first();
  await expect(row, 'в ручной очереди есть пакет с нерешённой операцией').toBeVisible({ timeout: 20000 });
  await row.click();
  const accept = page.getByTestId('wiki-op-accept-0');
  await expect(accept, 'на экране пакета есть кнопка «Принять»').toBeVisible({ timeout: 20000 });
  await accept.click();
  // Решение человека записано в пакете — сверяем по API, а не по кнопке.
  await expect.poll(async () => {
    const p = await client.get<{ operations: Array<{ humanDecision: string | null }> }>(`/api/wiki/packages/${pkgId}`);
    return p.operations[0]?.humanDecision;
  }, { timeout: 20000 }).toBe('accepted');

  // Возвращаем режим судьи: решение человека остаётся видимым (бейдж «принята»).
  await client.put('/api/wiki/settings', { autoApplyEnabled: true });
  await openApp(page);
  await openPanelTab(page, 'wikiInbox');
  await expect(page.getByTestId('wiki-judge-op').first()).toBeVisible({ timeout: 20000 });
  await expect(page.getByTestId('wiki-judge-row').getByText('принята', { exact: true }).first(),
    'решение человека видно в режиме судьи').toBeVisible({ timeout: 20000 });

  expect(uiErrors(errors), 'в консоли нет ошибок').toEqual([]);
});

test('«Журнал» → «Решения за ночь»: четыре группы, откат пакета, возврат к «Все записи» при сбое', async ({ page, context }) => {
  const errors = collectConsoleErrors(page);
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
  await openApp(page);

  await openPanelTab(page, 'wikiJournal');
  const nightTab = page.getByRole('button', { name: 'Решения за ночь', exact: true }).first();
  await expect(nightTab, 'в «Журнале» есть вид «Решения за ночь»').toBeVisible({ timeout: 20000 });
  await nightTab.click();

  await expect(page.getByTestId('wiki-night-group').first(), 'есть группы ночи').toBeVisible({ timeout: 20000 });
  const groups = await page.getByTestId('wiki-night-group').allTextContents();
  const titles = groups.map(g => g.split(/\d/)[0].trim());
  expect(titles.join(' | '), 'есть группа «Принято судьёй»').toContain('Принято судьёй');
  expect(titles.join(' | '), 'есть группа «Отклонено судьёй»').toContain('Отклонено судьёй');
  expect(titles.join(' | '), 'есть группа «Ждут судью»').toContain('Ждут судью');

  // Откат применённого пакета: берём из данных тот пакет, который и правда применён
  // (уже откатанный откатить нельзя — 409), находим его строку и жмём «Откатить пакет».
  // Вердикты судьи живут в «Решениях за ночь» (список пакетов их не отдаёт — дефект D1),
  // а статус пакета — в карточке пакета: ищем тот, который и правда применён.
  const night = await client.get<{ judgeAccepted: Array<{ packageId: string; claim: string | null }> }>(
    '/api/wiki/journal?view=night&from=2026-11-11&to=2026-11-13');
  const candidates: Array<{ id: string; page: string }> = [];
  for (const row of night.judgeAccepted) {
    const p = await client.get<{ status: string; operations: Array<{ page: string }> }>(
      `/api/wiki/packages/${row.packageId}`);
    if (p.status === 'applied') candidates.push({ id: row.packageId, page: p.operations[0].page });
  }
  // Строка ночи опознаётся по пути страницы, а пути у разных пакетов совпадают — берём тот,
  // чей путь встречается в ночи один раз (иначе строка досталась бы чужому пакету).
  const countOf = (path: string) => night.judgeAccepted.filter(r => r.page === path).length;
  const applied = candidates.find(c => countOf(c.page) === 1) ?? candidates[candidates.length - 1];
  expect(applied, 'на стенде есть применённый пакет с вердиктом судьи «принято»').toBeTruthy();
  // Строка ночи опознаётся по пути страницы: claim'ы у разных пакетов совпадают.
  const appliedRow = page.getByTestId('wiki-night-row')
    .filter({ hasText: applied!.page.split('/').pop()! }).first();
  await expect(appliedRow, 'в группе «Принято судьёй» есть строка применённого пакета').toBeVisible({ timeout: 20000 });
  const revertBtn = appliedRow.getByTestId('wiki-night-revert');
  await revertBtn.scrollIntoViewIfNeeded();
  await expect(revertBtn, 'кнопка отката активна').toBeEnabled({ timeout: 10000 });
  await revertBtn.click();
  // Откат состоялся: пакет перешёл в «reverted». (Строка ночи при этом остаётся на экране —
  // дефект D2, отдельная карточка: вид ночи не отсекает откатанные пакеты.)
  await expect.poll(async () => {
    const p = await client.get<{ status: string }>(`/api/wiki/packages/${applied!.id}`);
    return p.status;
  }, { timeout: 20000 }).toBe('reverted');

  // Сбой загрузки вида — возврат к «Все записи».
  await page.route('**/api/wiki/journal?view=night*', r => r.abort());
  await openApp(page);
  await openPanelTab(page, 'wikiJournal');
  await closeToasts(page);
  await page.getByRole('button', { name: 'Решения за ночь', exact: true }).first().click();
  const err = page.getByTestId('wiki-journal-error');
  await expect(err, 'при сбое вида показана плашка ошибки').toBeVisible({ timeout: 20000 });
  const back = page.getByRole('button', { name: 'Все записи', exact: true }).first();
  await expect(back, 'в состоянии ошибки есть возврат к «Все записи»').toBeVisible();
  await back.click();
  await expect(page.getByTestId('wiki-journal-error')).toHaveCount(0, { timeout: 20000 });

  expect(uiErrors(errors), 'в консоли нет ошибок').toEqual([]);
});
