import { test, expect, type Page } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';

// QA-П1 (после M-1) · LLM-wiki — замер шапки чата на 320/360/1280.
// Временный файл: удаляется после прогона.
//   PLAYWRIGHT_BASE_URL=http://127.0.0.1:5555 QA_WIKI_SHOTS=../.cc-attachments/qa-llm-wiki-p1 \
//     npx playwright test e2e/qa-llm-wiki-m1.spec.ts

const SHOTS = process.env.QA_WIKI_SHOTS || path.join(process.cwd(), '..', '.cc-attachments', 'qa-llm-wiki-p1');
const USER = process.env.E2E_USER || 'admin';
const PASS = process.env.E2E_PASS || '12345';
const PID = '047a15f0-1bda-4567-8bbb-ed39f55c376e';
// Фикстуры этого прогона: 7054c067 — разобран (applied, хроника создана),
// a236bcff — разбирается прямо из UI (пункт 3: точка после разбора)
// Фикстуры этого прогона (разобраны, applied + хроника):
const CHAT_DONE = '18b5e5c5-ca1b-471b-8d14-46743ae78fea';   // QA-M1-точка-A — разбор, точка, reload, откат
const CHAT_RETRACT = 'aaea9581-9843-46d5-89bf-dc34ed84ca4e'; // QA-M1-откат-B — меню, откат, снятие хроники
const CHAT_LIVE = '4e8b35a1-9bb1-4d48-aaf7-c62fcabd5b72';     // QA-M1-точка-G — разбор из UI (п.3)
const CHAT_RETRACT2 = '51decd57-7435-4f34-9937-c8eb0c0e5164'; // QA-M1-откат-E — applied, для отката под точкой
// Чат без записи в сторе вики: у него нет chroniclePath → пункта «Открыть хронику» нет
const CHAT_NOCHRON = 'c79592c8-7cdf-419e-a161-a0fe01ef55df'; // QA-M1-без-хроники-H — не разобран, записи в вики нет

const D = { width: 1280, height: 900 };
const W360 = { width: 360, height: 780 };
const W320 = { width: 320, height: 720 };

async function shot(page: Page, name: string) {
  fs.mkdirSync(SHOTS, { recursive: true });
  await page.screenshot({ path: path.join(SHOTS, name), fullPage: false });
}

async function loginToken(user = USER, pass = PASS): Promise<string> {
  const r = await fetch(`${process.env.PLAYWRIGHT_BASE_URL}/api/auth/login`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ username: user, password: pass }),
  });
  return (await r.json()).token as string;
}

test.use({ serviceWorkers: 'block' });

// Триггер меню «Вики» на компакте: кнопка с подписью «Вики» (triggerLabel).
// У неё нет data-testid — ищем по тексту в шапке.
const wikiTrigger = (page: Page) => page.locator('button', { hasText: /^Вики$/ }).first();

// Deeplink на чат (#/project/{pid}/chat/{id}) без reload приводит маршрут к #/project/{pid}
// и открывает другой чат — известная грабля Hash-SPA в продукте (не дефект фичи «Вики»).
// Поэтому открываем чат кликом по списку и сверяем заголовок шапки.
async function openChat(page: Page, chatId: string, chatName: string, wait = 4000) {
  // Deeplink + reload: без reload маршрут схлопывается в #/project/{pid} (грабля Hash-SPA
  // в продукте, не дефект фичи). Клик по списку ненадёжен: после reload список чатов
  // может быть не отрисован — элемент есть, но невидим.
  // 1) список чатов проекта + клик по названию (штатный путь человека)
  await page.goto(`/#/project/${PID}`);
  await page.waitForTimeout(5000);
  const link = page.getByText(chatName, { exact: true }).first();
  if (await link.count()) {
    try {
      await link.click({ timeout: 15000 });
    } catch {
      console.log(`[навигация] клик по «${chatName}» не удался, deeplink+reload`);
      await page.goto(`/#/project/${PID}/chat/${chatId}`);
      await page.reload();
    }
  } else {
    console.log(`[навигация] «${chatName}» нет в списке, deeplink+reload`);
    await page.goto(`/#/project/${PID}/chat/${chatId}`);
    await page.reload();
  }
  await page.waitForTimeout(wait);
  const header = (await page.locator('body').innerText()).slice(0, 300);
  console.log(`[навигация] открыт «${chatName}»:`, header.includes(chatName) ? 'да' : `НЕТ (шапка: ${header.slice(0, 120)})`);
}

test('п.1 — 360 и 320: одна кнопка «Вики», шапка без обрезки, цели касания', async ({ page, context, playwright, baseURL }) => {
  const request = await playwright.request.newContext({ baseURL });
  const token = await loginToken();
  await request.dispose();
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);

  const reqs: string[] = [];
  page.on('request', r => { if (r.url().includes('/api/wiki')) reqs.push(`${r.method()} ${r.url().split('/api/wiki')[1]}`); });

  // Один раз открываем чат и дальше меняем только ширину окна: useWindowWidth реагирует
  // на resize, а повторная навигация по списку на втором шаге блокируется (клик не actionability).
  await page.setViewportSize(W360);
  await openChat(page, CHAT_DONE, 'QA-M1-точка-A');

  for (const [label, vp] of [['360', W360], ['320', W320]] as const) {
    await page.setViewportSize(vp);
    await page.waitForTimeout(2500);

    const trig = wikiTrigger(page);
    console.log(`[${label}] триггеров «Вики» в шапке:`, await trig.count());
    console.log(`[${label}] кнопок ряда wiki-chat-* (desktop-ряд):`, await page.locator('[data-testid^="wiki-chat-"]').count());

    const box = await trig.boundingBox();
    console.log(`[${label}] триггер:`, JSON.stringify(box));

    // Горизонтальный скролл документа и шапки
    const overflow = await page.evaluate(() => {
      const de = document.documentElement;
      const btn = [...document.querySelectorAll('button')].find(b => b.textContent?.trim() === 'Вики');
      // Контейнер шапки: ближайший предок триггера с overflow-x не visible
      let hdr: HTMLElement | null = null;
      let el = btn?.parentElement ?? null;
      while (el) {
        const ox = getComputedStyle(el).overflowX;
        if (ox === 'hidden' || ox === 'auto' || ox === 'scroll') { hdr = el; break; }
        el = el.parentElement;
      }
      const hb = hdr?.getBoundingClientRect();
      return {
        docScrollW: de.scrollWidth,
        docClientW: de.clientWidth,
        innerW: window.innerWidth,
        headerScrollW: hdr?.scrollWidth ?? null,
        headerClientW: hdr?.clientWidth ?? null,
        headerRect: hb ? { x: hb.x, w: hb.width } : null,
        headerOverflowX: hdr ? getComputedStyle(hdr).overflowX : null,
      };
    });
    console.log(`[${label}] переполнение:`, JSON.stringify(overflow));

    // Все интерактивные элементы верхних 140px — в пределах экрана?
    const escapes = await page.evaluate((vw: number) => {
      const out: string[] = [];
      document.querySelectorAll<HTMLElement>('button, a, input, [role="button"]').forEach(el => {
        const r = el.getBoundingClientRect();
        if (r.width === 0 || r.height === 0) return;
        if (r.top > 140) return;
        if (r.right > vw + 1 || r.left < -1) {
          out.push(`${el.tagName}.${el.className || '-'} «${(el.textContent || '').trim().slice(0, 30)}» x=${r.x.toFixed(1)} w=${r.width.toFixed(1)} right=${r.right.toFixed(1)}`);
        }
      });
      return out;
    }, vp.width);
    console.log(`[${label}] вылезающие за экран элементы шапки:`, JSON.stringify(escapes));

    await shot(page, `m1-header-${label}.png`);
  }
  console.log('[замер] запросы /api/wiki за прогон:', JSON.stringify(reqs));
});

test('п.2 — меню на 360: боттом-шит, три пункта, откат красный, подтверждение и откат', async ({ page, context, playwright, baseURL }) => {
  const request = await playwright.request.newContext({ baseURL });
  const token = await loginToken();
  await request.dispose();
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);

  await page.setViewportSize(W360);
  await openChat(page, CHAT_RETRACT, 'QA-M1-откат-B');

  await wikiTrigger(page).click();
  await page.waitForTimeout(1200);
  const sheet = page.locator('.cc-sheet-card');
  console.log('[меню] боттом-шит (role=dialog + cc-sheet-card):', await sheet.count(), await page.locator('[role="dialog"]').count());
  await shot(page, 'm1-menu-360.png');

  const items = page.locator('[role="dialog"] [role="menuitem"]');
  const n = await items.count();
  console.log('[меню] пунктов:', n);
  for (let i = 0; i < n; i++) {
    const it = items.nth(i);
    const b = await it.boundingBox();
    const color = await it.evaluate(el => getComputedStyle(el).color);
    console.log(`[меню] пункт ${i}: «${(await it.innerText()).trim()}» высота=${b?.height} цвет=${color}`);
  }
  console.log('[меню] тексты:', JSON.stringify(await items.allInnerTexts()));
  const dangerColor = await page.evaluate(() => getComputedStyle(document.documentElement).getPropertyValue('--c-danger-text'));
  console.log('[меню] C.dangerText из темы:', dangerColor.trim());

  // Откат: пункт → подтверждение → «Откатить всё»
  const retract = items.filter({ hasText: 'Откатить всё из этого чата' }).first();
  await retract.click();
  await page.waitForTimeout(1000);
  await shot(page, 'm1-retract-confirm-360.png');
  const confirmText = await page.getByText('Откатить всё из этого чата?', { exact: true }).count();
  const confirmBody = await page.locator('body').innerText();
  console.log('[откат] заголовок подтверждения найден:', confirmText);
  console.log('[откат] текст подтверждения:', JSON.stringify(confirmBody.slice(0, 400)));
  const confirmBtn = page.getByRole('button', { name: 'Откатить всё', exact: true }).first();
  console.log('[откат] кнопка подтверждения:', await confirmBtn.count());
  await confirmBtn.click();
  await page.waitForTimeout(4000);
  await shot(page, 'm1-after-retract-360.png');
  console.log('[откат] текст после отката:', JSON.stringify((await page.locator('body').innerText()).slice(0, 600)));

  // Хроника снята? Проверяем REST: чат и список вики
  const r = await fetch(`${baseURL}/api/wiki/chats`, { headers: { Authorization: `Bearer ${token}` } });
  const list = await r.json() as { items: Array<{ sessionId: string; chroniclePath: string | null }> };
  const rec = list.items.find(i => i.sessionId === CHAT_RETRACT);
  console.log('[откат] chroniclePath после отката:', JSON.stringify(rec?.chroniclePath ?? null));
});

test.setTimeout(300_000);
test('п.3 — точка-сигнал на закрытом триггере: после разбора, после отката, после reload', async ({ page, context, playwright, baseURL }) => {
  const request = await playwright.request.newContext({ baseURL });
  const token = await loginToken();
  await request.dispose();
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);

  // Точка — span 8×8 с border-radius 50% внутри триггера
  const dotInfo = async (p: Page) => p.evaluate(() => {
    const btn = [...document.querySelectorAll('button')].find(b => b.textContent?.trim() === 'Вики');
    if (!btn) return { found: false as const };
    const dots = [...btn.querySelectorAll<HTMLElement>('span')].filter(s => {
      const r = s.getBoundingClientRect();
      return Math.abs(r.width - 8) < 1.5 && Math.abs(r.height - 8) < 1.5 && getComputedStyle(s).borderRadius.includes('50%');
    });
    return {
      found: true as const,
      count: dots.length,
      color: dots.map(d => getComputedStyle(d).backgroundColor),
    };
  });

  await page.setViewportSize(W360);
  // Разбор из UI на чате без разбора → точка цвета успеха на закрытом триггере
  await openChat(page, CHAT_LIVE, 'QA-M1-точка-G');
  await wikiTrigger(page).click();
  await page.waitForTimeout(1000);
  await page.locator('[role="dialog"] [role="menuitem"]', { hasText: 'Разобрать этот чат' }).first().click();
  console.log('[точка] разбор запущен, жду…');
  for (let i = 0; i < 24; i++) {
    await page.waitForTimeout(5000);
    const st = await (await fetch(`${baseURL}/api/wiki/chats/${CHAT_LIVE}/analyze-status`, { headers: { Authorization: `Bearer ${token}` } })).json() as { running: boolean };
    if (!st.running) break;
  }
  const st = await (await fetch(`${baseURL}/api/wiki/chats/${CHAT_LIVE}/analyze-status`, { headers: { Authorization: `Bearer ${token}` } })).json();
  console.log('[точка] исход разбора:', JSON.stringify(st).slice(0, 300));

  // Закрываем меню (Escape) и смотрим точку на ЗАКРЫТОМ триггере.
  // Во время разбора статус = «разбираю…» (C.textMuted) — точка приглушённая; когда исход
  // применён, статус = «готово» (C.success) — точка зелёная. Ждём смены цвета.
  await page.keyboard.press('Escape');
  let dotAfterAnalyze: unknown = null;
  for (let i = 0; i < 20; i++) {
    await page.waitForTimeout(3000);
    dotAfterAnalyze = await dotInfo(page);
    const colors = (dotAfterAnalyze as { color: string[] }).color ?? [];
    if (colors.some(c => c === 'rgb(94, 139, 78)')) break;
  }
  console.log('[точка] на закрытом триггере после разбора:', JSON.stringify(dotAfterAnalyze));
  await shot(page, 'm1-dot-after-analyze-360.png');

  // Reload: статус восстанавливается → точка есть
  await page.reload();
  await page.waitForTimeout(8000);
  console.log('[точка] после reload страницы:', JSON.stringify(await dotInfo(page)));
  await shot(page, 'm1-dot-after-reload-360.png');

  // Откат на чате с применённым пакетом (откат-E, пакет applied, не откатан) → точка приглушённая
  await openChat(page, CHAT_RETRACT2, 'QA-M1-откат-E');
  await wikiTrigger(page).click();
  await page.waitForTimeout(1000);
  await page.locator('[role="dialog"] [role="menuitem"]', { hasText: 'Откатить всё из этого чата' }).first().click();
  await page.waitForTimeout(800);
  await page.getByRole('button', { name: 'Откатить всё', exact: true }).first().click();
  await page.waitForTimeout(4000);
  await page.keyboard.press('Escape');
  await page.waitForTimeout(1200);
  console.log('[точка] после отката:', JSON.stringify(await dotInfo(page)));
  await shot(page, 'm1-dot-after-retract-360.png');
});

test('п.3б — пункт «Открыть хронику» только при наличии хроники', async ({ page, context, playwright }) => {
  const request = await playwright.request.newContext({ baseURL: process.env.PLAYWRIGHT_BASE_URL });
  const token = await loginToken();
  await request.dispose();
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);

  await page.setViewportSize(W360);
  await openChat(page, CHAT_NOCHRON, 'QA-M1-без-хроники-H');
  await wikiTrigger(page).click();
  await page.waitForTimeout(1200);
  const items = page.locator('[role="dialog"] [role="menuitem"]');
  console.log('[хроника] пунктов у чата без хроники:', await items.count());
  console.log('[хроника] тексты:', JSON.stringify(await items.allInnerTexts()));
  await shot(page, 'm1-menu-no-chronicle-360.png');
});

// После отката: остаётся ли пункт «Открыть хронику» (хроника снята с диска, но список
// вики отдаёт путь по формуле) и куда ведёт клик.
test('п.7 — «Открыть хронику» после отката', async ({ page, context, playwright }) => {
  const request = await playwright.request.newContext({ baseURL: process.env.PLAYWRIGHT_BASE_URL });
  const token = await loginToken();
  await request.dispose();
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);

  await page.setViewportSize(W360);
  await openChat(page, '51decd57-7435-4f34-9937-c8eb0c0e5164', 'QA-M1-откат-E');
  await wikiTrigger(page).click();
  await page.waitForTimeout(1500);
  const items = page.locator('[role="dialog"] [role="menuitem"]');
  console.log('[после отката] пунктов в меню:', await items.count());
  console.log('[после отката] тексты:', JSON.stringify(await items.allInnerTexts()));
  const chron = items.filter({ hasText: 'Открыть хронику' }).first();
  if (await chron.count()) {
    await chron.click();
    await page.waitForTimeout(4000);
    console.log('[после отката] url после клика:', page.url());
    const body = await page.locator('body').innerText();
    console.log('[после отката] текст вики (фрагмент):', JSON.stringify(body.slice(0, 400)));
    await shot(page, 'm1-chronicle-after-retract-360.png');
  }
});

// До отката: пункт «Открыть хронику» ведёт на страницу хроники (содержимое есть).
test('п.7б — «Открыть хронику» до отката ведёт на страницу', async ({ page, context, playwright }) => {
  const request = await playwright.request.newContext({ baseURL: process.env.PLAYWRIGHT_BASE_URL });
  const token = await loginToken();
  await request.dispose();
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);

  await page.setViewportSize(W360);
  await openChat(page, '4e8b35a1-9bb1-4d48-aaf7-c62fcabd5b72', 'QA-M1-точка-G');
  await wikiTrigger(page).click();
  await page.waitForTimeout(1500);
  const chron = page.locator('[role="dialog"] [role="menuitem"]', { hasText: 'Открыть хронику' }).first();
  console.log('[до отката] пункт «Открыть хронику» есть:', await chron.count());
  if (await chron.count()) {
    await chron.click();
    await page.waitForTimeout(5000);
    const body = await page.locator('body').innerText();
    console.log('[до отката] url:', page.url());
    console.log('[до отката] есть «Не найдено»:', body.includes('Не найдено'));
    console.log('[до отката] фрагмент вики:', JSON.stringify(body.slice(0, 300)));
    await shot(page, 'm1-chronicle-before-retract-360.png');
  }
});

test('п.4 — 1280: прежний ряд из трёх кнопок, те же тексты', async ({ page, context, playwright }) => {
  const request = await playwright.request.newContext({ baseURL: process.env.PLAYWRIGHT_BASE_URL });
  const token = await loginToken();
  await request.dispose();
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);

  await page.setViewportSize(D);
  await openChat(page, CHAT_DONE, 'QA-M1-точка-A');
  const btns = page.locator('[data-testid^="wiki-chat-"]');
  console.log('[1280] кнопок вики в шапке:', await btns.count());
  console.log('[1280] подписи:', JSON.stringify(await btns.allInnerTexts()));
  for (const b of await btns.all()) {
    console.log('[1280] кнопка', JSON.stringify((await b.innerText()).trim()), JSON.stringify(await b.boundingBox()));
  }
  console.log('[1280] триггеров-меню «Вики»:', await wikiTrigger(page).count());
  await shot(page, 'm1-header-1280.png');

  // Тот же замер обрезки на десктопе
  const overflow = await page.evaluate(() => {
    const de = document.documentElement;
    return { docScrollW: de.scrollWidth, docClientW: de.clientWidth, innerW: window.innerWidth };
  });
  console.log('[1280] переполнение документа:', JSON.stringify(overflow));

  // Откат на десктопе: та же кнопка, то же подтверждение, тот же итог
  const retract = page.locator('[data-testid="wiki-chat-retract"]');
  await retract.click();
  await page.waitForTimeout(1200);
  const dlg = await page.getByText('Откатить всё из этого чата?', { exact: true }).count();
  console.log('[1280] подтверждение отката открылось:', dlg);
  await shot(page, 'm1-retract-confirm-1280.png');
  await page.getByRole('button', { name: 'Откатить всё', exact: true }).first().click();
  await page.waitForTimeout(4000);
  console.log('[1280] итог отката в шапке:', JSON.stringify(await page.locator('[data-testid="wiki-chat-retract-status"]').allInnerTexts()));
  await shot(page, 'm1-after-retract-1280.png');
});

// Известная граница: колонка «Стены» при широком окне (≥1000) — ширина ОКНА wide, но колонка
// узкая, а порог компактности в WikiChatActions читает ширину ОКНА: ряд из трёх кнопок
// остаётся и может обрезаться по краю колонки. Замер: обрезка воспроизводится или нет.
test('п.6 — граница «Стены»: колонка чатов при широком окне', async ({ page, context, playwright }) => {
  const request = await playwright.request.newContext({ baseURL: process.env.PLAYWRIGHT_BASE_URL });
  const token = await loginToken();
  await request.dispose();
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);

  await page.setViewportSize(D);
  await page.goto('/#/wall');
  await page.waitForTimeout(7000);
  // Стена пустая — добавляем чат через пикер (штатный путь человека)
  // Чаты на стене уже набраны прошлым прогоном (состав стены живёт на сервере).
  // Если стена пуста — добираем первый чат через док (лупа «Найти чат для стены»).
  if (await page.getByText('Соберите свою стену', { exact: false }).count()) {
    const lens = page.getByRole('button', { name: 'Найти чат для стены' }).first();
    if (await lens.count()) {
      await lens.click();
      await page.waitForTimeout(2500);
      const pk = page.getByText('QA-M1-точка-G', { exact: true }).first();
      if (await pk.count()) { await pk.click({ force: true }); await page.waitForTimeout(1500); }
      const done = page.getByRole('button', { name: 'Готово' }).first();
      if (await done.count()) { await done.click(); }
      await page.waitForTimeout(5000);
    }
  }
  await page.waitForTimeout(4000);
  await shot(page, 'm1-wall-1280.png');
  console.log('[Стены] url:', page.url());
  const wall = await page.evaluate(() => {
    const out: string[] = [];
    // Контейнер шапки каждой колонки: ближайший предок кнопки с overflow-x не visible
    const heads: Record<string, unknown> = {};
    document.querySelectorAll<HTMLElement>('button').forEach(b => {
      const t = (b.textContent || '').trim();
      if (t !== 'Разобрать этот чат' && t !== 'Откатить всё из этого чата' && t !== 'Открыть хронику') return;
      const r = b.getBoundingClientRect();
      let el = b.parentElement, hdr: HTMLElement | null = null;
      while (el) {
        const ox = getComputedStyle(el).overflowX;
        if (ox !== 'visible') { hdr = el; break; }
        el = el.parentElement;
      }
      const hr = hdr?.getBoundingClientRect();
      const key = hdr ? `${Math.round(hr!.x)}` : '?';
      if (!heads[key]) {
        heads[key] = hdr ? {
          x: hr!.x, w: hr!.width, right: hr!.right,
          scrollW: hdr!.scrollWidth, clientW: hdr!.clientWidth,
          overflowX: getComputedStyle(hdr!).overflowX,
          clipped: hdr!.scrollWidth > hdr!.clientWidth,
        } : null;
      }
      const cut = hr ? (hr.left - r.left) : 0;
      const cs = getComputedStyle(b);
      out.push(`«${t}» x=${r.x.toFixed(1)} w=${r.width.toFixed(1)} right=${r.right.toFixed(1)} колонкаLeft=${hr ? hr.left.toFixed(1) : '?'} колонкаRight=${hr ? hr.right.toFixed(1) : '?'} срезаноСлева=${cut.toFixed(1)} текст=${cs.textOverflow}/${cs.whiteSpace}`);
    });
    return { buttons: out, heads, docScrollW: document.documentElement.scrollWidth, innerW: window.innerWidth };
  });
  console.log('[Стены] кнопки вики в колонках:', JSON.stringify(wall, null, 1));

  // Кликабельна ли обрезанная первая кнопка? Клик по её видимой части.
  const first = page.getByRole('button', { name: 'Разобрать этот чат' }).first();
  if (await first.count()) {
    const bb = await first.boundingBox();
    console.log('[Стены] первая кнопка:', JSON.stringify(bb));
    await first.click({ timeout: 8000 }).then(
      () => console.log('[Стены] клик по обрезанной кнопке: сработал'),
      e => console.log('[Стены] клик по обрезанной кнопке НЕ сработал:', String(e).split('\n')[0]),
    );
    await page.waitForTimeout(2500);
    console.log('[Стены] диалогов после клика:', await page.locator('[role="dialog"]').count());
    await shot(page, 'm1-wall-after-click-1280.png');
  }
});

test('п.5 — флаг llm-wiki выключен: ни кнопки, ни запросов /api/wiki', async ({ page, context, playwright, baseURL }) => {
  const request = await playwright.request.newContext({ baseURL });
  const token = await loginToken();
  await request.dispose();
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);

  // Гасим флаг у admin на сервере
  const put = await fetch(`${baseURL}/api/feature-flags/llm-wiki`, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${token}` },
    body: JSON.stringify({ enabled: false }),
  });
  console.log('[флаг] PUT:', put.status);

  const reqs: string[] = [];
  page.on('request', r => { if (r.url().includes('/api/wiki')) reqs.push(`${r.method()} ${r.url().split('/api/wiki')[1]}`); });

  await page.setViewportSize(D);
  await openChat(page, CHAT_DONE, 'QA-M1-точка-A');
  console.log('[флаг] кнопок wiki-chat-* при флаге выкл:', await page.locator('[data-testid^="wiki-chat-"]').count());
  console.log('[флаг] триггеров «Вики» при флаге выкл:', await wikiTrigger(page).count());
  await shot(page, 'm1-flagoff-chat-1280.png');

  await page.setViewportSize(W360);
  await openChat(page, CHAT_DONE, 'QA-M1-точка-A');
  console.log('[флаг] на 360 кнопок при выкл:', await page.locator('[data-testid^="wiki-chat-"]').count());
  console.log('[флаг] на 360 триггеров при выкл:', await wikiTrigger(page).count());
  await shot(page, 'm1-flagoff-chat-360.png');
  console.log('[флаг] запросы /api/wiki за сеанс:', JSON.stringify(reqs));

  // Возвращаем флаг
  const put2 = await fetch(`${baseURL}/api/feature-flags/llm-wiki`, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${token}` },
    body: JSON.stringify({ enabled: true }),
  });
  console.log('[флаг] PUT (вернуть):', put2.status);
});
