// Область модуля «Звук» (ADR-021 §2, как у картинок): id проекта у чата проекта или
// `personal` у личного чата вне проекта. Ключ совпадает с AudioEditScope.Key бэкенда —
// по нему события audio_* сопоставляются с чатом.

export const PERSONAL_SCOPE = 'personal';

export const audioScope = (projectId: string | null): string => projectId ?? PERSONAL_SCOPE;

export const isPersonalScope = (scope: string | null | undefined) => scope === PERSONAL_SCOPE;

// База REST области: проектные ручки — по проекту, личные — по чату
export function audioBase(scope: string, sessionId: string | null): string {
  if (!isPersonalScope(scope)) return `/projects/${encodeURIComponent(scope)}/audio-editor`;
  if (!sessionId) throw new Error('Личная область звука без чата');
  return `/audio-editor/chats/${encodeURIComponent(sessionId)}`;
}

// Ручки чата (state, threads): у проекта — …/sessions/{sessionId}, у личной области чат уже в базе
export const chatBase = (scope: string, sessionId: string) =>
  isPersonalScope(scope)
    ? audioBase(scope, sessionId)
    : `${audioBase(scope, sessionId)}/sessions/${encodeURIComponent(sessionId)}`;
