import { useEffect, useState, type CSSProperties } from 'react';
import { Brain, Calendar, Cog, BookOpen, FileText, Folder } from 'lucide-react';
import { api } from '../lib/api';
import { usePersonas, personaLabel } from '../lib/personas';
import { useRecallManifest } from '../lib/recallManifest';
import type { PersonaBinding, PersonaMemoryEntry, PersonaMemoryType, Task } from '../types';
import { C, FONT, R, SHADOW } from '../lib/design';
import { PersonaAvatar } from '../features/personas/PersonaAvatar';

// Вкладка «Контекст персоны» в ArtifactsPanel (①-L2a + ①-L2b): показывает рядом с чатом то,
// что делает персону «не stateless» — долгую память, привязанные знания и активные задачи,
// а также «использовано сейчас» — что персона подтянула в последний ход (манифест recall, F3).
export function PersonaContextTab({ personaId, sessionId }: { personaId: string; sessionId?: string | null }) {
  const personas = usePersonas();
  const persona = personas.find(p => p.id === personaId);
  const usedNow = useRecallManifest(sessionId ?? null);
  const [mem, setMem] = useState<PersonaMemoryEntry[] | null>(null);
  const [tasks, setTasks] = useState<Task[] | null>(null);

  useEffect(() => {
    let alive = true;
    setMem(null); setTasks(null);
    api.personas.memory(personaId).then(m => { if (alive) setMem(m); }).catch(() => { if (alive) setMem([]); });
    api.tasks.listByPersona(personaId).then(t => { if (alive) setTasks(t); }).catch(() => { if (alive) setTasks([]); });
    return () => { alive = false; };
  }, [personaId]);

  if (!persona) {
    return <div style={emptyStyle}>Персона не найдена.</div>;
  }

  const memory = (mem ?? []).slice(0, 6);
  const active = (tasks ?? []).filter(t => t.status !== 'done').slice(0, 6);
  const knowledge = (persona.bindings ?? []).filter(b =>
    b.type === 'knowledge' || b.type === 'notes' || b.type === 'project' || b.type === 'projectPath');

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 12, padding: '4px 2px' }}>
      {/* Шапка персоны */}
      <div style={{ display: 'flex', alignItems: 'center', gap: 12, padding: '4px 2px 8px' }}>
        <PersonaAvatar persona={persona} size={40} />
        <div style={{ display: 'flex', flexDirection: 'column', minWidth: 0 }}>
          <span style={{ fontFamily: FONT.serif, fontSize: 15, color: C.textHeading, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{personaLabel(persona)}</span>
          {persona.description ? (
            <span style={{ fontSize: 12, color: C.textMuted, fontFamily: FONT.sans, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{persona.description}</span>
          ) : null}
        </div>
      </div>

      {usedNow.length > 0 && (
        <Section title="Использовано сейчас">
          {usedNow.map((it, i) => (
            <Row key={it.ref ?? i} icon={it.kind === 'memory' ? <Brain size={13} color={C.info} /> : it.kind === 'note' ? <BookOpen size={13} color={C.info} /> : <FileText size={13} color={C.info} />}>
              {it.title}
            </Row>
          ))}
        </Section>
      )}

      <Section title={`Память${mem && mem.length ? ` · ${mem.length}` : ''}`}>
        {memory.length === 0 ? (
          mem === null ? <Skeleton /> : <Muted>Пока ничего не запомнено.</Muted>
        ) : (
          memory.map(m => (
            <Row key={m.id} icon={memoryIcon(m.type)}>{m.text}</Row>
          ))
        )}
      </Section>

      <Section title={`Знает${knowledge.length ? ` · ${knowledge.length}` : ''}`}>
        {knowledge.length === 0 ? <Muted>Нет привязанных источников.</Muted> : knowledge.map(b => (
          <Row key={b.id} icon={bindingIcon(b.type)}>{b.condition || bindingLabel(b)}</Row>
        ))}
      </Section>

      <Section title={`Задачи${active.length ? ` · ${active.length}` : ''}`}>
        {active.length === 0 ? (
          tasks === null ? <Skeleton /> : <Muted>Нет активных задач.</Muted>
        ) : (
          active.map(t => (
            <Row key={t.id} icon={<span style={{ width: 8, height: 8, borderRadius: '50%', background: C.accent, flexShrink: 0, marginTop: 4 }} />}>
              {t.title}{t.dueDate ? <span style={{ color: C.textMuted }}> · до {t.dueDate}</span> : null}
            </Row>
          ))
        )}
      </Section>
    </div>
  );
}

function memoryIcon(t: PersonaMemoryType) {
  if (t === 'semantic') return <Brain size={13} color={C.info} />;
  if (t === 'episodic') return <Calendar size={13} color={C.info} />;
  return <Cog size={13} color={C.info} />;
}
function bindingIcon(type: string) {
  if (type === 'knowledge') return <BookOpen size={13} color={C.textMuted} />;
  if (type === 'notes') return <FileText size={13} color={C.textMuted} />;
  return <Folder size={13} color={C.textMuted} />;
}
function bindingLabel(b: PersonaBinding): string {
  return b.type === 'knowledge' ? 'база' : b.type === 'notes' ? 'заметки' : b.type === 'project' ? 'проект' : 'путь';
}

function Section({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <div style={sectionCard}>
      <div style={sectionTitle}>{title}</div>
      <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>{children}</div>
    </div>
  );
}
function Row({ icon, children }: { icon: React.ReactNode; children: React.ReactNode }) {
  return (
    <div style={{ display: 'flex', alignItems: 'flex-start', gap: 8 }}>
      <span style={{ flexShrink: 0, marginTop: 2, display: 'flex' }}>{icon}</span>
      <span style={rowTextStyle}>{children}</span>
    </div>
  );
}
function Muted({ children }: { children: React.ReactNode }) {
  return <div style={{ fontSize: 12.5, color: C.textMuted, fontFamily: FONT.sans }}>{children}</div>;
}
function Skeleton() {
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 5 }}>
      <div style={skeletonBar(70)} />
      <div style={skeletonBar(90)} />
    </div>
  );
}

const emptyStyle: CSSProperties = { padding: 16, color: C.textMuted, fontFamily: FONT.sans, fontSize: 13 };
const sectionCard: CSSProperties = { background: C.bgWhite, border: `1px solid ${C.borderLight}`, borderRadius: R.xl, boxShadow: SHADOW.card, padding: '12px 14px' };
const sectionTitle: CSSProperties = { fontSize: 11, fontWeight: 700, color: C.textSecondary, fontFamily: FONT.sans, textTransform: 'uppercase', letterSpacing: '0.04em', marginBottom: 8 };
const rowTextStyle: CSSProperties = { fontSize: 12.5, color: C.textPrimary, fontFamily: FONT.sans, lineHeight: 1.45, flex: 1, minWidth: 0 };
function skeletonBar(w: number): CSSProperties {
  return { height: 11, width: `${w}%`, background: C.bgInset, borderRadius: R.sm, opacity: 0.6 };
}
