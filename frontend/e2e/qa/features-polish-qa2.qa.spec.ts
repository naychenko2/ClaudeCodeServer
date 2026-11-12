import { test, expect, type APIRequestContext, type Page } from '@playwright/test';
import { mkdirSync, writeFileSync } from 'node:fs';
import { collectConsoleErrors, login } from './helpers';

// QA-2: повторная проверка четырёх дефектов приёмки 816dbb66 (доводка раздела «Фичи»).
// Иду строго по шагам карточек ef2f1dc3 / 4100c6d8 / b9e6460d / e128aac4; новых сценариев не ищу.
// Данные — копия боевых (фича 11041582 сейчас «идёт» из-за моего QA-шага; 66b5e075 смержена).
// Стенд: start_stand из рабочего дерева чата, порт 5560; вход andrey (E2E_USER/E2E_PASS).

// Чат с персоной и строкой «идёт»: собственный чат этого QA-шага (персона Вера, фича 11041582
// сейчас идёт из-за него). Прямой hash-route и pending-канал на этом стенде открывают последний
// активный чат (подтверждено первым прогоном) — берём тот, что открывается сам, он тот же по классу.
const PID = 'fedfe8c3-eaab-48ae-a5bd-1661bb68f7cc';
const PERSONA_NAME = 'Вера';
const PERSONA_ID = '5270c8a6-7122-49ee-8bc4-a4ebe7aecea3';
const F_MERGED = '66b5e075-f7e5-4761-8a84-e1dd05302aca'; // «LLM-wiki: новая постановка ночной вики», смержена
const F_EPIC = '11041582-a74b-4f54-9d6d-6c0e3e610245'; // «Фичи: упрощение…», сейчас идёт (мой QA-шаг)
const OUT = '/home/an/Sources/ClaudeCodeServer-features-polish/.cc-attachments/qa-fp2/';

test.describe.configure({ mode: 'serial' });

let request: APIRequestContext;
let token: string;

test.beforeAll(async ({ playwright, baseURL }) => {
  mkdirSync(OUT, { recursive: true });
  request = await playwright.request.newContext({ baseURL });
  token = await login(request);
});

test.afterAll(async () => {
  await request?.dispose();
});

function errors(page: Page): string[] {
  const sink = collectConsoleErrors(page);
  // Шум копии данных: отсутствующие аватары и фоновая плитка — не поведение фичи.
  return sink.filter(e => !/avatar|background\/tile|providers\/local-qwen|404/.test(e));
}

async function openChat(page: Page, width: number, height: number) {
  await page.addInitScript(t => localStorage.setItem('cc_token', t as string), token);
  await page.setViewportSize({ width, height });
  await page.goto(`/#/project/${PID}`);
  if (width < 600) {
    // на мобилной раскладке центр держит список чатов: открываем целевой штатным кликом по его
    // элементу списка (тот же путь, что делает человек)
    await page.waitForTimeout(6000);
    const item = page.getByText('повторная проверка QA-2').first();
    await expect(item, 'элемент целевого чата виден в списке').toBeVisible({ timeout: 15000 });
    await item.click();
  }
  await page.waitForSelector('[data-testid="feature-chat-subtitle"]', { timeout: 30000 });
  await page.waitForTimeout(2500); // оседание ленты
  // убедиться, что в центре именно целевой чат: имя его персоны стоит в шапке (верх страницы)
  const headerHasName = await page.evaluate((name) => {
    const walk = (el: Element): boolean => {
      for (const c of el.children) {
        const r = c.getBoundingClientRect();
        if (r.top < 120 && r.width > 20 && [...c.childNodes].some(n => n.nodeType === 3 && (n.textContent || '').includes(name))) return true;
        if (r.top < 120 && walk(c)) return true;
      }
      return false;
    };
    return walk(document.body);
  }, PERSONA_NAME);
  expect(headerHasName, `в центре чат с персоной ${PERSONA_NAME} (имя в шапке)`).toBe(true);
}

const isOpen = (page: Page) => page.evaluate(() => !!document.querySelector('[data-testid="feature-popover"]'));

test('D1 ef2f1dc3 · 1280: клик по строке фичи открывает поповер; переход к персоне — по имени', async ({ page }) => {
  const res: Record<string, unknown> = {};
  await openChat(page, 1280, 900);
  const sub = page.getByTestId('feature-chat-subtitle');
  await expect(sub, 'строка фичи в шапке есть').toBeVisible();

  await sub.click();
  await page.waitForTimeout(600);
  res.popoverAfterRowClick = await isOpen(page);
  res.urlAfterRowClick = page.url();
  await page.screenshot({ path: `${OUT}q2-d1-popover.png` });

  await page.keyboard.press('Escape');
  await page.waitForTimeout(400);
  res.popoverAfterEsc1280 = await isOpen(page);

  expect(res.popoverAfterRowClick, 'клик по строке открыл поповер').toBe(true);
  expect(String(res.urlAfterRowClick), 'клик по строке не ведёт на карточку персоны').not.toContain('/persona/');
  expect(res.popoverAfterEsc1280, 'на 1280 Escape закрывает поповер').toBe(false);

  // переход к персоне — по клику на имя в заголовке (имя персоны — заголовок чата с персоной)
  const nameCandidates = await page.evaluate((name) => {
    const cands: Array<{ x: number; y: number; w: number; h: number; tag: string; testid: string; text: string }> = [];
    const walk = (el: Element) => {
      for (const c of el.children) {
        const own = [...c.childNodes].some(n => n.nodeType === 3 && (n.textContent || '').includes(name));
        const r = c.getBoundingClientRect();
        if (r.top < 120 && r.width > 20 && r.height > 8) {
          if (own) cands.push({ x: r.x, y: r.y, w: r.width, h: r.height, tag: c.tagName, testid: c.getAttribute('data-testid') || '', text: (c.textContent || '').slice(0, 40) });
          walk(c);
        }
      }
    };
    walk(document.body);
    // самый «жирный» кандидат по площади — сам блок имени
    cands.sort((a, b) => b.w * b.h - a.w * a.h);
    return cands.slice(0, 5);
  }, PERSONA_NAME);
  res.nameCandidates = nameCandidates;
  res.urlAfterNameClick = page.url();
  res.consoleErrors = errors(page);
  writeFileSync(`${OUT}/result-d1.json`, JSON.stringify(res, null, 1));

  expect(nameCandidates.length, 'в шапке найдено имя персоны').toBeGreaterThan(0);
  const hit = nameCandidates[0];
  await page.mouse.click(hit.x + hit.w / 2, hit.y + hit.h / 2);
  await page.waitForTimeout(1000);
  res.urlAfterNameClick = page.url();
  await page.screenshot({ path: `${OUT}q2-d1-persona.png` });
  writeFileSync(`${OUT}/result-d1.json`, JSON.stringify(res, null, 1));
  expect(String(res.urlAfterNameClick), 'клик по имени ведёт на карточку персоны').toContain(`/persona/${PERSONA_ID}`);
});

test('D2 4100c6d8 · 360: Escape закрывает шторку; Enter/Space по строке не открывают переключатель чатов', async ({ page }) => {
  const res: Record<string, unknown> = {};
  await openChat(page, 360, 740);
  const sub = page.getByTestId('feature-chat-subtitle');

  await sub.click();
  await page.waitForTimeout(600);
  res.openedAfterClick = await isOpen(page);
  await page.screenshot({ path: `${OUT}q2-d2-open.png` });

  await page.keyboard.press('Escape');
  await page.waitForTimeout(600);
  res.openAfterEscape1 = await isOpen(page);
  await page.screenshot({ path: `${OUT}q2-d2-esc.png` });
  // второй раз, как в исходных шагах («проверено три раза подряд»)
  await sub.click(); await page.waitForTimeout(400);
  await page.keyboard.press('Escape'); await page.waitForTimeout(400);
  res.openAfterEscape2 = await isOpen(page);

  expect(res.openedAfterClick, 'клик по строке открыл шторку').toBe(true);
  expect(res.openAfterEscape1, 'Escape закрыл шторку').toBe(false);
  expect(res.openAfterEscape2, 'Escape закрыл шторку повторно').toBe(false);

  // Enter/Space по сфокусированной строке: переключатель чатов открыться не должен
  const probe = async (key: string) => {
    await page.evaluate(() => {
      const el = document.querySelector('[data-testid="feature-chat-subtitle"]') as HTMLElement | null;
      el?.focus?.();
    });
    await page.keyboard.press(key);
    await page.waitForTimeout(600);
    const expanded = await page.evaluate(() => {
      const t = document.querySelector('[aria-label^="Переключить чат"]');
      const dlg = [...document.querySelectorAll('[role="dialog"]')].find(d => !d.querySelector('[data-testid="feature-popover"]'));
      return {
        switcherExpanded: t ? t.getAttribute('aria-expanded') : null,
        foreignDialog: dlg ? (dlg.getAttribute('aria-label') || dlg.className || '').slice(0, 80) : null,
        featureSheet: !!document.querySelector('[data-testid="feature-popover"]'),
      };
    });
    // закрыть, если что-то открылось
    await page.keyboard.press('Escape'); await page.waitForTimeout(300);
    return expanded;
  };
  res.enter = await probe('Enter');
  res.space = await probe('Space');
  await page.screenshot({ path: `${OUT}q2-d2-keys.png` });
  res.consoleErrors = errors(page);
  writeFileSync(`${OUT}/result-d2.json`, JSON.stringify(res, null, 1));

  expect((res.enter as { switcherExpanded: string | null }).switcherExpanded !== 'true', 'Enter не открыл переключатель чатов').toBe(true);
  expect((res.space as { switcherExpanded: string | null }).switcherExpanded !== 'true', 'Space не открыл переключатель чатов').toBe(true);
});

test('D3 b9e6460d · 360: вся зона цели строки кликабельна; под курсором нет ни кромки-трекера, ни ленты; тулбар ≤ 58', async ({ page }) => {
  const res: Record<string, unknown> = {};
  await openChat(page, 360, 740);
  const box = await page.getByTestId('feature-chat-subtitle').boundingBox();
  expect(box, 'зона цели измерена').not.toBeNull();
  res.zone = box;

  // что под курсором в каждой точке зоны (до клика) и открывается ли шторка после клика
  const rows: Array<{ y: number; under: string; opened: boolean }> = [];
  for (let y = Math.ceil(box!.y); y <= Math.floor(box!.y + box!.height); y += 2) {
    if (await isOpen(page)) { await page.keyboard.press('Escape'); await page.waitForTimeout(250); }
    const x = box!.x + box!.width / 2;
    const under = await page.evaluate(([x, y]) => {
      const e = document.elementFromPoint(x, y);
      if (!e) return 'null';
      const testid = e.getAttribute('data-testid') || '';
      const cls = (e.className || '').toString().slice(0, 40);
      return `${e.tagName}/${testid || cls}`;
    }, [x, y]);
    await page.mouse.click(x, y);
    await page.waitForTimeout(350);
    const opened = await isOpen(page);
    rows.push({ y, under, opened });
  }
  res.probe = rows;
  const badUnder = rows.filter(r => /chat-feature-edge|feature-bar/.test(r.under) || r.under === 'null');
  const notOpened = rows.filter(r => !r.opened);
  res.badUnder = badUnder;
  res.notOpened = notOpened;

  // геометрия шапки: контейнер-тулбар (предок строки, во всю ширину), имя чата/персоны, начало ленты
  const geo = await page.evaluate(([zoneBottom, name]) => {
    const sub = document.querySelector('[data-testid="feature-chat-subtitle"]')!;
    let bar: Element | null = null;
    for (let p: Element | null = sub; p; p = p.parentElement) {
      const r = p.getBoundingClientRect();
      if (r.width >= window.innerWidth - 1 && r.top <= 2) { bar = p; break; }
    }
    const barR = bar?.getBoundingClientRect();
    // первая «шапка» после тулбара: скроллируемый контейнер ленты
    let feed: Element | null = null;
    for (const el of document.querySelectorAll('*')) {
      const cs = getComputedStyle(el);
      const r = el.getBoundingClientRect();
      if ((cs.overflowY === 'auto' || cs.overflowY === 'scroll') && r.height > 250 && r.top > 20) { feed = el; break; }
    }
    const feedR = feed?.getBoundingClientRect();
    // имя чата/персоны в шапке
    const nameRects: DOMRect[] = [];
    const walk = (el: Element) => {
      for (const c of el.children) {
        const own = [...c.childNodes].some(n => n.nodeType === 3 && (n.textContent || '').includes(name as string));
        const r = c.getBoundingClientRect();
        if (own && r.top < 160) nameRects.push(r);
        walk(c);
      }
    };
    walk(document.body);
    const nameFound = nameRects[0] ?? null;
    return {
      bar: barR ? { tag: bar!.tagName, testid: bar!.getAttribute('data-testid') || '', y: barR.y, h: barR.height, bottom: barR.bottom } : null,
      feedTop: feedR ? { top: feedR.top } : null,
      name: nameFound ? { y: nameFound.y, bottom: nameFound.bottom } : null,
      zoneBottom,
    };
  }, [box!.y + box!.height, PERSONA_NAME]);
  res.geo = geo;
  await page.screenshot({ path: `${OUT}q2-d3-header.png`, clip: { x: 0, y: 0, width: 360, height: 240 } });
  await page.screenshot({ path: `${OUT}q2-d3-full.png` });

  // риск из ревью: если зона заходит на нижнюю часть имени — клик по этой полосе
  const nameOverlap = geo.name ? Math.min(geo.name.bottom, box!.y + box!.height) - box!.y : 0;
  res.overlapWith = {
    nameOverlapPx: nameOverlap > 0 ? nameOverlap : 0,
    feedOverlapPx: geo.feedTop ? Math.max(0, box!.y + box!.height - geo.feedTop.top) : null,
  };
  if (nameOverlap > 0 && geo.name) {
    const y = (Math.max(box!.y, geo.name.y) + Math.min(box!.y + box!.height, geo.name.bottom)) / 2;
    if (await isOpen(page)) { await page.keyboard.press('Escape'); await page.waitForTimeout(250); }
    await page.mouse.click(180, y);
    await page.waitForTimeout(500);
    res.clickOnNameOverlap = {
      y,
      featureSheet: await isOpen(page),
      switcherExpanded: await page.evaluate(() => {
        const t = document.querySelector('[aria-label^="Переключить чат"]');
        return t ? t.getAttribute('aria-expanded') : null;
      }),
    };
    await page.screenshot({ path: `${OUT}q2-d3-nameclick.png` });
    await page.keyboard.press('Escape');
  }
  res.consoleErrors = errors(page);
  writeFileSync(`${OUT}/result-d3.json`, JSON.stringify(res, null, 1));

  expect(badUnder.length, `под курсором нигде нет кромки-трекера: ${JSON.stringify(badUnder)}`).toBe(0);
  expect(notOpened.length, `шторка открывается по всей зоне; провалы: ${JSON.stringify(notOpened)}`).toBe(0);
  if (geo.bar) expect(geo.bar.h, `высота тулбара ≤ 58: ${JSON.stringify(geo.bar)}`).toBeLessThanOrEqual(58);
});

// числа страницы — по названным блокам (герой, строка анализа, полоса, «Объём», «Главные
// потери») плюс страховой осмотр видимых блоков. Границы слов — только внутри блока: склейка
// соседних блоков врёт («25 | ВОЗВРАТЫ И ПОТЕРИ», «Слияний 4 | Доработок после мержа 25») —
// обе артефакт-пары доказаны прогонами. Скрытые поддеревья (шапка чата за оверлеем фичи)
// не сканируем: в ленте моего чата лежит текст этой задачи со своими числами.
const collectInPage = () => {
  const visible = (e: Element) => (e as HTMLElement).offsetParent !== null || e.tagName === 'BODY';
  const scan = (re: RegExp) => {
    const out = new Set<string>();
    const blocks: string[] = [];
    document.querySelectorAll('*').forEach(e => {
      if (!visible(e)) return;
      const t = (e as HTMLElement).innerText || '';
      if (!re.test(t)) return;
      if ([...e.children].some(c => re.test((c as HTMLElement).innerText || ''))) return;
      for (const m of t.matchAll(re)) { const n = m[1] ?? m[2]; if (n) out.add(n); }
      blocks.push(t.replace(/\n+/g, ' | ').slice(0, 160));
    });
    return { numbers: [...out], blocks };
  };
  return {
    returns: scan(/(\d+)\s*(?:возврат(?:ы|а|ов))|(?:Возвращены|возвращено)\s*(\d+)/gi),
    followups: scan(/(?:Доработок после мержа)\s*(\d+)|(\d+)\s*доработок после мержа/gi),
    loops: scan(/(\d+)\s*круг(?:а|ов)?/gi),
  };
};

const FEATURE_TITLE: Record<string, string> = {
  [F_MERGED]: 'LLM-wiki: новая постановка ночной вики',
  [F_EPIC]: 'Фичи: упрощение и прозрачность раздела',
};

async function readFeaturePage(page: Page, featureId: string, label: string) {
  const res: Record<string, unknown> = { featureId };
  await page.addInitScript(t => localStorage.setItem('cc_token', t as string), token);
  await page.setViewportSize({ width: 1280, height: 900 });
  await page.goto(`/#/project/${PID}/feature/${featureId}`);
  // смена hash без перезагрузки не перематчивает SPA (проверено: вторая страница вернула DOM
  // первой) — перезагружаем приложение на адресе целевой фичи
  await page.reload();
  await page.waitForSelector('[role="tab"]', { timeout: 25000 });
  await page.waitForTimeout(3500);
  await expect(page.locator('body'), `открыта страница фичи «${FEATURE_TITLE[featureId]}»`).toContainText(FEATURE_TITLE[featureId]);
  const readAll = async () => page.evaluate(() => {
    const pick = (sel: string) => {
      const e = document.querySelector(sel);
      return e ? (e as HTMLElement).innerText.replace(/\n+/g, ' | ') : null;
    };
    return {
      hero: pick('[data-testid="closed-summary"]'),
      heroTiles: [...document.querySelectorAll('[data-testid^="closed-tile-"]')].map(t => (t as HTMLElement).innerText.replace(/\n+/g, ' ')),
      analysisLine: pick('[data-testid="feature-analysis-line"]'),
      outcomeBar: pick('[data-testid="returns-outcome"]'),
      losses: pick('[data-testid="stats-losses"]'),
      lossBigs: [...document.querySelectorAll('[data-testid^="loss-big-"]')].map(t => (t as HTMLElement).innerText.replace(/\n+/g, ' ')),
      volume: pick('[data-testid="review-volume"]'),
      tabs: [...document.querySelectorAll('[role="tab"]')].map(t => (t as HTMLElement).innerText.trim()),
      bodyText: document.body.innerText,
    };
  });
  const first = await readAll();
  res.firstScreen = { ...first, bodyText: undefined };
  res.numbersFirst = await page.evaluate(collectInPage);
  await page.screenshot({ path: `${OUT}q2-d4-${label}-first.png` });

  const tab = page.getByRole('tab', { name: /Разбор/ });
  if (await tab.count()) {
    await tab.first().click();
    await page.waitForTimeout(3000);
    const rev = await readAll();
    res.reviewTab = { ...rev, bodyText: undefined };
    res.numbersReview = await page.evaluate(collectInPage);
    await page.screenshot({ path: `${OUT}q2-d4-${label}-review.png`, fullPage: true });
  }
  const pulse = page.getByRole('tab', { name: /Пульс|Статистик/ });
  res.pulseTabExists = await pulse.count();
  if (res.pulseTabExists) {
    await pulse.first().click();
    await page.waitForTimeout(2500);
    const st = await readAll();
    res.statsTab = { ...st, bodyText: undefined };
    res.numbersStats = await page.evaluate(collectInPage);
    await page.screenshot({ path: `${OUT}q2-d4-${label}-stats.png`, fullPage: true });
  }
  res.consoleErrors = errors(page);
  return res;
}

type Num = { returns: { numbers: string[]; blocks: string[] }; followups: { numbers: string[] }; loops: { numbers: string[]; blocks: string[] } };

// structured parse of the blocks named by the task — no cross-block text joins
const grab = (text: string | null | undefined, re: RegExp) => (text ?? '').match(re)?.[1] ?? null;

function checkFeature(res: Record<string, unknown>) {
  const first = (res.firstScreen ?? {}) as Record<string, string | null | undefined | string[]>;
  const rev = (res.reviewTab ?? {}) as Record<string, string | null | undefined>;
  const tiles = (first.heroTiles as string[] | undefined) ?? [];
  const heroReturns = tiles.find(t => /возврат/i.test(t))?.match(/(\d+)/)?.[1] ?? null;
  const heroFollow = tiles.find(t => /доработок после мержа/i.test(t))?.match(/(\d+)/)?.[1] ?? null;
  const lineText = (rev.analysisLine ?? first.analysisLine ?? null) as string | null;
  const bar = rev.outcomeBar ?? null;
  const parsed = {
    heroReturns,
    heroFollow,
    lineReturns: grab(lineText, /(\d+)\s*возврат(?:ы|а|ов)/i),
    barReturns: grab(bar, /Возвращены[\s|]*(\d+)/i),
    barRemarks: grab(bar, /С замечаниями[\s|]*(\d+)/i),
    barFirstTime: grab(bar, /(\d+)\s*%/),
    volFollow: grab(rev.volume, /Доработок после мержа[\s|]*(\d+)/i),
  };
  const scanReturns = new Set<string>();
  for (const k of ['numbersFirst', 'numbersReview', 'numbersStats'] as const) {
    ((res[k] as Num | undefined)?.returns.numbers ?? []).forEach(n => scanReturns.add(n));
  }
  return { parsed, scanReturns: [...scanReturns] };
}

test('D4 e128aac4: «возвраты» — одно число на странице; «круги» без слова «возвраты»; герой = «Объём»', async ({ page }) => {
  const merged = await readFeaturePage(page, F_MERGED, 'merged');
  const mc = checkFeature(merged);
  merged.check = mc;
  writeFileSync(`${OUT}/result-d4-merged.json`, JSON.stringify(merged, null, 1));

  const { parsed: m } = mc;
  // сущность дефекта: герой, строка анализа и полоса называют одно число возвратов
  expect(m.heroReturns, 'на странице смерженной фичи есть герой с числом возвратов').toBeTruthy();
  expect(m.lineReturns, `строка анализа называет те же возвраты, что герой (${JSON.stringify(m)})`).toBe(m.heroReturns);
  expect(m.barReturns, `полоса «Разбора» называет те же возвраты, что герой (${JSON.stringify(m)})`).toBe(m.heroReturns);
  // страховой осмотр: видимые вхождения слова «возврат» на странице — только значение полосы
  expect(mc.scanReturns.filter(n => n !== m.barReturns), `чужих чисел у слова «возвраты» нет: ${JSON.stringify(mc.scanReturns)}`).toEqual([]);

  // «Главные потери»: формат «N кругов», слова «возвраты» там нет
  const lossesText = String((merged.reviewTab as { losses?: string | null })?.losses ?? (merged.statsTab as { losses?: string | null })?.losses ?? '');
  expect(lossesText, 'блок «Главные потери» найден').not.toBe('');
  expect(/круг(а|ов)/.test(lossesText), '«Главные потери» говорят «круги»').toBe(true);
  expect(/возврат/i.test(lossesText), 'в «Главных потерях» нет слова «возвраты»').toBe(false);

  // «доработки после мержа»: герой и «Объём» — одно число
  expect(m.volFollow, '«Объём» называет доработки после мержа').toBeTruthy();
  expect(m.volFollow, `герой и «Объём» согласуются (${JSON.stringify(m)})`).toBe(m.heroFollow);

  const epic = await readFeaturePage(page, F_EPIC, 'epic');
  const ec = checkFeature(epic);
  epic.check = ec;
  writeFileSync(`${OUT}/result-d4-epic.json`, JSON.stringify(epic, null, 1));
  // эпика сейчас идёт (мой QA-шаг) — героя смерженной фичи у неё нет; сверяем строку анализа и полосу
  const e = ec.parsed;
  if (e.heroReturns) {
    expect(e.barReturns, `на странице эпика герой и полоса согласуются (${JSON.stringify(e)})`).toBe(e.heroReturns);
  }
  expect(e.lineReturns ?? e.barReturns, `на странице эпика число возвратов названо (${JSON.stringify(e)})`).toBeTruthy();
  if (e.lineReturns && e.barReturns) expect(e.lineReturns, `строка анализа и полоса эпика согласуются (${JSON.stringify(e)})`).toBe(e.barReturns);
  expect(ec.scanReturns.filter(n => n !== (e.barReturns ?? e.lineReturns)), `на странице эпика «возвраты» тоже одно число: ${JSON.stringify(ec.scanReturns)}`).toEqual([]);
});
