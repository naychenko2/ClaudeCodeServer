import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { throttleRefetch } from './visiblePoll';

// throttleRefetch: leading-edge (первый в серии сразу) + один хвостовой вызов в конце
// окна. Без хвоста ход, начавшийся и закончившийся в пределах окна, терял бы последний
// status_changed — точка активности/статус в списке врут до следующего опроса.
describe('throttleRefetch: leading + trailing', () => {
  beforeEach(() => {
    vi.useFakeTimers();
    vi.setSystemTime(1_000_000);
  });
  afterEach(() => {
    vi.useRealTimers();
  });

  it('первый вызов уходит сразу (leading), повторы в окне схлопываются', () => {
    let n = 0;
    const [call] = throttleRefetch(() => { n++; }, 1000);
    call();
    expect(n).toBe(1);
    vi.advanceTimersByTime(500); // в окне
    call();
    expect(n).toBe(1); // повтор в окне не дублирует
  });

  it('ход, начавшийся и закончившийся в окне: хвостовой вызов доносит последний статус', () => {
    let n = 0;
    const [call] = throttleRefetch(() => { n++; }, 1000);
    call(); // t=0: leading
    vi.advanceTimersByTime(100);
    call(); // t=100: в окне → взводит trailing
    vi.advanceTimersByTime(100);
    call(); // t=200: в окне → trailing уже взведён, не дублируем
    vi.advanceTimersByTime(800); // до конца окна (t=1000)
    expect(n).toBe(2); // leading + trailing
  });

  it('dispose снимает хвостовой таймер: после снятия подписки trailing не уходит', () => {
    let n = 0;
    const [call, dispose] = throttleRefetch(() => { n++; }, 1000);
    call(); // leading
    vi.advanceTimersByTime(100);
    call(); // взводит trailing
    dispose(); // снятие подписки
    vi.advanceTimersByTime(2000);
    expect(n).toBe(1); // только leading, trailing снят
  });
});
