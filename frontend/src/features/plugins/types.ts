// Типы «Плагинов владельца» (ADR-031, вертикаль ClaudeHomeServer.Plugins). Публичный экспорт
// lib/api не изменился: там re-export `export type *` из этого файла.
// === Плагины владельца (ADR-031, вертикаль ClaudeHomeServer.Plugins) ===
// DTO зеркалит PluginResponse.cs: значения секретов наружу НЕ едут — только имя
// + признак «заполнен» (M7). id — {publisher}/{slug}; в URL — один сегмент publisher~slug
// (бэк нормализует ~ → /), поэтому urlId кодирует разделитель.
export interface PluginSecretView {
  name: string;
  set: boolean;
  // Время последнего изменения (шаг 3.3): для бейджа «задан · {дата}». Значение не едет.
  updatedAt?: string | null;
}
export interface PluginInfo {
  id: string;
  version: string;
  sha256: string;
  requested: string[];
  granted: string[];
  enabled: boolean;
  source: string;
  secrets: PluginSecretView[];
  // Вклады пакета (contributes из манифеста): нужны, чтобы слот панели выбирал
  // плагин, у которого панель объявлена, а не просто первый включённый.
  // panels — {id, title, entry}; entry — путь панели относительно корня пакета
  // (контракт GET /api/plugins/{id}/{ver}/assets/{entry}, ADR-031 §3.1).
  contributes?: { panels?: PluginPanelView[] };
}
// Панель пакета (contributes.panels из манифеста, ADR-031 §2.1): id + title + entry.
// entry — путь относительно **корня пакета** (например panel/index.html), тот же
// контракт пути, что у GET /api/plugins/{id}/{ver}/assets/{entry} (catch-all {**path}).
// slot в ответ списка не отдаётся (UI не использует) — см. CLAUDE.md вертикали, REST.
export interface PluginPanelView {
  id: string;
  title: string;
  entry: string;
}
// Тело POST /api/plugins/{id}/grant: выданные права (подмножество requested) + значения секретов
// value: null — явное удаление секрета; не присланный (нет в списке) бэк не трогает («изменить», не «заменить»)
export interface PluginGrantSecret {
  name: string;
  value: string | null;
}
export interface PluginPermissionsView {
  requested: string[];
  granted: string[];
}

// === Черновики build-it-yourself (ADR-031, шаги 4.3a/4.3b/4.3c, вертикаль Plugins) ===
// DTO зеркалит PluginDraftService/PluginDraftDiff. Ключи camelCase — глобальная
// JSON-политика MVC (типы без [JsonPropertyName] сериализуются дефолтом Web).
// Значения секретов наружу не едут: только имя + признак «обязательный».

// Сводка черновика для списка (GET /api/plugins/drafts): активные + отклонённые
export interface PluginDraftSummary {
  slug: string;
  id: string;
  version: string;
  updatedAt: string;
  // Число проблем последней проверки (валидатор пакета)
  problems: number;
  // Есть ли запись installed.json с тем же Id+Version
  installedSameVersion: boolean;
  // true — отклонён (в _rejected/{slug}); у активных false
  rejected: boolean;
  // Когда отклонённый исчезнет (rejectedAt + 30 дней); у активных null
  expiresAt: string | null;
  // Человекочитаемое имя пакета («Погода»): title первой панели манифеста. null/нет —
  // панелей нет или title пуст (старый сервер поле не отдаёт): фронт берёт запасное имя.
  displayName?: string | null;
}
// Панель в предпросмотре (PluginDraftPanel)
export interface PluginDraftPanelView {
  id: string;
  slot: string;
  title: string;
}
// Секрет в предпросмотре (PluginDraftSecret): имя + признак «обязательный»
// + задан ли у установленного плагина (QA-фикс R; отсутствие = старое поведение, false)
export interface PluginDraftSecretView {
  name: string;
  required: boolean;
  // Задан ли секрет у установленного плагина с тем же id (бэк QA-фикс R).
  // Отсутствие (старый сервер) = false.
  set?: boolean;
}
// Файл в предпросмотре (PluginDraftFile): путь, каталог/файл, размер
export interface PluginDraftFileView {
  path: string;
  isDir: boolean;
  size: number;
}
// Проблема валидатора (PluginDraftProblem)
export interface PluginDraftProblemView {
  code: string;
  message: string;
}
// Предпросмотр черновика (GET /api/plugins/drafts/{slug}/preview)
export interface PluginDraftPreview {
  slug: string;
  name: string;
  version: string;
  panels: PluginDraftPanelView[];
  commands: string[];
  skills: string[];
  requestedPermissions: string[];
  secrets: PluginDraftSecretView[];
  files: PluginDraftFileView[];
  validation: { isValid: boolean; problems: PluginDraftProblemView[] };
  // Версия установленного плагина с тем же id (QA-фикс R); null/отсутствие = первая установка
  installedVersion?: string | null;
}
// Строка diff (PluginDraftDiffLine): kind = ctx/del/add
export interface PluginDraftDiffLine {
  oldLine: number;
  newLine: number;
  kind: 'ctx' | 'del' | 'add';
  text: string;
}
// Файл в diff (PluginDraftDiffFile): binary/truncated → содержимое подавлено
export interface PluginDraftDiffFile {
  path: string;
  size: number;
  binary: boolean;
  truncated: boolean;
  lines: PluginDraftDiffLine[] | null;
}
// Секрет в diff (PluginDraftDiffSecret)
export interface PluginDraftDiffSecret {
  name: string;
  required: boolean;
}
// Разница манифеста (PluginDraftDiffManifest): права и секреты
export interface PluginDraftDiffManifest {
  permissionsAdded: string[];
  permissionsRemoved: string[];
  secretsAdded: PluginDraftDiffSecret[];
  secretsRemoved: PluginDraftDiffSecret[];
  secretsRequiredChanged: PluginDraftDiffSecret[];
}
// Сравнение черновика с установленной версией (GET /api/plugins/drafts/{slug}/diff)
export interface PluginDraftDiff {
  status: 'new' | 'updated';
  slug: string;
  id: string;
  version: string;
  installedVersion: string | null;
  // Суммарный потолок diff (1000 строк) превышен
  truncated: boolean;
  added: PluginDraftDiffFile[];
  removed: PluginDraftDiffFile[];
  modified: PluginDraftDiffFile[];
  // null, если статус "new"
  manifest: PluginDraftDiffManifest | null;
}
// Ответ «Принять» (POST /api/plugins/drafts/{slug}/accept)
export interface PluginDraftAcceptResult {
  id: string;
  // Человекочитаемое имя пакета для диалога согласия (title первой панели манифеста);
  // null/нет — панелей нет или старый сервер: имя выводится из id (humanizeId).
  displayName?: string | null;
  version: string;
  sha256: string;
  source: string;
  enabled: boolean;
  requested: string[];
  granted: string[];
  // true + newPermissions — только если обновление добавило новые запрошенные права
  consentRequired: boolean;
  newPermissions: string[];
  // Глеб MAJOR-1: права, которые переходят на новое содержимое при обновлении (пусто, если
  // не обновление). consentRequired=true и когда newPermissions пуст, но у установленного
  // плагина есть выданные — диалог показывает блок «уже выданные» и требует явного подтверждения.
  // Старый сервер поле не отдаёт → undefined → фронт считает [] (не падать).
  inheritedPermissions?: string[];
  // Глеб MAJOR 4: признак обновления уже установленного плагина (true) vs первая установка
  // (false). Старый сервер поле не отдаёт → undefined → фронт считает false (режим install).
  isUpdate?: boolean;
  // Версия, на которую плагин был установлен ДО этого accept (null при первой установке)
  previousVersion?: string | null;
  secrets: PluginSecretView[];
}
// Ответ «Отклонить» (POST /api/plugins/drafts/{slug}/reject)
export interface PluginDraftRejectResult {
  slug: string;
  rejectedAt: string;
  expiresAt: string;
}
// Ответ «Вернуть» (POST /api/plugins/drafts/{slug}/restore)
export interface PluginDraftRestoreResult {
  slug: string;
  restored: boolean;
}
