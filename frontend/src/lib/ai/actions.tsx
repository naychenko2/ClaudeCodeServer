// Единый реестр AI-возможностей продукта. И палитра (pull), и проактивные
// подсказки (push) читают один и тот же каталог. Каждое действие декларативно
// описывает, КОГДА доступно (по навигационному контексту, флагам и caps) и КАК
// запускается — переиспользуя существующие api.* и обработчики компонентов через
// событие cc-ai-run, без дублирования логики.

import type { ReactNode } from 'react';
import {
  Link2, Tag, Calendar, MessageCircle, Play, Sun, Search, History, FileText, List, FilePlus2,
  Sparkles, ImagePlus, BookOpen,
  GitBranch, GitCompare, FileClock, MessageCircleQuestion, BookPlus, ListChecks, MessagesSquare,
  CalendarClock, CalendarX2, Users,
  ShieldCheck, Copy, Zap, Network, UserPlus, LayoutDashboard, RotateCcw, ListPlus, PenLine,
} from 'lucide-react';
import { ICON_SIZE } from '../../components/ui/icons';
import type { NavSnapshot } from '../nav';
import { api } from '../api';
import { showToast } from '../toast';
import { openNoteById } from '../../features/notes/saveToNote';
import { startChatWithPrompt } from './startChat';

// Событие для контекстных действий: «владелец» (открытый компонент — NoteView,
// ChatHeaderBar, TaskDetailsPane, NotesPage) слушает его и выполняет действие над
// своей текущей сущностью.
export const AI_RUN_EVENT = 'cc-ai-run';
export function dispatchAiRun(action: string) {
  window.dispatchEvent(new CustomEvent(AI_RUN_EVENT, { detail: { action } }));
}

// Открытие оверлеев верхнего уровня (слушает App)
const PRODUCT_HISTORY_EVENT = 'open-product-history'; // = HubHeader.PRODUCT_HISTORY_EVENT
export const OPEN_GLOBAL_SEARCH_EVENT = 'cc-open-global-search';

export type AiSection = 'notes' | 'tasks' | 'chat' | 'global' | 'personas' | 'knowledge' | 'project';

export interface AiActionCtx {
  nav: NavSnapshot | null;
  online: boolean;
  flag: (key: string) => boolean;
  caps: { semantic: boolean };
  // Открыт ли сейчас чат (проектный или в разделе «Чаты») и есть ли в нём переписка.
  // Активная сессия проекта не отражается в nav — ChatPanel сообщает это отдельно.
  // tail — краткий хвост переписки для локального ранжирования (опционально).
  chat: { active: boolean; hasMessages: boolean; tail?: string };
  // Является ли текущий открытый проект git-репозиторием (undefined — ещё не выяснено).
  // Гейтит git-действия, чтобы AI не предлагал их (в палитре и в LLM-рекомендациях)
  // в проекте без git.
  git?: { isRepo: boolean };
}

export interface AiAction {
  id: string;
  title: string;
  hint: string;
  section: AiSection;
  sectionLabel: string;
  icon: ReactNode;
  when: (ctx: AiActionCtx) => boolean;        // доступно ли действие сейчас
  contextual?: (ctx: AiActionCtx) => boolean; // релевантно ли (открыта нужная сущность) → наверх
  run: (ctx: AiActionCtx) => void | Promise<void>;
}

// --- Иконки (lucide-react, единый стиль раздела; имена сохранены) ---
const ico = { size: ICON_SIZE.sm, strokeWidth: 2, style: { flexShrink: 0 } as const };
const IcLink = <Link2 {...ico} />;
const IcTag = <Tag {...ico} />;
const IcCalendar = <Calendar {...ico} />;
const IcChat = <MessageCircle {...ico} />;
const IcPlay = <Play {...ico} />;
const IcSun = <Sun {...ico} />;
const IcSearch = <Search {...ico} />;
const IcHistory = <History {...ico} />;
const IcDoc = <FileText {...ico} />;
const IcList = <List {...ico} />;
const IcPlus = <FilePlus2 {...ico} />;
const IcSparkle = <Sparkles {...ico} />;
const IcAvatar = <ImagePlus {...ico} />;
const IcKnowledge = <BookOpen {...ico} />;
// --- Иконки волны 2 (git, история файла, база знаний, задачи-из-заметки, календарь, команда) ---
const IcGit = <GitBranch {...ico} />;
const IcGitLog = <GitCompare {...ico} />;
const IcFileHistory = <FileClock {...ico} />;
const IcAsk = <MessageCircleQuestion {...ico} />;
const IcBookPlus = <BookPlus {...ico} />;
const IcChecks = <ListChecks {...ico} />;
const IcComments = <MessagesSquare {...ico} />;
const IcWeek = <CalendarClock {...ico} />;
const IcOverdue = <CalendarX2 {...ico} />;
const IcTeam = <Users {...ico} />;
// --- Иконки волны 3 (ревью diff, дедуп, проактивность, привязки, исполнитель, дашборд) ---
const IcReview = <ShieldCheck {...ico} />;
const IcDedup = <Copy {...ico} />;
const IcAutomation = <Zap {...ico} />;
const IcBindings = <Network {...ico} />;
const IcAssignee = <UserPlus {...ico} />;
const IcOverview = <LayoutDashboard {...ico} />;
const IcResume = <RotateCcw {...ico} />;
const IcCapture = <ListPlus {...ico} />;
const IcRetitle = <PenLine {...ico} />;
const IcToc = <List {...ico} />;
const IcTranslate = <Sparkles {...ico} />;

// --- Предикаты контекста ---
const noteOpen = (c: AiActionCtx) => c.nav?.screen === 'notes' && !!c.nav.note;
const taskOpen = (c: AiActionCtx) => !!c.nav?.task;
const chatOpen = (c: AiActionCtx) => c.chat.active;
const personaOpen = (c: AiActionCtx) => c.nav?.screen === 'personas' && !!c.nav.persona;
const knowledgeScreen = (c: AiActionCtx) => c.nav?.screen === 'knowledge';
const knowledgeOpen = (c: AiActionCtx) => c.nav?.screen === 'knowledge' && !!c.nav.knowledge;
const fileOpen = (c: AiActionCtx) => c.nav?.screen === 'project' && !!c.nav.file;
// Документ (pdf/docx/xlsx/pptx…) — по расширению открытого файла; трансформация в MD только для них
const DOC_EXTS = new Set(['pdf', 'doc', 'docx', 'xls', 'xlsx', 'ppt', 'pptx', 'odt', 'ods', 'odp', 'epub', 'csv', 'htm', 'html']);
const fileExt = (c: AiActionCtx) => (c.nav?.file?.split('.').pop() ?? '').toLowerCase();
const docOpen = (c: AiActionCtx) => fileOpen(c) && DOC_EXTS.has(fileExt(c));
// Суть/выжимка осмысленны и для текстовых файлов (markdown, txt, код) — не только бинарных документов
const TEXT_EXTS = new Set(['md', 'markdown', 'mdx', 'txt', 'text', 'rst', 'tex', 'json', 'yaml', 'yml', 'log', 'ini', 'cfg', 'toml']);
const summarizableOpen = (c: AiActionCtx) => fileOpen(c) && (DOC_EXTS.has(fileExt(c)) || TEXT_EXTS.has(fileExt(c)));
const calendarScreen = (c: AiActionCtx) => c.nav?.screen === 'calendar';
const projectOpen = (c: AiActionCtx) => c.nav?.screen === 'project' && !!c.nav.project;
const homeScreen = (c: AiActionCtx) => c.nav?.screen === 'home';
// Git-действия осмысленны только в git-репозитории. Пока статус не выяснен (undefined) —
// действие скрыто: лучше не предложить, чем предложить разбор коммитов в проекте без git.
const gitRepoOpen = (c: AiActionCtx) => projectOpen(c) && c.git?.isRepo === true;
const fileInGitRepo = (c: AiActionCtx) => fileOpen(c) && c.git?.isRepo === true;

// Контекстный промпт действия «Показать интерактивный виджет» (chat.widget):
// в проекте — дашборд состояния, в календаре — сводка задач, в заметках — статистика
// базы, иначе — свободная тема. Сам виджет модель строит инструментом widget_show.
function widgetPrompt(c: AiActionCtx): string {
  if (c.nav?.screen === 'project' && c.nav.project)
    return 'Сделай интерактивный виджет-дашборд по состоянию проекта: git-статус и последние '
      + 'изменения (git log), структура ключевых папок. Покажи через widget_show.';
  if (c.nav?.screen === 'calendar')
    return 'Покажи виджетом наглядную сводку моих задач (tasks_list): статусы, сроки, '
      + 'приоритеты, просроченные. Покажи через widget_show.';
  if (c.nav?.screen === 'notes')
    return 'Покажи виджетом статистику моей базы заметок (notes_list, notes_graph): '
      + 'количество по источникам, теги, связность графа. Покажи через widget_show.';
  return 'Сделай наглядный интерактивный виджет (widget_show) по теме, которую я укажу '
    + 'следующим сообщением: дашборд, график, таблица или калькулятор.';
}

// --- Каталог действий (порядок задаёт ранжирование контекстной группы) ---
export const AI_ACTIONS: AiAction[] = [
  // ===== Заметки =====
  {
    id: 'note.links', title: 'Предложить связи', hint: 'найти связанные заметки',
    section: 'notes', sectionLabel: 'Заметки', icon: IcLink,
    when: c => noteOpen(c) && c.online, contextual: noteOpen,
    run: () => dispatchAiRun('note.links'),
  },
  {
    id: 'note.tags', title: 'Предложить теги', hint: 'авто-теги по смыслу',
    section: 'notes', sectionLabel: 'Заметки', icon: IcTag,
    when: c => noteOpen(c) && c.online, contextual: noteOpen,
    run: () => dispatchAiRun('note.tags'),
  },
  {
    id: 'note.title', title: 'Придумать заголовок', hint: 'заголовок по содержимому',
    section: 'notes', sectionLabel: 'Заметки', icon: IcSparkle,
    when: c => noteOpen(c), contextual: noteOpen,
    run: () => dispatchAiRun('note.title'),
  },
  {
    id: 'note.daily', title: 'Конспект дня', hint: 'итоги в дневниковую заметку',
    section: 'notes', sectionLabel: 'Заметки', icon: IcCalendar,
    when: c => noteOpen(c) && c.online, contextual: noteOpen,
    run: () => dispatchAiRun('note.daily'),
  },
  {
    id: 'note.semantic', title: 'Поиск по смыслу', hint: 'семантический поиск по заметкам',
    section: 'notes', sectionLabel: 'Заметки', icon: IcSearch,
    when: c => c.nav?.screen === 'notes' && c.caps.semantic && c.online,
    contextual: c => c.nav?.screen === 'notes',
    run: () => dispatchAiRun('note.semantic'),
  },
  {
    id: 'note.ask', title: 'Спросить Claude про заметку', hint: 'открыть чат с текстом заметки',
    section: 'notes', sectionLabel: 'Заметки', icon: IcChat,
    when: c => noteOpen(c), contextual: noteOpen,
    run: () => dispatchAiRun('note.ask'),
  },
  {
    id: 'note.promoteTasks', title: 'Превратить пункты в задачи', hint: 'чекбоксы заметки → задачи',
    section: 'notes', sectionLabel: 'Заметки', icon: IcChecks,
    when: c => noteOpen(c) && c.online, contextual: noteOpen,
    run: c => startChatWithPrompt(
      `В этой заметке (id ${c.nav?.note}) преобразуй незавершённые чекбоксы в задачи через notes_promote_task. `
      + `Сначала покажи список пунктов, промоуть после моего согласия.`, c),
  },
  {
    id: 'note.annotations', title: 'Разобрать комментарии документа', hint: 'обработать open-комментарии',
    section: 'notes', sectionLabel: 'Заметки', icon: IcComments,
    when: c => noteOpen(c) && c.online && c.flag('doc-annotations'), contextual: noteOpen,
    run: c => startChatWithPrompt(
      `Разбери необработанные (open) комментарии документа (заметка id ${c.nav?.note}): прочитай их (notes_annotations), `
      + `по каждому внеси правку или ответь (notes_reply), затем закрой (notes_set_status). Сначала покажи план.`, c),
  },
  {
    id: 'note.toc', title: 'Собрать оглавление', hint: 'вставить оглавление по заголовкам',
    section: 'notes', sectionLabel: 'Заметки', icon: IcToc,
    when: c => noteOpen(c), contextual: noteOpen,
    run: () => dispatchAiRun('note.toc'),
  },
  {
    id: 'note.translate', title: 'Перевести заметку', hint: 'перевод на другой язык в конце заметки',
    section: 'notes', sectionLabel: 'Заметки', icon: IcTranslate,
    when: c => noteOpen(c) && c.online, contextual: noteOpen,
    run: () => dispatchAiRun('note.translate'),
  },

  // ===== Задачи =====
  {
    id: 'task.subtasks', title: 'Предложить подзадачи', hint: 'разбить задачу на шаги',
    section: 'tasks', sectionLabel: 'Задачи', icon: IcList,
    when: c => taskOpen(c) && c.online, contextual: taskOpen,
    run: () => dispatchAiRun('task.subtasks'),
  },
  {
    id: 'task.description', title: 'Сгенерировать описание', hint: 'описание задачи по названию',
    section: 'tasks', sectionLabel: 'Задачи', icon: IcDoc,
    when: c => taskOpen(c) && c.online, contextual: taskOpen,
    run: () => dispatchAiRun('task.description'),
  },
  {
    id: 'task.execute', title: 'Выполнить задачу с Claude', hint: 'запустить Claude-исполнителя',
    section: 'tasks', sectionLabel: 'Задачи', icon: IcPlay,
    when: c => taskOpen(c) && c.online, contextual: taskOpen,
    run: () => dispatchAiRun('task.execute'),
  },
  {
    id: 'task.classify', title: 'Оценить приоритет и метки', hint: 'предложить приоритет и метки задаче',
    section: 'tasks', sectionLabel: 'Задачи', icon: IcTag,
    when: c => taskOpen(c), contextual: taskOpen,
    run: () => dispatchAiRun('task.classify'),
  },
  {
    id: 'task.dedup', title: 'Проверить на дубли', hint: 'нет ли похожей существующей задачи',
    section: 'tasks', sectionLabel: 'Задачи', icon: IcSearch,
    when: c => taskOpen(c), contextual: taskOpen,
    run: () => dispatchAiRun('task.dedup'),
  },
  {
    id: 'task.suggestPersona', title: 'Подобрать исполнителя', hint: 'какая персона лучше выполнит',
    section: 'tasks', sectionLabel: 'Задачи', icon: IcAssignee,
    when: c => taskOpen(c) && c.online, contextual: taskOpen,
    run: c => startChatWithPrompt(
      `Подбери исполнителя для задачи (id ${c.nav?.task}): посмотри доступные персоны (personas_list), выбери `
      + `наиболее подходящую по роли и специализации, обоснуй. Назначь её исполнителем (tasks_update personaId) `
      + `после моего подтверждения.`, c),
  },
  {
    id: 'tasks.weekPlan', title: 'План недели', hint: 'задачи недели + приоритеты',
    section: 'tasks', sectionLabel: 'Задачи', icon: IcWeek,
    when: c => calendarScreen(c) && c.online, contextual: calendarScreen,
    run: c => startChatWithPrompt(
      `Покажи мои задачи на этой неделе (tasks_list с диапазоном дат from/to) и предложи план: приоритеты, группировка.`, c),
  },
  {
    id: 'tasks.overdue', title: 'Разобрать просроченные', hint: 'перенести / разбить / закрыть',
    section: 'tasks', sectionLabel: 'Задачи', icon: IcOverdue,
    when: c => calendarScreen(c) && c.online, contextual: calendarScreen,
    run: c => startChatWithPrompt(
      `Покажи мои просроченные задачи (tasks_list с to=сегодня) и предложи: перенести срок / разбить / закрыть.`, c),
  },

  // ===== Чат =====
  {
    id: 'chat.extract', title: 'Извлечь задачи из чата', hint: 'action items из диалога',
    section: 'chat', sectionLabel: 'Чат', icon: IcPlus,
    when: c => chatOpen(c) && c.chat.hasMessages && c.online, contextual: chatOpen,
    run: () => dispatchAiRun('chat.extract'),
  },
  {
    id: 'chat.summary', title: 'Итог сессии в заметку', hint: 'конспект чата заметкой',
    section: 'chat', sectionLabel: 'Чат', icon: IcDoc,
    when: c => chatOpen(c) && c.chat.hasMessages && c.online, contextual: chatOpen,
    run: () => dispatchAiRun('chat.summary'),
  },
  {
    id: 'chat.retitle', title: 'Обновить название чата', hint: 'переименовать по смыслу переписки',
    section: 'chat', sectionLabel: 'Чат', icon: IcRetitle,
    when: c => chatOpen(c) && c.chat.hasMessages && c.online, contextual: chatOpen,
    run: () => dispatchAiRun('chat.retitle'),
  },
  {
    // Интерактивный HTML-виджет в ленте чата (флаг chat-widgets): промпт подстраивается
    // под открытый раздел — дашборд проекта / сводка задач / статистика заметок / свободная тема
    id: 'chat.widget', title: 'Показать интерактивный виджет', hint: 'дашборд, график или сводка прямо в чате',
    section: 'chat', sectionLabel: 'Чат', icon: IcOverview,
    when: c => c.online && c.flag('chat-widgets'),
    contextual: c => projectOpen(c) || calendarScreen(c) || noteOpen(c),
    run: c => startChatWithPrompt(widgetPrompt(c), c),
  },

  // ===== Персоны =====
  {
    id: 'persona.character', title: 'Улучшить характер персоны', hint: 'сгенерировать/доработать характер',
    section: 'personas', sectionLabel: 'Персоны', icon: IcSparkle,
    when: c => personaOpen(c) && c.online, contextual: personaOpen,
    run: () => dispatchAiRun('persona.character'),
  },
  {
    id: 'persona.avatar', title: 'Сгенерировать аватар', hint: 'фото-аватар персоны через ИИ',
    section: 'personas', sectionLabel: 'Персоны', icon: IcAvatar,
    when: c => personaOpen(c) && c.online, contextual: personaOpen,
    run: () => dispatchAiRun('persona.avatar'),
  },
  {
    id: 'persona.team', title: 'Предложить команду под проект', hint: 'роли и характеры персон',
    section: 'personas', sectionLabel: 'Персоны', icon: IcTeam,
    when: c => c.nav?.screen === 'personas' && c.online,
    contextual: c => c.nav?.screen === 'personas',
    run: c => startChatWithPrompt(
      `Предложи команду персон (personas_ai_team): роли и характеры под задачу/проект. `
      + `Покажи черновики — я решу, кого создать.`, c),
  },
  {
    id: 'persona.automation', title: 'Настроить проактивность', hint: 'правила реакций персоны на события',
    section: 'personas', sectionLabel: 'Персоны', icon: IcAutomation,
    when: c => personaOpen(c) && c.online, contextual: personaOpen,
    run: c => startChatWithPrompt(
      `Предложи для персоны (id ${c.nav?.persona}) правила проактивности под её роль: на какие события реагировать `
      + `и как. Покажи черновики, создавай через personas_automation_create только с моего согласия.`, c),
  },
  {
    id: 'persona.bindings', title: 'Предложить привязки', hint: 'к каким проектам и базам подключить',
    section: 'personas', sectionLabel: 'Персоны', icon: IcBindings,
    when: c => personaOpen(c) && c.online, contextual: personaOpen,
    run: c => startChatWithPrompt(
      `Предложи для персоны (id ${c.nav?.persona}) уместные привязки знаний и правил `
      + `(personas_suggest_bindings), объясни каждую. Применяй (personas_bindings_set) после подтверждения.`, c),
  },

  // ===== Знания =====
  {
    id: 'knowledge.search', title: 'Поиск по смыслу в базе', hint: 'семантический поиск по базе знаний',
    section: 'knowledge', sectionLabel: 'Знания', icon: IcKnowledge,
    when: c => knowledgeScreen(c) && c.online, contextual: knowledgeScreen,
    run: () => dispatchAiRun('knowledge.search'),
  },
  {
    id: 'knowledge.describe', title: 'Сгенерировать описание базы', hint: 'описание по составу документов',
    section: 'knowledge', sectionLabel: 'Знания', icon: IcDoc,
    when: c => knowledgeOpen(c), contextual: knowledgeOpen,
    run: () => dispatchAiRun('knowledge.describe'),
  },
  {
    id: 'knowledge.ask', title: 'Спросить Claude по базе', hint: 'ответы с опорой на базу знаний',
    section: 'knowledge', sectionLabel: 'Знания', icon: IcAsk,
    when: c => knowledgeOpen(c) && c.online, contextual: knowledgeOpen,
    run: c => startChatWithPrompt(
      `Отвечай на мои вопросы, опираясь на базу знаний (id ${c.nav?.knowledge}) — используй kb_search. Мой вопрос: `, c),
  },
  {
    id: 'knowledge.fill', title: 'Наполнить базу', hint: 'предложить и добавить материалы',
    section: 'knowledge', sectionLabel: 'Знания', icon: IcBookPlus,
    when: c => knowledgeOpen(c) && c.online, contextual: knowledgeOpen,
    run: c => startChatWithPrompt(
      `Помоги наполнить базу знаний (id ${c.nav?.knowledge}): предложи материалы и по моему согласию `
      + `добавляй через kb_add_document.`, c),
  },
  {
    id: 'knowledge.dedup', title: 'Найти дубли документов', hint: 'похожие и устаревшие материалы',
    section: 'knowledge', sectionLabel: 'Знания', icon: IcDedup,
    when: c => knowledgeOpen(c) && c.online, contextual: knowledgeOpen,
    run: c => startChatWithPrompt(
      `Просмотри документы базы знаний (id ${c.nav?.knowledge}): найди дубли и устаревшие материалы, `
      + `сравнивая по смыслу, и покажи группы похожих. Удаляй только с моего подтверждения.`, c),
  },

  // ===== Проект / файлы =====
  {
    id: 'file.ask', title: 'Спросить Claude про файл', hint: 'открыть чат с содержимым файла',
    section: 'project', sectionLabel: 'Проект', icon: IcChat,
    when: c => fileOpen(c) && c.online, contextual: fileOpen,
    run: () => dispatchAiRun('file.ask'),
  },
  {
    id: 'file.summary', title: 'Краткое содержание', hint: 'суть файла за 5-8 пунктов',
    section: 'project', sectionLabel: 'Проект', icon: IcDoc,
    when: summarizableOpen, contextual: summarizableOpen,
    run: () => dispatchAiRun('file.summary'),
  },
  {
    id: 'file.extract', title: 'Выжимка', hint: 'решения, даты, участники, действия',
    section: 'project', sectionLabel: 'Проект', icon: IcChecks,
    when: summarizableOpen, contextual: summarizableOpen,
    run: () => dispatchAiRun('file.extract'),
  },
  {
    id: 'file.toMarkdown', title: 'Сохранить как Markdown', hint: 'трансформировать документ в .md рядом',
    section: 'project', sectionLabel: 'Проект', icon: IcBookPlus,
    when: docOpen, contextual: docOpen,
    run: () => dispatchAiRun('file.toMarkdown'),
  },
  {
    id: 'file.history', title: 'История и авторы файла', hint: 'git log + blame по файлу',
    section: 'project', sectionLabel: 'Проект', icon: IcFileHistory,
    when: c => fileInGitRepo(c) && c.online, contextual: fileOpen,
    run: c => startChatWithPrompt(
      `Покажи историю изменений файла «${c.nav?.file}»: кто и что менял (git log + blame), кратко суммируй эволюцию.`, c),
  },
  {
    id: 'project.gitReview', title: 'Разобрать незакоммиченные изменения', hint: 'сгруппировать и предложить коммиты',
    section: 'project', sectionLabel: 'Проект', icon: IcGit,
    when: c => gitRepoOpen(c) && c.online, contextual: projectOpen,
    run: c => startChatWithPrompt(
      `Посмотри git-статус и незакоммиченные изменения проекта, сгруппируй по смыслу и предложи атомарные коммиты `
      + `с сообщениями (Conventional Commits на русском). Ничего не коммить без моего подтверждения.`, c),
  },
  {
    id: 'project.gitOverview', title: 'Что менялось за неделю', hint: 'сводка git-лога за 7 дней',
    section: 'project', sectionLabel: 'Проект', icon: IcGitLog,
    when: c => gitRepoOpen(c) && c.online, contextual: projectOpen,
    run: c => startChatWithPrompt(
      `Пройди по git-логу за последнюю неделю и кратко суммируй ключевые изменения проекта и их смысл.`, c),
  },
  {
    id: 'project.reviewDiff', title: 'Ревью изменений перед коммитом', hint: 'найти баги и риски в git diff',
    section: 'project', sectionLabel: 'Проект', icon: IcReview,
    when: c => gitRepoOpen(c) && c.online, contextual: projectOpen,
    run: c => startChatWithPrompt(
      `Сделай ревью незакоммиченных изменений проекта перед коммитом: пройди по git diff, найди возможные баги, `
      + `риски и небрежности, сгруппируй находки по важности (критично / стоит поправить / мелочи). `
      + `Ничего не меняй без моего согласия.`, c),
  },
  {
    id: 'file.indexKnowledge', title: 'Добавить файл в знания', hint: 'проиндексировать в базу знаний',
    section: 'project', sectionLabel: 'Проект', icon: IcBookPlus,
    when: c => fileOpen(c) && c.online, contextual: fileOpen,
    run: c => startChatWithPrompt(
      `Добавь файл «${c.nav?.file}» в подходящую базу знаний: предложи, в какую именно (или новую), `
      + `и по моему согласию проиндексируй через kb_add_document.`, c),
  },

  // ===== Глобальные =====
  {
    id: 'calendar.plan', title: 'Спланировать день', hint: 'собрать план дня в дневник',
    section: 'global', sectionLabel: 'Глобально', icon: IcSun,
    when: c => calendarScreen(c) && c.online, contextual: calendarScreen,
    run: () => runBriefing(),
  },
  {
    id: 'global.briefing', title: 'Утренний бриф', hint: 'собрать план дня в дневник',
    section: 'global', sectionLabel: 'Глобально', icon: IcSun,
    when: c => c.online,
    run: () => runBriefing(),
  },
  {
    id: 'global.search', title: 'Единый поиск', hint: 'по заметкам и задачам сразу',
    section: 'global', sectionLabel: 'Глобально', icon: IcSearch,
    when: () => true,
    run: () => window.dispatchEvent(new Event(OPEN_GLOBAL_SEARCH_EVENT)),
  },
  {
    id: 'global.whatsnew', title: 'Что нового', hint: 'AI-сводка изменений продукта',
    section: 'global', sectionLabel: 'Глобально', icon: IcHistory,
    when: () => true,
    run: () => window.dispatchEvent(new Event(PRODUCT_HISTORY_EVENT)),
  },
  {
    id: 'home.overview', title: 'Обзор за меня', hint: 'приоритеты на сегодня по задачам и заметкам',
    section: 'global', sectionLabel: 'Глобально', icon: IcOverview,
    when: c => c.online, contextual: homeScreen,
    run: c => startChatWithPrompt(
      `Собери короткий обзор на сегодня: активные и просроченные задачи (tasks_list), свежие заметки `
      + `(notes_list/notes_search) — и что из этого важнее всего. Дай 3 приоритета на день.`, c),
  },
  {
    id: 'home.resume', title: 'На чём остановился', hint: 'вспомнить контекст последней работы',
    section: 'global', sectionLabel: 'Глобально', icon: IcResume,
    when: c => c.online, contextual: homeScreen,
    run: c => startChatWithPrompt(
      `Помоги вспомнить, на чём я остановился: покажи мои недавно изменённые заметки и задачи в работе, `
      + `кратко суммируй по каждому направлению и предложи следующий шаг.`, c),
  },
  {
    id: 'global.capture', title: 'Быстро в задачу', hint: 'создать задачу из мысли',
    section: 'global', sectionLabel: 'Глобально', icon: IcCapture,
    when: c => c.online,
    run: c => startChatWithPrompt(
      `Создай задачу через tasks_create по моему описанию, уточнив срок и приоритет при необходимости. Вот что нужно записать: `, c),
  },
];

// Сбор дневного брифа (утренний бриф / план дня в календаре) — общая логика
function runBriefing(): void {
  showToast('Собираю бриф', 'Claude готовит план дня…', 'claude');
  api.briefing.today(localDate())
    .then(n => openNoteById(n.id))
    .catch(() => showToast('Не удалось собрать бриф', 'ИИ недоступен (claude не залогинен на сервере)', 'info'));
}

// Локальная дата YYYY-MM-DD (для брифа)
function localDate(): string {
  const d = new Date();
  const p = (n: number) => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())}`;
}

// Запустить действие реестра по id (для проактивных подсказок push-слоя)
export function runActionById(id: string, ctx: AiActionCtx): void {
  AI_ACTIONS.find(a => a.id === id)?.run(ctx);
}

// Доступные сейчас действия, отранжированные: контекстные (релевантные открытой
// сущности) — первыми, затем остальные. Опциональный текстовый фильтр.
export function rankedActions(ctx: AiActionCtx, query = ''): { action: AiAction; contextual: boolean }[] {
  const q = query.trim().toLowerCase();
  const match = (a: AiAction) => !q || a.title.toLowerCase().includes(q) || a.hint.toLowerCase().includes(q);
  const avail = AI_ACTIONS.filter(a => a.when(ctx) && match(a));
  const ctxList = avail.filter(a => a.contextual?.(ctx));
  const rest = avail.filter(a => !a.contextual?.(ctx));
  return [
    ...ctxList.map(action => ({ action, contextual: true })),
    ...rest.map(action => ({ action, contextual: false })),
  ];
}
