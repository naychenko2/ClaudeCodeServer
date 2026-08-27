import { useEffect, useState } from 'react';
import type { Persona } from '../../types';
import { AGENT_COLORS } from '../AgentSelector';
import { NEUTRAL_AGENT_ACCENT } from './AgentContentBlocks';
import { PersonaAvatar } from '../../features/personas/PersonaAvatar';
import { personaLabel } from '../../lib/personas';
import { C, FS, R, SHADOW } from '../../lib/design';
import { TEAM_PLANNING_TITLE, TEAM_PLANNING_TEXT, teamPlanningElapsedLabel } from '../../lib/teamImplement';

// Спокойная карточка «Готовит план…» в ленте на стадии планирования режима
// «Командная реализация». Пауза между концом интервью и карточкой плана занимает
// минуты (потолок планировщика 300с), и молчащая лента читалась как «всё встало»
// (прод 2026-08-04): человек не понимал, идёт работа или нет.
//
// С персоной планировщика — карточка той же формы, что PersonaConsultCard: аватар
// с цветом, имя и роль в шапке, статус «Готовит план…» и счётчик прошедших минут
// справа. Без персоны (events пришли старые, без personaId) — деградируем до прежней
// безличной плашки: лучше так, чем безымянный «Планировщик» (та же болезнь, что
// лечили в правке координатора). Гаснет сама по teamPlanningIndicatorVisible.
//
// Отсчёт времени — тот же приём, что у фаз workflow-карточки («работает N мин»),
// но точку старта берём из события team_planning, когда она есть (см. ChatState.teamPlanning) —
// она переживает ремонт плашки внутри одной живой сессии вкладки (переключение вкладок
// туда-обратно не сбрасывает счёт, в отличие от Date.now() при монтировании). startedAt не
// пришёл (чат открыли уже посреди планирования, событий не видели) — считаем от монтирования,
// как раньше; после ПЕРЕЗАГРУЗКИ страницы счёт в любом случае идёт с нуля — таймстампа начала
// стадии на проводе нет.
export function TeamPlanningIndicator({
  startedAt: liveStartedAt,
  persona,
}: {
  startedAt?: number;
  // Планировщик. null — событие пришло без personaId (старый сервер) или резолв
  // ничего не дал; компонент деградирует до безличной плашки
  persona?: Persona | null;
}) {
  // Точка отсчёта фиксируется один раз при монтировании (ленивый инициализатор useState,
  // а не эффект) — иначе синхронный setState в теле эффекта плодит лишний рендер
  const [startedAt] = useState(() => liveStartedAt ?? Date.now());
  const [elapsed, setElapsed] = useState(() => teamPlanningElapsedLabel(startedAt, Date.now()));
  useEffect(() => {
    const t = setInterval(() => setElapsed(teamPlanningElapsedLabel(startedAt, Date.now())), 10_000);
    return () => clearInterval(t);
  }, [startedAt]);

  if (persona) return <PersonaCard persona={persona} elapsed={elapsed} />;

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

// Карточка с лицом планировщика: аватар + имя/роль + статус + счётчик. Та же обёртка
// (border, фон, тень), что у безличной плашки — единый ритм ленты; внутри — лицо
// и слот для статуса со спиннером. На размонтировании (планирование кончилось)
// исчезает сама, без явного «свернуть» — карточка team_plan следом подхватывает автора
function PersonaCard({ persona, elapsed }: { persona: Persona; elapsed: string }) {
  const accent = AGENT_COLORS[persona.avatar?.color ?? ''] ?? NEUTRAL_AGENT_ACCENT;
  return (
    <div
      data-testid="team-planning-indicator"
      style={{
        border: `1px solid ${C.border}`, borderRadius: R.xl,
        background: C.bgCard, boxShadow: SHADOW.card,
        padding: '10px 12px',
        display: 'flex', alignItems: 'center', gap: 12,
      }}
    >
      <div style={{ position: 'relative', flexShrink: 0, width: 32, height: 32 }}>
        <PersonaAvatar persona={persona} size={32} />
        <div
          className="tool-spinner"
          style={{
            position: 'absolute', inset: -3,
            border: `1.5px solid ${accent}`,
            borderTopColor: 'transparent',
            borderRadius: '50%',
          }}
        />
      </div>
      <div style={{ flex: 1, minWidth: 0 }}>
        <div style={{
          fontSize: FS.base, fontWeight: 600, color: C.textHeading, lineHeight: 1.3,
          whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis',
        }}>
          {personaLabel(persona)}
        </div>
        <div style={{ fontSize: FS.sm, color: C.textSecondary, lineHeight: 1.45, marginTop: 1 }}>
          {TEAM_PLANNING_TITLE}
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
