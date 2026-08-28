import { useEffect, useRef } from 'react';
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
 * раскладку. **Пропал слот — эфир не гаснет, а продолжает идти невидимым**: закрыл
 * панель, ушёл в «Заметки», сменил проект — звук остаётся, как у радио в фоне.
 * Это осознанная смена прежнего поведения (кадр умирал через отсрочку в 5 секунд,
 * а при явном закрытии панели — сразу): эфир слушают фоном, и закрытие панели
 * значит «убери картинку», а не «выключи». Выключает ровно одно — ПАУЗА (у эфира
 * она и есть снятие кадра); плюс временно снимает кадр занятый звук продукта.
 * Что эфир идёт при закрытой панели, видно по точке на её кнопке в рельсе.
 *
 * Плавающее окно рисует свой кадр само (VideoFloat) — на это время оверлей
 * снимается НЕМЕДЛЕННО, без отсрочки: два живых iframe одного эфира дают два
 * звука внахлёст.
 *
 * Известное ограничение: оверлей не знает, что его место чем-то перекрыли внутри
 * страницы (drawer соседней зоны на планшете). Модалки и меню лежат выше по
 * z-index, так что видимая часть случаев закрыта шкалой.
 */

// Куда девать кадр, пока места ему никто не отдал: за левый край экрана, сохранив
// размер. Ни display:none, ни нулевой размер не годятся — браузер вправе усыпить
// такой iframe вместе со звуком, а нам нужно ровно обратное: звук живёт, картинки нет.
const OFFSCREEN: VideoSlot = {
  frame: { x: -10000, y: 0, w: 320, h: 180 },
  clip: { x: -10000, y: 0, w: 320, h: 180 },
};

export function VideoStageFrame() {
  const stage = useVideoStage();
  const panelChannel = usePanelChannel();
  const slots = useVideoSlots();

  const place = videoFramePlace(stage, panelChannel);
  const channel = place ? (stage?.channel ?? panelChannel) : null;
  const slot = place ? slots[place] : null;

  const { frameRef, visible } = useVideoFrame(channel);

  // Последняя известная геометрия: пока места нет, кадр стоит там же, где стоял,
  // просто спрятанный. Перенести его «никуда» нельзя — iframe без места в разметке
  // перезагрузится, а эфир начнётся заново. Совсем не было места (канал включили,
  // а панель уже закрыли) — уводим за край экрана, звук от этого не страдает.
  const lastSlot = useRef<VideoSlot | null>(null);
  useEffect(() => { if (slot) lastSlot.current = slot; }, [slot]);
  const box = slot ?? lastSlot.current ?? OFFSCREEN;

  if (!channel || !channel.embedUrl) return null;

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
