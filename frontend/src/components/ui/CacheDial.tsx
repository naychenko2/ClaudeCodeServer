// Циферблат кэша промпта (фича «Индикатор prompt cache и перенос в новый чат», шаг F-1b).
// Кухонный таймер 14×14 (ICON_SIZE.xs) справа от кольца контекста в той же пилюле: сектор
// показывает, сколько кэшу осталось жить, стрелка — сколько прошло (по часовой, к остыванию
// доходит до 12). Макет и состояния — docs/mockups/prompt-cache-dial-v1.{html,md}.
// Состояние берётся из хука usePromptCache — компонент ничего не считает сам, только рисует.
// Своей кнопки нет: цель клика — вся пилюля (BadgeShell). Нет состояния — не рисуется ни
// циферблат, ни разделитель: блок появляется и исчезает целиком.
import { type ReactNode } from 'react';
import { usePrefersReducedMotion } from '../../hooks/usePrefersReducedMotion';
import { C, FONT, FS, SP } from '../../lib/design';
import { ICON_SIZE } from './icons';
import { type PromptCacheState } from '../../lib/promptCache';

// Геометрия рисуется в координатах viewBox 20×20 (как у RingIcon): контур r 8.5, сектор r 7,
// стрелка r 6.5, ось r 1.3. Один и тот же циферблат — на пилюле (14 px) и пипом строки
// поповера (12 px).
const SR = 7; // радиус сектора остатка
const HR = 6.5; // длина стрелки

// Состояние циферблата = состояние ленты, либо «идёт ход»: якорь вот-вот сдвинется,
// поэтому остаток во время хода не показывается нигде.
type DialState = PromptCacheState['status'] | 'turn';

const COLOR: Record<DialState, string> = {
  warm: C.success,
  cooling: C.warning,
  cold: C.danger,
  turn: C.textMuted,
};

// «N мин», округление вверх; «1 мин» держится до самого остывания (cacheLeftText из origin)
export function cacheLeftText(remainingMs: number): string {
  return `${Math.max(1, Math.ceil(remainingMs / 60_000))} мин`;
}

// Строка кэша для подсказки и aria-label пилюли — смысл без цвета (цвет не единственный носитель)
export function cacheSpoken(state: PromptCacheState, isWaiting: boolean): string {
  if (isWaiting) return 'Кэш · идёт ход';
  if (state.status === 'cold') return 'Кэш остыл';
  return `${state.status === 'cooling' ? 'Кэш скоро остынет' : 'Кэш тёплый'} · ${cacheLeftText(state.remainingMs)}`;
}

// Доля оставшегося срока: remainingMs / (expiresAt − anchorAtMs)
function dialFrac(state: PromptCacheState): number {
  const total = Math.max(1, state.expiresAt - state.anchorAtMs);
  return Math.min(1, Math.max(0, state.remainingMs / total));
}

// Цвета — только через style: C.* — это var(--…), а в SVG-атрибутах CSS-переменные не резолвятся.
function DialSvg({ st, frac, size, reducedMotion }: {
  st: DialState; frac: number; size: number; reducedMotion: boolean;
}) {
  const c = COLOR[st];
  // Стрелка стоит на прошедшей доле и идёт по часовой от 12; сектор остатка — от стрелки
  // по часовой до 12.
  const a = 2 * Math.PI * (1 - frac);
  const pt = (r: number) => `${(10 + r * Math.sin(a)).toFixed(2)} ${(10 - r * Math.cos(a)).toFixed(2)}`;
  let sector: ReactNode = null;
  if ((st === 'warm' || st === 'cooling') && frac > 0) {
    // «Дыхание» сектора — только у остывающего и только без reduced motion; вся пилюля не мигает
    const style: React.CSSProperties = {
      fill: c,
      fillOpacity: 0.35,
      ...(st === 'cooling' && !reducedMotion ? { animation: 'cc-cache-breathe 1.6s ease-in-out infinite' } : {}),
    };
    sector = frac >= 0.999
      ? <circle cx="10" cy="10" r={SR} style={style} />
      : <path d={`M10 10 L${pt(SR)} A${SR} ${SR} 0 ${frac > 0.5 ? 1 : 0} 1 10 ${10 - SR} Z`} style={style} />;
  }
  // Холодный и «идёт ход»: стрелка на 12 — срок истёк / якорь вот-вот сдвинется
  const hand = st === 'turn' || st === 'cold' ? `10 ${10 - HR}` : pt(HR);
  return (
    <svg width={size} height={size} viewBox="0 0 20 20" aria-hidden shapeRendering="geometricPrecision"
      style={{ display: 'block', flexShrink: 0 }}>
      <circle cx="10" cy="10" r="8.5" fill="none" strokeWidth={1.8}
        strokeDasharray={st === 'cold' ? '3 2.2' : undefined} style={{ stroke: c }} />
      {sector}
      {/* При reduced motion — без вращения: у хода остаётся неподвижная серая стрелка на 12 */}
      <g style={{ transformOrigin: '10px 10px', animation: st === 'turn' && !reducedMotion ? 'cc-spin 2.4s linear infinite' : undefined }}>
        <path d={`M10 10 L${hand}`} strokeWidth={1.8} strokeLinecap="round" style={{ stroke: c }} />
      </g>
      <circle cx="10" cy="10" r="1.3" style={{ fill: c }} />
    </svg>
  );
}

// Пип строки «Осталось» в поповере — тот же циферблат размером 12
export function CacheDialPip({ state, isWaiting }: { state: PromptCacheState; isWaiting: boolean }) {
  return <DialSvg st={isWaiting ? 'turn' : state.status} frac={dialFrac(state)} size={12} reducedMotion={usePrefersReducedMotion()} />;
}

// Циферблат на пилюле: разделитель + таймер + текст. Текст — только на десктопе и планшете
// (на мобиле шапка не резиновая, минуты и «остыл» — в поповере). Возвращает null, когда
// состояния нет: разделитель тоже не рисуется.
export function CacheDial({ state, isWaiting, isMobile }: {
  state: PromptCacheState | null; isWaiting: boolean; isMobile?: boolean;
}) {
  if (!state) return null;
  const st: DialState = isWaiting ? 'turn' : state.status;
  return (
    <span style={{ display: 'flex', alignItems: 'center', gap: SP.xs }}>
      <span aria-hidden style={{ width: 1, height: 12, background: C.border, margin: `0 ${SP.xxs}px`, flexShrink: 0 }} />
      {/* Высота пилюли не растёт: svg обёрнут в span нулевой высоты, как RingIcon */}
      <span style={{ display: 'flex', alignItems: 'center', height: 0, flexShrink: 0 }}>
        <DialSvg st={st} frac={dialFrac(state)} size={ICON_SIZE.xs} reducedMotion={usePrefersReducedMotion()} />
      </span>
      {st === 'cooling' && !isMobile && (
        <span style={{ fontFamily: FONT.mono, fontSize: FS.sm, fontWeight: 700, color: C.warningText }}>
          {cacheLeftText(state.remainingMs)}
        </span>
      )}
      {st === 'cold' && !isMobile && (
        <span style={{ fontFamily: FONT.mono, fontSize: FS.sm, fontWeight: 700, color: C.dangerText }}>остыл</span>
      )}
    </span>
  );
}
