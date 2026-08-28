import { useEffect, useState, useSyncExternalStore, type PointerEvent as ReactPointerEvent } from 'react';
import { GripVertical, Maximize2, X } from 'lucide-react';
import { IconButton } from '../../components/ui';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import { C, FONT, FS, R, SHADOW, SP, Z } from '../../lib/design';
import { startPointerDrag } from '../../lib/pointerDrag';
import { NAV_CHANGE_EVENT } from '../../lib/nav';
import { useVideoFrame } from './useVideoFrame';
import {
  FLOAT_HEADER_H, FLOAT_MAX_W, FLOAT_MIN_W, getFloatRect,
  setFloatRect, setVideoStage, useFloatRect, useVideoCenterBlocked, type VideoStageState,
} from '../../lib/videoStage';

/**
 * Плавающее окно с эфиром: двигается за шапку, тянется за нижний правый угол.
 *
 * Рендерится в App — НАД страницами, а не внутри них, и потому переживает переход
 * между проектами и разделами. Кадр панели и центра живёт там же (VideoStageFrame),
 * разница в другом: окну место не нужно, оно висит поверх ЛЮБОГО раздела, а кадру
 * панели и острова его отдаёт страница — и без неё он гаснет.
 *
 * Пропорция 16:9 держится жёстко: тянут за угол только ширину, высота считается.
 * Свободный ресайз давал бы чёрные поля внутри и без того маленького окна.
 */
export function VideoFloat({ stage }: { stage: VideoStageState }) {
  const rect = useFloatRect();
  // Экран, где есть центральная область под кадр: «Чаты» и страница проекта
  const onCenterScreen = useSyncExternalStore(subscribeHash, hasCenterNow, hasCenterNow);
  // …и она сейчас свободна: занятый файлом центр кадр не примет. Хук отдельной
  // строкой — за `&&` он вызывался бы через раз, а это нарушение правил хуков
  const centerBlocked = useVideoCenterBlocked();
  const hasCenter = onCenterScreen && !centerBlocked;
  const { frameRef, visible, audioBusy } = useVideoFrame(stage.channel);
  // Пока тащим, экран накрыт прозрачным слоем: курсор идёт над ЧУЖИМИ iframe (видео,
  // панель «Сервисы», проброс телеметрии), а те съедают pointermove — без слоя
  // перетаскивание обрывается на первом же кадре, попавшем под курсор.
  const [dragCursor, setDragCursor] = useState<string | null>(null);
  const dragging = dragCursor !== null;

  // Возврат окна в границы экрана. Кламп нужен и НА МОНТИРОВАНИИ: пока окна не было,
  // слушатель не жил, и сохранённая геометрия могла остаться от широкого монитора —
  // окно нарисовалось бы за краем и стало недоступно. Зависимостей нет намеренно:
  // актуальный прямоугольник берём геттером, иначе слушатель пересоздавался бы
  // на каждый пиксель перетаскивания.
  useEffect(() => {
    const fit = () => setFloatRect(getFloatRect());
    fit();
    window.addEventListener('resize', fit);
    return () => window.removeEventListener('resize', fit);
  }, []);

  // Escape возвращает кадр в панель — та же привычка, что у центра и модалок
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape' && !e.defaultPrevented) { e.preventDefault(); setVideoStage(null); }
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, []);

  const startMove = (e: ReactPointerEvent) => {
    // Кнопки в шапке не должны таскать окно
    if ((e.target as HTMLElement).closest('button')) return;
    e.preventDefault();
    const dx = e.clientX - rect.x;
    const dy = e.clientY - rect.y;
    setDragCursor('grabbing');
    startPointerDrag(
      ev => setFloatRect({ ...rect, x: ev.clientX - dx, y: ev.clientY - dy }),
      { cursor: 'grabbing', onEnd: () => setDragCursor(null) },
    );
  };

  const startResize = (e: ReactPointerEvent) => {
    e.preventDefault();
    e.stopPropagation();
    const startX = e.clientX;
    const startW = rect.w;
    setDragCursor('nwse-resize');
    startPointerDrag(
      ev => {
        const w = Math.min(Math.max(startW + (ev.clientX - startX), FLOAT_MIN_W), FLOAT_MAX_W);
        setFloatRect({ ...rect, w });
      },
      { cursor: 'nwse-resize', onEnd: () => setDragCursor(null) },
    );
  };

  return (
    <>
      {dragging && (
        <div style={{ position: 'fixed', inset: 0, zIndex: Z.floatWindow, cursor: dragCursor }} />
      )}

    <div
      style={{
        position: 'fixed', left: rect.x, top: rect.y, width: rect.w, height: rect.h,
        // Поверх раскладки, но НИЖЕ модалок: диалог, открытый над окном, должен
        // перекрывать его, а не наоборот
        zIndex: Z.floatWindow + 1,
        display: 'flex', flexDirection: 'column',
        background: C.bgCard, border: `1px solid ${C.border}`,
        borderRadius: R.lg, boxShadow: SHADOW.dropdown, overflow: 'hidden',
      }}
    >
      <div
        onPointerDown={startMove}
        style={{
          height: FLOAT_HEADER_H, flex: 'none', display: 'flex', alignItems: 'center',
          gap: SP.xs, padding: `0 ${SP.xs}px 0 ${SP.sm}px`, cursor: 'grab',
          // Без этого палец прокручивает страницу, а окно не двигается вовсе
          touchAction: 'none',
          borderBottom: `1px solid ${C.border}`, background: C.bgPanel,
        }}
      >
        <GripVertical size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} color={C.textMuted} />
        <div style={{
          flex: 1, minWidth: 0, fontFamily: FONT.sans, fontSize: FS.xs, color: C.textHeading,
          overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
        }}>
          {stage.channel.title}
        </div>
        {/* Центр рисуют только «Чаты» и проект. На прочих экранах кнопка увела бы
            кадр в никуда: он исчез бы, а вернуть его было бы нечем. */}
        {hasCenter && (
          <IconButton size="xs" title="Развернуть в центре" onClick={() => setVideoStage(stage.channel, 'center')}>
            <Maximize2 size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
          </IconButton>
        )}
        <IconButton size="xs" title="Вернуть кадр в панель" onClick={() => setVideoStage(null)}>
          <X size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
        </IconButton>
      </div>

      <div style={{ flex: 1, minHeight: 0, position: 'relative', background: C.mediaBackdrop }}>
        {stage.channel.embedUrl && visible && (
          <iframe
            ref={frameRef}
            key={stage.channel.embedUrl}
            src={stage.channel.embedUrl}
            title={stage.channel.title}
            style={{ position: 'absolute', inset: 0, width: '100%', height: '100%', border: 'none' }}
            allow="autoplay; fullscreen; encrypted-media; picture-in-picture"
            allowFullScreen
          />
        )}
        {!visible && audioBusy && (
          <div style={{
            position: 'absolute', inset: 0, display: 'flex', alignItems: 'center', justifyContent: 'center',
            color: C.onDark, fontFamily: FONT.sans, fontSize: FS.xs, textAlign: 'center', padding: SP.sm,
          }}>
            Эфир приостановлен — идёт разговор
          </div>
        )}

        {/* Уголок ресайза: поверх кадра, потому что сам кадр — чужой iframe и события мыши съедает */}
        <div
          onPointerDown={startResize}
          title="Потяните, чтобы изменить размер"
          aria-label="Изменить размер окна"
          style={{
            position: 'absolute', right: 0, bottom: 0, width: SP.xl, height: SP.xl,
            display: 'flex', alignItems: 'flex-end', justifyContent: 'flex-end',
            cursor: 'nwse-resize', background: C.mediaScrim, color: C.onDark,
            borderTopLeftRadius: R.sm, touchAction: 'none',
          }}
        >
          {/* Видимая «лапка»: без неё уголок теряется на светлом кадре */}
          <GripVertical size={ICON_SIZE.xs} strokeWidth={ICON_STROKE}
            style={{ transform: 'rotate(-45deg)' }} />
        </div>
      </div>
    </div>
    </>
  );
}

// Есть ли на текущем экране центральная область: её рисуют «Чаты» и проект.
// Читаем адрес, а не состояние страниц: окно живёт НАД ними и про их внутренности
// ничего не знает — иначе пришлось бы тащить через App лишний проп ради одной кнопки.
function hasCenterNow(): boolean {
  const h = window.location.hash;
  return h.startsWith('#/chats') || h.startsWith('#/project');
}

function subscribeHash(cb: () => void): () => void {
  window.addEventListener('hashchange', cb);
  window.addEventListener('popstate', cb);
  window.addEventListener(NAV_CHANGE_EVENT, cb);
  return () => {
    window.removeEventListener('hashchange', cb);
    window.removeEventListener('popstate', cb);
    window.removeEventListener(NAV_CHANGE_EVENT, cb);
  };
}
