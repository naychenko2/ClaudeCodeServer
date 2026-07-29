// Единый реестр панелей воркспейса: ключи, мета (иконка + заголовок), домашняя
// зона и групповые признаки.
//
// Панель — сущность ВОРКСПЕЙСА, а не зоны: зона (левая/правая рельса) это просто
// место, где панель сейчас лежит, поэтому одна и та же панель обязана иметь один
// ключ и одну мету независимо от стороны экрана. Раньше наборы были раздельными
// (LEFT_PANEL_KEYS / RIGHT_PANEL_KEYS со своими PANEL_META), и в них завелись
// синонимы: `personas` слева и `team` справа — это одна панель «Команда» с одной
// иконкой, а `tools` слева дублировал пару `terminal` + `preview`. Перенос панели
// между зонами при таком раздвоении невозможен в принципе.
//
// ВНИМАНИЕ: в components/artifacts/meta.tsx живёт СВОЙ, несвязанный PanelKey —
// категории артефактов сессии (plan/todos/notes/comments/…), которыми пользуется
// panelBadge. Значения частично пересекаются намеренно (plan/agents/context), но
// это разные типы: там, где импортируются оба, брать один из них под алиасом.
import {
  ClipboardList, FolderTree, GitCompare, ListTodo, Bot, User, Users,
  SquareTerminal, MonitorPlay, Network, MessageCircle, type LucideIcon,
} from 'lucide-react';

// Сторона экрана. Зон ровно две, и обе равноправны: любая панель может лежать
// в любой из них.
export type Zone = 'left' | 'right';

// Все панели воркспейса. Порядок = порядок иконок в рельсе сверху вниз (внутри
// своей группы, см. SESSION_KEYS).
export const PANEL_KEYS = [
  'chats', 'files', 'changes', 'tasks', 'graph', 'team', 'terminal', 'preview',
  'plan', 'agents', 'context',
] as const;
export type PanelKey = typeof PANEL_KEYS[number];

// Иконка и заголовок панели — общие для обеих зон.
export const PANEL_META: Record<PanelKey, { title: string; Icon: LucideIcon }> = {
  chats:    { title: 'Чаты',      Icon: MessageCircle },
  files:    { title: 'Файлы',     Icon: FolderTree },
  changes:  { title: 'Изменения', Icon: GitCompare },
  tasks:    { title: 'Задачи',    Icon: ListTodo },
  graph:    { title: 'Граф',      Icon: Network },
  team:     { title: 'Команда',   Icon: Users },
  terminal: { title: 'Терминал',  Icon: SquareTerminal },
  preview:  { title: 'Preview',   Icon: MonitorPlay },
  plan:     { title: 'План',      Icon: ClipboardList },
  agents:   { title: 'Агенты',    Icon: Bot },
  // 'context' — досье персоны-собеседника (память/привязки/recall)
  context:  { title: 'Персона',   Icon: User },
};

// Домашняя зона панели: где её иконка стоит, пока панель закрыта, и где она
// открывается по умолчанию. Открытая панель показывает иконку в ТОЙ зоне, где
// лежит, — то есть иконка ездит вместе с панелью, а закрытие возвращает её домой.
export const PANEL_HOME: Record<PanelKey, Zone> = {
  chats: 'left',
  files: 'right',
  changes: 'right',
  tasks: 'right',
  graph: 'right',
  team: 'right',
  terminal: 'right',
  preview: 'right',
  plan: 'right',
  agents: 'right',
  context: 'right',
};

// Панели ТЕКУЩЕЙ СЕССИИ: их видимость в рельсе считается не по наличию контента,
// а по артефактам сессии (План — если был план, Агенты — если есть содержимое,
// Персона — если собеседник персона). В рельсе они отделены сепаратором от
// инструментов проекта.
export const SESSION_KEYS: readonly PanelKey[] = ['plan', 'agents', 'context'];

// Инструменты проекта — всё, что не относится к сессии. Первая группа рельсы.
export const PROJECT_KEYS: readonly PanelKey[] = PANEL_KEYS.filter(k => !SESSION_KEYS.includes(k));

// Панели, доступные только при включённых инструментах проекта.
export const TOOLS_KEYS: readonly PanelKey[] = ['terminal', 'preview'];

export function isPanelKey(v: unknown): v is PanelKey {
  return typeof v === 'string' && (PANEL_KEYS as readonly string[]).includes(v);
}

// Переименования ключей при чтении старых раскладок из localStorage. Синонимы
// схлопнуты в одну панель, поэтому сохранённые у пользователей ключи левой рельсы
// надо перевести на общие; `tools` пары-наследника не имеет и отбрасывается —
// его роль закрывают `terminal` и `preview`, которые пользователь откроет сам.
const LEGACY_KEY_ALIASES: Record<string, PanelKey> = {
  personas: 'team',
};

// Ключ старой раскладки → ключ реестра (null — панель упразднена).
export function migrateLegacyKey(v: unknown): PanelKey | null {
  if (typeof v !== 'string') return null;
  const aliased = LEGACY_KEY_ALIASES[v] ?? v;
  return isPanelKey(aliased) ? aliased : null;
}
