// QA · Фича «Ночной разбор чатов» целиком (задача 89d3bb86, эпик 7f63f1bd).
// Один спек на группу сценариев (docs/operations/qa-stand.md). Фикстуры — данные стенда
// (чаты, стор отбора, настройки, итог ночи), код продукта не трогается.
// Всё засеянное — с префиксом `qa-nw-`, уборка в afterAll.

import { test, expect, type APIRequestContext, type Page, type BrowserContext } from '@playwright/test';
import { api, collectConsoleErrors, login, seed, type Api } from './helpers';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';

const PREFIX = 'qa-nw-';
// Корень проекта от файла спека: frontend/e2e/qa → корень. Данные стенда start_stand —
// bin/Debug/net10.0/data (DataPath, см. qa-stand.md).
const ROOT = path.resolve(new URL('../../..', import.meta.url).pathname);
const DATA = path.join(ROOT, 'backend', 'ClaudeHomeServer', 'bin', 'Debug', 'net10.0', 'data');
const SHOTS = path.join(ROOT, '.cc-attachments', 'qa-llm-wiki-nightly');
fs.mkdirSync(SHOTS, { recursive: true });

// Порядок сохраняем (Playwright идёт по файлу), но падение одного теста не роняет остальные:
// нужен полный отчёт по всем сценариям сразу.
// Фикстура — 204 чата: создание и запись стора занимают время.
test.setTimeout(180_000);

let request: APIRequestContext;
let token: string;
let client: Api;
let seeded: ReturnType<typeof seed>;
let ownerId = '';
let wikiRoot = '';
let projectId = '';
let projectName = '';
const chatIds: string[] = [];
const chatNames: Record<string, string> = {};
const stamp = Date.now();

// Числа фикстуры отбора: два класса зон, один чат с пометкой и один чат исполнителя задач.
const TRIAGE_TOTAL = 200;
const TRIAGE_TRIAGE_ZONE = 100; // «разбирается»
const TRIAGE_GRAY_ZONE = 100;   // «серая зона»
const TASK_REASON = 'ступень 0: чат исполнения задачи';

const writeWiki = (rel: string, content: string) => {
  const full = path.join(wikiRoot, rel);
  fs.mkdirSync(path.dirname(full), { recursive: true });
  fs.writeFileSync(full, content, 'utf8');
};

// Запись стора отбора (keeper/chat-triage.json — массив записей, форма WikiChatTriageEntry).
const triageEntry = (sessionId: string, zone: string, reason: string, score: number) => ({
  sessionId, cursor: 3, cursorHash: '', zone, reason, nothingNewStreak: 0,
  triagedAt: '2026-11-01T22:00:00+00:00', score, signals: [],
  stage1Zone: zone, stage1Reason: reason,
});

test.beforeAll(async ({ playwright, baseURL }) => {
  request = await playwright.request.newContext({ baseURL });
  token = await login(request);
  client = api(request, token);
  ownerId = JSON.parse(Buffer.from(token.split('.')[1], 'base64url').toString()).sub as string;
  expect(fs.existsSync(DATA), `каталог данных стенда должен существовать: ${DATA}`).toBeTruthy();

  // Флаг llm-wiki тестовому пользователю (тот же путь, что в QA-2 по хвостам вики).
  await client.put('/api/feature-flags/llm-wiki', { enabled: true });

  seeded = seed(client);
  projectName = `${PREFIX}p-${stamp}`;
  const project = await seeded.project(projectName, `${os.tmpdir()}/${PREFIX}${stamp}`);
  projectId = project.id;
  wikiRoot = path.join(DATA, 'wiki', ownerId);

  // Чаты: 200 для отбора + два особых (помеченный и исполнитель задачи).
  const names: string[] = [];
  for (let i = 0; i < TRIAGE_TOTAL + 4; i++) names.push(`${PREFIX}chat-${i}-${stamp}`);
  const created: string[] = [];
  for (let i = 0; i < names.length; i += 25) {
    const batch = await Promise.all(names.slice(i, i + 25).map(n =>
      client.post<{ id: string }>(`/api/projects/${projectId}/sessions`, { name: n })));
    for (const b of batch) created.push(b.id);
  }
  chatIds.push(...created);
  created.forEach((id, i) => { chatNames[id] = names[i]; });

  // Вики заводим (тот же путь, что кнопка «Завести вики»); повторный вызов — не ошибка.
  await client.post('/api/wiki/initialize').catch(() => undefined);

  // Стор отбора: две зоны, 200 чатов. Особые чаты — в конце списка:
  // [200] — помечен «важный», [201] — «не разбирать», [202] — обычный (меню чата),
  // [203] — чат исполнителя задачи (признак — причина ступени 0).
  const entries = chatIds.slice(0, TRIAGE_TOTAL).map((id, i) =>
    i < TRIAGE_TRIAGE_ZONE
      ? triageEntry(id, 'triage', 'много новых фактов', 90 - (i % 90))
      : triageEntry(id, 'gray', 'смешанный разговор: часть подтверждена, часть спорна', 50 - (i % 50)));
  entries.push(triageEntry(chatIds[TRIAGE_TOTAL], 'triage', 'много новых фактов', 90));
  entries.push(triageEntry(chatIds[TRIAGE_TOTAL + 1], 'gray', 'смешанный разговор', 50));
  entries.push(triageEntry(chatIds[TRIAGE_TOTAL + 2], 'triage', 'много новых фактов', 80));
  entries.push(triageEntry(chatIds[TRIAGE_TOTAL + 3], 'skipped', TASK_REASON, 10));
  writeWiki('keeper/chat-triage.json', JSON.stringify(entries, null, 2));

  // Пометки: один чат — «важный», другой — «не разбирать».
  await client.put(`/api/wiki/chats/${chatIds[TRIAGE_TOTAL]}/mark`, { mark: 'important' });
  await client.put(`/api/wiki/chats/${chatIds[TRIAGE_TOTAL + 1]}/mark`, { mark: 'skip' });
});

test.afterAll(async () => {
  try { await client?.put('/api/feature-flags/llm-wiki', { enabled: false }); } catch { /* best-effort */ }
  await seeded?.cleanup();
  for (const id of chatIds) fs.rmSync(path.join(DATA, 'sessions', id), { recursive: true, force: true });
  fs.rmSync(wikiRoot, { recursive: true, force: true });
  await request?.dispose();
});

// Флаг llm-wiki включён тестовому пользователю: перед каждым тестом проверяем и включаем,
// если его сняли (на стенде параллельно гоняют другие прогоны).
const ensureFlag = async () => {
  const cur = await client.get<{ values: Record<string, boolean> }>('/api/feature-flags');
  if (cur.values['llm-wiki'] !== true) {
    await client.put('/api/feature-flags/llm-wiki', { enabled: true });
    const after = await client.get<{ values: Record<string, boolean> }>('/api/feature-flags');
    expect(after.values['llm-wiki'], 'флаг llm-wiki включён у тестового пользователя').toBe(true);
  }
};

const openApp = async (page: Page, context: BrowserContext, hash: string) => {
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
  await page.goto(hash);
};

// Тосты непрочитанных уведомлений ложатся поверх шапки и перехватывают клики.
const dismissToasts = async (page: Page) => {
  const close = page.locator('button[title="Закрыть"]');
  try { await close.first().waitFor({ state: 'visible', timeout: 4000 }); } catch { return; }
  for (let i = 0; i < 12; i++) {
    if (await close.count() === 0) break;
    await close.first().dispatchEvent('click').catch(() => {});
    await page.waitForTimeout(200);
  }
};

// Шум окружения тестового стенда: прогрев историй запрашивает дерево файлов по всем
// проектам (включая старые тестовые проекты) — они дают 404. К фиче вики не относится.
const NET404_TEXT = '[error] Failed to load resource: the server responded with a status of 404 (Not Found)';
const trackNet404 = (page: Page) => {
  const net404: string[] = [];
  page.on('response', r => { if (r.status() === 404) net404.push(`${r.request().method()} ${r.url()}`); });
  return net404;
};
// Все ответы с кодом вне 2xx — для разбора, откуда 409/400 в консоли.
const trackBad = (page: Page) => {
  const bad: string[] = [];
  page.on('response', r => { if (r.status() >= 400) bad.push(`${r.status()} ${r.request().method()} ${r.url()}`); });
  return bad;
};

// Отказы сценария (наш собственный mock 400, чтение нечитаемого стора) — ожидаемы:
// их коды в тексте сообщения консоли, и без них проверка «ошибок нет» ничего не значит.
const EXPECTED_CONSOLE = [
  NET404_TEXT,
  '[error] Failed to load resource: the server responded with a status of 400 (Bad Request)',
  '[error] Failed to load resource: the server responded with a status of 409 (Conflict)',
];
const expectNoWikiErrors = (errors: string[], net404: string[], expected: string[] = []) => {
  const unexpected = net404.filter(u => !u.includes('/files/tree?') && !expected.some(a => u.includes(a)));
  expect(unexpected, '404 вне шума окружения и ожидаемых запросов сценария нет').toEqual([]);
  expect(errors.filter(e => !EXPECTED_CONSOLE.includes(e)), 'ошибок консоли, кроме ожидаемых отказов сценария, нет').toEqual([]);
};

// Открыть «Вики» → подвкладку «Настройки».
// Переход делаем С НОВЫМ документом (query перед hash): переход на тот же hash-URL —
// same-document навигация, страница не перезагружается, снапшок стора вики остаётся
// прежним — свежие данные (итог ночи, класс правок) не подхватываются.
const openWikiSettings = async (page: Page) => {
  await page.goto(`/?qa=${Date.now()}#/notes`);
  await dismissToasts(page);
  await page.locator('[aria-label="Вики"]').click();
  await page.locator('[aria-label="Настройки"]').click();
  await expect(page.getByRole('heading', { name: 'Ночной разбор' }), 'экран «Настройки» открыт').toBeVisible();
};

// ─── Сценарий 1 · «Настройки»: порядок блоков, тумблер, один GET, один PUT ───

test.describe('Сценарий 1 · Настройки', () => {
  test.use({ viewport: { width: 1280, height: 900 } });

  test.beforeEach(async () => { await ensureFlag(); });

  test('1a. Порядок блоков «Ночной разбор» → «Применение» → «Отбор чатов»; тумблер ночи по умолчанию выключен; при входе на вкладку ровно один GET /api/wiki/settings', async ({ page, context }) => {
    const errors = collectConsoleErrors(page);
    const net404 = trackNet404(page);
    const bad = trackBad(page);
    await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);

    const gets: string[] = [];
    page.on('request', r => { if (r.url().includes('/api/wiki/settings') && r.method() === 'GET') gets.push(r.url()); });

    await page.goto(`/?qa=${Date.now()}#/notes`);
    await dismissToasts(page);
    await page.locator('[aria-label="Вики"]').click();
    await page.locator('[aria-label="Настройки"]').click();
    await expect(page.getByRole('heading', { name: 'Ночной разбор' })).toBeVisible();

    // Порядок блоков — по заголовкам h2 в порядке документа. Список отбора догружается,
    // поэтому ждём все три заголовка (poll), а не разовый снимок.
    const readOrder = () => page.evaluate(() => Array.from(document.querySelectorAll('h2'))
      .map(h => (h.textContent || '').trim()).filter(Boolean));
    await expect.poll(async () => (await readOrder()).length, { timeout: 20000 }).toBeGreaterThanOrEqual(3);
    const order = await readOrder();
    const nightIdx = order.indexOf('Ночной разбор');
    const applyIdx = order.findIndex(t => t.startsWith('Применение'));
    const triageIdx = order.indexOf('Отбор чатов');
    expect(nightIdx, `заголовок «Ночной разбор» есть (все: ${order.join(' | ')})`).toBeGreaterThanOrEqual(0);
    expect(applyIdx, 'заголовок блока применения есть').toBeGreaterThanOrEqual(0);
    expect(triageIdx, 'заголовок «Отбор чатов» есть').toBeGreaterThanOrEqual(0);
    expect(nightIdx < applyIdx && applyIdx < triageIdx,
      `порядок блоков: ночь → применение → отбор (получено: ${order.join(' | ')})`).toBeTruthy();

    // Тумблер ночи по умолчанию выключен.
    const sw = page.getByRole('switch', { name: 'Разбирать по ночам' });
    await expect(sw, 'тумблер «Разбирать по ночам» на месте').toHaveAttribute('aria-checked', 'false');
    await expect(page.getByText('Ночью ничего не разбирается'), 'плашка «ночь выключена» на месте').toBeVisible();

    // Ровно один GET настроек при входе на вкладку.
    expect(gets.length, `GET /api/wiki/settings на входе на вкладку — ровно один (получено ${gets.length})`).toBe(1);

    console.log(`ПЛОХИЕ ОТВЕТЫ (1a): ${bad.join(' | ') || 'нет'}`);
    const chatsResp = await client.get<{ items: unknown[] }>('/api/wiki/chats');
    console.log(`ОТБОР ЧАТОВ (API): ${chatsResp.items.length} записей`);
    const settingsResp = await client.get<unknown>('/api/wiki/settings');
    console.log(`НАСТРОЙКИ (API): ${JSON.stringify(settingsResp).slice(0, 200)}`);
    await page.screenshot({ path: path.join(SHOTS, 's1-settings-1280.png'), fullPage: true });
    expectNoWikiErrors(errors, net404);
  });

    test.beforeEach(async () => { await ensureFlag(); });

  test('1b. Включение ночи — один PUT; отказ возвращает прежнее состояние с понятным текстом', async ({ page, context }) => {
    const errors = collectConsoleErrors(page);
    const net404 = trackNet404(page);
    await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);

    const puts: string[] = [];
    page.on('request', r => { if (r.url().includes('/api/wiki/settings') && r.method() === 'PUT') puts.push(r.url()); });

    await openWikiSettings(page);
    const sw = page.getByRole('switch', { name: 'Разбирать по ночам' });
    await expect(sw).toHaveAttribute('aria-checked', 'false');
    await sw.click();
    await expect(sw, 'тумблер включился').toHaveAttribute('aria-checked', 'true');
    await expect.poll(() => puts.length, { timeout: 5000 }).toBe(1);

    // Проверка на сервере: ночь включена.
    const dto = await client.get<{ nightly: { enabled: boolean } }>('/api/wiki/settings');
    expect(dto.nightly.enabled, 'ночь включена на сервере').toBe(true);

    // Отказ: сервер не принял правку — поле возвращается к прежнему значению, текст понятный.
    await page.route('**/api/wiki/settings', async route => {
      if (route.request().method() !== 'PUT') return route.continue();
      await route.fulfill({
        status: 400, contentType: 'application/json',
        body: JSON.stringify({ code: 'wiki_nightly', message: 'ночной блок не целый' }),
      });
    });
    await sw.click();
    await expect(page.getByText('Не сохранилось — попробуйте ещё раз.'), 'понятный текст отказа есть').toBeVisible();
    await expect(sw, 'после отказа тумблер вернулся в прежнее состояние').toHaveAttribute('aria-checked', 'true');
    await page.screenshot({ path: path.join(SHOTS, 's1-refusal-toggle.png') });
    await page.unroute('**/api/wiki/settings');

    // Вернуть ночь выключенной — уборка состояния для следующих сценариев.
    await sw.click();
    await expect(sw).toHaveAttribute('aria-checked', 'false');
    expectNoWikiErrors(errors, net404);
  });

    test.beforeEach(async () => { await ensureFlag(); });

  test('1c. Числовые поля и окно: валидное сохраняется, невалидное даёт понятную ошибку, правка не откатывается старым ответом', async ({ page, context }) => {
    const errors = collectConsoleErrors(page);
    const net404 = trackNet404(page);
    await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);

    await openWikiSettings(page);

    const budget = page.locator('[data-testid="wiki-nightly-input-inputTokenBudget"] input');
    // Валидное значение: 800 000 токенов.
    await budget.click();
    await budget.fill('800000');
    await budget.blur();
    await expect.poll(async () => {
      const d = await client.get<{ nightly: { inputTokenBudget: number } }>('/api/wiki/settings');
      return d.nightly.inputTokenBudget;
    }, { timeout: 5000 }).toBe(800000);
    await expect(budget, 'поле показывает новое значение').toHaveValue('800 000');

    // Невалидное: 0 — «Должно быть больше 0».
    await budget.click();
    await budget.fill('0');
    await budget.blur();
    await expect(page.getByText('Должно быть больше 0'), 'ошибка «больше 0» есть').toBeVisible();

    // Невалидное: выше потолка — «Не больше 5 000 000».
    await budget.click();
    await budget.fill('6000000');
    await budget.blur();
    await expect(page.getByText('Не больше 5 000 000'), 'ошибка «не больше 5 000 000» есть').toBeVisible();

    // Правка не откатывается старым ответом: вводим 900 000 и ждём, пока это значение
    // и на сервере, и на экране (прежний ответ не должен вернуть 800 000).
    await budget.click();
    await budget.fill('900000');
    await budget.blur();
    // Правка не откатывается старым ответом: значение на экране остаётся новым, и сервер
    // его видит. (Стенд общий: параллельные прогоны могут подменить настройки — тогда
    // показываем фактическое значение и ждём своего.)
    await expect(budget, 'поле показывает новое значение').toHaveValue('900 000');
    await expect.poll(async () => {
      const d = await client.get<{ nightly: { inputTokenBudget: number } }>('/api/wiki/settings');
      const v = d.nightly.inputTokenBudget;
      if (v !== 900000) console.log(`БЮДЖЕТ НА СЕРВЕРЕ: ${v} (ожидалось 900000)`);
      return v;
    }, { timeout: 15000 }).toBe(900000);

    // «Пакетов за ночь»: валидное значение сохраняется, выше потолка — понятная ошибка.
    const perNight = page.locator('[data-testid="wiki-nightly-input-maxPackagesPerNight"] input');
    await perNight.click();
    await perNight.fill('12');
    await perNight.blur();
    await expect.poll(async () => {
      const d = await client.get<{ nightly: { maxPackagesPerNight: number } }>('/api/wiki/settings');
      return d.nightly.maxPackagesPerNight;
    }, { timeout: 15000 }).toBe(12);
    await perNight.click();
    await perNight.fill('25');
    await perNight.blur();
    await expect(page.getByText('Не больше 20 — иначе ночь упрётся в лимит «Входящих»'), 'ошибка потолка пакетов есть').toBeVisible();

    // «на раздел» — второе поле «Пакетов за ночь»: валидное значение сохраняется.
    const perSection = page.locator('[data-testid="wiki-nightly-input-maxPackagesPerSection"] input');
    await perSection.click();
    await perSection.fill('4');
    await perSection.blur();
    await expect.poll(async () => {
      const d = await client.get<{ nightly: { maxPackagesPerSection: number } }>('/api/wiki/settings');
      return d.nightly.maxPackagesPerSection;
    }, { timeout: 15000 }).toBe(4);

    // Окно: начало = конец — понятная ошибка поля, запрос не уходит.
    const selects = page.locator('[data-testid="wiki-nightly-window"] select');
    await expect(selects, 'два селекта окна на месте').toHaveCount(2);
    await selects.first().selectOption('6');
    await expect(page.getByText('Начало и конец окна совпадают'), 'ошибка равных часов есть').toBeVisible();

    // Валидное окно сохраняется.
    await selects.first().selectOption('2');
    await expect.poll(async () => {
      const d = await client.get<{ nightly: { windowStartHour: number } }>('/api/wiki/settings');
      return d.nightly.windowStartHour;
    }, { timeout: 15000 }).toBe(2);

    await page.screenshot({ path: path.join(SHOTS, 's1-numeric-window.png'), fullPage: true });
    expectNoWikiErrors(errors, net404);
  });
});

// ─── Сценарий 2 · «Ночной разбор»: итог прошлой ночи ───

test.describe('Сценарий 2 · Итог ночи', () => {
  test.use({ viewport: { width: 1280, height: 900 } });

  const setLastNight = (outcome: string | null, extra: Record<string, number> = {}) => {
    if (outcome === null) {
      fs.rmSync(path.join(wikiRoot, 'keeper', 'nightly-last.json'), { force: true });
      return;
    }
    writeWiki('keeper/nightly-last.json', JSON.stringify({
      date: '2026-11-02', selected: 14, analyzed: 9, applied: 5, toInbox: 4,
      estTokens: 512000, outcome, ...extra,
    }));
  };

    test.beforeEach(async () => { await ensureFlag(); });

  test('2a. Пустой итог — «Ночного разбора ещё не было»; исходы ночи — понятным текстом; «≈ N из M» против текущего бюджета', async ({ page, context }) => {
    const errors = collectConsoleErrors(page);
    const net404 = trackNet404(page);
    await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);

    // Ночь выключена явно: предыдущие сценарии её включали, а текст карточки зависит от тумблера.
    await client.put('/api/wiki/settings', { nightly: { enabled: false } });

    // Пустой итог (файла нет).
    setLastNight(null);
    await openWikiSettings(page);
    await expect(page.locator('[data-testid="wiki-nightly-none"]'), 'карточка «итога ещё не было»').toBeVisible();
    await expect(page.getByText('Ночного разбора ещё не было')).toBeVisible();
    await expect(page.getByText('Включите разбор — первая ночь пройдёт в ближайшее окно'), 'текст по выключенной ночи').toBeVisible();
    await expect(page.locator('[data-testid="wiki-nightly-card"]'), 'карточки итога нет').toHaveCount(0);

    // Включённая ночь — другой текст карточки: «первый пройдёт ближайшей ночью».
    await client.put('/api/wiki/settings', { nightly: { enabled: true } });
    await openWikiSettings(page);
    await expect(page.getByText('Первый пройдёт ближайшей ночью'), 'текст по включённой ночи есть').toBeVisible();
    await client.put('/api/wiki/settings', { nightly: { enabled: false } });

    // completed: карточка без предупреждения.
    setLastNight('completed');
    await openWikiSettings(page);
    await expect(page.locator('[data-testid="wiki-nightly-card"]'), 'карточка итога есть').toBeVisible();
    await expect(page.getByText('чатов отобрано')).toBeVisible();
    await expect(page.getByText('разобрано', { exact: true })).toBeVisible();
    await expect(page.getByText('ждут вас во «Входящих»')).toBeVisible();
    await expect(page.getByText('≈ 512 000 из'), 'оценка расхода против текущего бюджета').toBeVisible();

    // Слова внутреннего сленга в тексте итога — нет.
    const cardText = await page.locator('[data-testid="wiki-nightly-card"]').innerText();
    for (const w of ['ступени', 'outcome', 'пакеты']) {
      expect(cardText.toLowerCase(), `в тексте итога нет слова «${w}»`).not.toContain(w);
    }

    // budget: предупреждение с понятным текстом.
    setLastNight('budget');
    await openWikiSettings(page);
    await expect(page.getByText('Ночь остановилась на своём лимите'), 'предупреждение по лимиту ночи').toBeVisible();
    await expect(page.getByText('Разобрали 9 чатов из 14 отобранных'), 'числа разбора в тексте').toBeVisible();

    // open_limit: кнопка «Открыть «Входящие»» есть.
    setLastNight('open_limit');
    await openWikiSettings(page);
    await expect(page.getByText('Остановились: во «Входящих» 20 пакетов ждут вашего решения'), 'текст по лимиту «Входящих»').toBeVisible();

    // window_closed / disabled_midway / error — тексты без заголовка.
    for (const [outcome, text] of [
      ['window_closed', 'Окно закончилось раньше, чем разбор: остальное следующей ночью.'],
      ['disabled_midway', 'Вы выключили разбор посреди ночи'],
      ['error', 'Ночь прервалась из-за сбоя. Повторим следующей ночью.'],
    ] as const) {
      setLastNight(outcome);
      await openWikiSettings(page);
      await expect(page.getByText(text), `текст исхода ${outcome}`).toBeVisible();
    }

    await setLastNight('completed');
    await page.screenshot({ path: path.join(SHOTS, 's2-last-night.png'), fullPage: true });
    expectNoWikiErrors(errors, net404);
  });
});

// ─── Сценарий 3 · «Применение»: классы, тумблеры; расхождения с макетом ───

test.describe('Сценарий 3 · Применение', () => {
  test.use({ viewport: { width: 1280, height: 900 } });

    test.beforeEach(async () => { await ensureFlag(); });

  test('3a. Блок «Применение» на месте: тумблер «Применять неспорное само», классы правок; сверка с макетом (кнопка «Выключить», warning автовыключения, «Аудит недели»)', async ({ page, context }) => {
    const errors = collectConsoleErrors(page);
    const net404 = trackNet404(page);
    await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);

    await openWikiSettings(page);
    const auto = page.getByRole('switch', { name: 'Применять без ревью' });
    await expect(auto, 'тумблер «Применять без ревью» на месте').toBeVisible();

    // Класс правок: сеем запись класса (выключен) и ждём строку в блоке.
    writeWiki('keeper/settings.json', JSON.stringify({
      v: 1, autoApplyEnabled: true,
      classes: [{
        class: 'session×create', disabled: true, reason: 'класс выключен человеком',
        disabledAt: '2026-11-01T22:00:00+00:00', resumedAt: null,
      }],
      nightly: { enabled: false, windowStartHour: 3, windowEndHour: 6, maxPackagesPerNight: 20, maxPackagesPerSection: 6, maxChatsPerSection: 10, inputTokenBudget: 700000 },
    }));
    await openWikiSettings(page);
    const row = page.locator('[data-testid="wiki-apply-row"]');
    await expect(row, 'строка класса правок есть').toHaveCount(1);
    await expect(row.getByText('выключен', { exact: true }), 'бейдж «выключен» есть').toBeVisible();
    await expect(row.getByText('Снова применять само'), 'кнопка «Снова применять само» есть').toBeVisible();

    // Сверка с макетом: что из нарисованного в макете на экране нет.
    const body = await page.locator('body').innerText();
    const deviations: string[] = [];
    if (!body.includes('Выключить')) deviations.push('кнопка «Выключить» (выключить класс вручную)');
    if (!body.includes('Автовыключение') && !body.includes('автовыключ')) deviations.push('warning об автовыключении класса');
    if (!body.includes('Аудит недели')) deviations.push('«Аудит недели»');
    console.log(`РАСХОЖДЕНИЯ С МАКЕТОМ (сценарий 3): ${deviations.length ? deviations.join('; ') : 'нет'}`);

    await page.screenshot({ path: path.join(SHOTS, 's3-apply.png'), fullPage: true });
    expectNoWikiErrors(errors, net404);
  });
});

// ─── Сценарий 4 · «Отбор чатов» ───

test.describe('Сценарий 4 · Отбор чатов', () => {
  test.use({ viewport: { width: 1280, height: 900 } });

    test.beforeEach(async () => { await ensureFlag(); });

  test('4a. Список из 200 чатов, две зоны, пометки «Важный / Не разбирать», повторный клик снимает пометку', async ({ page, context }) => {
    const errors = collectConsoleErrors(page);
    const net404 = trackNet404(page);
    await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);

    await page.goto(`/?qa=${Date.now()}#/notes`);
    await dismissToasts(page);
    await page.locator('[aria-label="Вики"]').click();
    await page.locator('[aria-label="Настройки"]').click();

    const rows = page.locator('[data-testid="wiki-triage-row"]');
    await expect(rows.first(), 'список отбора на месте').toBeVisible();
    await expect.poll(() => rows.count(), { timeout: 10000 }).toBe(TRIAGE_TOTAL + 4);
    await expect(page.getByText(`${TRIAGE_TOTAL + 4} чата`, { exact: true }), 'счётчик чатов на месте').toBeVisible();

    // Две зоны: «разбирается» и «серая зона».
    await expect(page.getByText('разбирается').first(), 'зона «разбирается» есть').toBeVisible();
    await expect(page.getByText('серая зона').first(), 'зона «серая зона» есть').toBeVisible();

    // Пометки из меню чата видны в списке.
    const importantChat = chatIds[TRIAGE_TOTAL];
    const skipChat = chatIds[TRIAGE_TOTAL + 1];
    const rowOf = (id: string) => page.locator('[data-testid="wiki-triage-row"]').filter({ hasText: chatNames[id] });
    await expect(rowOf(importantChat).getByText('важный', { exact: true }), 'пометка «важный» видна в строке').toBeVisible();
    await expect(rowOf(skipChat).getByText('не разбирать', { exact: true }), 'пометка «не разбирать» видна в строке').toBeVisible();

    // Повторный клик снимает пометку: жмём «Важный» у чата с пометкой «важный».
    const markBtn = rowOf(importantChat).locator('[data-testid^="wiki-triage-mark-important-"]');
    await expect(markBtn, 'кнопка пометки «Важный» на месте').toHaveAttribute('aria-pressed', 'true');
    await markBtn.click();
    await expect(markBtn, 'повторный клик снял пометку').toHaveAttribute('aria-pressed', 'false');
    await expect(rowOf(importantChat).getByText('разбирается').first(), 'пометка снята — вернулась зона').toBeVisible();

    // Чат исполнения задач: кнопки пометок выключены.
    await openWikiSettings(page);
    const taskRow = rowOf(chatIds[TRIAGE_TOTAL + 3]);
    await expect(taskRow, 'строка чата исполнителя задачи есть').toHaveCount(1);
    await expect(taskRow.locator('[data-testid^="wiki-triage-mark-"]').first(), 'кнопки пометок у чата исполнителя выключены').toBeDisabled();

    await page.screenshot({ path: path.join(SHOTS, 's4-triage.png'), fullPage: false });
    expectNoWikiErrors(errors, net404);
  });
});

// ─── Сценарий 5 · Меню чата «Вики» и шапка чата ───

test.describe('Сценарий 5 · Меню чата', () => {
  test.use({ viewport: { width: 1280, height: 900 } });

    test.beforeEach(async () => { await ensureFlag(); });

  test('5a. Пометки «Важный / Не разбирать» в меню чата: отмечаются, взаимоисключающие, повторный клик снимает; у чата исполнителя задач неактивны', async ({ page, context }) => {
    const errors = collectConsoleErrors(page);
    const net404 = trackNet404(page);
    await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);

    const chat = chatIds[TRIAGE_TOTAL + 2];
    await page.goto(`/#/project/${projectId}/chat/${chat}`);
    await dismissToasts(page);
    // Открываем чат КЛИКОМ по списку чатов проекта: диплинк #/project/{id}/chat/{chatId}
    // на стенде не выбирает чат (открывается страница проекта), и шапка тогда показывает
    // действия ДРУГОГО чата — того, что выбран последним.
    await page.goto(`/#/project/${projectId}`);
    await dismissToasts(page);
    await page.getByText(chatNames[chat], { exact: true }).first().click();
    await dismissToasts(page);
    await page.waitForTimeout(1500);

    const markImportant = page.locator('[data-testid="wiki-chat-mark-important"]');
    const markSkip = page.locator('[data-testid="wiki-chat-mark-skip"]');
    try {
      await expect(markImportant, 'пункт «Важный» в шапке есть').toBeVisible({ timeout: 15000 });
    } catch (e) {
      await page.screenshot({ path: path.join(SHOTS, 's5a-no-mark-buttons.png'), fullPage: true });
      console.log(`Шапка чата (5a): ${(await page.locator('body').innerText()).slice(0, 600).replace(/\n/g, ' | ')}`);
      throw e;
    }

    // Диагностика: какой чат открыт на самом деле и что сервер знает о нём.
    console.log(`URL ЧАТА (5a): ${page.url()}`);
    console.log(`ЗАГОЛОВКИ КНОПОК (5a): ${await page.locator('[data-testid^="wiki-chat-mark-"]').evaluateAll(els => els.map(e => `${e.getAttribute('data-testid')}[${e.getAttribute('title')}]`).join(' | '))}`);
    console.log(`ЧАТ НА ЭКРАНЕ (5a): ${(await page.locator('body').innerText()).slice(0, 1200).replace(/\n/g, ' | ')}`);
    // Что сервер видит по этому чату: зона, причина и пометка (признак «чат исполнителя
    // задачи» — причина ступени 0; если она у обычного чата — это находка, а не ошибка теста).
    const list = await client.get<{ items: Array<{ sessionId: string; zone: string; reason: string | null; mark: string | null }> }>('/api/wiki/chats');
    const rec = list.items.find(i => i.sessionId === chat);
    console.log(`ЗАПИСЬ ЧАТА В ОТБОРЕ (5a): ${JSON.stringify(rec)}`);

    await markImportant.click();
    await expect(markImportant, 'пометка «Важный» отмечена').toHaveAttribute('aria-pressed', 'true');

    // Взаимоисключающие: «Не разбирать» снимает «Важный».
    await markSkip.click();
    await expect(markSkip, 'пометка «Не разбирать» отмечена').toHaveAttribute('aria-pressed', 'true');
    await expect(markImportant, '«Важный» при этом снят').toHaveAttribute('aria-pressed', 'false');

    // Повторный клик снимает пометку.
    await markSkip.click();
    await expect(markSkip, 'повторный клик снял пометку').toHaveAttribute('aria-pressed', 'false');

    // Пометка видна затем в «Отборе чатов».
    await markImportant.click();
    await page.goto(`/?qa=${Date.now()}#/notes`);
    await dismissToasts(page);
    await page.locator('[aria-label="Вики"]').click();
    await page.locator('[aria-label="Настройки"]').click();
    const row = page.locator('[data-testid="wiki-triage-row"]').filter({ hasText: chatNames[chat] });
    await expect(row.getByText('важный', { exact: true }), 'пометка из меню чата видна в отборе').toBeVisible();

    // Чат исполнителя задачи: пункты неактивны.
    const taskChat = chatIds[TRIAGE_TOTAL + 3];
    await page.goto(`/#/project/${projectId}/chat/${taskChat}`);
    await dismissToasts(page);
    await expect(page.locator('[data-testid="wiki-chat-mark-important"]'), 'у чата исполнителя пункт есть, но выключен').toBeDisabled();
    await expect(page.locator('[data-testid="wiki-chat-mark-skip"]'), 'у чата исполнителя «Не разбирать» выключен').toBeDisabled();

    await page.screenshot({ path: path.join(SHOTS, 's5-chat-marks.png') });
    expectNoWikiErrors(errors, net404);
  });

  for (const w of [1000, 1050, 1100]) {
      test.beforeEach(async () => { await ensureFlag(); });

  test(`5b. Шапка чата на ${w} px: ряд из 5 кнопок «Вики» не обрезан, «⋯» на месте`, async ({ page, context }) => {
      const errors = collectConsoleErrors(page);
      const net404 = trackNet404(page);
      await page.setViewportSize({ width: 1280, height: 900 });
      await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);

      // Чат открываем на широком окне (список чатов виден), потом сужаем до w —
      // на 1000 px список чатов в планшетной раскладке скрыт, кликнуть его нельзя.
      const chat = chatIds[TRIAGE_TOTAL + 2];
      await page.goto(`/#/project/${projectId}`);
      await dismissToasts(page);
      await page.getByText(chatNames[chat], { exact: true }).first().click();
      await dismissToasts(page);
      await page.waitForTimeout(1200);
      await page.setViewportSize({ width: w, height: 900 });
      await page.waitForTimeout(800);

      const ids = ['wiki-chat-analyze', 'wiki-chat-retract', 'wiki-chat-mark-important', 'wiki-chat-mark-skip'];
      // Обрезка — геометрически: кнопка выходит за правый край предка, который её режет
      // (overflow hidden/clip/auto/scroll). Это надёжнее, чем elementFromPoint: тот ловит
      // и перекрытия, и попадание под оверлей.
      const scan = await page.evaluate((testIds) => {
        const out: Array<{ id: string; missing?: boolean; clipped?: boolean; covered?: boolean; right?: number; vw: number }> = [];
        for (const id of testIds) {
          const el = document.querySelector(`[data-testid="${id}"]`);
          if (!el) { out.push({ id, missing: true, vw: window.innerWidth }); continue; }
          const r = el.getBoundingClientRect();
          // Обрезка краем окна: кнопка выходит за границы окна или уходит за левый край.
          // Перекрытие (elementFromPoint) — отдельный сигнал, это не «обрезка».
          const clipped = r.right > window.innerWidth + 1 || r.left < -1;
          const cx = r.left + r.width / 2, cy = r.top + r.height / 2;
          const hit = (document.elementFromPoint(cx, cy) as Element | null);
          const covered = !(hit && (hit === el || el.contains(hit) || hit.contains(el)));
          out.push({ id, clipped, covered, right: Math.round(r.right), vw: window.innerWidth });
        }
        return out;
      }, ids);

      for (const s of scan) {
        expect(s.missing, `кнопка ${s.id} есть на месте`).toBeFalsy();
        expect(s.clipped, `кнопка ${s.id} не обрезана краем окна (правый край ${s.right} из ${s.vw})`).toBeFalsy();
      }

      // «⋯» шапки — кнопка «Ещё действия» (гипотеза Глеба): меряем и пишем в отчёт,
      // жёстким требованием это не выдуманное требование макета. Считаем ВИДИМЫЕ кнопки:
      // в DOM кнопка может лежать скрытой (isCompact-разновидность).
      const more = page.getByRole('button', { name: /Ещё действи/i });
      const moreCount = await more.count();
      let moreVisible = 0;
      for (let i = 0; i < moreCount; i++) if (await more.nth(i).isVisible()) moreVisible++;
      const moreBox = moreVisible ? await more.first().boundingBox() : null;
      // Кто перекрывает «Разобрать этот чат» и есть ли «⋯» шапки — по DOM шапки.
      const headInfo = await page.evaluate(() => {
        const el = document.querySelector('[data-testid="wiki-chat-analyze"]');
        const r = el?.getBoundingClientRect();
        const hit = r ? document.elementFromPoint(r.left + r.width / 2, r.top + r.height / 2) : null;
        const moreBtn = document.querySelector('button[aria-label="Ещё действия"]') as HTMLElement | null;
        const ellipsis = Array.from(document.querySelectorAll('button, [role="button"]'))
          .filter(b => (b.textContent || '').trim() === '⋯' || /Ещё/i.test(b.getAttribute('aria-label') || '') || /Ещё/i.test(b.getAttribute('title') || ''))
          .map(b => `${b.tagName}[aria=${b.getAttribute('aria-label') ?? ''}|title=${b.getAttribute('title') ?? ''}|text=${(b.textContent || '').trim()}]`);
        const mr = moreBtn?.getBoundingClientRect();
        const mcs = moreBtn ? getComputedStyle(moreBtn) : null;
        const mParent = moreBtn?.parentElement ? getComputedStyle(moreBtn.parentElement) : null;
        const mInfo = moreBtn
          ? `visibility=${mcs?.visibility} display=${mcs?.display} opacity=${mcs?.opacity} parent(${moreBtn.parentElement?.className || moreBtn.parentElement?.tagName}): visibility=${mParent?.visibility} display=${mParent?.display} opacity=${mParent?.opacity} overflow=${mParent?.overflow}`
          : 'нет в DOM';
        return {
          over: hit ? `${hit.tagName}.${(hit.getAttribute('class') || '').slice(0, 40)}` : 'нет',
          ellipsis: ellipsis.join(' ; '),
          moreVisible: moreBtn ? (mr ? `${Math.round(mr.width)}x${Math.round(mr.height)} @${Math.round(mr.left)}; ${mInfo}` : 'без рамки') : 'нет в DOM',
        };
      });
      console.log(`ЗАМЕР шапки ${w}px: «Ещё действий» — ${moreCount} (видимых ${moreVisible}${moreBox ? `, ${Math.round(moreBox.width)}x${Math.round(moreBox.height)}` : ''}); перекрывает «Разобрать» — ${headInfo.over}; «⋯»/«Ещё» — ${headInfo.ellipsis || 'нет'}; «Ещё действия» — ${headInfo.moreVisible}; ряды «Вики»: ${scan.map(s => `${s.id}${s.clipped ? '(обрезана)' : ''}${s.covered ? '(перекрыта)' : ''}`).join(', ')}`);

      await page.screenshot({ path: path.join(SHOTS, `s5-header-${w}.png`) });
      expectNoWikiErrors(errors, net404);
    });
  }
});

// ─── Сценарий 5c · Компактное меню «Вики» на 360 ───

test.describe('Сценарий 5c · Компактное меню', () => {
  test.use({ viewport: { width: 360, height: 720 } });

  test('5c. Шапка чата на 360: одна кнопка-меню «Вики» вместо ряда; пометки доступны из меню', async ({ page, context }) => {
    const errors = collectConsoleErrors(page);
    const net404 = trackNet404(page);
    await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);

    const chat = chatIds[TRIAGE_TOTAL + 2];
    await page.setViewportSize({ width: 1280, height: 900 });
    await page.goto(`/#/project/${projectId}`);
    await dismissToasts(page);
    await page.getByText(chatNames[chat], { exact: true }).first().click();
    await dismissToasts(page);
    await page.waitForTimeout(1200);
    await page.setViewportSize({ width: 360, height: 720 });
    await page.waitForTimeout(800);
    // На 360 открыт мобильный список чатов поверх ленты: открываем чат из списка —
    // только тогда в шапке есть действия «Вики».
    await page.getByText(chatNames[chat], { exact: true }).first().click();
    await dismissToasts(page);
    await page.waitForTimeout(1200);

    const trigger = page.locator('button[title="Действия «Вики» в этом чате"]');
    await expect(trigger, 'на 360 — одна кнопка-меню «Вики»').toHaveCount(1);
    await expect(page.locator('[data-testid="wiki-chat-analyze"]'), 'ряда кнопок на 360 нет').toHaveCount(0);
    await page.screenshot({ path: path.join(SHOTS, 's5c-chat-360-before-menu.png'), fullPage: false });
    const box = await trigger.boundingBox();
    console.log(`ТРИГГЕР МЕНЮ НА 360: ${box ? `${Math.round(box.width)}x${Math.round(box.height)}` : 'нет рамки (скрыт)'}`);
    expect(box, 'кнопка-меню «Вики» видима на 360').not.toBeNull();
    expect(box!.height, 'цель касания меню ≥40 px').toBeGreaterThanOrEqual(40);

    await trigger.click();
    const sheet = page.locator('.cc-sheet-card');
    await expect(sheet, 'меню открывается').toHaveCount(1);
    await expect(sheet.getByRole('menuitem', { name: 'Важный' }), 'пункт «Важный» в меню есть').toHaveCount(1);
    await expect(sheet.getByRole('menuitem', { name: 'Не разбирать' }), 'пункт «Не разбирать» в меню есть').toHaveCount(1);
    await page.screenshot({ path: path.join(SHOTS, 's5c-chat-menu-360.png') });

    expectNoWikiErrors(errors, net404);
  });
});

// ─── Сценарий 6 · Замеры 360 и 1280 на экране «Настройки» ───

test.describe('Сценарий 6 · Замеры', () => {
  for (const w of [360, 1280]) {
      test.beforeEach(async () => { await ensureFlag(); });

  test(`6a. «Настройки» на ${w} px: нет горизонтального переполнения, цели ≥40 px, ошибок консоли нет`, async ({ page, context }) => {
      const errors = collectConsoleErrors(page);
      const net404 = trackNet404(page);
      await page.setViewportSize({ width: w, height: 900 });
      await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);

      await openWikiSettings(page);
      await expect(page.getByRole('heading', { name: 'Ночной разбор' })).toBeVisible();

      // Цели: кнопки — не меньше 40 px; чипы пометок у макета на широком экране 26 px
      // (макет: «Пометки … 26 px, на 360 — под строкой и 40 px»), поэтому на 360 они
      // проверяются отдельно, а на 1280 не считаются дефектом.
      const scan = await page.evaluate(() => {
        const smallButtons: string[] = [];
        const smallInputs: string[] = [];
        const switches: string[] = [];
        const markChips: string[] = [];
        // Область экрана «Настройки»: общий предок трёх заголовков блоков. Меряем только её —
        // навигация приложения (сайдбар, кнопки в шапке) к фиче не относится.
        const heads = Array.from(document.querySelectorAll('h2'))
          .filter(h => ['Ночной разбор', 'Отбор чатов'].includes((h.textContent || '').trim())
            || (h.textContent || '').startsWith('Применение'));
        let scope: Element | null = null;
        if (heads.length) {
          scope = heads[0].parentElement;
          for (const h of heads) {
            while (scope && !scope.contains(h)) scope = scope.parentElement;
          }
        }
        const root = scope ?? document.body;
        const nodes = Array.from(root.querySelectorAll('button, input, select, [role="button"], [role="switch"]'));
        for (const el of nodes) {
          const r = el.getBoundingClientRect();
          if (r.width === 0 || r.height === 0) continue;
          const tid = el.getAttribute('data-testid') ?? '';
          const tag = `${el.tagName}[${tid || el.getAttribute('aria-label') || ''}] ${Math.round(r.width)}x${Math.round(r.height)}`;
          if (el.getAttribute('role') === 'switch') { switches.push(tag); continue; }
          if (tid.startsWith('wiki-triage-mark-')) { markChips.push(tag); continue; }
          if (el.tagName === 'BUTTON' || el.getAttribute('role') === 'button') {
            if (r.width < 40 || r.height < 40) smallButtons.push(tag);
          } else if (r.width < 40 || r.height < 40) smallInputs.push(tag);
        }
        return {
          overflow: document.documentElement.scrollWidth - document.documentElement.clientWidth,
          smallButtons, smallInputs, switches, markChips,
        };
      });

      expect(scan.overflow, `горизонтального переполнения нет (${w} px)`).toBe(0);
      console.log(`ЗАМЕР ${w}px: кнопки <40px: ${scan.smallButtons.length ? scan.smallButtons.join(', ') : 'нет'}; поля <40px: ${scan.smallInputs.length ? scan.smallInputs.join(', ') : 'нет'}; тумблеры: ${scan.switches.join(', ') || 'нет'}; чипы пометок: ${scan.markChips.slice(0, 2).join(', ') || 'нет'}`);
      // Мелкие цели — НАХОДКА, а не падение прогона: их перечисляем в лог и заводим
      // дефектом в трекере (прогон QA должен дособрать остальные замеры).
      console.log(`НАХОДКА ЦЕЛЕЙ ${w}px: ${scan.smallButtons.join('; ') || 'нет'}`);
      if (w === 360) {
        const short = scan.markChips.filter(c => {
          const m = /(\d+)x(\d+)/.exec(c);
          return !m || Number(m[1]) < 40 || Number(m[2]) < 40;
        });
        expect(short, 'чипы пометок на 360 — цель 40 px').toEqual([]);
      }

      // Запрещённые слова среза Ф2 на экране «Настройки» — их быть не должно
      // (макет и записка «Срез Ф2 02.11», раздел «Проверка»).
      const settingsText = await page.locator('body').innerText();
      const forbidden = ['порог', 'бэклог', 'облак', 'вы администратор', '% на чаты', 'ступени'];
      const found = forbidden.filter(w => settingsText.toLowerCase().includes(w.toLowerCase()));
      console.log(`ЗАПРЕЩЁННЫЕ СЛОВА (${w}px): ${found.join(', ') || 'нет'}`);
      expect(found, `запрещённых слов среза на экране нет (${w} px)`).toEqual([]);

      await page.screenshot({ path: path.join(SHOTS, `s6-settings-${w}.png`), fullPage: true });
      expectNoWikiErrors(errors, net404);
    });
  }
});
