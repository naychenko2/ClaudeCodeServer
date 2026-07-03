import type { Project } from '../types';

// Снимок навигационного положения. Хранится в history.state, чтобы кнопки
// «назад/вперёд» браузера могли восстановить экран. Дополнительно снапшот
// отражается в hash-части URL (#/calendar, #/project/{id}/task/{taskId}…) —
// адрес можно копировать/обновлять, серверного роутинга под пути не нужно.
export interface NavSnapshot {
  screen: 'projects' | 'project' | 'chats' | 'calendar';
  project?: Project;              // когда screen === 'project'
  chatId?: string;                // активный чат вне проекта (screen === 'chats')
  view?: 'sidebar' | 'chat';     // мобильный вид внутри проекта / чатов
  file?: string | null;          // открытый файл (путь) или null
  task?: string | null;          // открытая задача (id) или null
}

// Hash-представление снапшота для адресной строки
function toHash(s: NavSnapshot): string {
  switch (s.screen) {
    case 'chats': return '#/chats';
    case 'calendar': return '#/calendar';
    case 'projects': return '#/projects';
    case 'project': {
      if (!s.project) return '#/projects';
      let h = `#/project/${s.project.id}`;
      if (s.task) h += `/task/${s.task}`;
      else if (s.file) h += `/file/${encodeURIComponent(s.file)}`;
      return h;
    }
  }
}

// Разбор hash при загрузке страницы (диплинк/обновление)
export interface HashTarget {
  screen: 'projects' | 'chats' | 'calendar' | 'project';
  projectId?: string;
  taskId?: string;
  file?: string;
}

export function parseHash(hash: string = window.location.hash): HashTarget | null {
  const parts = hash.replace(/^#\/?/, '').split('/').filter(Boolean);
  if (parts.length === 0) return null;
  switch (parts[0]) {
    case 'chats': return { screen: 'chats' };
    case 'calendar': {
      const target: HashTarget = { screen: 'calendar' };
      // #/calendar/task/{id} — диплинк на личную задачу (модал в календаре)
      if (parts[1] === 'task' && parts[2]) target.taskId = parts[2];
      return target;
    }
    case 'projects': return { screen: 'projects' };
    case 'project': {
      if (!parts[1]) return { screen: 'projects' };
      const target: HashTarget = { screen: 'project', projectId: parts[1] };
      if (parts[2] === 'task' && parts[3]) target.taskId = parts[3];
      else if (parts[2] === 'file' && parts[3]) target.file = decodeURIComponent(parts.slice(3).join('/'));
      return target;
    }
    default: return null;
  }
}

// Новая запись истории (переход «вглубь»)
export function navPush(s: NavSnapshot) {
  window.history.pushState(s, '', toHash(s));
}

// Перезапись текущей записи (сидирование/латеральные смены)
export function navReplace(s: NavSnapshot) {
  window.history.replaceState(s, '', toHash(s));
}

export function getNav(): NavSnapshot | null {
  return (window.history.state as NavSnapshot | null) ?? null;
}
