import { useRef } from 'react';
import type { LucideIcon } from 'lucide-react';
import { ICON_STROKE } from '../ui/icons';
import { InlineSegmented } from '../ui/InlineSegmented';
import { IconSegmented } from '../ui/IconSegmented';

// Переключатель режима панели генерации — один и тот же в полосе над полем ввода и
// в шапке панели («зеркало»): звук — Голос / Музыка / Обработка, картинки —
// Создать / Править. На широком экране сегменты с иконкой и подписью, на телефоне
// одни иконки (подпись уходит в подсказку).
//
// muted — режиму нужен источник, а его нет: сегмент приглушён, но не тупик —
// клик открывает меню выбора (onMutedClick с прямоугольником сегмента для якоря)
// вместо смены режима. Без onMutedClick приглушённый сегмент меняет режим как обычно.

export interface GenerationModeOption<T extends string> {
  value: T;
  label: string;
  icon: LucideIcon;
  muted?: boolean;
  // Подсказка вместо подписи: «Править — сначала выберите картинку»
  title?: string;
}

export interface ModeClickArgs<T extends string> {
  options: readonly GenerationModeOption<T>[];
  value: T;
  onChange: (v: T) => void;
  onMutedClick?: (v: T, anchor: DOMRect) => void;
  // Прямоугольник нажатого сегмента; считается лениво — только для меню
  anchor: () => DOMRect;
}

// Куда уходит клик по сегменту — отдельно от разметки, чтобы проверять без DOM
export function handleModeClick<T extends string>({ options, value, onChange, onMutedClick, anchor }: ModeClickArgs<T>): void {
  const opt = options.find(o => o.value === value);
  if (opt?.muted && onMutedClick) { onMutedClick(value, anchor()); return; }
  onChange(value);
}

export function GenerationModeSwitch<T extends string>({ value, options, onChange, onMutedClick, compact, isMobile, quiet, persistKey }: {
  value: T;
  options: readonly GenerationModeOption<T>[];
  onChange: (v: T) => void;
  onMutedClick?: (v: T, anchor: DOMRect) => void;
  // Одни иконки — телефон 360 и тесные места
  compact?: boolean;
  isMobile?: boolean;
  // Тихий вид иконок (полоса над полем ввода), см. IconSegmented
  quiet?: boolean;
  persistKey?: string;
}) {
  const box = useRef<HTMLSpanElement>(null);
  const click = (v: T) => handleModeClick({
    options, value: v, onChange, onMutedClick,
    anchor: () => {
      const i = options.findIndex(o => o.value === v);
      const btn = box.current?.querySelectorAll('button')[i];
      return (btn ?? box.current)?.getBoundingClientRect() ?? new DOMRect();
    },
  });
  return (
    <span ref={box} style={{ display: 'inline-flex', flexShrink: 0 }}>
      {compact ? (
        <IconSegmented<T>
          value={value}
          onChange={click}
          quiet={quiet}
          persistKey={persistKey}
          options={options.map(({ value: v, label, icon: Icon, muted, title }) => ({
            value: v, label, muted, title, icon: <Icon size={14} strokeWidth={ICON_STROKE} />,
          }))}
        />
      ) : (
        <InlineSegmented<T>
          value={value}
          onChange={click}
          isMobile={isMobile}
          options={options.map(({ value: v, label, icon: Icon, muted, title }) => ({
            value: v, label, muted, title, icon: <Icon size={12} strokeWidth={ICON_STROKE} />,
          }))}
        />
      )}
    </span>
  );
}
