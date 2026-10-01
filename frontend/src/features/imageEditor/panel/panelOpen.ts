// Открыта ли панель «Картинки» колонкой рабочей области: её тело смонтировано, только пока
// зона её показывает. По признаку сводка в полосе подсвечивается акцентной обводкой и
// подсказкой «Настройки открыты в панели «Картинки» справа».

import { useEffect, useSyncExternalStore } from 'react';

let shown = 0;
const listeners = new Set<() => void>();
const emit = () => listeners.forEach(fn => fn());

// Зовёт сама панель, пока стоит колонкой
export function useMarkImagesPanelShown(on: boolean) {
  useEffect(() => {
    if (!on) return;
    shown++;
    emit();
    return () => { shown--; emit(); };
  }, [on]);
}

const get = () => shown > 0;

export function useImagesPanelShown(): boolean {
  return useSyncExternalStore(
    fn => { listeners.add(fn); return () => { listeners.delete(fn); }; },
    get, get,
  );
}
