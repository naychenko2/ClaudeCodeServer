// Коды ошибок, маршруты и имя события контекста чата — единственное место на фронте
// (зеркало бэкенд-констант ChatContextErrors, ChatContextRoutes, ChatContextEventNames).

export const CHAT_CONTEXT_EVENT = 'chat_context_changed';

export const ChatContextErrors = {
  // 409: ревизия клиента устарела, в теле — свежий контекст
  contextChanged: 'context_changed',
  kindUnknown: 'kind_unknown',
  refInvalid: 'ref_invalid',
  roleNotAccepted: 'role_not_accepted',
  refsLimit: 'refs_limit',
  projectLocalUnsupported: 'project_local_unsupported',
} as const;
export type ChatContextErrorCode = typeof ChatContextErrors[keyof typeof ChatContextErrors];

// Потолок референсов на бэкенде (ChatContextErrors.MaxRefs)
export const MAX_REFS = 16;

const base = (sessionId: string) => `/chats/${encodeURIComponent(sessionId)}/context`;

export const chatContextRoutes = {
  base,
  primary: (sessionId: string) => `${base(sessionId)}/primary`,
  refs: (sessionId: string) => `${base(sessionId)}/refs`,
  ref: (sessionId: string, itemId: string) => `${base(sessionId)}/refs/${encodeURIComponent(itemId)}`,
  savedFiles: (sessionId: string) => `${base(sessionId)}/saved-files`,
};
