import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import type { UsageResponse, UsageSnapshot } from '../../types';
import { accountSnapshotsFor, __test } from '../accountUsage';
import { api } from '../api';

const snap = (limitType: string, subscriptionKey?: string): UsageSnapshot =>
  ({ timestamp: '2026-09-27T12:00:00Z', limitType, subscriptionKey, utilization: 0.1 });

describe('accountSnapshotsFor', () => {
  it('нет данных → пусто', () => {
    expect(accountSnapshotsFor(null, 'claude')).toEqual([]);
  });

  it('пул: берутся снимки только своего аккаунта', () => {
    const mine = [snap('five_hour', 'claude-3')];
    const usage: UsageResponse = {
      snapshots: [],
      subscriptions: { claude: { snapshots: [snap('five_hour', 'claude')] }, 'claude-3': { snapshots: mine } },
    };
    expect(accountSnapshotsFor(usage, 'claude-3')).toBe(mine);
  });

  it('пул без ключа чата → пусто, чужой процент не подтягивается', () => {
    const usage: UsageResponse = { snapshots: [snap('five_hour')], subscriptions: { claude: { snapshots: [snap('five_hour')] } } };
    expect(accountSnapshotsFor(usage, 'work-account')).toEqual([]);
  });

  it('без пула: общие снимки минус сторонние провайдеры', () => {
    const own = snap('five_hour');
    const usage: UsageResponse = { snapshots: [own, snap('five_hour', 'glm')], providers: { glm: [] } };
    expect(accountSnapshotsFor(usage, 'claude')).toEqual([own]);
  });
});

// Общий стор: подписка/отписка и расписание. HAR прода — 9 GET /api/usage за 52 с,
// потому что каждый размонтированный/перемонтированный чат дёргал load() заново.
describe('общий стор: повторная подписка не дёргает сервер, пока данные свежие', () => {
  const usage: UsageResponse = { snapshots: [snap('five_hour')] };
  let getSpy: ReturnType<typeof vi.spyOn>;

  beforeEach(() => {
    vi.useFakeTimers();
    vi.setSystemTime(0);
    __test.reset();
    getSpy = vi.spyOn(api.usage, 'get').mockResolvedValue(usage);
  });
  afterEach(() => {
    getSpy.mockRestore();
    __test.reset();
    vi.useRealTimers();
  });

  // .then/.catch/.finally решённого промиса — микрозадачи; таймеры (setInterval) НЕ трогаем,
  // иначе runAllTicks выстрелит периодический load() раньше времени
  async function flush() {
    for (let i = 0; i < 4; i++) await Promise.resolve();
  }

  it('подписка → отписка → подписка в пределах 60 с → 1 запрос', async () => {
    const u1 = __test.subscribe(() => {});
    await flush();
    expect(getSpy).toHaveBeenCalledTimes(1);

    u1(); // подписчиков 0 → таймер снят

    const u2 = __test.subscribe(() => {}); // t=0, lastLoadAt=0, diff=0 < 60000
    await flush();
    expect(getSpy).toHaveBeenCalledTimes(1); // свежее — не дёргаем

    u2();
  });

  it('подписка → отписка → подписка через 61 с → 2 запроса', async () => {
    const u1 = __test.subscribe(() => {});
    await flush();
    expect(getSpy).toHaveBeenCalledTimes(1);
    u1();

    vi.setSystemTime(61_000); // данные устарели: 61 с > 60 с

    const u2 = __test.subscribe(() => {});
    await flush();
    expect(getSpy).toHaveBeenCalledTimes(2); // повторная загрузка

    u2();
  });
});
