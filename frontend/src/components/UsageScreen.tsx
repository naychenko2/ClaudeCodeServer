import { useEffect, useState } from 'react';
import { api } from '../lib/api';
import type { UsageResponse } from '../types';
import { C, FONT, SHADOW } from '../lib/design';
import { RATE_COLORS, windowLabel, fmtReset, latestPerWindow, seriesByWindow } from '../lib/rateLimit';

const STALE_MS = 30 * 60 * 1000;

function fmtClock(ts: number): string {
  const d = new Date(ts);
  return isNaN(d.getTime()) ? '' : d.toLocaleTimeString('ru-RU', { hour: '2-digit', minute: '2-digit' });
}

// Кольцо-donut: процент по центру, либо «—» если процент неизвестен (низкий расход — Claude его не шлёт)
function Ring({ pct, color, unknown }: { pct: number; color: string; unknown?: boolean }) {
  const r = 34, circ = 2 * Math.PI * r;
  const off = circ * (1 - Math.min(100, Math.max(0, pct)) / 100);
  return (
    <svg width="84" height="84" viewBox="0 0 84 84" style={{ flexShrink: 0 }}>
      <circle cx="42" cy="42" r={r} fill="none" stroke="#E5DCCB" strokeWidth="8" />
      {!unknown && (
        <circle cx="42" cy="42" r={r} fill="none" stroke={color} strokeWidth="8" strokeLinecap="round"
          strokeDasharray={circ} strokeDashoffset={off} transform="rotate(-90 42 42)" />
      )}
      <text x="42" y="48" textAnchor="middle"
        style={{ fontFamily: FONT.mono, fontSize: unknown ? 22 : 19, fontWeight: 700, fill: unknown ? '#9A8F7E' : color }}>
        {unknown ? '—' : `${pct}%`}
      </text>
    </svg>
  );
}

// Спарклайн тренда использования окна
function Sparkline({ points, color }: { points: { t: number; u: number }[]; color: string }) {
  if (points.length < 2) return null;
  const w = 240, h = 30, pad = 3;
  const ts = points.map(p => p.t);
  const tmin = Math.min(...ts), tmax = Math.max(...ts), span = tmax - tmin || 1;
  const xy = points.map(p => {
    const x = pad + (w - 2 * pad) * (p.t - tmin) / span;
    const y = pad + (h - 2 * pad) * (1 - Math.min(1, Math.max(0, p.u)));
    return `${x.toFixed(1)},${y.toFixed(1)}`;
  }).join(' ');
  return (
    <svg width="100%" height={h} viewBox={`0 0 ${w} ${h}`} preserveAspectRatio="none" style={{ display: 'block', maxWidth: w }}>
      <polyline points={xy} fill="none" stroke={color} strokeWidth="1.5" strokeLinejoin="round" strokeLinecap="round" />
    </svg>
  );
}

export function UsageScreen({ onClose }: { onClose: () => void }) {
  const [data, setData] = useState<UsageResponse | null>(null);

  useEffect(() => {
    let cancelled = false;
    api.usage.get()
      .then(d => { if (!cancelled) setData(d); })
      .catch(() => { if (!cancelled) setData({ snapshots: [] }); });
    return () => { cancelled = true; };
  }, []);

  const snapshots = data?.snapshots ?? null;
  const windows = snapshots ? latestPerWindow(snapshots) : [];
  const series = snapshots ? seriesByWindow(snapshots) : {};
  const latestTs = windows.reduce<number>((acc, w) => {
    const t = w.timestamp ? new Date(w.timestamp).getTime() : 0;
    return t > acc ? t : acc;
  }, 0);
  const stale = latestTs > 0 && Date.now() - latestTs > STALE_MS;
  const asOf = latestTs > 0 ? fmtClock(latestTs) : '';

  return (
    <div
      style={{ position: 'fixed', inset: 0, background: 'rgba(23,19,15,0.42)', zIndex: 1000, display: 'flex', alignItems: 'center', justifyContent: 'center', padding: 16 }}
      onClick={e => { if (e.target === e.currentTarget) onClose(); }}
    >
      <div style={{ width: '100%', maxWidth: 720, maxHeight: '85vh', background: '#FFFFFF', borderRadius: 20, boxShadow: SHADOW.dropdown, display: 'flex', flexDirection: 'column', overflow: 'hidden' }}>
        {/* Шапка */}
        <div style={{ padding: '14px 20px', borderBottom: `1px solid ${C.border}`, display: 'flex', alignItems: 'center', justifyContent: 'space-between', flexShrink: 0, gap: 10 }}>
          <div style={{ display: 'flex', alignItems: 'baseline', gap: 10, minWidth: 0, flexWrap: 'wrap' }}>
            <span style={{ fontSize: 15, fontWeight: 700, color: C.textHeading, fontFamily: FONT.sans }}>Использование подписки</span>
            {data?.plan && (
              <span style={{ fontFamily: FONT.sans, fontSize: 11, fontWeight: 700, color: '#B05C38', background: '#F1DDD1', borderRadius: 6, padding: '2px 7px' }}>
                {data.plan.label}
              </span>
            )}
            {asOf && (
              <span title="Данные обновляются с ответами Claude — запросить отдельно нельзя"
                style={{ fontFamily: FONT.mono, fontSize: 11.5, color: C.textMuted }}>
                по сост. {asOf} ⟳
              </span>
            )}
          </div>
          <button onClick={onClose} style={{ border: 'none', background: 'none', cursor: 'pointer', color: C.textMuted, fontSize: 18, padding: '0 4px', borderRadius: 6, flexShrink: 0 }}>✕</button>
        </div>

        {/* Тело */}
        <div style={{ flex: 1, overflow: 'auto', padding: '8px 20px 18px' }}>
          {snapshots === null ? (
            <div style={{ padding: '40px 0', textAlign: 'center', color: C.textMuted, fontFamily: FONT.sans, fontSize: 13 }}>Загрузка…</div>
          ) : windows.length === 0 ? (
            <div style={{ padding: '36px 12px', textAlign: 'center', color: C.textMuted, fontFamily: FONT.sans, fontSize: 13, lineHeight: 1.5 }}>
              <div style={{ fontSize: 26, marginBottom: 8, opacity: 0.5 }}>◌</div>
              Данных пока нет.<br />Лимиты приходят с ответом Claude — отправьте сообщение в любом чате, и здесь появятся данные.
            </div>
          ) : (
            <div style={{ opacity: stale ? 0.55 : 1 }}>
              {stale && (
                <div style={{ fontFamily: FONT.sans, fontSize: 11.5, color: '#9A6B1E', background: '#FBF0DC', border: '1px solid #EAD2A0', borderRadius: 8, padding: '6px 10px', margin: '8px 0' }}>
                  Снимок старше 30 минут — возможно, неактуально. Обновится после следующего ответа Claude.
                </div>
              )}
              {windows.map((w, i) => {
                const c = RATE_COLORS[w.level];
                const reset = fmtReset(w.resetsAt);
                const overage = w.isUsingOverage || (!!w.overageStatus && w.overageStatus !== 'allowed');
                return (
                  <div key={w.limitType} style={{ display: 'flex', gap: 18, alignItems: 'center', padding: '16px 4px', borderTop: i === 0 ? 'none' : `1px solid ${C.bgInset}` }}>
                    <Ring pct={w.pct} color={c.fill} unknown={!w.hasUtil} />
                    <div style={{ flex: 1, minWidth: 0 }}>
                      <div style={{ fontFamily: FONT.sans, fontWeight: 700, fontSize: 14, color: C.textHeading }}>
                        {windowLabel(w.limitType)}
                      </div>
                      <div style={{ fontFamily: FONT.sans, fontSize: 12, color: C.textMuted, margin: '2px 0 8px' }}>
                        {!w.hasUtil && 'в пределах нормы'}
                        {!w.hasUtil && reset && ' · '}
                        {reset && `сброс ${reset}`}
                        {!reset && w.hasUtil && 'время сброса неизвестно'}
                      </div>
                      {overage && (
                        <div style={{ fontFamily: FONT.sans, fontSize: 11.5, color: RATE_COLORS.danger.text, marginBottom: 6 }}>
                          {w.isUsingOverage ? '⚠ используется перерасход' : `перерасход: ${w.overageStatus}`}
                          {w.overageResetsAt && ` · сброс ${fmtReset(w.overageResetsAt)}`}
                        </div>
                      )}
                      <Sparkline points={series[w.limitType] ?? []} color={c.fill} />
                    </div>
                  </div>
                );
              })}
              <div style={{ fontFamily: FONT.sans, fontSize: 11, color: C.textMuted, marginTop: 14, lineHeight: 1.5 }}>
                Проценты приходят с ответами Claude и обычно показываются для окна, близкого к лимиту; при низком расходе — «в пределах нормы» (точный % Claude Code не отдаёт). Стоимость по API-тарифу — в бейдже «Claude» в шапке чата.
              </div>
            </div>
          )}
        </div>
      </div>
    </div>
  );
}
