import { useEffect, useState } from 'react';

// Перетаскиваемая ширина панели (пара к ui/Splitter): персист в localStorage, клампы.
// rightSide — панель справа: тянем влево → ширина растёт (как артефакты в Workspace).
// Хук вынесен из компонентного shared.tsx: экспорт хука рядом с компонентом ломает
// fast refresh (см. eslint.config.js, примечание к react-refresh/only-export-components).
export function usePanelWidth(storageKey: string, def: number, min: number, max: number, rightSide = false) {
  const [width, setWidth] = useState(() => {
    const v = localStorage.getItem(storageKey);
    return v ? Math.max(min, Math.min(max, Number(v))) : def;
  });
  useEffect(() => { localStorage.setItem(storageKey, String(width)); }, [width, storageKey]);
  const [dragging, setDragging] = useState(false);

  const startDrag = (e: React.PointerEvent) => {
    e.preventDefault();
    setDragging(true);
    const startX = e.clientX;
    const startW = width;
    const onMove = (ev: PointerEvent) => {
      const d = ev.clientX - startX;
      setWidth(Math.max(min, Math.min(max, rightSide ? startW - d : startW + d)));
    };
    const onUp = () => {
      document.removeEventListener('pointermove', onMove);
      document.removeEventListener('pointerup', onUp);
      document.body.style.cursor = '';
      document.body.style.userSelect = '';
      setDragging(false);
    };
    document.body.style.cursor = 'col-resize';
    document.body.style.userSelect = 'none';
    document.addEventListener('pointermove', onMove);
    document.addEventListener('pointerup', onUp);
  };

  return [width, dragging, startDrag] as const;
}
