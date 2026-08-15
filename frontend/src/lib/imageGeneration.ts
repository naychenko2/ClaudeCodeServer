import { useEffect, useSyncExternalStore } from 'react';
import type { ImageGenerationSettings } from '../types';
import { api } from './api';

// Общий снимок настройки генератора картинок (по образцу lib/notes, но проще): подпись
// «чем рисуется» висит в трёх местах — диалог иконки и две формы персоны, — а настройка
// меняется редко. Держим один запрос на вкладку и общий снимок, чтобы после правки в
// «Применении» открытый диалог не показывал прежний генератор.
// undefined — ещё не грузили, null — запрос не удался.

let snapshot: ImageGenerationSettings | null | undefined = undefined;
let inflight: Promise<void> | null = null;
const listeners = new Set<() => void>();

function load(): Promise<void> {
  if (!inflight) {
    inflight = api.imageGeneration.get()
      .then(s => { snapshot = s; })
      .catch(() => { snapshot = null; })
      .finally(() => { inflight = null; listeners.forEach(fn => fn()); });
  }
  return inflight;
}

// Свежий ответ сервера (GET и PUT секции «Картинки») — сразу в общий снимок.
// Оптимистичные снимки сюда НЕ кладём: диалоги не должны обещать несохранённое.
export function setImageGenerationSnapshot(s: ImageGenerationSettings): void {
  snapshot = s;
  listeners.forEach(fn => fn());
}

export function useImageGenerator(): ImageGenerationSettings | null | undefined {
  const s = useSyncExternalStore(
    fn => { listeners.add(fn); return () => listeners.delete(fn); },
    () => snapshot,
    () => snapshot,
  );
  // Прошлая неудача не липнет: следующее открытие диалога пробует снова
  useEffect(() => { if (snapshot === undefined || snapshot === null) void load(); }, []);
  return s;
}
