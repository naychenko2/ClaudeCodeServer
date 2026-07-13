import { useState, useEffect, useRef } from 'react';
import { Filter } from 'lucide-react';
import type { Persona } from '../types';
import { C, R, FONT, SHADOW, Z } from '../lib/design';
import { personaLabel } from '../lib/personas';
import { PersonaAvatar } from '../features/personas/PersonaAvatar';

// === Компактный триггер фильтрации списка чатов ===
// Одна едва заметная ссылка/иконка — при нажатии открывается поповер с настройками.
// Когда фильтры активны — рядом показывается краткая сводка.

interface FilterBarProps {
  hideTaskChats: boolean;
  onChangeHideTaskChats: (v: boolean) => void;
  activeOnly: boolean;
  onChangeActiveOnly: (v: boolean) => void;
  filterPersonaId: string | null;
  onChangeFilterPersona: (id: string | null) => void;
  personaIdsInList: string[];
  allPersonas: Persona[];
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
        padding: '4px 11px',
        fontSize: 12.5,
        fontWeight: 600,
        fontFamily: FONT.sans,
        border: 'none',
        background: active ? C.accent : 'transparent',
        color: active ? C.onAccent : C.textSecondary,
        cursor: 'pointer',
        lineHeight: '24px',
        borderTopLeftRadius: isFirst ? R.pill : 0,
        borderBottomLeftRadius: isFirst ? R.pill : 0,
        borderTopRightRadius: isLast ? R.pill : 0,
        borderBottomRightRadius: isLast ? R.pill : 0,
        borderRight: isLast ? 'none' : `1px solid ${C.borderLight}`,
        transition: 'background 0.12s',
      }}
    >
      {label}
    </button>
  );
}

// === Группа pill-переключателей (сегмент) ===
function PillGroup<T extends string>({ options, value, onChange, label }: {
  options: { value: T; label: string }[];
  value: T;
  onChange: (v: T) => void;
  label: string;
}) {
  return (
    <div style={{ marginBottom: 4 }}>
      <div style={{
        fontSize: 10.5, fontWeight: 700, color: C.textMuted,
        textTransform: 'uppercase', letterSpacing: '0.06em',
        marginBottom: 6, fontFamily: FONT.sans,
      }}>
        {label}
      </div>
      <div style={{
        display: 'flex',
        borderRadius: R.pill,
        border: `1px solid ${C.borderLight}`,
        overflow: 'hidden',
      }}>
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
  const [open, setOpen] = useState(false);
  const rootRef = useRef<HTMLDivElement>(null);

  // Закрытие по клику вне попапа
  useEffect(() => {
    if (!open) return;
    const onDown = (e: MouseEvent) => {
      if (rootRef.current && !rootRef.current.contains(e.target as Node))
        setOpen(false);
    };
    document.addEventListener('mousedown', onDown);
    return () => document.removeEventListener('mousedown', onDown);
  }, [open]);

  const hasFilters = hideTaskChats || activeOnly || filterPersonaId !== null;

  // Сводка активных фильтров
  const summaryParts: string[] = [];
  if (hideTaskChats) summaryParts.push('без задач');
  if (activeOnly) summaryParts.push('активные');
  const selectedPersona = filterPersonaId
    ? allPersonas.find(p => p.id === filterPersonaId) ?? null
    : null;
  if (selectedPersona) summaryParts.push(personaLabel(selectedPersona));

  const personasInChats = personaIdsInList
    .map(id => allPersonas.find(p => p.id === id))
    .filter((p): p is Persona => p !== undefined);

  const showPersonaFilter = personaIdsInList.length > 0;

  const popoverStyle: React.CSSProperties = isMobile
    ? {
        position: 'fixed',
        top: 60, left: 12, right: 12,
        maxHeight: 'calc(100vh - 80px)',
        overflowY: 'auto',
        background: C.bgWhite,
        border: `1px solid ${C.border}`,
        borderRadius: R.xl,
        boxShadow: SHADOW.dropdown,
        padding: 12,
        zIndex: Z.dropdown,
      }
    : {
        position: 'absolute',
        top: 'calc(100% + 4px)',
        left: 0,
        minWidth: 280,
        background: C.bgWhite,
        border: `1px solid ${C.border}`,
        borderRadius: R.xl,
        boxShadow: SHADOW.dropdown,
        padding: 12,
        zIndex: Z.dropdown,
      };

  return (
    <div ref={rootRef} style={{ position: 'relative', flexShrink: 0 }}>
      {/* Триггер — компактный, почти незаметный когда фильтры по умолчанию */}
      <div
        onClick={() => setOpen(o => !o)}
        style={{
          display: 'flex',
          alignItems: 'center',
          gap: 4,
          cursor: 'pointer',
          userSelect: 'none',
          padding: '2px 0',
          color: hasFilters ? C.accent : C.textMuted,
          fontSize: 12,
          fontWeight: 600,
          fontFamily: FONT.sans,
          transition: 'color 0.15s',
          opacity: hasFilters ? 1 : 0.5,
        }}
        title={hasFilters ? summaryParts.join(', ') : 'Фильтры'}
      >
        <Filter size={12} strokeWidth={2.2} style={{ flexShrink: 0 }} />
        <span style={{ marginLeft: 1 }}>
          {hasFilters ? summaryParts.join(', ') : 'Фильтр'}
        </span>
        {hiddenCount > 0 && (
          <span style={{
            fontSize: 10, fontWeight: 700, fontFamily: FONT.mono,
            color: C.onAccent, background: C.accent,
            padding: '0 5px', borderRadius: R.pill, lineHeight: '16px',
            minWidth: 16, textAlign: 'center',
          }}>
            {hiddenCount}
          </span>
        )}
      </div>

      {/* Поповер */}
      {open && (
        <div style={popoverStyle}>
          <PillGroup
            label="Показывать"
            options={[
              { value: 'all' as const, label: 'Все чаты' },
              { value: 'hide' as const, label: 'Без задач' },
            ]}
            value={hideTaskChats ? 'hide' : 'all'}
            onChange={v => onChangeHideTaskChats(v === 'hide')}
          />

          <div style={{ height: 1, background: C.divider, margin: '10px 0' }} />

          <PillGroup
            label="Время"
            options={[
              { value: 'all' as const, label: 'Все' },
              { value: 'active' as const, label: 'Последние 5 мин' },
            ]}
            value={activeOnly ? 'active' : 'all'}
            onChange={v => onChangeActiveOnly(v === 'active')}
          />

          {showPersonaFilter && (
            <>
              <div style={{ height: 1, background: C.divider, margin: '10px 0' }} />
              <div style={{ marginBottom: 4 }}>
                <div style={{
                  fontSize: 10.5, fontWeight: 700, color: C.textMuted,
                  textTransform: 'uppercase', letterSpacing: '0.06em',
                  marginBottom: 6, fontFamily: FONT.sans,
                }}>
                  Персона
                </div>
                <div style={{ display: 'flex', flexWrap: 'wrap', gap: 4 }}>
                  <button
                    onClick={() => onChangeFilterPersona(null)}
                    style={{
                      padding: '4px 10px',
                      borderRadius: R.pill,
                      border: `1px solid ${C.borderLight}`,
                      background: !filterPersonaId ? C.accent : 'transparent',
                      color: !filterPersonaId ? C.onAccent : C.textSecondary,
                      fontSize: 12,
                      fontWeight: 600,
                      fontFamily: FONT.sans,
                      cursor: 'pointer',
                      transition: 'background 0.12s',
                    }}
                  >
                    Все
                  </button>
                  {personasInChats.map(p => (
                    <button
                      key={p.id}
                      onClick={() => onChangeFilterPersona(
                        filterPersonaId === p.id ? null : p.id
                      )}
                      style={{
                        display: 'inline-flex',
                        alignItems: 'center',
                        gap: 4,
                        padding: '4px 10px',
                        borderRadius: R.pill,
                        border: `1px solid ${C.borderLight}`,
                        background: filterPersonaId === p.id ? C.accent : 'transparent',
                        color: filterPersonaId === p.id ? C.onAccent : C.textSecondary,
                        fontSize: 12,
                        fontWeight: 600,
                        fontFamily: FONT.sans,
                        cursor: 'pointer',
                        transition: 'background 0.12s',
                      }}
                    >
                      <PersonaAvatar persona={p} size={14} />
                      <span>{personaLabel(p)}</span>
                    </button>
                  ))}
                </div>
              </div>
            </>
          )}

          <div style={{
            marginTop: 12,
            paddingTop: 10,
            borderTop: `1px solid ${C.divider}`,
            display: 'flex',
            justifyContent: 'space-between',
            alignItems: 'center',
          }}>
            <span style={{ fontSize: 11.5, color: C.textMuted, fontFamily: FONT.sans }}>
              {hiddenCount > 0
                ? `Скрыто ${hiddenCount} ${hiddenCount === 1 ? 'чат' : 'чатов'}`
                : 'Все чаты показаны'}
            </span>
            <button
              onClick={() => setOpen(false)}
              style={{
                padding: '5px 14px',
                borderRadius: R.md,
                border: 'none',
                background: C.accent,
                color: C.onAccent,
                fontSize: 12.5,
                fontWeight: 600,
                fontFamily: FONT.sans,
                cursor: 'pointer',
              }}
            >
              Готово
            </button>
          </div>
        </div>
      )}
    </div>
  );
}
