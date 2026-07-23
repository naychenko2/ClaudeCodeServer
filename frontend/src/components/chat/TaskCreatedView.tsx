import { memo, useContext, useEffect, useMemo, useState } from 'react';
import { SquareCheck, ArrowRight } from 'lucide-react';
import type { ChatItem, Task } from '../../types';
import { C, FONT, SHADOW } from '../../lib/design';
import { dueLabel, isDueUrgent, openTaskInSection, PRIORITY_LABEL } from '../../lib/tasks';
import { PriorityFlag, MeBadge, ClaudeBadge, LabelChip, SubtaskCheck, CalendarIcon } from '../../features/tasks/bits';
import { usePersonas, ensurePersonasLoaded, personaLabel } from '../../lib/personas';
import { PersonaAvatar } from '../../features/personas/PersonaAvatar';
import { ToolUseView } from './ToolUseView';
import { ChatProjectContext } from './contexts';

type ToolUseItem = Extract<ChatItem, { kind: 'tool_use' }>;

// Вызов tasks_create (mcp__tasks__tasks_create) — сравнение по суффиксу, без регистра
export function isTasksCreate(name: string): boolean {
  return name.toLowerCase().endsWith('__tasks_create');
}

// Защитный разбор ответа tasks-server: полный Task JSON или null (фолбэк на ToolUseView)
function parseTask(result: string): Task | null {
  try {
    const t = JSON.parse(result) as { id?: unknown; title?: unknown };
    if (t && typeof t === 'object' && typeof t.id === 'string' && typeof t.title === 'string')
      return t as unknown as Task;
  } catch { /* не JSON — деградация в обычный блок инструмента */ }
  return null;
}

// Подпись ячейки сетки деталей
function CellLabel({ children }: { children: React.ReactNode }) {
  return (
    <div style={{
      fontSize: 10, fontWeight: 700, letterSpacing: '0.06em', textTransform: 'uppercase',
      color: C.textMuted, marginBottom: 4,
    }}>
      {children}
    </div>
  );
}

const cellValueStyle: React.CSSProperties = {
  display: 'flex', alignItems: 'center', gap: 6,
  fontSize: 12.5, fontWeight: 600, color: C.textPrimary, minWidth: 0,
};

// Карточка «Задача создана» — вызов MCP tasks_create в ленте чата (макет варианта C).
// Вместо технического блока: тонированная шапка с техподписью, название, описание,
// сетка деталей (исполнитель/срок/приоритет/подзадачи), метки, футер-ссылка.
// Вся карточка кликабельна → задача в её разделе (проектная — «Задачи», личная — «Календарь»).
export const TaskCreatedView = memo(function TaskCreatedView({ item, online, onOpenFile }: {
  item: ToolUseItem;
  online: boolean;
  onOpenFile?: (path: string) => void;
}) {
  // Резолв персоны-исполнителя: в чатах без персоны стор мог быть не загружен
  useEffect(() => { void ensurePersonasLoaded(); }, []);
  const personas = usePersonas();
  const project = useContext(ChatProjectContext);
  const [hovered, setHovered] = useState(false);

  const running = item.result === undefined;
  const task = useMemo(
    () => (running || item.isError ? null : parseTask(item.result!)),
    [running, item.isError, item.result],
  );

  // Ошибка создания или непарсибельный ответ — стандартный блок инструмента
  if (!running && !task) return <ToolUseView item={item} online={online} onOpenFile={onOpenFile} />;

  const inp = (item.input ?? {}) as { title?: unknown };
  const title = task?.title ?? (typeof inp.title === 'string' ? inp.title : '');

  // В ответе личной задачи projectId нет — для hash-URL берём проект чата (личный чат → null)
  const openTask = () => {
    if (task) openTaskInSection(task.projectId ? task : { ...task, projectId: project?.id });
  };

  const persona = task?.personaId ? personas.find(p => p.id === task.personaId) : undefined;
  const subtasks = task?.subtasks ?? [];
  const subtasksDone = subtasks.filter(s => s.isDone).length;
  const labels = task?.labels ?? [];
  const description = task?.description?.trim() ?? '';
  const dueUrgent = task ? isDueUrgent(task) : false;

  return (
    <div
      onClick={task ? openTask : undefined}
      onMouseEnter={() => setHovered(true)}
      onMouseLeave={() => setHovered(false)}
      title={task ? 'Открыть задачу' : undefined}
      style={{
        border: `1px solid ${hovered && task ? C.accentMuted : C.borderLight}`,
        borderLeft: `3px solid ${C.accent}`,
        borderRadius: 12, background: C.bgWhite, overflow: 'hidden',
        boxShadow: SHADOW.card, maxWidth: '100%',
        cursor: task ? 'pointer' : 'default',
        transition: 'border-color 0.15s',
      }}
    >
      {/* Шапка: значок + «Задача создана» + техподпись инструмента */}
      <div style={{
        display: 'flex', alignItems: 'center', gap: 9, padding: '9px 12px',
        background: C.accentLight, borderBottom: `1px solid ${C.divider}`,
      }}>
        <div style={{
          width: 26, height: 26, borderRadius: '50%', flexShrink: 0,
          background: C.accentMuted, color: C.accent,
          display: 'flex', alignItems: 'center', justifyContent: 'center',
        }}>
          <SquareCheck size={14} strokeWidth={2} />
        </div>
        <span style={{ fontFamily: FONT.serif, fontSize: 14, fontWeight: 700, color: C.textHeading }}>
          Задача создана
        </span>
        {running ? (
          <span style={{ marginLeft: 'auto', display: 'flex', alignItems: 'center', gap: 7, flexShrink: 0 }}>
            <div className="tool-spinner" />
            <span style={{ fontSize: 11, color: C.textMuted }}>Создаю задачу…</span>
          </span>
        ) : (
          <span style={{ marginLeft: 'auto', fontFamily: FONT.mono, fontSize: 11, color: C.textMuted, flexShrink: 0 }}>
            tasks · create
          </span>
        )}
      </div>

      {/* Тело: название, описание, сетка деталей, метки. Пока input стримится и
          заголовка ещё нет — не рендерим пустой блок с паддингами */}
      {(title || task) && (
      <div style={{ padding: '11px 14px 12px' }}>
        {title && (
          <div style={{ fontSize: 15, fontWeight: 700, lineHeight: 1.35, color: C.textHeading }}>
            {title}
          </div>
        )}
        {description && (
          <div style={{
            marginTop: 5, fontSize: 12.5, lineHeight: 1.5, color: C.textSecondary,
            display: '-webkit-box', WebkitLineClamp: 2, WebkitBoxOrient: 'vertical' as const,
            overflow: 'hidden',
          }}>
            {description}
          </div>
        )}
        {task && (
          <div style={{
            marginTop: 12, display: 'grid',
            gridTemplateColumns: 'repeat(auto-fit, minmax(140px, 1fr))', gap: '10px 16px',
          }}>
            {(persona || task.assignee) && (
              <div>
                <CellLabel>Исполнитель</CellLabel>
                <div style={cellValueStyle}>
                  {persona ? (
                    <>
                      <PersonaAvatar persona={persona} size={20} />
                      <span style={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                        {personaLabel(persona)}
                      </span>
                    </>
                  ) : task.assignee === 'claude' ? (
                    <><ClaudeBadge size={18} />Claude</>
                  ) : (
                    <><MeBadge size={20} />Я</>
                  )}
                </div>
              </div>
            )}
            {task.dueDate && (
              <div>
                <CellLabel>Срок</CellLabel>
                <div style={{ ...cellValueStyle, color: dueUrgent ? C.danger : C.textPrimary }}>
                  <CalendarIcon size={12} />
                  {dueLabel(task.dueDate)}{task.dueTime ? ` · ${task.dueTime}` : ''}
                </div>
              </div>
            )}
            <div>
              <CellLabel>Приоритет</CellLabel>
              <div style={cellValueStyle}>
                <PriorityFlag priority={task.priority} size={12} />
                {PRIORITY_LABEL[task.priority] ?? task.priority}
              </div>
            </div>
            {subtasks.length > 0 && (
              <div>
                <CellLabel>Подзадачи</CellLabel>
                <div style={cellValueStyle}>
                  <SubtaskCheck done={subtasksDone === subtasks.length} size={13} />
                  {subtasksDone} из {subtasks.length}
                </div>
              </div>
            )}
          </div>
        )}
        {labels.length > 0 && (
          <div style={{ marginTop: 11, display: 'flex', gap: 6, flexWrap: 'wrap' }}>
            {labels.map(l => <LabelChip key={l} label={l} />)}
          </div>
        )}
      </div>
      )}

      {/* Футер-ссылка — самая явная аффорданса перехода */}
      {task && (
        <div style={{
          display: 'flex', alignItems: 'center', justifyContent: 'flex-end', gap: 5,
          padding: '8px 14px', borderTop: `1px solid ${C.divider}`,
          fontSize: 12, fontWeight: 600, color: C.accent,
        }}>
          Открыть задачу
          <span style={{
            display: 'flex', transform: hovered ? 'translateX(3px)' : 'none',
            transition: 'transform 0.15s',
          }}>
            <ArrowRight size={13} strokeWidth={2} />
          </span>
        </div>
      )}
    </div>
  );
});
