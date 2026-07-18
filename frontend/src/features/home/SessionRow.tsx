import { MessageCircle } from 'lucide-react';
import type { HomeSessionInfo } from '../../types';
import { C, FONT } from '../../lib/design';
import { getPersonaById, personaLabel } from '../../lib/personas';
import { relTime } from './WidgetCard';

// Точка статуса сессии: живая — зеленая/оранжевая, ждет разрешения — accent
const STATUS_DOT: Record<HomeSessionInfo['status'], string> = {
  starting: C.warning,
  working: C.success,
  waiting: C.accent,
  active: C.info,
  finished: C.textMuted,
  error: C.danger,
  orphaned: C.textMuted,
};

const STATUS_LABEL: Record<HomeSessionInfo['status'], string> = {
  starting: 'запускается',
  working: 'работает',
  waiting: 'ждет ответа',
  active: 'открыта',
  finished: 'завершена',
  error: 'ошибка',
  orphaned: '',
};

// Заголовок строки: имя чата > персона > последнее сообщение > заглушка
// (пустой чат зовем «Новый чат» — как его называет список в разделе «Чаты»)
function rowTitle(s: HomeSessionInfo): string {
  if (s.name?.trim()) return s.name;
  if (s.personaId) {
    const p = getPersonaById(s.personaId);
    if (p) return personaLabel(p);
  }
  if (s.lastMessage?.trim()) return s.lastMessage;
  return 'Новый чат';
}

// Строка сессии/чата на дашборде: точка статуса, заголовок, чип проекта, время.
// Общая для «Сейчас работают» (showStatus) и «Недавних» (время вместо статуса).
export function SessionRow({ s, showStatus, onOpen, hideChatBadge }: {
  s: HomeSessionInfo;
  showStatus?: boolean;
  onOpen: (s: HomeSessionInfo) => void;
  // Не показывать чип «Чат» у вне-проектных строк (когда список и так только из чатов)
  hideChatBadge?: boolean;
}) {
  const sub = showStatus ? STATUS_LABEL[s.status] : relTime(s.updatedAt);
  return (
    <button
      onClick={() => onOpen(s)}
      style={{
        display: 'flex', alignItems: 'center', gap: 9, width: '100%', textAlign: 'left',
        background: 'none', border: 'none', borderRadius: 8, padding: '7px 8px',
        margin: '0 -8px', cursor: 'pointer', minWidth: 0,
      }}
      onMouseEnter={e => { e.currentTarget.style.background = C.bgSelected; }}
      onMouseLeave={e => { e.currentTarget.style.background = 'none'; }}
    >
      <span style={{
        width: 8, height: 8, borderRadius: '50%', flexShrink: 0,
        background: STATUS_DOT[s.status],
      }} />
      <span style={{
        fontFamily: FONT.sans, fontSize: 13, color: C.textPrimary, flex: 1, minWidth: 0,
        whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis',
      }}>
        {rowTitle(s)}
      </span>
      {s.taskId && (
        <span style={{
          fontFamily: FONT.sans, fontSize: 11, color: C.planText, background: C.planLight,
          border: `1px solid ${C.planBorder}`, borderRadius: 999, padding: '1px 7px', flexShrink: 0,
        }}>
          задача
        </span>
      )}
      {(s.projectName || !hideChatBadge) && (
        <span style={{
          display: 'inline-flex', alignItems: 'center', gap: 4, flexShrink: 0,
          fontFamily: FONT.sans, fontSize: 11.5, color: C.textMuted,
          maxWidth: 130, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis',
        }}>
          {s.projectName ?? <><MessageCircle size={11} strokeWidth={2} />Чат</>}
        </span>
      )}
      {sub && (
        <span style={{ fontFamily: FONT.sans, fontSize: 11.5, color: C.textMuted, flexShrink: 0 }}>
          {sub}
        </span>
      )}
    </button>
  );
}

// Диплинк открытия сессии: проектная → чат внутри проекта, иначе — глобальный чат
export function openSession(s: HomeSessionInfo): void {
  const url = s.projectId
    ? `#/project/${s.projectId}/chat/${encodeURIComponent(s.id)}`
    : `#/chats/${encodeURIComponent(s.id)}`;
  window.dispatchEvent(new CustomEvent('cc-open-url', { detail: { url } }));
}
