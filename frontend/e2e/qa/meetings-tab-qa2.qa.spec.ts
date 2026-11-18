import { test, expect, type APIRequestContext, type Page } from '@playwright/test';
import { api, collectConsoleErrors, login, seed, type Api } from './helpers';
import { mkdirSync, readFileSync, rmSync, utimesSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';

// QA-2 · повторная приёмка вкладки «Встречи» (фича 76117203): проверяем ТОЛЬКО то, что
// менялось после первой приёмки — пять закрытых дефектов и правки после неё.
//
// Состояния конвейера задаются фикстурами: spec пишет свой каталог под /home/an/qa-meetings-fix
// и меняет Transcription:PipelineRoot в appsettings.Local.json рабочего дерева — конфиг
// перечитывается на лету (Program.cs:69 reloadOnChange:true), стенд не перезапускается.
// Живые данные worker (/home/an/services/transcription-pipeline) проверяются отдельно,
// первыми: «нет данных» как отговорку не принимаем.
//
// Порядок запуска — serial: PipelineRoot общий на процесс, паралельные тесты мешали бы друг другу.

const PREFIX = 'qa2-';
const CONF = '/home/an/Sources/ClaudeCodeServer-wt/feat~meetings-tab/backend/ClaudeHomeServer/appsettings.Local.json';
const LIVE_ROOT = '/home/an/services/transcription-pipeline';
const FIXROOT = '/home/an/qa-meetings-fix';

test.describe.configure({ mode: 'serial' });

let request: APIRequestContext;
let token: string;
let client: Api;
let seeded: ReturnType<typeof seed>;
let projectId: string;
let originalRoot = '';

// ─── время ────────────────────────────────────────────────────────────────────────
// Метки конвейера — местное время узла без смещения; его же парсит бэкенд
// (DateTimeStyles.None) и его же даёт Date в браузере этой же машины.
function stamp(ms: number): string {
  const d = new Date(ms);
  const p = (n: number) => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())} ${p(d.getHours())}:${p(d.getMinutes())}:${p(d.getSeconds())}`;
}
function stampMin(ms: number): string { return stamp(ms).slice(0, 16); }

// ─── фикстуры ─────────────────────────────────────────────────────────────────────
interface FixtureOpts {
  // содержимое logs/state.json; null — файла нет (путь «не проверялось»)
  state?: Record<string, unknown> | null;
  summary?: Record<string, unknown>;
  journal?: string[];
  dify?: Record<string, unknown>;
  // каталоги вывода папок (создаются); marker — строки output_host.tsv в каждом из них
  outputs?: string[];
  marker?: Array<[string, number]>;
  // файлы, которые «писал» этот каталог: {путь в выводе, mtime}
  mdFiles?: Array<{ path: string; mtimeMs: number }>;
}

// Каталог фикстуры + её общий каталог вывода (по нему спек узнаёт, что конфиг применился).
function fixture(caseName: string, opts: FixtureOpts): { root: string; outDir: string } {
  const root = join(FIXROOT, `${PREFIX}${caseName}`);
  const outDir = join(root, 'out');
  mkdirSync(join(root, 'logs'), { recursive: true });
  const dirs = opts.outputs ?? ['ГПБ', 'Сиам'];
  for (const d of dirs) {
    mkdirSync(join(outDir, d), { recursive: true });
    if (opts.marker) {
      const rows = opts.marker.map(([host, seq]) => `${host}\t${Math.floor(Date.now() / 1000)}\t${stamp(Date.now())}\t${seq}`);
      writeFileSync(join(outDir, d, 'output_host.tsv'), rows.join('\n') + '\n');
    }
  }
  for (const f of opts.mdFiles ?? []) {
    const p = join(outDir, f.path);
    mkdirSync(join(p, '..'), { recursive: true });
    writeFileSync(p, '# метка\n');
    utimesSync(p, f.mtimeMs, f.mtimeMs);
  }
  const watch = join(root, 'watch');
  mkdirSync(watch, { recursive: true });
  const folders = dirs.map(d => `  - name: Video-${d}\n    watch: ${watch}\n    output: ${outDir}/${d}`).join('\n');
  writeFileSync(join(root, 'config.yaml'), `folders:\n${folders}\n`);
  if (opts.state !== undefined) {
    if (opts.state === null) { /* файла нет намеренно */ }
    else writeFileSync(join(root, 'logs', 'state.json'), JSON.stringify(opts.state, null, 1));
  }
  if (opts.summary) writeFileSync(join(root, 'logs', 'summary_state.json'), JSON.stringify(opts.summary, null, 1));
  if (opts.journal) writeFileSync(join(root, 'logs', 'journal.tsv'), opts.journal.join('\n') + '\n');
  if (opts.dify) writeFileSync(join(root, 'logs', 'dify_state.json'), JSON.stringify(opts.dify, null, 1));
  return { root, outDir };
}

// Сменить PipelineRoot и дождаться, пока снимок правда из новой фикстуры
// (у каждой свой каталог вывода — он и признак).
async function useRoot(root: string, expectOutDir: string | null): Promise<void> {
  const cfg = JSON.parse(readFileSync(CONF, 'utf8'));
  cfg.Transcription = { ...(cfg.Transcription ?? {}), PipelineRoot: root };
  writeFileSync(CONF, JSON.stringify(cfg, null, 2));
  const deadline = Date.now() + 60_000;
  for (;;) {
    const s = await client.get<Record<string, unknown>>('/api/local-model/transcription');
    const got = typeof s?.outputDir === 'string' ? s.outputDir : null;
    if (got === expectOutDir) return;
    if (Date.now() > deadline) throw new Error(`PipelineRoot не применился: ждали ${expectOutDir}, в снимке ${got}`);
    await new Promise(r => setTimeout(r, 1500));
  }
}

// ─── браузер ──────────────────────────────────────────────────────────────────────
async function openAs(page: Page, role: 'admin' | 'user', tab: string): Promise<void> {
  await page.addInitScript(([tk, r, t]) => {
    localStorage.setItem('cc_token', tk as string);
    localStorage.setItem('cc_role', r as string);
    localStorage.setItem('cc_username', r === 'admin' ? 'admin' : 'qa2-user');
    localStorage.setItem('cc_local_model_tab', t as string);
  }, [token, role, tab] as const);
  await page.goto(`/#/project/${projectId}`);
  await expect(page.locator('#root')).not.toBeEmpty();
}

async function openPanel(page: Page) {
  const rail = page.locator('button[aria-label^="Ферма"]');
  await expect(rail.first(), 'значок панели в рельсе').toBeVisible({ timeout: 120_000 });
  await rail.first().click();
  const panel = page.locator('[role="complementary"][aria-label="Ферма"]');
  await expect(panel, 'панель открылась').toBeVisible({ timeout: 30_000 });
  return panel;
}

// Лента вкладок живёт в шапке карточки и по общему правилу PanelShell схлопнута
// (maxWidth:0, opacity:0), пока курсор не на карточе: PanelShell.tsx:167-172 controlsFade.
// Касание вкладок возможно только после наведения — это поведение панели-контейнера,
// не этой фичи; на тач-раскладке переключатель рисуется в теле панели.
async function revealSwitcher(page: Page, panel: import('@playwright/test').Locator) {
  await panel.hover();
  await page.waitForTimeout(400);
}

// Точка-состояние (6 px) на кнопке вкладки переключателя: ищем span нужной геометрии
// внутри кнопки с именем вкладки; цвет — против значения токена темы.
// Переключатель живёт в шапке контейнера (PanelHeaderSlot), вне role=complementary —
// поэтому корнем всегда служит страница, а не панель.
async function dotOnTab(page: Page, label: string): Promise<string | null> {
  const btn = page.getByRole('button', { name: label, exact: true }).first();
  if (await btn.count() === 0) return null;
  const color = await btn.evaluate((el) => {
    for (const s of Array.from(el.querySelectorAll('span'))) {
      const cs = getComputedStyle(s);
      if (cs.width === '6px' && cs.height === '6px' && parseFloat(cs.borderRadius) >= 3) return cs.backgroundColor;
    }
    return null;
  });
  return color;
}

async function themeColor(panel: import('@playwright/test').Locator, varName: string): Promise<string> {
  return await panel.evaluate((el, v) => getComputedStyle(el).getPropertyValue(v).trim(), varName);
}

// Цвет текста строки kv-значения по её тексту.
async function colorOfText(panel: import('@playwright/test').Locator, text: string): Promise<string | null> {
  const el = panel.getByText(text, { exact: false }).first();
  if (await el.count() === 0) return null;
  return await el.evaluate((n) => getComputedStyle(n).color);
}

// Заголовки карточек вкладки в порядке DOM.
async function cardTitles(panel: import('@playwright/test').Locator): Promise<string[]> {
  return await panel.locator('h3').evaluateAll(els => els.map(e => (e.textContent || '').trim()));
}

// Ошибки стенда, не относящиеся к фиче: ERR_NETWORK_CHANGED при перезагрузке и 404
// локального провайдера (/api/providers/{key}/balance у локального провайдера — он же и на проде).
function realErrors(errors: string[]): string[] {
  return errors.filter(e => !/ERR_NETWORK_CHANGED/.test(e) && !/Failed to load resource.*404/.test(e));
}

function rgbOf(hex: string): string {
  const h = hex.replace('#', '');
  const n = (i: number) => parseInt(h.slice(i, i + 2), 16);
  return `rgb(${n(0)}, ${n(2)}, ${n(4)})`;
}

// Счётник обращений к админским ручкам панели (по ним проверяем «не-админ не опрашивает»).
function watchAdminApi(page: Page): string[] {
  const hits: string[] = [];
  // Админские ручки: /transcription, /stats (не /stats/media — она доступна и не-админу), /hardware
  page.on('request', r => {
    const u = r.url();
    if (/\/api\/local-model\/transcription/.test(u) || /\/api\/local-model\/stats(?![/.])/.test(u) || /\/api\/local-model\/hardware/.test(u)) hits.push(u);
  });
  return hits;
}

async function snapshot(): Promise<Record<string, any>> {
  return await client.get<Record<string, any>>('/api/local-model/transcription');
}

// ─── setup / teardown ─────────────────────────────────────────────────────────────
test.beforeAll(async ({ playwright, baseURL }) => {
  request = await playwright.request.newContext({ baseURL });
  token = await login(request);
  client = api(request, token);
  seeded = seed(client);
  originalRoot = String(JSON.parse(readFileSync(CONF, 'utf8')).Transcription?.PipelineRoot ?? '');
  const project = await seeded.project(`${PREFIX}p-${Date.now()}`, `/tmp/${PREFIX}${Date.now()}`);
  projectId = project.id;
});

test.afterAll(async () => {
  // Конфиг возвращаем как был: он машинный, после прогона на нём может работать кто-то ещё.
  try {
    const cfg = JSON.parse(readFileSync(CONF, 'utf8'));
    cfg.Transcription = { ...(cfg.Transcription ?? {}), PipelineRoot: originalRoot };
    writeFileSync(CONF, JSON.stringify(cfg, null, 2));
  } catch { /* best-effort */ }
  await seeded?.cleanup();
  await request?.dispose();
});

// ─────────────────────────────────────────────────────────────────────────────────
// 1. Живые данные worker: точка горит, не открывая вкладку; «Пишет в» не туда — жёлтая;
//    опрос живёт, пока открыта ПАНЕЛЬ (не зависит от активной вкладки).
//    (закрытые дефекты 1fb0da0a, a51824d0)
test('П1. Живой worker: точка «Встреч» без открытия вкладки, жёлтая «Пишет в», опрос при закрытой вкладке', async ({ page }) => {
  const errors = collectConsoleErrors(page);
  await useRoot(LIVE_ROOT, '/srv/ccs/home/andrey/Встречи');
  const s = await snapshot();
  // Честность сигнала на живых данных: маркера output_host.tsv нет, последний локальный
  // «ok» старше свежих .md в каталоге вывода → бэкенд обязан сказать «не этот узел»
  // (OutputHost=null при известном OutputDir) и уровень warn.
  console.log('LIVE SNAPSHOT:', JSON.stringify({ level: s.level, phase: s.phase, outputDir: s.outputDir, outputHost: s.outputHost, host: s.host }));
  expect(s.outputDir, 'каталог вывода known').toBe('/srv/ccs/home/andrey/Встречи');
  expect(s.outputHost, 'чужой узел: OutputHost=null').toBeNull();
  expect(s.level, 'тревога warn на живых данных').toBe('warn');

  await openAs(page, 'admin', 'text');
  const hits = watchAdminApi(page);
  const panel = await openPanel(page);

  // Оставаясь на «LLM»: опрос снимка идёт (интервал 5 с) и точка на «Встречах» горит.
  await page.waitForTimeout(12_000);
  const polls = hits.filter(u => u.includes('/transcription')).length;
  expect(polls, 'опрос снимка живёт при активной другой вкладке').toBeGreaterThanOrEqual(2);

  const warn = rgbOf(await themeColor(panel, '--c-warning'));
  // Наведение на карточку — чтобы лента вкладок стала видимой (поведение PanelShell).
  await revealSwitcher(page, panel);
  const dot = await dotOnTab(page, 'Встречи');
  expect(dot, 'точка на «Встречах» без открытия вкладки').not.toBeNull();
  expect(dot, 'цвет точки — warning').toBe(warn);

  // Открыть «Встречи»: строка «Пишет в» — жёлтая, с «не та машина».
  await page.getByRole('button', { name: 'Встречи', exact: true }).click();
  const line = 'не та машина';
  await expect(panel.getByText(line).first(), 'жёлтая строка «не та машина»').toBeVisible({ timeout: 20_000 });
  const color = await colorOfText(panel, line);
  // KvRow tone='warn' красит значение C.warningText (не самим C.warning)
  expect(color, 'цвет строки — warningText').toBe(rgbOf(await themeColor(panel, '--c-warning-text')));

  // Закрыть панель — опрос прекращается (не при уходе с вкладки, а при закрытии панели).
  // Закрытие этой панели — подмена значка в шапке крестиком при наведении на шапку
  // (PanelShell.tsx:230-240 closeMode='icon'): наводимся на заголовок и жмём крестик.
  await page.getByText('Ферма', { exact: true }).first().hover();
  await page.waitForTimeout(500);
  await page.locator('[title="Скрыть панель"]').first().click({ timeout: 15_000 });
  await expect(page.locator('[role="complementary"][aria-label="Ферма"]'), 'панель закрылась').toHaveCount(0);
  await page.waitForTimeout(2000);
  const before = hits.length;
  await page.waitForTimeout(12_000);
  expect(hits.length - before, 'при закрытой панели опроса нет').toBe(0);

  expect(realErrors(errors), 'ошибок консоли нет').toEqual([]);
});

// 2. Та же точка на «Стендах» с любой вкладки (дефект f0b06ce3). Ответ фермы на этой
//    машине level=ok, поэтому состояние фермы мокируется: проверяется поведение фронта.
test('П2. Точка на «Стендах» горит, не открывая вкладку (мок фермы level=warn)', async ({ page }) => {
  const errors = collectConsoleErrors(page);
  await page.route('**/api/local-model/hardware/farm', route =>
    route.fulfill({ json: { at: Date.now(), level: 'warn', farmNodes: [{ key: 'worker', title: 'worker', roles: ['text'], engines: [], media: false, status: 'ok', since: null, level: 'warn', hottest: null, gpus: [], uptimeS: 100, cpuLoadPerc: 1, ramUsedPerc: 10 }] } }));
  await openAs(page, 'admin', 'text');
  const panel = await openPanel(page);
  await page.waitForTimeout(3000);
  await revealSwitcher(page, panel);
  const warn = rgbOf(await themeColor(panel, '--c-warning'));
  const dot = await dotOnTab(page, 'Стенды');
  expect(dot, 'точка на «Стендах» без открытия вкладки').not.toBeNull();
  expect(dot, 'цвет точки — warning').toBe(warn);
  expect(realErrors(errors), 'ошибок консоли нет').toEqual([]);
});

// 3. Нет снимка (state.json отсутствует) — «не проверялось», а не «Остановлен»/«Процесса нет.»
//    (дефект 6fd686e3)
test('П3. Нет state.json: «не проверялось», а не «Остановлен»', async ({ page }) => {
  const errors = collectConsoleErrors(page);
  const f = fixture('no-state', { state: null });
  await useRoot(f.root, f.outDir);
  const s = await snapshot();
  expect(s.level ?? null, 'уровня нет — «не проверялось»').toBeNull();
  await openAs(page, 'admin', 'transcription');
  const panel = await openPanel(page);
  await expect(panel.getByText('не проверялось').first(), 'плашка «не проверялось»').toBeVisible({ timeout: 20_000 });
  await expect(panel.getByText(/Снимок состояния \(state\.json\) не читается/), 'честная строка о снимке').toBeVisible();
  await expect(panel.getByText('Остановлен'), '«Остановлен» нет').toHaveCount(0);
  await expect(panel.getByText('Процесса нет.'), '«Процесса нет.» нет').toHaveCount(0);
  expect(realErrors(errors), 'ошибок консоли нет').toEqual([]);
});

// 4. Возраст снимка < 5 с — форма «N с назад», не «только что» (дефект 518cadeb)
test('П4. «Обновлено N с назад» при возрасте < 5 с', async ({ page }) => {
  const errors = collectConsoleErrors(page);
  const t = Date.now() - 3_000;
  const f = fixture('fresh', { state: { phase: 'ожидание', queue: 0, queue_items: [], updated: stamp(t), pid: process.pid } });
  await useRoot(f.root, f.outDir);
  const s = await snapshot();
  expect(s.level, 'свежий снимок — уровня тревоги нет').not.toBe('warn');
  await openAs(page, 'admin', 'transcription');
  const panel = await openPanel(page);
  const row = panel.getByText('Обновлено', { exact: true }).first();
  await expect(row, 'строка «Обновлено»').toBeVisible({ timeout: 20_000 });
  const value = await row.evaluate(el => (el.parentElement?.textContent || '').replace('Обновлено', '').trim());
  console.log('AGE TEXT:', value);
  expect(value, 'форма — «N с назад»').toMatch(/^\d+ с назад$/);
  expect(value, '«только что» нет').not.toContain('только что');
  expect(realErrors(errors), 'ошибок консоли нет').toEqual([]);
});

// 5. «идёт N мин» считает от начала операции (stage_started_at), а не от возраста снимка
//    (доработка Б1 3453297a3)
test('П5. «идёт 23 мин» по началу операции, а не по возрасту снимка', async ({ page }) => {
  const errors = collectConsoleErrors(page);
  const now = Date.now();
  const stageStart = Math.floor((now - 23 * 60_000) / 1000);
  const f = fixture('stage', {
    state: { phase: 'транскрибация', current: 'qa2-длинная-встреча.mp4', folder: 'Video-ГПБ', queue: 0, queue_items: [], updated: stamp(now - 20_000), stage_started_at: stageStart, pid: process.pid },
  });
  await useRoot(f.root, f.outDir);
  const s = await snapshot();
  expect(s.stageStartedAt, 'метка начала в снимке есть').toBe(stageStart * 1000);
  await openAs(page, 'admin', 'transcription');
  const panel = await openPanel(page);
  const running = panel.getByText(/идёт\s+\d+/).first();
  await expect(running, 'строка «идёт …»').toBeVisible({ timeout: 20_000 });
  const text = await running.evaluate(el => (el.textContent || '').trim());
  console.log('RUNNING TEXT:', text);
  expect(text, 'длительность — от начала операции (23 мин), не 20 с').toMatch(/идёт\s+2[0-9]\s+мин/);
  expect(text, '«меньше минуты» при 23-минутной операции нет').not.toContain('меньше минуты');
  expect(realErrors(errors), 'ошибок консоли нет').toEqual([]);
});

// 6. Маркер конвейера output_host.tsv: своя строка worker гасит тревогу, чужая — даёт жёлтую
//    (М5/Б2, 32637d0cc + b9e73df89)
test('П6. output_host.tsv: своя строка worker гасит тревогу', async ({ page }) => {
  const errors = collectConsoleErrors(page);
  const now = Date.now();
  const own = fixture('marker-own', {
    state: { phase: 'ожидание', queue: 0, queue_items: [], updated: stamp(now - 3_000), pid: process.pid },
    // в каталоге есть и строка чужого узла, но больший seq — за worker: он писал последним
    marker: [['ai', 2], ['worker', 5]],
  });
  await useRoot(own.root, own.outDir);
  const sOwn = await snapshot();
  expect(sOwn.outputHost, 'последним в каталог писал worker → свой узел').toBe('worker');
  expect(sOwn.level ?? null, 'тревоги нет').not.toBe('warn');
  await openAs(page, 'admin', 'transcription');
  const panel = await openPanel(page);
  await expect(panel.getByText('Конвейер', { exact: true }).first()).toBeVisible({ timeout: 20_000 });
  await expect(panel.getByText('не та машина'), 'жёлтой строки нет').toHaveCount(0);
  const line = await panel.getByText('Пишет в', { exact: true }).first().evaluate(el => (el.parentElement?.textContent || '').trim());
  console.log('WRITES LINE:', line);
  expect(line, 'строка называет свой узел и каталог').toContain('worker');
  expect(realErrors(errors), 'ошибок консоли нет').toEqual([]);
});

test('П6б. output_host.tsv: чужой узел с большим seq — жёлтая строка', async ({ page }) => {
  const errors = collectConsoleErrors(page);
  const foreign = fixture('marker-foreign', {
    state: { phase: 'ожидание', queue: 0, queue_items: [], updated: stamp(Date.now() - 3_000), pid: process.pid },
    marker: [['ai', 7], ['worker', 3]],
  });
  await useRoot(foreign.root, foreign.outDir);
  const sF = await snapshot();
  expect(sF.outputHost, 'последний писавший — чужой узел').toBeNull();
  expect(sF.level, 'уровень warn').toBe('warn');
  await openAs(page, 'admin', 'transcription');
  const panel = await openPanel(page);
  await expect(panel.getByText('не та машина').first(), 'жёлтая строка при чужом узле').toBeVisible({ timeout: 20_000 });
  expect(await colorOfText(panel, 'не та машина'), 'цвет — warningText').toBe(rgbOf(await themeColor(panel, '--c-warning-text')));
  expect(realErrors(errors), 'ошибок консоли нет').toEqual([]);
});

// 7. Пояснение «пишет не туда» называет узел «worker» и не содержит «этот узел»;
//    жёлтая строка не исчезает при баннере дефицита Dify (Ф3д, 1086fa101)
test('П7. Баннер дефицита Dify: «не на узле «worker»», без «этот узел»; жёлтая строка рядом', async ({ page }) => {
  const errors = collectConsoleErrors(page);
  const now = Date.now();
  const f = fixture('dify-deficit', {
    state: { phase: 'ожидание', queue: 0, queue_items: [], updated: stamp(now - 3_000), pid: process.pid },
    // своего «ok» в журнале час назад, а .md в выводе — 10 минут назад → писал не этот конвейер
    journal: [
      `${stampMin(now - 60 * 60_000)}\tok\tVideo-ГПБ\tqa2-старая.mp4\t12.5\t2\tok`,
      `${stampMin(now - 20 * 60_000)}\tfail\tVideo-Сиам\t-\t-\t-\tпапка недоступна: отказано в доступе`,
    ],
    mdFiles: [{ path: 'ГПБ/qa2-чужая.mp4.md', mtimeMs: now - 10 * 60_000 }],
    dify: { checked: Math.floor((now - 5 * 60_000) / 1000), folders: [{ folder: 'Video-ГПБ', local: 10, inDify: 8, checked: Math.floor((now - 5 * 60_000) / 1000) }] },
  });
  await useRoot(f.root, f.outDir);
  const s = await snapshot();
  expect(s.outputHost, 'чужой вывод').toBeNull();
  expect(s.level, 'warn и по выводу, и по дефициту Dify').toBe('warn');
  await openAs(page, 'admin', 'transcription');
  const panel = await openPanel(page);
  await expect(panel.getByText('2 встречи не попали в Dify').first(), 'баннер дефицита').toBeVisible({ timeout: 20_000 });
  const body = await panel.evaluate(el => el.textContent || '');
  console.log('BANNER:', body);
  expect(body, 'пояснение на названном узле').toContain('Конвейер пишет не на узле «worker»');
  expect(body, 'путаного «этот узел» нет').not.toContain('этот узел');
  await expect(panel.getByText('не та машина').first(), 'жёлтая строка остаётся при баннере').toBeVisible();
  expect(realErrors(errors), 'ошибок консоли нет').toEqual([]);
});

// 8. Баннер «Сервис остановлен» — команда системного сервиса (дефект 94d456bd)
test('П8. Баннер остановленного сервиса: sudo systemctl, не systemctl --user', async ({ page }) => {
  const errors = collectConsoleErrors(page);
  const now = Date.now();
  const f = fixture('stopped', {
    state: { phase: 'остановлен', queue: 0, queue_items: [], updated: stamp(now - 60 * 60_000), pid: process.pid },
  });
  await useRoot(f.root, f.outDir);
  const s = await snapshot();
  expect(s.phase, 'фаза stopped').toBe('stopped');
  await openAs(page, 'admin', 'transcription');
  const panel = await openPanel(page);
  await expect(panel.getByText(/Сервис transcription-supervisor остановлен/).first(), 'баннер').toBeVisible({ timeout: 20_000 });
  const body = await panel.evaluate(el => el.textContent || '');
  console.log('STOP BANNER:', body);
  expect(body, 'команда системного юнита').toContain('sudo systemctl start transcription-supervisor');
  expect(body, 'несуществующей user-команды нет').not.toContain('systemctl --user');
  expect(realErrors(errors), 'ошибок консоли нет').toEqual([]);
});

// 9. Длительности целыми минутами: «не отчитывался 23 мин», длина встречи «47 мин»/«13 мин»
//    (Ф3 круг 2, a9baea7fd)
test('П9. Целые минуты: баннер «не отчитывался 23 мин», длина встречи «47 мин» и «13 мин»', async ({ page }) => {
  const errors = collectConsoleErrors(page);
  const now = Date.now();
  const f = fixture('hung', {
    // снимок старше порога при живом pid → «Завис?» → баннер «не отчитывался …»
    state: { phase: 'транскрибация', current: 'qa2-зависшая.mp4', folder: 'Video-ГПБ', queue: 0, queue_items: [], updated: stamp(now - 23 * 60_000 - 10_000), stage_started_at: Math.floor((now - 23 * 60_000) / 1000), pid: process.pid },
    journal: [
      `${stampMin(now - 30 * 60_000)}\tok\tVideo-ГПБ\tqa2-длинная.mp4\t47.3\t4\tasr + саммари`,
      `${stampMin(now - 25 * 60_000)}\tok\tVideo-ГПБ\tqa2-короткая.mp4\t12.5\t2\tasr + саммари`,
    ],
  });
  await useRoot(f.root, f.outDir);
  const s = await snapshot();
  expect(s.level, 'danger по «Завис?»').toBe('danger');
  await openAs(page, 'admin', 'transcription');
  const panel = await openPanel(page);
  const bannerTitle = panel.getByText(/Конвейер не отчитывался/).first();
  await expect(bannerTitle, 'баннер «Завис?»').toBeVisible({ timeout: 20_000 });
  const bt = await bannerTitle.evaluate(el => (el.textContent || '').trim());
  console.log('HUNG BANNER:', bt);
  expect(bt, 'целыми минутами').toMatch(/Конвейер не отчитывался 23 мин$/);
  expect(bt, 'секунд в форме нет').not.toMatch(/мин \d+ с/);
  await expect(panel.getByText('47 мин', { exact: true }).first(), 'длина встречи «47 мин»').toBeVisible();
  await expect(panel.getByText('13 мин', { exact: true }).first(), 'длина встречи «13 мин» (12.5)').toBeVisible();
  // «47.3 мин» и «12.5 мин» как дробные значения в строках быть не должно
  expect(await panel.evaluate(el => el.textContent || ''), 'дробных минут в строках нет').not.toMatch(/\d+\.5\s*мин/);
  expect(realErrors(errors), 'ошибок консоли нет').toEqual([]);
});

// 10. Порядок восьми блоков по макету; в шапке «Последних встреч» нет обещания открыть сводку;
//     тап по строке — честный тост (дефект fd7be3fb)
test('П10. Порядок блоков по макету; обещания в шапке нет; тап — тост «пока недоступен»', async ({ page }) => {
  const errors = collectConsoleErrors(page);
  const now = Date.now();
  const f = fixture('order', {
    state: { phase: 'ожидание', queue: 1, queue_items: [{ file: 'qa2-ждёт.mp4', folder: 'Video-ГПБ', since: Math.floor((now - 6 * 60_000) / 1000) }], updated: stamp(now - 3_000), pid: process.pid },
    journal: [
      `${stampMin(now - 40 * 60_000)}\tok\tVideo-ГПБ\tqa2-встреча.mp4\t9.2\t3\tok`,
      `${stampMin(now - 20 * 60_000)}\tfail\tVideo-Сиам\tqa2-битый.mp4\t-\t-\twav не читается`,
    ],
    mdFiles: [{ path: 'ГПБ/qa2-встреча.md', mtimeMs: now - 40 * 60_000 }],
    dify: { checked: Math.floor((now - 5 * 60_000) / 1000), folders: [{ folder: 'Video-ГПБ', local: 10, inDify: 9, checked: Math.floor((now - 5 * 60_000) / 1000) }] },
  });
  await useRoot(f.root, f.outDir);
  await openAs(page, 'admin', 'transcription');
  const panel = await openPanel(page);
  await expect(panel.getByText('Сверка с Dify').first(), 'все блоки на экране').toBeVisible({ timeout: 20_000 });
  const titles = await cardTitles(panel);
  console.log('TITLES:', JSON.stringify(titles));
  const want = ['Конвейер', 'Сейчас', 'Очередь', 'Сбои за сутки · 1', 'Последние встречи', 'Сверка с Dify', 'Зависимости'];
  const idx = want.map(w => titles.findIndex(t => t === w));
  expect(idx.every(i => i >= 0), `все блоки присутствуют: ${JSON.stringify(titles)}`).toBeTruthy();
  expect(idx, 'порядок — по макету').toEqual([...idx].sort((a, b) => a - b));
  await expect(panel.getByText(/Сверка с Dify.*раз в 30 мин/), 'в заголовке сверки — когда была проверка').toBeVisible();

  // Обещания открыть сводку в шапке блока нет.
  const recentTitle = panel.getByText('Последние встречи', { exact: true }).first();
  const head = await recentTitle.evaluate(el => (el.parentElement?.textContent || ''));
  expect(head, 'подписи «касание — открыть сводку» в шапке нет').not.toContain('касание');
  expect(head, 'никакого обещания в шапке нет').not.toMatch(/открыть|сводк/);

  // Тап по строке встречи — честный тост.
  const row = panel.locator('[aria-label^="Открыть сводку"]').first();
  await expect(row, 'строка встречи доступна касанию').toBeVisible();
  await row.click();
  await expect(page.getByText('Файл встречи пока недоступен в приложении'), 'тост').toBeVisible({ timeout: 10_000 });
  expect(realErrors(errors), 'ошибок консоли нет').toEqual([]);
});

// 11. Пустые состояния не рисуют лишних блоков: нет сбоев/встреч/расхождений — нет карточек
test('П11. Пустые состояния: карточек «Сбои», «Последние встречи», «Сверка» нет', async ({ page }) => {
  const errors = collectConsoleErrors(page);
  const now = Date.now();
  const f = fixture('empty', {
    state: { phase: 'ожидание', queue: 0, queue_items: [], updated: stamp(now - 3_000), pid: process.pid },
  });
  await useRoot(f.root, f.outDir);
  const s = await snapshot();
  expect(s.level, 'пустой живой конвейер — тревоги нет').toBe('ok');
  await openAs(page, 'admin', 'transcription');
  const panel = await openPanel(page);
  await expect(panel.getByText('Конвейер', { exact: true }).first()).toBeVisible({ timeout: 20_000 });
  await expect(panel.getByText('Сейчас', { exact: true }).first()).toBeVisible();
  await expect(panel.getByText('Очередь', { exact: true }).first()).toBeVisible();
  await expect(panel.getByText('Зависимости', { exact: true }).first()).toBeVisible();
  await expect(panel.getByText(/Сбои за сутки/), 'блока сбоев нет').toHaveCount(0);
  await expect(panel.getByText('Последние встречи'), 'блока встреч нет').toHaveCount(0);
  await expect(panel.getByText('Сверка с Dify'), 'блока сверки нет').toHaveCount(0);
  expect(realErrors(errors), 'ошибок консоли нет').toEqual([]);
});

// 12. Не-админ с сохранённой вкладкой «Встречи»: рабочая панель («Медиа»), а не «Не удалось
//     получить данные»; переключателя и мёртвой ссылки «→ Стенды» нет; админские ручки не опрашиваются
//     (дефект 1442e856, доработка b2ab7a34)
test('П12. Не-админ: рабочая панель, ни админских вкладок, ни админских запросов', async ({ browser }) => {
  const created = await client.post<{ id: string }>('/api/users', { username: `${PREFIX}user`, password: 'qa2-pass-1234', role: 'user' });
  expect(created?.id, 'пользователь создан').toBeTruthy();
  const context = await browser.newContext();
  const page = await context.newPage();
  const errors = collectConsoleErrors(page);
  const hits = watchAdminApi(page);
  let own: { id?: string } | undefined;
  let userToken = '';
  try {
    // Токен — своего пользователя: вход под qa2-user (не админский).
    const loginR = await (await context.request.post('/api/auth/login', { data: { username: `${PREFIX}user`, password: 'qa2-pass-1234' } })).json();
    userToken = loginR.token as string;
    expect(userToken, 'вход не-админа').toBeTruthy();
    // Свой проект не-адмена: админский проект ему недоступен, вести его туда нельзя —
    // тогда он не попадает ни в какую страницу с рельсой, и проверка ничего не проверяет.
    const pr = await context.request.post('/api/projects', {
      data: { name: `${PREFIX}u-p`, rootPath: `${FIXROOT}/${PREFIX}u-p`, createDirectory: true },
      headers: { Authorization: `Bearer ${userToken}` },
    });
    expect(pr.status(), 'проект не-адмена создан').toBe(201);
    own = await pr.json();
    expect(own?.id, 'id проекта').toBeTruthy();
    // Сохранённая вкладка админа — как после визита админа на этом устройстве.
    await page.addInitScript(([tk]) => {
      localStorage.setItem('cc_token', tk as string);
      localStorage.setItem('cc_role', 'user');
      localStorage.setItem('cc_username', 'qa2-user');
      localStorage.setItem('cc_local_model_tab', 'transcription');
    }, [userToken] as const);
    await page.goto(`/#/project/${own!.id}`);
    const panel = await openPanel(page);
    await page.waitForTimeout(3000);
    const body = await panel.evaluate(el => (el.textContent || ''));
    console.log('NON-ADMIN PANEL:', body.slice(0, 400));
    expect(body, 'сломанного состояния нет').not.toContain('Не удалось получить данные');
    expect(await page.getByRole('button', { name: 'Встречи', exact: true }).count(), 'переключателя «Встречи» нет').toBe(0);
    expect(await page.getByRole('button', { name: 'LLM', exact: true }).count(), 'переключателя «LLM» нет').toBe(0);
    expect(body, 'мёртвой ссылки «→ Стенды» нет').not.toContain('→ Стенды');
    expect(hits, 'админских ручек не-админ не опрашивает').toEqual([]);
    expect(realErrors(errors), 'ошибок консоли нет').toEqual([]);
  } finally {
    await page.close();
    // Чужой проект админ не удалит (404) — только сам владелец.
    if (own?.id) {
      try { await context.request.delete(`/api/projects/${own.id}`, { headers: { Authorization: `Bearer ${userToken}` } }); } catch { /* уборка best-effort */ }
    }
    await context.close();
    rmSync(`${FIXROOT}/${PREFIX}u-p`, { recursive: true, force: true });
    await client.del(`/api/users/${created!.id}`);
  }
});
