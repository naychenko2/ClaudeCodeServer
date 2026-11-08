import { test, expect } from '@playwright/test';
import { api, login } from './helpers';
import { readFileSync, mkdirSync } from 'node:fs';

// Снимки меню «Вики» для отчёта: шапка на 1280 и 360, светлая и тёмная темы.
const IDS = readFileSync('/tmp/probe-ids.txt', 'utf-8').trim().split(/\s+/);
const [projectId, chatId] = IDS;
const OUT = '/home/an/Sources/ClaudeCodeServer-wiki-create/.cc-attachments/qa';

test.setTimeout(180_000);

test('снимки меню «Вики»', async ({ browser, playwright, baseURL }) => {
  const request = await playwright.request.newContext({ baseURL });
  const token = await login(request);
  const client = api(request, token);
  await client.put('/api/feature-flags/llm-wiki', { enabled: true });
  mkdirSync(OUT, { recursive: true });

  for (const [w, scheme] of [[1280, 'light'], [360, 'light'], [1280, 'dark']] as const) {
    const ctx = await browser.newContext({ viewport: { width: w, height: w === 360 ? 780 : 800 }, colorScheme: scheme });
    await ctx.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
    const page = await ctx.newPage();
    await page.goto(`/#/project/${projectId}/chat/${chatId}`);
    await page.waitForTimeout(3500);
    const trigger = page.locator('[data-testid="wiki-chat-menu"] button[title="Действия «Вики» в этом чате"]').first();
    await expect(trigger).toBeVisible({ timeout: 20000 });
    await trigger.click();
    await page.waitForTimeout(800);
    await page.screenshot({ path: `${OUT}/wiki-menu-${w}-${scheme}.png` });
    await ctx.close();
  }
  expect(true).toBeTruthy();
});
