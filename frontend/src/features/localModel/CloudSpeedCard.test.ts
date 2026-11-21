// «Облачные модели: скорость ответа»: рендер с данными, пустое состояние,
// 403 (карточка не показывается), подсказки и разница определений с локальной
// «Скоростью ответа». Рендер — через linkedom, как в TextTab.test.ts.

import { describe, expect, it, vi } from 'vitest';
import { parseHTML } from 'linkedom';

const { window: win, document: doc } = parseHTML('<!doctype html><html><body><div id="root"></div></body></html>');
vi.stubGlobal('window', win);
vi.stubGlobal('document', doc);
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

function renderTab(props: Record<string, unknown>): string {
  const host = doc.createElement('div');
  doc.body.appendChild(host);
  const root = createRoot(host);
  act(() => {
    root.render(createElement(TextTab, { ...TAB_DEFAULTS, ...props } as never));
  });
  const html = host.innerHTML;
  act(() => { root.unmount(); });
  host.remove();
  return html;
}

describe('карточка «Облачные модели: скорость ответа»', () => {
  const data = mockLocalModelStats('working');

  // Только фрагмент карточки облака: «0 ток/с» встречается в соседних карточках
  // («1240 ток/с» — чтение промпта), и проверка по всему html ловила бы чужое число.
  function cloudCardHtml(props: Record<string, unknown>): string {
    const html = renderTab(props);
    const from = html.indexOf('>Облачные модели: скорость ответа</h3>');
    if (from < 0) return '';
    const to = html.indexOf('</section>', from);
    return html.slice(from, to < 0 ? html.length : to);
  }

  it('строка модели: имя, провайдер, сквозная скорость, медиана и число ходов', () => {
    const html = renderTab({ data, cloudSpeed: cloudState() });
    expect(html).toContain('>Облачные модели: скорость ответа</h3>');
    expect(html).toContain('claude-sonnet-5-5');
    expect(html).toContain('claude');
    expect(html).toContain('38 ток/с');
    expect(html).toContain('41 ток/с');
    expect(html).toContain('57');
    expect(html).toContain('ходов за сутки');
  });

  // Хвост ревью aa8af844, п.1: окно — последние 24 часа, их считает бэкенд. Дата-пара
  // «за 19.11 — 20.11» обещала календарные сутки, которых никто не считал, — её нет.
  it('окно подписано честно: «за сутки», без даты-пары из ответа', () => {
    const html = cloudCardHtml({ data, cloudSpeed: cloudState() });
    expect(html).toContain('за сутки');
    expect(html).not.toContain('за 19.11 — 20.11');
  });

  // Хвост ревью aa8af844, п.4: бесплатный не-локальный поставщик остаётся в карточке,
  // но строка подписана поставщиком и моделью: одна модель у двух поставщиков — две
  // строки, а не одна усреднённая.
  it('одна модель у двух поставщиков — две строки, каждая со своим поставщиком', () => {
    const html = cloudCardHtml({
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

  it('нет моделей — спокойная строка, не нули', () => {
    const html = cloudCardHtml({ data, cloudSpeed: cloudState({ data: { ...CLOUD, models: [] } }) });
    expect(html).toContain('Нет данных: за сутки не было ни одного хода облачной модели.');
    expect(html).not.toContain('0 ток/с');
  });

  it('null-поля — «нет данных» в своей ячейке, а не 0', () => {
    const html = cloudCardHtml({
      data,
      cloudSpeed: cloudState({ data: { ...CLOUD, models: [{ ...CLOUD.models[0], endToEndTps: null, medianTps: null }] } }),
    });
    expect(html).toContain('нет данных');
    expect(html).not.toContain('0 ток/с');
  });

  it('403 — карточка не показывается вовсе', () => {
    const html = renderTab({ data, cloudSpeed: cloudState({ forbidden: true, data: null }) });
    expect(html).not.toContain('Облачные модели: скорость ответа');
  });

  it('без снимка (опрос не шёл) — карточки нет', () => {
    const html = renderTab({ data });
    expect(html).not.toContain('Облачные модели: скорость ответа');
  });

  // Ф-3 (порядок v1, макет .html:206–214): облачное сравнение — последней строкой
  // вкладки, после закрытого раскрытия локальных метрик (в нём и локальная «Скорость
  // ответа»). Мутация «поднять облако над раскрытием» краснит.
  it('карточка — последней строкой: после локальных метрик под закрытием', () => {
    const html = renderTab({ data, cloudSpeed: cloudState() });
    expect(html.indexOf('Точные метрики локальной модели')).toBeLessThan(html.indexOf('>Облачные модели: скорость ответа</h3>'));
    expect(html.indexOf('>Скорость ответа</h3>')).toBeLessThan(html.indexOf('>Облачные модели: скорость ответа</h3>'));
  });

  it('подсказки: определение, Artificial Analysis, не сравнимо с локальной, TTFT', () => {
    const html = renderTab({ data, cloudSpeed: cloudState() });
    expect(html).toContain('сквозная скорость облачной модели за сутки');
    expect(html).toContain('duration_api_ms');
    expect(html).toContain('Определение сессионное');
    expect(html).toContain('Artificial Analysis');
    expect(html).toContain('не сравнимо');
    expect(html).toContain('время до первого токена у облачных моделей CCS не меряет');
    expect(html).toContain('не короче 32 токенов');
  });

  // Задача 654c083c, п.4: знаменатель — приращение за ход, и в него попадают служебные запросы
  // между ходами (подсказки следующего шага). Это сказано прямо: «чуть ниже скорости чистого
  // ответа» — а не молчание о расхождении с замером по чистым ответам.
  it('подсказка определения: время API — приращение за ход и оно чуть ниже чистого ответа', () => {
    const html = renderTab({ data, cloudSpeed: cloudState() });
    expect(html).toContain('приращение накопительного duration_api_ms');
    expect(html).toContain('чуть ниже скорости чистого ответа');
  });
});
