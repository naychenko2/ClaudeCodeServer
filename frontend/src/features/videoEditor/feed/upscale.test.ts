import { describe, expect, it } from 'vitest';
import { CATALOG, version } from '../mocks';
import type { JobProgress } from '../store/videoStore';
import { stagesOf, upscaleFailView, upscaleInfo, upscaledCaption } from './upscale';

const job = (extra: Partial<JobProgress>): JobProgress => ({
  jobId: 'job-7', sessionId: 'c1', sceneId: 's1', stage: 'running', queuePosition: null, etaSeconds: 330, upscaleEtaSeconds: 915,
  variant: 1, count: 1, runStartedAt: 0, percent: null, floor: 0, ...extra,
});
const h3 = upscaleInfo(CATALOG, 'local', 'minimax-h3', '16:9');

describe('«Макс» local H3: стадии карточки съёмки (записка, сценарий 10)', () => {
  it('информация берётся из каталога: ступень съёмки и ступень с увеличением', () => {
    expect(h3).toEqual({ shotLabel: '768p', shotSize: '1344×768', targetLabel: '1440p', targetSize: '2528×1440' });
    expect(upscaleInfo(CATALOG, 'local', 'minimax-h3', '9:16')).toMatchObject({ shotSize: '768×1344', targetSize: '1440×2528' });
  });
  it('у модели без увеличения информации нет — стадий нет', () => {
    expect(upscaleInfo(CATALOG, 'fal', 'veo-3.1', '16:9')).toBeNull();
    expect(stagesOf(null, job({}))).toBeNull();
  });
  it('съёмка идёт: «Снимаем · 1344×768…» со временем и «Отменить», вторая стадия «следом»', () => {
    const rows = stagesOf(h3, job({}))!;
    expect(rows[0]).toMatchObject({ n: 1, text: 'Снимаем · 1344×768…', badge: '~5,5 мин', state: 'now', cancel: 'job' });
    expect(rows[1]).toMatchObject({ n: 2, text: 'Увеличение до 1440p · 2528×1440', badge: 'следом · ~15 мин', state: 'wait' });
    expect(rows[1].cancel).toBeUndefined();
  });
  it('идёт увеличение: съёмка «готово», «Увеличиваем до 1440p…» «ещё ~15 мин» и «Отменить увеличение»', () => {
    const rows = stagesOf(h3, job({ stage: 'upscaling', etaSeconds: 915 }))!;
    expect(rows[0]).toMatchObject({ text: 'Съёмка · 1344×768', badge: 'готово', state: 'done' });
    expect(rows[1]).toMatchObject({ text: 'Увеличиваем до 1440p…', badge: 'ещё ~15 мин', state: 'now', cancel: 'upscale' });
  });
  it('запуск без увеличения следом (ступень ниже «Макс») стадий не рисует', () => {
    expect(stagesOf(h3, job({ upscaleEtaSeconds: null }))).toBeNull();
  });
  const h3v = (n: number, extra: Parameters<typeof version>[1] = {}) => version(n, { provider: 'local', model: 'minimax-h3', width: 1344, height: 768, ...extra });
  it('сбой увеличения: ⚠, текст записки и кнопка «Повторить»', () => {
    const v = h3v(1, { upscaleError: 'ComfyUI упал' });
    expect(upscaleFailView(h3!, [v], v)).toEqual({
      text: 'Увеличение до 1440p не удалось. Съёмка 768p сохранена версией 1 — в 1440p её нет.', tone: 'warning', action: { label: 'Повторить' },
    });
  });
  it('отмена увеличения: нейтрально, без «не удалось», кнопка «Увеличить до 1440p»', () => {
    const v = h3v(1, { upscaleError: 'Увеличение отменено', upscaleCancelled: true });
    expect(upscaleFailView(h3!, [v], v)).toEqual({
      text: 'Увеличение отменено. Съёмка 768p сохранена версией 1 — в 1440p её нет.', tone: 'info', action: { label: 'Увеличить до 1440p' },
    });
  });
  it('1440p той же съёмки уже есть: без кнопки и без ⚠, что где лежит; тот же текст и для отмены', () => {
    const failed = h3v(1, { upscaleError: 'упал' });
    const done = h3v(2, { width: 2528, height: 1440 });
    const want = { text: 'Съёмка 768p сохранена версией 1, а в 1440p — версией 2.', tone: 'info', action: null };
    expect(upscaleFailView(h3!, [failed, done], failed)).toEqual(want);
    const cancelled = h3v(1, { upscaleError: 'Увеличение отменено', upscaleCancelled: true });
    expect(upscaleFailView(h3!, [cancelled, done], cancelled)).toEqual(want);
  });
  it('чужие 1440p не считаются: другая модель, другие входы, более ранняя версия, версия сама с пометкой сбоя, не тот размер', () => {
    const failed = h3v(3, { upscaleError: 'упал' });
    const others = [
      h3v(4, { width: 2528, height: 1440, model: 'other' }),
      h3v(4, { width: 2528, height: 1440, inputs: { text: 'другой текст' } }),
      h3v(1, { width: 2528, height: 1440 }),
      h3v(4, { width: 2528, height: 1440, upscaleError: 'тоже упала' }),
      h3v(4, { width: 1344, height: 768 }),
    ];
    expect(upscaleFailView(h3!, [...others, failed], failed).action).toEqual({ label: 'Повторить' });
  });
  it('готово: «Версия 1 · 1440p · 2528×1440» только у настоящего 1440p без пометки сбоя', () => {
    expect(upscaledCaption(h3, version(1, { width: 2528, height: 1440 }))).toBe('Версия 1 · 1440p · 2528×1440');
    expect(upscaledCaption(h3, version(1, { width: 1344, height: 768, upscaleError: 'упал' }))).toBeNull();
    expect(upscaledCaption(h3, version(1, { width: 1344, height: 768 }))).toBeNull();
  });
});
