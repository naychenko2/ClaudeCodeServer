import { useEffect, useState } from 'react';
import type { ChatItem, Persona } from '../../types';
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
  persona?: Persona | null;      // координатор чата; нет — нейтральная серая шапка «Координатор»
  statusLine: string;            // фаза хода: «Разбирает доклады волны 2»
  collapsedSummary: string;      // свёрнутая строка: «Координатор · разобрал доклады волны 2 · 12 с · 4 действия»
  answer: string;                // сводка волны
  metrics?: CoordinatorTurnMetrics;
  running: boolean;
  isError: boolean;
  aborted?: boolean;             // ход прерван человеком — статус в шапке и свёрнутой строке
  runningLabel?: string;         // подпись у спиннера в шапке (со строкой состояния не рисуется)
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

  return (
    <PersonaConsultCard
      persona={persona}
      badge="координатор"
      fallbackTitle="Координатор"
      question=""
      statusLine={statusLine}
      running={running}
      isError={isError}
      aborted={aborted}
      answer={answer}
      // Ход, который раздал задачи и замолчал, — штатный случай, а не «ответ без текста»
      emptyAnswerNote="Без сводки: ход закрылся действиями"
      metrics={metrics}
      runningLabel={runningLabel}
      // Чип отвечает на вопрос «какая модель поедет, если позвать ЭТУ персону сабагентом»,
      // а ход координатора — ход самого чата
      showModelChip={false}
      // Рутинный отчёт не должен перекрикивать элементы, ждущие человека:
      // вопрос человеку → эскалация → ход координатора
      quiet
      collapsed={open ? undefined : { summary: collapsedSummary, onToggle: () => setUserOpen(true) }}
      // Свернуть обратно — кликом по шапке (отдельной кнопки снаружи карточки в ленте нет)
      onCollapse={open ? () => setUserOpen(false) : undefined}
    >
      {activity && activity.length > 0 && (
        <ActivitySection activity={activity} running={running} accent={accent}
          online={online} onOpenFile={onOpenFile} renderChild={renderChild} />
      )}
    </PersonaConsultCard>
  );
}
