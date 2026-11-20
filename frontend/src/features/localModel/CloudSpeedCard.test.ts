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
const { cloudSpeedWindowDates, cloudSpeedWindowCaption } = await import('./format');

const TAB_DEFAULTS = {
  isMobile: false, onOpenChat: () => {}, onRefetch: () => {}, onOpenHardware: () => {}, onOpenMedia: () => {},
  window: '5m', onWindowChange: () => {},
};

const CLOUD = {
  from: '2026-11-19',
  to: '2026-11-20',
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

  it('окно суток подписано датами из ответа', () => {
    const html = renderTab({ data, cloudSpeed: cloudState() });
    expect(html).toContain('за 19.11 — 20.11');
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

  it('карточка — после локальной «Скорости ответа» (порядок карточек)', () => {
    const html = renderTab({ data, cloudSpeed: cloudState() });
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
});

describe('окно запроса облачной скорости', () => {
  it('даты окна: вчера и сегодня по локальному времени', () => {
    const { from, to } = cloudSpeedWindowDates(new Date(2026, 10, 20, 12, 0, 0));
    expect(from).toBe('2026-11-19');
    expect(to).toBe('2026-11-20');
  });

  it('подпись окна из ответа; без дат — «за сутки»', () => {
    expect(cloudSpeedWindowCaption('2026-11-19', '2026-11-20')).toBe('за 19.11 — 20.11');
    expect(cloudSpeedWindowCaption(null, null)).toBe('за сутки');
  });
});
