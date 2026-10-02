// Поднята ли шторка панели генерации (GenerationPanel, вид sheet). Пока поднята, оболочка
// уступает ей экран: плавающий аватар AI-хаба прячется, а тосты уходят под её слой —
// на 88 % высоты шапка шторки как раз в зоне тостов (дизайн-ревью панели «Картинки»).

import { useSyncExternalStore } from 'react';

let raised = 0;
const listeners = new Set<() => void>();
const emit = () => listeners.forEach(fn => fn());

// Шторка поднялась; возвращает снятие. Счётчик, а не флаг: снятие одной не гасит другую
export function holdGenSheetRaised(): () => void {
  raised++;
  emit();
  let done = false;
  return () => {
    if (done) return;
    done = true;
    raised--;
    emit();
  };
}

export const isGenSheetRaised = () => raised > 0;

export function useGenSheetRaised(): boolean {
  return useSyncExternalStore(
    fn => { listeners.add(fn); return () => { listeners.delete(fn); }; },
    isGenSheetRaised, isGenSheetRaised,
  );
}
