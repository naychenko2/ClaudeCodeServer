import { describe, expect, it } from 'vitest';
import { loraFailure } from './loraErrors';
// Классы из кита — те же, что бросает request() хоста: в remote-сборке
// loraErrors обязан ловить именно их, а не вторую копию из lib/offline.
import { OfflineError, RequestTimeoutError } from 'aihome_shell/kit';

describe('loraFailure: сетевые ошибки из кита', () => {
  it('OfflineError из кита → network', () => {
    expect(loraFailure(new OfflineError())).toEqual({ reason: 'network' });
  });
  it('RequestTimeoutError из кита → network', () => {
    expect(loraFailure(new RequestTimeoutError(30_000))).toEqual({ reason: 'network' });
  });
  it('TypeError (обрыв fetch) → network', () => {
    expect(loraFailure(new TypeError('Failed to fetch'))).toEqual({ reason: 'network' });
  });
});

describe('loraFailure: прочие причины', () => {
  it('известный code из body → причина, busyUntil прикладывается', () => {
    const e = new Error('server said no') as Error & { body?: unknown };
    e.body = { code: 'gpu_busy', busyUntil: '2026-10-12T12:00:00Z' };
    expect(loraFailure(e)).toEqual({ reason: 'gpu_busy', busyUntil: '2026-10-12T12:00:00Z' });
  });
  it('неизвестный code и прочая ошибка → unknown', () => {
    const e = new Error('boom') as Error & { body?: unknown };
    e.body = { code: 'something_else' };
    expect(loraFailure(e)).toEqual({ reason: 'unknown' });
    expect(loraFailure(new Error('boom'))).toEqual({ reason: 'unknown' });
  });
});
