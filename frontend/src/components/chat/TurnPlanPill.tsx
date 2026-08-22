import { useState } from 'react';
import { ListChecks } from 'lucide-react';
import { C, FONT } from '../../lib/design';
import { Menu } from '../ui';
import { ICON_SIZE, ICON_STROKE } from '../ui/icons';
import { TodoList } from './TodoList';
import { planHint, type TodoItem } from '../../hooks/useSessionArtifacts';

// Пилюля прогресса плана хода: «список · 3/6 · Гоняю тесты», по клику — весь список.
//
// Зачем отдельным элементом, а не подписью индикатора: индикатор живёт ТОЛЬКО пока
// идёт ход, и между ходами прогресс исчезал вместе с ним — а посмотреть «что там
// по плану» нужно как раз в паузе. Пилюля рисуется в том же месте (конец ленты,
// справа), поэтому при старте и конце хода она не прыгает.
//
// Пока план не доделан — показываем текущий шаг; когда выполнен весь, пилюля не
// исчезает, а показывает итог: между ходами это обычное состояние, и пропадающий
// элемент читался бы как сбой.
export function TurnPlanPill({ todos }: { todos: TodoItem[] }) {
  const [menu, setMenu] = useState<DOMRect | null>(null);
  if (!todos.length) return null;

  const hint = planHint(todos);
  const done = todos.filter(t => t.status === 'completed').length;
  const label = hint?.text ?? 'План выполнен';

  return (
    <>
      <button
        onClick={e => setMenu(menu ? null : e.currentTarget.getBoundingClientRect())}
        title="Показать план хода"
        style={{
          // Ровно тот же стиль, что у мета-строки итога хода («✓ 5 шагов · 12.3с»):
          // обе — служебные пилюли ленты, и разнобой между ними читался бы как разные
          // сущности. Отличие одно — курсор: эта кликается и раскрывает список.
          fontSize: 11, color: C.textMuted, fontFamily: FONT.mono,
          background: C.bgSelected, border: 'none', borderRadius: 8, padding: '4px 11px',
          display: 'flex', alignItems: 'center', gap: 7, cursor: 'pointer',
          maxWidth: 300, minWidth: 0, flexShrink: 1,
        }}
      >
        {/* Иконка acccent-цветом, пока план в работе: это активное состояние хода,
            а закончившийся план отличается от идущего с одного взгляда */}
        <ListChecks
          size={ICON_SIZE.xs} strokeWidth={ICON_STROKE}
          color={hint ? C.accent : C.textMuted}
          style={{ flexShrink: 0 }}
        />
        <span style={{ flexShrink: 0 }}>{done}/{todos.length}</span>
        <span style={{ opacity: 0.45, flexShrink: 0 }}>·</span>
        {/* Текст шага сжимается первым и обрезается — счётчик и иконка остаются видны
            на любой ширине, включая мобильную */}
        <span style={{
          minWidth: 0, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis',
        }}>
          {label}
        </span>
      </button>
      {menu && (
        <Menu onClose={() => setMenu(null)} anchor={menu} minWidth={280} maxWidth={380} maxHeight={340}>
          <div style={{ padding: '9px 12px 11px' }}>
            <div style={{
              display: 'flex', alignItems: 'center', gap: 8, paddingBottom: 7, marginBottom: 5,
              borderBottom: `1px solid ${C.divider}`,
            }}>
              <span style={{ fontFamily: FONT.serif, fontSize: 13, fontWeight: 700, color: C.textHeading }}>
                План хода
              </span>
              <span style={{ marginLeft: 'auto', fontFamily: FONT.mono, fontSize: 11, color: C.textMuted }}>
                {done}/{todos.length}
              </span>
            </div>
            <TodoList todos={todos} />
          </div>
        </Menu>
      )}
    </>
  );
}
