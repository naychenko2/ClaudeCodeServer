// Чистые правила нити: цепочка шагов стопки, подписи карточки и чипа. Под юнит-тестом.

import { splitPath } from '../format';
import type { ImageThread, ImageThreadEvent, ImageThreadLaunch, ImageThreadStack, ImageThreadVersion } from './threadsApi';

// Позиция стопки: исходный файл (stepId = null) или шаг правки
export interface ThreadPos { stepId: string | null }

export const threadName = (t: ImageThread) => (t.file ? splitPath(t.file).name : 'Новая картинка');

// Нить без единой правки: ни шагов стопок, ни версий от ИИ, ни правок исходника
export const isEmptyThread = (t: ImageThread) =>
  t.stacks.every(s => !s.steps.length) && versionsOf(t).every(v => v.id === ORIGIN && !v.steps.length);

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

// Чип выбранной картинки: «Работаем с: hero.png · версия 2» (short — «hero.png · в2»);
// у нити со стопками — «hero.png · шаг 2»
export function focusLabel(t: ImageThread, short = false): string {
  if (!t.file && isEmptyThread(t)) {
    const folder = t.draftFolder ? `${t.draftFolder.replace(/\/$/, '')}/` : 'корень проекта';
    return `Новая картинка · сохранять в ${folder}`;
  }
  const cur = currentVersion(t);
  if (cur && !isLegacyThread(t)) {
    if (cur.id === ORIGIN && versionsOf(t).length === 1) return threadName(t);
    return `${threadName(t)} · ${short ? versionShort(cur) : versionName(cur)}`;
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

// Промпт запуска из записи журнала launched: «Человек запустил вручную: «промпт» · модель…»
export function launchedPrompt(text: string): string | null {
  const m = /: «([\s\S]*)» · /.exec(text);
  return m?.[1].trim() || null;
}

// Задача нити, оборванная перезапуском сервера: карточка показывает «Генерация прервана»
// вместо вечного «Рисуем…». Идущая задача важнее пометки. prompt — для перезапуска тем же
// текстом; null, если запись о запуске уже ушла из журнала
export function interruptedOf(
  t: ImageThread, events: readonly ImageThreadEvent[] = [],
): { jobId: string; prompt: string | null } | null {
  const jobId = t.interruptedJobId;
  if (!jobId || t.pendingJobId) return null;
  const launched = events.find(e => e.kind === 'launched' && e.jobId === jobId);
  return { jobId, prompt: launched ? launchedPrompt(launched.text) : null };
}

// ── Версии (изменение 27.09 к ADR-019): каждый вариант запуска ИИ — версия внизу ленты ──

export const ORIGIN = 'origin';

export const versionsOf = (t: ImageThread): ImageThreadVersion[] => t.versions ?? [];

// Нить со стопками (формат до 27.09): у неё «Взять», откат и карточка-стопка
export const isLegacyThread = (t: ImageThread) => t.stacks.some(s => s.steps.length > 0) || !!t.pendingJobId;

export const findVersion = (t: ImageThread, id: string | null | undefined) =>
  versionsOf(t).find(v => v.id === id) ?? null;

export const currentVersion = (t: ImageThread) => findVersion(t, t.currentVersionId) ?? versionsOf(t)[0] ?? null;

// Шаг картинки версии — как ImageThread.ImageStepOf на сервере: у исходника без правок —
// текущий шаг старой стопки; null — файл нити или пустой черновик
export const versionStep = (t: ImageThread, v: ImageThreadVersion) =>
  v.currentStepId ?? (v.id === ORIGIN ? t.currentStepId : v.baseStepId);

// Файл исходника: после «Сохранить в проект» нить идёт за новым файлом, а исходник — первый
export const originFile = (t: ImageThread) => t.lineage[0] ?? t.file;

export const versionHasImage = (t: ImageThread, v: ImageThreadVersion) =>
  !!versionStep(t, v) || (v.id === ORIGIN && !!originFile(t));

// «исходник» / «версия 3» — как в блоке хвоста хода агента: «поправь вторую» значит одно и то же
export const versionName = (v: ImageThreadVersion) => (v.id === ORIGIN ? 'исходник' : `версия ${v.number}`);
export const versionShort = (v: ImageThreadVersion) => (v.id === ORIGIN ? 'исходник' : `в${v.number}`);
export const fromVersion = (v: ImageThreadVersion) => (v.id === ORIGIN ? 'от исходника' : `от версии ${v.number}`);

// Промпт последнего запуска нити (по времени, кто бы ни запускал) — затравка поля режима
// «Картинка». Запуски без промпта (фон, апскейл) пропускаются
export function lastLaunchPrompt(t: ImageThread): string | null {
  let best: ImageThreadLaunch | null = null;
  for (const l of t.launches ?? []) {
    if (l.prompt?.trim() && (!best || Date.parse(l.at) > Date.parse(best.at))) best = l;
  }
  return best?.prompt?.trim() ?? null;
}

export const launchOf = (t: ImageThread, jobId: string): ImageThreadLaunch | null =>
  t.launches?.find(l => l.jobId === jobId) ?? null;

// Версии одного запуска по порядку вариантов
export const launchVersions = (t: ImageThread, jobId: string) =>
  versionsOf(t).filter(v => v.jobId === jobId).sort((a, b) => (a.variant ?? 0) - (b.variant ?? 0));

export const hasRunningLaunch = (t: ImageThread) => (t.launches ?? []).some(l => l.status === 'running');

// Надпись под запуском, кончившимся не целиком. Отмена и перезапуск сервера с частью готовых
// вариантов называются прямо: без надписи лента выглядит так, будто остальное недорисовалось
export function launchEndNote(status: ImageThreadLaunch['status'], ready: number, count: number): string | null {
  if (status === 'running' || status === 'done') return null;
  if (ready > 0) {
    const of = `готово ${ready} из ${Math.max(ready, count)}`;
    return status === 'cancelled' ? `Отменено — ${of}.`
      : status === 'interrupted' ? `Прервано перезапуском сервера — ${of}.`
      : null;
  }
  return status === 'cancelled' ? 'Генерация отменена.'
    : status === 'interrupted' ? 'Генерация прервана перезапуском сервера.'
    : 'Сервис рисования отказал — версий нет.';
}

// Подпись под картинкой версии: «вариант 1 из 2 · от исходника · FLUX Kontext»
export function versionMeta(t: ImageThread, v: ImageThreadVersion, model?: string | null): string {
  if (v.id === ORIGIN) return [t.file ? 'исходный файл' : '', v.steps.length ? 'с правками без ИИ' : ''].filter(Boolean).join(' · ');
  const siblings = v.jobId ? launchVersions(t, v.jobId) : [];
  const k = siblings.findIndex(x => x.id === v.id) + 1;
  const base = findVersion(t, v.baseVersionId);
  return [
    siblings.length > 1 ? `вариант ${k} из ${siblings.length}` : '',
    base && versionHasImage(t, base) ? fromVersion(base) : 'по тексту',
    v.steps.length > 1 ? 'с правками без ИИ' : '',
    model ?? '',
  ].filter(Boolean).join(' · ');
}

// Главная кнопка карточки версии (прототип полос): у версии в работе — «Сохранить в проект»,
// пока она черновик; у остальных версий выбранной картинки — «Продолжить от неё»; у
// картинки, которая не в работе, — «Работать с этой». null — кнопки нет
export type VersionPrimary = 'save' | 'continue' | 'work' | null;

export function versionPrimary(t: ImageThread, v: ImageThreadVersion, focused: boolean, saved: boolean): VersionPrimary {
  if (!focused) return 'work';
  if (t.currentVersionId !== v.id) return 'continue';
  return !saved && versionStep(t, v) ? 'save' : null;
}
