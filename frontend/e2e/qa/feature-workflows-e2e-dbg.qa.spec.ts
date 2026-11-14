import { test, expect } from '@playwright/test';
import { login } from './helpers';

// Отладка селекторов редактора шаблонов: что действительно в DOM после «+ Новый».

test('Д1: DOM черновика шаблона', async ({ context, playwright, baseURL }) => {
  test.setTimeout(120_000);
  const req = await playwright.request.newContext({ baseURL });
  const token = await login(req);
  await req.dispose();
  const page = await context.newPage();
  page.setViewportSize({ width: 1280, height: 900 });
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
  await page.goto('/#/specialties', { waitUntil: 'domcontentloaded' });
  await page.waitForTimeout(2000);
  await page.getByText('Процессы фичи', { exact: true }).click();
  await page.waitForTimeout(1500);
  await page.getByTestId('new-template').click();
  await page.waitForTimeout(1500);

  const labels = await page.locator('label').evaluateAll(els => els.map(e => ({
    text: (e.textContent ?? '').slice(0, 40), visible: (e as HTMLElement).offsetParent !== null,
  })));
  console.log('Д1 labels:', JSON.stringify(labels.slice(0, 20)));
  const inputs = await page.locator('input, textarea, select').evaluateAll(els => els.map(e => ({
    tag: e.tagName, ph: (e as HTMLInputElement).placeholder ?? '', visible: (e as HTMLElement).offsetParent !== null,
  })));
  console.log('Д1 inputs:', JSON.stringify(inputs.slice(0, 20)));
  const btns = await page.getByRole('button').allInnerTexts();
  console.log('Д1 кнопки:', JSON.stringify(btns.slice(0, 25)));
  expect(true).toBeTruthy();
});
