// Блок отказа «клон MiniMax протух» снимается, как только «Пересоздать» запустило задачу
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { isValidElement, type ReactElement, type ReactNode } from 'react';

// Окружение node: тостам нужен window с событиями
(globalThis as unknown as { window: EventTarget }).window ??= new EventTarget();

import { voicesApi } from '../voices/api';
import { recreateClone, RecreateButton } from '../voices/RecreateButton';
import { CloneRefusalNote } from './CloneRefusalNote';
import type { CloneRefusal } from './run';

const REFUSAL = { slug: 'anya', message: 'MiniMax удалил клон', quote: null } as unknown as CloneRefusal;

function find(node: ReactNode, type: unknown): ReactElement | null {
  if (Array.isArray(node)) {
    for (const n of node) { const f = find(n, type); if (f) return f; }
    return null;
  }
  if (!isValidElement(node)) return null;
  if (node.type === type) return node;
  return find((node.props as { children?: ReactNode }).children, type);
}

beforeEach(() => { vi.restoreAllMocks(); });

describe('отказ клона после «Пересоздать»', () => {
  it('кнопка «Пересоздать» в блоке снимает блок, когда задача пошла', () => {
    const onCleared = vi.fn();
    const button = find(CloneRefusalNote({ scope: 'p1', refusal: REFUSAL, onCleared }), RecreateButton);
    expect(button?.props).toMatchObject({ scope: 'p1', slug: 'anya' });
    (button!.props as { onStarted?: () => void }).onStarted?.();
    expect(onCleared).toHaveBeenCalledOnce();
  });

  it('задача ушла — onStarted; сервер отказал — блок остаётся', async () => {
    const started = vi.fn();
    const recreate = vi.spyOn(voicesApi, 'recreate').mockResolvedValueOnce({ jobId: 'j1' });
    expect(await recreateClone('p1', 'anya', 'q1', started)).toBe(true);
    expect(recreate).toHaveBeenCalledWith('p1', 'anya', 'q1');
    expect(started).toHaveBeenCalledOnce();

    recreate.mockRejectedValueOnce(new Error('Котировка истекла'));
    expect(await recreateClone('p1', 'anya', 'q2', started)).toBe(false);
    expect(started).toHaveBeenCalledOnce();
  });
});
