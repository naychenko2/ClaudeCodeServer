// Рамки лиц поверх картинки холста (этап 2 · шаг 10b, по образцу CropOverlay): пронумерованные
// <button> по координатам сервера (bbox в пикселях → доли через width/height), номер-бейдж в
// левом верхнем углу. Белая линия C.onDark с ореолом C.mediaScrim — видна на любой фотографии,
// от темы не зависит. Выбранная — C.accent + кольцо C.accentMuted; мелкая (small) — пунктир,
// выбрать нельзя (тап подсвечивает C.warning). На мобиле зона касания ≥ 44×44 CSS px.

import { useState } from 'react';
import { C, R, SHADOW } from 'aihome_shell/kit';
import type { DetectedFace } from './api';
import { faceRect, faceVisual, FS_TEXT, type FaceRect, type FaceVisual } from './faceSwapView';

const pct = (v: number) => `${(Math.max(0, Math.min(1, v)) * 100).toFixed(3)}%`;

// Зона касания: на мобиле не меньше 44×44 CSS px, на десктопе не меньше 32×32
const HIT = { mobile: 44, desktop: 32 };

// Бейдж номера: пилюля C.mediaScrim, цифра C.onDark, обводка 1px; центр — на углу рамки
function FaceBadge({ n, visual, mobile }: { n: number; visual: FaceVisual; mobile: boolean }) {
  const size = mobile ? 24 : 22;
  const bg = visual === 'selected' ? C.accent : visual === 'smallTapped' ? C.warning : visual === 'error' ? C.danger : C.mediaScrim;
  const fg = visual === 'selected' ? C.onAccent : C.onDark;
  const border = visual === 'selected' ? C.accent : visual === 'smallTapped' ? C.warning : visual === 'error' ? C.danger : C.onDark;
  return (
    <span aria-hidden style={{
      position: 'absolute', left: 0, top: 0, transform: 'translate(-45%, -45%)',
      minWidth: size, height: size, padding: `0 ${size >= 24 ? 6 : 4}px`, boxSizing: 'border-box',
      borderRadius: R.max, background: bg, color: fg, border: `1px ${visual === 'small' || visual === 'smallTapped' ? 'dashed' : 'solid'} ${border}`,
      display: 'inline-flex', alignItems: 'center', justifyContent: 'center',
      fontSize: mobile ? 12 : 11, fontWeight: 700, lineHeight: 1, whiteSpace: 'nowrap',
    }}>{n}</span>
  );
}

// Одна рамка лица. rect — доли картинки (faceRect); visual — состояние (faceSwapView.faceVisual)
function FaceBox({ f, rect, visual, mobile, onTap }: {
  f: DetectedFace; rect: FaceRect; visual: FaceVisual; mobile: boolean; onTap: () => void;
}) {
  const [hover, setHover] = useState(false);
  const hit = mobile ? HIT.mobile : HIT.desktop;
  const small = visual === 'small' || visual === 'smallTapped';
  const border = visual === 'selected'
    ? `3px solid ${C.accent}`
    : visual === 'error'
      ? `2px solid ${C.danger}`
      : `2px solid ${C.onDark}`;
  const boxShadow = visual === 'selected'
    ? `0 0 0 1px ${C.mediaScrim}, 0 0 0 4px ${C.accentMuted}`
    : `0 0 0 1px ${C.mediaScrim}, inset 0 0 0 1px ${C.mediaScrim}`;
  return (
    <button
      type="button"
      aria-label={small ? `Лицо ${f.n}: слишком мелкое` : `Лицо ${f.n}`}
      aria-pressed={visual === 'selected'}
      aria-disabled={small || undefined}
      title={small ? FS_TEXT.small : undefined}
      onClick={onTap}
      onMouseEnter={() => setHover(true)}
      onMouseLeave={() => setHover(false)}
      style={{
        position: 'absolute', left: pct(rect.x), top: pct(rect.y),
        width: pct(rect.width), height: pct(rect.height),
        minWidth: hit, minHeight: hit, boxSizing: 'border-box',
        border, borderRadius: R.md,
        borderStyle: small ? 'dashed' : 'solid',
        background: 'none', padding: 0, cursor: small ? 'not-allowed' : 'pointer',
        opacity: visual === 'dim' ? 0.65 : small ? 0.85 : 1,
        boxShadow, outline: 'none',
        // hover (только десктоп): рамка C.accentSoft
        ...(hover && !small && visual !== 'selected' ? { borderColor: C.accentSoft } : null),
      }}
    >
      <FaceBadge n={f.n} visual={visual} mobile={mobile} />
    </button>
  );
}

// Оверлей: контейнер absolute inset:0 (внутри контейнера картинки, как CropOverlay) + рамки.
// width/height — размеры картинки из ответа (для bbox→доли).
export function FaceOverlay({ faces, width, height, selectedFace, tappedSmall, errorFaces, mobile, onSelect }: {
  faces: readonly DetectedFace[];
  width: number;
  height: number;
  selectedFace: number | null;
  tappedSmall: number | null;
  errorFaces: readonly number[];
  mobile: boolean;
  onSelect: (n: number) => void;
}) {
  return (
    <div style={{ position: 'absolute', inset: 0 }} onPointerDown={e => e.stopPropagation()}>
      {faces.map(f => (
        <FaceBox key={f.n} f={f} rect={faceRect(f, width, height)} mobile={mobile}
          visual={faceVisual(f, { selectedFace, tappedSmall, errorFaces })}
          onTap={() => onSelect(f.n)} />
      ))}
    </div>
  );
}

// Спиннер поиска (состояние 1): плоский, в плашке C.glass
export function FaceSearchSpinner() {
  return (
    <span style={{
      width: 14, height: 14, flexShrink: 0, display: 'inline-block',
      border: '2px solid var(--c-progress-track)', borderTopColor: C.accent,
      borderRadius: '50%', animation: 'cc-spin 0.9s linear infinite',
    }} />
  );
}

function ClockIcon() {
  return (
    <svg width={14} height={14} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={1.75} strokeLinecap="round" strokeLinejoin="round" aria-hidden style={{ flexShrink: 0 }}>
      <circle cx="12" cy="12" r="10" /><path d="M12 6v6l4 2" />
    </svg>
  );
}

// Затемнение картинки во время поиска (состояние 1/1б): C.overlay + плашка C.glass со спиннером/часами
export function FaceSearchScrim({ busy, text }: { busy: boolean; text: string }) {
  return (
    <div style={{
      position: 'absolute', inset: 0, borderRadius: R.md, background: C.overlay,
      display: 'flex', alignItems: 'center', justifyContent: 'center', padding: 12,
    }}>
      <span style={{
        display: 'inline-flex', alignItems: 'center', gap: 8, maxWidth: '100%',
        padding: '8px 12px', borderRadius: R.lg, background: C.glass, color: C.textPrimary,
        fontSize: 12, fontWeight: 600, lineHeight: 1.4, boxShadow: SHADOW.card,
      }}>
        {busy ? <ClockIcon /> : <FaceSearchSpinner />}
        <span style={{ overflow: 'hidden', textOverflow: 'ellipsis' }}>{text}</span>
      </span>
    </div>
  );
}
