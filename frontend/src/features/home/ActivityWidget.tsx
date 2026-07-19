import { useEffect } from 'react';
import { Activity } from 'lucide-react';
import type { BoardItem, HomeSessionInfo } from '../../types';
import { C, FONT } from '../../lib/design';
import { ensureAgentsLoaded, useAgentBoard } from '../../lib/agentBoard';
import { getPersonaById, personaLabel } from '../../lib/personas';
import { WidgetCard, WidgetEmpty } from './WidgetCard';
import { SessionRow, openSession } from './SessionRow';

// Агент с доски: клик ведет к задаче (вкладка «Задачи» проекта или модал календаря)
function AgentRow({ item }: { item: BoardItem }) {
  const persona = item.personaId ? getPersonaById(item.personaId) : undefined;
  const open = () => {
    const url = item.projectId
      ? `#/project/${item.projectId}/task/${item.taskId}`
      : `#/calendar/task/${item.taskId}`;
    window.dispatchEvent(new CustomEvent('cc-open-url', { detail: { url } }));
  };
  const working = item.column === 'working';
  return (
    <button
      onClick={open}
      style={{
        display: 'flex', alignItems: 'center', gap: 9, width: '100%', textAlign: 'left',
        background: 'none', border: 'none', borderRadius: 8, padding: '7px 8px',
        margin: '0 -8px', cursor: 'pointer', minWidth: 0,
      }}
      onMouseEnter={e => { e.currentTarget.style.background = C.bgSelected; }}
      onMouseLeave={e => { e.currentTarget.style.background = 'none'; }}
    >
      <span style={{
        width: 8, height: 8, borderRadius: '50%', flexShrink: 0,
        background: item.permissionPending ? C.accent : working ? C.success : C.textMuted,
      }} />
      <span style={{
        fontFamily: FONT.sans, fontSize: 13, color: C.textPrimary, flex: 1, minWidth: 0,
        whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis',
      }}>
        {item.title}
      </span>
      {item.permissionPending && (
        <span style={{
          fontFamily: FONT.sans, fontSize: 11, color: C.onAccent, background: C.accent,
          borderRadius: 999, padding: '1px 7px', flexShrink: 0,
        }}>
          ждет разрешения
        </span>
      )}
      <span style={{
        fontFamily: FONT.sans, fontSize: 11.5, color: C.textMuted, flexShrink: 0,
        maxWidth: 130, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis',
      }}>
        {persona ? personaLabel(persona) : item.currentToolName ?? ''}
      </span>
    </button>
  );
}

// «Сейчас работают»: живые агенты-исполнители задач (доска) + остальные активные сессии.
// Сессии, уже представленные карточкой агента, не дублируем (фильтр по sessionId).
// fill — виджет живет в ряду фиксированной высоты (по соседу): длинный список
// скроллится внутри, а не раздувает ряд.
export function ActivityWidget({ active, fill }: { active: HomeSessionInfo[]; fill?: boolean }) {
  const board = useAgentBoard();
  useEffect(() => { void ensureAgentsLoaded(); }, []);

  // Живые агенты: в работе или ждут разрешения (done/queue на дашборде не показываем)
  const liveAgents = board.filter(i => i.column === 'working' || i.column === 'waiting');
  const agentSessionIds = new Set(liveAgents.map(i => i.sessionId).filter(Boolean));
  const sessions = active.filter(s => !agentSessionIds.has(s.id));
  const empty = liveAgents.length === 0 && sessions.length === 0;

  return (
    <WidgetCard icon={<Activity size={16} strokeWidth={2} />} title="Сейчас работают" fill={fill}>
      {empty
        ? <WidgetEmpty text="Все агенты отдыхают — активных сессий нет." />
        : (
          <div style={{
            display: 'flex', flexDirection: 'column',
            // Длинный список не раздувает карточку: в fill-ячейке — скролл по высоте
            // соседа, в потоке колонки — скролл после ~6 строк (maxHeight)
            ...(fill
              ? { flex: 1, minHeight: 0, overflowY: 'auto' as const }
              : { maxHeight: 300, overflowY: 'auto' as const }),
          }}>
            {liveAgents.map(item => <AgentRow key={item.taskId} item={item} />)}
            {sessions.map(s => <SessionRow key={s.id} s={s} showStatus onOpen={openSession} />)}
          </div>
        )}
    </WidgetCard>
  );
}
