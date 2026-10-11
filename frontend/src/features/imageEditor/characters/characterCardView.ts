// Чистая логика карточки персонажа (спека character-panel-final-spec §4, §6, §7):
// номер состояния, фраза, бейдж, три деления шкалы «Насколько похож» и вид главного действия.
// React здесь нет — тестируется в node. Тексты — ключи characterTexts.ts, цвета — токены C.*.

import type { LoraVersionDto } from '../api';
import { C } from '../../../lib/design';
import { METER } from './characterTexts';
import { formatTrainedDate, type TrainSectionView } from './loraTrainView';
import { versionTitle } from './loraVersionsView';
import { MIN_PHOTOS } from './photos';
import { TRAIN_MIN } from './trainingPhotos';

export interface CardViewInput {
  // Число фото лица
  photos: number;
  // Число фото для обучения; null — ещё не известно
  train: number | null;
  view: TrainSectionView;
  versions: LoraVersionDto[];
  // character.local?.lora — своя обученная версия
  own: string | null | undefined;
}

export type CardState = 2 | 3 | 4 | 5;

export type CardSay =
  | { key: 'ready' | 'running' | 'queued' | 'trainedOwn' | 'failed' | 'failedLost' | 'failedAllRejected' }
  | { key: 'needTrain'; n: number }
  | { key: 'trained'; title: string };

export interface CardBadge { text: 'done' | 'failed'; tone: 'success' | 'danger' }

export interface MeterStop {
  value: number;
  tone: 'success' | 'accent';
  estimate: boolean;
  // running — заголовок «Обучается · N %» собирается из percent (METER.running), а не берётся из METER.stops
  titleKey: keyof typeof METER.stops | 'running';
  // Только для running: null — процент ещё не известен
  percent?: number | null;
  titleColor: string;
  hint: string;
}

export type CardAction =
  | { kind: 'train'; disabled: boolean; fixTrigger: boolean }
  | { kind: 'addTrainPhotos'; count: number }
  | { kind: 'progress' }
  | { kind: 'toContext' }
  | { kind: 'retry' }
  | { kind: 'replaceTrainPhotos' }
  // Особые виды зоны кнопки: loading, off, noLocal, busyOther, busyMine
  | { kind: 'notice'; view: TrainSectionView };

export interface CardView {
  state: CardState;
  say: CardSay;
  badge: CardBadge | null;
  stops: [MeterStop, MeterStop, MeterStop];
  action: CardAction;
}

const NOTICE_KINDS: TrainSectionView['kind'][] = ['loading', 'off', 'noLocal', 'busyOther', 'busyMine'];

export function characterCardView(i: CardViewInput): CardView {
  const { view, photos, train } = i;
  const connected = i.versions.find(v => v.connected);
  const own = !!i.own;
  const trained = !!connected || own || view.kind === 'done';

  let state: CardState;
  if (view.kind === 'queued' || view.kind === 'running') state = 3;
  else if (view.kind === 'failed') state = 5;
  else state = trained ? 4 : 2;

  return {
    state,
    say: sayFor(state, i, connected),
    badge: state === 4 ? { text: 'done', tone: 'success' } : state === 5 ? { text: 'failed', tone: 'danger' } : null,
    stops: [faceStop(photos), trainStop(photos, train, view), resultStop(state, i, connected)],
    action: actionFor(state, i),
  };
}

function sayFor(state: CardState, i: CardViewInput, connected: LoraVersionDto | undefined): CardSay {
  const { view } = i;
  if (state === 3) return { key: view.kind === 'queued' ? 'queued' : 'running' };
  if (state === 5 && view.kind === 'failed') {
    return { key: view.lost ? 'failedLost' : view.allRejected ? 'failedAllRejected' : 'failed' };
  }
  if (state === 4) {
    // Своя версия перекрывает версии обучения: рисуется по ней
    if (i.own) return { key: 'trainedOwn' };
    const v = connected ?? (view.kind === 'done' ? view.version : undefined);
    return { key: 'trained', title: v ? versionTitle(v, i.versions) : '' };
  }
  // Число фото для обучения ещё грузится — «нужно ещё N» было бы неправдой, фраза без числа
  if (i.train === null) return { key: 'ready' };
  // Обучение недоступно (off/noLocal) — не зовём к шагу, которого нет: фраза без призыва «нужно ещё N фото»
  if (view.kind === 'off' || view.kind === 'noLocal') return { key: 'ready' };
  const left = TRAIN_MIN - i.train;
  return left > 0 ? { key: 'needTrain', n: left } : { key: 'ready' };
}

function faceStop(photos: number): MeterStop {
  const full = photos >= MIN_PHOTOS;
  return {
    value: full ? 100 : 0, tone: 'success', estimate: false, titleKey: 'recognized',
    titleColor: full ? C.successText : C.textSecondary, hint: METER.facePhotos(photos),
  };
}

function trainStop(photos: number, train: number | null, view: TrainSectionView): MeterStop {
  const full = (train ?? 0) >= TRAIN_MIN;
  // Деление 1 есть, 2 нет — это следующий шаг, подсвечиваем accent;
  // если обучение недоступно (off/noLocal) — шага нет, заголовок приглушён
  const available = view.kind !== 'off' && view.kind !== 'noLocal';
  const next = available && !full && photos >= MIN_PHOTOS;
  return {
    value: full ? 100 : 0, tone: 'success', estimate: false, titleKey: 'canTrain',
    titleColor: full ? C.successText : next ? C.accent : C.textSecondary,
    hint: train == null ? '…' : full ? METER.trainPhotos(train) : METER.trainNeed(train, TRAIN_MIN),
  };
}

function resultStop(state: CardState, i: CardViewInput, connected: LoraVersionDto | undefined): MeterStop {
  const { view } = i;
  if (view.kind === 'queued') {
    return { value: 8, tone: 'accent', estimate: true, titleKey: 'queued', titleColor: C.accent, hint: METER.queuedNote };
  }
  if (view.kind === 'running') {
    return {
      value: view.percent ?? 8, tone: 'accent', estimate: view.percent === null, titleKey: 'running',
      percent: view.percent, titleColor: C.accent, hint: view.etaText ? METER.eta(view.etaText) : '',
    };
  }
  if (state === 5) {
    return { value: 0, tone: 'success', estimate: false, titleKey: 'failed', titleColor: C.dangerText, hint: METER.retry };
  }
  if (state === 4) {
    const v = connected ?? (view.kind === 'done' ? view.version : undefined);
    const date = v ? formatTrainedDate(v.trainedAt ?? v.createdAt) : '';
    return {
      value: 100, tone: 'success', estimate: false, titleKey: 'similar', titleColor: C.successText,
      hint: i.own ? METER.own : date ? METER.trainedOn(date) : '',
    };
  }
  return { value: 0, tone: 'success', estimate: false, titleKey: 'similar', titleColor: C.textSecondary, hint: METER.afterTrain };
}

function actionFor(state: CardState, i: CardViewInput): CardAction {
  const { view } = i;
  if (state === 3) return { kind: 'progress' };
  if (state === 4) return { kind: 'toContext' };
  if (state === 5 && view.kind === 'failed') return view.allRejected ? { kind: 'replaceTrainPhotos' } : { kind: 'retry' };
  if (NOTICE_KINDS.includes(view.kind)) return { kind: 'notice', view };
  const train = i.train ?? 0;
  if (train < TRAIN_MIN) {
    return { kind: 'addTrainPhotos', count: train };
  }
  const fixTrigger = view.kind === 'form' && view.reason === 'trigger';
  return { kind: 'train', disabled: fixTrigger, fixTrigger };
}
