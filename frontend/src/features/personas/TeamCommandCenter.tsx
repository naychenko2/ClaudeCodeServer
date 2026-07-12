import { useEffect, useState, type CSSProperties } from 'react';
import type { Project, TeamMemoryEntry } from '../../types';
import { api } from '../../lib/api';
import { usePersonas, personaLabel } from '../../lib/personas';
import { agentDotColor } from '../../components/AgentSelector';
import { C, FONT, R } from '../../lib/design';

// Командный центр проекта (①-L1): вид команды целиком — статус участников, активность-лента
// проекта (из лога событий F1) и общая память команды (③-3.4). Показывается отдельным режимом
// вкладки «Команда» (поверх сайдбара-списка персон). MVP: опрос эндпоинтов (realtime — позже).
//
// Событие лога — { ts, type, actor, summary }. actor=personaId резолвится в подпись персоной.
export function TeamCommandCenter({ project }: { project: Project }) {
  const personas = usePersonas();
  const team = personas.filter(p => p.scope === 'project' && p.projectId === project.id);
  const [events, setEvents] = useState<EventRow[] | null>(null);
  const [mem, setMem] = useState<TeamMemoryEntry[] | null>(null);
  const [newMem, setNewMem] = useState('');

  const refresh = async () => {
    try {
      const evs = await api.projects.events(project.id, { limit: 40 }) as unknown as EventRow[];
      setEvents(evs ?? []);
    } catch { setEvents([]); }
    try { setMem(await api.projects.teamMemory(project.id)); } catch { setMem([]); }
  };
  useEffect(() => { void refresh(); }, [project.id]);

  const addMem = async () => {
    const text = newMem.trim();
    if (!text) return;
    try {
      await api.projects.addTeamMemory(project.id, text);
      setNewMem('');
      setMem(await api.projects.teamMemory(project.id));
    } catch { /* тишина */ }
  };
  const removeMem = async (id: string) => {
    try {
      await api.projects.removeTeamMemory(project.id, id);
      setMem(prev => (prev ?? []).filter(m => m.id !== id));
    } catch { /* тишина */ }
  };

  const personaById = (id: string) => team.find(p => p.id === id);
  const actorLabel = (a: string) => {
    const p = personaById(a);
    return p ? personaLabel(p) : a === 'user' ? 'Вы' : a;
  };

  return (
    <div style={{ height: '100%', overflowY: 'auto', padding: '18px 20px', background: C.bgMain }}>
      <div style={{ maxWidth: 760, margin: '0 auto', display: 'flex', flexDirection: 'column', gap: 22 }}>
        <SectionTitle>Команда · {project.name}</SectionTitle>

        {/* Статус команды */}
        <div>
          <SectionLabel>Состав{team.length ? ` · ${team.length}` : ''}</SectionLabel>
          {team.length === 0 ? (
            <Muted>В проекте нет персон. Создайте членов команды в списке слева.</Muted>
          ) : (
            <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
              {team.map(p => (
                <div key={p.id} style={rowStyle}>
                  <span style={{ ...dotStyle, background: agentDotColor(p.avatar?.color) }} />
                  <span style={{ fontSize: 13.5, color: C.textPrimary, fontFamily: FONT.sans }}>{personaLabel(p)}</span>
                  {p.specialty && p.specialty !== 'none' && (
                    <span style={badgeStyle}>{SPECIALTY_LABEL[p.specialty] ?? p.specialty}</span>
                  )}
                </div>
              ))}
            </div>
          )}
        </div>

        {/* Память команды */}
        <div>
          <SectionLabel>Память команды</SectionLabel>
          <div style={{ fontSize: 12, color: C.textMuted, fontFamily: FONT.sans, marginBottom: 8 }}>
            Общие факты и договорённости проекта — их remember все персоны команды.
          </div>
          <div style={{ display: 'flex', gap: 8, marginBottom: 10 }}>
            <input
              value={newMem}
              onChange={e => setNewMem(e.target.value)}
              onKeyDown={e => { if (e.key === 'Enter') void addMem(); }}
              placeholder="Напр.: ревью идут через PR в main; релизы по пятницам"
              style={inputStyle}
            />
            <button onClick={() => void addMem()} disabled={!newMem.trim()} style={btnPrimary}>Добавить</button>
          </div>
          <div style={{ display: 'flex', flexDirection: 'column', gap: 5 }}>
            {mem === null ? <Muted>Загрузка…</Muted>
              : mem.length === 0 ? <Muted>Пока пусто.</Muted>
              : mem.map(m => (
                <div key={m.id} style={memRowStyle}>
                  <span style={{ fontSize: 13, color: C.textPrimary, fontFamily: FONT.sans, flex: 1 }}>{m.text}</span>
                  <button onClick={() => void removeMem(m.id)} aria-label="Удалить" style={iconBtnStyle}>×</button>
                </div>
              ))}
          </div>
        </div>

        {/* Активность проекта */}
        <div>
          <SectionLabel>Активность проекта</SectionLabel>
          {events === null ? <Muted>Загрузка…</Muted>
            : events.length === 0 ? <Muted>Пока нет событий.</Muted>
            : <div style={{ display: 'flex', flexDirection: 'column', gap: 4 }}>
                {events.map(e => (
                  <div key={e.id} style={evRowStyle}>
                    <span style={typeTagStyle}>{EVENT_LABEL[e.type] ?? e.type}</span>
                    <span style={{ fontSize: 13, color: C.textPrimary, fontFamily: FONT.sans, flex: 1 }}>
                      {e.summary}
                    </span>
                    <span style={{ fontSize: 11, color: C.textMuted, fontFamily: FONT.sans, flexShrink: 0 }}>
                      {actorLabel(e.actor)} · {shortAgo(e.ts)}
                    </span>
                  </div>
                ))}
              </div>}
        </div>
      </div>
    </div>
  );
}

function SectionTitle({ children }: { children: React.ReactNode }) {
  return <div style={{ fontFamily: FONT.serif, fontSize: 20, color: C.textHeading }}>{children}</div>;
}
function SectionLabel({ children }: { children: React.ReactNode }) {
  return <div style={{ fontSize: 11, fontWeight: 600, color: C.textSecondary, fontFamily: FONT.sans, textTransform: 'uppercase', letterSpacing: 0.4, marginBottom: 10 }}>{children}</div>;
}
function Muted({ children }: { children: React.ReactNode }) {
  return <div style={{ fontSize: 13, color: C.textMuted, fontFamily: FONT.sans }}>{children}</div>;
}

const rowStyle: CSSProperties = { display: 'flex', alignItems: 'center', gap: 10 };
const dotStyle: CSSProperties = { width: 12, height: 12, borderRadius: '50%', flexShrink: 0 };
const badgeStyle: CSSProperties = { fontSize: 10.5, fontWeight: 600, color: C.textSecondary, background: C.bgInset, borderRadius: R.sm, padding: '1px 7px', fontFamily: FONT.sans };
const memRowStyle: CSSProperties = { display: 'flex', alignItems: 'center', gap: 10, background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.md, padding: '7px 10px' };
const evRowStyle: CSSProperties = { display: 'flex', alignItems: 'center', gap: 10, padding: '5px 2px', borderBottom: `1px solid ${C.borderLight}` };
const typeTagStyle: CSSProperties = { fontSize: 10, fontWeight: 600, color: C.textSecondary, fontFamily: FONT.sans, background: C.bgInset, borderRadius: R.sm, padding: '1px 6px', flexShrink: 0, minWidth: 56, textAlign: 'center' };
const iconBtnStyle: CSSProperties = { border: 'none', background: 'transparent', color: C.textMuted, cursor: 'pointer', fontSize: 16, lineHeight: 1, padding: '2px 4px', flexShrink: 0 };
const inputStyle: CSSProperties = { flex: 1, padding: '7px 10px', borderRadius: R.md, border: `1px solid ${C.border}`, background: C.bgWhite, color: C.textPrimary, fontFamily: FONT.sans, fontSize: 13, outline: 'none' };
const btnPrimary: CSSProperties = { flexShrink: 0, background: C.accent, color: C.onAccent, border: 'none', borderRadius: R.md, padding: '7px 14px', fontSize: 13, fontWeight: 600, cursor: 'pointer', fontFamily: FONT.sans };

const SPECIALTY_LABEL: Record<string, string> = {
  analyst: 'Аналитик', planner: 'Планировщик', reviewer: 'Ревьюер', executor: 'Исполнитель',
  secretary: 'Секретарь', coordinator: 'Координатор', mentor: 'Ментор', designer: 'Дизайнер',
  consultant: 'Консультант', librarian: 'Библиотекарь',
};

const EVENT_LABEL: Record<string, string> = {
  chat_turn: 'чат', task_created: 'задача', task_completed: 'готово', task_spawned: 'регуляр.',
  task_deleted: 'удалено', memory_learned: 'память', knowledge_changed: 'база', note_changed: 'заметка',
  team_joined: '+ команда', team_left: '− команда', meeting: 'совещание', pipeline: 'конвейер',
};

type EventRow = { id: number; ts: string; type: string; actor: string; summary: string };

function shortAgo(iso: string): string {
  const t = new Date(iso).getTime();
  if (!Number.isFinite(t)) return '';
  const mins = Math.floor((Date.now() - t) / 60000);
  if (mins < 1) return 'только что';
  if (mins < 60) return `${mins} мин`;
  const hrs = Math.floor(mins / 60);
  if (hrs < 24) return `${hrs} ч`;
  const days = Math.floor(hrs / 24);
  return `${days} д`;
}
