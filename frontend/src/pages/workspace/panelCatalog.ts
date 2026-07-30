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
  BookOpenText, ClipboardList, FolderTree, GitCompare, ListTodo, Bot, User, Users,
  SquareTerminal, MonitorPlay, Network, MessageCircle, NotebookPen, Library, LayoutGrid,
  type LucideIcon,
} from 'lucide-react';

// Сторона экрана. Зон ровно две, и обе равноправны: любая панель может лежать
// в любой из них.
export type Zone = 'left' | 'right';

// Все панели продукта. Порядок = порядок иконок в рельсе сверху вниз (внутри
// своей группы, см. SESSION_KEYS). Какие из них доступны на конкретном экране,
// решает сам экран (проп allowedKeys у PanelZone): в воркспейсе — инструменты
// проекта и сессии, в разделах хаба — их собственные панели.
export const PANEL_KEYS = [
  'projects', 'chats', 'files', 'docs', 'changes', 'tasks', 'graph', 'team', 'terminal', 'preview',
  'plan', 'agents', 'context',
  // Панели разделов хаба
  'notesList', 'notesGraph', 'knowledgeList', 'personasList', 'projectGroups',
] as const;
export type PanelKey = typeof PANEL_KEYS[number];

// Иконка и заголовок панели — общие для обеих зон.
export const PANEL_META: Record<PanelKey, { title: string; Icon: LucideIcon }> = {
  // Переключатель проектов: сменить проект, не уходя из воркспейса. Иконка та же,
  // что у «Всех проектов» в палитре и сайдбаре раздела — одна сущность, один знак.
  projects: { title: 'Проекты',   Icon: LayoutGrid },
  chats:    { title: 'Чаты',      Icon: MessageCircle },
  files:    { title: 'Файлы',     Icon: FolderTree },
  // «Документы» рядом с «Файлами»: обе про содержимое репозитория, но Файлы — дерево
  // для работы с кодом, а Документы — документация как связный корпус (README + docs/**).
  // Раскрытая книга с текстом: читаемая документация. Родственный BookOpen занят
  // «Знаниями» (lib/ai/actions, KnowledgePanel, FileExplorer) — здесь строки текста
  // внутри разводят их между собой; FileText отдан заметкам.
  docs:     { title: 'Документация', Icon: BookOpenText },
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

  // Разделы хаба. Ключи намеренно длиннее воркспейсных: рядом живут похожие по
  // смыслу панели проекта, и путать их нельзя. personasList — все персоны
  // пользователя (раздел «Персоны»), тогда как team — персоны конкретного
  // проекта; notesGraph — граф ЗАМЕТОК, а graph — граф зависимостей кода.
  notesList:     { title: 'Заметки',  Icon: NotebookPen },
  notesGraph:    { title: 'Граф',     Icon: Network },
  knowledgeList: { title: 'Базы',     Icon: Library },
  personasList:  { title: 'Персоны',  Icon: Users },
  projectGroups: { title: 'Группы',   Icon: FolderTree },
};

// Домашняя зона панели: где её иконка стоит, пока панель закрыта, и где она
// открывается по умолчанию. Открытая панель показывает иконку в ТОЙ зоне, где
// лежит, — то есть иконка ездит вместе с панелью, а закрытие возвращает её домой.
export const PANEL_HOME: Record<PanelKey, Zone> = {
  projects: 'left',
  chats: 'left',
  files: 'right',
  docs: 'right',
  changes: 'right',
  tasks: 'right',
  graph: 'right',
  team: 'right',
  terminal: 'right',
  preview: 'right',
  plan: 'right',
  agents: 'right',
  context: 'right',
  // Разделы хаба выросли из левого сайдбара — там их дом
  notesList: 'left',
  notesGraph: 'left',
  knowledgeList: 'left',
  personasList: 'left',
  projectGroups: 'left',
};

// Наборы ключей по экранам — что вообще доступно в этой рельсе (проп allowedKeys)
export const WORKSPACE_KEYS: readonly PanelKey[] = [
  'projects', 'chats', 'files', 'docs', 'changes', 'tasks', 'graph', 'team', 'terminal', 'preview',
  'plan', 'agents', 'context',
];
// Раздел «Чаты»: список чатов плюс панели активной сессии (проекта там нет)
export const CHAT_KEYS: readonly PanelKey[] = ['chats', 'plan', 'agents', 'context'];
export const NOTES_KEYS: readonly PanelKey[] = ['notesList', 'notesGraph'];
export const KNOWLEDGE_KEYS: readonly PanelKey[] = ['knowledgeList'];
export const PERSONAS_KEYS: readonly PanelKey[] = ['personasList'];
export const PROJECTS_KEYS: readonly PanelKey[] = ['projectGroups'];

// Панели ТЕКУЩЕЙ СЕССИИ: их видимость в рельсе считается не по наличию контента,
// а по артефактам сессии (План — если был план, Агенты — если есть содержимое,
// Персона — если собеседник персона). В рельсе они отделены сепаратором от
// инструментов проекта.
export const SESSION_KEYS: readonly PanelKey[] = ['plan', 'agents', 'context'];

// Всё, что не относится к текущей сессии: инструменты проекта и панели разделов.
// Первая группа рельсы — от сессионной её отделяет сепаратор.
export const PROJECT_KEYS: readonly PanelKey[] = PANEL_KEYS.filter(k => !SESSION_KEYS.includes(k));

// Панели, доступные только при включённых инструментах проекта.
export const TOOLS_KEYS: readonly PanelKey[] = ['terminal', 'preview'];

// Панели ФИКСИРОВАННОЙ ВЫСОТЫ: их содержимое не тянется (строка-переключатель
// проектов), и растянутая карточка дала бы полколонки пустоты под одной строкой.
// Такая панель всегда стоит по контенту — и в одиночку, и в общей колонке; высоту
// между собой делят остальные, а хендл ресайза рядом с ней не рисуется: тянуть
// нечего. Первая такая панель — «Проекты».
export const FIXED_HEIGHT_KEYS: readonly PanelKey[] = ['projects'];

export function isFixedHeight(k: PanelKey): boolean {
  return FIXED_HEIGHT_KEYS.includes(k);
}

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
