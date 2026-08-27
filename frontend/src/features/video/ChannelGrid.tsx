import { ExternalLink, Play, Radio } from 'lucide-react';
import type { VideoChannel } from '../../types';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import { C } from '../../lib/design';
import { VideoCard } from './VideoCard';
import { VideoGrid } from './VideoGrid';

/**
 * Сетка телеканалов. Каналы делятся на два сорта, и разница видна на карточке:
 * играбельные открывают плеер, остальные уводят на сайт сервиса (их поток вещается
 * чужим плеером, привязанным к своему домену).
 */
export function ChannelGrid({ channels, onPlay }: {
  channels: VideoChannel[];
  onPlay: (c: VideoChannel) => void;
}) {
  return (
    <VideoGrid minWidth={190}>
      {channels.map(c => <ChannelCard key={`${c.provider}:${c.id}`} channel={c} onPlay={onPlay} />)}
    </VideoGrid>
  );
}

function ChannelCard({ channel, onPlay }: { channel: VideoChannel; onPlay: (c: VideoChannel) => void }) {
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
