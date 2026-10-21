// Лента времени фичи (волна 3, Ф7a, макет features-overview-3-final §«Статистика»):
// ЧИСТЫЕ функции над `stats.timeline` — фронт только раскладывает, бэк считает.
//
// Модель: лента — последовательность БЛОКОВ в хронологическом порядке:
//   · stage — блок этапа (cut/plan/prod/…/merge) со своими сегментами (work/waiting/
//     idle/error/notWorking); ширина ∝ сумме минут сегментов;
//   · break — СЖАТЫЙ разрыв: пауза длиннее порога (2 ч) схлопывается в фиксированную
//     ширину с подписью «разрыв · N ч» (шкала честно помечена как сжатая, не в масштабе).
// Никаких выдуманных данных: всё — из `from`/`to`/`segments`/`current`/`source`/`truncated`.

import type { FeatureTimeline } from '../../types/featureStats';
import type { StageKey } from '../../types/featureRun';

// Порог сжатия паузы (минуты): длиннее — схлопывается в «разрыв · N ч».
export const PAUSE_BREAK_MIN = 120; // 2 ч

// Виды сегментов (те же, что в FeatureTimeline.segments).
export type SegmentKind = 'work' | 'waiting' | 'idle' | 'error' | 'notWorking';

// Сегмент ленты: интервал + вид + (опц.) задача.
export interface TimelineSegment {
  from: string;
  to: string;
  kind: SegmentKind;
  minutes: number;
  taskId: string | null;
}

// Блок ленты: этап (с сегментами) или сжатый разрыв.
export type TimelineBlock =
  | { type: 'stage'; key: string; label: string; from: string; to: string; minutes: number; segments: TimelineSegment[]; reviewMinutes: number }
  | { type: 'break'; from: string; to: string; minutes: number; label: string };

// Итоговая модель ленты (что рисует вкладка).
export interface TimelineModel {
  // Блоки в хронологическом порядке (этапы + сжатые разрывы).
  blocks: TimelineBlock[];
  // Ось времени: от создания до «сейчас»/мержа/закрытия + подпись конца.
  axis: { from: string; to: string; endLabel: string };
  // Есть ли дорожка ревью (сегменты track=review) — рисуем тонкую дорожку.
  hasReview: boolean;
  // «Сейчас»: текущее состояние (waiting/stopped/merge_pending) с моментом.
  current: { kind: string; since: string; label: string } | null;
  // Точность: откуда числа (approx/journal) и обрезана ли лента.
  accuracy: { source: 'approx' | 'journal'; truncated: boolean };
  // Есть ли что показывать (сегменты или текущее состояние); false → пустое состояние.
  hasData: boolean;
}

// === Форматирование времени (TZ пользователя: локальные геттеры Date) ===

// «ЧЧ:ММ» (локальное время).
export function formatClock(iso: string): string {
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return '';
  return `${String(d.getHours()).padStart(2, '0')}:${String(d.getMinutes()).padStart(2, '0')}`;
}

// «dd.mm» (локальная дата).
export function formatDate(iso: string): string {
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return '';
  return `${String(d.getDate()).padStart(2, '0')}.${String(d.getMonth() + 1).padStart(2, '0')}`;
}

// «dd.mm · ЧЧ:ММ» (локальная дата + время).
export function formatDateTime(iso: string): string {
  const dt = formatDate(iso);
  const tm = formatClock(iso);
  if (!dt && !tm) return '';
  return `${dt} · ${tm}`.replace(' · ', ' · ');
}

// Длительность: «N мин» / «N ч» / «N ч MM мин» (как fmt в макете: минуты с ведущим 0, если есть часы).
export function formatDuration(minutes: number): string {
  const m = Math.max(0, Math.round(minutes));
  const h = Math.floor(m / 60);
  const r = m % 60;
  if (h === 0) return `${r} мин`;
  if (r === 0) return `${h} ч`;
  return `${h} ч ${String(r).padStart(2, '0')} мин`;
}

// === Подписи и цвета видов сегментов ===

// Подпись вида (макет KIND): work/waiting/idle/error/notWorking.
export function kindLabel(kind: SegmentKind): string {
  switch (kind) {
    case 'work': return 'работа';
    case 'waiting': return 'ждёт вас';
    case 'idle': return 'простой';
    case 'error': return 'ошибка / стоит';
    case 'notWorking': return 'не работали';
  }
}

// Цвет вида — ТОЛЬКО токены C.* (макет: work=accent, waiting=info, idle=progress-track,
// error=danger, notWorking=штриховка bgSelected/bgInset). Возвращает CSS-подложку.
export function kindBackground(kind: SegmentKind): string {
  switch (kind) {
    case 'work': return 'var(--c-accent)';
    case 'waiting': return 'var(--c-info)';
    case 'idle': return 'var(--c-progress-track)';
    case 'error': return 'var(--c-danger)';
    // notWorking — штриховка (ожидание и простой без журнала не разделить).
    case 'notWorking':
      return 'repeating-linear-gradient(135deg, var(--c-bg-selected) 0 5px, var(--c-bg-inset) 5px 10px)';
  }
}

// Текст поверх вида: на заливке (work/waiting/error) — onAccent, на нейтральных — textPrimary.
export function kindTextColor(kind: SegmentKind): string {
  switch (kind) {
    case 'work':
    case 'waiting':
    case 'error':
      return 'var(--c-on-accent)';
    case 'idle':
    case 'notWorking':
      return 'var(--c-text-primary)';
  }
}

// Имя этапа по ключу (макет: cut→Разрез, …, merge→Мерж). Неизвестный ключ — как есть.
export function stageLabel(key: string): string {
  const names: Record<string, string> = {
    cut: 'Разрез', plan: 'План', prod: 'Производство', review: 'Ревью',
    qa: 'Проверка', final: 'Финальное ревью', merge: 'Мерж',
  };
  return names[key] ?? key;
}

// === Чистая функция: модель ленты из stats.timeline ===

// Длительность интервала в минутах (0, если невалидно/отрицательно).
function spanMinutes(from: string, to: string): number {
  const a = new Date(from).getTime();
  const b = new Date(to).getTime();
  if (Number.isNaN(a) || Number.isNaN(b)) return 0;
  return Math.max(0, Math.round((b - a) / 60000));
}

// Подпись «сейчас» по текущему состоянию (макет: «ждёт вашего ответа сейчас · с ЧЧ:ММ»).
function currentLabel(kind: string, since: string): string {
  const t = formatClock(since);
  switch (kind) {
    case 'waiting': return `ждёт вашего ответа сейчас${t ? ' · с ' + t : ''}`;
    case 'stopped': return `стоит сейчас${t ? ' · с ' + t : ''}`;
    case 'merge_pending': return `ждёт согласия на мерж сейчас${t ? ' · с ' + t : ''}`;
    default: return `сейчас${t ? ' · с ' + t : ''}`;
  }
}

// Подпись конца оси: «сейчас · ЧЧ:ММ» (идёт) / «закрыта · dd.mm · ЧЧ:ММ» / «смержена · …».
function axisEndLabel(to: string, isClosed: boolean): string {
  const dt = formatDateTime(to);
  if (!dt) return isClosed ? 'закрыта' : 'сейчас';
  return `${isClosed ? 'закрыта' : 'сейчас'} · ${dt}`;
}

// Строит модель ленты из stats.timeline. `closed` — фича закрыта/смержена
// (конец оси — не «сейчас», а время закрытия; current == null).
export function buildTimelineModel(tl: FeatureTimeline, source: 'approx' | 'journal', truncated: boolean, closed: boolean): TimelineModel {
  const segs = [...(tl.segments ?? [])].sort((a, b) => new Date(a.from).getTime() - new Date(b.from).getTime());

  // Группировка сегментов в блоки этапов: подряд идущие сегменты одного этапа — один блок.
  const blocks: TimelineBlock[] = [];
  for (const s of segs) {
    const minutes = spanMinutes(s.from, s.to);
    const seg: TimelineSegment = { from: s.from, to: s.to, kind: s.kind, minutes, taskId: s.taskId ?? null };
    const last = blocks[blocks.length - 1];
    if (last && last.type === 'stage' && last.key === s.stage) {
      last.segments.push(seg);
      last.minutes += minutes;
      if (s.track === 'review') last.reviewMinutes += minutes;
      if (new Date(s.to).getTime() > new Date(last.to).getTime()) last.to = s.to;
    } else {
      const from = s.from;
      blocks.push({ type: 'stage', key: s.stage, label: stageLabel(s.stage), from, to: s.to, minutes, segments: [seg], reviewMinutes: s.track === 'review' ? minutes : 0 });
    }
  }

  // Сжатие разрывов: пауза (между блоками, до первого и после последнего) длиннее порога
  // → вставляется блок break фиксированной ширины с подписью «разрыв · N ч».
  const out: TimelineBlock[] = [];
  const from0 = tl.from;
  const to0 = tl.to;
  const pushBreak = (from: string, to: string) => {
    const minutes = spanMinutes(from, to);
    if (minutes >= PAUSE_BREAK_MIN) {
      out.push({ type: 'break', from, to, minutes, label: `разрыв · ${formatDuration(minutes)}` });
    }
  };

  if (blocks.length === 0) {
    // Ни одного сегмента: данных нет (разрыв НЕ выдумываем — это просто пустой span;
    // hasData решится по current). Иначе «считать нечего» превращалось бы в гигантский разрыв.
  } else {
    // Пауза до первого блока.
    pushBreak(from0, blocks[0]!.from);
    for (let i = 0; i < blocks.length; i++) {
      const b = blocks[i]!;
      out.push(b);
      // Пауза до следующего блока.
      if (i < blocks.length - 1) pushBreak(b.to, blocks[i + 1]!.from);
    }
    // Пауза после последнего блока.
    pushBreak(blocks[blocks.length - 1]!.to, to0);
  }

  // Дорожка ревью: есть ли сегменты track=review (кроме «разрывов»).
  const hasReview = out.some(b => b.type === 'stage' && b.reviewMinutes > 0);

  // «Сейчас»: текущее состояние (если фича не закрыта и current есть).
  const cur = tl.current;
  const current = cur && !closed
    ? { kind: cur.kind, since: cur.since, label: currentLabel(cur.kind, cur.since) }
    : null;

  const hasData = out.length > 0 || current != null;

  return {
    blocks: out,
    axis: { from: from0, to: to0, endLabel: axisEndLabel(to0, closed) },
    hasReview,
    current,
    accuracy: { source, truncated },
    hasData,
  };
}

// Точка «сейчас» на оси: доля 0..1 от from до to по моменту `since` (для отметки на ленте).
export function nowFraction(from: string, to: string, at: string): number {
  const a = new Date(from).getTime();
  const b = new Date(to).getTime();
  const c = new Date(at).getTime();
  if (Number.isNaN(a) || Number.isNaN(b) || b === a) return 1;
  const f = (c - a) / (b - a);
  return Math.min(1, Math.max(0, f));
}

// Тип-помощник: ключ этапа как StageKey (для focusFeatureStage).
export function asStageKey(key: string): StageKey {
  return key as StageKey;
}
