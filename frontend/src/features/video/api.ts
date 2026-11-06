// Домен «Видео» (вынесено из lib/api.ts, docs/research/frontend-api-split-2026-11.md):
// эфиры телеканалов и лента подписок YouTube. Тонкая обёртка над живым REST;
// DTO-типы — types/index.ts (переедут в эту папку в S5-P3).
import { request } from '../../lib/offline';
import type {
  VideoChannelsResponse,
  VideoFavoritesResponse,
  VideoFeedResponse,
  VideoProviderInfo,
} from '../../types';

// Методы домена; lib/api.ts подмешивает их в объект api spread'ом
// (адреса api.video.* остаются прежними для всех потребителей).
export const videoApi = {
  // Раздел «Видео»: эфиры телеканалов и лента подписок YouTube.
  // live: true у списков — офлайн-кэш здесь ВРЕДЕН: у карточки канала есть поле
  // «сейчас в эфире», и подставленный из IndexedDB ответ показывал бы передачу,
  // которая шла полчаса назад, как текущую (тот же класс вранья, что у статуса выкатки).
  video: {
    providers: () => request<VideoProviderInfo[]>('/video/providers', { live: true }),
    // refresh: true — кнопка «Обновить». Без него провайдер отдаёт из кеша (минута у
    // программы передач, полчаса у ленты), и нажатие визуально не делало бы ничего.
    channels: (provider: string, refresh = false) =>
      request<VideoChannelsResponse>(
        `/video/channels?provider=${encodeURIComponent(provider)}${refresh ? '&refresh=true' : ''}`,
        { live: true }),
    feed: (provider: string, channelId?: string, refresh = false) =>
      request<VideoFeedResponse>(
        `/video/feed?provider=${encodeURIComponent(provider)}`
        + (channelId ? `&channelId=${encodeURIComponent(channelId)}` : '')
        + (refresh ? '&refresh=true' : ''),
        { live: true }),
    // Избранные каналы полосы. live: true — как и у каналов: подставленный из офлайн-кэша
    // набор увёл бы полосу на чужой состав и первое же нажатие звёздочки сохранило бы его.
    favorites: () => request<VideoFavoritesResponse>('/video/favorites', { live: true }),
    setFavorites: (keys: string[]) =>
      request<VideoFavoritesResponse>('/video/favorites', {
        method: 'PUT', body: JSON.stringify({ keys }), live: true,
      }),
    youtubeAuthUrl: () => request<{ url: string }>('/video/youtube/auth-url'),
    youtubeDisconnect: () => request<{ ok: boolean }>('/video/youtube/disconnect', { method: 'POST' }),
  },
};
