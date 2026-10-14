// Темы чатов проекта (docs/features/chat-subjects.md): правила имени
// на клиенте, сборка правок реестра и общий стор состояния разбора («Разложить»).
import { useEffect, useSyncExternalStore } from 'react';
import type { ProjectSubject, ProjectSubjectEdit, Session, SubjectRunState } from '../types';
import { api } from './api';
import { showToast } from './toast';
import { chatCountWord } from './chatFilters';

// Зеркало ChatSubjectRules бэка: имя 2–40 символов, тем в реестре не больше 50 (потолок
// ручного PUT; 12 — потолок тем, которые заводит модель, человеку он не мешает)
export const SUBJECT_NAME_MIN = 2;
export const SUBJECT_NAME_MAX = 40;
export const SUBJECTS_MAX_HUMAN = 50;

export const SUBJECT_AI_MARK = '✦';
export const SUBJECT_AI_TITLE = 'Тему предложил AI. Переименуйте или переставьте её — и она станет вашей';

// Ошибка имени для подсказки у поля; null — имя годится. Остальное (буквы, ссылки)
// проверит санитайзер бэка — его текст придёт в 400
export function subjectNameError(raw: string, registry: ProjectSubject[], selfId?: string): string | null {
  const name = raw.trim();
  if (name.length < SUBJECT_NAME_MIN || name.length > SUBJECT_NAME_MAX) {
    return `От ${SUBJECT_NAME_MIN} до ${SUBJECT_NAME_MAX} символов`;
  }
  const key = name.toLowerCase();
  if (registry.some(s => s.id !== selfId && s.name.trim().toLowerCase() === key)) return 'Такая тема уже есть';
  return null;
}

// Реестр в порядке order
export const sortedSubjects = (registry: ProjectSubject[]) => [...registry].sort((a, b) => a.order - b.order);

// Правки PUT с ЯВНЫМ order у каждой темы: без него бэк берёт позицию в запросе, и у
// темы модели, чей order не совпал с позицией (после удаления соседа), «изменился
// порядок» — она молча стала бы human
const toEdit = (s: ProjectSubject): ProjectSubjectEdit => ({ id: s.id, name: s.name, order: s.order, color: s.color ?? null });

export function renameEdits(registry: ProjectSubject[], id: string, name: string): ProjectSubjectEdit[] {
  return registry.map(s => (s.id === id ? { ...toEdit(s), name: name.trim() } : toEdit(s)));
}

// Перестановка с соседом: меняются местами ровно два order — третьи темы не трогаются
export function moveEdits(registry: ProjectSubject[], id: string, dir: -1 | 1): ProjectSubjectEdit[] | null {
  const list = sortedSubjects(registry);
  const i = list.findIndex(s => s.id === id);
  const j = i + dir;
  if (i < 0 || j < 0 || j >= list.length) return null;
  const a = list[i], b = list[j];
  // Совпавшие order (данные из прошлого) разводим, иначе перестановка была бы невидимой
  const [oa, ob] = a.order === b.order ? [b.order + (dir > 0 ? 1 : -1), a.order] : [b.order, a.order];
  return list.map(s => s.id === a.id ? { ...toEdit(s), order: oa } : s.id === b.id ? { ...toEdit(s), order: ob } : toEdit(s));
}

export function removeEdits(registry: ProjectSubject[], id: string): ProjectSubjectEdit[] {
  return registry.filter(s => s.id !== id).map(toEdit);
}

export function addEdits(registry: ProjectSubject[], name: string): ProjectSubjectEdit[] {
  const next = registry.reduce((m, s) => Math.max(m, s.order), -1) + 1;
  return [...registry.map(toEdit), { name: name.trim(), order: next }];
}

// === Свежесть реестра в списке чатов ===
// Опрос сессий принёс subjectId, которого нет в локальном реестре: тему завела модель
// (фон или проход), а реестр у списка старый. Перечитываем, только если среди
// неизвестных есть id, по которому ещё не перечитывали: настоящий висячий id (тему
// удалили) иначе гонял бы перечитывание по кругу на каждом опросе. tried — уже
// опробованные id, функция дописывает в него новые
export function needsRegistryReload(sessions: { subjectId?: string | null }[], registry: ProjectSubject[], tried: Set<string>): boolean {
  const known = new Set(registry.map(s => s.id));
  let fresh = false;
  for (const c of sessions) {
    if (!c.subjectId || known.has(c.subjectId) || tried.has(c.subjectId)) continue;
    tried.add(c.subjectId);
    fresh = true;
  }
  return fresh;
}

// Перенос чата, который опрос не должен перетереть старой темой: PUT ещё в полёте, либо
// ответил, но опросы, ушедшие до ответа, ещё не вернулись. settledAt — номер последнего
// ушедшего опроса на момент ответа PUT; undefined — PUT в полёте
export interface PendingSubjectMove {
  patch: Pick<Session, 'subjectId' | 'subjectSource'>;
  settledAt?: number;
}

export function overlayPendingMoves<T extends Session>(list: T[], pending: ReadonlyMap<string, PendingSubjectMove>): T[] {
  if (pending.size === 0) return list;
  return list.map(c => {
    const p = pending.get(c.id);
    return p ? { ...c, ...p.patch } : c;
  });
}

// Ответ опроса pollNo свежее последнего применённого (applied)? Да — запоминает его номер.
// Нет — опрос ушёл раньше уже применённого, но вернулся позже (медленная сеть,
// переподключение): его список старее экрана, и он откатил бы перенос назад
export function takeFreshPoll(applied: { current: number }, pollNo: number): boolean {
  if (pollNo < applied.current) return false;
  applied.current = pollNo;
  return true;
}

// Ответ опроса pollNo: снимает переносы, чей PUT ответил ДО ухода этого опроса, — его
// ответ уже несёт тему с сервера. Зовётся вне апдейтера setState (он чистый)
export function prunePendingMoves(pending: Map<string, PendingSubjectMove>, pollNo: number): void {
  for (const [id, p] of pending) {
    if (p.settledAt !== undefined && pollNo > p.settledAt) pending.delete(id);
  }
}

// === Состояние разбора («Разложить сейчас») ===
// Один стор на проект для обоих входов (заголовок «Без темы» и настройки проекта): опрос
// GET …/subjects/run раз в 2 с идёт ТОЛЬКО пока проход running и смонтирован хотя бы один
// вход; тост о конце прохода — один на переход, а не по тосту на каждый вход.
interface RunEntry {
  state: SubjectRunState | null;
  starting: boolean;
  watchers: number;
  timer: ReturnType<typeof setTimeout> | null;
  snapshot: SubjectRunView;
  // Поколение запросов: растёт на каждом запуске. Ответ GET, ушедший раньше последнего
  // POST, к стору не допускается — иначе устаревший idle перетёр бы running запуска
  gen: number;
  // Проход, запущенный нами: finishedAt прошлого конца (null — концов ещё не было).
  // Пока он задан, idle без НОВОГО finishedAt — снимок до запуска, а не конец прохода
  // (бэк ставит finishedAt на каждом конце). undefined — запуска не было
  runBaseline: string | null | undefined;
  // Номер последнего ушедшего GET и номер последнего GET, ушедшего до ответа POST. Сверка
  // с runBaseline — только для них: GET после ответа POST видит сервер уже с нашим
  // проходом, и его idle — правда. Иначе после рестарта бэка (проход умер, finishedAt
  // пуст) idle навсегда считался бы устаревшим и полоса «Разбираю чаты» висела бы
  getNo: number;
  staleUpTo: number;
}

export interface SubjectRunView {
  state: SubjectRunState | null;
  starting: boolean;
  running: boolean;
}

const POLL_MS = 2000;
const entries = new Map<string, RunEntry>();
const listeners = new Set<() => void>();
const emit = () => listeners.forEach(l => l());
// Подписчики конца прохода (running → idle): модель завела и раздала темы — реестр и
// сводки у потребителей устарели, их надо перечитать
const finishListeners = new Set<(projectId: string) => void>();

export function onSubjectRunFinished(cb: (projectId: string) => void): () => void {
  finishListeners.add(cb);
  return () => { finishListeners.delete(cb); };
}

function entry(projectId: string): RunEntry {
  let e = entries.get(projectId);
  if (!e) {
    e = {
      state: null, starting: false, watchers: 0, timer: null,
      snapshot: { state: null, starting: false, running: false },
      gen: 0, runBaseline: undefined, getNo: 0, staleUpTo: 0,
    };
    entries.set(projectId, e);
  }
  return e;
}

function update(projectId: string, patch: Partial<Pick<RunEntry, 'state' | 'starting'>>) {
  const e = entry(projectId);
  const wasRunning = e.state?.state === 'running';
  Object.assign(e, patch);
  const running = e.state?.state === 'running';
  e.snapshot = { state: e.state, starting: e.starting, running };
  if (wasRunning && !running && e.state) {
    e.runBaseline = undefined;
    if (e.state.lastError) showToast('Разбор не удался', e.state.lastError, 'info');
    // Бэк ставит finishedAt на каждом конце прохода; idle без него — состояние по умолчанию
    // после рестарта сервера: проход умер посреди дела, «разложено 0» было бы неправдой
    else if (!e.state.finishedAt) showToast('Разбор прерван', 'Сервер перезапустился посреди разбора — запустите его ещё раз', 'info');
    else showToast('Темы разложены', `Разобрано ${e.state.done} ${chatCountWord(e.state.done)}`, 'claude');
    finishListeners.forEach(l => l(projectId));
  }
  schedule(projectId);
  emit();
}

// idle, который не может быть концом нашего прохода: finishedAt не новее прошлого конца
// (сравниваются два серверных времени — расхождение часов клиента не мешает)
function isStaleIdle(e: RunEntry, s: SubjectRunState): boolean {
  if (s.state !== 'idle' || e.runBaseline === undefined) return false;
  if (!s.finishedAt) return true;
  return e.runBaseline !== null && Date.parse(s.finishedAt) <= Date.parse(e.runBaseline);
}

// GET состояния с отсевом устаревших ответов (см. RunEntry.gen/runBaseline)
function fetchRunState(projectId: string) {
  const e = entry(projectId);
  const gen = e.gen;
  const no = ++e.getNo;
  return api.projects.subjects.runState(projectId).then(s => {
    if (gen !== e.gen) return;
    if (no <= e.staleUpTo && isStaleIdle(e, s)) { schedule(projectId); return; }
    update(projectId, { state: s });
  });
}

function schedule(projectId: string) {
  const e = entry(projectId);
  if (e.timer) { clearTimeout(e.timer); e.timer = null; }
  if (e.watchers <= 0 || e.state?.state !== 'running') return;
  e.timer = setTimeout(() => {
    e.timer = null;
    fetchRunState(projectId).catch(() => schedule(projectId));
  }, POLL_MS);
}

// Запуск прохода. Повторный клик при идущем проходе бэк отвечает 202 running без
// второго прохода — тихо переходим в running, без тоста
export async function startSubjectRun(projectId: string, resetAi = false): Promise<void> {
  const e = entry(projectId);
  // GET, ушедшие до этого POST, больше не правда — их ответы отбросятся
  e.gen++;
  const baseline = e.state?.finishedAt ?? null;
  update(projectId, { starting: true });
  try {
    const s = await api.projects.subjects.run(projectId, resetAi);
    e.staleUpTo = e.getNo;
    if (s.state === 'running') e.runBaseline = baseline;
    // busy — замок занят разбором ДРУГОГО проекта: у этого ничего не идёт и не запустилось,
    // состояние проекта не трогаем, только говорим, почему кнопка не сработала
    if (s.state === 'busy') {
      update(projectId, { starting: false });
      showToast('Разбор не запущен', s.lastError ?? 'Идёт разбор в другом проекте', 'info');
      return;
    }
    update(projectId, { state: s, starting: false });
  } catch (err) {
    update(projectId, { starting: false });
    showToast('Разбор не удался', err instanceof Error ? err.message : 'Сервер недоступен', 'info');
  }
}

export function useSubjectRun(projectId: string, enabled: boolean): SubjectRunView {
  useEffect(() => {
    if (!enabled) return;
    const e = entry(projectId);
    e.watchers++;
    // Первый вход узнаёт, не идёт ли проход уже (запущен фоном, в другой вкладке)
    if (e.watchers === 1) fetchRunState(projectId).catch(() => {});
    else schedule(projectId);
    return () => {
      e.watchers--;
      schedule(projectId);
    };
  }, [projectId, enabled]);
  return useSyncExternalStore(
    cb => { listeners.add(cb); return () => { listeners.delete(cb); }; },
    () => entry(projectId).snapshot,
  );
}
