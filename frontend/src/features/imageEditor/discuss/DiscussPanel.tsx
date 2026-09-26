// Панель ответа «Обсудить с Claude» (макет image-editor-v1, блок .helper в панели
// запроса). Картинка с пометками уходит на ручку discuss, сервер создаёт новый чат
// проекта и возвращает его id; ответ панель читает тем же хуком, что и ChatPanel
// (useSession: joinSession + события чата + догрузка после переподключения).

import { useMemo } from 'react';
import { MessageSquare, X } from 'lucide-react';
import { Button, IconButton, WaitingIndicator, ICON_SIZE, ICON_STROKE, C, FS, R, SP, useSession, MarkdownContent } from 'aihome_shell/kit';
import type { ChatItem } from '../../../types';
import { extractImagePrompt } from './discussText';

export interface DiscussState {
  // null — чат ещё создаётся
  sessionId: string | null;
  // Миниатюра отправленной размеченной копии и подпись «что ушло»
  thumbUrl: string | null;
  summary: string;
  error: string | null;
}

// Открыть чат проекта тем же каналом, что диплинки: воркспейс подхватит ключ
export function openProjectChat(projectId: string, sessionId: string) {
  sessionStorage.setItem('cc_pending_project_chat', `${projectId}|${sessionId}`);
  window.dispatchEvent(new Event('cc-pending-project-chat'));
}

// Ответ Claude — текстовые реплики после последнего сообщения человека, без сабагентов
function replyText(items: ChatItem[]): string {
  let start = 0;
  items.forEach((it, i) => { if (it.kind === 'user_message') start = i + 1; });
  return items.slice(start)
    .flatMap(it => (it.kind === 'text' && !it.parentToolUseId && it.text.trim() ? [it.text] : []))
    .join('\n\n');
}

export function DiscussPanel({ projectId, projectName, state, onUsePrompt, onOpenChat, onClose }: {
  projectId: string;
  projectName: string;
  state: DiscussState;
  onUsePrompt: (text: string) => void;
  onOpenChat: (sessionId: string) => void;
  onClose: () => void;
}) {
  const session = useSession(state.sessionId, projectId);
  const reply = useMemo(() => replyText(session.items), [session.items]);
  const suggested = useMemo(() => (session.isWaiting ? null : extractImagePrompt(reply)), [reply, session.isWaiting]);
  const waiting = !state.error && (!state.sessionId || session.isWaiting || !reply);

  return (
    <div style={{
      border: `1px solid ${C.border}`, background: C.bgWhite, borderRadius: R.xl, padding: SP.md,
      display: 'flex', flexDirection: 'column', gap: SP.sm, minWidth: 0,
    }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: SP.xs, fontSize: FS.sm, color: C.textSecondary }}>
        <MessageSquare size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
        <span style={{ flex: 1, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
          Обсуждение в чате проекта «{projectName}»
        </span>
        <IconButton size="xs" title="Закрыть" ariaLabel="Закрыть" onClick={onClose}>
          <X size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
        </IconButton>
      </div>

      <div style={{
        alignSelf: 'flex-end', maxWidth: '90%', display: 'flex', gap: SP.sm, alignItems: 'center',
        fontSize: FS.sm, lineHeight: 1.45, background: C.accentLight, borderRadius: `${R.lg}px ${R.lg}px ${SP.xxs}px ${R.lg}px`,
        padding: `${SP.sm}px ${SP.sm}px`, color: C.textPrimary,
      }}>
        {state.thumbUrl && (
          <img src={state.thumbUrl} alt="" style={{
            width: 44, flex: '0 0 44px', borderRadius: R.sm, border: `1px solid ${C.border}`, display: 'block',
          }} />
        )}
        <span style={{ minWidth: 0, overflowWrap: 'anywhere' }}>{state.summary}</span>
      </div>

      <div style={{ fontSize: FS.xs, fontWeight: 600, color: C.textSecondary }}>Claude</div>
      {state.error ? (
        <div style={{ fontSize: FS.sm, color: C.dangerText }}>{state.error}</div>
      ) : reply ? (
        <div style={{ fontSize: FS.sm, color: C.textPrimary, minWidth: 0, overflowWrap: 'anywhere', maxHeight: 360, overflow: 'auto' }}>
          <MarkdownContent text={reply} />
        </div>
      ) : null}
      {waiting && <WaitingIndicator hint={state.sessionId ? 'Claude смотрит картинку и материалы проекта' : 'Создаём чат проекта'} />}

      <div style={{ display: 'flex', gap: SP.sm, justifyContent: 'flex-end', flexWrap: 'wrap' }}>
        <Button variant="ghost" size="sm" disabled={!state.sessionId} onClick={() => state.sessionId && onOpenChat(state.sessionId)}>
          Открыть чат
        </Button>
        <Button variant="primary" size="sm" disabled={!suggested} onClick={() => suggested && onUsePrompt(suggested)}
          title={suggested ? undefined : 'Claude ещё не предложил запрос'}>
          Подставить в запрос
        </Button>
      </div>
    </div>
  );
}
