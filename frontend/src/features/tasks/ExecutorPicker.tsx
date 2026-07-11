// Единый пикер исполнителя задачи: Я | Claude | персона (с аватаром).
// Заменяет пару «карточки Я/Claude + отдельный select персоны». Выбор персоны
// под капотом означает assignee='claude' + personaId — задачу выполнит Claude
// от лица персоны. Список персон — доступные в контексте задачи (глобальные +
// её проекта), гейт по флагу personas.

import { useEffect, useRef, useState } from 'react';
import type { Persona, TaskAssignee } from '../../types';
import { C, FONT, R, SHADOW, Z } from '../../lib/design';
import { api } from '../../lib/api';
import { personaLabel } from '../../lib/personas';
import { PersonaAvatar } from '../personas/PersonaAvatar';
import { ClaudeBadge, MeBadge } from './bits';

export interface ExecutorValue {
  assignee: TaskAssignee;
  // id персоны-исполнителя (только при assignee='claude'); null — обычный Claude
  personaId: string | null;
}

interface Props {
  assignee: TaskAssignee;
  personaId: string | null;
  // Контекст для списка персон: null/undefined — личная задача (только глобальные)
  projectId?: string | null;
  onChange: (v: ExecutorValue) => void;
  disabled?: boolean;
}

export function ExecutorPicker({ assignee, personaId, projectId, onChange, disabled }: Props) {
  const [personas, setPersonas] = useState<Persona[]>([]);
  const [open, setOpen] = useState(false);
  const wrapRef = useRef<HTMLDivElement>(null);

  // Персоны, доступные в контексте задачи (глобальные + её проекта)
  useEffect(() => {
    let alive = true;
    api.personas.list({ scope: 'context', projectId: projectId ?? undefined })
      .then(list => { if (alive) setPersonas(list); })
      .catch(() => { if (alive) setPersonas([]); });
    return () => { alive = false; };
  }, [projectId]);

  // Закрытие по клику вне контрола
  useEffect(() => {
    if (!open) return;
    const onDoc = (e: MouseEvent) => {
      if (wrapRef.current && !wrapRef.current.contains(e.target as Node)) setOpen(false);
    };
    document.addEventListener('mousedown', onDoc);
    return () => document.removeEventListener('mousedown', onDoc);
  }, [open]);

  // Выбранная персона (если исполнитель — персона и она ещё доступна)
  const selectedPersona = personaId ? personas.find(p => p.id === personaId) ?? null : null;

  // Группы персон: команда проекта / глобальные
  const projectPersonas = personas.filter(p => p.scope === 'project' && p.projectId === projectId);
  const globalPersonas = personas.filter(p => p.scope === 'global');

  const pick = (v: ExecutorValue) => { onChange(v); setOpen(false); };

  return (
    <div ref={wrapRef} style={{ position: 'relative' }}>
      <button
        type="button"
        onClick={() => !disabled && setOpen(o => !o)}
        disabled={disabled}
        style={{
          width: '100%', display: 'flex', alignItems: 'center', gap: 11,
          padding: '11px 14px', boxSizing: 'border-box', textAlign: 'left',
          border: `1px solid ${open ? C.accent : C.border}`, borderRadius: R.xl,
          background: C.bgWhite, cursor: disabled ? 'default' : 'pointer',
          transition: 'border-color 0.12s',
        }}
      >
        {/* Текущий исполнитель: аватар + подпись */}
        {selectedPersona
          ? <PersonaAvatar persona={selectedPersona} size={34} />
          : assignee === 'claude'
            ? <ClaudeBadge size={34} />
            : <MeBadge size={34} />}
        <span style={{ flex: 1, minWidth: 0 }}>
          <span style={{ display: 'block', fontFamily: FONT.sans, fontSize: 14, fontWeight: 600, color: C.textHeading, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
            {selectedPersona ? personaLabel(selectedPersona) : assignee === 'claude' ? 'Claude' : 'Я'}
          </span>
          <span style={{ display: 'block', fontFamily: FONT.sans, fontSize: 12, color: C.textMuted, marginTop: 1 }}>
            {selectedPersona ? 'Выполнит от своего лица' : assignee === 'claude' ? 'Выполнит Claude' : 'Задача на вас'}
          </span>
        </span>
        <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke={C.textMuted} strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" style={{ flexShrink: 0, transform: open ? 'rotate(180deg)' : 'none', transition: 'transform 0.15s' }}>
          <path d="M6 9l6 6 6-6" />
        </svg>
      </button>

      {open && (
        <div
          style={{
            position: 'absolute', top: 'calc(100% + 6px)', left: 0, right: 0,
            background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.xl,
            boxShadow: SHADOW.dropdown, padding: 4, zIndex: Z.dropdown,
            maxHeight: 340, overflowY: 'auto',
          }}
        >
          <GroupHeader text="Основные" />
          <BasicItem
            icon={<MeBadge size={28} />} label="Я"
            active={assignee === 'me'}
            onClick={() => pick({ assignee: 'me', personaId: null })}
          />
          <BasicItem
            icon={<ClaudeBadge size={28} />} label="Claude"
            active={assignee === 'claude' && !personaId}
            onClick={() => pick({ assignee: 'claude', personaId: null })}
          />

          {projectPersonas.length > 0 && (
            <>
              <GroupHeader text="Команда проекта" />
              {projectPersonas.map(p => (
                <PersonaItem key={p.id} persona={p} active={p.id === personaId}
                  onClick={() => pick({ assignee: 'claude', personaId: p.id })} />
              ))}
            </>
          )}
          {globalPersonas.length > 0 && (
            <>
              <GroupHeader text="Глобальные" />
              {globalPersonas.map(p => (
                <PersonaItem key={p.id} persona={p} active={p.id === personaId}
                  onClick={() => pick({ assignee: 'claude', personaId: p.id })} />
              ))}
            </>
          )}
        </div>
      )}
    </div>
  );
}

// Заголовок группы в списке
function GroupHeader({ text }: { text: string }) {
  return (
    <div style={{
      padding: '7px 10px 3px', fontFamily: FONT.sans, fontSize: 10.5, fontWeight: 700,
      color: C.textMuted, textTransform: 'uppercase', letterSpacing: '0.06em',
    }}>
      {text}
    </div>
  );
}

// Пункт «Я» / «Claude»
function BasicItem({ icon, label, active, onClick }: { icon: React.ReactNode; label: string; active: boolean; onClick: () => void }) {
  return (
    <button type="button" onClick={onClick}
      onMouseEnter={e => { if (!active) (e.currentTarget as HTMLElement).style.background = C.accentLight; }}
      onMouseLeave={e => { if (!active) (e.currentTarget as HTMLElement).style.background = 'transparent'; }}
      style={{
        width: '100%', display: 'flex', alignItems: 'center', gap: 9, padding: '8px 10px',
        borderRadius: R.md, border: 'none', background: active ? C.accentLight : 'transparent',
        cursor: 'pointer', textAlign: 'left',
      }}
    >
      {icon}
      <span style={{ flex: 1, minWidth: 0, fontFamily: FONT.sans, fontSize: 13, fontWeight: 600, color: C.textHeading }}>{label}</span>
      {active && <Check />}
    </button>
  );
}

// Пункт персоны
function PersonaItem({ persona, active, onClick }: { persona: Persona; active: boolean; onClick: () => void }) {
  return (
    <button type="button" onClick={onClick}
      onMouseEnter={e => { if (!active) (e.currentTarget as HTMLElement).style.background = C.accentLight; }}
      onMouseLeave={e => { if (!active) (e.currentTarget as HTMLElement).style.background = 'transparent'; }}
      style={{
        width: '100%', display: 'flex', alignItems: 'center', gap: 9, padding: '8px 10px',
        borderRadius: R.md, border: 'none', background: active ? C.accentLight : 'transparent',
        cursor: 'pointer', textAlign: 'left',
      }}
    >
      <PersonaAvatar persona={persona} size={28} />
      <span style={{ flex: 1, minWidth: 0, fontFamily: FONT.sans, fontSize: 13, fontWeight: 600, color: C.textHeading, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
        {personaLabel(persona)}
      </span>
      {active && <Check />}
    </button>
  );
}

function Check() {
  return (
    <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke={C.accent} strokeWidth="3" strokeLinecap="round" strokeLinejoin="round" style={{ flexShrink: 0 }}>
      <path d="M20 6L9 17l-5-5" />
    </svg>
  );
}
