import {
  AppWindow, Bell, Bot, Database, FolderOpen, FolderSearch, GitBranch,
  Globe, LayoutGrid, ListTodo, MessageSquare, MessagesSquare, Network, NotebookPen,
  StickyNote, Trash2, UserCog, Users, Wrench,
} from 'lucide-react';
import type { LucideIcon } from 'lucide-react';
import { C } from '../../lib/design';

// Метаданные пикера инструментов (шаг «Цель» для привязки типа tool): группы,
// их тона и иконки каждого ключа каталога. Классификация ключей зеркалит
// PersonaBindingsService (ServerKeys / PresetKeys / базовые) — при добавлении
// ключа в ToolCatalog на бэке сюда попадает иконка и группа; неизвестные ключи
// не ломают пикер: уходят в группу «Прочее» с нейтральным тоном.

export type ToolGroupKey = 'base' | 'mcp' | 'workspace' | 'specialty' | 'danger' | 'other';

export const TOOL_GROUPS: Record<ToolGroupKey, { title: string; bg: string; fg: string }> = {
  base:      { title: 'Базовые',            bg: C.successBg,  fg: C.successText },
  mcp:       { title: 'MCP-серверы',        bg: C.infoBg,     fg: C.info },
  workspace: { title: 'Рабочее пространство', bg: C.planLight, fg: C.plan },
  specialty: { title: 'По специальности',   bg: C.warningBg,  fg: C.warning },
  danger:    { title: 'Опасные',            bg: C.dangerBg,   fg: C.dangerText },
  other:     { title: 'Прочее',             bg: C.bgSelected, fg: C.textSecondary },
};

// Порядок групп в пикере («Прочее» — всегда последняя, только если есть неизвестные ключи)
export const TOOL_GROUP_ORDER: ToolGroupKey[] = ['base', 'mcp', 'workspace', 'specialty', 'danger', 'other'];

const TOOL_GROUP_OF: Record<string, ToolGroupKey> = {
  tasks: 'base', notes: 'base', web: 'base',
  personas: 'mcp', consultants: 'mcp', codegraph: 'mcp', notifications: 'mcp', widgets: 'mcp',
  projects: 'workspace', chats: 'workspace', files: 'workspace', knowledge: 'workspace',
  git: 'specialty', kb: 'specialty', 'personas-manage': 'specialty',
  'personas-automation': 'specialty', 'notes-annotations': 'specialty', browser: 'specialty',
  destructive: 'danger',
};

export function toolGroupOf(key: string): ToolGroupKey {
  return TOOL_GROUP_OF[key.toLowerCase()] ?? 'other';
}

// Иконка каждого инструмента (lucide). Фолбэк для неизвестных ключей — Wrench.
const TOOL_ICONS: Record<string, LucideIcon> = {
  tasks: ListTodo,
  notes: NotebookPen,
  web: Globe,
  personas: Users,
  consultants: MessagesSquare,
  codegraph: Network,
  notifications: Bell,
  widgets: LayoutGrid,
  projects: FolderOpen,
  chats: MessageSquare,
  files: FolderSearch,
  knowledge: Database,
  destructive: Trash2,
  git: GitBranch,
  kb: Database,
  'personas-manage': UserCog,
  'personas-automation': Bot,
  'notes-annotations': StickyNote,
  browser: AppWindow,
};

export function toolIcon(key: string): LucideIcon {
  return TOOL_ICONS[key.toLowerCase()] ?? Wrench;
}

// Подпись дефолтного состояния (без привязки) — по полям defaultEnabled/defaultOrigin
// из binding-targets?type=tool&personaId=. null — данных нет (старый бэк / без personaId).
export function toolDefaultCaption(t: { defaultEnabled?: boolean | null; defaultOrigin?: 'settings' | 'role' | null }): { text: string; fg: string } | null {
  if (t.defaultEnabled == null) return null;
  if (t.defaultEnabled) {
    return t.defaultOrigin === 'role'
      ? { text: 'включён по роли', fg: C.successText }
      : { text: 'включён по умолчанию', fg: C.successText };
  }
  return { text: 'выключен', fg: C.textMuted };
}
