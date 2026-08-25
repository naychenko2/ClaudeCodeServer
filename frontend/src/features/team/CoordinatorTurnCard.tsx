import { useEffect, useState } from 'react';
import type { ChatItem, Persona } from '../../types';
import { C, FONT, FS, SP } from '../../lib/design';
import { AGENT_COLORS } from '../../components/AgentSelector';
import { NEUTRAL_AGENT_ACCENT } from '../../components/chat/AgentContentBlocks';
import { ActivitySection, PersonaConsultCard } from '../../components/chat/PersonaTaskView';
import type { ActivityEntry } from '../../components/chat/timeline';

// Метрики хода координатора: считает вызывающий (системного хвоста CLI, как у сабагента,
// у собственного хода чата нет)
export interface CoordinatorTurnMetrics {
  tokens?: number;
  toolUses?: number;
  durationMs?: number;
}

// Карточка одного хода координатора «Командной реализации» — та же форма, что карточка
// персоны-субагента (PersonaConsultCard), только шапка — персона-координатор самого чата,
// вместо вопроса строка состояния фазы, а ответ — сводка волны.
// Компонент презентационный: всё приходит пропсами, логику группировки ленты он не знает.
export function CoordinatorTurnCard({
  persona, statusLine, collapsedSummary, answer, metrics,
  running, isError, aborted, runningLabel = 'Работает…',
  activity, renderChild, online = true, onOpenFile,
}: {
  persona?: Persona | null;      // координатор чата; нет — нейтральная серая шапка «Агент»
  statusLine: string;            // фаза хода: «Разбирает доклады волны 2»
  collapsedSummary: string;      // свёрнутая строка: «Координатор · разобрал доклады волны 2 · 4 действия · 12 с»
  answer: string;                // сводка волны
  metrics?: CoordinatorTurnMetrics;
  running: boolean;
  isError: boolean;
  aborted?: boolean;
  runningLabel?: string;         // подпись у спиннера в шапке
  activity?: ActivityEntry[];    // вложенные действия координатора (вызовы инструментов, текст, размышления)
  renderChild?: (item: ChatItem, idx: number) => React.ReactNode;   // renderItem ленты
  online?: boolean;
  onOpenFile?: (path: string) => void;
}) {
  // Идущий ход раскрыт (виден живой прогресс), завершённый свёрнут — как ActivitySection.
  // Ручной выбор приоритетнее автоповедения до следующей смены running
  const [userOpen, setUserOpen] = useState<boolean | null>(null);
  // eslint-disable-next-line react-hooks/set-state-in-effect -- сброс ручного выбора при завершении хода
  useEffect(() => { setUserOpen(null); }, [running]);
  const open = userOpen ?? running;

  const accent = persona
    ? (AGENT_COLORS[persona.avatar?.color ?? ''] ?? NEUTRAL_AGENT_ACCENT)
    : NEUTRAL_AGENT_ACCENT;

  const card = (
    <PersonaConsultCard
      persona={persona}
      badge="координатор"
      question=""
      statusLine={statusLine}
      running={running}
      isError={isError}
      aborted={aborted}
      answer={answer}
      metrics={metrics}
      runningLabel={runningLabel}
      collapsed={open ? undefined : { summary: collapsedSummary, onToggle: () => setUserOpen(true) }}
    >
      {activity && activity.length > 0 && (
        <ActivitySection activity={activity} running={running} accent={accent}
          online={online} onOpenFile={onOpenFile} renderChild={renderChild} />
      )}
    </PersonaConsultCard>
  );

  if (!open) return card;

  // Свернуть обратно: неприметная строка-кнопка НАД карточкой — у длинного хода она
  // остаётся под рукой, а в шапку не лезет (там спиннер и статус)
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: SP.xs, maxWidth: '100%' }}>
      <button
        onClick={() => setUserOpen(false)}
        style={{
          alignSelf: 'flex-end', display: 'inline-flex', alignItems: 'center', gap: SP.xs,
          border: 'none', background: 'transparent', cursor: 'pointer', padding: '0 2px',
          fontFamily: FONT.sans, fontSize: FS.xs, color: C.textMuted,
        }}
      >
        <span>▾</span>
        <span>Свернуть</span>
      </button>
      {card}
    </div>
  );
}
