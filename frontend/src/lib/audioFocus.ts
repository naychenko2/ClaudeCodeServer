// Кто в продукте сейчас занимает звук: озвучка ответа или режим разговора.
//
// Нужен фоновому видео в панели: телевизор рядом с говорящим ассистентом — это каша
// из двух голосов, а в режиме разговора ещё и эхо в микрофон, от которого распознавание
// слышит собственный телевизор. Опрашивать isSpeaking() в таймере было бы дешевле
// написать, но тогда реакция запаздывает на такт опроса и невозможно понять, ЧТО именно
// звучит: у эфира и у ролика разные способы замолчать (см. VideoPanel).
//
// Намеренно без React: клеймят его модули без хуков (lib/tts), а подписываются компоненты.

export type AudioFocusReason = 'speech' | 'conversation';

const holders = new Set<AudioFocusReason>();
const listeners = new Set<(busy: boolean) => void>();

/** Занят ли звук продукта прямо сейчас. */
export function isAudioBusy(): boolean {
  return holders.size > 0;
}

/**
 * Заявить или снять причину занятости. Причины считаются множеством, а не счётчиком:
 * разговор может идти поверх озвучки, и снятие одной не должна отпускать другую.
 */
export function setAudioFocus(reason: AudioFocusReason, active: boolean): void {
  const before = holders.size > 0;
  if (active) holders.add(reason);
  else holders.delete(reason);

  const after = holders.size > 0;
  if (before !== after) for (const cb of listeners) cb(after);
}

/** Подписка на смену состояния; возвращает функцию отписки. */
export function onAudioFocusChange(cb: (busy: boolean) => void): () => void {
  listeners.add(cb);
  return () => listeners.delete(cb);
}
