// Единый реестр категорий артефактов: ключи, иконки, заголовки и логика
// видимости/счётчиков. Используется И вкладками старой ArtifactsPanel,
// И рельсой/панельками нового интерфейса (workspace-cc-panels) — чтобы
// счётчики в двух режимах никогда не разъезжались.
import { ClipboardList, ListTodo, StickyNote, MessageCircle, Bot, File, Link2, User, type LucideIcon } from 'lucide-react';
import type { SessionArtifacts } from '../../hooks/useSessionArtifacts';

export const PANEL_KEYS = ['plan', 'todos', 'notes', 'comments', 'agents', 'files', 'links', 'context'] as const;
export type PanelKey = typeof PANEL_KEYS[number];

export const PANEL_META: Record<PanelKey, { title: string; Icon: LucideIcon }> = {
  plan: { title: 'План', Icon: ClipboardList },
  todos: { title: 'Задачи', Icon: ListTodo },
  notes: { title: 'Заметки', Icon: StickyNote },
  comments: { title: 'Комментарии', Icon: MessageCircle },
  agents: { title: 'Агенты', Icon: Bot },
  files: { title: 'Файлы', Icon: File },
  links: { title: 'Ссылки', Icon: Link2 },
  context: { title: 'Контекст', Icon: User },
};

export interface PanelBadgeOpts {
  // Чат запущен ради задачи (Session.taskId) — она входит в счётчик «Задачи»
  executingTask: boolean;
  // Собеседник-персона — включает категорию «Контекст»
  personaId?: string | null;
  // Чат-режим (без проекта): файлы открывать некуда — категория «Файлы» скрыта
  isChat: boolean;
}

export interface PanelBadge {
  visible: boolean;
  // Текст для ярлыка вкладки/чипа шапки панельки: '3', '2/5' или null (без счётчика)
  badge: string | null;
  // Компактное число для кружка-бейджа на рельсе (дроби туда не влезают) или null
  railCount: number | null;
}

// Логика перенесена из блока tabs.push(...) старой ArtifactsPanel один-в-один.
export function panelBadge(key: PanelKey, a: SessionArtifacts, opts: PanelBadgeOpts): PanelBadge {
  switch (key) {
    case 'plan':
      return { visible: a.plans.length > 0, badge: null, railCount: null };
    case 'todos': {
      const extra = opts.executingTask ? 1 : 0;
      const done = a.todos.filter(t => t.status === 'completed').length;
      const total = a.todos.length + extra;
      return { visible: a.todos.length > 0 || opts.executingTask, badge: `${done + extra}/${total}`, railCount: total };
    }
    case 'notes':
      return { visible: a.notes.length > 0, badge: `${a.notes.length}`, railCount: a.notes.length };
    case 'comments':
      return { visible: a.comments.length > 0, badge: `${a.comments.length}`, railCount: a.comments.length };
    case 'agents': {
      // Все агенты сессии (одиночные + внутри workflow); «завершено» = done + error,
      // чтобы счётчик доходил до N/N, когда никто не пашет (см. коммент в старой панели)
      const all = [...a.agents, ...a.workflows.flatMap(w => w.agents)];
      const running = all.filter(x => x.status === 'running').length;
      const total = all.length + a.workflows.filter(w => !w.agents.length).length;
      return { visible: total > 0, badge: `${total - running}/${total}`, railCount: total };
    }
    case 'files':
      return { visible: a.files.length > 0 && !opts.isChat, badge: `${a.files.length}`, railCount: a.files.length };
    case 'links':
      return { visible: a.links.length > 0, badge: `${a.links.length}`, railCount: a.links.length };
    case 'context':
      return { visible: !!opts.personaId, badge: null, railCount: null };
  }
}
