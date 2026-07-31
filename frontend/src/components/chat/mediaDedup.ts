// Кросс-блочный дедуп медиа по ленте хода: один и тот же файл, пришедший в нескольких
// tool_result (glif: get_job_status/project_update + view_media/media_view), рендерится
// ОДНИМ MediaBlock. Выживший — финальная галерея (glif view_media, outputType media_view):
// она идёт после промежуточных статусов и несёт полный набор; промежуточные блоки
// скрывают URL, уже показанные в галерее. Между блоками одного приоритета выживает
// первый по ленте. У fal дубля нет данными (медиа приходит один раз), поэтому для него
// проход ничего не меняет — регрессии нет.
//
// Производительность: extract из результата кэшируется по ссылке на ChatItem (элементы
// ленты иммутабельны — редьюсер заменяет объект при изменении), так что построение карты
// на каждую стрим-дельту дёшево: парсятся только новые элементы.
import { useContext } from 'react';
import type { ChatItem } from '../../types';
import { MediaVisibilityContext } from './contexts';
import { extractMediaFromResult, extractMediaMeta, type MediaItem, type MediaMeta } from './MediaBlock';

export type ToolUseItem = Extract<ChatItem, { kind: 'tool_use' }>;

// Ключ дедупа: нормализованный URL (trim + без финального слэша). Регистр и query
// не трогаем: путь CDN регистрозначим, а query у cloudinary может задавать трансформацию —
// чрезмерная нормализация сливала бы разные файлы. Боевой дубль glif несёт дословно
// одинаковые URL, этого достаточно.
export function normalizeMediaUrl(url: string): string {
  const u = url.trim();
  return u.length > 1 && u.endsWith('/') ? u.slice(0, -1) : u;
}

const mediaCache = new WeakMap<ToolUseItem, MediaItem[]>();
const metaCache = new WeakMap<ToolUseItem, MediaMeta>();

// Медиа из результата tool-блока (кэш по ссылке на элемент ленты)
export function getItemMedia(item: ToolUseItem): MediaItem[] {
  let m = mediaCache.get(item);
  if (!m) {
    m = !item.isError && typeof item.result === 'string' ? extractMediaFromResult(item.result) : [];
    mediaCache.set(item, m);
  }
  return m;
}

// Метаданные генерации tool-блока (кэш по ссылке)
export function getItemMediaMeta(item: ToolUseItem, media?: MediaItem[]): MediaMeta {
  let meta = metaCache.get(item);
  if (!meta) {
    meta = typeof item.result === 'string' ? extractMediaMeta(item.result, media) : {};
    metaCache.set(item, meta);
  }
  return meta;
}

// Финальная галерея glif (view_media) — предпочтительный выживший блок при дедупе
function isFinalGallery(item: ToolUseItem, media: MediaItem[]): boolean {
  const meta = getItemMediaMeta(item, media);
  return meta.source === 'glif' && meta.outputType === 'media_view';
}

// Для каждого tool-блока ленты — видимый набор медиа после кросс-блочного дедупа.
// Блоки без медиа в карту не попадают; блок, чьё медиа скрыто целиком, — попадает
// с пустым массивом (чтобы рендер не пересчитывал extract и ушёл в обычную строку «готово»).
export function buildMediaVisibility(items: ChatItem[]): Map<string, MediaItem[]> {
  const visibility = new Map<string, MediaItem[]>();
  // Проход 1: финальные галереи в порядке ленты — их URL сильнее любых других блоков
  const galleryUrls = new Set<string>();
  for (const it of items) {
    if (it.kind !== 'tool_use') continue;
    const media = getItemMedia(it);
    if (media.length === 0 || !isFinalGallery(it, media)) continue;
    const visible: MediaItem[] = [];
    for (const m of media) {
      const key = normalizeMediaUrl(m.url);
      if (galleryUrls.has(key)) continue; // дубль ранней галереи
      galleryUrls.add(key);
      visible.push(m);
    }
    visibility.set(it.id, visible);
  }
  // Проход 2: остальные медиа-блоки скрывают то, что показано в галереях,
  // и дедупятся между собой по порядку ленты
  const claimed = new Set<string>();
  for (const it of items) {
    if (it.kind !== 'tool_use' || visibility.has(it.id)) continue;
    const media = getItemMedia(it);
    if (media.length === 0) continue;
    const visible: MediaItem[] = [];
    for (const m of media) {
      const key = normalizeMediaUrl(m.url);
      if (galleryUrls.has(key) || claimed.has(key)) continue;
      claimed.add(key);
      visible.push(m);
    }
    visibility.set(it.id, visible);
  }
  return visibility;
}

// Видимое медиа tool-блока: из карты дедупа, если лента её предоставила,
// иначе — прямой extract (рендер вне ChatPanel, тесты)
export function useVisibleMedia(item: ToolUseItem): MediaItem[] {
  const visibility = useContext(MediaVisibilityContext);
  if (!visibility) return getItemMedia(item);
  return visibility.get(item.id) ?? getItemMedia(item);
}
