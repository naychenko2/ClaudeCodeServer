import { test, expect, type Page } from '@playwright/test';
import { writeFileSync } from 'node:fs';
import { login } from './helpers';

// QA-приёмка фичи «Ферма помнит выбор окон графиков» (шаг farm-window-memory,
// ветка feat/local-model-window-memory, коммиты 5693f5de7 и 4e89e882b).
//
// Что проверяем (задача 318d4822):
//   1. Выбор окон «Нагрузка и кэш» (1 ч) и «Температуры» (5 мин) переживает переход
//      в другой проект и в персональный чат; оба окна независимы.
//   2. Перезагрузка страницы — выбор сохраняется.
//   3. Мусор в обоих ключах localStorage → дефолты 5m/15m, ошибок в консоли нет.
//   4. Мобильная ширина 360 — переключатели работают, ничего не обрезано.
//
// Ключи: cc_local_model_stats_window (дефолт 5m) и cc_local_model_hardware_window
// (дефолт 15m) — см. frontend/src/features/localModel/windowStorage.ts.
//
// Стенд: поднят из worktree ветки (юнит ccs-qa-farm-window), фронт раздаётся из
// frontend/dist этого worktree. Засева нет: нужны два проекта и личный чат — они
// заведены на стенде заранее (проекты «Ферма-QA-a»/«Ферма-QA-b», личный чат).
// Уборки нечего — засеянное не создаётся этим прогоном.

const PROJECT_A = '79f45721-0c36-4be3-adcc-b20df37bd5ba';
const PROJECT_B = 'cda41769-46f8-42f7-94ed-a25a4431cd81';
const PERSONAL_CHAT = '1e8d5ab8-03f9-4f8b-ba28-515c1f9b5a08';

const STATS_KEY = 'cc_local_model_stats_window';
const HW_KEY = 'cc_local_model_hardware_window';

// Скриншоты — в .cc-attachments проекта (путь абсолютный: прогон идёт из frontend/e2e/qa).
const SHOTS = '/home/an/Sources/ClaudeCodeServer-wt/farm-window-memory/.cc-attachments/qa-farm-window';

let token: string;

test.describe.configure({ mode: 'serial' });

test.beforeAll(async ({ playwright, baseURL }) => {
  const request = await playwright.request.newContext({ baseURL });
  token = await login(request);
  await request.dispose();
});

test.afterAll(() => {
  writeFileSync('/tmp/qa-farm-window-result.json', JSON.stringify({ ok: true }));
});

// Общий контекст: админ + панель «Ферма» в правой зоне проекта (cc_ws_zones),
// вкладка панели — LLM. Ключи окон НЕ засеваем: проверка читает их из хранилища.
async function openPanel(page: Page, opts: { zones?: boolean } = {}) {
  await page.addInitScript(({ tk, zones }: { tk: string; zones: boolean }) => {
    localStorage.setItem('cc_token', tk);
    localStorage.setItem('cc_role', 'admin');
    localStorage.setItem('cc_username', 'admin');
    localStorage.setItem('cc_local_model_tab', 'text');
    if (zones) {
      localStorage.setItem('cc_ws_zones', JSON.stringify({
        left: { layout: [['chats']], stash: [] },
        right: { layout: [['localModel']], stash: [] },
      }));
    }
  }, { tk: token, zones: opts.zones ?? true });
}

// Выбор окна в карточке: нажимаем кнопку с точной подписью внутри нужной карточки.
// Подпись «5 мин» — подстрока «15 мин», поэтому сравнение точное.
async function pickWindow(page: Page, card: 'Нагрузка и кэш' | 'Температуры', label: string) {
  const btn = page.locator('section', { has: page.getByRole('heading', { name: card, exact: true }) })
    .locator('button[aria-pressed]', { hasText: new RegExp(`^${label}$`) })
    .first();
  await btn.waitFor({ state: 'visible', timeout: 20000 });
  await btn.evaluate((el: HTMLElement) => el.click());
  await page.waitForTimeout(400);
}

// Нажатое окно в карточке (aria-pressed=true) — по подписи.
async function pressedWindow(page: Page, card: 'Нагрузка и кэш' | 'Температуры'): Promise<string | null> {
  return page.evaluate((cardName: string) => {
    const p = document.querySelector('[role="complementary"][aria-label="Ферма"]');
    if (!p) return null;
    for (const s of Array.from(p.querySelectorAll('section'))) {
      const h = s.querySelector('h3,h4');
      if ((h?.textContent ?? '').trim() !== cardName) continue;
      const on = Array.from(s.querySelectorAll('button[aria-pressed]'))
        .filter(b => b.getAttribute('aria-pressed') === 'true')
        .map(b => (b.textContent ?? '').trim());
      return on.find(t => /^\d+ (мин|ч)$/.test(t)) ?? null;
    }
    return null;
  }, card);
}

// Снимок карточки с переключателем окон (окно целиком видно — снимок элемента).
async function shotCard(page: Page, card: 'Нагрузка и кэш' | 'Температуры', file: string) {
  // Снимок блока карточки с переключателем: рамка берётся по заголовку карточки
  // (карточка — не всегда section: «Нагрузка и кэш» — Card, «Температуры» — section).
  const box = await page.evaluate((cardName: string) => {
    const p = document.querySelector('[role="complementary"][aria-label="Ферма"]');
    if (!p) return null;
    const head = [...p.querySelectorAll('h3,h4')].find(h => (h.textContent ?? '').trim() === cardName);
    if (!head) return null;
    // Блок карточки: section, а если его нет — родитель заголовка (у Card это div).
    const block = (head.closest('section') as HTMLElement | null) ?? (head.parentElement as HTMLElement);
    block.scrollIntoView({ block: 'center' });
    // Прокрутка применяется после кадра — рамку берём по нему, иначе clip мимо.
    return new Promise<{ x: number; y: number; width: number; height: number } | null>(resolve => {
      requestAnimationFrame(() => requestAnimationFrame(() => {
        const r = block.getBoundingClientRect();
        resolve({ x: Math.max(0, r.x), y: Math.max(0, r.y), width: r.width, height: r.height });
      }));
    });
  }, card);
  await page.waitForTimeout(600);
  if (!box || box.width < 10) { await page.screenshot({ path: `${SHOTS}/${file}` }); return; }
  await page.screenshot({ path: `${SHOTS}/${file}`, clip: box });
}

// Вкладка панели: «Стенды» в шапке панели (PanelHeaderSlot) — по aria-label.
async function openTab(page: Page, label: string) {
  const tab = page.locator(`button[aria-label="${label}"]`).first();
  await tab.waitFor({ state: 'visible', timeout: 20000 });
  await tab.evaluate((el: HTMLElement) => el.click());
  await page.waitForTimeout(2500);
}

// Переход по маршруту: с ?r= — иначе hash-SPA схлопывает адрес в «#/<раздел>»
// (известная грабля: без reload приложение не переключает маршрут).
async function go(page: Page, hash: string) {
  await page.goto(`/?r=${Date.now()}${hash}`);
}

async function stored(page: Page): Promise<{ stats: string | null; hw: string | null }> {
  return page.evaluate(({ sk, hk }: { sk: string; hk: string }) => ({
    stats: localStorage.getItem(sk),
    hw: localStorage.getItem(hk),
  }), { sk: STATS_KEY, hk: HW_KEY });
}

test('1: выбор окон живёт через другой проект и персональный чат; окна независимы', async ({ page }) => {
  const errors: string[] = [];
  page.on('console', m => { if (m.type() === 'error') errors.push(m.text()); });
  page.on('pageerror', e => errors.push(String(e)));

  await openPanel(page);
  await go(page, `/#/project/${PROJECT_A}`);
  await page.locator('[role="complementary"][aria-label="Ферма"]').waitFor({ timeout: 40000 });
  await page.waitForTimeout(3000);

  // Дефолты: LLM — 5 мин, Стенды — 15 мин.
  expect(await pressedWindow(page, 'Нагрузка и кэш'), 'дефолт «Нагрузка и кэш» — 5 мин').toBe('5 мин');
  await openTab(page, 'Стенды');
  expect(await pressedWindow(page, 'Температуры'), 'дефолт «Температуры» — 15 мин').toBe('15 мин');

  // Выбор: 1 ч у «Нагрузка и кэш», 5 мин у «Температуры».
  await pickWindow(page, 'Температуры', '5 мин');
  expect(await pressedWindow(page, 'Температуры')).toBe('5 мин');
  await openTab(page, 'LLM');
  await pickWindow(page, 'Нагрузка и кэш', '1 ч');
  expect(await pressedWindow(page, 'Нагрузка и кэш')).toBe('1 ч');

  const s1 = await stored(page);
  expect(s1.stats, 'окно «Нагрузка и кэш» записано в свой ключ').toBe('1h');
  expect(s1.hw, 'окно «Температуры» записано в свой ключ').toBe('5m');

  // Другой проект: панель перемонтируется — выбор должен восстановиться из хранилища.
  await go(page, `/#/project/${PROJECT_B}`);
  await page.locator('[role="complementary"][aria-label="Ферма"]').waitFor({ timeout: 40000 });
  await page.waitForTimeout(3000);
  expect(await pressedWindow(page, 'Нагрузка и кэш'), 'в другом проекте окно LLM на месте').toBe('1 ч');
  await openTab(page, 'Стенды');
  expect(await pressedWindow(page, 'Температуры'), 'в другом проекте окно «Температуры» на месте').toBe('5 мин');
  await shotCard(page, 'Температуры', '01-project-b-hw-window.png');

  // Персональный чат: панель живёт в правой зоне личного чата, открывается кнопкой рельса.
  await go(page, `/#/chats/${PERSONAL_CHAT}`);
  await page.waitForTimeout(5000);
  // Значок панели в личном чате появляется после лёгкого опроса configured — ждём его.
  // Подпись значка несёт состояние движка («Ферма: Модель недоступна») — матчим по префиксу.
  const farmBtn = page.locator('button[aria-label^="Ферма"], button[title^="Ферма"]').first();
  await farmBtn.waitFor({ state: 'visible', timeout: 60000 });
  await farmBtn.evaluate((el: HTMLElement) => el.click());
  await page.locator('[role="complementary"][aria-label="Ферма"]').waitFor({ timeout: 40000 });
  await page.waitForTimeout(3000);
  expect(await pressedWindow(page, 'Нагрузка и кэш'), 'в личном чате окно LLM на месте').toBe('1 ч');
  // Снимок — до переключения вкладки: карточка «Нагрузка и кэш» размонтируется на «Стендах».
  await shotCard(page, 'Нагрузка и кэш', '02-personal-chat-stats-window.png');
  await openTab(page, 'Стенды');
  expect(await pressedWindow(page, 'Температуры'), 'в личном чате окно «Температуры» на месте').toBe('5 мин');
  await shotCard(page, 'Температуры', '02b-personal-chat-hw-window.png');

  // Независимость: смена одного окна не трогает второе.
  await openTab(page, 'LLM');
  await pickWindow(page, 'Нагрузка и кэш', '15 мин');
  const s2 = await stored(page);
  expect(s2.stats, 'окно LLM сменилось само').toBe('15m');
  expect(s2.hw, 'окно «Температуры» не затронуто').toBe('5m');

  const real = errors.filter(e => !/ERR_NETWORK_CHANGED|Failed to load resource/.test(e));
  expect(real, 'в консоли нет ошибок').toEqual([]);
});

test('2: перезагрузка страницы — выбор сохранён', async ({ page }) => {
  await openPanel(page);
  await go(page, `/#/project/${PROJECT_A}`);
  await page.locator('[role="complementary"][aria-label="Ферма"]').waitFor({ timeout: 40000 });
  await page.waitForTimeout(3000);

  await openTab(page, 'Стенды');
  await pickWindow(page, 'Температуры', '1 ч');
  await openTab(page, 'LLM');
  await pickWindow(page, 'Нагрузка и кэш', '1 мин');
  const before = await stored(page);
  expect(before.stats).toBe('1m');
  expect(before.hw).toBe('1h');

  await page.reload();
  await page.locator('[role="complementary"][aria-label="Ферма"]').waitFor({ timeout: 40000 });
  await page.waitForTimeout(3000);
  expect(await pressedWindow(page, 'Нагрузка и кэш'), 'после перезагрузки окно LLM на месте').toBe('1 мин');
  await openTab(page, 'Стенды');
  expect(await pressedWindow(page, 'Температуры'), 'после перезагрузки окно «Температуры» на месте').toBe('1 ч');
  await shotCard(page, 'Температуры', '03-reload-hw-window.png');
});

test('3: мусор в ключах — дефолты, без ошибок в консоли', async ({ page }) => {
  const errors: string[] = [];
  page.on('console', m => { if (m.type() === 'error') errors.push(m.text()); });
  page.on('pageerror', e => errors.push(String(e)));

  await openPanel(page);
  // Мусор в оба ключа ДО загрузки страницы.
  await page.addInitScript(({ sk, hk }: { sk: string; hk: string }) => {
    localStorage.setItem(sk, 'abc');
    localStorage.setItem(hk, 'abc');
  }, { sk: STATS_KEY, hk: HW_KEY });
  await go(page, `/#/project/${PROJECT_A}`);
  await page.locator('[role="complementary"][aria-label="Ферма"]').waitFor({ timeout: 40000 });
  await page.waitForTimeout(3000);

  expect(await pressedWindow(page, 'Нагрузка и кэш'), 'мусор → дефолт LLM (5 мин)').toBe('5 мин');
  await openTab(page, 'Стенды');
  expect(await pressedWindow(page, 'Температуры'), 'мусор → дефолт «Температуры» (15 мин)').toBe('15 мин');
  await shotCard(page, 'Температуры', '04-garbage-hw-default.png');

  const real = errors.filter(e => !/ERR_NETWORK_CHANGED|Failed to load resource/.test(e));
  expect(real, 'в консоли нет ошибок').toEqual([]);
});

test('4: мобильная ширина 360 — переключатели работают, ничего не обрезано', async ({ page }) => {
  await page.setViewportSize({ width: 360, height: 780 });
  await openPanel(page, { zones: false });
  await go(page, `/#/project/${PROJECT_A}`);
  await page.waitForTimeout(5000);

  // На мобиле панель живёт в меню «Ещё» и открывается модалкой.
  const more = page.locator('button[aria-label^="Ещё"], button[title^="Ещё"]').first();
  await more.waitFor({ timeout: 30000 });
  await more.evaluate((el: HTMLElement) => el.click());
  const item = page.getByText('Ферма', { exact: true }).last();
  await item.waitFor({ timeout: 20000 });
  await item.evaluate((el: HTMLElement) => el.click());
  await page.locator('[role="complementary"][aria-label="Ферма"]').waitFor({ timeout: 30000 });
  await page.waitForTimeout(3000);

  expect(await pressedWindow(page, 'Нагрузка и кэш'), 'на 360 окно LLM показано').toBeTruthy();
  await pickWindow(page, 'Нагрузка и кэш', '1 ч');
  expect(await pressedWindow(page, 'Нагрузка и кэш'), 'на 360 переключатель работает').toBe('1 ч');
  const s = await stored(page);
  expect(s.stats, 'на 360 выбор записан в хранилище').toBe('1h');
  await shotCard(page, 'Нагрузка и кэш', '05-mobile-360-stats-window.png');

  // Обрезка: у переключателей окон и их карточек нет обрезанного текста.
  const clipped = await page.evaluate(() => {
    const p = document.querySelector('[role="complementary"][aria-label="Ферма"]');
    if (!p) return null;
    const bad: string[] = [];
    for (const el of Array.from(p.querySelectorAll('button[aria-pressed], h3, h4'))) {
      const e = el as HTMLElement;
      if (e.scrollWidth > e.clientWidth + 1 && getComputedStyle(e).textOverflow !== 'ellipsis') {
        bad.push((e.textContent ?? '').trim().slice(0, 40));
      }
    }
    return bad;
  });
  expect(clipped ?? [], 'на 360 ничего не обрезано без многоточия').toEqual([]);
});
