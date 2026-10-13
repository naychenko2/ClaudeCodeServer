// «Подставить персонажа» (этап 2 · шаг 10b): чистая логика экрана — масштаб рамок
// bbox→доли, видимость входа, состояния (плашка/плашки/canSubmit/подпись). Разделена от
// React, чтобы тестировать без DOM. React-обвязка — FaceOverlay/FaceSwapBar/FaceSwapPanel.

import type { DetectedFace, FaceTarget, FacesResponse } from './api';
import type { FaceSwapPhase } from './thread/faceSwap';

// ── Тексты (Софья, дословно из макета character-face-swap-v1.html) ─────────────
export const FS_TEXT = {
  action: 'Подставить персонажа',
  search: 'Ищу лица на картинке…',
  queue: 'Видеокарта занята другой задачей — найду лица, как только освободится',
  none: 'Лиц не нашлось. Закрасьте лицо кистью — и выберите персонажа',
  brush: 'Закрасить кистью',
  one: 'Нашёлся один человек. Выберите, чьё лицо подставить',
  many: 'Нажмите на лицо, на которое нужно подставить персонажа',
  pickTitle: 'Чьё лицо подставить?',
  pickHint: 'Персонаж из строки контекста сам не подставляется: он обычно уже есть на картинке',
  over8: 'Показаны 8 самых крупных лиц',
  small: 'Лицо слишком мелкое — персонаж получится непохожим. Закрасьте его кистью или увеличьте картинку',
  close: 'Лица стоят слишком близко — закрасьте нужное кистью',
  // face_out_of_frame (этап 2 · шаг 10d): тире, как у соседних (утверждено продактом)
  outOfFrame: 'Лицо вне картинки — выберите другое лицо или закрасьте нужное кистью',
  busy: 'Видеокарта занята долгой задачей. Попробуйте чуть позже',
  provider: 'Подставлять персонажа можно только локальными моделями',
  // provider_unavailable, когда локальные модели недоступны: действие не помогает (этап 2 · шаг 10d)
  localUnavailable: 'Локальные модели сейчас недоступны. Попробуйте позже',
  // feature_disabled: флаг выключили, пока экран был открыт — режим закрывается (этап 2 · шаг 10d)
  featureDisabled: 'Режим «Подставить персонажа» отключён',
  useLocal: 'Использовать локальные для этого запуска',
  changed: 'Картинка изменилась — лица найдены заново',
  go: 'Подставить',
  quote: 'бесплатно · ~1 мин',
  localOnce: 'Локальные модели · этот запуск',
} as const;

// Подпись карточки запуска/версии: «{Имя} → лицо {n}»
export const faceCaption = (name: string, n: number) => `${name} → лицо ${n}`;

// Выбор подписи на карточке запуска (этап 2 · шаг 10c): faceTarget null — подписи нет
// (карточка прежняя). Имя — имя персонажа по slug из списка проекта; персонаж не найден
// (удалён) — сам slug
export function faceTargetCaption(
  ft: { n: number; characterSlug: string } | null | undefined,
  name: string | null | undefined,
): string | null {
  if (!ft) return null;
  const n = name?.trim();
  return faceCaption(n || ft.characterSlug, ft.n);
}

// ── Масштаб рамок: bbox в пикселях картинки → доли (0..1) ─────────────────────
// bbox = [x1, y1, x2, y2]; width/height — размеры картинки из ответа. Доли — как
// координаты картинки в макете: рамка рисуется в долях контейнера, который сам
// масштабирует картинку (object-fit: contain), поэтому масштабирование = деление на
// размеры картинки, а не на отображаемый размер.
export interface FaceRect { x: number; y: number; width: number; height: number }

export function faceRect(f: { bbox: [number, number, number, number] }, w: number, h: number): FaceRect {
  const [x1, y1, x2, y2] = f.bbox;
  const width = Math.max(1, w);
  const height = Math.max(1, h);
  return { x: x1 / width, y: y1 / height, width: (x2 - x1) / width, height: (y2 - y1) / height };
}

// ── Видимость входа «Подставить персонажа» ─────────────────────────────────────
// Пункт/режим есть, когда: не личная область, флаг включён, в проекте есть персонажи
// и локальные модели доступны. Нет персонажей (11) — пункта нет вовсе.
export function faceSwapEntryVisible(opts: {
  personal: boolean;
  flag: boolean;
  hasCharacters: boolean;
  localAvailable: boolean;
}): boolean {
  return !opts.personal && opts.flag && opts.hasCharacters && opts.localAvailable;
}

// ── Состояние экрана ───────────────────────────────────────────────────────────
// phase — фаза поиска (useFaceSwap); provider — исполнитель текущего запуска;
// localAvailable — локальные модели отвечают; useLocalOnce — человек переключил
// исполнителя только на этот запуск (состояние 9б). tappedSmall — мелкое лицо,
// которое тапнули (состояние 6). over8 — сервер отдал 8 из N (состояние 5).
// facesTooClose/busy — отказы запуска (состояния 7/8). facesChanged — 409 (состояние 10).
export interface FaceSwapViewInput {
  phase: FaceSwapPhase;
  faces: readonly DetectedFace[];
  selectedFace: number | null;
  selectedCharacter: string | null;
  error: string | null;
  provider: string | null;
  localAvailable: boolean;
  useLocalOnce: boolean;
  tappedSmall: number | null;
  over8: boolean;
  facesTooClose: boolean;
  faceOutOfFrame: boolean;
  busy: boolean;
  providerUnavailable: boolean;
  facesChanged: boolean;
  // «Лиц нет» (состояние 2): «Подставить» активна, когда есть закрашенная область
  hasMask: boolean;
}

export type NoticeTone = 'info' | 'warning' | 'danger';
export interface NoticeSpec { tone: NoticeTone; icon: 'info' | 'alert' | 'clock' | 'cpu' | 'redo'; text: string; action?: { label: string; kind: 'ghostAccent' | 'secondary' } }

// Иконка плашки режима: search (спиннер) / clock (очередь) / scan (обычный шаг)
export type BarIcon = 'search' | 'clock' | 'scan';

export interface FaceSwapView {
  bar: string;
  barIcon: BarIcon;
  notices: NoticeSpec[];
  canSubmit: boolean;
  // Список персонажей доступен, когда есть к чему подставлять: лицо выбрано (или
  // закрашено кистью) — до выбора лица список виден, но недоступен
  listEnabled: boolean;
  // Бейдж «Локальные модели · этот запуск» (состояние 9б)
  localOnce: boolean;
}

// ── Реакция на отказ запуска (этап 2 · шаг 10d) ────────────────────────────────
// Код ошибки запуска → вид отказа. Чистая функция: без React, без DOM.
// Тело запуска несёт code (offline.ts: body = JSON-ответ сервера); проверка по тексту
// сообщения убрана — только по коду. provider_unavailable → 9/нейтрально решает
// faceSwapView по localAvailable (действие «использовать локальные» не помогает, когда
// локальные недоступны).
export type LaunchFailure =
  | { kind: 'facesTooClose' }        // faces_too_close → плашка 7 (danger, «близко»)
  | { kind: 'faceOutOfFrame' }       // face_out_of_frame → плашка (новый текст)
  | { kind: 'busy' }                 // busy → плашка 8 (danger, clock, «Видеокарта занята…»)
  | { kind: 'providerUnavailable' }  // provider_unavailable → плашка 9 / нейтрально
  | { kind: 'facesChanged' }         // faces_changed → обновление рамок + сброс выбора
  | { kind: 'featureDisabled' }      // feature_disabled → закрытие режима
  | { kind: 'generic'; message: string }; // неизвестный код → общая ошибка

export function launchFailure(code: string | null, message: string | null): LaunchFailure {
  switch (code) {
    case 'faces_too_close': return { kind: 'facesTooClose' };
    case 'face_out_of_frame': return { kind: 'faceOutOfFrame' };
    case 'busy': return { kind: 'busy' };
    case 'provider_unavailable': return { kind: 'providerUnavailable' };
    case 'faces_changed': return { kind: 'facesChanged' };
    case 'feature_disabled': return { kind: 'featureDisabled' };
    default: return { kind: 'generic', message: message ?? '' };
  }
}

// Текст причины отказа ПОИСКА лиц (не запуска) — для плашки шага 16. busy → очередь
// видеокарты, provider_unavailable → локальные недоступны, прочие → общий. Чистая функция:
// без React, без DOM.
export function searchFailureText(code: string): string {
  switch (code) {
    case 'busy': return FS_TEXT.queue;
    case 'provider_unavailable': return FS_TEXT.localUnavailable;
    case 'not_found': return FS_TEXT.provider;
    case 'feature_disabled': return FS_TEXT.featureDisabled;
    default: return 'Не удалось найти лица. Попробуйте позже';
  }
}

// ── Локальное состояние экрана «Подставить персонажа» (EditorModal) ──────────
// tappedSmall — мелкое лицо, которое тапнули; флаги отказов запуска; useLocalOnce —
// «локальные на этот запуск»; submitting — идёт запуск.
export interface FaceUi {
  tappedSmall: number | null;
  tooClose: boolean;
  faceOutOfFrame: boolean;
  busy: boolean;
  providerUnavailable: boolean;
  facesChanged: boolean;
  useLocalOnce: boolean;
  submitting: boolean;
}

// Сброс ВСЕХ флагов отказов (в начале запуска, вместе с submitting: true):
// иначе старый отказ (например face_out_of_frame) висит поверх нового (busy) —
// две противоречащие плашки. Остальные поля (tappedSmall/useLocalOnce) не трогаем.
export function clearFailures(ui: FaceUi): FaceUi {
  return { ...ui, tooClose: false, faceOutOfFrame: false, busy: false, providerUnavailable: false, facesChanged: false };
}

// Смена выбранного лица: «вне картинки» и «близко» гасятся (человек выбрал другое
// лицо); остальные флаги (busy/providerUnavailable/facesChanged) — нет.
export function clearFaceChange(ui: FaceUi): FaceUi {
  return { ...ui, faceOutOfFrame: false, tooClose: false };
}

// Чистое вычисление состояния экрана. canSubmit — по матрице макета: лицо и персонаж
// выбраны, исполнитель — локальные (или переключены на этот запуск).
export function faceSwapView(i: FaceSwapViewInput): FaceSwapView {
  const { phase, faces, selectedFace, selectedCharacter, error, provider, localAvailable, useLocalOnce,
    tappedSmall, over8, facesTooClose, faceOutOfFrame, busy, providerUnavailable, facesChanged, hasMask } = i;

  // Исполнитель запуска: локальные модели, либо переключены на этот запуск
  const local = useLocalOnce || provider === 'local';
  const localOk = local && localAvailable;

  // ── Плашка режима (шаг) ──
  let bar: string;
  let barIcon: BarIcon;
  if (phase === 'loading') {
    // Очередь видеокарты (1б) — часы, иначе спиннер (1)
    barIcon = error === 'busy' ? 'clock' : 'search';
    bar = error === 'busy' ? FS_TEXT.queue : FS_TEXT.search;
  } else if (phase === 'error') {
    barIcon = 'scan';
    bar = FS_TEXT.action;
  } else if (faces.length === 1) {
    barIcon = 'scan';
    bar = FS_TEXT.one;
  } else {
    barIcon = 'scan';
    bar = FS_TEXT.many;
  }

  // ── Плашки (notices) ──
  const notices: NoticeSpec[] = [];
  if (phase === 'ready') {
    if (faces.length === 0) {
      notices.push({ tone: 'info', icon: 'info', text: FS_TEXT.none, action: { label: FS_TEXT.brush, kind: 'ghostAccent' } });
    } else if (over8) {
      notices.push({ tone: 'info', icon: 'info', text: FS_TEXT.over8 });
    }
    if (tappedSmall != null) {
      notices.push({ tone: 'warning', icon: 'alert', text: FS_TEXT.small, action: { label: FS_TEXT.brush, kind: 'ghostAccent' } });
    }
    if (facesTooClose) {
      notices.push({ tone: 'danger', icon: 'alert', text: FS_TEXT.close, action: { label: FS_TEXT.brush, kind: 'ghostAccent' } });
    }
    if (faceOutOfFrame) {
      notices.push({ tone: 'danger', icon: 'alert', text: FS_TEXT.outOfFrame, action: { label: FS_TEXT.brush, kind: 'ghostAccent' } });
    }
    if (busy) {
      notices.push({ tone: 'danger', icon: 'clock', text: FS_TEXT.busy });
    }
    if (facesChanged) {
      notices.push({ tone: 'info', icon: 'redo', text: FS_TEXT.changed });
    }
    // provider_unavailable: локальные модели реально недоступны → нейтральная плашка
    // (действие «использовать локальные» не помогает). Независимо от localOk: кнопка
    // «Подставить» активна только при localOk, поэтому отказ приходит, когда клиент
    // считал локальные доступными, — плашка обязана быть, иначе отказ немой.
    // Провайдер не локальные (9): warning + явное действие; тихой подмены нет.
    if (providerUnavailable) {
      notices.push({ tone: 'info', icon: 'cpu', text: FS_TEXT.localUnavailable });
    } else if (!localOk) {
      notices.push({ tone: 'warning', icon: 'cpu', text: FS_TEXT.provider, action: { label: FS_TEXT.useLocal, kind: 'secondary' } });
    }
  }

  // ── canSubmit: по матрице ──
  // Лицо выбрано (или закрашено кистью, когда лиц нет) + персонаж + локальные модели
  const faceReady = selectedFace != null || (faces.length === 0 && hasMask);
  const canSubmit = phase === 'ready' && faceReady && selectedCharacter != null && localOk
    // Отказ «близко» (7): неактивна, пока выбор не изменится
    && !facesTooClose;

  // ── Список персонажей: доступен, когда есть к чему подставлять ──
  const listEnabled = phase === 'ready' && (selectedFace != null || faces.length === 0);

  return { bar, barIcon, notices, canSubmit, listEnabled, localOnce: useLocalOnce && localOk };
}

// ── Состояние рамки одного лица (для FaceOverlay) ─────────────────────────────
// normal / selected / dim (остальные при выбранной) / small (пунктир, не выбрать) /
// smallTapped (тапнули мелкое — warning) / error (близко — danger)
export type FaceVisual = 'normal' | 'selected' | 'dim' | 'small' | 'smallTapped' | 'error';

export function faceVisual(f: DetectedFace, i: {
  selectedFace: number | null;
  tappedSmall: number | null;
  errorFaces: readonly number[];
}): FaceVisual {
  if (f.small) return i.tappedSmall === f.n ? 'smallTapped' : 'small';
  if (i.errorFaces.includes(f.n)) return 'error';
  if (i.selectedFace === f.n) return 'selected';
  if (i.selectedFace != null) return 'dim';
  return 'normal';
}

// ── Назначения «лицо → персонаж» (ADR-028, этап 3) ─────────────────────────────
// Чистая модель без React: assignments — максимум MAX_FACE_PASSES (лимит MaxFacePasses);
// active — лицо, на котором сейчас выбираем персонажа; limit — лицо, вызвавшее плашку
// лимита (тап по свободной рамке при полном наборе). Все операции — чистые функции:
// возвращают новое состояние, исходный объект не мутируют.
export const MAX_FACE_PASSES = 2;

export interface FaceAssignments {
  assignments: Map<number, string>; // faceN → characterSlug
  active: number | null;
  limit: number | null;
}

export const emptyAssignments: FaceAssignments = { assignments: new Map(), active: null, limit: null };

// Есть ли назначения — для будущего подтверждения кисти («Кисть работает с одним лицом —
// назначения сбросятся», шаг 16)
export const hasAssignments = (a: FaceAssignments): boolean => a.assignments.size > 0;

// Персонаж, уже стоящий на ДРУГОМ лице, недоступен (занят): один персонаж — одно лицо.
// Снятие назначения с того лица освобождает его.
export function isCharacterTaken(a: FaceAssignments, slug: string, forFace: number): boolean {
  for (const [face, s] of a.assignments) if (face !== forFace && s === slug) return true;
  return false;
}

// Назначить персонажа лицу: создаёт или ЗАМЕНЯЕТ назначение (не добавляет второе);
// active сбрасывается (остаёмся на месте, Р1 а) и плашка лимита снимается (любое изменение
// назначений её гасит).
export function assignCharacter(a: FaceAssignments, face: number, slug: string): FaceAssignments {
  const assignments = new Map(a.assignments);
  assignments.set(face, slug);
  return { ...a, assignments, active: null, limit: null };
}

// Снять назначение с лица: освобождает персонажа; если это было активное лицо — active
// сбрасывается; плашка лимита снимается. Идемпотентно: нет назначения — состояние не меняется.
export function removeAssignment(a: FaceAssignments, face: number): FaceAssignments {
  if (!a.assignments.has(face)) return a;
  const assignments = new Map(a.assignments);
  assignments.delete(face);
  return { ...a, assignments, active: a.active === face ? null : a.active, limit: null };
}

// Тап по лицу. faces — полный список (для проверки small). Правила:
//  - мелкое → не трогать (UI ведёт tappedSmall, как в этапе 2);
//  - тап по назначенной: повторный тап по активной → active = null, иначе → active = n (переназначение);
//  - 2 назначения и тап по свободной → limit = n (active не меняется) — плашка лимита;
//  - иначе (свободная при <2) → active = n.
// Любое изменение назначений / тап по назначенной снимает плашку лимита.
export function tapFace(a: FaceAssignments, face: number, faces: readonly DetectedFace[]): FaceAssignments {
  const f = faces.find(x => x.n === face);
  if (f?.small) return a;
  if (a.assignments.has(face)) {
    if (a.active === face) return { ...a, active: null, limit: null };
    return { ...a, active: face, limit: null };
  }
  if (a.assignments.size >= MAX_FACE_PASSES) return { ...a, limit: face };
  return { ...a, active: face, limit: null };
}

// Цель подстановки (ADR-028): {versionId, facesKey, faces:[{face, characterSlug}]}
// в порядке возрастания номера лица; без назначений → null (запуска нет)
export function faceTargetFromAssignments(a: FaceAssignments, versionId: string, facesKey: string): FaceTarget | null {
  if (a.assignments.size === 0) return null;
  const faces = [...a.assignments.entries()]
    .sort((x, y) => x[0] - y[0])
    .map(([face, characterSlug]) => ({ face, characterSlug }));
  return { versionId, facesKey, faces };
}

// Тело 409 faces_changed при ЗАПУСКЕ — свежий ImageThreadFacesResponse (facesKey + faces),
// НЕ {code, error} (в отличие от прочих отказов запуска). Разбор в FacesResponse, чтобы
// редьюсер обновил facesKey/список и сбросил назначения; null — тело не похоже на список лиц.
export function parseFacesChangedBody(body: unknown): FacesResponse | null {
  const r = body as FacesResponse | null | undefined;
  if (!r || typeof r !== 'object') return null;
  if (typeof r.facesKey !== 'string' || !Array.isArray(r.faces)) return null;
  return r;
}
