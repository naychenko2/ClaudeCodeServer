import { useRef } from 'react';
import type { KeyboardEvent, ReactNode } from 'react';
import { C, FS, SP, R, SHADOW, FONT } from '../../lib/design';

// Вкладки боковой панели генерации («Настройки | Голоса», «Настройки | Персонажи»).
// Не путать с SegmentedControl/IconSegmented: те выбирают ЗНАЧЕНИЕ, а вкладки
// переключают содержимое под собой — отсюда tablist/tab и навигация стрелками.
// Подчёркивание активной вкладки — по макету docs/mockups/image-editor-v4-panel.html.

export interface TabItem<T extends string> {
  value: T;
  label: string;
  icon?: ReactNode;
  count?: number;        // счётчик рядом с подписью («Голоса 12»)
  // Точка-индикатор «ждёт вашего ответа» (6, C.info) — после счётчика
  // (страница фичи, волна 3; макет R-2).
  dot?: boolean;
}

const FOCUS_CLASS = 'cc-tab';
if (typeof document !== 'undefined' && !document.getElementById('cc-tab-style')) {
  const el = document.createElement('style');
  el.id = 'cc-tab-style';
  el.textContent = `.${FOCUS_CLASS}:focus-visible{outline:none;box-shadow:${SHADOW.focus};}`;
  document.head.appendChild(el);
}

// Размер вкладки: 'sm' — прежний (32, панель генерации); 'md' — страница фичи
// (волна 3, макет R-1: 38, на мобиле 44; поля 0 10 / 0 12). Размер живёт в ките,
// не литералом на странице.
export type TabsSize = 'sm' | 'md';

export function Tabs<T extends string>({ value, items, onChange, ariaLabel, transparent, size = 'sm', isMobile = false, className }: {
  value: T;
  items: TabItem<T>[];
  onChange: (v: T) => void;
  ariaLabel?: string;
  transparent?: boolean;   // без своей подложки: на карточке шторки bgInset читается полосой
  size?: TabsSize;
  isMobile?: boolean;
  // Класс полосы (tablist): вызывающий может отключить видимую полосу прокрутки
  // (страница фичи: горизонтальная прокрутка «без полосы»).
  className?: string;
}) {
  const refs = useRef<(HTMLButtonElement | null)[]>([]);
  // Геометрия по размеру: 'md' — выше и с полями макета (10/12).
  const tabHeight = size === 'md' ? (isMobile ? 44 : 38) : 32;
  const tabPadX = size === 'md' ? (isMobile ? 12 : 10) : SP.md;

  // Стрелки двигают выбор по кругу и переносят фокус на новую вкладку
  // (автоматическая активация по WAI-ARIA: содержимое лёгкое, ждать Enter незачем).
  const onKeyDown = (e: KeyboardEvent, i: number) => {
    let next = -1;
    if (e.key === 'ArrowRight') next = (i + 1) % items.length;
    else if (e.key === 'ArrowLeft') next = (i - 1 + items.length) % items.length;
    else if (e.key === 'Home') next = 0;
    else if (e.key === 'End') next = items.length - 1;
    if (next < 0) return;
    e.preventDefault();
    onChange(items[next].value);
    refs.current[next]?.focus();
  };

  return (
    <div
      role="tablist"
      aria-label={ariaLabel}
      className={className}
      style={{
        display: 'flex', gap: SP.xxs, padding: `${SP.xs}px ${SP.sm}px 0`,
        background: transparent ? 'transparent' : C.bgInset, borderBottom: `1px solid ${C.borderLight}`,
        overflowX: 'auto',
      }}
    >
      {items.map((t, i) => {
        const active = t.value === value;
        return (
          <button
            key={t.value}
            ref={el => { refs.current[i] = el; }}
            type="button"
            role="tab"
            aria-selected={active}
            tabIndex={active ? 0 : -1}
            className={FOCUS_CLASS}
            onClick={() => onChange(t.value)}
            onKeyDown={e => onKeyDown(e, i)}
            style={{
              display: 'inline-flex', alignItems: 'center', gap: SP.xs + 2, flexShrink: 0,
              height: tabHeight, padding: `0 ${tabPadX}px`, border: 'none',
              borderBottom: `2px solid ${active ? C.accent : 'transparent'}`,
              borderRadius: `${R.sm}px ${R.sm}px 0 0`,
              background: 'transparent', cursor: 'pointer', fontFamily: FONT.sans,
              fontSize: FS.base, fontWeight: active ? 600 : 400,
              color: active ? C.accent : C.textSecondary,
              transition: 'color 0.12s, border-color 0.12s',
            }}
          >
            {t.icon}
            {t.label}
            {t.count !== undefined && (
              <span style={{
                // 'md' (страница фичи): активный счётчик — accent; 'sm' (панель генерации) — как раньше (muted).
                fontSize: FS.xs, fontWeight: 600, color: size === 'md' && active ? C.accent : C.textMuted,
                background: C.bgSelected, borderRadius: R.sm, padding: `0 ${SP.xs}px`,
              }}>
                {t.count}
              </span>
            )}
            {t.dot && (
              <span
                aria-label="ждёт вашего ответа"
                style={{ width: 6, height: 6, borderRadius: R.full, background: C.info, flexShrink: 0 }}
              />
            )}
          </button>
        );
      })}
    </div>
  );
}
