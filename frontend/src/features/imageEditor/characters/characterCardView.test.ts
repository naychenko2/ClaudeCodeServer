import { describe, expect, it } from 'vitest';
import type { LoraVersionDto } from '../api';
import { C } from '../../../lib/design';
import { characterCardView, type CardViewInput } from './characterCardView';
import { METER } from './characterTexts';
import type { TrainSectionView } from './loraTrainView';

const ver = (p: Partial<LoraVersionDto> = {}): LoraVersionDto => ({
  runId: 'r1', status: 'ready', photosCount: 30, connected: false, trainedAt: '2026-10-10T09:00:00Z', title: 'Весна', ...p,
});
const form = (p: object = {}): TrainSectionView =>
  ({ kind: 'form', label: '', disabled: false, reason: null, stopped: false, photosChanged: false, ...p }) as TrainSectionView;
const input = (p: Partial<CardViewInput> = {}): CardViewInput =>
  ({ photos: 5, train: 25, view: form(), versions: [], own: null, ...p });
const running = (percent: number | null, etaText: string | null = null): TrainSectionView =>
  ({ kind: 'running', text: '', percent, etaText });

describe('состояния §4', () => {
  it('2: не обучен, фото хватает', () => {
    const r = characterCardView(input());
    expect(r.state).toBe(2);
    expect(r.say).toEqual({ key: 'ready' });
    expect(r.badge).toBeNull();
    expect(r.action).toEqual({ kind: 'train', disabled: false, fixTrigger: false });
    expect(r.stops[2]).toMatchObject({ value: 0, titleKey: 'similar', hint: METER.afterTrain });
  });
  it('2: число фото для обучения ещё грузится — фраза без числа', () => {
    const r = characterCardView(input({ train: null, view: form({ reason: 'photos' }) }));
    expect(r.say).toEqual({ key: 'ready' });
  });
  it('2: не хватает фото для обучения', () => {
    const r = characterCardView(input({ train: 14, view: form({ reason: 'photos' }) }));
    expect(r.say).toEqual({ key: 'needTrain', n: 6 });
    expect(r.action).toEqual({ kind: 'addTrainPhotos', count: 14 });
    expect(r.stops[1].hint).toBe(METER.trainNeed(14, 20));
  });
  it('2: обучение недоступно (off/noLocal) — фраза без призыва, не ready', () => {
    for (const kind of ['off', 'noLocal'] as const) {
      const r = characterCardView(input({ train: 5, view: { kind } as TrainSectionView }));
      expect(r.say).toEqual({ key: 'readyNoTrain' });
    }
  });
  it('2: слово-метка не подходит — кнопка выключена', () => {
    expect(characterCardView(input({ view: form({ reason: 'trigger' }) })).action)
      .toEqual({ kind: 'train', disabled: true, fixTrigger: true });
  });
  it('2: особые виды — notice', () => {
    for (const kind of ['loading', 'off', 'noLocal', 'busyMine'] as const) {
      const view = { kind } as TrainSectionView;
      expect(characterCardView(input({ view })).action).toEqual({ kind: 'notice', view });
    }
  });
  it('3: в очереди', () => {
    const r = characterCardView(input({ view: { kind: 'queued' } }));
    expect(r.state).toBe(3);
    expect(r.say).toEqual({ key: 'queued' });
    expect(r.action).toEqual({ kind: 'progress' });
    expect(r.stops[2]).toMatchObject({ value: 8, estimate: true, titleKey: 'queued', hint: METER.queuedNote });
  });
  it('3: идёт, с процентом и без', () => {
    const a = characterCardView(input({ view: running(40, '10 мин') }));
    expect(a.say).toEqual({ key: 'running' });
    expect(a.stops[2]).toMatchObject({ value: 40, estimate: false, hint: METER.eta('10 мин'), titleColor: C.accent });
    const b = characterCardView(input({ view: running(null) }));
    expect(b.stops[2]).toMatchObject({ value: 8, estimate: true, hint: '' });
  });
  it('3: подпись деления — «Обучается · N %», а не «Заметно похож»', () => {
    const a = characterCardView(input({ view: running(40) })).stops[2];
    expect(a).toMatchObject({ titleKey: 'running', percent: 40 });
    const b = characterCardView(input({ view: running(null) })).stops[2];
    expect(b).toMatchObject({ titleKey: 'running', percent: null });
  });
  it('3: идущее обучение важнее подключённой версии', () => {
    expect(characterCardView(input({ view: running(10), versions: [ver({ connected: true })] })).state).toBe(3);
  });
  it('4: подключённая версия', () => {
    const r = characterCardView(input({ versions: [ver({ connected: true })] }));
    expect(r.state).toBe(4);
    expect(r.say).toEqual({ key: 'trained', title: 'Весна' });
    expect(r.badge).toEqual({ text: 'done', tone: 'success' });
    expect(r.action).toEqual({ kind: 'toContext' });
    expect(r.stops[2]).toMatchObject({ value: 100, titleColor: C.successText });
    expect(r.stops[2].hint).toMatch(/^обучен /);
  });
  it('4: своя версия', () => {
    const r = characterCardView(input({ own: 'my.safetensors', versions: [ver({ connected: true })] }));
    expect(r.say).toEqual({ key: 'trainedOwn' });
    expect(r.stops[2].hint).toBe(METER.own);
  });
  it('4: только своя, без версий', () => {
    expect(characterCardView(input({ own: 'my.safetensors' })).state).toBe(4);
  });
  it('4: вид done без подключённой версии', () => {
    const version = ver();
    const r = characterCardView(input({ view: { kind: 'done', version, first: false, meta: '', photosChanged: false } }));
    expect(r.state).toBe(4);
    expect(r.say).toEqual({ key: 'trained', title: 'Весна' });
  });
  it('5: обычная ошибка', () => {
    const r = characterCardView(input({ view: { kind: 'failed', version: ver(), allRejected: false, lost: false } }));
    expect(r.state).toBe(5);
    expect(r.say).toEqual({ key: 'failed' });
    expect(r.badge).toEqual({ text: 'failed', tone: 'danger' });
    expect(r.action).toEqual({ kind: 'retry' });
    expect(r.stops[2]).toMatchObject({ value: 0, titleKey: 'failed', titleColor: C.dangerText, hint: METER.retry });
  });
  it('5: allRejected — заменить фото', () => {
    const r = characterCardView(input({ view: { kind: 'failed', version: ver(), allRejected: true, lost: false } }));
    expect(r.say).toEqual({ key: 'failedAllRejected' });
    expect(r.action).toEqual({ kind: 'replaceTrainPhotos' });
  });
  it('5: lost — фраза своя, кнопка «Попробовать снова»', () => {
    const r = characterCardView(input({ view: { kind: 'failed', version: ver(), allRejected: false, lost: true } }));
    expect(r.say).toEqual({ key: 'failedLost' });
    expect(r.action).toEqual({ kind: 'retry' });
  });
});

describe('шкала', () => {
  it('train === null: деление 2 пустое, пояснение «…»', () => {
    const r = characterCardView(input({ train: null, view: form({ reason: 'photos' }) }));
    expect(r.stops[1]).toMatchObject({ value: 0, hint: '…' });
  });
  it('деление 1 есть, 2 нет — заголовок accent', () => {
    const r = characterCardView(input({ photos: 3, train: 5 }));
    expect(r.stops[0]).toMatchObject({ value: 100, titleColor: C.successText });
    expect(r.stops[1]).toMatchObject({ value: 0, titleColor: C.accent });
  });
  it('нет деления 1 — деление 2 не подсвечено', () => {
    const r = characterCardView(input({ photos: 2, train: 5 }));
    expect(r.stops[0]).toMatchObject({ value: 0, titleColor: C.textSecondary });
    expect(r.stops[1].titleColor).toBe(C.textSecondary);
  });
  it('обучение недоступно (off/noLocal) — деление 2 не подсвечено, даже если деление 1 есть', () => {
    for (const kind of ['off', 'noLocal'] as const) {
      const r = characterCardView(input({ photos: 3, train: 5, view: { kind } as TrainSectionView }));
      expect(r.stops[0]).toMatchObject({ value: 100, titleColor: C.successText });
      expect(r.stops[1].titleColor).toBe(C.textSecondary);
    }
  });
  it('оба деления заполнены', () => {
    const r = characterCardView(input({ photos: 3, train: 20 }));
    expect(r.stops[1]).toMatchObject({ value: 100, titleColor: C.successText, hint: METER.trainPhotos(20) });
  });
});
