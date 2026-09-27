import { describe, it, expect } from 'vitest';
import type { UsageResponse, UsageSnapshot } from '../../types';
import { accountSnapshotsFor } from '../accountUsage';

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
