// QA-приёмка волны С3: «Начать вики заново» (ADR-039 решение 10) — предпросмотр по разделам,
// «Отмена» ничего не удаляет, подтверждение (число удалённых и путь архива), страница человека и
// приёмыши остаются, архив zip на диске содержит удалённое, повторная чистка при N=0 говорит
// «Удалять нечего».
//
// Чистка разрушающая: спек гоняется ПОСЛЕ остальных (данные стенда data-qa — свои).

import { test, expect, type APIRequestContext, type Page } from '@playwright/test';
import { existsSync, readdirSync } from 'node:fs';
import { api, login, type Api } from './helpers';

const BASE = process.env.PLAYWRIGHT_BASE_URL || 'http://127.0.0.1:5561';
const WIKI_ROOT = '/home/an/Sources/ClaudeCodeServer-wiki-judge/data-qa/wiki';
const SECTION = 'projects/0db0e722-667b-4125-bb96-20f04c6e63f1';
const HUMAN_PAGE = 'Проекты/0db0e722-667b-4125-bb96-20f04c6e63f1/Моя страница человека.md';

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

async function openSettings(page: Page): Promise<void> {
  const card = panelCard(page, 'Настройки');
  if (await card.count() > 0 && await card.first().isVisible()) return;
  await railButton(page, 'wikiSettings').click();
  await expect(card.first()).toBeVisible({ timeout: 20000 });
}

test.beforeAll(async ({ playwright, baseURL }) => {
  request = await playwright.request.newContext({ baseURL: baseURL || BASE });
  token = await login(request);
  client = api(request, token);
});

test.afterAll(async () => {
  await request?.dispose();
});

test('предпросмотр чистки показывает состав по разделам; «Отмена» ничего не удаляет', async ({ page, context }) => {
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
  await openApp(page);
  await openSettings(page);

  const before = await client.post<{ deleted: number; countsBySection: Record<string, Record<string, number>> }>(
    '/api/wiki/purge?dryRun=true');

  await page.getByTestId('wiki-purge-open').click();
  // У Modal нет role=dialog — ищем по заголовку окна.
  const modal = page.getByText('Начать вики заново?');
  await expect(modal, 'открылась модалка предпросмотра').toBeVisible({ timeout: 20000 });
  await expect(page.getByText(/Будет удалено \d+/), 'в предпросмотре есть число состава').toBeVisible();
  await expect(page.getByTestId('wiki-purge-section-projects/0db0e722-667b-4125-bb96-20f04c6e63f1').first(),
    'в предпросмотре есть раздел').toBeVisible();

  // «Отмена» — ничего не удаляет.
  await page.getByRole('button', { name: /отмена/i }).click();
  await expect(page.getByText('Начать вики заново?'), 'модалка закрылась').toHaveCount(0, { timeout: 10000 });

  const after = await client.post<{ deleted: number; countsBySection: Record<string, Record<string, number>> }>(
    '/api/wiki/purge?dryRun=true');
  expect(after.countsBySection, 'состав после отмены тот же').toEqual(before.countsBySection);
  expect(after.deleted, 'после отмены ничего не удалено').toBe(0);

  // Страница человека на месте.
  const human = await client.get<unknown>(`/api/wiki/page?section=${SECTION}&page=${encodeURIComponent(HUMAN_PAGE)}`);
  expect(human, 'страница человека жива после отмены').toBeTruthy();
});

test('подтверждение чистки: число удалённых, путь архива, страница человека остаётся, архив на диске', async ({ page, context }) => {
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
  await openApp(page);
  await openSettings(page);

  await page.getByTestId('wiki-purge-open').click();
  const modal = page.getByText('Начать вики заново?');
  await expect(modal).toBeVisible({ timeout: 20000 });
  const confirm = page.getByRole('button', { name: /^Удалить \d+/ });
  await expect(confirm, 'кнопка подтверждения активна').toBeEnabled();
  await confirm.click();

  await expect(page.getByText(/Удалено \d+/), 'показан итог — число удалённых').toBeVisible({ timeout: 30000 });
  const archiveLine = page.getByText(/Архив:/);
  await expect(archiveLine, 'показан путь архива').toBeVisible();
  const archivePath = (await archiveLine.textContent())!.replace('Архив:', '').trim();

  // Архив лежит на диске стенда.
  const owner = readdirSync(WIKI_ROOT)[0];
  const zip = `${WIKI_ROOT}/${owner}/${archivePath.replace(/^\/+/, '')}`;
  expect(existsSync(zip), `архив есть на диске (${zip})`).toBeTruthy();

  // Страница человека осталась, ИИ-страница ушла.
  const human = await client.get<unknown>(`/api/wiki/page?section=${SECTION}&page=${encodeURIComponent(HUMAN_PAGE)}`);
  expect(human, 'страница человека осталась после чистки').toBeTruthy();
  // ИИ-страница после чистки — 404: её больше нет (проверяем кодом ответа).
  const aiPage = await request.get(
    `/api/wiki/page?section=${SECTION}&page=${encodeURIComponent('Проекты/0db0e722-667b-4125-bb96-20f04c6e63f1/Состояние сада и инфраструктуры.md')}`,
    { headers: { Authorization: `Bearer ${token}` } });
  expect(aiPage.status(), 'ИИ-страница удалена (404)').toBe(404);
});

test('повторная чистка при пустом составе говорит «Удалять нечего» и не даёт подтвердить', async ({ page, context }) => {
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
  await openApp(page);
  await openSettings(page);

  await page.getByTestId('wiki-purge-open').click();
  await expect(page.getByText('Начать вики заново?')).toBeVisible({ timeout: 20000 });
  await expect(page.getByText('Удалять нечего.'), 'при N=0 сказано «Удалять нечего»').toBeVisible();
  const confirm = page.getByRole('button', { name: /^Удалить 0/ });
  await expect(confirm, 'подтверждение при N=0 выключено').toBeDisabled();
});
