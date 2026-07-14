import { useEffect, useState, type CSSProperties } from 'react';
import { Brain, Calendar, Cog, BookOpen, FileText, Users } from 'lucide-react';
import { api } from '../lib/api';
import { usePersonas, personaLabel } from '../lib/personas';
import { useRecallManifest } from '../lib/recallManifest';
import type { PersonaBinding, PersonaMemoryEntry, PersonaMemoryType, Task } from '../types';
import { C, FONT, R, SHADOW } from '../lib/design';
import { PersonaAvatar } from '../features/personas/PersonaAvatar';
import { BINDING_ICONS, useBindingLabels } from '../features/personas/bindingMeta';

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

  const memoryAll = mem ?? [];
  const activeAll = (tasks ?? []).filter(t => t.status !== 'done');
  // Все 6 типов привязок (не только источники знаний — Tool/Skill тоже «то, что персона знает»)
  const bindings = persona.bindings ?? [];
  const resolveBindingLabel = useBindingLabels(bindings);

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
            <Row key={it.ref ?? i} icon={recallIcon(it.kind)}>
              {it.title}
            </Row>
          ))}
        </Section>
      )}

      <Section title={`Память${memoryAll.length ? ` · ${memoryAll.length}` : ''}`}>
        {memoryAll.length === 0 ? (
          mem === null ? <Skeleton /> : <Muted>Пока ничего не запомнено.</Muted>
        ) : (
          <ExpandableRows items={memoryAll} previewCount={5}
            renderRow={m => <Row key={m.id} icon={memoryIcon(m.type)}>{m.text}</Row>} />
        )}
      </Section>

      <Section title={`Знает${bindings.length ? ` · ${bindings.length}` : ''}`}>
        {bindings.length === 0 ? <Muted>Нет привязанных источников и правил.</Muted> : (
          <ExpandableRows items={bindings} previewCount={5}
            renderRow={b => <Row key={b.id} icon={bindingIcon(b.type)}>{b.condition || resolveBindingLabel(b)}</Row>} />
        )}
      </Section>

      <Section title={`Задачи${activeAll.length ? ` · ${activeAll.length}` : ''}`}>
        {activeAll.length === 0 ? (
          tasks === null ? <Skeleton /> : <Muted>Нет активных задач.</Muted>
        ) : (
          <ExpandableRows items={activeAll} previewCount={5} renderRow={t => (
            <Row key={t.id} icon={<span style={{ width: 8, height: 8, borderRadius: '50%', background: C.accent, flexShrink: 0, marginTop: 4 }} />}>
              {t.title}{t.dueDate ? <span style={{ color: C.textMuted }}> · до {t.dueDate}</span> : null}
            </Row>
          )} />
        )}
      </Section>
    </div>
  );
}

function recallIcon(kind: string) {
  // team — память команды проекта (③-3.4): выделена акцентным цветом, а не общим info,
  // чтобы отличаться от личной памяти/заметок персоны с первого взгляда
  if (kind === 'team') return <Users size={13} color={C.accent} />;
  if (kind === 'memory') return <Brain size={13} color={C.info} />;
  if (kind === 'note') return <BookOpen size={13} color={C.info} />;
  return <FileText size={13} color={C.info} />;
}
function memoryIcon(t: PersonaMemoryType) {
  if (t === 'semantic') return <Brain size={13} color={C.info} />;
  if (t === 'episodic') return <Calendar size={13} color={C.info} />;
  return <Cog size={13} color={C.info} />;
}
// Те же иконки/цвета типов, что и в редакторе привязок (bindingMeta) — консистентность
function bindingIcon(type: PersonaBinding['type']) {
  return <span style={{ display: 'flex', color: C.textMuted }}>{BINDING_ICONS[type](13)}</span>;
}

// Список с раскрытием: по умолчанию первые previewCount, остальные — по клику «ещё N».
function ExpandableRows<T>({ items, previewCount, renderRow }: {
  items: T[]; previewCount: number; renderRow: (item: T) => React.ReactElement;
}) {
  const [expanded, setExpanded] = useState(false);
  const shown = expanded ? items : items.slice(0, previewCount);
  const hiddenCount = items.length - shown.length;
  return (
    <>
      {shown.map(renderRow)}
      {hiddenCount > 0 && (
        <button type="button" onClick={() => setExpanded(true)} style={showMoreStyle}>ещё {hiddenCount}</button>
      )}
      {expanded && items.length > previewCount && (
        <button type="button" onClick={() => setExpanded(false)} style={showMoreStyle}>Свернуть</button>
      )}
    </>
  );
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
const showMoreStyle: CSSProperties = {
  alignSelf: 'flex-start', border: 'none', background: 'none', padding: 0, marginTop: 2,
  fontSize: 12, fontFamily: FONT.sans, color: C.accent, cursor: 'pointer',
};
function skeletonBar(w: number): CSSProperties {
  return { height: 11, width: `${w}%`, background: C.bgInset, borderRadius: R.sm, opacity: 0.6 };
}
