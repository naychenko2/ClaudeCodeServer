import { useEffect, useRef, useState } from 'react';
import { isAudioBusy, onAudioFocusChange } from '../../lib/audioFocus';
import type { VideoChannel } from '../../types';

/**
 * Общая механика кадра для всех трёх мест показа (панель, центр, плавающее окно).
 *
 * Главное здесь — РАЗНОЕ поведение под занятый звук продукта, и разница не косметическая:
 *
 * - Прямой эфир (СМОТРИМ). Плеер чужой и команд извне не слушает вовсе, поэтому кадр
 *   СНИМАЕТСЯ. Для эфира это равноценно приглушению: вернувшись, попадаешь в текущую
 *   минуту, а не в пропущенную.
 * - Ролик YouTube. У него есть ПОЗИЦИЯ просмотра, и снятый кадр вернулся бы с нуля —
 *   поэтому его глушим штатной командой плеера (enablejsapi добавляет провайдер),
 *   а кадр остаётся жить.
 */
export function useVideoFrame(channel: VideoChannel | null) {
  const audioBusy = useAudioBusy();
  const frameRef = useRef<HTMLIFrameElement | null>(null);

  // Ролик глушим командой, а не снятием: позиция просмотра сохраняется
  const mutable = channel?.provider === 'youtube';

  useEffect(() => {
    if (!mutable) return;
    const win = frameRef.current?.contentWindow;
    if (!win) return;
    // Протокол IFrame Player API: команда приходит строкой JSON в postMessage.
    // Origin '*' здесь безопасен: команда не несёт данных, а плеер сам сверяет источник.
    win.postMessage(JSON.stringify({
      event: 'command',
      func: audioBusy ? 'mute' : 'unMute',
      args: [],
    }), '*');
  }, [audioBusy, mutable]);

  return {
    frameRef,
    /** Показывать ли кадр: у эфира под занятый звук его снимают. */
    visible: frameVisible(channel, audioBusy),
    audioBusy,
  };
}

/**
 * Занят ли звук продукта (озвучка ответа, разговор без рук). Отдельно от кадра:
 * место показа рисует по этому признаку свою подпись («Эфир приостановлен»), а сам
 * кадр живёт не в нём, а в общем оверлее.
 */
export function useAudioBusy(): boolean {
  const [audioBusy, setAudioBusy] = useState(isAudioBusy);
  useEffect(() => onAudioFocusChange(setAudioBusy), []);
  return audioBusy;
}

/** Виден ли кадр под занятый звук: эфир снимают, ролик остаётся (его глушат командой). */
export function frameVisible(channel: VideoChannel | null, audioBusy: boolean): boolean {
  return !audioBusy || channel?.provider === 'youtube';
}
