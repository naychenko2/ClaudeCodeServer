import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import type { AuthState, Persona, Project, Session } from '../../types';
import type { HubTab } from '../../components/HubTabs';
import { HubHeader } from '../../components/HubHeader';
import { EmptyState } from '../../components/EmptyState';
import { C, FONT, R } from '../../lib/design';
import { AGENT_COLORS } from '../../components/AgentSelector';
import { api } from '../../lib/api';
import { usePersonas, ensurePersonasLoaded, bumpPersonas, personaLabel } from '../../lib/personas';
import { navPush, navReplace, getNav, parseHash, type NavSnapshot } from '../../lib/nav';
import { PersonaList } from './PersonaList';
import { PersonaForm, type PersonaFormHandle, type PersonaFormStatus } from './PersonaForm';
import { PersonaToolbar, type PersonaView } from './PersonaToolbar';
import { PersonaPreview } from './PersonaPreview';
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

// Иконка раздела для пустого состояния (персона)
function IconPersonas() {
  return (
    <svg width="26" height="26" viewBox="0 0 24 24" fill="none" stroke="currentColor"
      strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
      <circle cx="12" cy="8" r="4" />
      <path d="M4 20c0-4 3.6-6 8-6s8 2 8 6" />
    </svg>
  );
}

export function PersonasPage({ auth, onLogout, onHubTab }: {
  auth: AuthState;
  onLogout: () => void;
  onHubTab: (t: HubTab) => void;
}) {
  const isMobile = useIsMobile();
  // Глобальный раздел показывает только глобальные персоны — проектные живут в своих проектах
  const allPersonas = usePersonas();
  const personas = useMemo(() => allPersonas.filter(p => p.scope === 'global'), [allPersonas]);
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [mobileView, setMobileView] = useState<'list' | 'card'>('list');
  // Режим создания новой персоны: пустая форма прямо в контентной зоне
  const [creating, setCreating] = useState(false);
  // Проекты — чтобы показать имя/зону проектной персоны и открыть её проект в «Поговорить»
  const [projects, setProjects] = useState<Project[]>([]);
  // Идёт создание чата по кнопке «Поговорить»
  const [talking, setTalking] = useState(false);

  useEffect(() => { void ensurePersonasLoaded(); }, []);
  useEffect(() => { api.projects.list().then(setProjects).catch(() => {}); }, []);

  // Диплинк #/personas/{id} (старый #/agents/{id} парсится как алиас)
  useEffect(() => {
    const t = parseHash();
    if (t?.screen === 'personas' && t.personaId) { setSelectedId(t.personaId); setMobileView('card'); }
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

  const selectPersona = (id: string) => {
    setCreating(false);
    setSelectedId(id); setMobileView('card');
    navPush({ screen: 'personas', persona: id });
  };
  const clearSelection = () => {
    setCreating(false);
    setSelectedId(null); setMobileView('list');
    if (getNav()?.persona) navReplace({ screen: 'personas' });
  };
  // Кнопка «Новая персона» — пустая форма создания в контентной зоне
  const startCreate = () => {
    setSelectedId(null); setCreating(true); setMobileView('card');
    if (getNav()?.persona) navReplace({ screen: 'personas' });
  };

  const onDelete = async (p: Persona) => {
    if (!window.confirm(`Удалить персону «${personaLabel(p)}»?`)) return;
    try {
      await api.personas.remove(p.id);
      bumpPersonas();
      clearSelection();
    } catch {
      alert('Не удалось удалить персону.');
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
        if (!proj) { alert('Проект персоны недоступен.'); return; }
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
      alert(e instanceof Error ? e.message : 'Не удалось создать чат');
    } finally {
      setTalking(false);
    }
  };

  // Открыть СУЩЕСТВУЮЩИЙ чат персоны (из «Недавних разговоров» обзора) —
  // та же навигация, что у talk(), но без создания новой сессии.
  const openSession = (session: Session) => {
    if (session.projectId) {
      const proj = projects.find(x => x.id === session.projectId);
      if (!proj) { alert('Проект чата недоступен.'); return; }
      sessionStorage.setItem('cc_pending_session', JSON.stringify(session));
      window.dispatchEvent(new CustomEvent('cc-open-session', { detail: { project: proj } }));
    } else {
      localStorage.setItem('cc_open_chat', session.id);
      onHubTab('chats');
    }
  };

  const sidebar = (
    <PersonaList personas={personas} selectedId={selectedId} onSelect={selectPersona} onNew={startCreate} />
  );

  const centerPane = creating
    ? <PersonaCreatePane
        projects={projects}
        onSaved={p => selectPersona(p.id)}
        onCancel={clearSelection}
        onBack={isMobile ? clearSelection : undefined} />
    : selected
    ? <PersonaStudio
        key={selected.id}
        persona={selected}
        projects={projects}
        talking={talking}
        onDelete={() => onDelete(selected)}
        onTalk={() => talk(selected)}
        onOpenSession={openSession}
        onBack={isMobile ? clearSelection : undefined}
        isMobile={isMobile} />
    : <div style={{ height: '100%', display: 'flex', alignItems: 'center', justifyContent: 'center' }}>
        <EmptyState icon={<IconPersonas />} title="Персоны"
          subtitle={personas.length ? 'Выбери персону слева, чтобы открыть её профиль и память' : 'Создай первую персону: имя, характер, аватар'}
          action={<button onClick={startCreate} style={newBtn}>Новая персона</button>} />
      </div>;

  const hasContent = creating || !!selected;

  const body = isMobile ? (
    (mobileView === 'card' && hasContent)
      ? <div style={{ height: '100%', display: 'flex', flexDirection: 'column' }}>{centerPane}</div>
      : <div style={{ height: '100%', display: 'flex', flexDirection: 'column', background: C.bgPanel }}>{sidebar}</div>
  ) : (
    <div style={{ height: '100%', display: 'flex' }}>
      <div style={{ width: 280, flex: 'none', background: C.bgPanel, borderRight: `1px solid ${C.border}`, display: 'flex', flexDirection: 'column' }}>
        {sidebar}
      </div>
      <div style={{ flex: 1, minWidth: 0 }}>{centerPane}</div>
    </div>
  );

  return (
    <div style={{ height: '100dvh', background: C.bgMain, fontFamily: FONT.sans, display: 'flex', flexDirection: 'column', overflow: 'hidden' }}>
      <HubHeader value="personas" onTab={onHubTab} auth={auth} onLogout={onLogout} />
      <div style={{ flex: 1, minHeight: 0 }}>{body}</div>
    </div>
  );
}

// Пане создания новой персоны. Первый экран — быстрое создание по промпту
// (ИИ придумывает роль/характер/аватар); «Заполнить вручную» переключает на
// привычную пустую PersonaForm с тулбаром (Отмена/Создать).
function PersonaCreatePane({ projects, onSaved, onCancel, onBack }: {
  projects: Project[];
  onSaved: (p: Persona) => void;
  onCancel: () => void;
  onBack?: () => void;
}) {
  const isMobile = useIsMobile();
  // Ручной путь создания (пустая форма) — по кнопке «Заполнить вручную»
  const [manual, setManual] = useState(false);
  // Выбранный шаблон роли — предзаполняет ручную форму
  const [template, setTemplate] = useState<PersonaTemplate | null>(null);
  const formRef = useRef<PersonaFormHandle>(null);
  const [status, setStatus] = useState<PersonaFormStatus>({ canSave: false, saving: false, dirty: false });
  const onStatus = useCallback((s: PersonaFormStatus) => {
    setStatus(prev => (prev.canSave === s.canSave && prev.saving === s.saving && prev.dirty === s.dirty ? prev : s));
  }, []);
  // Живой цвет из формы — для перекраски акцентной полосы тулбара при создании
  const [liveColor, setLiveColor] = useState<string>('orange');
  const accent = AGENT_COLORS[liveColor] ?? C.accent;

  // Стартовый экран — быстрое создание по промпту (глобальная зона)
  if (!manual) {
    return (
      <PersonaQuickCreate
        scope="global"
        onCreated={onSaved}
        onManual={() => { setTemplate(null); setManual(true); }}
        onTemplate={t => { setTemplate(t); setManual(true); }}
        onCancel={onCancel}
        onBack={onBack}
        isMobile={isMobile}
      />
    );
  }

  return (
    <div style={{ height: '100%', display: 'flex', flexDirection: 'column', overflow: 'hidden' }}>
      <PersonaToolbar
        mode="create"
        accent={accent}
        status={status}
        onSave={() => void formRef.current?.save()}
        onCancel={onCancel}
        onBack={onBack}
        isMobile={isMobile}
      />
      <div style={{ flex: 'none', height: 2, background: `${accent}55` }} />
      <div style={{ flex: 1, minHeight: 0 }}>
        <PersonaForm ref={formRef} persona={null} projects={projects}
          initial={template ? { name: template.namePlaceholder, role: template.role, description: template.description, contract: template.contract, greeting: template.greeting, color: template.avatarColor, tools: template.tools, access: template.access, model: template.model, effort: template.effort } : undefined}
          onStatus={onStatus} onColorChange={setLiveColor} onSaved={onSaved} />
      </div>
    </div>
  );
}

// Студия персоны: центральная область = обзор-визитка (дефолт), инлайн-форма
// профиля ИЛИ долгая память. Чата здесь нет — разговор живёт среди обычных
// чатов (кнопка «Поговорить»).
function PersonaStudio({ persona, projects, talking, onDelete, onTalk, onOpenSession, onBack, isMobile }: {
  persona: Persona;
  projects: Project[];
  talking: boolean;
  onDelete: () => void;
  onTalk: () => void;
  onOpenSession: (s: Session) => void;
  onBack?: () => void;
  isMobile: boolean;
}) {
  // Активный вид: обзор (read-only визитка), профиль (форма) или память.
  // Компонент перемонтируется по key={persona.id} — смена персоны сама сбрасывает вид на обзор.
  const [view, setView] = useState<PersonaView>('preview');

  // Императивный доступ к форме профиля + её состояние (для кнопок тулбара)
  const formRef = useRef<PersonaFormHandle>(null);
  const [status, setStatus] = useState<PersonaFormStatus>({ canSave: false, saving: false, dirty: false });
  const onStatus = useCallback((s: PersonaFormStatus) => {
    setStatus(prev => (prev.canSave === s.canSave && prev.saving === s.saving && prev.dirty === s.dirty ? prev : s));
  }, []);

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
    : view === 'profile'
    // Профиль — инлайн-форма настройки прямо в контентной зоне (действия — в тулбаре)
    ? <div style={{ flex: 1, minHeight: 0 }}>
        <PersonaForm ref={formRef} persona={persona} projects={projects} onStatus={onStatus}
          onColorChange={setLiveColor} onOpenMemory={() => setView('memory')}
          onSaved={() => {}} onDelete={() => onDelete()} />
      </div>
    // Обзор — read-only визитка со сводкой и недавними разговорами
    : <div style={{ flex: 1, minHeight: 0 }}>
        <PersonaPreview persona={persona} accent={accent} talking={talking}
          onTalk={onTalk} onOpenSession={onOpenSession}
          onEditProfile={() => setView('profile')} isMobile={isMobile} />
      </div>;

  return (
    <div style={{ height: '100%', display: 'flex', flexDirection: 'column', overflow: 'hidden' }}>
      <PersonaToolbar
        mode="edit"
        persona={persona}
        accent={accent}
        zoneLabel={zoneLabel}
        view={view}
        onView={setView}
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
    </div>
  );
}

const newBtn: React.CSSProperties = {
  background: C.accent, color: C.onAccent, border: 'none', borderRadius: R.md,
  padding: '9px 16px', fontSize: 13, fontWeight: 500, cursor: 'pointer', fontFamily: FONT.sans,
};
