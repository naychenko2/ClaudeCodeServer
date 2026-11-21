// Вкладка «LLM»: текстовые helper'ы (notReportedText), точные подписи статуса
// (statusPill/standDot/standHint — макет bonsai-toolbar §4) и рендер TextTab:
// переключатель движка (виден только при двух configured и над любым состоянием
// вкладки — со стендом, с карточкой сбоя и со скелетоном, дефект b129c182),
// сокрытие полей из `unsupported` (не заглушки, а ничего), состояние 'yielded'
// (нейтральный тон, не нули), карточка Video и «места сервера» вместо чатов, блок
// «Дежурство» (только у выбранного второй движок; без duty — пустого: блок сам
// возвращает null; без арбитра — строка «Арбитр не подключён»), совместная карточка
// «Кто сейчас на модели» второго стенда: чаты дорожки + места из duty.callsNow (F3),
// порядок вкладки v1 «Сначала ферма» (KPI → график → чаты → карты → «Дежурство» →
// закрытые точные метрики → облако) и само закрытие: по умолчанию закрыто, клик
// открывает полные «Скорость» и «Скорость ответа».
// Рендер — через linkedom (как tasks/acceptanceBlock.test.ts); ResizeObserver
// стабится: ThroughputChart меряет им свою ширину.

import { describe, expect, it, vi } from 'vitest';
import { parseHTML } from 'linkedom';
import { SP } from '../../lib/design';

const { window: win, document: doc } = parseHTML('<!doctype html><html><body><div id="root"></div></body></html>');
vi.stubGlobal('window', win);
vi.stubGlobal('document', doc);
// usePrefersReducedMotion → matchMedia; localStorage — общий кэш сторов
(win as unknown as { matchMedia: unknown }).matchMedia = () => ({
  matches: false, addEventListener() {}, removeEventListener() {},
});
vi.stubGlobal('localStorage', { getItem: () => null, setItem: () => {} });
vi.stubGlobal('navigator', { language: 'ru-RU', onLine: true, userAgent: 'node' });
vi.stubGlobal('HTMLElement', win.HTMLElement);
vi.stubGlobal('Node', win.Node);
vi.stubGlobal('Event', win.Event);
class FakeResizeObserver {
  observe() {} unobserve() {} disconnect() {}
}
vi.stubGlobal('ResizeObserver', FakeResizeObserver);
// WhoCard читает стор проектов (useAllProjects → fetch): в тесте — пустой список
vi.stubGlobal('fetch', () => Promise.resolve({ ok: true, json: async () => [] }));
(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true;

// Динамический импорт ПОСЛЕ стабов: react-dom читает window при загрузке
const { act, createElement } = await import('react');
const { createRoot } = await import('react-dom/client');
const { TextTab } = await import('./TextTab');
const { notReportedText, standDot, standHint, statusPill } = await import('./format');
const { mockEngineStats, mockLocalModelStats, mockNamedMixedStats, mockNamedVllmStats } = await import('./localModel.fixtures');
const { dutyWhoRows } = await import('./dutyFormat');
const ENGINES_BOTH = [
  { name: 'main' as const, title: 'Основная', configured: true },
  { name: 'gemma' as const, title: 'Gemma 4', configured: true },
];

const TAB_DEFAULTS = {
  isMobile: false, onOpenChat: () => {}, onRefetch: () => {}, onOpenHardware: () => {}, onOpenMedia: () => {},
  window: '5m', onWindowChange: () => {},
};

// Монтирует вкладку и оставляет дерево живым (для клика по сегменту).
function mountTab(props: Record<string, unknown>) {
  const host = doc.createElement('div');
  doc.body.appendChild(host);
  const root = createRoot(host);
  act(() => {
    root.render(createElement(TextTab, { ...TAB_DEFAULTS, ...props }));
  });
  return { host, root };
}

function renderTab(props: Record<string, unknown>): string {
  const { host, root } = mountTab(props);
  const html = host.innerHTML;
  act(() => { root.unmount(); });
  host.remove();
  return html;
}

// Клик по сегменту переключателя: linkedom'овский click() — настоящий Event с
// bubbles (linkedom/esm/html/element.js:76), React 18 слушает «click» на корне
// createRoot — всплытие доходит до его делегата.
function clickSegment(host: ReturnType<typeof mountTab>['host'], label: string): void {
  const btn = Array.from(host.querySelectorAll('button')).find(b => (b.textContent ?? '').trim() === label);
  if (!btn) throw new Error(`сегмент «${label}» не найден в разметке`);
  act(() => { btn.click(); });
}

// Разметка одной карточки: от её заголовка до первого закрывающего </section>.
// Якорь берётся с тегом: «>Нагрузка и кэш</h3>», «>Видеокарты</span>» — голый текст
// заголовка находит не тот блок: та же подстрока живёт в чужих строках (aria-label
// строки «Видеокарты» несёт её целиком), а соседний заголовок может быть её началом
// («Нагрузка» — начало «Нагрузка и кэш»). У заголовка с правым span («Сейчас на
// ферме» + «локально») хвост меняется по ширине, поэтому там якорь только
// открывающий — «>Сейчас на ферме» (R-Ф-3).
function sectionHtml(html: string, anchor: string): string | null {
  const from = html.indexOf(anchor);
  if (from < 0) return null;
  const to = html.indexOf('</section>', from);
  return html.slice(from, to < 0 ? html.length : to);
}

// Верхний отступ из inline-стиля элемента: явный margin-top или первое значение
// шортхенда margin; свойства нет — отступ 0 (для guard'а границы группы, Ф-5р).
function topMarginPx(style: string): number {
  const one = style.match(/(?:^|;)margin-top:\s*([^;]+)/);
  if (one) return parseFloat(one[1]) || 0;
  const all = style.match(/(?:^|;)margin:\s*([^;]+)/);
  if (!all) return 0;
  return parseFloat(all[1].trim().split(/\s+/)[0]) || 0;
}

// История с кэш-содержанием и вытеснениями: на ней видно и разницу main/gemma —
// у main поля рисуются, у gemma (unsupported из фикстуры) — скрыты целиком.
const T0 = Date.now();
function historyWithKv() {
  const h = [];
  for (let i = 0; i < 40; i++) {
    h.push({
      t: T0 - (39 - i) * 5000,
      genThroughput: 30 + i, waiting: 0, running: 1,
      kvUsage: 0.5, prefixHits: 10, prefixQueries: 20, ramHits: 2, ramQueries: 20,
      preemptions: i % 7 === 0 ? 2 : 0,
      genTokens: 100, itlSeconds: 3, itlCount: 30,
      prefillSeconds: 1, queueSeconds: 0.5, promptTokens: 500,
    });
  }
  return h;
}

describe('notReportedText — «Движок … это не сообщает»', () => {
  it('с именем движка — «Движок {имя} это не сообщает»', () => {
    expect(notReportedText('vLLM')).toBe('Движок vLLM это не сообщает');
    expect(notReportedText('  vllm  ')).toBe('Движок vllm это не сообщает');
  });
  it('без имени движка — «Движок этого не сообщает» (не «Движок движок»)', () => {
    expect(notReportedText(null)).toBe('Движок этого не сообщает');
    expect(notReportedText(undefined)).toBe('Движок этого не сообщает');
    expect(notReportedText('')).toBe('Движок этого не сообщает');
    expect(notReportedText('   ')).toBe('Движок этого не сообщает');
  });
});

describe('statusPill — точные подписи статуса (макет §4)', () => {
  it('обычные статусы — прежние подписи и тона', () => {
    expect(statusPill({ configured: true, status: 'working' })).toEqual({ label: 'Работает', tone: 'success' });
    expect(statusPill({ configured: true, status: 'busy' })).toEqual({ label: 'Занята', tone: 'warning' });
    expect(statusPill({ configured: true, status: 'down' })).toEqual({ label: 'Недоступна', tone: 'danger' });
    expect(statusPill({ configured: true, status: 'notConnected' })).toEqual({ label: 'Не подключена', tone: 'muted' });
  });
  it('yielded с причиной — «Уступила карту», нейтральный тон (не авария)', () => {
    const p = statusPill(mockEngineStats('yielded'));
    expect(p).toEqual({ label: 'Уступила карту', tone: 'muted' });
  });
  it('yielded без причин, с остатком паузы — «Ждёт возврата»', () => {
    const d = mockEngineStats('yielded');
    d.duty = { ...d.duty!, arbiter: { ...d.duty!.arbiter!, reasons: [], quietLeftS: 160 } };
    expect(statusPill(d)).toEqual({ label: 'Ждёт возврата', tone: 'muted' });
  });
  it('starting — «Загружается», waiting-vram — «Ждёт память карты» (warning: память сама не освободится)', () => {
    expect(statusPill(mockEngineStats('starting'))).toEqual({ label: 'Загружается', tone: 'muted' });
    expect(statusPill(mockEngineStats('waiting-vram'))).toEqual({ label: 'Ждёт память карты', tone: 'warning' });
  });
  it('без арбитра/без причин — групповая подпись (Б3b: без арбитра yielded не ставится)', () => {
    const d = mockEngineStats('yielded');
    d.duty = { ...d.duty!, arbiter: null };
    expect(statusPill(d).label).toBe('Уступила карту');
    expect(statusPill({ configured: true, status: 'yielded' }).label).toBe('Уступила карту');
  });
});

describe('standDot / standHint — тон точки и подсказка (макет §4)', () => {
  it('уступка — muted; память и молчание стенда — warning; работа — точки нет', () => {
    expect(standDot(mockEngineStats('yielded'))).toBe('muted');
    expect(standDot(mockEngineStats('starting'))).toBe('muted');
    expect(standDot(mockEngineStats('waiting-vram'))).toBe('warning');
    expect(standDot(mockEngineStats('unknown'))).toBe('warning');
    expect(standDot(mockEngineStats('ready'))).toBeNull();
  });
  it('подсказки — по состоянию; у работающего стенда их нет', () => {
    // фикстура 'yielded' имеет reasons=[comfy video] — короткая форма «видео» (таблица KINDS макета)
    expect(standHint(mockEngineStats('yielded'))).toBe('Модель уступила карту: видео');
    expect(standHint(mockEngineStats('returning'))).toBe('Модель вернётся через 2:40');
    const zero = mockEngineStats('returning');
    zero.duty = { ...zero.duty!, arbiter: { ...zero.duty!.arbiter!, quietLeftS: 0 } };
    expect(standHint(zero)).toBe('Модель вот-вот вернётся');
    expect(standHint(mockEngineStats('starting'))).toBe('Модель загружается');
    expect(standHint(mockEngineStats('waiting-vram'))).toBe('Модель ждёт память карты');
    expect(standHint(mockEngineStats('unknown'))).toBe('Модель недоступна');
    expect(standHint(mockEngineStats('ready'))).toBeNull();
  });
});

describe('переключатель «Основная · второй движок» (c1)', () => {
  const data = mockLocalModelStats('working');
  it('два движка — переключатель есть, выбранный сегмент aria-pressed', () => {
    const html = renderTab({ data, engines: ENGINES_BOTH, engine: 'main', onEngineChange: () => {} });
    expect(html).toContain('Основная');
    expect(html).toContain('Gemma 4');
    expect(html).toContain('aria-pressed="true"');
  });
  // Сегменты переключателя подписаны titles из /engines («Основная», «второй движок»);
  // ни одного из этих слов в стенде нет — их отсутствие и есть «переключателя нет»
  it('один движок — переключателя нет (ни одного сегмента)', () => {
    const html = renderTab({ data, engines: [ENGINES_BOTH[0]], engine: 'main', onEngineChange: () => {} });
    expect(html).not.toContain('Основная');
    expect(html).not.toContain('Gemma 4');
  });
  it('второй движок не настроен — переключателя нет', () => {
    const html = renderTab({
      data, engine: 'main', onEngineChange: () => {},
      engines: [ENGINES_BOTH[0], { ...ENGINES_BOTH[1], configured: false }],
    });
    expect(html).not.toContain('Основная');
    expect(html).not.toContain('Gemma 4');
  });
  // Точка и подсказка — по снимку ВЫБРАННОГО движка: data — снимок текущего стенда,
  // состояние второй движок с основного стенда неизвестно (Ф6 — из useLocalModelWatcher).
  // Раньше тест подставлял невозможную пару (main + снимок второй движок); теперь — реальные.
  it('основной стенд со своим снимком (down) — на сегменте «второй движок» нет точки и подсказки', () => {
    const html = renderTab({
      data: mockLocalModelStats('down'), engines: ENGINES_BOTH, engine: 'main', onEngineChange: () => {},
    });
    // сегмент есть, но пустой: ни title-подсказки, ни 6-пиксельной точки
    expect(html).toContain('Gemma 4');
    expect(html).not.toContain('Gemma 4 —');
    expect(html).not.toContain('width:6px');
  });
  it('выбран «второй движок» и его снимок (yielded) — точка и подсказка на сегменте', () => {
    const html = renderTab({
      data: mockEngineStats('yielded'), engines: ENGINES_BOTH, engine: 'gemma', onEngineChange: () => {},
    });
    expect(html).toContain('Gemma 4 — уступила карту');
    expect(html).toContain('width:6px');
  });
});

describe('подстрока под статусом (макет §3, .html:414)', () => {
  it('второй движок — «llama-server · Video · обновлено …», без «карта/карт, TP=»', () => {
    const html = renderTab({
      data: { ...mockEngineStats('ready'), history: historyWithKv() }, engine: 'gemma',
    });
    expect(html).toContain('llama-server · Video ·');
    expect(html).toContain('обновлено');
    expect(html).not.toContain('TP=');
  });
  it('main — прежняя «N карты, TP=N», без «Video»', () => {
    const html = renderTab({ data: mockLocalModelStats('working') });
    // engine — lowercase-тип с wire (бэк нормализует: LocalModelStatsPoller ToLowerInvariant)
    expect(html).toContain('vllm · 2 карты, TP=2');
    expect(html).not.toContain('Video');
  });
  it('main с подписями из Stats — движок и раскладка вместо engine и «TP=N», модель — из model', () => {
    const html = renderTab({
      data: {
        ...mockLocalModelStats('working'),
        engine: 'vllm', engineLabel: 'Strata 0.2.7', topology: 'разрез слоёв 26/22',
        model: 'Qwen3.8-Flash-Next IQ3_XXS',
      },
    });
    expect(html).toContain('Strata 0.2.7 · 2 карты, разрез слоёв 26/22');
    expect(html).toContain('Qwen3.8-Flash-Next IQ3_XXS');
    expect(html).not.toContain('TP=');
    expect(html).not.toContain('vllm ·');
  });
});

describe('поля из unsupported не рендерятся (c2)', () => {
  it('main с той же историей — кэш, вытеснения и время агентов на месте', () => {
    const html = renderTab({ data: { ...mockLocalModelStats('busy'), history: historyWithKv() } });
    expect(html).toContain('Нагрузка и кэш');
    expect(html).toContain('Кэш, %');
    expect(html).toContain('Вытеснения');
    expect(html).toContain('Куда уходит время агентов');
    expect(html).not.toContain('ступеньками');
  });
  it('фикстура второй движок с той же историей — 0 блоков KV/RAM-тира/вытеснений; карточка «Нагрузка» с подписью про ступеньки', () => {
    const html = renderTab({
      data: { ...mockEngineStats('ready'), history: historyWithKv() }, engine: 'gemma',
    });
    expect(html).not.toContain('Кэш, %');
    expect(html).not.toContain('Вытеснения');
    expect(html).not.toContain('Куда уходит время агентов');
    expect(html).not.toContain('Движок llama-server это не сообщает');
    expect(html).toContain('>Нагрузка</h3>');
    expect(html).toContain('ступеньками');
  });
  it('main с unsupported сырыми именами метрик vLLM — ничего не скрыто: блоки на месте', () => {
    // Сокрытие идёт по ИМЕНАМ ПОЛЕЙ снимка (KvUsagePerc/Preemptions/QueueTimeSum —
    // ThroughputChart.tsx:919, TextTab.kvHidden), а не по сырым именам метрик движка:
    // незнакомые строки не должны прятать ни одного блока
    const html = renderTab({
      data: {
        ...mockLocalModelStats('busy'), history: historyWithKv(),
        unsupported: ['vllm:kv_cache_usage_perc', 'vllm:num_preemptions_total', 'vllm:request_queue_time_seconds_sum'],
      },
    });
    expect(html).toContain('Нагрузка и кэш');
    expect(html).toContain('Кэш, %');
    expect(html).toContain('Вытеснения');
    expect(html).toContain('Куда уходит время агентов');
  });
});

describe('состояние «уступила карту» (макет §4)', () => {
  it('нейтральный баннер; KPI и «Скорость» — «нет данных», не нули и не красное', () => {
    const html = renderTab({ data: mockEngineStats('yielded'), engine: 'gemma' });
    expect(html).toContain('Уступила карту');
    expect(html).toContain('Модель уступила карту — это по плану.');
    // KPI выгруженной модели: все четыре ячейки «нет данных» — ни нулей её снимка
    // (running/waiting = 0), ни живых скоростей фикстуры (genPerStream = 21).
    // Спека «Состояния всех вариантов», Yielded: «KPI … пишут отсутствие данных, а не нули».
    const kpi = sectionHtml(html, '>Сейчас на ферме') ?? '';
    expect(kpi).not.toBe('');
    expect(kpi.split('нет данных').length - 1).toBe(4);
    // «ни нулей, ни живых скоростей» — по тексту самих значений. По сырой innerHTML
    // такие проверки пусты: единицы скорости — отдельный span (FarmKpiCard.tsx:61),
    // и «21 ток/с» в разметке нет даже при живом числе (тот же вывод, что R-Ф-2 п.3).
    // Клетки — прямые дети грида, значение — первый его ребёнок (как cells() в
    // FarmKpiCard.test.ts:47): «нет данных» в каждой, ни нуля, ни живой скорости.
    const { document: d } = parseHTML(`<!doctype html><body>${html}</body>`);
    const h3 = Array.from(d.querySelectorAll('h3')).find(h => (h.textContent ?? '').includes('Сейчас на ферме'));
    expect(h3).toBeTruthy();
    const cells = Array.from(h3!.parentElement?.querySelectorAll('div[style*="grid-template-columns"] > div') ?? []);
    expect(cells.length).toBe(4);
    for (const c of cells) {
      expect(c.firstElementChild?.textContent ?? '').toContain('нет данных');
    }
    // «Скорость» и «Скорость ответа» — под закрытием: тело смонтировано и в закрытом
    // состоянии (display:none), поэтому их спокойная строка по-прежнему в разметке
    expect(html).toContain('Нет данных, пока модель выгружена.');
    // уступка не приглушается (приглушение — только авария)
    expect(html).not.toContain('grayscale');
  });
  it('«Ждёт память карты» — свой тон и свой текст баннера', () => {
    const html = renderTab({ data: mockEngineStats('waiting-vram'), engine: 'gemma' });
    expect(html).toContain('Ждёт память карты');
    expect(html).toContain('Модель ждёт память карты.');
  });
  it('авария без выгрузки (unknown → down) — текст про аварию, не про уступку', () => {
    const html = renderTab({ data: mockEngineStats('unknown'), engine: 'gemma' });
    expect(html).toContain('Модель не отвечает');
    expect(html).toContain('а арбитр её не выгружал');
    expect(html).toContain('Это авария, не уступка');
  });
});

describe('карточка Video и «места сервера» (стенд второй движок)', () => {
  it('«Видеокарта · Video» с чипом «общая с ComfyUI», полоса памяти и KPI', () => {
    const html = renderTab({ data: mockEngineStats('ready'), engine: 'gemma' });
    expect(html).toContain('Видеокарта · Video');
    expect(html).toContain('общая с ComfyUI');
    expect(html).toContain('8/24 ГБ');
    expect(html).not.toContain('Стенды');
  });
  it('«Кто сейчас» — места со звеном и длительностью; строки не кнопки', () => {
    const html = renderTab({ data: mockEngineStats('yielded'), engine: 'gemma' });
    expect(html).toContain('Классификация задачи');
    expect(html).toContain('MiniMax · страховка');
    expect(html).toContain('2,1 с');
    // совместная карточка F3: строки мест по-прежнему не кнопки, и их поясняет подпись
    expect(html).toContain('Места в списке — это вызовы сервера');
    const { window: w2, document: d2 } = parseHTML(`<!doctype html><body>${html}</body>`);
    const buttons = Array.from(d2.querySelectorAll('button')).map(b => b.textContent ?? '');
    expect(buttons.some(t => t.includes('Классификация задачи'))).toBe(false);
  });
  it('длительность ждущего вызова и «ждёт в очереди» — из dutyFormat', () => {
    const rows = dutyWhoRows([{ place: 'Сводка хода', link: null, durationS: 0.3 }]);
    expect(rows[0].linkText).toBe('ждёт в очереди');
    expect(rows[0].queued).toBe(true);
  });
});

describe('подпись «Скорость» (макет §3)', () => {
  // «с ожиданием» / «без ожидания» — по типу движка из снимка (data.engine), а не по
  // имени стенда: vLLM бывает и main, и именованным (MetricsEngine 'vllm');
  // чистое время чтения — только у llama-server.
  it('main — «с ожиданием» (prompt-время vLLM включает очередь)', () => {
    const html = renderTab({ data: mockLocalModelStats('working') });
    expect(html).toContain('чтение промпта с ожиданием');
  });
  // llm-F2 (docs/features/llm-answer-speed-frontend.md, §3): KPI «на запрос» переименован
  // в «пока пишет» и подсказан — с облачными оценками сверяют «Скорость ответа», не это.
  it('KPI генерации — «скорость генерации (пока пишет)» с подсказкой, прежнего имени нет', () => {
    const html = renderTab({ data: mockLocalModelStats('working') });
    expect(html).toContain('скорость генерации (пока пишет), ток/с, за последнюю минуту');
    expect(html).not.toContain('генерация на запрос');
    expect(html).toContain('OpenRouter сравнивается');
  });
  it('второй движок llama-server — без «с ожиданием» (у llama-server это чистое время чтения)', () => {
    const html = renderTab({ data: mockEngineStats('ready'), engine: 'gemma' });
    expect(html).toContain('чтение промпта, ток/с');
    expect(html).not.toContain('с ожиданием');
  });
});

// F1: «именованный стенд» и «семантика llama-server» — разные вещи. Именованный движок
// на Video бывает и vLLM (LocalEngines:<name>:MetricsEngine): его prompt-время включает
// очередь (нужно «с ожиданием»), ступенчатых счётчиков нет, KV и prefix-попадания
// сообщаются (карточка «Нагрузка и кэш» открыта). Мутация «вернуть условие named в
// подписи чтения промпта» краснит первый тест; «вернуть named в условии ступенек» — второй.
describe('именованный vLLM-стенд: подписи и кэш по типу движка (F1)', () => {
  it('vLLM на Video — «с ожиданием» есть, ступенек нет, карточка «Нагрузка и кэш» открыта', () => {
    const html = renderTab({ data: { ...mockNamedVllmStats(), history: historyWithKv() }, engine: 'gemma' });
    expect(html).toContain('чтение промпта с ожиданием');
    expect(html).not.toContain('чтение промпта, ток/с');
    expect(html).not.toContain('ступеньками');
    expect(html).toContain('Нагрузка и кэш');
    expect(html).toContain('Кэш, %');
  });
  it('тот же vLLM с KvUsagePerc в unsupported — «Нагрузка» без ступеньки: сокрытие по unsupported, как у main', () => {
    // Состояние искусственное ровно затем, чтобы проверить УСЛОВИЕ: сокрытие — по
    // unsupported (kvHidden), а не по named; подпись ступеньки — по типу движка, а не
    // по named (у vLLM её нет даже при скрытом KV)
    const d = { ...mockNamedVllmStats(), history: historyWithKv(), unsupported: ['KvUsagePerc'] };
    const html = renderTab({ data: d, engine: 'gemma' });
    expect(html).toContain('>Нагрузка</h3>');
    expect(html).not.toContain('Нагрузка и кэш');
    expect(html).not.toContain('Кэш, %');
    expect(html).not.toContain('ступеньками');
  });
});

// F3: «Кто сейчас на модели» второго стенда — одна карточка: чаты своей дорожки
// (свой — кнопка, клик зовёт onOpen(sessionId); чужой — без перехода) + места из
// duty.callsNow (без перехода) + «прочие». Счётчик = чаты + места + прочие запросы.
// main не затронут: его WhoCard не читает duty и не передаёт места в whoRows
// (инвариант: чат на стенде X — только во вкладке X). Мутация «счётчик без мест»
// краснит ровно первый тест.
describe('«Кто сейчас на модели»: чаты и места в одной карточке (named, F3)', () => {
  const data = mockNamedMixedStats();

  // Счётчик из шапки карточки: span-сосед h3 с заголовком
  function whoCardCounter(html: string): string | null {
    const { document: d } = parseHTML(`<!doctype html><body>${html}</body>`);
    const h3 = Array.from(d.querySelectorAll('h3')).find(h => (h.textContent ?? '').trim() === 'Кто сейчас на модели');
    const span = h3?.parentElement?.querySelector('span');
    return span?.textContent?.trim() ?? null;
  }

  it('счётчик в шапке = чаты + места: 1 свой + 1 чужой + 1 место = 3', () => {
    expect(whoCardCounter(renderTab({ data, engine: 'gemma' }))).toBe('3');
  });

  it('свой чат — кнопка, клик по ней зовёт onOpen(sessionId)', () => {
    const opened: string[] = [];
    const { host, root } = mountTab({ data, engine: 'gemma', onOpenChat: (id: string) => opened.push(id) });
    const btn = Array.from(host.querySelectorAll('button')).find(b => (b.textContent ?? '').includes('Ревью Video-стенда'));
    expect(btn).toBeTruthy();
    act(() => btn!.click());
    expect(opened).toEqual(['s-named-1']);
    act(() => { root.unmount(); });
    host.remove();
  });

  it('чужой чат и место — не кнопки; место подписано звеном, длительностью и пояснением', () => {
    const html = renderTab({ data, engine: 'gemma' });
    const { document: d } = parseHTML(`<!doctype html><body>${html}</body>`);
    const buttons = Array.from(d.querySelectorAll('button')).map(b => b.textContent ?? '');
    expect(buttons.some(t => t.includes('Чат другого пользователя'))).toBe(false);
    expect(buttons.some(t => t.includes('Заголовок чата'))).toBe(false);
    // строка места: звено цепочки и длительность — тот же маппинг, что «Дежурство»
    expect(html).toContain('Заголовок чата');
    expect(html).toContain('локальная');
    expect(html).toContain('1,4 с');
    expect(html).toContain('Места в списке — это вызовы сервера');
  });

  it('порядок строк: чаты, затем места (порядок whoRows)', () => {
    const html = renderTab({ data, engine: 'gemma' });
    expect(html.indexOf('Ревью Video-стенда')).toBeLessThan(html.indexOf('Чат другого пользователя'));
    expect(html.indexOf('Чат другого пользователя')).toBeLessThan(html.indexOf('Заголовок чата'));
  });

  it('мобила: строка своего чата ≥ 40 px по высоте (44 на 360)', () => {
    const html = renderTab({ data, engine: 'gemma', isMobile: true });
    const { document: d } = parseHTML(`<!doctype html><body>${html}</body>`);
    const btn = Array.from(d.querySelectorAll('button')).find(b => (b.textContent ?? '').includes('Ревью Video-стенда'));
    expect((btn?.getAttribute('style') ?? '')).toContain('min-height:44px');
  });
});

describe('блок «Дежурство» в стенде (интеграция, Ф5)', () => {
  it('main — «Дежурства» нет, даже когда оба движка настроены', () => {
    const html = renderTab({
      data: mockLocalModelStats('working'), engines: ENGINES_BOTH, engine: 'main', onEngineChange: () => {},
    });
    expect(html).not.toContain('Дежурство');
    expect(html).not.toContain('Цепочка');
  });
  it('main с duty в снимке — «Дежурства» нет (условие движка, а не наличия duty)', () => {
    // В реальном API main duty не приносит — пара искусственная ровно затем, чтобы
    // проверить условие движка: без него блок появился бы на любом снимке с duty
    const html = renderTab({
      data: { ...mockLocalModelStats('working'), duty: mockEngineStats('yielded').duty },
      engines: ENGINES_BOTH, engine: 'main', onEngineChange: () => {},
    });
    expect(html).not.toContain('>Дежурство</h3>');
    expect(html).not.toContain('Цепочка');
  });
  it('второй движок без duty — блока нет (контракт DutyBlock: при duty === null он null)', () => {
    const d = { ...mockEngineStats('ready') };
    delete d.duty;
    const html = renderTab({ data: d, engine: 'gemma' });
    expect(html).not.toContain('Дежурство');
  });
  it('второй движок без арбитра — блок с «Арбитр не подключён» (строка самого блока)', () => {
    // Состояние 'no-arbiter' было покрыто только API-тестом (statusPill); здесь —
    // строка самого блока: движок без StateUrl остаётся видимым, но без причин и возврата
    const html = renderTab({ data: mockEngineStats('no-arbiter'), engine: 'gemma' });
    expect(html).toContain('>Дежурство</h3>');
    expect(html).toContain('Арбитр не подключён: почему уступила карта и когда модель вернётся — неизвестно.');
  });
  it('второй движок с duty — «Дежурство» есть и стоит последним блоком', () => {
    const html = renderTab({ data: mockEngineStats('yielded'), engine: 'gemma' });
    // заголовок именно карточки (h3): слово «Дежурство» есть и в тексте баннера
    // уступки выше — по одному ему наличие блока не проверить
    expect(html).toContain('>Дежурство</h3>');
    expect(html).toContain('видео H3'); // причина — задание ComfyUI с этого сервера
    expect(html).toContain('→ Медиа'); // known:true + onOpenMedia от панели — переход есть
    expect(html).toContain('Цепочка');
    expect(html).toContain('Ответы мест за час');
    expect(html).toContain('Уступила · вернулась');
    expect(html).toContain('14 · 14');
    expect(html).toContain('карта целиком'); // чип режима exclusive
    // последним блоком — после заголовка «Кто сейчас на модели» (макет §3)
    expect(html.lastIndexOf('>Дежурство</h3>')).toBeGreaterThan(html.lastIndexOf('>Кто сейчас на модели</h3>'));
  });
});

// Ф10: нет данных из-за сбоя поллера — пустое тело объяснён карточкой. Мутация
// «убрать ветку NoConnection» краснит первый тест; «не различать offline/failed»
// — второй; «переставить скелетон раньше карточки» — третий.
describe('нет данных при сбое — объяснение вместо пустого тела (Ф10)', () => {
  it('offline — «Нет связи с сервером», без чисел как текущих', () => {
    const html = renderTab({ data: null, loading: false, error: 'offline' });
    expect(html).toContain('Нет связи с сервером');
    expect(html).toContain('Данные появятся, как только связь вернётся');
    // ни одного блока стенда: карточка одна (подстрока статуса — «обновлено …»)
    expect(html).not.toContain('Кто сейчас');
    expect(html).not.toContain('обновлено');
  });
  it('failed — «Не удалось получить данные. Повторим автоматически.», не «Нет связи»', () => {
    const html = renderTab({ data: null, loading: false, error: 'failed' });
    expect(html).toContain('Не удалось получить данные');
    expect(html).toContain('Повторим автоматически');
    expect(html).not.toContain('Нет связи с сервером');
  });
  it('сбой и параллельный повторный опрос (loading) — карточка, не мигающий скелетон', () => {
    const html = renderTab({ data: null, loading: true, error: 'offline' });
    expect(html).toContain('Нет связи с сервером');
    // скелетон — это пульс «ccs-lm-skeleton»; у карточки его нет (aria-hidden
    // не маркер: lucide-иконки несут его сами)
    expect(html).not.toContain('ccs-lm-skeleton');
  });
  // Ревью Ф10 (Глеб): карточка — живое состояние вкладки, её смена должна
  // доходить до скринридера. Мутация «убрать обёртку role=status» краснит.
  it('карточка сбоя — в обёртке role="status" aria-live="polite"', () => {
    const html = renderTab({ data: null, loading: false, error: 'offline' });
    expect(html).toContain('role="status"');
    expect(html).toContain('aria-live="polite"');
  });
  it('первое открытие без причины — прежний скелетон; без данных и без error — пусто', () => {
    const skel = renderTab({ data: null, loading: true });
    expect(skel).not.toContain('Нет связи с сервером');
    expect(skel).toContain('aria-hidden'); // скелетон — прежнее поведение
    expect(renderTab({ data: null, loading: false })).not.toContain('Нет связи с сервером');
  });
});

// Дефект b129c182 (повторная проверка Веры): карточка сбоя занимала всю вкладку, и
// вместе с ней исчезал переключатель «Основная · второй движок» — при упавшем одном стенде
// уйти на другой можно было только через F5. Список движков живёт своим опросом
// (useLocalModelEngines) и не зависит от снимка статистики, поэтому переключатель
// рисётся НАД карточкой сбоя и НАД скелетоном.
// Мутация «вернуть engineSwitch за ранние return» краснит первый тест.
describe('переключатель над состоянием без данных (дефект b129c182)', () => {
  it('сбой и два движка — переключатель над карточкой и работает: клик по «второй движок» зовёт onEngineChange', () => {
    const chosen: string[] = [];
    const { host, root } = mountTab({
      data: null, loading: false, error: 'offline',
      engines: ENGINES_BOTH, engine: 'main', onEngineChange: (e: string) => { chosen.push(e); },
    });
    const html = host.innerHTML;
    expect(html).toContain('Нет связи с сервером');
    expect(html).toContain('Основная');
    expect(html).toContain('Gemma 4');
    expect(html).toContain('aria-pressed="true"'); // выбранный сегмент помечен
    // порядок: переключатель — первой строкой вкладки, карточка под ним
    expect(html.indexOf('Основная')).toBeLessThan(html.indexOf('Нет связи с сервером'));
    clickSegment(host, 'Gemma 4');
    expect(chosen).toEqual(['gemma']);
    act(() => { root.unmount(); });
    host.remove();
  });
  it('скелетон (данных нет, опрос идёт) и два движка — переключатель над ним, карточки сбоя нет', () => {
    const html = renderTab({
      data: null, loading: true, engines: ENGINES_BOTH, engine: 'main', onEngineChange: () => {},
    });
    expect(html).toContain('Основная');
    expect(html).toContain('Gemma 4');
    expect(html).toContain('ccs-lm-skeleton'); // скелетон на месте
    expect(html).not.toContain('Нет связи с сервером');
    expect(html.indexOf('Основная')).toBeLessThan(html.indexOf('ccs-lm-skeleton'));
  });
  it('один движок — переключателя нет ни в сбое, ни в скелетоне, ни со стендом', () => {
    const states = [
      { data: null, loading: false, error: 'offline' },
      { data: null, loading: true },
      { data: mockLocalModelStats('working') },
    ];
    for (const s of states) {
      const html = renderTab({ ...s, engines: [ENGINES_BOTH[0]], engine: 'main', onEngineChange: () => {} });
      expect(html).not.toContain('Основная');
      expect(html).not.toContain('Gemma 4');
    }
  });
  // Точки и подсказки на сегментах без снимка нет: состояние другого стенда по
  // чужому снимку не знать (тот же инвариант Ф6), выдумывать его нечем.
  it('без данных — на сегментах нет точки и подсказки', () => {
    const html = renderTab({
      data: null, loading: false, error: 'failed', engines: ENGINES_BOTH, engine: 'gemma', onEngineChange: () => {},
    });
    expect(html).toContain('Gemma 4'); // сам переключатель есть
    expect(html).not.toContain('width:6px');
    expect(html).not.toContain('Gemma 4 —');
  });
});

// llm-F2 (docs/features/llm-answer-speed-frontend.md): карточка «Скорость ответа» —
// сквозная скорость, медианы по запросам и время до первого токена по окнам 5m/1h из
// блока answerSpeed (контракт — docs/features/llm-answer-speed-spec.md). Окна берутся
// по КЛЮЧУ ответа, а не по индексу; нет данных — «нет данных», а не нули.
// Мутации: «взять windows[0]/[1] по индексу» краснит «окна — по ключу»; «0 вместо null» —
// «нет значений» и «нет поля»; «не группировать TTFT по окнам» — «TTFT сгруппирован».
describe('карточка «Скорость ответа» (llm-F2)', () => {
  const ANSWER_SPEED = {
    windows: [
      { window: '5m', endToEndTps: 42.1, medianTps: 46.0, medianTtftSec: 6.4, p90TtftSec: 14.2, requests: 402 },
      { window: '1h', endToEndTps: 41.3, medianTps: 45.1, medianTtftSec: 6.2, p90TtftSec: 13.8, requests: 4804 },
    ],
    minTokensForMedian: 32,
  };
  const withAnswer = () => ({ ...mockLocalModelStats('working'), answerSpeed: ANSWER_SPEED });
  const pos = (html: string, s: string) => html.indexOf(s);
  // Бросок по отсутствующему якорю (R-Ф-8): без него отсутствие даёт −1 и сравнение
  // −1 < x проходит молча (та же ловушка, что описана в блоке «порядок вкладки»).
  function head(html: string, anchor: string): number {
    const i = html.indexOf(anchor);
    if (i < 0) throw new Error(`нет якоря «${anchor}» в разметке`);
    return i;
  }

  it('числа из ответа: сквозная, медианы и TTFT по обоим окнам, с подписями окон', () => {
    const html = renderTab({ data: withAnswer() });
    expect(html).toContain('>Скорость ответа</h3>');
    expect(html).toContain('42 ток/с');
    expect(html).toContain('41 ток/с');
    expect(html).toContain('46 ток/с');
    expect(html).toContain('45 ток/с');
    expect(html).toContain('6,4 с');
    expect(html).toContain('14,2 с');
    expect(html).toContain('6,2 с');
    expect(html).toContain('13,8 с');
    expect(html).toContain('за 5 минут');
    expect(html).toContain('за час');
    expect(html).toContain('запросов в окне: 402');
    expect(html).toContain('запросов в окне: 4804');
    expect(html).toContain('Время до первого токена');
  });

  // Ф-3: карточки переехали под закрытие «Точные метрики» — порядок
  // внутри него прежний («Скорость», затем сквозная), а само раскрытие — после графика
  // (макет v1 .html:194–205). Мутация «вернуть метрики над графиком» краснит.
  it('обе карточки — под закрытием «Точных метрик», оно — после графика', () => {
    const html = renderTab({ data: withAnswer() });
    expect(head(html, '>Скорость</h3>')).toBeLessThan(head(html, '>Скорость ответа</h3>'));
    // Якорь с тегом и почему он устойчив (R-Ф-8): та же фраза есть в подсказке облака
    // (CLOUD_SPEED_DEFINITION_HINT, format.ts:461-462), но там она обведена типографскими
    // кавычками — «…смотрите в раскрытии „Точные метрики“.», — поэтому «>Точные метрики</span>»
    // с ней не совпадёт: ни в title-атрибуте ячейки, ни в видимом span (перенос подсказки
    // в span — мутация, тест зелёный). Инвариант: подсказка облака не должна
    // попасть в текстовый узел, оканчивающийся на «Точные метрики», а якорь обязан
    // оставаться тегосодержащим.
    expect(head(html, '>Нагрузка и кэш</h3>')).toBeLessThan(head(html, '>Точные метрики</span>'));
    expect(head(html, '>Точные метрики</span>')).toBeLessThan(head(html, '>Скорость</h3>'));
  });

  it('окна — по ключу ответа, а не по порядку в нём: 5m остаётся заглавным', () => {
    const reversed = { ...ANSWER_SPEED, windows: [ANSWER_SPEED.windows[1], ANSWER_SPEED.windows[0]] };
    const html = renderTab({ data: { ...mockLocalModelStats('working'), answerSpeed: reversed } });
    expect(pos(html, '42 ток/с')).toBeLessThan(pos(html, '41 ток/с'));
    expect(pos(html, 'за 5 минут')).toBeLessThan(pos(html, 'за час'));
  });

  it('TTFT сгруппирован по окнам: числа одного окна идут подряд, 5m раньше 1h', () => {
    const html = renderTab({ data: withAnswer() });
    expect(pos(html, '6,4 с')).toBeLessThan(pos(html, '14,2 с'));
    expect(pos(html, '14,2 с')).toBeLessThan(pos(html, '6,2 с'));
    expect(pos(html, '6,2 с')).toBeLessThan(pos(html, '13,8 с'));
  });

  it('подсказки у чисел: что это, с чем сравнивать, порог медианы из ответа', () => {
    const html = renderTab({ data: withAnswer() });
    expect(html).toContain('title="сквозная скорость ответа');
    expect(html).toContain('Так же считают Artificial Analysis и OpenRouter');
    expect(html).toContain('не короче 32 токенов');
    expect(html).toContain('90-й перцентиль');
    // хвост ревью 9a059b29 + поправка Артёма (5987bf81): точка отсчёта названа прямо —
    // сборщик шлюза снимает начало до SendAsync (LlmGatewayEndpoints.cs:120–125;
    // :229–241 — передача уже снятого начала, а не снятие).
    expect(html).toContain('от отправки запроса шлюзом CCS до последнего токена');
  });

  it('нет поля answerSpeed (старый бэкенд) — спокойная «нет данных», не нули', () => {
    const html = renderTab({ data: mockLocalModelStats('working') });
    expect(html).toContain('>Скорость ответа</h3>');
    expect(html).toContain('Нет данных: сквозной скорости ещё не измерено.');
    // нулей нет ('>0' — начало числа: «1240 ток/с» из фикстуры под это не попадает)
    expect(html).not.toContain('>0 ток/с');
    expect(html).not.toContain('>0,0 с');
  });

  it('часть значений null — «нет данных» в своей ячейке, а не 0', () => {
    const d = {
      ...mockLocalModelStats('working'),
      answerSpeed: {
        windows: [
          { window: '5m', endToEndTps: 42.1, medianTps: null, medianTtftSec: null, p90TtftSec: null },
          { window: '1h', endToEndTps: null, medianTps: null, medianTtftSec: null, p90TtftSec: null },
        ],
      },
    };
    const html = renderTab({ data: d });
    expect(html).toContain('42 ток/с');
    expect(html).toContain('нет данных');
    expect(html).not.toContain('>0 ток/с');
    expect(html).not.toContain('>0,0 с');
  });

  // Хвосты ревью eb3bc6ec: подпись числа запросов не должна разъезжаться по склонению
  // («1 запросов», «21 запросов»). Форма нейтральная: число после двоеточия — одна на любой N.
  it('число запросов — одна нейтральная подпись при любом N (1, 21)', () => {
    const at = (n: number) => renderTab({
      data: {
        ...mockLocalModelStats('working'),
        answerSpeed: {
          windows: [
            { window: '5m', endToEndTps: 42.1, medianTps: null, medianTtftSec: null, p90TtftSec: null, requests: n },
            { window: '1h', endToEndTps: null, medianTps: null, medianTtftSec: null, p90TtftSec: null, requests: null },
          ],
        },
      },
    });
    expect(at(1)).toContain('запросов в окне: 1');
    expect(at(21)).toContain('запросов в окне: 21');
    expect(at(1)).not.toContain('1 запросов');
    expect(at(21)).not.toContain('21 запросов');
  });

  it('выгруженная модель — «нет данных» в том же спокойном тоне, что «Скорость»', () => {
    const d = { ...mockEngineStats('yielded'), answerSpeed: ANSWER_SPEED };
    const html = renderTab({ data: d, engine: 'gemma' });
    expect(html).toContain('>Скорость ответа</h3>');
    expect(html).toContain('Нет данных, пока модель выгружена.');
    expect(html).not.toContain('42 ток/с');
  });

  // Ф-5р (вердикт R-Ф-5): границу группы несёт отступ, а не рамка. Тело
  // DisclosureSection не даёт верхнего паддинга (0 SP.md SP.md), а снятая обёртка-грид
  // нёс gap/paddingTop — верхний отступ заголовка группы возвращает его (макет
  // .detail-body h4 { margin: 12px 0 6px }). Мутация «вернуть margin: 0» краснит.
  it('граница группы: заголовки «Скорость» и «Скорость ответа» отделены сверху ≥ SP.md', () => {
    const html = renderTab({ data: withAnswer() });
    for (const title of ['Скорость', 'Скорость ответа']) {
      const head = html.match(new RegExp(`<h3[^>]*style="([^"]*)"[^>]*>${title}<\\/h3>`));
      expect(head, `заголовок «${title}» с inline-стилем не найден`).toBeTruthy();
      expect(topMarginPx(head![1])).toBeGreaterThanOrEqual(SP.md);
    }
  });
});

// llm-F2 §4: формулу скорости в строке «Кто сейчас» не меняем — подсказка обязана
// называть и то, что в неё входит, и её область: ActiveChats отдаёт среднее по
// завершённым запросам текущего хода (LocalModelTurnMeter.cs:388, 465–472).
// Мутация «убрать title у числа» краснит первый тест; «сквозная скорость запроса»
// вместо среднего по ходу — тоже первый тест.
describe('подсказка скорости в строке «Кто сейчас на модели» (llm-F2)', () => {
  const HINT_PREFIX = 'title="средняя сквозная скорость по завершённым запросам этого хода';
  it('число генерации — с подсказкой про среднюю сквозную скорость по ходу', () => {
    const html = renderTab({ data: mockLocalModelStats('working') });
    expect(html).toContain(HINT_PREFIX);
    // Текст подсказки — по формуле бэка (SseUsageObserver.cs:167–178): от начала ответа
    // движка до последнего токена. Strata шлёт заголовки до ожидания слота и чтения
    // промпта — они в интервале; вне его только очередь CCS до отправки (Артём, 02a99fe4).
    expect(html).toContain('от начала ответа движка до последнего токена');
    expect(html).toContain('с ожиданием слота в движке и чтением промпта');
    expect(html).toContain('очередь CCS до отправки запроса не входит');
  });
  it('«читает» и «ждёт» — без числовой подсказки', () => {
    const d = { ...mockLocalModelStats('working'), chats: [{ own: true, sessionId: 's-1', title: 'Чат', phase: 'reading' as const }] };
    const html = renderTab({ data: d });
    expect(html).not.toContain(HINT_PREFIX);
  });
});

// Ф-3 (v1 «Сначала ферма»: спека §v1 строка 50, макет .html:169–214): порядок вкладки —
// KPI фермы → график → чаты → карты → «Дежурство» (только named) → закрытые точные
// метрики → облако последним. Мутации: «вернуть метрики над графиком» краснит первый
// тест; «поставить облако перед «Дежурством»» — первый и второй; «не поднимать display
// тела при открытии» — четвёртый.
describe('порядок вкладки v1 и закрытые точные метрики (Ф-3)', () => {
  const ANSWER = {
    windows: [
      { window: '5m', endToEndTps: 42.1, medianTps: 46.0, medianTtftSec: 6.4, p90TtftSec: 14.2, requests: 402 },
      { window: '1h', endToEndTps: 41.3, medianTps: 45.1, medianTtftSec: 6.2, p90TtftSec: 13.8, requests: 4804 },
    ],
    minTokensForMedian: 32,
  };
  const CLOUD = {
    from: '2026-11-19T18:51:00Z', to: '2026-11-20T18:51:00Z', minTokensForMedian: 32,
    models: [{ provider: 'claude', model: 'claude-sonnet-5-5', endToEndTps: 38.2, medianTps: 41.0, requests: 57, outputTokens: 412000, apiMs: 10785000 }],
  };
  // hardware — поле админа: без него строки-ссылки «Видеокарты» нет (ADR-030), а по
  // порядку v1 она обязана стоять между чатами и метриками
  const HW = {
    level: 'ok' as const,
    gpus: [
      { uuid: 'gpu-0', memTempC: 68, level: 'ok' as const, powerCap: false, thermal: false },
      { uuid: 'gpu-1', memTempC: 64, level: 'ok' as const, powerCap: false, thermal: false },
    ],
  };
  const data = () => ({ ...mockLocalModelStats('busy'), history: historyWithKv(), answerSpeed: ANSWER, hardware: HW });
  const cloud = { data: CLOUD, loading: false, forbidden: false };

  // Якорь с тегом (R-Ф-3): «>Видеокарты</span>», а не «Видеокарты» — голый текст
  // нашёл бы aria-label той же строки; «>Нагрузка и кэш</h3>», а не «Нагрузка» —
  // соседний заголовок может быть её началом. Бросок по отсутствующему якорю
  // обязателен: без него отсутствие даёт −1 и сравнение −1 < x проходит молча.
  function head(html: string, anchor: string): number {
    const i = html.indexOf(anchor);
    if (i < 0) throw new Error(`нет якоря «${anchor}» в разметке`);
    return i;
  }
  function card(html: string, anchor: string): string {
    const s = sectionHtml(html, anchor);
    if (s == null) throw new Error(`нет карточки «${anchor}» в разметке`);
    return s;
  }

  it('main: KPI → график → чаты → видеокарты → метрики → облако, без «Дежурства»', () => {
    const html = renderTab({ data: data(), cloudSpeed: cloud });
    const kpi = head(html, '>Сейчас на ферме');
    const chart = head(html, '>Нагрузка и кэш</h3>');
    const who = head(html, '>Кто сейчас на модели</h3>');
    const gpus = head(html, '>Видеокарты</span>');
    const metrics = head(html, '>Точные метрики</span>');
    const cloudCard = head(html, '>Сравнить с облаком</span>');
    expect(kpi).toBeLessThan(chart);
    expect(chart).toBeLessThan(who);
    expect(who).toBeLessThan(gpus);
    expect(gpus).toBeLessThan(metrics);
    expect(metrics).toBeLessThan(cloudCard);
    // «Дежурство» — только у второго движка (условие движка при перестановке сохранено)
    expect(html).not.toContain('>Дежурство</h3>');
  });

  it('named: VideoCard и «Дежурство» — последние локальные блоки перед метриками и облаком', () => {
    const html = renderTab({ data: mockEngineStats('yielded'), engine: 'gemma', cloudSpeed: cloud });
    const who = head(html, '>Кто сейчас на модели</h3>');
    const video = head(html, '>Видеокарта · Video</h3>');
    const duty = head(html, '>Дежурство</h3>');
    const metrics = head(html, '>Точные метрики</span>');
    const cloudCard = head(html, '>Сравнить с облаком</span>');
    expect(who).toBeLessThan(video);
    expect(video).toBeLessThan(duty);
    expect(duty).toBeLessThan(metrics);
    expect(metrics).toBeLessThan(cloudCard);
  });

  it('метрики закрыты по умолчанию: aria-expanded=false, тело display:none', () => {
    const html = renderTab({ data: data(), cloudSpeed: cloud });
    const { document: d } = parseHTML(`<!doctype html><body>${html}</body>`);
    const btn = Array.from(d.querySelectorAll('button')).find(b => (b.textContent ?? '').includes('Точные метрики'));
    expect(btn).toBeTruthy();
    expect(btn!.getAttribute('aria-expanded')).toBe('false');
    // приписка строки — полная, по решению Майи (21.11): смысл строки читается сам,
    // а не только в коротком заголовке
    expect(btn!.textContent ?? '').toContain('Локальная модель · TTFT · медианы');
    const body = d.getElementById(btn!.getAttribute('aria-controls') ?? '');
    expect(body).toBeTruthy();
    expect(body!.getAttribute('style') ?? '').toContain('display:none');
    // закрытая карточка KPI не дублирует то, что под закрытием: медианы и TTFT — только внутри
    expect(sectionHtml(html, '>Сейчас на ферме') ?? '').not.toContain('медиана по запросам');
  });

  it('клик открывает метрики полными: промпт, обе скорости, запросы, медианы и TTFT', () => {
    const { host, root } = mountTab({ data: data(), cloudSpeed: cloud });
    const btn = Array.from(host.querySelectorAll('button')).find(b => (b.textContent ?? '').includes('Точные метрики'));
    expect(btn).toBeTruthy();
    expect(btn!.getAttribute('aria-expanded')).toBe('false');
    const bodyId = btn!.getAttribute('aria-controls') ?? '';
    act(() => { btn!.click(); });
    expect(btn!.getAttribute('aria-expanded')).toBe('true');
    const body = doc.getElementById(bodyId);
    expect(body).toBeTruthy();
    // открыто: display:none снят (тело смонтировано и в закрытом состоянии — тот же
    // смысл, что в AccordionSection: закрытие не останавливает живущее в нём)
    expect(body!.getAttribute('style') ?? '').not.toContain('display:none');
    const after = host.innerHTML;
    const speed = card(after, '>Скорость</h3>');
    expect(speed).toContain('21 ток/с');
    expect(speed).toContain('860 ток/с');
    expect(speed).toContain('чтение промпта с ожиданием');
    expect(speed).not.toContain('нет данных');
    const answer = card(after, '>Скорость ответа</h3>');
    expect(answer).toContain('42 ток/с');
    expect(answer).toContain('41 ток/с');
    expect(answer).toContain('46 ток/с');
    expect(answer).toContain('45 ток/с');
    expect(answer).toContain('запросов в окне: 402');
    expect(answer).toContain('запросов в окне: 4804');
    expect(answer).toContain('6,4 с');
    expect(answer).toContain('14,2 с');
    expect(answer).toContain('6,2 с');
    expect(answer).toContain('13,8 с');
    act(() => { root.unmount(); });
    host.remove();
  });

  // Заголовок KPI-карточки — по его тексту, а не по тексту всей секции (R-Ф-3р):
  // «локальн…» входит в легитимные строки вкладки (приписка «Локальная модель · …»,
  // «локальной модели» в подсказках) и красил бы тест при целом
  // исправном инварианте.
  function kpiHead(html: string): Element {
    const { document: d } = parseHTML(`<!doctype html><body>${html}</body>`);
    const h = Array.from(d.querySelectorAll('h3')).find(x => (x.textContent ?? '').includes('Сейчас на ферме'));
    if (!h) throw new Error('заголовок «Сейчас на ферме» не найден в разметке');
    return h;
  }

  it('подпись «локально» — только на десктопе: через TextTab с isMobile=true её нет (Ф-3р)', () => {
    // Обвязка (вердикт R-Ф-3): сам FarmKpiCard прячет правую подпись по своему prop'у
    // (FarmKpiCard.tsx:97), но вкладка обязана передать isMobile — иначе десктопная
    // подпись рендерится и на мобиле. Инвариант проверяется рендером через TextTab,
    // а не напрямую через компонент: защищена обвязка, а не только компонент.
    const mobile = kpiHead(renderTab({ data: data(), isMobile: true }));
    expect(mobile.textContent ?? '').not.toContain('локальн');
    // десктоп: правый span — последний ребёнок заголовка (образец FarmKpiCard.test.ts:161)
    const desktop = kpiHead(renderTab({ data: data(), isMobile: false }));
    expect(desktop.lastElementChild?.textContent ?? '').toBe('локально');
  });
});
