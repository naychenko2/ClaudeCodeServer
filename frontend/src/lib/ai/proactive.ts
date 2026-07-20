// Push-слой AI-хаба: движок проактивных подсказок. Дозированно и ненавязчиво —
// оценивает текущий контекст (навигация + данные открытой сущности) и, если момент
// удачный, предлагает одно релевантное действие. Правила проверяют реальные данные
// (есть ли у заметки раздел «Связанное», есть ли у задачи подзадачи), а не только факт
// открытия — чтобы не предлагать уже сделанное. Частотный лимит + дедуп + тумблер
// держат подсказки тихими.

import type { AiActionCtx } from './actions';
import { api } from '../api';

export interface Suggestion {
  key: string;       // ключ дедупа (на сущность/день) — не показываем повторно
  actionId: string;  // какое действие реестра предложить
  text: string;      // что написать в балуне
}

// Асинхронная оценка контекста → одна подсказка или ничего. Тянет данные открытой
// сущности, чтобы правило было точным. Вызывается редко (раз на «дожитие» контекста).
export async function computeSuggestion(ctx: AiActionCtx): Promise<Suggestion | null> {
  const nav = ctx.nav;
  if (!nav) return null;

  // Открыта заметка — точные правила по её содержимому
  if (nav.screen === 'notes' && nav.note && ctx.online) {
    try {
      const note = await api.notes.get(nav.note);
      const isDaily = note.source === 'personal' && note.path.startsWith('Journal/');
      if (isDaily && !/(^|\n)##\s*Итоги дня/i.test(note.content))
        return { key: `note-daily:${nav.note}`, actionId: 'note.daily', text: 'Собрать конспект этого дня?' };
      // Содержательная заметка без раздела «Связанное» — предложить связи
      if (note.content.trim().length > 200 && !/(^|\n)##\s*Связанное/i.test(note.content))
        return { key: `note-links:${nav.note}`, actionId: 'note.links', text: 'У заметки нет связей — найти похожие?' };
      // Есть незавершённые чекбоксы — предложить превратить их в задачи
      if (/(^|\n)\s*[-*]\s+\[ \]\s+\S/.test(note.content))
        return { key: `note-tasks:${nav.note}`, actionId: 'note.promoteTasks', text: 'Превратить пункты заметки в задачи?' };
      // Документ с необработанными комментариями (флаг doc-annotations) — разобрать
      if (ctx.flag('doc-annotations')) {
        try {
          const anns = await api.notes.annotations(note.source, note.path);
          if (anns.some(a => a.status === 'open'))
            return { key: `note-annot:${nav.note}`, actionId: 'note.annotations', text: 'Есть необработанные комментарии — разобрать?' };
        } catch { /* нет комментариев/офлайн */ }
      }
    } catch { /* офлайн/ошибка — без подсказки */ }
    return null;
  }

  // Открыта задача — по её данным
  if (nav.task && ctx.online) {
    try {
      const task = await api.tasks.get(nav.task);
      if (task.status === 'done') return null;
      if (task.subtasks.length === 0)
        return { key: `task-subs:${nav.task}`, actionId: 'task.subtasks', text: 'Разбить задачу на подзадачи?' };
      const running = !!task.claudeStartedAt && !task.claudeResult;
      if (task.assignee === 'claude' && !running)
        return { key: `task-exec:${nav.task}`, actionId: 'task.execute', text: 'Поручить эту задачу Claude-исполнителю?' };
    } catch { /* без подсказки */ }
    return null;
  }

  // Открыта персона с пустым характером — предложить сгенерировать
  if (nav.screen === 'personas' && nav.persona && ctx.online) {
    try {
      const p = await api.personas.get(nav.persona);
      const hasCharacter = !!(p.contract?.character?.trim() || p.systemPrompt?.trim());
      if (!hasCharacter)
        return { key: `persona-char:${nav.persona}`, actionId: 'persona.character', text: 'У персоны пустой характер — сгенерировать?' };
    } catch { /* без подсказки */ }
    return null;
  }

  // Открыт проект — если в рабочем дереве есть незакоммиченные изменения, предложить разбор
  if (nav.screen === 'project' && nav.project && ctx.online) {
    try {
      const st = await api.git.status(nav.project.id);
      if (st.isRepo && (st.staged.length > 0 || st.unstaged.length > 0 || st.untracked.length > 0))
        return { key: `git-review:${nav.project.id}`, actionId: 'project.gitReview', text: 'Есть незакоммиченные изменения — разобрать и предложить коммиты?' };
    } catch { /* не git-репо/офлайн — без подсказки */ }
    return null;
  }

  // Календарь + есть просроченные задачи — предложить их разобрать
  if (nav.screen === 'calendar' && ctx.online) {
    try {
      const today = todayKey();
      const tasks = await api.tasks.listAll(undefined, today);
      if (tasks.some(t => t.status !== 'done' && !!t.dueDate && t.dueDate < today))
        return { key: `overdue:${today}`, actionId: 'tasks.overdue', text: 'Есть просроченные задачи — разобрать?' };
    } catch { /* офлайн — без подсказки */ }
    return null;
  }

  // Обзорные экраны + включён бриф — предложить собрать план дня (раз в день)
  if ((nav.screen === 'chats' || nav.screen === 'projects') && ctx.online)
    return { key: `briefing:${todayKey()}`, actionId: 'global.briefing', text: 'Собрать план дня — задачи, заметки, активность?' };

  return null;
}

// --- Дозирование: тумблер + частотный лимит + дедуп (localStorage) ---

const ENABLED_KEY = 'ai_proactive_enabled';
const STATE_KEY = 'ai_proactive_state';
const DISMISS_COOLDOWN_MS = 12 * 60 * 60 * 1000; // не повторять ту же подсказку 12 ч
const DAILY_CAP = 3;                             // не больше 3 балунов в день

interface ProactiveState {
  dismissed: Record<string, number>; // key → ts последнего отклонения
  day: string;                       // локальная дата, для которой считаем cap
  count: number;                     // сколько показано сегодня
}

export function isProactiveEnabled(): boolean {
  try { return localStorage.getItem(ENABLED_KEY) !== '0'; } catch { return true; }
}
export function setProactiveEnabled(on: boolean): void {
  try { localStorage.setItem(ENABLED_KEY, on ? '1' : '0'); } catch { /* ignore */ }
}

function loadState(): ProactiveState {
  try {
    const raw = localStorage.getItem(STATE_KEY);
    if (raw) {
      const s = JSON.parse(raw) as ProactiveState;
      if (s.day !== todayKey()) return { dismissed: s.dismissed ?? {}, day: todayKey(), count: 0 };
      return { dismissed: s.dismissed ?? {}, day: s.day, count: s.count ?? 0 };
    }
  } catch { /* ignore */ }
  return { dismissed: {}, day: todayKey(), count: 0 };
}
function saveState(s: ProactiveState): void {
  try { localStorage.setItem(STATE_KEY, JSON.stringify(s)); } catch { /* ignore */ }
}

// Можно ли показать подсказку с этим ключом прямо сейчас
export function canShow(key: string): boolean {
  if (!isProactiveEnabled()) return false;
  const s = loadState();
  if (s.count >= DAILY_CAP) return false;
  const last = s.dismissed[key];
  if (last && Date.now() - last < DISMISS_COOLDOWN_MS) return false;
  return true;
}

// Отметить показ (увеличивает дневной счётчик)
export function markShown(): void {
  const s = loadState();
  saveState({ ...s, count: s.count + 1 });
}

// Отметить отклонение/выполнение — не повторяем эту подсказку 12 ч
export function markDismissed(key: string): void {
  const s = loadState();
  s.dismissed[key] = Date.now();
  saveState(s);
}

function todayKey(): string {
  const d = new Date();
  const p = (n: number) => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())}`;
}
