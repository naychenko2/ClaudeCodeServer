import { useState, useEffect } from 'react';
import { ChevronUp, ChevronDown } from 'lucide-react';
import type { ChatItem } from '../../types';
import { C, FONT } from '../../lib/design';
import { toolWord } from './ToolUseView';
import { AgentTextBlock, AgentThinkingBlock, NEUTRAL_AGENT_ACCENT } from './AgentContentBlocks';

// Элемент активности сабагента: дочерний элемент ленты (tool_use/text/thinking с
// parentToolUseId) + его глобальный индекс — нужен renderChild'у (renderItem из ChatPanel)
export interface ActivityEntry { item: ChatItem; idx: number }

// Блок группы инструментов: во время стриминга — раскрыт плоско; после завершения —
// автоматически сворачивается в строку «N действий» (одиночные тоже — содержимое
// действий по умолчанию скрыто). Кнопка заголовка переключает. summary — что остаётся
// видимым в свёрнутом виде под заголовком (плашки изменённых файлов); при раскрытии
// вместо него рендерятся children, где те же файлы стоят на своих местах в хронологии.
export function ToolGroupBlock({ isGroupDone, toolCount, summary, children }: {
  isGroupDone: boolean;
  toolCount: number;
  summary?: React.ReactNode;
  children: React.ReactNode;
}) {
  const [expanded, setExpanded] = useState(true);
  // Группа из одних file_changed (toolCount 0) не сворачивается — иначе строка «0 действий»
  const collapsible = isGroupDone && toolCount > 0;
  useEffect(() => {
    if (collapsible) setExpanded(false);
  }, [collapsible]);

  return (
    <div style={{ borderTop: `1px solid ${C.bgInset}`, borderBottom: `1px solid ${C.bgInset}` }}>
      {collapsible && (
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
      {(!collapsible || expanded) ? children : summary}
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

// Плашка активности обычного (не-персона) сабагента: вызовы инструментов + его
// текст/thinking (полный поток, как в карточке персоны, но с нейтральным акцентом)
export function AgentActionsBlock({ entries, renderChild }: {
  entries: ActivityEntry[];
  renderChild: (item: ChatItem, idx: number) => React.ReactNode;
}) {
  const [open, setOpen] = useState(false);
  const toolCount = entries.filter(e => e.item.kind === 'tool_use').length;
  const label = toolCount === 1 ? '1 действие' : `${toolCount} действий`;
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
        {toolCount > 0 ? label : 'ход агента'}
      </div>
      {open && (
        <div style={{ paddingLeft: 14, borderLeft: `2px solid ${C.border}` }}>
          {entries.map((e, ci) => (
            <div key={itemKey(e.item, e.idx)} style={ci === 0 ? undefined : { borderTop: `1px solid ${C.bgInset}` }}>
              {e.item.kind === 'text'
                ? <AgentTextBlock text={e.item.text} accent={NEUTRAL_AGENT_ACCENT} />
                : e.item.kind === 'thinking'
                  ? <AgentThinkingBlock text={e.item.text} />
                  : renderChild(e.item, e.idx)}
            </div>
          ))}
        </div>
      )}
    </div>
  );
}
