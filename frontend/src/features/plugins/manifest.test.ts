// Манифест «Плагины»: ленивый DraftAnchor в слоте chat-item-tool (лента чата).
// Рендер слота прямым вызовом (без Suspense-границы в ядре) — граница живёт в манифесте.
// В ленте fallback={null} корректен (строка занимает своё место), поэтому тест проверяет
// главное: рендер слота с ещё не загруженным чанком НЕ падает.
// Мутация: убрать <Suspense> из манифеста → тест красный (suspended, no fallback).

import { describe, expect, it, vi } from 'vitest';
import { renderToStaticMarkup } from 'react-dom/server';
import type { ChatItemToolCtx } from '../../../lib/subsystems/registryCore';
import type { ChatItem } from '../../../types';

// Компонент, который «suspend»ит: бросает never-resolving проми. vi.hoisted — фабрика
// vi.mock (hoisted) не видит обычные top-level константы.
const suspender = vi.hoisted(() => {
  const pending = new Promise<never>(() => {});
  return function Suspender(): null { throw pending; };
});

vi.mock('./feed/records', () => ({ DraftAnchor: suspender }));

const { manifest } = await import('./manifest');

const ctx: ChatItemToolCtx = {
  item: { kind: 'module_record', recordType: 'plugin_draft', data: { slug: 'my-plugin' } } as unknown as ChatItem,
  online: true, projectId: 'p1', sessionId: 's1', persona: null,
};

describe('plugins/manifest: Suspense вокруг ленивного DraftAnchor', () => {
  it('рендер слота с незагруженным чанком не падает', () => {
    const entry = manifest.slots?.['chat-item-tool']?.[0];
    expect(entry?.render, 'вклад chat-item-tool не найден').toBeTruthy();
    // Suspense-граница держит suspend: рендер завершается (fallback={null} → пустая строка)
    renderToStaticMarkup(entry!.render!(ctx) as Parameters<typeof renderToStaticMarkup>[0]);
  });
});
