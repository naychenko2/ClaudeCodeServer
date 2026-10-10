// Главная кнопка карточки: для каждого вида ровно одна главная кнопка или блок хода, тексты подтверждений
// прежние, отмена чужого обучения — только админу.
import { describe, expect, it, vi } from 'vitest';
import { createElement, isValidElement, type ReactNode } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { CharacterHero } from './CharacterHero';
import { HERO } from './characterTexts';
import { TRAIN_ETA_UNKNOWN } from './loraTrainTexts';
import { Notice } from 'aihome_shell/kit';
import { characterCardView } from './characterCardView';
import type { TrainSectionView } from './loraTrainView';
import { TRAIN_TEXT } from './loraTrainTexts';
import { confirmParts } from './loraTrainView';
import type { useCharacterTrain } from './useCharacterTrain';

// Для проверки обработчиков без DOM: единственный useState героя (модалка) подменяем шпионом
const setModal = vi.fn();
vi.mock('react', async orig => ({ ...(await orig<typeof import('react')>()), useState: (init: unknown) => [init, setModal] }));

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

// Разбор дерева элементов без рендера: CharacterHero вызывается как функция
type El = { type: unknown; props: Record<string, unknown> };
const walk = (node: ReactNode, out: El[] = []): El[] => {
  if (Array.isArray(node)) node.forEach(n => walk(n, out));
  else if (isValidElement(node)) {
    const el = node as unknown as El;
    out.push(el);
    walk(el.props.children as ReactNode, out);
    walk(el.props.action as ReactNode, out);
  }
  return out;
};
const text = (n: ReactNode): string => Array.isArray(n) ? n.map(text).join('') : typeof n === 'string' ? n : isValidElement(n) ? text((n.props as { children?: ReactNode }).children) : '';
const tree = (view: TrainSectionView, o: { dismiss?: () => void; session?: string | null; connect?: () => Promise<boolean> } = {}) => {
  const { action } = characterCardView({ photos: 5, train: 25, view, versions: [], own: null });
  const train = { ...mkTrain(view), dismiss: o.dismiss ?? (() => {}) } as unknown as ReturnType<typeof useCharacterTrain>;
  const versions = { pending: [], ctl: o.connect ? { connect: o.connect } : null, error: null } as never;
  return walk(CharacterHero({
    action, train, versions, slug: 'a', projectId: 'p', hasConnected: false,
    contextSessionId: o.session === undefined ? null : o.session, onOpenPhotos: () => {}, onOpenTune: () => {},
  }));
};
const byText = (els: El[], label: string) => els.find(e => e.props.onClick !== undefined || e.props.disabled !== undefined ? text(e.props.children as ReactNode).includes(label) : false);

describe('CharacterHero: поведение кнопок', () => {
  it('«Отменить обучение» в блоке хода открывает подтверждение отмены', () => {
    setModal.mockClear();
    const btn = byText(tree({ kind: 'running', text: 'Обучаем', percent: 10, etaText: 'Осталось' }), HERO.cancel);
    expect(btn).toBeDefined();
    (btn!.props.onClick as () => void)();
    expect(setModal).toHaveBeenCalledWith('cancel');
  });

  it('очередь без срока показывает строку «срок неизвестен», как running', () => {
    expect(render({ kind: 'queued' })).toContain('Идёт обучение, оно займёт около');
    expect(TRAIN_ETA_UNKNOWN('2 часа')).toContain('около 2 часа');
  });

  it('без чата «В контекст чата» disabled', () => {
    const btn = byText(tree({ kind: 'done', version: ver, first: true, meta: '', photosChanged: false }), HERO.toChatContext);
    expect(btn!.props.disabled).toBe(true);
  });

  it('крестик у первой версии вызывает dismiss(runId)', () => {
    const dismiss = vi.fn();
    const x = tree({ kind: 'done', version: ver, first: true, meta: '', photosChanged: false }, { dismiss })
      .find(e => e.props.ariaLabel === 'Закрыть');
    (x!.props.onClick as () => void)();
    expect(dismiss).toHaveBeenCalledWith('r1');
  });

  it('«Подключить» у не первой версии подключает runId и после успеха закрывает плашку', async () => {
    const dismiss = vi.fn();
    const connect = vi.fn(async () => true);
    const btn = byText(tree({ kind: 'done', version: ver, first: false, meta: '', photosChanged: false }, { dismiss, connect }), TRAIN_TEXT.connect);
    (btn!.props.onClick as () => void)();
    await Promise.resolve(); await Promise.resolve();
    expect(connect).toHaveBeenCalledWith('r1');
    expect(dismiss).toHaveBeenCalledWith('r1');
  });

  it('«Подключить» при неудаче плашку не закрывает', async () => {
    const dismiss = vi.fn();
    const btn = byText(tree({ kind: 'done', version: ver, first: false, meta: '', photosChanged: false }, { dismiss, connect: async () => false }), TRAIN_TEXT.connect);
    (btn!.props.onClick as () => void)();
    await Promise.resolve(); await Promise.resolve();
    expect(dismiss).not.toHaveBeenCalled();
  });

  it('busyOther — Notice тона info', () => {
    const n = tree({ kind: 'busyOther', text: 'Занято', canStopForeign: false }).find(e => e.type === Notice);
    expect(n!.props.tone).toBe('info');
  });
});
