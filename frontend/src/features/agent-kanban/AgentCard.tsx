import type { CSSProperties } from 'react';
import type { BoardItem } from '../../types';
import { C, FONT, SHADOW } from '../../lib/design';
import { openTaskInSection } from '../../lib/tasks';
import { api } from '../../lib/api';
import { ensurePersonasLoaded, usePersonas } from '../../lib/personas';
import { useEffect } from 'react';

const ICON_STYLE: CSSProperties = { width: 12, height: 12, borderRadius: '50%', flexShrink: 0 };

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

export function AgentCard({ item, onOpenChat }: {
  item: BoardItem;
  onOpenChat: (sessionId: string) => void;
}) {
  const personas = usePersonas();
  const persona = item.personaId ? personas.find(p => p.id === item.personaId) : undefined;

  useEffect(() => { void ensurePersonasLoaded(); }, []);
  const personaLabel = persona
    ? (persona.role ? `${persona.role} (${persona.name})` : persona.name)
    : 'Claude';

  // Цвет карточки — свой для колонки
  const accent = item.column === 'working' ? C.accent
    : item.column === 'waiting' ? C.warning
    : item.column === 'done' ? C.success
    : C.textMuted;

  const handleOpenTask = () => {
    // Открыть задачу в её разделе
    openTaskInSection({ id: item.taskId, projectId: item.projectId, title: item.title } as any);
  };

  return (
    <div style={{
      display: 'flex', flexDirection: 'column', gap: 6,
      background: C.bgWhite, border: `1px solid ${C.borderLight}`,
      borderRadius: 12, padding: '11px 12px',
      boxShadow: SHADOW.card,
      cursor: 'default',
    }}>
      {/* Верхняя строка: аватар + роль + заголовок задачи */}
      <div style={{ display: 'flex', gap: 8, alignItems: 'flex-start' }}>
        {/* Аватар персоны или иконка Claude */}
        <div style={{
          width: 28, height: 28, borderRadius: 8, flexShrink: 0, overflow: 'hidden',
          background: persona?.avatar?.color ?? C.bgSelected,
          display: 'flex', alignItems: 'center', justifyContent: 'center',
          fontSize: 11, fontWeight: 700, color: C.textPrimary,
        }}>
          {persona?.avatar?.imageFile ? (
            <img src={`/api/personas/${persona.id}/avatar`} alt="" style={{ width: '100%', height: '100%', objectFit: 'cover' }} />
          ) : persona?.avatar?.kind === 'initials' ? (
            (persona.name?.charAt(0) ?? '🤖').toUpperCase()
          ) : (
            'C'
          )}
        </div>

        <div style={{ flex: 1, minWidth: 0 }}>
          {/* Имя персоны */}
          <div style={{
            fontFamily: FONT.sans, fontSize: 11.5, fontWeight: 700, color: C.textHeading,
            lineHeight: 1.3,
          }}>
            {personaLabel}
          </div>

          {/* Заголовок задачи — кликабельный */}
          <button
            onClick={handleOpenTask}
            title="Открыть задачу"
            style={{
              display: 'block', width: '100%',
              border: 'none', background: 'transparent', cursor: 'pointer',
              padding: 0, margin: 0,
              textAlign: 'left',
              fontFamily: FONT.sans, fontSize: 12.5, fontWeight: 600, color: C.textPrimary,
              lineHeight: 1.35,
              overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
            }}
          >
            {item.title}
          </button>
        </div>

        {/* Цветной индикатор колонки */}
        <span style={{ ...ICON_STYLE, background: accent, marginTop: 4 }} />
      </div>

      {/* Строка статуса */}
      <div style={{ display: 'flex', alignItems: 'center', gap: 6, flexWrap: 'wrap' }}>
        {item.column === 'working' && (
          <span style={{ fontFamily: FONT.sans, fontSize: 11, color: C.textSecondary }}>
            {'▶'} {formatDuration(item.startedAt)}
          </span>
        )}
        {item.column === 'waiting' && (
          <span style={{ fontFamily: FONT.sans, fontSize: 11, color: C.warning, fontWeight: 600 }}>
            {'⚠'} Ждёт ответа
          </span>
        )}
        {item.column === 'done' && (
          <span style={{ fontFamily: FONT.sans, fontSize: 11, color: C.success, fontWeight: 600 }}>
            {'✓'} Завершено
          </span>
        )}
        {item.column === 'queue' && (
          <span style={{ fontFamily: FONT.sans, fontSize: 11, color: C.textMuted }}>
            В очереди
          </span>
        )}
        {item.projectId && (
          <span style={{ fontFamily: FONT.sans, fontSize: 10, color: C.textMuted }}>
            #{item.projectId.slice(0, 7)}
          </span>
        )}
      </div>

      {/* Кнопки действий */}
      <div style={{ display: 'flex', alignItems: 'center', gap: 6, marginTop: 2 }}>
        {item.sessionId && (
          <ActionButton onClick={() => onOpenChat(item.sessionId!)} color={C.textSecondary}>
            Открыть чат
          </ActionButton>
        )}
        {item.column === 'working' && item.sessionId && (
          <ActionButton onClick={() => interruptAgent(item.sessionId!)} color={C.danger}>
            Прервать
          </ActionButton>
        )}
        {item.column === 'waiting' && item.sessionId && (
          <ActionButton onClick={() => openSession(item.sessionId!)} color={C.accent}>
            Ответить
          </ActionButton>
        )}
      </div>
    </div>
  );
}

function ActionButton({ onClick, color, children }: {
  onClick: () => void;
  color: string;
  children: React.ReactNode;
}) {
  return (
    <button
      onClick={onClick}
      style={{
        display: 'inline-flex', alignItems: 'center', gap: 4,
        padding: '4px 9px', cursor: 'pointer',
        border: `1px solid ${color}33`, borderRadius: 7,
        background: `${color}11`,
        color,
        fontFamily: FONT.sans, fontSize: 11, fontWeight: 600,
        lineHeight: 1.3,
      }}
    >
      {children}
    </button>
  );
}

async function interruptAgent(sessionId: string) {
  try { await api.board.interrupt(sessionId); } catch { /* ignore */ }
}

function openSession(sessionId: string) {
  // Открываем чат в разделе «Чаты» — используем навигацию
  window.location.hash = `#/chats/${sessionId}`;
}
