import { useEffect, useRef } from 'react';
import type { SkillInfo } from '../types';
import { C, R, FONT, SHADOW, Z } from '../lib/design';

interface Props {
  skills: SkillInfo[];
  query: string; // текст после /
  onSelect: (skill: SkillInfo) => void;
  onClose: () => void;
  anchorRef: React.RefObject<HTMLElement | null>;
  isMobile?: boolean;
}

export function SkillsDropdown({ skills, query, onSelect, onClose, anchorRef, isMobile }: Props) {
  const containerRef = useRef<HTMLDivElement>(null);

  const filtered = query
    ? skills.filter(s =>
        s.name.toLowerCase().includes(query.toLowerCase()) ||
        s.description.toLowerCase().includes(query.toLowerCase())
      )
    : skills;

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
    const items = containerRef.current?.querySelectorAll<HTMLButtonElement>('[data-skill-item]');
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
      {filtered.map((skill, idx) => (
        <button
          key={skill.name}
          data-skill-item
          onClick={() => onSelect(skill)}
          onMouseEnter={e => { (e.currentTarget as HTMLElement).style.background = C.accentLight; }}
          onMouseLeave={e => { (e.currentTarget as HTMLElement).style.background = 'transparent'; }}
          style={{
            width: '100%',
            display: 'flex',
            flexDirection: 'column',
            alignItems: 'flex-start',
            gap: 2,
            padding: '8px 10px',
            borderRadius: R.md,
            border: 'none',
            background: idx === 0 && !query ? C.accentLight : 'transparent',
            cursor: 'pointer',
            textAlign: 'left',
          }}
        >
          <span style={{
            fontFamily: FONT.mono,
            fontSize: 13,
            fontWeight: 600,
            color: C.accent,
          }}>
            /{skill.name}
            {skill.argumentHint && (
              <span style={{ color: C.textMuted, fontWeight: 400, marginLeft: 6 }}>
                {skill.argumentHint}
              </span>
            )}
          </span>
          {skill.description && (
            <span style={{
              fontSize: 11.5,
              color: C.textSecondary,
              lineHeight: 1.35,
              display: '-webkit-box',
              WebkitBoxOrient: 'vertical',
              WebkitLineClamp: 2,
              overflow: 'hidden',
            }}>
              {skill.description}
            </span>
          )}
        </button>
      ))}
    </div>
  );
}
