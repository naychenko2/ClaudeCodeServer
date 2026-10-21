// Пометки переходов «чат / задача» на странице фичи и в чате (волна 3, Ф9b).
//
// Источник правды — данные, уже пришедшие с бэка (фронт ничего не считает «с нуля»):
//   • «задача закрыта» — статус задачи done среди задач агрегата FeatureRun
//     (epic, cut, plan, steps, reviews, qa, final, merge);
//   • «в архиве» — чат архивный: chats[].archived страницы фичи и
//     stages[].executors[].chatArchived агрегата.
// Строится ОДИН раз на страницу/плашку и раздаётся строкам-потребителям пропом `marks`,
// чтобы каждое место NavTargetButton брало пометку из реальных данных, а не из `false`
// по умолчанию. Незнакомый id → не помечен (нет данных — нет пометки).

import type { FeatureRun, FeatureTask } from '../types/featureRun';
import type { FeatureChatLink as FeaturePageChatLink } from '../types/featureChats';

export interface NavMarks {
  // Чат в архиве (по id чата).
  chatArchived: (chatId: string | null | undefined) => boolean;
  // Задача закрыта: status === 'done' (по id задачи).
  taskClosed: (taskId: string | null | undefined) => boolean;
}

// Пустые пометки: данных нет — ничего не помечено (значение по умолчанию у проп-потребителей).
export const EMPTY_NAV_MARKS: NavMarks = {
  chatArchived: () => false,
  taskClosed: () => false,
};

// Все задачи агрегата (в тех полях, где бэк их несёт).
function runTasks(run: FeatureRun): FeatureTask[] {
  const all: (FeatureTask | null | undefined)[] = [
    run.epic, run.final, run.merge,
    ...(run.steps ?? []), ...(run.reviews ?? []), ...(run.qa ?? []),
    ...(run.cut ?? []), ...(run.plan ?? []),
  ];
  return all.filter((t): t is FeatureTask => !!t);
}

// Собрать пометки из агрегата фичи и (необязательно) списка чатов страницы.
export function buildNavMarks(
  run: FeatureRun | null | undefined,
  chats?: readonly Pick<FeaturePageChatLink, 'id' | 'archived'>[] | null,
): NavMarks {
  const closed = new Set<string>();
  const archived = new Set<string>();
  if (run) {
    for (const t of runTasks(run)) if (t.status === 'done') closed.add(t.id);
    for (const s of run.stages ?? []) {
      for (const x of s.executors ?? []) if (x.chatId && x.chatArchived) archived.add(x.chatId);
    }
  }
  for (const c of chats ?? []) if (c.archived) archived.add(c.id);
  return {
    chatArchived: id => !!id && archived.has(id),
    taskClosed: id => !!id && closed.has(id),
  };
}

// Контекст переходов для блоков, у которых нет своих featureId/агрегата (вкладка «Статистика»):
// фича и название — для точки возврата («← к фиче · Статистика»), marks — пометки.
export interface FeatureNavCtx {
  featureId?: string | null;
  featureTitle?: string | null;
  marks?: NavMarks;
}
