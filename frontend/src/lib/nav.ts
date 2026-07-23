import type { Project } from '../types';

// Снимок навигационного положения. Хранится в history.state, чтобы кнопки
// «назад/вперёд» браузера могли восстановить экран. Дополнительно снапшот
// отражается в hash-части URL (#/calendar, #/project/{id}/task/{taskId}…) —
// адрес можно копировать/обновлять, серверного роутинга под пути не нужно.
export interface NavSnapshot {
  screen: 'home' | 'projects' | 'project' | 'chats' | 'calendar' | 'notes' | 'personas' | 'knowledge' | 'notifications' | 'module';
  moduleId?: string;              // когда screen === 'module' (внешний модуль платформы)
  project?: Project;              // когда screen === 'project'
  chatId?: string;                // активный чат: screen === 'chats' — глобальный, screen === 'project' — проектный
  view?: 'sidebar' | 'chat';     // мобильный вид внутри проекта / чатов
  file?: string | null;          // открытый файл (путь) или null
  task?: string | null;          // открытая задача (id) или null
  board?: boolean;               // режим Kanban-доски проекта (screen === 'project')
  note?: string | null;          // открытая заметка (id) или null (screen === 'notes')
  persona?: string | null;       // открытая персона (id) или null (screen === 'personas')
  knowledge?: string | null;     // открытая база знаний (id датасета Dify) или null
}

// Hash-представление снапшота для адресной строки
function toHash(s: NavSnapshot): string {
  switch (s.screen) {
    case 'home': return '#/home';
    case 'chats': return s.chatId ? `#/chats/${encodeURIComponent(s.chatId)}` : '#/chats';
    case 'calendar': return s.board ? '#/calendar/board' : '#/calendar';
    case 'notes': return s.note ? `#/notes/${encodeURIComponent(s.note)}` : '#/notes';
    case 'personas': return s.persona ? `#/personas/${encodeURIComponent(s.persona)}` : '#/personas';
    case 'knowledge': return s.knowledge ? `#/knowledge/${encodeURIComponent(s.knowledge)}` : '#/knowledge';
    case 'module': return s.moduleId ? `#/module/${encodeURIComponent(s.moduleId)}` : '#/home';
    case 'notifications': return '#/notifications';
    case 'projects': return '#/projects';
    case 'project': {
      if (!s.project) return '#/projects';
      let h = `#/project/${s.project.id}`;
      if (s.task) h += `/task/${s.task}`;
      else if (s.file) h += `/file/${encodeURIComponent(s.file)}`;
      else if (s.board) h += '/board';
      else if (s.chatId) h += `/chat/${encodeURIComponent(s.chatId)}`;
      else if (s.persona) h += `/persona/${encodeURIComponent(s.persona)}`;
      return h;
    }
  }
}

// Разбор hash при загрузке страницы (диплинк/обновление)
export interface HashTarget {
  screen: 'home' | 'projects' | 'chats' | 'calendar' | 'project' | 'notes' | 'personas' | 'knowledge' | 'notifications' | 'module';
  projectId?: string;
  moduleId?: string;             // #/module/{id}
  taskId?: string;
  file?: string;
  board?: boolean;
  noteId?: string;
  personaId?: string;
  personaView?: 'automation'; // сразу открыть вкладку студии персоны (бэйдж автоматизации в чате)
  knowledgeId?: string;
  chatId?: string;   // диплинк на конкретный чат: #/chats/{id} — глобальный, #/project/{id}/chat/{chatId} — проектный
  history?: boolean; // #/history — открыть overlay «Что нового» (поверх дашборда)
}

export function parseHash(hash: string = window.location.hash): HashTarget | null {
  const parts = hash.replace(/^#\/?/, '').split('/').filter(Boolean);
  if (parts.length === 0) return null;
  switch (parts[0]) {
    case 'home': return { screen: 'home' };
    // Overlay «Что нового»: открывается поверх дашборда (App диспатчит событие открытия)
    case 'history': return { screen: 'home', history: true };
    case 'chats': {
      const target: HashTarget = { screen: 'chats' };
      // #/chats/{id} — диплинк на конкретный чат (уведомления проактивных персон)
      if (parts[1]) target.chatId = decodeURIComponent(parts[1]);
      return target;
    }
    case 'calendar': {
      const target: HashTarget = { screen: 'calendar' };
      // #/calendar/task/{id} — диплинк на личную задачу (модал в календаре)
      if (parts[1] === 'task' && parts[2]) target.taskId = parts[2];
      else if (parts[1] === 'board') target.board = true;
      return target;
    }
    case 'notes': {
      const target: HashTarget = { screen: 'notes' };
      if (parts[1]) target.noteId = decodeURIComponent(parts[1]);
      return target;
    }
    // 'agents' — алиас старых диплинков: раздел переименован в «Персоны»
    case 'personas':
    case 'agents': {
      const target: HashTarget = { screen: 'personas' };
      if (parts[1]) target.personaId = decodeURIComponent(parts[1]);
      if (parts[2] === 'automation') target.personaView = 'automation';
      return target;
    }
    case 'knowledge': {
      const target: HashTarget = { screen: 'knowledge' };
      if (parts[1]) target.knowledgeId = decodeURIComponent(parts[1]);
      return target;
    }
    case 'notifications': return { screen: 'notifications' };
    case 'module': {
      if (!parts[1]) return { screen: 'home' };
      return { screen: 'module', moduleId: decodeURIComponent(parts[1]) };
    }
    case 'projects': return { screen: 'projects' };
    case 'project': {
      if (!parts[1]) return { screen: 'projects' };
      const target: HashTarget = { screen: 'project', projectId: parts[1] };
      // #/project/{id}/chat/{chatId} — диплинк на конкретный чат внутри проекта
      if (parts[2] === 'chat' && parts[3]) { target.chatId = decodeURIComponent(parts[3]); return target; }
      if (parts[2] === 'task' && parts[3]) target.taskId = parts[3];
      else if (parts[2] === 'file' && parts[3]) target.file = decodeURIComponent(parts.slice(3).join('/'));
      else if (parts[2] === 'board') target.board = true;
      else if (parts[2] === 'persona' && parts[3]) {
        target.personaId = parts[3];
        if (parts[4] === 'automation') target.personaView = 'automation';
      }
      return target;
    }
    default: return null;
  }
}

// Событие смены навигации — для потребителей, которым нужен немедленный отклик на переход
// (напр. AI-хаб сбрасывает свой статус сразу, не дожидаясь опросного тика). pushState/
// replaceState сами не шлют popstate/hashchange, поэтому уведомляем явно.
export const NAV_CHANGE_EVENT = 'cc-nav-change';
function notifyNavChange() { window.dispatchEvent(new Event(NAV_CHANGE_EVENT)); }

// Новая запись истории (переход «вглубь»)
export function navPush(s: NavSnapshot) {
  window.history.pushState(s, '', toHash(s));
  notifyNavChange();
}

// Перезапись текущей записи (сидирование/латеральные смены)
export function navReplace(s: NavSnapshot) {
  window.history.replaceState(s, '', toHash(s));
  notifyNavChange();
}

export function getNav(): NavSnapshot | null {
  return (window.history.state as NavSnapshot | null) ?? null;
}
