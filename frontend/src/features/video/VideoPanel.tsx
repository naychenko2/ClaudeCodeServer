import { useCallback, useEffect, useRef, useState } from 'react';
import { ExternalLink, ListVideo, Maximize2, MonitorOff, PictureInPicture2, Radio } from 'lucide-react';
import type { VideoChannel } from '../../types';
import { Button, IconButton, PanelHeaderSlot } from '../../components/ui';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import { C, FONT, FS, R, SP } from '../../lib/design';
import { api } from '../../lib/api';
import { useVideoFrame } from './useVideoFrame';
import {
  setPanelChannel, setVideoPicker, setVideoStage, usePanelChannel, useVideoCenterBlocked, useVideoStage,
} from '../../lib/videoStage';

/**
 * Панель «Видео» в рельсе чатов и проекта: эфир сбоку, пока идёт работа.
 *
 * Панель монтируется страницей и живёт, пока человек ходит по чатам ВНУТРИ неё.
 * Переход между проектами страницу пересоздаёт — эфир начнётся заново.
 *
 * Кадр здесь маленький: панель для фона. Кнопка разворота переносит канал в
 * ЦЕНТРАЛЬНЫЙ остров страницы, и тогда панель свой плеер снимает.
 */
export function VideoPanel() {
  const [channels, setChannels] = useState<VideoChannel[]>([]);
  const [failed, setFailed] = useState(false);
  // Свой канал панель держит в ОБЩЕМ сторе: выбирают его и здесь, полосой в шапке,
  // и в каталоге — тот стоит в центре и до локального состояния не дотянулся бы
  const own = usePanelChannel();
  // Тот же канал, развёрнутый в центре: свой плеер тогда снимаем — два живых iframe
  // одного эфира дают два звука сразу
  const staged = useVideoStage();
  // Центральный остров занят файлом или задачей — дороги в центр нет, и обе ведущие
  // туда кнопки гаснут. Молча «съесть» нажатие нельзя: кадр исчез бы и отсюда тоже
  const centerBlocked = useVideoCenterBlocked();

  useEffect(() => {
    let alive = true;
    void (async () => {
      try {
        const res = await api.video.channels('smotrim');
        if (!alive) return;
        // В узкой панели место есть только у того, что реально играет: карточки-ссылки
        // на чужой сайт сюда не помещаются и смысла не несут
        const playable = res.channels.filter(c => c.embeddable && c.embedUrl);
        setChannels(playable);
        setFailed(playable.length === 0);
      } catch {
        if (alive) setFailed(true);
      }
    })();
    return () => { alive = false; };
  }, []);

  // Что панель ОТРАЖАЕТ: уехавший в центр или окно кадр она показать не может, но
  // обязана о нём сказать — иначе писала бы «выберите канал», пока эфир идёт правее.
  const displayed = staged?.channel ?? own;
  // Плеер заводим только под СВОЙ кадр: уехавший рисует центр или окно
  const { frameRef, visible, audioBusy } = useVideoFrame(staged ? null : own);

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
  const pick = (id: string) => {
    const next = channels.find(c => c.id === id);
    if (!next) return;
    // Пока кадр показывают ВНЕ панели, выбор канала уводит новый канал туда же:
    // иначе панель завела бы собственный плеер рядом с уехавшим — два эфира разом,
    // причём второй не видно (панель узкая и стоит с краю).
    if (staged) setVideoStage(next, staged.mode);
    else setPanelChannel(next);
  };

  return (
    <PanelBody>
      {/* Выбор канала — переключатель вида этой панели («что смотрим»), поэтому
          левый слот шапки, у самого названия. В теле полоса съедала высоту, которой
          и так мало: панель узкая, и каждый её пиксель нужен кадру */}
      {channels.length > 0 && (
        <PanelHeaderSlot side="left">
          <ChannelStrip channels={channels} activeId={displayed?.id ?? null} onPick={pick} />
        </PanelHeaderSlot>
      )}

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

      <div style={{ padding: SP.sm, display: 'flex', flexDirection: 'column', gap: SP.xs }}>
        <Stage
          channel={displayed}
          audioBusy={audioBusy}
          visible={visible}
          frameRef={frameRef}
          stagedHere={stagedHere}
          stagedMode={staged?.mode}
          onOpenPicker={centerBlocked ? undefined : () => setVideoPicker(true)}
        />

        {displayed && (
          <div style={{ display: 'flex', alignItems: 'center', gap: SP.xs }}>
            <div style={{
              flex: 1, minWidth: 0, fontFamily: FONT.sans, fontSize: FS.xs, color: C.textMuted,
              overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
            }}>
              {displayed.nowPlaying || displayed.title}
            </div>
            {/* Кнопки паузы здесь нет намеренно: у прямого эфира паузы не бывает.
                Кадр пришлось бы снять целиком, а вернувшись — попасть в текущую
                минуту; «пауза», после которой продолжения нет, только врёт. */}
            <IconButton
              size="sm"
              title={stagedHere && staged?.mode === 'center'
                ? 'Вернуть кадр в панель'
                : centerBlocked
                  ? 'В центре сейчас файл или задача — закройте, чтобы смотреть там'
                  : 'Развернуть в центре — панель узкая, там кадр крупнее'}
              disabled={centerBlocked}
              onClick={() => setVideoStage(
                stagedHere && staged?.mode === 'center' ? null : displayed, 'center')}
            >
              <Maximize2 size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
            </IconButton>
            <IconButton
              size="sm"
              title={stagedHere && staged?.mode === 'float'
                ? 'Вернуть кадр в панель'
                : 'В плавающее окно — его двигают и тянут за угол, и оно переживает переходы'}
              onClick={() => setVideoStage(
                stagedHere && staged?.mode === 'float' ? null : displayed, 'float')}
            >
              <PictureInPicture2 size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
            </IconButton>
            {displayed.externalUrl && (
              <IconButton
                size="sm"
                title="Открыть на сайте канала"
                onClick={() => window.open(displayed.externalUrl!, '_blank', 'noopener,noreferrer')}
              >
                <ExternalLink size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
              </IconButton>
            )}
          </div>
        )}
      </div>
    </PanelBody>
  );
}

/**
 * Сам кадр. Плеер СНИМАЕТСЯ, пока звук занят продуктом: управлять громкостью чужого
 * iframe нельзя — он не слушает сообщений извне. Для ПРЯМОГО эфира это честная замена
 * приглушению: вернувшись, попадаешь в текущую минуту, а не в пропущенную.
 */
function Stage({ channel, audioBusy, visible, frameRef, stagedHere, stagedMode, onOpenPicker }: {
  channel: VideoChannel | null;
  audioBusy: boolean;
  /** Кадр разрешён к показу: у эфира под занятый звук его снимают, ролик глушат. */
  visible: boolean;
  frameRef: React.RefObject<HTMLIFrameElement | null>;
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
    <div style={{
      position: 'relative', width: '100%', aspectRatio: '16 / 9',
      background: C.mediaBackdrop, borderRadius: R.md, overflow: 'hidden',
    }}>
      {channel && visible && !stagedHere && (
        <iframe
          ref={frameRef}
          key={channel.embedUrl!}
          src={channel.embedUrl!}
          title={channel.title}
          style={{ position: 'absolute', inset: 0, width: '100%', height: '100%', border: 'none' }}
          allow="autoplay; fullscreen; encrypted-media; picture-in-picture"
          allowFullScreen
        />
      )}

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
          {channel && audioBusy && <span>Эфир приостановлен — идёт разговор</span>}
          {channel && !audioBusy && stagedHere && (
            <span>{stagedMode === 'float' ? 'Идёт в плавающем окне' : 'Идёт в центре экрана'}</span>
          )}
        </div>
      )}
    </div>
  );
}

/** Полоса каналов: в узкой панели список вертикальный не помещается, а горизонтальный — да. */
function ChannelStrip({ channels, activeId, onPick }: {
  channels: VideoChannel[];
  activeId: string | null;
  onPick: (id: string) => void;
}) {
  const ref = useRef<HTMLDivElement>(null);

  // Выбранный канал держим на виду: полоса узкая, и после перезахода он мог остаться за краем
  const scrollToActive = useCallback(() => {
    const el = ref.current?.querySelector<HTMLElement>('[data-active="1"]');
    el?.scrollIntoView({ block: 'nearest', inline: 'nearest' });
  }, []);
  useEffect(scrollToActive, [activeId, scrollToActive]);

  return (
    <div
      ref={ref}
      style={{
        display: 'flex', gap: 3, overflowX: 'auto', flex: '0 1 auto', minWidth: 0,
        // Полоса прокрутки в шапке высотой 32px не помещается и режет кнопки
        scrollbarWidth: 'none',
      }}
    >
      {channels.map(c => {
        const on = c.id === activeId;
        return (
          <button
            key={c.id}
            data-active={on ? '1' : undefined}
            onClick={() => onPick(c.id)}
            title={c.nowPlaying ? `${c.title} — ${c.nowPlaying}` : c.title}
            aria-label={c.title}
            style={{
              flex: 'none', display: 'flex', alignItems: 'center', height: 22,
              padding: `0 ${SP.xs}px`, cursor: 'pointer',
              background: on ? C.bgSelected : 'transparent',
              border: `1px solid ${on ? C.accentMuted : 'transparent'}`,
              borderRadius: R.sm, fontFamily: FONT.sans, fontSize: FS.xs,
              color: on ? C.textHeading : C.textMuted, whiteSpace: 'nowrap',
            }}
          >
            {c.title}
          </button>
        );
      })}
    </div>
  );
}

function PanelBody({ children }: { children: React.ReactNode }) {
  return (
    <div style={{
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
