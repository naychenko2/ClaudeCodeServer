// Ядро реестра фронтовых подсистем: типы манифеста и слотов, хранилище и хуки.
//
// Идея пилота «физическая извлекаемость»: общие компоненты и страницы больше НЕ
// импортируют модули фич напрямую. Каркас объявляет слоты (точки расширения), а
// подсистема регистрирует в них свои вклады — рендер (компонент/узел) или
// действие/данные (функция/объект). Удаление папки подсистемы вместе с одной
// строкой её регистрации оставляет каркас собирающимся, а слоты — пустыми.
//
// Рантайм-разделение файлов не случайно: сам реестр НЕ импортирует фичи (иначе
// получился бы цикл импортов и обращение к хранилищу до его инициализации).
// Регистрация — побочным эффектом импорта модуля фичи, а строки регистрации
// собраны в публичном registry.ts. Само ядро импортируют и каркас (через
// registry.ts), и модули-регистраторы фич.

import { useMemo, useSyncExternalStore } from 'react';
import type { ComponentType, LazyExoticComponent, ReactNode } from 'react';
import type { AuthState, NoteDetail, Session } from '../../types';
import type { HubTabValue } from '../../components/hubTabsModel';
import { isSubsystemEnabled, subscribeSubsystems } from '../subsystems';

// Вклад в слот. Ровно два вида:
//  • render — компонент/узел (каркас зовёт с контекстом слота и рисует результат);
//  • action — функция/объект с данными и вызовами (каркас зовёт напрямую).
// Поле name различает несколько вкладов в одном слоте (например, четыре панели
// FileViewer живут в одном слоте file-viewer-panel).
export interface SlotContribution<C = never, A = Record<string, unknown>> {
  name?: string;
  order?: number;
  render?: (ctx: C) => ReactNode;
  action?: A;
}

// Пропсы страницы-раздела хаба: их получает ленивый компонент вкладки подсистемы.
export interface SubsystemTabProps {
  auth: AuthState;
  onLogout: () => void;
  onHubTab: (t: HubTabValue) => void;
}

export interface SubsystemManifest {
  key: string;
  title: string;
  icon?: ReactNode;
  order: number;
  tab?: { component: LazyExoticComponent<ComponentType<SubsystemTabProps>> };
  slots?: Record<string, SlotContribution[]>;
}

// ---- Контексты слотов ----
// Объявлены здесь, чтобы обе стороны (каркас и фича) видели один тип и не
// тянули типы друг друга напрямую.

export interface FileViewerNoteViewCtx {
  noteId: string;
  existingTitles: Set<string>;
  onWikilink: (target: string) => void;
  onSelectNote: (id: string) => void;
  onDeleted: () => void;
  isMobile?: boolean;
  onBack?: () => void;
  onOpenFileRef?: (path: string) => void;
  extraToolbar?: ReactNode;
}
export interface FileViewerNoteEditorCtx {
  filePath: string;
  value: string;
  onChange: (v: string) => void;
  onWikilink: (target: string) => void;
}
// Markdown-редактор в общем виде (например, поля описания/результата задачи):
// каркас отдаёт значение и обработчик, редактор приходит вкладом подсистемы.
export interface MarkdownEditorCtx {
  value: string;
  onChange: (v: string) => void;
  placeholder?: string;
  minHeight?: number;
}
export interface FileViewerNoteConnectionsCtx {
  note: NoteDetail;
  onOpenNote: (id: string, title: string) => void;
  onWikilink: (target: string) => void;
}
export interface FileViewerDocCommentsCtx {
  scope: string;
  docPath: string;
  content: string;
  isMobile?: boolean;
  onCounts?: (total: number, open: number) => void;
  panelTarget: HTMLElement | null;
  onDocLink?: (href: string) => void;
  resolveImageSrc: (src: string) => string | undefined;
}

export interface FileExplorerFolderIconCtx { size?: number }
export interface FileExplorerNoteDialogCtx {
  defaults: { source: string; folder?: string; file?: string };
  onClose: () => void;
  onCreated: (id: string) => void;
}

export interface WorkspacePanelNotesCtx {
  projectId: string;
  activeFilePath?: string | null;
  onOpenFile: (path: string) => void;
}

export interface HomeWidgetNotesCtx { onHubTab: (t: HubTabValue) => void }

export interface ActionButtonProps {
  icon: ReactNode;
  label: string;
  onClick: () => void;
  disabled?: boolean;
}
export interface QuickActionNoteCtx { ActionButton: ComponentType<ActionButtonProps> }

export interface ChatItemSaveNoteCtx {
  text: string;
  projectId?: string | null;
  online: boolean;
}
// Данные карточки изменённого файла: заметка ли это (match) и как её открыть/нарисовать.
export interface ChatItemFileChangedApi {
  match: (path: string) => boolean;
  open: (path: string) => void;
  icon: ReactNode;
}

export interface ChatHeaderSummaryCtx {
  session: Session;
  hasMessages: boolean;
  online: boolean;
}
export interface ChatHeaderMenuItemCtx { run: () => void }

export interface PlanChipCtx { plan: string; projectId?: string }
export interface PlanButtonCtx { plan: string; online: boolean }

// Action-вклады (поведенческие).
export interface GlobalSearchNoteApi { open: (id: string) => void }
export interface AiNoteOpenerApi { openNote: (id: string) => void }

// ---- Хранилище ----
const _manifests: SubsystemManifest[] = [];
let _version = 0;
const _listeners = new Set<() => void>();

function emit() {
  _version++;
  _listeners.forEach(fn => fn());
}

export function registerSubsystem(m: SubsystemManifest) {
  // Дедупликация по ключу: при dev-HMR модуль-регистратор исполняется повторно,
  // и без замены в реестре оказались бы две «Заметки» (двойные слоты/вкладки).
  const i = _manifests.findIndex(x => x.key === m.key);
  if (i >= 0) _manifests[i] = m;
  else _manifests.push(m);
  emit();
}

export function getRegisteredSubsystems(): SubsystemManifest[] {
  return _manifests;
}

export function getSubsystem(key: string): SubsystemManifest | undefined {
  return _manifests.find(m => m.key === key);
}

export function getSubsystemTab(key: string): LazyExoticComponent<ComponentType<SubsystemTabProps>> | undefined {
  return getSubsystem(key)?.tab?.component;
}

// Вклады слота от ВКЛЮЧЁННЫХ подсистем: выключенная подсистема не отдаёт ни один
// вклад (гейт — на чтении, а не только внутри компонентов).
export function getSlotContributions<C = never, A = Record<string, unknown>>(slot: string): SlotContribution<C, A>[] {
  const out: SlotContribution<C, A>[] = [];
  for (const m of _manifests) {
    if (!isSubsystemEnabled(m.key)) continue;
    const list = m.slots?.[slot];
    if (list) out.push(...(list as unknown as SlotContribution<C, A>[]));
  }
  return out.sort((a, b) => (a.order ?? 0) - (b.order ?? 0));
}

export function getSlotItem<C = never, A = Record<string, unknown>>(slot: string, name: string): SlotContribution<C, A> | undefined {
  return getSlotContributions<C, A>(slot).find(c => c.name === name);
}

export function getSlotAction<A = Record<string, unknown>>(slot: string, name: string): A | undefined {
  return getSlotItem<never, A>(slot, name)?.action;
}

// Подписка каркаса на изменение состава вкладов. Тумблеры подсистем меняют
// видимость вкладов (read-time гейт в getSlotContributions), поэтому на каждое
// изменение стора подсистем поднимаем версию и оповещаем подписчиков — иначе
// useSyncExternalStore вернул бы прежний снапшот и слот не перерисовался бы.
subscribeSubsystems(emit);

// Примитивы подписки — база хуков ниже. Экспортируются, чтобы тест мог проверить
// пересчёт вкладов на смену тумблера без рендера React.
export function subscribeRegistry(fn: () => void) {
  _listeners.add(fn);
  return () => { _listeners.delete(fn); };
}
export function getRegistryVersion() { return _version; }

export function useSlot<C = never, A = Record<string, unknown>>(slot: string): SlotContribution<C, A>[] {
  const version = useSyncExternalStore(subscribeRegistry, getRegistryVersion, getRegistryVersion);
  // eslint-disable-next-line react-hooks/exhaustive-deps -- version в deps: пересчёт при изменении реестра/тумблеров
  return useMemo(() => getSlotContributions<C, A>(slot), [slot, version]);
}

export function useSlotItem<C = never, A = Record<string, unknown>>(slot: string, name: string): SlotContribution<C, A> | undefined {
  const version = useSyncExternalStore(subscribeRegistry, getRegistryVersion, getRegistryVersion);
  // eslint-disable-next-line react-hooks/exhaustive-deps -- version в deps: пересчёт при изменении реестра/тумблеров
  return useMemo(() => getSlotItem<C, A>(slot, name), [slot, name, version]);
}

// Список зарегистрированных подсистем с подпиской: перерисовка при регистрации
// новой подсистемы и при смене тумблеров включённости. База для таббара хаба.
export function useRegisteredSubsystems(): SubsystemManifest[] {
  const version = useSyncExternalStore(subscribeRegistry, getRegistryVersion, getRegistryVersion);
  // eslint-disable-next-line react-hooks/exhaustive-deps -- version в deps: пересчёт при регистрации/тумблерах
  return useMemo(() => getRegisteredSubsystems(), [version]);
}
