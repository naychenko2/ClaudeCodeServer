// Область редактора (разрез docs/research/image-editor-personal-chats-cut-2026-09.md, §4):
// внутри модуля всегда есть непустой ключ области — id проекта у чата проекта или
// `personal` у личного чата вне проекта. Параметры `projectId: string` модуля — это ключ
// области, а не обязательно проект: проектное API каркаса (файлы, персонажи, «Сохранить
// в проект») у личной области не зовётся никогда, проверять isPersonalScope.

export const PERSONAL_SCOPE = 'personal';

export const imageScope = (projectId: string | null): string => projectId ?? PERSONAL_SCOPE;

export const isPersonalScope = (scope: string | null | undefined) => scope === PERSONAL_SCOPE;

// База REST области: проектные ручки — по проекту, личные — по чату
export function imageBase(scope: string, sessionId: string | null): string {
  if (!isPersonalScope(scope)) return `/projects/${encodeURIComponent(scope)}/image-editor`;
  if (!sessionId) throw new Error('Личная область редактора без чата');
  return `/image-editor/chats/${encodeURIComponent(sessionId)}`;
}

// Нити чата: у проекта — …/sessions/{sessionId}/threads, у личной области чат уже в базе
export const threadsBase = (scope: string, sessionId: string) =>
  isPersonalScope(scope)
    ? `${imageBase(scope, sessionId)}/threads`
    : `${imageBase(scope, sessionId)}/sessions/${encodeURIComponent(sessionId)}/threads`;

// Личная область одна на владельца: каталог, котировка, задачи, шаги и выбор человека
// висят на любом его личном чате (гейт сверяет только владельца и отсутствие проекта).
// Ручкам без sessionId в сигнатуре (api.ts, prefs) нужен чат — берём тот, в котором
// модуль сейчас рисуется: каждая точка входа модуля переводит свой ctx в область через
// enterScope, синхронно в рендере, до эффектов с запросами
let _personalChat: string | null = null;

export function enterScope(projectId: string | null, sessionId: string | null): string {
  const scope = imageScope(projectId);
  if (isPersonalScope(scope) && sessionId) _personalChat = sessionId;
  return scope;
}

export const scopeBase = (scope: string) => imageBase(scope, isPersonalScope(scope) ? _personalChat : null);

// Проектные ручки (save, characters): у личной области их нет по построению — отказ до запроса
export function projectBase(scope: string): string {
  if (isPersonalScope(scope)) throw new Error('У личного чата нет проекта');
  return imageBase(scope, null);
}

// Сброс — только для тестов
export function __resetScope() {
  _personalChat = null;
}
