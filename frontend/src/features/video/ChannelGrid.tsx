import { ExternalLink, Play, Radio, Star } from 'lucide-react';
import type { VideoChannel } from '../../types';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import { C, R, SHADOW } from '../../lib/design';
import { channelKey, toggleFavorite, useFavoriteKeys } from '../../lib/videoFavorites';
import { VideoCard } from './VideoCard';
import { VideoGrid } from './VideoGrid';

/**
 * Сетка телеканалов. Каналы делятся на два сорта, и разница видна на карточке:
 * играбельные открывают плеер, остальные уводят на сайт сервиса (их поток вещается
 * чужим плеером, привязанным к своему домену).
 *
 * Здесь же отмечают избранное — то, что потом показывает полоса каналов в шапке
 * панели, центра и плавающего окна. Каталог для этого и открывают: выбирают глазами,
 * по обложкам.
 */
export function ChannelGrid({ channels, onPlay }: {
  channels: VideoChannel[];
  onPlay: (c: VideoChannel) => void;
}) {
  const { keys } = useFavoriteKeys();
  const favorites = new Set(keys ?? []);

  return (
    <VideoGrid minWidth={190}>
      {channels.map(c => (
        <ChannelCard
          key={channelKey(c)}
          channel={c}
          onPlay={onPlay}
          favorite={favorites.has(channelKey(c))}
          // Набор ещё не приехал — звёздочку не рисуем: нажатие до загрузки ничего
          // не сделало бы, а выглядело бы как поломка
          canFavorite={keys !== null}
        />
      ))}
    </VideoGrid>
  );
}

function ChannelCard({ channel, onPlay, favorite, canFavorite }: {
  channel: VideoChannel;
  onPlay: (c: VideoChannel) => void;
  favorite: boolean;
  canFavorite: boolean;
}) {
  const playable = channel.embeddable && !!channel.embedUrl;

  const open = () => {
    if (playable) onPlay(channel);
    else if (channel.externalUrl) window.open(channel.externalUrl, '_blank', 'noopener,noreferrer');
  };

  return (
    <VideoCard
      coverUrl={channel.coverUrl}
      fallbackIcon={<Radio size={ICON_SIZE.xl} strokeWidth={ICON_STROKE} color={C.textMuted} />}
      badge={playable
        ? <Play size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} fill="currentColor" />
        : <ExternalLink size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />}
      // Звёздочка только у играбельных: полоса каналов показывает то, что можно
      // СМОТРЕТЬ, и канал-ссылка на чужой сайт в ней оказался бы кнопкой в никуда
      corner={playable && canFavorite
        ? <FavoriteStar channel={channel} favorite={favorite} />
        : undefined}
      title={channel.title}
      // Пометка обязана пережить программу передач: без неё у неиграбельного канала
      // остаётся только мелкая стрелка в углу, и уход на чужой сайт становится сюрпризом
      note={playable ? undefined : 'на сайте'}
      // Без программы передач подпись не должна повторять пометку «на сайте»:
      // в карточке всего две строки текста, и обе про одно — это пустая трата места
      subtitle={channel.nowPlaying || (playable ? 'Прямой эфир' : 'Эфир на сайте канала')}
      hint={playable ? `Смотреть: ${channel.title}` : `Открыть «${channel.title}» на сайте сервиса`}
      onClick={open}
    />
  );
}

/**
 * Звёздочка избранного. Видна ВСЕГДА, а не по наведению: каталог открывают и с планшета,
 * где наведения нет вовсе, — спрятанная под hover кнопка там недостижима. Отмеченная
 * горит акцентом, неотмеченная приглушена, поэтому шумом она не становится.
 */
function FavoriteStar({ channel, favorite }: { channel: VideoChannel; favorite: boolean }) {
  return (
    <button
      onClick={e => { e.stopPropagation(); void toggleFavorite(channel); }}
      title={favorite ? 'Убрать из избранного' : 'В избранное — канал появится в полосе'}
      aria-label={favorite ? 'Убрать из избранного' : 'Добавить в избранное'}
      aria-pressed={favorite}
      style={{
        display: 'flex', alignItems: 'center', justifyContent: 'center',
        // Тач-цель: значок мелкий, а промахнуться пальцем по соседней карточке легко
        width: 28, height: 28, padding: 0, borderRadius: R.full,
        // Тёмная плашка той же плотности, что у значка сорта карточки: под кнопкой
        // обложка канала любого цвета, и темозависимый фон на светлой пропадал бы
        background: C.mediaScrim, border: 'none', cursor: 'pointer',
        color: favorite ? C.accent : C.onDark,
        boxShadow: favorite ? SHADOW.card : undefined,
      }}
    >
      <Star
        size={ICON_SIZE.xs}
        strokeWidth={ICON_STROKE}
        fill={favorite ? 'currentColor' : 'none'}
        // Неотмеченная звезда приглушена: в сетке из сорока карточек сорок ярких
        // значков перетянули бы внимание с самих обложек
        style={{ opacity: favorite ? 1 : 0.75 }}
      />
    </button>
  );
}
