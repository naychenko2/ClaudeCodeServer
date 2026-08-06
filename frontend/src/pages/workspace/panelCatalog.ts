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
  BookOpen, BookOpenText, ClipboardList, FolderTree, GitCompare, ListTodo, Bot, User, Users,
  SquareTerminal, MonitorPlay, Network, MessageCircle, NotebookPen, Library, Puzzle,
  TableOfContents,
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
  // Порядок рельсы: сначала работа с проектом «здесь и сейчас» — дерево файлов,
  // его изменения, задачи по ним; дальше справочное (документация, знания, граф)
  // и командное
  'chats', 'files', 'changes', 'tasks', 'docs', 'knowledge', 'graph', 'team', 'skills', 'terminal', 'preview',
  'plan', 'agents', 'context',
  'toc',
  // Панели разделов хаба
  'notesList', 'notesGraph', 'knowledgeList', 'personasList', 'projectGroups',
] as const;
export type PanelKey = typeof PANEL_KEYS[number];

// Иконка и заголовок панели — общие для обеих зон.
export const PANEL_META: Record<PanelKey, { title: string; Icon: LucideIcon }> = {
  chats:    { title: 'Чаты',      Icon: MessageCircle },
  files:    { title: 'Файлы',     Icon: FolderTree },
  // «Документы» рядом с «Файлами»: обе про содержимое репозитория, но Файлы — дерево
  // для работы с кодом, а Документы — документация как связный корпус (README + docs/**).
  // Раскрытая книга с текстом: читаемая документация. Родственный BookOpen занят
  // «Знаниями» (панель ниже, lib/ai/actions) — здесь строки текста внутри разводят их
  // между собой; FileText отдан заметкам.
  docs:     { title: 'Документация', Icon: BookOpenText },
  // База знаний ЭТОГО проекта: что проиндексировано в Dify и доступно ассистенту
  // семантическим поиском. Не путать с knowledgeList («Базы») — тот раздел хаба
  // показывает ВСЕ датасеты пользователя. Пара ровно того же рода, что team
  // (персоны проекта) и personasList (все персоны), поэтому и ключи разной длины.
  knowledge: { title: 'Знания',   Icon: BookOpen },
  changes:  { title: 'Изменения', Icon: GitCompare },
  tasks:    { title: 'Задачи',    Icon: ListTodo },
  graph:    { title: 'Граф',      Icon: Network },
  team:     { title: 'Команда',   Icon: Users },
  // Навыки и агенты рабочей папки (.claude/skills, .claude/agents) — файловые
  // умения CLI, а не персоны-контакты: поэтому отдельная панель рядом с «Командой»,
  // а не вкладка внутри неё. Bot занят сессионными «Агентами» (артефакт хода),
  // отсюда деталь пазла — навык как подключаемый кусок умения.
  skills:   { title: 'Навыки',    Icon: Puzzle },
  terminal: { title: 'Терминал',  Icon: SquareTerminal },
  // Ключ остался preview (он лежит в сохранённых раскладках), подпись — «Сервисы»
  preview:  { title: 'Сервисы',   Icon: MonitorPlay },
  plan:     { title: 'План',      Icon: ClipboardList },
  agents:   { title: 'Агенты',    Icon: Bot },
  // 'context' — досье персоны-собеседника (память/привязки/recall)
  context:  { title: 'Персона',   Icon: User },
  // Оглавление документа, открытого в ЦЕНТРЕ. Панель существует, только пока там md,
  // — отсюда своя группа рельсы (CENTER_KEYS), а не соседство с содержимым проекта:
  // «Файлы» и «Документация» показывают репозиторий, эта — то, что сейчас читают.
  toc:      { title: 'Оглавление', Icon: TableOfContents },

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
  chats: 'left',
  files: 'right',
  docs: 'right',
  knowledge: 'right',
  changes: 'right',
  tasks: 'right',
  graph: 'right',
  team: 'right',
  skills: 'right',
  terminal: 'right',
  preview: 'right',
  plan: 'right',
  agents: 'right',
  context: 'right',
  toc: 'right',
  // Разделы хаба выросли из левого сайдбара — там их дом
  notesList: 'left',
  notesGraph: 'left',
  knowledgeList: 'left',
  personasList: 'left',
  projectGroups: 'left',
};

// Наборы ключей по экранам — что вообще доступно в этой рельсе (проп allowedKeys)
export const WORKSPACE_KEYS: readonly PanelKey[] = [
  'chats', 'files', 'changes', 'tasks', 'docs', 'knowledge', 'graph', 'team', 'skills', 'terminal', 'preview',
  'plan', 'agents', 'context', 'toc',
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

// Панели ЦЕНТРАЛЬНОЙ ОБЛАСТИ: показывают не проект и не сессию, а то, что открыто
// в центре прямо сейчас. Живут ровно столько, сколько живёт их источник: закрыли
// документ — панель исчезла вместе с кнопкой (контент стал null, см. keyAvailable),
// открыли другой — вернулась на своё место в раскладке.
//
// Категория отдельная от сессионных (у тех видимость считается по артефактам хода,
// здесь — по тому, что открыто в центре), но в РЕЛЬСЕ они идут одной группой, без
// черты между собой: и те и другие отвечают на вопрос «что сейчас перед глазами».
export const CENTER_KEYS: readonly PanelKey[] = ['toc'];

// Панели, доступные только при включённых инструментах проекта. В рельсе они идут
// СВОЕЙ группой: остальные панели показывают содержимое проекта, эти запускают в нём
// процессы — и выключаются вместе с инструментами, унося и свой разделитель.
export const TOOLS_KEYS: readonly PanelKey[] = ['terminal', 'preview'];

// Содержимое проекта и панели разделов: всё, что не относится ни к текущей сессии,
// ни к запуску процессов, ни к центральной области. Первая группа рельсы, дальше
// инструменты, сессионные и центральные — разделители рисует сама рельса.
export const PROJECT_KEYS: readonly PanelKey[] = PANEL_KEYS.filter(
  k => !SESSION_KEYS.includes(k) && !TOOLS_KEYS.includes(k) && !CENTER_KEYS.includes(k),
);

// Реестра панелей «полной высоты» здесь нет намеренно. Потребность в высоте зависит
// не от ключа, а от состояния самой панели («Документация» тянется до низа только с
// включённой нижней зоной превью), поэтому её объявляет панель — см. panelFill.ts.

export function isPanelKey(v: unknown): v is PanelKey {
  return typeof v === 'string' && (PANEL_KEYS as readonly string[]).includes(v);
}

// Переименования ключей при чтении старых раскладок из localStorage. Синонимы
// схлопнуты в одну панель, поэтому сохранённые у пользователей ключи левой рельсы
// надо перевести на общие; `tools` пары-наследника не имеет и отбрасывается —
// его роль закрывают `terminal` и `preview`, которые пользователь откроет сам.
// Так же отбрасывается `projects`: панель-переключатель проектов упразднена, её
// заменил док проектов второй левой рельсой (features/projects/ProjectRail).
const LEGACY_KEY_ALIASES: Record<string, PanelKey> = {
  personas: 'team',
};

// Ключ старой раскладки → ключ реестра (null — панель упразднена).
export function migrateLegacyKey(v: unknown): PanelKey | null {
  if (typeof v !== 'string') return null;
  const aliased = LEGACY_KEY_ALIASES[v] ?? v;
  return isPanelKey(aliased) ? aliased : null;
}
