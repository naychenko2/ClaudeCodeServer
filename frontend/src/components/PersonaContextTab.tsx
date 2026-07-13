import { useEffect, useState, type CSSProperties } from 'react';
import { api } from '../lib/api';
import { usePersonas, personaLabel } from '../lib/personas';
import { useRecallManifest } from '../lib/recallManifest';
import type { PersonaBinding, PersonaMemoryEntry, PersonaMemoryType, Task } from '../types';
import { C, FONT, R } from '../lib/design';
import { agentDotColor } from './AgentSelector';

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

  const dot = agentDotColor(persona.avatar?.color);
  const memory = (mem ?? []).slice(0, 6);
  const active = (tasks ?? []).filter(t => t.status !== 'done').slice(0, 6);
  const knowledge = (persona.bindings ?? []).filter(b =>
    b.type === 'knowledge' || b.type === 'notes' || b.type === 'project' || b.type === 'projectPath');

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 16 }}>
      {/* Шапка персоны */}
      <div style={{ display: 'flex', alignItems: 'center', gap: 10 }}>
        <span style={{ ...dotStyle, background: dot } as CSSProperties} />
        <div style={{ display: 'flex', flexDirection: 'column' }}>
          <span style={{ fontFamily: FONT.serif, fontSize: 15, color: C.textHeading }}>{personaLabel(persona)}</span>
          {persona.description ? (
            <span style={{ fontSize: 12, color: C.textMuted, fontFamily: FONT.sans }}>{persona.description}</span>
          ) : null}
        </div>
      </div>

      {usedNow.length > 0 && (
        <Section title="Использовано сейчас">
          {usedNow.map((it, i) => (
            <div key={it.ref ?? i} style={rowStyle}>
              <span style={typeBadgeStyle}>{it.kind === 'memory' ? '🧠' : it.kind === 'note' ? '📚' : '📖'}</span>
              <span style={rowTextStyle}>{it.title}</span>
            </div>
          ))}
        </Section>
      )}

      <Section title={`Память${mem && mem.length ? ` · ${mem.length}` : ''}`}>
        {memory.length === 0 ? (
          mem === null ? <Skeleton /> : <Muted>Пока ничего не запомнено.</Muted>
        ) : (
          memory.map(m => (
            <div key={m.id} style={rowStyle}>
              <span style={typeBadgeStyle}>{typeLabel(m.type)}</span>
              <span style={rowTextStyle}>{m.text}</span>
            </div>
          ))
        )}
      </Section>

      <Section title={`Знает${knowledge.length ? ` · ${knowledge.length}` : ''}`}>
        {knowledge.length === 0 ? <Muted>Нет привязанных источников.</Muted> : knowledge.map(b => (
          <div key={b.id} style={rowStyle}>
            <span style={typeBadgeStyle}>{bindingLabel(b)}</span>
            <span style={rowTextStyle}>{b.condition || bindingTarget(b)}</span>
          </div>
        ))}
      </Section>

      <Section title={`Задачи${active.length ? ` · ${active.length}` : ''}`}>
        {active.length === 0 ? (
          tasks === null ? <Skeleton /> : <Muted>Нет активных задач.</Muted>
        ) : (
          active.map(t => (
            <div key={t.id} style={rowStyle}>
              <span style={{ ...typeBadgeStyle, background: C.accentSoft, color: C.accent }}>●</span>
              <span style={rowTextStyle}>
                {t.title}{t.dueDate ? <span style={{ color: C.textMuted }}> · до {t.dueDate}</span> : null}
              </span>
            </div>
          ))
        )}
      </Section>
    </div>
  );
}

function Section({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <div>
      <div style={{ fontSize: 11, fontWeight: 600, color: C.textSecondary, fontFamily: FONT.sans, textTransform: 'uppercase', letterSpacing: 0.4, marginBottom: 8 }}>{title}</div>
      <div style={{ display: 'flex', flexDirection: 'column', gap: 7 }}>{children}</div>
    </div>
  );
}

function Muted({ children }: { children: React.ReactNode }) {
  return <div style={{ fontSize: 12.5, color: C.textMuted, fontFamily: FONT.sans }}>{children}</div>;
}

function Skeleton() {
  return <div style={{ fontSize: 12.5, color: C.textMuted, fontFamily: FONT.sans }}>Загрузка…</div>;
}

const emptyStyle: CSSProperties = { padding: 16, color: C.textMuted, fontFamily: FONT.sans, fontSize: 13 };
const dotStyle: CSSProperties = { width: 14, height: 14, borderRadius: '50%', flexShrink: 0 };
const rowStyle: CSSProperties = { display: 'flex', alignItems: 'flex-start', gap: 8 };
const rowTextStyle: CSSProperties = { fontSize: 12.5, color: C.textPrimary, fontFamily: FONT.sans, lineHeight: 1.45, flex: 1, minWidth: 0 };
const typeBadgeStyle: CSSProperties = {
  fontSize: 10, fontWeight: 600, color: C.textSecondary, fontFamily: FONT.sans,
  background: C.bgInset, borderRadius: R.sm, padding: '1px 6px', flexShrink: 0, marginTop: 1, minWidth: 18, textAlign: 'center',
};

function typeLabel(t: PersonaMemoryType): string {
  return t === 'semantic' ? '🧠' : t === 'episodic' ? '📅' : '⚙️';
}

function bindingLabel(b: PersonaBinding): string {
  return b.type === 'knowledge' ? 'база' : b.type === 'notes' ? 'заметки' : b.type === 'project' ? 'проект' : 'путь';
}

function bindingTarget(b: PersonaBinding): string {
  return b.target || bindingLabel(b);
}
