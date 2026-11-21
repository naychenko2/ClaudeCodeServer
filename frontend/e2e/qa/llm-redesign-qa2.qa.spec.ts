import { test, expect, type Page } from '@playwright/test';
import { writeFileSync } from 'node:fs';
import { collectConsoleErrors, login } from './helpers';

// QA-2 (задача 924c3420): повторная приёмка дефекта b45ceaa6 и правки 6464ad57 на стенде
// ветки feat/local-model-llm-redesign-v1. Проверяем: (1) заголовок «Точные метрики» и приписка
// «Локальная модель · TTFT · медианы» читаются целиком на 360 и 1280 (clipped=0);
// (2) приписка переносится, высота кнопки приемлема, соседние блоки не поехали;
// (3) подсказка облачного сравнения ведёт в раскрытие по ТЕКУЩЕМУ имени, старого имени
// «Точные метрики локальной модели» в DOM нет; (4) выборочно: порядок вкладки и оба состояния облака.
// Засева нет: стенд — фикстура QA-1 (проект 182fd1b3 в data-qa), уборки нечего.

const PROJECT_ID = '182fd1b3-6507-4ef2-9d87-d59a7de50831';
const TITLE = 'Точные метрики';
const META = 'Локальная модель · TTFT · медианы';
const OLD_NAME = 'Точные метрики локальной модели';

let token: string;
const results: Record<string, unknown> = {};

test.beforeAll(async ({ playwright, baseURL }) => {
  const request = await playwright.request.newContext({ baseURL });
  token = await login(request);
  await request.dispose();
});

test.afterAll(() => {
  writeFileSync('/tmp/qa2-result.json', JSON.stringify(results, null, 2));
});


async function openPanel(page: Page, width: number) {
  if (width > 700) {
    await page.waitForSelector('[role="complementary"][aria-label="Ферма"]', { timeout: 30000 });
    return;
  }
  // На мобильной раскладке панель живёт в меню «Ещё» и открывается модалкой (как в QA-1).
  // Метка кнопки на мобиле может нести состояние движка («Ещё · Модель недоступна») — префиксный матч.
  // DOM-клики: тост уведомления (NotificationToasts.tsx:253) на 360 перекрывает шапку и
  // pointer-click упирается в него — к фиче отношения не имеет (отмечено в отчёте контекстом).
  const more = page.locator('button[aria-label^="Ещё"], button[title^="Ещё"]').first();
  await more.waitFor({ timeout: 30000 });
  await more.evaluate((el: HTMLElement) => el.click());
  const item = page.getByText('Ферма', { exact: true }).last();
  await item.waitFor({ timeout: 15000 });
  await item.evaluate((el: HTMLElement) => el.click());
  await page.waitForSelector('[role="complementary"][aria-label="Ферма"]', { timeout: 30000 });
}

// Снимок строки раскрытия метрик и порядка блоков — тот же метод, что ui_probe/QA-1:
// числа по фактическому layout (scrollWidth против clientWidth, rect блоков).
const PROBE = ([title, oldName]: [string, string]) => {
  const panel = document.querySelector('[role="complementary"][aria-label="Ферма"]');
  if (!panel) return null;
  const btns = [...panel.querySelectorAll('button[aria-expanded]')] as HTMLElement[];
  const metrics = btns.find(b => (b.textContent ?? '').includes(title));
  const spans = metrics
    ? [...metrics.querySelectorAll('span')].map((s: Element) => {
        const el = s as HTMLElement;
        return {
          text: (el.textContent ?? '').replace(/\s+/g, ' ').trim(),
          clientW: el.clientWidth,
          scrollW: el.scrollWidth,
          clipped: el.scrollWidth > el.clientWidth + 1,
          whiteSpace: getComputedStyle(el).whiteSpace,
        };
      })
    : [];
  const metricsBtn = metrics ? metrics.getBoundingClientRect() : null;
  const cloud = btns.find(b => (b.textContent ?? '').includes('Сравнить с облаком'));
  const h3 = [...panel.querySelectorAll('h3')].find(h => h.textContent?.includes('Сейчас на ферме'));
  const container = h3?.closest('section')?.parentElement;
  const blocks = [...(container?.children ?? [])].map((el: Element) => {
    const e = el as HTMLElement;
    const r = e.getBoundingClientRect();
    const head = e.querySelector('h3,h4,button');
    return {
      top: Math.round(r.top),
      bottom: Math.round(r.bottom),
      h: Math.round(r.height),
      head: (head?.textContent ?? '').replace(/\s+/g, ' ').trim().slice(0, 60),
    };
  });
  // Подсказка облака живёт в title-атрибуте ячейки «сквозная скорость» (asCell, TextTab.tsx:335).
  const titleAttrs = [...panel.querySelectorAll('[title]')].map(e => (e.getAttribute('title') ?? ''));
  const hint = titleAttrs.find(t => t.includes('смотрите в раскрытии')) ?? null;
  return {
    metricsFound: !!metrics,
    metricsExpanded: metrics?.getAttribute('aria-expanded') ?? null,
    metricsBtnH: metricsBtn ? Math.round(metricsBtn.height) : null,
    metricsBtnTop: metricsBtn ? Math.round(metricsBtn.top) : null,
    spans,
    clippedCount: spans.filter(s => s.clipped).length,
    cloudExpanded: cloud?.getAttribute('aria-expanded') ?? null,
    blocks,
    // Старое имя не должно жить ни в видимом тексте, ни в подсказках title
    oldNamePresent:
      (document.body.textContent ?? '').includes(oldName) ||
      [...document.querySelectorAll('[title]')].some(e => (e.getAttribute('title') ?? '').includes(oldName)),
    hint: hint ? hint.replace(/\s+/g, ' ').trim().slice(-160) : null,
  };
};

async function run(page: Page, context: any, width: number, cloudOpen: boolean, errors: string[]): Promise<any> {
  // Функция init-скрипта сериализуется в страницу: самодостаточная, всё — аргументами.
  await context.addInitScript(([tk, cloud]: [string, boolean]) => {
    localStorage.setItem('cc_token', tk);
    localStorage.setItem('cc_role', 'admin');
    localStorage.setItem('cc_local_model_tab', 'text');
    localStorage.setItem('cc_ws_zones', JSON.stringify({
      left: { layout: [['chats']], stash: [] },
      right: { layout: [['localModel']], stash: [] },
    }));
    if (cloud) localStorage.setItem('cc_local_llm_cloud_open', '1');
  }, [token, cloudOpen]);
  await page.goto(`/#/project/${PROJECT_ID}`);
  await page.waitForTimeout(3000);
  await openPanel(page, width);
  await page.waitForTimeout(3000);
  const args: [string, string] = [TITLE, OLD_NAME];
  const initial = await page.evaluate(PROBE, args);
  // Раскрыть «Точные метрики» — замер строки в открытом состоянии тоже нужен (кнопка та же,
  // но тело меняет высоту соседей).
  // DOM-клик: кнопка лежит внутри scroll-зоны панели (LocalModelPanel.tsx:234), и pointer-click
  // упирается в контейнер; событие click такое же и React-обработчик тот же.
  const metrics = page.locator('button[aria-expanded]', { hasText: TITLE }).first();
  expect(await metrics.count(), `кнопка раскрытия «${TITLE}» одна`).toBe(1);
  await metrics.evaluate((el: HTMLElement) => el.click());
  await page.waitForTimeout(1500);
  const opened = await page.evaluate(PROBE, args);
  await page.screenshot({ path: `/tmp/qa2-${width}-${cloudOpen ? 'open' : 'closed'}.png` });
  return { width, cloudOpen, errors, initial, opened };
}

function assertClean(res: any, width: number) {
  expect(res.initial, `панель не найдена на ${width}`).toBeTruthy();
  for (const state of [res.initial, res.opened]) {
    expect(state.metricsFound, `строка «${TITLE}» найдена на ${width}`).toBeTruthy();
    expect(state.clippedCount, `обрезанных фрагментов нет (spans: ${JSON.stringify(state.spans)})`).toBe(0);
    const titleSpan = state.spans.find((s: any) => s.text === TITLE);
    const metaSpan = state.spans.find((s: any) => s.text === META);
    expect(titleSpan, `заголовок «${TITLE}» читается целиком`).toBeTruthy();
    expect(metaSpan, `приписка «${META}» читается целиком`).toBeTruthy();
    // c2: высота кнопки раскрытия приемлема (одна-две строки, не простыня)
    expect(state.metricsBtnH, `высота кнопки раскрытия на ${width}`).toBeLessThanOrEqual(80);
    expect(state.oldNamePresent, `старого имени «${OLD_NAME}» в DOM нет`).toBe(false);
    // Текст подсказки (format.ts:462): «…смотрите в раскрытии „Точные метрики“» — кавычки „…“
    expect(state.hint ?? '', `подсказка облака ведёт в раскрытие по текущему имени`).toContain(`раскрытии „${TITLE}“`);
    expect(state.hint ?? '').not.toContain(OLD_NAME);
  }
  // c2: соседние блоки не поехали — ряд не сломан, пересечений блоков нет
  const blocks = res.opened.blocks;
  for (let i = 1; i < blocks.length; i++) {
    expect(blocks[i].top, `блок «${blocks[i].head}» не наползает на предыдущий`).toBeGreaterThanOrEqual(blocks[i - 1].bottom - 2);
  }
}

test.describe('1280', () => {
  test.use({ viewport: { width: 1280, height: 900 } });

  test('облако закрыто: без обрезки, порядок вкладки, облако закрыто', async ({ page, context }) => {
    const errors = collectConsoleErrors(page);
    const res = await run(page, context, 1280, false, errors);
    results['1280-closed'] = res;
    assertClean(res, 1280);
    expect(res.initial.cloudExpanded, 'облако закрыто по умолчанию').toBe('false');
    // c4 (выборочно): порядок v1 — ферма первой, метрики перед облаком, облако последним блоком
    const heads = res.initial.blocks.map((b: any) => b.head);
    const iFarm = heads.findIndex((h: string) => h.includes('Сейчас на ферме'));
    const iMetrics = heads.findIndex((h: string) => h.includes(TITLE));
    const iCloud = heads.findIndex((h: string) => h.includes('Сравнить с облаком'));
    expect(iFarm, 'карточка «Сейчас на ферме» — первый блок').toBe(0);
    expect(iMetrics, 'раскрытие метрик есть в ряду').toBeGreaterThanOrEqual(0);
    expect(iCloud, 'облачное сравнение — последний блок').toBe(heads.length - 1);
    expect(iMetrics).toBeLessThan(iCloud);
  });

  test('облако открыто: без обрезки, старого имени нет', async ({ page, context }) => {
    const errors = collectConsoleErrors(page);
    const res = await run(page, context, 1280, true, errors);
    results['1280-open'] = res;
    assertClean(res, 1280);
    expect(res.initial.cloudExpanded, 'состояние облака сохранено (открыто)').toBe('true');
  });
});

test.describe('360', () => {
  test.use({ viewport: { width: 360, height: 900 } });

  test('облако закрыто: без обрезки, приписка переносится', async ({ page, context }) => {
    const errors = collectConsoleErrors(page);
    const res = await run(page, context, 360, false, errors);
    results['360-closed'] = res;
    assertClean(res, 360);
    expect(res.initial.cloudExpanded, 'облако закрыто по умолчанию').toBe('false');
    const heads = res.initial.blocks.map((b: any) => b.head);
    expect(heads[0] ?? '', 'карточка «Сейчас на ферме» — первый блок').toContain('Сейчас на ферме');
  });

  test('облако открыто: без обрезки, старого имени нет', async ({ page, context }) => {
    const errors = collectConsoleErrors(page);
    const res = await run(page, context, 360, true, errors);
    results['360-open'] = res;
    assertClean(res, 360);
    expect(res.initial.cloudExpanded, 'состояние облака сохранено (открыто)').toBe('true');
  });
});
