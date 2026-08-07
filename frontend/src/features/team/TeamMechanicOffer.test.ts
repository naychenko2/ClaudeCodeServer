// Детект «отказались от механики» для гашения кнопки «Запустить»: карточка остаётся
// в ленте историей разговора, но после того, как диалог пошёл дальше без запуска,
// случайный дорогой клик должен быть невозможен. Отказ = новый ЖИВОЙ ход пользователя
// после карточки; служебные ходы (директива цикла, заметка штаба, авто-продолжение)
// отказом не считаются. Запущенная механика сюда не доходит — её раньше ловит launched.
import { describe, it, expect } from 'vitest';
import { hasUserTurnAfter, type FeedTurnLike } from './TeamMechanicOffer';

const user = (): FeedTurnLike => ({ kind: 'user_message' });
const assistant = (): FeedTurnLike => ({ kind: 'text' });

describe('hasUserTurnAfter — детект отказа от механики', () => {
  it('нет хода пользователя после карточки — отказа нет', () => {
    const items = [assistant(), assistant()];
    expect(hasUserTurnAfter(items, 0)).toBe(false);
  });

  it('новый ответ пользователя после карточки — отказ', () => {
    const items = [assistant(), user(), assistant()];
    expect(hasUserTurnAfter(items, 0)).toBe(true);
  });

  it('ход пользователя ДО карточки отказом не считается', () => {
    const items = [user(), assistant()];
    expect(hasUserTurnAfter(items, 1)).toBe(false);
  });

  it('служебные ходы после карточки отказом не считаются', () => {
    const items: FeedTurnLike[] = [
      assistant(),
      { kind: 'user_message', systemDirective: true },
      { kind: 'user_message', staffNote: 'сводка штаба' },
      { kind: 'user_message', auto: true },
      assistant(),
    ];
    expect(hasUserTurnAfter(items, 0)).toBe(false);
  });

  it('живой ход после служебных — всё равно отказ', () => {
    const items: FeedTurnLike[] = [
      assistant(),
      { kind: 'user_message', systemDirective: true },
      user(),
    ];
    expect(hasUserTurnAfter(items, 0)).toBe(true);
  });

  it('карточка — последний элемент ленты (стрим идёт) — отказа нет', () => {
    const items = [user(), assistant()];
    expect(hasUserTurnAfter(items, 1)).toBe(false);
  });
});
