import { useState, useEffect, useLayoutEffect, useRef } from 'react';
import type { Persona } from '../types';
import { C, R, FONT, SHADOW, Z } from '../lib/design';
import { personaLabel } from '../lib/personas';
import { PersonaAvatar } from '../features/agents/PersonaAvatar';

interface Props {
  personas: Persona[];
  selectedPersona: Persona | null;
  onSelect: (persona: Persona | null) => void;
  isMobile?: boolean;
  // Направление раскрытия: по умолчанию вверх (composer прижат к низу).
  // dropUp=false — раскрытие вниз (для триггера у верха панели, напр. список чатов).
  dropUp?: boolean;
}

// Селектор олицетворённого агента для композера пустого чата — «с кем ведём разговор».
// По образцу AgentSelector: кнопка-триггер (аватар + имя выбранной персоны либо
// компактная иконка), выпадающий список персон + пункт «Без агента». Раскрывается
// вверх (composer прижат к низу). Показывается родителем только для пустого чата.
export function PersonaSelector({ personas, selectedPersona, onSelect, isMobile, dropUp = true }: Props) {
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

  useLayoutEffect(() => {
    if (!open || !isMobile || !rootRef.current) return;
    const rect = rootRef.current.getBoundingClientRect();
    // Вверх — от верха триггера к верху экрана; вниз — от низа триггера к низу экрана
    setFixedPos(dropUp
      ? { bottom: window.innerHeight - rect.top + 6, top: 16, left: 16, right: 16 }
      : { bottom: 16, top: rect.bottom + 6, left: 16, right: 16 });
  }, [open, isMobile, dropUp]);

  if (personas.length === 0) return null;

  const dropdownStyle: React.CSSProperties = isMobile && fixedPos
    ? {
        position: 'fixed',
        bottom: fixedPos.bottom, top: fixedPos.top, left: fixedPos.left, right: fixedPos.right,
        overflowY: 'auto', background: C.bgWhite, border: `1px solid ${C.border}`,
        borderRadius: R.xl, boxShadow: SHADOW.dropdown, padding: 4, zIndex: Z.dropdown,
      }
    : {
        position: 'absolute', right: 0,
        ...(dropUp ? { bottom: 'calc(100% + 6px)' } : { top: 'calc(100% + 6px)' }),
        minWidth: 300, maxWidth: 'calc(100vw - 32px)', maxHeight: 'min(70vh, 460px)',
        overflowY: 'auto', background: C.bgWhite, border: `1px solid ${C.border}`,
        borderRadius: R.xl, boxShadow: SHADOW.dropdown, padding: 4, zIndex: Z.dropdown,
      };

  return (
    <div ref={rootRef} style={{ position: 'relative', flexShrink: 0, minWidth: 0 }}>
      {selectedPersona ? (
        // Агент выбран — плашка с мини-аватаром и именем
        <button
          onClick={() => setOpen(o => !o)}
          title="Выбрать агента для чата"
          style={{
            height: isMobile ? 32 : 28, padding: '0 8px 0 4px', borderRadius: R.md, border: 'none',
            background: open ? C.bgSelected : C.accentLight, color: C.textSecondary,
            fontSize: 12.5, fontWeight: 600, cursor: 'pointer',
            display: 'flex', alignItems: 'center', gap: 6,
            maxWidth: isMobile ? 120 : 200, overflow: 'hidden',
          }}
        >
          <PersonaAvatar persona={selectedPersona} size={isMobile ? 24 : 20} />
          <span style={{ fontFamily: FONT.sans, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', minWidth: 0, flex: 1 }}>
            {personaLabel(selectedPersona)}
          </span>
          <svg width="9" height="9" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="3"
            strokeLinecap="round" strokeLinejoin="round" style={{ flexShrink: 0, opacity: 0.55, transform: open ? 'rotate(180deg)' : 'none', transition: 'transform 0.15s' }}>
            <path d="M6 9l6 6 6-6" />
          </svg>
        </button>
      ) : (
        // Агент не выбран — компактная иконка «собеседник»
        <button
          onClick={() => setOpen(o => !o)}
          title="Выбрать агента для чата"
          style={{
            width: isMobile ? 32 : 28, height: isMobile ? 32 : 28, borderRadius: R.md, border: 'none',
            background: open ? C.bgSelected : 'transparent', color: C.textMuted, cursor: 'pointer',
            display: 'flex', alignItems: 'center', justifyContent: 'center', padding: 0, flexShrink: 0,
          }}
        >
          <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2"
            strokeLinecap="round" strokeLinejoin="round">
            <path d="M21 11.5a8.5 8.5 0 0 1-12 7.7L3 21l1.8-6A8.5 8.5 0 1 1 21 11.5z" />
          </svg>
        </button>
      )}

      {open && (
        <div style={dropdownStyle}>
          {selectedPersona && (
            <button
              onClick={() => { onSelect(null); setOpen(false); }}
              onMouseEnter={e => { (e.currentTarget as HTMLElement).style.background = C.bgSelected; }}
              onMouseLeave={e => { (e.currentTarget as HTMLElement).style.background = 'transparent'; }}
              style={{
                width: '100%', display: 'flex', alignItems: 'center', gap: 8, padding: '8px 10px',
                borderRadius: R.md, border: 'none', background: 'transparent', cursor: 'pointer',
                textAlign: 'left', color: C.textMuted, fontSize: 12.5,
              }}
            >
              <span style={{ width: 20, height: 20, borderRadius: R.full, border: `1.5px dashed ${C.border}`, flexShrink: 0 }} />
              Без агента
            </button>
          )}

          {/* Группировка: команда проекта сверху, глобальные ниже. Заголовки групп —
              только когда есть обе группы (иначе зона очевидна из контекста). */}
          {(() => {
            const projectAgents = personas.filter(p => p.scope === 'project');
            const globalAgents = personas.filter(p => p.scope === 'global');
            const showHeaders = projectAgents.length > 0 && globalAgents.length > 0;
            const groupHeader = (text: string) => (
              <div style={{
                padding: '7px 10px 3px', fontSize: 10.5, fontWeight: 700, color: C.textMuted,
                textTransform: 'uppercase', letterSpacing: '0.06em', fontFamily: FONT.sans,
              }}>{text}</div>
            );
            const item = (persona: Persona) => {
              const active = selectedPersona?.id === persona.id;
              return (
                <button
                  key={persona.id}
                  onClick={() => { onSelect(persona); setOpen(false); }}
                  onMouseEnter={e => { if (!active) (e.currentTarget as HTMLElement).style.background = C.accentLight; }}
                  onMouseLeave={e => { if (!active) (e.currentTarget as HTMLElement).style.background = 'transparent'; }}
                  style={{
                    width: '100%', display: 'flex', alignItems: 'center', gap: 9, padding: '8px 10px',
                    borderRadius: R.md, border: 'none', background: active ? C.accentLight : 'transparent',
                    cursor: 'pointer', textAlign: 'left',
                  }}
                >
                  <PersonaAvatar persona={persona} size={28} />
                  <span style={{ flex: 1, minWidth: 0, display: 'block', fontSize: 13, fontWeight: 600, color: C.textHeading, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                    {personaLabel(persona)}
                  </span>
                  {active && (
                    <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke={C.accent} strokeWidth="3"
                      strokeLinecap="round" strokeLinejoin="round" style={{ flexShrink: 0 }}>
                      <path d="M20 6L9 17l-5-5" />
                    </svg>
                  )}
                </button>
              );
            };
            return (
              <>
                {showHeaders && projectAgents.length > 0 && groupHeader('Команда проекта')}
                {projectAgents.map(item)}
                {showHeaders && globalAgents.length > 0 && groupHeader('Глобальные')}
                {globalAgents.map(item)}
              </>
            );
          })()}
        </div>
      )}
    </div>
  );
}
