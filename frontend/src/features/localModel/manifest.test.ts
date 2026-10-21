// Манифест «Локальные модели»: ленивая панель в слоте workspace-panel-def.
// Рендер слота прямым вызовом (без Suspense-границы в ядре) — поэтому граница
// живёт в самом манифесте. Тест: чанк ещё не загрузился (компонент suspend-ит) →
// рендер не падает и не оставляет пустой корень (показывается заглушка-каркас).
// Мутация: убрать <Suspense> из манифеста → тест красный (suspended, no fallback).

import { describe, expect, it, vi } from 'vitest';
import { renderToStaticMarkup } from 'react-dom/server';
import type { WorkspacePanelDefCtx } from '../../../lib/subsystems/registryCore';

// Компонент, который «suspend»ит: бросает never-resolving проми. vi.hoisted — фабрика
// vi.mock (hoisted) не видит обычные top-level константы.
const suspender = vi.hoisted(() => {
  const pending = new Promise<never>(() => {});
  return function Suspender(): null { throw pending; };
});

vi.mock('./LocalModelPanel', () => ({ LocalModelPanel: suspender }));

const { manifest } = await import('./manifest');

const ctx: WorkspacePanelDefCtx = {
  projectId: 'p1', sessionId: 's1', isMobile: false, onClose: () => {},
};

describe('localModel/manifest: Suspense вокруг ленивной панели', () => {
  it('рендер слота с незагруженным чанком не падает и не оставляет пустой корень', () => {
    const entry = manifest.slots?.['workspace-panel-def']?.[0];
    expect(entry?.render, 'вклад workspace-panel-def не найден').toBeTruthy();
    // Suspense-граница: suspend-компонент → заглушка-каркас (не пустой корень), без падения
    const html = renderToStaticMarkup(entry!.render!(ctx) as Parameters<typeof renderToStaticMarkup>[0]);
    expect(html.length, 'корень пуст — Suspense-граница не держит макет').toBeGreaterThan(0);
  });
});
