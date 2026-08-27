import { C, FONT, FS, R, SP } from '../../lib/design';
import { useVideoFrame } from './useVideoFrame';
import type { VideoChannel } from '../../types';

/**
 * Кадр в центральном острове — том самом, где открывается файл.
 *
 * Здесь только сам кадр: заголовок, переключение на каталог и крестик рисует
 * шапка острова (VideoCenter), общая у кадра и каталога. Панель в рельсе свой
 * плеер на это время снимает — два живых iframe одного эфира дают два звука.
 *
 * Кадр держит 16:9 и вписывается в остров ПО ЦЕНТРУ, а не растягивается на всю
 * его площадь. Растянутый iframe вписывает картинку сам, но добирает разницу
 * чёрными полями — а остров рядом с лентой узкий и высокий, и полей выходило
 * вдвое больше самого кадра: читалось как поехавшая вёрстка, а не как кино.
 */
export function VideoStage({ channel }: { channel: VideoChannel }) {
  const { frameRef, visible, audioBusy } = useVideoFrame(channel);

  return (
    <div style={{
      flex: 1, minHeight: 0, display: 'flex', alignItems: 'center', justifyContent: 'center',
      padding: SP.sm, background: C.bgMain,
    }}>
      <div style={{
        position: 'relative', width: '100%', aspectRatio: '16 / 9',
        // Высота острова меньше, чем требует пропорция (узкое окно, низкий остров) —
        // кадр ужимается по высоте, а ширину добирает сам iframe
        maxHeight: '100%',
        background: C.mediaBackdrop, borderRadius: R.md, overflow: 'hidden',
      }}>
        {channel.embedUrl && visible && (
          <iframe
            ref={frameRef}
            key={channel.embedUrl}
            src={channel.embedUrl}
            title={channel.title}
            style={{ position: 'absolute', inset: 0, width: '100%', height: '100%', border: 'none' }}
            allow="autoplay; fullscreen; encrypted-media; picture-in-picture"
            allowFullScreen
          />
        )}

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
