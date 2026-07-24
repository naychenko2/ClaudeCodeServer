import type { BoardItem } from '../../types';
import { C, FONT, SHADOW } from '../../lib/design';
import { getTaskById, openTaskInSection } from '../../lib/tasks';
import { PersonaAvatar } from '../personas/PersonaAvatar';
import { ClaudeBadge } from '../tasks/bits';
import { usePersonas } from '../../lib/personas';

// Форматирование длительности: X мин / N ч M мин
function formatDuration(startedAt?: string): string {
  if (!startedAt) return '';
  const diff = Date.now() - new Date(startedAt).getTime();
  const mins = Math.floor(diff / 60000);
  if (mins < 1) return 'только что';
  if (mins < 60) return `${mins} мин`;
  const h = Math.floor(mins / 60);
  const m = mins % 60;
  return m > 0 ? `${h} ч ${m} мин` : `${h} ч`;
}

export function AgentCard({ item }: {
  item: BoardItem;
}) {
  const personas = usePersonas();
  const persona = item.personaId ? personas.find(p => p.id === item.personaId) : undefined;

  const personaLabel = persona
    ? (persona.role ? `${persona.role} (${persona.name})` : persona.name)
    : 'AI';

  // Цвет колонки — для полосы слева (как в TaskCard)
  const accent = item.column === 'working' ? C.accent
    : item.column === 'waiting' ? C.warning
    : item.column === 'done' ? C.success
    : C.textMuted;

  // Клик по всей карточке → задача
  const handleOpenTask = () => {
    const task = getTaskById(item.taskId);
    if (task) {
      openTaskInSection(task);
    } else {
      const url = item.projectId
        ? `#/project/${item.projectId}/task/${item.taskId}`
        : `#/calendar/task/${item.taskId}`;
      window.dispatchEvent(new CustomEvent('cc-open-url', { detail: { url } }));
    }
  };

  // Открыть чат (кнопка)
  const handleOpenChat = (e: React.MouseEvent) => {
    e.stopPropagation();
    if (item.sessionId) {
      window.dispatchEvent(new CustomEvent('cc-open-url', {
        detail: { url: `#/chats/${item.sessionId}` },
      }));
    }
  };

  return (
    <div
      onClick={handleOpenTask}
      style={{
        display: 'flex', gap: 10,
        background: C.bgWhite,
        border: `1px solid ${C.borderLight}`,
        boxShadow: SHADOW.card,
        borderRadius: 12,
        padding: '10px 12px',
        cursor: 'pointer',
        transition: 'border-color 0.12s, box-shadow 0.12s',
      }}
      onMouseEnter={e => {
        e.currentTarget.style.borderColor = C.accentMuted;
        e.currentTarget.style.boxShadow = SHADOW.dropdown;
      }}
      onMouseLeave={e => {
        e.currentTarget.style.borderColor = C.borderLight;
        e.currentTarget.style.boxShadow = SHADOW.card;
      }}
    >
      {/* Цветная полоса колонки слева (как TaskCard) */}
      <div style={{
        width: 3, borderRadius: 2, background: accent,
        flexShrink: 0, alignSelf: 'stretch',
      }} />

      {/* Аватар: PersonaAvatar для персон, ClaudeBadge для Claude */}
      <div style={{ flexShrink: 0, marginTop: 1 }}>
        {persona ? (
          <PersonaAvatar persona={persona} size={24} />
        ) : (
          <ClaudeBadge size={20} />
        )}
      </div>

      <div style={{ flex: 1, minWidth: 0 }}>
        {/* Имя/роль персоны */}
        <div style={{
          fontFamily: FONT.sans, fontSize: 11.5, fontWeight: 700,
          color: C.textHeading, lineHeight: 1.3,
          marginBottom: 2,
        }}>
          {personaLabel}
        </div>

        {/* Заголовок задачи */}
        <div style={{
          fontFamily: FONT.sans, fontSize: 13, fontWeight: 600,
          color: C.textPrimary, lineHeight: 1.35,
          overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
          marginBottom: 4,
        }}>
          {item.title}
        </div>

        {/* Строка статуса */}
        <div style={{ display: 'flex', alignItems: 'center', gap: 6, flexWrap: 'wrap' }}>
          {item.column === 'working' && (
            <span style={{
              display: 'inline-flex', alignItems: 'center', gap: 3,
              fontFamily: FONT.sans, fontSize: 11, color: C.accent, fontWeight: 600,
            }}>
              <svg width="10" height="10" viewBox="0 0 24 24" fill="currentColor">
                <circle cx="12" cy="12" r="10" />
              </svg>
              {formatDuration(item.startedAt)}
            </span>
          )}
          {item.column === 'waiting' && (
            <span style={{
              fontFamily: FONT.sans, fontSize: 11, color: C.warning, fontWeight: 600,
              display: 'inline-flex', alignItems: 'center', gap: 3,
            }}>
              <svg width="10" height="10" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.5">
                <circle cx="12" cy="12" r="10" /><line x1="12" y1="8" x2="12" y2="12" /><line x1="12" y1="16" x2="12.01" y2="16" />
              </svg>
              Ждёт ответа
            </span>
          )}
          {item.column === 'done' && (
            <span style={{
              fontFamily: FONT.sans, fontSize: 11, color: C.success, fontWeight: 600,
              display: 'inline-flex', alignItems: 'center', gap: 3,
            }}>
              <svg width="10" height="10" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="3">
                <polyline points="20 6 9 17 4 12" />
              </svg>
              Готово
            </span>
          )}
          {item.column === 'queue' && (
            <span style={{
              fontFamily: FONT.sans, fontSize: 11, color: C.textMuted,
            }}>
              В очереди
            </span>
          )}
        </div>

        {/* Кнопки действий */}
        <div style={{ display: 'flex', alignItems: 'center', gap: 6, marginTop: 6 }}>
          {item.sessionId && (
            <button
              onClick={handleOpenChat}
              style={{
                display: 'inline-flex', alignItems: 'center', gap: 4,
                padding: '3px 8px', border: 'none', borderRadius: 6,
                background: C.bgSelected, color: C.textSecondary,
                fontFamily: FONT.sans, fontSize: 10.5, fontWeight: 600, cursor: 'pointer',
                lineHeight: 1.4,
              }}
              onMouseEnter={e => {
                e.currentTarget.style.background = C.accentLight;
                e.currentTarget.style.color = C.accent;
              }}
              onMouseLeave={e => {
                e.currentTarget.style.background = C.bgSelected;
                e.currentTarget.style.color = C.textSecondary;
              }}
            >
              <svg width="10" height="10" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.5">
                <path d="M21 15a2 2 0 0 1-2 2H7l-4 4V5a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2z" />
              </svg>
              Чат
            </button>
          )}
          {item.column === 'working' && item.sessionId && (
            <button
              onClick={e => { e.stopPropagation(); void interruptAgent(item.sessionId!); }}
              style={{
                display: 'inline-flex', alignItems: 'center', gap: 4,
                padding: '3px 8px', border: 'none', borderRadius: 6,
                background: C.dangerBg, color: C.danger, cursor: 'pointer',
                fontFamily: FONT.sans, fontSize: 10.5, fontWeight: 600, lineHeight: 1.4,
              }}
              onMouseEnter={e => {
                e.currentTarget.style.background = C.danger;
                e.currentTarget.style.color = '#fff';
              }}
              onMouseLeave={e => {
                e.currentTarget.style.background = C.dangerBg;
                e.currentTarget.style.color = C.danger;
              }}
            >
              <svg width="10" height="10" viewBox="0 0 24 24" fill="currentColor">
                <rect x="6" y="4" width="4" height="16" rx="1" /><rect x="14" y="4" width="4" height="16" rx="1" />
              </svg>
              Стоп
            </button>
          )}
          {item.column === 'waiting' && item.sessionId && (
            <button
              onClick={handleOpenChat}
              style={{
                display: 'inline-flex', alignItems: 'center', gap: 4,
                padding: '3px 8px', border: 'none', borderRadius: 6,
                background: C.accentLight, color: C.accent, cursor: 'pointer',
                fontFamily: FONT.sans, fontSize: 10.5, fontWeight: 600, lineHeight: 1.4,
              }}
            >
              <svg width="10" height="10" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.5">
                <path d="M21 11.5a8.38 8.38 0 0 1-.9 3.8 8.5 8.5 0 0 1-7.6 4.7 8.38 8.38 0 0 1-3.8-.9L3 21l1.9-5.7a8.38 8.38 0 0 1-.9-3.8 8.5 8.5 0 0 1 4.7-7.6 8.38 8.38 0 0 1 3.8-.9h.5a8.48 8.48 0 0 1 8 8v.5z" />
              </svg>
              Ответить
            </button>
          )}
        </div>
      </div>
    </div>
  );
}

async function interruptAgent(sessionId: string) {
  try {
    const { api } = await import('../../lib/api');
    await api.board.interrupt(sessionId);
  } catch { /* ignore */ }
}
