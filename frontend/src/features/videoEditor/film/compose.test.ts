import { beforeEach, describe, expect, it, vi } from 'vitest';

const filmToSound = vi.hoisted(() => vi.fn(async () => true));
vi.mock('../context/handoff', () => ({ filmToSound }));

// Окружение node: showToast шлёт событие в window
(globalThis as unknown as { window: Pick<Window, 'dispatchEvent'> }).window = { dispatchEvent: () => true };

import { videoApi } from '../api';
import { film } from '../mocks';
import { composeForFilm, isComposing } from './compose';

beforeEach(() => {
  filmToSound.mockClear();
  vi.restoreAllMocks();
});

describe('«Сочинить под фильм…»', () => {
  it('черновик звука передаётся в «Звук» через контекст с описанием стиля и возвратом «К фильму»', async () => {
    const f = film();
    vi.spyOn(videoApi, 'composeMusic').mockResolvedValue({ threadId: 't1' });
    expect(await composeForFilm('p1', 'c1', 'Мой', f, false)).toBe(true);
    expect(filmToSound).toHaveBeenCalledTimes(1);
    const arg = (filmToSound.mock.calls[0] as unknown[])[0] as Record<string, unknown>;
    expect(arg).toMatchObject({ sessionId: 'c1', path: f.path, filmName: 'Мой', threadId: 't1', reveal: false });
    expect(String(arg.style)).toContain('Мой');
    expect(isComposing(f.path, f.document.music?.file)).toBe(true);
  });

  it('отказ сервера: передачи нет, результат false', async () => {
    vi.spyOn(videoApi, 'composeMusic').mockRejectedValue(new Error('нет'));
    expect(await composeForFilm('p1', 'c1', 'Мой', film())).toBe(false);
    expect(filmToSound).not.toHaveBeenCalled();
  });
});
