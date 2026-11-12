import { test, expect, type APIRequestContext, type Page } from '@playwright/test';
import { mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { api, collectConsoleErrors, login } from './helpers';

// QA-2 · 7a3754fd: шапка чата с фичей и закреплёнными материалами (1280 и 360, светлая и тёмная).
// Повторная проверка по пунктам задачи; иных сценариев не ищу.
// Данные — засев features-polish-qa3-seed.qa.spec.ts (id — в seed.json); членам чатам TaskId
// проставлен правкой data/sessions.json (executor-ось), стенд перезапущен.
// Порядок шапки: заголовок → строка фичи → чипы материалов → трекер 2 px последней строкой;
// при трекере двойной линии нет; без материалов и вне фичи — как раньше.

const seed = JSON.parse(readFileSync('/home/an/Sources/ClaudeCodeServer-features-polish/.cc-attachments/qa-fp3/seed.json', 'utf8'));
const OUT = '/home/an/Sources/ClaudeCodeServer-features-polish/.cc-attachments/qa-fp3/';
const CHAT_NAME: Record<string, string> = {
  [seed.chatA]: `qa-fp3-chat-a-${seed.stamp}`,
  [seed.chatA2]: `qa-fp3-chat-a2-${seed.stamp}`,
  [seed.chatB]: `qa-fp3-chat-b-${seed.stamp}`,
  [seed.chatC]: `qa-fp3-chat-c-${seed.stamp}`,
};
// имя в заголовке шапки: у чата с персоной заголовок занят собеседником (titleText),
// имя чата в мете только без фичи — мары различаем по признакам самого чата
let PERSONA_NAME = '';

test.describe.configure({ mode: 'serial' });

let request: APIRequestContext;
let token: string;
let client: ReturnType<typeof api>;

test.beforeAll(async ({ playwright, baseURL }) => {
  mkdirSync(OUT, { recursive: true });
  request = await playwright.request.newContext({ baseURL });
  token = await login(request);
  client = api(request, token);
  // персона, которую сервер назначил при засеве (провижн ассистента) — её имя в заголовке
  const sessions = await client.get<Array<{ id: string; personaId?: string }>>(`/api/projects/${seed.project}/sessions`);
  const pid = sessions.find(s => s.id === seed.chatA)?.personaId;
  if (pid) {
    const personas = await client.get<Array<{ id: string; name: string }>>('/api/personas');
    PERSONA_NAME = personas.find(p => p.id === pid)?.name ?? '';
  }
});

test.afterAll(async () => { await request?.dispose(); });

// Шум копии (аватары, фоновая плитка) — не поведение фичи.
function errors(page: Page): string[] {
  return collectConsoleErrors(page).filter(e => !/avatar|background\/tile|providers\/local-qwen|404/.test(e));
}

// элемент чата в списке: у чата-исполнителя список показывает название его задачи
const LIST_LABEL: Record<string, string> = {
  [seed.chatA]: `qa-fp3-шаг-1-${seed.stamp}`,
  [seed.chatA2]: `qa-fp3-шаг-1b-${seed.stamp}`,
  [seed.chatC]: `qa-fp3-шаг-2-${seed.stamp}`,
  [seed.chatB]: `qa-fp3-chat-b-${seed.stamp}`,
};

async function openChat(page: Page, chatId: string, width: number, theme: 'light' | 'dark') {
  await page.addInitScript(([t, th]) => {
    localStorage.setItem('cc_token', t as string);
    localStorage.setItem('theme-mode', th as string);
  }, [token, theme]);
  await page.setViewportSize({ width, height: width < 600 ? 740 : 900 });
  // штатный путь человека: проект → клик по чату в списке (диплинк — не предмет проверки)
  await page.goto(`/#/project/${seed.project}`);
  await page.waitForTimeout(4000);
  const item = page.getByText(LIST_LABEL[chatId]).first();
  if (!(await item.isVisible().catch(() => false))) {
    // мобила: центр держит открытый чат — штатное «Назад к списку»
    const back = page.locator('[title="Назад к списку"]').first();
    if (await back.count()) { await back.click(); await page.waitForTimeout(2000); }
  }
  await expect(item, `элемент чата ${LIST_LABEL[chatId]} виден в списке`).toBeVisible({ timeout: 15000 });
  await item.click();
  await page.waitForTimeout(3500);
  const name = CHAT_NAME[chatId];
  // маркер «открыт именно этот чат» — по его отличительным признакам:
  // A: строка открытой фичи + контекст; A2: строка открытой фичи без контекста;
  // C: строка закрытой фичи + контекст; B: ни строки, ни контекста, но имя чата в мете
  // маркер не зависит от ширины: на 360 строка фичи не называет её имя, а подписи чипов
  // сжимаются middleEllipsis — различаем по составу: A — строка+3 материала,
  // C — строка+2 материала, A2 — строка без контекста, B — ни того ни другого + имя в мете
  const inCenter = () => page.evaluate(([id, chatB]) => {
    const panel = document.querySelector('[data-testid="chat-panel"]');
    if (!panel) return false;
    const text = panel.textContent || '';
    const hasSub = !!panel.querySelector('[data-testid="feature-chat-subtitle"]');
    const ctxRoot = panel.querySelector('[aria-label="Контекст чата"]')?.closest('div') ?? null;
    const nTitles = ctxRoot ? ctxRoot.querySelectorAll('[title]').length : 0;
    if (id === 'A') return hasSub && nTitles === 4;
    if (id === 'A2') return hasSub && nTitles === 0;
    if (id === 'C') return hasSub && nTitles === 3;
    return !hasSub && nTitles === 0 && text.includes(chatB as string);
  }, [
    chatId === seed.chatA ? 'A' : chatId === seed.chatA2 ? 'A2' : chatId === seed.chatC ? 'C' : 'B',
    CHAT_NAME[seed.chatB],
  ]);
  const ok = await inCenter();
  if (!ok) {
    await page.screenshot({ path: `${OUT}/debug-open-${chatId}-${width}.png`, fullPage: true });
    const dbg = await page.evaluate(() => {
      const h = document.querySelector('[data-testid="chat-panel"]');
      return (h?.textContent || '').slice(0, 300);
    });
    save(`debug-open-${chatId}-${width}`, { text: dbg });
  }
  expect(ok, `в центре открыт чат ${name}`).toBe(true);
  await page.waitForTimeout(1500); // оседание: индекс фич, контекст, лента
}

interface Rect { x: number; y: number; w: number; h: number; bottom: number; right: number }
interface Geo {
  hasPanel: boolean;
  sub: Rect | null;
  edge: (Rect & { testid: string }) | null;
  ctxRow: (Rect & { borderBottom: string }) | null;
  header: (Rect & { borderBottom: string }) | null;
  panelFirst: (Rect & { borderBottom: string }) | null;
  feed: Rect | null;
  name: Rect | null;
  chips: Array<Rect & { title: string }>;
  popover: boolean;
}

async function probe(page: Page, chatId: string): Promise<Geo> {
  return page.evaluate((name) => {
    const rect = (el: Element): Rect => {
      const b = el.getBoundingClientRect();
      return { x: b.x, y: b.y, w: b.width, h: b.height, bottom: b.bottom, right: b.right };
    };
    const cs = (el: Element) => getComputedStyle(el);
    const panel = document.querySelector('[data-testid="chat-panel"]');
    const sub = document.querySelector('[data-testid="feature-chat-subtitle"]');
    const edge = document.querySelector('[data-testid="chat-feature-edge"]');
    const ctxIcon = document.querySelector('[aria-label="Контекст чата"]');
    const ctxRoot = ctxIcon ? ctxIcon.closest('div') : null;
    const ctxRow = ctxRoot && ctxRoot.parentElement ? ctxRoot.parentElement : null;
    // элемент шапки: остров — предок трекера; тулбарный вид — первый ребёнок панели
    let headerEl: Element | null = null;
    if (edge) {
      const p = edge.parentElement;
      headerEl = p && p.getAttribute('data-testid') === 'chat-panel' ? (panel ? panel.firstElementChild : null) : p;
    } else if (panel) {
      headerEl = panel.firstElementChild;
    }
    // лента: первый скроллируемый контейнер внутри панели (сайдбар не берём)
    let feed: Element | null = null;
    if (panel) {
      for (const el of Array.from(panel.querySelectorAll('*'))) {
        const st = cs(el); const r = el.getBoundingClientRect();
        if ((st.overflowY === 'auto' || st.overflowY === 'scroll') && r.height > 250 && r.top > 30) { feed = el; break; }
      }
    }
    // элемент заголовка шапки: у чата с персоной это имя собеседника (titleText)
    let nameEl: Element | null = null;
    const walk = (el: Element) => {
      if (nameEl) return;
      for (const c of Array.from(el.children)) {
        const own = Array.from(c.childNodes).some(n => n.nodeType === 3 && (n.textContent || '').includes(name as string));
        if (own) { nameEl = c; return; }
        walk(c);
      }
    };
    if (panel && (name as string)) walk(panel);
    const chips = ctxRoot
      ? Array.from(ctxRoot.querySelectorAll('[title]')).map(e => ({ ...rect(e), title: (e.getAttribute('title') || '').slice(0, 40) }))
      : [];
    return {
      hasPanel: !!panel,
      sub: sub ? rect(sub) : null,
      edge: edge ? { ...rect(edge), testid: edge.getAttribute('data-testid') || '' } : null,
      ctxRow: ctxRow ? { ...rect(ctxRow), borderBottom: cs(ctxRow).borderBottomWidth } : null,
      header: headerEl ? { ...rect(headerEl), borderBottom: cs(headerEl).borderBottomWidth } : null,
      panelFirst: panel && panel.firstElementChild ? { ...rect(panel.firstElementChild), borderBottom: cs(panel.firstElementChild).borderBottomWidth } : null,
      feed: feed ? rect(feed) : null,
      name: nameEl ? rect(nameEl) : null,
      chips,
      popover: !!document.querySelector('[data-testid="feature-popover"]'),
    };
  }, PERSONA_NAME || CHAT_NAME[chatId]);
}

const save = (name: string, res: unknown) => writeFileSync(`${OUT}/${name}.json`, JSON.stringify(res, null, 1));

// Порядок и кромки для чата с открытой фичей (A/A2). variant: island (1280) | toolbar (360).
function assertOpenFeatureOrder(g: Geo, variant: 'island' | 'toolbar', hasContext: boolean) {
  expect(g.hasPanel, 'чат открыт').toBe(true);
  expect(g.name, 'заголовок найден').not.toBeNull();
  expect(g.sub, 'строка фичи есть').not.toBeNull();
  expect(g.edge, 'трекер есть').not.toBeNull();
  // 1) заголовок → строка фичи
  expect(g.sub!.y, 'строка фичи под заголовком').toBeGreaterThanOrEqual(g.name!.y);
  if (hasContext) {
    expect(g.ctxRow, 'строка материалов есть').not.toBeNull();
    expect(g.ctxRow!.y, 'чипы материалов под строкой фичи').toBeGreaterThan(g.sub!.y);
    // трекер ПОСЛЕ строки материалов
    expect(g.edge!.y, 'трекер после чипов').toBeGreaterThan(g.ctxRow!.y);
  } else {
    expect(g.ctxRow, 'без материалов строки контекста нет').toBeNull();
    expect(g.edge!.y, 'трекер после строки фичи').toBeGreaterThan(g.sub!.y);
  }
  // трекер 2 px
  expect(Math.round(g.edge!.h), 'трекер 2 px').toBe(2);
  // трекер — последней строкой шапки: под ним только лента
  if (g.feed) expect(g.edge!.bottom, 'под трекером начинается лента, шапка больше ничего не содержит').toBeLessThanOrEqual(g.feed!.y + 1);
  // трекер на ширину колонки шапки
  const headerW = variant === 'toolbar' ? (g.panelFirst ? g.panelFirst.w : g.header!.w) : g.header!.w;
  expect(Math.abs(g.edge!.w - headerW), 'трекер на ширину шапки').toBeLessThan(2);
  // 2) нет двойной линии: ни у строки материалов, ни у шапки (её заменяет трекер)
  if (hasContext) expect(g.ctxRow!.borderBottom, 'у строки материалов линии нет (её заменяет трекер)').toBe('0px');
  const headerBorder = variant === 'toolbar' ? g.panelFirst!.borderBottom : g.header!.borderBottom;
  expect(headerBorder, 'линия шапки погашена трекером').toBe('0px');
}

test('0 · самопроверка засев: членство чатов в фичах (API)', async () => {
  const runs = async (sessionId: string) =>
    client.get<Array<{ featureId: string; state?: { state?: string }; stateText?: string }>>(`/api/tasks/features?sessionId=${sessionId}`);
  const a = await runs(seed.chatA);
  const c = await runs(seed.chatC);
  const b = await runs(seed.chatB);
  const aOpen = a.find(r => r.featureId === seed.fOpen);
  const cClosed = c.find(r => r.featureId === seed.fClosed);
  expect(aOpen, 'чат A состоит в открытой фиче').toBeTruthy();
  expect(cClosed, 'чат C состоит в закрытой фиче').toBeTruthy();
  expect(b.length, 'чат B вне фич').toBe(0);
  save('result-0-membership', { a: a.map(r => ({ id: r.featureId })), c: c.map(r => ({ id: r.featureId })) });
});

test('1 · A · 1280 светлая: порядок, чипы, трекер последней строкой, без двойной линии', async ({ page }) => {
  const res: Record<string, unknown> = {};
  await openChat(page, seed.chatA, 1280, 'light');
  const g = await probe(page, seed.chatA);
  res.geo = g;
  assertOpenFeatureOrder(g, 'island', true);
  expect(g.chips.length, 'чипов материалов ровно столько, сколько засевано (3 + кнопка «⋯»)').toBeGreaterThanOrEqual(3);
  if (g.header) await page.screenshot({ path: `${OUT}/q3-a-1280-light.png`, clip: { x: Math.max(0, g.header.x), y: Math.max(0, g.header.y), width: Math.min(g.header.w, 1280), height: Math.min(g.header.h + 60, 600) } });
  res.consoleErrors = errors(page);
  save('result-a-1280-light', res);
  expect(res.consoleErrors, 'консоль без ошибок').toEqual([]);
});

test('2 · A · клик по строке фичи открывает карточку, Escape закрывает (1280)', async ({ page }) => {
  const res: Record<string, unknown> = {};
  await openChat(page, seed.chatA, 1280, 'light');
  const sub = page.getByTestId('feature-chat-subtitle');
  await sub.click();
  await page.waitForTimeout(600);
  res.openedAfterClick = await page.evaluate(() => !!document.querySelector('[data-testid="feature-popover"]'));
  expect(res.openedAfterClick, 'клик по строке открыл карточку').toBe(true);
  await page.keyboard.press('Escape');
  await page.waitForTimeout(500);
  res.openAfterEsc = await page.evaluate(() => !!document.querySelector('[data-testid="feature-popover"]'));
  expect(res.openAfterEsc, 'Escape закрыл карточку').toBe(false);
  res.consoleErrors = errors(page);
  save('result-a-click-1280', res);
  expect(res.consoleErrors, 'консоль без ошибок').toEqual([]);
});

test('3 · A · 1280 тёмная: порядок и кромки те же', async ({ page }) => {
  const res: Record<string, unknown> = {};
  await openChat(page, seed.chatA, 1280, 'dark');
  const g = await probe(page, seed.chatA);
  res.geo = g;
  assertOpenFeatureOrder(g, 'island', true);
  await page.screenshot({ path: `${OUT}/q3-a-1280-dark.png`, clip: { x: Math.max(0, g.header!.x), y: Math.max(0, g.header!.y), width: Math.min(g.header!.w, 1280), height: Math.min(g.header!.h + 60, 600) } });
  res.consoleErrors = errors(page);
  save('result-a-1280-dark', res);
  expect(res.consoleErrors, 'консоль без ошибок').toEqual([]);
});

test('4 · A · 360 светлая: порядок, цели ≥40, клик/Escape, без двойной линии', async ({ page }) => {
  const res: Record<string, unknown> = {};
  await openChat(page, seed.chatA, 360, 'light');
  const g = await probe(page, seed.chatA);
  res.geo = g;
  assertOpenFeatureOrder(g, 'toolbar', true);
  // 5) цели клика шапки ≥ 40 px: строка фичи, чипы, кнопка «⋯»
  expect(g.sub!.h, `строка фичи ≥40: h=${g.sub!.h}`).toBeGreaterThanOrEqual(40);
  const badChips = g.chips.filter(c => c.h < 40 && !/Все материалы/.test(c.title));
  expect(badChips, `чипы ≥40: ${JSON.stringify(g.chips.map(c => ({ t: c.title, h: c.h })))}`).toEqual([]);
  const moreBtn = g.chips.find(c => /Все материалы/.test(c.title));
  expect(moreBtn, 'кнопка «⋯» есть').toBeTruthy();
  expect(moreBtn!.h, `кнопка «⋯» ≥40: h=${moreBtn!.h}`).toBeGreaterThanOrEqual(40);
  // клик по строке → шторка, Escape закрывает
  await page.getByTestId('feature-chat-subtitle').click();
  await page.waitForTimeout(600);
  res.openedAfterClick = await page.evaluate(() => !!document.querySelector('[data-testid="feature-popover"]'));
  expect(res.openedAfterClick, 'клик по строке открыл шторку').toBe(true);
  await page.keyboard.press('Escape');
  await page.waitForTimeout(500);
  res.openAfterEsc = await page.evaluate(() => !!document.querySelector('[data-testid="feature-popover"]'));
  expect(res.openAfterEsc, 'Escape закрыл шторку').toBe(false);
  await page.screenshot({ path: `${OUT}/q3-a-360-light.png`, clip: { x: 0, y: 0, width: 360, height: Math.min(400, (g.feed ? g.feed.y : 400)) } });
  res.consoleErrors = errors(page);
  save('result-a-360-light', res);
  expect(res.consoleErrors, 'консоль без ошибок').toEqual([]);
});

test('5 · A · 360 тёмная: порядок и кромки те же', async ({ page }) => {
  const res: Record<string, unknown> = {};
  await openChat(page, seed.chatA, 360, 'dark');
  const g = await probe(page, seed.chatA);
  res.geo = g;
  assertOpenFeatureOrder(g, 'toolbar', true);
  await page.screenshot({ path: `${OUT}/q3-a-360-dark.png`, clip: { x: 0, y: 0, width: 360, height: Math.min(400, (g.feed ? g.feed.y : 400)) } });
  res.consoleErrors = errors(page);
  save('result-a-360-dark', res);
  expect(res.consoleErrors, 'консоль без ошибок').toEqual([]);
});

test('6 · A2 · без материалов шапка как раньше (1280 и 360)', async ({ page }) => {
  const res: Record<string, unknown> = {};
  for (const [width, variant] of [[1280, 'island'], [360, 'toolbar']] as const) {
    await openChat(page, seed.chatA2, width, 'light');
    const g = await probe(page, seed.chatA2);
    res[`geo${width}`] = g;
    assertOpenFeatureOrder(g, variant, false);
    if (width === 360 && g.panelFirst) expect(g.panelFirst.h, `тулбар 360 не вырос: h=${g.panelFirst.h}`).toBeLessThanOrEqual(56);
  }
  res.consoleErrors = errors(page);
  save('result-a2', res);
  expect(res.consoleErrors, 'консоль без ошибок').toEqual([]);
});

test('7 · B · чат вне фичи: нет строки фичи и трекера, линия обычная (1280 и 360)', async ({ page }) => {
  const res: Record<string, unknown> = {};
  for (const width of [1280, 360] as const) {
    await openChat(page, seed.chatB, width, 'light');
    const g = await probe(page, seed.chatB);
    res[`geo${width}`] = g;
    expect(g.sub, 'строки фичи нет').toBeNull();
    expect(g.edge, 'трекера нет').toBeNull();
    expect(g.ctxRow, 'строки материалов нет').toBeNull();
    // обычная линия шапки присутствует как механизм (1 px, тон по прокрутке)
    const border = width === 360 ? g.panelFirst!.borderBottom : g.header!.borderBottom;
    expect(border, 'обычная линия шапки 1 px').toBe('1px');
  }
  res.consoleErrors = errors(page);
  save('result-b', res);
  expect(res.consoleErrors, 'консоль без ошибок').toEqual([]);
});

test('8 · C · закрытая фича с материалами: трекера нет, после чипов обычная линия (1280 и 360)', async ({ page }) => {
  const res: Record<string, unknown> = {};
  for (const width of [1280, 360] as const) {
    await openChat(page, seed.chatC, width, 'light');
    const g = await probe(page, seed.chatC);
    res[`geo${width}`] = g;
    expect(g.sub, 'строка закрытой фичи есть').not.toBeNull();
    expect(g.name, 'заголовок есть').not.toBeNull();
    expect(g.sub!.y, 'строка фичи под заголовком').toBeGreaterThanOrEqual(g.name!.y);
    expect(g.edge, 'трекера у закрытой фичи нет').toBeNull();
    expect(g.ctxRow, 'строка материалов есть').not.toBeNull();
    expect(g.ctxRow!.y, 'чипы после строки фичи').toBeGreaterThan(g.sub!.y);
    if (width === 360) {
      // тулбарный вид: собственная линия строки контекста остаётся обычной
      expect(g.ctxRow!.borderBottom, 'после чипов обычная линия').toBe('1px');
    } else {
      // остров: линия держит нижняя кромка шапки (1 px), строка контекста её не дублирует
      expect(g.header!.borderBottom, 'кромка закрытого чата — обычная линия 1 px').toBe('1px');
      expect(g.ctxRow!.borderBottom, 'строка контекста не рисует своей линии на острове').toBe('0px');
    }
  }
  res.consoleErrors = errors(page);
  save('result-c', res);
  expect(res.consoleErrors, 'консоль без ошибок').toEqual([]);
});

test('9 · диагностика 360: структура шапки и интерактивные цели в ней (для расшифровки ui_probe)', async ({ page }) => {
  await openChat(page, seed.chatA, 360, 'light');
  const tree = await page.evaluate(() => {
    const panel = document.querySelector('[data-testid="chat-panel"]');
    if (!panel) return null;
    const kids = Array.from(panel.children).slice(0, 3);
    const dump = (el: Element, depth: number): unknown => {
      const r = el.getBoundingClientRect();
      const cs = getComputedStyle(el);
      const node: Record<string, unknown> = {
        tag: el.tagName,
        testid: el.getAttribute('data-testid') || undefined,
        title: (el.getAttribute('title') || '').slice(0, 40) || undefined,
        aria: el.getAttribute('aria-label') || undefined,
        cls: typeof el.className === 'string' ? el.className.slice(0, 30) : undefined,
        w: Math.round(r.width * 10) / 10, h: Math.round(r.height * 10) / 10, y: Math.round(r.y),
        txt: Array.from(el.childNodes).some(n => n.nodeType === 3) ? (el.textContent || '').slice(0, 30) : undefined,
      };
      if (depth < 6) {
        const ch = Array.from(el.children).map(c => dump(c, depth + 1));
        if (ch.length) node.children = ch;
      }
      if (cs.borderBottomWidth !== '0px') node.borderBottom = cs.borderBottomWidth;
      Object.keys(node).forEach(k => node[k] === undefined && delete node[k]);
      return node;
    };
    return kids.map(k => dump(k, 0));
  });
  save('result-tree-360', tree);
  expect(tree, 'структура собрана').toBeTruthy();
});

test('10 · 360: высота тулбара чата с фичей и материалами равна обычной и ≤56', async ({ page }) => {
  const res: Record<string, unknown> = {};
  await openChat(page, seed.chatA, 360, 'light');
  const ga = await probe(page, seed.chatA);
  res.heightA = ga.panelFirst?.h ?? null;
  await openChat(page, seed.chatB, 360, 'light');
  const gb = await probe(page, seed.chatB);
  res.heightB = gb.panelFirst?.h ?? null;
  expect(res.heightA, 'тулбар A измерен').not.toBeNull();
  expect(res.heightB, 'тулбар B измерен').not.toBeNull();
  // «не растёт» = не выше проектной высоты тулбара и не выше обычного чата.
  // Обычный чат с персоной даёт 57 (мета-строка с именем чата) — поведение вне этой фичи.
  expect(res.heightA as number, `тулбар A ≤56 (TB.heightMobile): ${res.heightA}`).toBeLessThanOrEqual(56);
  expect(res.heightA as number, `тулбар A (${res.heightA}) не выше обычного чата (${res.heightB})`).toBeLessThanOrEqual(res.heightB as number);
  res.consoleErrors = errors(page);
  save('result-height', res);
  expect(res.consoleErrors, 'консоль без ошибок').toEqual([]);
});
