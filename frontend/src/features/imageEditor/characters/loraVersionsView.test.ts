import { describe, expect, it } from 'vitest';
import type { LoraVersionDto, LoraRunStatus } from '../api';
import {
  atVersionLimit, needsRevertConfirm, validateVersionTitle, versionActions, versionMark, versionTitle,
} from './loraVersionsView';
import { VERSIONS_TEXT } from './loraTrainTexts';

const v = (p: Partial<LoraVersionDto> & { runId: string }): LoraVersionDto => ({
  status: 'ready', photosCount: 34, connected: false, trainedAt: '2026-10-10T09:00:00Z', ...p,
});

describe('versionActions', () => {
  const cases: [LoraRunStatus, boolean, string[]][] = [
    ['ready', false, ['connect', 'rename', 'remove']],
    ['ready', true, ['rename', 'remove']],
    ['training', false, []],
    ['failed', false, ['remove']],
    ['cancelled', false, ['remove']],
  ];
  it.each(cases)('%s, подключена=%s', (status, connected, expected) => {
    expect(versionActions(v({ runId: 'a', status, connected }))).toEqual(expected);
  });
});

describe('versionMark', () => {
  it('подключённая и готовая различаются', () => {
    expect(versionMark(v({ runId: 'a', connected: true })).text).toBe('Подключена');
    expect(versionMark(v({ runId: 'a' })).text).toBe('Готова к подключению');
  });
  it('идущая, упавшая и остановленная', () => {
    expect(versionMark(v({ runId: 'a', status: 'training' })).tone).toBe('accent');
    expect(versionMark(v({ runId: 'a', status: 'failed' })).tone).toBe('danger');
    expect(versionMark(v({ runId: 'a', status: 'cancelled' })).tone).toBe('neutral');
  });
});

describe('versionTitle', () => {
  it('идущее обучение без своего названия — подсказка, а не «Обучено…»', () => {
    expect(versionTitle(v({ runId: 'a', status: 'training' }), [])).toBe(VERSIONS_TEXT.trainingHint);
  });
  it('своё название важнее шаблона, пробелы обрезаются', () => {
    expect(versionTitle(v({ runId: 'a', title: '  Для открыток ' }), [])).toBe('Для открыток');
  });
  it('шаблон: число фото — оставшиеся, иначе все', () => {
    expect(versionTitle(v({ runId: 'a', photosKept: 31 }), [])).toMatch(/^Обучено .+, 31 фото$/);
    expect(versionTitle(v({ runId: 'a' }), [])).toMatch(/, 34 фото$/);
  });
  it('пустое название — шаблон', () => {
    expect(versionTitle(v({ runId: 'a', title: '   ' }), [])).toMatch(/^Обучено /);
  });
  it('в один день к дате добавляется время, в разные — нет', () => {
    const a = v({ runId: 'a', trainedAt: '2026-10-10T09:00:00Z' });
    const b = v({ runId: 'b', trainedAt: '2026-10-10T10:00:00Z' });
    const c = v({ runId: 'c', trainedAt: '2026-10-12T10:00:00Z' });
    expect(versionTitle(a, [a, b])).toMatch(/\d{2}:\d{2}, 34 фото$/);
    expect(versionTitle(c, [a, b, c])).not.toMatch(/\d{2}:\d{2}/);
  });
  it('у упавшей берётся createdAt', () => {
    const f = v({ runId: 'f', status: 'failed', trainedAt: undefined, createdAt: '2026-10-09T22:10:00Z' });
    expect(versionTitle(f, [f])).toMatch(/^Обучено .+, 34 фото$/);
  });
});

describe('needsRevertConfirm', () => {
  const old = v({ runId: 'old', trainedAt: '2026-10-01T09:00:00Z' });
  const cur = v({ runId: 'cur', trainedAt: '2026-10-05T09:00:00Z', connected: true });
  const fresh = v({ runId: 'new', trainedAt: '2026-10-09T09:00:00Z' });
  it('старее подключённой — спрашивать', () => expect(needsRevertConfirm(old, [old, cur, fresh])).toBe(true));
  it('новее подключённой — подключать сразу', () => expect(needsRevertConfirm(fresh, [old, cur, fresh])).toBe(false));
  it('подключённой нет — сразу', () => {
    expect(needsRevertConfirm(old, [old, { ...cur, connected: false }])).toBe(false);
  });
});

describe('validateVersionTitle', () => {
  it('пустое и из пробелов — сброс к шаблону', () => {
    expect(validateVersionTitle('')).toEqual({ ok: true, title: '' });
    expect(validateVersionTitle('   ')).toEqual({ ok: true, title: '' });
  });
  it('ровно 60 допустимо, 61 — ошибка', () => {
    expect(validateVersionTitle('а'.repeat(60)).ok).toBe(true);
    expect(validateVersionTitle('а'.repeat(61)).ok).toBe(false);
  });
  it('длина считается после trim', () => {
    expect(validateVersionTitle(` ${'а'.repeat(60)} `)).toEqual({ ok: true, title: 'а'.repeat(60) });
  });
});

describe('atVersionLimit', () => {
  it('предел 5', () => {
    expect(atVersionLimit(4, 5)).toBe(false);
    expect(atVersionLimit(5, 5)).toBe(true);
  });
});
