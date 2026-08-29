import { ListVideo, MonitorOff, PanelTop, Pause, PictureInPicture2, Play, Radio, Volume2, VolumeX } from 'lucide-react';
import type { VideoChannel } from '../../types';
import { Button, IconButton, PanelHeaderSlot } from '../../components/ui';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import { C, FONT, FS, R, SP } from '../../lib/design';
import { useLiveChannelsState } from '../../lib/videoFavorites';
import { useAudioBusy, frameVisible } from './useVideoFrame';
import { useVideoSlot } from './useVideoSlot';
import { VideoStrip } from './VideoStrip';
import {
  setPanelChannel, setVideoPicker, setVideoPlayerState, setVideoStage,
  usePanelChannel, useVideoCenterBlocked, useVideoPlayerState, useVideoStage,
} from '../../lib/videoStage';

/**
 * Панель «Видео» в рельсе чатов и проекта: эфир сбоку, пока идёт работа.
 *
 * Сам кадр панель НЕ рисует: она отдаёт под него место (useVideoSlot), а живой
 * iframe кладёт поверх общий оверлей из App (VideoStageFrame). Только так эфир
 * переживает переход между проектами — страница вместе с панелью перемонтируется,
 * а кадр этого не замечает.
 *
 * Кадр здесь маленький: панель для фона. Кнопка разворота переносит канал в
 * ЦЕНТРАЛЬНЫЙ остров страницы, и тогда панель свой плеер снимает.
 */
export function VideoPanel() {
  // Каталог каналов — из ОБЩЕГО стора: ту же полосу рисуют центральный остров и
  // плавающее окно, и три собственных запроса на один переезд кадра были бы лишними
  const { failed } = useLiveChannelsState();
  // Свой канал панель держит в ОБЩЕМ сторе: выбирают его и здесь, полосой в шапке,
  // и в каталоге — тот стоит в центре и до локального состояния не дотянулся бы
  const own = usePanelChannel();
  // Тот же канал, развёрнутый в центре: свой плеер тогда снимаем — два живых iframe
  // одного эфира дают два звука сразу
  const staged = useVideoStage();
  // Центральный остров занят файлом или задачей — дороги в центр нет, и обе ведущие
  // туда кнопки гаснут. Молча «съесть» нажатие нельзя: кадр исчез бы и отсюда тоже
  const centerBlocked = useVideoCenterBlocked();

  // Что панель ОТРАЖАЕТ: уехавший в центр или окно кадр она показать не может, но
  // обязана о нём сказать — иначе писала бы «выберите канал», пока эфир идёт правее.
  const displayed = staged?.channel ?? own;
  const audioBusy = useAudioBusy();
  const player = useVideoPlayerState();
  // Показывать ли кадр вообще: у эфира под занятый звук продукта или паузу его снимают
  const visible = frameVisible(displayed, audioBusy, player.paused);
  // Место под кадр отдаём, только пока кадр ЗДЕСЬ: уехавший в центр или окно рисуют
  // они сами, и лишний слот сбивал бы оверлей с толку. Занятый звук на слот не
  // влияет — снимает кадр сам оверлей, а место его дожидается на своём месте.
  const { frameRef, clipRef } = useVideoSlot('panel', !staged && !!own);

  if (failed) {
    return (
      <PanelBody>
        <div style={{ ...noticeStyle, flex: 1 }}>
          <MonitorOff size={ICON_SIZE.lg} strokeWidth={ICON_STROKE} />
          <span>Каналы не загрузились</span>
        </div>
      </PanelBody>
    );
  }

  // Кадр показывают ВНЕ панели — свой плеер снимаем, каким бы канал ни был.
  // Сравнение по id тут было дефектом: отправил один канал в окно, ткнул другой
  // в полосе — и панель заводила второй живой iframe, два звука внахлёст.
  const stagedHere = !!staged;

  // Пока кадр показывают ВНЕ панели, выбор канала уводит новый канал туда же.
  // Иначе панель завела бы собственный плеер рядом с уехавшим — два эфира разом,
  // причём второй не видно: панель узкая и стоит с краю.
  const pick = (next: VideoChannel) => {
    // Пока кадр показывают ВНЕ панели, выбор канала уводит новый канал туда же:
    // иначе панель завела бы собственный плеер рядом с уехавшим — два эфира разом,
    // причём второй не видно (панель узкая и стоит с краю).
    if (staged) setVideoStage(next, staged.mode);
    else setPanelChannel(next);
  };

  return (
    <PanelBody bodyRef={clipRef}>
      {/* Выбор канала — переключатель вида этой панели («что смотрим»), поэтому
          левый слот шапки, у самого названия. В теле полоса съедала высоту, которой
          и так мало: панель узкая, и каждый её пиксель нужен кадру */}
      <PanelHeaderSlot side="left">
        <VideoStrip
          activeId={displayed?.id ?? null}
          onPick={pick}
          onOpenCatalog={centerBlocked ? undefined : () => setVideoPicker(true)}
        />
      </PanelHeaderSlot>

      <PanelHeaderSlot>
        {/* Каталог открывается в ЦЕНТРАЛЬНОМ острове, а не второй панелью сбоку:
            каналы выбирают по обложкам, а в рельсе их не разглядеть */}
        <IconButton
          size="xs"
          title={centerBlocked
            ? 'Каталог откроется в центре — сейчас там файл или задача'
            : 'Все каналы и лента'}
          disabled={centerBlocked}
          onClick={() => setVideoPicker(true)}
        >
          <ListVideo size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
        </IconButton>
      </PanelHeaderSlot>

      {/* Кадр — во всю ширину панели, вплотную к шапке и бокам: панель узкая, и
          каждый её пиксель нужен кадру. Скругление живёт у самого кадра — и у
          плейсхолдера здесь, и у живого оверлея (VideoStageFrame), радиус один */}
      <Stage
        channel={displayed}
        audioBusy={audioBusy}
        visible={visible}
        paused={player.paused}
        slotRef={frameRef}
        stagedHere={stagedHere}
        stagedMode={staged?.mode}
        onOpenPicker={centerBlocked ? undefined : () => setVideoPicker(true)}
      />

      {/* Строка управления — обычные поля панели: кнопки не липнут к рамке.
          Отступ сверху держит зазор до кадра (бывший gap обёртки) */}
      {displayed && (
        <div style={{ padding: `${SP.xs}px ${SP.sm}px` }}>
          <PlayerRow
            channel={displayed}
            stagedHere={stagedHere}
            stagedMode={staged?.mode}
            centerBlocked={centerBlocked}
          />
        </div>
      )}
    </PanelBody>
  );
}

/**
 * Сам кадр. Плеер СНИМАЕТСЯ, пока звук занят продуктом: управлять громкостью чужого
 * iframe нельзя — он не слушает сообщений извне. Для ПРЯМОГО эфира это честная замена
 * приглушению: вернувшись, попадаешь в текущую минуту, а не в пропущенную.
 */
function Stage({ channel, audioBusy, visible, paused, slotRef, stagedHere, stagedMode, onOpenPicker }: {
  channel: VideoChannel | null;
  audioBusy: boolean;
  /** Кадр разрешён к показу: у эфира под занятый звук его снимают, ролик глушат. */
  visible: boolean;
  /** Пауза нажата кнопкой: подпись в пустом кадре обязана говорить правду, почему пусто. */
  paused: boolean;
  /** Место под кадр: сюда его кладёт оверлей из App, сама панель iframe не рисует. */
  slotRef: React.RefObject<HTMLDivElement | null>;
  /** Этот же канал показывают вне панели (центр или окно) — здесь кадр не дублируем. */
  stagedHere: boolean;
  /** Куда именно уехал: для подписи в пустом кадре. */
  stagedMode?: 'center' | 'float';
  /** Открыть каталог. Пустому кадру он нужен как ЕДИНСТВЕННЫЙ видимый путь к выбору:
      полоса каналов живёт в шапке и в покое приглушена — не зная об этом, человек
      упёрся бы в «выберите канал» без единой кнопки рядом. undefined — центр занят. */
  onOpenPicker?: () => void;
}) {
  return (
    <div
      ref={slotRef}
      style={{
        position: 'relative', width: '100%', aspectRatio: '16 / 9',
        background: C.mediaBackdrop, borderRadius: R.md, overflow: 'hidden',
      }}
    >

      {(!channel || !visible || stagedHere) && (
        <div style={{
          position: 'absolute', inset: 0, display: 'flex', flexDirection: 'column',
          alignItems: 'center', justifyContent: 'center', gap: SP.xs,
          color: C.onDark, fontFamily: FONT.sans, fontSize: FS.xs, textAlign: 'center', padding: SP.sm,
        }}>
          {!channel && (
            <>
              <Radio size={ICON_SIZE.lg} strokeWidth={ICON_STROKE} />
              {onOpenPicker
                ? <Button size="sm" variant="ghost" onClick={onOpenPicker}>Выбрать канал</Button>
                : <span>Выберите канал</span>}
            </>
          )}
          {channel && paused && <span>Пауза — нажмите ▶, чтобы продолжить</span>}
          {channel && audioBusy && !paused && <span>Эфир приостановлен — идёт разговор</span>}
          {channel && !audioBusy && !paused && stagedHere && (
            <span>{stagedMode === 'float' ? 'Идёт в плавающем окне' : 'Идёт в центре экрана'}</span>
          )}
        </div>
      )}
    </div>
  );
}

/**
 * Строка плеера под кадром: что идёт в эфире + управление.
 *
 * Кнопки делятся на две группы, отсюда сепаратор: play/pause и mute управляют
 * самим показом, а правые — ГДЕ он идёт (центр, плавающее окно). Ссылки на сайт
 * канала здесь нет намеренно: уводить из продукта туда, где тот же эфир идёт
 * прямо в кадре, незачем — наружу отправляют только каналы БЕЗ своего потока,
 * и делает это карточка каталога.
 *
 * Развилка по провайдеру — та же, что у занятого звука (useVideoFrame):
 * - эфир (СМОТРИМ): плеер команд не слушает, «пауза» = снять кадр (вернёшься в
 *   текущую минуту), а mute невозможен вовсе — кнопку не рисуем;
 * - ролик YouTube: настоящие pause/mute командами плеера, кадр живёт.
 */
function PlayerRow({ channel, stagedHere, stagedMode, centerBlocked }: {
  channel: VideoChannel;
  stagedHere: boolean;
  stagedMode?: 'center' | 'float';
  centerBlocked: boolean;
}) {
  const player = useVideoPlayerState();
  const isLive = channel.provider !== 'youtube';

  const togglePause = () => setVideoPlayerState(!player.paused, player.muted);
  const toggleMute = () => setVideoPlayerState(player.paused, !player.muted);

  return (
    <div style={{ display: 'flex', alignItems: 'center', gap: SP.xs }}>
      <IconButton
        size="sm"
        title={player.paused
          ? 'Продолжить'
          : isLive
            ? 'Приостановить эфир — звук прекратится, а вернётесь в текущую минуту'
            : 'Пауза'}
        onClick={togglePause}
      >
        {player.paused
          ? <Play size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
          : <Pause size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />}
      </IconButton>
      {!isLive && (
        <IconButton
          size="sm"
          title={player.muted ? 'Включить звук' : 'Выключить звук'}
          onClick={toggleMute}
        >
          {player.muted
            ? <VolumeX size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
            : <Volume2 size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />}
        </IconButton>
      )}
      {/* Сепаратор групп: управление показом против «где смотреть» */}
      <div aria-hidden style={{ width: 1, height: 16, flexShrink: 0, background: C.border }} />

      <div style={{
        flex: 1, minWidth: 0, fontFamily: FONT.sans, fontSize: FS.xs, color: C.textMuted,
        overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
      }}>
        {channel.nowPlaying || channel.title}
      </div>
      <IconButton
        size="sm"
        title={stagedHere && stagedMode === 'float'
          ? 'Вернуть кадр в панель'
          : 'В плавающее окно — его двигают и тянут за угол, и оно остаётся поверх любого раздела'}
        onClick={() => setVideoStage(
          stagedHere && stagedMode === 'float' ? null : channel, 'float')}
      >
        <PictureInPicture2 size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
      </IconButton>
      <IconButton
        size="sm"
        title={stagedHere && stagedMode === 'center'
          ? 'Вернуть кадр в панель'
          : centerBlocked
            ? 'В центре сейчас файл или задача — закройте, чтобы смотреть там'
            : 'Развернуть в центре — панель узкая, там кадр крупнее'}
        disabled={centerBlocked}
        onClick={() => setVideoStage(
          stagedHere && stagedMode === 'center' ? null : channel, 'center')}
      >
        <PanelTop size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
      </IconButton>
    </div>
  );
}

function PanelBody({ children, bodyRef }: {
  children: React.ReactNode;
  /** Тело панели режет кадр по своему краю: в короткой панели он вылезал бы наружу. */
  bodyRef?: React.RefObject<HTMLDivElement | null>;
}) {
  return (
    <div ref={bodyRef} style={{
      flex: 1, minHeight: 0, display: 'flex', flexDirection: 'column',
      background: C.bgWhite, overflow: 'hidden',
    }}>
      {children}
    </div>
  );
}

const noticeStyle: React.CSSProperties = {
  display: 'flex', flexDirection: 'column', alignItems: 'center', justifyContent: 'center',
  gap: SP.xs, color: C.textMuted, fontFamily: FONT.sans, fontSize: FS.sm, padding: SP.lg,
};
