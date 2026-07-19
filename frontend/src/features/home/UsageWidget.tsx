import { useEffect, useState } from 'react';
import { Gauge } from 'lucide-react';
import type { UsageResponse } from '../../types';
import { api } from '../../lib/api';
import { C, FONT } from '../../lib/design';
import { type RateWindow, RATE_COLORS, windowLabel, fmtReset, latestPerWindow, worstWindow } from '../../lib/rateLimit';
import { cliProviderKeys, providerCapsByKey, providerLabel } from '../../lib/models';
import { UsageScreen } from '../../components/UsageScreen';
import { WidgetCard, WidgetAction, WidgetEmpty } from './WidgetCard';

// Балансы инертны — раз в 5 минут достаточно
const POLL_MS = 5 * 60_000;
const LOW_BALANCE = 5;

// Строка окна лимита: название, время сброса, процент (или «в пределах нормы») + шкала
function WindowRow({ w }: { w: RateWindow }) {
  const c = RATE_COLORS[w.level];
  const reset = fmtReset(w.resetsAt);
  return (
    <div>
      <div style={{ display: 'flex', alignItems: 'baseline', gap: 8 }}>
        <span style={{ fontFamily: FONT.sans, fontSize: 12.5, color: C.textPrimary, flex: 1, minWidth: 0 }}>
          {windowLabel(w.limitType)}
        </span>
        {reset && (
          <span style={{ fontFamily: FONT.sans, fontSize: 11, color: C.textMuted }}>сброс {reset}</span>
        )}
        <span
          style={{
            fontFamily: w.hasUtil ? FONT.mono : FONT.sans, fontSize: w.hasUtil ? 13 : 11,
            fontWeight: w.hasUtil ? 700 : 400,
            color: w.hasUtil ? (w.level === 'normal' ? C.textHeading : c.text) : C.textMuted,
          }}
          // Долю CLI присылает только при приближении к лимиту, и молчание —
          // это «расход невелик», а не «ноль». Поясняем, чтобы прочерк не выглядел сбоем.
          title={w.hasUtil ? undefined : 'Claude сообщает долю использования только при приближении к лимиту'}
        >
          {w.hasUtil ? `${w.pct}%` : 'в пределах нормы'}
        </span>
      </div>
      {/* Шкалу рисуем ТОЛЬКО с реальными данными: пустая полоса читалась как
          «израсходовано 0%», хотя означала «доля неизвестна». Так же на экране
          «Использование» (UsageScreen.WindowCard) */}
      {w.hasUtil && (
        <div style={{ height: 5, borderRadius: 3, background: C.track, overflow: 'hidden', marginTop: 5 }}>
          <div style={{ width: `${Math.min(100, w.pct)}%`, height: '100%', background: c.fill }} />
        </div>
      )}
    </div>
  );
}

// «Использование»: окна лимитов по каждому аккаунту Claude из пула + плашки провайдеров
// (балансы DeepSeek/fal; GLM без балансового API — процент 5-часового окна) и fal.
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

  // Снимки сторонних провайдеров (glm/deepseek) лежат под их ключами — из окон Claude
  // исключаем, это лимиты чужих эндпоинтов
  const provKeys = new Set(Object.keys(usage?.providers ?? {}));
  const claudeSnaps = (usage?.snapshots ?? []).filter(s => !s.subscriptionKey || !provKeys.has(s.subscriptionKey));

  // Пул подписок → секция окон (5ч + неделя) на каждый аккаунт; без пула — один блок без имени
  const subs = usage?.subscriptions;
  const accounts = subs
    ? Object.entries(subs)
        .map(([key, s]) => ({ key, name: s.name ?? (key === 'claude' ? 'Claude' : key), windows: latestPerWindow(s.snapshots ?? []) }))
        .filter(a => a.windows.length > 0)
    : claudeSnaps.length > 0
      ? [{ key: 'claude', name: null as string | null, windows: latestPerWindow(claudeSnaps) }]
      : [];

  // Плашки провайдеров без балансового API (GLM): процент 5-часового окна из их снимков
  const limitChips = cliProviderKeys()
    .filter(k => !providerCapsByKey(k).hasBalance)
    .map(k => ({ key: k, worst: worstWindow(latestPerWindow(usage?.providers?.[k] ?? [])) }))
    .filter((c): c is { key: string; worst: RateWindow } => !!c.worst);

  const empty = accounts.length === 0 && balances.length === 0 && limitChips.length === 0;

  return (
    <WidgetCard
      icon={<Gauge size={16} strokeWidth={2} />}
      title="Использование"
      action={<WidgetAction label="Подробнее →" onClick={() => setShowUsage(true)} />}
    >
      {empty && <WidgetEmpty text="Данных об использовании пока нет." />}
      {accounts.length > 0 && (
        <div style={{ display: 'flex', flexDirection: 'column', gap: 11 }}>
          {accounts.map(a => (
            <div key={a.key}>
              {a.name && (
                <div style={{ fontFamily: FONT.sans, fontSize: 11, fontWeight: 700, color: C.textMuted, marginBottom: 5 }}>
                  {a.name}
                </div>
              )}
              <div style={{ display: 'flex', flexDirection: 'column', gap: 9 }}>
                {a.windows.map(w => <WindowRow key={w.limitType} w={w} />)}
              </div>
            </div>
          ))}
        </div>
      )}
      {(balances.length > 0 || limitChips.length > 0) && (
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
          {limitChips.map(({ key, worst: w }) => (
            <div key={key} style={{
              display: 'flex', alignItems: 'baseline', gap: 6, borderRadius: 10,
              padding: '7px 11px', background: C.bgCard, border: `1px solid ${C.borderLight}`,
            }}>
              <span style={{
                fontFamily: w.hasUtil ? FONT.mono : FONT.sans, fontSize: w.hasUtil ? 15 : 12, fontWeight: 700,
                color: w.level === 'normal' ? C.textHeading : RATE_COLORS[w.level].text,
              }}>
                {w.hasUtil ? `${w.pct}%` : 'в норме'}
              </span>
              <span style={{ fontFamily: FONT.sans, fontSize: 11.5, color: C.textMuted }}>
                {providerLabel(key)} · {windowLabel(w.limitType)}
              </span>
            </div>
          ))}
        </div>
      )}
      {showUsage && <UsageScreen onClose={() => setShowUsage(false)} />}
    </WidgetCard>
  );
}
