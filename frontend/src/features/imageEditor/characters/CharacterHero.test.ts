// Главная кнопка карточки: для каждого вида ровно одна главная кнопка или блок хода, тексты подтверждений
// прежние, отмена чужого обучения — только админу.
import { describe, expect, it } from 'vitest';
import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { CharacterHero } from './CharacterHero';
import { characterCardView } from './characterCardView';
import type { TrainSectionView } from './loraTrainView';
import { TRAIN_TEXT } from './loraTrainTexts';
import { confirmParts } from './loraTrainView';
import type { useCharacterTrain } from './useCharacterTrain';

const ver = { runId: 'r1' } as never;
const form = (o: object = {}) => ({ kind: 'form', label: '', disabled: false, reason: null, stopped: false, photosChanged: false, ...o }) as TrainSectionView;
const mkTrain = (view: TrainSectionView, isAdmin = false) => ({
  view, isAdmin, edited: null, dismiss: () => {}, actionError: null, connectError: null,
  training: { busy: false, start: async () => {}, cancel: async () => {}, refresh: async () => {} },
}) as unknown as ReturnType<typeof useCharacterTrain>;

const render = (view: TrainSectionView, o: { train?: number | null; isAdmin?: boolean; session?: string | null } = {}) => {
  const train = o.train === undefined ? 25 : o.train;
  const { action } = characterCardView({ photos: 5, train, view, versions: [], own: null });
  return renderToStaticMarkup(createElement(CharacterHero, {
    action, train: mkTrain(view, o.isAdmin), versions: { pending: [], ctl: null, error: null } as never,
    slug: 'a', projectId: 'p', hasConnected: false, contextSessionId: o.session === undefined ? null : o.session,
    onOpenPhotos: () => {}, onOpenTune: () => {},
  }));
};
const mains = (h: string) => h.split('data-hero="main"').length - 1;

describe('CharacterHero', () => {
  const withMain: [string, TrainSectionView, number | null][] = [
    ['loading', { kind: 'loading' }, 25],
    ['form, фото хватает', form(), 25],
    ['form, фото мало', form(), 5],
    ['form, триггер', form({ reason: 'trigger', disabled: true }), 25],
    ['form, stopped', form({ stopped: true }), 25],
    ['form, photosChanged', form({ photosChanged: true }), 25],
    ['queued', { kind: 'queued' }, 25],
    ['running', { kind: 'running', text: 'Обучаем', percent: 10, etaText: 'Осталось' }, 25],
    ['failed', { kind: 'failed', version: ver, allRejected: false, lost: false }, 25],
    ['failed allRejected', { kind: 'failed', version: ver, allRejected: true, lost: false }, 25],
    ['done первая', { kind: 'done', version: ver, first: true, meta: '', photosChanged: false }, 25],
    ['done новая', { kind: 'done', version: ver, first: false, meta: '', photosChanged: true }, 25],
  ];
  it.each(withMain)('%s: ровно одна главная кнопка или блок хода', (_n, v, t) => {
    expect(mains(render(v, { train: t, session: 's1' }))).toBe(1);
  });

  it('без чата «В контекст чата» disabled с подсказкой', () => {
    const h = render({ kind: 'done', version: ver, first: true, meta: '', photosChanged: false });
    expect(mains(h)).toBe(1);
    expect(h).toContain('Откройте чат');
  });

  it.each([
    ['off', { kind: 'off' }, TRAIN_TEXT.disabled],
    ['noLocal', { kind: 'noLocal' }, TRAIN_TEXT.noLocal],
    ['busyMine', { kind: 'busyMine' }, TRAIN_TEXT.busyMine],
    ['busyOther', { kind: 'busyOther', text: 'Занято', canStopForeign: false }, 'Занято'],
  ] as [string, TrainSectionView, string][])('%s: только Notice, кнопок хода нет', (_n, v, text) => {
    const h = render(v);
    expect(mains(h)).toBe(0);
    expect(h).toContain(text);
  });

  it('новая версия: «Подключить» и «Оставить прежнюю»; первая — нет', () => {
    const n = render({ kind: 'done', version: ver, first: false, meta: '', photosChanged: false });
    expect(n).toContain(TRAIN_TEXT.connect);
    expect(n).toContain(TRAIN_TEXT.keepOld);
    const f = render({ kind: 'done', version: ver, first: true, meta: '', photosChanged: false });
    expect(f).not.toContain(TRAIN_TEXT.keepOld);
  });

  it('отмена чужого обучения — только админу', () => {
    const v: TrainSectionView = { kind: 'busyOther', text: 'Занято', canStopForeign: true };
    expect(render(v, { isAdmin: true })).toContain(TRAIN_TEXT.stopForeign);
    expect(render(v, { isAdmin: false })).not.toContain(TRAIN_TEXT.stopForeign);
  });

  it('триггер: кнопка отключена, есть «Тонкая настройка»', () => {
    const h = render(form({ reason: 'trigger', disabled: true }));
    expect(h).toContain('Тонкая настройка');
    expect(h).toContain('disabled');
  });

  it('тексты подтверждения старта прежние', () => {
    expect(confirmParts(false)).toEqual({
      before: 'Видеокарта будет занята примерно ', bold: '2 часа',
      after: '. Всё это время новые картинки на локальной модели будут стоять в очереди и ждать. Отменить обучение можно в любой момент.',
      note: null,
    });
    expect(confirmParts(true).note).toBe(TRAIN_TEXT.confirmConnected);
  });
});
