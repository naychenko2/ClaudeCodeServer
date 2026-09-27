// Чистые правила нити: цепочка шагов стопки, подписи карточки и чипа. Под юнит-тестом.

import { splitPath } from '../format';
import type { ImageThread, ImageThreadStack } from './threadsApi';

// Позиция стопки: исходный файл (stepId = null) или шаг правки
export interface ThreadPos { stepId: string | null }

export const threadName = (t: ImageThread) => (t.file ? splitPath(t.file).name : 'Новая картинка');

export const isEmptyThread = (t: ImageThread) => t.stacks.every(s => !s.steps.length);

export const findStack = (t: ImageThread, stackId: string | null | undefined) =>
  t.stacks.find(s => s.stackId === stackId) ?? null;

export const currentStack = (t: ImageThread) => findStack(t, t.currentStackId) ?? t.stacks[t.stacks.length - 1] ?? null;

// Цепочка стопки: исходник (если он есть у нити) и шаги. Стопка после отката хранит
// шаги целиком с начала (общие до развилки плюс новые) — склеивать с родителем не нужно
export function chainOf(t: ImageThread, stack: ImageThreadStack | null): ThreadPos[] {
  const own = (stack?.steps ?? []).map(stepId => ({ stepId }));
  return t.file || t.lineage.length ? [{ stepId: null }, ...own] : own;
}

// Где в цепочке стоит нить сейчас; у чужой (старой) стопки — последний шаг
export function currentIndex(t: ImageThread, chain: ThreadPos[], stack: ImageThreadStack | null): number {
  if (!chain.length) return -1;
  if (stack && stack.stackId === currentStack(t)?.stackId) {
    const i = chain.findIndex(p => p.stepId === t.currentStepId);
    if (i >= 0) return i;
  }
  return chain.length - 1;
}

// «шаг 2 из 3»: исходник — тоже шаг, как в макете («шаг 1 из 1» у свежего файла)
export const stepOf = (i: number, total: number) => `шаг ${i + 1} из ${total}`;

// «v1 · в проекте» — показан сам файл; «черновик» — шаг, ещё не сохранённый в проект
export function versionLabel(t: ImageThread, pos: ThreadPos | undefined, savedStepId: string | null): string {
  if (!t.file) return 'черновик';
  if (!pos || pos.stepId === null || pos.stepId === savedStepId) return 'в проекте';
  return 'черновик';
}

// Чип выбранной картинки: «Работаем с: hero.png · шаг 2»
export function focusLabel(t: ImageThread): string {
  if (!t.file && isEmptyThread(t)) {
    const folder = t.draftFolder ? `${t.draftFolder.replace(/\/$/, '')}/` : 'корень проекта';
    return `Новая картинка · сохранять в ${folder}`;
  }
  const stack = currentStack(t);
  const chain = chainOf(t, stack);
  const i = currentIndex(t, chain, stack);
  return chain.length > 1 ? `${threadName(t)} · шаг ${i + 1}` : threadName(t);
}

// Папка сохранения нити: рядом с файлом или папка черновика
export function saveFolder(t: ImageThread): string {
  if (t.file) return splitPath(t.file).folder;
  return t.draftFolder ?? '';
}
