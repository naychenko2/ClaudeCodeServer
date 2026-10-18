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
  it('all — карточка «Видеокарта» есть', () => {
    expect(renderMediaTab(mockLocalModelMedia('run'))).toContain('Видеокарта');
  });
  it('self — карточки «Видеокарта» нет', () => {
    expect(renderMediaTab(mockLocalModelMediaSelf('run'))).not.toContain('Видеокарта');
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
