import { useState, useEffect } from 'react';
import { ChevronUp, ChevronDown } from 'lucide-react';
import type { ChatItem } from '../../types';
import { C, FONT } from '../../lib/design';
import { toolWord, type ToolUseItem } from './ToolUseView';

// Блок группы инструментов: во время стриминга — раскрыт плоско; после завершения —
// N≤5 остаётся раскрытым, N>5 автоматически сворачивается. Кнопка заголовка переключает.
export function ToolGroupBlock({ isGroupDone, toolCount, children }: {
  isGroupDone: boolean;
  toolCount: number;
  children: React.ReactNode;
}) {
  const [expanded, setExpanded] = useState(true);
  useEffect(() => {
    if (isGroupDone && toolCount > 5) setExpanded(false);
  }, [isGroupDone, toolCount]);

  return (
    <div style={{ borderTop: `1px solid ${C.bgInset}`, borderBottom: `1px solid ${C.bgInset}` }}>
      {isGroupDone && (
        <button
          onClick={() => setExpanded(v => !v)}
          style={{
            width: '100%', display: 'flex', alignItems: 'center', gap: 6,
            padding: '5px 14px', border: 'none', background: 'transparent',
            cursor: 'pointer', textAlign: 'left',
          }}
        >
          <span style={{ fontSize: 11.5, color: C.textMuted, flex: 1, fontFamily: FONT.sans }}>
            {toolCount} {toolWord(toolCount)}
          </span>
          {expanded
            ? <ChevronUp size={12} color={C.textMuted} strokeWidth={2} style={{ flexShrink: 0 }} />
            : <ChevronDown size={12} color={C.textMuted} strokeWidth={2} style={{ flexShrink: 0 }} />}
        </button>
      )}
      {(!isGroupDone || expanded) && children}
    </div>
  );
}

// Стабильный ключ элемента ленты: у интерактивных элементов есть собственный id —
// он переживает перезагрузку истории (индексы могут сдвигаться), поэтому state
// раскрытых блоков (ToolUseView и т.п.) не теряется. Для остальных индекс допустим.
export function itemKey(item: ChatItem, i: number): string {
  switch (item.kind) {
    case 'tool_use': return `tu-${item.id}`;
    case 'permission_request': return `perm-${item.requestId}`;
    case 'plan_review': return `plan-${item.requestId}`;
    case 'ask_question': return `ask-${item.toolUseId}`;
    default: return `i-${i}`;
  }
}

export function AgentActionsBlock({ items, renderChild, idxMap }: {
  items: ToolUseItem[];
  renderChild: (item: ChatItem, idx: number) => React.ReactNode;
  idxMap: Map<string, number>;
}) {
  const [open, setOpen] = useState(false);
  const label = items.length === 1 ? '1 действие' : `${items.length} действий`;
  return (
    <div style={{ marginLeft: 8 }}>
      <div
        onClick={() => setOpen(o => !o)}
        style={{
          display: 'inline-flex', alignItems: 'center', gap: 4, cursor: 'pointer',
          padding: '2px 6px', borderRadius: 4,
          color: C.textMuted, fontSize: 11, userSelect: 'none',
        }}
      >
        <span style={{
          display: 'inline-block',
          transform: open ? 'rotate(0deg)' : 'rotate(-90deg)',
          transition: 'transform 0.15s',
          fontSize: 10,
        }}>▾</span>
        {label}
      </div>
      {open && (
        <div style={{ paddingLeft: 14, borderLeft: `2px solid ${C.border}` }}>
          {items.map((child, ci) => (
            <div key={child.id} style={ci === 0 ? undefined : { borderTop: `1px solid ${C.bgInset}` }}>
              {renderChild(child, idxMap.get(child.id) ?? 0)}
            </div>
          ))}
        </div>
      )}
    </div>
  );
}
