import { test, expect } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { login, api, alignEpicRoute, type Api } from './helpers';

// QA-3 (задача a1b3e53f). Повторная приёмка «Процессов фичи» на master 52ea5e6d7.
// Доработка QA-4 (задача 55314d7f): T4 дополнен замером переноса однословного имени
// (дефект 20e96078 закрыт Ф-2.36); T6 — замер закрытия 604497e2.
// Пункты, не покрытые спеками прошлой приёмки:
// T1 = п.1: тач-цели на 360, обе темы: «Сменить»/«Почему так» строки маршрута, «из чего N ›»,
//      вкладки карточки персоны, «Специальность: …» (цели редактора — S9a/S9b measure-спека).
// T2 = п.2: ряды вкладок на планшетной ширине (700/900): в компактном ряду нет целей <40.
// T3 = п.6: рёбра графа: фиолетовый пунктир только у незавершённых proposed; от done — зелёное.
// T4 = п.7: обрезка имён узлов (line-clamp), шевроны края ленты, прокручиваемость ленты.
// T5 = п.8: метка этапа: локальная подсказка редактора и серверный 400 — одинаковые («этап «роль»»).
// T6 = п.9: визитка роли: заголовка «Пресеты для роли» нет, «Секции промпта» не дублируется.

const PID = 'ce3e224a-d0f8-4828-9519-f51d4c3dbf6a';
const EPIC = 'f571aace-a780-4c3e-abca-05741154c8ad';
const SHOTS = '.cc-attachments/qa-wf-e2e';
const PERSONA_NAME = 'qa-e2e-qa3-персона';

let client: Api;
let request: import('@playwright/test').APIRequestContext;
let token: string;
let personaId: string | null = null;
const tplIds: string[] = [];
const epicIds: string[] = [];

test.beforeAll(async ({ playwright, baseURL }) => {
  mkdirSync(SHOTS, { recursive: true });
  request = await playwright.request.newContext({ baseURL });
  token = await login(request);
  client = api(request, token);
  // Строка маршрута («Сменить»/«Почему так») и граф — на подтверждённом маршруте фиче-сида.
  await alignEpicRoute(client, EPIC);
});

test.afterAll(async () => {
  try {
    const personas = await client.get<any[]>('/api/personas');
    for (const p of personas.filter(x => x.name === PERSONA_NAME)) await client.del(`/api/personas/${p.id}`).catch(() => undefined);
  } catch { /* best-effort */ }
  for (const id of tplIds) await client.del(`/api/workflow-templates/${id}`).catch(() => undefined);
  for (const id of epicIds) await client.del(`/api/projects/${PID}/tasks/${id}`).catch(() => undefined);
  await request?.dispose();
});

type Rect = { w: number; h: number } | null;

const rectOf = (page: import('@playwright/test').Page, testId: string): Promise<Rect> =>
  page.getByTestId(testId).first().evaluate((el: Element) => {
    const r = (el as HTMLElement).getBoundingClientRect();
    return { w: Math.round(r.width * 10) / 10, h: Math.round(r.height * 10) / 10 };
  }).catch(() => null);

const rectByLabel = (page: import('@playwright/test').Page, label: string): Promise<Rect> =>
  page.getByRole('button', { name: label }).first().evaluate((el: Element) => {
    const r = (el as HTMLElement).getBoundingClientRect();
    return { w: Math.round(r.width * 10) / 10, h: Math.round(r.height * 10) / 10 };
  }).catch(() => null);

// Все видимые цели <40 на странице — с текстом и признаком «только иконка» (компакт).
const smallOnPage = (page: import('@playwright/test').Page) => page.evaluate(() => {
  const doc = document.documentElement;
  const small = [...document.querySelectorAll('button, [role="tab"], select, a[href], [role="checkbox"]')]
    .filter(e => (e as HTMLElement).offsetParent !== null)
    .map(e => {
      const r = (e as HTMLElement).getBoundingClientRect();
      const inner = ((e as HTMLElement).innerText || '').trim();
      return {
        w: Math.round(r.width * 10) / 10, h: Math.round(r.height * 10) / 10,
        top: Math.round(r.top),
        iconOnly: inner === '',
        text: (inner || e.getAttribute('aria-label') || e.getAttribute('title') || '').slice(0, 40),
      };
    }).filter(x => x.w > 0 && x.h > 0 && (x.w < 40 || x.h < 40));
  return { docOverflow: doc.scrollWidth - doc.clientWidth, small };
});

async function openPage(context: import('@playwright/test').BrowserContext, path: string, width: number, height: number, theme: string) {
  const page = await context.newPage();
  page.setViewportSize({ width, height });
  const errs: string[] = [];
  page.on('console', m => { if (m.type() === 'error') errs.push(m.text().slice(0, 200)); });
  page.on('pageerror', e => errs.push(String(e).slice(0, 200)));
  await context.addInitScript(([tk, th]: string[]) => {
    localStorage.setItem('cc_token', tk);
    localStorage.setItem('theme-mode', th);
  }, [token, theme]);
  await page.goto(path, { waitUntil: 'domcontentloaded' });
  await page.waitForTimeout(4000);
  return { page, errs };
}

const ge40 = (r: Rect) => r !== null && r.w >= 40 && r.h >= 40;

test('T1: тач-цели на 360 в обеих темах: строка маршрута, «из чего N ›», вкладки и «Специальность» карточки персоны', async ({ browser }) => {
  test.setTimeout(240_000);
  for (const old of (await client.get<any[]>('/api/personas')).filter(x => x.name === PERSONA_NAME)) {
    await client.del(`/api/personas/${old.id}`).catch(() => undefined);
  }
  const persona = await client.post<any>('/api/personas', { name: PERSONA_NAME, specialty: 'analyst', inheritsAccess: true });
  personaId = persona.id;

  for (const theme of ['light', 'dark']) {
    const ctx = await browser.newContext();
    const { page, errs } = await openPage(ctx, `/#/project/${PID}/feature/${EPIC}`, 360, 800, theme);
    const rChange = await rectOf(page, 'feature-workflow-confirm-change');
    const rWhy = await rectOf(page, 'feature-workflow-confirm-why');
    const rSteps = await rectOf(page, 'volume-open-steps');
    console.log(`T1 фича @360 ${theme}:`, JSON.stringify({ Сменить: rChange, ПочемуТак: rWhy, изЧего: rSteps }));
    const pageSmall = await smallOnPage(page);
    console.log(`T1 фича @360 ${theme} цели <40:`, JSON.stringify(pageSmall).slice(0, 1200));
    await page.screenshot({ path: `${SHOTS}/qa3-t1-feature-360-${theme}.png`, fullPage: true });
    expect.soft(ge40(rChange), `«Сменить» ≥40 (${theme})`).toBeTruthy();
    expect.soft(ge40(rWhy), `«Почему так» ≥40 (${theme})`).toBeTruthy();
    expect.soft(ge40(rSteps), `«из чего N ›» ≥40 (${theme})`).toBeTruthy();
    await page.close();

    const { page: pp, errs: pe } = await openPage(ctx, `/#/personas/${personaId}`, 360, 800, theme);
    const tabs: Record<string, Rect> = {};
    for (const t of ['Профиль', 'Умения', 'Проактивность', 'Память', 'Задачи']) tabs[t] = await rectByLabel(pp, t);
    tabs['Специальность'] = await rectOf(pp, 'persona-specialty-open');
    console.log(`T1 персона @360 ${theme}:`, JSON.stringify(tabs));
    await pp.screenshot({ path: `${SHOTS}/qa3-t1-persona-360-${theme}.png`, fullPage: true });
    for (const [name, r] of Object.entries(tabs)) {
      expect.soft(ge40(r), `«${name}» ≥40 (${theme})`).toBeTruthy();
    }
    console.log(`T1 ошибки консоли ${theme}:`, JSON.stringify([...new Set([...errs, ...pe])]));
    await pp.close();
    await ctx.close();
  }
});

test('T2: ряды вкладок на планшетной ширине: в компактном ряду (иконки) нет целей <40', async ({ browser }) => {
  test.setTimeout(240_000);
  // 620 — чтобы гарантированно включить autoCompact (полный текстовый ряд не влезает);
  // 700/900 — обычный планшет: ряд может остаться полнотекстовым (десктопная норма 32 — не этот дефект).
  for (const width of [620, 700, 900]) {
    const ctx = await browser.newContext();
    const { page } = await openPage(ctx, `/#/project/${PID}`, width, 900, 'light');
    const small = await smallOnPage(page);
    // Ряд вкладок — верхняя полоса шапки (top < 100). Прочее (рейлы панелей и т.п.) — не ряд.
    const tabWords = ['Чаты', 'Задачи', 'Заметки', 'Знания', 'Персоны', 'Проекты', 'Календарь'];
    const tabSmall = small.small.filter(x => x.top < 100 && tabWords.some(w => x.text.includes(w)));
    const iconTabSmall = tabSmall.filter(x => x.iconOnly);
    console.log(`T2 проект @${width}: docOverflow=${small.docOverflow} целей <40 всего: ${small.small.length}; в верхнем ряду вкладок: ${tabSmall.length}, из них иконочных: ${iconTabSmall.length}`, JSON.stringify(tabSmall).slice(0, 1200));
    console.log(`T2 проект @${width} прочие <40 (не ряд):`, JSON.stringify(small.small.filter(x => x.top >= 100)).slice(0, 800));
    await page.screenshot({ path: `${SHOTS}/qa3-t2-project-${width}.png`, fullPage: true });
    // Дефект по п.2 — иконочный (компакт) сегмент ряда <40. Полнотекстовый 32×32 — десктопная норма, не этот дефект.
    expect.soft(iconTabSmall.length, `в компактном ряду вкладок @${width} нет иконочных целей <40`).toBe(0);
    await page.close();
    await ctx.close();
  }
  // Ряды с autoCompact (4 опции) из Ф-2.35 п.3: на десктопе тулбар чатов даёт отдельные
  // xs-контролы (не PillSwitch), а модалка «Вид списка» с compact-рядами живёт на мобиле.
  // Модалка на 360 — иконочные сегменты обязаны быть ≥40.
  const ctxD = await browser.newContext();
  const { page: pd } = await openPage(ctxD, '/#/chats', 360, 800, 'light');
  await pd.getByRole('button', { name: /Вид: группировка/ }).first().click({ timeout: 15000 });
  await pd.waitForTimeout(2000);
  const modalSmall = await pd.evaluate(() => {
    const modal = document.querySelector('.cc-sheet-card') ?? document.querySelector('.cc-modal-card') ?? document.body;
    const out: any[] = [];
    for (const e of [...modal.querySelectorAll('button')]) {
      const el = e as HTMLElement;
      if (el.offsetParent === null) continue;
      const r = el.getBoundingClientRect();
      const inner = (el.innerText || '').trim();
      out.push({
        w: Math.round(r.width * 10) / 10, h: Math.round(r.height * 10) / 10, iconOnly: inner === '',
        text: (inner || el.getAttribute('aria-label') || el.getAttribute('title') || '').slice(0, 30),
      });
    }
    return out;
  }).catch(() => []);
  // Ряды вкладок листа — группы/сортировка/«Готово». Крестик «Закрыть» — не ряд:
  // его замер логируем отдельно (найдено 28×28 — заведён отдельной карточкой).
  const rows = modalSmall.filter(x => x.text !== 'Закрыть');
  const compactSmall = rows.filter(x => (x.w < 40 || x.h < 40) && x.iconOnly);
  const closeBtn = modalSmall.find(x => x.text === 'Закрыть');
  console.log('T2 «Вид списка» @360 все кнопки:', JSON.stringify(modalSmall).slice(0, 1500));
  console.log('T2 «Вид списка» @360 рядовых иконочных <40:', compactSmall.length, '| крестик листа:', JSON.stringify(closeBtn));
  await pd.screenshot({ path: `${SHOTS}/qa3-t2-view-modal-360.png`, fullPage: true });
  expect.soft(compactSmall.length, 'в compact-рядах модалки «Вид списка» @360 нет иконочных целей <40').toBe(0);
  await pd.close();
  await ctxD.close();

  // Карточка персоны на 760 (планшет): ряд вкладок персоны НЕ в компакте (полный текст,
  // десктопная норма 32) — это наблюдение, не проверка п.2; компактный ряд персоны меряется на 360 (T1).
  const ctx = await browser.newContext();
  const { page } = await openPage(ctx, `/#/personas/${personaId}`, 760, 900, 'light');
  const tabs: Record<string, Rect> = {};
  for (const t of ['Профиль', 'Умения', 'Проактивность', 'Память', 'Задачи']) tabs[t] = await rectByLabel(page, t);
  console.log('T2 персона @760 (ряд полнотекстовый, не компакт — наблюдение):', JSON.stringify(tabs));
  await page.screenshot({ path: `${SHOTS}/qa3-t2-persona-760.png`, fullPage: true });
  await page.close();
  await ctx.close();
});

test('T3: рёбра графа: фиолетовый пунктир только у незавершённых proposed; от done — зелёное', async ({ browser }) => {
  test.setTimeout(240_000);
  const readGraph = async (page: import('@playwright/test').Page) => {
    const edges = await page.locator('[data-testid^="pgraph-edge-"]').evaluateAll((els: Element[]) => els.map(e => ({
      id: e.getAttribute('data-testid'),
      stroke: e.getAttribute('stroke'),
      dash: e.getAttribute('stroke-dasharray'),
      proposed: e.getAttribute('data-proposed'),
      far: e.getAttribute('data-far'),
    })));
    const nodes = await page.locator('[data-testid^="pgraph-node-"]').evaluateAll((els: Element[]) => els.map(e => ({
      id: e.getAttribute('data-testid'),
      state: e.getAttribute('data-state'),
      proposed: e.getAttribute('data-proposed'),
    })));
    return { edges, nodes };
  };

  // А. Фиче-сид: подтверждённый маршрут — purple быть не должно; от done-узла — зелёное ребро.
  const ctx = await browser.newContext();
  const { page } = await openPage(ctx, `/#/project/${PID}/feature/${EPIC}`, 1280, 900, 'light');
  const g1 = await readGraph(page);
  console.log('T3 фиче-сид рёбра:', JSON.stringify(g1.edges).slice(0, 1500));
  console.log('T3 фиче-сид узлы:', JSON.stringify(g1.nodes).slice(0, 800));
  const purple1 = g1.edges.filter(e => e.stroke === 'var(--c-plan-border)');
  expect.soft(purple1.length, 'на подтверждённом маршруте нет фиолетовых рёбер').toBe(0);
  const doneIds = new Set(g1.nodes.filter(n => n.state === 'done').map(n => (n.id ?? '').replace('pgraph-node-', '')));
  const badGreen = g1.edges.filter(e => {
    const from = (e.id ?? '').replace('pgraph-edge-', '').split('-')[0];
    return doneIds.has(from) && e.stroke !== 'var(--c-success)';
  });
  expect.soft(badGreen.length, 'от done-узлов рёбра зелёные').toBe(0);
  await page.screenshot({ path: `${SHOTS}/qa3-t3-confirmed.png`, fullPage: true });
  await page.close();
  await ctx.close();

  // Б. Предложенный маршрут (без подтверждения): к незавершённым proposed — фиолетовый пунктир.
  // Свой цепочечный шаблон (review после qa) — чтобы в предложенном составе были рёбра;
  // в описании эпика просим именно его (рекомендатель выбирает моделью, не детерминированно).
  const chainTpl = await client.post<any>('/api/workflow-templates', {
    name: 'qa-e2e-qa3-chain', stages: [{ role: 'qa', presence: 'on' }, { role: 'review', presence: 'on', after: ['qa'] }],
  });
  const chainId = chainTpl?.id ?? chainTpl?.template?.id ?? null;
  if (chainId) tplIds.push(chainId);
  const epic2 = await client.post<any>(`/api/projects/${PID}/tasks`, {
    title: 'qa-e2e-qa3-proposed-feature', role: 'feature', assignee: 'me',
    description: 'Проверка рёбер предложенного маршрута. Возьми шаблон «qa-e2e-qa3-chain»: ревью после проверки.',
  });
  epicIds.push(epic2.id);
  const rec = await request.post(`/api/tasks/${epic2.id}/workflow/recommend`, {
    headers: { Authorization: `Bearer ${token}` }, data: {},
    timeout: 120_000,
  });
  const recBody = await rec.json().catch(() => null);
  console.log('T3 recommend:', rec.status(), JSON.stringify({ state: recBody?.state, reason: recBody?.reason }).slice(0, 200));
  if (recBody?.state === 'proposed' || recBody?.proposal) {
    const ctx2 = await browser.newContext();
    const { page: p2 } = await openPage(ctx2, `/#/project/${PID}/feature/${epic2.id}`, 1280, 900, 'light');
    const g2 = await readGraph(p2);
    console.log('T3 proposed рёбра:', JSON.stringify(g2.edges).slice(0, 1500));
    console.log('T3 proposed узлы:', JSON.stringify(g2.nodes).slice(0, 800));
    if (g2.edges.length === 0 && g2.nodes.length > 1) {
      console.log('T3 Б: в предложенном составе нет рёбер (состав параллельный или выбран не мой шаблон) — фиолетовый пунктир вживую не измерен');
    }
    const badProposed = g2.edges.filter(e => e.proposed === 'true' && (e.stroke !== 'var(--c-plan-border)' || !e.dash));
    expect.soft(badProposed.length, 'у незавершённых proposed рёбер — фиолетовый пунктир').toBe(0);
    const purpleNotProposed = g2.edges.filter(e => e.stroke === 'var(--c-plan-border)' && e.proposed !== 'true');
    expect.soft(purpleNotProposed.length, 'фиолетовый пунктир только у proposed').toBe(0);
    await p2.screenshot({ path: `${SHOTS}/qa3-t3-proposed.png`, fullPage: true });
    await p2.close();
    await ctx2.close();
  } else {
    console.log('T3 Б не проверено: рекомендация не ответила (state:', recBody?.state, ')');
  }
});

test('T4: имена узлов с многоточием, шевроны края и прокручиваемость ленты на 360', async ({ browser }) => {
  test.setTimeout(180_000);
  const ctx = await browser.newContext();
  const { page, errs } = await openPage(ctx, `/#/project/${PID}/feature/${EPIC}`, 360, 800, 'light');
  const strip = await page.locator('[data-testid="feature-process-graph"]').first().evaluate((el: Element) => {
    const e = el as HTMLElement;
    return { scrollWidth: e.scrollWidth, clientWidth: e.clientWidth, scrollLeft: e.scrollLeft };
  }).catch(() => null);
  console.log('T4 лента @360:', JSON.stringify(strip));
  expect.soft(strip !== null && strip.scrollWidth > strip.clientWidth, 'лента шире контейнера — прокручивается').toBeTruthy();
  // Ширина узла на мобиле (NODE_W_MOBILE=104): правка переноса её не меняла и менять не должна.
  const nodeWidths = await page.locator('[data-testid^="pgraph-node-"]').evaluateAll((els: Element[]) => els.map((e) => Math.round((e as HTMLElement).getBoundingClientRect().width * 10) / 10));
  console.log('T4 ширина узлов @360:', JSON.stringify(nodeWidths));
  expect.soft(nodeWidths.length > 0 && nodeWidths.every((w) => w === 104), 'ширина узлов на мобиле 104 px (не изменилась)').toBeTruthy();

  const chevrons = async () => page.evaluate(() => {
    const vis = (sel: string) => { const e = document.querySelector(sel) as HTMLElement | null; return e ? (e.offsetParent !== null) : false; };
    return { left: vis('[data-testid="pgraph-scroll-left"]'), right: vis('[data-testid="pgraph-scroll-right"]') };
  });
  // Лента при старте может быть сама сведена к выбранному узлу (scrollIntoView) — приводим к левому краю.
  await page.locator('[data-testid="feature-process-graph"]').first().evaluate((el: Element) => { (el as HTMLElement).scrollLeft = 0; }).catch(() => undefined);
  await page.waitForTimeout(1200);
  const c0 = await chevrons();
  console.log('T4 шевроны в начале:', JSON.stringify(c0));
  expect.soft(c0.right, 'при незавершённом правом крае виден шеврон «есть ещё»').toBeTruthy();
  expect.soft(c0.left, 'у левого края шеврона нет').toBeFalsy();
  const scrolled = await page.locator('[data-testid="feature-process-graph"]').first().evaluate((el: Element) => {
    const e = el as HTMLElement;
    e.scrollLeft = e.scrollWidth;
    return new Promise<boolean>(res => setTimeout(() => res(true), 800));
  }).catch(() => false);
  void scrolled;
  await page.waitForTimeout(1200);
  const c1 = await chevrons();
  console.log('T4 шевроны после прокрутки в конец:', JSON.stringify(c1));
  expect.soft(c1.left, 'после прокрутки в конец виден левый шеврон').toBeTruthy();
  expect.soft(c1.right, 'у правого края шеврон гаснет').toBeFalsy();

  // Обрезка имён: clamp с многоточием (line-clamp), а не срез по букве контейнером.
  const titles = await page.locator('[data-testid^="pgraph-title-"]').evaluateAll((els: Element[]) => els.map(e => {
    const el = e as HTMLElement;
    const cs = getComputedStyle(el);
    return {
      id: e.getAttribute('data-testid'),
      clamp: cs.webkitLineClamp ?? (cs as any).WebkitLineClamp ?? null,
      overflow: cs.overflow,
      clipped: el.scrollWidth > el.clientWidth || el.scrollHeight > el.clientHeight,
    };
  }));
  console.log('T4 заголовки:', JSON.stringify(titles).slice(0, 1200));
  const noClamp = titles.filter(t => !t.clamp || t.clamp === 'none' || t.overflow !== 'hidden');
  expect.soft(noClamp.length, 'каждый заголовок обрезан line-clamp с hidden').toBe(0);
  await page.screenshot({ path: `${SHOTS}/qa3-t4-graph-360.png`, fullPage: true });
  await page.close();
  await ctx.close();

  // Длинное имя (40 символов, предел TitleLimit): на узкой колонке имя обязано резаться
  // clamp'ом с многоточием, полное имя остаётся в title/aria. Перенос однословного имени —
  // по слогам со знаком (дефект 20e96078 закрыт Ф-2.36, коммит 06c0f6556): меряется ниже.
  const LONG = 'Очень длинное название этапа процесса'; // 37 символов, в пределах TitleLimit=40
  const WORD = 'Производственная'; // одно слово: в колонку 104 px целиком не влезает
  const tpl = await client.post<any>('/api/workflow-templates', {
    name: 'qa-e2e-qa3-longname', stages: [{ role: 'qa', presence: 'on', title: LONG }, { role: 'review', presence: 'on', title: WORD }],
  });
  const tplId = tpl?.id ?? tpl?.template?.id ?? null;
  if (tplId) tplIds.push(tplId);
  const epic2 = await client.post<any>(`/api/projects/${PID}/tasks`, {
    title: 'qa-e2e-qa3-longname-feature', role: 'feature', assignee: 'me', description: 'Проверка обрезки длинных имён узлов.',
  });
  epicIds.push(epic2.id);
  await client.post(`/api/tasks/${epic2.id}/workflow/confirm`, { templateId: tplId, scope: 'global' });
  const ctx2 = await browser.newContext();
  const { page: p2, errs: e2 } = await openPage(ctx2, `/#/project/${PID}/feature/${epic2.id}`, 360, 800, 'light');
  const longTitle = await page2Info(p2, LONG);
  console.log('T4 длинное имя @360:', JSON.stringify(longTitle));
  expect.soft(longTitle !== null && longTitle.clamped, 'длинное имя обрезано (clamped)').toBeTruthy();
  expect.soft(longTitle?.fullInTitle, 'полное имя сохранено в title/aria').toBeTruthy();

  // Перенос однословного имени (дефект 20e96078, Ф-2.36): политика — hyphens:auto +
  // break-word (не 'anywhere'), lang='ru'. Разрыв по слогам со знаком меряется по знаку:
  // чернила строки на скриншоте длиннее адванса текста на ширину дефиса. (Ширины сами по
  // себе не различают политики: точка разрыва при hyphens и 'anywhere' здесь совпадает.)
  const loc = p2.locator('[data-testid^="pgraph-title-"]').filter({ hasText: WORD }).first();
  await loc.scrollIntoViewIfNeeded({ timeout: 15000 }).catch(() => undefined);
  await p2.waitForTimeout(500);
  const wrap = await measureWrap(p2, WORD);
  console.log(`T4 перенос «${WORD}» @360:`, JSON.stringify(wrap));
  expect.soft(wrap !== null && wrap.found, `узел с именем «${WORD}» найден`).toBeTruthy();
  expect.soft(wrap?.style.overflowWrap, 'overflow-wrap — break-word').toBe('break-word');
  expect.soft(wrap?.style.overflowWrap, 'overflow-wrap — не anywhere').not.toBe('anywhere');
  expect.soft(wrap?.style.hyphens, 'hyphens — auto').toBe('auto');
  expect.soft(wrap?.lang, 'lang страницы — ru (условие hyphens)').toBe('ru');
  expect.soft((wrap?.lines ?? 0) >= 2, 'длинное слово перенесено на вторую строку').toBeTruthy();
  expect.soft(wrap?.nextDoesNotFit, 'разрыв вынужденный: следующая буква не влезает').toBeTruthy();
  // Знак переноса — по прямоугольнику строки: Blink включает отрисованный автодефис в rect
  // текстового узла (пробник qa4-probe: правый край = ширина текста + дефис ровно; на
  // скриншоте элемента это видно глазами: «Производ-ственная»). Пиксели скриншота для замера
  // ненадёжны: элемент может быть обрезан лентой.
  expect.soft(wrap !== null && wrap.lineRight - wrap.lineLeft >= wrap.wLine1 + 2, 'разрыв со знаком: прямоугольник строки длиннее текста на ширину дефиса').toBeTruthy();
  await p2.screenshot({ path: `${SHOTS}/qa3-t4-longname-360.png`, fullPage: true });

  // Мутация чувствительности замера (правкой кода продукта не является): на живом элементе
  // вернуть прежнюю политику 'anywhere' без hyphens — разрыв может остаться на том же месте,
  // но знак обязан исчезнуть из rect'а; потом вернуть как было.
  if (wrap) {
    const mut = await p2.evaluate(([w]: string[]) => {
      const el = [...document.querySelectorAll('[data-testid^="pgraph-title-"]')].find((e) => ((e as HTMLElement).textContent ?? '').trim().startsWith(w.slice(0, 12))) as HTMLElement | null;
      if (!el) return null;
      const prevOw = el.style.overflowWrap;
      const prevHy = el.style.hyphens;
      el.style.overflowWrap = 'anywhere';
      el.style.hyphens = 'none';
      const tn = [...el.childNodes].find((n) => n.nodeType === 3) as Text;
      const rg = document.createRange();
      rg.selectNodeContents(tn);
      const rects = [...rg.getClientRects()].filter((r) => r.width > 0 && r.height > 0);
      const lines: { top: number; left: number; right: number }[] = [];
      for (const r of rects) {
        const L = lines.find((l) => Math.abs(l.top - r.top) < 2);
        if (L) { L.left = Math.min(L.left, r.left); L.right = Math.max(L.right, r.right); } else lines.push({ top: r.top, left: r.left, right: r.right });
      }
      let lo = 0;
      let hi = tn.data.length;
      while (lo < hi) {
        const mid = (lo + hi + 1) >> 1;
        const r2 = document.createRange();
        r2.setStart(tn, 0);
        r2.setEnd(tn, mid);
        const rs = [...r2.getClientRects()].filter((r) => r.width > 0 && r.height > 0);
        if (rs.length && rs.every((r) => Math.abs(r.top - lines[0].top) < 2)) lo = mid; else hi = mid - 1;
      }
      const line1 = tn.data.slice(0, lo);
      const cs = getComputedStyle(el);
      const probe = document.createElement('span');
      probe.style.cssText = 'position:absolute;visibility:hidden;white-space:pre;'
        + `font-family:${cs.fontFamily};font-size:${cs.fontSize};font-weight:${cs.fontWeight};font-style:${cs.fontStyle};letter-spacing:${cs.letterSpacing};`;
      document.body.appendChild(probe);
      probe.textContent = line1;
      const wLine1 = Math.round(probe.getBoundingClientRect().width * 10) / 10;
      probe.remove();
      const res = { line1, wLine1, lineLeft: Math.round(lines[0].left * 10) / 10, lineRight: Math.round(lines[0].right * 10) / 10, lines: lines.length };
      el.style.overflowWrap = prevOw;
      el.style.hyphens = prevHy;
      return res;
    }, [WORD]).catch(() => null);
    console.log(`T4 мутация «anywhere» без hyphens @360:`, JSON.stringify(mut));
    expect.soft(mut !== null && mut.lineRight - mut.lineLeft < mut.wLine1 + 2, 'при «anywhere» без hyphens знака на строке нет (замер чувствителен к политике)').toBeTruthy();
  }
  console.log('T4 ошибки консоли:', JSON.stringify([...new Set([...errs, ...e2])]));
  await p2.close();
  await ctx2.close();
});

// Найти узел с длинным именем и померить его заголовок.
async function page2Info(page: import('@playwright/test').Page, fullTitle: string) {
  const info = await page.locator('[data-testid^="pgraph-title-"]').evaluateAll((els: Element[], full: string) => {
    for (const e of els) {
      const el = e as HTMLElement;
      if ((el.textContent ?? '').trim().startsWith(full.slice(0, 12))) {
        const parent = el.closest('[data-testid^="pgraph-node-"]') as HTMLElement | null;
        return {
          clamped: el.scrollHeight > el.clientHeight || el.scrollWidth > el.clientWidth,
          clamp: getComputedStyle(el).webkitLineClamp ?? null,
          fullInTitle: (parent?.getAttribute('aria-label') ?? '').includes(full.slice(0, 12)) || (el.getAttribute('title') ?? '').includes(full.slice(0, 12)),
        };
      }
    }
    return null;
  }, fullTitle).catch(() => null);
  return info;
}

// Замер переноса однословного имени (дефект 20e96078): строки layout'а, текст первой строки
// (бинарный поиск по прямоугольникам диапазона), контрольные ширины «текст» / «текст+дефис» /
// «текст+след. буква», знак в innerText, computed style и lang.
type WrapMeasure = {
  found: boolean;
  clientWidth: number;
  lines: number;
  line1: string;
  next: string;
  wLine1: number;
  wLine1Dash: number;
  wLine1Next: number;
  dashFits: boolean;
  nextDoesNotFit: boolean;
  innerTextDash: boolean;
  // Прямоугольник первой строки текста: Blink включает в него отрисованный автодефис
  // (пробник 2026-11-15: правый край = ширина текста + дефис ровно). Его длина против
  // ширины текста — прямой признак знака; пиксели скриншота для этого ненадёжны
  // (обрезка элемента лентой).
  lineLeft: number;
  lineRight: number;
  style: { hyphens: string; overflowWrap: string; clamp: string | null };
  lang: string | null;
};

const measureWrap = (page: import('@playwright/test').Page, word: string): Promise<WrapMeasure | null> =>
  page.locator('[data-testid^="pgraph-title-"]').evaluateAll((els: Element[], word: string) => {
    const mk = (found: boolean, el: HTMLElement | null, line1 = '', next = ''): WrapMeasure => ({
      found,
      clientWidth: el ? el.clientWidth : 0,
      lines: 0,
      line1,
      next,
      wLine1: 0,
      wLine1Dash: 0,
      wLine1Next: 0,
      dashFits: false,
      nextDoesNotFit: false,
      innerTextDash: false,
      lineLeft: 0,
      lineRight: 0,
      style: el ? { hyphens: getComputedStyle(el).hyphens, overflowWrap: getComputedStyle(el).overflowWrap, clamp: getComputedStyle(el).webkitLineClamp || null } : { hyphens: '', overflowWrap: '', clamp: null },
      lang: el ? (el.closest('[lang]')?.getAttribute('lang') ?? document.documentElement.getAttribute('lang')) : null,
    });
    for (const e of els) {
      const el = e as HTMLElement;
      const text = (el.textContent ?? '').trim();
      if (!text.startsWith(word.slice(0, 12))) continue;
      const tn = [...el.childNodes].find((n) => n.nodeType === 3) as Text | undefined;
      if (!tn) continue;
      const rg = document.createRange();
      rg.selectNodeContents(tn);
      const rects = [...rg.getClientRects()].filter((r) => r.width > 0 && r.height > 0);
      const lines: { top: number; left: number; right: number }[] = [];
      for (const r of rects) {
        const L = lines.find((l) => Math.abs(l.top - r.top) < 2);
        if (L) { L.left = Math.min(L.left, r.left); L.right = Math.max(L.right, r.right); } else lines.push({ top: r.top, left: r.left, right: r.right });
      }
      if (lines.length < 2) return mk(true, el);
      let lo = 0;
      let hi = tn.data.length;
      while (lo < hi) {
        const mid = (lo + hi + 1) >> 1;
        const r2 = document.createRange();
        r2.setStart(tn, 0);
        r2.setEnd(tn, mid);
        const rs = [...r2.getClientRects()].filter((r) => r.width > 0 && r.height > 0);
        if (rs.length && rs.every((r) => Math.abs(r.top - lines[0].top) < 2)) lo = mid; else hi = mid - 1;
      }
      const line1 = tn.data.slice(0, lo);
      const next = tn.data[lo] ?? '';
      const cs = getComputedStyle(el);
      const probe = document.createElement('span');
      probe.style.cssText = 'position:absolute;visibility:hidden;white-space:pre;'
        + `font-family:${cs.fontFamily};font-size:${cs.fontSize};font-weight:${cs.fontWeight};font-style:${cs.fontStyle};letter-spacing:${cs.letterSpacing};`;
      document.body.appendChild(probe);
      const wOf = (s: string) => { probe.textContent = s; return probe.getBoundingClientRect().width; };
      const wLine1 = wOf(line1);
      const wLine1Dash = wOf(line1 + '-');
      const wLine1Next = wOf(line1 + next);
      probe.remove();
      const inner = (el.innerText ?? '') as string;
      return {
        found: true,
        clientWidth: el.clientWidth,
        lines: lines.length,
        line1,
        next,
        wLine1: Math.round(wLine1 * 10) / 10,
        wLine1Dash: Math.round(wLine1Dash * 10) / 10,
        wLine1Next: Math.round(wLine1Next * 10) / 10,
        dashFits: wLine1Dash <= el.clientWidth,
        nextDoesNotFit: wLine1Next > el.clientWidth,
        innerTextDash: /-\s*\n/.test(inner),
        lineLeft: Math.round(lines[0].left * 10) / 10,
        lineRight: Math.round(lines[0].right * 10) / 10,
        style: { hyphens: cs.hyphens, overflowWrap: cs.overflowWrap, clamp: cs.webkitLineClamp || null },
        lang: el.closest('[lang]')?.getAttribute('lang') ?? document.documentElement.getAttribute('lang'),
      };
    }
    return null;
  }, word).catch(() => null);

test('T5: метка этапа одинаковая: локальная подсказка редактора и серверный 400', async ({ browser }) => {
  test.setTimeout(180_000);
  // Серверный отказ: дубль одиночной роли.
  const r = await request.post('/api/workflow-templates', {
    headers: { Authorization: `Bearer ${token}` },
    data: { name: 'qa-e2e-qa3-dup', stages: [{ role: 'cut', presence: 'on' }, { role: 'cut', presence: 'on' }] },
  });
  const body = await r.text();
  console.log('T5 сервер:', r.status(), body.slice(0, 300));
  expect.soft(r.status(), 'дубль одиночной роли — 400').toBe(400);
  expect.soft(body, 'сервер называет этап по роли').toContain('этап «cut»');

  // Та же ошибка с обеих сторон: имя этапа длиннее предела. Локальная подсказка редактора
  // и серверный 400 обязаны назвать один этап одинаково («этап «qa»»).
  const LONG41 = 'Имя этапа процесса фичи, которое заведомо длиннее сорока символов';
  const r2 = await request.post('/api/workflow-templates', {
    headers: { Authorization: `Bearer ${token}` },
    data: { name: 'qa-e2e-qa3-long', stages: [{ role: 'qa', presence: 'on', title: LONG41 }] },
  });
  const body2 = await r2.text();
  console.log('T5 сервер (длинное имя):', r2.status(), body2.slice(0, 300));
  expect.soft(r2.status(), 'длинное имя — серверный 400').toBe(400);
  expect.soft(body2, 'сервер называет этап «qa»').toContain('этап «qa»');
  expect.soft(body2).toContain('имя этапа длиннее 40 символов');

  const tpl = await client.post<any>('/api/workflow-templates', {
    name: 'qa-e2e-qa3-label', stages: [{ role: 'qa', presence: 'on' }],
  });
  const tplId = tpl?.id ?? tpl?.template?.id ?? null;
  if (tplId) tplIds.push(tplId);
  const ctx = await browser.newContext();
  const { page, errs } = await openPage(ctx, '/#/specialties', 1280, 900, 'light');
  await page.getByText('Процессы фичи', { exact: true }).click({ timeout: 10000 });
  await page.waitForTimeout(1500);
  await page.getByRole('button', { name: /qa-e2e-qa3-label/ }).first().click({ timeout: 10000 });
  await page.waitForTimeout(2000);
  await page.locator('[data-stage-index]').filter({ hasText: 'qa' }).first().click({ timeout: 10000 });
  await page.waitForTimeout(1000);
  // Поле «Имя этапа» — первый textbox панели настройки этапа [data-stage-settings].
  await page.locator('[data-stage-settings]').getByRole('textbox').first().fill(LONG41, { timeout: 10000 });
  await page.waitForTimeout(1500);
  const bodyText = (await page.locator('body').innerText()).replace(/\n/g, ' | ');
  const localErr = bodyText.match(/Сохранить нельзя[^|]*|этап «qa»[^|]*|Имя длиннее[^|]*/g) ?? [];
  console.log('T5 локальная подсказка:', JSON.stringify(localErr.slice(0, 4)));
  expect.soft(localErr.length, 'редактор показывает локальную ошибку по этой же метке').toBeGreaterThan(0);
  expect.soft(bodyText, 'локальная подсказка называет этап «qa»').toContain('этап «qa»');
  await page.screenshot({ path: `${SHOTS}/qa3-t5-local-error.png`, fullPage: true });
  console.log('T5 ошибки консоли:', JSON.stringify([...new Set(errs)]));
  await page.close();
  await ctx.close();
});

test('T6: визитка роли и настройка роли: при showTitle=false нет заголовка «Пресеты для роли», «Секции промпта» не дублируется', async ({ browser }) => {
  test.setTimeout(180_000);
  const ctx = await browser.newContext();
  const { page, errs } = await openPage(ctx, '/#/specialties', 1280, 900, 'light');
  await page.getByText('Аналитик', { exact: true }).first().click({ timeout: 15000 });
  await page.waitForTimeout(3000);
  // Текст рендерится в верхнем регистре (text-transform) — искать без учёта регистра.
  const count = (s: string, re: RegExp) => (s.toLowerCase().match(re) ?? []).length;
  const bodyText = await page.locator('body').innerText();
  const presetsView = count(bodyText, /пресеты для роли/g);
  const sectionsView = count(bodyText, /секции промпта/g);
  console.log('T6 визитка роли: «Пресеты для роли»:', presetsView, '| «Секции промпта»:', sectionsView);
  await page.screenshot({ path: `${SHOTS}/qa3-t6-role-view.png`, fullPage: true });
  expect.soft(presetsView, 'заголовка «Пресеты для роли» в визитке роли нет (showTitle=false)').toBe(0);
  expect.soft(sectionsView, '«Секции промпта» не дублируется').toBeLessThanOrEqual(1);

  // Настройка роли (правка): та же проверка в режиме edit.
  await page.getByRole('button', { name: 'Редактировать' }).first().click({ timeout: 15000 });
  await page.waitForTimeout(3000);
  const editText = await page.locator('body').innerText();
  const presetsEdit = count(editText, /пресеты для роли/g);
  const sectionsEdit = count(editText, /секции промпта/g);
  console.log('T6 настройка роли: «Пресеты для роли»:', presetsEdit, '| «Секции промпта»:', sectionsEdit);
  await page.screenshot({ path: `${SHOTS}/qa3-t6-role-edit.png`, fullPage: true });
  expect.soft(presetsEdit, 'заголовка «Пресеты для роли» в настройке роли нет (showTitle=false)').toBe(0);
  expect.soft(sectionsEdit, '«Секции промпта» в настройке роли не дублируется').toBeLessThanOrEqual(1);
  console.log('T6 ошибки консоли:', JSON.stringify([...new Set(errs)]));
  await page.close();
  await ctx.close();
});
