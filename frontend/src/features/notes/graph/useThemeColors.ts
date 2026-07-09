import { useEffect, useMemo, useState } from 'react';

// Canvas 2D не понимает 'var(--c-*)' из design.ts: резолвим CSS-переменные в
// конкретные значения через getComputedStyle. Смена темы переключает атрибут
// data-theme на <html> — ловим её MutationObserver'ом, сбрасываем кэш и
// поднимаем version, чтобы потребители перечитали цвета.
export interface ThemeColors {
  version: number;
  resolve: (color: string) => string;
}

export function useThemeColors(): ThemeColors {
  const [version, setVersion] = useState(0);

  useEffect(() => {
    const mo = new MutationObserver(() => setVersion(v => v + 1));
    mo.observe(document.documentElement, { attributes: true, attributeFilter: ['data-theme'] });
    return () => mo.disconnect();
  }, []);

  return useMemo(() => {
    const cache = new Map<string, string>();
    const resolve = (color: string): string => {
      const hit = cache.get(color);
      if (hit) return hit;
      const m = /^var\((--[^),\s]+)\)$/.exec(color.trim());
      const value = m
        ? getComputedStyle(document.documentElement).getPropertyValue(m[1]).trim() || '#888888'
        : color;
      cache.set(color, value);
      return value;
    };
    return { version, resolve };
  }, [version]);
}
