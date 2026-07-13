import { useEffect, useMemo, useState, type CSSProperties } from 'react';
import {
  Users, MessageSquare, Mic, Workflow, Plus, CheckCircle2, Repeat, Trash2,
  Brain, BookOpen, FileText, UserPlus, UserMinus, ChevronRight, MoreHorizontal, Settings,
} from 'lucide-react';
import type { ComponentType } from 'react';
import type { Persona, Project, Session, Task, TeamMemoryEntry } from '../../types';
import { api } from '../../lib/api';
import { usePersonas, personaLabel } from '../../lib/personas';
import { projectColor } from '../../lib/tasks';
import { C, FONT, R, SHADOW } from '../../lib/design';
import { PersonaAvatar } from './PersonaAvatar';
import { Modal, IconButton } from '../../components/ui';
import { NewTaskDialog } from '../tasks/NewTaskDialog';

// Командный центр проекта (①-L1, редизайн): приборная панель команды — hero со сводкой
// live-статуса и командными действиями, сетка состава/памяти, кликабельная лента активности
// по дням с фильтрами. Каждое событие с entityRef ведёт в свой раздел.
export function TeamCommandCenter({
  project,
  onOpenPersona, onNewPersona, onOpenSession, onOpenSessionById,
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
  const [newMem, setNewMem] = useState('');
  const [filter, setFilter] = useState<FilterKey>('all');
  const [limit, setLimit] = useState(40);
  const [pickerOpen, setPickerOpen] = useState(false);
  const [newTaskOpen, setNewTaskOpen] = useState(false);
  const [menuOpen, setMenuOpen] = useState(false);

  const refresh = async () => {
    try { setEvents((await api.projects.events(project.id, { limit })) as unknown as EventRow[] ?? []); }
    catch { setEvents([]); }
    try { setMem(await api.projects.teamMemory(project.id)); } catch { setMem([]); }
    try { setTasks(await api.tasks.listByProject(project.id)); } catch { setTasks([]); }
  };
  useEffect(() => { void refresh(); /* eslint-disable-next-line */ }, [project.id, limit]);

  // --- live-статус ---
  const inFlight = useMemo(() => {
    const m = new Map<string, string>();
    for (const t of tasks ?? []) if (t.personaId && t.claudeStartedAt && !t.claudeResult && t.status !== 'done') m.set(t.personaId, t.title);
    return m;
  }, [tasks]);
  const onlineSet = useMemo(() => {
    const s = new Set<string>();
    const now = Date.now();
    for (const e of events ?? []) {
      if (e.type !== 'chat_turn' || !e.actor || e.actor === 'user' || e.actor === 'system') continue;
      if (now - new Date(e.ts).getTime() < 10 * 60_000) s.add(e.actor);
    }
    return s;
  }, [events]);
  const live = useMemo(() => {
    const now = Date.now();
    let last: EventRow | null = null;
    for (const e of events ?? []) {
      if (e.type === 'meeting' || e.type === 'pipeline') {
        if (now - new Date(e.ts).getTime() < 30 * 60_000 && (!last || e.ts > last.ts)) last = e;
      }
    }
    return last; // MVP: последнее meeting/pipeline за 30 мин — считаем идущим
  }, [events]);
  const tasksActive = (tasks ?? []).filter(t => t.status !== 'done').length;
  const chatsToday = useMemo(() => {
    const today = new Date().toISOString().slice(0, 10);
    const s = new Set<string>();
    for (const e of events ?? []) if (e.type === 'chat_turn' && e.ts.slice(0, 10) === today && e.entityRef) s.add(e.entityRef);
    return s.size;
  }, [events]);

  const personaById = (id: string) => team.find(p => p.id === id);
  const addMem = async () => {
    const text = newMem.trim(); if (!text) return;
    try { await api.projects.addTeamMemory(project.id, text); setNewMem(''); setMem(await api.projects.teamMemory(project.id)); } catch { /* тишина */ }
  };
  const removeMem = async (id: string) => {
    try { await api.projects.removeTeamMemory(project.id, id); setMem(prev => (prev ?? []).filter(m => m.id !== id)); } catch { /* тишина */ }
  };

  const filtered = useMemo(() => {
    const list = events ?? [];
    if (filter === 'all') return list;
    return list.filter(e => CATEGORY[e.type] === filter);
  }, [events, filter]);

  const stripe = projectColor(project.id).main;
  const isMobile = useIsMobile();

  return (
    <div style={{ height: '100%', overflowY: 'auto', background: C.bgMain, padding: isMobile ? '16px 14px 24px' : '20px 24px 32px' }}>
      <div style={{ maxWidth: 920, margin: '0 auto', display: 'flex', flexDirection: 'column', gap: 16 }}>
        {/* HERO */}
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
              {live.entityRef && (
                <button onClick={() => onOpenSessionById(live.entityRef!)} style={linkBtn}>Открыть →</button>
              )}
            </div>
          )}

          {/* ActionsRow */}
          <div style={{ display: 'flex', gap: 8, flexWrap: 'wrap', marginTop: 12 }}>
            <button onClick={() => setPickerOpen(true)} disabled={team.length < 2} style={primaryBtn}>
              <MessageSquare size={15} /> Созвать команду
            </button>
            <button onClick={() => setNewTaskOpen(true)} style={ghostBtn}><Plus size={15} /> Новая задача</button>
            <button onClick={onNewPersona} style={ghostBtn}><UserPlus size={15} /> Новая персона</button>
            <div style={{ position: 'relative' }}>
              <IconButton variant="soft" size="sm" title="Ещё" onClick={() => setMenuOpen(v => !v)}><MoreHorizontal size={16} /></IconButton>
              {menuOpen && (
                <div style={menuStyle}>
                  <button style={menuItem} onClick={() => { setMenuOpen(false); createTeamChat(team, onOpenSession); }}>Создать командный чат</button>
                </div>
              )}
            </div>
          </div>
        </div>

        {/* Пустая команда */}
        {team.length === 0 ? (
          <div style={{ ...cardStyle, display: 'flex', flexDirection: 'column', alignItems: 'center', gap: 12, padding: '40px 24px', textAlign: 'center' }}>
            <Users size={36} color={C.textMuted} style={{ opacity: 0.5 }} />
            <div style={{ fontFamily: FONT.serif, fontSize: 16, color: C.textHeading }}>В этом проекте ещё нет команды</div>
            <div style={{ fontSize: 13, color: C.textMuted, fontFamily: FONT.sans, maxWidth: 340, lineHeight: 1.5 }}>
              Создайте персон — задайте роль, характер и аватар. Они начнут работать вместе: чаты, задачи, совещания и конвейеры.
            </div>
            <button onClick={onNewPersona} style={primaryBtn}><UserPlus size={15} /> Новая персона</button>
          </div>
        ) : (
          <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(320px, 1fr))', gap: 16 }}>
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
                        {p.specialty && p.specialty !== 'none' && (
                          <span style={specialtyBadge}>{SPECIALTY_LABEL[p.specialty] ?? p.specialty}</span>
                        )}
                      </div>
                      <StatusDot status={status} />
                    </button>
                  );
                })}
              </div>
            </div>

            {/* Память команды */}
            <div style={cardStyle}>
              <SectionLabel>Память команды{mem && mem.length ? ` · ${mem.length}` : ''}</SectionLabel>
              <div style={{ fontSize: 12, color: C.textMuted, fontFamily: FONT.sans, margin: '8px 0 10px' }}>
                Общие факты — их remember все персоны команды.
              </div>
              <div style={{ display: 'flex', gap: 8, marginBottom: 10 }}>
                <input value={newMem} onChange={e => setNewMem(e.target.value)} onKeyDown={e => { if (e.key === 'Enter') void addMem(); }}
                  placeholder="Напр.: ревью через PR; релизы по пятницам" style={inputStyle} />
                <button onClick={() => void addMem()} disabled={!newMem.trim()} style={primaryBtn}>Добавить</button>
              </div>
              <div style={{ display: 'flex', flexDirection: 'column', gap: 5 }}>
                {mem === null ? <Muted>Загрузка…</Muted>
                  : mem.length === 0 ? <Muted>Пока пусто. Запишите общий факт.</Muted>
                  : mem.map(m => (
                    <div key={m.id} style={memRowStyle}>
                      <span style={{ fontSize: 13, color: C.textPrimary, fontFamily: FONT.sans, flex: 1 }}>{m.text}</span>
                      <button onClick={() => void removeMem(m.id)} aria-label="Удалить" style={iconBtnStyle}>×</button>
                    </div>
                  ))}
              </div>
            </div>
          </div>
        )}

        {/* Активность */}
        <div style={cardStyle}>
          <div style={{ display: 'flex', alignItems: 'center', gap: 8, flexWrap: 'wrap', marginBottom: 8 }}>
            <SectionLabel>Активность проекта</SectionLabel>
            <div style={{ marginLeft: 'auto', display: 'flex', gap: 5, flexWrap: 'wrap' }}>
              {FILTERS.map(f => (
                <button key={f.key} onClick={() => setFilter(f.key)} style={filter === f.key ? filterChipActive : filterChip}>{f.label}</button>
              ))}
            </div>
          </div>
          {events === null ? <Muted>Загрузка…</Muted>
            : filtered.length === 0 ? <Muted>Пока нет событий.</Muted>
            : <DayGroups events={filtered} personaById={personaById} onOpen={(e) => onEventClick(e, project.id, onOpenSessionById, onOpenPersona)} />}
          {events && events.length >= limit && (
            <button onClick={() => setLimit(n => n + 40)} style={{ ...linkBtn, margin: '10px auto 0', display: 'block' }}>Показать ещё</button>
          )}
        </div>
      </div>

      {pickerOpen && (
        <GroupChatPicker team={team} onClose={() => setPickerOpen(false)}
          onCreated={s => { setPickerOpen(false); onOpenSession(s); }} />
      )}
      {newTaskOpen && (
        <NewTaskDialog defaultProjectId={project.id} onCreated={() => setNewTaskOpen(false)} onClose={() => setNewTaskOpen(false)} />
      )}
    </div>
  );
}

// --- кликабельность активности ---
type EventRow = { id: number; ts: string; type: string; actor: string; summary: string; entityRef?: string | null };
type Nav = 'session' | 'task' | 'persona' | 'note' | 'knowledge' | null;
function eventTarget(e: EventRow): { nav: Nav; id: string } | null {
  if (!e.entityRef) return null;
  switch (e.type) {
    case 'chat_turn': case 'meeting': case 'pipeline': return { nav: 'session', id: e.entityRef };
    case 'task_created': case 'task_completed': case 'task_spawned': return { nav: 'task', id: e.entityRef };
    case 'memory_learned': return { nav: 'persona', id: e.entityRef };
    case 'team_joined': return { nav: 'persona', id: e.entityRef };
    case 'note_changed': return { nav: 'note', id: e.entityRef };
    case 'knowledge_changed': return { nav: 'knowledge', id: e.entityRef };
    default: return null; // task_deleted, team_left — не кликабельно
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

// --- мета событий ---
type FilterKey = 'all' | 'tasks' | 'memory' | 'chats' | 'content' | 'team';
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
const EVENT_META: Record<string, { Icon: ComponentType<{ size?: number; color?: string }>; color: string }> = {
  chat_turn: { Icon: MessageSquare, color: C.accent },
  meeting: { Icon: Mic, color: C.accent },
  pipeline: { Icon: Workflow, color: C.accent },
  task_created: { Icon: Plus, color: C.success },
  task_completed: { Icon: CheckCircle2, color: C.success },
  task_spawned: { Icon: Repeat, color: C.success },
  task_deleted: { Icon: Trash2, color: C.danger },
  memory_learned: { Icon: Brain, color: C.info },
  knowledge_changed: { Icon: BookOpen, color: C.info },
  note_changed: { Icon: FileText, color: C.info },
  team_joined: { Icon: UserPlus, color: C.textSecondary },
  team_left: { Icon: UserMinus, color: C.textSecondary },
};
const SPECIALTY_LABEL: Record<string, string> = {
  analyst: 'Аналитик', planner: 'Планировщик', reviewer: 'Ревьюер', executor: 'Исполнитель',
  secretary: 'Секретарь', coordinator: 'Координатор', mentor: 'Ментор', designer: 'Дизайнер',
  consultant: 'Консультант', librarian: 'Библиотекарь',
};

// ---DayGroups: группировка по дням ---
function DayGroups({ events, personaById, onOpen }: {
  events: EventRow[];
  personaById: (id: string) => Persona | undefined;
  onOpen: (e: EventRow) => void;
}) {
  const groups = useMemo(() => {
    const today = new Date().toISOString().slice(0, 10);
    const yest = new Date(Date.now() - 86_400_000).toISOString().slice(0, 10);
    const m = new Map<string, EventRow[]>();
    for (const e of events) {
      const d = e.ts.slice(0, 10);
      const label = d === today ? 'Сегодня' : d === yest ? 'Вчера' : d;
      (m.get(label) ?? m.set(label, []).get(label)!).push(e);
    }
    return [...m.entries()];
  }, [events]);
  return (
    <div>
      {groups.map(([label, rows]) => (
        <div key={label}>
          <div style={dayHeaderStyle}>{label}</div>
          {rows.map(e => <EventRowView key={e.id} e={e} personaById={personaById} onOpen={onOpen} />)}
        </div>
      ))}
    </div>
  );
}

function EventRowView({ e, personaById, onOpen }: {
  e: EventRow; personaById: (id: string) => Persona | undefined; onOpen: (e: EventRow) => void;
}) {
  const target = eventTarget(e);
  const meta = EVENT_META[e.type] ?? { Icon: Settings, color: C.textMuted };
  const p = personaById(e.actor);
  const clickable = !!target;
  return (
    <button onClick={() => clickable && onOpen(e)} disabled={!clickable} style={clickable ? evRowClickable : evRowStatic}>
      <meta.Icon size={15} color={meta.color} />
      {p ? <PersonaAvatar persona={p} size={20} /> : <span style={actorDot}>{e.actor === 'user' ? 'Я' : <Settings size={13} color={C.textMuted} />}</span>}
      <span style={{ fontSize: 13, color: C.textPrimary, fontFamily: FONT.sans, flex: 1, textAlign: 'left', overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{e.summary}</span>
      <span style={{ fontSize: 11, color: C.textMuted, fontFamily: FONT.sans, flexShrink: 0 }}>{shortAgo(e.ts)}</span>
      {clickable && <ChevronRight size={14} color={C.border} />}
    </button>
  );
}

// --- GroupChatPicker ---
function GroupChatPicker({ team, onClose, onCreated }: {
  team: Persona[]; onClose: () => void; onCreated: (s: Session) => void;
}) {
  const [sel, setSel] = useState<string[]>(team.length >= 2 ? [team[0].id, team[1].id] : team.map(p => p.id));
  const [busy, setBusy] = useState(false);
  const toggle = (id: string) => setSel(prev => prev.includes(id) ? prev.filter(x => x !== id) : prev.length < 4 ? [...prev, id] : prev);
  const create = async () => {
    setBusy(true);
    try { const s = await api.chats.createGroup(sel); onCreated(s); } finally { setBusy(false); }
  };
  return (
    <Modal width={420} title="Созвать команду" subtitle="Выберите 2–4 участниц. Первая — ведущая." onClose={onClose}
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
                {i === 0 && sel[0] === p.id && <span style={{ fontSize: 11, color: C.accent, fontFamily: FONT.sans }}>ведущая</span>}
              </div>
              <span style={{ ...checkDot, background: checked ? C.accent : 'transparent', border: `2px solid ${checked ? C.accent : C.border}` }} />
            </button>
          );
        })}
      </div>
    </Modal>
  );
}

async function createTeamChat(team: Persona[], onOpenSession: (s: Session) => void) {
  try { const s = await api.chats.createGroup(team.slice(0, 4).map(p => p.id)); onOpenSession(s); }
  catch { /* тишина */ }
}

// --- мелкие компоненты ---
function StatusChip({ children, color, dot }: { children: React.ReactNode; color: string; dot?: boolean }) {
  return (
    <span style={{ display: 'flex', alignItems: 'center', gap: 5, fontSize: 12, fontWeight: 600, fontFamily: FONT.sans, color }}>
      {dot && <span style={{ width: 7, height: 7, borderRadius: '50%', background: color }} />}
      {children}
    </span>
  );
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
function useIsMobile(): boolean {
  const [m, setM] = useState(() => typeof window !== 'undefined' && window.matchMedia('(max-width: 767px)').matches);
  useEffect(() => {
    const mq = window.matchMedia('(max-width: 767px)');
    const h = (e: MediaQueryListEvent) => setM(e.matches);
    mq.addEventListener('change', h); return () => mq.removeEventListener('change', h);
  }, []);
  return m;
}
function shortAgo(iso: string): string {
  const t = new Date(iso).getTime(); if (!Number.isFinite(t)) return '';
  const mins = Math.floor((Date.now() - t) / 60000);
  if (mins < 1) return 'только что'; if (mins < 60) return `${mins} мин`;
  const hrs = Math.floor(mins / 60); if (hrs < 24) return `${hrs} ч`;
  return `${Math.floor(hrs / 24)} д`;
}

// --- стили ---
const cardStyle: CSSProperties = { background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.xl, boxShadow: SHADOW.card, padding: 16 };
const primaryBtn: CSSProperties = { display: 'inline-flex', alignItems: 'center', gap: 6, background: C.accent, color: C.onAccent, border: 'none', borderRadius: R.xl, padding: '8px 14px', fontSize: 13, fontWeight: 600, cursor: 'pointer', fontFamily: FONT.sans };
const ghostBtn: CSSProperties = { display: 'inline-flex', alignItems: 'center', gap: 6, background: 'transparent', color: C.textSecondary, border: `1px solid ${C.border}`, borderRadius: R.xl, padding: '8px 14px', fontSize: 13, fontWeight: 600, cursor: 'pointer', fontFamily: FONT.sans };
const linkBtn: CSSProperties = { background: 'transparent', border: 'none', color: C.accent, fontSize: 13, fontWeight: 600, cursor: 'pointer', fontFamily: FONT.sans, padding: '4px 8px' };
const memberCard: CSSProperties = { display: 'flex', alignItems: 'center', gap: 10, width: '100%', textAlign: 'left', background: C.bgWhite, border: `1px solid ${C.borderLight}`, borderRadius: R.lg, padding: '10px 12px', cursor: 'pointer', transition: 'border-color .12s, box-shadow .12s' };
const specialtyBadge: CSSProperties = { display: 'inline-block', fontSize: 10.5, fontWeight: 600, color: C.textSecondary, background: C.bgInset, borderRadius: R.sm, padding: '1px 7px', fontFamily: FONT.sans, marginTop: 3 };
const inputStyle: CSSProperties = { flex: 1, padding: '7px 10px', borderRadius: R.md, border: `1px solid ${C.border}`, background: C.bgWhite, color: C.textPrimary, fontFamily: FONT.sans, fontSize: 13, outline: 'none' };
const memRowStyle: CSSProperties = { display: 'flex', alignItems: 'center', gap: 10, background: C.bgWhite, border: `1px solid ${C.borderLight}`, borderLeft: `3px solid ${C.accentMuted}`, borderRadius: R.md, padding: '7px 10px' };
const iconBtnStyle: CSSProperties = { border: 'none', background: 'transparent', color: C.textMuted, cursor: 'pointer', fontSize: 16, lineHeight: 1, padding: '2px 4px', flexShrink: 0 };
const evRowClickable: CSSProperties = { display: 'flex', alignItems: 'center', gap: 10, width: '100%', padding: '7px 4px', border: 'none', background: 'transparent', cursor: 'pointer', borderRadius: R.sm };
const evRowStatic: CSSProperties = { ...evRowClickable, cursor: 'default' };
const actorDot: CSSProperties = { width: 20, height: 20, borderRadius: '50%', background: C.bgInset, display: 'flex', alignItems: 'center', justifyContent: 'center', fontSize: 10, color: C.textSecondary, flexShrink: 0 };
const dayHeaderStyle: CSSProperties = { position: 'sticky', top: 0, background: C.bgWhite, padding: '8px 0 4px', fontSize: 11, fontWeight: 700, textTransform: 'uppercase', letterSpacing: 0.04, color: C.textMuted, fontFamily: FONT.sans, borderBottom: `1px solid ${C.borderLight}` };
const filterChip: CSSProperties = { border: 'none', background: C.bgInset, color: C.textSecondary, borderRadius: R.sm, padding: '2px 8px', fontSize: 11, fontWeight: 600, cursor: 'pointer', fontFamily: FONT.sans };
const filterChipActive: CSSProperties = { ...filterChip, background: C.accentLight, color: C.accent };
const menuStyle: CSSProperties = { position: 'absolute', top: '100%', right: 0, marginTop: 4, background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.md, boxShadow: SHADOW.dropdown, padding: 4, zIndex: 50, minWidth: 200 };
const menuItem: CSSProperties = { display: 'block', width: '100%', textAlign: 'left', border: 'none', background: 'transparent', color: C.textPrimary, fontFamily: FONT.sans, fontSize: 13, padding: '7px 10px', borderRadius: R.sm, cursor: 'pointer' };
const checkDot: CSSProperties = { width: 16, height: 16, borderRadius: '50%', flexShrink: 0 };
