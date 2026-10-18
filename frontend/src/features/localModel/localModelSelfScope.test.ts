// scope=self (не-админ): скрытие видеокарты, фильтра по логину, колонки владельца;
// своя пометка «только ваши генерации». Рендер через renderToStaticMarkup (node,
// без jsdom) — паттерн GpuCard.test.ts. Effects не бегут, поэтому fetchMediaHistory
// (useMediaHistory) в статическом рендере не вызывается — мок не нужен.

import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it } from 'vitest';
import { mockLocalModelMedia, mockLocalModelMediaSelf } from './localModel.fixtures';
import { MediaTab } from './MediaTab';
import { HistoryFilterMenu } from './MediaHistoryFilters';
import { DEFAULT_HISTORY_FILTERS } from './useMediaHistory';

function renderMediaTab(data: Parameters<typeof MediaTab>[0]['data']): string {
  return renderToStaticMarkup(
    createElement(MediaTab, {
      data,
      loading: false,
      isMobile: false,
      onOpenChat: () => {},
      onRefetch: () => {},
    }),
  );
}

describe('MediaTab: scope=self скрывает видеокарту', () => {
  // GpuCard ушёл на «Железо» (GpuDetailCard); на «Медиа» у админа — только
  // строка-ссылка HwLinkRow (title «Видеокарта»/«Видеокарты» + «Железо →»).
  it('all — строка-ссылка «Видеокарта» есть, карточек карт нет', () => {
    const html = renderMediaTab(mockLocalModelMedia('run'));
    expect(html).toContain('Видеокарта');
    expect(html).toContain('Железо →');
  });
  it('self — ни строки-ссылки, ни слова «Видеокарта»', () => {
    // Инвариант приватности (ADR-030): у не-админа видеокарт нет вовсе.
    // Фикстура hardware намеренно есть — guard `!self` в MediaTab её и скрывает;
    // мутация (убрать guard) рендерит строку и тест краснеет.
    const html = renderMediaTab(mockLocalModelMediaSelf('run'));
    expect(html).not.toContain('Видеокарта');
    expect(html).not.toContain('Железо →');
  });
});

describe('MediaTab: пометка о видимости', () => {
  it('all — «Видны генерации всех пользователей»', () => {
    expect(renderMediaTab(mockLocalModelMedia('run'))).toContain('Видны генерации всех пользователей');
  });
  it('self — «Показаны только ваши генерации»', () => {
    expect(renderMediaTab(mockLocalModelMediaSelf('run'))).toContain('Показаны только ваши генерации');
  });
});

describe('MediaTab: «перед вами в очереди: N» (queueAhead)', () => {
  it('queueAhead=3 — подпись со склонением «3 задачи»', () => {
    expect(renderMediaTab(mockLocalModelMediaSelf('run'))).toContain('Перед вами в очереди: 3 задачи');
  });
  it('queueAhead=null — подписи нет', () => {
    const data = { ...mockLocalModelMediaSelf('run'), queueAhead: null };
    expect(renderMediaTab(data)).not.toContain('Перед вами в очереди');
  });
});

describe('HistoryFilterMenu: scope=self скрывает фильтр по логину', () => {
  const base = {
    filters: DEFAULT_HISTORY_FILTERS,
    counters: { kinds: {}, outcomes: {} },
    records: [],
    meUsername: 'me',
    onPatch: () => {},
    onReset: () => {},
    onClose: () => {},
  };
  it('hideLogin=false — секция «Пользователь» есть', () => {
    const html = renderToStaticMarkup(createElement(HistoryFilterMenu, { ...base, hideLogin: false }));
    expect(html).toContain('Пользователь');
  });
  it('hideLogin=true — секции «Пользователь» нет', () => {
    const html = renderToStaticMarkup(createElement(HistoryFilterMenu, { ...base, hideLogin: true }));
    expect(html).not.toContain('Пользователь');
  });
});
