// Виджет «Токены» на дашборде «Домой»: расход за сегодня, неделя и темп
// (кольцо — доля обычного дневного объёма). Клик ведёт в раздел «Аналитика токенов».
// Обновление — по завершению ходов (status_changed SignalR) + страховочный поллинг.
import { useEffect, useState } from 'react';
import { Coins } from 'lucide-react';
import type { SpendWidgetResponse } from '../../types';
import { api } from '../../lib/api';
import { C, FONT, R } from '../../lib/design';
import { onMessage, onReconnected } from '../../lib/signalr';
import { fmtTok, openSpend } from '../../lib/spend';
import { WidgetCard, WidgetAction, WidgetEmpty } from './WidgetCard';

const POLL_MS = 60_000;

export function SpendWidget() {
  const [data, setData] = useState<SpendWidgetResponse | null>(null);
  const [failed, setFailed] = useState(false);

  useEffect(() => {
    let cancelled = false;
    const fetchWidget = () => {
      api.spend.widget()
        .then(d => { if (!cancelled) { setData(d); setFailed(false); } })
        .catch(() => { if (!cancelled) setFailed(true); });
    };
    fetchWidget();
    const timer = setInterval(fetchWidget, POLL_MS);
    // Ход завершился → статус сессии меняется → сводка трат могла вырасти
    const offMessage = onMessage(msg => { if (msg.type === 'status_changed') fetchWidget(); });
    const offReconnected = onReconnected(fetchWidget);
    return () => { cancelled = true; clearInterval(timer); offMessage(); offReconnected(); };
  }, []);

  const open = () => openSpend();

  let body;
  if (failed && !data) {
    body = <WidgetEmpty text="Сводка трат не ответила — попробуйте позже." />;
  } else if (!data) {
    body = <div className="cc-skel" style={{ height: 64, borderRadius: R.lg }} />;
  } else if (data.week.total === 0 && data.weekFalGenerations === 0) {
    body = <WidgetEmpty text="Трат ещё нет — сводка появится после первого хода." />;
  } else {
    // Темп дня: сегодня против среднего дневного объёма недели
    const avgDay = data.week.total / 7;
    const pace = avgDay > 0 ? Math.round(data.today.total / avgDay * 100) : 0;
    const ring = Math.min(100, pace);
    body = (
      <div onClick={open} style={{ display: 'flex', gap: 14, alignItems: 'center', cursor: 'pointer' }}>
        <div style={{
          width: 64, height: 64, borderRadius: R.full, flexShrink: 0,
          background: `conic-gradient(${C.accent} ${ring}%, ${C.bgSelected} 0)`,
          display: 'flex', alignItems: 'center', justifyContent: 'center',
        }}>
          <div style={{
            width: 48, height: 48, borderRadius: R.full, background: C.bgWhite,
            display: 'flex', alignItems: 'center', justifyContent: 'center',
            fontSize: 11, fontFamily: FONT.mono, color: C.textSecondary,
          }}>
            {pace}%
          </div>
        </div>
        <div style={{ minWidth: 0 }}>
          <div style={{ fontFamily: FONT.mono, fontSize: 26, fontWeight: 600, color: C.accent, lineHeight: 1.1 }}>
            {fmtTok(data.today.total)}
          </div>
          <div style={{ fontSize: 11, color: C.textSecondary, fontFamily: FONT.sans, marginTop: 3 }}>
            сегодня · {data.todayTurns} ходов · неделя {fmtTok(data.week.total)}
          </div>
          <div style={{ fontSize: 11, color: C.textMuted, fontFamily: FONT.sans, marginTop: 4 }}>
            темп: {pace}% обычного дня
            {data.weekFalGenerations > 0 && <span style={{ color: C.planText }}> · fal {data.weekFalGenerations} ген.</span>}
          </div>
        </div>
      </div>
    );
  }

  return (
    <WidgetCard
      icon={<Coins size={16} strokeWidth={2} />}
      title="Токены"
      action={<WidgetAction label="Аналитика →" onClick={open} />}
    >
      {body}
    </WidgetCard>
  );
}
