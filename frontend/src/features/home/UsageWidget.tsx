import { useEffect, useState } from 'react';
import { Gauge } from 'lucide-react';
import type { UsageResponse } from '../../types';
import { api } from '../../lib/api';
import { C, FONT } from '../../lib/design';
import { type RateWindow, RATE_COLORS, windowLabel, fmtReset, latestPerWindow, worstWindow } from '../../lib/rateLimit';
import { cliProviderKeys, providerCapsByKey, providerLabel } from '../../lib/models';
import { ModelsSpendModal } from '../modelsSpend/ModelsSpendModal';
import { WidgetCard, WidgetAction, WidgetEmpty } from './WidgetCard';

// Балансы инертны — раз в 5 минут достаточно
const POLL_MS = 5 * 60_000;
const LOW_BALANCE = 5;
// Тот же «мало осталось», но в рублях: доллар и рубль на одной шкале сравнивать нельзя —
// 5 ₽ это не «мало», это уже почти ноль
const LOW_BALANCE_RUB = 300;

// Строка окна лимита: название, время сброса, «израсходовано N%» (или «в пределах
// нормы») + шкала. Доля всегда подписана словом расхода — голый процент рядом со
// временем сброса читался как остаток (та же инверсия, что вычищена в модалке и шапке чата)
function WindowRow({ w }: { w: RateWindow }) {
  const c = RATE_COLORS[w.level];
  const reset = fmtReset(w.resetsAt);
  return (
    <div>
      {/* flexWrap: на узкой карточке «израсходовано N%» переносится на вторую строку —
          перенос допустим, горизонтальный скролл нет */}
      <div style={{ display: 'flex', alignItems: 'baseline', gap: 8, flexWrap: 'wrap' }}>
        <span style={{ fontFamily: FONT.sans, fontSize: 12.5, color: C.textPrimary, flex: 1, minWidth: 0 }}>
          {windowLabel(w.limitType)}
        </span>
        {reset && (
          <span style={{ fontFamily: FONT.sans, fontSize: 11, color: C.textMuted }}>сброс {reset}</span>
        )}
        <span
          style={{ display: 'inline-flex', alignItems: 'baseline', gap: 5, whiteSpace: 'nowrap' }}
          // Долю CLI присылает только при приближении к лимиту, и молчание —
          // это «расход невелик», а не «ноль». Поясняем, чтобы прочерк не выглядел сбоем.
          title={w.hasUtil ? undefined : 'Claude сообщает долю использования только при приближении к лимиту'}
        >
          {w.hasUtil ? (
            <>
              <span style={{ fontFamily: FONT.sans, fontSize: 11, color: C.textMuted }}>израсходовано</span>
              <span style={{ fontFamily: FONT.mono, fontSize: 13, fontWeight: 700, color: w.level === 'normal' ? C.textHeading : c.text }}>{w.pct}%</span>
            </>
          ) : (
            <span style={{ fontFamily: FONT.sans, fontSize: 11, color: C.textMuted }}>в пределах нормы</span>
          )}
        </span>
      </div>
      {/* Шкалу рисуем ТОЛЬКО с реальными данными: пустая полоса читалась как
          «израсходовано 0%», хотя означала «доля неизвестна». Так же на экране
          «Использование» (виджет главной) */}
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
// Компактная выжимка раздела «Модели и расход»; «Подробнее» открывает полную модалку.
export function UsageWidget() {
  const [usage, setUsage] = useState<UsageResponse | null>(null);
  const [balances, setBalances] = useState<Array<{
    key: string; label: string; value: number; credits?: boolean; rub?: boolean;
  }>>([]);
  const [showUsage, setShowUsage] = useState(false);

  useEffect(() => {
    let cancelled = false;
    const fetchAll = () => {
      api.usage.get()
        .then(d => { if (!cancelled) setUsage(d); })
        .catch(() => { if (!cancelled) setUsage({ snapshots: [] }); });
      for (const key of cliProviderKeys()) {
        // Ненастроенный провайдер (configured === false) не запрашиваем — без ключа
        // баланс вернёт 404 и забьёт консоль (та же проверка, что в QuotasTab)
        const caps = providerCapsByKey(key);
        if (!caps.hasBalance || caps.configured === false) continue;
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
      // Yandex Cloud: баланс приходит строкой и только админу (не-админу поле пустое —
      // плашки просто не будет). Валюта своя, поэтому формат и порог «мало» отдельные
      api.yandex.account(30)
        .then(d => {
          const v = d.account?.balance ? parseFloat(d.account.balance) : NaN;
          if (cancelled || !d.enabled || isNaN(v)) return;
          setBalances(prev => [...prev.filter(b => b.key !== 'yandex'),
            { key: 'yandex', label: 'Yandex Cloud', value: v, rub: (d.account?.currency ?? 'RUB') === 'RUB' }]);
        })
        .catch(() => {});
      api.glif.account()
        .then(d => {
          if (cancelled || !d.enabled || typeof d.balance !== 'number') return;
          // glif считает в кредитах, не в USD — формат плашки другой
          setBalances(prev => [...prev.filter(b => b.key !== 'glif'), { key: 'glif', label: 'glif', value: d.balance!, credits: true }]);
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
                color: b.value < (b.rub ? LOW_BALANCE_RUB : LOW_BALANCE) ? C.dangerText : C.textHeading,
              }}>
                {b.credits
                  ? `${(Number.isInteger(b.value) ? b.value.toLocaleString('ru-RU') : b.value.toFixed(2))} кр.`
                  : b.rub
                    ? `${b.value.toLocaleString('ru-RU', { maximumFractionDigits: 2 })} ₽`
                    : `$${b.value < 1 ? b.value.toFixed(3) : b.value.toFixed(2)}`}
              </span>
              <span style={{ fontFamily: FONT.sans, fontSize: 11.5, color: C.textMuted }}>{b.label}</span>
            </div>
          ))}
          {limitChips.map(({ key, worst: w }) => (
            <div key={key} style={{
              display: 'flex', alignItems: 'baseline', gap: 6, borderRadius: 10,
              padding: '7px 11px', background: C.bgCard, border: `1px solid ${C.borderLight}`,
            }}>
              <span style={{ display: 'inline-flex', alignItems: 'baseline', gap: 5, whiteSpace: 'nowrap' }}>
                {w.hasUtil ? (
                  <>
                    <span style={{ fontFamily: FONT.sans, fontSize: 11, fontWeight: 400, color: C.textMuted }}>израсходовано</span>
                    <span style={{ fontFamily: FONT.mono, fontSize: 15, fontWeight: 700, color: w.level === 'normal' ? C.textHeading : RATE_COLORS[w.level].text }}>{w.pct}%</span>
                  </>
                ) : (
                  <span style={{ fontFamily: FONT.sans, fontSize: 12, fontWeight: 700, color: w.level === 'normal' ? C.textHeading : RATE_COLORS[w.level].text }}>в норме</span>
                )}
              </span>
              <span style={{ fontFamily: FONT.sans, fontSize: 11.5, color: C.textMuted }}>
                {providerLabel(key)} · {windowLabel(w.limitType)}
              </span>
            </div>
          ))}
        </div>
      )}
      {showUsage && <ModelsSpendModal onClose={() => setShowUsage(false)} />}
    </WidgetCard>
  );
}
