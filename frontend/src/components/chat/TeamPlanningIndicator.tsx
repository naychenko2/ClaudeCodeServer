import { useEffect, useState } from 'react';
import { C, FS, R, SHADOW } from '../../lib/design';
import { TEAM_PLANNING_TITLE, TEAM_PLANNING_TEXT, teamPlanningElapsedLabel } from '../../lib/teamImplement';

// Спокойная плашка в ленте на стадии планирования режима «Командная реализация».
// Пауза между концом интервью и карточкой плана занимает минуты (потолок планировщика
// 300с), и молчащая лента читается как «всё встало» (прод 2026-08-04): человек не
// понимал, идёт работа или нет. Плашка показывает, что штаб готовит план, и исчезает
// с появлением карточки плана/отказа — видимость считает ChatPanel, сам компонент
// только рисуется и ведёт отсчёт.
//
// Отсчёт времени — тот же приём, что у фаз workflow-карточки («работает N мин»),
// но точку старта берём из события team_planning, когда она есть (см. ChatState.teamPlanning) —
// она переживает ремонт плашки внутри одной живой сессии вкладки (переключение вкладок
// туда-обратно не сбрасывает счёт, в отличие от Date.now() при монтировании). startedAt не
// пришёл (чат открыли уже посреди планирования, событий не видели) — считаем от монтирования,
// как раньше; после ПЕРЕЗАГРУЗКИ страницы счёт в любом случае идёт с нуля — таймстампа начала
// стадии на проводе нет.
export function TeamPlanningIndicator({ startedAt: liveStartedAt }: { startedAt?: number }) {
  // Точка отсчёта фиксируется один раз при монтировании (ленивый инициализатор useState,
  // а не эффект) — иначе синхронный setState в теле эффекта плодит лишний рендер
  const [startedAt] = useState(() => liveStartedAt ?? Date.now());
  const [elapsed, setElapsed] = useState(() => teamPlanningElapsedLabel(startedAt, Date.now()));
  useEffect(() => {
    const t = setInterval(() => setElapsed(teamPlanningElapsedLabel(startedAt, Date.now())), 10_000);
    return () => clearInterval(t);
  }, [startedAt]);

  return (
    <div
      data-testid="team-planning-indicator"
      style={{
        border: `1px solid ${C.border}`, borderRadius: R.xl,
        background: C.bgCard, boxShadow: SHADOW.card,
        padding: '10px 14px',
        display: 'flex', alignItems: 'center', gap: 10,
      }}
    >
      <div className="tool-spinner" style={{ width: 14, height: 14, flexShrink: 0 }} />
      <div style={{ flex: 1, minWidth: 0 }}>
        <div style={{ fontSize: FS.base, fontWeight: 600, color: C.textPrimary, lineHeight: 1.3 }}>
          {TEAM_PLANNING_TITLE}
        </div>
        <div style={{ fontSize: FS.sm, color: C.textSecondary, lineHeight: 1.45, marginTop: 1 }}>
          {TEAM_PLANNING_TEXT}
        </div>
      </div>
      <span
        data-testid="team-planning-elapsed"
        style={{ fontSize: FS.xs, color: C.textMuted, whiteSpace: 'nowrap', flexShrink: 0 }}
      >
        {elapsed}
      </span>
    </div>
  );
}
