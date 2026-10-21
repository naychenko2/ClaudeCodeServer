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
  TableOfContents, Lightbulb, DraftingCompass, SlidersHorizontal, Mic, Cpu, Plug,
  type LucideIcon,
} from 'lucide-react';
import type { BadgeTone } from '../../components/ui/CountBadge';
import { GEN_PANEL_KEYS } from '../../lib/genPanelKeys';

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
  // «Локальные модели» (фича local-model-stats): только админ, значок — при
  // настроенном локальном провайдере (условие — у фичи, см. manifest)
  'localModel',
  // «Голоса» редактора звука: библиотека voices/ проекта отдельной панелью
  'voices',
  // Единая панель «Контекст» чата (ADR-023 §Д1): вместо прежних панелей генерации «Картинки», «Звук» и
  // «Видео». Рисует её оболочка (ContextPanel), а не вертикаль. Ключ `context` занят панелью «Персона» и
  // сохранён в раскладках — отсюда отдельный ключ
  'chatContext',
  // Фоновый эфир рядом с работой: живёт и в проекте, и в разделе «Чаты».
  // Каталог каналов панелью НЕ является: он открывается в центральном острове
  // (кнопка в шапке этой панели), потому что каналы выбирают по обложкам,
  // а в рельсе их не разглядеть.
  'video',
  // Маркер семейства плагинов (ADR-031, S5): сам по себе панелью больше не
  // является (упразднён, см. LEGACY_KEY_ALIASES) — у каждой панели плагина своя
  // динамическая карточка plugin:*. Живёт в реестре, чтобы WORKSPACE_KEYS мог
  // разрешить всё семейство plugin:* одним маркером (keyAllowed), а не перечислять
  // каждый ключ.
  'pluginPanels',
  // Панели разделов хаба
  'notesList', 'notesGraph', 'knowledgeList', 'personasList', 'projectGroups',
] as const;
// Статические ключи реестра: все панели продукта, известные на момент компиляции
export type StaticPanelKey = typeof PANEL_KEYS[number];
// Ключ панели плагина (ADR-031, S3): `plugin:{publisher}/{slug}:{panelId}` —
// ключ на ПАНЕЛЬ, а не на плагин: у каждой панели плагина своя карточка в
// боковой колонке, и их можно раскладывать по зонам одновременно.
// Версии в ключе НЕТ: при обновлении плагина ключ стабилен, карточка остаётся
// на своём месте в раскладке.
export type PluginPanelKey = `plugin:${string}`;
// Все панели продукта: статические ключи + семейство плагинов (динамическое, S3)
export type PanelKey = StaticPanelKey | PluginPanelKey;

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

// Иконка и заголовок панели — общие для обеих зон. pluginPanels исключён (R-Gleb,
// minor 7): слот упразднён (S5), это маркер семейства, а не реальная панель — меты
// у него нет (реальные карточки рисуются по динамическим ключам plugin:*).
export const PANEL_META: Record<Exclude<PanelKey, 'pluginPanels'>, { title: string; Icon: LucideIcon }> = {
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
  // pluginPanels (S5) исключён из меты: маркер семейства, не реальная панель
  // (R-Gleb, minor 7) — реальные карточки рисуются по динамическим ключам plugin:*.
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
  // Локальные модели (фича local-model-stats): статистика локального движка, только админ
  localModel: { title: 'Локальные модели', Icon: Cpu },
  // Голоса редактора звука (модуль audio-editor): библиотека voices/ проекта
  voices: { title: 'Голоса', Icon: Mic },
  // Панели генерации: заголовок и иконку в рельсе отдаёт вклад, здесь — запасные
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
  // Маркер семейства (S5): не панель, 'right' — домашняя зона реальных plugin:*
  pluginPanels: 'right',
  preview: 'right',
  plan: 'right',
  agents: 'right',
  context: 'right',
  toc: 'right',
  characters: 'right',
  localModel: 'right',
  voices: 'right',
  chatContext: 'right',
  // Разделы хаба выросли из левого сайдбара — там их дом
  notesList: 'left',
  notesGraph: 'left',
  knowledgeList: 'left',
  personasList: 'left',
  projectGroups: 'left',
};

// Наборы ключей по экранам — что вообще доступно в этой рельсе (проп allowedKeys)
export const WORKSPACE_KEYS: readonly PanelKey[] = [
  'chats', 'files', 'changes', 'tasks', 'docs', 'dossiers', 'knowledge', 'notes', 'graph', 'arch', 'team', 'skills', 'terminal', 'preview',
  'plan', 'agents', 'context', 'toc', 'video', 'characters', 'localModel', 'voices', 'chatContext', 'pluginPanels',
];
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

// Правая зона раздела «Чаты»: панели сессии плюс «Контекст» личного чата
export const CHAT_RIGHT_KEYS: readonly PanelKey[] = [...SESSION_KEYS, 'chatContext'];

// Панели генерации: в планшетной зоне они держат поток до GEN_PANEL_INLINE_MIN (genPanelPlacement).
// Источник один — lib/genPanelKeys.ts, вторая копия в lib/genPanelDismissed.ts отдаёт тот же список
export const genPanelKeys = (): readonly PanelKey[] => GEN_PANEL_KEYS as readonly PanelKey[];

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

// Синтаксис ключа плагина: `plugin:{publisher}/{slug}:{panelId}`. Алфавит
// исключает ':' и '/' (разделители) и пробелы; ограничение длины защищает
// формат хранилища от мусора.
const PLUGIN_KEY_RE = /^plugin:[A-Za-z0-9@._-]+\/[A-Za-z0-9._-]+:[A-Za-z0-9._-]+$/;
// Ключ — идентификаторы (npm-имя пакета {publisher}/{slug} + id панели). Потолок
// 200, а не 64 (R-Gleb, minor 3): у длинного id (scoped-пакет + длинный slug) ключ
// превышал 64 и карточка МОЛЧА не переживала перезапуск (isPluginPanelKey → false →
// sanitizeLayout отбрасывал). 200 — запас на реальные длинные id; длиннее — мусор.
const PLUGIN_KEY_MAX_LEN = 200;

// Предикат ключа плагина: принимается по СИНТАКСИСУ, а не по списку установленных
// плагинов — стор читает localStorage при импорте модуля, а список (асинхронный
// GET) в этот момент ещё не известен. Иначе карточки молча выселялись бы из
// раскладки: человек терял бы их место без единого сообщения.
export function isPluginPanelKey(v: unknown): v is PluginPanelKey {
  return typeof v === 'string' && v.length <= PLUGIN_KEY_MAX_LEN && PLUGIN_KEY_RE.test(v);
}

// isPanelKey принимает статические ключи плюс синтаксически валидный ключ
// плагина: формат хранилища не меняется, старые раскладки читаются как есть
export function isPanelKey(v: unknown): v is PanelKey {
  return typeof v === 'string' && ((PANEL_KEYS as readonly string[]).includes(v) || isPluginPanelKey(v));
}

// Конструктор ключа плагина: pluginId — id пакета `{publisher}/{slug}`
// (PluginInfo.id), panelId — id панели из манифеста (PluginPanelView.id)
export function pluginPanelKey(pluginId: string, panelId: string): PluginPanelKey {
  return `plugin:${pluginId}:${panelId}` as PluginPanelKey;
}

// Реестр меты плагинов: заголовок и иконка каждой панели плагина. Наполняет
// WorkspacePage (S5) по списку установленных плагинов; сам стор о плагинах
// не знает — принимает только ключи по синтаксису.
const pluginPanelMeta = new Map<PluginPanelKey, { title: string; Icon: LucideIcon }>();

// Заменить реестр целиком: текущий набор панелей плагинов (пустой — очистить).
// Следующие вызовы panelMeta читают уже новый набор.
export function setPluginPanelMeta(meta: Record<PluginPanelKey, { title: string; Icon: LucideIcon }>): void {
  pluginPanelMeta.clear();
  for (const [k, v] of Object.entries(meta)) pluginPanelMeta.set(k as PluginPanelKey, v);
}

// Мета панели по ключу: статические — из реестра, плагины — из реестра меты
// плагинов. Для ключа плагина без меты — undefined: ключ принят по синтаксису,
// а мета известна только после GET списка плагинов.
export function panelMeta(k: PanelKey): { title: string; Icon: LucideIcon } | undefined {
  // pluginPanels — маркер семейства (S5), не реальная панель: меты у него нет
  // (R-Gleb, minor 7) — реальные карточки читают meta по своим ключам plugin:*.
  if (k === 'pluginPanels') return undefined;
  return isPluginPanelKey(k) ? pluginPanelMeta.get(k) : PANEL_META[k];
}

// Домашняя зона панели: статические — из реестра, ключи `plugin:` — 'right'
// (как у прежней панели pluginPanels, S3)
export function panelHome(k: PanelKey): Zone {
  return isPluginPanelKey(k) ? 'right' : PANEL_HOME[k];
}

// Мета панели с запасным вариантом (S4): ключ плагина без записи в реестре (пока
// S5 его не наполнил) даёт заголовок из имени панели и иконку по умолчанию — панель
// не падает. Статические ключи мету всегда имеют, так что fallback нужен только
// для plugin:*. Возвращает объект, а не undefined: потребитель не обязан проверять.
export function panelMetaResolved(k: PanelKey): { title: string; Icon: LucideIcon } {
  const m = panelMeta(k);
  if (m) return m;
  if (isPluginPanelKey(k)) {
    // plugin:{publisher}/{slug}:{panelId} — имя панели (panelId) как запасной заголовок
    const panelId = k.slice(k.lastIndexOf(':') + 1);
    return { title: panelId, Icon: Plug };
  }
  // Статический ключ без меты — не должен встречаться, но не падаем
  return { title: k, Icon: AppWindow };
}

// Разрешает ли экран панель: статические ключи — по перечню, ключи плагинов —
// СЕМЕЙСТВОМ: экран разрешает всё семейство plugin:* (маркер — слот pluginPanels
// в перечне, либо сам ключ, если экран уже знает установленные плагины), а не
// перечень отдельных ключей. allowedKeys при этом остаётся статичным: экран
// объявляет семейство, а стор раскрывает его для каждого ключа.
export function keyAllowed(k: PanelKey, allowed: readonly PanelKey[]): boolean {
  if (isPluginPanelKey(k)) {
    const list = allowed as readonly string[];
    return list.includes(k) || list.includes('pluginPanels');
  }
  return (allowed as readonly string[]).includes(k);
}

// Переименования ключей при чтении старых раскладок из localStorage. Синонимы
// схлопнуты в одну панель, поэтому сохранённые у пользователей ключи левой рельсы
// надо перевести на общие; `tools` пары-наследника не имеет и отбрасывается —
// его роль закрывают `terminal` и `preview`, которые пользователь откроет сам.
// Так же отбрасывается `projects`: панель-переключатель проектов упразднена, её
// заменил док проектов второй левой рельсой (features/projects/ProjectRail).
// Панели генерации «Картинки», «Звук» и «Видео» упразднены (ADR-023): в сохранённых раскладках их место
// занимает «Контекст»; повтор в одной раскладке снимает санитайз (дубли), поэтому три ключа дают одну панель.
const LEGACY_KEY_ALIASES: Record<string, PanelKey | null> = {
  personas: 'team',
  images: 'chatContext',
  sound: 'chatContext',
  videoEditor: 'chatContext',
  // S5: единый слот pluginPanels упразднён — у каждой панели плагина теперь своя
  // карточка (ключ plugin:*). Старая раскладка не переносится (null): карточки
  // встают на своё место по правилам рельса, историческое место не восстанавливаем.
  pluginPanels: null,
};

// Ключ старой раскладки → ключ реестра (null — панель упразднена).
// Явный `null` в таблице (не «отсутствует») — упразднение: старый слот не
// переносится, а его панели встают на своё место по правилам рельса.
export function migrateLegacyKey(v: unknown): PanelKey | null {
  if (typeof v !== 'string') return null;
  if (v in LEGACY_KEY_ALIASES) return LEGACY_KEY_ALIASES[v];
  return isPanelKey(v) ? v : null;
}
