import { useEffect, useState } from 'react';
import type { AuthState, Persona, Project, Session, SkillInfo } from '../../types';
import type { HubTab } from '../../components/HubTabs';
import { HubHeader } from '../../components/HubHeader';
import { EmptyState } from '../../components/EmptyState';
import { ChatPanel } from '../../components/ChatPanel';
import { C, FONT, R, SHADOW } from '../../lib/design';
import { AGENT_COLORS } from '../../components/AgentSelector';
import { api } from '../../lib/api';
import { usePersonas, ensurePersonasLoaded, bumpPersonas } from '../../lib/personas';
import { parseHash, navPush, navReplace, getNav, type NavSnapshot } from '../../lib/nav';
import { PersonaList } from './PersonaList';
import { PersonaEditor } from './PersonaEditor';
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
  // Редактор: null — закрыт; { persona } — правка; {} — создание
  const [editor, setEditor] = useState<{ persona?: Persona } | null>(null);
  // Проекты — чтобы показать имя/зону проектной персоны и отдать project в ChatPanel
  const [projects, setProjects] = useState<Project[]>([]);
  // Глобальные скиллы для композера (чат от лица персоны — как чат вне проекта)
  const [skills, setSkills] = useState<SkillInfo[]>([]);

  useEffect(() => { void ensurePersonasLoaded(); }, []);
  useEffect(() => { api.projects.list().then(setProjects).catch(() => {}); }, []);
  useEffect(() => { api.skills.listGlobal().then(setSkills).catch(() => {}); }, []);

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
    setSelectedId(id); setMobileView('card');
    navPush({ screen: 'agents', agent: id });
  };
  const clearSelection = () => {
    setSelectedId(null); setMobileView('list');
    if (getNav()?.agent) navReplace({ screen: 'agents' });
  };

  const onDelete = async (p: Persona) => {
    if (!window.confirm(`Удалить агента «${p.name}»?`)) return;
    try {
      await api.personas.remove(p.id);
      bumpPersonas();
      clearSelection();
    } catch {
      alert('Не удалось удалить агента.');
    }
  };

  const sidebar = (
    <PersonaList personas={personas} selectedId={selectedId} onSelect={selectPersona} onNew={() => setEditor({})} />
  );

  const centerPane = selected
    ? <PersonaChat
        key={selected.id}
        persona={selected}
        projects={projects}
        skills={skills}
        onEdit={() => setEditor({ persona: selected })}
        onDelete={() => onDelete(selected)}
        onBack={isMobile ? clearSelection : undefined}
        isMobile={isMobile} />
    : <div style={{ height: '100%', display: 'flex', alignItems: 'center', justifyContent: 'center' }}>
        <EmptyState icon={<IconAgents />} title="Агенты"
          subtitle={personas.length ? 'Выбери агента слева, чтобы начать разговор' : 'Создай первого олицетворённого агента: имя, характер, аватар'}
          action={<button onClick={() => setEditor({})} style={newBtn}>Новый агент</button>} />
      </div>;

  const body = isMobile ? (
    (mobileView === 'list' || !selected)
      ? <div style={{ height: '100%', display: 'flex', flexDirection: 'column', background: C.bgPanel }}>{sidebar}</div>
      : <div style={{ height: '100%', display: 'flex', flexDirection: 'column' }}>{centerPane}</div>
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
      {editor && (
        <PersonaEditor
          persona={editor.persona}
          onClose={() => setEditor(null)}
          onSaved={p => { setEditor(null); selectPersona(p.id); }}
        />
      )}
    </div>
  );
}

// Читаемое имя чата персоны для списка/дропдауна
function chatTitle(s: Session): string {
  if (s.name && s.name.trim()) return s.name;
  const d = new Date(s.updatedAt || s.createdAt);
  return `Разговор от ${d.toLocaleDateString('ru-RU', { day: 'numeric', month: 'short' })}`;
}

// Центральная область выбранной персоны: чат от её лица (переиспользует ChatPanel).
// Слева/сверху — стрип персоны (аватар, имя, зона), список её чатов и действия.
function PersonaChat({ persona, projects, skills, onEdit, onDelete, onBack, isMobile }: {
  persona: Persona;
  projects: Project[];
  skills: SkillInfo[];
  onEdit: () => void;
  onDelete: () => void;
  onBack?: () => void;
  isMobile: boolean;
}) {
  const [chats, setChats] = useState<Session[]>([]);
  const [activeId, setActiveId] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [creating, setCreating] = useState(false);
  const [menuOpen, setMenuOpen] = useState(false);
  const [attachedFiles, setAttachedFiles] = useState<string[]>([]);
  // Активный вид центральной области: разговор или долгая память персоны
  const [view, setView] = useState<'chat' | 'memory'>('chat');

  // Загрузка чатов персоны при её выборе — открываем самый свежий
  useEffect(() => {
    let alive = true;
    setLoading(true);
    api.personas.chats(persona.id)
      .then(list => {
        if (!alive) return;
        const sorted = [...list].sort((a, b) => (b.updatedAt || '').localeCompare(a.updatedAt || ''));
        setChats(sorted);
        setActiveId(sorted[0]?.id ?? null);
      })
      .catch(() => { if (alive) { setChats([]); setActiveId(null); } })
      .finally(() => { if (alive) setLoading(false); });
    return () => { alive = false; };
  }, [persona.id]);

  // Вложения относятся к конкретному чату — сбрасываем при переключении
  useEffect(() => { setAttachedFiles([]); }, [activeId]);

  const startChat = async () => {
    if (creating) return;
    setCreating(true);
    try {
      const chat = await api.personas.createChat(persona.id, { mode: 'auto' });
      setChats(prev => [chat, ...prev]);
      setActiveId(chat.id);
    } catch (e) {
      alert(e instanceof Error ? e.message : 'Не удалось создать чат');
    } finally {
      setCreating(false);
    }
  };

  const activeChat = chats.find(c => c.id === activeId) ?? null;
  const activeProject = activeChat?.projectId ? projects.find(p => p.id === activeChat.projectId) : undefined;

  const isProjectScope = persona.scope === 'project';
  const zoneName = isProjectScope
    ? (projects.find(p => p.id === persona.projectId)?.name ?? persona.projectId ?? 'Проект')
    : null;

  // Цветовая тема персоны: акцент из палитры аватара; пусто → дефолтный accent продукта
  const accent = AGENT_COLORS[persona.avatar?.color ?? ''] ?? C.accent;

  const onSessionUpdated = (updated: Session) =>
    setChats(prev => prev.map(c => c.id === updated.id ? { ...c, ...updated } : c));

  // Приветственный бабл: показывается в пустом чате как первое сообщение от лица персоны
  // (визуально, в бэкенд не уходит). Пропадает, как только появляются реальные сообщения.
  const greetingBubble = persona.greeting?.trim() ? (
    <div style={{ display: 'flex', gap: 10, alignItems: 'flex-start', maxWidth: 620, marginTop: 8 }}>
      <PersonaAvatar persona={persona} size={30} />
      <div style={{
        background: C.bgWhite, border: `1px solid ${accent}33`, borderLeft: `3px solid ${accent}`,
        borderRadius: R.xl, padding: '11px 15px', fontSize: 14, lineHeight: 1.55, color: C.textPrimary,
      }}>
        {persona.greeting}
      </div>
    </div>
  ) : undefined;

  // Стрип персоны над чатом (левая полоса — акцент персоны)
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
        <div style={{ fontFamily: FONT.serif, fontSize: 15, fontWeight: 600, color: accent, letterSpacing: '-0.01em', overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
          {persona.name}
        </div>
        <span style={zoneBadge(isProjectScope, accent)}>{isProjectScope ? `Проект · ${zoneName}` : 'Глобальный'}</span>
      </div>

      {/* Переключатель Чат | Память */}
      <div style={{ marginLeft: 6, display: 'flex', gap: 2, padding: 2, background: C.bgInset, borderRadius: R.pill }}>
        <button onClick={() => setView('chat')} style={segBtn(view === 'chat')}>Чат</button>
        <button onClick={() => setView('memory')} style={segBtn(view === 'memory')}>Память</button>
      </div>

      <div style={{ flex: 1 }} />

      {/* Чат-специфичные контролы — только в режиме разговора */}
      {view === 'chat' && chats.length > 0 && (
        <ChatSwitcher chats={chats} activeId={activeId} onSelect={id => setActiveId(id)} />
      )}
      {view === 'chat' && (
        <button onClick={startChat} disabled={creating} style={btnGhost} title="Новый чат с агентом">
          + Новый чат
        </button>
      )}
      {/* Меню действий с персоной */}
      <div style={{ position: 'relative' }}>
        <button onClick={() => setMenuOpen(o => !o)} aria-label="Действия" style={iconBtn}>
          <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round">
            <circle cx="12" cy="5" r="1.6" /><circle cx="12" cy="12" r="1.6" /><circle cx="12" cy="19" r="1.6" />
          </svg>
        </button>
        {menuOpen && (
          <>
            <div onClick={() => setMenuOpen(false)} style={{ position: 'fixed', inset: 0, zIndex: 40 }} />
            <div style={{
              position: 'absolute', top: '100%', right: 0, marginTop: 4, zIndex: 41, minWidth: 160,
              background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.lg, boxShadow: SHADOW.dropdown, padding: 4,
            }}>
              <button onClick={() => { setMenuOpen(false); onEdit(); }} style={menuItem}>Редактировать</button>
              <button onClick={() => { setMenuOpen(false); onDelete(); }} style={{ ...menuItem, color: C.dangerText }}>Удалить</button>
            </div>
          </>
        )}
      </div>
    </div>
  );

  let content: React.ReactNode;
  if (view === 'memory') {
    content = (
      <div style={{ flex: 1, minHeight: 0 }}>
        <PersonaMemoryPanel persona={persona} isMobile={isMobile} embedded />
      </div>
    );
  } else if (loading) {
    content = (
      <div style={{ flex: 1, display: 'flex', alignItems: 'center', justifyContent: 'center', color: C.textMuted, fontSize: 13 }}>
        Загрузка…
      </div>
    );
  } else if (activeChat) {
    content = (
      <div style={{ flex: 1, minHeight: 0 }}>
        <ChatPanel
          key={activeChat.id}
          session={activeChat}
          project={activeProject}
          skills={skills}
          attachedFiles={attachedFiles}
          onAttachedFilesChange={setAttachedFiles}
          onSessionUpdated={onSessionUpdated}
          isMobile={isMobile}
          greetingBubble={greetingBubble}
        />
      </div>
    );
  } else {
    // Нет ни одного чата — приглашение начать разговор
    content = (
      <div style={{ flex: 1, minHeight: 0, overflowY: 'auto', display: 'flex', alignItems: 'center', justifyContent: 'center', padding: 24 }}>
        <div style={{ maxWidth: 440, display: 'flex', flexDirection: 'column', alignItems: 'center', textAlign: 'center', gap: 14 }}>
          <PersonaAvatar persona={persona} size={72} />
          <div style={{ fontFamily: FONT.serif, fontSize: 24, fontWeight: 500, color: C.textHeading, letterSpacing: '-0.01em' }}>
            {persona.name}
          </div>
          {persona.description && (
            <div style={{ fontSize: 14, color: C.textSecondary, lineHeight: 1.5 }}>{persona.description}</div>
          )}
          {persona.greeting && (
            <div style={{
              fontSize: 14, color: C.textPrimary, lineHeight: 1.55, fontStyle: 'italic',
              background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.xl, padding: '12px 16px',
            }}>
              «{persona.greeting}»
            </div>
          )}
          <button onClick={startChat} disabled={creating} style={{ ...newBtn, marginTop: 4, opacity: creating ? 0.6 : 1 }}>
            {creating ? 'Создаём…' : 'Начать разговор'}
          </button>
        </div>
      </div>
    );
  }

  return (
    <div style={{ height: '100%', display: 'flex', flexDirection: 'column', overflow: 'hidden' }}>
      {strip}
      {/* Тонкая акцентная полоса персоны над лентой */}
      <div style={{ flex: 'none', height: 2, background: `${accent}55` }} />
      {content}
    </div>
  );
}

// Дропдаун выбора чата персоны
function ChatSwitcher({ chats, activeId, onSelect }: {
  chats: Session[];
  activeId: string | null;
  onSelect: (id: string) => void;
}) {
  const [open, setOpen] = useState(false);
  const active = chats.find(c => c.id === activeId) ?? null;
  if (chats.length <= 1) return null;
  return (
    <div style={{ position: 'relative' }}>
      <button onClick={() => setOpen(o => !o)} style={{ ...btnGhost, display: 'flex', alignItems: 'center', gap: 6, maxWidth: 200 }}>
        <span style={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
          {active ? chatTitle(active) : 'Чаты'}
        </span>
        <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.4" strokeLinecap="round" strokeLinejoin="round">
          <path d="M6 9l6 6 6-6" />
        </svg>
      </button>
      {open && (
        <>
          <div onClick={() => setOpen(false)} style={{ position: 'fixed', inset: 0, zIndex: 40 }} />
          <div style={{
            position: 'absolute', top: '100%', right: 0, marginTop: 4, zIndex: 41, minWidth: 220, maxWidth: 300,
            maxHeight: 320, overflowY: 'auto', background: C.bgWhite, border: `1px solid ${C.border}`,
            borderRadius: R.lg, boxShadow: SHADOW.dropdown, padding: 4,
          }}>
            {chats.map(c => (
              <button key={c.id} onClick={() => { onSelect(c.id); setOpen(false); }}
                style={{ ...menuItem, background: c.id === activeId ? C.bgSelected : 'transparent', display: 'block', width: '100%' }}>
                <span style={{ display: 'block', overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{chatTitle(c)}</span>
              </button>
            ))}
          </div>
        </>
      )}
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
const btnGhost: React.CSSProperties = {
  background: 'transparent', border: `1px solid ${C.border}`, borderRadius: R.lg,
  padding: '6px 12px', fontSize: 13, cursor: 'pointer', fontFamily: FONT.sans, color: C.textSecondary, flexShrink: 0,
};
const menuItem: React.CSSProperties = {
  background: 'transparent', border: 'none', borderRadius: R.md, width: '100%', textAlign: 'left',
  padding: '8px 10px', fontSize: 13, cursor: 'pointer', fontFamily: FONT.sans, color: C.textPrimary,
};
const newBtn: React.CSSProperties = {
  background: C.accent, color: C.onAccent, border: 'none', borderRadius: R.md,
  padding: '9px 16px', fontSize: 13, fontWeight: 500, cursor: 'pointer', fontFamily: FONT.sans,
};
// Сегмент переключателя Чат | Память
function segBtn(active: boolean): React.CSSProperties {
  return {
    border: 'none', borderRadius: R.pill, padding: '5px 12px', fontSize: 12.5, fontWeight: 600,
    cursor: 'pointer', fontFamily: FONT.sans, whiteSpace: 'nowrap',
    background: active ? C.bgWhite : 'transparent',
    color: active ? C.textHeading : C.textMuted,
    boxShadow: active ? SHADOW.thumb : 'none',
  };
}
