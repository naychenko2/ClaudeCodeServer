import { useEffect, useState } from 'react';
import type { AuthState, Persona, Project } from '../../types';
import type { HubTab } from '../../components/HubTabs';
import { HubHeader } from '../../components/HubHeader';
import { EmptyState } from '../../components/EmptyState';
import { C, FONT, R, SHADOW } from '../../lib/design';
import { AGENT_COLORS } from '../../components/AgentSelector';
import { api } from '../../lib/api';
import { usePersonas, ensurePersonasLoaded, bumpPersonas, personaLabel, personaTitleLines } from '../../lib/personas';
import { navPush, navReplace, getNav, parseHash, type NavSnapshot } from '../../lib/nav';
import { PersonaList } from './PersonaList';
import { PersonaForm } from './PersonaForm';
import { PersonaAvatar } from './PersonaAvatar';
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

// Иконка раздела для пустого состояния (персона/агент)
function IconAgents() {
  return (
    <svg width="26" height="26" viewBox="0 0 24 24" fill="none" stroke="currentColor"
      strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
      <circle cx="12" cy="8" r="4" />
      <path d="M4 20c0-4 3.6-6 8-6s8 2 8 6" />
    </svg>
  );
}

export function AgentsPage({ auth, onLogout, onHubTab }: {
  auth: AuthState;
  onLogout: () => void;
  onHubTab: (t: HubTab) => void;
}) {
  const isMobile = useIsMobile();
  const personas = usePersonas();
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [mobileView, setMobileView] = useState<'list' | 'card'>('list');
  // Режим создания нового агента: пустая форма прямо в контентной зоне
  const [creating, setCreating] = useState(false);
  // Проекты — чтобы показать имя/зону проектной персоны и открыть её проект в «Поговорить»
  const [projects, setProjects] = useState<Project[]>([]);
  // Идёт создание чата по кнопке «Поговорить»
  const [talking, setTalking] = useState(false);

  useEffect(() => { void ensurePersonasLoaded(); }, []);
  useEffect(() => { api.projects.list().then(setProjects).catch(() => {}); }, []);

  // Диплинк #/agents/{id}
  useEffect(() => {
    const t = parseHash();
    if (t?.screen === 'agents' && t.agentId) { setSelectedId(t.agentId); setMobileView('card'); }
  }, []);

  // Back/forward браузера внутри раздела «Агенты»
  useEffect(() => {
    const onPop = (e: PopStateEvent) => {
      const s = e.state as NavSnapshot | null;
      if (s?.screen === 'agents') {
        setSelectedId(s.agent ?? null);
        setMobileView(s.agent ? 'card' : 'list');
      }
    };
    window.addEventListener('popstate', onPop);
    return () => window.removeEventListener('popstate', onPop);
  }, []);

  const selected = personas.find(p => p.id === selectedId) ?? null;

  const selectPersona = (id: string) => {
    setCreating(false);
    setSelectedId(id); setMobileView('card');
    navPush({ screen: 'agents', agent: id });
  };
  const clearSelection = () => {
    setCreating(false);
    setSelectedId(null); setMobileView('list');
    if (getNav()?.agent) navReplace({ screen: 'agents' });
  };
  // Кнопка «Новый агент» — пустая форма создания в контентной зоне
  const startCreate = () => {
    setSelectedId(null); setCreating(true); setMobileView('card');
    if (getNav()?.agent) navReplace({ screen: 'agents' });
  };

  const onDelete = async (p: Persona) => {
    if (!window.confirm(`Удалить агента «${personaLabel(p)}»?`)) return;
    try {
      await api.personas.remove(p.id);
      bumpPersonas();
      clearSelection();
    } catch {
      alert('Не удалось удалить агента.');
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
        if (!proj) { alert('Проект агента недоступен.'); return; }
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
        onBack={isMobile ? clearSelection : undefined}
        isMobile={isMobile} />
    : <div style={{ height: '100%', display: 'flex', alignItems: 'center', justifyContent: 'center' }}>
        <EmptyState icon={<IconAgents />} title="Агенты"
          subtitle={personas.length ? 'Выбери агента слева, чтобы открыть его профиль и память' : 'Создай первого олицетворённого агента: имя, характер, аватар'}
          action={<button onClick={startCreate} style={newBtn}>Новый агент</button>} />
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
      <HubHeader value="agents" onTab={onHubTab} auth={auth} onLogout={onLogout} />
      <div style={{ flex: 1, minHeight: 0 }}>{body}</div>
    </div>
  );
}

// Пане создания нового агента: лёгкая шапка + пустая PersonaForm в контентной зоне
function PersonaCreatePane({ projects, onSaved, onCancel, onBack }: {
  projects: Project[];
  onSaved: (p: Persona) => void;
  onCancel: () => void;
  onBack?: () => void;
}) {
  return (
    <div style={{ height: '100%', display: 'flex', flexDirection: 'column', overflow: 'hidden' }}>
      <div style={{
        flex: 'none', display: 'flex', alignItems: 'center', gap: 10, padding: '8px 12px',
        borderBottom: `1px solid ${C.border}`, borderLeft: `3px solid ${C.accent}`, background: C.bgPanel,
      }}>
        {onBack && (
          <button onClick={onBack} aria-label="Назад" style={iconBtn}>
            <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
              <path d="M15 18l-6-6 6-6" />
            </svg>
          </button>
        )}
        <div style={{ fontFamily: FONT.serif, fontSize: 15, fontWeight: 600, color: C.textHeading, letterSpacing: '-0.01em' }}>
          Новый агент
        </div>
      </div>
      <div style={{ flex: 1, minHeight: 0 }}>
        <PersonaForm persona={null} projects={projects} onSaved={onSaved} onCancel={onCancel} />
      </div>
    </div>
  );
}

// Студия персоны: центральная область = инлайн-форма профиля ИЛИ долгая память.
// Чата здесь нет — разговор живёт среди обычных чатов (кнопка «Поговорить»).
function PersonaStudio({ persona, projects, talking, onDelete, onTalk, onBack, isMobile }: {
  persona: Persona;
  projects: Project[];
  talking: boolean;
  onDelete: () => void;
  onTalk: () => void;
  onBack?: () => void;
  isMobile: boolean;
}) {
  // Активный вид: профиль (форма настройки) или долгая память персоны
  const [view, setView] = useState<'profile' | 'memory'>('profile');

  const isProjectScope = persona.scope === 'project';
  const zoneName = isProjectScope
    ? (projects.find(p => p.id === persona.projectId)?.name ?? persona.projectId ?? 'Проект')
    : null;

  // Цветовая тема персоны: акцент из палитры аватара; пусто → дефолтный accent продукта
  const accent = AGENT_COLORS[persona.avatar?.color ?? ''] ?? C.accent;

  // Стрип персоны над содержимым (левая полоса — акцент персоны)
  const strip = (
    <div style={{
      flex: 'none', display: 'flex', alignItems: 'center', gap: 10, padding: '8px 12px',
      borderBottom: `1px solid ${C.border}`, borderLeft: `3px solid ${accent}`, background: C.bgPanel, position: 'relative',
    }}>
      {onBack && (
        <button onClick={onBack} aria-label="Назад" style={iconBtn}>
          <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
            <path d="M15 18l-6-6 6-6" />
          </svg>
        </button>
      )}
      <PersonaAvatar persona={persona} size={34} />
      <div style={{ minWidth: 0, display: 'flex', flexDirection: 'column', gap: 1 }}>
        {/* Роль — главная строка (serif, цвет персоны), имя под ней (мельче, приглушённо) */}
        <div style={{ fontFamily: FONT.serif, fontSize: 15, fontWeight: 600, color: accent, letterSpacing: '-0.01em', overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
          {personaTitleLines(persona).primary}
        </div>
        {personaTitleLines(persona).secondary && (
          <div style={{ fontSize: 11.5, color: C.textMuted, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
            {personaTitleLines(persona).secondary}
          </div>
        )}
        <span style={zoneBadge(isProjectScope, accent)}>{isProjectScope ? `Проект · ${zoneName}` : 'Глобальный'}</span>
      </div>

      {/* Переключатель Профиль | Память */}
      <div style={{ marginLeft: 6, display: 'flex', gap: 2, padding: 2, background: C.bgInset, borderRadius: R.pill }}>
        <button onClick={() => setView('profile')} style={segBtn(view === 'profile')}>Профиль</button>
        <button onClick={() => setView('memory')} style={segBtn(view === 'memory')}>Память</button>
      </div>

      <div style={{ flex: 1 }} />

      {/* «Поговорить» — создать чат от лица персоны */}
      <button onClick={onTalk} disabled={talking}
        style={{ ...talkBtn(accent), padding: '7px 14px', fontSize: 13, opacity: talking ? 0.6 : 1, cursor: talking ? 'default' : 'pointer' }}>
        <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
          <path d="M21 11.5a8.5 8.5 0 0 1-12 7.7L3 21l1.8-6A8.5 8.5 0 1 1 21 11.5z" />
        </svg>
        {talking ? 'Создаём…' : 'Поговорить'}
      </button>
    </div>
  );

  const content = view === 'memory'
    // Память — под стрипом, свой заголовок не нужен (идентичность уже в стрипе)
    ? <div style={{ flex: 1, minHeight: 0 }}><PersonaMemoryPanel persona={persona} isMobile={isMobile} embedded /></div>
    // Профиль — инлайн-форма настройки прямо в контентной зоне (удаление — из формы)
    : <div style={{ flex: 1, minHeight: 0 }}>
        <PersonaForm persona={persona} projects={projects} onSaved={() => {}} onDelete={() => onDelete()} />
      </div>;

  return (
    <div style={{ height: '100%', display: 'flex', flexDirection: 'column', overflow: 'hidden' }}>
      {strip}
      {/* Тонкая акцентная полоса персоны */}
      <div style={{ flex: 'none', height: 2, background: `${accent}55` }} />
      {content}
    </div>
  );
}

// Бейдж зоны персоны (проект/глобальный) — тонирован акцентом персоны
// (проектная зона — чуть плотнее заливка, чтобы отличать от глобальной)
function zoneBadge(isProject: boolean, accent: string): React.CSSProperties {
  return {
    display: 'inline-block', fontSize: 10.5, fontWeight: 600, letterSpacing: '0.02em',
    padding: '1px 7px', borderRadius: R.pill, width: 'fit-content',
    background: `${accent}${isProject ? '2E' : '17'}`,
    color: accent,
  };
}

const iconBtn: React.CSSProperties = {
  width: 32, height: 32, borderRadius: R.md, border: 'none', background: 'transparent',
  color: C.textSecondary, cursor: 'pointer', display: 'flex', alignItems: 'center', justifyContent: 'center', flexShrink: 0,
};
const newBtn: React.CSSProperties = {
  background: C.accent, color: C.onAccent, border: 'none', borderRadius: R.md,
  padding: '9px 16px', fontSize: 13, fontWeight: 500, cursor: 'pointer', fontFamily: FONT.sans,
};
// Кнопка «Поговорить» — залита акцентом персоны
function talkBtn(accent: string): React.CSSProperties {
  return {
    display: 'inline-flex', alignItems: 'center', gap: 7,
    background: accent, color: '#fff', border: 'none', borderRadius: R.lg,
    padding: '9px 18px', fontSize: 13.5, fontWeight: 600, fontFamily: FONT.sans,
  };
}
// Сегмент переключателя Профиль | Память
function segBtn(active: boolean): React.CSSProperties {
  return {
    border: 'none', borderRadius: R.pill, padding: '5px 12px', fontSize: 12.5, fontWeight: 600,
    cursor: 'pointer', fontFamily: FONT.sans, whiteSpace: 'nowrap',
    background: active ? C.bgWhite : 'transparent',
    color: active ? C.textHeading : C.textMuted,
    boxShadow: active ? SHADOW.thumb : 'none',
  };
}
