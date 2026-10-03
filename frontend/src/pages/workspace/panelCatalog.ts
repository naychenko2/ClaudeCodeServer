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
// panelBadge. Значения частично пересекаются намеренно (plan/agents/context/notes:
// здешний notes — панель заметок ПРОЕКТА, тамошний — заметки-артефакты хода), но
// это разные типы: там, где импортируются оба, брать один из них под алиасом.
import {
  BookOpen, BookOpenText, ClipboardList, Contact, FolderTree, GitCompare, ListTodo, Bot, User, Users,
  SquareTerminal, AppWindow, MonitorPlay, Network, MessageCircle, NotebookPen, StickyNote, Library, Puzzle,
  TableOfContents, Lightbulb, DraftingCompass, Image as ImageIcon, AudioLines, SlidersHorizontal, Mic, Clapperboard,
  type LucideIcon,
} from 'lucide-react';
import type { BadgeTone } from '../../components/ui/CountBadge';
import { genPanelKeys as liveGenPanelKeys } from '../../lib/genPanelKeys';

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
  'chats', 'files', 'changes', 'tasks', 'docs', 'dossiers', 'knowledge', 'notes', 'graph', 'arch', 'team', 'skills', 'terminal', 'preview',
  'plan', 'agents', 'context',
  'toc',
  // Панели подсистем (слот workspace-panel-def): ключ зарезервирован здесь, тело и
  // доступность — у подсистемы; выключена подсистема — нет содержимого, нет и кнопки
  'characters',
  // «Голоса» редактора звука: библиотека voices/ проекта отдельной панелью (при флаге composer-context-row)
  'voices',
  // Панели генерации (ADR-021 §3): «Картинки» и «Звук» — вклады вертикалей тем же
  // слотом; справа одновременно живёт только одна из них (см. EXCLUSIVE_PANEL_SETS)
  'images', 'sound',
  // «Видео» (ADR-022) — вклад вертикали тем же слотом; строка контекста его пока не заменяет
  'videoEditor',
  // Единая панель «Контекст» чата (ADR-023 §Д1): при флаге composer-context-row заменяет обе
  // панели генерации. Рисует её оболочка (ContextPanel), а не вертикаль. Ключ `context` занят
  // панелью «Персона» и сохранён в раскладках — отсюда отдельный ключ
  'chatContext',
  // Фоновый эфир рядом с работой: живёт и в проекте, и в разделе «Чаты».
  // Каталог каналов панелью НЕ является: он открывается в центральном острове
  // (кнопка в шапке этой панели), потому что каналы выбирают по обложкам,
  // а в рельсе их не разглядеть.
  'video',
  // Панели разделов хаба
  'notesList', 'notesGraph', 'knowledgeList', 'personasList', 'projectGroups',
] as const;
export type PanelKey = typeof PANEL_KEYS[number];

// Данные кружков-индикаторов на кнопке панели рельсы. Источник — WorkspacePage
// (changes/tasks/terminal/preview), useSessionPanels (plan/agents) и ChatsPage
// (chats); собирает их PanelZone в RailItem. primary — основной кружок (оранжевый,
// правый верхний угол), secondary — второй (серый, правый нижний; сейчас это
// незапушенные коммиты на «Изменениях»), hint — расшифровка в тултипе-плашке.
//
// hint — либо строка (одно значение, рисуется с оранжевой точкой как primary),
// либо список линий: каждая со своим тоном под соответствующий кружок на иконке
// (accent — оранжевый/primary, muted — серый/secondary, warning — жёлтый). Так в
// тултипе «Изменений» две строки: ●(оранж) N незафиксированных и ●(сер) N неопубликованных.
export interface HintLine {
  text: string;
  tone?: BadgeTone;   // дефолт 'accent'
}
export type RailHint = string | readonly HintLine[];

export interface RailBadgeInfo {
  primary?: number;
  secondary?: number;
  // Тон каждого кружка (дефолт: primary=accent/оранжевый, secondary=muted/серый).
  // «Изменения» инвертирует: незафиксированные файлы — серые (норма, рабочее состояние),
  // неопубликованные коммиты — оранжевые (требуют пуша)
  primaryTone?: BadgeTone;
  secondaryTone?: BadgeTone;
  // Точка без числа: не «сколько чего-то», а «что-то ИДЁТ прямо сейчас» (эфир в
  // «Видео»). Числовой кружок здесь врал бы — считать нечего, а «1» читается как счётчик.
  dot?: BadgeTone;
  hint?: RailHint;
}

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
  // Заметки ТЕКУЩЕГО проекта (физические .md в notes/ репы) — пара к разделу хаба
  // notesList («Заметки» — все источники), того же рода, что knowledge/knowledgeList
  // и team/personasList. NotebookPen занят разделом хаба — здесь StickyNote.
  notes:    { title: 'Заметки',   Icon: StickyNote },
  // «История решений» (рабочее имя change-dossiers): «зачем менялся код, что решили,
  // что отвергли» — записи у файла, привязанные к коммитам из чата/задачи. Lightbulb
  // (идея/решение), а не History — та иконка уже занята вкладкой «История» (git-лог
  // файла) в FileViewer, и рядом друг с другом они читались бы как одно и то же
  dossiers: { title: 'История решений', Icon: Lightbulb },
  // База знаний ЭТОГО проекта: что проиндексировано в Dify и доступно ассистенту
  // семантическим поиском. Не путать с knowledgeList («Базы») — тот раздел хаба
  // показывает ВСЕ датасеты пользователя. Пара ровно того же рода, что team
  // (персоны проекта) и personasList (все персоны), поэтому и ключи разной длины.
  knowledge: { title: 'Знания',   Icon: BookOpen },
  changes:  { title: 'Изменения', Icon: GitCompare },
  tasks:    { title: 'Задачи',    Icon: ListTodo },
  graph:    { title: 'Граф',      Icon: Network },
  // «Архитектура» (C4-модель Viaduct, есть, когда загружен модуль architecture) — сосед «Графа» по смыслу:
  // граф — код «как есть», архитектура — «как задумано». Чертёжный циркуль — инструмент
  // архитектора: проектирование, а не содержимое. Boxes читался как «кубики/склад» и занят
  // «Командным спринтом»; Layers/LayoutDashboard/Shapes/SquareStack уже заняты в продукте
  arch:     { title: 'Архитектура', Icon: DraftingCompass },
  team:    { title: 'Команда',   Icon: Users },
  // Навыки и агенты рабочей папки (.claude/skills, .claude/agents) — файловые
  // умения CLI, а не персоны-контакты: поэтому отдельная панель рядом с «Командой»,
  // а не вкладка внутри неё. Bot занят сессионными «Агентами» (артефакт хода),
  // отсюда деталь пазла — навык как подключаемый кусок умения.
  skills:   { title: 'Навыки',    Icon: Puzzle },
  terminal: { title: 'Терминал',  Icon: SquareTerminal },
  // Ключ остался preview (он лежит в сохранённых раскладках), подпись — «Сервисы»
  preview:  { title: 'Сервисы',   Icon: AppWindow },
  video:    { title: 'Эфир',      Icon: MonitorPlay },
  plan:     { title: 'План',      Icon: ClipboardList },
  agents:   { title: 'Агенты',    Icon: Bot },
  // 'context' — досье персоны-собеседника (память/привязки/recall)
  context:  { title: 'Персона',   Icon: User },
  // Оглавление документа, открытого в ЦЕНТРЕ. Панель существует, только пока там md,
  // — отсюда своя группа рельсы (CENTER_KEYS), а не соседство с содержимым проекта:
  // «Файлы» и «Документация» показывают репозиторий, эта — то, что сейчас читают.
  toc:      { title: 'Оглавление', Icon: TableOfContents },
  // Персонажи редактора картинок (модуль image-editor): люди с фото для генераций
  characters: { title: 'Персонажи', Icon: Contact },
  // Голоса редактора звука (модуль audio-editor): библиотека voices/ проекта
  voices: { title: 'Голоса', Icon: Mic },
  // Панели генерации: заголовок и иконку в рельсе отдаёт вклад, здесь — запасные
  images:   { title: 'Картинки',  Icon: ImageIcon },
  sound:    { title: 'Звук',      Icon: AudioLines },
  videoEditor: { title: 'Видео',  Icon: Clapperboard },
  chatContext: { title: 'Контекст', Icon: SlidersHorizontal },

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
  notes: 'right',
  dossiers: 'right',
  knowledge: 'right',
  changes: 'right',
  tasks: 'right',
  graph: 'right',
  arch: 'right',
  team: 'right',
  skills: 'right',
  terminal: 'right',
  // Справа, как «Сервисы»: слева живёт список чатов, а эфир рядом с ним отнимал бы
  // место у навигации — смотрят его сбоку от ленты, а не вместо неё
  video: 'right',
  preview: 'right',
  plan: 'right',
  agents: 'right',
  context: 'right',
  toc: 'right',
  characters: 'right',
  voices: 'right',
  images: 'right',
  sound: 'right',
  videoEditor: 'right',
  chatContext: 'right',
  // Разделы хаба выросли из левого сайдбара — там их дом
  notesList: 'left',
  notesGraph: 'left',
  knowledgeList: 'left',
  personasList: 'left',
  projectGroups: 'left',
};

// Наборы ключей по экранам — что вообще доступно в этой рельсе (проп allowedKeys)
// Панели генерации в наборе зависят от флага composer-context-row (ctx): с флагом «Картинок» и «Звука»
// в рельсе нет, вместо них одна «Контекст»; без флага набор прежний
const GEN_LEGACY: readonly PanelKey[] = ['images', 'sound', 'videoEditor'];
const GEN_CONTEXT: readonly PanelKey[] = ['chatContext', 'videoEditor'];
const genKeysFor = (ctx: boolean): readonly PanelKey[] => (ctx ? GEN_CONTEXT : GEN_LEGACY);

const workspaceBase = (ctx: boolean): readonly PanelKey[] => [
  'chats', 'files', 'changes', 'tasks', 'docs', 'dossiers', 'knowledge', 'notes', 'graph', 'arch', 'team', 'skills', 'terminal', 'preview',
  'plan', 'agents', 'context', 'toc', 'video', 'characters', 'voices',
  ...genKeysFor(ctx),
];
export const WORKSPACE_KEYS: readonly PanelKey[] = workspaceBase(false);
const WORKSPACE_KEYS_CTX: readonly PanelKey[] = workspaceBase(true);
// Ссылка стабильна: зона держит набор в зависимостях эффектов
export const workspaceKeys = (ctx: boolean): readonly PanelKey[] => (ctx ? WORKSPACE_KEYS_CTX : WORKSPACE_KEYS);
// Раздел «Чаты»: список чатов плюс панели активной сессии (проекта там нет)
export const CHAT_KEYS: readonly PanelKey[] = ['chats', 'plan', 'agents', 'context', 'video'];
export const NOTES_KEYS: readonly PanelKey[] = ['notesList', 'notesGraph'];
export const KNOWLEDGE_KEYS: readonly PanelKey[] = ['knowledgeList'];
export const PERSONAS_KEYS: readonly PanelKey[] = ['personasList'];
export const PROJECTS_KEYS: readonly PanelKey[] = ['projectGroups'];

// Панели ТЕКУЩЕЙ СЕССИИ: их видимость в рельсе считается не по наличию контента,
// а по артефактам сессии (План — если был план, Агенты — если есть содержимое,
// Персона — если собеседник персона). В рельсе они отделены сепаратором от
// инструментов проекта.
export const SESSION_KEYS: readonly PanelKey[] = ['plan', 'agents', 'context'];

// Правая зона раздела «Чаты»: панели сессии плюс панели генерации личного чата
export const CHAT_RIGHT_KEYS: readonly PanelKey[] = [...SESSION_KEYS, ...genKeysFor(false)];
const CHAT_RIGHT_KEYS_CTX: readonly PanelKey[] = [...SESSION_KEYS, ...genKeysFor(true)];
export const chatRightKeys = (ctx: boolean): readonly PanelKey[] => (ctx ? CHAT_RIGHT_KEYS_CTX : CHAT_RIGHT_KEYS);

// Панели генерации: справа одна за раз, а в планшетной зоне они держат поток до
// GEN_PANEL_INLINE_MIN (genPanelPlacement). С флагом composer-context-row — одна «chatContext».
// Функция, а не константа: набор зависит от флага пользователя; источник один — lib/genPanelKeys.ts,
// вторая копия в lib/genPanelDismissed.ts отдаёт тот же список
export const genPanelKeys = (): readonly PanelKey[] => liveGenPanelKeys() as readonly PanelKey[];

// Наборы взаимоисключающих панелей: открытие одной закрывает остальные из её набора
// в ЛЮБОЙ зоне, при любой ширине окна. «Справа одна панель генерации» (ADR-021 §3):
// с прочими панелями картинки и звук соседствуют по обычной модели зоны. С флагом набор из одного
// ключа — исключать некого.
export const exclusivePanelSets = (): readonly (readonly PanelKey[])[] => [genPanelKeys()];

// Соперники панели — те, кого её открытие закрывает
export function panelRivals(k: PanelKey): PanelKey[] {
  return exclusivePanelSets().flatMap(set => (set.includes(k) ? set.filter(x => x !== k) : []));
}

// Панели ЦЕНТРАЛЬНОЙ ОБЛАСТИ: показывают не проект и не сессию, а то, что открыто
// в центре прямо сейчас. Живут ровно столько, сколько живёт их источник: закрыли
// документ — панель исчезла вместе с кнопкой (контент стал null, см. keyAvailable),
// открыли другой — вернулась на своё место в раскладке.
//
// Категория отдельная от сессионных (у тех видимость считается по артефактам хода,
// здесь — по тому, что открыто в центре), но в РЕЛЬСЕ они идут одной группой, без
// черты между собой: и те и другие отвечают на вопрос «что сейчас перед глазами».
export const CENTER_KEYS: readonly PanelKey[] = ['toc'];

// Панели, запускающие в проекте ПРОЦЕССЫ. В рельсе они идут СВОЕЙ группой:
// остальные панели показывают содержимое проекта, эти в нём что-то запускают.
//
// Раньше группа гейтилась настройкой проекта «Инструменты» (Project.ToolsEnabled)
// и по дефолту была скрыта у всех. Гейт убран: видимость кнопок — дело ящика
// рельсы («…»), а не настроек проекта, и per-project разницы у неё нет. Терминал и
// «Сервисы» лежат в ящике по умолчанию (defaultTucked у wsPanels).
//
// «Архитектура» (C4-модель, есть, когда загружен модуль architecture) — первая в группе, но,
// как терминал и «Сервисы», по умолчанию лежит в ящике «…» (defaultTucked у wsPanels):
// отдельная группа «Проектирование» ради одной кнопки дробила рельсу лишней чертой.
// С выключенным модулем кнопки просто нет, группа живёт без неё.
export const TOOLS_KEYS: readonly PanelKey[] = ['arch', 'terminal', 'preview', 'video'];

// Содержимое проекта и панели разделов: всё, что не относится ни к текущей сессии,
// ни к запуску процессов, ни к центральной области. Первая группа рельсы, дальше
// инструменты, сессионные и центральные — разделители рисует сама рельса.
export const PROJECT_KEYS: readonly PanelKey[] = PANEL_KEYS.filter(
  k => !SESSION_KEYS.includes(k) && !TOOLS_KEYS.includes(k) && !CENTER_KEYS.includes(k),
);

// Группы кнопок рельсы СВЕРХУ ВНИЗ: содержимое проекта, инструменты запуска и всё,
// что относится к текущему контексту — панели сессии и центральной области. Последние
// две категории разделены выше (у них разные источники видимости), но в рельсе идут
// ОДНОЙ группой: это соседи по смыслу — «что сейчас перед глазами», и черта между
// ними делила бы рельсу там, где деления нет.
//
// Группа — ещё и предел перестановки кнопок: порядок внутри неё пользовательский,
// а между группами — нет (разделители отделяют разные по смыслу наборы).
//
// Список лежит в РЕЕСТРЕ, а не у зоны, потому что по нему считается место панели в
// раскладке (placeByRail), и спрашивают его двое: сама зона и стор — фолбэком, когда
// зоны на экране нет (см. railSequence в panelStackState). Разойдись эти копии,
// внешний показ панели встал бы не туда, куда обещает рельса.
export const RAIL_GROUPS: readonly (readonly PanelKey[])[] = [
  PROJECT_KEYS,
  TOOLS_KEYS,
  [...SESSION_KEYS, ...CENTER_KEYS],
];

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
