// Ушко «Продолжить в новом чате» (вариант 2 · v2.2, спецификация
// docs/design/mockups/continue-hint-v2.md «Спецификация 2 · Ушко»): закладка у правого края
// верхней кромки. Не новая форма — язык композера: фон, рамка и тень основания (губы или поля
// ввода), по бокам вогнутые переходы, как у вкладок браузера. Низ ушка слит с основанием.
//
// Основание задаёт контекст LipEarBaseContext (ContextRowView знает, есть ли губа: showsRow):
//   'lip'   — есть губа: фон/рамка губы (C.bgMain / C.borderLight), zIndex 1;
//   'field' — губы нет: ушко растёт из кромки самого поля ввода, фон/рамка карточки
//             (C.bgWhite / C.border), zIndex 3 (выше карточки, у неё 2).
// По умолчанию — 'lip' (витрина UiKitPage показывает оба варианта явным провайдером).
import { createContext, useContext, type CSSProperties } from 'react';
import { ArrowRight, Gauge } from 'lucide-react';
import { C, FS, R, SP, SHADOW } from '../../lib/design';
import { ICON_SIZE, ICON_STROKE } from '../ui/icons';
import { formatContextK } from '../../lib/chatContinue';
import { RowNoticeAction, RowNoticeSnooze } from './RowNotice';

export const LipEarBaseContext = createContext<'lip' | 'field'>('lip');

// Геометрия ушка: высота 23 (десктоп) / 31 (мобила) = COMPOSER_LIP.rowMini (20) + edgeTab (2)
// + рамка 1 сверху; правый отступ R.xxl + SP.sm (22 px) — вогнутый переход ложится на прямую
// часть кромки, а не на скругление угла (оба R.xxl). Токены — по спецификации.
const EAR_H = { desk: 23, mob: 31 };
const EAR_RIGHT = R.xxl + SP.sm; // 22 px
// Вогнутый переход: квадрат SP.sm, дуга радиуса SP.sm; кольцо 1 px — рамка, снаружи — фон
// основания. Рамка ушка плавно уходит в верхнюю рамку основания, наклейки нет.
const FL = SP.sm; // 8
const FL_OFF = -(FL + 1);

export function LipEar({ tokens, onOpen, onSnooze, isMobile }: {
  tokens: number;
  onOpen: () => void;
  onSnooze: () => void;
  isMobile?: boolean;
}) {
  const base = useContext(LipEarBaseContext);
  const onLip = base === 'lip';
  // Фон/рамка основания: губа — C.bgMain/C.borderLight, поле ввода — C.bgWhite/C.border
  const bg = onLip ? C.bgMain : C.bgWhite;
  const bd = onLip ? C.borderLight : C.border;
  const num = formatContextK(tokens);

  const ear: CSSProperties = {
    position: 'relative',
    zIndex: onLip ? 1 : 3,
    alignSelf: 'flex-end',
    display: 'flex', alignItems: 'center', gap: SP.sm - 2,
    boxSizing: 'border-box',
    height: isMobile ? EAR_H.mob : EAR_H.desk,
    maxWidth: 'calc(100% - 40px)',
    margin: `0 ${EAR_RIGHT}px -1px 0`,
    padding: `0 ${SP.xs}px 0 ${SP.sm}px`,
    background: bg,
    border: `1px solid ${bd}`, borderBottom: 'none',
    borderRadius: `${R.lg}px ${R.lg}px 0 0`,
    boxShadow: SHADOW.liftSoft,
    fontSize: FS.sm, color: C.textSecondary,
  };

  // Полоска стыка: закрывает верхнюю рамку основания и тень под ушком
  const seam: CSSProperties = {
    position: 'absolute', left: 0, right: 0, bottom: -4, height: 5,
    background: bg,
  };

  // Вогнутые переходы (как у вкладок браузера): левый и правый
  const flLeft: CSSProperties = {
    position: 'absolute', bottom: 0, left: FL_OFF, width: FL, height: FL, pointerEvents: 'none',
    background: `radial-gradient(circle at 0 0, transparent ${FL - 1}px, ${bd} ${FL - 1}px, ${bd} ${FL}px, ${bg} ${FL}px)`,
  };
  const flRight: CSSProperties = {
    position: 'absolute', bottom: 0, right: FL_OFF, width: FL, height: FL, pointerEvents: 'none',
    background: `radial-gradient(circle at 100% 0, transparent ${FL - 1}px, ${bd} ${FL - 1}px, ${bd} ${FL}px, ${bg} ${FL}px)`,
  };

  // Текст: число — заголовочный полужирный, остальное — вторичный. Без flex:1 — ушко по
  // ширине содержимого; minWidth:0 + ellipsis дают сжатие, если maxWidth упрётся.
  const text: CSSProperties = { minWidth: 0, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' };

  return (
    <div
      data-testid="continue-hint"
      role="status"
      title={`Контекст ${num} — дешевле продолжить в новом чате`}
      style={ear}
    >
      <span aria-hidden="true" style={flLeft} />
      <span aria-hidden="true" style={flRight} />
      <span aria-hidden="true" style={seam} />
      <span style={{ display: 'inline-flex', alignItems: 'center', flexShrink: 0, color: C.textMuted }}>
        <Gauge size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
      </span>
      <span style={text}>
        <b style={{ color: C.textHeading, fontWeight: 600 }}>{num}</b>
        {isMobile ? '' : ' · дешевле в новом чате'}
      </span>
      <RowNoticeAction
        label={isMobile ? 'В новый чат' : 'Продолжить'}
        onClick={onOpen}
        testId="continue-hint-open"
        iconAfter={<ArrowRight size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />}
        isMobile={isMobile}
      />
      <RowNoticeSnooze
        onClick={onSnooze}
        title="Не сейчас — напомню, когда вырастет ещё на 100K"
        ariaLabel="Не сейчас"
        testId="continue-hint-snooze"
        isMobile={isMobile}
        size={isMobile ? { w: 32, h: 28 } : { w: 20, h: 20 }}
      />
    </div>
  );
}
