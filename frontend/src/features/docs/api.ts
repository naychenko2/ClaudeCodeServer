// Методы REST «Документации проекта» (README.md + docs/**) для панели «Документы»:
// корпус со связями, а не файлы. lib/api.ts подмешивает их в объект api spread'ом
// (адрес api.docs.* остаётся прежним для потребителей); Doc* типы — в types/index.ts.
import { request } from '../../lib/offline';
import type {
  DocDetail,
  DocEntry,
  DocProperty,
  DocSearchHit,
  DocTypeSchema,
  DocsScope,
  DocsScopeInfo,
} from '../../types';

export const docsApi = {
  // Документация проекта (README.md + docs/**) для панели «Документы»: корпус со связями,
  // а не файлы — отсюда отдельная секция рядом с files
  docs: {
    index: (projectId: string) =>
      request<DocEntry[]>(`/projects/${projectId}/docs`),
    doc: (projectId: string, path: string) =>
      request<DocDetail>(`/projects/${projectId}/docs/doc?path=${encodeURIComponent(path)}`),
    search: (projectId: string, q: string) =>
      request<DocSearchHit[]>(`/projects/${projectId}/docs/search?q=${encodeURIComponent(q)}`),
    // Область документации: что выбрано и что вообще годится (папки, файлы корня, типы)
    scope: (projectId: string) =>
      request<DocsScopeInfo>(`/projects/${projectId}/docs/scope`),
    // У каждой оси null — вернуть её к дефолту, [] — «ничего отсюда».
    // Ответ отдаёт СОХРАНЁННОЕ значение (сервер отбрасывает мусор), его и показываем
    setScope: (projectId: string, scope: Partial<DocsScope>) =>
      request<DocsScopeInfo>(`/projects/${projectId}/docs/scope`, {
        method: 'PUT',
        body: JSON.stringify({
          folders: scope.folders ?? null,
          rootFiles: scope.rootFiles ?? null,
          types: scope.types ?? null,
          // undefined — не трогать выбор «Начала»; '' — вернуть авто-README
          home: scope.home === undefined ? null : scope.home,
          // undefined — не трогать исключения (старый бандл их не знает); [] — убрать все
          excludeFolders: scope.excludeFolders ?? null,
        }),
      }),
    // Вынести текущую область в файл .docs репозитория: дальше она версионируется и
    // одинакова у всех, кто открыл репозиторий, а setScope правит уже файл
    saveScopeFile: (projectId: string) =>
      request<DocsScopeInfo>(`/projects/${projectId}/docs/scope-file`, { method: 'POST' }),
    // Порядок страниц папки — правка .order в рабочем дереве, поэтому только по жесту
    // пользователя. items — имена БЕЗ расширения в новом порядке (как строки в файле);
    // это подмножество папки, остальные её строки сервер оставляет на своих местах.
    // Ответ — свежий индекс: порядок приезжает вместе с подтверждением
    // Создать документ или раздел. name — ЧЕЛОВЕЧЕСКОЕ название: имя файла из него делает
    // сервер (пробелы → дефисы), а само название становится заголовком первой строки.
    // Раздел создаётся парой «страница + папка» — в wiki он существует только так
    create: (projectId: string, folder: string, name: string, kind: 'doc' | 'section') =>
      request<{ path: string; index: DocEntry[] }>(`/projects/${projectId}/docs/create`, {
        method: 'POST',
        body: JSON.stringify({ folder, name, kind }),
      }),
    // Переименовать документ или раздел. Раздел переезжает парой со всем поддеревом:
    // moved — карта «старый путь → новый» по каждому переехавшему документу, по ней
    // панель чинит закреплённые и открытый документ. updateLinks=false оставляет чужие
    // файлы нетронутыми и возвращает число ссылок, оставшихся битыми
    rename: (projectId: string, path: string, newName: string, updateLinks: boolean) =>
      request<{ path: string; updatedDocs: number; brokenLinks: number; moved: Record<string, string>; index: DocEntry[] }>(
        `/projects/${projectId}/docs/rename`, {
          method: 'POST',
          body: JSON.stringify({ path, newName, updateLinks }),
        }),
    // Удалить документ или раздел. Раздел уходит парой «страница + папка» со всем
    // содержимым, включая файлы, которых панель не показывала (removedFiles).
    // brokenLinks — сколько ссылок на удалённое осталось: починить их нечем
    remove: (projectId: string, path: string) =>
      request<{ removed: string[]; brokenLinks: number; removedFiles: number; index: DocEntry[] }>(
        `/projects/${projectId}/docs/delete`, {
          method: 'POST',
          body: JSON.stringify({ path }),
        }),
    // Перенести документ или раздел в другую папку области. Раздел переезжает со всем
    // поддеревом; updateLinks чинит и чужие ссылки на переехавшее, и его собственные —
    // при смене папки меняется глубина, и относительные пути ломаются в обе стороны
    move: (projectId: string, path: string, targetFolder: string, updateLinks: boolean) =>
      request<{ path: string; updatedDocs: number; brokenLinks: number; moved: Record<string, string>; index: DocEntry[] }>(
        `/projects/${projectId}/docs/move`, {
          method: 'POST',
          body: JSON.stringify({ path, targetFolder, updateLinks }),
        }),
    setOrder: (projectId: string, folder: string, items: string[]) =>
      request<DocEntry[]>(`/projects/${projectId}/docs/order`, {
        method: 'PUT',
        body: JSON.stringify({ folder, items }),
      }),
    // Значение свойства в шапке документа: сервер правит строку «**Ключ:** …» прямо в md
    // (или добавляет её на место по схеме). value=null — снять свойство, '' — пустой слот.
    // Вместе со значением может обновиться «дата смены» — какие ключи изменились
    // фактически, говорит touched. Ответ несёт свежий индекс: метка в дереве обязана
    // приехать вместе с подтверждением, а не вторым запросом
    setProperty: (projectId: string, path: string, key: string, value: string | null) =>
      request<{ properties: DocProperty[]; touched: string[]; index: DocEntry[] }>(
        `/projects/${projectId}/docs/property`, {
          method: 'PUT',
          body: JSON.stringify({ path, key, value }),
        }),
    // Схема типов документов: перезаписывает секцию docTypes файла .docs. Файла нет — он
    // будет создан вместе с действующей областью (схеме больше негде жить)
    setDocTypes: (projectId: string, types: DocTypeSchema[]) =>
      request<{ scope: DocsScopeInfo; index: DocEntry[] }>(`/projects/${projectId}/docs/types`, {
        method: 'PUT',
        body: JSON.stringify({ types }),
      }),
  },
};
