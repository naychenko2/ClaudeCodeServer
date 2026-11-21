// Управляемый примитив раскрытия (v1 «Сначала ферма»): строка-заголовок с припиской
// и chevron плюс тело, которое остаётся смонтированным и скрывается через display —
// закрытие не останавливает опросы, живущие внутри тела (тот же смысл, что в
// AccordionSection). Состояние и его хранение держит потребитель: open/onToggle —
// его, внутри примитива нет ни useState для раскрытия, ни localStorage.
//
// Макет: docs/mockups/local-model-llm-redesign-v1.html (строки 194–214, 238–240);
// спека: docs/mockups/local-model-llm-redesign-proposal.md, §v1 и «Состояния всех вариантов».

import { useId, useState } from 'react';
import type { ReactNode } from 'react';
import { ChevronDown } from 'lucide-react';
import { C, FS, R, SHADOW, SP } from '../../lib/design';
import { ICON_STROKE } from './icons';
import { usePrefersReducedMotion } from '../../hooks/usePrefersReducedMotion';

// Кольцо фокуса с клавиатуры и отклик нажатия — через :focus-visible/:active
// (паттерн ChatCard/FeaturePageHeader): инлайн-стиль не различает таб и мышь,
// а :active инлайном не выставить вовсе.
const HEAD_CLASS = 'cc-disclosure-head';
if (typeof document !== 'undefined' && !document.getElementById('cc-disclosure-style')) {
  const el = document.createElement('style');
  el.id = 'cc-disclosure-style';
  el.textContent =
    `.${HEAD_CLASS}:focus-visible{outline:none;box-shadow:${SHADOW.focus};}` +
    `.${HEAD_CLASS}:active{box-shadow:${SHADOW.press};}`;
  // head ?? body: fake-DOM тестов не всегда несёт head — не роняем импорт.
  (document.head ?? document.body).appendChild(el);
}

export interface DisclosureSectionProps {
  title: string;
  // Приписка справа от названия: «Локальная модель · TTFT · медианы», «3 модели · за сутки».
  // Длинная приписка переносится на вторую строку, а не обрезается (решение Майи 21.11):
  // полный смысл обязан читаться и в узкой колонке, и на мобиле.
  meta?: string;
  // Управляемое состояние: его держит потребитель (в v1 — localStorage панели).
  open: boolean;
  onToggle: () => void;
  isMobile: boolean;
  // Нет содержимого — нет и раскрытия: статичная закрытая строка (пустое облачное
  // окно), без кнопки, chevron и пустого тела.
  children?: ReactNode;
}

export function DisclosureSection({ title, meta, open, onToggle, isMobile, children }: DisclosureSectionProps) {
  const reactId = useId();
  const headId = `${reactId}-head`;
  const bodyId = `${reactId}-body`;
  const [hovered, setHovered] = useState(false);
  const reducedMotion = usePrefersReducedMotion();

  // Высота строки-заголовка: 40 на десктопе, 44 на мобильной шторке (цель пальцем,
  // как .sheet summary в макете).
  const headH = isMobile ? 44 : 40;
  // Hover — только десктоп: на тачевом листе строка и так tap-зона 44px.
  const showHover = !isMobile && hovered;

  if (children == null) {
    return (
      <div style={shell}>
        <div style={{ ...headBase, minHeight: headH, cursor: 'default' }}>
          {titleSpan(title)}
          {metaSpan(meta)}
        </div>
      </div>
    );
  }

  return (
    <div style={shell}>
      <button
        type="button"
        id={headId}
        className={HEAD_CLASS}
        aria-expanded={open}
        aria-controls={bodyId}
        onClick={onToggle}
        onMouseEnter={() => setHovered(true)}
        onMouseLeave={() => setHovered(false)}
        style={{
          ...headBase,
          minHeight: headH,
          cursor: 'pointer',
          background: showHover ? C.bgSelected : 'transparent',
          transition: 'background 0.12s',
        }}
      >
        {titleSpan(title)}
        {metaSpan(meta)}
        {/* Поворот на месте: фиксированный квадрат иконки и transform — соседи не сдвигаются;
            при prefers-reduced-motion поворот остаётся, гасится только переход. */}
        <ChevronDown
          size={14}
          strokeWidth={ICON_STROKE}
          style={{
            marginLeft: 'auto', flexShrink: 0, color: C.textMuted,
            transform: open ? 'rotate(180deg)' : 'none',
            transition: reducedMotion ? 'none' : 'transform 0.15s',
          }}
        />
      </button>
      {/* Тело смонтировано всегда: скрытие display — чтобы закрытая секция продолжала
        свой опрос и открылась уже с данными. */}
      <div
        id={bodyId}
        role="region"
        aria-labelledby={headId}
        style={{
          display: open ? 'block' : 'none',
          borderTop: `1px solid ${C.borderLight}`,
          padding: `0 ${SP.md}px ${SP.md}px`,
        }}
      >
        {children}
      </div>
    </div>
  );
}

const shell = {
  background: C.bgWhite,
  border: `1px solid ${C.borderLight}`,
  borderRadius: R.xl,
  overflow: 'hidden',
} as const;

const headBase = {
  display: 'flex', alignItems: 'center', gap: SP.sm,
  width: '100%', padding: `${SP.sm}px ${SP.md}px`,
  border: 'none', textAlign: 'left', fontFamily: 'inherit',
} as const;

// Инвариант строки раскрытия (R-Ф-6): смысл читается самими её словами — заголовок
// целое, приписка переносится. title не усечём: при flex '0 1 auto' усечение
// делилось бы по flex-basis (auto = ширина однострочного содержимого), и заголовок
// терял бы долю ширины даже когда целиком помещается — «Точные метрик…» при полной
// приписке (дефект, закрытый Ф-6). flexShrink: 0: заголовок держит свою ширину;
// вся нехватка строки — на перенос приписки (metaSpan ниже). Longhand вместо
// шортхенда flex: так же и в браузере (basis по умолчанию auto), но читается тестом.
const titleSpan = (title: string) => (
  <span style={{ flexGrow: 0, flexShrink: 0, fontSize: FS.sm, fontWeight: 600, color: C.textHeading }}>
    {title}
  </span>
);

// Приписка — перенос по словам, без многоточия: flexShrink: 0 и nowrap давали ей
// собственную ширину и обрезку, когда строка не помещалась (дефект b45ceaa6).
// minWidth: 0 обязателен: без него flex-элемент не сужается ниже содержимого и
// переноса не происходит вовсе.
const metaSpan = (meta?: string) => (meta ? (
  <span
    title={meta}
    style={{
      flex: '1 1 auto', minWidth: 0,
      fontSize: FS.xs, fontWeight: 400, color: C.textMuted,
      whiteSpace: 'normal', overflowWrap: 'break-word',
    }}
  >
    {meta}
  </span>
) : null);
