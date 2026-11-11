// CenterViewShell — общая оболочка «просмотр в центре» (FileViewer + FeaturePage).
// Тестируем ПРОПСЫ оболочки, а не весь экран:
//   • gap — зазор шапки (strip + fixedLeft) прокинут пропсом: файл передаёт rowGap
//     (4 в tight, иначе TB.gap), страница — TB.gap (дефолт). Мутация «зашить gap:8»
//     (убрать пропс) красит: strip теряет заданный зазор.
//   • FS-T8 (дефект c27d286c): мобильная кнопка возврата — тач-цель ≥ 40 (TB.iconHitMobile);
//     десктопный close (IconButton md = 32) — без изменений. Мутация «вернуть height: 32»
//     в мобильной ветке красит мобильный тест.
//
// Окружение node (без jsdom): fakeDom (mount) + wireWindow (useEscapeClose вешает
// window.addEventListener('keydown')). Имя — span[data-testid=shell-name], его родитель
// — strip (div с gap); по нему проверяем зазор.

import { createElement } from 'react';
import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { CenterViewShell } from './CenterViewShell';
import { TB } from '../lib/design';
import { installFakeDom, uninstallFakeDom, mount, findNodeByAttr } from './chat/__tests__/fakeDom';

// useEscapeClose вешает window.addEventListener('keydown') и читает document.querySelector.
function wireWindow() {
  const win = globalThis as any;
  if (!win.window.addEventListener) {
    const listeners: Record<string, Set<Function>> = {};
    win.window.addEventListener = (t: string, f: Function) => { (listeners[t] ??= new Set()).add(f); };
    win.window.removeEventListener = (t: string, f: Function) => { listeners[t]?.delete(f); };
    win.window.dispatchEvent = (e: { type: string }) => { listeners[e.type]?.forEach(f => f(e)); return true; };
  }
  const doc = (globalThis as any).document;
  if (!doc.querySelector) doc.querySelector = () => null;
}

function renderShell(props: { gap?: number }) {
  installFakeDom();
  wireWindow();
  const { container } = mount(createElement(CenterViewShell, {
    onClose: () => {},
    name: 'имя',
    nameTestId: 'shell-name',
    rootTestId: 'shell-root',
    gap: props.gap,
    children: createElement('div', { 'data-testid': 'shell-body' }),
  }));
  return container;
}

// strip = родитель span[data-testid=shell-name] (имя — прямой ребёнок strip).
function stripGap(container: unknown): string | undefined {
  const name = findNodeByAttr(container as never, 'data-testid', 'shell-name');
  const strip = (name as unknown as { parentNode: { style: Record<string, string> } | null })?.parentNode;
  return strip?.style?.gap;
}

describe('CenterViewShell: gap пропсом (п.2)', () => {
  beforeEach(() => { vi.useFakeTimers(); });
  afterEach(() => { vi.useRealTimers(); uninstallFakeDom(); vi.restoreAllMocks(); });

  it('gap пропсом: strip получает заданный зазор (4px)', () => {
    const c = renderShell({ gap: 4 });
    expect(stripGap(c), 'strip gap = 4px (rowGap tight)').toBe('4px');
  });

  it('gap по умолчанию = TB.gap (8px) — страница фичи', () => {
    const c = renderShell({});
    expect(stripGap(c), 'strip gap = TB.gap (8px) по умолчанию').toBe(`${TB.gap}px`);
  });
});

// Close-контроль: span[data-testid=closeTestId] → его первый элементный ребёнок и есть
// кнопка (мобила — BackButton/PressableArea, десктоп — IconButton).
function renderClose(isMobile: boolean) {
  installFakeDom();
  wireWindow();
  const { container } = mount(createElement(CenterViewShell, {
    isMobile,
    onClose: () => {},
    closeLabel: 'К списку',
    mobileTitle: 'К списку файлов',
    closeTestId: 'shell-close',
    name: 'имя',
    nameTestId: 'shell-name',
    rootTestId: 'shell-root',
    children: createElement('div', { 'data-testid': 'shell-body' }),
  }));
  const wrap = findNodeByAttr(container, 'data-testid', 'shell-close');
  return wrap?.children[0] ?? null;
}

describe('CenterViewShell: тач-цель кнопки возврата (FS-T8, дефект c27d286c)', () => {
  beforeEach(() => { vi.useFakeTimers(); });
  afterEach(() => { vi.useRealTimers(); uninstallFakeDom(); vi.restoreAllMocks(); });

  it('мобильная кнопка возврата — min-height ≥ 40 (TB.iconHitMobile)', () => {
    const btn = renderClose(true);
    expect(btn, 'мобильный close (BackButton) смонтирован').toBeTruthy();
    const minH = Number.parseFloat((btn as { style: Record<string, string> }).style.minHeight ?? '');
    expect(minH >= TB.iconHitMobile, `min-height ${minH} < тач-порога ${TB.iconHitMobile}`).toBe(true);
  });

  it('десктопный close не тронут: IconButton md = 32, без min-height 40', () => {
    const btn = renderClose(false);
    expect(btn, 'десктопный close (IconButton) смонтирован').toBeTruthy();
    const st = (btn as { style: Record<string, string> }).style;
    expect(st.height, 'десктопный close остаётся 32px').toBe('32px');
    expect(Number.parseFloat(st.minHeight ?? '0') >= TB.iconHitMobile, 'десктопному close не задан min-height 40').toBe(false);
  });
});
