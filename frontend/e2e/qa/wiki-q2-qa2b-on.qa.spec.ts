import { test, expect, type Page, type APIRequestContext } from '@playwright/test';
import { readFileSync, existsSync, statSync, readdirSync } from 'node:fs';
import { relative, sep } from 'node:path';
import { createHash } from 'node:crypto';
import { login, api } from './helpers';

// QA-2б · повтор выката 2 (слияние страниц) по дефектам QA-2 (задача d38a4e87).
// Стенд: worktree feat/wiki-queue2-c, вики — копия корпуса 19.11, Wiki:MergeProposals=true
// (appsettings.Local.json), «Применять без ревью» ВЫКЛ (ручной режим); режим судьи включается
// настройкой внутри прогона и возвращается обратно.
// Повтор ровно по списку прошлого круга:
//  D1 (93572246): ночной merge применим — «Принять» во «Входящих» даёт applied (A удалена,
//    строки и сноски перенесены в «Сводку» B, заголовок A — алиас B); откат пакета возвращает
//    оба файла побайтно; старые apply_failed-пакеты и откат по маркеру «session:merge:…» —
//    осмысленный отказ, вики не сломана.
//  D2 (ba3e4901): в режиме судьи merge — строка судьи «A → B» без кнопок человека;
//    счётчик рельса сходится со списком; на 360 px без переполнения.
//  Регрессия: лимиты ≤10/≤3 и «открытых <15»; пары-версии, различающиеся только датой
//    («… 2026-11-12»), не предлагаются; повторная ночь не плодит дубли.

const ROOT = process.env.QA_STAND_ROOT || '/home/an/Sources/ClaudeCodeServer-wiki-queue2c';
const OWNER = '11111111-2222-3333-4444-555555555501';
const WIKI = `${process.env.QA_STAND_DATA || `${ROOT}/data-qa`}/wiki/${OWNER}`;
const PAGES = `${WIKI}/pages`;
const NIGHT_FILE = `${WIKI}/keeper/nightly-last.json`;
const SHOTS = process.env.QA_SHOTS || `${ROOT}/.cc-attachments/wiki-q2-qa2b/shots`;
const T = { timeout: 20000 };
// Тот же разбор даты из заголовка, что на бэке (WikiTopicScore.DateToken)
const DATE_TOKEN = /(?:\b\d{4}[-./]\d{1,2}[-./]\d{1,2}\b|\b\d{1,2}[-./]\d{1,2}[-./]\d{2,4}\b)/g;

type MergeOp = {
  index: number;
  op: string;
  page: string;
  target: string | null;
  reason: string | null;
  reasonCode: string | null;
  humanDecision: string | null;
  judgeDecision: string | null;
};
type PkgDto = {
  id: string;
  section: string;
  status: string;
  statusReason: string | null;
  createdAt: string;
  sources: { origin: string; ref: string }[];
  operations: MergeOp[];
};

// Что осталось от теста применения: хэши файлов ДО «Принятья» — их сверяет тест отката
let appliedRun: { id: string; pageKey: string; targetKey: string; aHash: string; bHash: string } | null = null;

let request: APIRequestContext;
let token: string;

test.describe.configure({ mode: 'serial' });
test.setTimeout(900_000);

test.beforeAll(async ({ playwright, baseURL }) => {
  request = await playwright.request.newContext({ baseURL });
  token = await login(request);
});

test.afterAll(async () => { await request?.dispose(); });

function client() { return api(request, token); }

// Ночной разбор вручную: POST /api/wiki/settings/nightly/run → 202, проход идёт в фоне.
// Ждём конца (nightlyRunning === false) и появления свежего итога в keeper/nightly-last.json.
async function runNight(): Promise<any> {
  const c = client();
  const before = nightFileMtime();
  const r = await request.post('/api/wiki/settings/nightly/run', { headers: { Authorization: `Bearer ${token}` } });
  test.info().annotations.push({ type: 'note', description: `POST nightly/run → ${r.status()} ${await r.text()}` });
  expect(r.status(), 'ручной запуск ночи принят').toBe(202);
  const deadline = Date.now() + 240_000;
  while (Date.now() < deadline) {
    await new Promise(r => setTimeout(r, 3000));
    const st = await c.get<any>('/api/wiki/settings');
    if (!st.nightlyRunning) {
      const now = nightFileMtime();
      if (now && now !== before) {
        const file = JSON.parse(readFileSync(NIGHT_FILE, 'utf8'));
        return { ...file, ...(st.lastNight ?? {}) };
      }
    }
  }
  throw new Error('ночь не завершилась за 4 минуты');
}

// Итог ночи лежит в файле: его mtime — признак свежести прохода (содержимое при
// повторной ноче может совпасть, а поле mergeProposed в DTO настроек не отдаётся).
function nightFileMtime(): number | null {
  try { return statSync(NIGHT_FILE).mtimeMs; } catch { return null; }
}

async function inboxPackages(): Promise<PkgDto[]> {
  const list = await client().get<{ packages: PkgDto[] }>('/api/wiki/packages?inbox=true');
  return list.packages;
}

async function allPackages(): Promise<PkgDto[]> {
  const list = await client().get<{ packages: PkgDto[] }>('/api/wiki/packages');
  return list.packages;
}

function mergeOps(pkgs: PkgDto[]) {
  return pkgs.flatMap(p => p.operations.filter(o => o.op === 'merge').map(o => ({ pkg: p, op: o })));
}

const pairKey = (o: MergeOp) => `${o.page}→${o.target}`;
const sha = (path: string) => createHash('sha256').update(readFileSync(path)).digest('hex');
const pagePath = (key: string) => `${PAGES}/${key}`;

function titleOf(key: string): string {
  for (const line of readFileSync(pagePath(key), 'utf8').split('\n')) {
    if (line.startsWith('# ')) return line.slice(2).trim();
  }
  return '';
}

// Тот же «остаток без дат», что у бэкового DateVersionsOnly: даты вырезаны, пробелы схлопнуты
const stripDates = (t: string) => t.replace(DATE_TOKEN, ' ').split(/\s+/).filter(Boolean).join(' ').toLowerCase();

// Открыть панель «Входящие» вики (единый раздел «Заметки и Вики»): панели вики живут
// в рельсе зон, кнопка помечена data-rail-item ключом панели (panelCatalog).
// Область панели по умолчанию — follow за разделом (W9a): для сценария по всему корпусу
// ставим область «all» тем же стором, что пишет чип панели (wikiPanelScope, ключ localStorage).
async function openInbox(page: Page, context: any) {
  page.setDefaultTimeout(20000);
  await context.addInitScript((tk: string) => {
    localStorage.setItem('cc_token', tk);
    localStorage.setItem('cc_notes_wiki_scope', JSON.stringify({ wikiInbox: { mode: 'all' } }));
  }, token);
  await page.goto('/#/notes');
  await page.reload();
  await page.waitForTimeout(1500);
  // Тосты перекрывают кнопки — закрываем штатной кнопкой
  const closes = page.locator('button[title="Закрыть"]');
  for (let i = await closes.count(); i > 0; i--) {
    try { await closes.first().click({ timeout: 1000 }); } catch { break; }
  }
  const inSection = await page.getByRole('button', { name: 'Вики', exact: true }).count()
    + await page.getByTestId('unified-filter').count();
  if (inSection === 0) {
    const nav = page.getByRole('button', { name: 'Заметки', exact: true }).first();
    if (await nav.count()) { await nav.click(T); await page.waitForTimeout(2500); }
  }
  const rail = page.locator('[data-rail-item="wikiInbox"] button');
  await expect(rail, 'кнопка панели «Входящие» в рельсе зон').toBeVisible({ timeout: 30000 });
  await rail.click(T);
  await page.waitForTimeout(2500);
}

// Открыть панель вики на узкой ширине: панели живут шторкой (кнопка wiki-open-panel), не рельсом.
async function openInboxMobile(page: Page, context: any) {
  page.setViewportSize({ width: 360, height: 780 });
  page.setDefaultTimeout(20000);
  await context.addInitScript((tk: string) => localStorage.setItem('cc_token', tk), token);
  await page.goto('/#/notes');
  await page.reload();
  await page.waitForTimeout(1800);
  const closes = page.locator('button[title="Закрыть"]');
  for (let i = await closes.count(); i > 0; i--) {
    try { await closes.first().click({ timeout: 1000 }); } catch { break; }
  }
  await page.getByRole('button', { name: 'Вики', exact: true }).first().click();
  await page.waitForTimeout(800);
  await page.getByTestId('wiki-open-tree').click();
  await expect(page.locator('[data-testid="unified-row"][data-kind="page"]').first()).toBeVisible({ timeout: 30000 });
  await page.locator('[data-testid="unified-row"][data-kind="page"]').first().click();
  await expect(page.getByTestId('wiki-page')).toBeVisible({ timeout: 30000 });
  await page.getByTestId('wiki-open-panel').click();
  await page.waitForTimeout(600);
  const panelBtn = page.getByRole('button', { name: /^(Входящие|Решения)$/ }).first();
  await expect(panelBtn, 'в шторке есть кнопка панели вики').toBeVisible({ timeout: 20000 });
  await panelBtn.click(T);
  await page.waitForTimeout(1500);
}

const noOverflow = async (page: Page) => {
  const overflow = await page.evaluate(() => ({ doc: document.documentElement.scrollWidth, win: window.innerWidth }));
  test.info().annotations.push({ type: 'note', description: `документ ${overflow.doc}, окно ${overflow.win}` });
  expect(overflow.doc, 'нет горизонтального переполнения на 360').toBeLessThanOrEqual(overflow.win + 1);
};

test('ночь с тумблером ВКЛ предлагает merge: пакеты есть, лимиты, раздел и даты соблюдены', async () => {
  const before = await inboxPackages();
  const beforeIds = new Set(before.map(p => p.id));
  const night = await runNight();
  test.info().annotations.push({ type: 'note', description: `итог ночи: ${JSON.stringify(night)}` });
  expect(night.mergeProposed, 'итог ночи называет число предложений merge (числом)').not.toBeUndefined();

  const pkgs = await inboxPackages();
  const merges = mergeOps(pkgs);
  const fresh = pkgs.filter(p => !beforeIds.has(p.id));
  test.info().annotations.push({ type: 'note', description: `пакетов во «Решениях»: ${pkgs.length}; merge-операций: ${merges.length}; новых пакетов после ночи: ${fresh.length}` });
  expect(merges.length, 'merge-операции ждут человека во «Решениях»').toBeGreaterThan(0);
  const freshMerge = fresh.filter(p => p.operations.some(o => o.op === 'merge')).length;
  expect(night.mergeProposed, 'число предложений merge в итоге ночи равно числу новых merge-пакетов').toBe(freshMerge);

  // Лимит открытых (MergeOpenLimit=15): ночь создаёт только пока открытых <15, значит после
  // неё очередь не больше 15 (граница «≥15 → 0» — за тестами бэка, не за живым прогоном)
  const openAfter = pkgs.filter(p => ['checking', 'proposed', 'accepted', 'partially_accepted', 'applying'].includes(p.status)).length;
  test.info().annotations.push({ type: 'note', description: `открытых после ночи: ${openAfter}` });
  expect(openAfter, 'открытых пакетов после ночи не больше 15').toBeLessThanOrEqual(15);

  for (const m of merges) {
    const idA = m.op.page.split('/')[1];
    const idB = m.op.target!.split('/')[1];
    expect(idA, `A «${m.op.page}» и B «${m.op.target}» — один раздел`).toBe(idB);
    expect(m.pkg.section, 'раздел пакета — раздел обеих страниц').toBe(`projects/${idA}`);
    expect(m.op.page, 'A и B разные страницы').not.toBe(m.op.target);
    expect(m.op.reasonCode, 'код причины — merge_proposal').toBe('merge_proposal');
  }

  // Пары-версии (заголовки различаются только датой) не предлагаются (доработка 7777967a3).
  // Считаем и такие пары на корпусе — проверка не должна быть пустой.
  const titles = new Map<string, string[]>();
  const walk = (dir: string) => {
    for (const e of readdirSync(dir, { withFileTypes: true })) {
      const p = `${dir}/${e.name}`;
      if (e.isDirectory()) { walk(p); continue; }
      if (!e.name.endsWith('.md') || e.name === '_schema.md') continue;
      const key = relative(PAGES, p).split(sep).join('/');
      const t = titleOf(key);
      if (!t) continue;
      const base = stripDates(t);
      if (!titles.has(base)) titles.set(base, []);
      titles.get(base)!.push(t);
    }
  };
  walk(PAGES);
  const versionGroups = [...titles.entries()].filter(([, ts]) =>
    ts.length > 1 && ts.some(t => t.match(DATE_TOKEN)) && new Set(ts.map(t => t.toLowerCase())).size > 1);
  test.info().annotations.push({ type: 'note', description: `групп-версий на корпусе: ${versionGroups.length}: ${JSON.stringify(versionGroups.map(([b]) => b))}` });
  expect(versionGroups.length, 'на копии корпуса есть страницы-версии с датой').toBeGreaterThan(0);
  for (const m of merges) {
    const tA = titleOf(m.op.page);
    const tB = titleOf(m.op.target!);
    if (tA.toLowerCase() !== tB.toLowerCase()) {
      expect(stripDates(tA), `предложенная пара «${tA}» / «${tB}» — версии, различающиеся только датой`).not.toBe(stripDates(tB));
    }
  }

  // Лимит за ночь: пакеты одной ночи создаются подряд (разрыв между ночами — минуты),
  // поэтому режем историю на волны по разрыву больше 10 секунд
  const sorted = [...merges].sort((a, b) => Date.parse(a.pkg.createdAt) - Date.parse(b.pkg.createdAt));
  const waves: Array<{ label: string; items: typeof merges }> = [];
  for (const m of sorted) {
    const t = Date.parse(m.pkg.createdAt);
    const last = waves[waves.length - 1];
    if (last && t - Date.parse(last.items[last.items.length - 1].pkg.createdAt) <= 10_000) last.items.push(m);
    else waves.push({ label: m.pkg.createdAt, items: [m] });
  }
  for (const wave of waves) {
    test.info().annotations.push({ type: 'note', description: `волна ${wave.label}: ${wave.items.length} предложений` });
    expect(wave.items.length, `за ночь (${wave.label}) не больше 10 предложений`).toBeLessThanOrEqual(10);
    const secCount = new Map<string, number>();
    for (const m of wave.items) {
      const id = m.op.page.split('/')[1];
      secCount.set(id, (secCount.get(id) ?? 0) + 1);
    }
    for (const [sec, n] of secCount)
      expect(n, `в разделе «${sec}» за ночь (${wave.label}) не больше 3 предложений`).toBeLessThanOrEqual(3);
  }
});

test('карточка merge во «Решениях» (ручной режим): A → B, причина, кнопки', async ({ page, context }) => {
  await openInbox(page, context);
  const row = page.locator('[data-testid="wiki-pkg-row"]').first();
  await expect(row, 'в очереди есть пакет').toBeVisible({ timeout: 30000 });
  await row.click(T);
  await page.waitForTimeout(2000);

  const op = page.locator('[data-testid="wiki-op-row"]').first();
  await expect(op, 'карточка операции открылась').toBeVisible({ timeout: 20000 });
  const opText = await op.innerText();
  test.info().annotations.push({ type: 'note', description: `карточка операции: ${JSON.stringify(opText)}` });
  expect(opText, 'бейдж операции merge').toContain('слить страницы');

  const merge = page.locator('[data-testid="wiki-op-merge"]');
  await expect(merge, 'в карточке есть строка «A → B»').toBeVisible({ timeout: 20000 });
  const mergeText = (await merge.innerText()).trim();
  test.info().annotations.push({ type: 'note', description: `строка слияния: «${mergeText}»` });
  expect(mergeText, 'строка слияния содержит стрелку').toContain('→');

  const why = page.locator('[data-testid="wiki-op-why"]');
  await expect(why, 'у карточки есть причина').toBeVisible({ timeout: 20000 });
  expect((await why.innerText()).length, 'причина непустая').toBeGreaterThan(10);

  await expect(page.locator('[data-testid="wiki-op-accept-0"]'), 'кнопка «Принять» есть').toBeVisible({ timeout: 20000 });
  await expect(page.locator('[data-testid="wiki-op-reject-0"]'), 'кнопка «Отклонить» есть').toBeVisible({ timeout: 20000 });

  await page.screenshot({ path: `${SHOTS}/qa2b-merge-inbox-1280.png`, fullPage: true });
});

test('мобильная ширина 360 (ручной режим): карточка merge читается, переполнения нет', async ({ page, context }) => {
  await openInboxMobile(page, context);
  const row = page.locator('[data-testid="wiki-pkg-row"]').first();
  await expect(row, 'на 360 в очереди есть пакет').toBeVisible({ timeout: 30000 });
  await row.click(T);
  await page.waitForTimeout(1500);
  const merge = page.locator('[data-testid="wiki-op-merge"]');
  await expect(merge, 'на 360 строка слияния «A → B» видна').toBeVisible({ timeout: 30000 });
  const text = (await merge.innerText()).trim();
  test.info().annotations.push({ type: 'note', description: `на 360 строка слияния: «${text}»` });
  expect(text, 'на 360 строка слияния содержит стрелку').toContain('→');
  await noOverflow(page);
  await page.screenshot({ path: `${SHOTS}/qa2b-merge-inbox-360.png`, fullPage: true });
});

test('D1: ночной merge применяется «Принятьем» из интерфейса: applied, A удалена, строки и сноски на B, алиас', async ({ page, context }) => {
  const pkgs = await inboxPackages();
  const merges = mergeOps(pkgs);
  expect(merges.length, 'есть merge-пакет для применения').toBeGreaterThan(0);
  const target = merges[0];
  const aPath = pagePath(target.op.page);
  const bPath = pagePath(target.op.target!);
  const aBefore = readFileSync(aPath, 'utf8');
  const aHashBefore = sha(aPath);
  const bHashBefore = sha(bPath);
  const titleA = titleOf(target.op.page);

  await openInbox(page, context);
  const row = page.locator('[data-testid="wiki-pkg-row"]', { hasText: target.op.page });
  await expect(row, `строка пакета «${target.op.page}» в очереди`).toBeVisible({ timeout: 30000 });
  await row.click(T);
  await page.waitForTimeout(2000);
  const accept = page.locator('[data-testid="wiki-op-accept-0"]');
  await expect(accept, 'кнопка «Принять»').toBeVisible({ timeout: 20000 });
  await accept.click(T);
  await page.waitForTimeout(3000);

  // Итог — по факту на диске и по состоянию пакета: «Принять» даёт applied, не apply_failed
  const after = await client().get<PkgDto>(`/api/wiki/packages/${target.pkg.id}`);
  test.info().annotations.push({ type: 'note', description: `статус после «Принять»: ${after.status}; причина: ${after.statusReason ?? '—'}` });
  expect(after.status, 'D1: ночной merge применён').toBe('applied');
  appliedRun = { id: target.pkg.id, pageKey: target.op.page, targetKey: target.op.target!, aHash: aHashBefore, bHash: bHashBefore };

  expect(existsSync(aPath), 'файл A удалён слиянием').toBe(false);
  const b = readFileSync(bPath, 'utf8');
  // Строки A — только тело секции «Сводка» (фронтматтер и сноски — не переносимые строки)
  const fmEnd = aBefore.indexOf('---', 3);
  const aBody = fmEnd >= 0 ? aBefore.slice(fmEnd + 3) : aBefore;
  const aLines = (aBody.split('## Сводка')[1]?.split(/\n## /)[0] ?? '')
    .split('\n').map(l => l.trim()).filter(l => l && !l.startsWith('[^'));
  expect(aLines.length, 'у A были строки для переноса').toBeGreaterThan(0);
  const summary = b.split('## Сводка')[1]?.split(/\n## /)[0] ?? '';
  for (const line of aLines) {
    const stem = line.replace(/\[\^\w+\]/g, '').trim();
    expect(summary, `строка A «${stem}» перенесена в «Сводку» B`).toContain(stem.slice(0, 40));
  }
  const aQuotes = [...aBefore.matchAll(/^\[\^\w+\]: (.+)$/gm)].map(m => m[1]);
  for (const q of aQuotes) expect(b, `сноска A «${q.slice(0, 40)}» живёт на B`).toContain(q.slice(0, 40));
  expect(b, 'заголовок A стал алиасом B').toContain(titleA);

  await page.screenshot({ path: `${SHOTS}/qa2b-merge-applied.png`, fullPage: true });
  // Готовим следующий тест: закрыть карточку/вернуться к списку не обязательно — дальше только API
});

test('D1: откат применённого пакета возвращает оба файла побайтно', async () => {
  expect(appliedRun, 'тест применения оставил снимок «до»').toBeTruthy();
  const run = appliedRun!;
  const aPath = pagePath(run.pageKey);
  const bPath = pagePath(run.targetKey);
  // Вторая точка сравнения — baseHashes пакета (их записала ночь до применения)
  const pkg = await client().get<PkgDto>(`/api/wiki/packages/${run.id}`);
  const op = pkg.operations.find(o => o.op === 'merge')!;
  expect((op as any).baseHashes?.[run.pageKey], 'baseHashes A пакета совпадают с хэшем «до»').toBe(run.aHash);
  expect((op as any).baseHashes?.[run.targetKey], 'baseHashes B пакета совпадают с хэшем «до»').toBe(run.bHash);

  const r = await request.post(`/api/wiki/packages/${run.id}/revert`, { headers: { Authorization: `Bearer ${token}` } });
  const body = await r.text();
  test.info().annotations.push({ type: 'note', description: `revert → ${r.status()} ${body}` });
  expect(r.status(), 'откат применённого пакета принят').toBe(200);
  const dto = JSON.parse(body);
  expect(dto.restoredPages, 'откат назвал обе страницы').toEqual(expect.arrayContaining([run.pageKey, run.targetKey]));

  expect(existsSync(aPath), 'файл A вернулся').toBe(true);
  expect(sha(aPath), 'хэш A после отката равен хэшу A до применения').toBe(run.aHash);
  expect(sha(bPath), 'хэш B после отката равен хэшу B до применения').toBe(run.bHash);
  const after = await client().get<PkgDto>(`/api/wiki/packages/${run.id}`);
  expect(after.status, 'пакет после отката — reverted').toBe('reverted');
});

test('D1: старые apply_failed-пакеты ночи — терминальны: решение и откат отказаны осмысленно, файлы не тронуты', async () => {
  const failed = (await allPackages()).filter(p => p.status === 'apply_failed' && p.operations.some(o => o.op === 'merge'));
  test.info().annotations.push({ type: 'note', description: `старых apply_failed merge-пакетов: ${failed.length}` });
  expect(failed.length, 'на стенде остались старые ночные пакеты (до правки D1)').toBeGreaterThan(0);
  for (const p of failed) {
    const op = p.operations.find(o => o.op === 'merge')!;
    const aPath = pagePath(op.page);
    const bPath = pagePath(op.target!);
    const hashBefore = [sha(aPath), sha(bPath)];

    const d = await request.post(`/api/wiki/packages/${p.id}/decide`, {
      headers: { Authorization: `Bearer ${token}` }, data: { index: op.index, accept: true },
    });
    const dBody = await d.text();
    test.info().annotations.push({ type: 'note', description: `decide ${p.id} → ${d.status()} ${dBody.slice(0, 200)}` });
    expect(d.status(), 'решение по терминальному пакету отказано').toBe(409);
    expect(dBody, 'отказ назван по-человечески').toContain('proposed');

    const r = await request.post(`/api/wiki/packages/${p.id}/revert`, { headers: { Authorization: `Bearer ${token}` } });
    const rBody = await r.text();
    test.info().annotations.push({ type: 'note', description: `revert ${p.id} → ${r.status()} ${rBody.slice(0, 200)}` });
    expect(r.status(), 'откат неприменённого пакета отказан').toBe(409);

    expect([sha(aPath), sha(bPath)], 'файлы пакета не тронуты после отказов').toEqual(hashBefore);
  }
});

test('D1: откат по источнику с маркером «session:merge:…» — осмысленный отказ (400), вики не сломана', async () => {
  const withMarker = (await allPackages()).find(p => p.sources.some(s => s.ref.startsWith('merge:')));
  expect(withMarker, 'есть пакет с источником-маркером слияния').toBeTruthy();
  const src = withMarker!.sources.find(s => s.ref.startsWith('merge:'))!;
  const r = await request.post('/api/wiki/packages/retract', {
    headers: { Authorization: `Bearer ${token}` }, data: { sourceRef: `${src.origin}:${src.ref}` },
  });
  const body = await r.text();
  test.info().annotations.push({ type: 'note', description: `retract ${src.origin}:${src.ref} → ${r.status()} ${body.slice(0, 200)}` });
  expect(r.status(), 'откат по маркеру отказан 400 (форма)').toBe(400);
  expect(body, 'отказ назван человеческим текстом').toContain('маркер слияния');
  // Вики не сломана: список «Входящих» читается и после отказа
  const pkgs = await inboxPackages();
  test.info().annotations.push({ type: 'note', description: `открытых пакетов после отказа: ${pkgs.length}` });
});

test('D2: режим судьи — merge строкой судьи «A → B» без кнопок человека; счётчик рельса сходится; 360 без переполнения', async ({ page, context }) => {
  const open = await inboxPackages();
  const merges = mergeOps(open);
  expect(merges.length, 'есть открытые merge-пакеты для проверки режима судьи').toBeGreaterThan(0);

  await client().put('/api/wiki/settings', { autoApplyEnabled: true });
  try {
    await openInbox(page, context);

    const notice = page.locator('[data-testid="wiki-inbox-notice"]');
    await expect(notice, 'плашка режима судьи').toBeVisible({ timeout: 30000 });
    expect(await notice.innerText(), 'плашка называется «Решения судьи»').toContain('Решения судьи');

    const judgeRows = page.locator('[data-testid="wiki-judge-row"]');
    await expect(judgeRows.first(), 'строки судьи есть').toBeVisible({ timeout: 30000 });
    const rowsCount = await judgeRows.count();
    expect(rowsCount, 'каждый открытый пакет — строка судьи').toBe(open.length);

    // Ручных строк и кнопок человека в списке нет (D2: merge допущен судье по полю DTO)
    expect(await page.locator('[data-testid="wiki-pkg-row"]').count(), 'ручных строк нет').toBe(0);
    expect(await page.locator('button:has-text("Принять")').count(), 'кнопок «Принять» в списке нет').toBe(0);

    // Строка судьи для merge несёт «A → B» той же разметкой, что карточка пакета
    const mergeLine = page.locator('[data-testid="wiki-judge-row"] [data-testid="wiki-op-merge"]').first();
    await expect(mergeLine, 'строка судьи «A → B»').toBeVisible({ timeout: 20000 });
    const lineText = (await mergeLine.innerText()).trim();
    test.info().annotations.push({ type: 'note', description: `строка судьи: «${lineText}»` });
    expect(lineText, 'строка содержит стрелку').toContain('→');
    const withArrow = await page.locator('[data-testid="wiki-judge-row"] [data-testid="wiki-op-merge"]').count();
    expect(withArrow, 'merge-строк с «A → B» ровно по числу merge-операций').toBe(merges.length);

    // Счётчик рельса сходится со списком. Под курсором на иконке открытой панели
    // бейджа нет по дизайну (closing гасит число, PanelRail.tsx:465) — отводим курсор.
    await page.mouse.move(640, 400);
    await page.waitForTimeout(800);
    const railBadge = await page.locator('[data-rail-item="wikiInbox"]').first().innerText();
    const railNum = Number((railBadge.match(/\d+/) ?? ['0'])[0]);
    test.info().annotations.push({ type: 'note', description: `рельс: «${railBadge}», API: ${open.length}, строк: ${rowsCount}` });
    expect(railNum, 'счётчик рельса равен числу открытых пакетов').toBe(open.length);

    await page.screenshot({ path: `${SHOTS}/qa2b-judge-1280.png`, fullPage: true });
  } finally {
    await client().put('/api/wiki/settings', { autoApplyEnabled: false });
  }
});

test('D2: режим судьи на 360 — строка «A → B» читается, переполнения нет', async ({ page, context }) => {
  const open = await inboxPackages();
  expect(mergeOps(open).length, 'есть открытые merge-пакеты').toBeGreaterThan(0);
  await client().put('/api/wiki/settings', { autoApplyEnabled: true });
  try {
    await openInboxMobile(page, context);
    const mergeLine = page.locator('[data-testid="wiki-judge-row"] [data-testid="wiki-op-merge"]').first();
    await expect(mergeLine, 'на 360 строка судьи «A → B» видна').toBeVisible({ timeout: 30000 });
    const text = (await mergeLine.innerText()).trim();
    test.info().annotations.push({ type: 'note', description: `на 360 строка судьи: «${text}»` });
    expect(text, 'на 360 строка содержит стрелку').toContain('→');
    expect(await page.locator('[data-testid="wiki-pkg-row"]').count(), 'на 360 тоже нет ручных строк').toBe(0);
    await noOverflow(page);
    await page.screenshot({ path: `${SHOTS}/qa2b-judge-360.png`, fullPage: true });
  } finally {
    await client().put('/api/wiki/settings', { autoApplyEnabled: false });
  }
});

test('повторная ночь не плодит дубли: открытые и откатанные пары не предлагаются заново', async () => {
  const before = await inboxPackages();
  const beforePairs = mergeOps(before).map(m => pairKey(m.op)).sort();
  expect(beforePairs.length, 'до второй ночи есть открытые предложения').toBeGreaterThan(0);

  // Пары, которые этот прогон применил и откатил (карантин отката), и применённые (A уже нет на диске)
  const all = await allPackages();
  const today = new Date().toISOString().slice(0, 10);
  const donePairs = all
    .filter(p => ['applied', 'reverted'].includes(p.status) && p.createdAt.startsWith(today))
    .flatMap(p => p.operations.filter(o => o.op === 'merge').map(o => pairKey(o)));
  test.info().annotations.push({ type: 'note', description: `пар, применённых/откатанных этим прогоном: ${JSON.stringify(donePairs)}` });

  const night = await runNight();
  test.info().annotations.push({ type: 'note', description: `итог второй ночи: ${JSON.stringify(night)}` });
  const after = await inboxPackages();
  const afterPairs = mergeOps(after).map(m => pairKey(m.op));
  test.info().annotations.push({ type: 'note', description: `открытых после второй ночи: ${after.length}; пар: ${afterPairs.length}` });

  for (const pair of afterPairs)
    expect(afterPairs.filter(p => p === pair).length, `пара «${pair}» не задвоилась`).toBe(1);
  for (const pair of beforePairs)
    expect(afterPairs, `открытая пара «${pair}» не предложена заново`).toContain(pair);
  for (const pair of donePairs)
    expect(afterPairs, `откатанная/применённая пара «${pair}» не вернулась в очередь`).not.toContain(pair);
  const newOnes = afterPairs.filter(p => !beforePairs.includes(p));
  expect(newOnes.length, 'за ночь не больше 10 новых предложений').toBeLessThanOrEqual(10);
  const openAfter2 = after.filter(p => ['checking', 'proposed', 'accepted', 'partially_accepted', 'applying'].includes(p.status)).length;
  test.info().annotations.push({ type: 'note', description: `открытых после второй ночи: ${openAfter2}` });
  expect(openAfter2, 'после второй ночи открытых не больше 15').toBeLessThanOrEqual(15);
});

test('изоляция: merge не выходит за раздел и не трогает страницы человека', async () => {
  const pkgs = await inboxPackages();
  const merges = mergeOps(pkgs);
  expect(merges.length, 'merge-операции есть').toBeGreaterThan(0);
  for (const m of merges) {
    const idA = m.op.page.split('/')[1];
    const idB = m.op.target!.split('/')[1];
    expect(idA, `A и B в одном разделе (пакет ${m.pkg.id})`).toBe(idB);
    expect(m.pkg.section, 'раздел пакета совпадает с разделом страниц').toBe(`projects/${idA}`);
  }
  // Страницы человека (секция «Заметки») не предлагаются: ни одна A не содержит секции «Заметки»
  for (const m of merges) {
    const text = readFileSync(pagePath(m.op.page), 'utf8');
    expect(text, `на A «${m.op.page}» нет секции «Заметки» человека`).not.toContain('## Заметки');
  }
});

test('уборка: открытые предложения этого прогона отклонены с причиной — off-часть стартует с чистой очередью', async () => {
  const open = (await inboxPackages()).filter(p => p.operations.some(o => o.op === 'merge'));
  test.info().annotations.push({ type: 'note', description: `отклоняем открытых merge-пакетов: ${open.length}` });
  for (const p of open) {
    for (const o of p.operations.filter(x => x.op === 'merge' && !x.humanDecision)) {
      const r = await request.post(`/api/wiki/packages/${p.id}/decide`, {
        headers: { Authorization: `Bearer ${token}` }, data: { index: o.index, accept: false, reason: 'QA-проверка: уборка фикстуры перед off-частью' },
      });
      test.info().annotations.push({ type: 'note', description: `reject ${p.id} → ${r.status()}` });
      expect(r.status(), `отклонение пакета ${p.id}`).toBe(200);
    }
  }
  const after = await inboxPackages();
  expect(after.filter(p => p.operations.some(o => o.op === 'merge')).length, 'открытых merge-пакетов не осталось').toBe(0);
});
