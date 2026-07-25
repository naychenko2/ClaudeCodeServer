// Бейдж расхода токенов чата в шапке: суммарные токены за жизнь чата, дельта после
// завершения хода, у бесплатной модели — зелёный вариант «бесплатно». Данные —
// /api/spend/sessions/{id}/badge; рефетч по росту числа result-сообщений ленты
// (тот же сигнал SignalR, которым обновляется лента). Клик — поповер с разбивкой
// и переходом в раздел «Аналитика токенов» с фильтром по этому чату.
import { useEffect, useRef, useState } from 'react';
import type { SpendBadgeResponse } from '../../types';
import { api } from '../../lib/api';
import { C, FONT, R, SHADOW } from '../../lib/design';
import { fmtTok, fmtDate, fmtTime, openSpend, sourceLabel } from '../../lib/spend';

export function SpendBadge({ sessionId, chatName, resultCount, isMobile }: {
  sessionId: string;
  chatName?: string | null;
  // Число result-сообщений в ленте — растёт по завершению хода (триггер рефетча)
  resultCount: number;
  isMobile?: boolean;
}) {
  const [badge, setBadge] = useState<SpendBadgeResponse | null>(null);
  const [delta, setDelta] = useState<number | null>(null);
  const [open, setOpen] = useState(false);
  const prevTotal = useRef<number | null>(null);

  useEffect(() => {
    let cancelled = false;
    api.spend.badge(sessionId)
      .then(d => {
        if (cancelled) return;
        // Дельта — прирост с прошлого значения В ЭТОМ чате (после завершения хода)
        if (prevTotal.current !== null && d.total.total > prevTotal.current) {
          setDelta(d.total.total - prevTotal.current);
        }
        prevTotal.current = d.total.total;
        setBadge(d);
      })
      .catch(() => { if (!cancelled) setBadge(null); });
    return () => { cancelled = true; };
  }, [sessionId, resultCount]);

  // Смена чата — прошлое значение не сравнимо
  useEffect(() => { prevTotal.current = null; setDelta(null); setBadge(null); }, [sessionId]);

  if (!badge || (badge.total.total === 0 && badge.turns === 0)) return null;

  const free = badge.lastTurn?.source === 'free';
  const bg = free ? C.successBg : C.accentLight;
  const fg = free ? C.successText : C.accent;
  const label = free
    ? `бесплатно · ${fmtTok(badge.total.total)} ткн`
    : `${fmtTok(badge.total.total)}${isMobile ? '' : ' ткн'}`;

  const openAnalytics = () => {
    setOpen(false);
    openSpend({
      screen: 'analysis',
      filters: [{ dim: 'chat', val: sessionId, label: chatName ?? 'этот чат' }],
    });
  };

  return (
    <div style={{ position: 'relative', flexShrink: 0 }}>
      <button
        type="button"
        onClick={() => setOpen(o => !o)}
        title="Расход токенов чата — нажмите для разбивки"
        style={{
          display: 'inline-flex', alignItems: 'center', gap: 5, padding: '3px 10px',
          borderRadius: R.max, border: 'none', cursor: 'pointer',
          background: bg, color: fg, fontFamily: FONT.mono, fontSize: 11, fontWeight: 600,
          whiteSpace: 'nowrap',
        }}
      >
        {label}
        {delta !== null && !free && (
          <span style={{ color: C.successText, fontFamily: FONT.sans, fontSize: 10, fontWeight: 600 }}>
            +{fmtTok(delta)}
          </span>
        )}
      </button>
      {open && (
        <>
          <div onClick={() => setOpen(false)} style={{ position: 'fixed', inset: 0, zIndex: 40 }} />
          <div style={{
            position: 'absolute', top: 'calc(100% + 6px)', right: 0, zIndex: 41, width: 250,
            background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.lg,
            boxShadow: SHADOW.dropdown, padding: '12px 14px',
          }}>
            <div style={{ fontFamily: FONT.sans, fontSize: 13, fontWeight: 700, color: C.textHeading, marginBottom: 2 }}>
              Токены чата
            </div>
            <div style={{ fontFamily: FONT.mono, fontSize: 22, fontWeight: 700, color: fg, margin: '2px 0 6px' }}>
              {fmtTok(badge.total.total)}
            </div>
            {([
              ['Ходов', String(badge.turns)],
              ['Входные токены', fmtTok(badge.total.input)],
              ['Выходные токены', fmtTok(badge.total.output)],
              ['Кэш (чтение)', fmtTok(badge.total.cacheRead)],
              ['Кэш (запись)', fmtTok(badge.total.cacheCreation)],
            ] as [string, string][]).map(([k, v]) => (
              <div key={k} style={{ display: 'flex', justifyContent: 'space-between', fontFamily: FONT.mono, fontSize: 12, color: C.textSecondary, padding: '2px 0' }}>
                <span style={{ color: C.textMuted }}>{k}</span><span style={{ fontWeight: 600 }}>{v}</span>
              </div>
            ))}
            {badge.lastTurn && (
              <div style={{ fontFamily: FONT.sans, fontSize: 10.5, color: C.textMuted, marginTop: 6 }}>
                последний ход: {fmtDate(badge.lastTurn.timestamp.slice(0, 10))} {fmtTime(badge.lastTurn.timestamp)} · {fmtTok(badge.lastTurn.tokens.total)} ткн
                {badge.lastTurn.source !== 'chat-turn' && ` · ${sourceLabel(badge.lastTurn.source).toLowerCase()}`}
              </div>
            )}
            <button
              onClick={openAnalytics}
              style={{
                marginTop: 10, width: '100%', padding: '6px 0', borderRadius: R.md,
                border: `1px solid ${C.border}`, background: 'none', cursor: 'pointer',
                fontFamily: FONT.sans, fontSize: 12, fontWeight: 600, color: C.textPrimary,
              }}
            >
              Открыть аналитику →
            </button>
          </div>
        </>
      )}
    </div>
  );
}
