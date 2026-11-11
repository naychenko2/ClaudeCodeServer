// «Продолжить в новом чате»: подсказка и плашки связи (кнопка под ответом удалена в F-2).
import { describe, it, expect } from 'vitest';
import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { ContinueHint, ContinuationBanner } from '../ContinueChat';

describe('подсказка при большом контексте (вариант 2 · Ушко)', () => {
  it('десктоп: число, «Продолжить» со стрелкой, ✕ «Не сейчас», role=status, видимый текст', () => {
    const html = renderToStaticMarkup(createElement(ContinueHint, { tokens: 312_000, onOpen: () => {}, onSnooze: () => {} }));
    expect(html).toContain('data-testid="continue-hint"');
    expect(html).toContain('role="status"');
    expect(html).toContain('data-testid="continue-hint-open"');
    expect(html).toContain('data-testid="continue-hint-snooze"');
    expect(html).toContain('aria-label="Не сейчас"');
    expect(html).toContain('>312K<');
    expect(html).toContain('Продолжить');
    // Видимый текст — «312K · дешевле в новом чате» (слово «Контекст» только в title-тултипе)
    expect(html).toContain('дешевле в новом чате');
  });
  it('мобила: короче — «312K» и «В новый чат», без длинного хвоста', () => {
    const html = renderToStaticMarkup(createElement(ContinueHint, { tokens: 312_000, isMobile: true, onOpen: () => {}, onSnooze: () => {} }));
    expect(html).toContain('data-testid="continue-hint"');
    expect(html).toContain('В новый чат');
    expect(html).not.toContain('дешевле в новом чате');
  });
});

describe('плашки связи', () => {
  it('в старом чате — «Продолжено в новом чате»', () => {
    const html = renderToStaticMarkup(createElement(ContinuationBanner, { kind: 'into', onOpen: () => {} }));
    expect(html).toContain('Продолжено в новом чате');
    expect(html).toContain('continuation-into');
  });
  it('в новом чате — «Продолжение чата <имя>»', () => {
    const html = renderToStaticMarkup(createElement(ContinuationBanner, { kind: 'from', name: 'Спринт 12', onOpen: () => {} }));
    expect(html).toContain('Продолжение чата «Спринт 12»');
    expect(html).toContain('continuation-from');
  });
});
