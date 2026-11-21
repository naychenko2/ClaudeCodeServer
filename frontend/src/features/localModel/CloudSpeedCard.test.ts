// «Сравнить с облаком» (Ф-4): последней ЗАКРЫТОЙ строкой, полное тело после раскрытия,
// приписка «моделей: N · за сутки», пустое окно — статичная строка «нет ходов за сутки»,
// 403 — секции нет, скелетон — только внутри открытого тела. Состояние раскрытия —
// localStorage 'cc_local_llm_cloud_open' ('1' — открыто): сохранённое открытие,
// гонка двух кликов и приватный режим (бросающий getItem/setItem).
// Рендер — через linkedom, как в TextTab.test.ts.

import { beforeEach, describe, expect, it, vi } from 'vitest';
import { parseHTML } from 'linkedom';

const { window: win, document: doc } = parseHTML('<!doctype html><html><body><div id="root"></div></body></html>');
vi.stubGlobal('window', win);
vi.stubGlobal('document', doc);
(win as unknown as { matchMedia: unknown }).matchMedia = () => ({
  matches: false, addEventListener() {}, removeEventListener() {},
});
// Живой localStorage: тесты читают и пишут тот же ключ, что компонент (дефолт — пусто).
const CLOUD_KEY = 'cc_local_llm_cloud_open';
const store = new Map<string, string>();
const normalImpl = {
  getItem: (k: string) => (store.has(k) ? store.get(k) ?? null : null),
  setItem: (k: string, v: string) => { store.set(k, String(v)); },
};
let lsImpl = normalImpl;
vi.stubGlobal('localStorage', {
  getItem: (k: string) => lsImpl.getItem(k),
  setItem: (k: string, v: string) => lsImpl.setItem(k, v),
});
vi.stubGlobal('navigator', { language: 'ru-RU', onLine: true, userAgent: 'node' });
vi.stubGlobal('HTMLElement', win.HTMLElement);
vi.stubGlobal('Node', win.Node);
vi.stubGlobal('Event', win.Event);
class FakeResizeObserver {
  observe() {} unobserve() {} disconnect() {}
}
vi.stubGlobal('ResizeObserver', FakeResizeObserver);
vi.stubGlobal('fetch', () => Promise.resolve({ ok: true, json: async () => [] }));
(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true;

// Динамический импорт ПОСЛЕ стабов: react-dom читает window при загрузке
const { act, createElement } = await import('react');
const { createRoot } = await import('react-dom/client');
const { TextTab } = await import('./TextTab');
const { mockLocalModelStats } = await import('./localModel.fixtures');

const TAB_DEFAULTS = {
  isMobile: false, onOpenChat: () => {}, onRefetch: () => {}, onOpenHardware: () => {}, onOpenMedia: () => {},
  window: '5m', onWindowChange: () => {},
};

// from/to — фактическое окно снимка (ISO UTC), его задал бэкенд (хвост ревью aa8af844, п.1)
const CLOUD = {
  from: '2026-11-19T18:51:00Z',
  to: '2026-11-20T18:51:00Z',
  minTokensForMedian: 32,
  models: [
    { provider: 'claude', model: 'claude-sonnet-5-5', endToEndTps: 38.2, medianTps: 41.0, requests: 57, outputTokens: 412000, apiMs: 10785000 },
  ],
};

function cloudState(over: Record<string, unknown> = {}) {
  return { data: CLOUD, loading: false, forbidden: false, ...over } as never;
}

// Монтирует вкладку и оставляет дерево живым (для клика по голове раскрытия).
function mountTab(props: Record<string, unknown>) {
  const host = doc.createElement('div');
  doc.body.appendChild(host);
  const root = createRoot(host);
  act(() => {
    root.render(createElement(TextTab, { ...TAB_DEFAULTS, ...props } as never));
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

// Голова раскрытия облака — кнопка с названием строки (у статичной строки кнопки нет).
function cloudHead(host: ReturnType<typeof mountTab>['host']): Element {
  const btn = Array.from(host.querySelectorAll('button')).find(b => (b.textContent ?? '').includes('Сравнить с облаком'));
  if (!btn) throw new Error('голова «Сравнить с облаком» не найдена в разметке');
  return btn;
}

// Фрагмент облака: от его строки до конца вкладки — облако последней позицией (Ф-4),
// дальше его содержимого нет.
function cloudHtml(html: string): string {
  const from = html.indexOf('Сравнить с облаком');
  return from < 0 ? '' : html.slice(from);
}

beforeEach(() => {
  store.clear();
  lsImpl = normalImpl;
});

describe('закрытая строка «Сравнить с облаком» (Ф-4)', () => {
  const data = mockLocalModelStats('working');

  // Только фрагмент облака: «0 ток/с» встречается в соседних карточках
  // («1240 ток/с» — чтение промпта), и проверка по всему html ловила бы чужое число.
  function cloudOf(props: Record<string, unknown>): string {
    return cloudHtml(renderTab(props));
  }

  // Позиция якоря с проверяющим броском (R-Ф-4): сырой indexOf при исчезновении якоря
  // даёт −1, и сравнение −1 < x проходит молча — проверка порядка становится
  // пустой. Тот же образец, что head() в TextTab.test.ts:818. Якорь — с тегом.
  function headAt(html: string, anchor: string): number {
    const i = html.indexOf(anchor);
    if (i < 0) throw new Error(`нет якоря «${anchor}» в разметке`);
    return i;
  }

  it('по умолчанию закрыто: aria-expanded=false, прежнего заголовка карточки нет', () => {
    const { host, root } = mountTab({ data, cloudSpeed: cloudState() });
    expect(cloudHead(host).getAttribute('aria-expanded')).toBe('false');
    expect(host.innerHTML).not.toContain('Облачные модели: скорость ответа');
    act(() => { root.unmount(); });
    host.remove();
  });

  it('закрытая строка несёт «моделей: N · за сутки» — одна форма при любом N (1, 3)', () => {
    const at = (models: typeof CLOUD.models) => cloudOf({ data, cloudSpeed: cloudState({ data: { ...CLOUD, models } }) });
    expect(at(CLOUD.models)).toContain('моделей: 1 · за сутки');
    const three = [
      CLOUD.models[0],
      { ...CLOUD.models[0], model: 'gpt-5.2', provider: 'openai' },
      { ...CLOUD.models[0], model: 'gemini-3-pro', provider: 'google' },
    ];
    expect(at(three)).toContain('моделей: 3 · за сутки');
    // склоняемых форм нет ни для 1, ни для 3
    expect(at(CLOUD.models)).not.toContain('1 модель ·');
    expect(at(three)).not.toContain('3 модели ·');
  });

  it('полное тело после раскрытия: имя, провайдер, сквозная скорость, медиана и число ходов', () => {
    const { host, root } = mountTab({ data, cloudSpeed: cloudState() });
    act(() => { cloudHead(host).click(); });
    const html = host.innerHTML;
    expect(html).toContain('aria-expanded="true"');
    expect(html).toContain('claude-sonnet-5-5');
    expect(html).toContain('claude');
    expect(html).toContain('38 ток/с');
    expect(html).toContain('41 ток/с');
    expect(html).toContain('57');
    expect(html).toContain('ходов за сутки');
    act(() => { root.unmount(); });
    host.remove();
  });

  // Тело смонтировано и в закрытом состоянии (display:none в DisclosureSection):
  // закрытие не пересобирает содержимое — те же числа, что после раскрытия.
  it('тело смонтировано и в закрытом состоянии — содержимое не пересобирается при открытии', () => {
    const html = cloudOf({ data, cloudSpeed: cloudState() });
    expect(html).toContain('claude-sonnet-5-5');
    expect(html).toContain('38 ток/с');
  });

  // Хвост ревью aa8af844, п.1: окно — последние 24 часа, их считает бэкенд. Дата-пара
  // «за 19.11 — 20.11» обещала календарные сутки, которых никто не считал, — её нет.
  it('окно подписано честно: «за сутки», без даты-пары из ответа', () => {
    const html = cloudOf({ data, cloudSpeed: cloudState() });
    expect(html).toContain('за сутки');
    expect(html).not.toContain('за 19.11 — 20.11');
  });

  // Различие окон остаётся явным и в закрытой строке, и в теле (макет .html:212):
  // локальные метрики выше — 5 мин / 1 ч, облако — сессионные сутки.
  it('разница окон названа прямо: 5 минут и час против 24 часов', () => {
    const html = cloudOf({ data, cloudSpeed: cloudState() });
    expect(html).toContain('у локальных метрик выше — 5 минут и час');
    expect(html).toContain('последние 24 часа');
    expect(html).toContain('Сравниваем по смыслу, а не как точный забег');
  });

  // Хвост ревью aa8af844, п.4: бесплатный не-локальный поставщик остаётся, но строка
  // подписана поставщиком и моделью: одна модель у двух поставщиков — две строки.
  it('одна модель у двух поставщиков — две строки, каждая со своим поставщиком', () => {
    const html = cloudOf({
      data,
      cloudSpeed: cloudState({
        data: { ...CLOUD, models: [
          { provider: 'minimax', model: 'qwen3-coder', endToEndTps: 400, medianTps: 400, requests: 4, outputTokens: 4000, apiMs: 10000 },
          { provider: 'freellmapi', model: 'qwen3-coder', endToEndTps: 100, medianTps: 100, requests: 2, outputTokens: 1000, apiMs: 10000 },
        ] },
      }),
    });
    expect(html).toContain('>qwen3-coder</div>');
    expect(html).toContain('>minimax</div>');
    expect(html).toContain('>freellmapi</div>');
    expect(html).toContain('400 ток/с');
    expect(html).toContain('100 ток/с');
  });

  // Пустое окно — та же закрытая строка с подписью «нет ходов за сутки», без кнопки,
  // chevron и пустого тела (макет .html:239); нулевые скорости не рисовать.
  it('пустое окно — статичная закрытая строка «нет ходов за сутки», без кнопки и тела', () => {
    const html = cloudOf({ data, cloudSpeed: cloudState({ data: { ...CLOUD, models: [] } }) });
    expect(html).toContain('Сравнить с облаком');
    expect(html).toContain('нет ходов за сутки');
    // статичная строка: ни aria-expanded, ни chevron (svg), ни тела-региона
    expect(html).not.toContain('aria-expanded');
    expect(html).not.toContain('role="region"');
    expect(html).not.toContain('0 ток/с');
  });

  it('null-поля — «нет данных» в своей ячейке, а не 0', () => {
    const html = cloudOf({
      data,
      cloudSpeed: cloudState({ data: { ...CLOUD, models: [{ ...CLOUD.models[0], endToEndTps: null, medianTps: null }] } }),
    });
    expect(html).toContain('нет данных');
    expect(html).not.toContain('0 ток/с');
  });

  it('403 — секции нет вовсе', () => {
    const html = renderTab({ data, cloudSpeed: cloudState({ forbidden: true, data: null }) });
    expect(html).not.toContain('Сравнить с облаком');
  });

  it('без снимка (опрос не шёл) — секции нет', () => {
    const html = renderTab({ data });
    expect(html).not.toContain('Сравнить с облаком');
  });

  // Ф-4 (макет .html:206–214): облачное сравнение — последней ЗАКРЫТОЙ строкой,
  // после закрытого раскрытия локальных метрик. Мутация «поднять облако над метриками» краснит.
  it('секция — последней строкой: после локальных метрик под их закрытием', () => {
    const html = renderTab({ data, cloudSpeed: cloudState() });
    const metrics = headAt(html, '>Точные метрики локальной модели</span>');
    const speedAnswer = headAt(html, '>Скорость ответа</h3>');
    const cloud = headAt(html, '>Сравнить с облаком</span>');
    expect(metrics).toBeLessThan(cloud);
    expect(speedAnswer).toBeLessThan(cloud);
  });

  it('подсказки тела: определение, Artificial Analysis, не сравнимо с локальной, TTFT', () => {
    const html = cloudOf({ data, cloudSpeed: cloudState() });
    expect(html).toContain('сквозная скорость облачной модели за сутки');
    expect(html).toContain('duration_api_ms');
    expect(html).toContain('Определение сессионное');
    expect(html).toContain('Artificial Analysis');
    expect(html).toContain('не сравнимо');
    expect(html).toContain('время до первого токена у облачных моделей CCS не меряет');
    expect(html).toContain('не короче 32 токенов');
  });

  // Задача 654c083c, п.4: знаменатель — приращение за ход, и в него попадают служебные
  // запросы между ходами. Это сказано прямо: «чуть ниже скорости чистого ответа».
  it('подсказка определения: время API — приращение за ход и оно чуть ниже чистого ответа', () => {
    const html = cloudOf({ data, cloudSpeed: cloudState() });
    expect(html).toContain('приращение накопительного duration_api_ms');
    expect(html).toContain('чуть ниже скорости чистого ответа');
  });

  // Спецификация «Состояния всех вариантов», строка 112: скелетон — только внутри
  // раскрытия; закрытая ферма скелетоном облака не блокируется. Тело примитива
  // смонтировано всегда (display:none в закрытом), поэтому проверка — принадлежность
  // скелетона телу и то, что закрытое тело скрыто: наружу, в ферму, он не выходит.
  it('первый ответ облака (loading без снимка): скелетон — только внутри тела, закрытое тело скрыто', () => {
    const html = renderTab({ data, cloudSpeed: cloudState({ data: null, loading: true }) });
    const { document: d } = parseHTML(`<!doctype html><body>${html}</body>`);
    const head = Array.from(d.querySelectorAll('button')).find(b => (b.textContent ?? '').includes('Сравнить с облаком'));
    expect(head).toBeTruthy();
    expect(head!.getAttribute('aria-expanded')).toBe('false');
    const body = d.getElementById(head!.getAttribute('aria-controls') ?? '');
    expect(body).toBeTruthy();
    expect(body!.innerHTML).toContain('ccs-lm-skeleton');
    // закрыто → тело display:none: скелетон не виден и ферму не блокирует
    expect(body!.getAttribute('style') ?? '').toContain('display:none');
    // над строкой-заголовком ничего из скелетона нет: он не становится блоком фермы
    expect(html.slice(0, html.indexOf('Сравнить с облаком'))).not.toContain('ccs-lm-skeleton');
    // после открытия — тот же скелетон виден (display:none снят)
    const { host, root } = mountTab({ data, cloudSpeed: cloudState({ data: null, loading: true }) });
    const btn = cloudHead(host);
    act(() => { btn.click(); });
    const open = doc.getElementById(btn.getAttribute('aria-controls') ?? '');
    expect(open!.getAttribute('style') ?? '').not.toContain('display:none');
    expect(open!.innerHTML).toContain('ccs-lm-skeleton');
    act(() => { root.unmount(); });
    host.remove();
  });
});

describe('сохранение состояния раскрытия (cc_local_llm_cloud_open)', () => {
  const data = mockLocalModelStats('working');

  it('дефолт закрыт; клик открывает и пишет «1», второй клик закрывает и пишет «0»', () => {
    const { host, root } = mountTab({ data, cloudSpeed: cloudState() });
    const head = cloudHead(host);
    expect(head.getAttribute('aria-expanded')).toBe('false');
    expect(store.get(CLOUD_KEY)).toBeUndefined();
    act(() => { head.click(); });
    expect(head.getAttribute('aria-expanded')).toBe('true');
    expect(store.get(CLOUD_KEY)).toBe('1');
    act(() => { head.click(); });
    expect(head.getAttribute('aria-expanded')).toBe('false');
    expect(store.get(CLOUD_KEY)).toBe('0');
    act(() => { root.unmount(); });
    host.remove();
  });

  it('сохранённая «1» открывает секцию при монтировании', () => {
    store.set(CLOUD_KEY, '1');
    const { host, root } = mountTab({ data, cloudSpeed: cloudState() });
    expect(cloudHead(host).getAttribute('aria-expanded')).toBe('true');
    act(() => { root.unmount(); });
    host.remove();
  });

  // Всё, что не '1', — закрыто (в т. ч. мусор от другой версии ключа).
  it('не-«1» в ключе — закрыто', () => {
    store.set(CLOUD_KEY, 'true');
    const { host, root } = mountTab({ data, cloudSpeed: cloudState() });
    expect(cloudHead(host).getAttribute('aria-expanded')).toBe('false');
    act(() => { root.unmount(); });
    host.remove();
  });

  // Гонка: два клика в одном act — между ними нет ререндера, следующий значение
  // берётся из актуального ref-зеркала. Мутация «next = !cloudOpen из замыкания»
  // даёт здесь aria-expanded="true" и '1' — этот тест краснеет; после отката зелёный.
  it('два клика в одном act от закрытого — снова закрыто и «0»', () => {
    const { host, root } = mountTab({ data, cloudSpeed: cloudState() });
    const head = cloudHead(host);
    expect(head.getAttribute('aria-expanded')).toBe('false');
    act(() => { head.click(); head.click(); });
    expect(head.getAttribute('aria-expanded')).toBe('false');
    expect(store.get(CLOUD_KEY)).toBe('0');
    act(() => { root.unmount(); });
    host.remove();
  });

  // Приватный режим: бросающие getItem/setItem не дают необработанного исключения
  // (наружу оно не уходит), раскрытие продолжает работать в памяти.
  it('бросающие getItem/setItem: исключения наружу нет, раскрытие работает в памяти', () => {
    lsImpl = {
      getItem: () => { throw new Error('private mode'); },
      setItem: () => { throw new Error('private mode'); },
    };
    try {
      const { host, root } = mountTab({ data, cloudSpeed: cloudState() });
      const head = cloudHead(host);
      expect(head.getAttribute('aria-expanded')).toBe('false');
      act(() => { head.click(); });
      expect(head.getAttribute('aria-expanded')).toBe('true');
      act(() => { head.click(); head.click(); });
      expect(head.getAttribute('aria-expanded')).toBe('true');
      act(() => { root.unmount(); });
      host.remove();
    } finally {
      lsImpl = normalImpl;
    }
  });
});
