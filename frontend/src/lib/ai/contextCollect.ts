// Сбор компактного описания открытой сущности для локального ранжирования действий.
// Фронт уже держит контекст (nav + открытая сущность) и умеет тянуть её данные теми же
// api.*.get, что и проактивный движок. Возвращает { type, text (усечён), actions }.
// null — контекст, для которого ранжировать нечего (нет доступных действий).

import type { AiActionCtx } from './actions';
import { api } from '../api';

export interface ActionOption { id: string; title: string; hint: string }

export interface CollectedContext {
  type: string;                                   // note|task|file|persona|knowledge|project|chat|calendar|global
  text: string;                                   // компактное описание сущности
  actions: ActionOption[];
}

const MAX = 1800; // усечение содержимого — на ранжирование хватает начала

function truncate(s: string): string {
  const t = s.trim();
  return t.length > MAX ? t.slice(0, MAX) + '…' : t;
}

// actions — уже отфильтрованные доступные действия (передаёт вызывающий, у которого есть
// AI_ACTIONS): так contextCollect не импортит actions.tsx как значение (нет цикла).
export async function collectContext(ctx: AiActionCtx, actions: ActionOption[]): Promise<CollectedContext | null> {
  const nav = ctx.nav;
  if (actions.length === 0) return null;

  try {
    // Заметка
    if (nav?.screen === 'notes' && nav.note) {
      const n = await api.notes.get(nav.note);
      const checkboxes = (n.content.match(/^\s*[-*]\s\[ \]/gm) || []).length;
      const meta = [
        `путь: ${n.path}`,
        n.tags?.length ? `теги: ${n.tags.join(', ')}` : 'без тегов',
        /(^|\n)##\s*Связанное/i.test(n.content) ? 'есть раздел «Связанное»' : 'нет раздела «Связанное»',
        checkboxes ? `незакрытых пунктов: ${checkboxes}` : 'без чекбоксов',
      ].join('; ');
      return { type: 'note', text: `${meta}\n\n${truncate(n.content)}`, actions };
    }

    // Задача
    if (nav?.task) {
      const t = await api.tasks.get(nav.task);
      const running = !!t.claudeStartedAt && !t.claudeResult;
      const meta = [
        `статус: ${t.status}`,
        `приоритет: ${t.priority}`,
        t.dueDate ? `срок: ${t.dueDate}` : 'без срока',
        `исполнитель: ${t.assignee ?? 'я'}`,
        `подзадач: ${t.subtasks.length}`,
        t.description?.trim() ? 'есть описание' : 'без описания',
        running ? 'исполнитель AI сейчас работает' : '',
      ].filter(Boolean).join('; ');
      return { type: 'task', text: `${t.title}\n${meta}\n\n${truncate(t.description || '')}`, actions };
    }

    // Файл проекта
    if (nav?.screen === 'project' && nav.file && nav.project) {
      const f = await api.files.getContent(nav.project.id, nav.file);
      if (f.isBinary || f.isImage) return { type: 'file', text: `${nav.file} (бинарный/изображение)`, actions };
      return { type: 'file', text: `${nav.file}\n\n${truncate(f.content || '')}`, actions };
    }

    // Персона
    if (nav?.screen === 'personas' && nav.persona) {
      const p = await api.personas.get(nav.persona);
      const hasChar = !!(p.contract?.character?.trim() || p.systemPrompt?.trim());
      const text = [
        `роль: ${p.role}`,
        `имя: ${p.name}`,
        hasChar ? 'характер задан' : 'характер пустой',
        `аватар: ${p.avatar?.kind === 'image' ? 'фото' : 'инициалы'}`,
      ].join('; ');
      return { type: 'persona', text, actions };
    }

    // База знаний
    if (nav?.screen === 'knowledge' && nav.knowledge) {
      const k = await api.knowledgeBases.get(nav.knowledge);
      const docs = k.documentCount ?? 0;
      const text = [
        `название: ${k.title}`,
        `документов: ${docs}${docs === 0 ? ' (пустая)' : ''}`,
        k.description ? `описание: ${k.description}` : '',
      ].filter(Boolean).join('; ');
      return { type: 'knowledge', text, actions };
    }

    // Проект (без открытого файла/задачи)
    if (nav?.screen === 'project' && nav.project) {
      return { type: 'project', text: `проект: ${nav.project.name}`, actions };
    }

    // Активный чат — краткий транскрипт последних реплик (если фронт его выставил)
    if (ctx.chat.active) {
      const tail = ctx.chat.tail?.trim();
      return { type: 'chat', text: tail ? truncate(tail) : 'открыт чат с перепиской', actions };
    }

    // Календарь
    if (nav?.screen === 'calendar') {
      return { type: 'calendar', text: 'открыт календарь задач', actions };
    }

    // Прочие обзорные экраны — глобальный контекст
    return { type: 'global', text: `экран: ${nav?.screen ?? 'home'}`, actions };
  } catch {
    return null; // офлайн/ошибка загрузки — без ранжирования (фолбэк на правила)
  }
}
