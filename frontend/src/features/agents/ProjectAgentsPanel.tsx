import { useEffect, useState } from 'react';
import type { Persona, Project, Session } from '../../types';
import { api } from '../../lib/api';
import { usePersonas, ensurePersonasLoaded, bumpPersonas, personaTitleLines } from '../../lib/personas';
import { AGENT_COLORS } from '../../components/AgentSelector';
import { C, FONT, R } from '../../lib/design';
import { PersonaList } from './PersonaList';
import { PersonaForm } from './PersonaForm';
import { PersonaAvatar } from './PersonaAvatar';

// Проектная вкладка «Агенты»: управление персонами ЭТОГО проекта прямо в
// workspace (без ухода в глобальный хаб «Агенты») + запуск чата с агентом внутри
// проекта. Самодостаточная панель: список ↔ студия (форма профиля + «Поговорить»).
export function ProjectAgentsPanel({ project, onOpenChat }: {
  project: Project;
  // Открыть созданный «Поговорить» чат на месте (переключить на «Чаты» и выбрать сессию)
  onOpenChat: (session: Session) => void;
}) {
  const personas = usePersonas();
  useEffect(() => { void ensurePersonasLoaded(); }, []);

  // Только персоны этого проекта (глобальные живут в хабе «Агенты»)
  const projectPersonas = personas.filter(p => p.scope === 'project' && p.projectId === project.id);

  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [creating, setCreating] = useState(false);
  const [talking, setTalking] = useState(false);

  const selected = projectPersonas.find(p => p.id === selectedId) ?? null;

  const backToList = () => { setSelectedId(null); setCreating(false); };

  const onDelete = async (p: Persona) => {
    if (!window.confirm(`Удалить агента «${personaTitleLines(p).primary}»?`)) return;
    try {
      await api.personas.remove(p.id);
      bumpPersonas();
      backToList();
    } catch {
      alert('Не удалось удалить агента.');
    }
  };

  // «Поговорить»: чат от лица проектной персоны — сессия создаётся в этом проекте.
  // Мы уже внутри нужного проекта, поэтому открываем её напрямую (без cc_pending_session).
  const talk = async (p: Persona) => {
    if (talking) return;
    setTalking(true);
    try {
      const session = await api.personas.createChat(p.id, { mode: 'auto' });
      onOpenChat(session);
    } catch (e) {
      alert(e instanceof Error ? e.message : 'Не удалось создать чат');
    } finally {
      setTalking(false);
    }
  };

  // Список — сайдбарная раскладка (кнопка «Новый агент» + персоны проекта)
  if (!creating && !selected) {
    return (
      <div style={{ display: 'flex', flexDirection: 'column', height: '100%', background: C.bgPanel }}>
        <PersonaList
          personas={projectPersonas}
          selectedId={null}
          onSelect={id => { setCreating(false); setSelectedId(id); }}
          onNew={() => { setSelectedId(null); setCreating(true); }}
        />
      </div>
    );
  }

  // Студия: шапка (аватар + подпись + назад + «Поговорить») и инлайн-форма профиля
  const accent = selected ? (AGENT_COLORS[selected.avatar?.color ?? ''] ?? C.accent) : C.accent;

  return (
    <div style={{ height: '100%', display: 'flex', flexDirection: 'column', overflow: 'hidden' }}>
      <div style={{
        flex: 'none', display: 'flex', alignItems: 'center', gap: 10, padding: '8px 12px',
        borderBottom: `1px solid ${C.border}`, borderLeft: `3px solid ${accent}`, background: C.bgPanel,
      }}>
        <button onClick={backToList} aria-label="Назад" style={iconBtn}>
          <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
            <path d="M15 18l-6-6 6-6" />
          </svg>
        </button>
        {selected ? (
          <>
            <PersonaAvatar persona={selected} size={30} />
            <div style={{ minWidth: 0, display: 'flex', flexDirection: 'column', gap: 1, flex: 1 }}>
              <div style={{ fontFamily: FONT.serif, fontSize: 15, fontWeight: 600, color: accent, letterSpacing: '-0.01em', overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                {personaTitleLines(selected).primary}
              </div>
              {personaTitleLines(selected).secondary && (
                <div style={{ fontSize: 11.5, color: C.textMuted, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                  {personaTitleLines(selected).secondary}
                </div>
              )}
            </div>
            <button onClick={() => talk(selected)} disabled={talking}
              style={{ ...talkBtn(accent), opacity: talking ? 0.6 : 1, cursor: talking ? 'default' : 'pointer' }}>
              <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
                <path d="M21 11.5a8.5 8.5 0 0 1-12 7.7L3 21l1.8-6A8.5 8.5 0 1 1 21 11.5z" />
              </svg>
              {talking ? 'Создаём…' : 'Поговорить'}
            </button>
          </>
        ) : (
          <div style={{ fontFamily: FONT.serif, fontSize: 15, fontWeight: 600, color: C.textHeading, letterSpacing: '-0.01em' }}>
            Новый агент
          </div>
        )}
      </div>
      <div style={{ flex: 1, minHeight: 0 }}>
        {selected ? (
          <PersonaForm
            key={selected.id}
            persona={selected}
            projects={[project]}
            onSaved={() => {}}
            onDelete={() => onDelete(selected)}
          />
        ) : (
          <PersonaForm
            persona={null}
            projects={[project]}
            defaultScope="project"
            defaultProjectId={project.id}
            onSaved={p => { setCreating(false); setSelectedId(p.id); }}
            onCancel={backToList}
          />
        )}
      </div>
    </div>
  );
}

const iconBtn: React.CSSProperties = {
  width: 32, height: 32, borderRadius: R.md, border: 'none', background: 'transparent',
  color: C.textSecondary, cursor: 'pointer', display: 'flex', alignItems: 'center', justifyContent: 'center', flexShrink: 0,
};
// Кнопка «Поговорить» — залита акцентом персоны
function talkBtn(accent: string): React.CSSProperties {
  return {
    display: 'inline-flex', alignItems: 'center', gap: 7, flexShrink: 0,
    background: accent, color: '#fff', border: 'none', borderRadius: R.lg,
    padding: '7px 14px', fontSize: 13, fontWeight: 600, fontFamily: FONT.sans,
  };
}
