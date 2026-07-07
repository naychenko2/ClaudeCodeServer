import { useEffect, useState } from 'react';
import { api } from '../lib/api';
import type { UsageResponse, FalAccountResponse } from '../types';
import { C, FONT, SHADOW } from '../lib/design';
import { type RateWindow, RATE_COLORS, windowLabel, fmtReset, latestPerWindow, seriesByWindow, worstWindow } from '../lib/rateLimit';
import { cliProviderKeys, providerCapsByKey, providerLabel } from '../lib/models';

const STALE_MS = 30 * 60 * 1000;
const MONEY = C.accent;
const LOW_BALANCE = 5;

const money = (c: number) => '$' + (c < 0.01 ? c.toFixed(4) : c < 1 ? c.toFixed(3) : c.toFixed(2));
const shortModel = (ep: string) => ep.split('/').slice(-2).join('/');
const fmtDay = (iso: string) => { const d = new Date(iso); return isNaN(d.getTime()) ? iso : String(d.getDate()); };

// Метрик-карточка: крупная цифра + лейбл + опц. содержимое (бар/подпись)
function MetricCard({ value, label, valueColor = C.textHeading, tone, children }: {
  value: string; label: string; valueColor?: string; tone?: 'warn' | 'danger'; children?: React.ReactNode;
}) {
  const bg = tone === 'warn' ? C.warningBg : tone === 'danger' ? C.dangerBg : C.bgWhite;
  const bd = tone === 'warn' ? C.warning : tone === 'danger' ? C.dangerBorder : C.border;
  return (
    <div style={{ background: bg, border: `1px solid ${bd}`, borderRadius: 12, padding: '13px 15px', minWidth: 0 }}>
      <div style={{ fontFamily: FONT.mono, fontSize: 24, fontWeight: 700, color: valueColor, lineHeight: 1.1, overflow: 'hidden', textOverflow: 'ellipsis' }}>{value}</div>
      <div style={{ fontFamily: FONT.sans, fontSize: 12, color: C.textMuted, marginTop: 3 }}>{label}</div>
      {children}
    </div>
  );
}

function Sparkline({ points, color }: { points: { t: number; u: number }[]; color: string }) {
  if (points.length < 2) return null;
  const w = 560, h = 44, pad = 3;
  const ts = points.map(p => p.t);
  const tmin = Math.min(...ts), tmax = Math.max(...ts), span = tmax - tmin || 1;
  const xy = points.map(p => {
    const x = pad + (w - 2 * pad) * (p.t - tmin) / span;
    const y = pad + (h - 2 * pad) * (1 - Math.min(1, Math.max(0, p.u)));
    return `${x.toFixed(1)},${y.toFixed(1)}`;
  }).join(' ');
  return (
    <svg width="100%" height={h} viewBox={`0 0 ${w} ${h}`} preserveAspectRatio="none" style={{ display: 'block' }}>
      <polyline points={xy} fill="none" stroke={color} strokeWidth="2" strokeLinejoin="round" strokeLinecap="round" />
    </svg>
  );
}

const blockLabel: React.CSSProperties = { fontFamily: FONT.sans, fontSize: 13, fontWeight: 500, color: C.textHeading, margin: '16px 0 7px' };

function WindowCard({ w }: { w: RateWindow }) {
  const c = RATE_COLORS[w.level];
  const tone = w.level === 'warn' ? 'warn' as const : w.level === 'danger' ? 'danger' as const : undefined;
  const reset = fmtReset(w.resetsAt);
  const sub = w.hasUtil
    ? (reset ? `сброс ${reset}` : '')
    : `в пределах нормы${reset ? ` · сброс ${reset}` : ''}`;
  const overage = w.isUsingOverage || (!!w.overageStatus && w.overageStatus !== 'allowed');
  return (
    <MetricCard
      value={w.hasUtil ? `${w.pct}%` : '—'}
      valueColor={w.hasUtil ? (w.level === 'normal' ? C.textHeading : c.text) : C.textMuted}
      label={windowLabel(w.limitType)}
      tone={tone}
    >
      {w.hasUtil && (
        <div style={{ height: 6, borderRadius: 3, background: C.track, overflow: 'hidden', margin: '8px 0 5px' }}>
          <div style={{ width: `${Math.min(100, w.pct)}%`, height: '100%', background: c.fill }} />
        </div>
      )}
      <div style={{ fontSize: 11, color: C.textMuted, marginTop: w.hasUtil ? 0 : 12 }}>{sub}</div>
      {overage && (
        <div style={{ fontSize: 11, color: C.dangerText, marginTop: 4 }}>
          {w.isUsingOverage ? '⚠ перерасход' : `перерасход: ${w.overageStatus}`}
          {w.overageResetsAt && ` · сброс ${fmtReset(w.overageResetsAt)}`}
        </div>
      )}
    </MetricCard>
  );
}

function ClaudeTab({ usage }: { usage: UsageResponse | null }) {
  const snapshots = usage?.snapshots ?? null;
  const windows = snapshots ? latestPerWindow(snapshots) : [];
  const series = snapshots ? seriesByWindow(snapshots) : {};
  const latestTs = windows.reduce<number>((a, w) => { const t = w.timestamp ? new Date(w.timestamp).getTime() : 0; return t > a ? t : a; }, 0);
  const stale = latestTs > 0 && Date.now() - latestTs > STALE_MS;
  const worst = worstWindow(windows);
  const trend = worst ? (series[worst.limitType] ?? []) : [];

  if (snapshots === null)
    return <div style={{ padding: '40px 0', textAlign: 'center', color: C.textMuted, fontSize: 13 }}>Загрузка…</div>;
  if (windows.length === 0)
    return (
      <div style={{ padding: '36px 12px', textAlign: 'center', color: C.textMuted, fontSize: 12.5, lineHeight: 1.5 }}>
        <div style={{ fontSize: 24, marginBottom: 6, opacity: 0.5 }}>◌</div>
        Лимиты приходят с ответом Claude — отправьте сообщение в любом чате, и здесь появятся данные.
      </div>
    );
  return (
    <div style={{ opacity: stale ? 0.6 : 1 }}>
      {stale && (
        <div style={{ fontSize: 11.5, color: C.warningText, background: C.warningBg, border: `1px solid ${C.warning}`, borderRadius: 8, padding: '6px 10px', marginBottom: 12 }}>
          Снимок старше 30 минут — возможно, неактуально. Обновится после следующего ответа Claude.
        </div>
      )}
      <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(180px, 1fr))', gap: 10 }}>
        {windows.map(w => <WindowCard key={w.limitType} w={w} />)}
      </div>
      {trend.length >= 2 && worst && (
        <>
          <div style={blockLabel}>Тренд · {windowLabel(worst.limitType)}</div>
          <Sparkline points={trend} color={RATE_COLORS[worst.level].fill} />
        </>
      )}
      <div style={{ fontSize: 11, color: C.textMuted, marginTop: 14, lineHeight: 1.5 }}>
        Процент приходит с ответами Claude, обычно для окна у лимита; при низком расходе — «в пределах нормы».
      </div>
    </div>
  );
}

type ProviderUsage = {
  balance: { available: boolean; currency: string; totalBalance: string } | null;
  snapshots: { timestamp: string; balance: number; currency: string }[];
};

// Ссылки пополнения и кабинетов по провайдерам (для карточки баланса / заглушки)
const TOPUP_URL: Record<string, string> = {
  deepseek: 'https://platform.deepseek.com/top_up',
};
const CABINET_URL: Record<string, string> = {
  glm: 'https://z.ai/manage-apikey/rate-limits',
  deepseek: 'https://platform.deepseek.com/usage',
};

function ProviderTab({ providerKey, data }: { providerKey: string; data: ProviderUsage | null | undefined }) {
  const name = providerLabel(providerKey);
  // Провайдер без балансового API (GLM): показываем пояснение вместо пустой вкладки
  if (!providerCapsByKey(providerKey).hasBalance)
    return (
      <div style={{ padding: '36px 12px', textAlign: 'center', color: C.textMuted, fontSize: 12.5, lineHeight: 1.5 }}>
        <div style={{ fontSize: 24, marginBottom: 6, opacity: 0.5 }}>◌</div>
        {name} не предоставляет API баланса и расхода — квоты и статистику смотрите в кабинете провайдера.
        {CABINET_URL[providerKey] && (
          <div style={{ marginTop: 10 }}>
            <a href={CABINET_URL[providerKey]} target="_blank" rel="noopener noreferrer"
              style={{ color: C.accent, fontSize: 12, fontWeight: 600, textDecoration: 'none' }}>кабинет {name} ↗</a>
          </div>
        )}
      </div>
    );
  if (data === undefined)
    return <div style={{ padding: '40px 0', textAlign: 'center', color: C.textMuted, fontSize: 13 }}>Загрузка…</div>;
  if (data === null)
    return (
      <div style={{ padding: '36px 12px', textAlign: 'center', color: C.textMuted, fontSize: 12.5, lineHeight: 1.5 }}>
        <div style={{ fontSize: 24, marginBottom: 6, opacity: 0.5 }}>◌</div>
        {name} не подключён. Добавьте API-ключ (<span style={{ fontFamily: FONT.mono }}>LlmProviders:{providerKey}:ApiKey</span>), чтобы видеть баланс и расход.
      </div>
    );

  const snaps = data.snapshots ?? [];
  const cur = data.balance ? parseFloat(data.balance.totalBalance) : NaN;
  const currency = data.balance?.currency ?? snaps[snaps.length - 1]?.currency ?? 'USD';
  const low = !isNaN(cur) && cur < 1;
  // Расход ≈ сумма падений баланса между снапшотами (пополнения не учитываются как «отрицательный расход»)
  let spent = 0;
  for (let i = 1; i < snaps.length; i++) {
    const d = snaps[i - 1].balance - snaps[i].balance;
    if (d > 0) spent += d;
  }
  const maxBal = Math.max(...snaps.map(s => s.balance), isNaN(cur) ? 0 : cur, 0.0001);
  const points = snaps.map(s => ({ t: new Date(s.timestamp).getTime(), u: s.balance / maxBal }));

  return (
    <div>
      <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(180px, 1fr))', gap: 10 }}>
        <MetricCard
          value={isNaN(cur) ? '—' : `${cur.toFixed(2)} ${currency}`}
          label={`баланс ${name}`}
          valueColor={low ? C.dangerText : MONEY}
          tone={low ? 'danger' : undefined}
        >
          {TOPUP_URL[providerKey] && (
            <a href={TOPUP_URL[providerKey]} target="_blank" rel="noopener noreferrer"
              style={{ display: 'inline-block', marginTop: 8, color: C.accent, fontSize: 11, fontWeight: 600, textDecoration: 'none' }}>пополнить ↗</a>
          )}
        </MetricCard>
        <MetricCard value={spent > 0 ? `≈ ${spent.toFixed(2)} ${currency}` : '—'} label="расход за период наблюдения" valueColor={MONEY} />
      </div>
      {points.length >= 2 && (
        <>
          <div style={blockLabel}>Баланс во времени</div>
          <Sparkline points={points} color={MONEY} />
        </>
      )}
      <div style={{ fontSize: 11, color: C.textMuted, marginTop: 14, lineHeight: 1.5 }}>
        Снимки баланса пишутся при каждом обновлении (примерно раз в 5 минут при активной работе), хранятся 8 дней.
      </div>
    </div>
  );
}

function FalTab({ days, setDays }: { days: number; setDays: (d: number) => void }) {
  const [fal, setFal] = useState<FalAccountResponse | null>(null);
  const [loading, setLoading] = useState(true);
  useEffect(() => {
    let c = false; setLoading(true);
    api.fal.account(days)
      .then(d => { if (!c) { setFal(d); setLoading(false); } })
      .catch(() => { if (!c) { setFal({ enabled: false }); setLoading(false); } });
    return () => { c = true; };
  }, [days]);

  const enabled = fal?.enabled !== false;
  const balance = fal?.balance ?? null;
  const lowBal = typeof balance === 'number' && balance < LOW_BALANCE;
  const u = fal?.usage ?? null;
  const spent = u?.total ?? 0;
  const models = u?.byModel ?? [];
  const series = u?.series ?? [];
  const maxModel = models.reduce((m, x) => Math.max(m, x.cost), 0) || 1;
  const maxDay = series.reduce((m, x) => Math.max(m, x.cost), 0) || 1;
  const top = models.slice(0, 6);
  const restCount = models.length - top.length;
  const restSum = models.slice(6).reduce((s, x) => s + x.cost, 0);

  return (
    <div>
      <div style={{ display: 'flex', justifyContent: 'flex-end', marginBottom: 10 }}>
        {enabled && (
          <span style={{ display: 'inline-flex', border: `1px solid ${C.border}`, borderRadius: 7, overflow: 'hidden' }}>
            {[7, 30].map(d => (
              <button key={d} type="button" onClick={() => setDays(d)}
                style={{ padding: '3px 11px', border: 'none', cursor: 'pointer', fontFamily: FONT.sans, fontSize: 11, fontWeight: days === d ? 700 : 500, background: days === d ? C.accentMuted : C.bgWhite, color: days === d ? MONEY : C.textMuted }}>{d}д</button>
            ))}
          </span>
        )}
      </div>
      {loading && !fal ? (
        <div style={{ padding: '40px 0', textAlign: 'center', color: C.textMuted, fontSize: 13 }}>Загрузка…</div>
      ) : !enabled ? (
        <div style={{ padding: '36px 12px', textAlign: 'center', color: C.textMuted, fontSize: 12.5, lineHeight: 1.5 }}>
          <div style={{ fontSize: 24, marginBottom: 6, opacity: 0.5 }}>◌</div>
          fal.ai не подключён. Добавьте API-ключ (<span style={{ fontFamily: FONT.mono }}>Fal:ApiKey</span> / <span style={{ fontFamily: FONT.mono }}>FAL_API_KEY</span>), чтобы видеть баланс и расход.
        </div>
      ) : (
        <div style={{ opacity: loading ? 0.5 : 1 }}>
          <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(180px, 1fr))', gap: 10 }}>
            <MetricCard value={typeof balance === 'number' ? money(balance) : '—'} label="баланс fal.ai" valueColor={lowBal ? C.dangerText : MONEY} tone={lowBal ? 'danger' : undefined}>
              <a href="https://fal.ai/dashboard/billing" target="_blank" rel="noopener noreferrer"
                style={{ display: 'inline-block', marginTop: 8, color: C.accent, fontSize: 11, fontWeight: 600, textDecoration: 'none' }}>пополнить ↗</a>
            </MetricCard>
            <MetricCard value={money(spent)} label={`расход за ${days} дней`} valueColor={MONEY} />
          </div>
          {typeof balance === 'number' && (spent > 0 || balance > 0) && (() => {
            const tot = spent + balance; const sp = tot > 0 ? Math.max(2, Math.round((spent / tot) * 100)) : 0;
            return (
              <div style={{ marginTop: 10 }}>
                <div style={{ height: 8, borderRadius: 4, overflow: 'hidden', background: C.track }}><div style={{ width: `${sp}%`, height: '100%', background: MONEY }} /></div>
                <div style={{ display: 'flex', justifyContent: 'space-between', fontSize: 11, color: C.textMuted, marginTop: 4 }}>
                  <span>потрачено {money(spent)}</span><span>остаток {typeof balance === 'number' ? money(balance) : '—'}</span>
                </div>
              </div>
            );
          })()}

          {spent <= 0 ? (
            <div style={{ padding: '20px 8px', textAlign: 'center', color: C.textMuted, fontSize: 12.5 }}>За {days} дней генераций не было.</div>
          ) : (
            <>
              <div style={blockLabel}>По моделям</div>
              {top.map(m => (
                <div key={m.endpointId} style={{ display: 'flex', alignItems: 'center', gap: 10, marginBottom: 6 }}>
                  <span style={{ fontSize: 12, color: C.textSecondary, width: 140, flexShrink: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{shortModel(m.endpointId)}</span>
                  <span style={{ flex: 1, height: 7, background: C.track, borderRadius: 4, overflow: 'hidden' }}>
                    <span style={{ display: 'block', width: `${Math.max(3, Math.round((m.cost / maxModel) * 100))}%`, height: '100%', background: MONEY }} />
                  </span>
                  <span style={{ fontFamily: FONT.mono, fontSize: 12, fontWeight: 700, color: MONEY, width: 50, textAlign: 'right', flexShrink: 0 }}>{money(m.cost)}</span>
                </div>
              ))}
              {restCount > 0 && (
                <div style={{ display: 'flex', justifyContent: 'space-between', fontSize: 12, color: C.textMuted, marginTop: 4 }}>
                  <span>…ещё {restCount}</span><span style={{ fontFamily: FONT.mono }}>{money(restSum)}</span>
                </div>
              )}
              {series.length > 0 && (
                <>
                  <div style={blockLabel}>Расход по дням</div>
                  <div style={{ display: 'flex', alignItems: 'flex-end', gap: 3, height: 70 }}>
                    {series.map(d => {
                      const h = Math.max(2, Math.round((d.cost / maxDay) * 64));
                      return (
                        <div key={d.date} title={`${d.date}: ${money(d.cost)}`} style={{ flex: 1, display: 'flex', flexDirection: 'column', alignItems: 'center', gap: 3, minWidth: 0 }}>
                          <div style={{ width: '100%', maxWidth: 26, height: h, background: d.cost >= maxDay ? C.accent : C.accentMuted, borderRadius: 2 }} />
                          <span style={{ fontFamily: FONT.mono, fontSize: 9.5, color: C.textMuted }}>{fmtDay(d.date)}</span>
                        </div>
                      );
                    })}
                  </div>
                </>
              )}
            </>
          )}
        </div>
      )}
    </div>
  );
}

export function UsageScreen({ onClose }: { onClose: () => void }) {
  const [tab, setTab] = useState<string>('claude');
  const [usage, setUsage] = useState<UsageResponse | null>(null);
  const [days, setDays] = useState(7);
  const [falBalance, setFalBalance] = useState<number | null | undefined>(undefined); // для строки-сводки + бейджа вкладки
  // По каждому CLI-провайдеру с балансовым API: undefined — грузим, null — недоступен
  const [provData, setProvData] = useState<Record<string, ProviderUsage | null | undefined>>({});
  const providerKeys = cliProviderKeys();

  useEffect(() => {
    let c = false;
    api.usage.get().then(d => { if (!c) setUsage(d); }).catch(() => { if (!c) setUsage({ snapshots: [] }); });
    api.fal.account(7).then(d => { if (!c) setFalBalance(d.enabled ? (d.balance ?? null) : null); }).catch(() => { if (!c) setFalBalance(null); });
    for (const key of cliProviderKeys()) {
      if (!providerCapsByKey(key).hasBalance) continue; // без балансового API — заглушка в ProviderTab
      api.providers.usage(key)
        .then(d => { if (!c) setProvData(prev => ({ ...prev, [key]: d })); })
        .catch(() => { if (!c) setProvData(prev => ({ ...prev, [key]: null })); });
    }
    return () => { c = true; };
  }, []);

  const windows = usage?.snapshots ? latestPerWindow(usage.snapshots) : [];
  const worst = worstWindow(windows);
  const plan = usage?.plan;
  const lowBal = typeof falBalance === 'number' && falBalance < LOW_BALANCE;
  const provBalanceOf = (key: string) => {
    const b = provData[key]?.balance;
    return b ? parseFloat(b.totalBalance) : NaN;
  };
  const provLow = (key: string) => { const b = provBalanceOf(key); return !isNaN(b) && b < 1; };
  // Вкладка на каждый настроенный CLI-провайдер (в каталог попадают только с ApiKey);
  // у провайдера без балансового API внутри — пояснение с ссылкой на кабинет
  const tabs = ([['claude', 'Claude']] as [string, string][])
    .concat(providerKeys.map(k => [k, providerLabel(k)] as [string, string]))
    .concat([['fal', 'fal.ai']]);

  return (
    <div
      style={{ position: 'fixed', inset: 0, background: C.overlay, zIndex: 1000, display: 'flex', alignItems: 'center', justifyContent: 'center', padding: 16 }}
      onClick={e => { if (e.target === e.currentTarget) onClose(); }}
    >
      <div style={{ width: '100%', maxWidth: 720, maxHeight: '85vh', background: C.bgCard, borderRadius: 18, boxShadow: SHADOW.dropdown, display: 'flex', flexDirection: 'column', overflow: 'hidden' }}>
        {/* Шапка */}
        <div style={{ padding: '13px 20px 6px', display: 'flex', alignItems: 'baseline', justifyContent: 'space-between', flexShrink: 0 }}>
          <span style={{ fontSize: 15, fontWeight: 700, color: C.textHeading, fontFamily: FONT.sans }}>Использование</span>
          <button onClick={onClose} style={{ border: 'none', background: 'none', cursor: 'pointer', color: C.textMuted, fontSize: 18, padding: '0 4px', borderRadius: 6 }}>✕</button>
        </div>
        {/* Строка-сводка */}
        <div style={{ padding: '0 20px 10px', fontFamily: FONT.sans, fontSize: 12, color: C.textMuted, flexShrink: 0 }}>
          {plan && <span>{plan.label}</span>}
          {worst?.hasUtil && <span> · {windowLabel(worst.limitType)} <span style={{ color: RATE_COLORS[worst.level].text, fontWeight: 700 }}>{worst.pct}%</span></span>}
          {providerKeys.filter(k => !isNaN(provBalanceOf(k))).map(k => (
            <span key={k}> · {providerLabel(k)} <span style={{ fontFamily: FONT.mono, color: provLow(k) ? C.dangerText : MONEY, fontWeight: 700 }}>{provBalanceOf(k).toFixed(2)} {provData[k]?.balance?.currency}</span></span>
          ))}
          {typeof falBalance === 'number' && <span> · fal <span style={{ fontFamily: FONT.mono, color: lowBal ? C.dangerText : MONEY, fontWeight: 700 }}>{money(falBalance)}</span></span>}
        </div>
        {/* Вкладки */}
        <div style={{ display: 'flex', gap: 6, padding: '0 18px', borderBottom: `1px solid ${C.bgInset}`, flexShrink: 0 }}>
          {tabs.map(([key, lbl]) => (
            <button key={key} type="button" onClick={() => setTab(key)}
              style={{ padding: '7px 14px', border: 'none', background: 'none', cursor: 'pointer', fontFamily: FONT.sans, fontSize: 13,
                fontWeight: tab === key ? 700 : 500, color: tab === key ? C.textHeading : C.textMuted,
                borderBottom: `2px solid ${tab === key ? C.accent : 'transparent'}`, marginBottom: -1, display: 'flex', alignItems: 'center', gap: 6 }}>
              {lbl}
              {key === 'fal' && lowBal && <span style={{ width: 6, height: 6, borderRadius: '50%', background: C.danger }} />}
              {key !== 'fal' && key !== 'claude' && provLow(key) && <span style={{ width: 6, height: 6, borderRadius: '50%', background: C.danger }} />}
            </button>
          ))}
        </div>
        {/* Тело вкладки */}
        <div style={{ flex: 1, overflow: 'auto', padding: '16px 20px 18px' }}>
          {tab === 'claude' ? <ClaudeTab usage={usage} />
            : tab === 'fal' ? <FalTab days={days} setDays={setDays} />
            : <ProviderTab providerKey={tab} data={provData[tab]} />}
        </div>
      </div>
    </div>
  );
}
