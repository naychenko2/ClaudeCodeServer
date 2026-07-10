import { useCallback, useEffect, useRef, useState } from 'react';
import type { Persona, Project, Session } from '../../types';
import { api } from '../../lib/api';
import { usePersonas, ensurePersonasLoaded, bumpPersonas, personaTitleLines } from '../../lib/personas';
import { AGENT_COLORS } from '../../components/AgentSelector';
import { C, FONT, R } from '../../lib/design';
import { PersonaList } from './PersonaList';
import { PersonaForm, type PersonaFormHandle, type PersonaFormStatus } from './PersonaForm';
import { PersonaToolbar } from './PersonaToolbar';
import { PersonaMemoryPanel } from './PersonaMemoryPanel';

function useIsMobile(): boolean {
  const [m, setM] = useState(() => typeof window !== 'undefined' && window.matchMedia('(max-width: 767px)').matches);
  useEffect(() => {
    const mq = window.matchMedia('(max-width: 767px)');
    const h = (e: MediaQueryListEvent) => setM(e.matches);
    mq.addEventListener('change', h);
    return () => mq.removeEventListener('change', h);
  }, []);
  return m;
}

// Проектная вкладка «Агенты»: САЙДБАРНЫЙ СПИСОК персон этого проекта.
// Форма редактирования/создания живёт отдельно — в центральной зоне (ProjectAgentPane).
// Выбор синхронизируется через контролируемые props (состояние поднято в WorkspacePage).
export function ProjectAgentsPanel({ project, selectedId, onSelect, onNew }: {
  project: Project;
  selectedId: string | null;
  onSelect: (id: string) => void;
  onNew: () => void;
}) {
  const personas = usePersonas();
  useEffect(() => { void ensurePersonasLoaded(); }, []);

  // Только персоны этого проекта (глобальные живут в хабе «Агенты»)
  const projectPersonas = personas.filter(p => p.scope === 'project' && p.projectId === project.id);

  return (
    <div style={{ display: 'flex', flexDirection: 'column', height: '100%', background: C.bgPanel }}>
      <PersonaList
        personas={projectPersonas}
        selectedId={selectedId}
        onSelect={onSelect}
        onNew={onNew}
      />
    </div>
  );
}

// Центральная зона проектных агентов: тулбар (аватар + подпись + Удалить/Поговорить/Сохранить)
// над широкой двухколоночной формой профиля. personaId=null — создание.
export function ProjectAgentPane({ project, personaId, creating, onOpenChat, onSelectAgent, onCleared, onBack }: {
  project: Project;
  personaId: string | null;
  creating: boolean;
  // Открыть созданный «Поговорить» чат на месте (переключить на «Чаты» и выбрать сессию)
  onOpenChat: (session: Session) => void;
  // Родитель выбирает агента (после создания переключаемся с «создания» на «редактирование»)
  onSelectAgent: (id: string) => void;
  // Сброс выбора/создания (после удаления или «Отмена»)
  onCleared: () => void;
  onBack?: () => void;
}) {
  const personas = usePersonas();
  const persona = personaId ? personas.find(p => p.id === personaId) ?? null : null;
  const isMobile = useIsMobile();

  // Активный вид (для существующей персоны): профиль или долгая память
  const [view, setView] = useState<'profile' | 'memory'>('profile');
  useEffect(() => { setView('profile'); }, [personaId]);

  const [talking, setTalking] = useState(false);
  const formRef = useRef<PersonaFormHandle>(null);
  const [status, setStatus] = useState<PersonaFormStatus>({ canSave: false, saving: false, dirty: false });
  // Стабильный колбэк + защита от лишних апдейтов (иначе цикл ре-рендеров формы)
  const onStatus = useCallback((s: PersonaFormStatus) => {
    setStatus(prev => (prev.canSave === s.canSave && prev.saving === s.saving && prev.dirty === s.dirty ? prev : s));
  }, []);
  // Живой цвет из формы — мгновенная перекраска акцентной полосы/тулбара
  const [liveColor, setLiveColor] = useState<string | undefined>(undefined);

  const onDelete = async (p: Persona) => {
    if (!window.confirm(`Удалить агента «${personaTitleLines(p).primary}»?`)) return;
    try {
      await api.personas.remove(p.id);
      bumpPersonas();
      onCleared();
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

  const accent = AGENT_COLORS[liveColor ?? persona?.avatar?.color ?? ''] ?? C.accent;

  return (
    <div style={{ height: '100%', display: 'flex', flexDirection: 'column', overflow: 'hidden' }}>
      {persona ? (
        <PersonaToolbar
          mode="edit"
          persona={persona}
          accent={accent}
          zoneLabel={`Проект · ${project.name}`}
          view={view}
          onView={setView}
          status={status}
          talking={talking}
          onTalk={() => talk(persona)}
          onDelete={() => onDelete(persona)}
          onSave={() => void formRef.current?.save()}
          onBack={onBack}
          isMobile={isMobile}
        />
      ) : (
        <PersonaToolbar
          mode="create"
          accent={accent}
          status={status}
          onSave={() => void formRef.current?.save()}
          onCancel={onCleared}
          onBack={onBack}
          isMobile={isMobile}
        />
      )}
      {/* Тонкая акцентная полоса персоны */}
      <div style={{ flex: 'none', height: 2, background: `${accent}55` }} />
      <div style={{ flex: 1, minHeight: 0 }}>
        {persona ? (
          view === 'memory' ? (
            <PersonaMemoryPanel persona={persona} isMobile={isMobile} embedded />
          ) : (
            <PersonaForm
              ref={formRef}
              key={persona.id}
              persona={persona}
              projects={[project]}
              onStatus={onStatus}
              onColorChange={setLiveColor}
              onOpenMemory={() => setView('memory')}
              onSaved={() => {}}
              onDelete={() => onDelete(persona)}
            />
          )
        ) : creating ? (
          <PersonaForm
            ref={formRef}
            persona={null}
            projects={[project]}
            defaultScope="project"
            defaultProjectId={project.id}
            onStatus={onStatus}
            onColorChange={setLiveColor}
            onSaved={p => onSelectAgent(p.id)}
          />
        ) : null}
      </div>
    </div>
  );
}

// Пустое состояние центральной зоны, когда агент не выбран
export function ProjectAgentEmpty({ hasAgents, onNew }: { hasAgents: boolean; onNew: () => void }) {
  return (
    <div style={{ height: '100%', display: 'flex', flexDirection: 'column', alignItems: 'center', justifyContent: 'center', gap: 14, padding: 24, textAlign: 'center' }}>
      <div style={{ color: C.textMuted, fontSize: 14, fontFamily: FONT.sans, maxWidth: 320, lineHeight: 1.5 }}>
        {hasAgents ? 'Выберите агента слева или создайте нового' : 'В этом проекте пока нет агентов. Создайте первого — задайте роль, характер и аватар.'}
      </div>
      <button onClick={onNew} style={btnPrimary}>Новый агент</button>
    </div>
  );
}

const btnPrimary: React.CSSProperties = {
  flexShrink: 0, background: C.accent, color: C.onAccent, border: 'none', borderRadius: R.lg,
  padding: '8px 17px', fontSize: 13, fontWeight: 600, cursor: 'pointer', fontFamily: FONT.sans,
};
