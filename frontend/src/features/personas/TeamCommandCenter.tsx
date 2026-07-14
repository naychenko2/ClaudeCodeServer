import { useEffect, useMemo, useState, type CSSProperties } from 'react';
import {
  Users, MessageSquare, Mic, Workflow, Plus, CheckCircle2, Repeat, Trash2,
  Brain, BookOpen, FileText, UserPlus, UserMinus, ChevronRight,
  MoreHorizontal, Settings, Wand2, EllipsisVertical,
} from 'lucide-react';
import type { ComponentType } from 'react';
import type { Persona, Project, Session, Task, TeamMemoryEntry, TeamMemberDraft } from '../../types';
import { api } from '../../lib/api';
import { onMessage } from '../../lib/signalr';
import { showToast } from '../../lib/toast';
import { useIsMobile } from '../../lib/breakpoints';
import { usePersonas, personaLabel } from '../../lib/personas';
import { projectColor } from '../../lib/tasks';
import { C, FONT, R, SHADOW } from '../../lib/design';
import { PersonaAvatar } from './PersonaAvatar';
import { SPECIALTY_LABEL } from './automationMeta';
import { TeamMemoryPanel } from './TeamMemoryPanel';
import { Modal, IconButton, Menu, MenuItem, WaitingIndicator } from '../../components/ui';
import { Toolbar, PillSwitch, tbBtnPrimary } from '../../components/Toolbar';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import { NewTaskDialog } from '../tasks/NewTaskDialog';
import { useAiJob, runAiJob, patchAiJobResult, resetAiJob } from '../../lib/aiJobStore';

// Командный центр проекта (①-L1, табовый): 3 вкладки — Обзор / Память команды / Активность-таймлайн.
type Tab = 'overview' | 'memory' | 'activity';
type FilterKey = 'all' | 'tasks' | 'memory' | 'chats' | 'content' | 'team';

const TAB_OPTIONS: { value: Tab; label: string }[] = [
  { value: 'overview', label: 'Обзор' },
  { value: 'memory', label: 'Память' },
  { value: 'activity', label: 'Активность' },
];

export function TeamCommandCenter({
  project, onOpenPersona, onNewPersona, onOpenSession, onOpenSessionById,
}: {
  project: Project;
  onOpenPersona: (id: string) => void;
  onNewPersona: () => void;
  onOpenSession: (session: Session) => void;
  onOpenSessionById: (sessionId: string) => void;
}) {
  const personas = usePersonas();
  const team = useMemo(() => personas.filter(p => p.scope === 'project' && p.projectId === project.id), [personas, project.id]);
  const [events, setEvents] = useState<EventRow[] | null>(null);
  const [mem, setMem] = useState<TeamMemoryEntry[] | null>(null);
  const [tasks, setTasks] = useState<Task[] | null>(null);
  const [tab, setTab] = useState<Tab>(() => (sessionStorage.getItem('cc_team_tab') as Tab) || 'overview');
  const [filter, setFilter] = useState<FilterKey>('all');
  const [actorFilter, setActorFilter] = useState<string | null>(null);
  const [limit, setLimit] = useState(40);
  const [pickerOpen, setPickerOpen] = useState(false);
  const [newTaskOpen, setNewTaskOpen] = useState(false);
  const [formTeamOpen, setFormTeamOpen] = useState(false);
  const [menuOpen, setMenuOpen] = useState(false);
  const isMobile = useIsMobile();
  const stripe = projectColor(project.id).main;

  useEffect(() => { sessionStorage.setItem('cc_team_tab', tab); }, [tab]);

  const refresh = async () => {
    try { setEvents((await api.projects.events(project.id, { limit })) as unknown as EventRow[] ?? []); }
    catch { setEvents([]); }
    try { setMem(await api.projects.teamMemory(project.id)); } catch { setMem([]); }
    try { setTasks(await api.tasks.listByProject(project.id)); } catch { setTasks([]); }
  };
  useEffect(() => { void refresh(); /* eslint-disable-next-line */ }, [project.id, limit]);

  // Realtime: перечитываем ленту/память/задачи по событиям изменений (с дебаунсом) —
  // иначе «Активность» и индикаторы «в работе»/«на связи» заморожены до перемонтирования.
  // Ходы чатов (chat_turn) отдельного realtime-события не имеют — их закрывает мягкий поллинг.
  useEffect(() => {
    let t: ReturnType<typeof setTimeout> | null = null;
    const debounced = () => { if (t) clearTimeout(t); t = setTimeout(() => { void refresh(); }, 1500); };
    const off = onMessage(msg => {
      if (msg.type === 'task_changed' || msg.type === 'notes_changed' || msg.type === 'personas_changed' || msg.type === 'team_memory_changed') debounced();
    });
    const poll = setInterval(() => { void refresh(); }, 60_000);
    return () => { off(); if (t) clearTimeout(t); clearInterval(poll); };
    // eslint-disable-next-line
  }, [project.id, limit]);

  const inFlight = useMemo(() => {
    const m = new Map<string, string>();
    for (const t of tasks ?? []) if (t.personaId && t.claudeStartedAt && !t.claudeResult && t.status !== 'done') m.set(t.personaId, t.title);
    return m;
  }, [tasks]);
  const onlineSet = useMemo(() => {
    const s = new Set<string>(); const now = Date.now();
    for (const e of events ?? []) if (e.type === 'chat_turn' && e.actor && e.actor !== 'user' && e.actor !== 'system' && now - new Date(e.ts).getTime() < 10 * 60_000) s.add(e.actor);
    return s;
  }, [events]);
  const live = useMemo(() => {
    const now = Date.now(); let last: EventRow | null = null;
    for (const e of events ?? []) if ((e.type === 'meeting' || e.type === 'pipeline') && now - new Date(e.ts).getTime() < 30 * 60_000 && (!last || e.ts > last.ts)) last = e;
    return last;
  }, [events]);
  const tasksActive = (tasks ?? []).filter(t => t.status !== 'done').length;
  const chatsToday = useMemo(() => {
    const today = new Date().toISOString().slice(0, 10); const s = new Set<string>();
    for (const e of events ?? []) if (e.type === 'chat_turn' && e.ts.slice(0, 10) === today && e.entityRef) s.add(e.entityRef);
    return s.size;
  }, [events]);

  const personaById = (id: string) => team.find(p => p.id === id);
  const addMem = async (text: string) => {
    try {
      const entry = await api.projects.addTeamMemory(project.id, text);
      setMem(prev => [entry, ...(prev ?? [])]);
    } catch { showToast('Память команды', 'Не удалось сохранить запись.'); }
  };
  const updateMem = async (id: string, text: string) => {
    try {
      const updated = await api.projects.updateTeamMemory(project.id, id, text);
      setMem(prev => (prev ?? []).map(m => m.id === id ? updated : m));
    } catch { showToast('Память команды', 'Не удалось сохранить изменения.'); }
  };
  const removeMem = async (id: string) => {
    try {
      await api.projects.removeTeamMemory(project.id, id);
      setMem(prev => (prev ?? []).filter(m => m.id !== id));
    } catch { showToast('Память команды', 'Не удалось удалить запись.'); }
  };
  const openEvent = (e: EventRow) => onEventClick(e, project.id, onOpenSessionById, onOpenPersona);
  const switchTab = (t: Tab) => setTab(t);

  const counts = useMemo(() => {
    const c: Record<FilterKey, number> = { all: 0, tasks: 0, memory: 0, chats: 0, content: 0, team: 0 };
    for (const e of events ?? []) { c.all++; (c[CATEGORY[e.type] ?? 'all'])++; }
    return c;
  }, [events]);

  return (
    <div style={{ height: '100%', display: 'flex', flexDirection: 'column', overflow: 'hidden', background: C.bgMain }}>
      {/* Единый тулбар в стиле PersonaToolbar */}
      <Toolbar isMobile={isMobile} style={{ borderLeft: `3px solid ${stripe}`, position: 'relative' }}>
        {/* Идентичность проекта (плитка 32 + serif + зона-бейдж) */}
        <div style={{ width: 32, height: 32, borderRadius: R.md, background: `${stripe}22`, display: 'flex', alignItems: 'center', justifyContent: 'center', flexShrink: 0 }}>
          <Users size={18} color={stripe} strokeWidth={ICON_STROKE} />
        </div>
        <div style={{ flex: 1, minWidth: 0, display: 'flex', flexDirection: 'column', gap: 1 }}>
          <div style={{ fontFamily: FONT.serif, fontSize: 15, fontWeight: 600, color: stripe, letterSpacing: '-0.01em', overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
            Команда · {project.name}
          </div>
          <div style={{ display: 'flex', alignItems: 'center', gap: 7, minWidth: 0 }}>
            <span style={{ fontSize: 11.5, color: C.textMuted, fontFamily: FONT.sans, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', maxWidth: 160 }}>
              {team.length} участников
            </span>
            <span style={{ display: 'inline-block', fontSize: 10.5, fontWeight: 600, padding: '1px 7px', borderRadius: R.pill, background: `${stripe}1F`, color: stripe, whiteSpace: 'nowrap', flexShrink: 0 }}>
              Проект
            </span>
          </div>
        </div>
        {/* Вкладки */}
        <PillSwitch<Tab> value={tab} onChange={t => switchTab(t)} options={TAB_OPTIONS} compact={isMobile} isMobile={isMobile} />
        {/* Действия */}
        {!isMobile && (
          <button onClick={() => setPickerOpen(true)} disabled={team.length < 2}
            style={{ ...tbBtnPrimary, background: stripe, color: C.onAccent, display: 'inline-flex', alignItems: 'center', gap: 7, opacity: team.length < 2 ? 0.5 : 1, cursor: team.length < 2 ? 'default' : 'pointer' }}>
            <MessageSquare size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} style={{ flexShrink: 0 }} />
            Созвать
          </button>
        )}
        <div style={{ position: 'relative', flexShrink: 0 }}>
          <IconButton onClick={() => setMenuOpen(o => !o)} title="Ещё" size={isMobile ? 'lg' : 'md'}>
            <EllipsisVertical size={ICON_SIZE.md} strokeWidth={ICON_STROKE} style={{ flexShrink: 0 }} />
          </IconButton>
          {menuOpen && (
            <Menu onClose={() => setMenuOpen(false)} align="right" top={38} minWidth={200}>
              <MenuItem icon={<UserPlus size={15} strokeWidth={ICON_STROKE} />} label="Новая персона" onClick={() => { setMenuOpen(false); onNewPersona(); }} />
              <MenuItem icon={<Wand2 size={15} strokeWidth={ICON_STROKE} />} label="Сформировать команду" onClick={() => { setMenuOpen(false); setFormTeamOpen(true); }} />
              <MenuItem icon={<Plus size={15} strokeWidth={ICON_STROKE} />} label="Новая задача" onClick={() => { setMenuOpen(false); setNewTaskOpen(true); }} />
            </Menu>
          )}
        </div>
      </Toolbar>
      {/* Тонкая полоса проекта */}
      <div style={{ flex: 'none', height: 2, background: `${stripe}55` }} />

      {/* Тело (скроллится) */}
      <div style={{ flex: 1, overflowY: 'auto', padding: isMobile ? '16px 16px 32px' : '20px 24px 40px' }}>
        <div style={{ maxWidth: 720, margin: '0 auto', display: 'flex', flexDirection: 'column', gap: 20 }}>
          {tab === 'overview' && (
            <OverviewPanel project={project} team={team} events={events} mem={mem} tasks={tasks}
              inFlight={inFlight} onlineSet={onlineSet} live={live} tasksActive={tasksActive} chatsToday={chatsToday}
              personaById={personaById} onOpenPersona={onOpenPersona} onSwitchTab={switchTab} onOpenEvent={openEvent}
              onPickerOpen={() => setPickerOpen(true)} onNewTaskOpen={() => setNewTaskOpen(true)} onNewPersona={onNewPersona}
              onFormTeamOpen={() => setFormTeamOpen(true)} onOpenSessionById={onOpenSessionById} onMenuOpen={() => createTeamChat(team, onOpenSession)} stripe={stripe} />
          )}
          {tab === 'memory' && (
            <TeamMemoryPanel mem={mem} onAdd={addMem} onUpdate={updateMem} onRemove={removeMem} stripe={stripe} />
          )}
          {tab === 'activity' && (
            <ActivityPanel events={events} personaById={personaById} filter={filter} setFilter={setFilter}
              actorFilter={actorFilter} setActorFilter={setActorFilter} counts={counts} limit={limit} onMore={() => setLimit(n => n + 40)} onOpenEvent={openEvent} />
          )}
        </div>
      </div>

      {pickerOpen && <GroupChatPicker team={team} onClose={() => setPickerOpen(false)} onCreated={s => { setPickerOpen(false); onOpenSession(s); }} />}
      {newTaskOpen && <NewTaskDialog defaultProjectId={project.id} onCreated={() => setNewTaskOpen(false)} onClose={() => setNewTaskOpen(false)} />}
      {formTeamOpen && <FormTeamDialog project={project} onClose={() => setFormTeamOpen(false)} onCreated={() => { setFormTeamOpen(false); void refresh(); }} />}
    </div>
  );
}

// ===== Вкладка 1: Обзор =====
function OverviewPanel(props: {
  project: Project; team: Persona[]; events: EventRow[] | null; mem: TeamMemoryEntry[] | null; tasks: Task[] | null;
  inFlight: Map<string, string>; onlineSet: Set<string>; live: EventRow | null; tasksActive: number; chatsToday: number;
  personaById: (id: string) => Persona | undefined; onOpenPersona: (id: string) => void; onSwitchTab: (t: Tab) => void; onOpenEvent: (e: EventRow) => void;
  onPickerOpen: () => void; onNewTaskOpen: () => void; onNewPersona: () => void; onFormTeamOpen: () => void; onOpenSessionById: (id: string) => void; onMenuOpen: () => void; stripe: string;
}) {
  const { project, team, events, mem, inFlight, onlineSet, live, tasksActive, chatsToday, personaById, onOpenPersona, onSwitchTab, onOpenEvent, onPickerOpen, onNewTaskOpen, onNewPersona, onFormTeamOpen, onOpenSessionById, onMenuOpen, stripe } = props;
  const recent = (events ?? []).slice(0, 6);
  const topMem = (mem ?? []).slice(0, 3);

  if (team.length === 0) {
    return (
      <div style={{ ...cardStyle, display: 'flex', flexDirection: 'column', alignItems: 'center', gap: 12, padding: '40px 24px', textAlign: 'center' }}>
        <Users size={36} color={C.textMuted} style={{ opacity: 0.5 }} />
        <div style={{ fontFamily: FONT.serif, fontSize: 16, color: C.textHeading }}>В этом проекте ещё нет команды</div>
        <div style={{ fontSize: 13, color: C.textMuted, fontFamily: FONT.sans, maxWidth: 340, lineHeight: 1.5 }}>
          Создайте персон — или попросите LLM сформировать команду по описанию.
        </div>
        <div style={{ display: 'flex', gap: 8, flexWrap: 'wrap', justifyContent: 'center' }}>
          <button onClick={onNewPersona} style={primaryBtn}><UserPlus size={15} /> Новая персона</button>
          <button onClick={onFormTeamOpen} style={ghostBtn}><Wand2 size={15} /> Сформировать команду</button>
        </div>
      </div>
    );
  }

  return (
    <>
      {/* Hero */}
      <div style={{ ...cardStyle, borderTop: `3px solid ${stripe}` }}>
        <div style={{ display: 'flex', alignItems: 'baseline', gap: 10, flexWrap: 'wrap' }}>
          <div style={{ fontFamily: FONT.serif, fontSize: 18, color: C.textHeading }}>Команда · {project.name}</div>
          <div style={{ marginLeft: 'auto', fontSize: 12, color: C.textMuted, fontFamily: FONT.sans }}>{team.length} в команде</div>
        </div>
        <div style={{ display: 'flex', gap: 14, flexWrap: 'wrap', marginTop: 10 }}>
          <StatusChip color={C.accent} dot>{inFlight.size} в работе</StatusChip>
          <StatusChip color={C.success}>{onlineSet.size} на связи</StatusChip>
          <StatusChip color={C.textSecondary}>{tasksActive} задач активно</StatusChip>
          <StatusChip color={C.textSecondary}>{chatsToday} чатов сегодня</StatusChip>
        </div>
        {live && (
          <div style={{ marginTop: 12, background: C.accentLight, border: `1px solid ${C.accentMuted}`, borderRadius: R.xl, padding: '10px 14px', display: 'flex', alignItems: 'center', gap: 12 }}>
            {live.type === 'meeting' ? <Mic size={16} color={C.accent} /> : <Workflow size={16} color={C.accent} />}
            <div style={{ flex: 1, minWidth: 0 }}>
              <div style={{ fontSize: 13, fontWeight: 700, color: C.accent, fontFamily: FONT.sans }}>{live.type === 'meeting' ? 'СОВЕЩАНИЕ ИДЁТ' : 'КОНВЕЙЕР ИДЁТ'}</div>
              <div style={{ fontSize: 12, color: C.textSecondary, fontFamily: FONT.sans, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{live.summary}</div>
            </div>
            {live.entityRef && <button onClick={() => onOpenSessionById(live.entityRef!)} style={linkBtn}>Открыть →</button>}
          </div>
        )}
        <div style={{ display: 'flex', gap: 8, flexWrap: 'wrap', marginTop: 12 }}>
          <button onClick={onPickerOpen} disabled={team.length < 2} style={primaryBtn}><MessageSquare size={15} /> Созвать команду</button>
          <button onClick={onNewTaskOpen} style={ghostBtn}><Plus size={15} /> Новая задача</button>
          <button onClick={onNewPersona} style={ghostBtn}><UserPlus size={15} /> Новая персона</button>
          <IconButton variant="soft" size="sm" title="Создать командный чат" onClick={onMenuOpen}><MoreHorizontal size={16} /></IconButton>
        </div>
      </div>

      <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(320px, 1fr))', gap: 14 }}>
        {/* Состав */}
        <div style={cardStyle}>
          <SectionLabel>Состав · {team.length}</SectionLabel>
          <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(180px, 1fr))', gap: 8, marginTop: 10 }}>
            {team.map(p => {
              const status: 'work' | 'online' | 'idle' = inFlight.has(p.id) ? 'work' : onlineSet.has(p.id) ? 'online' : 'idle';
              return (
                <button key={p.id} onClick={() => onOpenPersona(p.id)} style={memberCard}>
                  <PersonaAvatar persona={p} size={32} />
                  <div style={{ minWidth: 0, flex: 1 }}>
                    <div style={{ fontSize: 13, fontWeight: 600, color: C.textHeading, fontFamily: FONT.sans, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{personaLabel(p)}</div>
                    {p.specialty && p.specialty !== 'none' && <span style={specialtyBadge}>{SPECIALTY_LABEL[p.specialty] ?? p.specialty}</span>}
                  </div>
                  <StatusDot status={status} />
                </button>
              );
            })}
          </div>
        </div>

        {/* Последняя активность — без карточки, только линия + точки */}
        <div>
          <SectionLabel>Последняя активность</SectionLabel>
          <div style={{ display: 'flex', flexDirection: 'column', marginTop: 8, paddingLeft: 26, position: 'relative' }}>
            <div style={{ position: 'absolute', left: 4, top: 8, bottom: 8, width: 2, background: C.divider }} />
            {recent.length === 0 ? <Muted>Пока нет событий.</Muted> : recent.map(e => <EventCard key={e.id} e={e} personaById={personaById} onOpen={onOpenEvent} />)}
          </div>
          {events && events.length > 6 && <button onClick={() => onSwitchTab('activity')} style={{ ...linkBtn, marginTop: 8 }}>Вся активность →</button>}
        </div>
      </div>

      {/* Память (топ-3) */}
      <div style={cardStyle}>
        <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
          <SectionLabel>Память команды{mem && mem.length ? ` · ${mem.length}` : ''}</SectionLabel>
        </div>
        <div style={{ fontSize: 12, color: C.textMuted, fontFamily: FONT.sans, margin: '6px 0 10px' }}>Общие факты — их remember все персоны команды.</div>
        <div style={{ display: 'flex', flexDirection: 'column', gap: 5 }}>
          {topMem.length === 0 ? <Muted>Пока пусто.</Muted> : topMem.map(m => (
            <div key={m.id} style={memRowStyle}><span style={{ fontSize: 13, color: C.textPrimary, fontFamily: FONT.sans, flex: 1 }}>{m.text}</span></div>
          ))}
        </div>
        {mem && mem.length > 3 && <button onClick={() => onSwitchTab('memory')} style={{ ...linkBtn, marginTop: 8 }}>Вся память ({mem.length}) →</button>}
      </div>
    </>
  );
}

// ===== Вкладка 2: Память команды ===== — вынесена в TeamMemoryPanel.tsx (образец: PersonaMemoryPanel)

// ===== Вкладка 3: Активность — таймлайн =====
function ActivityPanel({ events, personaById, filter, setFilter, actorFilter, setActorFilter, counts, limit, onMore, onOpenEvent }: {
  events: EventRow[] | null; personaById: (id: string) => Persona | undefined;
  filter: FilterKey; setFilter: (f: FilterKey) => void; actorFilter: string | null; setActorFilter: (a: string | null) => void;
  counts: Record<FilterKey, number>; limit: number; onMore: () => void; onOpenEvent: (e: EventRow) => void;
}) {
  const filtered = useMemo(() => {
    const list = events ?? [];
    return list.filter(e => {
      if (filter !== 'all' && (CATEGORY[e.type] ?? 'all') !== filter) return false;
      if (actorFilter && e.actor !== actorFilter) return false;
      return true;
    });
  }, [events, filter, actorFilter]);

  const actors = useMemo(() => {
    const ids = new Set<string>();
    for (const e of events ?? []) if (e.actor && e.actor !== 'user' && e.actor !== 'system' && personaById(e.actor)) ids.add(e.actor);
    return [...ids];
  }, [events, personaById]);

  return (
    <>
      {/* FilterBar */}
      <div style={filterBarStyle}>
        <div style={{ display: 'flex', gap: 5, flexWrap: 'wrap' }}>
          {FILTERS.map(f => (
            <button key={f.key} onClick={() => setFilter(f.key)} style={filter === f.key ? filterChipActive : filterChip}>
              {f.label}{counts[f.key] ? ` ${counts[f.key]}` : ''}
            </button>
          ))}
        </div>
        {actors.length > 1 && (
          <div style={{ display: 'flex', gap: 5, flexWrap: 'wrap', marginTop: 6, alignItems: 'center' }}>
            <span style={{ fontSize: 11, color: C.textMuted, fontFamily: FONT.sans }}>исп.:</span>
            <button onClick={() => setActorFilter(null)} style={actorFilter === null ? filterChipActive : filterChip}>Все</button>
            {actors.map(id => {
              const p = personaById(id)!;
              return (
                <button key={id} onClick={() => setActorFilter(id)} style={actorFilter === id ? filterChipActive : filterChip}>
                  {personaLabel(p)}
                </button>
              );
            })}
          </div>
        )}
      </div>

      {events === null ? <Muted>Загрузка…</Muted>
        : filtered.length === 0 ? (
          <div style={{ ...cardStyle, textAlign: 'center', color: C.textMuted, fontFamily: FONT.sans, fontSize: 13, padding: '32px 16px' }}>
            {events.length === 0 ? 'Пока нет событий. Как только команда начнёт работать — здесь появится лента.' : 'Нет событий по фильтру. '}
            {events.length > 0 && <button onClick={() => { setFilter('all'); setActorFilter(null); }} style={{ ...linkBtn, display: 'inline-block', marginLeft: 6 }}>Сбросить</button>}
          </div>
        ) : (
          <Timeline events={filtered} personaById={personaById} onOpen={onOpenEvent} />
        )}
      {events && events.length >= limit && (
        <button onClick={onMore} style={{ ...linkBtn, margin: '12px auto 0', display: 'block' }}>Показать ещё</button>
      )}
    </>
  );
}

// ===== Таймлайн (вертикальная линия + цветные точки) =====
function Timeline({ events, personaById, onOpen }: {
  events: EventRow[]; personaById: (id: string) => Persona | undefined; onOpen: (e: EventRow) => void;
}) {
  const groups = useMemo(() => {
    const today = new Date().toISOString().slice(0, 10);
    const yest = new Date(Date.now() - 86_400_000).toISOString().slice(0, 10);
    const m = new Map<string, EventRow[]>();
    for (const e of events) {
      const d = e.ts.slice(0, 10);
      const label = d === today ? 'Сегодня' : d === yest ? 'Вчера' : fmtDay(d);
      (m.get(label) ?? m.set(label, []).get(label)!).push(e);
    }
    return [...m.entries()];
  }, [events]);

  return (
    <div style={{ display: 'flex', flexDirection: 'column' }}>
      {groups.map(([label, rows]) => (
        <div key={label} style={{ position: 'relative', paddingLeft: 26 }}>
          <div style={dayHeaderStyle}>{label}</div>
          {/* вертикальная линия таймлайна */}
          <div style={{ position: 'absolute', left: 4, top: 30, bottom: 6, width: 2, background: C.divider }} />
          {rows.map(e => <EventCard key={e.id} e={e} personaById={personaById} onOpen={onOpen} />)}
        </div>
      ))}
    </div>
  );
}

// ===== EventCard — карточка события: клик → переход к объекту (без раскрытия) =====
function EventCard({ e, personaById, onOpen }: {
  e: EventRow; personaById: (id: string) => Persona | undefined; onOpen: (e: EventRow) => void;
}) {
  const target = eventTarget(e);
  const meta = EVENT_META[e.type] ?? { Icon: Settings, color: C.textMuted, bg: C.bgInset, label: e.type };
  const p = personaById(e.actor);
  const clickable = !!target;

  return (
    <div style={{ position: 'relative' }}>
      {/* точка-маркер цветом категории */}
      <span style={{ position: 'absolute', left: -26, top: 14, width: 10, height: 10, borderRadius: '50%', background: meta.color, border: `2px solid ${C.bgMain}`, boxSizing: 'border-box', boxShadow: `0 0 0 1px ${meta.color}` }} />
      <div style={{ ...eventCardStyle, cursor: clickable ? 'pointer' : 'default' }} onClick={() => clickable && onOpen(e)}>
        {/* цветная полоса категории */}
        <div style={{ width: 3, borderRadius: 2, background: meta.color, flexShrink: 0, alignSelf: 'stretch' }} />
        {/* чип-иконка */}
        <div style={{ ...chipIconStyle, background: meta.bg }}><meta.Icon size={15} color={meta.color} /></div>
        <div style={{ flex: 1, minWidth: 0 }}>
          <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
            <span style={actionLabelStyle}>{meta.label}</span>
            <span style={{ marginLeft: 'auto', fontSize: 11, color: C.textMuted, fontFamily: FONT.sans, flexShrink: 0 }}>{shortAgo(e.ts)}</span>
          </div>
          <div style={{ display: 'flex', alignItems: 'center', gap: 6, marginTop: 4 }}>
            {p ? <PersonaAvatar persona={p} size={20} /> : <span style={actorDot}>{e.actor === 'user' ? 'Я' : <Settings size={12} color={C.textMuted} />}</span>}
            <span style={objTitleStyle}>{e.summary}</span>
          </div>
        </div>
        {clickable && <ChevronRight size={16} color={C.border} style={{ flexShrink: 0 }} />}
      </div>
    </div>
  );
}

// ===== Диалоги =====
function GroupChatPicker({ team, onClose, onCreated }: { team: Persona[]; onClose: () => void; onCreated: (s: Session) => void; }) {
  const [sel, setSel] = useState<string[]>(team.length >= 2 ? [team[0].id, team[1].id] : team.map(p => p.id));
  const [busy, setBusy] = useState(false);
  const toggle = (id: string) => setSel(prev => prev.includes(id) ? prev.filter(x => x !== id) : prev.length < 4 ? [...prev, id] : prev);
  const create = async () => {
    setBusy(true);
    try { const s = await api.chats.createGroup(sel); sessionStorage.setItem('cc_auto_discuss', s.id); onCreated(s); }
    catch (e) {
      window.dispatchEvent(new CustomEvent('cc-local-toast', {
        detail: { title: 'Не удалось создать групповой чат', body: e instanceof Error ? e.message : '', kind: 'info' },
      }));
    }
    finally { setBusy(false); }
  };
  return (
    <Modal width={420} title="Созвать команду" subtitle="Выберите 2–4 участников. Первый — ведущий." onClose={onClose}
      footer={<div style={{ display: 'flex', gap: 8, justifyContent: 'flex-end' }}>
        <button onClick={onClose} style={ghostBtn}>Отмена</button>
        <button onClick={() => void create()} disabled={sel.length < 2 || busy} style={primaryBtn}>Создать чат</button>
      </div>}>
      <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
        {team.map((p, i) => {
          const checked = sel.includes(p.id);
          return (
            <button key={p.id} onClick={() => toggle(p.id)} style={{ ...memberCard, border: `1px solid ${checked ? C.accent : C.borderLight}` }}>
              <PersonaAvatar persona={p} size={28} />
              <div style={{ minWidth: 0, flex: 1 }}>
                <div style={{ fontSize: 13, fontWeight: 600, color: C.textHeading, fontFamily: FONT.sans }}>{personaLabel(p)}</div>
                {i === 0 && sel[0] === p.id && <span style={{ fontSize: 11, color: C.accent, fontFamily: FONT.sans }}>ведущий</span>}
              </div>
              <span style={{ ...checkDot, background: checked ? C.accent : 'transparent', border: `2px solid ${checked ? C.accent : C.border}` }} />
            </button>
          );
        })}
      </div>
    </Modal>
  );
}

// Кандидаты AI-подбора команды с чекбоксами — статус/результат в aiJobStore
// (переживает закрытие/переоткрытие диалога того же проекта)
interface TeamSuggestResult {
  members: (TeamMemberDraft & { on: boolean })[];
}

function FormTeamDialog({ project, onClose, onCreated }: { project: Project; onClose: () => void; onCreated: () => void; }) {
  const teamKey = `personas:team-generate:${project.id}`;
  const teamJob = useAiJob<TeamSuggestResult>(teamKey);
  const [prompt, setPrompt] = useState('');
  const [creating, setCreating] = useState(false);
  // Сколько персон уже создано в текущем прогоне (для «N из M» — фото генерируются
  // последовательно и создание может занять заметное время)
  const [createdCount, setCreatedCount] = useState(0);
  const [error, setError] = useState<string | null>(null);
  const generating = teamJob.status === 'running';
  const members = teamJob.status === 'done' ? teamJob.result?.members ?? [] : [];
  const generate = () => {
    if (!prompt.trim() || generating) return;
    setError(null);
    runAiJob<TeamSuggestResult>(teamKey, async () => {
      const res = await api.personas.aiTeam(project.id, prompt.trim());
      return { members: (res.members ?? []).map(m => ({ ...m, on: true })) };
    });
  };
  const toggle = (i: number) => patchAiJobResult<TeamSuggestResult>(teamKey, prev => ({
    members: prev.members.map((m, idx) => idx === i ? { ...m, on: !m.on } : m),
  }));
  const create = async () => {
    if (creating || members.length === 0) return;
    setCreating(true); setCreatedCount(0); setError(null);
    try {
      for (const m of members) {
        if (!m.on) continue;
        await api.personas.create({
          name: m.name?.trim() || 'Персона', role: m.role, description: m.description,
          contract: { character: m.character, tone: m.tone }, specialty: m.specialty as Persona['specialty'] | undefined,
          scope: 'project', projectId: project.id, color: m.color, greeting: m.greeting, memoryEnabled: true,
          autoAvatar: true, avatarPrompt: m.avatarPrompt,
        });
        setCreatedCount(c => c + 1);
      }
      resetAiJob(teamKey);
      onCreated();
    } catch (e) { setError(e instanceof Error ? e.message : 'Не удалось создать персон'); }
    finally { setCreating(false); }
  };
  const selCount = members.filter(m => m.on).length;
  return (
    <Modal width={480} title="Сформировать команду" onClose={onClose} subtitle="Опишите команду — LLM проанализирует проект и предложит состав."
      footer={members.length > 0 ? (
        <div style={{ display: 'flex', gap: 8, justifyContent: 'flex-end', alignItems: 'center' }}>
          <span style={{ fontSize: 12, color: C.textMuted, fontFamily: FONT.sans }}>{selCount} из {members.length}</span>
          <button onClick={onClose} style={ghostBtn}>Отмена</button>
          <button onClick={() => void create()} disabled={selCount === 0 || creating} style={primaryBtn}>
            {creating ? `Создаю… ${createdCount}/${selCount}` : `Создать ${selCount || ''}`}
          </button>
        </div>
      ) : undefined}>
      <div style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
        {teamJob.status !== 'done' && (
          <>
            <textarea value={prompt} onChange={e => setPrompt(e.target.value)} autoFocus placeholder="Напр.: команда для бэкенда на .NET — аналитик, разработчик, ревьюер и тестировщик." style={{ ...inputStyle, minHeight: 80, resize: 'vertical' }} />
            {(error || (teamJob.status === 'error' && teamJob.error)) && (
              <div style={{ fontSize: 12.5, color: C.dangerText, fontFamily: FONT.sans }}>{error || teamJob.error}</div>
            )}
            {generating && <WaitingIndicator hint="Анализирую проект и подбираю состав — обычно 10–20 секунд" />}
            <button onClick={generate} disabled={!prompt.trim() || generating} style={primaryBtn}>{generating ? 'Анализирую проект…' : 'Сформировать состав'}</button>
          </>
        )}
        {teamJob.status === 'done' && members.length === 0 && (
          <div style={{ fontSize: 13, color: C.textMuted, fontFamily: FONT.sans }}>
            Не удалось предложить состав. Уточните промпт.
            <button onClick={() => resetAiJob(teamKey)} style={{ ...linkBtn, display: 'block', margin: '8px auto 0' }}>← назад</button>
          </div>
        )}
        {teamJob.status === 'done' && members.length > 0 && (
          <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
            {members.map((m, i) => {
              const checked = m.on;
              return (
                <button key={i} onClick={() => toggle(i)} style={{ ...memberCard, border: `1px solid ${checked ? C.accent : C.borderLight}` }}>
                  <div style={{ minWidth: 0, flex: 1 }}>
                    <div style={{ display: 'flex', gap: 8, alignItems: 'center' }}>
                      <span style={{ fontSize: 13, fontWeight: 600, color: C.textHeading, fontFamily: FONT.sans }}>{m.role ?? 'Роль'}{m.name ? ` (${m.name})` : ''}</span>
                      {m.specialty && <span style={specialtyBadge}>{SPECIALTY_LABEL[m.specialty] ?? m.specialty}</span>}
                    </div>
                    {m.description && <div style={{ fontSize: 12, color: C.textMuted, fontFamily: FONT.sans, marginTop: 2 }}>{m.description}</div>}
                  </div>
                  <span style={{ ...checkDot, background: checked ? C.accent : 'transparent', border: `2px solid ${checked ? C.accent : C.border}` }} />
                </button>
              );
            })}
            {creating && <WaitingIndicator hint="Создаю персон и подбираю им фото — до минуты на каждую" />}
            {error && <div style={{ fontSize: 12.5, color: C.dangerText, fontFamily: FONT.sans }}>{error}</div>}
          </div>
        )}
      </div>
    </Modal>
  );
}

async function createTeamChat(team: Persona[], onOpenSession: (s: Session) => void) {
  try { const s = await api.chats.createGroup(team.slice(0, 4).map(p => p.id)); onOpenSession(s); } catch { /* тишина */ }
}

// ===== Shared: мета событий, навигация, хелперы, стили =====
type EventRow = { id: number; ts: string; type: string; actor: string; summary: string; entityRef?: string | null };
type Nav = 'session' | 'task' | 'persona' | 'note' | 'knowledge' | null;
function eventTarget(e: EventRow): { nav: Nav; id: string } | null {
  if (!e.entityRef) return null;
  switch (e.type) {
    case 'chat_turn': case 'meeting': case 'pipeline': return { nav: 'session', id: e.entityRef };
    case 'task_created': case 'task_completed': case 'task_spawned': return { nav: 'task', id: e.entityRef };
    case 'memory_learned': case 'team_joined': return { nav: 'persona', id: e.entityRef };
    case 'note_changed': return { nav: 'note', id: e.entityRef };
    case 'knowledge_changed': return { nav: 'knowledge', id: e.entityRef };
    default: return null;
  }
}
function onEventClick(e: EventRow, projectId: string, openSession: (id: string) => void, openPersona: (id: string) => void) {
  const t = eventTarget(e); if (!t) return;
  if (t.nav === 'session') openSession(t.id);
  else if (t.nav === 'task') window.dispatchEvent(new CustomEvent('cc-open-url', { detail: { url: `#/project/${projectId}/task/${t.id}` } }));
  else if (t.nav === 'persona') openPersona(t.id);
  else if (t.nav === 'note') window.dispatchEvent(new CustomEvent('cc-open-url', { detail: { url: `#/notes/${t.id}` } }));
  else if (t.nav === 'knowledge') window.dispatchEvent(new CustomEvent('cc-open-url', { detail: { url: `#/knowledge/${t.id}` } }));
}

const CATEGORY: Record<string, FilterKey> = {
  chat_turn: 'chats', meeting: 'chats', pipeline: 'chats',
  task_created: 'tasks', task_completed: 'tasks', task_spawned: 'tasks', task_deleted: 'tasks',
  memory_learned: 'memory', note_changed: 'content', knowledge_changed: 'content',
  team_joined: 'team', team_left: 'team',
};
const FILTERS: { key: FilterKey; label: string }[] = [
  { key: 'all', label: 'всё' }, { key: 'tasks', label: 'задачи' }, { key: 'memory', label: 'память' },
  { key: 'chats', label: 'чаты' }, { key: 'content', label: 'контент' }, { key: 'team', label: 'команда' },
];
const EVENT_META: Record<string, { Icon: ComponentType<{ size?: number; color?: string }>; color: string; bg: string; label: string }> = {
  chat_turn: { Icon: MessageSquare, color: C.accent, bg: C.accentLight, label: 'Ответ в чате' },
  meeting: { Icon: Mic, color: C.accent, bg: C.accentLight, label: 'Совещание' },
  pipeline: { Icon: Workflow, color: C.accent, bg: C.accentLight, label: 'Конвейер' },
  task_created: { Icon: Plus, color: C.success, bg: C.successBg, label: 'Задача создана' },
  task_completed: { Icon: CheckCircle2, color: C.success, bg: C.successBg, label: 'Задача завершена' },
  task_spawned: { Icon: Repeat, color: C.accent, bg: C.accentLight, label: 'Следующий экземпляр' },
  task_deleted: { Icon: Trash2, color: C.danger, bg: C.dangerBg, label: 'Задача удалена' },
  memory_learned: { Icon: Brain, color: C.info, bg: C.infoBg, label: 'Запомнил факт' },
  knowledge_changed: { Icon: BookOpen, color: C.info, bg: C.infoBg, label: 'База знаний' },
  note_changed: { Icon: FileText, color: C.info, bg: C.infoBg, label: 'Заметка' },
  team_joined: { Icon: UserPlus, color: C.success, bg: C.successBg, label: 'В команде' },
  team_left: { Icon: UserMinus, color: C.textSecondary, bg: C.bgInset, label: 'Покинул(а) команду' },
};
function StatusChip({ children, color, dot }: { children: React.ReactNode; color: string; dot?: boolean }) {
  return <span style={{ display: 'flex', alignItems: 'center', gap: 5, fontSize: 12, fontWeight: 600, fontFamily: FONT.sans, color }}>{dot && <span style={{ width: 7, height: 7, borderRadius: '50%', background: color }} />}{children}</span>;
}
function StatusDot({ status }: { status: 'work' | 'online' | 'idle' }) {
  const c = status === 'work' ? C.accent : status === 'online' ? C.success : C.border;
  return <span style={{ width: 8, height: 8, borderRadius: '50%', background: c, flexShrink: 0 }} title={status === 'work' ? 'в работе' : status === 'online' ? 'на связи' : 'простаивает'} />;
}
function SectionLabel({ children }: { children: React.ReactNode }) {
  return <div style={{ fontSize: 11, fontWeight: 600, color: C.textSecondary, fontFamily: FONT.sans, textTransform: 'uppercase', letterSpacing: 0.4 }}>{children}</div>;
}
function Muted({ children }: { children: React.ReactNode }) {
  return <div style={{ fontSize: 13, color: C.textMuted, fontFamily: FONT.sans }}>{children}</div>;
}
function shortAgo(iso: string): string {
  const t = new Date(iso).getTime(); if (!Number.isFinite(t)) return '';
  const mins = Math.floor((Date.now() - t) / 60000);
  if (mins < 1) return 'только что'; if (mins < 60) return `${mins} мин`;
  const hrs = Math.floor(mins / 60); if (hrs < 24) return `${hrs} ч`;
  return `${Math.floor(hrs / 24)} д`;
}
function fmtDay(d: string): string {
  try { const dt = new Date(d); return dt.toLocaleDateString('ru-RU', { day: 'numeric', month: 'long' }); } catch { return d; }
}

// --- стили ---
const cardStyle: CSSProperties = { background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.xl, boxShadow: SHADOW.card, padding: 16 };
const primaryBtn: CSSProperties = { display: 'inline-flex', alignItems: 'center', gap: 6, background: C.accent, color: C.onAccent, border: 'none', borderRadius: R.xl, padding: '8px 14px', fontSize: 13, fontWeight: 600, cursor: 'pointer', fontFamily: FONT.sans };
const ghostBtn: CSSProperties = { display: 'inline-flex', alignItems: 'center', gap: 6, background: 'transparent', color: C.textSecondary, border: `1px solid ${C.border}`, borderRadius: R.xl, padding: '8px 14px', fontSize: 13, fontWeight: 600, cursor: 'pointer', fontFamily: FONT.sans };
const linkBtn: CSSProperties = { background: 'transparent', border: 'none', color: C.accent, fontSize: 13, fontWeight: 600, cursor: 'pointer', fontFamily: FONT.sans, padding: '4px 8px' };
const memberCard: CSSProperties = { display: 'flex', alignItems: 'center', gap: 10, width: '100%', textAlign: 'left', background: C.bgWhite, border: `1px solid ${C.borderLight}`, borderRadius: R.lg, padding: '10px 12px', cursor: 'pointer', transition: 'border-color .12s, box-shadow .12s' };
const specialtyBadge: CSSProperties = { display: 'inline-block', fontSize: 10.5, fontWeight: 600, color: C.textSecondary, background: C.bgInset, borderRadius: R.sm, padding: '1px 7px', fontFamily: FONT.sans, marginTop: 3 };
const inputStyle: CSSProperties = { flex: 1, padding: '8px 10px', borderRadius: R.md, border: `1px solid ${C.border}`, background: C.bgWhite, color: C.textPrimary, fontFamily: FONT.sans, fontSize: 13, outline: 'none' };
const memRowStyle: CSSProperties = { display: 'flex', alignItems: 'center', gap: 10, background: C.bgWhite, border: `1px solid ${C.borderLight}`, borderLeft: `3px solid ${C.accentMuted}`, borderRadius: R.md, padding: '10px 12px' };
const filterBarStyle: CSSProperties = { position: 'sticky', top: 44, zIndex: 4, background: C.bgMain, padding: '8px 0 10px', borderBottom: `1px solid ${C.borderLight}` };
const filterChip: CSSProperties = { border: 'none', background: C.bgInset, color: C.textSecondary, borderRadius: R.sm, padding: '3px 9px', fontSize: 11.5, fontWeight: 600, cursor: 'pointer', fontFamily: FONT.sans };
const filterChipActive: CSSProperties = { ...filterChip, background: C.accentLight, color: C.accent };
const dayHeaderStyle: CSSProperties = { position: 'sticky', top: 88, zIndex: 3, background: C.bgMain, padding: '8px 0 6px', fontSize: 11, fontWeight: 700, textTransform: 'uppercase', letterSpacing: '0.04em', color: C.textMuted, fontFamily: FONT.sans, borderBottom: `1px solid ${C.borderLight}` };
const actorDot: CSSProperties = { width: 20, height: 20, borderRadius: '50%', background: C.bgInset, display: 'flex', alignItems: 'center', justifyContent: 'center', fontSize: 10, color: C.textSecondary, flexShrink: 0 };
const eventCardStyle: CSSProperties = { display: 'flex', alignItems: 'stretch', gap: 10, width: '100%', background: C.bgWhite, border: `1px solid ${C.borderLight}`, borderRadius: R.xl, boxShadow: SHADOW.card, padding: '10px 12px', transition: 'border-color .12s, box-shadow .12s' };
const chipIconStyle: CSSProperties = { width: 28, height: 28, borderRadius: R.md, display: 'flex', alignItems: 'center', justifyContent: 'center', flexShrink: 0 };
const actionLabelStyle: CSSProperties = { fontSize: 11, fontWeight: 700, color: C.textSecondary, fontFamily: FONT.sans, textTransform: 'uppercase', letterSpacing: '0.04em' };
const objTitleStyle: CSSProperties = { fontSize: 13.5, fontWeight: 600, color: C.textHeading, fontFamily: FONT.sans, flex: 1, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' };
const checkDot: CSSProperties = { width: 16, height: 16, borderRadius: '50%', flexShrink: 0 };
