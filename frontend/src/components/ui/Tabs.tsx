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
}

const FOCUS_CLASS = 'cc-tab';
if (typeof document !== 'undefined' && !document.getElementById('cc-tab-style')) {
  const el = document.createElement('style');
  el.id = 'cc-tab-style';
  el.textContent = `.${FOCUS_CLASS}:focus-visible{outline:none;box-shadow:${SHADOW.focus};}`;
  document.head.appendChild(el);
}

export function Tabs<T extends string>({ value, items, onChange, ariaLabel, transparent }: {
  value: T;
  items: TabItem<T>[];
  onChange: (v: T) => void;
  ariaLabel?: string;
  transparent?: boolean;   // без своей подложки: на карточке шторки bgInset читается полосой
}) {
  const refs = useRef<(HTMLButtonElement | null)[]>([]);

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
              height: 32, padding: `0 ${SP.md}px`, border: 'none',
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
                fontSize: FS.xs, fontWeight: 600, color: C.textMuted,
                background: C.bgSelected, borderRadius: R.sm, padding: `0 ${SP.xs}px`,
              }}>
                {t.count}
              </span>
            )}
          </button>
        );
      })}
    </div>
  );
}
