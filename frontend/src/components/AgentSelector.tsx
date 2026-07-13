import { useState, useEffect, useLayoutEffect, useRef } from 'react';
import { ChevronDown, User, Check } from 'lucide-react';
import type { AgentInfo } from '../types';
import { C, R, FONT, SHADOW, Z } from '../lib/design';

export const AGENT_COLORS: Record<string, string> = {
  yellow:  '#F39C12',
  orange:  '#D97757',
  blue:    '#3E7CA6',
  green:   '#5E8B4E',
  purple:  '#6C5CB0',
  red:     '#B4452F',
  brown:   '#8A6A28',
  cyan:    '#1A9DAF',
  pink:    '#C2385B',
};

export function agentDotColor(color?: string): string {
  return (color && AGENT_COLORS[color]) ?? C.textMuted;
}

interface Props {
  agents: AgentInfo[];
  selectedAgent: AgentInfo | null;
  onSelect: (agent: AgentInfo | null) => void;
  isMobile?: boolean;
}

export function AgentSelector({ agents, selectedAgent, onSelect, isMobile }: Props) {
  const [open, setOpen] = useState(false);
  const rootRef = useRef<HTMLDivElement>(null);
  // На мобиле dropdown позиционируется через fixed с вычисленными координатами
  const [fixedPos, setFixedPos] = useState<{ bottom: number; top: number; left: number; right: number } | null>(null);

  useEffect(() => {
    if (!open) return;
    const onDown = (e: MouseEvent) => {
      if (rootRef.current && !rootRef.current.contains(e.target as Node)) setOpen(false);
    };
    document.addEventListener('mousedown', onDown);
    return () => document.removeEventListener('mousedown', onDown);
  }, [open]);

  // Вычисляем позицию для мобильного fixed-dropdown
  useLayoutEffect(() => {
    if (!open || !isMobile || !rootRef.current) return;
    const rect = rootRef.current.getBoundingClientRect();
    setFixedPos({
      bottom: window.innerHeight - rect.top + 6,
      top: 16,
      left: 16,
      right: 16,
    });
  }, [open, isMobile]);

  if (agents.length === 0) return null;

  const dotColor = agentDotColor(selectedAgent?.color);

  const dropdownStyle: React.CSSProperties = isMobile && fixedPos
    ? {
        position: 'fixed',
        bottom: fixedPos.bottom,
        top: fixedPos.top,
        left: fixedPos.left,
        right: fixedPos.right,
        overflowY: 'auto',
        background: C.bgWhite,
        border: `1px solid ${C.border}`,
        borderRadius: R.xl,
        boxShadow: SHADOW.dropdown,
        padding: 4,
        zIndex: Z.dropdown,
      }
    : {
        position: 'absolute',
        bottom: 'calc(100% + 6px)',
        right: 0,
        minWidth: 360,
        maxWidth: 'calc(100vw - 32px)',
        maxHeight: 'min(70vh, 480px)',
        overflowY: 'auto',
        background: C.bgWhite,
        border: `1px solid ${C.border}`,
        borderRadius: R.xl,
        boxShadow: SHADOW.dropdown,
        padding: 4,
        zIndex: Z.dropdown,
      };

  return (
    <div ref={rootRef} style={{ position: 'relative', flexShrink: 0, minWidth: 0 }}>
      {selectedAgent ? (
        // Агент выбран — плашка с именем (ограничена по ширине на мобиле)
        <button
          onClick={() => setOpen(o => !o)}
          title="Выбрать агента"
          style={{
            height: isMobile ? 32 : 28,
            padding: '0 8px',
            borderRadius: R.md,
            border: 'none',
            background: open ? C.bgSelected : C.accentLight,
            color: C.textSecondary,
            fontSize: 12.5,
            fontWeight: 600,
            cursor: 'pointer',
            display: 'flex',
            alignItems: 'center',
            gap: 5,
            maxWidth: isMobile ? 110 : 200,
            overflow: 'hidden',
          }}
        >
          <span style={{ width: 8, height: 8, borderRadius: '50%', flexShrink: 0, background: dotColor }} />
          <span style={{
            fontFamily: FONT.sans,
            overflow: 'hidden',
            textOverflow: 'ellipsis',
            whiteSpace: 'nowrap',
            minWidth: 0,
            flex: 1,
          }}>{selectedAgent.name}</span>
          <ChevronDown size={9} strokeWidth={2.5}
            style={{ flexShrink: 0, opacity: 0.55, transform: open ? 'rotate(180deg)' : 'none', transition: 'transform 0.15s' }} />
        </button>
      ) : (
        // Агент не выбран — компактная иконка
        <button
          onClick={() => setOpen(o => !o)}
          title="Выбрать агента"
          style={{
            width: isMobile ? 32 : 28,
            height: isMobile ? 32 : 28,
            borderRadius: R.md,
            border: 'none',
            background: open ? C.bgSelected : 'transparent',
            color: C.textMuted,
            cursor: 'pointer',
            display: 'flex',
            alignItems: 'center',
            justifyContent: 'center',
            padding: 0,
            flexShrink: 0,
          }}
        >
          <User size={15} strokeWidth={2} />
        </button>
      )}

      {open && (
        <div style={dropdownStyle}>
          {selectedAgent && (
            <button
              onClick={() => { onSelect(null); setOpen(false); }}
              onMouseEnter={e => { (e.currentTarget as HTMLElement).style.background = C.bgSelected; }}
              onMouseLeave={e => { (e.currentTarget as HTMLElement).style.background = 'transparent'; }}
              style={{
                width: '100%',
                display: 'flex',
                alignItems: 'center',
                gap: 8,
                padding: '8px 10px',
                borderRadius: R.md,
                border: 'none',
                background: 'transparent',
                cursor: 'pointer',
                textAlign: 'left',
                color: C.textMuted,
                fontSize: 12.5,
              }}
            >
              <span style={{ width: 8, height: 8, borderRadius: '50%', background: C.border, flexShrink: 0 }} />
              Без агента
            </button>
          )}

          {agents.map(agent => {
            const active = selectedAgent?.fileName === agent.fileName;
            const dot = agentDotColor(agent.color);
            return (
              <button
                key={agent.fileName}
                onClick={() => { onSelect(agent); setOpen(false); }}
                onMouseEnter={e => { if (!active) (e.currentTarget as HTMLElement).style.background = C.accentLight; }}
                onMouseLeave={e => { if (!active) (e.currentTarget as HTMLElement).style.background = 'transparent'; }}
                style={{
                  width: '100%',
                  display: 'flex',
                  alignItems: 'flex-start',
                  gap: 8,
                  padding: '8px 10px',
                  borderRadius: R.md,
                  border: 'none',
                  background: active ? C.accentLight : 'transparent',
                  cursor: 'pointer',
                  textAlign: 'left',
                }}
              >
                <span style={{ width: 8, height: 8, borderRadius: '50%', background: dot, flexShrink: 0, marginTop: 5 }} />
                <span style={{ flex: 1, minWidth: 0 }}>
                  <span style={{ display: 'block', fontSize: 13, fontWeight: 600, color: C.textHeading }}>
                    {agent.name}
                  </span>
                  {agent.description && (
                    <span style={{
                      display: 'block',
                      fontSize: 11.5,
                      color: C.textMuted,
                      marginTop: 1,
                      lineHeight: 1.35,
                      overflow: 'hidden',
                    }}>
                      {agent.description.length > 80 ? agent.description.slice(0, 80) + '…' : agent.description}
                    </span>
                  )}
                  {agent.tools.length > 0 && (
                    <span style={{ display: 'block', fontSize: 10.5, color: C.textMuted, marginTop: 3 }}>
                      {agent.tools.join(', ')}
                    </span>
                  )}
                </span>
                {active && (
                  <Check size={15} strokeWidth={2.5} stroke={C.accent} style={{ flexShrink: 0, marginTop: 2 }} />
                )}
              </button>
            );
          })}
        </div>
      )}
    </div>
  );
}
