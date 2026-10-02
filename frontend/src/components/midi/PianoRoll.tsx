import { useCallback, useEffect, useLayoutEffect, useRef, useState } from 'react';
import { C, FONT, FS, GROUP_COLORS, SP } from '../../lib/design';
import { useIsMobile } from '../../lib/breakpoints';
import { subscribeThemeMode } from '../../lib/themeMode';
import type { MidiDoc, MidiNote } from './midiModel';
import { shouldFollowPlayhead } from './midiViewerLogic';

// Нотная лента на canvas: клавиатура слева, линейка тактов сверху, ноты цветом дорожки.
// Холст размером с видимую область, рисуется только окно времени под scrollLeft.

export type PianoRollProps = {
  doc: MidiDoc;
  positionSec: number;
  muted: Set<number>;
  pxPerSec: number;
  height: number;
  // Явный переход (старт, перемотка): новый key — показать playhead на sec, даже если лента прокручена в сторону
  reveal?: { sec: number; key: number };
  onSeek(sec: number): void;
};

export const RULER_H = 20;
const KEY_W = 56;
const KEY_W_MOBILE = 40;
export const rollKeyWidth = (isMobile: boolean) => (isMobile ? KEY_W_MOBILE : KEY_W);
// Запас по высоте сверху и снизу диапазона нот
const PITCH_PAD = 2;
const MIN_ROWS = 12;

// Ударные GM: основные номера 35–59
export const DRUM_NAMES: Record<number, string> = {
  35: 'Бочка 2', 36: 'Бочка', 37: 'Римшот', 38: 'Малый', 39: 'Хлопок', 40: 'Малый 2',
  41: 'Том низ. 2', 42: 'Хэт закр.', 43: 'Том низ.', 44: 'Хэт ногой', 45: 'Том ср. 2',
  46: 'Хэт откр.', 47: 'Том ср.', 48: 'Том выс. 2', 49: 'Тарелка', 50: 'Том выс.',
  51: 'Райд', 52: 'Китайская', 53: 'Райд-купол', 54: 'Бубен', 55: 'Сплэш',
  56: 'Ковбелл', 57: 'Тарелка 2', 58: 'Вибрслэп', 59: 'Райд 2',
};

const BLACK = new Set([1, 3, 6, 8, 10]);
export const isBlackKey = (pitch: number) => BLACK.has(((pitch % 12) + 12) % 12);

export type RollLayout = {
  keyW: number;
  pxPerSec: number;
  scrollLeft: number;
  viewW: number;
  lowPitch: number;
  highPitch: number;
  rowH: number;
};

// Раскладка ленты: чистые функции, без DOM
export function makeLayout(
  doc: Pick<MidiDoc, 'minPitch' | 'maxPitch' | 'noteCount'>,
  opts: { keyW: number; pxPerSec: number; scrollLeft: number; viewW: number; height: number },
): RollLayout {
  let low = doc.noteCount > 0 ? doc.minPitch - PITCH_PAD : 60 - MIN_ROWS / 2;
  let high = doc.noteCount > 0 ? doc.maxPitch + PITCH_PAD : 60 + MIN_ROWS / 2 - 1;
  const lack = MIN_ROWS - (high - low + 1);
  if (lack > 0) {
    low -= Math.floor(lack / 2);
    high += Math.ceil(lack / 2);
  }
  low = Math.max(0, low);
  high = Math.min(127, high);
  const rows = high - low + 1;
  return {
    keyW: opts.keyW,
    pxPerSec: opts.pxPerSec,
    scrollLeft: opts.scrollLeft,
    viewW: opts.viewW,
    lowPitch: low,
    highPitch: high,
    rowH: Math.max(1, (opts.height - RULER_H) / rows),
  };
}

// Время → x в координатах холста (с учётом прокрутки и клавиатуры)
export const timeToX = (l: RollLayout, sec: number) => l.keyW + sec * l.pxPerSec - l.scrollLeft;
// x холста → время (обратное к timeToX)
export const xToTime = (l: RollLayout, x: number) => (x - l.keyW + l.scrollLeft) / l.pxPerSec;
// Высота → верх строки на холсте; верхняя строка — highPitch
export const pitchToY = (l: RollLayout, pitch: number) => RULER_H + (l.highPitch - pitch) * l.rowH;

// Видимое окно времени: всё, что правее клавиатуры и в пределах ширины холста
export function visibleWindow(l: RollLayout): [number, number] {
  return [Math.max(0, xToTime(l, l.keyW)), xToTime(l, l.viewW)];
}

export const noteVisible = (n: MidiNote, [t0, t1]: [number, number]) =>
  n.time <= t1 && n.time + n.duration >= t0;

export const noteSounding = (n: MidiNote, sec: number) => n.time <= sec && sec < n.time + n.duration;

// Значения CSS-переменных токенов: canvas не понимает var(...)
type Palette = {
  bg: string; blackRow: string; grid: string; bar: string; text: string; accent: string;
  keyWhite: string; keyBlack: string; keyBorder: string; textStrong: string;
};

function resolveToken(token: string): string {
  const m = /^var\((--[^),\s]+)\)$/.exec(token.trim());
  if (!m || typeof document === 'undefined') return token;
  return getComputedStyle(document.documentElement).getPropertyValue(m[1]).trim() || token;
}

function readPalette(): Palette {
  return {
    bg: resolveToken(C.bgPanel),
    blackRow: resolveToken(C.bgInset),
    grid: resolveToken(C.borderLight),
    bar: resolveToken(C.divider),
    text: resolveToken(C.textMuted),
    textStrong: resolveToken(C.textPrimary),
    accent: resolveToken(C.accent),
    keyWhite: resolveToken(C.bgCard),
    keyBlack: resolveToken(C.border),
    keyBorder: resolveToken(C.borderLight),
  };
}

const NOTE_NAMES = ['C', 'C♯', 'D', 'D♯', 'E', 'F', 'F♯', 'G', 'G♯', 'A', 'A♯', 'B'];
const pitchName = (p: number) => `${NOTE_NAMES[p % 12]}${Math.floor(p / 12) - 1}`;
const trackColor = (index: number) => GROUP_COLORS[index % GROUP_COLORS.length];

// Минимальная ширина такта в px, при которой подписывается каждый номер
const BAR_LABEL_MIN_PX = 28;

function draw(
  ctx: CanvasRenderingContext2D,
  doc: MidiDoc,
  l: RollLayout,
  height: number,
  pal: Palette,
  positionSec: number,
  muted: Set<number>,
  drumKeys: boolean,
) {
  const { keyW, viewW, rowH, lowPitch, highPitch } = l;
  const win = visibleWindow(l);

  ctx.fillStyle = pal.bg;
  ctx.fillRect(0, 0, viewW, height);

  // Подложка чёрных клавиш в поле нот
  if (!drumKeys) {
    ctx.fillStyle = pal.blackRow;
    for (let p = lowPitch; p <= highPitch; p++) {
      if (isBlackKey(p)) ctx.fillRect(keyW, pitchToY(l, p), viewW - keyW, rowH);
    }
  }

  // Доли и такты
  const [num] = doc.timeSig;
  const barLen = doc.bars.length > 1 ? doc.bars[1] - doc.bars[0] : (60 / doc.bpm) * num;
  const barPx = barLen * l.pxPerSec;
  const labelStep = Math.max(1, Math.ceil(BAR_LABEL_MIN_PX / Math.max(1, barPx)));
  ctx.font = `${FS.xs}px ${FONT.sans}`;
  ctx.textBaseline = 'middle';
  for (let i = 0; i < doc.bars.length; i++) {
    const start = doc.bars[i];
    const end = i + 1 < doc.bars.length ? doc.bars[i + 1] : start + barLen;
    if (end < win[0] || start > win[1]) continue;
    const beat = (end - start) / num;
    ctx.fillStyle = pal.grid;
    for (let b = 1; b < num; b++) {
      const x = Math.round(timeToX(l, start + b * beat));
      if (x > keyW) ctx.fillRect(x, RULER_H, 1, height - RULER_H);
    }
    const x = Math.round(timeToX(l, start));
    if (x >= keyW) {
      ctx.fillStyle = pal.bar;
      ctx.fillRect(x, 0, 1, height);
      if (i % labelStep === 0) {
        ctx.fillStyle = pal.text;
        ctx.textAlign = 'left';
        ctx.fillText(String(i + 1), x + SP.xs, RULER_H / 2);
      }
    }
  }

  // Ноты: сначала тихие и приглушённые, звучащие — поверх
  const sounding = new Map<number, string>();
  const active: Array<[MidiNote, string]> = [];
  ctx.save();
  ctx.beginPath();
  ctx.rect(keyW, RULER_H, viewW - keyW, height - RULER_H);
  ctx.clip();
  for (const t of doc.tracks) {
    const color = trackColor(t.index);
    const isMuted = muted.has(t.index);
    ctx.fillStyle = color;
    for (const n of t.notes) {
      if (n.midi < lowPitch || n.midi > highPitch || !noteVisible(n, win)) continue;
      if (!isMuted && noteSounding(n, positionSec)) {
        active.push([n, color]);
        sounding.set(n.midi, color);
        continue;
      }
      ctx.globalAlpha = isMuted ? 0.15 : 0.35 + 0.55 * n.velocity;
      const x = timeToX(l, n.time);
      ctx.fillRect(x, pitchToY(l, n.midi) + 0.5, Math.max(2, n.duration * l.pxPerSec - 1), Math.max(1, rowH - 1));
    }
  }
  ctx.globalAlpha = 1;
  ctx.strokeStyle = pal.textStrong;
  ctx.lineWidth = 1;
  for (const [n, color] of active) {
    const x = timeToX(l, n.time);
    const y = pitchToY(l, n.midi) + 0.5;
    const w = Math.max(2, n.duration * l.pxPerSec - 1);
    const h = Math.max(1, rowH - 1);
    ctx.fillStyle = color;
    ctx.fillRect(x, y, w, h);
    if (h >= 4) ctx.strokeRect(x + 0.5, y + 0.5, w - 1, h - 1);
  }
  ctx.restore();

  // Playhead
  const px = Math.round(timeToX(l, positionSec));
  if (px >= keyW && px <= viewW) {
    ctx.fillStyle = pal.accent;
    ctx.fillRect(px - 1, 0, 2, height);
  }

  // Клавиатура поверх поля нот
  ctx.fillStyle = pal.bg;
  ctx.fillRect(0, 0, keyW, RULER_H);
  ctx.fillStyle = pal.text;
  ctx.textAlign = 'left';
  ctx.fillText(drumKeys ? 'удары' : 'ноты', SP.xs, RULER_H / 2);
  ctx.fillStyle = pal.grid;
  ctx.fillRect(0, RULER_H - 1, viewW, 1);
  const labelFits = rowH >= FS.xs - 2;
  for (let p = lowPitch; p <= highPitch; p++) {
    const y = pitchToY(l, p);
    const black = !drumKeys && isBlackKey(p);
    const hit = sounding.get(p);
    ctx.fillStyle = hit ?? (black ? pal.keyBlack : pal.keyWhite);
    ctx.fillRect(0, y, keyW, rowH);
    ctx.fillStyle = pal.keyBorder;
    ctx.fillRect(0, y + rowH - 1, keyW, 1);
    const label = drumKeys ? DRUM_NAMES[p] : p % 12 === 0 ? pitchName(p) : undefined;
    if (label && labelFits) {
      ctx.fillStyle = hit ? pal.bg : pal.text;
      ctx.textAlign = 'right';
      ctx.fillText(label, keyW - SP.xs, y + rowH / 2, keyW - SP.sm);
    }
  }
  ctx.fillStyle = pal.bar;
  ctx.fillRect(keyW - 1, 0, 1, height);
}

export function PianoRoll({ doc, positionSec, muted, pxPerSec, height, reveal, onSeek }: PianoRollProps) {
  const isMobile = useIsMobile();
  const keyW = rollKeyWidth(isMobile);
  const wrapRef = useRef<HTMLDivElement>(null);
  const canvasRef = useRef<HTMLCanvasElement>(null);
  const paletteRef = useRef<Palette | null>(null);
  const [viewW, setViewW] = useState(0);
  const [scrollLeft, setScrollLeft] = useState(0);
  const [themeTick, setThemeTick] = useState(0);
  const prevPosRef = useRef(positionSec);

  // Ударные подписи — когда все дорожки ударные
  const drumKeys = doc.tracks.length > 0 && doc.tracks.every(t => t.isDrums);
  const contentW = keyW + Math.max(0, doc.durationSec) * pxPerSec;

  useLayoutEffect(() => {
    const el = wrapRef.current;
    if (!el) return;
    setViewW(el.clientWidth);
    const ro = new ResizeObserver(() => setViewW(el.clientWidth));
    ro.observe(el);
    return () => ro.disconnect();
  }, []);

  useEffect(() => subscribeThemeMode(() => {
    paletteRef.current = null;
    setThemeTick(t => t + 1);
  }), []);

  const layoutFor = useCallback(
    (sl: number) => makeLayout(doc, { keyW, pxPerSec, scrollLeft: sl, viewW, height }),
    [doc, keyW, pxPerSec, viewW, height],
  );

  const follow = useCallback((prev: number, sec: number, force: boolean) => {
    const el = wrapRef.current;
    if (!el || viewW === 0) return;
    if (shouldFollowPlayhead(prev, sec, visibleWindow(layoutFor(el.scrollLeft)), force)) {
      el.scrollLeft = Math.max(0, sec * pxPerSec - SP.xxl);
    }
  }, [layoutFor, pxPerSec, viewW]);

  // Playhead ушёл за край во время игры — догоняем, если до этого он был виден
  useEffect(() => {
    const prev = prevPosRef.current;
    prevPosRef.current = positionSec;
    follow(prev, positionSec, false);
  }, [positionSec, follow]);

  // Явный переход — к playhead принудительно
  const revealKey = reveal?.key;
  const revealSec = reveal?.sec ?? 0;
  useEffect(() => {
    if (revealKey !== undefined) follow(revealSec, revealSec, true);
    // eslint-disable-next-line react-hooks/exhaustive-deps -- срабатывает только на новый key
  }, [revealKey]);

  useEffect(() => {
    const canvas = canvasRef.current;
    if (!canvas || viewW === 0) return;
    const dpr = window.devicePixelRatio || 1;
    const w = Math.round(viewW * dpr);
    const h = Math.round(height * dpr);
    if (canvas.width !== w) canvas.width = w;
    if (canvas.height !== h) canvas.height = h;
    const ctx = canvas.getContext('2d');
    if (!ctx) return;
    paletteRef.current ??= readPalette();
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    draw(ctx, doc, layoutFor(scrollLeft), height, paletteRef.current, positionSec, muted, drumKeys);
  }, [doc, layoutFor, scrollLeft, height, positionSec, muted, drumKeys, viewW, themeTick]);

  const onPointerDown = (e: React.PointerEvent<HTMLCanvasElement>) => {
    const rect = e.currentTarget.getBoundingClientRect();
    const x = e.clientX - rect.left;
    const y = e.clientY - rect.top;
    if (y > RULER_H || x < keyW) return;
    const sec = xToTime(layoutFor(scrollLeft), x);
    onSeek(Math.min(Math.max(0, sec), doc.durationSec));
  };

  const onPointerMove = (e: React.PointerEvent<HTMLCanvasElement>) => {
    const rect = e.currentTarget.getBoundingClientRect();
    const onRuler = e.clientY - rect.top <= RULER_H && e.clientX - rect.left >= keyW;
    e.currentTarget.style.cursor = onRuler ? 'pointer' : 'default';
  };

  return (
    <div
      ref={wrapRef}
      onScroll={e => setScrollLeft(e.currentTarget.scrollLeft)}
      style={{ width: '100%', height, overflowX: 'auto', overflowY: 'hidden', background: C.bgPanel }}
    >
      <div style={{ width: Math.max(contentW, viewW), height }}>
        <canvas
          ref={canvasRef}
          onPointerDown={onPointerDown}
          onPointerMove={onPointerMove}
          style={{ position: 'sticky', left: 0, display: 'block', width: viewW, height }}
        />
      </div>
    </div>
  );
}
