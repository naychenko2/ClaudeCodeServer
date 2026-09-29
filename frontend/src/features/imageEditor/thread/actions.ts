// Действия человека над нитью (ADR-019, решение 1: взять вариант, откатиться и
// сохранить в проект может только человек). Каждое — мутация с ревизией через стор;
// тексты тостов — из записки v3, раздел «Тексты».

import { showToast } from 'aihome_shell/kit';
import { imageEditorApi, nameTakenSuggestion, type ImageEncodeFormat } from '../api';
import { nameStem } from '../saveAs';
import { isPersonalScope } from '../scope';
import {
  chainOf, currentStack, currentVersion, hasRunningLaunch, isEmptyThread, isLegacyThread, ORIGIN, originFile, saveFolder,
  versionStep,
} from './model';
import { closeEditor, getEditor, getThreadsState, mutate, requestImageMode, setThreadMarks } from './threadStore';
import { threadsApi, type ImageThread, type ImageThreadTake, type ImageThreadVersion } from './threadsApi';

// Шаг, который уже лежит в проекте файлом нити: версия «в проекте», а не «черновик»
const _saved = new Map<string, string>();
const _savedSteps = new Set<string>();
export const savedStepOf = (threadId: string) => _saved.get(threadId) ?? null;
export const isSavedStep = (stepId: string | null) => !!stepId && _savedSteps.has(stepId);

// Версия лежит в проекте: исходник без правок — это и есть файл, остальные — после «Сохранить»
export function versionSaved(t: ImageThread, v: ImageThreadVersion): boolean {
  if (v.id === ORIGIN && !v.steps.length) return !!originFile(t);
  return isSavedStep(versionStep(t, v));
}

function markSaved(threadId: string, stepId: string) {
  _saved.set(threadId, stepId);
  _savedSteps.add(stepId);
}

// Шаг, от которого идёт следующая правка: у нити с версиями — картинка текущей версии
export function activeStepOf(t: ImageThread): string | null {
  if (isLegacyThread(t)) return t.currentStepId;
  const v = currentVersion(t);
  return v ? versionStep(t, v) : t.currentStepId;
}

// «Продолжить от неё» и «Работать с этой»: версия становится текущей, нить — в работе
export const continueFrom = (projectId: string, sessionId: string, t: ImageThread, versionId: string) =>
  mutate(projectId, sessionId, rev => threadsApi.current(projectId, sessionId, t.id, versionId, rev));

// Правка без ИИ: у нити с версиями — шаг текущей версии, у старой — «Взять» шага в стопку
export const applyStep = (projectId: string, sessionId: string, t: ImageThread, stepId: string) =>
  isLegacyThread(t)
    ? takeVariant(projectId, sessionId, t, { stepId })
    : mutate(projectId, sessionId, rev => threadsApi.addStep(projectId, sessionId, t.id, stepId, rev));

export async function takeVariant(projectId: string, sessionId: string, t: ImageThread, what: ImageThreadTake): Promise<boolean> {
  const ok = await mutate(projectId, sessionId, rev => threadsApi.take(projectId, sessionId, t.id, what, rev));
  if (ok && 'jobId' in what) {
    const next = getThreadsState(sessionId).threads.find(x => x.id === t.id);
    const n = next ? chainOf(next, currentStack(next)).length : 0;
    showToast(n ? `Шаг ${n} — черновик. Сохраните в проект, когда понравится` : 'Вариант взят', '', 'info');
  }
  return ok;
}

export const dismissJob = (projectId: string, sessionId: string, t: ImageThread, jobId: string) =>
  mutate(projectId, sessionId, rev => threadsApi.dismiss(projectId, sessionId, t.id, jobId, rev));

export async function rollbackTo(projectId: string, sessionId: string, t: ImageThread, stepId: string | null, label: string) {
  const ok = await mutate(projectId, sessionId, rev => threadsApi.rollback(projectId, sessionId, t.id, stepId, rev));
  if (ok) showToast(`${label}: следующая правка пойдёт от него, поздние шаги останутся в ленте старой стопкой`, '', 'info');
  return ok;
}

// «Работать с этой»: фокус на существующую нить
export const workWith = (projectId: string, sessionId: string, threadId: string | null) =>
  mutate(projectId, sessionId, rev => threadsApi.focus(projectId, sessionId, threadId, rev));

// Файл проекта в работу: сервер найдёт его нить или заведёт новую с якорем в ленте
export const workWithFile = (projectId: string, sessionId: string, file: string) =>
  mutate(projectId, sessionId, rev => threadsApi.create(projectId, sessionId, { file, revision: rev }));

// «✦ Нарисовать новую»: карточка-черновик «Новая картинка» и фокус на неё. Черновик завёл
// человек — это и есть просьба о режиме «Картинка»; черновик агента режим не меняет
export async function createDraft(projectId: string, sessionId: string, folder: string) {
  const ok = await mutate(projectId, sessionId, rev => threadsApi.create(projectId, sessionId, { draftFolder: folder, revision: rev }));
  if (ok) requestImageMode(sessionId);
  return ok;
}

// ✕ на чипе: снять выбор; пустая нить (черновик или файл без шагов) уходит из ленты целиком
export async function releaseFocus(projectId: string, sessionId: string, t: ImageThread | null) {
  const empty = !!t && isEmptyThread(t) && !t.pendingJobId && !hasRunningLaunch(t);
  const ok = empty && t
    ? await mutate(projectId, sessionId, rev => threadsApi.remove(projectId, sessionId, t.id, rev))
    : await mutate(projectId, sessionId, rev => threadsApi.focus(projectId, sessionId, null, rev));
  if (!ok) return;
  if (t) setThreadMarks(t.id, [], null);
  if (t && getEditor()?.threadId === t.id) closeEditor();
  showToast('Картинка больше не выбрана: режим «Чат»', '', 'info');
}

// «Сохранить в проект»: файл — следующей версией рядом, черновик — под свободным именем
// в своей папке. Карточка переходит на новый файл (это делает сервер по threadId)
export async function saveToProject(
  projectId: string, sessionId: string, t: ImageThread, stepId: string | null = activeStepOf(t),
): Promise<string | null> {
  // Личному чату сохранять некуда: кнопки там нет, а это — страховка от будущих вызовов
  if (isPersonalScope(projectId) || !stepId) return null;
  const api = imageEditorApi();
  try {
    let res: { path: string };
    if (t.file) {
      res = await api.save(projectId, { variant: 0, stepId, sourcePath: t.file, mode: 'next-version', sessionId, threadId: t.id });
    } else {
      const folder = saveFolder(t);
      // Формат нужен проверке имени только для расширения подсказки; итоговое ставит сервер по байтам
      const check = await api.saveCheck(projectId, { folder, name: 'kartinka', format: 'png' });
      const stem = nameStem(check.suggestion ?? check.path.split('/').pop() ?? 'kartinka');
      res = await api.save(projectId, { variant: 0, stepId, folder, fileName: stem, mode: 'as', sessionId, threadId: t.id });
    }
    markSaved(t.id, stepId);
    showToast(`Сохранено в проект: ${res.path}`, '', 'info');
    return res.path;
  } catch (e) {
    showToast(`Не сохранилось: ${(e as Error).message}`, '', 'error');
    return null;
  }
}

// «Сохранить как…» из попапа: имя и папку выбрал человек; занятое имя — 409 с подсказкой
export async function saveAsInThread(
  projectId: string, sessionId: string, t: ImageThread, v: { folder: string; fileName: string }, format?: ImageEncodeFormat,
  stepId: string | null = activeStepOf(t),
): Promise<void> {
  if (isPersonalScope(projectId) || !stepId) return;
  try {
    const res = await imageEditorApi().save(projectId, {
      variant: 0, stepId, folder: v.folder, fileName: v.fileName, mode: 'as', sessionId, threadId: t.id,
      encode: format ? { format } : null,
    });
    markSaved(t.id, stepId);
    showToast(`Сохранено в проект: ${res.path}`, '', 'info');
  } catch (e) {
    const suggestion = nameTakenSuggestion(e);
    throw new Error(suggestion ? `Такой файл уже есть — свободное имя: ${suggestion}` : (e as Error).message, { cause: e });
  }
}
