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

// Ручки библиотеки голосов без sessionId в сигнатуре (создание, правка, образцы) в личной области висят
// на чате: «Личное» одно на владельца, любой его личный чат подходит. Точка входа вкладки «Голоса»
// запоминает свой чат синхронно в рендере, до эффектов с запросами
let _personalChat: string | null = null;

export function enterVoicesScope(scope: string, sessionId: string | null): void {
  if (isPersonalScope(scope) && sessionId) _personalChat = sessionId;
}

export const voicesScopeBase = (scope: string) => audioBase(scope, isPersonalScope(scope) ? _personalChat : null);

// Сброс — только для тестов
export function __resetVoicesScope() {
  _personalChat = null;
}
