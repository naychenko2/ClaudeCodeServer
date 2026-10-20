// CenterViewShell — общая оболочка «просмотр в центре» (FileViewer + FeaturePage).
// Тестируем ПРОПСЫ оболочки, а не весь экран:
//   • gap — зазор шапки (strip + fixedLeft) прокинут пропсом: файл передаёт rowGap
//     (4 в tight, иначе TB.gap), страница — TB.gap (дефолт). Мутация «зашить gap:8»
//     (убрать пропс) красит: strip теряет заданный зазор.
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
