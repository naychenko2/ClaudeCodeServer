// Единый реестр AI-возможностей продукта. И палитра (pull), и проактивные
// подсказки (push) читают один и тот же каталог. Каждое действие декларативно
// описывает, КОГДА доступно (по навигационному контексту, флагам и caps) и КАК
// запускается — переиспользуя существующие api.* и обработчики компонентов через
// событие cc-ai-run, без дублирования логики.

import type { ReactNode } from 'react';
import {
  Link2, Tag, Calendar, MessageCircle, Play, Sun, Search, History, FileText, List, FilePlus2,
} from 'lucide-react';
import { ICON_SIZE } from '../../components/ui/icons';
import type { NavSnapshot } from '../nav';
import { api } from '../api';
import { showToast } from '../toast';
import { openNoteById } from '../../features/notes/saveToNote';

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

export type AiSection = 'notes' | 'tasks' | 'chat' | 'global';

export interface AiActionCtx {
  nav: NavSnapshot | null;
  online: boolean;
  flag: (key: string) => boolean;
  caps: { semantic: boolean };
  // Открыт ли сейчас чат (проектный или в разделе «Чаты») и есть ли в нём переписка.
  // Активная сессия проекта не отражается в nav — ChatPanel сообщает это отдельно.
  chat: { active: boolean; hasMessages: boolean };
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

// --- Предикаты контекста ---
const noteOpen = (c: AiActionCtx) => c.nav?.screen === 'notes' && !!c.nav.note;
const taskOpen = (c: AiActionCtx) => !!c.nav?.task;
const chatOpen = (c: AiActionCtx) => c.chat.active;

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

  // ===== Глобальные =====
  {
    id: 'global.briefing', title: 'Утренний бриф', hint: 'собрать план дня в дневник',
    section: 'global', sectionLabel: 'Глобально', icon: IcSun,
    when: c => c.online,
    run: () => {
      showToast('Собираю бриф', 'Claude готовит план дня…', 'claude');
      api.briefing.today(localDate())
        .then(n => openNoteById(n.id))
        .catch(() => showToast('Не удалось собрать бриф', 'ИИ недоступен (claude не залогинен на сервере)', 'info'));
    },
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
