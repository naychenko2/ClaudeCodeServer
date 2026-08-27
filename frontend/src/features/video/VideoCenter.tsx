import { useEffect, useMemo, useState } from 'react';
import { ListVideo, MonitorPlay, PictureInPicture2, X } from 'lucide-react';
import { IconButton, IslandHeader } from '../../components/ui';
import { PanelHeaderSlotContext } from '../../components/ui/panelHeaderSlotContext';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import { C, FONT, FS } from '../../lib/design';
import {
  closeVideoCenter, setVideoPicker, setVideoStage, useVideoCenter, useVideoStage,
} from '../../lib/videoStage';
import { VideoPicker } from './VideoPicker';
import { VideoStage } from './VideoStage';

/**
 * Центральный остров видео: шапка плюс то, что сейчас смотрят или выбирают.
 *
 * Кадр живёт РЯДОМ с чатом, вторым островом — как открытый файл: смотреть и
 * переписываться одновременно это и есть сценарий, ради которого раздел заводился.
 * Каталог, наоборот, разворачивается на весь центр: там выбирают по обложкам,
 * и половина ширины оставляла бы одну карточку в ряд.
 *
 * Кадр и каталог делят один остров и одну шапку: центр — место для одного занятия.
 *
 * Компонент рисует СОДЕРЖИМОЕ острова; сама рамка-карточка (Island) — на странице,
 * как и у файла: только она знает, идёт ли остров в split или во всю ширину.
 */
export function VideoCenter() {
  const view = useVideoCenter();
  const stage = useVideoStage();

  // Курсор на шапке: тогда иконка слева подменяется крестиком — тот же приём, что
  // у панелей рельсы (PanelShell). Шапка не тратит место на отдельную кнопку,
  // а закрытие оказывается там же, где человек его ищет у любой другой панели.
  const [headerEl, setHeaderEl] = useState<HTMLDivElement | null>(null);
  const [headerHover, setHeaderHover] = useState(false);

  // Слоты для контролов содержимого — тот же контракт, что у панелей рельсы
  // (PanelHeaderSlot). Благодаря этому каталог кладёт своё «Обновить» в шапку
  // острова тем же кодом, каким клал бы его в шапку панели: свой механизм
  // «поднять кнопку наверх» заводить не пришлось.
  const [slotEl, setSlotEl] = useState<HTMLDivElement | null>(null);
  const [slotLeftEl, setSlotLeftEl] = useState<HTMLDivElement | null>(null);
  const [slotPinnedEl, setSlotPinnedEl] = useState<HTMLDivElement | null>(null);
  const slotValue = useMemo(
    // hold — заглушка: у содержимого острова нет попапов, которым надо удерживать
    // контролы видимыми (контролы здесь и так не гаснут)
    () => ({ hasHeader: true, el: slotEl, elLeft: slotLeftEl, elPinned: slotPinnedEl, hold: () => {} }),
    [slotEl, slotLeftEl, slotPinnedEl]);

  // Escape освобождает центр — привычка та же, что у файла и модалок
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape' && !e.defaultPrevented) { e.preventDefault(); closeVideoCenter(); }
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, []);

  // Наведение ловим НАТИВНО, а не пропсами React: контролы шапки приезжают порталом,
  // в React-дереве они не потомки шапки, и onMouseLeave срабатывал бы на каждом
  // переходе курсора с шапки на её же кнопку — крестик мигал бы.
  useEffect(() => {
    if (!headerEl) return;
    const on = () => setHeaderHover(true);
    const off = () => setHeaderHover(false);
    headerEl.addEventListener('mouseenter', on);
    headerEl.addEventListener('mouseleave', off);
    return () => {
      headerEl.removeEventListener('mouseenter', on);
      headerEl.removeEventListener('mouseleave', off);
    };
  }, [headerEl]);

  if (!view) return null;

  // В режиме кадра канал есть всегда: 'player' и означает «кадр развёрнут в центре»
  const channel = view === 'player' ? stage?.channel ?? null : null;
  const Glyph = channel ? MonitorPlay : ListVideo;

  return (
    <PanelHeaderSlotContext.Provider value={slotValue}>
      <IslandHeader
        // Место иконки в потоке шапки — ровно её размер: иначе заголовок съезжал бы
        // вправо, когда на её месте появляется кнопка (она крупнее значка)
        icon={
          <span style={{
            position: 'relative', width: 15, height: 15, flexShrink: 0,
            display: 'flex', alignItems: 'center', justifyContent: 'center',
          }}>
            {headerHover ? (
              <span style={{
                position: 'absolute', top: '50%', left: '50%',
                transform: 'translate(-50%, -50%)', display: 'flex',
              }}>
                <IconButton
                  size="xs"
                  variant="soft"
                  title={channel ? 'Вернуть кадр в панель' : 'Закрыть каталог'}
                  onClick={closeVideoCenter}
                >
                  <X size={14} strokeWidth={ICON_STROKE} />
                </IconButton>
              </span>
            ) : (
              <Glyph size={15} strokeWidth={ICON_STROKE} color={C.textMuted} />
            )}
          </span>
        }
        title={channel ? channel.title : 'Каналы и лента'}
        // Что идёт в эфире — продолжение заголовка, поэтому leading, а не children:
        // те стоят ПОСЛЕ распорки и текст уехал бы к кнопкам, читаясь как подпись к ним
        leading={
          <>
            {channel?.nowPlaying && (
              <span style={{
                flex: '0 1 auto', minWidth: 0, fontFamily: FONT.sans, fontSize: FS.xs, color: C.textMuted,
                overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
              }}>
                {channel.nowPlaying}
              </span>
            )}
            <div ref={setSlotLeftEl} style={{ flexShrink: 0, display: 'flex', alignItems: 'center', gap: 4 }} />
          </>
        }
        headerProps={{ ref: setHeaderEl }}
        actions={channel ? (
          <>
            <IconButton size="sm" title="Все каналы и лента" onClick={() => setVideoPicker(true)}>
              <ListVideo size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
            </IconButton>
            <IconButton
              size="sm"
              title="В плавающее окно — его двигают и тянут за угол, и оно переживает переходы"
              onClick={() => setVideoStage(channel, 'float')}
            >
              <PictureInPicture2 size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
            </IconButton>
          </>
        ) : undefined}
      >
        {/* Слот контролов содержимого: сюда порталом приезжает «Обновить» каталога */}
        <div ref={setSlotEl} style={{ flexShrink: 0, display: 'flex', alignItems: 'center', gap: 4 }} />
        <div ref={setSlotPinnedEl} style={{ flexShrink: 0, display: 'flex', alignItems: 'center', gap: 4 }} />
      </IslandHeader>

      {channel ? <VideoStage channel={channel} /> : <VideoPicker />}
    </PanelHeaderSlotContext.Provider>
  );
}
