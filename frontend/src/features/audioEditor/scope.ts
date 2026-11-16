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

// sessionId явно — чат берётся из аргумента и от порядка вызовов не зависит; без него остаётся запомненный
// чат точки входа (вызовы voices/api.ts без sessionId в сигнатуре)
export const voicesScopeBase = (scope: string, sessionId?: string | null) =>
  audioBase(scope, isPersonalScope(scope) ? sessionId ?? _personalChat : null);

// ── Файлы «Личного» (личные файлы чата, features/files/personalFiles.ts): вход «звук из Личного» ──

export const personalFilesPath = (sessionId: string) => `/personal/chats/${encodeURIComponent(sessionId)}/files`;

const SOUND_FILE = /\.(wav|mp3|flac|ogg)$/i;

export interface PersonalFileEntry { name: string; path: string; isDirectory: boolean }

// Звуковые файлы из плоского дерева «Личного» по пути; папки и прочие файлы не берём
export const personalSoundFiles = (entries: readonly PersonalFileEntry[]): PersonalFileEntry[] =>
  entries.filter(e => !e.isDirectory && SOUND_FILE.test(e.path)).sort((a, b) => a.path.localeCompare(b.path, 'ru'));

// Сброс — только для тестов
export function __resetVoicesScope() {
  _personalChat = null;
}
