// W6 (фича «Заметки+Вики»): ярлык шапки рельсы зоны — проп, а не зашитая строка.
// Без пропа PanelZone передаёт в PanelRail прежние значения («Панели»/«Панели
// инструментов») — все существующие экраны выглядят как раньше; с пропом — как
// передано (раздел «Заметки» передаст «Вики», шаг W3).
//
// PanelRail мокан на уровне модуля: тест проверяет ЗНАЧЕНИЯ, которые зона отдаёт
// рельсе, а не рендер рельсы. Остальной модуль (RAIL_W, RAIL_GAP) — настоящий
// через spread: зона берёт их для раскладки.

import { createElement } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { installFakeDom, mount, uninstallFakeDom } from '../../components/chat/__tests__/fakeDom';

// Ширина — десктоп: обычный (не compact) путь рендера, где рельса стоит в дереве.
const { envMock } = vi.hoisted(() => ({ envMock: { width: 1280 } }));
vi.mock('../../lib/breakpoints', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../lib/breakpoints')>();
  return { ...orig, useWindowWidth: () => envMock.width };
});

// Записываем props каждого вызова PanelRail: первый рендер зоны — первый элемент.
const { railProps } = vi.hoisted(() => ({ railProps: [] as Record<string, unknown>[] }));
vi.mock('../../components/ui/PanelRail', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../components/ui/PanelRail')>();
  return { ...orig, PanelRail: (props: Record<string, unknown>) => { railProps.push(props); return null; } };
});

import { PanelZone } from './PanelZone';

// Контент у панели быть должен: пустая зона («ничего показать не могу») выходит
// раньше рендера рельсы (PanelZone.tsx:798) — тогда проверять было бы нечего.
// «files» — в default-наборе WORKSPACE_KEYS и домашней зоной справа (panelCatalog.ts:202).
function mountZone(props: Record<string, unknown> = {}) {
  const { root } = mount(createElement(PanelZone, {
    side: 'right', panels: { files: createElement('div', null, 'файлы') }, ...props,
  } as never));
  return root;
}

describe('PanelZone: ярлык рельсы — пропом, по умолчанию прежний', () => {
  beforeEach(() => {
    installFakeDom();
    railProps.length = 0;
    // PanelZone в эффекте пишет CSS-переменные в documentElement (стаб, как в
    // тесте воркспейса: в fake-DOM documentElement — null) и в первом кадре
    // зовёт rAF/cAF (PanelZone.tsx:181-184) — стабы на обе стороны.
    const d = (globalThis as { document: Record<string, unknown> }).document;
    d.documentElement = { style: { setProperty: () => {}, removeProperty: () => {} } };
    (globalThis as { requestAnimationFrame?: unknown }).requestAnimationFrame = (cb: () => void) => { cb(); return 1; };
    (globalThis as { cancelAnimationFrame?: unknown }).cancelAnimationFrame = () => {};
  });
  afterEach(() => {
    delete (globalThis as { requestAnimationFrame?: unknown }).requestAnimationFrame;
    delete (globalThis as { cancelAnimationFrame?: unknown }).cancelAnimationFrame;
    vi.unstubAllGlobals();
    uninstallFakeDom();
  });

  it('без пропа — зона отдаёт рельсе прежние значения по умолчанию', () => {
    const root = mountZone();
    expect(railProps.length, 'рельса отрендерена').toBeGreaterThan(0);
    expect(railProps[0].hat, 'ярлык по умолчанию').toBe('Панели');
    expect(railProps[0].hatTitle, 'тултип по умолчанию').toBe('Панели инструментов');
    root.unmount();
  });

  it('с пропом — зона отдаёт рельсе переданное как есть', () => {
    const root = mountZone({ hat: 'Вики', hatTitle: 'Панели вики' });
    expect(railProps.length, 'рельса отрендерена').toBeGreaterThan(0);
    expect(railProps[0].hat, 'ярлык из пропа').toBe('Вики');
    expect(railProps[0].hatTitle, 'тултип из пропа').toBe('Панели вики');
    root.unmount();
  });
});
