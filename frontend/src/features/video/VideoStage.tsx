import { C, FONT, FS, R, SP } from '../../lib/design';
import { frameVisible, useAudioBusy } from './useVideoFrame';
import { useVideoSlot } from './useVideoSlot';
import type { VideoChannel } from '../../types';

/**
 * Кадр в центральном острове — том самом, где открывается файл.
 *
 * Здесь только сам кадр: заголовок, переключение на каталог и крестик рисует
 * шапка острова (VideoCenter), общая у кадра и каталога. Панель в рельсе свой
 * плеер на это время снимает — два живых iframe одного эфира дают два звука.
 *
 * Живой iframe остров, как и панель, НЕ рисует: он отдаёт место (useVideoSlot),
 * а кадр кладёт поверх общий оверлей из App. Иначе эфир обрывался бы на каждой
 * смене проекта — страница вместе с островом перемонтируется.
 *
 * Кадр держит 16:9 и вписывается в остров ПО ЦЕНТРУ, а не растягивается на всю
 * его площадь. Растянутый iframe вписывает картинку сам, но добирает разницу
 * чёрными полями — а остров рядом с лентой узкий и высокий, и полей выходило
 * вдвое больше самого кадра: читалось как поехавшая вёрстка, а не как кино.
 */
export function VideoStage({ channel }: { channel: VideoChannel }) {
  const audioBusy = useAudioBusy();
  const visible = frameVisible(channel, audioBusy);
  const { frameRef } = useVideoSlot('center', true);

  return (
    <div style={{
      flex: 1, minHeight: 0, display: 'flex', alignItems: 'center', justifyContent: 'center',
      padding: SP.sm, background: C.bgMain,
    }}>
      <div
        ref={frameRef}
        style={{
          position: 'relative', width: '100%', aspectRatio: '16 / 9',
          // Высота острова меньше, чем требует пропорция (узкое окно, низкий остров) —
          // кадр ужимается по высоте, а ширину добирает сам iframe
          maxHeight: '100%',
          background: C.mediaBackdrop, borderRadius: R.md, overflow: 'hidden',
        }}
      >
        {!visible && (
          <div style={{
            position: 'absolute', inset: 0, display: 'flex', alignItems: 'center', justifyContent: 'center',
            color: C.onDark, fontFamily: FONT.sans, fontSize: FS.sm, textAlign: 'center', padding: SP.lg,
          }}>
            {audioBusy ? 'Эфир приостановлен — идёт разговор' : 'Эфир приостановлен'}
          </div>
        )}
      </div>
    </div>
  );
}
