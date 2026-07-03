import type { Project } from '../types';

// Снимок навигационного положения. Хранится в history.state, чтобы кнопки
// «назад/вперёд» браузера могли восстановить экран. URL не меняем — навигация
// идёт по записям истории (popstate), без серверного роутинга под пути.
export interface NavSnapshot {
  screen: 'projects' | 'project' | 'chats' | 'calendar';
  project?: Project;              // когда screen === 'project'
  chatId?: string;                // активный чат вне проекта (screen === 'chats')
  view?: 'sidebar' | 'chat';     // мобильный вид внутри проекта / чатов
  file?: string | null;          // открытый файл (путь) или null
}

// Новая запись истории (переход «вглубь»)
export function navPush(s: NavSnapshot) {
  window.history.pushState(s, '');
}

// Перезапись текущей записи (сидирование/латеральные смены)
export function navReplace(s: NavSnapshot) {
  window.history.replaceState(s, '');
}

export function getNav(): NavSnapshot | null {
  return (window.history.state as NavSnapshot | null) ?? null;
}
