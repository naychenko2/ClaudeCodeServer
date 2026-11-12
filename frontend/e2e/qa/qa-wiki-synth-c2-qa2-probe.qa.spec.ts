// QA-2 · догляд: бейдж rejected_skipped в списке разбора (с1 после 3 отказов).

import { test, expect, type Page } from '@playwright/test';
import { api, login, type Api } from './helpers';
import fs from 'node:fs';
import path from 'node:path';

const ROOT = path.resolve(new URL('../../..', import.meta.url).pathname);
type Seed = { chats: { key: string; sessionId: string }[] };
const seed = JSON.parse(fs.readFileSync(path.join(ROOT, '.cc-attachments', 'qa-wiki-synth-c2', 'seed.json'), 'utf8')) as Seed;

let client: Api;
let token = '';

test.setTimeout(120_000);

const railButton = (page: Page, key: string) => page.locator(`[data-rail-item="${key}"] button`);
const panelCard = (page: Page, title: string) => page.locator(
  `xpath=//span[contains(@style,"font-weight: 600") and normalize-space()="${title}"]` +
  `/ancestor::div[contains(@style,"flex-direction: column")][1]`);

test.beforeAll(async ({ playwright, baseURL }) => {
  const request = await playwright.request.newContext({ baseURL });
  token = await login(request);
  client = api(request, token);
});

test('rejected_skipped: бейдж и позиция', async ({ page }) => {
  const s1 = seed.chats.find(c => c.key === 's1')!;
  await client.put(`/api/wiki/chats/${s1.sessionId}/mark`, { mark: null });
  await page.context().addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
  await page.goto('/#/notes');
  await page.waitForTimeout(2500);
  await railButton(page, 'wikiSettings').click();
  await expect(panelCard(page, 'Настройки')).toBeVisible({ timeout: 20000 });
  const rows = page.getByTestId('wiki-triage-row');
  const n = await rows.count();
  const seen: string[] = [];
  for (let i = 0; i < n; i++) {
    const t = (await rows.nth(i).innerText()).replace(/\s+/g, ' ');
    if (t.includes('qa-c2-')) seen.push(t.slice(0, 160));
  }
  console.log('ROWS:', JSON.stringify(seen, null, 1));
  const s1row = seen.find(t => t.includes('qa-c2-s1-'));
  expect(s1row, 'строка s1 с бейджем «пропущено после 3 отказов»').toBeTruthy();
  expect(s1row).toContain('пропущено после 3 отказов');
});
