// Глобальный индикатор «AI-хаб занят»: действия из палитры/подсказок (краткое содержание,
// выжимка, описание базы, оценка задачи…) выполняются в разных компонентах, а показать
// процесс нужно на одной кнопке AI-хаба. Компоненты оборачивают свою async-работу в
// withAiBusy, а AiLauncher подписывается через useAiBusy и рисует круговой индикатор.

import { useSyncExternalStore } from 'react';

let count = 0;
const listeners = new Set<() => void>();
const emit = () => listeners.forEach(l => l());

export function beginAiBusy(): void { count++; emit(); }
export function endAiBusy(): void { count = Math.max(0, count - 1); emit(); }

// Обернуть async-работу действия: индикатор включается на время выполнения.
export async function withAiBusy<T>(fn: () => Promise<T>): Promise<T> {
  beginAiBusy();
  try { return await fn(); }
  finally { endAiBusy(); }
}

function subscribe(cb: () => void): () => void {
  listeners.add(cb);
  return () => { listeners.delete(cb); };
}
const isBusy = () => count > 0;

export function useAiBusy(): boolean {
  return useSyncExternalStore(subscribe, isBusy, () => false);
}
