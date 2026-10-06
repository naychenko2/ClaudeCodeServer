import { afterEach, describe, expect, it, vi } from 'vitest';
import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';

// Отсчёт инструмента сразу за порогом: иначе статика (эффекты не идут) остаётся в глаголах
vi.mock('../../../hooks/useRunningElapsed', () => ({ useRunningElapsed: () => 5000 }));

import { WaitingIndicator } from '../WaitingIndicator';

// Тестовая среда — node, window нет: подставляем его с одним matchMedia
const reducedMotion = (on: boolean) => {
  vi.stubGlobal('window', { matchMedia: (q: string) => ({ matches: on && q.includes('reduce'), addEventListener() {}, removeEventListener() {} }) });
};

describe('WaitingIndicator — идёт инструмент (вариант D)', () => {
  afterEach(() => { vi.unstubAllGlobals(); });
  const render = (props: Record<string, unknown>) => renderToStaticMarkup(createElement(WaitingIndicator, {
    activeToolLabel: 'Тесты · dotnet', activeToolStartedAt: 1_000, ...props,
  }));

  it('в ветке инструмента нет мигающего курсора; время стоит до текста', () => {
    const html = render({ activeToolDetail: '412 из 7951' });
    expect(html).not.toContain('blink');
    expect(html.indexOf('data-waiting-clock')).toBeGreaterThan(-1);
    expect(html.indexOf('data-waiting-clock')).toBeLessThan(html.indexOf('Тесты · dotnet'));
  });

  it('после монтирования (F5) первая фраза стоит целиком', () => {
    expect(render({})).toContain('>Тесты · dotnet<');
  });

  it('reduced-motion — без печати, «подпись · счётчик» одной строкой, «упало» красным', () => {
    reducedMotion(true);
    const html = render({ activeToolDetail: '412 из 7951 · упало 2' });
    // Текст в теле, а не в title: после «>»
    expect(html).toContain('>Тесты · dotnet · 412 из 7951 · <');
    expect(html).toContain('var(--c-danger-text)">упало 2</span>');
  });

  it('иконка операции перед временем', () => {
    const html = render({ activeToolOperation: 'tests' });
    expect(html).toContain('lucide-flask-conical');
    expect(html.indexOf('lucide-flask-conical')).toBeLessThan(html.indexOf('data-waiting-clock'));
  });

  it('тихий режим (карточка видна) — подписи, времени и иконки нет, вместо них глаголы с курсором', () => {
    const html = render({ activeToolOnScreen: true, activeToolOperation: 'tests', activeToolDetail: '412 из 7951' });
    expect(html).not.toContain('Тесты · dotnet');
    expect(html).not.toContain('data-waiting-clock');
    expect(html).not.toContain('lucide-flask-conical');
    expect(html).not.toContain('data-waiting-tool');
    expect(html).toContain('blink');
  });

  it('без инструмента — глаголы с курсором, как раньше', () => {
    const html = renderToStaticMarkup(createElement(WaitingIndicator, {}));
    expect(html).toContain('blink');
    expect(html).not.toContain('data-waiting-tool');
  });
});
