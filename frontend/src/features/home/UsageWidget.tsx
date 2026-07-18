import { useEffect, useState } from 'react';
import { Gauge } from 'lucide-react';
import type { UsageResponse } from '../../types';
import { api } from '../../lib/api';
import { C, FONT } from '../../lib/design';
import { RATE_COLORS, windowLabel, fmtReset, latestPerWindow } from '../../lib/rateLimit';
import { cliProviderKeys, providerCapsByKey, providerLabel } from '../../lib/models';
import { UsageScreen } from '../../components/UsageScreen';
import { WidgetCard, WidgetAction, WidgetEmpty } from './WidgetCard';

// Балансы инертны — раз в 5 минут достаточно
const POLL_MS = 5 * 60_000;
const LOW_BALANCE = 5;

// «Использование»: худшее окно лимитов подписки Claude + балансы CLI-провайдеров и fal.
// Компактная выжимка UsageScreen; «Подробнее» открывает полный экран модалом.
export function UsageWidget() {
  const [usage, setUsage] = useState<UsageResponse | null>(null);
  const [balances, setBalances] = useState<Array<{ key: string; label: string; value: number }>>([]);
  const [showUsage, setShowUsage] = useState(false);

  useEffect(() => {
    let cancelled = false;
    const fetchAll = () => {
      api.usage.get()
        .then(d => { if (!cancelled) setUsage(d); })
        .catch(() => { if (!cancelled) setUsage({ snapshots: [] }); });
      for (const key of cliProviderKeys()) {
        if (!providerCapsByKey(key).hasBalance) continue;
        api.providers.usage(key)
          .then(d => {
            const v = d.balance ? parseFloat(d.balance.totalBalance) : NaN;
            if (cancelled || isNaN(v)) return;
            setBalances(prev => [...prev.filter(b => b.key !== key), { key, label: providerLabel(key), value: v }]);
          })
          .catch(() => {});
      }
      api.fal.account(7)
        .then(d => {
          if (cancelled || !d.enabled || typeof d.balance !== 'number') return;
          setBalances(prev => [...prev.filter(b => b.key !== 'fal'), { key: 'fal', label: 'fal.ai', value: d.balance! }]);
        })
        .catch(() => {});
    };
    fetchAll();
    const timer = setInterval(fetchAll, POLL_MS);
    return () => { cancelled = true; clearInterval(timer); };
  }, []);

  // Все окна лимитов (5-часовое, недельные и т.д.) — данные и так приходят одним
  // запросом, показываем каждое компактной строкой с прогресс-баром
  const windows = usage?.snapshots?.length ? latestPerWindow(usage.snapshots) : [];
  const empty = windows.length === 0 && balances.length === 0;

  return (
    <WidgetCard
      icon={<Gauge size={16} strokeWidth={2} />}
      title="Использование"
      action={<WidgetAction label="Подробнее →" onClick={() => setShowUsage(true)} />}
    >
      {empty && <WidgetEmpty text="Данных об использовании пока нет." />}
      {windows.length > 0 && (
        <div style={{ display: 'flex', flexDirection: 'column', gap: 9 }}>
          {windows.map(w => {
            const c = RATE_COLORS[w.level];
            const reset = fmtReset(w.resetsAt);
            return (
              <div key={w.limitType}>
                <div style={{ display: 'flex', alignItems: 'baseline', gap: 8 }}>
                  <span style={{ fontFamily: FONT.sans, fontSize: 12.5, color: C.textPrimary, flex: 1, minWidth: 0 }}>
                    {windowLabel(w.limitType)}
                  </span>
                  {reset && (
                    <span style={{ fontFamily: FONT.sans, fontSize: 11, color: C.textMuted }}>сброс {reset}</span>
                  )}
                  <span style={{
                    fontFamily: FONT.mono, fontSize: 13, fontWeight: 700,
                    color: w.hasUtil ? (w.level === 'normal' ? C.textHeading : c.text) : C.textMuted,
                  }}>
                    {w.hasUtil ? `${w.pct}%` : '—'}
                  </span>
                </div>
                <div style={{ height: 5, borderRadius: 3, background: C.track, overflow: 'hidden', marginTop: 5 }}>
                  <div style={{ width: `${w.hasUtil ? Math.min(100, w.pct) : 0}%`, height: '100%', background: c.fill }} />
                </div>
              </div>
            );
          })}
        </div>
      )}
      {balances.length > 0 && (
        <div style={{ display: 'flex', flexWrap: 'wrap', gap: 8 }}>
          {balances.map(b => (
            <div key={b.key} style={{
              display: 'flex', alignItems: 'baseline', gap: 6, borderRadius: 10,
              padding: '7px 11px', background: C.bgCard, border: `1px solid ${C.borderLight}`,
            }}>
              <span style={{
                fontFamily: FONT.mono, fontSize: 15, fontWeight: 700,
                color: b.value < LOW_BALANCE ? C.dangerText : C.textHeading,
              }}>
                ${b.value < 1 ? b.value.toFixed(3) : b.value.toFixed(2)}
              </span>
              <span style={{ fontFamily: FONT.sans, fontSize: 11.5, color: C.textMuted }}>{b.label}</span>
            </div>
          ))}
        </div>
      )}
      {showUsage && <UsageScreen onClose={() => setShowUsage(false)} />}
    </WidgetCard>
  );
}
