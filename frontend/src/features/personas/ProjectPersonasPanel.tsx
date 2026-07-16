import { useCallback, useEffect, useRef, useState } from 'react';
import { Users } from 'lucide-react';
import type { Persona, Project, Session } from '../../types';
import { api } from '../../lib/api';
import { usePersonas, ensurePersonasLoaded, bumpPersonas, personaTitleLines } from '../../lib/personas';
import { AGENT_COLORS } from '../../components/AgentSelector';
import { C, FONT, R } from '../../lib/design';
import { showToast } from '../../lib/toast';
import { ConfirmDialog } from '../../components/ui';
import { PersonaList } from './PersonaList';
import { PersonaForm, type PersonaFormHandle, type PersonaFormStatus } from './PersonaForm';
import { PersonaToolbar, type PersonaView } from './PersonaToolbar';
import { PersonaEditFab } from './PersonaEditFab';
import { PersonaPreview } from './PersonaPreview';
import { PersonaMemoryPanel } from './PersonaMemoryPanel';
import { PersonaBindingsPanel } from './PersonaBindingsPanel';
import { PersonaTasksPanel } from './PersonaTasksPanel';
import { PersonaAutomationPanel } from './PersonaAutomationPanel';
import { PersonaWizard } from './PersonaWizard';
import { useIsMobile } from '../../lib/breakpoints';

// Проектная вкладка «Команда»: САЙДБАРНЫЙ СПИСОК персон этого проекта.
// Форма редактирования/создания живёт отдельно — в центральной зоне (ProjectPersonaPane).
// Выбор синхронизируется через контролируемые props (состояние поднято в WorkspacePage).
export function ProjectPersonasPanel({ project, selectedId, onSelect, onNew, onShowTeam, teamActive }: {
  project: Project;
  selectedId: string | null;
  onSelect: (id: string) => void;
  onNew: () => void;
  // Показать командный центр (сбросить выбор персоны) — кнопка вверху сайдбара, всегда достижима
  onShowTeam?: () => void;
  teamActive?: boolean;
}) {
  const personas = usePersonas();
  useEffect(() => { void ensurePersonasLoaded(); }, []);

  // Только персоны этого проекта (глобальные живут в хабе «Персоны»)
  const projectPersonas = personas.filter(p => p.scope === 'project' && p.projectId === project.id);

  return (
    <div style={{ display: 'flex', flexDirection: 'column', height: '100%', background: C.bgPanel }}>
      {onShowTeam && (
        <button
          onClick={onShowTeam}
          style={{
            display: 'flex', alignItems: 'center', gap: 9, width: '100%',
            padding: '9px 14px', border: 'none', cursor: 'pointer', textAlign: 'left',
            background: teamActive ? C.accentLight : 'transparent',
            color: teamActive ? C.accent : C.textSecondary,
            fontFamily: FONT.sans, fontSize: 13, fontWeight: 600,
            borderBottom: `1px solid ${C.border}`,
          }}
        >
          <Users size={15} strokeWidth={2} />
          Командный центр
        </button>
      )}
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
export function ProjectPersonaPane({ project, personaId, creating, initialView, onOpenChat, onSelectPersona, onCleared, onBack }: {
  project: Project;
  personaId: string | null;
  creating: boolean;
  // Вкладка, на которую нужно сразу открыться (бэйдж автоматизации в чате)
  initialView?: PersonaView | null;
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

  // Активный вид (для существующей персоны): профиль (дефолт), умения, память, задачи.
  // Смена персоны в списке сбрасывает вид обратно на профиль (или на initialView, если задан).
  const [view, setView] = useState<PersonaView>(initialView ?? 'preview');
  // Развёрнута ли форма правки профиля (внутри вида «Профиль»)
  const [editing, setEditing] = useState(false);
  useEffect(() => { setView(initialView ?? 'preview'); setEditing(false); }, [personaId, initialView]);

  const [talking, setTalking] = useState(false);
  const formRef = useRef<PersonaFormHandle>(null);
  const [status, setStatus] = useState<PersonaFormStatus>({ canSave: false, saving: false, dirty: false });
  // Стабильный колбэк + защита от лишних апдейтов (иначе цикл ре-рендеров формы)
  const onStatus = useCallback((s: PersonaFormStatus) => {
    setStatus(prev => (prev.canSave === s.canSave && prev.saving === s.saving && prev.dirty === s.dirty ? prev : s));
  }, []);
  // Навигация между вкладками: если правим и есть несохранённое — сначала спросить
  const goView = (v: PersonaView) => {
    if (editing && status.dirty) { setConfirmDiscard(() => () => { setEditing(false); setView(v); }); return; }
    setEditing(false);
    setView(v);
  };
  // Живой цвет из формы — мгновенная перекраска акцентной полосы/тулбара
  const [liveColor, setLiveColor] = useState<string | undefined>(undefined);
  // Подтверждение отмены несохранённых изменений — через ConfirmDialog вместо window.confirm
  const [confirmDiscard, setConfirmDiscard] = useState<null | (() => void)>(null);

  // Удаление в два шага: запрос подтверждения (диалог) → само удаление
  const [deleteTarget, setDeleteTarget] = useState<Persona | null>(null);
  const onDelete = (p: Persona) => setDeleteTarget(p);
  const doDelete = async (p: Persona) => {
    try {
      await api.personas.remove(p.id);
      bumpPersonas();
      onCleared();
    } catch {
      showToast('Персоны', 'Не удалось удалить персону.');
    } finally {
      setDeleteTarget(null);
    }
  };

  // «Поговорить»: чат от лица персоны — сессия создаётся в этом проекте
  // (projectId кладёт в проект и чат глобальной персоны, позванной из «Команды»).
  // Мы уже внутри нужного проекта, поэтому открываем её напрямую (без cc_pending_session).
  const talk = async (p: Persona) => {
    if (talking) return;
    setTalking(true);
    try {
      const session = await api.personas.createChat(p.id, { mode: 'auto', projectId: project.id });
      onOpenChat(session);
    } catch (e) {
      showToast('Персоны', e instanceof Error ? e.message : 'Не удалось создать чат');
    } finally {
      setTalking(false);
    }
  };

  const accent = AGENT_COLORS[liveColor ?? persona?.avatar?.color ?? ''] ?? C.accent;

  // Создание — пошаговый мастер целиком (тулбар внутри компонента).
  // После успеха родитель выбирает созданную персону → откроется её редактор.
  if (!persona && creating) {
    return (
      <PersonaWizard
        scope="project"
        projectId={project.id}
        projects={[project]}
        onOpenStudio={p => onSelectPersona(p.id)}
        onStartChat={talk}
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
          onView={goView}
          editing={editing}
          onEdit={() => setEditing(true)}
          onCancelEdit={() => { if (status.dirty) setConfirmDiscard(() => () => setEditing(false)); else setEditing(false); }}
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
          ) : view === 'tasks' ? (
            <PersonaTasksPanel persona={persona} isMobile={isMobile} />
          ) : view === 'knowledge' ? (
            // Знания — привязки источников и правил (фича persona-bindings)
            <PersonaBindingsPanel persona={persona} accent={accent} isMobile={isMobile} />
          ) : view === 'automation' ? (
            // Проактивность — правила «событие → действие» (событийно-управляемая автоматизация)
            <PersonaAutomationPanel persona={persona} projects={[project]} accent={accent} isMobile={isMobile} />
          ) : editing ? (
            // Профиль в режиме правки — форма; успешное сохранение возвращает к визитке
            <PersonaForm
              ref={formRef}
              key={persona.id}
              persona={persona}
              projects={[project]}
              onStatus={onStatus}
              onColorChange={setLiveColor}
              onOpenMemory={() => goView('memory')}
              onOpenKnowledge={() => goView('knowledge')}
              onSaved={() => setEditing(false)}
              onDelete={() => onDelete(persona)}
            />
          ) : (
            // Профиль — read-only визитка; чаты проектной персоны открываются на месте
            <PersonaPreview
              persona={persona}
              accent={accent}
              talking={talking}
              onTalk={() => talk(persona)}
              onOpenSession={onOpenChat}
              onEditProfile={() => setEditing(true)}
              onOpenKnowledge={() => goView('knowledge')}
              onOpenTasks={() => goView('tasks')}
              onOpenAutomation={() => goView('automation')}
              onOpenMemory={() => goView('memory')}
              isMobile={isMobile}
            />
          )
        ) : null}
      </div>
      {/* Плавающая «Редактировать» на мобиле — вместо карандаша в тулбаре */}
      {isMobile && persona && view === 'preview' && !editing && (
        <PersonaEditFab accent={accent} onClick={() => setEditing(true)} />
      )}
      {deleteTarget && (
        <ConfirmDialog
          title="Удалить персону?"
          subtitle={<>Персона «<strong style={{ color: C.textPrimary, fontWeight: 600 }}>{personaTitleLines(deleteTarget).primary}</strong>» будет удалена без возможности восстановления.</>}
          confirmLabel="Удалить"
          confirmVariant="danger"
          onConfirm={() => doDelete(deleteTarget)}
          onCancel={() => setDeleteTarget(null)}
        />
      )}
      {confirmDiscard && (
        <ConfirmDialog
          title="Отменить изменения?"
          subtitle="Несохранённые изменения профиля будут потеряны."
          confirmLabel="Отменить изменения"
          confirmVariant="danger"
          onConfirm={() => { const act = confirmDiscard; setConfirmDiscard(null); act?.(); }}
          onCancel={() => setConfirmDiscard(null)}
        />
      )}
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
