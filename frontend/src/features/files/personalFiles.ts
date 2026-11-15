// «Мои файлы» личного чата (ADR-040 Ф2-9): тот же файловый UI, что у проекта (FileExplorer/FileViewer),
// но маршруты — личные: api/personal/chats/{sessionId}/files. «Проекта» у личного чата нет, поэтому UI
// получает псевдо-проект с id вида `personal-files:{sessionId}`; api-слой (features/files/api.ts) по этому
// префиксу переключает базу пути, а набор доступных действий (маршрут 'personal') — только то, что умеет
// бэкенд: просмотр, скачивание, загрузка файла, создание папки и файла, переименование и перенос, удаление.
import type { Project } from '../projects/types';
import type { ProjectRoute } from '../../lib/deviceAgentRoutes';

const PREFIX = 'personal-files:';

export const personalFilesId = (sessionId: string): string => `${PREFIX}${sessionId}`;

export const isPersonalFilesId = (id: string | null | undefined): boolean => !!id && id.startsWith(PREFIX);

// База REST личных файлов (без /api): chat — из id псевдо-проекта
export function personalFilesBase(id: string): string {
  return `/personal/chats/${encodeURIComponent(id.slice(PREFIX.length))}/files`;
}

// Маршруты, которые есть у личного маршрута сервера. Всё прочее (сохранение текста, git,
// офисные форматы, заметки, офлайн) панель прячет — маршрута нет, контрола нет.
// 'POST files/upload' — загрузка в панель «Файлы» личного чата (Ф-6/Ф-7): multipart-поле file,
// необязательный ?path= каталога; перезапись цели разрешена (атомарная подмена на бэке)
export const PERSONAL_FILES_ROUTES: readonly ProjectRoute[] = [
  'GET files',
  'GET files/tree',
  'GET files/search',
  'GET files/content',
  'GET files/stream',
  'POST files/create',
  'POST files/mkdir',
  'POST files/rename',
  'POST files/upload',
  'DELETE files',
];

export function personalFilesSupports(route: ProjectRoute): boolean {
  return PERSONAL_FILES_ROUTES.includes(route);
}

// Псевдо-проект для FileExplorer/FileViewer: без роли (все двери записи открыты — решает бэкенд),
// без устройства (маршрут сервера), корень не показывается
export function personalFilesProject(sessionId: string): Project {
  const now = new Date(0).toISOString();
  return {
    id: personalFilesId(sessionId),
    name: 'Мои файлы',
    rootPath: '',
    createdAt: now,
    updatedAt: now,
  };
}
