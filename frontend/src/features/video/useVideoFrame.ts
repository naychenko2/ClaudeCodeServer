import { useEffect, useRef, useState } from 'react';
import { isAudioBusy, onAudioFocusChange } from '../../lib/audioFocus';
import type { VideoChannel } from '../../types';
import { useVideoPlayerState } from '../../lib/videoStage';

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
 *
 * Кнопки паузы/тишины работают по той же развилке: ролик слушается команд
 * (pauseVideo/mute), эфир — нет, и для него пауза означает снятие кадра
 * (frameVisible), а тишина недостижима вовсе — кнопку mute для эфира не рисуют.
 */
export function useVideoFrame(channel: VideoChannel | null) {
  const audioBusy = useAudioBusy();
  const player = useVideoPlayerState();
  const frameRef = useRef<HTMLIFrameElement | null>(null);

  // Ролик глушим командой, а не снятием: позиция просмотра сохраняется
  const mutable = channel?.provider === 'youtube';

  useEffect(() => {
    if (!mutable) return;
    const win = frameRef.current?.contentWindow;
    if (!win) return;
    // Протокол IFrame Player API: команда приходит строкой JSON в postMessage.
    // Origin '*' здесь безопасен: команда не несёт данных, а плеер сам сверяет источник.
    // Пауза сильнее тишины: сняв паузу при заглушенном звуке, ролик не должен
    // заорать — команда unMute уходит только когда тишины нет вовсе
    const muted = audioBusy || player.muted;
    const func = player.paused ? 'pauseVideo' : (muted ? 'mute' : 'unMute');
    win.postMessage(JSON.stringify({
      event: 'command',
      func,
      args: [],
    }), '*');
  }, [audioBusy, mutable, player.muted, player.paused]);

  return {
    frameRef,
    /** Показывать ли кадр: у эфира под занятый звук или паузу его снимают. */
    visible: frameVisible(channel, audioBusy, player.paused),
    audioBusy,
    /** Состояние плеера — подписи пустого кадра обязаны различать паузу и занятый звук. */
    player,
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

/**
 * Виден ли кадр: эфир снимают под занятый звук продукта И под паузу (плеер команд
 * не слушает — для эфира пауза и есть снятие кадра), ролик остаётся жить всегда
 * (его глушат и ставят на паузу командой).
 */
export function frameVisible(channel: VideoChannel | null, audioBusy: boolean, paused = false): boolean {
  if (channel?.provider === 'youtube') return true;
  return !audioBusy && !paused;
}
