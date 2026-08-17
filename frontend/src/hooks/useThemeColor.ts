import { useEffect } from 'react';

// Цвет заголовка окна браузера (meta[name="theme-color"]): Chromium красит им
// титлбар и полосу вкладок, на мобиле — ещё и статус-бар PWA. Вход — цвет из
// design.ts или конкретный hex: var(--c-*) резолвится через getComputedStyle,
// потому что meta не понимает CSS-переменные. Смена data-theme перевычисляет
// вход (например, акцент темы), поэтому слушаем её MutationObserver'ом.

// var(--c-accent) → #D97757 (или #E38A6A в тёмной); hex проходит как есть
function resolveCssColor(color: string): string {
  const m = /^var\((--[^),\s]+)\)$/.exec(color.trim());
  if (!m) return color;
  const value = getComputedStyle(document.documentElement).getPropertyValue(m[1]).trim();
  return value || color;
}

export function useThemeColor(color: string): void {
  useEffect(() => {
    const el = document.querySelector('meta[name="theme-color"]');
    if (!el) return;
    const apply = () => el.setAttribute('content', resolveCssColor(color));
    apply();
    const mo = new MutationObserver(apply);
    mo.observe(document.documentElement, { attributes: true, attributeFilter: ['data-theme'] });
    return () => mo.disconnect();
  }, [color]);
}
