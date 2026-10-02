// A/B двух версий на одном <audio>: смена src сбрасывает currentTime в 0 и
// останавливает звук, поэтому позицию и признак «играло» запоминаем до смены и
// возвращаем, как только у нового файла появятся метаданные.

/** Ровно то, что нужно от HTMLMediaElement: так логику можно гонять без браузера. */
export interface MediaLike {
  src: string;
  currentTime: number;
  readonly paused: boolean;
  readonly duration: number;
  play(): Promise<void> | void;
  pause(): void;
  load(): void;
  addEventListener(type: 'loadedmetadata', fn: () => void, opts?: { once?: boolean }): void;
  removeEventListener(type: 'loadedmetadata', fn: () => void): void;
}

/**
 * Переключает источник, сохраняя позицию воспроизведения и состояние play/pause.
 * Позиция ограничивается длиной новой версии (версия могла стать короче).
 * Возвращает отмену — на случай, если до метаданных переключат ещё раз.
 */
export function swapSourceKeepingPosition(el: MediaLike, src: string): () => void {
  if (el.src === src) return () => {};
  const at = el.currentTime;
  const wasPlaying = !el.paused;
  const onMeta = () => {
    const d = el.duration;
    el.currentTime = Number.isFinite(d) && d > 0 ? Math.min(at, d) : at;
    if (wasPlaying) void Promise.resolve(el.play()).catch(() => {});
  };
  el.addEventListener('loadedmetadata', onMeta, { once: true });
  el.src = src;
  el.load();
  return () => el.removeEventListener('loadedmetadata', onMeta);
}
