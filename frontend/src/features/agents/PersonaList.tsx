import type { Persona } from '../../types';
import { C, FONT, R } from '../../lib/design';
import { personaTitleLines } from '../../lib/personas';
import { PersonaAvatar } from './PersonaAvatar';

// Иконка «плюс» для кнопки создания
function IconPlus() {
  return (
    <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor"
      strokeWidth="2.4" strokeLinecap="round" strokeLinejoin="round">
      <path d="M12 5v14M5 12h14" />
    </svg>
  );
}

// Бейдж зоны контекста персоны
function ScopeBadge({ persona }: { persona: Persona }) {
  const isProject = persona.scope === 'project';
  return (
    <span style={{
      flexShrink: 0, fontFamily: FONT.sans, fontSize: 10, fontWeight: 600,
      color: C.textMuted, background: C.bgSelected, borderRadius: R.sm,
      padding: '2px 6px', lineHeight: 1.4, whiteSpace: 'nowrap',
    }}>
      {isProject ? 'Проект' : 'Глобальный'}
    </span>
  );
}

// Сайдбар раздела «Агенты»: кнопка создания сверху, ниже — список персон.
export function PersonaList({ personas, selectedId, onSelect, onNew }: {
  personas: Persona[];
  selectedId: string | null;
  onSelect: (id: string) => void;
  onNew: () => void;
}) {
  return (
    <>
      <div style={{ padding: '10px 10px 9px', borderBottom: `1px solid ${C.border}`, flex: 'none' }}>
        <button onClick={onNew} style={newBtn}>
          <IconPlus />Новый агент
        </button>
      </div>
      <div style={{ flex: 1, minHeight: 0, overflowY: 'auto', padding: 6 }}>
        {personas.length === 0 ? (
          <div style={{ padding: '20px 12px', color: C.textMuted, fontSize: 13, fontFamily: FONT.sans, lineHeight: 1.5 }}>
            Пока нет агентов. Создай первого — задай ему имя, характер и аватар.
          </div>
        ) : (
          personas.map(p => {
            const active = p.id === selectedId;
            return (
              <button
                key={p.id}
                onClick={() => onSelect(p.id)}
                onMouseEnter={e => { if (!active) (e.currentTarget as HTMLElement).style.background = C.accentLight; }}
                onMouseLeave={e => { if (!active) (e.currentTarget as HTMLElement).style.background = 'transparent'; }}
                style={{
                  width: '100%', display: 'flex', alignItems: 'center', gap: 10,
                  padding: '8px 10px', borderRadius: R.md, border: 'none', cursor: 'pointer',
                  textAlign: 'left', marginBottom: 2,
                  background: active ? C.accentMuted : 'transparent',
                }}
              >
                <PersonaAvatar persona={p} size={32} />
                <span style={{ flex: 1, minWidth: 0 }}>
                  {/* Роль — главная строка, имя под ней (мельче, приглушённо) */}
                  <span style={{
                    display: 'block', fontSize: 13, fontWeight: 600, color: C.textHeading,
                    overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
                  }}>
                    {personaTitleLines(p).primary}
                  </span>
                  {personaTitleLines(p).secondary && (
                    <span style={{
                      display: 'block', fontSize: 11.5, color: C.textMuted, marginTop: 1,
                      overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
                    }}>
                      {personaTitleLines(p).secondary}
                    </span>
                  )}
                  {p.description && (
                    <span style={{
                      display: 'block', fontSize: 11.5, color: C.textMuted, marginTop: 1,
                      overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
                    }}>
                      {p.description}
                    </span>
                  )}
                </span>
                <ScopeBadge persona={p} />
              </button>
            );
          })
        )}
      </div>
    </>
  );
}

const newBtn: React.CSSProperties = {
  width: '100%', display: 'flex', alignItems: 'center', justifyContent: 'center', gap: 5,
  background: C.accent, color: C.onAccent, border: 'none', borderRadius: R.md,
  padding: '8px 12px', fontSize: 13, fontWeight: 500, cursor: 'pointer', fontFamily: FONT.sans,
};
