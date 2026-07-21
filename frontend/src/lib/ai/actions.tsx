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

// --- Предикаты контекста ---
const noteOpen = (c: AiActionCtx) => c.nav?.screen === 'notes' && !!c.nav.note;
const taskOpen = (c: AiActionCtx) => !!c.nav?.task;
const chatOpen = (c: AiActionCtx) => c.chat.active;
const personaOpen = (c: AiActionCtx) => c.nav?.screen === 'personas' && !!c.nav.persona;
const knowledgeScreen = (c: AiActionCtx) => c.nav?.screen === 'knowledge';
const knowledgeOpen = (c: AiActionCtx) => c.nav?.screen === 'knowledge' && !!c.nav.knowledge;
const fileOpen = (c: AiActionCtx) => c.nav?.screen === 'project' && !!c.nav.file;
// Документ (pdf/docx/xlsx/pptx…) — по расширению открытого файла; ИИ-действия документа только для них
const DOC_EXTS = new Set(['pdf', 'doc', 'docx', 'xls', 'xlsx', 'ppt', 'pptx', 'odt', 'ods', 'odp', 'epub', 'csv', 'htm', 'html']);
const docOpen = (c: AiActionCtx) => fileOpen(c) && DOC_EXTS.has((c.nav?.file?.split('.').pop() ?? '').toLowerCase());
const calendarScreen = (c: AiActionCtx) => c.nav?.screen === 'calendar';
const projectOpen = (c: AiActionCtx) => c.nav?.screen === 'project' && !!c.nav.project;

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

  // ===== Знания =====
  {
    id: 'knowledge.search', title: 'Поиск по смыслу в базе', hint: 'семантический поиск по базе знаний',
    section: 'knowledge', sectionLabel: 'Знания', icon: IcKnowledge,
    when: c => knowledgeScreen(c) && c.online, contextual: knowledgeScreen,
    run: () => dispatchAiRun('knowledge.search'),
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

  // ===== Проект / файлы =====
  {
    id: 'file.ask', title: 'Спросить Claude про файл', hint: 'открыть чат с содержимым файла',
    section: 'project', sectionLabel: 'Проект', icon: IcChat,
    when: c => fileOpen(c) && c.online, contextual: fileOpen,
    run: () => dispatchAiRun('file.ask'),
  },
  {
    id: 'file.summary', title: 'Краткое содержание документа', hint: 'суть pdf/docx/xlsx за 5-8 пунктов',
    section: 'project', sectionLabel: 'Проект', icon: IcDoc,
    when: docOpen, contextual: docOpen,
    run: () => dispatchAiRun('file.summary'),
  },
  {
    id: 'file.extract', title: 'Выжимка из документа', hint: 'решения, даты, участники, действия',
    section: 'project', sectionLabel: 'Проект', icon: IcChecks,
    when: docOpen, contextual: docOpen,
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
    when: c => fileOpen(c) && c.online, contextual: fileOpen,
    run: c => startChatWithPrompt(
      `Покажи историю изменений файла «${c.nav?.file}»: кто и что менял (git log + blame), кратко суммируй эволюцию.`, c),
  },
  {
    id: 'project.gitReview', title: 'Разобрать незакоммиченные изменения', hint: 'сгруппировать и предложить коммиты',
    section: 'project', sectionLabel: 'Проект', icon: IcGit,
    when: c => projectOpen(c) && c.online, contextual: projectOpen,
    run: c => startChatWithPrompt(
      `Посмотри git-статус и незакоммиченные изменения проекта, сгруппируй по смыслу и предложи атомарные коммиты `
      + `с сообщениями (Conventional Commits на русском). Ничего не коммить без моего подтверждения.`, c),
  },
  {
    id: 'project.gitOverview', title: 'Что менялось за неделю', hint: 'сводка git-лога за 7 дней',
    section: 'project', sectionLabel: 'Проект', icon: IcGitLog,
    when: c => projectOpen(c) && c.online, contextual: projectOpen,
    run: c => startChatWithPrompt(
      `Пройди по git-логу за последнюю неделю и кратко суммируй ключевые изменения проекта и их смысл.`, c),
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
