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
import { PersonaQuickCreate } from './PersonaQuickCreate';
import type { PersonaTemplate } from './personaTemplates';

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

// Проектная вкладка «Команда»: САЙДБАРНЫЙ СПИСОК персон этого проекта.
// Форма редактирования/создания живёт отдельно — в центральной зоне (ProjectPersonaPane).
// Выбор синхронизируется через контролируемые props (состояние поднято в WorkspacePage).
export function ProjectPersonasPanel({ project, selectedId, onSelect, onNew }: {
  project: Project;
  selectedId: string | null;
  onSelect: (id: string) => void;
  onNew: () => void;
}) {
  const personas = usePersonas();
  useEffect(() => { void ensurePersonasLoaded(); }, []);

  // Только персоны этого проекта (глобальные живут в хабе «Персоны»)
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

// Центральная зона проектных персон: тулбар (аватар + подпись + Удалить/Поговорить/Сохранить)
// над широкой двухколоночной формой профиля. personaId=null — создание.
export function ProjectPersonaPane({ project, personaId, creating, onOpenChat, onSelectPersona, onCleared, onBack }: {
  project: Project;
  personaId: string | null;
  creating: boolean;
  // Открыть созданный «Поговорить» чат на месте (переключить на «Чаты» и выбрать сессию)
  onOpenChat: (session: Session) => void;
  // Родитель выбирает персону (после создания переключаемся с «создания» на «редактирование»)
  onSelectPersona: (id: string) => void;
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

  // Создание: сначала экран быстрого создания по промпту, «Заполнить вручную» — пустая форма.
  // Сбрасываем на быстрый экран при каждом новом входе в режим создания.
  const [manualCreate, setManualCreate] = useState(false);
  // Выбранный шаблон роли — предзаполняет ручную форму
  const [template, setTemplate] = useState<PersonaTemplate | null>(null);
  useEffect(() => { if (creating) { setManualCreate(false); setTemplate(null); } }, [creating]);

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
    if (!window.confirm(`Удалить персону «${personaTitleLines(p).primary}»?`)) return;
    try {
      await api.personas.remove(p.id);
      bumpPersonas();
      onCleared();
    } catch {
      alert('Не удалось удалить персону.');
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

  // Быстрое создание по промпту — свой экран целиком (тулбар внутри компонента).
  // После успеха родитель выбирает созданную персону → откроется её редактор.
  if (!persona && creating && !manualCreate) {
    return (
      <PersonaQuickCreate
        scope="project"
        projectId={project.id}
        onCreated={p => onSelectPersona(p.id)}
        onManual={() => { setTemplate(null); setManualCreate(true); }}
        onTemplate={t => { setTemplate(t); setManualCreate(true); }}
        onCancel={onCleared}
        onBack={onBack}
        isMobile={isMobile}
      />
    );
  }

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
            initial={template ? { role: template.role, description: template.description, systemPrompt: template.systemPrompt, greeting: template.greeting, color: template.avatarColor, tools: template.tools } : undefined}
            onStatus={onStatus}
            onColorChange={setLiveColor}
            onSaved={p => onSelectPersona(p.id)}
          />
        ) : null}
      </div>
    </div>
  );
}

// Пустое состояние центральной зоны, когда персона не выбрана
export function ProjectPersonaEmpty({ hasPersonas, onNew }: { hasPersonas: boolean; onNew: () => void }) {
  return (
    <div style={{ height: '100%', display: 'flex', flexDirection: 'column', alignItems: 'center', justifyContent: 'center', gap: 14, padding: 24, textAlign: 'center' }}>
      <div style={{ color: C.textMuted, fontSize: 14, fontFamily: FONT.sans, maxWidth: 320, lineHeight: 1.5 }}>
        {hasPersonas ? 'Выберите персону слева или создайте новую' : 'В этом проекте пока нет персон. Создайте первую — задайте роль, характер и аватар.'}
      </div>
      <button onClick={onNew} style={btnPrimary}>Новая персона</button>
    </div>
  );
}

const btnPrimary: React.CSSProperties = {
  flexShrink: 0, background: C.accent, color: C.onAccent, border: 'none', borderRadius: R.lg,
  padding: '8px 17px', fontSize: 13, fontWeight: 600, cursor: 'pointer', fontFamily: FONT.sans,
};
