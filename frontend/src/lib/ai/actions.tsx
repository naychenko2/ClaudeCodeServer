// Единый реестр AI-возможностей продукта. И палитра (pull), и проактивные
// подсказки (push) читают один и тот же каталог. Каждое действие декларативно
// описывает, КОГДА доступно (по навигационному контексту, флагам и caps) и КАК
// запускается — переиспользуя существующие api.* и обработчики компонентов через
// событие cc-ai-run, без дублирования логики.

import type { ReactNode } from 'react';
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

// --- Иконки (локальные, чтобы реестр не зависел от иконок разделов) ---
const sIco = (d: ReactNode) => (
  <svg viewBox="0 0 24 24" width="16" height="16" fill="none" stroke="currentColor" strokeWidth={2} strokeLinecap="round" strokeLinejoin="round">{d}</svg>
);
const IcLink = sIco(<><path d="M9 15l6-6" /><path d="M10.5 6.5l1-1a4 4 0 015.6 5.6l-1 1" /><path d="M13.5 17.5l-1 1a4 4 0 01-5.6-5.6l1-1" /></>);
const IcTag = sIco(<><path d="M3 12l8-8 9 9-8 8z" /><circle cx="8" cy="8" r="1.3" fill="currentColor" stroke="none" /></>);
const IcCalendar = sIco(<><rect x="3" y="4" width="18" height="17" rx="2" /><path d="M3 9h18M8 2v4M16 2v4" /></>);
const IcChat = sIco(<path d="M4 5h16v11H9l-4 4z" />);
const IcPlay = sIco(<path d="M6 4l14 8-14 8z" fill="currentColor" stroke="none" />);
const IcSun = sIco(<><circle cx="12" cy="12" r="4" /><path d="M12 2v2M12 20v2M4 12H2M22 12h-2M5 5l1.5 1.5M19 19l-1.5-1.5M19 5l-1.5 1.5M5 19l1.5-1.5" /></>);
const IcSearch = sIco(<><circle cx="11" cy="11" r="7" /><path d="M21 21l-4.3-4.3" /></>);
const IcHistory = sIco(<><circle cx="12" cy="12" r="9" /><path d="M12 7v5l3 2" /></>);
const IcDoc = sIco(<><path d="M6 3h8l4 4v14H6z" /><path d="M14 3v4h4" /></>);
const IcList = sIco(<><path d="M8 6h12M8 12h12M8 18h12" /><path d="M4 6h.01M4 12h.01M4 18h.01" /></>);
const IcPlus = sIco(<><rect x="3" y="4" width="13" height="16" rx="2" /><path d="M7 9h6M7 13h3M18 14v6M15 17h6" /></>);

// --- Предикаты контекста ---
const noteOpen = (c: AiActionCtx) => c.nav?.screen === 'notes' && !!c.nav.note;
const taskOpen = (c: AiActionCtx) => !!c.nav?.task;
const chatOpen = (c: AiActionCtx) => c.nav?.screen === 'chats' && !!c.nav.chatId;

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
    when: c => chatOpen(c) && c.flag('chat-extract-tasks') && c.online, contextual: chatOpen,
    run: () => dispatchAiRun('chat.extract'),
  },
  {
    id: 'chat.summary', title: 'Итог сессии в заметку', hint: 'конспект чата заметкой',
    section: 'chat', sectionLabel: 'Чат', icon: IcDoc,
    when: c => chatOpen(c) && c.flag('notes') && c.flag('notes-session-summary') && c.online, contextual: chatOpen,
    run: () => dispatchAiRun('chat.summary'),
  },

  // ===== Глобальные =====
  {
    id: 'global.briefing', title: 'Утренний бриф', hint: 'собрать план дня в дневник',
    section: 'global', sectionLabel: 'Глобально', icon: IcSun,
    when: c => c.flag('daily-briefing') && c.online,
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
    when: c => c.flag('unified-search'),
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
