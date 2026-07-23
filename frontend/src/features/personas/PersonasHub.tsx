import { useRef, useState } from 'react';
import { Plus, Sparkles, MessageSquare, Brain, ListChecks, Zap, Users, AtSign } from 'lucide-react';
import type { LucideIcon } from 'lucide-react';
import type { Persona, Session } from '../../types';
import { C, FONT, R } from '../../lib/design';
import { personaTitleLines } from '../../lib/personas';
import { PersonaAvatar } from './PersonaAvatar';
import { PersonaActivityFeed } from './PersonaActivityFeed';
import { usePersonasActivity } from './personasActivity';

// Хаб раздела «Персоны»: показывается в центральной зоне, когда персона не выбрана
// и не идёт создание. Единая шапка (текст + компактная карточка возможностей),
// витрина «Твои помощники» + компактный вход в мастер создания слева, лента
// «Активность» справа (сворачивается на всю ширину, вытесняя левую колонку —
// см. PersonaActivityFeed). Создание — единая точка входа PersonaWizard (9 шагов:
// сам предлагает способ — по описанию/из шаблона/с нуля), хаб сюда ничего не
// дублирует, только зовёт onNew().
export function PersonasHub({ personas, talking, onTalk, onOpenSession, onNew, onOpenPersonaView }: {
  personas: Persona[];
  talking?: boolean;
  onTalk: (p: Persona) => void;
  onOpenSession: (s: Session) => void;
  onNew: () => void;
  onOpenPersonaView: (id: string, view?: 'memory') => void;
}) {
  const [activityExpanded, setActivityExpanded] = useState(false);
  const { items: activityItems, loading: activityLoading } = usePersonasActivity(personas);
  const hasPersonas = personas.length > 0;
  const scrollRef = useRef<HTMLDivElement>(null);
  const toggleActivity = () => {
    setActivityExpanded(v => !v);
    scrollRef.current?.scrollTo({ top: 0 });
  };

  return (
    <div ref={scrollRef} style={{ height: '100%', overflowY: 'auto', background: C.bgMain }}>
      <div style={{ maxWidth: 1180, margin: '0 auto', padding: '28px 32px 60px' }}>

        {/* Шапка: текст слева, компактная карточка «что умеет персона» справа */}
        <div style={heroRow}>
          <div style={{ flex: 1, minWidth: 320 }}>
            <div style={h1}>Персоны</div>
            <div style={heroText}>
              Персона — не ещё один режим чата, а помощник со своим лицом: у него есть роль, характер,
              память о прошлых разговорах и свои инструменты. Разговаривай с ним как с коллегой,
              поручай задачи и зови на совещание вместе с другими персонами.
            </div>
          </div>
          <div style={capsCard}>
            <div style={capsLabel}>Что умеет персона</div>
            {CAPABILITIES.map(c => (
              <div key={c.label} style={capsRow}>
                <span style={capsIcon}><c.Icon size={13} strokeWidth={2} /></span>
                {c.label}
              </div>
            ))}
          </div>
        </div>

        <div style={activityExpanded ? undefined : hubGrid}>
          {!activityExpanded && (
            <div style={{ display: 'flex', flexDirection: 'column', gap: 34, minWidth: 0 }}>

              {/* Твои помощники */}
              <section>
                <SectionTitle title="Твои помощники" sub="Кликни, чтобы открыть, или наведи, чтобы поговорить" />
                {hasPersonas ? (
                  <div style={showcaseGrid}>
                    {personas.map(p => (
                      <AssistantCard
                        key={p.id}
                        persona={p}
                        talking={talking}
                        onOpen={() => onOpenPersonaView(p.id)}
                        onTalk={() => onTalk(p)}
                      />
                    ))}
                    <div
                      role="button" tabIndex={0} onClick={onNew}
                      onKeyDown={e => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); onNew(); } }}
                      onMouseEnter={e => { e.currentTarget.style.borderColor = C.accent; e.currentTarget.style.color = C.accent; e.currentTarget.style.background = C.accentLight; }}
                      onMouseLeave={e => { e.currentTarget.style.borderColor = C.dashed; e.currentTarget.style.color = C.textSecondary; e.currentTarget.style.background = 'transparent'; }}
                      style={addTile}
                    >
                      <Plus size={20} strokeWidth={2} />
                      <span style={{ fontSize: 12.5, fontWeight: 600 }}>Новая персона</span>
                    </div>
                  </div>
                ) : (
                  <div style={emptyBoxBig}>
                    <div style={{ fontSize: 13, color: C.textMuted, lineHeight: 1.5 }}>
                      Пока нет помощников. Создай первого — задай роль, характер и аватар.
                    </div>
                    <button type="button" onClick={onNew} style={primaryBtn}>Новая персона</button>
                  </div>
                )}
              </section>

              {/* Новая персона — единая точка входа: мастер сам спросит способ */}
              <section>
                <SectionTitle title="Новая персона" sub="Мастер сам предложит способ — по описанию, из шаблона или с нуля" />
                <div style={createPanel}>
                  <span style={createIcon}><Sparkles size={18} strokeWidth={2} /></span>
                  <div style={{ flex: 1, minWidth: 0 }}>
                    <div style={createPanelTitle}>Девять шагов, своя пара минут на каждый</div>
                    <div style={createPanelDesc}>Роль, характер, поведение, умения и доступ — с ИИ-подбором на каждом шаге.</div>
                  </div>
                  <button type="button" onClick={onNew} style={primaryBtn}>Создать</button>
                </div>
              </section>
            </div>
          )}

          <aside style={activityExpanded ? { maxWidth: 760, margin: '0 auto', width: '100%' } : { minWidth: 0 }}>
            <PersonaActivityFeed
              personas={personas}
              items={activityItems}
              loading={activityLoading}
              expanded={activityExpanded}
              onToggleExpanded={toggleActivity}
              onOpenSession={onOpenSession}
              onOpenPersonaView={onOpenPersonaView}
            />
          </aside>
        </div>
      </div>
    </div>
  );
}

function SectionTitle({ title, sub }: { title: string; sub: string }) {
  return (
    <div style={{ marginBottom: 14 }}>
      <div style={heading}>{title}</div>
      <div style={subheading}>{sub}</div>
    </div>
  );
}

function AssistantCard({ persona, talking, onOpen, onTalk }: {
  persona: Persona; talking?: boolean; onOpen: () => void; onTalk: () => void;
}) {
  const lines = personaTitleLines(persona);
  return (
    <div
      role="button" tabIndex={0} onClick={onOpen}
      onKeyDown={e => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); onOpen(); } }}
      onMouseEnter={e => { e.currentTarget.style.borderColor = C.accentMuted; }}
      onMouseLeave={e => { e.currentTarget.style.borderColor = C.border; }}
      style={assistantCard}
    >
      <div style={{ display: 'flex', alignItems: 'center', gap: 10 }}>
        <PersonaAvatar persona={persona} size={40} />
        <div style={{ minWidth: 0 }}>
          <div style={assistantName}>{lines.primary}</div>
          {lines.secondary && <div style={assistantSecondary}>{lines.secondary}</div>}
        </div>
      </div>
      {persona.description?.trim() && <div style={assistantDesc}>{persona.description}</div>}
      <button
        type="button"
        disabled={talking}
        onClick={e => { e.stopPropagation(); onTalk(); }}
        style={{ ...talkLink, opacity: talking ? 0.6 : 1, cursor: talking ? 'default' : 'pointer' }}
      >
        <MessageSquare size={12} strokeWidth={2.2} /> {talking ? 'Создаём…' : 'Поговорить'}
      </button>
    </div>
  );
}

const CAPABILITIES: { Icon: LucideIcon; label: string }[] = [
  { Icon: Brain, label: 'Помнит факты между разговорами' },
  { Icon: ListChecks, label: 'Может быть исполнителем задач' },
  { Icon: Zap, label: 'Реагирует на события сама' },
  { Icon: Users, label: 'Групповые чаты и совещания' },
  { Icon: AtSign, label: 'Персоны спрашивают друг друга' },
];

const heroRow: React.CSSProperties = {
  display: 'flex', alignItems: 'stretch', justifyContent: 'space-between', gap: 32, flexWrap: 'wrap',
  paddingBottom: 26, borderBottom: `1px solid ${C.borderLight}`, marginBottom: 28,
};
// Заголовок хаба — в стиле заголовка раздела «Календарь» (serif 28 / 500)
const h1: React.CSSProperties = {
  fontFamily: FONT.serif, fontSize: 28, fontWeight: 500, color: C.textHeading,
  lineHeight: 1.28, letterSpacing: '-0.01em', marginBottom: 12, maxWidth: 600,
};
const heroText: React.CSSProperties = { fontSize: 14, color: C.textMuted, lineHeight: 1.65, maxWidth: 560 };
const capsCard: React.CSSProperties = {
  flex: 'none', width: 300, alignSelf: 'stretch', background: C.bgWhite, border: `1px solid ${C.borderLight}`,
  borderRadius: R.xxl, padding: '16px 18px', display: 'flex', flexDirection: 'column', gap: 11,
};
const capsLabel: React.CSSProperties = {
  fontFamily: FONT.mono, fontSize: 10.5, fontWeight: 600, letterSpacing: '0.05em', textTransform: 'uppercase',
  color: C.textSecondary,
};
const capsRow: React.CSSProperties = { display: 'flex', alignItems: 'center', gap: 10, fontSize: 12.5, color: C.textMuted };
const capsIcon: React.CSSProperties = {
  width: 26, height: 26, borderRadius: R.md, background: C.bgPanel, color: C.textMuted,
  display: 'flex', alignItems: 'center', justifyContent: 'center', flexShrink: 0,
};
const hubGrid: React.CSSProperties = { display: 'grid', gridTemplateColumns: 'minmax(0,1fr) 300px', gap: 28, alignItems: 'start' };
const heading: React.CSSProperties = { fontFamily: FONT.serif, fontSize: 19, fontWeight: 700, color: C.textHeading };
const subheading: React.CSSProperties = { fontSize: 12.5, color: C.textSecondary, marginTop: 4 };
const showcaseGrid: React.CSSProperties = { display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(210px, 1fr))', gap: 12 };
const assistantCard: React.CSSProperties = {
  display: 'flex', flexDirection: 'column', gap: 9, background: C.bgWhite, border: `1px solid ${C.border}`,
  borderRadius: R.xxl, padding: 14, cursor: 'pointer', transition: 'border-color 0.15s',
};
const assistantName: React.CSSProperties = {
  fontSize: 13.5, fontWeight: 700, color: C.textHeading, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
};
const assistantSecondary: React.CSSProperties = {
  fontSize: 11.5, color: C.textMuted, marginTop: 1, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
};
const assistantDesc: React.CSSProperties = {
  fontSize: 12, color: C.textMuted, lineHeight: 1.5, flex: 1,
  display: '-webkit-box', WebkitLineClamp: 2, WebkitBoxOrient: 'vertical', overflow: 'hidden',
};
const talkLink: React.CSSProperties = {
  alignSelf: 'flex-start', display: 'flex', alignItems: 'center', gap: 5, background: 'none', border: 'none',
  padding: 0, fontFamily: FONT.sans, fontSize: 11.5, fontWeight: 700, color: C.accent,
};
const addTile: React.CSSProperties = {
  display: 'flex', flexDirection: 'column', alignItems: 'center', justifyContent: 'center', gap: 8,
  minHeight: 108, border: `1.5px dashed ${C.dashed}`, borderRadius: R.xxl, color: C.textSecondary,
  cursor: 'pointer', transition: 'border-color 0.12s, color 0.12s, background 0.12s',
};
const emptyBoxBig: React.CSSProperties = {
  border: `1px dashed ${C.dashed}`, borderRadius: R.xxl, padding: '28px 20px',
  display: 'flex', flexDirection: 'column', alignItems: 'center', gap: 14, textAlign: 'center',
};
const primaryBtn: React.CSSProperties = {
  background: C.accent, color: C.onAccent, border: 'none', borderRadius: R.md,
  padding: '9px 18px', fontSize: 13, fontWeight: 600, fontFamily: FONT.sans, cursor: 'pointer',
};
const createPanel: React.CSSProperties = {
  display: 'flex', alignItems: 'center', gap: 16,
  background: C.bgWhite, border: `1px solid ${C.borderLight}`, borderRadius: R.xxl, padding: '18px 20px',
};
const createIcon: React.CSSProperties = {
  width: 40, height: 40, borderRadius: R.lg, background: C.accentLight, color: C.accent,
  display: 'flex', alignItems: 'center', justifyContent: 'center', flexShrink: 0,
};
const createPanelTitle: React.CSSProperties = { fontSize: 14, fontWeight: 700, color: C.textHeading };
const createPanelDesc: React.CSSProperties = { fontSize: 12.5, color: C.textMuted, marginTop: 3, lineHeight: 1.5 };
