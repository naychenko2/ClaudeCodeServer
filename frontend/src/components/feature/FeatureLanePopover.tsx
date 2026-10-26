// Поповер и шторка интервала этапа (GC-F6, макеты feature-gantt-6/7 §«Детали поверх»).
//
// Три слоя, все над лентой (портал в document.body, position: fixed — в потоке они
// раздвигали бы строки ленты, чего нельзя: «сдвига раскладки нет»):
//   • FeatureLanePreview — превью по наведению (role=tooltip), без кнопок;
//   • FeatureLanePopover — закреплённый поповер (role=dialog, 288 px) по клику/Enter/Space;
//   • FeatureLaneSheet — мобильная шторка (ui/Modal в мобильном режиме).
//
// Позиция поповера — общая функция tooltipPlacement (ui/Tooltip.tsx): своей
// позиционирующей арифметики у нас нет.
//
// ЧТО ПОПОВЕР ОБЯЗАН СКАЗАТЬ СЛОВАМИ (карточка GC-F6):
//   • состояние интервала словами — «работа» / «ждёт» / «стоит» / «прервано»;
//   • из чего сложился интервал — по taskIds интервала, а НЕ «весь интервал ждал»:
//     «ждала задача X, в этом же интервале работала Y»;
//   • сколько задач в интервале — по LaneInterval.taskIds, выводится явно.
//
// Модель данных (LaneIntervalDetails) собирает РОДИТЕЛЬ (FeatureTimelineTab): там
// есть задачи, исполнители и персоны. Здесь — только честные слова по готовым данным.

import { useEffect, useLayoutEffect, useRef, useState } from 'react';
import type { ReactNode } from 'react';
import { createPortal } from 'react-dom';
import { X } from 'lucide-react';
import { C, FONT, FS, R, SHADOW, SP, Z } from '../../lib/design';
import { Button } from '../ui/Button';
import { IconButton } from '../ui/IconButton';
import { Modal } from '../ui/Modal';
import { PressableArea } from '../ui/PressableArea';
import { tooltipPlacement } from '../ui/Tooltip';
import { ICON_SIZE, ICON_STROKE } from '../ui/icons';
import { formatClock, formatDateTime, formatDuration } from './FeatureStatsBits';
import type { LaneIntervalState } from './FeatureLaneModel';
import type { StageKey } from '../../types/featureRun';

// Ширина поповера (макет feature-gantt-6 §«Поповер»): 288 px.
export const LANE_POPOVER_W = 288;
// id превью: на него ссылается интервал через aria-describedby (макет §«Доступность»).
export const LANE_TIP_ID = 'lane-tip';
// Поле от кромки окна — как у ui/Tooltip (поповер не прилипает к рамке).
const EDGE_PAD = 8;
// Оценка высоты поповера до замера (два прохода, как в ui/Tooltip).
const POPOVER_H_EST = 240;

// Задача в составе интервала: кто работал, какая модель, чей это чат.
export interface LaneTaskLine {
  taskId: string;
  title: string | null;
  persona: string | null;
  model: string | null;
  chatId: string | null;
  // Задача ЖДЁТ ответа В ЭТОМ интервале (опознана по правилу модели: state === 'wait').
  // Исторический интервал с тем же taskId ждущей не был — там waiting = false.
  waiting: boolean;
  // С какого момента задача ждёт (timeline.current.since) — только при waiting.
  waitingSince: string | null;
}

export interface LaneIntervalDetails {
  stageKey: StageKey;
  stageName: string;
  // Индекс интервала в этапе (0-based) и число интервалов этапа.
  index: number;
  total: number;
  from: string;
  to: string;
  minutes: number;
  state: LaneIntervalState;
  lastOutcome: string | null;
  tasks: LaneTaskLine[];
  // Пауза до следующего интервала этого же этапа (минуты).
  pauseAfterMinutes: number;
}

// Слово состояния интервала. «Прервано» — по исходу последнего хода (cancelled /
// interrupted / error): интервал закрылся, но работа в нём сорвалась.
export function intervalStateWord(state: LaneIntervalState, lastOutcome: string | null): string {
  if (lastOutcome === 'cancelled' || lastOutcome === 'interrupted' || lastOutcome === 'error') return 'прервано';
  switch (state) {
    case 'live': return 'работа';
    case 'wait': return 'ждёт';
    case 'stalled': return 'стоит';
    case 'done': return 'работа завершена';
  }
}

// Сколько задач в интервале — выводим явно (карточка GC-F6).
export function intervalTaskCountLabel(tasks: LaneTaskLine[]): string {
  return `Задач в интервале: ${tasks.length}`;
}

// Состав интервала словами. Правда, а не «весь интервал ждал»: ждущая задача —
// одна, остальные в этом же интервале работали.
// Ждущая задача — та, что ЖИВЬЁМ ждёт в этом интервале (state === 'wait'), и про
// неё говорим «ждёт с <время>»: без времени «ждала» читалось как вечное ожидание.
export function intervalCompositionLines(details: LaneIntervalDetails): string[] {
  const lines: string[] = [intervalTaskCountLabel(details.tasks)];
  const hasWaiting = details.tasks.some(t => t.waiting);
  for (const t of details.tasks) {
    const name = t.title ?? t.taskId;
    const who = [t.persona, t.model].filter(Boolean).join(' · ');
    const tail = who ? ` — ${who}` : '';
    if (t.waiting) {
      const since = t.waitingSince ? formatClock(t.waitingSince) : '';
      lines.push(`Задача «${name}» ждёт${since ? ` с ${since}` : ''}${tail}`);
    } else if (hasWaiting) lines.push(`В этом же интервале работала задача «${name}»${tail}`);
    else lines.push(`Работала задача «${name}»${tail}`);
  }
  return lines;
}

// Кто работал и на каких моделях — по составу интервала (персоны и модели исполнителей).
export function intervalWorkerLines(details: LaneIntervalDetails): string[] {
  const seen = new Set<string>();
  const out: string[] = [];
  for (const t of details.tasks) {
    if (!t.persona && !t.model) continue;
    const line = [t.persona, t.model].filter(Boolean).join(' · ');
    if (seen.has(line)) continue;
    seen.add(line);
    out.push(line);
  }
  return out;
}

// Пауза до следующего интервала этапа.
export function intervalPauseText(minutes: number): string {
  return minutes > 0 ? `Пауза до следующего: ${formatDuration(minutes)}` : 'Паузы нет';
}

// Границы интервала словами: «24.10 16:12 – 18:24 · 2 ч 12 мин».
export function intervalRangeText(details: LaneIntervalDetails): string {
  return `${formatDateTime(details.from)} – ${formatClock(details.to)} · ${formatDuration(details.minutes)}`;
}

// Главная задача интервала: ждущая (если ждёт) — иначе первая по составу. На неё
// ведут «Открыть задачу» и «чат →».
export function primaryLaneTask(details: LaneIntervalDetails): LaneTaskLine | null {
  if (details.tasks.length === 0) return null;
  return details.tasks.find(t => t.waiting) ?? details.tasks[0]!;
}

// Подпись секции (FS.xxs UPPERCASE, textMuted — макет).
function SectionLabel({ children }: { children: ReactNode }) {
  return (
    <div
      style={{
        fontFamily: FONT.sans, fontSize: FS.xxs, fontWeight: 700, letterSpacing: '0.06em',
        color: C.textMuted, marginBottom: SP.xs,
      }}
    >
      {children}
    </div>
  );
}

// Общий блок деталей интервала — один и тот же для поповера и шторки.
export function LaneIntervalDetailsView({ details, testId }: { details: LaneIntervalDetails; testId: string }) {
  const lines = intervalCompositionLines(details);
  const workers = intervalWorkerLines(details);
  return (
    <div data-testid={`${testId}-details`} style={{ display: 'flex', flexDirection: 'column', gap: SP.sm, fontFamily: FONT.sans }}>
      <div>
        <SectionLabel>СОСТОЯНИЕ</SectionLabel>
        <div data-testid={`${testId}-state`} style={{ fontSize: FS.base, fontWeight: 700, color: C.textHeading }}>
          {intervalStateWord(details.state, details.lastOutcome)}
        </div>
        {details.lastOutcome && (
          <div data-testid={`${testId}-outcome`} style={{ fontSize: FS.xs, color: C.textMuted, marginTop: SP.xxs }}>
            Последний ход: {details.lastOutcome}
          </div>
        )}
      </div>
      <div>
        <SectionLabel>ЗАДАЧИ</SectionLabel>
        <div data-testid={`${testId}-tasks`} style={{ fontSize: FS.sm, color: C.textPrimary, display: 'flex', flexDirection: 'column', gap: 2 }}>
          {lines.map((line, i) => (
            <div key={i} style={{ overflowWrap: 'anywhere' }}>{line}</div>
          ))}
        </div>
      </div>
      <div>
        <SectionLabel>КТО РАБОТАЛ</SectionLabel>
        <div data-testid={`${testId}-workers`} style={{ fontSize: FS.sm, color: C.textSecondary, display: 'flex', flexDirection: 'column', gap: 2 }}>
          {workers.length === 0
            ? <div>Исполнители не указаны</div>
            : workers.map((line, i) => <div key={i}>{line}</div>)}
        </div>
      </div>
      <div>
        <SectionLabel>ПАУЗА ДО СЛЕДУЮЩЕГО</SectionLabel>
        <div data-testid={`${testId}-pause`} style={{ fontSize: FS.sm, color: C.textSecondary }}>
          {intervalPauseText(details.pauseAfterMinutes)}
        </div>
      </div>
    </div>
  );
}

// Кнопки действий интервала: «Показать в ленте ↓», «Открыть задачу», «чат →».
function LaneActions({
  details, testId, variant, onShowInFeed, onOpenTask, onOpenChat,
}: {
  details: LaneIntervalDetails;
  testId: string;
  variant: 'popover' | 'sheet';
  onShowInFeed: () => void;
  onOpenTask: (taskId: string) => void;
  onOpenChat: (chatId: string) => void;
}) {
  const primary = primaryLaneTask(details);
  const primaryVariant = variant === 'sheet' ? 'primary' : 'ghost';
  const secondaryVariant = variant === 'sheet' ? 'secondary' : 'ghost';
  return (
    <div style={{ display: 'flex', gap: SP.sm, flexWrap: 'wrap' }}>
      <Button
        variant={primaryVariant}
        size={variant === 'sheet' ? 'md' : 'sm'}
        onClick={onShowInFeed}
        dataTestId={`${testId}-show-in-feed`}
      >
        Показать в ленте ↓
      </Button>
      {primary && (
        <Button
          variant={secondaryVariant}
          size={variant === 'sheet' ? 'md' : 'sm'}
          onClick={() => onOpenTask(primary.taskId)}
          dataTestId={`${testId}-open-task`}
        >
          Открыть задачу
        </Button>
      )}
      {primary?.chatId && (
        <Button
          variant={secondaryVariant}
          size={variant === 'sheet' ? 'md' : 'sm'}
          onClick={() => onOpenChat(primary.chatId!)}
          dataTestId={`${testId}-open-chat`}
        >
          чат →
        </Button>
      )}
    </div>
  );
}

// Превью по наведению (1280): role=tooltip, без кнопок, внизу — «Нажмите — закрепить».
export function FeatureLanePreview({ details, anchor }: {
  details: LaneIntervalDetails;
  anchor: { left: number; right: number; bottom: number };
}) {
  const [placement, setPlacement] = useState<{ left: number; top: number } | null>(null);
  const [size, setSize] = useState<{ width: number; height: number } | null>(null);
  const tipRef = useRef<HTMLDivElement | null>(null);

  useLayoutEffect(() => {
    const vw = typeof window !== 'undefined' && window.innerWidth > 0 ? window.innerWidth : 1024;
    const vh = typeof window !== 'undefined' && window.innerHeight > 0 ? window.innerHeight : 768;
    const p = tooltipPlacement(
      anchor,
      { width: vw, height: vh },
      'start',
      SP.sm - 1,
      EDGE_PAD,
      { width: size?.width ?? 240, height: size?.height ?? 90 },
    );
    setPlacement({ left: p.left, top: p.top });
  }, [anchor, size]);

  useLayoutEffect(() => {
    if (!placement) return;
    const el = tipRef.current;
    if (!el) return;
    const r = el.getBoundingClientRect();
    const h = Math.round(r.height);
    const w = Math.round(r.width);
    if (!size || size.height !== h || size.width !== w) setSize({ width: w, height: h });
  }, [placement, size]);

  return createPortal(
    <div
      ref={tipRef}
      role="tooltip"
      id={LANE_TIP_ID}
      data-testid={LANE_TIP_ID}
      style={{
        position: 'fixed',
        left: placement?.left ?? EDGE_PAD,
        top: placement?.top ?? EDGE_PAD,
        maxWidth: 260,
        zIndex: Z.dropdown,
        background: C.bgCard,
        border: `1px solid ${C.border}`,
        borderRadius: R.lg,
        boxShadow: SHADOW.dropdown,
        padding: `${SP.sm}px ${SP.md}px`,
        fontFamily: FONT.sans,
        color: C.textHeading,
        pointerEvents: 'none',
        whiteSpace: 'normal',
      }}
    >
      <div style={{ fontSize: FS.base, fontWeight: 700 }}>{intervalStateWord(details.state, details.lastOutcome)}</div>
      <div style={{ fontFamily: FONT.mono, fontSize: FS.xs, color: C.textSecondary, marginTop: SP.xxs }}>
        {intervalRangeText(details)}
      </div>
      <div style={{ fontSize: FS.sm, color: C.textSecondary, marginTop: SP.xxs }}>
        {intervalTaskCountLabel(details.tasks)}
      </div>
      <div style={{ fontSize: FS.xs, color: C.textMuted, marginTop: SP.xs }}>Нажмите — закрепить</div>
    </div>,
    document.body,
  );
}

// Закреплённый поповер (1280): role=dialog, 288 px. Esc/✕/клик мимо закрывают;
// фокус при открытии уходит на ✕ (родитель вернёт его на интервал при закрытии).
export function FeatureLanePopover({ details, anchor, anchorEl, onClose, onShowInFeed, onOpenTask, onOpenChat }: {
  details: LaneIntervalDetails;
  anchor: { left: number; right: number; bottom: number };
  // Элемент-якорь (кнопка интервала): клик по нему поповер НЕ закрывает.
  anchorEl?: HTMLElement | null;
  onClose: () => void;
  onShowInFeed: () => void;
  onOpenTask: (taskId: string) => void;
  onOpenChat: (chatId: string) => void;
}) {
  const rootRef = useRef<HTMLDivElement | null>(null);
  const [placement, setPlacement] = useState<{ left: number; top: number } | null>(null);
  const [size, setSize] = useState<{ width: number; height: number } | null>(null);

  useLayoutEffect(() => {
    const vw = typeof window !== 'undefined' && window.innerWidth > 0 ? window.innerWidth : 1024;
    const vh = typeof window !== 'undefined' && window.innerHeight > 0 ? window.innerHeight : 768;
    const p = tooltipPlacement(
      anchor,
      { width: vw, height: vh },
      'start',
      SP.sm - 1,
      EDGE_PAD,
      { width: LANE_POPOVER_W, height: size?.height ?? POPOVER_H_EST },
    );
    setPlacement({ left: p.left, top: p.top });
  }, [anchor, size]);

  useLayoutEffect(() => {
    if (!placement) return;
    const el = rootRef.current;
    if (!el) return;
    const r = el.getBoundingClientRect();
    const h = Math.round(r.height);
    if (!size || size.height !== h) setSize({ width: LANE_POPOVER_W, height: h });
  }, [placement, size]);

  // Esc — закрыть (фокус на интервал вернёт родитель).
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key !== 'Escape') return;
      e.preventDefault();
      onClose();
    };
    document.addEventListener('keydown', onKey);
    return () => document.removeEventListener('keydown', onKey);
  }, [onClose]);

  // Клик мимо — закрыть. Клик по самому интервару поповер не закрывает (перезакрепление).
  useEffect(() => {
    const onDown = (e: MouseEvent) => {
      const target = e.target as Node | null;
      if (!target) return;
      if (rootRef.current?.contains(target)) return;
      if (anchorEl && anchorEl.contains(target)) return;
      onClose();
    };
    document.addEventListener('mousedown', onDown);
    return () => document.removeEventListener('mousedown', onDown);
  }, [onClose, anchorEl]);

  // Фокус на ✕ при открытии.
  useEffect(() => {
    const root = rootRef.current;
    if (!root) return;
    const btn = root.querySelector('button');
    if (btn && typeof (btn as HTMLElement).focus === 'function') (btn as HTMLElement).focus();
  }, []);

  return createPortal(
    <div
      ref={rootRef}
      role="dialog"
      aria-label={`${details.stageName}, интервал ${details.index + 1} из ${details.total}`}
      data-testid="lane-popover"
      style={{
        position: 'fixed',
        left: placement?.left ?? EDGE_PAD,
        top: placement?.top ?? EDGE_PAD,
        width: LANE_POPOVER_W,
        maxWidth: '100%',
        boxSizing: 'border-box',
        background: C.bgCard,
        border: `1px solid ${C.border}`,
        borderRadius: R.lg,
        boxShadow: SHADOW.dropdown,
        padding: SP.md,
        zIndex: Z.dropdown,
        fontFamily: FONT.sans,
        display: 'flex',
        flexDirection: 'column',
        gap: SP.sm,
      }}
    >
      <div style={{ display: 'flex', alignItems: 'flex-start', gap: SP.sm }}>
        <div style={{ flex: 1, minWidth: 0, fontSize: FS.base, fontWeight: 700, color: C.textHeading }}>
          {details.stageName} · интервал {details.index + 1} из {details.total}
        </div>
        <span data-testid="lane-popover-close" style={{ display: 'inline-flex', flexShrink: 0 }}>
          <IconButton size="sm" tone="muted" variant="ghost" ariaLabel="Закрыть" onClick={onClose}>
            <X size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
          </IconButton>
        </span>
      </div>
      <div data-testid="lane-popover-range" style={{ fontFamily: FONT.mono, fontSize: FS.xs, color: C.textSecondary, overflowWrap: 'anywhere' }}>
        {intervalRangeText(details)}
      </div>
      <LaneIntervalDetailsView details={details} testId="lane-popover" />
      <LaneActions
        details={details}
        testId="lane-popover"
        variant="popover"
        onShowInFeed={onShowInFeed}
        onOpenTask={onOpenTask}
        onOpenChat={onOpenChat}
      />
    </div>,
    document.body,
  );
}

// Мобильная шторка (360): ui/Modal в мобильном режиме. Сверху — детали выбранного
// интервала и кнопки, ниже — «Все интервалы этапа · N» строками по 44 px.
export function FeatureLaneSheet({ items, selectedIndex, onSelect, onClose, onShowInFeed, onOpenTask, onOpenChat }: {
  items: LaneIntervalDetails[];
  selectedIndex: number;
  onSelect: (index: number) => void;
  onClose: () => void;
  onShowInFeed: () => void;
  onOpenTask: (taskId: string) => void;
  onOpenChat: (chatId: string) => void;
}) {
  const details = items[selectedIndex] ?? items[0];
  if (!details) return null;
  return (
    <Modal
      title={`${details.stageName} · интервал ${selectedIndex + 1} из ${items.length}`}
      onClose={onClose}
      width={420}
    >
      <div style={{ display: 'flex', flexDirection: 'column', gap: SP.md }}>
        <LaneIntervalDetailsView details={details} testId="lane-sheet" />
        <LaneActions
          details={details}
          testId="lane-sheet"
          variant="sheet"
          onShowInFeed={onShowInFeed}
          onOpenTask={onOpenTask}
          onOpenChat={onOpenChat}
        />
        <div>
          <SectionLabel>ВСЕ ИНТЕРВАЛЫ ЭТАПА · {items.length}</SectionLabel>
          <div data-testid="lane-sheet-list" style={{ display: 'flex', flexDirection: 'column' }}>
            {items.map((it, i) => (
              <PressableArea
                key={i}
                onPress={() => onSelect(i)}
                data-testid={`lane-sheet-interval-${i}`}
                aria-label={`${it.stageName}, интервал ${i + 1} из ${items.length}: ${intervalRangeText(it)}`}
                aria-expanded={i === selectedIndex}
                style={{
                  display: 'flex',
                  alignItems: 'center',
                  justifyContent: 'space-between',
                  gap: SP.sm,
                  minHeight: 44,
                  padding: `${SP.xs}px 0`,
                  borderTop: i === 0 ? 'none' : `1px solid ${C.borderLight}`,
                  fontFamily: FONT.sans,
                  fontSize: FS.sm,
                  fontWeight: i === selectedIndex ? 700 : 400,
                  color: i === selectedIndex ? C.textHeading : C.textSecondary,
                  textAlign: 'left',
                  width: '100%',
                  boxSizing: 'border-box',
                }}
              >
                <span style={{ overflowWrap: 'anywhere' }}>
                  {formatDateTime(it.from)} – {formatClock(it.to)}
                </span>
                <span style={{ fontFamily: FONT.mono, fontSize: FS.xs, color: C.textMuted, flexShrink: 0 }}>
                  {intervalStateWord(it.state, it.lastOutcome)}
                </span>
              </PressableArea>
            ))}
          </div>
        </div>
      </div>
    </Modal>
  );
}
