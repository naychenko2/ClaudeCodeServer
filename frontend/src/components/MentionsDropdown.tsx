import { useEffect, useRef } from 'react';
import type { Persona } from '../types';
import { C, R, FONT, SHADOW, Z } from '../lib/design';
import { personaTitleLines } from '../lib/personas';
import { PersonaAvatar } from '../features/personas/PersonaAvatar';

// Автокомплит @упоминаний персон в композере (флаг persona-mentions).
// Паттерн — SkillsDropdown: список над полем, стрелки/Enter/Tab/Escape, клик вне закрывает.
interface Props {
  personas: Persona[];
  query: string; // текст после @
  onSelect: (persona: Persona) => void;
  onClose: () => void;
  anchorRef: React.RefObject<HTMLElement | null>;
  isMobile?: boolean;
}

export function MentionsDropdown({ personas, query, onSelect, onClose, anchorRef, isMobile }: Props) {
  const containerRef = useRef<HTMLDivElement>(null);

  const q = query.toLowerCase();
  const filtered = q
    ? personas.filter(p =>
        p.handle.toLowerCase().includes(q) ||
        p.name.toLowerCase().includes(q) ||
        (p.role ?? '').toLowerCase().includes(q))
    : personas;

  // Закрытие по клику вне
  useEffect(() => {
    const onDown = (e: MouseEvent) => {
      if (
        containerRef.current && !containerRef.current.contains(e.target as Node) &&
        anchorRef.current && !anchorRef.current.contains(e.target as Node)
      ) {
        onClose();
      }
    };
    document.addEventListener('mousedown', onDown);
    return () => document.removeEventListener('mousedown', onDown);
  }, [onClose, anchorRef]);

  // Навигация стрелками
  const selectedRef = useRef(0);
  useEffect(() => { selectedRef.current = 0; }, [query]);

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') { e.stopPropagation(); onClose(); return; }
      if (filtered.length === 0) return;
      if (e.key === 'ArrowDown') {
        e.preventDefault();
        selectedRef.current = (selectedRef.current + 1) % filtered.length;
        highlightItem(selectedRef.current);
      } else if (e.key === 'ArrowUp') {
        e.preventDefault();
        selectedRef.current = (selectedRef.current - 1 + filtered.length) % filtered.length;
        highlightItem(selectedRef.current);
      } else if (e.key === 'Enter' || e.key === 'Tab') {
        e.preventDefault();
        onSelect(filtered[selectedRef.current]);
      }
    };
    document.addEventListener('keydown', onKey, true);
    return () => document.removeEventListener('keydown', onKey, true);
  }, [filtered, onSelect, onClose]);

  const highlightItem = (idx: number) => {
    const items = containerRef.current?.querySelectorAll<HTMLButtonElement>('[data-mention-item]');
    if (!items) return;
    items.forEach((el, i) => {
      el.style.background = i === idx ? C.accentLight : 'transparent';
    });
  };

  if (filtered.length === 0) return null;

  return (
    <div
      ref={containerRef}
      style={{
        position: 'absolute',
        bottom: 'calc(100% + 6px)',
        left: 0,
        right: 0,
        background: C.bgWhite,
        border: `1px solid ${C.border}`,
        borderRadius: R.xl,
        boxShadow: SHADOW.dropdown,
        padding: 4,
        zIndex: Z.dropdown,
        maxHeight: isMobile ? 220 : 280,
        overflowY: 'auto',
      }}
    >
      {filtered.map((p, idx) => (
        <button
          key={p.id}
          data-mention-item
          onClick={() => onSelect(p)}
          onMouseEnter={e => { (e.currentTarget as HTMLElement).style.background = C.accentLight; }}
          onMouseLeave={e => { (e.currentTarget as HTMLElement).style.background = 'transparent'; }}
          style={{
            width: '100%',
            display: 'flex',
            alignItems: 'center',
            gap: 9,
            padding: '7px 10px',
            borderRadius: R.md,
            border: 'none',
            background: idx === 0 && !query ? C.accentLight : 'transparent',
            cursor: 'pointer',
            textAlign: 'left',
          }}
        >
          <PersonaAvatar persona={p} size={26} />
          <span style={{ flex: 1, minWidth: 0 }}>
            <span style={{
              display: 'block', fontSize: 13, fontWeight: 600, color: C.textHeading, fontFamily: FONT.sans,
              overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
            }}>
              {personaTitleLines(p).primary}
            </span>
            <span style={{ display: 'block', fontSize: 11.5, color: C.textMuted, fontFamily: FONT.mono }}>
              @{p.handle}
            </span>
          </span>
        </button>
      ))}
    </div>
  );
}
