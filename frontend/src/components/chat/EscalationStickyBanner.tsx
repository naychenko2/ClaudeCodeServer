// Закреплённая полоса «практика ждёт вашего решения» над композером.
// Карточка остановки — обычный элемент ленты: её уносит вверх потоком докладов
// исполнителей, и человек её не замечает (прод 25.08, чат «Удаление чатов с архивом»,
// практика молча стояла 40+ минут, пока человек не написал в чат случайное сообщение).
// Предикат — из lib/teamImplement: открытая карточка = team_escalation && !resolved.
// Полоса повторяет самую свежую из открытых, счётчик остальных — справа.
//
// Тон берётся из teamEscalationTone: warning/success/muted/work — единая палитра
// с карточкой остановки, чтобы баннер и его цель читались как одна история.
// При клике «К карточке» — прокрутка ленты к data-feed-index с мягкой подсветкой
// (мигание рамки 1.5с), без ремаунта/мутаций в items.

import { useCallback, useEffect, useRef } from 'react';
import { AlertTriangle, ChevronUp } from 'lucide-react';
import type { ChatItem } from '../../types';
import { C, FS, R, SHADOW } from '../../lib/design';
import { teamEscalationTone } from '../../lib/teamImplement';

interface OpenEscalation {
  item: Extract<ChatItem, { kind: 'team_escalation' }>;
  idx: number;
}

function isOpenEscalation(it: ChatItem, i: number): OpenEscalation | null {
  if (it.kind !== 'team_escalation') return null;
  if (it.escalation.resolved) return null;
  return { item: it, idx: i };
}

export function findOpenEscalations(items: readonly ChatItem[]): OpenEscalation[] {
  const out: OpenEscalation[] = [];
  for (let i = 0; i < items.length; i++) {
    const m = isOpenEscalation(items[i], i);
    if (m) out.push(m);
  }
  return out;
}

export function EscalationStickyBanner({
  top, others, onJump,
}: {
  // Самая свежая открытая карточка (последняя в ленте по индексу)
  top: OpenEscalation;
  // Сколько ещё открытых помимо неё
  others: number;
  // Прыжок к карточке в ленте: скролл + мягкая подсветка
  onJump: (idx: number) => void;
}) {
  const flashRef = useRef<number | null>(null);

  // Мягкая подсветка: при добавлении класса на 1.5с элемент мигает рамкой.
  // На смене top.idx (новая «самая свежая») повторяем эффект — иначе человек кликал
  // бы в баннер и не видел реакции, если карточка за пределами видимой области
  useEffect(() => {
    const idx = top.idx;
    const node = document.querySelector<HTMLElement>(`[data-feed-index="${idx}"]`);
    if (!node) return;
    node.classList.add('escalation-flash');
    flashRef.current = window.setTimeout(() => {
      node.classList.remove('escalation-flash');
      flashRef.current = null;
    }, 1500);
    return () => {
      if (flashRef.current !== null) {
        window.clearTimeout(flashRef.current);
        flashRef.current = null;
      }
      node.classList.remove('escalation-flash');
    };
  }, [top.idx]);

  const handleJump = useCallback(() => {
    // Прокручиваем ПОСЛЕ снятия подсветки из прошлого прыжка: иначе кратковременный
    // класс зависает на старой карточке, а новая мигает «поверх» старого стиля
    onJump(top.idx);
  }, [onJump, top.idx]);

  const tone = teamEscalationTone(top.item.escalation.kind);
  const palette = bannerPalette(tone);
  const title = top.item.escalation.title;

  return (
    <button
      type="button"
      data-testid="escalation-sticky-banner"
      onClick={handleJump}
      style={{
        display: 'flex',
        alignItems: 'center',
        gap: 10,
        width: '100%',
        textAlign: 'left',
        padding: '8px 12px',
        margin: '0 0 6px',
        border: `1px solid ${palette.border}`,
        borderRadius: R.lg,
        background: palette.bg,
        color: palette.text,
        cursor: 'pointer',
        boxShadow: SHADOW.card,
        font: 'inherit',
        fontFamily: 'inherit',
      }}
    >
      <AlertTriangle
        size={14}
        strokeWidth={2.2}
        color={palette.text}
        style={{ flexShrink: 0 }}
      />
      <span style={{
        flex: 1,
        minWidth: 0,
        fontSize: FS.sm,
        lineHeight: 1.4,
        color: palette.text,
        overflow: 'hidden',
        textOverflow: 'ellipsis',
        whiteSpace: 'nowrap',
      }}>
        <span style={{ fontWeight: 600 }}>Практика ждёт вашего решения: </span>
        <span>{title}</span>
      </span>
      {others > 0 && (
        <span style={{
          fontSize: FS.xs,
          color: palette.text,
          opacity: 0.75,
          whiteSpace: 'nowrap',
          flexShrink: 0,
        }}>
          ещё {others}
        </span>
      )}
      <span style={{
        display: 'inline-flex',
        alignItems: 'center',
        gap: 4,
        fontSize: FS.xs,
        fontWeight: 600,
        color: C.onAccent,
        background: palette.ctaBg,
        padding: '4px 10px',
        borderRadius: R.md,
        flexShrink: 0,
      }}>
        К карточке
        <ChevronUp size={12} strokeWidth={2.4} />
      </span>
    </button>
  );
}

interface BannerPalette {
  bg: string;
  text: string;
  border: string;
  ctaBg: string;
}

// Палитра полосы — по тону карточки остановки (та же логика, что у TeamEscalationView):
// warning ждёт решения, success — гейт волны, muted — пауза, work — добавочная волна.
// Берём фоны и тексты из дизайн-токенов (warning/danger/success), чтобы баннер и его
// цель визуально совпадали
function bannerPalette(tone: ReturnType<typeof teamEscalationTone>): BannerPalette {
  switch (tone) {
    case 'success':
      return {
        bg: C.successBg,
        text: C.successText,
        border: 'transparent',
        ctaBg: C.successText,
      };
    case 'muted':
      return {
        bg: C.bgPanel,
        text: C.textSecondary,
        border: C.border,
        ctaBg: C.accent,
      };
    case 'work':
      return {
        bg: C.accentSoft,
        text: C.textHeading,
        border: 'transparent',
        ctaBg: C.accent,
      };
    case 'warning':
    default:
      return {
        bg: C.warningBg,
        text: C.warningText,
        border: 'transparent',
        ctaBg: C.warningText,
      };
  }
}
