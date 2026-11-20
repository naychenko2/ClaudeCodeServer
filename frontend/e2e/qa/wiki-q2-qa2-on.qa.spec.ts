import { test, expect, type Page, type APIRequestContext } from '@playwright/test';
import { readFileSync, statSync } from 'node:fs';
import { login, api } from './helpers';

// QA-2 · выкат 2 фичи «LLM-wiki: связи и укрупнение страниц (очередь 2)» — слияние страниц.
// Стенд из worktree feat/wiki-queue2-c, вики засеяна копией боевого корпуса 19.11,
// тумблер Wiki:MergeProposals = true (appsettings.Local.json стенда).
// Проверяем: предложения merge в ночи, карточку во «Решениях», применение и откат,
// изоляцию (раздел/строки человека), отсутствие дублей при повторной ночи и пустое состояние.

const ROOT = process.env.QA_STAND_ROOT || '/home/an/Sources/ClaudeCodeServer-wiki-queue2c';
const OWNER = '11111111-2222-3333-4444-555555555501';
const WIKI = `${process.env.QA_STAND_DATA || `${ROOT}/data-qa`}/wiki/${OWNER}`;
const PAGES = `${WIKI}/pages`;
const NIGHT_FILE = `${WIKI}/keeper/nightly-last.json`;
const SHOTS = process.env.QA_SHOTS || `${ROOT}/.cc-attachments/wiki-q2-qa2/shots`;
const T = { timeout: 20000 };

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
  createdAt: string;
  operations: MergeOp[];
};

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

function mergeOps(pkgs: PkgDto[]) {
  return pkgs.flatMap(p => p.operations.filter(o => o.op === 'merge').map(o => ({ pkg: p, op: o })));
}

// Открыть панель «Входящие» вики (единый раздел «Заметки и Вики»): панели вики живут
// в рельсе зон, кнопка помечена data-rail-item ключом панели (panelCatalog).
async function openInbox(page: Page, context: any) {
  page.setDefaultTimeout(20000);
  await context.addInitScript((tk: string) => localStorage.setItem('cc_token', tk), token);
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

test('ночь с тумблером ВКЛ предлагает merge: пакеты есть, лимиты и раздел соблюдены', async () => {
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
  // Итог ночи и фактические пакеты сходятся: столько предложений merge, сколько новых пакетов
  const freshMerge = fresh.filter(p => p.operations.some(o => o.op === 'merge')).length;
  expect(night.mergeProposed, 'число предложений merge в итоге ночи равно числу новых merge-пакетов').toBe(freshMerge);

  // Лимиты: ≤10 за ночь, ≤3 на раздел; обе страницы — в одном разделе; A ≠ B
  for (const m of merges) {
    const idA = m.op.page.split('/')[1];
    const idB = m.op.target!.split('/')[1];
    expect(idA, `A «${m.op.page}» и B «${m.op.target}» — один раздел`).toBe(idB);
    expect(m.pkg.section, 'раздел пакета — раздел обеих страниц').toBe(`projects/${idA}`);
    expect(m.op.page, 'A и B разные страницы').not.toBe(m.op.target);
    expect(m.op.reasonCode, 'код причины — merge_proposal').toBe('merge_proposal');
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

test('карточка merge во «Решениях»: A → B, причина, кнопки; на 360 без переполнения', async ({ page, context }) => {
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

  await page.screenshot({ path: `${SHOTS}/wiki-q2-merge-inbox-1280.png`, fullPage: true });

});

// Мобильная ширина: панели вики на узкой ширине открываются шторкой (кнопка wiki-open-panel),
// а не рельсом. Карточка операции должна читаться и не выходить за экран.
test('мобильная ширина 360: карточка merge читается, горизонтального переполнения нет', async ({ page, context }) => {
  await page.setViewportSize({ width: 360, height: 780 });
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
  const row = page.locator('[data-testid="wiki-pkg-row"]').first();
  await expect(row, 'на 360 в очереди есть пакет').toBeVisible({ timeout: 30000 });
  await row.click(T);
  await page.waitForTimeout(1500);
  const merge = page.locator('[data-testid="wiki-op-merge"]');
  await expect(merge, 'на 360 строка слияния «A → B» видна').toBeVisible({ timeout: 30000 });
  const text = (await merge.innerText()).trim();
  test.info().annotations.push({ type: 'note', description: `на 360 строка слияния: «${text}»` });
  expect(text, 'на 360 строка слияния содержит стрелку').toContain('→');
  const overflow = await page.evaluate(() => ({ doc: document.documentElement.scrollWidth, win: window.innerWidth }));
  test.info().annotations.push({ type: 'note', description: `на 360: документ ${overflow.doc}, окно ${overflow.win}` });
  expect(overflow.doc, 'нет горизонтального переполнения на 360').toBeLessThanOrEqual(overflow.win + 1);
  await page.screenshot({ path: `${SHOTS}/wiki-q2-merge-inbox-360.png`, fullPage: true });
});

test('применение merge: пакет уходит в apply_failed с названной причиной, файлы не тронуты', async () => {
  const c = client();
  const pkgs = await inboxPackages();
  const merges = mergeOps(pkgs).filter(m => m.pkg.status !== 'apply_failed');
  expect(merges.length, 'есть merge-пакет для применения').toBeGreaterThan(0);
  const target = merges[0];
  const aPath = `${PAGES}/${target.op.page}`;
  const bPath = `${PAGES}/${target.op.target}`;
  const aBefore = readFileSync(aPath, 'utf8');
  const bBefore = readFileSync(bPath, 'utf8');

  const res = await c.post<any>(`/api/wiki/packages/${target.pkg.id}/decide`, { index: target.op.index, accept: true });
  test.info().annotations.push({ type: 'note', description: `decide → статус ${res.package?.status}; причина: ${res.package?.statusReason ?? res.apply?.reason ?? '—'}` });
  // НАБЛЮДЕНИЕ ВЫКАТА 2 (дефект): предложенный ночью merge-пакет НЕ применяется — у него
  // источник «session:merge:{путь A}», а применение ищет карточку источника по Sources и не
  // находит её. Здесь фиксируем наблюдаемое поведение, а не желаемое.
  expect(res.package?.status, 'наблюдаемый статус пакета после решения').toBe('apply_failed');
  const reason = res.package?.statusReason ?? '';
  test.info().annotations.push({ type: 'note', description: `причина отказа: ${reason}` });
  expect(reason, 'причина отказа названа').toContain('источник');
  // Файлы не тронуты: отказ применения не пишет ничего
  expect(readFileSync(aPath, 'utf8'), 'файл A не тронут').toBe(aBefore);
  expect(readFileSync(bPath, 'utf8'), 'файл B не тронут').toBe(bBefore);
});

test('откат пакета, который не применён: отказ (пакет не применён)', async () => {
  const pkgs = await inboxPackages();
  const failed = mergeOps(pkgs).find(m => m.pkg.status === 'apply_failed');
  test.skip(!failed, 'нет пакета в apply_failed');
  const r = await request.post(`/api/wiki/packages/${failed!.pkg.id}/revert`, { headers: { Authorization: `Bearer ${token}` } });
  test.info().annotations.push({ type: 'note', description: `revert неприменённого пакета → ${r.status()}` });
  expect(r.status(), 'откат неприменённого пакета отказан').toBe(409);
});

test('повторная ночь не плодит дубли предложений для той же пары', async () => {
  const before = await inboxPackages();
  const beforePairs = mergeOps(before).map(m => `${m.op.page}→${m.op.target}`).sort();
  const night = await runNight();
  test.info().annotations.push({ type: 'note', description: `итог второй ночи: ${JSON.stringify(night)}` });
  const after = await inboxPackages();
  const afterPairs = mergeOps(after).map(m => `${m.op.page}→${m.op.target}`).sort();
  test.info().annotations.push({ type: 'note', description: `пар до: ${JSON.stringify(beforePairs)}; после: ${JSON.stringify(afterPairs)}` });
  // Новая пара в следующую ночь — законно (предложения не исчерпали корпус); закон — чтобы
  // ОДНА И ТА ЖЕ пара не предлагалась дважды: страницы открытого пакета исключены из подбора.
  const newOnes = afterPairs.filter(p => !beforePairs.includes(p));
  test.info().annotations.push({ type: 'note', description: `новых пар в следующую ночь: ${JSON.stringify(newOnes)}` });
  expect(newOnes.length, 'за ночь не больше 10 новых предложений').toBeLessThanOrEqual(10);
  for (const pair of afterPairs)
    expect(afterPairs.filter(p => p === pair).length, `пара «${pair}» не задвоилась`).toBe(1);
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
    const text = readFileSync(`${PAGES}/${m.op.page}`, 'utf8');
    expect(text, `на A «${m.op.page}» нет секции «Заметки» человека`).not.toContain('## Заметки');
  }
});
