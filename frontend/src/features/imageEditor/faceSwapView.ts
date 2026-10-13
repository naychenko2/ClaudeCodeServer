// «Подставить персонажа» (этап 2 · шаг 10b): чистая логика экрана — масштаб рамок
// bbox→доли, видимость входа, состояния (плашка/плашки/canSubmit/подпись). Разделена от
// React, чтобы тестировать без DOM. React-обвязка — FaceOverlay/FaceSwapBar/FaceSwapPanel.

import type { DetectedFace, FaceTarget, FacesResponse } from './api';
import type { FaceSwapPhase } from './thread/faceSwap';

// ── Тексты (Софья, дословно из макетов character-face-swap-v1.html и v2-notes.md) ─
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
  localOnce: 'Локальные модели · этот запуск',
  // ── Этап 3 (ADR-028): назначения, лимит, отказы по номеру лица ──
  // Заголовок выбора персонажа
  pickFor: (n: number) => `Кто будет на лице ${n}?`,
  // Плашка режима: лицо активно, не назначено
  barPick: (n: number) => `Выберите персонажа для лица ${n}`,
  // Плашка режима: лицо активно, уже назначено (переназначение)
  barReassign: (n: number) => `Выберите другого персонажа для лица ${n} — или снимите назначение`,
  // Плашка режима: одно назначение
  barOne: 'Можно назначить ещё одно лицо — или подставить сразу',
  // Плашка режима: два назначения
  barTwo: 'Два лица назначены — можно подставлять',
  // Заголовок секции назначений
  asgTitle: 'Назначения',
  // Счётчик назначений
  asgMeta: (k: number) => `${k} из 2`,
  // Пустое состояние списка назначений
  asgEmpty: 'Пока никого не назначили. Нажмите на лицо на картинке',
  // Слот после первого назначения
  asgSlot: 'Нажмите на другое лицо, чтобы назначить второго персонажа',
  // Строка активного неназначенного лица
  asgPicking: 'выберите персонажа',
  // Бейдж «Лицо n»
  face: (n: number) => `Лицо ${n}`,
  // aria-label кнопки ✕
  removeAria: (n: number) => `Снять назначение с лица ${n}`,
  // Занятый персонаж: строка в списке
  takenOn: (n: number) => `Уже на лице ${n}`,
  // Занятый персонаж: плитка мобилы
  busyTile: (n: number) => `на лице ${n}`,
  // Плашка лимита
  limit: 'Не больше 2 лиц за раз. Снимите одно назначение, чтобы выбрать это лицо',
  // Отказ: лицо слишком близко (по номеру)
  closeN: (n: number) => `Лицо ${n} стоит слишком близко к соседнему — его не подставить`,
  // Причина отказа «близко» в строке назначения
  closeWhy: 'слишком близко к соседнему',
  // Отказ: лицо вне кадра (по номеру)
  outN: (n: number) => `Лицо ${n} выходит за край картинки — его не подставить`,
  // Причина отказа «вне кадра» в строке назначения
  outWhy: 'выходит за край картинки',
  // Кнопка в плашке отказа: снять назначение
  unassignN: (n: number) => `Снять назначение с лица ${n}`,
  // 409 faces_changed: назначения сброшены
  changed2: 'Картинка изменилась — лица найдены заново. Назначьте персонажей ещё раз',
  // Кнопка при двух назначениях
  go2: 'Подставить (2)',
  // Подтверждение кисти при существующих назначениях
  brushConfirm: 'Кисть работает с одним лицом — назначения сбросятся',
} as const;

// Склонение «проход»: 1 проход, 2–4 прохода, 5+ (и 11–14) проходов
export function passesWord(n: number): string {
  const d = n % 10, dd = n % 100;
  if (d === 1 && dd !== 11) return 'проход';
  if (d >= 2 && d <= 4 && (dd < 12 || dd > 14)) return 'прохода';
  return 'проходов';
}

// Чип котировки подвала (ADR-028, шаг 16б): число проходов и время — из котировки
// (facePasses, expectedSeconds), а не из макета. ETA округляется вверх до минуты, не меньше 1.
// Котировки нет (ещё едет или отказ) — чипа нет; без facePasses — один проход (этап 2)
export function faceQuoteChip(q: { facePasses?: number | null; expectedSeconds?: number | null } | null): string | null {
  if (!q) return null;
  const n = q.facePasses ?? 1;
  const parts = [`${n} ${passesWord(n)}`, 'бесплатно'];
  if (q.expectedSeconds != null) parts.push(`~${Math.max(1, Math.ceil(q.expectedSeconds / 60))} мин`);
  return parts.join(' · ');
}

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
  // Этап 3 (ADR-028): назначения «лицо → персонаж»; undefined — режим этапа 2
  assignments?: FaceAssignments;
  // Причина отказа ПОИСКА лиц (busy/provider_unavailable/…): плашка, пока не повторят поиск
  searchFailure?: { code: string; text: string } | null;
}

export type NoticeTone = 'info' | 'warning' | 'danger';
export interface NoticeSpec {
  tone: NoticeTone; icon: 'info' | 'alert' | 'clock' | 'cpu' | 'redo'; text: string;
  action?: { label: string; kind: 'ghostAccent' | 'secondary' };
  // Номера лиц, с которых плашка предлагает «Снять назначение с лица n» (этап 3)
  unassign?: number[];
}

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
  const { phase, faces, selectedFace, error, provider, localAvailable, useLocalOnce,
    tappedSmall, over8, facesTooClose, faceOutOfFrame, busy, providerUnavailable, facesChanged, hasMask,
    assignments, searchFailure } = i;

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
  } else if (assignments) {
    // Этап 3: текст зависит от состояния назначений
    barIcon = 'scan';
    if (assignments.active != null) {
      bar = assignments.assignments.has(assignments.active)
        ? FS_TEXT.barReassign(assignments.active)
        : FS_TEXT.barPick(assignments.active);
    } else if (assignments.assignments.size === 0) {
      bar = faces.length === 1 ? FS_TEXT.one : FS_TEXT.many;
    } else if (assignments.assignments.size === 1) {
      bar = FS_TEXT.barOne;
    } else {
      bar = FS_TEXT.barTwo;
    }
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
    // Этап 3: отказ ведёт к «Снять назначение с лица n». Сервер номер лица в теле не отдаёт,
    // поэтому при одном назначении номер однозначен (текст по номеру), при двух — общий
    // текст и кнопка снятия на каждое назначенное лицо
    const asgFaces = assignments ? [...assignments.assignments.keys()].sort((a, b) => a - b) : [];
    const refusal = (byN: (n: number) => string, general: string, legacy: string) => {
      if (!assignments || asgFaces.length === 0) {
        notices.push({ tone: 'danger', icon: 'alert', text: legacy, action: { label: FS_TEXT.brush, kind: 'ghostAccent' } });
      } else {
        notices.push({ tone: 'danger', icon: 'alert', text: asgFaces.length === 1 ? byN(asgFaces[0]) : general, unassign: asgFaces });
      }
    };
    if (facesTooClose) refusal(FS_TEXT.closeN, FS_TEXT.close, FS_TEXT.close);
    if (faceOutOfFrame) refusal(FS_TEXT.outN, FS_TEXT.outOfFrame, FS_TEXT.outOfFrame);
    if (assignments?.limit != null) {
      notices.push({ tone: 'warning', icon: 'alert', text: FS_TEXT.limit });
    }
    if (busy) {
      notices.push({ tone: 'danger', icon: 'clock', text: FS_TEXT.busy });
    }
    if (facesChanged) {
      notices.push({ tone: 'info', icon: 'redo', text: assignments ? FS_TEXT.changed2 : FS_TEXT.changed });
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
  } else if (phase === 'error' && searchFailure) {
    // Отказ ПОИСКА лиц не «немой» (дефект приёмки Майи): busy — очередь, provider_unavailable — локальные недоступны
    notices.push(searchFailure.code === 'busy'
      ? { tone: 'info', icon: 'clock', text: searchFailure.text }
      : searchFailure.code === 'provider_unavailable'
        ? { tone: 'info', icon: 'cpu', text: searchFailure.text }
        : { tone: 'danger', icon: 'alert', text: searchFailure.text });
  }

  // ── canSubmit: по матрице ──
  // Этап 3: назначения ≥ 1 + локальные модели + нет отказов по текущему набору
  // Этап 2 (без assignments): лицо выбрано + персонаж + локальные
  const faceReady = assignments
    ? assignments.assignments.size >= 1
    : (selectedFace != null || (faces.length === 0 && hasMask)) && i.selectedCharacter != null;
  const canSubmit = phase === 'ready' && faceReady && localOk
    // Отказ «близко» (7): неактивна, пока выбор не изменится
    && !facesTooClose
    // Этап 3: отказ «вне кадра» тоже блокирует (привязан к номеру лица)
    && !faceOutOfFrame;

  // ── Список персонажей: доступен, когда есть к чему подставлять ──
  // Этап 3: пикер виден, пока есть активное лицо; этап 2 — как раньше
  const listEnabled = phase === 'ready' && (
    assignments ? assignments.active != null
                : selectedFace != null || faces.length === 0
  );

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

// Отсечка при смене списка лиц (поиск заново, 409): назначения, active и limit на лица, которых
// нет в новом списке, отбрасываются. Ничего не отброшено — возвращается тот же объект
export function pruneAssignments(a: FaceAssignments, faces: readonly DetectedFace[]): FaceAssignments {
  const has = (n: number | null) => n != null && faces.some(f => f.n === n);
  const kept = new Map([...a.assignments].filter(([face]) => has(face)));
  const active = has(a.active) ? a.active : null;
  const limit = has(a.limit) ? a.limit : null;
  if (kept.size === a.assignments.size && active === a.active && limit === a.limit) return a;
  return { assignments: kept, active, limit };
}

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
