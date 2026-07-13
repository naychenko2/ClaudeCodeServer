import { useState, useEffect, useRef } from 'react';
import { ChevronDown } from 'lucide-react';
import type { Persona } from '../types';
import { C, R, FONT, SHADOW, Z } from '../lib/design';
import { personaLabel } from '../lib/personas';
import { PersonaAvatar } from '../features/personas/PersonaAvatar';

// === Компактная строка фильтров списка чатов ===
// Pill-переключатели и дропдауны в едином UI-языке приложения.

interface FilterBarProps {
  /** Скрывать чаты-исполнители задач (taskExecution === true) */
  hideTaskChats: boolean;
  onChangeHideTaskChats: (v: boolean) => void;
  /** Только чаты с updatedAt не старше 5 минут */
  activeOnly: boolean;
  onChangeActiveOnly: (v: boolean) => void;
  /** Фильтр по id персоны: null = все */
  filterPersonaId: string | null;
  onChangeFilterPersona: (id: string | null) => void;
  /** ID персон, присутствующих в списке (для селектора) */
  personaIdsInList: string[];
  /** Все персоны (для резолва имени/аватара) */
  allPersonas: Persona[];
  /** Сколько чатов скрыто активными фильтрами */
  hiddenCount: number;
  isMobile?: boolean;
}

// === Pill-кнопка внутри сегмента ===
function PillBtn({ active, label, onClick, isFirst, isLast }: {
  active: boolean;
  label: string;
  onClick: () => void;
  isFirst: boolean;
  isLast: boolean;
}) {
  return (
    <button
      onClick={onClick}
      style={{
        padding: '3px 10px',
        fontSize: 12,
        fontWeight: 600,
        fontFamily: FONT.sans,
        border: 'none',
        background: active ? C.accent : 'transparent',
        color: active ? C.onAccent : C.textSecondary,
        cursor: 'pointer',
        lineHeight: '20px',
        borderTopLeftRadius: isFirst ? R.pill : 0,
        borderBottomLeftRadius: isFirst ? R.pill : 0,
        borderTopRightRadius: isLast ? R.pill : 0,
        borderBottomRightRadius: isLast ? R.pill : 0,
        borderRight: isLast ? 'none' : `1px solid ${C.borderLight}`,
        transition: 'background 0.15s, color 0.15s',
      }}
    >
      {label}
    </button>
  );
}

// === Группа pill-переключателей (сегмент) ===
function PillGroup<T extends string>({ options, value, onChange }: {
  options: { value: T; label: string; tooltip?: string }[];
  value: T;
  onChange: (v: T) => void;
}) {
  return (
    <div
      title={options.find(o => o.value === value)?.tooltip}
      style={{
        display: 'flex',
        borderRadius: R.pill,
        border: `1px solid ${C.borderLight}`,
        overflow: 'hidden',
        flexShrink: 0,
      }}
    >
      {options.map((o, i) => (
        <PillBtn
          key={o.value}
          active={o.value === value}
          label={o.label}
          onClick={() => onChange(o.value)}
          isFirst={i === 0}
          isLast={i === options.length - 1}
        />
      ))}
    </div>
  );
}

export function FilterBar({
  hideTaskChats, onChangeHideTaskChats,
  activeOnly, onChangeActiveOnly,
  filterPersonaId, onChangeFilterPersona,
  personaIdsInList, allPersonas,
  hiddenCount, isMobile,
}: FilterBarProps) {
  const [personaOpen, setPersonaOpen] = useState(false);
  const personaRef = useRef<HTMLDivElement>(null);

  // Закрытие по клику вне дропдауна
  useEffect(() => {
    if (!personaOpen) return;
    const onDown = (e: MouseEvent) => {
      if (personaRef.current && !personaRef.current.contains(e.target as Node))
        setPersonaOpen(false);
    };
    document.addEventListener('mousedown', onDown);
    return () => document.removeEventListener('mousedown', onDown);
  }, [personaOpen]);

  // Персоны из списка чатов (резолв по id)
  const personasInChats = personaIdsInList
    .map(id => allPersonas.find(p => p.id === id))
    .filter((p): p is Persona => p !== undefined);

  const selectedPersona = filterPersonaId
    ? allPersonas.find(p => p.id === filterPersonaId) ?? null
    : null;

  const showPersonaFilter = personaIdsInList.length > 0;

  return (
    <div style={{
      padding: '6px 8px',
      display: 'flex',
      alignItems: 'center',
      gap: 6,
      flexWrap: 'wrap',
    }}>
      {/* Task filter */}
      <PillGroup
        options={[
          { value: 'all' as const, label: 'Все чаты' },
          { value: 'hide' as const, label: 'Без задач' },
        ]}
        value={hideTaskChats ? 'hide' : 'all'}
        onChange={v => onChangeHideTaskChats(v === 'hide')}
      />

      {/* Active filter */}
      <PillGroup
        options={[
          { value: 'all' as const, label: 'Все' },
          { value: 'active' as const, label: 'Активные', tooltip: 'Обновлялись менее 5 мин назад' },
        ]}
        value={activeOnly ? 'active' : 'all'}
        onChange={v => onChangeActiveOnly(v === 'active')}
      />

      {/* Persona filter dropdown */}
      {showPersonaFilter && (
        <div ref={personaRef} style={{ position: 'relative', flexShrink: 0 }}>
          <button
            onClick={() => setPersonaOpen(o => !o)}
            title="Фильтр по персоне"
            style={{
              height: 26,
              padding: '0 8px',
              borderRadius: R.pill,
              border: `1px solid ${C.borderLight}`,
              background: personaOpen || filterPersonaId ? C.bgSelected : 'transparent',
              color: filterPersonaId ? C.textHeading : C.textSecondary,
              fontSize: 12,
              fontWeight: 600,
              fontFamily: FONT.sans,
              cursor: 'pointer',
              display: 'flex',
              alignItems: 'center',
              gap: 4,
              transition: 'background 0.15s',
              maxWidth: isMobile ? 100 : 140,
            }}
          >
            {selectedPersona ? (
              <>
                <PersonaAvatar persona={selectedPersona} size={16} />
                <span style={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                  {personaLabel(selectedPersona)}
                </span>
              </>
            ) : (
              <span>Персона</span>
            )}
            <ChevronDown size={9} strokeWidth={2.5}
              style={{
                flexShrink: 0,
                opacity: 0.55,
                transform: personaOpen ? 'rotate(180deg)' : 'none',
                transition: 'transform 0.15s',
              }}
            />
          </button>

          {personaOpen && (
            <div style={{
              position: 'absolute',
              top: 'calc(100% + 4px)',
              left: 0,
              minWidth: 180,
              maxWidth: 240,
              background: C.bgWhite,
              border: `1px solid ${C.border}`,
              borderRadius: R.lg,
              boxShadow: SHADOW.dropdown,
              padding: 4,
              zIndex: Z.dropdown,
            }}>
              <button
                onClick={() => { onChangeFilterPersona(null); setPersonaOpen(false); }}
                onMouseEnter={e => { if (filterPersonaId) (e.currentTarget as HTMLElement).style.background = C.accentLight; }}
                onMouseLeave={e => { if (filterPersonaId) (e.currentTarget as HTMLElement).style.background = 'transparent'; }}
                style={{
                  width: '100%',
                  display: 'flex',
                  alignItems: 'center',
                  gap: 8,
                  padding: '6px 8px',
                  borderRadius: R.md,
                  border: 'none',
                  background: !filterPersonaId ? C.accentLight : 'transparent',
                  cursor: 'pointer',
                  textAlign: 'left',
                  fontSize: 12.5,
                  fontWeight: 600,
                  color: C.textHeading,
                  fontFamily: FONT.sans,
                }}
              >
                <span style={{
                  width: 18, height: 18, borderRadius: R.full,
                  border: `1.5px dashed ${C.border}`, flexShrink: 0,
                }} />
                Все
              </button>
              {personasInChats.map(p => (
                <button
                  key={p.id}
                  onClick={() => { onChangeFilterPersona(p.id); setPersonaOpen(false); }}
                  onMouseEnter={e => { if (filterPersonaId !== p.id) (e.currentTarget as HTMLElement).style.background = C.accentLight; }}
                  onMouseLeave={e => { if (filterPersonaId !== p.id) (e.currentTarget as HTMLElement).style.background = 'transparent'; }}
                  style={{
                    width: '100%',
                    display: 'flex',
                    alignItems: 'center',
                    gap: 8,
                    padding: '6px 8px',
                    borderRadius: R.md,
                    border: 'none',
                    background: filterPersonaId === p.id ? C.accentLight : 'transparent',
                    cursor: 'pointer',
                    textAlign: 'left',
                    fontSize: 12.5,
                    fontWeight: 600,
                    color: C.textHeading,
                    fontFamily: FONT.sans,
                  }}
                >
                  <PersonaAvatar persona={p} size={18} />
                  <span style={{
                    flex: 1,
                    overflow: 'hidden',
                    textOverflow: 'ellipsis',
                    whiteSpace: 'nowrap',
                  }}>
                    {personaLabel(p)}
                  </span>
                </button>
              ))}
            </div>
          )}
        </div>
      )}

      {/* Hidden count badge */}
      {hiddenCount > 0 && (
        <span style={{
          flexShrink: 0,
          fontSize: 11,
          fontWeight: 700,
          color: C.textMuted,
          fontFamily: FONT.mono,
          padding: '1px 7px',
          borderRadius: R.pill,
          background: C.bgSelected,
          lineHeight: '20px',
        }}>
          +{hiddenCount}
        </span>
      )}
    </div>
  );
}
