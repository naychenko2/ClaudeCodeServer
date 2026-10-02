// Область модуля «Видео» (ADR-022 §2, как у звука): id проекта у чата проекта или `personal`
// у личного чата вне проекта. Ключ совпадает с VideoEditScope.Key бэкенда — по нему события
// video_* сопоставляются с чатом.

export const PERSONAL_SCOPE = 'personal';

export const videoScope = (projectId: string | null): string => projectId ?? PERSONAL_SCOPE;

export const isPersonalScope = (scope: string | null | undefined) => scope === PERSONAL_SCOPE;

// База REST области: проектные ручки — по проекту, личные — по чату
export function videoBase(scope: string, sessionId: string | null): string {
  if (!isPersonalScope(scope)) return `/projects/${encodeURIComponent(scope)}/video-editor`;
  if (!sessionId) throw new Error('Личная область видео без чата');
  return `/video-editor/chats/${encodeURIComponent(sessionId)}`;
}

// Ручки чата (state, scenes): у проекта — …/sessions/{sessionId}, у личной области чат уже в базе
export const chatBase = (scope: string, sessionId: string) =>
  isPersonalScope(scope)
    ? videoBase(scope, sessionId)
    : `${videoBase(scope, sessionId)}/sessions/${encodeURIComponent(sessionId)}`;
