// Чистая логика раздела «Обучить персонажа»: какой вид показать и какие тексты подставить.
// Вход — данные хука статуса, число фото и роль; React здесь нет, тестируется в node.

import type { LoraTrainStateDto, LoraVersionDto } from '../api';
import type { LoraFailure } from './loraErrors';
import type { LoraTrainView } from './useLoraTraining';
import {
  TRAIN_ACTION_FAILED, TRAIN_BLOCKED, TRAIN_BUSY_OTHER, TRAIN_BUSY_OTHER_NOTIME, TRAIN_ETA, TRAIN_ETA_UNKNOWN,
  TRAIN_BADGE, TRAIN_RUNNING, TRAIN_TEXT, TRAIN_TRAINED_META, TRAIN_VERSION_LIMIT,
} from './loraTrainTexts';
import { TRAIN_MIN } from './trainingPhotos';

// ADR-026 §1: латиница, цифры и «_», от 3 до 24 символов, начинать с буквы
export const TRIGGER_RE = /^[a-z][a-z0-9_]{2,23}$/;
// Оценка по умолчанию (ADR-026 §3), если ни воркер, ни пресет времени не прислали
const DEFAULT_ETA_SEC = 6660;

export const isValidTrigger = (t: string) => TRIGGER_RE.test(t);

export function formatDuration(seconds: number): string {
  const m = Math.max(1, Math.round(seconds / 60));
  if (m < 60) return `${m} мин`;
  const h = Math.floor(m / 60);
  const r = m % 60;
  return r ? `${h} ч ${r} мин` : `${h} ч`;
}

export function formatTrainedDate(iso: string | undefined): string {
  const t = iso ? Date.parse(iso) : NaN;
  if (Number.isNaN(t)) return '';
  return new Date(t).toLocaleDateString('ru-RU', { day: 'numeric', month: 'long' });
}

export interface TrainViewInput {
  view: LoraTrainView;
  state: LoraTrainStateDto | null;
  // Последний отказ действия или опроса
  error: LoraFailure | null;
  // null — число фото ещё не известно
  photoCount: number | null;
  minPhotos?: number;
  trigger: string;
  isAdmin: boolean;
  // Прогон, который человек уже закрыл («Оставить прежнюю»)
  dismissedRunId: string | null;
  now?: number;
}

export type TrainSectionView =
  | { kind: 'loading' }
  | { kind: 'off' }
  | { kind: 'noLocal' }
  | { kind: 'busyOther'; text: string; canStopForeign: boolean }
  | { kind: 'busyMine' }
  | { kind: 'queued' }
  | { kind: 'running'; text: string; percent: number | null; etaText: string | null }
  | { kind: 'done'; version: LoraVersionDto; first: boolean; meta: string; photosChanged: boolean }
  | { kind: 'failed'; version: LoraVersionDto; allRejected: boolean }
  | {
    kind: 'form';
    // Подпись и блокировка кнопки старта
    label: string;
    disabled: boolean;
    reason: 'photos' | 'trigger' | null;
    stopped: boolean;
    photosChanged: boolean;
  };

export function isAllRejected(v: LoraVersionDto): boolean {
  if (v.photosKept === 0) return true;
  return v.photosCount > 0 && (v.rejected?.length ?? 0) >= v.photosCount;
}

function busyText(busyUntil: string | undefined, now: number): string {
  const t = busyUntil ? Date.parse(busyUntil) : NaN;
  if (Number.isNaN(t) || t <= now) return TRAIN_BUSY_OTHER_NOTIME;
  return TRAIN_BUSY_OTHER(formatDuration((t - now) / 1000));
}

export function trainSectionView(i: TrainViewInput): TrainSectionView {
  const now = i.now ?? Date.now();
  const min = i.minPhotos ?? TRAIN_MIN;
  const { view, state } = i;

  if (view.kind === 'loading') return { kind: 'loading' };
  if (view.kind === 'unavailable') return { kind: view.reason === 'disabled' ? 'off' : 'noLocal' };
  if (view.kind === 'queued') return { kind: 'queued' };
  if (view.kind === 'running') {
    const preset = state?.presets[0]?.etaSeconds;
    if (view.step > 0 && view.totalSteps) {
      return {
        kind: 'running',
        text: TRAIN_RUNNING(view.step, view.totalSteps),
        percent: Math.min(100, Math.round((view.step / view.totalSteps) * 100)),
        etaText: view.etaSeconds != null ? TRAIN_ETA(formatDuration(view.etaSeconds)) : null,
      };
    }
    // Шагов нет — показываем только оценку времени: из воркера, иначе из пресета
    const eta = view.etaSeconds ?? preset ?? DEFAULT_ETA_SEC;
    return {
      kind: 'running',
      text: TRAIN_ETA_UNKNOWN(formatDuration(eta)),
      percent: null,
      etaText: null,
    };
  }
  if (view.kind === 'done' && i.dismissedRunId !== view.version.runId) {
    const v = view.version;
    return {
      kind: 'done', version: v, first: v.connected,
      meta: TRAIN_TRAINED_META(formatTrainedDate(v.trainedAt ?? v.createdAt), v.photosKept ?? v.photosCount),
      photosChanged: !!state?.photosChanged,
    };
  }
  if (view.kind === 'failed') return { kind: 'failed', version: view.version, allRejected: isAllRejected(view.version) };

  // idle / cancelled / закрытое done: занятость карты, затем форма старта
  if (i.error?.reason === 'gpu_busy' || (state?.gpu.busy && !state.gpu.mine && !state.current)) {
    return {
      kind: 'busyOther', text: busyText(i.error?.busyUntil ?? state?.gpu.busyUntil, now), canStopForeign: i.isAdmin,
    };
  }
  if (i.error?.reason === 'owner_busy' || (state?.gpu.mine && !state.current)) return { kind: 'busyMine' };

  const count = i.photoCount ?? 0;
  const photosChanged = !!state?.photosChanged;
  const retrain = photosChanged || view.kind === 'done';
  const base = { stopped: view.kind === 'cancelled', photosChanged };
  if (count < min) {
    return { kind: 'form', ...base, label: TRAIN_BLOCKED(min - count), disabled: true, reason: 'photos' };
  }
  const label = retrain ? TRAIN_TEXT.restart : TRAIN_TEXT.start;
  if (!isValidTrigger(i.trigger)) return { kind: 'form', ...base, label, disabled: true, reason: 'trigger' };
  return { kind: 'form', ...base, label, disabled: false, reason: null };
}

// Текст отказа старта/подключения (кроме занятости карты — её показывает вид)
export function failureText(f: LoraFailure, min = TRAIN_MIN): string | null {
  switch (f.reason) {
    case 'gpu_busy': case 'owner_busy': return null;
    case 'bad_trigger': return TRAIN_TEXT.triggerError;
    case 'too_few_photos': return TRAIN_BLOCKED(min);
    case 'version_limit': return TRAIN_VERSION_LIMIT;
    case 'disabled': return TRAIN_TEXT.disabled;
    case 'no_seam': case 'unavailable': return TRAIN_TEXT.noLocal;
    default: return TRAIN_ACTION_FAILED;
  }
}

// Текст окна «Начать обучение?»: с оговоркой, если у персонажа уже есть подключённая LoRA
export function confirmParts(hasConnected: boolean) {
  return {
    before: TRAIN_TEXT.confirmBefore,
    bold: TRAIN_TEXT.confirmBold,
    after: TRAIN_TEXT.confirmAfter,
    note: hasConnected ? TRAIN_TEXT.confirmConnected : null,
  };
}

export function badgeFor(v: TrainSectionView): { text: string; tone: 'neutral' | 'accent' | 'success' | 'danger' } | null {
  switch (v.kind) {
    case 'queued': return { text: TRAIN_BADGE.queued, tone: 'neutral' };
    case 'running': return { text: v.percent != null ? `${v.percent} %` : TRAIN_BADGE.running, tone: 'accent' };
    case 'done': return { text: TRAIN_BADGE.done, tone: 'success' };
    case 'failed': return { text: TRAIN_BADGE.failed, tone: 'danger' };
    default: return null;
  }
}
