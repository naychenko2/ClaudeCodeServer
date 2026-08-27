// Детект «отказались от механики» для гашения кнопки «Запустить»: карточка остаётся
// в ленте историей разговора, но после того, как диалог пошёл дальше без запуска,
// случайный дорогой клик должен быть невозможен. Отказ = новый ЖИВОЙ ход пользователя
// после карточки; служебные ходы (директива цикла, заметка штаба, авто-продолжение)
// отказом не считаются. Запущенная механика сюда не доходит — её раньше ловит launched.
import { describe, it, expect } from 'vitest';
import {
  hasUserTurnAfter, hasLaunchedAfter, hasFailedLaunchAfter, buildMechanicOffers,
  type FeedTurnLike, type MechanicOfferItem,
} from './TeamMechanicOffer';

const user = (text = 'обычное сообщение'): MechanicOfferItem => ({ kind: 'user_message', text });
const assistant = (text = ''): MechanicOfferItem => ({ kind: 'text', text });
const offer = (text: string): MechanicOfferItem => ({ kind: 'text', text });
const error = (): MechanicOfferItem => ({ kind: 'error', text: 'упал ход' });
const result = (): MechanicOfferItem => ({ kind: 'result' });

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

describe('hasLaunchedAfter — детект запуска механики', () => {
  const CONSENSUS = '<team-mechanic id="consensus" topic="тема"/>';

  it('нет user_message после карточки — запуска нет', () => {
    const items = [assistant(), assistant()];
    expect(hasLaunchedAfter(items, 0, 'consensus')).toBe(false);
  });

  it('user_message с командой этой механики после карточки — запуск', () => {
    const items = [assistant(CONSENSUS), user('/oh-my-claudecode:ralplan "тема"'), assistant()];
    expect(hasLaunchedAfter(items, 0, 'consensus')).toBe(true);
  });

  it('запуск ДО карточки её не гасит — старая карточка тоже снята, но новая живая', () => {
    // Сценарий из задачи: чат-штаб с прошлым /team-implement, потом модель заново предложила.
    const items = [
      user('/team-implement {"task":"прошлая"}'),
      assistant(CONSENSUS),
      assistant('свежий текст'),
    ];
    // Запуск был до нового маркера — для новой карточки launched = false
    expect(hasLaunchedAfter(items, 1, 'consensus')).toBe(false);
  });

  it('запуск ДРУГОЙ механики не гасит карточку', () => {
    const items = [
      assistant(CONSENSUS),
      user('/team-implement {"task":"другая"}'),
    ];
    expect(hasLaunchedAfter(items, 0, 'consensus')).toBe(false);
  });

  it('служебный user_message (systemDirective/staffNote/auto) запуском не считается', () => {
    const items: MechanicOfferItem[] = [
      assistant(CONSENSUS),
      { kind: 'user_message', text: '/oh-my-claudecode:ralplan "тема"', systemDirective: true },
      { kind: 'user_message', text: '/oh-my-claudecode:ralplan "тема"', staffNote: 'штаб' },
      { kind: 'user_message', text: '/oh-my-claudecode:ralplan "тема"', auto: true },
    ];
    expect(hasLaunchedAfter(items, 0, 'consensus')).toBe(false);
  });

  it('прошлый запуск и новая карточка той же механики → кнопка активна (главный сценарий)', () => {
    // В чате раньше уже был /team-implement; теперь модель заново предложила ту же механику.
    const items = [
      user('/team-implement {"task":"прошлый"}'),
      assistant('что-то'),
      assistant(CONSENSUS),
    ];
    // hasLaunchedAfter смотрит ТОЛЬКО после индекса карточки — старый запуск игнорируется
    expect(hasLaunchedAfter(items, 2, 'implement')).toBe(false);
  });
});

describe('hasFailedLaunchAfter — детект провалившегося запуска', () => {
  const CONSENSUS = '<team-mechanic id="consensus" topic="тема"/>';

  it('error без user_message с командой — провала нет (карточка вообще не запущена)', () => {
    const items = [assistant(CONSENSUS), error()];
    expect(hasFailedLaunchAfter(items, 0)).toBe(false);
  });

  it('user_message + error — провал (кнопка «Повторить»)', () => {
    const items = [
      assistant(CONSENSUS),
      user('/oh-my-claudecode:ralplan "тема"'),
      error(),
    ];
    expect(hasFailedLaunchAfter(items, 0)).toBe(true);
  });

  it('user_message + result — штатный запуск, не провал', () => {
    const items = [
      assistant(CONSENSUS),
      user('/oh-my-claudecode:ralplan "тема"'),
      result(),
    ];
    expect(hasFailedLaunchAfter(items, 0)).toBe(false);
  });

  it('error ДО команды провалом запуска не считается', () => {
    const items = [
      assistant(CONSENSUS),
      error(),
      user('/oh-my-claudecode:ralplan "тема"'),
      result(),
    ];
    expect(hasFailedLaunchAfter(items, 0)).toBe(false);
  });
});
