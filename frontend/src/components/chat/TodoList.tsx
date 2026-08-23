import { C } from '../../lib/design';
import type { TodoItem } from '../../hooks/useSessionArtifacts';

// Список пунктов плана хода — общая разметка для ДВУХ мест: карточки «План» в ленте
// (ChatItemView) и поповера пилюли прогресса (TurnPlanPill). Разметка одна, чтобы
// кружки статусов и зачёркивание выполненных нигде не разъезжались.
//
// Кружки нарисованы inline-SVG, а не иконками lucide: нужны три разных состояния
// одного размера (сплошной с галочкой / точка в кольце / пунктирный контур), и в наборе
// готовой тройки нет.
export function TodoList({ todos }: { todos: TodoItem[] }) {
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 1 }}>
      {todos.map((t, i) => {
        const isDone = t.status === 'completed';
        const isActive = t.status === 'in_progress';
        const label = isActive && t.activeForm ? t.activeForm : t.content;
        return (
          <div key={i} style={{ display: 'flex', alignItems: 'flex-start', gap: 9, padding: '4px 0' }}>
            <span style={{ flexShrink: 0, marginTop: 1, display: 'flex' }}>
              {isDone ? (
                <svg width="15" height="15" viewBox="0 0 16 16" fill="none">
                  <circle cx="8" cy="8" r="8" fill={C.success} />
                  <path d="M4.5 8.2l2.2 2.2 4.8-4.8" stroke={C.onAccent} strokeWidth="1.6" strokeLinecap="round" strokeLinejoin="round" />
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
              fontSize: 13, lineHeight: 1.4,
              color: isDone ? C.textMuted : isActive ? C.textHeading : C.textSecondary,
              textDecoration: isDone ? 'line-through' : 'none',
              fontWeight: isActive ? 600 : 400,
            }}>
              {label}
            </span>
          </div>
        );
      })}
    </div>
  );
}
