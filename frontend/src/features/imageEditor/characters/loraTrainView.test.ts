import { describe, expect, it } from 'vitest';
import type { LoraTrainStateDto, LoraVersionDto } from '../api';
import { TRAIN_BLOCKED, TRAIN_TEXT } from './loraTrainTexts';
import {
  badgeFor, confirmParts, failureText, formatDuration, isAllRejected, isValidTrigger, trainSectionView,
  type TrainViewInput,
} from './loraTrainView';

const state: LoraTrainStateDto = {
  enabled: true, available: true, gpu: { busy: false, mine: false }, current: null,
  defaultTrigger: 'ccsandrey', presets: [{ key: 'standard', etaSeconds: 6660 }], photosChanged: false,
};
const ver = (p: Partial<LoraVersionDto> = {}): LoraVersionDto =>
  ({ runId: 'r1', status: 'ready', photosCount: 34, photosKept: 31, connected: true, trainedAt: '2026-10-10T09:52:10Z', ...p });
const input = (p: Partial<TrainViewInput> = {}): TrainViewInput => ({
  view: { kind: 'idle' }, state, error: null, photoCount: 34, trigger: 'ccsandrey', isAdmin: false,
  dismissedRunId: null, now: Date.parse('2026-10-10T10:00:00Z'), ...p,
});

describe('граница 20 фото', () => {
  it('19 фото: кнопка заблокирована и говорит, сколько добавить', () => {
    expect(trainSectionView(input({ photoCount: 19 }))).toMatchObject({
      kind: 'form', disabled: true, reason: 'photos', label: TRAIN_BLOCKED(1),
    });
  });
  it('20 фото: можно обучать', () => {
    expect(trainSectionView(input({ photoCount: 20 }))).toMatchObject({
      kind: 'form', disabled: false, reason: null, label: TRAIN_TEXT.start,
    });
  });
  it('число фото неизвестно: считается нулём', () => {
    expect(trainSectionView(input({ photoCount: null }))).toMatchObject({ disabled: true, label: TRAIN_BLOCKED(20) });
  });
});

describe('слово-метка', () => {
  it.each([['abc', true], ['a_1', true], ['a'.repeat(24), true], ['a'.repeat(25), false], ['ab', false],
    ['1abc', false], ['_abc', false], ['abc', true], ['abc1', true], ['Abc', false], ['аня_1', false], ['a-bc', false]])('%s → %s', (t, ok) => {
    expect(isValidTrigger(t)).toBe(ok);
  });
  it('неверная метка блокирует старт, фото при этом достаточно', () => {
    expect(trainSectionView(input({ trigger: 'Аня-1' }))).toMatchObject({ kind: 'form', disabled: true, reason: 'trigger' });
  });
});

describe('выбор состояния', () => {
  it('нет данных → loading', () => {
    expect(trainSectionView(input({ view: { kind: 'loading' } })).kind).toBe('loading');
  });
  it('выключено админом и недоступная модель — разные виды без кнопки', () => {
    expect(trainSectionView(input({ view: { kind: 'unavailable', reason: 'disabled' } })).kind).toBe('off');
    expect(trainSectionView(input({ view: { kind: 'unavailable', reason: 'no_seam' } })).kind).toBe('noLocal');
    expect(trainSectionView(input({ view: { kind: 'unavailable', reason: 'unavailable' } })).kind).toBe('noLocal');
  });
  it('очередь и идёт', () => {
    expect(trainSectionView(input({ view: { kind: 'queued', position: 1 } })).kind).toBe('queued');
    const run = trainSectionView(input({ view: { kind: 'running', step: 1240, totalSteps: 2000, etaSeconds: 2700 } }));
    expect(run).toMatchObject({
      kind: 'running', text: 'Обучаем персонажа · шаг 1240 из 2000', percent: 62, etaText: 'Осталось примерно 45 мин',
    });
  });
  it('нет шага — только оценка времени, без процента', () => {
    expect(trainSectionView(input({ view: { kind: 'running', step: 0, totalSteps: null, etaSeconds: 7200 } })))
      .toMatchObject({ text: 'Идёт обучение, оно займёт около 2 ч.', percent: null, etaText: null });
  });
  it('нет ни шага, ни оценки воркера — берём пресет', () => {
    expect(trainSectionView(input({ view: { kind: 'running', step: 0, totalSteps: null, etaSeconds: null } })))
      .toMatchObject({ text: 'Идёт обучение, оно займёт около 1 ч 51 мин.' });
  });
  it('готово: первое обучение подключено, новая версия — нет', () => {
    const first = trainSectionView(input({ view: { kind: 'done', version: ver() } }));
    expect(first).toMatchObject({ kind: 'done', first: true });
    expect(first.kind === 'done' && first.meta).toMatch(/^Обучен 10 октября · 31 фото$/);
    expect(trainSectionView(input({ view: { kind: 'done', version: ver({ connected: false }) } })))
      .toMatchObject({ kind: 'done', first: false });
  });
  it('«Оставить прежнюю»: закрытая версия возвращает форму с «Обучить заново»', () => {
    expect(trainSectionView(input({ view: { kind: 'done', version: ver({ connected: false }) }, dismissedRunId: 'r1' })))
      .toMatchObject({ kind: 'form', label: TRAIN_TEXT.restart });
  });
  it('ошибка: обычная и «все фото отброшены»', () => {
    const failed = ver({ status: 'failed', error: { code: 'worker_error', message: 'x' } });
    expect(trainSectionView(input({ view: { kind: 'failed', version: failed } }))).toMatchObject({ kind: 'failed', allRejected: false });
    const all = ver({ status: 'failed', photosCount: 22, photosKept: 0, rejected: [] });
    expect(trainSectionView(input({ view: { kind: 'failed', version: all } }))).toMatchObject({ allRejected: true });
    expect(isAllRejected(ver({ photosCount: 2, photosKept: undefined, rejected: [
      { name: 'a', reason: 'no_face' }, { name: 'b', reason: 'many_faces' }] }))).toBe(true);
  });
  it('отмена: форма «Обучить» (не «заново») с пометкой «остановлено»', () => {
    const cancelled = { kind: 'cancelled' as const, version: ver({ status: 'cancelled' }) };
    expect(trainSectionView(input({ view: cancelled })))
      .toMatchObject({ kind: 'form', stopped: true, label: TRAIN_TEXT.start });
    // закрытый прогон на отменённую форму не влияет
    expect(trainSectionView(input({ view: cancelled, dismissedRunId: 'r1' })))
      .toMatchObject({ kind: 'form', stopped: true, label: TRAIN_TEXT.start });
  });
  it('отмена при изменённых фото: «Обучить заново»', () => {
    const cancelled = { kind: 'cancelled' as const, version: ver({ status: 'cancelled' }) };
    expect(trainSectionView(input({ view: cancelled, state: { ...state, photosChanged: true } })))
      .toMatchObject({ stopped: true, label: TRAIN_TEXT.restart });
  });
  it('карта занята чужим: время освобождения и кнопка админа', () => {
    const busy = { ...state, gpu: { busy: true, mine: false, busyUntil: '2026-10-10T11:20:00Z' } };
    expect(trainSectionView(input({ state: busy }))).toMatchObject({
      kind: 'busyOther', canStopForeign: false,
      text: 'Видеокарта сейчас занята обучением. Освободится примерно через 1 ч 20 мин. Попробуйте позже.',
    });
    expect(trainSectionView(input({ state: busy, isAdmin: true }))).toMatchObject({ canStopForeign: true });
  });
  it('отказ gpu_busy при старте даёт тот же вид со временем из ошибки', () => {
    const v = trainSectionView(input({ error: { reason: 'gpu_busy', busyUntil: '2026-10-10T10:30:00Z' } }));
    expect(v).toMatchObject({ kind: 'busyOther', text: expect.stringContaining('30 мин') });
  });
  it('у меня уже идёт обучение другого персонажа', () => {
    expect(trainSectionView(input({ state: { ...state, gpu: { busy: true, mine: true } } })).kind).toBe('busyMine');
    expect(trainSectionView(input({ error: { reason: 'owner_busy' } })).kind).toBe('busyMine');
  });
  it('фото изменились: пометка и «Обучить заново»', () => {
    expect(trainSectionView(input({ state: { ...state, photosChanged: true } })))
      .toMatchObject({ kind: 'form', photosChanged: true, label: TRAIN_TEXT.restart });
  });
});

describe('тексты и бейджи', () => {
  it('подтверждение старта: оговорка только при подключённой версии', () => {
    expect(confirmParts(false).note).toBeNull();
    expect(confirmParts(true).note).toBe(TRAIN_TEXT.confirmConnected);
    expect(confirmParts(false).bold).toBe('2 часа');
  });
  it('длительность', () => {
    expect(formatDuration(30)).toBe('1 мин');
    expect(formatDuration(2700)).toBe('45 мин');
    expect(formatDuration(7200)).toBe('2 ч');
    expect(formatDuration(4800)).toBe('1 ч 20 мин');
  });
  it('бейдж шапки', () => {
    expect(badgeFor({ kind: 'queued' })).toMatchObject({ text: 'В очереди' });
    expect(badgeFor({ kind: 'running', text: '', percent: 62, etaText: null })).toMatchObject({ text: '62 %' });
    expect(badgeFor({ kind: 'running', text: '', percent: null, etaText: null })).toMatchObject({ text: 'Идёт' });
    expect(badgeFor({ kind: 'off' })).toBeNull();
    const form = { kind: 'form' as const, label: '', disabled: false, reason: null, photosChanged: false };
    expect(badgeFor({ ...form, stopped: true })).toMatchObject({ text: 'Остановлено' });
    expect(badgeFor({ ...form, stopped: false })).toBeNull();
  });
  it('отказы действий', () => {
    expect(failureText({ reason: 'bad_trigger' })).toBe(TRAIN_TEXT.triggerError);
    expect(failureText({ reason: 'gpu_busy' })).toBeNull();
    expect(failureText({ reason: 'network' })).toBeTruthy();
  });
});
