// Транспорт контекста чата (ADR-023 §2.1): REST …/chats/{sessionId}/context и событие
// chat_context_changed. Каждая мутация несёт revision и отвечает полным DTO; чужая ревизия — 409 с
// {error, context} в теле.

import { request } from '../offline';
import { onMessage } from '../signalr';
import { CHAT_CONTEXT_EVENT, ChatContextErrors, chatContextRoutes as routes } from './errors';
import type { ChatContextChangedEvent, ChatContextConflictDto, ChatContextDto, ContextRefInput, SavedFileDto } from './types';

const send = <T>(url: string, method: string, body?: unknown) =>
  request<T>(url, { method, ...(body === undefined ? null : { body: JSON.stringify(body) }) });

export const chatContextApi = {
  get: (sessionId: string) => request<ChatContextDto>(routes.base(sessionId), { live: true }),
  // input = null — снять основной объект
  setPrimary: (sessionId: string, input: Pick<ContextRefInput, 'kind' | 'ref'> | null, revision: number) =>
    send<ChatContextDto>(routes.primary(sessionId), 'PUT', input ? { ...input, revision } : { kind: null, revision }),
  attachRef: (sessionId: string, input: ContextRefInput, revision: number) =>
    send<ChatContextDto>(routes.refs(sessionId), 'POST', { ...input, revision }),
  detachRef: (sessionId: string, itemId: string, revision: number) =>
    send<ChatContextDto>(`${routes.ref(sessionId, itemId)}?revision=${revision}`, 'DELETE'),
  clear: (sessionId: string, revision: number) =>
    send<ChatContextDto>(`${routes.base(sessionId)}?revision=${revision}`, 'DELETE'),
  savedFiles: (sessionId: string) => request<SavedFileDto[]>(routes.savedFiles(sessionId), { live: true }),
  subscribe: (handler: (e: ChatContextChangedEvent) => void) => onMessage(msg => {
    const m = msg as unknown as { type?: string; sessionId?: string; context?: ChatContextDto };
    if (m.type === CHAT_CONTEXT_EVENT && m.sessionId && m.context) handler(m as ChatContextChangedEvent);
  }),
};

// Свежий DTO из тела 409 context_changed — перечитывать не нужно
export function conflictContext(e: unknown): ChatContextDto | null {
  const err = e as { status?: unknown; body?: Partial<ChatContextConflictDto> } | null;
  if (err?.status !== 409 || err.body?.error !== ChatContextErrors.contextChanged) return null;
  const c = err.body.context;
  return c && typeof c.revision === 'number' && Array.isArray(c.refs) ? c : null;
}
