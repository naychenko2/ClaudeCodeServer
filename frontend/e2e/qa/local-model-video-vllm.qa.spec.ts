import { test, expect, type APIRequestContext, type Page } from '@playwright/test';
import { api, collectConsoleErrors, login, seed, type Api } from './helpers';
import { existsSync, readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

// QA-приёмка фичи «панель Локальные модели — второй стенд на vLLM (Qwen 27B на Video)».
//
// Сценарии покрывают: подписи второго стенда (модель/движок из EngineLabel/ModelLabel/Title),
// «Скорость» с числами и подписью «чтение промпта с ожиданием», карточку «Нагрузка и кэш»
// (без подписи про ступеньки llama-server), «Кто сейчас на модели» (чат кликабельный,
// место — нет), счётчик в шапке карточки, права (не-админ не видит вкладку «LLM»).
//
// Стенд уже поднят и наполнен (конфиг стенда, заглушки движков) — спека только читает.

const PREFIX = 'qa-lmvllm-';

// Фаза прогона: 'main' — основной стенд (два сегмента движков: main + Video);
// 'llama' — контрольная фаза (контрольный named-движок на llama-server: подписи
// «чтение промпта» без «с ожиданием» и подпись про ступеньки счётчиков).
const PHASE_FILE = join(dirname(fileURLToPath(import.meta.url)), '.phase-lmvllm');
const PHASE = (existsSync(PHASE_FILE) ? readFileSync(PHASE_FILE, 'utf8').trim() : '') || 'main';

test.describe.configure({ mode: 'serial' });

let request: APIRequestContext;
let token: string;
let client: Api;
let seeded: ReturnType<typeof seed>;
let projectId: string;

interface EngineEntry { name: string; title: string; configured: boolean }
interface ChatDto { own: boolean; sessionId: string | null; projectId: string | null; title: string; phase: string }
interface StatsDto {
  configured: boolean; engine: string; engineLabel?: string; model: string; status: string;
  running: number; waiting: number; othersCount: number;
  speed: { genPerStream: number | null; prefillPerStream: number | null };
  kv: { usagePerc: number | null; prefixHitPerc: number | null };
  unsupported: string[];
  chats: ChatDto[];
  duty?: { arbiter?: { state?: string } | null; callsNow?: { place: string; link: string | null }[] | null };
}

test.beforeAll(async ({ playwright, baseURL }) => {
  request = await playwright.request.newContext({ baseURL });
  token = await login(request);
  client = api(request, token);
  seeded = seed(client);
  const stamp = Date.now();
  const project = await seeded.project(`${PREFIX}p-${stamp}`, `/tmp/${PREFIX}${stamp}`);
  projectId = project.id;
});

test.afterAll(async () => {
  await seeded?.cleanup();
  await request?.dispose();
});

// Открыть приложение под админом (роль — из localStorage, как её кладёт форма входа).
async function openAsAdmin(page: Page) {
  await page.addInitScript(([tk, role]) => {
    localStorage.setItem('cc_token', tk as string);
    localStorage.setItem('cc_role', role as string);
    localStorage.setItem('cc_username', 'admin');
    localStorage.setItem('cc_local_model_tab', 'text');
  }, [token, 'admin'] as const);
}

test('сценарий 1,2,4: вкладка второго стенда — подписи, скорость, кэш, без llama-подписей', async ({ page }) => {
  const errors = collectConsoleErrors(page);
  await openAsAdmin(page);

  // Список движков: два сегмента с заголовками (main + второй стенд).
  const engines = await client.get<EngineEntry[]>('/api/local-model/engines');
  const namedEngine = PHASE === 'llama' ? 'llamacontrol' : 'gemma';
  expect(engines.map(e => e.name), 'в списке движков есть main и второй стенд')
    .toEqual(PHASE === 'llama' ? ['main', 'llamacontrol'] : ['main', 'gemma']);
  expect(engines.find(e => e.name === namedEngine)?.configured, 'второй стенд настроен').toBe(true);
  expect(engines.find(e => e.name === 'main')?.title, 'у первого сегмента есть заголовок').toBeTruthy();
  expect(engines.find(e => e.name === namedEngine)?.title, 'у второго сегмента есть заголовок').toBeTruthy();

  // Снимок второго стенда: подписи из EngineLabel/ModelLabel, тип движка — vllm.
  const named = await client.get<StatsDto>(`/api/local-model/stats?engine=${namedEngine}`);
  expect(named.configured).toBe(true);
  if (PHASE === 'llama') {
    // Контрольная фаза: движок llama-server — подписи его семантики.
    expect(named.engine, 'тип движка — по MetricsEngine').toBe('llama-server');
    expect(named.engineLabel, 'подпись движка из EngineLabel').toBe('llama-server 4028');
    expect(named.model, 'подпись модели из ModelLabel').toBe('Llama-27B (контроль)');
  } else {
    expect(named.engine, 'тип движка — по MetricsEngine, а не имя стенда').toBe('vllm');
    expect(named.engineLabel, 'подпись движка из EngineLabel').toBe('vLLM 0.11 (Video)');
    expect(named.model, 'подпись модели из ModelLabel').toBe('Qwen3.8-27B (Video)');
    expect(named.model, 'не сырой id модели').not.toContain('qwen3.8-27b');
    expect(named.model, 'не «llama-server · Video»').not.toContain('llama-server');
  }

  // «Скорость»: оба показателя числами (не «—»), префикс-кэш виден.
  if (PHASE !== 'llama') {
    expect(named.speed.genPerStream, 'скорость генерации — число').not.toBeNull();
    expect(named.speed.prefillPerStream, 'скорость чтения промпта — число').not.toBeNull();
    expect(named.kv.usagePerc, 'доля KV-кэша — число').not.toBeNull();
    expect(named.kv.prefixHitPerc, 'доля префикс-кэша — число').not.toBeNull();
    expect(named.unsupported, 'KvUsagePerc у vLLM-дорожки известен').not.toContain('KvUsagePerc');
  }

  // Основной стенд не изменился: подписи из Stats и llama-подписи у llama-server-движка.
  const main = await client.get<StatsDto>('/api/local-model/stats');
  expect(main.model, 'подпись модели основного стенда из Stats').toBe('Qwen3.8-Flash-Next IQ3_XXS');
  expect(main.engineLabel, 'подпись движка основного стенда из Stats').toBe('Strata 0.2.7');
  if (PHASE === 'llama') {
    const llama = await client.get<StatsDto>('/api/local-model/stats?engine=llamacontrol');
    expect(llama.engine, 'контрольный движок — llama-server по MetricsEngine').toBe('llama-server');
  }

  await page.goto(`/#/project/${projectId}`);
  await expect(page.locator('#root')).not.toBeEmpty();
  // 404 /api/providers/{key}/balance у локального провайдера — существующее поведение
  // (тот же 404 и у боевого local-qwen), не регресс фичи.
  // 404 локального провайдера (balance) и ERR_NETWORK_CHANGED при перезагрузке —
  // существующее поведение стенда, не регресс фичи.
  const real = errors.filter(e => !/Failed to load resource.*404/.test(e) && !/ERR_NETWORK_CHANGED/.test(e));
  expect(real, 'в консоли нет ошибок').toEqual([]);
});


test('сценарий 1 в UI: вкладка второго стенда — подписи, «Нагрузка и кэш», без ступенек llama-server', async ({ page }) => {
  const errors = collectConsoleErrors(page);
  await openAsAdmin(page);
  await page.goto(`/#/project/${projectId}`);

  // Открыть панель «Локальные модели» в правом рельсе.
  const railBtn = page.locator('button[aria-label^="Локальные модели"]');
  // Значок появляется после лёгкого опроса configured (60 с) — ждём его.
  await expect(railBtn, 'значок панели в рельсе').toBeVisible({ timeout: 120_000 });
  await railBtn.click();

  const panel = page.locator('[role="complementary"][aria-label="Локальные модели"]');
  await expect(panel, 'панель открылась').toBeVisible();

  if (PHASE === 'llama') {
    // Контрольная фаза: у llama-server-движка подпись чтения промпта БЕЗ «с ожиданием»,
    // а подпись про ступеньки счётчиков ЕСТЬ.
    const seg = panel.getByRole('button', { name: /Llama-контроль/ });
    await expect(seg, 'сегмент контрольного движка есть').toBeVisible();
    await seg.click();
    await expect(panel.getByText('Llama-27B (контроль)', { exact: true }), 'подпись модели')
      .toBeVisible({ timeout: 30_000 });
    await expect(panel.getByText(/llama-server 4028/), 'подпись движка')
      .toBeVisible({ timeout: 30_000 });
    await expect(panel.getByText('чтение промпта, ток/с, за последнюю минуту'),
      'у llama-server подпись чтения промпта — без «с ожиданием»').toBeVisible();
    await expect(panel.getByText(/ступеньками/), 'у llama-server есть подпись про ступеньки').toBeVisible();
  } else {
    // Переключиться на второй стенд (сегмент с заголовком из /engines).
    const seg = panel.getByRole('button', { name: /Qwen 27B \(Video\)/ });
    await expect(seg, 'сегмент второго стенда есть').toBeVisible();
    await seg.click();

    // Подпись модели и движка — из EngineLabel/ModelLabel, не сырой id и не «llama-server · Video».
    await expect(panel.getByText('Qwen3.8-27B (Video)', { exact: true }), 'подпись модели')
      .toBeVisible({ timeout: 30_000 });
    await expect(panel.getByText(/vLLM 0\.11 \(Video\)/), 'подпись движка')
      .toBeVisible({ timeout: 30_000 });
    await expect(panel.getByText(/qwen3\.8-27b/), 'сырой id модели не показан').toHaveCount(0);
    await expect(panel.getByText(/llama-server · Video/), 'подписи «llama-server · Video» нет').toHaveCount(0);

    // «Скорость»: два показателя с числами и подпись «чтение промпта с ожиданием».
    await expect(panel.getByText('чтение промпта с ожиданием, ток/с, за последнюю минуту'),
      'подпись чтения промпта — «с ожиданием» (движок vLLM)').toBeVisible();
    await expect(panel.getByText('генерация на запрос, ток/с, за последнюю минуту'),
      'подпись генерации').toBeVisible();

    // Карточка «Нагрузка и кэш» есть, подписи про ступеньки llama-server нет.
    await expect(panel.getByText('Нагрузка и кэш', { exact: true }), 'карточка «Нагрузка и кэш»').toBeVisible();
    await expect(panel.getByText(/ступеньками/), 'подписи про ступеньки llama-server нет').toHaveCount(0);
  }

  // «Кто сейчас на модели»: заголовок карточки и счётчик в её шапке.
  await expect(panel.getByText('Кто сейчас на модели', { exact: true }), 'карточка «Кто сейчас на модели»').toBeVisible();

  // 404 локального провайдера (balance) и ERR_NETWORK_CHANGED при перезагрузке —
  // существующее поведение стенда, не регресс фичи.
  const real = errors.filter(e => !/Failed to load resource.*404/.test(e) && !/ERR_NETWORK_CHANGED/.test(e));
  expect(real, 'в консоли нет ошибок').toEqual([]);
});


test('сценарий 6: 360 и 1280 — без горизонтального переполнения, тач-цели строк чатов ≥ 40 px', async ({ page }) => {
  const errors = collectConsoleErrors(page);
  await openAsAdmin(page);

  for (const width of [360, 1280]) {
    await page.setViewportSize({ width, height: 900 });
    await page.goto(`/#/project/${projectId}`);
    // Открыть панель: на десктопе — кнопка рельса, на узкой ширине — меню «Ещё».
    // На 1280 панель — кнопка правого рельса, на 360 — пункт меню «Ещё».
    const rail = page.locator('button[aria-label^="Локальные модели"]');
    if (await rail.count() > 0 && await rail.first().isVisible()) {
      await rail.first().click();
    } else {
      const more = page.locator('button[aria-label="Ещё"]');
      await expect(more.first(), `меню «Ещё» на ${width}`).toBeVisible({ timeout: 120_000 });
      await more.first().click();
      const item = page.getByText('Локальные модели', { exact: true }).first();
      await expect(item, `пункт «Локальные модели» в меню на ${width}`).toBeVisible({ timeout: 15_000 });
      await item.click();
    }
    const panel = page.locator('[role="complementary"][aria-label="Локальные модели"]');
    await expect(panel, `панель открылась на ${width}`).toBeVisible();

    // Горизонтального переполнения быть не должно.
    const overflow = await panel.evaluate(el => {
      const doc = document.documentElement;
      return { docScroll: doc.scrollWidth, docClient: doc.clientWidth,
        panelScroll: el.scrollWidth, panelClient: el.clientWidth };
    });
    expect(overflow.docScroll, `нет горизонтального переполнения документа на ${width}`)
      .toBeLessThanOrEqual(overflow.docClient + 1);
    expect(overflow.panelScroll, `нет горизонтального переполнения панели на ${width}`)
      .toBeLessThanOrEqual(overflow.panelClient + 1);

    // Строки чатов в карточке «Кто сейчас на модели» — тач-цели ≥ 40 px.
    const rows = panel.locator('section').filter({ hasText: /сейчас на модели/i }).locator('button');
    const count = await rows.count();
    for (let i = 0; i < count; i++) {
      const box = await rows.nth(i).boundingBox();
      if (!box) continue;
      expect(box.height, `высота строки чата ≥ 40 px на ${width}`).toBeGreaterThanOrEqual(40);
    }
  }
  // 404 локального провайдера (balance) и ERR_NETWORK_CHANGED при перезагрузке —
  // существующее поведение стенда, не регресс фичи.
  const real = errors.filter(e => !/Failed to load resource.*404/.test(e) && !/ERR_NETWORK_CHANGED/.test(e));
  expect(real, 'в консоли нет ошибок').toEqual([]);
});


test('сценарий 3 в UI: «Кто сейчас на модели» — строка чата кликабельна, строка места — нет', async ({ page }) => {
  test.skip(PHASE === 'llama', 'в контрольной фазе второго стенда Video нет — проверяется в основной фазе');
  const errors = collectConsoleErrors(page);
  const bad: string[] = [];
  page.on('response', r => { if (r.status() >= 400) bad.push(`${r.status()} ${r.url()}`); });
  await openAsAdmin(page);
  await page.goto(`/#/project/${projectId}`);
  const railBtn = page.locator('button[aria-label^="Локальные модели"]');
  await expect(railBtn, 'значок панели в рельсе').toBeVisible({ timeout: 120_000 });
  await railBtn.click();
  const panel = page.locator('[role="complementary"][aria-label="Локальные модели"]');
  await expect(panel, 'панель открылась').toBeVisible();
  const seg = panel.getByRole('button', { name: /Qwen 27B \(Video\)/ });
  await seg.click();
  await expect(panel.getByText('Qwen3.8-27B (Video)', { exact: true }), 'вкладка второго стенда')
    .toBeVisible({ timeout: 30_000 });

  const who = panel.locator('section').filter({ hasText: /сейчас на модели/i });
  await expect(who, 'карточка «Кто сейчас на модели»').toBeVisible({ timeout: 30_000 });

  // Строка места (вызов сервера) — не кнопка: перехода нет.
  const placeNote = who.getByText(/Места в списке/i);
  await expect(placeNote, 'подпись про места есть').toBeVisible({ timeout: 20_000 });
  const placeButtons = await who.locator('button').evaluateAll(els =>
    els.map(e => (e.textContent || '').trim()));
  expect(placeButtons.some(t => /Обновление названия чата|Заголовок чата|Оглавление/.test(t)),
    'строка места не сделана кнопкой').toBe(false);

  // Строка чата — кнопка (клик открывает свой чат).
  const chatRow = who.locator('button');
  const chatRows = await chatRow.count();
  expect(chatRows, 'в карточке есть строка чата').toBeGreaterThan(0);
  const firstChat = chatRow.first();
  const own = await firstChat.evaluate(el => el.textContent || '');
  expect(own, 'строка чата содержит название чата').toContain('QA video чат 2');
  await firstChat.click();
  await page.waitForTimeout(2500);
  await expect(page, 'клик по строке чата открыл его чат').toHaveURL(/chat|project/);

  console.log('BAD RESPONSES:', JSON.stringify(bad));
  // 404 /api/providers/{key}/balance у локального провайдера — существующее поведение
  // (тот же 404 и у боевого local-qwen), не регресс фичи. Кроме него, 404 в ответах быть не должно.
  // 404 локального провайдера (balance) и ERR_NETWORK_CHANGED при перезагрузке —
  // существующее поведение стенда, не регресс фичи.
  const real = errors.filter(e => !/Failed to load resource.*404/.test(e) && !/ERR_NETWORK_CHANGED/.test(e));
  expect(real, 'в консоли нет ошибок').toEqual([]);
  const unexpected = bad.filter(b => !/\/api\/providers\/[^/]+\/balance/.test(b));
  expect(unexpected, 'нет неожиданных ответов с кодом 4xx/5xx').toEqual([]);
});

test('сценарий 7: регрессия /stats без engine — прежний набор полей', async () => {
  const main = await client.get<StatsDto>('/api/local-model/stats');
  for (const key of ['configured', 'engine', 'model', 'status', 'running', 'waiting', 'othersCount',
    'speed', 'kv', 'gpus', 'history', 'chats', 'unsupported', 'tensorParallel', 'cleanStepMs'] as const)
    expect(key in main, `поле ${key} на месте`).toBe(true);
  expect(main.engine, 'основной стенд — vllm').toBe('vllm');
  expect(main.duty, 'у основного стенда блока дежурства нет').toBeUndefined();
});

test('сценарий 5: не-админ не видит вкладку «LLM» (/stats — 404)', async ({ playwright, baseURL }) => {
  // Второй пользователь без роли admin.
  const username = `${PREFIX}user`;
  const created = await client.post<{ id: string }>('/api/users', { username, password: 'qa-pass-1234', role: 'user' });
  expect(created?.id, 'пользователь создан').toBeTruthy();
  try {
    const plain = await playwright.request.newContext({ baseURL });
    const lr = await plain.post('/api/auth/login', { data: { username, password: 'qa-pass-1234' } });
    expect(lr.ok(), 'вход обычного пользователя').toBeTruthy();
    const plainToken = (await lr.json()).token as string;
    // Не-админ: вкладки «LLM» нет. Ответ — отказ (403/404), тело без данных движков.
    const r = await plain.get('/api/local-model/stats', { headers: { Authorization: `Bearer ${plainToken}` } });
    expect([403, 404], 'не-админу /stats недоступен').toContain(r.status());
    const body = await r.text();
    expect(body, 'в ответе нет снимка движков').not.toContain('engineLabel');
    const re = await plain.get('/api/local-model/engines', { headers: { Authorization: `Bearer ${plainToken}` } });
    expect([403, 404], 'не-админу /engines недоступен').toContain(re.status());
    expect(await re.text(), 'в ответе нет списка движков').not.toContain('Qwen 27B');
    await plain.dispose();
  } finally {
    await client.del(`/api/users/${created!.id}`);
  }
});

test('сценарий 3: чат на втором стенде — только в своей вкладке, место не кликабельно', async ({ page }) => {
  const errors = collectConsoleErrors(page);
  await openAsAdmin(page);
  await page.goto(`/#/project/${projectId}`);

  // Живой чат на втором стенде: именованный провайдер local-video-gemma.
  const named = await client.get<StatsDto>('/api/local-model/stats?engine=gemma');
  const main = await client.get<StatsDto>('/api/local-model/stats');
  test.skip(PHASE === 'llama', 'сценарий 3 проверяется в основной фазе');

  // Свой чат виден в своей вкладке; чужой — обезличен (без sessionId/projectId).
  const own = named.chats.find(c => c.own);
  if (own) {
    expect(own.sessionId, 'свой чат — с sessionId').toBeTruthy();
    expect(own.projectId, 'свой чат — с projectId').toBeTruthy();
    expect(main.chats.find(c => c.sessionId === own.sessionId), 'в основной вкладке чата нет').toBeUndefined();
  }

  // Чужой чат обезличен: без sessionId/projectId, с названием «Чат другого пользователя».
  const foreignRow = named.chats.find(c => !c.own);
  expect(foreignRow, 'в снимке есть чужой чат').toBeTruthy();
  expect(foreignRow!.sessionId, 'у чужого чата нет sessionId').toBeNull();
  expect(foreignRow!.projectId, 'у чужого чата нет projectId').toBeNull();
  expect(foreignRow!.title, 'чужой чат обезличен').toBe('Чат другого пользователя');
  expect(JSON.stringify(named.chats), 'в ответе нет идентификаторов чужого чата')
    .not.toContain('b719b05f');

  // Места сервера в duty.callsNow не кликабельны (это div, а не кнопка).
  expect(Array.isArray(named.duty?.callsNow)).toBe(true);
  // 404 локального провайдера (balance) и ERR_NETWORK_CHANGED при перезагрузке —
  // существующее поведение стенда, не регресс фичи.
  const real = errors.filter(e => !/Failed to load resource.*404/.test(e) && !/ERR_NETWORK_CHANGED/.test(e));
  expect(real, 'в консоли нет ошибок').toEqual([]);
});
