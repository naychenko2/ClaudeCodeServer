import { useEffect, useRef, useState } from 'react';
import { R, Z } from '../../lib/design';
import { useVideoFrame } from './useVideoFrame';
import {
  useVideoSlots, usePanelChannel, useVideoStage, videoFramePlace, type VideoSlot,
} from '../../lib/videoStage';

/**
 * ЕДИНСТВЕННЫЙ живой кадр панели и центрального острова.
 *
 * Рендерится в App — НАД страницами, как плавающее окно. Смысл тот же: страница
 * при переходе между проектами перемонтируется (WorkspacePage идёт с key проекта),
 * и кадр, нарисованный внутри неё, умирал вместе с ней — эфир начинался заново.
 * Здесь кадр переживает и смену проекта, и переезд между панелью и центром: iframe
 * один и тот же, меняется только его прямоугольник.
 *
 * Место кадру отдают сами панель и остров (useVideoSlot) — они одни знают свою
 * раскладку. Пропал слот (ушли в раздел без панели, панель закрыли, страница
 * перемонтируется) — кадр не гасим сразу, а доживаем короткую отсрочку: переход
 * между проектами занимает миллисекунды, и гасить ради него эфир незачем.
 *
 * Плавающее окно рисует свой кадр само (VideoFloat) — на это время оверлей
 * снимается НЕМЕДЛЕННО, без отсрочки: два живых iframe одного эфира дают два
 * звука внахлёст.
 *
 * Известное ограничение: оверлей не знает, что его место чем-то перекрыли внутри
 * страницы (drawer соседней зоны на планшете). Модалки и меню лежат выше по
 * z-index, так что видимая часть случаев закрыта шкалой.
 */

// Отсрочка гашения. Перемонтаж страницы укладывается в кадр-другой; пять секунд
// с запасом покрывают и медленный переход, и при этом не превращаются в «звук
// из ниоткуда» надолго: приглушить эфир СМОТРИМ нельзя, плеер команд не слушает.
const GRACE_MS = 5000;

export function VideoStageFrame() {
  const stage = useVideoStage();
  const panelChannel = usePanelChannel();
  const slots = useVideoSlots();

  const place = videoFramePlace(stage, panelChannel);
  const channel = place ? (stage?.channel ?? panelChannel) : null;
  const slot = place ? slots[place] : null;

  const { frameRef, visible } = useVideoFrame(channel);

  // Отсрочка: слот пропал, но место у кадра ещё есть — ждём, не вернётся ли оно
  const [expired, setExpired] = useState(false);
  useEffect(() => {
    if (slot || !channel) { setExpired(false); return; }
    const t = window.setTimeout(() => setExpired(true), GRACE_MS);
    return () => window.clearTimeout(t);
  }, [slot, channel]);

  // Последняя известная геометрия: пока идёт отсрочка, кадр стоит там же, где стоял,
  // просто спрятанный. Перенести его «никуда» нельзя — iframe без места в разметке
  // перезагрузится, а это ровно то, от чего мы уходим.
  const lastSlot = useRef<VideoSlot | null>(null);
  useEffect(() => { if (slot) lastSlot.current = slot; }, [slot]);
  const box = slot ?? lastSlot.current;

  if (!channel || !channel.embedUrl || !box || expired) return null;

  // Кадр снят: у эфира под занятый звук продукта его убирают целиком (плеер чужой
  // и команд не слушает). Подпись об этом рисует само место — панель или остров.
  if (!visible) return null;

  const hidden = !slot;

  return (
    <div
      style={{
        // Внешний слой режет кадр телом панели: у короткой панели он вылезал бы
        // за её край поверх соседей
        position: 'fixed', left: box.clip.x, top: box.clip.y, width: box.clip.w, height: box.clip.h,
        zIndex: Z.videoFrame, overflow: 'hidden',
        visibility: hidden ? 'hidden' : 'visible',
        // Слой клипа шире кадра — он накрывает ВСЁ тело панели, вместе с кнопками
        // под кадром. Указатель сквозь него обязан проходить, иначе «развернуть в
        // центре», «в окно» и ссылка на сайт канала перестают нажиматься.
        pointerEvents: 'none',
      }}
    >
      <div style={{
        position: 'absolute',
        left: box.frame.x - box.clip.x, top: box.frame.y - box.clip.y,
        width: box.frame.w, height: box.frame.h,
        borderRadius: R.md, overflow: 'hidden',
        // …а сам кадр указатель ловит: в плеере кликают полноэкранный режим и звук
        pointerEvents: hidden ? 'none' : 'auto',
      }}>
        <iframe
          ref={frameRef}
          key={channel.embedUrl}
          src={channel.embedUrl}
          title={channel.title}
          style={{ position: 'absolute', inset: 0, width: '100%', height: '100%', border: 'none' }}
          allow="autoplay; fullscreen; encrypted-media; picture-in-picture"
          allowFullScreen
        />
      </div>
    </div>
  );
}
