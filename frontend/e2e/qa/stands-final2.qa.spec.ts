import { test, expect, type Page } from '@playwright/test';
import { login } from './helpers';

// Финал-2 · повторная приёмка фичи «Доработка вкладки „Стенды“: иерархия показателей и
// компактный ИБП» (задача 3bbfce03, роль final) после доработки ST6 (54b12763) и ревью
// дельты (19a5381c). Ветка feat/stands-metric-hierarchy, коммиты 1455b2a5c + d8a203451.
//
// Проверяем только пять пунктов постановки: усечения (дефекты 13afda65, 9f94bf4c),
// график под KPI «Загрузка CPU», подпись про процессоры, нейтральность загрузки.
//
// Стенд: ccs-qa-stands-f2b (юнит) на :5562 из worktree этой ветки; фронт — frontend/dist
// этого worktree. Узлы: worker (реальный хост, два k10temp → «2 сокета»), rig/rig-hot/quiet
// — фейковые агенты (/tmp/qa-stands/fake_farm.py), zero — новый фикстурный узел с нулевой
// загрузкой (/tmp/qa-stands/zero_node.py, :19104), dead — не отвечает.

const SHOTS = '/home/an/Sources/ClaudeCodeServer-stands-hierarchy/.cc-attachments/qa-stands-final2';

let token: string;
let projectId: string;

test.describe.configure({ mode: 'serial' });

test.beforeAll(async ({ playwright, baseURL }) => {
  const request = await playwright.request.newContext({ baseURL });
  token = await login(request);
  const p = await request.post('/api/projects', {
    headers: { Authorization: `Bearer ${token}` },
    data: { name: 'Стенды-финал2-QA', rootPath: '/tmp/qa-stands/proj-stands-final2', createDirectory: true },
  });
  expect(p.ok(), `создание проекта → ${p.status()}`).toBeTruthy();
  projectId = (await p.json()).id as string;
  await request.dispose();
});

test.afterAll(async ({ playwright, baseURL }) => {
  const request = await playwright.request.newContext({ baseURL });
  const del = await request.delete(`/api/projects/${projectId}`, { headers: { Authorization: `Bearer ${token}` } });
  expect([200, 204, 404]).toContain(del.status());
  await request.dispose();
});

async function openPanel(page: Page, opts: { zones?: boolean } = {}) {
  await page.addInitScript(({ tk, zones }: { tk: string; zones: boolean }) => {
    localStorage.setItem('cc_token', tk);
    localStorage.setItem('cc_role', 'admin');
    localStorage.setItem('cc_username', 'admin');
    localStorage.setItem('cc_local_model_tab', 'hardware');
    if (zones) {
      localStorage.setItem('cc_ws_zones', JSON.stringify({
        left: { layout: [['chats']], stash: [] },
        right: { layout: [['localModel']], stash: [] },
      }));
    }
  }, { tk: token, zones: opts.zones ?? true });
}

async function go(page: Page, hash: string) {
  await page.goto(`/?r=${Date.now()}${hash}`);
}

async function openStandsTab(page: Page) {
  if (await page.locator('section[aria-labelledby="stand-card-title"]').count()) return;
  const clicked = await page.evaluate(() => {
    const btns = Array.from(document.querySelectorAll('button')) as HTMLElement[];
    const b = btns.find(x => (x.getAttribute('aria-label') ?? '').trim() === 'Стенды');
    if (!b) return btns.map(x => x.getAttribute('aria-label'));
    b.click();
    return true;
  });
  expect(clicked, 'кнопка вкладки «Стенды» найдена').toBe(true);
  await page.waitForTimeout(2500);
  await page.locator('section[aria-labelledby="stand-card-title"]').waitFor({ timeout: 30000 });
}

function panel(page: Page) {
  return page.locator('[role="complementary"][aria-label="Ферма"]');
}

async function selectNode(page: Page, title: string) {
  const node = page.locator(`[aria-label^="Узел ${title}:"]`).first();
  await node.waitFor({ state: 'visible', timeout: 20000 });
  await node.evaluate((el: HTMLElement) => el.click());
  await page.waitForTimeout(3000);
}

// Открыть «Ферму» на мобильной ширине: меню «Ещё» → пункт «Ферма» (шторка).
async function openFarmMobile(page: Page) {
  const more = page.locator('button[aria-label^="Ещё"], button[title^="Ещё"]').first();
  await more.waitFor({ timeout: 30000 });
  await more.evaluate((el: HTMLElement) => el.click());
  const item = page.getByText('Ферма', { exact: true }).last();
  await item.waitFor({ timeout: 20000 });
  await item.evaluate((el: HTMLElement) => el.click());
  await panel(page).waitFor({ timeout: 30000 });
  await page.waitForTimeout(2500);
}

// Плитка KPI «Загрузка CPU» ищется в каждом evaluate сама (span подписи → div строки →
// div плитки): Playwright не сериализует функции-аргументы, а замыкания из модуля в
// страницу не доезжают.

test('1: KPI «Загрузка CPU» — живой график C.info с подсказкой, нулевая загрузка «0 %», «2 сокета», нейтральность', async ({ page }) => {
  const errors: string[] = [];
  page.on('console', m => { if (m.type() === 'error') errors.push(m.text()); });
  page.on('pageerror', e => errors.push(String(e)));

  await page.setViewportSize({ width: 1280, height: 900 });
  await openPanel(page);
  await go(page, `/#/project/${projectId}`);
  await panel(page).waitFor({ timeout: 60000 });
  await openStandsTab(page);

  // Пункт 3 (живая часть): график под значением на узле с историей.
  await selectNode(page, 'Стойка GPU');
  const spark = page.locator('svg[aria-label*="пики за 10 с"]').first();
  await spark.waitFor({ state: 'visible', timeout: 30000 });
  const sparkInfo = await page.evaluate(() => {
    const tile = (() => {
    const p = document.querySelector('[role="complementary"][aria-label="Ферма"]');
    if (!p) return null;
    const span = Array.from(p.querySelectorAll('span')).find(s => (s.textContent ?? '').trim() === 'Загрузка CPU');
    const row = span?.closest('div');
    return row?.parentElement ?? null;
  })();
    if (!tile) return null;
    const svg = tile.querySelector('svg[aria-label*="пики за 10 с"]');
    const wrap = tile.querySelector('div[title*="Пики за 10 с"]');
    const polylines = Array.from(svg?.querySelectorAll('polyline') ?? [])
      .map(p => p.getAttribute('stroke') ?? '');
    const value = Array.from(tile.querySelectorAll('div')).find(d => {
      const st = getComputedStyle(d);
      return st.fontFamily.includes('mono') && /^\d+ ?%$/.test((d.textContent ?? '').replace(/\s+/g, ' ').trim());
    }) as HTMLElement | undefined;
    return {
      hasSvg: !!svg,
      ariaLabel: svg?.getAttribute('aria-label') ?? '',
      title: wrap?.getAttribute('title') ?? '',
      polylines,
      valueText: (value?.textContent ?? '').replace(/\s+/g, ' ').trim(),
      valueSize: value ? parseFloat(getComputedStyle(value).fontSize) : 0,
      detail: Array.from(tile.querySelectorAll('div')).map(d => (d.textContent ?? '').trim())
        .find(t => t.includes('все ядра')) ?? '',
    };
  });
  expect(sparkInfo, 'плитка KPI «Загрузка CPU» найдена').not.toBeNull();
  expect(sparkInfo!.hasSvg, 'под значением загрузки есть график истории').toBe(true);
  expect(sparkInfo!.ariaLabel, `aria-label графика: ${sparkInfo!.ariaLabel}`).toContain('пики за 10 с');
  expect(sparkInfo!.title, `подпись-подсказка: ${sparkInfo!.title}`).toContain('Пики за 10 с');
  expect(sparkInfo!.polylines.length, 'ряд рисуется линией').toBeGreaterThan(0);
  for (const stroke of sparkInfo!.polylines) expect(stroke, `цвет линии C.info: ${stroke}`).toContain('var(--c-info)');
  expect(sparkInfo!.valueText, `значение загрузки: ${sparkInfo!.valueText}`).toMatch(/^\d+ %$/);
  expect(sparkInfo!.valueSize, 'значение KPI — крупное (28 px)').toBe(28);
  // Пункт 4 (один сокет): деталь «все ядра» без числа сокетов.
  expect(sparkInfo!.detail, `деталь на одно-сокетном узле: ${sparkInfo!.detail}`).toBe('все ядра');
  await page.screenshot({ path: `${SHOTS}/01-cpu-spark-rig.png` });

  // Пункт 3 (ноль): значение 0 — «0 %», а не «нет данных».
  await selectNode(page, 'Нулевой узел');
  const zero = await page.evaluate(() => {
    const tile = (() => {
    const p = document.querySelector('[role="complementary"][aria-label="Ферма"]');
    if (!p) return null;
    const span = Array.from(p.querySelectorAll('span')).find(s => (s.textContent ?? '').trim() === 'Загрузка CPU');
    const row = span?.closest('div');
    return row?.parentElement ?? null;
  })();
    if (!tile) return null;
    const texts = Array.from(tile.querySelectorAll('*')).map(e => (e.textContent ?? '').trim());
    const value = Array.from(tile.querySelectorAll('div')).find(d => {
      const st = getComputedStyle(d);
      return st.fontFamily.includes('mono') && /^\d+ ?%$/.test((d.textContent ?? '').replace(/\s+/g, ' ').trim());
    }) as HTMLElement | undefined;
    return {
      hasNoData: texts.includes('нет данных'),
      valueText: (value?.textContent ?? '').replace(/\s+/g, ' ').trim(),
      valueColor: value ? getComputedStyle(value).color : '',
      tileBg: getComputedStyle(tile).backgroundColor,
      dots: Array.from(tile.querySelectorAll('span')).filter(s => {
        const r = s.getBoundingClientRect();
        return r.width <= 10 && r.height <= 10 && getComputedStyle(s).borderRadius.includes('9999') ||
          (r.width <= 10 && r.height <= 10 && getComputedStyle(s).borderRadius !== '0px' && s.getAttribute('aria-hidden') === 'true');
      }).length,
    };
  });
  expect(zero, 'плитка на нулевом узле найдена').not.toBeNull();
  expect(zero!.hasNoData, 'нулевая загрузка не «нет данных»').toBe(false);
  expect(zero!.valueText, `значение нулевой загрузки: ${zero!.valueText}`).toBe('0 %');
  // Пункт 5: нейтральность — нет тревожной заливки плитки, нет цветной точки, значение не окрашено.
  const tones = await page.evaluate((vars: string[]) => {
    const out: Record<string, { color: string; bg: string }> = {};
    for (const v of vars) {
      const el = document.createElement('span');
      el.style.color = `var(${v})`;
      el.style.background = `var(${v})`;
      document.body.appendChild(el);
      const cs = getComputedStyle(el);
      out[v] = { color: cs.color, bg: cs.backgroundColor };
      el.remove();
    }
    return out;
  }, ['--c-warning-bg', '--c-danger-bg', '--c-success-bg', '--c-success', '--c-warning-text', '--c-danger-text', '--c-bg-card', '--c-border-light']);
  expect(zero!.tileBg, `фон плитки — C.bgCard, не тревожная заливка: ${zero!.tileBg}`).toBe(tones['--c-bg-card'].bg);
  expect([tones['--c-warning-bg'].bg, tones['--c-danger-bg'].bg, tones['--c-success-bg'].bg],
    `фон плитки не warning/danger/success: ${zero!.tileBg}`).not.toContain(zero!.tileBg);
  expect(zero!.dots, `в KPI загрузки нет точки-светофора: ${zero!.dots}`).toBe(0);
  expect([tones['--c-warning-text'].color, tones['--c-danger-text'].color],
    `значение загрузки не окрашено тревожно: ${zero!.valueColor}`).not.toContain(zero!.valueColor);
  await page.screenshot({ path: `${SHOTS}/02-zero-node-neutral.png` });

  // Пункт 4 (два сокета): реальный хост с двумя k10temp — «все ядра · 2 сокета», отдельных строк про процессоры нет.
  await selectNode(page, 'worker (этот хост)');
  const two = await page.evaluate(() => {
    const tile = (() => {
    const p = document.querySelector('[role="complementary"][aria-label="Ферма"]');
    if (!p) return null;
    const span = Array.from(p.querySelectorAll('span')).find(s => (s.textContent ?? '').trim() === 'Загрузка CPU');
    const row = span?.closest('div');
    return row?.parentElement ?? null;
  })();
    const detail = tile ? Array.from(tile.querySelectorAll('div')).map(d => (d.textContent ?? '').trim())
      .find(t => t.includes('все ядра')) ?? '' : '';
    const p = document.querySelector('[role="complementary"][aria-label="Ферма"]');
    const text = (p?.textContent ?? '').replace(/\s+/g, ' ');
    return {
      detail,
      perCpuLines: (text.match(/(CPU|Процессор) ?\d+ · ?\d+ ?%/g) ?? []),
      cpuTiles: Array.from(p?.querySelectorAll('span') ?? []).map(s => (s.textContent ?? '').trim())
        .filter(t => /^Процессор \d+$/.test(t)),
    };
  });
  expect(two.detail, `деталь на двух-сокетном узле: ${two.detail}`).toBe('все ядра · 2 сокета');
  expect(two.perCpuLines, `отдельных строк «CPU N · M %» нет: ${JSON.stringify(two.perCpuLines)}`).toEqual([]);
  // Плитки «Процессор 1/2» — температуры (это уровень 2, не строки загрузки).
  expect(two.cpuTiles.length, 'плитки температур по сокетам на месте').toBeGreaterThanOrEqual(2);
  await page.screenshot({ path: `${SHOTS}/03-two-sockets.png` });

  const real = errors.filter(e => !/ERR_NETWORK_CHANGED|Failed to load resource/.test(e));
  expect(real, 'в консоли нет ошибок').toEqual([]);
});

test('2: пустой ряд истории — выдуманной линии нет (mock: cpuLoadSpark15m = [])', async ({ page }) => {
  // Счётчик доказывает, что проверка идёт на действительно ПУСТОМ ряду с сервера.
  let mocked = 0;
  await page.route('**/api/local-model/hardware**', async route => {
    const res = await route.fetch();
    let body: any = null;
    try { body = await res.json(); } catch { /* не JSON — не трогать */ }
    if (body && typeof body === 'object' && 'cpuLoadSpark15m' in body) {
      body.cpuLoadSpark15m = [];
      mocked++;
      await route.fulfill({ response: res, body: JSON.stringify(body) });
      return;
    }
    await route.fulfill({ response: res });
  });
  await page.setViewportSize({ width: 1280, height: 900 });
  await openPanel(page);
  await go(page, `/#/project/${projectId}`);
  await panel(page).waitFor({ timeout: 60000 });
  await openStandsTab(page);
  await selectNode(page, 'Стойка GPU');

  const state = await page.evaluate(() => {
    const tile = (() => {
    const p = document.querySelector('[role="complementary"][aria-label="Ферма"]');
    if (!p) return null;
    const span = Array.from(p.querySelectorAll('span')).find(s => (s.textContent ?? '').trim() === 'Загрузка CPU');
    const row = span?.closest('div');
    return row?.parentElement ?? null;
  })();
    if (!tile) return null;
    return {
      // Иконка Cpu — тоже svg: считаем только спарклайн (Sparkline рендерит svg.spark).
      sparks: tile.querySelectorAll('svg.spark').length,
      valueText: Array.from(tile.querySelectorAll('div')).filter(d => {
        const st = getComputedStyle(d);
        return st.fontFamily.includes('mono') && /^\d+ ?%$/.test((d.textContent ?? '').replace(/\s+/g, ' ').trim());
      }).map(d => (d.textContent ?? '').replace(/\s+/g, ' ').trim())[0] ?? '',
    };
  });
  expect(state, 'плитка KPI «Загрузка CPU» найдена').not.toBeNull();
  expect(mocked, 'ответы сервера реально прошли через мок с пустым рядом').toBeGreaterThan(0);
  expect(state!.sparks, 'пустой ряд не рисуется выдуманной линией').toBe(0);
  expect(state!.valueText, `значение при этом остаётся: ${state!.valueText}`).toMatch(/^\d+ %$/);
  await page.screenshot({ path: `${SHOTS}/04-empty-series-no-line.png` });
});

test('3: ширины 320 и 360 — «Загрузка CPU» и «Температуры» читаются целиком, окна на своей строке', async ({ page }) => {
  for (const width of [320, 360]) {
    await page.setViewportSize({ width, height: 780 });
    await openPanel(page, { zones: false });
    await go(page, `/#/project/${projectId}`);
    await page.waitForTimeout(5000);
    await openFarmMobile(page);
    await openStandsTab(page);
    await selectNode(page, 'Стойка GPU');

    // Горизонтальной прокрутки нет.
    const overflow = await page.evaluate(() => ({
      doc: document.documentElement.scrollWidth - document.documentElement.clientWidth,
      panel: (() => {
        const p = document.querySelector('[role="complementary"][aria-label="Ферма"]');
        return p ? p.scrollWidth - p.clientWidth : -1;
      })(),
    }));
    expect(overflow.doc, `на ${width} нет горизонтальной прокрутки`).toBeLessThanOrEqual(1);
    expect(overflow.panel, `на ${width} панель без горизонтальной прокрутки`).toBeLessThanOrEqual(1);

    // Усечение с многоточием допустимо только у подписи «Оперативная память».
    const ellipsis = await page.evaluate(() => {
      const p = document.querySelector('[role="complementary"][aria-label="Ферма"]');
      if (!p) return [];
      const out: string[] = [];
      for (const el of Array.from(p.querySelectorAll('*'))) {
        const e = el as HTMLElement;
        if (e.children.length > 0) continue;
        if (getComputedStyle(e).textOverflow !== 'ellipsis') continue;
        if (e.scrollWidth <= e.clientWidth + 1) continue;
        out.push((e.textContent ?? '').trim());
      }
      return out;
    });
    console.log(`ELLIPSIS_${width}=` + JSON.stringify(ellipsis));
    expect(ellipsis.filter(t => t !== 'Оперативная память'),
      `на ${width} усечение осталось только у «Оперативная память»: ${JSON.stringify(ellipsis)}`).toEqual([]);

    // Дефект 13afda65: «Загрузка CPU» присутствует и не усечена.
    const cpuLabel = await page.evaluate(() => {
      const p = document.querySelector('[role="complementary"][aria-label="Ферма"]');
      const span = Array.from(p?.querySelectorAll('span') ?? []).find(s => (s.textContent ?? '').trim() === 'Загрузка CPU');
      if (!span) return null;
      return { clipped: span.scrollWidth > span.clientWidth + 1, scrollW: span.scrollWidth, clientW: span.clientWidth };
    });
    expect(cpuLabel, 'подпись «Загрузка CPU» на месте').not.toBeNull();
    expect(cpuLabel!.clipped, `«Загрузка CPU» не усечена (scrollW=${cpuLabel!.scrollW} clientW=${cpuLabel!.clientW})`).toBe(false);

    // Дефект 9f94bf4c: заголовок «Температуры» не усечён; переключатель окон — на своей строке,
    // кнопки не наезжают, помещаются в viewport.
    const temp = await page.evaluate(() => {
      const p = document.querySelector('[role="complementary"][aria-label="Ферма"]');
      const h = Array.from(p?.querySelectorAll('h3') ?? []).find(x => (x.textContent ?? '').trim() === 'Температуры');
      if (!h) return null;
      const hR = (h as HTMLElement).getBoundingClientRect();
      const btns = Array.from(p?.querySelectorAll('button') ?? [])
        .filter(b => ['1 мин', '5 мин', '15 мин', '1 ч'].includes((b.textContent ?? '').trim()));
      const rects = btns.map(b => {
        const r = b.getBoundingClientRect();
        return { label: (b.textContent ?? '').trim(), x: r.x, y: r.y, w: r.width, h: r.height, right: r.right };
      });
      const overlaps: string[] = [];
      for (let i = 0; i < rects.length; i++) {
        for (let j = i + 1; j < rects.length; j++) {
          const a = rects[i], b = rects[j];
          if (a.x < b.right && b.x < a.right && a.y < b.y + b.h && b.y < a.y + a.h) overlaps.push(`${a.label}×${b.label}`);
        }
        if (hR.x < rects[i].right && rects[i].x < hR.right && hR.y < rects[i].y + rects[i].h && rects[i].y < hR.y + hR.height) overlaps.push(`${rects[i].label}×заголовок`);
      }
      return {
        headerClipped: (h as HTMLElement).scrollWidth > (h as HTMLElement).clientWidth + 1,
        count: rects.length, rects, overlaps,
        viewportW: document.documentElement.clientWidth,
      };
    });
    expect(temp, 'заголовок «Температуры» найден').not.toBeNull();
    expect(temp!.headerClipped, 'заголовок «Температуры» не усечён').toBe(false);
    expect(temp!.count, 'все четыре кнопки окон на месте').toBe(4);
    for (const r of temp!.rects) {
      expect(r.x, `кнопка «${r.label}» в границах экрана по x`).toBeGreaterThanOrEqual(0);
      expect(r.right, `кнопка «${r.label}» помещается по x (${r.right} ≤ ${temp!.viewportW})`).toBeLessThanOrEqual(temp!.viewportW + 1);
      expect(r.h, `кнопка «${r.label}» не тоньше 28 px`).toBeGreaterThanOrEqual(28);
    }
    expect(temp!.overlaps, `кнопки и заголовок не наезжают друг на друга: ${JSON.stringify(temp!.overlaps)}`).toEqual([]);

    // Кнопки нажимаются: клик по каждой выбирает окно.
    const tempSection = page.locator('section', { has: page.getByRole('heading', { name: 'Температуры', exact: true }) }).first();
    for (const label of ['1 мин', '5 мин', '15 мин', '1 ч']) {
      const btn = tempSection.locator('button', { hasText: new RegExp(`^${label}$`) }).first();
      await btn.click({ timeout: 10000 });
      await page.waitForTimeout(800);
      const pressed = await tempSection.locator('button[aria-pressed="true"]').allTextContents();
      expect(pressed.map(t => t.trim()), `окно «${label}» выбирается кликом`).toContain(label);
    }

    await page.screenshot({ path: `${SHOTS}/05-temp-${width}.png`, fullPage: true });
  }
});
