// Рамки лиц поверх картинки холста (этап 2 · шаг 10b, по образцу CropOverlay; этап 3 · шаг 16б —
// назначения): пронумерованные <button> по координатам сервера (bbox в пикселях → доли через
// width/height), номер-бейдж в левом верхнем углу. Белая линия C.onDark с ореолом C.mediaScrim —
// видна на любой фотографии, от темы не зависит. Активная — C.accent + кольцо C.accentMuted;
// назначенная — C.accent без кольца и ярлык «аватар + имя»; лимит — C.warning; отказ — C.danger;
// мелкая (small) — пунктир, выбрать нельзя (тап подсвечивает C.warning). На мобиле зона касания
// ≥ 44×44 CSS px.

import { useState } from 'react';
import { AlertTriangle, ScanFace } from 'lucide-react';
import { C, FS, R, SHADOW, SP, ICON_STROKE } from 'aihome_shell/kit';
import type { DetectedFace } from './api';
import { faceAria, faceRect, faceVisual, FS_TEXT, labelPlace, type FaceRect, type FaceVisual } from './faceSwapView';

const pct = (v: number) => `${(Math.max(0, Math.min(1, v)) * 100).toFixed(3)}%`;

// Зона касания: на мобиле не меньше 44×44 CSS px, на десктопе не меньше 32×32
const HIT = { mobile: 44, desktop: 32 };

// Цвет вида рамки: акцент (активная/назначенная), warning (лимит, мелкое тапнутое), danger (отказ)
const accentLike = (v: FaceVisual) => v === 'selected' || v === 'assigned';
const toneOf = (v: FaceVisual): string | null =>
  accentLike(v) ? C.accent : v === 'limit' || v === 'smallTapped' ? C.warning : v === 'error' ? C.danger : null;

// Ярлык назначенного лица: имя персонажа и аватар (url фото или null — значок)
export interface FaceLabel { name: string; avatar: string | null }

// Бейдж номера: пилюля C.mediaScrim, цифра C.onDark, обводка 1px; центр — на углу рамки
function FaceBadge({ n, visual, mobile }: { n: number; visual: FaceVisual; mobile: boolean }) {
  const size = mobile ? 24 : 22;
  const tone = toneOf(visual);
  const fg = accentLike(visual) ? C.onAccent : C.onDark;
  return (
    <span aria-hidden style={{
      position: 'absolute', left: 0, top: 0, transform: 'translate(-45%, -45%)',
      minWidth: size, height: size, padding: `0 ${size >= 24 ? 6 : 4}px`, boxSizing: 'border-box',
      borderRadius: R.max, background: tone ?? C.mediaScrim, color: fg, border: `1px ${visual === 'small' || visual === 'smallTapped' ? 'dashed' : 'solid'} ${tone ?? C.onDark}`,
      display: 'inline-flex', alignItems: 'center', justifyContent: 'center',
      fontSize: mobile ? 12 : 11, fontWeight: 700, lineHeight: 1, whiteSpace: 'nowrap',
    }}>{n}</span>
  );
}

// Ярлык «аватар + имя» под рамкой (C.accent; у отказа — C.danger и значок тревоги). Внутри
// кнопки рамки: тап по нему = тап по рамке; сам ярлык pointer-events: none
function FaceNameLabel({ label, rect, danger, mobile }: { label: FaceLabel; rect: FaceRect; danger: boolean; mobile: boolean }) {
  const place = labelPlace(rect);
  const pos = place.inside ? { bottom: SP.xs } : { top: `calc(100% + ${SP.xs}px)` };
  const h = place.h === 'right' ? { right: -3 } : place.h === 'left' ? { left: -3 } : { left: '50%', transform: 'translateX(-50%)' };
  return (
    <span data-face-label="true" aria-hidden style={{
      position: 'absolute', ...pos, ...h, pointerEvents: 'none',
      height: mobile ? 24 : 22, maxWidth: 120, boxSizing: 'border-box',
      display: 'inline-flex', alignItems: 'center', gap: SP.xs, padding: `0 ${SP.sm}px 0 ${SP.xxs}px`,
      borderRadius: R.max, background: danger ? C.danger : C.accent, color: danger ? C.onDark : C.onAccent,
      boxShadow: `0 0 0 1px ${C.mediaScrim}`, fontSize: FS.xs, fontWeight: 700, lineHeight: 1, whiteSpace: 'nowrap',
    }}>
      {danger
        ? <span style={{ display: 'inline-flex', flexShrink: 0, paddingLeft: SP.xxs }}><AlertTriangle size={12} strokeWidth={ICON_STROKE} /></span>
        : (
          <span style={{
            width: 18, height: 18, flex: '0 0 18px', borderRadius: R.max, overflow: 'hidden', boxSizing: 'border-box',
            border: `1px solid ${C.onAccent}`, display: 'inline-flex', alignItems: 'center', justifyContent: 'center',
          }}>
            {label.avatar
              ? <img src={label.avatar} alt="" style={{ width: '100%', height: '100%', objectFit: 'cover', display: 'block' }} />
              : <ScanFace size={12} strokeWidth={ICON_STROKE} />}
          </span>
        )}
      <span style={{ minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis' }}>{label.name}</span>
    </span>
  );
}

// Одна рамка лица. rect — доли картинки (faceRect); visual — состояние (faceSwapView.faceVisual);
// label — ярлык назначения (null — лицо свободно)
function FaceBox({ f, rect, visual, label, mobile, onTap }: {
  f: DetectedFace; rect: FaceRect; visual: FaceVisual; label: FaceLabel | null; mobile: boolean; onTap: () => void;
}) {
  const [hover, setHover] = useState(false);
  const hit = mobile ? HIT.mobile : HIT.desktop;
  const small = visual === 'small' || visual === 'smallTapped';
  const tone = toneOf(visual);
  const border = accentLike(visual) ? `3px solid ${C.accent}` : `2px solid ${tone ?? C.onDark}`;
  // Кольцо C.accentMuted — только у активной (у назначенной его нет, по v2)
  const boxShadow = visual === 'selected'
    ? `0 0 0 1px ${C.mediaScrim}, 0 0 0 4px ${C.accentMuted}`
    : `0 0 0 1px ${C.mediaScrim}, inset 0 0 0 1px ${C.mediaScrim}`;
  return (
    <button
      type="button"
      data-face-visual={visual}
      aria-label={faceAria(f.n, small, label?.name ?? null)}
      aria-pressed={visual === 'selected'}
      aria-disabled={small || undefined}
      aria-invalid={visual === 'error' || undefined}
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
        // hover (только десктоп): рамка C.accentSoft — у свободной и приглушённой
        ...(hover && (visual === 'normal' || visual === 'dim') ? { borderColor: C.accentSoft } : null),
      }}
    >
      <FaceBadge n={f.n} visual={visual} mobile={mobile} />
      {label && !small && <FaceNameLabel label={label} rect={rect} danger={visual === 'error'} mobile={mobile} />}
    </button>
  );
}

// Оверлей: контейнер absolute inset:0 (внутри контейнера картинки, как CropOverlay) + рамки.
// width/height — размеры картинки из ответа (для bbox→доли). labels — ярлыки назначенных лиц
// (этап 3); limitFace — лицо, вызвавшее плашку лимита
export function FaceOverlay({ faces, width, height, selectedFace, tappedSmall, errorFaces, labels, limitFace = null, mobile, onSelect }: {
  faces: readonly DetectedFace[];
  width: number;
  height: number;
  selectedFace: number | null;
  tappedSmall: number | null;
  errorFaces: readonly number[];
  labels?: ReadonlyMap<number, FaceLabel>;
  limitFace?: number | null;
  mobile: boolean;
  onSelect: (n: number) => void;
}) {
  const assigned = labels ? [...labels.keys()] : [];
  return (
    <div style={{ position: 'absolute', inset: 0 }} onPointerDown={e => e.stopPropagation()}>
      {faces.map(f => (
        <FaceBox key={f.n} f={f} rect={faceRect(f, width, height)} mobile={mobile}
          visual={faceVisual(f, { selectedFace, tappedSmall, errorFaces, assigned, limitFace })}
          label={labels?.get(f.n) ?? null}
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
