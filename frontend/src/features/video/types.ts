// Типы домена «Видео» (переехали из types/index.ts, S5-P3). types/index.ts ре-экспортирует их через `export type *` — внешние импорты не меняются.

// === Раздел «Видео» ===

// Что источник умеет показывать: live — прямые эфиры (канал и есть содержимое),
// feed — лента подписок (канал = подписка, смотрят ролики из неё).
export type VideoProviderKind = 'live' | 'feed';

// Отказы источника разведены классами: каждому в разделе отвечает своё пустое
// состояние. «Подключите аккаунт» вместо «кончилась квота» — это человек, который
// жмёт не ту кнопку.
export type VideoError = 'not-configured' | 'needs-auth' | 'quota-exceeded' | 'unreachable';

export interface VideoProviderInfo {
  key: string;
  title: string;
  kind: VideoProviderKind;
  connected: boolean;
  needsAuth: boolean;
}

export interface VideoChannel {
  id: string;
  provider: string;
  title: string;
  // Можно ли играть внутри продукта. У эфиров это не все каналы каталога: часть
  // вещается чужим плеером по домену-реферреру, их уводим наружу по externalUrl.
  embeddable: boolean;
  embedUrl: string | null;
  externalUrl: string | null;
  coverUrl: string | null;
  // Что идёт сейчас (EPG). Приходит и у неиграбельных каналов.
  nowPlaying: string | null;
}

export interface VideoItem {
  id: string;
  provider: string;
  title: string;
  channelId: string;
  channelTitle: string;
  thumbnailUrl: string | null;
  publishedAt: string | null;
  embedUrl: string;
  externalUrl: string;
}

export interface VideoChannelsResponse {
  error: VideoError | null;
  channels: VideoChannel[];
}

export interface VideoFavoritesResponse {
  // Настраивал ли человек набор сам. false — keys это дефолт сервера, и пустым он не
  // бывает; true с пустым keys — осознанно снятые звёздочки, полоса честно пуста.
  configured: boolean;
  // Ключи каналов вида "smotrim:1" (провайдер + id).
  keys: string[];
}

export interface VideoFeedResponse {
  error: VideoError | null;
  items: VideoItem[];
}
