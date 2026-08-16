import type { ServerMessage } from '../types';
import { onMessage } from './signalr';

// Догоняющая генерация картинок: сущность, созданная при выключенном (или упавшем)
// генераторе, получает иконку/аватар позже — бэк шлёт image_backfilled в группу владельца.

// Места генерации — канон ImagePlaces.All / ImageBackfillKinds.* на бэке
export const IMAGE_PLACE = {
  icon: 'project-icon',
  avatar: 'persona-avatar',
} as const;

export type ImageGenKind = keyof typeof IMAGE_PLACE;
export type ImagePlaceKey = typeof IMAGE_PLACE[ImageGenKind];

// id сущности, которой дорисовали картинку в этом месте; null — сообщение не про нас.
// Аватар персоны отдельной обработки не требует: бэк дублирует его штатным
// personas_changed, по которому стор персон перечитывает список.
export function imageBackfillEntityId(msg: ServerMessage, place: ImagePlaceKey): string | null {
  if (msg.type !== 'image_backfilled' || msg.kind !== place) return null;
  return msg.entityId || null;
}

// Подписка на догнавшую картинку места. Возвращает отписку.
export function onImageBackfilled(place: ImagePlaceKey, handler: (entityId: string) => void): () => void {
  return onMessage(msg => {
    const id = imageBackfillEntityId(msg, place);
    if (id) handler(id);
  });
}
