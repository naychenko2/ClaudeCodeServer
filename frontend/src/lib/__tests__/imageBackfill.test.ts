import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { ServerMessage } from '../../types';

// Обработчик события догоняющей генерации: какое сообщение считается «нашим» и что
// уезжает подписчику. Ошибка здесь молчаливая — иконка просто не появится сама,
// а человеку мы уже пообещали, что появится.

// Подменяем сокет: тесту нужен сам фильтр сообщений, а не соединение
const handlers: ((msg: ServerMessage) => void)[] = [];
const off = vi.fn();
vi.mock('../signalr', () => ({
  onMessage: (h: (msg: ServerMessage) => void) => {
    handlers.push(h);
    return off;
  },
}));

const { IMAGE_PLACE, imageBackfillEntityId, onImageBackfilled } = await import('../imageBackfill');

const backfilled = (kind: 'project-icon' | 'persona-avatar', entityId: string): ServerMessage =>
  ({ type: 'image_backfilled', kind, entityId });

beforeEach(() => {
  handlers.length = 0;
  off.mockClear();
});

describe('какое сообщение про нас', () => {
  it('иконка проекта — отдаёт id проекта', () => {
    expect(imageBackfillEntityId(backfilled('project-icon', 'p1'), IMAGE_PLACE.icon)).toBe('p1');
  });

  it('аватар персоны в место иконки не течёт', () => {
    expect(imageBackfillEntityId(backfilled('persona-avatar', 'x1'), IMAGE_PLACE.icon)).toBeNull();
    expect(imageBackfillEntityId(backfilled('persona-avatar', 'x1'), IMAGE_PLACE.avatar)).toBe('x1');
  });

  it('чужое событие игнорируется', () => {
    const other: ServerMessage = { type: 'personas_changed', action: 'updated', personaId: 'x1' };
    expect(imageBackfillEntityId(other, IMAGE_PLACE.icon)).toBeNull();
  });

  it('пустой entityId — не повод дёргать перечитывание', () => {
    expect(imageBackfillEntityId(backfilled('project-icon', ''), IMAGE_PLACE.icon)).toBeNull();
  });
});

describe('подписка', () => {
  it('зовёт обработчик только на своё место и отдаёт id сущности', () => {
    const seen: string[] = [];
    onImageBackfilled(IMAGE_PLACE.icon, id => seen.push(id));
    handlers.forEach(h => h(backfilled('persona-avatar', 'x1')));
    handlers.forEach(h => h(backfilled('project-icon', 'p1')));
    expect(seen).toEqual(['p1']);
  });

  it('возвращает отписку сокета', () => {
    const unsubscribe = onImageBackfilled(IMAGE_PLACE.icon, () => {});
    unsubscribe();
    expect(off).toHaveBeenCalled();
  });
});
