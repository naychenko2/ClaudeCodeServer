import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { ChevronsLeft, Menu, Pin } from 'lucide-react';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import type { AuthState, Persona, Project, Session } from '../../types';
import type { HubTab } from '../../components/HubTabs';
import { HubHeader } from '../../components/HubHeader';
import { C, FONT } from '../../lib/design';
import { AGENT_COLORS } from '../../components/AgentSelector';
import { api } from '../../lib/api';
import { usePersonas, ensurePersonasLoaded, bumpPersonas, personaLabel } from '../../lib/personas';
import { navPush, navReplace, getNav, parseHash, type NavSnapshot } from '../../lib/nav';
import { showToast } from '../../lib/toast';
import { ConfirmDialog, Splitter, IconButton } from '../../components/ui';
import { useSidebarDrag } from '../../lib/sidebarWidth';
import { useIsMobile } from '../../lib/breakpoints';
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
import { PersonasHub } from './PersonasHub';

export function PersonasPage({ auth, onLogout, onHubTab }: {
  auth: AuthState;
  onLogout: () => void;
  onHubTab: (t: HubTab) => void;
}) {
  const isMobile = useIsMobile();
  // Ширина сайдбара — общая со всеми разделами (cc_sidebar_width) + перетаскиваемый сплиттер
  const { width: listWidth, dragging: listDragging, startDrag: startListDrag } = useSidebarDrag();
  // Режим сайдбара: pinned | collapsed | open (как в «Заметках»/«Чатах»/воркспейсе)
  const [sidebarMode, setSidebarMode] = useState<'pinned' | 'collapsed' | 'open'>(() =>
    localStorage.getItem('cc_personas_sidebar_mode') === 'collapsed' ? 'collapsed' : 'pinned');
  useEffect(() => { if (sidebarMode !== 'open') localStorage.setItem('cc_personas_sidebar_mode', sidebarMode); }, [sidebarMode]);
  // Глобальный раздел показывает только глобальные персоны — проектные живут в своих проектах
  const allPersonas = usePersonas();
  const personas = useMemo(() => allPersonas.filter(p => p.scope === 'global'), [allPersonas]);
  const [selectedId, setSelectedId] = useState<string | null>(null);
  // Вкладка студии, на которую нужно сразу открыться (бэйдж автоматизации в чате) —
  // одноразовая, сбрасывается любым обычным выбором персоны из списка
  const [pendingView, setPendingView] = useState<PersonaView | null>(null);
  const [mobileView, setMobileView] = useState<'list' | 'card'>('list');
  // Режим создания новой персоны: мастер прямо в контентной зоне
  const [creating, setCreating] = useState(false);
  // Проекты — чтобы показать имя/зону проектной персоны и открыть её проект в «Поговорить»
  const [projects, setProjects] = useState<Project[]>([]);
  // Идёт создание чата по кнопке «Поговорить»
  const [talking, setTalking] = useState(false);

  useEffect(() => { void ensurePersonasLoaded(); }, []);
  useEffect(() => { api.projects.list().then(setProjects).catch(() => {}); }, []);

  // Диплинк #/personas/{id} (старый #/agents/{id} парсится как алиас) — прямой заход/обновление
  // страницы. Плюс pending-канал cc_pending_persona_id + событие cc-open-persona — навигация
  // изнутри приложения (бэйдж автоматизации в чате глобальной персоны, см. lib/chatOrigin.ts),
  // когда раздел «Персоны» уже смонтирован и hash просто переключился на «#/personas» без id.
  useEffect(() => {
    const consume = () => {
      const pending = sessionStorage.getItem('cc_pending_persona_id');
      if (pending) {
        sessionStorage.removeItem('cc_pending_persona_id');
        const view = sessionStorage.getItem('cc_pending_persona_view');
        sessionStorage.removeItem('cc_pending_persona_view');
        setSelectedId(pending); setMobileView('card');
        setPendingView(view === 'automation' ? 'automation' : null);
        navPush({ screen: 'personas', persona: pending });
        return;
      }
      const t = parseHash();
      if (t?.screen === 'personas' && t.personaId) {
        setSelectedId(t.personaId); setMobileView('card');
        setPendingView(t.personaView === 'automation' ? 'automation' : null);
      }
    };
    consume();
    window.addEventListener('cc-open-persona', consume);
    return () => window.removeEventListener('cc-open-persona', consume);
  }, []);

  // Back/forward браузера внутри раздела «Персоны»
  useEffect(() => {
    const onPop = (e: PopStateEvent) => {
      const s = e.state as NavSnapshot | null;
      if (s?.screen === 'personas') {
        setSelectedId(s.persona ?? null);
        setMobileView(s.persona ? 'card' : 'list');
      }
    };
    window.addEventListener('popstate', onPop);
    return () => window.removeEventListener('popstate', onPop);
  }, []);

  const selected = personas.find(p => p.id === selectedId) ?? null;

  // Диплинк/выбор указывает на проектную персону — в глобальном разделе её нет, сбрасываем выбор
  useEffect(() => {
    if (selectedId && allPersonas.some(p => p.id === selectedId) && !personas.some(p => p.id === selectedId)) {
      setSelectedId(null); setMobileView('list');
      if (getNav()?.persona) navReplace({ screen: 'personas' });
    }
  }, [selectedId, allPersonas, personas]);

  // view — опционально сразу открыть конкретную вкладку студии (бэйдж автоматизации
  // в чате, клик по событию памяти в ленте активности хаба и т.п.)
  const selectPersona = (id: string, view?: PersonaView) => {
    setCreating(false);
    setSelectedId(id); setMobileView('card'); setPendingView(view ?? null);
    navPush({ screen: 'personas', persona: id });
    // В режиме drawer после выбора — закрываем оверлей (как в «Заметках»)
    setSidebarMode(m => m === 'open' ? 'collapsed' : m);
  };
  const clearSelection = () => {
    setCreating(false);
    setSelectedId(null); setMobileView('list'); setPendingView(null);
    if (getNav()?.persona) navReplace({ screen: 'personas' });
  };
  // Кнопка «Новая персона» — мастер создания в контентной зоне
  const startCreate = () => {
    setSelectedId(null); setCreating(true); setMobileView('card');
    if (getNav()?.persona) navReplace({ screen: 'personas' });
  };

  // Удаление в два шага: запрос подтверждения (диалог) → само удаление
  const [deleteTarget, setDeleteTarget] = useState<Persona | null>(null);
  const onDelete = (p: Persona) => setDeleteTarget(p);
  const doDelete = async (p: Persona) => {
    try {
      await api.personas.remove(p.id);
      bumpPersonas();
      clearSelection();
    } catch {
      showToast('Персоны', 'Не удалось удалить персону.');
    } finally {
      setDeleteTarget(null);
    }
  };

  // «Поговорить»: создаём чат от лица персоны и уводим пользователя в раздел разговоров.
  // Глобальная персона → чат вне проекта (таб «Чаты»). Проектная → её проект и стартовая сессия.
  const talk = async (p: Persona) => {
    if (talking) return;
    setTalking(true);
    try {
      const session = await api.personas.createChat(p.id, { mode: 'auto' });
      if (session.projectId) {
        const proj = projects.find(x => x.id === session.projectId);
        if (!proj) { showToast('Персоны', 'Проект персоны недоступен.'); return; }
        // Стартовую сессию отдаём проекту через sessionStorage — её подхватит WorkspacePage
        sessionStorage.setItem('cc_pending_session', JSON.stringify(session));
        window.dispatchEvent(new CustomEvent('cc-open-session', { detail: { project: proj } }));
      } else {
        // Глобальная персона: её чат живёт среди обычных чатов. Активный чат ChatsPage
        // читает из localStorage (ключ cc_open_chat) при монтировании.
        localStorage.setItem('cc_open_chat', session.id);
        onHubTab('chats');
      }
    } catch (e) {
      showToast('Персоны', e instanceof Error ? e.message : 'Не удалось создать чат');
    } finally {
      setTalking(false);
    }
  };

  // Открыть СУЩЕСТВУЮЩИЙ чат персоны (из «Недавних разговоров» обзора) —
  // та же навигация, что у talk(), но без создания новой сессии.
  const openSession = (session: Session) => {
    if (session.projectId) {
      const proj = projects.find(x => x.id === session.projectId);
      if (!proj) { showToast('Персоны', 'Проект чата недоступен.'); return; }
      sessionStorage.setItem('cc_pending_session', JSON.stringify(session));
      window.dispatchEvent(new CustomEvent('cc-open-session', { detail: { project: proj } }));
    } else {
      localStorage.setItem('cc_open_chat', session.id);
      onHubTab('chats');
    }
  };

  // Строка управления панелью (только десктоп): свернуть (◀◀) + «Закрепить» (📌) в режиме drawer
  const sidebarHeader = (
    <div style={{ display: 'flex', alignItems: 'center', gap: 6, padding: '8px 10px 0', minHeight: 28, flex: 'none' }}>
      <IconButton onClick={() => setSidebarMode('collapsed')} title="Свернуть панель" size="sm" style={{ marginLeft: -2 }}>
        <ChevronsLeft size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} style={{ flexShrink: 0 }} />
      </IconButton>
      <span style={{ flex: 1, minWidth: 0, fontSize: 14, fontWeight: 600, color: C.textHeading, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>Персоны</span>
      {sidebarMode === 'open' && (
        <IconButton onClick={() => setSidebarMode('pinned')} title="Закрепить панель" size="sm">
          <Pin size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} style={{ flexShrink: 0 }} />
        </IconButton>
      )}
    </div>
  );

  const sidebar = (
    <>
      {!isMobile && sidebarHeader}
      <PersonaList personas={personas} selectedId={selectedId} onSelect={selectPersona} onNew={startCreate} />
    </>
  );

  const centerPane = creating
    ? <PersonaCreatePane
        projects={projects}
        onOpenStudio={p => selectPersona(p.id)}
        onStartChat={p => void talk(p)}
        onCancel={clearSelection}
        onBack={isMobile ? clearSelection : undefined} />
    : selected
    ? <PersonaStudio
        key={selected.id}
        persona={selected}
        projects={projects}
        talking={talking}
        initialView={pendingView}
        onDelete={() => onDelete(selected)}
        onTalk={() => talk(selected)}
        onOpenSession={openSession}
        onBack={isMobile ? clearSelection : undefined}
        isMobile={isMobile} />
    : <PersonasHub
        personas={personas}
        talking={talking}
        onTalk={talk}
        onOpenSession={openSession}
        onNew={startCreate}
        onOpenPersonaView={selectPersona} />;

  const hasContent = creating || !!selected;

  const body = isMobile ? (
    (mobileView === 'card' && hasContent)
      ? <div style={{ height: '100%', display: 'flex', flexDirection: 'column' }}>{centerPane}</div>
      : <div style={{ height: '100%', display: 'flex', flexDirection: 'column', background: C.bgPanel }}>{sidebar}</div>
  ) : (
    <div style={{ height: '100%', display: 'flex', position: 'relative' }}>
      {/* Pinned: в потоке + перетаскиваемый сплиттер (ширина общая со всеми разделами) */}
      {sidebarMode === 'pinned' && (
        <>
          <div style={{ width: listWidth, flex: 'none', background: C.bgPanel, display: 'flex', flexDirection: 'column', minHeight: 0 }}>
            {sidebar}
          </div>
          <Splitter active={listDragging} onMouseDown={startListDrag} />
        </>
      )}

      {/* Collapsed/Open: drawer поверх контента */}
      {sidebarMode !== 'pinned' && (
        <div style={{
          position: 'absolute', top: 0, left: 0, bottom: 0, zIndex: 10, width: Math.min(listWidth, 320),
          background: C.bgPanel, borderRight: `1px solid ${C.border}`, display: 'flex', flexDirection: 'column',
          transform: sidebarMode === 'open' ? 'translateX(0)' : 'translateX(-110%)',
          transition: 'transform 0.3s cubic-bezier(0.4, 0, 0.2, 1)',
          boxShadow: sidebarMode === 'open' ? '4px 0 20px rgba(20,16,10,0.15)' : 'none',
        }}>
          {sidebar}
        </div>
      )}

      {/* Backdrop — только когда drawer открыт */}
      {sidebarMode === 'open' && (
        <div onClick={() => setSidebarMode('collapsed')} style={{ position: 'absolute', inset: 0, zIndex: 9, background: C.overlay }} />
      )}

      {/* Центр: в свёрнутом режиме — тонкая шапка с гамбургером «открыть панель» */}
      <div style={{ flex: 1, minWidth: 0, display: 'flex', flexDirection: 'column' }}>
        {sidebarMode === 'collapsed' && (
          <div style={{ flex: 'none', display: 'flex', alignItems: 'center', padding: '0 8px', height: 48, borderBottom: `1px solid ${C.divider}` }}>
            <IconButton onClick={() => setSidebarMode('open')} title="Открыть панель" size="md" variant="soft">
              <Menu size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} style={{ flexShrink: 0 }} />
            </IconButton>
          </div>
        )}
        <div style={{ flex: 1, minWidth: 0, minHeight: 0 }}>{centerPane}</div>
      </div>
    </div>
  );

  return (
    <div style={{ height: '100dvh', background: C.bgMain, fontFamily: FONT.sans, display: 'flex', flexDirection: 'column', overflow: 'hidden' }}>
      <HubHeader value="personas" onTab={onHubTab} auth={auth} onLogout={onLogout} />
      <div style={{ flex: 1, minHeight: 0 }}>{body}</div>
      {deleteTarget && (
        <ConfirmDialog
          title="Удалить персону?"
          subtitle={<>Персона «<strong style={{ color: C.textPrimary, fontWeight: 600 }}>{personaLabel(deleteTarget)}</strong>» будет удалена без возможности восстановления.</>}
          confirmLabel="Удалить"
          confirmVariant="danger"
          onConfirm={() => doDelete(deleteTarget)}
          onCancel={() => setDeleteTarget(null)}
        />
      )}
    </div>
  );
}

// Панель создания новой персоны — пошаговый мастер (единая точка входа:
// по описанию / из шаблона / с нуля).
function PersonaCreatePane({ projects, onOpenStudio, onStartChat, onCancel, onBack }: {
  projects: Project[];
  onOpenStudio: (p: Persona) => void;
  onStartChat: (p: Persona) => void;
  onCancel: () => void;
  onBack?: () => void;
}) {
  const isMobile = useIsMobile();
  return (
    <PersonaWizard
      scope="global"
      projects={projects}
      onOpenStudio={onOpenStudio}
      onStartChat={onStartChat}
      onCancel={onCancel}
      onBack={onBack}
      isMobile={isMobile}
    />
  );
}

// Студия персоны: центральная область = обзор-визитка (дефолт), инлайн-форма
// профиля ИЛИ долгая память. Чата здесь нет — разговор живёт среди обычных
// чатов (кнопка «Поговорить»).
function PersonaStudio({ persona, projects, talking, initialView, onDelete, onTalk, onOpenSession, onBack, isMobile }: {
  persona: Persona;
  projects: Project[];
  talking: boolean;
  // Вкладка, на которую нужно сразу открыться (бэйдж автоматизации в чате) — только при монтировании
  initialView?: PersonaView | null;
  onDelete: () => void;
  onTalk: () => void;
  onOpenSession: (s: Session) => void;
  onBack?: () => void;
  isMobile: boolean;
}) {
  // Активный вид: профиль (визитка/форма), умения, память или задачи.
  // Компонент перемонтируется по key={persona.id} — смена персоны сама сбрасывает вид на профиль.
  const [view, setView] = useState<PersonaView>(initialView ?? 'preview');
  // Развёрнута ли форма правки профиля (внутри вида «Профиль»). key={persona.id}
  // перемонтирует компонент, так что смена персоны сбрасывает editing сама.
  const [editing, setEditing] = useState(false);
  // Подтверждение отмены несохранённых изменений — через ConfirmDialog вместо window.confirm
  const [confirmDiscard, setConfirmDiscard] = useState<null | (() => void)>(null);

  // Императивный доступ к форме профиля + её состояние (для кнопок тулбара)
  const formRef = useRef<PersonaFormHandle>(null);
  const [status, setStatus] = useState<PersonaFormStatus>({ canSave: false, saving: false, dirty: false });
  const onStatus = useCallback((s: PersonaFormStatus) => {
    setStatus(prev => (prev.canSave === s.canSave && prev.saving === s.saving && prev.dirty === s.dirty ? prev : s));
  }, []);

  // Навигация между вкладками: если правим и есть несохранённое — сначала спросить
  const goView = (v: PersonaView) => {
    if (editing && status.dirty) { setConfirmDiscard(() => () => { setEditing(false); setView(v); }); return; }
    setEditing(false);
    setView(v);
  };

  const isProjectScope = persona.scope === 'project';
  const zoneName = isProjectScope
    ? (projects.find(p => p.id === persona.projectId)?.name ?? persona.projectId ?? 'Проект')
    : null;
  const zoneLabel = isProjectScope ? `Проект · ${zoneName}` : 'Глобальный';

  // Живой цвет из формы (перекрашивает акцент мгновенно) с фолбэком на сохранённый
  const [liveColor, setLiveColor] = useState<string | undefined>(undefined);
  const accent = AGENT_COLORS[liveColor ?? persona.avatar?.color ?? ''] ?? C.accent;

  const content = view === 'memory'
    // Память — под тулбаром, свой заголовок не нужен (идентичность уже в тулбаре)
    ? <div style={{ flex: 1, minHeight: 0 }}><PersonaMemoryPanel persona={persona} isMobile={isMobile} embedded /></div>
    : view === 'tasks'
    // Задачи — отфильтрованный вид реальных задач, где персона исполнитель
    ? <div style={{ flex: 1, minHeight: 0 }}><PersonaTasksPanel persona={persona} isMobile={isMobile} /></div>
    : view === 'knowledge'
    // Знания — привязки источников и правил (фича persona-bindings)
    ? <div style={{ flex: 1, minHeight: 0 }}>
        <PersonaBindingsPanel persona={persona} accent={accent} isMobile={isMobile} />
      </div>
    : view === 'automation'
    // Проактивность — правила «событие → действие» (событийно-управляемая автоматизация)
    ? <div style={{ flex: 1, minHeight: 0 }}>
        <PersonaAutomationPanel persona={persona} projects={projects} accent={accent} isMobile={isMobile} />
      </div>
    : editing
    // Профиль в режиме правки — инлайн-форма (действия — в тулбаре); успешное
    // сохранение возвращает к визитке
    ? <div style={{ flex: 1, minHeight: 0 }}>
        <PersonaForm ref={formRef} persona={persona} projects={projects} onStatus={onStatus}
          onColorChange={setLiveColor} onOpenMemory={() => goView('memory')}
          onOpenKnowledge={() => goView('knowledge')}
          onSaved={() => setEditing(false)} onDelete={() => onDelete()} />
      </div>
    // Профиль — read-only визитка со сводкой и недавними разговорами
    : <div style={{ flex: 1, minHeight: 0 }}>
        <PersonaPreview persona={persona} accent={accent} talking={talking}
          onTalk={onTalk} onOpenSession={onOpenSession}
          onEditProfile={() => setEditing(true)}
          onOpenKnowledge={() => goView('knowledge')}
          onOpenTasks={() => goView('tasks')}
          onOpenAutomation={() => goView('automation')}
          onOpenMemory={() => goView('memory')} isMobile={isMobile} />
      </div>;

  return (
    <div style={{ height: '100%', display: 'flex', flexDirection: 'column', overflow: 'hidden' }}>
      <PersonaToolbar
        mode="edit"
        persona={persona}
        accent={accent}
        zoneLabel={zoneLabel}
        view={view}
        onView={goView}
        editing={editing}
        onEdit={() => setEditing(true)}
        onCancelEdit={() => { if (status.dirty) setConfirmDiscard(() => () => setEditing(false)); else setEditing(false); }}
        status={status}
        talking={talking}
        onTalk={onTalk}
        onDelete={onDelete}
        onSave={() => void formRef.current?.save()}
        onBack={onBack}
        isMobile={isMobile}
      />
      {/* Тонкая акцентная полоса персоны */}
      <div style={{ flex: 'none', height: 2, background: `${accent}55` }} />
      {content}

      {/* Плавающая «Редактировать» на мобиле — вместо карандаша в тулбаре */}
      {isMobile && view === 'preview' && !editing && (
        <PersonaEditFab accent={accent} onClick={() => setEditing(true)} />
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
