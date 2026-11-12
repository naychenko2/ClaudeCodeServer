// QA-2 · повтор по дефектам приёмки b0065470 (задача 3c7bfe87, фича 66b5e075, ветка feat/wiki-judge).
//
// 1. 67cd64d0 — «Решения» в режиме судьи: бейдж вердикта и «почему».
// 2. b58d81c0 + 76f4c7d6 — «Откатить пакет» только у применённого; пометки «откачен» / «не применён».
// 3. cd900a6f — итог чистки не теряется при закрытии окна (Esc).
// 4. 682d18f0 — green build: проверяется вне спека (npm run build, этап build:notes).
// 5. Тексты: «<$0.01», «Отклонено кодом: C» отдельной строкой, «Ждут решения»,
//    рельс «N ждут решения», aria-label тумблера = подпись.
//
// Данные — свои (data-qa этого дерева): чат с историей, страница, пакеты — файлами.
// Вердикты судьи — поля операций: «Решения за ночь» — чистая проекция keeper/packages
// (WikiJournalReader.NightDecisions), поэтому живой ночной проход и вызов модели здесь не нужны.
// Живые ручки при этом настоящие: accept-all → применение (порождает «applied», прообразы и
// запись применения) → revert; чистка — настоящим POST /api/wiki/purge по своим файлам.

import { test, expect, type APIRequestContext, type Page } from '@playwright/test';
import { randomUUID } from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';
import { api, collectConsoleErrors, login, seed, type Api } from './helpers';

const ROOT = path.resolve(new URL('../../..', import.meta.url).pathname);
const DATA = (() => {
  const cfg = JSON.parse(fs.readFileSync(path.join(ROOT, 'backend', 'ClaudeHomeServer', 'appsettings.Local.json'), 'utf8')) as { DataPath?: string };
  expect(cfg.DataPath, 'стенд обязан стоять на своём каталоге данных (appsettings.Local.json: DataPath)').toBeTruthy();
  return path.dirname(path.resolve(ROOT, cfg.DataPath!));
})();
const SHOTS = path.join(ROOT, '.cc-attachments', 'qa-c3r');
const STAMP = Date.now();
const PREFIX = 'qa-c3r-';

test.describe.configure({ mode: 'serial' });
test.setTimeout(180_000);

let request: APIRequestContext;
let token: string;
let client: Api;
let seeded: ReturnType<typeof seed>;
let ownerId = '';
let projectId = '';
let chatId = '';
let wikiRoot = '';
const sectionKey = () => `projects/${projectId}`;
// У каждого пакета своя страница: и строка «Решений», и строка ночи опознаются по пути страницы.
const pageOf = (kind: string) => `Проекты/${projectId}/Планы ${kind}.md`;
// Маркер пакета в тексте claim'а: по нему опознаём строку ночи.
const claim = (kind: string) => `Повтор ${kind} ${STAMP}: урожай в прошлом году составил 400 килограммов.`;

const nowIso = (hoursAgo = 0) => new Date(Date.now() - hoursAgo * 3_600_000).toISOString();

function railButton(page: Page, key: string) {
  return page.locator(`[data-rail-item="${key}"] button`);
}

function panelCard(page: Page, title: string) {
  return page.locator(
    `xpath=//span[contains(@style,"font-weight: 600") and normalize-space()="${title}"]` +
    `/ancestor::div[contains(@style,"flex-direction: column")][1]`);
}

const PANEL_TITLES: Record<string, string> = {
  wikiInbox: 'Решения', wikiJournal: 'Журнал', wikiSettings: 'Настройки',
};

async function closeToasts(page: Page): Promise<void> {
  const closes = page.locator('button[title="Закрыть"]');
  for (let i = await closes.count(); i > 0; i--) {
    try { await closes.first().click({ timeout: 1000 }); } catch { break; }
  }
}

async function openApp(page: Page): Promise<void> {
  // Reload здесь не нужен и вреден: на свежей загрузке приложение не держит #/notes
  // (гейт MF-remote, App.tsx:535) и после перезагрузки возвращается на «Чаты».
  await page.goto('/#/notes');
  await closeToasts(page);
  const rail = page.locator('[data-rail-item="wikiInbox"]');
  // Первая загрузка после холодного старта стенда медленная (MF-remote «Заметок»
  // регистрируется до первого рендера) — ждём рельсу терпеливо, при нужде идём кликом.
  for (let attempt = 0; attempt < 10; attempt++) {
    if (await rail.count()) return;
    const nav = page.getByRole('button', { name: 'Заметки', exact: true }).first();
    if (await nav.count()) await nav.click();
    await page.waitForTimeout(3000);
  }
  expect(await rail.count(), 'раздел «Заметки» с рельсой вики открыт').toBeGreaterThan(0);
}

async function openPanelTab(page: Page, key: string): Promise<void> {
  const card = panelCard(page, PANEL_TITLES[key]);
  if (!(await card.count() > 0 && await card.first().isVisible())) {
    await railButton(page, key).click();
    try {
      await expect(card.first()).toBeVisible({ timeout: 8000 });
    } catch {
      await railButton(page, key).click();
      await expect(card.first()).toBeVisible({ timeout: 20000 });
    }
  }
  // Курсор над рельсой поднимает её флаут («Скрыть «Журнал»»), он перехватывает клики
  // по вкладкам панели — уводим курсор в панель.
  await page.mouse.move(700, 500);
  await page.waitForTimeout(300);
  await scopeAllSections(page);
}

// Панель вики помнит раздел, выбранный раньше (чип в её шапке). Данные прогона —
// в своём разделе, поэтому область панели ставим явно: «Все разделы».
async function scopeAllSections(page: Page): Promise<void> {
  const chip = page.locator('button[title="Раздел вики, по которому панель показывает данные"]').first();
  if (!(await chip.count())) return;
  if ((await chip.textContent() ?? '').includes('Все разделы')) return;
  await chip.click();
  await page.getByRole('button', { name: 'Все разделы', exact: true }).first().click();
  await page.waitForTimeout(500);
}

function uiErrors(sink: string[]): string[] {
  return sink.filter(t => !t.includes('net::ERR_') && !t.includes('Failed to load resource')
    && !t.includes('404 (Not Found)') && !t.includes('Failed to fetch dynamically imported module'));
}

// Пакет автора ai со спорными операциями: вердикты судьи — поля операции (тот же контракт,
// что пишет проход судьи через WikiPackageStore.JudgeOperations).
type OpSeed = {
  claimKind: string; reasonCode: string;
  judge?: { decision: 'accepted' | 'rejected'; model: string; reason: string } | null;
};

// Страницу пишет Хранитель (POST /api/wiki/pages/edit): своя запись файлом на диске не
// входит в манифест раздела, и применение пакета на ней отказывает («файл на диске, но не в
// манифесте»). hash ответа — тот, что потом сверяется с baseHashes операции.
async function writePage(pageKey: string, content: string): Promise<string> {
  const r = await client.post<{ status: string; hash: string }>('/api/wiki/pages/edit', {
    section: sectionKey(), page: pageKey, content,
  });
  return r.hash;
}

function seedPackage(id: string, kind: string, hash: string, ops: OpSeed[], status: string): void {
  const page = pageOf(kind);
  const op = (o: OpSeed) => ({
    op: 'append', page, pageSection: null, target: null,
    text: claim(o.claimKind), claim: claim(o.claimKind),
    quotes: ['В саду растут 12 яблонь, урожай в прошлом году был 400 килограммов.'],
    kind: null, asOf: null, class: 'session×append', decision: 'inbox',
    reason: 'число не подтверждено источником', reasonCode: o.reasonCode,
    baseHashes: { [page]: hash }, inputTokens: 700, outputTokens: 70,
    humanDecision: null, humanReason: null,
    judgeDecision: o.judge?.decision ?? null, judgeReason: o.judge?.reason ?? null,
    judgeModel: o.judge?.model ?? null,
    judgeAt: o.judge ? nowIso(1) : null, judgeCostUsd: o.judge ? 0.0065 : null, judgeAttempts: o.judge ? 1 : 0,
  });
  const pkg = {
    v: 1, id, author: 'ai', action: null, section: sectionKey(),
    sources: [{ origin: 'session', ref: `session:${chatId}#m1-2` }],
    operations: ops.map(op),
    status, statusReason: null, createdAt: nowIso(2), updatedAt: nowIso(1),
  };
  const dir = path.join(wikiRoot, 'keeper', 'packages');
  fs.mkdirSync(dir, { recursive: true });
  fs.writeFileSync(path.join(dir, `${id}.json`), JSON.stringify(pkg, null, 1), 'utf-8');
}

const P_ACCEPT = randomUUID(); // вердикты судьи + нерешённая операция → «proposed»
const P_APPLY = randomUUID();  // вердикт судьи + операция без решения → применится через accept-all
const P_FAILED = randomUUID(); // принят судьёй, но не применился → «apply_failed»
const P_CODE = randomUUID();   // отказ вынесен кодом без модели + нерешённая операция

test.beforeAll(async ({ playwright, baseURL }) => {
  request = await playwright.request.newContext({ baseURL });
  token = await login(request);
  client = api(request, token);
  seeded = seed(client);
  fs.mkdirSync(SHOTS, { recursive: true });

  await client.put('/api/feature-flags/llm-wiki', { enabled: true });
  const project = await seeded.project(`${PREFIX}p-${STAMP}`);
  projectId = project.id;
  chatId = (await seeded.session(projectId, `${PREFIX}chat-${STAMP}`)).id;
  ownerId = (await client.get<{ userId: string }>('/api/auth/me')).userId;
  wikiRoot = path.join(DATA, 'wiki', ownerId);

  // История чата — файлом: источник пакета должен существовать, иначе цитата не выводима.
  const dir = path.join(DATA, 'sessions', chatId);
  fs.mkdirSync(dir, { recursive: true });
  fs.writeFileSync(path.join(dir, 'history.json'), JSON.stringify([
    { kind: 'user_message', timestamp: Date.now() - 60_000, text: 'В саду растут 12 яблонь, урожай в прошлом году был 400 килограммов.' },
    { kind: 'text', timestamp: Date.now() - 50_000, text: 'Запишу в базу.' },
  ]), 'utf-8');

  await client.post('/api/wiki/initialize', { section: sectionKey() });

  // Страницы, к которым идут операции пакетов: по одной на пакет, записанные Хранителем.
  const hashOf: Record<string, string> = {};
  for (const kind of ['садовые', 'теплицы', 'полива', 'дорожек']) {
    // Шапка страницы — обязательна: type из разрешённых (WikiFrontmatter.Parse).
    // Шапка и четыре секции — контракт страницы (WikiPage.SectionNames); строки в секциях
    // обязаны заканчиваться сноской, поэтому здесь секции пустые: их наполнит применение.
    hashOf[kind] = await writePage(pageOf(kind),
      `---\ntype: topic\n---\n\n# Планы ${kind}\n\n## Сводка\n\n## Хронология\n\n## Противоречия\n\n## История\n`);
  }

  seedPackage(P_ACCEPT, 'садовые', hashOf['садовые'], [
    { claimKind: 'принято', reasonCode: 'number_unconfirmed', judge: { decision: 'accepted', model: 'qwen3.8-27b', reason: 'число совпадает с источником' } },
    { claimKind: 'отклонено', reasonCode: 'number_unconfirmed', judge: { decision: 'rejected', model: 'qwen3.8-27b', reason: 'число расходится с другой формулировкой источника' } },
    { claimKind: 'ждёт', reasonCode: 'number_unconfirmed' },
  ], 'proposed');
  // Применённый пакет: состояние «applied» засеяно вместе с тем, чем живой откат меряется, —
  // прообразом страницы и записью состояния применения (WikiApplyService.PreimageRel /
  // AppliedRecordRel). Сам вызов применения здесь не играется: для него нужна карточка
  // источника, а её человек ручкой правки страницы не создаёт.
  seedPackage(P_APPLY, 'теплицы', hashOf['теплицы'], [
    { claimKind: 'применён', reasonCode: 'number_unconfirmed', judge: { decision: 'accepted', model: 'qwen3.8-27b', reason: 'число совпадает с источником' } },
  ], 'applied');
  const applyPage = pageOf('теплицы');
  const beforeBytes = fs.readFileSync(path.join(wikiRoot, 'pages', applyPage));
  fs.mkdirSync(path.join(wikiRoot, 'keeper', 'preimages', P_APPLY, `Проекты/${projectId}`), { recursive: true });
  fs.writeFileSync(path.join(wikiRoot, 'keeper', 'preimages', P_APPLY, applyPage), beforeBytes);
  fs.mkdirSync(path.join(wikiRoot, 'keeper', 'applied'), { recursive: true });
  fs.writeFileSync(path.join(wikiRoot, 'keeper', 'applied', `${P_APPLY}.json`), JSON.stringify({ [applyPage]: hashOf['теплицы'] }));
  seedPackage(P_FAILED, 'полива', hashOf['полива'], [
    { claimKind: 'неприменён', reasonCode: 'number_unconfirmed', judge: { decision: 'accepted', model: 'qwen3.8-27b', reason: 'число совпадает с источником' } },
  ], 'apply_failed');
  seedPackage(P_CODE, 'дорожек', hashOf['дорожек'], [
    { claimKind: 'кодом', reasonCode: 'unknown_op', judge: { decision: 'rejected', model: 'code', reason: 'операция вне контракта' } },
    { claimKind: 'кодом ждёт', reasonCode: 'number_unconfirmed' },
  ], 'proposed');

  // Итог ночи, в которой работал только судья (J-J4): карточка и её цена — предмет проверки 5.
  fs.writeFileSync(path.join(wikiRoot, 'keeper', 'nightly-last.json'), JSON.stringify({
    date: '2026-11-12', selected: 0, analyzed: 0, applied: 0, toInbox: 0, estTokens: 0, outcome: 'completed',
    inputTokens: 0, manual: true,
    judgeCalls: 1, judgeCostUsd: 0.0065, judgeApplied: 1, judgeRejected: 1, judgeDeferred: 0, codeRejected: 2,
  }, null, 1), 'utf-8');
});

test.afterAll(async () => {
  await request?.dispose();
  await seeded?.cleanup();
  // Свои файлы вики: раздел этого проекта и его пакеты (чистка сносит дерево целиком).
  if (wikiRoot) {
    // Вместе с файлами — и запись раздела в манифесте: оставить хэш несуществующей страницы
    // значит оставить раздел в дрейфе для следующего прогона.
    fs.rmSync(path.join(wikiRoot, 'pages', `Проекты/${projectId}`), { recursive: true, force: true });
    fs.rmSync(path.join(wikiRoot, 'keeper', 'manifest', `projects/${projectId}.json`), { force: true });
    fs.rmSync(path.join(wikiRoot, 'keeper', 'preimages'), { recursive: true, force: true });
    fs.rmSync(path.join(wikiRoot, 'keeper', 'applied'), { recursive: true, force: true });
    // Пакеты этого прогона — и те, что породила запись страниц ручкой Хранителя.
    fs.rmSync(path.join(wikiRoot, 'keeper', 'packages'), { recursive: true, force: true });
  }
});

test('1. «Решения» в режиме судьи: бейдж вердикта и «почему» (67cd64d0)', async ({ page, context }) => {
  const errors = collectConsoleErrors(page);
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
  const settings = await client.get<{ autoApplyEnabled: boolean }>('/api/wiki/settings');
  expect(settings.autoApplyEnabled, 'стенд в режиме судьи').toBeTruthy();

  await openApp(page);
  await openPanelTab(page, 'wikiInbox');
  const row = page.getByTestId('wiki-judge-row').filter({ hasText: 'Планы садовые.md' }).first();
  await expect(row, 'в «Решениях» есть строка пакета с вердиктами судьи').toBeVisible({ timeout: 20000 });

  await expect(row.getByText('принята судьёй', { exact: true }), 'бейдж принятого вердикта').toBeVisible();
  await expect(row.getByText('отклонена судьёй', { exact: true }), 'бейдж отклонённого вердикта').toBeVisible();
  await expect(row.getByText('вердикта ещё нет', { exact: true }), 'нерешённая операция помечена честно').toBeVisible();
  await expect(row.getByText('число совпадает с источником'), '«почему» принятого').toBeVisible();
  await expect(row.getByText('число расходится с другой формулировкой источника'), '«почему» отклонённого').toBeVisible();
  await expect(row.getByText('Почему судья решил:', { exact: true }).first(), 'подпись «почему» есть').toBeVisible();
  // Кнопок решения человека в режиме судьи нет.
  expect(await page.getByRole('button', { name: /принять/i }).count(), 'кнопок решения в режиме судьи нет').toBe(0);

  await page.screenshot({ path: path.join(SHOTS, 'p1-judge-rows.png'), fullPage: true });
  expect(uiErrors(errors), 'в консоли нет ошибок').toEqual([]);
});

test('2. Рельс «Решений» в режиме судьи: «N ждут решения» (тексты J-F1)', async ({ page, context }) => {
  const errors = collectConsoleErrors(page);
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
  await openApp(page);
  const aria = (await railButton(page, 'wikiInbox').getAttribute('aria-label')) ?? '';
  expect(aria, 'рельс называет ожидание решения без «вашего»').toContain('ждут решения');
  expect(aria, 'в режиме судьи «вашего» не обещается').not.toContain('вашего');
  expect(uiErrors(errors), 'в консоли нет ошибок').toEqual([]);
});

test('3. «Журнал» → «Решения за ночь»: кнопка только у применённого, пометки статуса (b58d81c0, 76f4c7d6)', async ({ page, context }) => {
  const errors = collectConsoleErrors(page);
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
  await openApp(page);
  await openPanelTab(page, 'wikiJournal');
  await page.getByRole('button', { name: 'Решения за ночь', exact: true }).first().click();

  const groups = page.getByTestId('wiki-night-group');
  await expect(groups.first(), 'есть группы ночи').toBeVisible({ timeout: 20000 });
  const titles = (await groups.allTextContents()).map(t => t.split(/\d/)[0].trim());
  for (const t of ['Принято судьёй', 'Отклонено судьёй', 'Отклонено кодом', 'Ждут решения']) {
    expect(titles.join(' | '), `есть группа «${t}»`).toContain(t);
  }

  await openApp(page);
  await openPanelTab(page, 'wikiJournal');
  await page.getByRole('button', { name: 'Решения за ночь', exact: true }).first().click();

  const group = (title: string) => groups.filter({ hasText: title }).first();
  const rowIn = async (title: string, kind: string) =>
    group(title).getByTestId('wiki-night-row').filter({ hasText: `Повтор ${kind}` }).first();

  const appliedRow = await rowIn('Принято судьёй', 'применён');
  await expect(appliedRow, 'строка применённого пакета в «Принято судьёй»').toBeVisible({ timeout: 20000 });
  await expect(appliedRow.getByTestId('wiki-night-revert'), 'у применённого пакета кнопка отката есть').toBeVisible();
  await expect(appliedRow.getByText('почему:', { exact: true }), '«почему» судьи в строке ночи').toBeVisible();

  // Принятый судьёй, но не применённый: пометка статуса вместо кнопки.
  const failedRow = await rowIn('Принято судьёй', 'неприменён');
  await expect(failedRow.getByText('не применён', { exact: true }), 'пометка «не применён»').toBeVisible();
  await expect(failedRow.getByTestId('wiki-night-revert'), 'кнопки отката у не применённого нет').toHaveCount(0);

  // Оставленный вердиктом в своей группе: очередь не имеет права отката.
  const waitingRow = await rowIn('Ждут решения', 'ждёт');
  await expect(waitingRow.getByTestId('wiki-night-revert'), 'у строки очереди кнопки нет').toHaveCount(0);
  const codeRow = await rowIn('Отклонено кодом', 'кодом');
  await expect(codeRow.getByTestId('wiki-night-revert'), 'у отказа кода кнопки отката нет').toHaveCount(0);

  await page.screenshot({ path: path.join(SHOTS, 'p3-night-groups.png'), fullPage: true });

  // Откат — настоящий вызов по настоящим прообразам; строка остаётся в группе с пометкой.
  const btn = appliedRow.getByTestId('wiki-night-revert');
  await btn.scrollIntoViewIfNeeded();
  await btn.click();
  await expect.poll(async () => (await client.get<{ status: string }>(`/api/wiki/packages/${P_APPLY}`)).status,
    { timeout: 20000 }).toBe('reverted');
  await expect(appliedRow.getByText('откачен', { exact: true }), 'после отката строка помечена «откачен»')
    .toBeVisible({ timeout: 20000 });
  await expect(appliedRow.getByTestId('wiki-night-revert'), 'кнопки после отката нет').toHaveCount(0);
  await page.screenshot({ path: path.join(SHOTS, 'p3-after-revert.png'), fullPage: true });

  expect(uiErrors(errors), 'в консоли нет ошибок').toEqual([]);
});

test('5. Тексты: цена «<$0.01», «Отклонено кодом» отдельной строкой, aria-label тумблера = подпись', async ({ page, context }) => {
  const errors = collectConsoleErrors(page);
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
  await openApp(page);
  await openPanelTab(page, 'wikiSettings');

  await expect(page.getByText('Судья: принял 1, отклонил 1; вызовов 1, <$0.01', { exact: true }),
    'цена меньше цента не округляется вверх').toBeVisible({ timeout: 20000 });
  const codeLine = page.getByText('Отклонено кодом: 2', { exact: true });
  await expect(codeLine, 'кодовые отказы — отдельная строка').toBeVisible();
  const judgeLine = page.getByText(/Судья: принял 1, отклонил 1/);
  expect((await judgeLine.textContent())!.includes('Отклонено кодом'),
    'кодовые числа не подмешаны в строку судья').toBe(false);

  const sw = page.getByLabel('Применять неспорное само');
  await expect(sw, 'тумблер по имени = видимой подписи').toBeVisible();
  await expect(sw, 'aria-label тумблера равен подписи').toHaveAttribute('aria-label', 'Применять неспорное само');
  await expect(page.getByText('Применять неспорное само', { exact: true }).first(), 'видимая подпись есть').toBeVisible();

  await page.screenshot({ path: path.join(SHOTS, 'p5-settings-texts.png'), fullPage: true });
  expect(uiErrors(errors), 'в консоли нет ошибок').toEqual([]);
});

test('3a. Чистка с закрытием окна по Esc: итог под кнопкой (cd900a6f)', async ({ page, context }) => {
  const errors = collectConsoleErrors(page);
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
  // Чистку тормозим намеренно: «сразу Esc» должен попасть в момент, когда запрос ещё идёт.
  await page.route('**/api/wiki/purge*', async route => { await new Promise(r => setTimeout(r, 1200)); await route.continue(); });
  await openApp(page);
  await openPanelTab(page, 'wikiSettings');

  await page.getByTestId('wiki-purge-open').click();
  const confirm = page.getByRole('button', { name: /^Удалить [0-9]/ });
  await expect(confirm, 'подтверждение с числом составов активно').toBeEnabled({ timeout: 20000 });
  const n = Number((await confirm.textContent())!.match(/\d+/)![0]);
  expect(n, 'состав чистки nonzero').toBeGreaterThan(0);
  await confirm.click();
  await page.keyboard.press('Escape');

  const status = page.getByTestId('wiki-purge-status');
  await expect(status.getByText('Чистка выполняется…'), 'пока чистка идёт, под кнопкой сказано об этом')
    .toBeVisible({ timeout: 5000 });
  await expect(status.getByText(/^Удалено /), 'после ответа — число удалённых').toBeVisible({ timeout: 30000 });
  const archive = status.getByText(/Архив:/);
  await expect(archive, 'путь архива под кнопкой').toBeVisible();
  const archivePath = (await archive.textContent())!.replace('Архив:', '').trim();
  expect(fs.existsSync(path.join(wikiRoot, archivePath)), `архив на диске (${archivePath})`).toBeTruthy();
  await status.scrollIntoViewIfNeeded();
  await page.waitForTimeout(300);
  await page.screenshot({ path: path.join(SHOTS, 'p3a-purge-after-esc.png') });
  expect(uiErrors(errors), 'в консоли нет ошибок').toEqual([]);
});

test('3b. Наблюдение: при открытом окне итог чистки виден дважды', async ({ page, context }) => {
  const errors = collectConsoleErrors(page);
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
  // После первой чистки состава нет — дописываем ИИ-страницы (ручкой Хранителя) и чистим снова.
  for (const kind of ['догляд', 'замечания']) {
    await writePage(pageOf(kind),
      `---\ntype: topic\n---\n\n# Планы ${kind}\n\n## Сводка\n\n## Хронология\n\n## Противоречия\n\n## История\n`);
  }
  await openApp(page);
  await openPanelTab(page, 'wikiSettings');
  await page.getByTestId('wiki-purge-open').click();
  const confirm = page.getByRole('button', { name: /^Удалить [0-9]/ });
  await expect(confirm, 'после засеве снова есть состав').toBeEnabled({ timeout: 20000 });
  await confirm.click();
  const inModal = page.getByText(/^Удалено /);
  await expect(inModal.first(), 'итог в окне').toBeVisible({ timeout: 30000 });
  const seen = await page.getByText(/^Удалено [0-9]/).count();
  console.log(`НАБЛЮДЕНИЕ (3b): итог чистки при открытом окне в DOM ${seen} раза(раз)`);
  // Что из этого видно человеку при открытом окне: подводим панель к строке под кнопкой.
  await page.getByTestId('wiki-purge-status').scrollIntoViewIfNeeded();
  await page.waitForTimeout(300);
  await page.screenshot({ path: path.join(SHOTS, 'p3b-purge-open-modal.png') });
  expect(uiErrors(errors), 'в консоли нет ошибок').toEqual([]);
});

test('3c. То же наблюдение в тёмной теме', async ({ page, context }) => {
  const errors = collectConsoleErrors(page);
  await context.addInitScript(tk => { localStorage.setItem('cc_token', tk as string); localStorage.setItem('theme-mode', 'dark'); }, token);
  for (const kind of ['тёмная-догляд', 'тёмная-замечания']) {
    await writePage(pageOf(kind),
      `---\ntype: topic\n---\n\n# Планы ${kind}\n\n## Сводка\n\n## Хронология\n\n## Противоречия\n\n## История\n`);
  }
  await openApp(page);
  await openPanelTab(page, 'wikiSettings');
  await page.getByTestId('wiki-purge-open').click();
  const confirm = page.getByRole('button', { name: /^Удалить [0-9]/ });
  await expect(confirm, 'состав есть').toBeEnabled({ timeout: 20000 });
  await confirm.click();
  await expect(page.getByText(/^Удалено /).first(), 'итог в окне').toBeVisible({ timeout: 30000 });
  const seen = await page.getByText(/^Удалено [0-9]/).count();
  console.log(`НАБЛЮДЕНИЕ (3c, тёмная тема): итог чистки при открытом окне в DOM ${seen} раза(раз)`);
  await page.getByTestId('wiki-purge-status').scrollIntoViewIfNeeded();
  await page.waitForTimeout(300);
  await page.screenshot({ path: path.join(SHOTS, 'p3c-purge-open-modal-dark.png') });
  expect(uiErrors(errors), 'в консоли нет ошибок').toEqual([]);
});
