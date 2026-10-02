// Действия человека над нитью (ADR-019, решение 1: взять вариант, откатиться и
// сохранить в проект может только человек). Каждое — мутация с ревизией через стор;
// тексты тостов — из записки v3, раздел «Тексты».

import {
  api as appApi, autoRevealGenerationPanel, createReleaseUndo, dropAgentPick, followSelection, revealWorkspacePanel, showToast,
} from 'aihome_shell/kit';
import { IMAGES_PANEL } from '../characters/panel';
import { imageEditorApi, nameTakenSuggestion, type ImageEncodeFormat } from '../api';
import { nameStem } from '../saveAs';
import { isPersonalScope } from '../scope';
import {
  chainOf, currentStack, currentVersion, hasRunningLaunch, isEmptyThread, isLegacyThread, ORIGIN, originFile, saveFolder,
  threadHasImage, versionStep,
} from './model';
import {
  closeEditor, getEditor, getFocusedThread, getThreadMarks, getThreadsState, imageDraftKey, mutate, requestImageMode, setThreadMarks,
} from './threadStore';
import { threadsApi, type ImageThread, type ImageThreadTake, type ImageThreadVersion } from './threadsApi';
import { effectiveImageMode, getStoredImageMode, modeAware, noteImageMode, setImageMode, type ImageMode } from './modeState';

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

// Как открывать панель после действия человека: auto — как решено в v4; none — не открывать.
// none зовут смена режима сегментом и выбор в меню полосы на телефоне: шторка не поднимается,
// человек выбрал картинку там, где пишет промпт (макет v5). Шторка ли сейчас — знает разметка
export type RevealMode = 'auto' | 'none';

// Выбор картинки человеком открывает панель «Картинки», пока её не закрыли в этом чате
// (решения Андрея по v4, 2). Выбор агента (image_focus) приходит в стор с сервера и сюда не идёт
function revealPanel(ok: boolean, sessionId: string, how: RevealMode = 'auto'): boolean {
  if (ok && how === 'auto') autoRevealGenerationPanel(IMAGES_PANEL, sessionId);
  return ok;
}

// Кнопка («Работать с этой», «Продолжить от неё») — просьба открыть: панель «Картинки»
// открывается и закрытая («Панель следует за выбором», правило 4)
function openPanel(ok: boolean, sessionId: string, threadId: string): boolean {
  if (!ok) return false;
  dropAgentPick(sessionId, imageDraftKey(threadId));
  revealWorkspacePanel(IMAGES_PANEL, 'settings', { sessionId, target: imageDraftKey(threadId) });
  return true;
}

// Картинку выбрал человек — режим «Править» (флаг image-panel-v5); выбор агента сюда не идёт
function humanPick(ok: boolean, sessionId: string): boolean {
  if (ok) noteImageMode(sessionId, 'edit');
  return ok;
}

// Клик человека по карточке в ленте: картинка — в работу, открытая панель переключается на
// «Картинки → Настройки», закрытая не открывается (правило 1)
export async function pickByHuman(projectId: string, sessionId: string, threadId: string, focused: boolean): Promise<boolean> {
  const ok = focused || await mutate(projectId, sessionId, rev => threadsApi.focus(projectId, sessionId, threadId, rev));
  if (ok) {
    noteImageMode(sessionId, 'edit');
    followSelection(IMAGES_PANEL, sessionId, imageDraftKey(threadId));
  }
  return ok;
}

// «Продолжить от неё» и «Работать с этой»: версия становится текущей, нить — в работе
export const continueFrom = async (projectId: string, sessionId: string, t: ImageThread, versionId: string) =>
  openPanel(humanPick(await mutate(projectId, sessionId, rev => threadsApi.current(projectId, sessionId, t.id, versionId, rev)), sessionId), sessionId, t.id);

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
export async function workWith(projectId: string, sessionId: string, threadId: string | null) {
  const ok = await mutate(projectId, sessionId, rev => threadsApi.focus(projectId, sessionId, threadId, rev));
  if (threadId) openPanel(humanPick(ok, sessionId), sessionId, threadId);
  return ok;
}

// Файл проекта в работу: сервер найдёт его нить или заведёт новую с якорем в ленте
export const workWithFile = async (projectId: string, sessionId: string, file: string, how: RevealMode = 'auto') =>
  revealPanel(humanPick(await mutate(projectId, sessionId, rev => threadsApi.create(projectId, sessionId, { file, revision: rev })), sessionId), sessionId, how);

// «✦ Нарисовать новую»: черновик «Новая картинка» (чип в полосе, в ленту не рисуется) и фокус на него. Черновик завёл
// человек — это и есть просьба о режиме «Картинка»; черновик агента режим не меняет
export async function createDraft(projectId: string, sessionId: string, folder: string, how: RevealMode = 'auto') {
  const ok = await mutate(projectId, sessionId, rev => threadsApi.create(projectId, sessionId, { draftFolder: folder, revision: rev }));
  if (ok) {
    noteImageMode(sessionId, 'create');
    requestImageMode(sessionId);
  }
  return revealPanel(ok, sessionId, how);
}

// ── Режим «Создать / Править» (флаг image-panel-v5) ──

// «Вернуть» после снятия выбора человеком в «Править»: одна плашка на модуль, полоса
// показывает её только своему чату. Агент снимает выбор событием нитей мимо releaseFocus — плашки нет
// file — картинка-файл без правок: её нить при снятии уходит из ленты, возвращается по файлу
export interface ImageReleaseSnapshot { projectId: string; sessionId: string; threadId: string; file: string | null }
export const imageReleaseUndo = createReleaseUndo<ImageReleaseSnapshot>();
export const IMAGE_RELEASE_TEXT = 'Картинка снята — дальше рисуем новую';

// Отметки снятой картинки живут до конца плашки: иначе «Вернуть» вернул бы её без них.
// Плашка ушла не через «Вернуть» и картинку снова не выбрали — отметки гасятся
let _offered: ImageReleaseSnapshot | null = null;
let _restoring: string | null = null;
imageReleaseUndo.subscribe(() => {
  const prev = _offered;
  _offered = imageReleaseUndo.current()?.snapshot ?? null;
  if (!prev || _offered?.threadId === prev.threadId || _restoring === prev.threadId) return;
  if (getFocusedThread(prev.sessionId)?.id !== prev.threadId) setThreadMarks(prev.threadId, [], null);
});

// Картинка — в работу человеком и «Править» — строго друг за другом: режим ставится уже на
// выбранную картинку. Поле ввода — в режим «Картинка»: промпт правки пишут там
export async function editThreadByHuman(projectId: string, sessionId: string, threadId: string, how: RevealMode): Promise<boolean> {
  const focused = getFocusedThread(sessionId)?.id === threadId;
  if (!focused && !await mutate(projectId, sessionId, rev => threadsApi.focus(projectId, sessionId, threadId, rev))) return false;
  setImageMode(sessionId, 'edit');
  requestImageMode(sessionId);
  dropAgentPick(sessionId, imageDraftKey(threadId));
  revealPanel(true, sessionId, how);
  return true;
}

// Файл проекта из «Что править?»: нить по файлу (найдётся или заведётся), затем «Править» —
// явно, как у editThreadByHuman, а не попутно через humanPick
export async function editFileByHuman(projectId: string, sessionId: string, file: string, how: RevealMode): Promise<boolean> {
  if (!await workWithFile(projectId, sessionId, file, how)) return false;
  setImageMode(sessionId, 'edit');
  requestImageMode(sessionId);
  return true;
}

// «С компьютера…»: в проекте файл ложится вложением чата в .cc-attachments и берётся в работу
// как файл проекта (сторож пути нити — тот же SafePath). В личном чате пункта нет
export async function editUploadByHuman(projectId: string, sessionId: string, file: File, how: RevealMode): Promise<boolean> {
  if (isPersonalScope(projectId)) return false;
  let path: string;
  try {
    path = (await appApi.chats.uploadFile(sessionId, file, projectId)).path;
  } catch (e) {
    showToast(`Не загрузилось: ${(e as Error).message}`, '', 'error');
    return false;
  }
  return editFileByHuman(projectId, sessionId, path, how);
}

// Сегмент «Создать / Править» в полосе и в панели. «Создать» при выбранной картинке заводит
// черновик «Новая картинка» (картинка остаётся в ленте); «Править» без картинки сюда не
// приходит — сегмент приглушён и спрашивает «Что править?». Панель смена режима не открывает
export async function setImageModeByHuman(projectId: string, sessionId: string, mode: ImageMode): Promise<boolean> {
  const t = getFocusedThread(sessionId);
  if (mode === 'create') {
    if (threadHasImage(t)) return createDraft(projectId, sessionId, '', 'none');
    setImageMode(sessionId, 'create');
    requestImageMode(sessionId);
    return true;
  }
  if (!t || !threadHasImage(t)) return false;
  setImageMode(sessionId, 'edit');
  requestImageMode(sessionId);
  return true;
}

// ✕ на чипе: снять выбор; пустая нить (черновик или файл без шагов) уходит из ленты целиком.
// С флагом image-panel-v5 поле остаётся «Картинкой» в «Создать», а снятие картинки из
// «Править» даёт плашку «Вернуть»; без флага — прежний тост про «Чат»
export async function releaseFocus(projectId: string, sessionId: string, t: ImageThread | null) {
  const empty = !!t && isEmptyThread(t) && !t.pendingJobId && !hasRunningLaunch(t);
  const v5 = modeAware();
  const wasEdit = v5 && !!t && threadHasImage(t) && effectiveImageMode(getStoredImageMode(sessionId), true) === 'edit';
  const ok = empty && t
    ? await mutate(projectId, sessionId, rev => threadsApi.remove(projectId, sessionId, t.id, rev))
    : await mutate(projectId, sessionId, rev => threadsApi.focus(projectId, sessionId, null, rev));
  if (!ok) return;
  if (t && getEditor()?.threadId === t.id) closeEditor();
  if (!v5) {
    if (t) setThreadMarks(t.id, [], null);
    showToast('Картинка больше не выбрана: режим «Чат»', '', 'info');
    return;
  }
  setImageMode(sessionId, 'create');
  if (wasEdit && t) {
    imageReleaseUndo.release({ snapshot: { projectId, sessionId, threadId: t.id, file: empty ? t.file : null }, text: IMAGE_RELEASE_TEXT }, true);
  } else if (t) setThreadMarks(t.id, [], null);
}

// «Вернуть»: та же картинка и «Править»; панель не открывается — выбор и не уходил из полосы
export async function undoImageRelease(): Promise<boolean> {
  const s = imageReleaseUndo.current()?.snapshot ?? null;
  if (!s) return false;
  _restoring = s.threadId;
  try {
    imageReleaseUndo.undo();
    const ok = s.file
      ? await editFileByHuman(s.projectId, s.sessionId, s.file, 'none')
      : await editThreadByHuman(s.projectId, s.sessionId, s.threadId, 'none');
    // Нить файла заведена заново под новым id — отметки переезжают на неё
    const now = ok ? getFocusedThread(s.sessionId)?.id ?? null : null;
    const { marks, size } = getThreadMarks(s.threadId);
    if (now && now !== s.threadId && marks.length) setThreadMarks(now, marks, size);
    if (now !== s.threadId) setThreadMarks(s.threadId, [], null);
    return ok;
  } finally {
    _restoring = null;
  }
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
