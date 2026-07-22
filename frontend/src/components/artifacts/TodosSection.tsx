// Секция «Задачи» (todo-список хода). Перенесена из ArtifactsPanel verbatim.
import { C, FONT, R, SHADOW } from '../../lib/design';
import type { TodoItem } from '../../hooks/useSessionArtifacts';

// Пункт todo-списка — те же иконки статусов, что у TodoPlanView в чате,
// чтобы прогресс в панели и в ленте выглядел одинаково.
function TodoRow({ todo }: { todo: TodoItem }) {
  const isDone = todo.status === 'completed';
  const isActive = todo.status === 'in_progress';
  const label = isActive && todo.activeForm ? todo.activeForm : todo.content;
  return (
    <div style={{ display: 'flex', alignItems: 'flex-start', gap: 9, padding: '9px 12px', border: `1px solid ${C.borderLight}`, borderRadius: R.lg, boxShadow: SHADOW.card, background: C.bgWhite }}>
      <span style={{ flexShrink: 0, marginTop: 2, display: 'flex' }}>
        {isDone ? (
          <svg width="15" height="15" viewBox="0 0 16 16" fill="none">
            <circle cx="8" cy="8" r="8" fill={C.success} />
            <path d="M4.5 8.2l2.2 2.2 4.8-4.8" stroke="#FFF" strokeWidth="1.6" strokeLinecap="round" strokeLinejoin="round" />
          </svg>
        ) : isActive ? (
          <svg width="15" height="15" viewBox="0 0 16 16" fill="none">
            <circle cx="8" cy="8" r="7" fill={C.accent} />
            <circle cx="8" cy="8" r="2.6" fill={C.accentLight} />
          </svg>
        ) : (
          <svg width="15" height="15" viewBox="0 0 16 16" fill="none">
            <circle cx="8" cy="8" r="6.5" stroke={C.dashed} strokeWidth="1.5" />
          </svg>
        )}
      </span>
      <span style={{
        fontFamily: FONT.sans, fontSize: 13, lineHeight: 1.4,
        color: isDone ? C.textMuted : isActive ? C.textHeading : C.textSecondary,
        textDecoration: isDone ? 'line-through' : 'none',
        fontWeight: isActive ? 600 : 400,
      }}>
        {label}
      </span>
    </div>
  );
}

export function TodosSection({ todos }: { todos: TodoItem[] }) {
  return (
    <div style={{ flex: 1, overflowY: 'auto', padding: '8px 12px', display: 'flex', flexDirection: 'column', gap: 5 }}>
      {todos.map((t, i) => <TodoRow key={i} todo={t} />)}
    </div>
  );
}
