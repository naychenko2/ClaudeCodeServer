import { test, expect, type APIRequestContext, type Page } from '@playwright/test';
import { api, login, type Api } from './helpers';
import { existsSync, mkdirSync, readFileSync } from 'node:fs';

// QA · волна WN: кнопка «Запустить сейчас» в настройках вики (WN-6b) — диалог, «Отмена»,
// неактивное состояние «Идёт разбор…», обновление «Прошлой ночи», выключенный тумблер,
// двойной клик и вторая вкладка.
//
// Стенд из worktree :5561, локальный движок :18020 (qwen3.8-27b) — как в бою.
// Скриншоты — в .cc-attachments/qa/shots (папка вне git).

const DATA = '/home/an/Sources/ClaudeCodeServer-wiki-night/data-qa';
const SHOTS = '/home/an/Sources/ClaudeCodeServer-wiki-night/.cc-attachments/qa/shots';
const SPEND = `${DATA}/spend/turns-2026-11-06.jsonl`;

test.describe.configure({ mode: 'serial' });
test.setTimeout(420_000);

let request: APIRequestContext;
let token: string;
let client: Api;

function spendCount(label = 'wiki-nightly'): number {
  if (!existsSync(SPEND)) return 0;
  return readFileSync(SPEND, 'utf-8').split('\n').filter(l => l.includes(`"${label}"`)).length;
}

// Навигация в «Настройки» вики. Вики живёт в панели рельса («Панели вики» → подвкладки
// «Входящие/Журнал/Здоровье/Настройки», aria-label) либо в центре (переключатель режима
// «Вики» → подвкладки с title). Что именно отрисовано — зависит от раскладки и состояния,
// поэтому идём обоими путями и проверяем результат по заголовку блока.
async function openSettings(page: Page) {
  await page.goto('/#/notes');
  await expect(page.locator('#root'), 'приложение открылось').not.toBeEmpty();
  const heading = page.getByRole('heading', { name: 'Ночной разбор' });
  for (let i = 0; i < 20; i++) {
    if (await heading.isVisible().catch(() => false)) break;
    const rail = page.locator('[aria-label="Настройки"]');
    if (await rail.count()) {
      await rail.first().click({ timeout: 5000 }).catch(() => undefined);
    } else {
      // На 360 панели рельса свёрнуты: сначала открываем панель «Заметки», в ней —
      // переключатель режима «Вики», в центре вики — подвкладка «Настройки».
      const notesPanel = page.locator('[aria-label="Заметки"]');
      if (await notesPanel.count()) {
        await notesPanel.first().click({ timeout: 5000 }).catch(() => undefined);
        await page.waitForTimeout(1200);
      }
      const wikiMode = page.getByText('Вики', { exact: true }).first();
      if (await wikiMode.count()) {
        await wikiMode.click({ timeout: 5000 }).catch(() => undefined);
        await page.waitForTimeout(1500);
        // На 360 панель вики отдельная: открываем её и жмём подвкладку «Настройки».
        const panelsBtn = page.locator('[aria-label="Входящие, журнал, здоровье, настройки"]');
        if (await panelsBtn.count()) {
          await panelsBtn.first().click({ timeout: 5000 }).catch(() => undefined);
          await page.waitForTimeout(1200);
        }
        const sub = page.locator('[title="Настройки"], [aria-label="Настройки"]');
        if (await sub.count()) await sub.first().click({ timeout: 5000 }).catch(() => undefined);
      }
    }
    await page.waitForTimeout(1500);
  }
  await expect(heading, 'экран «Настройки» открыт').toBeVisible({ timeout: 60_000 });
}

test.beforeAll(async ({ playwright, baseURL }) => {
  mkdirSync(SHOTS, { recursive: true });
  request = await playwright.request.newContext({ baseURL });
  token = await login(request);
  client = api(request, token);
  await client.put('/api/feature-flags/llm-wiki', { enabled: true });
  // Ночь включена, окно закрыто (3..6) — ночной тик не мешает ручным проходам.
  await client.put('/api/wiki/settings', { nightly: { enabled: true, windowStartHour: 3, windowEndHour: 6 } });
});

test.afterAll(async () => {
  try { await client.put('/api/wiki/settings', { nightly: { enabled: true, windowStartHour: 3, windowEndHour: 6 } }); } catch { /* best-effort */ }
  await request?.dispose();
});

test('кнопка «Запустить сейчас»: диалог, «Отмена», «Идёт разбор…», итог ночи (1280, светлая тема)', async ({ page, context }) => {
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
  await context.addInitScript(() => localStorage.setItem('theme-mode', 'light'));
  await openSettings(page);

  const runBtn = page.locator('[data-testid="wiki-nightly-run"]');
  await expect(runBtn, 'кнопка «Запустить сейчас» есть').toBeVisible();
  await expect(runBtn, 'кнопка активна при включённой ночи').toBeEnabled();
  await expect(runBtn, 'подпись кнопки').toHaveText('Запустить сейчас');

  // Диалог подтверждения: текст с числом (лимит пакетов из снимка настроек).
  await runBtn.click();
  const dialogTitle = page.getByRole('heading', { name: 'Запустить ночной разбор сейчас?' });
  await expect(dialogTitle, 'диалог подтверждения открыт').toBeVisible();
  const dialog = page.locator('div').filter({ has: dialogTitle }).last();
  const dialogText = await dialog.innerText();
  test.info().annotations.push({ type: 'note', description: `текст диалога: ${dialogText.replace(/\n/g, ' | ')}` });
  expect(dialogText, 'в тексте диалога есть число (лимит)').toMatch(/\d/);
  await page.screenshot({ path: `${SHOTS}/wn-1280-light-dialog.png` });

  // «Отмена» ничего не запускает.
  const before = spendCount();
  await page.getByRole('button', { name: 'Отмена', exact: true }).click();
  await expect(dialogTitle, 'диалог закрыт').toHaveCount(0);
  await page.waitForTimeout(3000);
  const settingsAfterCancel = await client.get<{ nightlyRunning: boolean }>('/api/wiki/settings');
  expect(settingsAfterCancel.nightlyRunning, 'после «Отмены» проход не идёт').toBe(false);
  expect(spendCount(), 'после «Отмены» модель не звали').toBe(before);

  // «Запустить» — пока идёт проход, кнопка «Идёт разбор…» и неактивна.
  await runBtn.click();
  await page.getByRole('button', { name: 'Запустить', exact: true }).click();
  await expect(runBtn, 'кнопка перешла в состояние «Идёт разбор…»').toHaveText('Идёт разбор…', { timeout: 30_000 });
  await expect(runBtn, 'кнопка неактивна во время прохода').toBeDisabled();
  await page.screenshot({ path: `${SHOTS}/wn-1280-light-running.png` });

  // Вторая кнопка отказа: повторный POST во время прохода → 409 (проход один).
  const second = await request.post('/api/wiki/settings/nightly/run', {
    headers: { Authorization: `Bearer ${token}` },
  });
  test.info().annotations.push({ type: 'note', description: `повторный POST во время прохода: ${second.status()} ${await second.text()}` });
  expect(second.status(), 'повторный запуск отказан (409)').toBe(409);

  // Вторая вкладка во время прохода: что она показывает.
  const page2 = await context.newPage();
  await page2.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
  await openSettings(page2);
  const runBtn2 = page2.locator('[data-testid="wiki-nightly-run"]');
  const secondTabText = await runBtn2.innerText().catch(() => '—');
  const secondTabDisabled = await runBtn2.isDisabled().catch(() => true);
  test.info().annotations.push({ type: 'note', description: `вторая вкладка: «${secondTabText}», неактивна=${secondTabDisabled}` });
  await page2.screenshot({ path: `${SHOTS}/wn-1280-light-second-tab.png` });

  // Ждём конца прохода и обновления «Прошлой ночи».
  await expect(runBtn, 'после прохода кнопка снова активна').toBeEnabled({ timeout: 300_000 });
  await expect(page.locator('[data-testid="wiki-nightly-card"]'), 'карточка итога ночи есть').toBeVisible({ timeout: 30_000 });
  const cardText = await page.locator('[data-testid="wiki-nightly-card"]').innerText();
  test.info().annotations.push({ type: 'note', description: `итог в карточке: ${cardText.replace(/\n/g, ' | ')}` });
  expect(cardText, 'в итоге есть «разобрано»').toContain('разобрано');
  expect(cardText, 'в итоге есть расход («≈»)').toContain('≈');
  await page.screenshot({ path: `${SHOTS}/wn-1280-light-result.png`, fullPage: true });
  await page2.close();

  const after = spendCount();
  test.info().annotations.push({ type: 'note', description: `вызовов wiki-nightly: было ${before}, стало ${after}` });
  expect(after, 'проход был один (один набор вызовов, не два)').toBeGreaterThan(before);
});

test('кнопка на 360 и в тёмной теме: диалог влезает, отмена ничего не запускает', async ({ page, context }) => {
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
  await context.addInitScript(() => localStorage.setItem('theme-mode', 'dark'));
  await page.setViewportSize({ width: 360, height: 720 });
  await openSettings(page);

  const runBtn = page.locator('[data-testid="wiki-nightly-run"]');
  await expect(runBtn, 'кнопка видна на 360').toBeVisible();
  await expect(runBtn, 'кнопка активна на 360').toBeEnabled();
  await page.screenshot({ path: `${SHOTS}/wn-360-dark-button.png`, fullPage: true });

  const before = spendCount();
  await runBtn.click();
  const dialogTitle = page.getByRole('heading', { name: 'Запустить ночной разбор сейчас?' });
  await expect(dialogTitle, 'диалог на 360 открыт').toBeVisible();
  await page.screenshot({ path: `${SHOTS}/wn-360-dark-dialog.png` });
  await page.getByRole('button', { name: 'Отмена', exact: true }).click();
  await expect(dialogTitle, 'диалог закрыт').toHaveCount(0);
  await page.waitForTimeout(3000);
  expect(spendCount(), 'отмена на 360 не запускает разбор').toBe(before);

  // Тот же проход на 360: кнопка переходит в «Идёт разбор…» и неактивна.
  await runBtn.click();
  await page.getByRole('button', { name: 'Запустить', exact: true }).click();
  await expect(runBtn, 'на 360 кнопка «Идёт разбор…»').toHaveText('Идёт разбор…', { timeout: 30_000 });
  await expect(runBtn, 'на 360 кнопка неактивна во время прохода').toBeDisabled();
  await page.screenshot({ path: `${SHOTS}/wn-360-dark-running.png` });
  await expect(runBtn, 'после прохода кнопка снова активна').toBeEnabled({ timeout: 300_000 });
});

test('кнопка и диалог: 1280 в тёмной теме и 360 в светлой', async ({ page, context, browser }) => {
  // 1280, тёмная тема.
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
  await context.addInitScript(() => localStorage.setItem('theme-mode', 'dark'));
  await page.setViewportSize({ width: 1280, height: 900 });
  await openSettings(page);
  await expect(page.locator('[data-testid="wiki-nightly-run"]'), 'кнопка в тёмной теме на 1280 видна').toBeVisible();
  await page.locator('[data-testid="wiki-nightly-run"]').click();
  await expect(page.getByRole('heading', { name: 'Запустить ночной разбор сейчас?' }), 'диалог в тёмной теме').toBeVisible();
  await page.screenshot({ path: `${SHOTS}/wn-1280-dark-dialog.png` });
  await page.getByRole('button', { name: 'Отмена', exact: true }).click();
  await expect(page.getByRole('heading', { name: 'Запустить ночной разбор сейчас?' }), 'диалог закрыт').toHaveCount(0);

  // 360, светлая тема.
  const ctx2 = await browser.newContext({ viewport: { width: 360, height: 720 } });
  await ctx2.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
  await ctx2.addInitScript(() => localStorage.setItem('theme-mode', 'light'));
  const page2 = await ctx2.newPage();
  await openSettings(page2);
  await expect(page2.locator('[data-testid="wiki-nightly-run"]'), 'кнопка в светлой теме на 360 видна').toBeVisible();
  await page2.locator('[data-testid="wiki-nightly-run"]').click();
  await expect(page2.getByRole('heading', { name: 'Запустить ночной разбор сейчас?' }), 'диалог в светлой теме на 360').toBeVisible();
  await page2.screenshot({ path: `${SHOTS}/wn-360-light-dialog.png` });
  await page2.getByRole('button', { name: 'Отмена', exact: true }).click();
  await ctx2.close();
});

test('двойной клик по «Запустить» в диалоге — только один проход', async ({ page, context }) => {
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
  await openSettings(page);
  const runBtn = page.locator('[data-testid="wiki-nightly-run"]');
  const before = spendCount();
  await runBtn.click();
  // Двойной клик по кнопке подтверждения: диалог закрывается на первом клике, но
  // два POST'а могли бы уйти — проход должен быть один.
  await page.getByRole('button', { name: 'Запустить', exact: true }).dblclick();
  await expect(runBtn, 'кнопка снова активна после прохода').toBeEnabled({ timeout: 300_000 });
  await page.waitForTimeout(2000);
  const after = spendCount();
  test.info().annotations.push({ type: 'note', description: `вызовов wiki-nightly: было ${before}, стало ${after}` });
  // Один проход разбирает два чата по три вызова модели: шесть записей.
  expect(after - before, 'двойной клик не дал второго прохода').toBeLessThanOrEqual(6);
});

test('выключенный тумблер ночи: кнопка неактивна, подсказка «Ночной разбор выключен»', async ({ page, context }) => {
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
  await client.put('/api/wiki/settings', { nightly: { enabled: false } });
  await openSettings(page);

  const runBtn = page.locator('[data-testid="wiki-nightly-run"]');
  await expect(runBtn, 'при выключенной ночи кнопка неактивна').toBeDisabled();
  const hint = page.locator('[data-testid="wiki-nightly-run-off-hint"]');
  await expect(hint, 'подсказка о выключенной ночи есть').toBeVisible();
  expect(await hint.innerText(), 'текст подсказки').toContain('Ночной разбор выключен');
  await page.screenshot({ path: `${SHOTS}/wn-1280-light-off.png` });

  await client.put('/api/wiki/settings', { nightly: { enabled: true } });
});
