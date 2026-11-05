import { test, expect } from '@playwright/test';
import { api, login } from './helpers';
import { readFileSync, writeFileSync } from 'node:fs';

// Замер шапки чата на 360 и 1280: размеры целей, переполнение, обрезанный текст, элементы без имени.
// Данные — из живого стенда, ширины — те же, что у ui_probe.

const IDS = readFileSync('/tmp/probe-ids.txt', 'utf-8').trim().split(/\s+/);
const [projectId, chatId] = IDS;

test.setTimeout(180_000);

async function measure(page: any) {
  return await page.evaluate(() => {
    const trigger = document.querySelector('[data-testid="wiki-chat-menu"] button[title="Действия «Вики» в этом чате"]');
    if (!trigger) return { error: 'кнопка «Вики» не найдена' };
    // шапка — ближайший общий предок кнопки и кнопки «Ещё действия»
    const more = document.querySelector('button[aria-label="Ещё действия"]');
    let header: HTMLElement | null = trigger as HTMLElement;
    while (header && !(header.querySelector('button[aria-label="Ещё действия"]'))) header = header.parentElement;
    const buttons = Array.from((header ?? document).querySelectorAll('button')).map((b: any) => {
      const r = b.getBoundingClientRect();
      const name = (b.getAttribute('aria-label') || b.getAttribute('title') || (b.textContent || '').trim()).slice(0, 40);
      return {
        name,
        w: Math.round(r.width * 10) / 10,
        h: Math.round(r.height * 10) / 10,
        small: r.width < 40 || r.height < 40,
        unnamed: !name,
      };
    });
    return {
      buttons,
      docOverflowX: document.documentElement.scrollWidth - document.documentElement.clientWidth,
      headerOverflowX: header ? header.scrollWidth - header.clientWidth : null,
    };
  });
}

test('шапка чата на 360 и 1280: размеры целей и переполнение', async ({ browser, playwright, baseURL }) => {
  const request = await playwright.request.newContext({ baseURL });
  const token = await login(request);
  const client = api(request, token);
  await client.put('/api/feature-flags/llm-wiki', { enabled: true });

  const out: any = {};
  for (const w of [360, 1280]) {
    const ctx = await browser.newContext({ viewport: { width: w, height: 800 } });
    await ctx.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
    const page = await ctx.newPage();
    await page.goto(`/#/project/${projectId}/chat/${chatId}`);
    await page.waitForTimeout(3500);
    out[w] = await measure(page);
    await ctx.close();
  }
  writeFileSync('/tmp/header-measure.json', JSON.stringify(out, null, 1));
  test.info().annotations.push({ type: 'note', description: JSON.stringify(out) });

  for (const w of [360, 1280]) {
    const m = out[w];
    expect(m.error ?? '', `шапка на ${w} измерена`).toBe('');
    const wiki = m.buttons.find((b: any) => b.name.includes('Вики'));
    expect(wiki, `кнопка «Вики» в шапке на ${w} есть`).toBeTruthy();
    // на 360 кнопка — тач-цель (≥ 40 px), на 1280 это строка шапки десктопа (высота ряда 34 px по дизайн-системе)
    const minH = w === 360 ? 40 : 32;
    expect(wiki.h, `высота кнопки «Вики» на ${w} ≥ ${minH} px`).toBeGreaterThanOrEqual(minH);
    expect(wiki.unnamed, `у кнопки «Вики» есть имя`).toBeFalsy();
    expect(m.docOverflowX, `нет горизонтального переполнения документа на ${w}`).toBeLessThanOrEqual(1);
  }
});
