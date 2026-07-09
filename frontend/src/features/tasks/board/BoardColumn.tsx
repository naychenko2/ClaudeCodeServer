// Элементы колонки статуса на Kanban-доске: заголовок с WIP-лимитом,
// ячейка-droppable (lane × status) со списком карточек и быстрым добавлением.

import { useState } from 'react';
import { useDroppable } from '@dnd-kit/core';
import { SortableContext, verticalListSortingStrategy } from '@dnd-kit/sortable';
import type { Task } from '../../../types';
import { C, FONT, R } from '../../../lib/design';
import { setWip } from '../../../lib/boardControls';
import { BoardCard } from './BoardCard';

// Заголовок колонки доски: точка, название, счётчик, WIP-лимит.
// over — превышен ли лимит (подсветка danger). WIP пишется в общий стор по columnId.
export function ColumnHeader({ name, color, count, wip, over, columnId }: {
  name: string;
  color: string;
  count: number;
  wip?: number;
  over: boolean;
  columnId: string;
}) {
  const onWip = (v?: number) => setWip(columnId, v);
  return (
    <div style={{
      display: 'flex', alignItems: 'center', gap: 8, padding: '0 4px 10px',
    }}>
      <span style={{ width: 9, height: 9, borderRadius: '50%', background: color, flexShrink: 0 }} />
      <span style={{ fontFamily: FONT.sans, fontSize: 13, fontWeight: 700, color: C.textHeading, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
        {name}
      </span>
      <span style={{
        fontFamily: FONT.sans, fontSize: 12, fontWeight: 700,
        color: over ? C.danger : C.textMuted,
        background: over ? C.dangerBg : C.bgSelected,
        borderRadius: R.sm, padding: '1px 7px', minWidth: 18, textAlign: 'center',
      }}>
        {count}{wip ? ` / ${wip}` : ''}
      </span>
      <span style={{ flex: 1 }} />
      <WipEditor wip={wip} onWip={onWip} />
    </div>
  );
}

// Мини-редактор WIP-лимита: иконка → инлайн-инпут по клику
function WipEditor({ wip, onWip }: { wip?: number; onWip: (v?: number) => void }) {
  const [editing, setEditing] = useState(false);
  if (editing) {
    return (
      <input
        type="number"
        min={1}
        autoFocus
        defaultValue={wip ?? ''}
        placeholder="∞"
        onBlur={e => {
          const n = parseInt(e.target.value, 10);
          onWip(Number.isFinite(n) && n > 0 ? n : undefined);
          setEditing(false);
        }}
        onKeyDown={e => { if (e.key === 'Enter') (e.target as HTMLInputElement).blur(); }}
        style={{
          width: 44, padding: '2px 6px', border: `1px solid ${C.accent}`, borderRadius: R.sm,
          fontFamily: FONT.sans, fontSize: 12, color: C.textHeading, background: C.bgWhite,
        }}
      />
    );
  }
  return (
    <button
      onClick={() => setEditing(true)}
      title="Лимит задач в колонке (WIP)"
      style={{
        display: 'flex', alignItems: 'center', gap: 4, cursor: 'pointer',
        border: 'none', background: 'transparent', color: C.textMuted, padding: 2,
        fontFamily: FONT.sans, fontSize: 11, fontWeight: 600,
      }}
    >
      <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round">
        <path d="M4 21v-7M4 10V3M12 21v-9M12 8V3M20 21v-5M20 12V3M1 14h6M9 8h6M17 16h6" />
      </svg>
      {wip ? wip : 'WIP'}
    </button>
  );
}

// Ячейка доски: конкретная колонка статуса в конкретной дорожке. Droppable + сортируемый список.
export function BoardCell({ cellId, cards, projectNameOf, onOpen, onQuickAdd, minEmptyHeight }: {
  cellId: string;
  cards: Task[];
  projectNameOf: (t: Task) => string | undefined;
  onOpen: (t: Task) => void;
  onQuickAdd?: (title: string) => void;   // undefined = без быстрого добавления (в режиме дорожек)
  minEmptyHeight: number;
}) {
  const { setNodeRef, isOver } = useDroppable({ id: cellId });
  const [adding, setAdding] = useState(false);
  const [title, setTitle] = useState('');

  const submit = () => {
    const t = title.trim();
    if (t && onQuickAdd) onQuickAdd(t);
    setTitle('');
    setAdding(false);
  };

  return (
    <div
      ref={setNodeRef}
      style={{
        display: 'flex', flexDirection: 'column', gap: 8,
        padding: 8, borderRadius: R.xl,
        background: isOver ? C.accentLight : C.bgInset,
        border: `1px solid ${isOver ? C.accent : 'transparent'}`,
        transition: 'background 0.12s, border-color 0.12s',
        minHeight: cards.length === 0 ? minEmptyHeight : undefined,
      }}
    >
      <SortableContext items={cards.map(c => c.id)} strategy={verticalListSortingStrategy}>
        {cards.map(t => (
          <BoardCard key={t.id} task={t} projectName={projectNameOf(t)} onOpen={() => onOpen(t)} />
        ))}
      </SortableContext>

      {cards.length === 0 && !adding && (
        <div style={{
          flex: 1, display: 'flex', alignItems: 'center', justifyContent: 'center',
          fontFamily: FONT.sans, fontSize: 12, color: C.textMuted, minHeight: 40,
        }}>
          Перетащите сюда
        </div>
      )}

      {onQuickAdd && (
        adding ? (
          <textarea
            autoFocus
            value={title}
            onChange={e => setTitle(e.target.value)}
            onBlur={submit}
            onKeyDown={e => {
              if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); submit(); }
              if (e.key === 'Escape') { setTitle(''); setAdding(false); }
            }}
            placeholder="Название задачи…"
            rows={2}
            style={{
              width: '100%', boxSizing: 'border-box', resize: 'none',
              padding: '9px 10px', border: `1px solid ${C.accent}`, borderRadius: 12,
              fontFamily: FONT.sans, fontSize: 13, color: C.textPrimary, background: C.bgWhite,
            }}
          />
        ) : (
          <button
            onClick={() => setAdding(true)}
            style={{
              display: 'flex', alignItems: 'center', gap: 6, width: '100%',
              padding: '8px 10px', cursor: 'pointer',
              border: `1px dashed ${C.dashed}`, borderRadius: 12, background: 'transparent',
              fontFamily: FONT.sans, fontSize: 12.5, fontWeight: 600, color: C.textMuted,
            }}
          >
            <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.4" strokeLinecap="round">
              <line x1="12" y1="5" x2="12" y2="19" /><line x1="5" y1="12" x2="19" y2="12" />
            </svg>
            Добавить
          </button>
        )
      )}
    </div>
  );
}
