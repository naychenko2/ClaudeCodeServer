// Детект «отказались от механики» для гашения кнопки «Запустить»: карточка остаётся
// в ленте историей разговора, но после того, как диалог пошёл дальше без запуска,
// случайный дорогой клик должен быть невозможен. Отказ = новый ЖИВОЙ ход пользователя
// после карточки; служебные ходы (директива цикла, заметка штаба, авто-продолжение)
// отказом не считаются. Запущенная механика сюда не доходит — её раньше ловит launched.
import { describe, it, expect } from 'vitest';
import {
  hasUserTurnAfter, buildMechanicOffers,
  type FeedTurnLike, type MechanicOfferItem,
} from './TeamMechanicOffer';

const user = (): FeedTurnLike => ({ kind: 'user_message' });
const assistant = (text = ''): MechanicOfferItem => ({ kind: 'text', text });
const offer = (text: string): MechanicOfferItem => ({ kind: 'text', text });

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

describe('buildMechanicOffers — карточка несёт последнее предложение', () => {
  const CONSENSUS = '<team-mechanic id="consensus" topic="первая тема"/>';
  const CONSENSUS_V2 = '<team-mechanic id="consensus" topic="свежая тема"/>';
  const EXPERT = '<team-mechanic id="panel" topic="тема экспертов"/>';

  it('одно предложение в чате — карточка одна', () => {
    const items = [user(), offer(CONSENSUS)];
    const map = buildMechanicOffers(items);
    expect(map.size).toBe(1);
    expect(map.get(1)).toEqual({ id: 'consensus', topic: 'первая тема' });
  });

  it('повторное предложение той же механики — карточка одна, у последнего, со свежим topic', () => {
    const items = [user(), offer(CONSENSUS), assistant(), offer(CONSENSUS_V2)];
    const map = buildMechanicOffers(items);
    expect(map.size).toBe(1);
    expect(map.has(1)).toBe(false); // старая карточка больше не несёт оффер
    expect(map.get(3)).toEqual({ id: 'consensus', topic: 'свежая тема' });
  });

  it('предложение после user_message + новый ответ модели — старая погашена, новая живая', () => {
    // Сценарий из задачи: модель предложила, пользователь уточнил, модель предложила ещё раз
    const items = [user(), offer(CONSENSUS), user(), offer(CONSENSUS_V2)];
    const map = buildMechanicOffers(items);
    expect(map.size).toBe(1);
    // declined для старой карточки (i=1) = true — user_message после неё был
    expect(hasUserTurnAfter(items, 1)).toBe(true);
    // declined для новой карточки (i=3) = false — user_message после неё нет
    expect(hasUserTurnAfter(items, 3)).toBe(false);
    // В ленте осталась одна живая карточка у последнего предложения
    expect(map.get(3)?.topic).toBe('свежая тема');
  });

  it('разные механики в одном чате — у каждой своя карточка', () => {
    const items = [user(), offer(CONSENSUS), offer(EXPERT)];
    const map = buildMechanicOffers(items);
    expect(map.size).toBe(2);
    expect(map.get(1)?.id).toBe('consensus');
    expect(map.get(2)?.id).toBe('panel');
  });

  it('текст сабагента (parentToolUseId) не порождает карточку', () => {
    const items: MechanicOfferItem[] = [
      user(),
      { kind: 'text', text: CONSENSUS, parentToolUseId: 'sub-1' },
    ];
    expect(buildMechanicOffers(items).size).toBe(0);
  });

  it('пустой/без маркера текст — карточек нет', () => {
    const items = [user(), assistant('обычный ответ без маркера')];
    expect(buildMechanicOffers(items).size).toBe(0);
  });
});
