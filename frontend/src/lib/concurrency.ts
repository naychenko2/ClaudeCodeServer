// Ограничение конкурентности: семафор для медленного канала.
//
// Контекст: картинки чата (ChatImage) грузятся как base64 через api.files.getContent.
// На медленном боевом канале N тяжёлых ответов забивают HTTP/1.1-пул браузера (~6 на origin),
// из-за чего health-ping (таймаут 4с) не успевает пролезть → canceled → мигание online/offline.
// Глобальный семафор гарантирует потолок одновременных загрузок, оставляя слоты под health/api.

// ~6/origin: 3 на картинки, остальное — health/metadata/sync.
export const IMAGE_CONCURRENCY = 3;

// Фабрика семафора: возвращает run(fn), разделяющий один лимит между всеми вызовами данного
// инстанса. Очередь ждущих — в замыкании инстанса. Слот освобождается в finally — гарантированно
// даже при throw/reject (JS однопоточный, мьютекс не нужен).
export function createSemaphore(limit: number): <T>(fn: () => Promise<T>) => Promise<T> {
  let active = 0;
  const queue: Array<() => void> = [];
  return async function run<T>(fn: () => Promise<T>): Promise<T> {
    while (active >= limit) {
      await new Promise<void>(resolve => queue.push(resolve));
    }
    active++;
    try {
      return await fn();
    } finally {
      active--;
      const next = queue.shift();
      if (next) next();
    }
  };
}

// Глобальный (module-level) семафор для всех проектных картинок чата.
// ВАЖНО: один инстанс на модуль — иначе каждый ChatImage получит свой лимит, и пул снова забьётся.
export const withImageLimit: <T>(fn: () => Promise<T>) => Promise<T> = createSemaphore(IMAGE_CONCURRENCY);
