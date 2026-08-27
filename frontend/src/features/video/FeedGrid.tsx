import { Play } from 'lucide-react';
import type { VideoItem } from '../../types';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import { C } from '../../lib/design';
import { VideoCard } from './VideoCard';
import { VideoGrid } from './VideoGrid';

/** Лента роликов: что вышло на каналах, на которые подписан владелец. */
export function FeedGrid({ items, onPlay }: {
  items: VideoItem[];
  onPlay: (i: VideoItem) => void;
}) {
  return (
    // Шире каналов ТОЛЬКО на десктопе: у ролика длинное название в две строки и подпись
    // с датой. На узком экране ширину диктует общий минимум обёртки — см. VideoGrid.
    <VideoGrid minWidth={230}>
      {items.map(i => (
        <VideoCard
          key={`${i.provider}:${i.id}`}
          coverUrl={i.thumbnailUrl}
          fallbackIcon={<Play size={ICON_SIZE.xl} strokeWidth={ICON_STROKE} color={C.textMuted} />}
          title={i.title}
          subtitle={[i.channelTitle, formatWhen(i.publishedAt)].filter(Boolean).join(' · ')}
          hint={i.title}
          onClick={() => onPlay(i)}
        />
      ))}
    </VideoGrid>
  );
}

/** Дата публикации по-человечески: «сегодня», «вчера», «3 дня назад», дальше — датой. */
function formatWhen(iso: string | null): string {
  if (!iso) return '';
  const date = new Date(iso);
  if (Number.isNaN(date.getTime())) return '';

  const days = Math.floor((Date.now() - date.getTime()) / 86_400_000);
  if (days <= 0) return 'сегодня';
  if (days === 1) return 'вчера';
  if (days < 7) return `${days} ${plural(days, 'день', 'дня', 'дней')} назад`;
  return date.toLocaleDateString('ru-RU', { day: 'numeric', month: 'short' });
}

function plural(n: number, one: string, few: string, many: string): string {
  const mod10 = n % 10;
  const mod100 = n % 100;
  if (mod10 === 1 && mod100 !== 11) return one;
  if (mod10 >= 2 && mod10 <= 4 && (mod100 < 12 || mod100 > 14)) return few;
  return many;
}
