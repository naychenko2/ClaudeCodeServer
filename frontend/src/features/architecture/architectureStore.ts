// Стор раздела «Архитектура»: модель проекта с сервера, её версия и жизненный цикл
// сохранений из iframe Viaduct. Модульный (как lib/codeGraph): панель в рельсе и
// документ в центре смотрят в одно состояние. Активен один проект за раз.
//
// Сохранение: мост присылает persist-обёртку стора (строку ключа localStorage) после
// дебаунса → PUT с версией, от которой шли правки. Совпало по смыслу с файлом — PUT не
// шлём (иначе первая же гидратация редактора переформатировала бы файл). Ушло в 409 —
// файл успели поменять (вторая вкладка, персона, «Собрать из кода»): плашка конфликта,
// дальнейшие правки не пишутся, пока человек не выберет «Перезагрузить» или «Скачать».
// Пока документ открыт, сверяем версию: чужая правка без своих несохранённых — тихая
// перезагрузка модели, со своими — та же плашка конфликта; удалённый файл — «модели нет».
// Главный сигнал сверки — push: событие filesChanged проекта с путём модели (git checkout,
// правка руками, удаление) приходит за доли секунды. Опрос раз в POLL_MS — страховка на
// потерянное событие (реконнект SignalR, сбой watcher'а).
import { useSyncExternalStore } from 'react';
import { api } from 'aihome_shell/kit';
import type { ArchitectureModelDto } from '../../lib/api';
import type { FrameStamp } from './ViaductFrame';

export type ArchStatus = 'idle' | 'loading' | 'ready' | 'missing' | 'error';
export type SaveState = 'saved' | 'saving' | 'failed';

export interface ArchConflict {
  content: string | null;
  version: string | null;
  updatedAt: string | null;
  updatedBy: string | null;
}

export interface ArchState {
  projectId: string | null;
  status: ArchStatus;
  error: string | null;
  // Файл на сервере: байты как есть + версия (SHA-256) + кто и когда
  content: string | null;
  version: string | null;
  updatedAt: string | null;
  updatedBy: string | null;
  // Счётчик перемонтирования iframe: растёт, когда фрейм надо поднять заново с content
  frameKey: number;
  // «Начать с пустого холста»: модели нет, но редактор открыт — первое сохранение создаст файл
  blank: boolean;
  generating: boolean;
  generateError: string | null;
  corrupt: boolean;
  save: SaveState;
  saveError: string | null;
  // Есть правки на холсте, которые ещё не легли в файл
  dirty: boolean;
  conflict: ArchConflict | null;
  // Только просмотр по выбору человека (мобила ставит его сама, см. документ)
  readOnly: boolean;
  // Последняя модель, присланная мостом, — для «Скачать мои правки»
  localValue: string | null;
  focus: { id: string; tick: number } | null;
}

const INITIAL: ArchState = {
  projectId: null, status: 'idle', error: null,
  content: null, version: null, updatedAt: null, updatedBy: null,
  frameKey: 0, blank: false, generating: false, generateError: null, corrupt: false,
  save: 'saved', saveError: null, dirty: false, conflict: null, readOnly: false,
  localValue: null, focus: null,
};

export const POLL_MS = 30_000;
export const MODEL_PATH = 'docs/architecture/model.viaduct.json';

let state: ArchState = INITIAL;
const listeners = new Set<() => void>();
function set(patch: Partial<ArchState>) {
  state = { ...state, ...patch };
  listeners.forEach(fn => fn());
}
function subscribe(fn: () => void) { listeners.add(fn); return () => { listeners.delete(fn); }; }
function getState() { return state; }

export function useArchitecture(): ArchState {
  return useSyncExternalStore(subscribe, getState, getState);
}

// Окно фрейма, чтобы попросить мост отдать отложенную правку (flush)
let frameWindow: Window | null = null;
export function setFrameWindow(w: Window | null) { frameWindow = w; }

// Канонический вид JSON (ключи по алфавиту): «то же самое по смыслу» при другом
// форматировании и порядке ключей — файл пишет сервер с отступами, стор Viaduct — в строку
function canonical(text: string | null): string | null {
  if (text == null) return null;
  const sort = (v: unknown): unknown => {
    if (Array.isArray(v)) return v.map(sort);
    if (v && typeof v === 'object') {
      return Object.fromEntries(Object.keys(v as object).sort().map(k => [k, sort((v as Record<string, unknown>)[k])]));
    }
    return v;
  };
  try { return JSON.stringify(sort(JSON.parse(text))); } catch { return null; }
}

function errorText(e: unknown): string {
  return e instanceof Error ? e.message : String(e);
}

function applyServer(dto: ArchitectureModelDto, remount: boolean) {
  set({
    status: dto.exists ? 'ready' : 'missing',
    error: null,
    content: dto.content,
    version: dto.version,
    updatedAt: dto.updatedAt,
    updatedBy: dto.updatedBy,
    corrupt: dto.exists && canonical(dto.content) === null,
    ...(remount ? { frameKey: state.frameKey + 1, dirty: false, save: 'saved' as SaveState, saveError: null, conflict: null, localValue: null } : {}),
  });
}

export async function loadArchitecture(projectId: string, force = false) {
  if (state.projectId !== projectId) {
    // Очередь сохранений принадлежит прежнему проекту — в новый она не уедет даже «Повторить»
    queued = null;
    allowFlushInReadOnly = false;
    state = { ...INITIAL, projectId, frameKey: state.frameKey + 1 };
  } else if (!force && state.status !== 'idle' && state.status !== 'error') {
    return;
  }
  set({ status: state.content === null && state.status !== 'ready' ? 'loading' : state.status, error: null });
  // Та же метка свежести, что у сверки: ответ, обогнанный более поздним обращением к файлу
  // (своё сохранение, сверка, повторная загрузка), откатил бы холст к прежней версии
  const seq = ++epoch;
  try {
    const dto = await api.projects.architectureModel(projectId);
    if (state.projectId !== projectId || epoch !== seq) return;
    applyServer(dto, true);
  } catch (e) {
    if (state.projectId !== projectId || epoch !== seq) return;
    set({ status: 'error', error: errorText(e) });
  }
}

// Сверка с сервером (опрос документа): чужая правка → тихая перезагрузка или конфликт
export async function checkRemote(projectId: string) {
  if (state.projectId !== projectId || inflight || state.conflict) return;
  // Более позднее обращение к файлу — своё сохранение, загрузка или следующая сверка
  // (push на серию правок, focus + visibilitychange разом) — делает ответ этой устаревшим:
  // он мог прочитать файл раньше и откатил бы холст к прежней версии
  const seq = ++epoch;
  try {
    const dto = await api.projects.architectureModel(projectId);
    // Пока идёт загрузка, совпадение версий не повод молчать: её ответ эта сверка обогнала,
    // и он уже не применится — без нас статус так и остался бы «загрузка»
    if (state.projectId !== projectId || inflight || epoch !== seq || state.conflict
      || (dto.version === state.version && state.status !== 'loading')) return;
    if (state.dirty) set({ conflict: { ...dto }, save: 'failed', saveError: 'Модель изменили, пока вы её правили' });
    else applyServer(dto, true);
  } catch (e) {
    // Опрос — не повод ругаться: следующий тик попробует снова. Но если эта сверка обогнала
    // загрузку, ответ загрузки уже отброшен по эпохе — без ошибки статус завис бы в «загрузке»
    if (state.projectId === projectId && epoch === seq && state.status === 'loading') {
      set({ status: 'error', error: errorText(e) });
    }
  }
}

// Push: в проекте поменялись файлы. Сверяемся, только если среди них модель (или пришёл
// сигнал полной пересинхронизации — пути тогда неизвестны)
export function onProjectFilesChanged(projectId: string, paths: string[], full?: boolean) {
  if (full || paths.some(isModelPath)) void checkRemote(projectId);
}

// Файловая система Windows регистронезависима, а watcher отдаёт путь как лежит на диске
// (`Docs\Architecture\…`) — сравниваем без учёта регистра и с приведёнными разделителями
function isModelPath(path: string): boolean {
  return path.replace(/\\/g, '/').replace(/^\.\//, '').toLowerCase() === MODEL_PATH.toLowerCase();
}

let inflight = false;
// Эпоха обращений к файлу (загрузка, сверка, сохранение) — метка свежести ответа сверки
let epoch = 0;
let queued: string | null = null;
// Значение, которое сейчас в полёте (PUT ещё не ответил)
let sending: string | null = null;
// Вход в «Только просмотр» с правкой в дебаунсе моста: её flush — последнее, что
// сделано ДО просмотра, и она сохраняется, хотя приедет уже в режиме просмотра
let allowFlushInReadOnly = false;

// Сообщение от фрейма, который уже не текущий: сменился проект или фрейм пересоздан
// («Перезагрузить», выход из просмотра, чужая правка). Такая правка — хвост pagehide
// старого холста, писать её некуда: она затёрла бы чужой файл или взятую версию
function isStale(from: FrameStamp): boolean {
  return from.projectId !== state.projectId || from.frameKey !== state.frameKey;
}

// Мост: редактор записал модель, сохранение ждёт дебаунса
export function onFrameDirty(from: FrameStamp) {
  // Правка в просмотре в файл не пойдёт — своей она не считается: иначе чужая правка
  // на мобиле (просмотр всегда) дала бы плашку конфликта вместо тихой перезагрузки
  if (isStale(from) || state.readOnly) return;
  if (!state.dirty) set({ dirty: true });
}

// Мост: persist-обёртка после дебаунса
export function onFrameSave(value: string, from: FrameStamp) {
  if (isStale(from)) return;
  set({ localValue: value });
  if (state.conflict) { set({ dirty: true }); return; }
  // Сначала сверка с файлом — и в просмотре тоже: гидратация стора редактора
  // перезаписывает модель без изменений, и это не правка (иначе на мобиле, где
  // просмотр всегда, любая чужая правка давала бы плашку конфликта)
  if (canonical(value) !== null && canonical(value) === canonical(state.content)) {
    if (!inflight) set({ dirty: false, save: 'saved', saveError: null });
    return;
  }
  if (state.readOnly && !allowFlushInReadOnly) return;
  allowFlushInReadOnly = false;
  queued = value;
  void pump();
}

async function pump() {
  if (inflight || queued === null || !state.projectId) return;
  const projectId = state.projectId;
  const value = queued;
  queued = null;
  inflight = true;
  epoch++;
  sending = value;
  set({ save: 'saving', dirty: true });
  // В файл — с отступами: так модель читается в git diff построчно
  let pretty = value;
  try { pretty = JSON.stringify(JSON.parse(value), null, 2) + '\n'; } catch { /* отдадим как есть — сервер скажет 400 */ }
  try {
    const res = await api.projects.architectureSaveModel(projectId, pretty, state.version);
    if (state.projectId !== projectId) return;
    set({
      content: pretty, version: res.version, updatedAt: res.updatedAt, updatedBy: res.updatedBy,
      status: 'ready', blank: false, save: 'saved', saveError: null, dirty: queued !== null,
    });
  } catch (e) {
    if (state.projectId !== projectId) return;
    const err = e as Error & { status?: number; body?: { current?: ArchConflict; message?: string } };
    if (err.status === 409 && err.body?.current) {
      queued = null;
      set({ conflict: { ...err.body.current }, save: 'failed', saveError: 'Модель изменили, пока вы её правили', dirty: true });
    } else {
      // Сеть/сервер: правку держим, «Повторить» отправит её снова
      queued = queued ?? value;
      set({ save: 'failed', saveError: err.body?.message ?? errorText(e), dirty: true });
      return;
    }
  } finally {
    inflight = false;
    sending = null;
    // Пока PUT прежнего проекта был в полёте, в новый пришло сохранение: его pump вышел
    // по inflight, и без этого пинка оно висело бы в очереди при статусе «Сохранено»
    if (state.projectId !== projectId && queued !== null) void pump();
  }
  if (queued !== null && !state.conflict) void pump();
}

export function retrySave() {
  const value = queued ?? state.localValue;
  if (value === null || state.conflict) return;
  queued = value;
  void pump();
}

// «Перезагрузить» в плашке конфликта: взять версию с сервера, свои правки отбросить
export function takeServerVersion() {
  const c = state.conflict;
  queued = null;
  if (c) {
    set({ status: c.content === null ? 'missing' : 'ready', content: c.content, version: c.version, updatedAt: c.updatedAt, updatedBy: c.updatedBy });
  }
  set({ conflict: null, dirty: false, save: 'saved', saveError: null, localValue: null, frameKey: state.frameKey + 1 });
  if (!c && state.projectId) void loadArchitecture(state.projectId, true);
}

// «Скачать мои правки»: просим мост отдать отложенное и выгружаем последнюю модель файлом
export function downloadLocal(fileName: string) {
  frameWindow?.postMessage({ source: 'ccs-host', v: 1, type: 'flush' }, '*');
  setTimeout(() => {
    const value = state.localValue ?? state.content;
    if (!value) return;
    const url = URL.createObjectURL(new Blob([value], { type: 'application/json' }));
    const a = document.createElement('a');
    a.href = url;
    a.download = fileName;
    a.click();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
  }, 300);
}

export function setReadOnly(readOnly: boolean) {
  if (readOnly === state.readOnly) return;
  if (readOnly) {
    // Правка, ждущая дебаунса моста, сделана ещё до просмотра — просим отдать её сейчас
    if (state.dirty && !state.conflict && frameWindow) {
      allowFlushInReadOnly = true;
      frameWindow.postMessage({ source: 'ccs-host', v: 1, type: 'flush' }, '*');
      // Ответ на flush приходит сразу; не пришёл (в дебаунсе было пусто) — разрешение
      // не должно дожить до правки, сделанной уже в просмотре
      setTimeout(() => { allowFlushInReadOnly = false; }, 1000);
    }
    set({ readOnly });
    return;
  }
  allowFlushInReadOnly = false;
  set({ readOnly });
  if (state.conflict) return;
  // Выход из просмотра: правки, сделанные в просмотре, в файл не идут никогда. Сами они
  // остались на холсте, а стор сохраняет модель целиком — первая же новая правка унесла
  // бы их с собой. Поэтому холст с расхождением пересоздаём из файла
  // Правка, которая прямо сейчас уходит в файл (flush на входе в просмотр), — не расхождение
  const local = canonical(state.localValue);
  const diverged = state.localValue !== null && local !== canonical(state.content)
    && local !== canonical(sending) && local !== canonical(queued);
  set({
    dirty: false, save: 'saved', saveError: null,
    ...(diverged ? { localValue: null, frameKey: state.frameKey + 1 } : {}),
  });
}

export function startBlank() {
  set({ blank: true, frameKey: state.frameKey + 1 });
}

export function requestFocus(id: string) {
  set({ focus: { id, tick: (state.focus?.tick ?? 0) + 1 } });
}

export async function generateArchitecture(projectId: string) {
  if (state.generating) return;
  set({ generating: true, generateError: null });
  try {
    await api.projects.architectureGenerate(projectId);
    if (state.projectId !== projectId) return;
    const dto = await api.projects.architectureModel(projectId);
    if (state.projectId !== projectId) return;
    // Свои несохранённые правки поверх свежей сборки не затираем — конфликт
    if (state.dirty) set({ conflict: { ...dto }, save: 'failed', saveError: 'Модель пересобрана из кода, пока вы её правили' });
    else applyServer(dto, true);
    set({ generating: false, blank: false });
  } catch (e) {
    if (state.projectId !== projectId) return;
    const err = e as Error & { status?: number; body?: { code?: string; message?: string } };
    set({
      generating: false,
      generateError: err.body?.message ?? errorText(e),
      corrupt: err.body?.code === 'model_corrupt' || state.corrupt,
    });
  }
}
