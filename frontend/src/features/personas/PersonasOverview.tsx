import type { Persona, Project } from '../../types';
import { C, FONT, R, SHADOW } from '../../lib/design';
import { AGENT_COLORS } from '../../components/AgentSelector';
import { personaTitleLines } from '../../lib/personas';
import { modelLabel } from '../../lib/models';
import { PersonaAvatar } from './PersonaAvatar';
import { PERSONA_TEMPLATES } from './personaTemplates';

// Обзор раздела «Персоны» — «витрина команды» в центральной зоне, когда ничего
// не выбрано: сетка карточек с идентичностью каждой персоны (аватар, роль, цвет)
// и быстрыми действиями. Клик по карточке / «Настроить» → студия-профиль
// (onSelect), «Поговорить» → чат от лица персоны (onTalk). Без персон —
// пригласительный hero с намёком на шаблонные роли. Никаких запросов per-карточку:
// всё рисуется из уже загруженного списка персон.

// Подписи возможностей персоны (ключи tools). Показываем только когда набор
// ограничен (tools != null) — «все возможности» это норма, её не подписываем.
const TOOL_LABELS: Record<string, string> = {
  tasks: 'Задачи',
  notes: 'Заметки',
  web: 'Веб',
};

// Русская форма слова «персона» по числу
function personasWord(n: number): string {
  const mod10 = n % 10, mod100 = n % 100;
  if (mod10 === 1 && mod100 !== 11) return 'персона';
  if (mod10 >= 2 && mod10 <= 4 && (mod100 < 12 || mod100 > 14)) return 'персоны';
  return 'персон';
}

// Иконка раздела для пригласительного hero
function IconPersonas() {
  return (
    <svg width="28" height="28" viewBox="0 0 24 24" fill="none" stroke="currentColor"
      strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
      <circle cx="12" cy="8" r="4" />
      <path d="M4 20c0-4 3.6-6 8-6s8 2 8 6" />
    </svg>
  );
}

// Иконка «плюс» для кнопки создания
function IconPlus() {
  return (
    <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor"
      strokeWidth="2.4" strokeLinecap="round" strokeLinejoin="round">
      <path d="M12 5v14M5 12h14" />
    </svg>
  );
}

export function PersonasOverview({ personas, projects, onSelect, onTalk, onNew, talking }: {
  personas: Persona[];
  // Для имени проекта у проектной персоны (в глобальном разделе их нет — на будущее)
  projects: Project[];
  onSelect: (id: string) => void;
  onTalk: (p: Persona) => void;
  onNew: () => void;
  // Идёт создание чата — блокируем повторные «Поговорить»
  talking: boolean;
}) {
  if (personas.length === 0) {
    return <EmptyHero onNew={onNew} />;
  }

  return (
    <div style={{ height: '100%', overflowY: 'auto' }}>
      <div style={{
        maxWidth: 1080, margin: '0 auto', boxSizing: 'border-box',
        padding: '28px 24px 60px', fontFamily: FONT.sans,
      }}>
        {/* Шапка обзора: заголовок + счётчик + создание */}
        <div style={{ display: 'flex', alignItems: 'flex-end', gap: 16, marginBottom: 20 }}>
          <div style={{ flex: 1, minWidth: 0 }}>
            <div style={{ fontFamily: FONT.serif, fontSize: 26, fontWeight: 700, color: C.textHeading, letterSpacing: '-0.01em' }}>
              Персоны
            </div>
            <div style={{ marginTop: 4, fontSize: 13.5, color: C.textMuted, lineHeight: 1.5 }}>
              {personas.length} {personasWord(personas.length)} в команде — выбери, с кем поговорить, или настрой профиль
            </div>
          </div>
          <button onClick={onNew} style={newBtn}><IconPlus />Новая персона</button>
        </div>

        {/* Сетка карточек: десктоп 2–3 колонки, узко — одна */}
        <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(280px, 1fr))', gap: 14 }}>
          {personas.map(p => (
            <PersonaCard key={p.id} persona={p} projects={projects}
              onSelect={() => onSelect(p.id)} onTalk={() => onTalk(p)} talking={talking} />
          ))}
        </div>
      </div>
    </div>
  );
}

// Карточка персоны: идентичность (аватар, роль, имя, цветовая полоса) +
// компактные факты (зона, возможности, модель, память) + действия.
function PersonaCard({ persona: p, projects, onSelect, onTalk, talking }: {
  persona: Persona;
  projects: Project[];
  onSelect: () => void;
  onTalk: () => void;
  talking: boolean;
}) {
  const accent = AGENT_COLORS[p.avatar?.color ?? ''] ?? C.accent;
  const lines = personaTitleLines(p);
  // Описание, а без него — первая строка характера (чтобы карточка не была пустой)
  const blurb = p.description?.trim() || p.systemPrompt?.trim().split('\n')[0] || '';

  const zoneLabel = p.scope === 'project'
    ? `Проект · ${projects.find(x => x.id === p.projectId)?.name ?? 'Проект'}`
    : 'Глобальная';

  // Чипы возможностей — только когда набор ограничен; пустой набор = «только чат»
  const toolChips = p.tools == null
    ? []
    : p.tools.length === 0
      ? ['Только чат']
      : p.tools.map(t => TOOL_LABELS[t] ?? t);

  return (
    <div
      onClick={onSelect}
      onMouseEnter={e => {
        const el = e.currentTarget as HTMLElement;
        el.style.borderColor = accent; el.style.boxShadow = SHADOW.card;
      }}
      onMouseLeave={e => {
        const el = e.currentTarget as HTMLElement;
        el.style.borderColor = C.border; el.style.boxShadow = 'none';
      }}
      style={{
        display: 'flex', flexDirection: 'column', cursor: 'pointer',
        background: C.bgCard, border: `1px solid ${C.border}`, borderRadius: R.xxl,
        overflow: 'hidden', transition: 'border-color 0.15s, box-shadow 0.15s',
      }}
    >
      {/* Цветовая полоса персоны — та же идентичность, что в тулбаре чата */}
      <div style={{ flex: 'none', height: 3, background: accent }} />

      <div style={{ flex: 1, display: 'flex', flexDirection: 'column', gap: 10, padding: '14px 16px 12px' }}>
        {/* Идентичность: аватар + «Роль / Имя» */}
        <div style={{ display: 'flex', alignItems: 'center', gap: 12 }}>
          <PersonaAvatar persona={p} size={52} />
          <div style={{ flex: 1, minWidth: 0 }}>
            <div style={{
              fontFamily: FONT.serif, fontSize: 16.5, fontWeight: 600, color: C.textHeading,
              letterSpacing: '-0.01em', overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
            }}>
              {lines.primary}
            </div>
            {lines.secondary && (
              <div style={{ marginTop: 1, fontSize: 12.5, color: C.textMuted, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                {lines.secondary}
              </div>
            )}
          </div>
        </div>

        {/* Описание / кусочек характера — максимум две строки */}
        {blurb && (
          <div style={{
            fontSize: 13, color: C.textSecondary, lineHeight: 1.45,
            overflow: 'hidden', textOverflow: 'ellipsis',
            display: '-webkit-box', WebkitLineClamp: 2, WebkitBoxOrient: 'vertical',
          }}>
            {blurb}
          </div>
        )}

        {/* Компактные факты: зона, возможности, модель, память */}
        <div style={{ display: 'flex', flexWrap: 'wrap', gap: 5, marginTop: 'auto' }}>
          <span style={chip}>{zoneLabel}</span>
          {toolChips.map(t => <span key={t} style={chip}>{t}</span>)}
          {p.model && <span style={chip}>{modelLabel(p.model)}</span>}
          {!p.memoryEnabled && <span style={chip}>Память выкл</span>}
        </div>
      </div>

      {/* Действия карточки */}
      <div style={{
        flex: 'none', display: 'flex', gap: 8, padding: '10px 16px 14px',
        borderTop: `1px solid ${C.borderLight}`,
      }}>
        <button
          onClick={e => { e.stopPropagation(); onTalk(); }}
          disabled={talking}
          style={{ ...talkBtn, opacity: talking ? 0.6 : 1, cursor: talking ? 'default' : 'pointer' }}
        >
          Поговорить
        </button>
        <button onClick={e => { e.stopPropagation(); onSelect(); }} style={ghostBtn}>
          Настроить
        </button>
      </div>
    </div>
  );
}

// Пригласительный hero, когда персон ещё нет: объяснение раздела + шаблонные
// роли как намёк, с чего начать (клик по любой пилюле ведёт в флоу создания).
function EmptyHero({ onNew }: { onNew: () => void }) {
  return (
    <div style={{ height: '100%', overflowY: 'auto', display: 'flex' }}>
      <div style={{
        margin: 'auto', maxWidth: 520, boxSizing: 'border-box', padding: '40px 24px',
        display: 'flex', flexDirection: 'column', alignItems: 'center', textAlign: 'center',
        fontFamily: FONT.sans,
      }}>
        <div style={{
          width: 64, height: 64, borderRadius: R.full, background: C.accentLight, color: C.accent,
          display: 'flex', alignItems: 'center', justifyContent: 'center', marginBottom: 18,
        }}>
          <IconPersonas />
        </div>
        <div style={{ fontFamily: FONT.serif, fontSize: 24, fontWeight: 700, color: C.textHeading, letterSpacing: '-0.01em' }}>
          Собери свою команду
        </div>
        <div style={{ marginTop: 8, fontSize: 13.5, color: C.textMuted, lineHeight: 1.55 }}>
          Персона — собеседник со своим характером, ролью и памятью.
          Опиши её одной фразой — ИИ придумает остальное, или начни с готовой роли.
        </div>
        <button onClick={onNew} style={{ ...newBtn, marginTop: 20 }}><IconPlus />Новая персона</button>

        {/* Намёк на шаблоны: роли-пилюли с фирменными цветами */}
        <div style={{ marginTop: 26, display: 'flex', flexWrap: 'wrap', gap: 6, justifyContent: 'center' }}>
          {PERSONA_TEMPLATES.map(t => (
            <button
              key={t.key}
              onClick={onNew}
              title={t.description}
              onMouseEnter={e => { (e.currentTarget as HTMLElement).style.background = C.accentLight; }}
              onMouseLeave={e => { (e.currentTarget as HTMLElement).style.background = C.bgCard; }}
              style={templatePill}
            >
              <span style={{
                width: 8, height: 8, borderRadius: R.full, flexShrink: 0,
                background: AGENT_COLORS[t.avatarColor] ?? C.accent,
              }} />
              {t.role}
            </button>
          ))}
        </div>
      </div>
    </div>
  );
}

const templatePill: React.CSSProperties = {
  display: 'inline-flex', alignItems: 'center', gap: 6,
  background: C.bgCard, color: C.textPrimary, border: `1px solid ${C.border}`,
  borderRadius: 999, padding: '5px 12px', fontSize: 12.5, fontWeight: 500,
  cursor: 'pointer', fontFamily: FONT.sans, transition: 'background 0.15s',
};

const chip: React.CSSProperties = {
  fontSize: 11.5, fontWeight: 500, color: C.textMuted, background: C.bgPanel,
  padding: '2.5px 8px', borderRadius: R.sm, whiteSpace: 'nowrap',
};

const newBtn: React.CSSProperties = {
  display: 'inline-flex', alignItems: 'center', gap: 5, flex: 'none',
  background: C.accent, color: C.onAccent, border: 'none', borderRadius: R.md,
  padding: '9px 16px', fontSize: 13, fontWeight: 500, cursor: 'pointer', fontFamily: FONT.sans,
};

const talkBtn: React.CSSProperties = {
  flex: 1, background: C.accent, color: C.onAccent, border: 'none', borderRadius: R.md,
  padding: '7px 12px', fontSize: 12.5, fontWeight: 600, fontFamily: FONT.sans,
};

const ghostBtn: React.CSSProperties = {
  flex: 1, background: 'transparent', color: C.textPrimary,
  border: `1px solid ${C.border}`, borderRadius: R.md,
  padding: '7px 12px', fontSize: 12.5, fontWeight: 500, cursor: 'pointer', fontFamily: FONT.sans,
};
