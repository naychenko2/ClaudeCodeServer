// Вкладка «LLM»: текстовые helper'ы (notReportedText), точные подписи статуса
// (statusPill/standDot/standHint — макет bonsai-toolbar §4) и рендер TextTab:
// переключатель движка (виден только при двух configured), сокрытие полей из
// `unsupported` (не заглушки, а ничего), состояние 'yielded' (нейтральный тон,
// не нули), карточка Video и «места сервера» вместо чатов.
// Рендер — через linkedom (как tasks/acceptanceBlock.test.ts); ResizeObserver
// стабится: ThroughputChart меряет им свою ширину.

import { describe, expect, it, vi } from 'vitest';
import { parseHTML } from 'linkedom';

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
const { mockBonsaiStats, mockLocalModelStats } = await import('./localModel.fixtures');
const { dutyWhoRows } = await import('./dutyFormat');
const ENGINES_BOTH = [
  { name: 'main' as const, title: 'Основная', configured: true },
  { name: 'bonsai' as const, title: 'Bonsai', configured: true },
];

function renderTab(props: Record<string, unknown>): string {
  const host = doc.createElement('div');
  doc.body.appendChild(host);
  const root = createRoot(host);
  act(() => {
    root.render(createElement(TextTab, {
      isMobile: false, onOpenChat: () => {}, onRefetch: () => {}, onOpenHardware: () => {},
      window: '5m', onWindowChange: () => {}, ...props,
    }));
  });
  const html = host.innerHTML;
  act(() => { root.unmount(); });
  host.remove();
  return html;
}

// История с кэш-содержанием и вытеснениями: на ней видно и разницу main/bonsai —
// у main поля рисуются, у bonsai (unsupported из фикстуры) — скрыты целиком.
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
    const p = statusPill(mockBonsaiStats('yielded'));
    expect(p).toEqual({ label: 'Уступила карту', tone: 'muted' });
  });
  it('yielded без причин, с остатком паузы — «Ждёт возврата»', () => {
    const d = mockBonsaiStats('yielded');
    d.duty = { ...d.duty!, arbiter: { ...d.duty!.arbiter!, reasons: [], quietLeftS: 160 } };
    expect(statusPill(d)).toEqual({ label: 'Ждёт возврата', tone: 'muted' });
  });
  it('starting — «Загружается», waiting-vram — «Ждёт память карты» (warning: память сама не освободится)', () => {
    expect(statusPill(mockBonsaiStats('starting'))).toEqual({ label: 'Загружается', tone: 'muted' });
    expect(statusPill(mockBonsaiStats('waiting-vram'))).toEqual({ label: 'Ждёт память карты', tone: 'warning' });
  });
  it('без арбитра/без причин — групповая подпись (Б3b: без арбитра yielded не ставится)', () => {
    const d = mockBonsaiStats('yielded');
    d.duty = { ...d.duty!, arbiter: null };
    expect(statusPill(d).label).toBe('Уступила карту');
    expect(statusPill({ configured: true, status: 'yielded' }).label).toBe('Уступила карту');
  });
});

describe('standDot / standHint — тон точки и подсказка (макет §4)', () => {
  it('уступка — muted; память и молчание стенда — warning; работа — точки нет', () => {
    expect(standDot(mockBonsaiStats('yielded'))).toBe('muted');
    expect(standDot(mockBonsaiStats('starting'))).toBe('muted');
    expect(standDot(mockBonsaiStats('waiting-vram'))).toBe('warning');
    expect(standDot(mockBonsaiStats('unknown'))).toBe('warning');
    expect(standDot(mockBonsaiStats('ready'))).toBeNull();
  });
  it('подсказки — по состоянию; у работающего стенда их нет', () => {
    expect(standHint(mockBonsaiStats('yielded'))).toBe('Bonsai уступила карту');
    const d = mockBonsaiStats('yielded');
    d.duty = { ...d.duty!, arbiter: { ...d.duty!.arbiter!, reasons: [], quietLeftS: 160 } };
    expect(standHint(d)).toBe('Bonsai ждёт возврата');
    expect(standHint(mockBonsaiStats('starting'))).toBe('Bonsai загружается');
    expect(standHint(mockBonsaiStats('waiting-vram'))).toBe('Bonsai ждёт память карты');
    expect(standHint(mockBonsaiStats('unknown'))).toBe('Bonsai недоступна');
    expect(standHint(mockBonsaiStats('ready'))).toBeNull();
  });
});

describe('переключатель «Основная · Bonsai» (c1)', () => {
  const data = mockLocalModelStats('working');
  it('два движка — переключатель есть, выбранный сегмент aria-pressed', () => {
    const html = renderTab({ data, engines: ENGINES_BOTH, engine: 'main', onEngineChange: () => {} });
    expect(html).toContain('Основная');
    expect(html).toContain('Bonsai');
    expect(html).toContain('aria-pressed="true"');
  });
  // Сегменты переключателя подписаны titles из /engines («Основная», «Bonsai»);
  // ни одного из этих слов в стенде нет — их отсутствие и есть «переключателя нет»
  it('один движок — переключателя нет (ни одного сегмента)', () => {
    const html = renderTab({ data, engines: [ENGINES_BOTH[0]], engine: 'main', onEngineChange: () => {} });
    expect(html).not.toContain('Основная');
    expect(html).not.toContain('Bonsai');
  });
  it('второй движок не настроен — переключателя нет', () => {
    const html = renderTab({
      data, engine: 'main', onEngineChange: () => {},
      engines: [ENGINES_BOTH[0], { ...ENGINES_BOTH[1], configured: false }],
    });
    expect(html).not.toContain('Основная');
    expect(html).not.toContain('Bonsai');
  });
  // Точка и подсказка — по снимку ВЫБРАННОГО движка: data — снимок текущего стенда,
  // состояние Bonsai с основного стенда неизвестно (Ф6 — из useLocalModelWatcher).
  // Раньше тест подставлял невозможную пару (main + снимок Bonsai); теперь — реальные.
  it('основной стенд со своим снимком (down) — на сегменте «Bonsai» нет точки и подсказки', () => {
    const html = renderTab({
      data: mockLocalModelStats('down'), engines: ENGINES_BOTH, engine: 'main', onEngineChange: () => {},
    });
    // сегмент есть, но пустой: ни title-подсказки, ни 6-пиксельной точки
    expect(html).toContain('Bonsai');
    expect(html).not.toContain('Bonsai —');
    expect(html).not.toContain('width:6px');
  });
  it('выбран «Bonsai» и его снимок (yielded) — точка и подсказка на сегменте', () => {
    const html = renderTab({
      data: mockBonsaiStats('yielded'), engines: ENGINES_BOTH, engine: 'bonsai', onEngineChange: () => {},
    });
    expect(html).toContain('Bonsai — уступила карту');
    expect(html).toContain('width:6px');
  });
});

describe('подстрока под статусом (макет §3, .html:414)', () => {
  it('Bonsai — «llama-server · Video · обновлено …», без «карта/карт, TP=»', () => {
    const html = renderTab({
      data: { ...mockBonsaiStats('ready'), history: historyWithKv() }, engine: 'bonsai',
    });
    expect(html).toContain('llama-server · Video ·');
    expect(html).toContain('обновлено');
    expect(html).not.toContain('TP=');
  });
  it('main — прежняя «N карты, TP=N», без «Video»', () => {
    const html = renderTab({ data: mockLocalModelStats('working') });
    expect(html).toContain('vLLM · 2 карты, TP=2');
    expect(html).not.toContain('Video');
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
  it('фикстура Bonsai с той же историей — 0 блоков KV/RAM-тира/вытеснений; карточка «Нагрузка» с подписью про ступеньки', () => {
    const html = renderTab({
      data: { ...mockBonsaiStats('ready'), history: historyWithKv() }, engine: 'bonsai',
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
  it('нейтральный баннер и спокойные «Сейчас»/«Скорость» — не нули и не красное', () => {
    const html = renderTab({ data: mockBonsaiStats('yielded'), engine: 'bonsai' });
    expect(html).toContain('Уступила карту');
    expect(html).toContain('Bonsai уступила карту — это по плану.');
    expect(html).toContain('Модель выгружена — запросов к ней нет.');
    expect(html).toContain('Нет данных, пока модель выгружена.');
    expect(html).not.toContain('запросов идёт');
    // уступка не приглушается (приглушение — только авария)
    expect(html).not.toContain('grayscale');
  });
  it('«Ждёт память карты» — свой тон и свой текст баннера', () => {
    const html = renderTab({ data: mockBonsaiStats('waiting-vram'), engine: 'bonsai' });
    expect(html).toContain('Ждёт память карты');
    expect(html).toContain('Bonsai ждёт память карты.');
  });
  it('авария без выгрузки (unknown → down) — текст про аварию, не про уступку', () => {
    const html = renderTab({ data: mockBonsaiStats('unknown'), engine: 'bonsai' });
    expect(html).toContain('Bonsai не отвечает');
    expect(html).toContain('а арбитр её не выгружал');
    expect(html).toContain('Это авария, не уступка');
  });
});

describe('карточка Video и «места сервера» (стенд Bonsai)', () => {
  it('«Видеокарта · Video» с чипом «общая с ComfyUI», полоса памяти и KPI', () => {
    const html = renderTab({ data: mockBonsaiStats('ready'), engine: 'bonsai' });
    expect(html).toContain('Видеокарта · Video');
    expect(html).toContain('общая с ComfyUI');
    expect(html).toContain('8/24 ГБ');
    expect(html).not.toContain('Железо');
  });
  it('«Кто сейчас» — места со звеном и длительностью; строки не кнопки', () => {
    const html = renderTab({ data: mockBonsaiStats('yielded'), engine: 'bonsai' });
    expect(html).toContain('Классификация задачи');
    expect(html).toContain('MiniMax · страховка');
    expect(html).toContain('2,1 с');
    expect(html).toContain('Здесь места сервера');
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
  it('main — «с ожиданием» (prompt-время vLLM включает очередь)', () => {
    const html = renderTab({ data: mockLocalModelStats('working') });
    expect(html).toContain('чтение промпта с ожиданием');
  });
  it('Bonsai — без «с ожиданием» (у llama-server это чистое время чтения)', () => {
    const html = renderTab({ data: mockBonsaiStats('ready'), engine: 'bonsai' });
    expect(html).toContain('чтение промпта, ток/с');
    expect(html).not.toContain('с ожиданием');
  });
});
