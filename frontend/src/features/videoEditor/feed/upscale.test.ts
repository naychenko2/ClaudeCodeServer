import { describe, expect, it } from 'vitest';
import { CATALOG, version } from '../mocks';
import type { JobProgress } from '../store/videoStore';
import { stagesOf, upscaleFailText, upscaleInfo, upscaledCaption } from './upscale';

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
  it('сбой увеличения: версия 768p с пометкой и текстом записки', () => {
    expect(upscaleFailText(h3!, version(1))).toBe('Увеличение до 1440p не удалось. Съёмка 768p сохранена версией 1 — в 1440p её нет.');
  });
  it('готово: «Версия 1 · 1440p · 2528×1440» только у настоящего 1440p без пометки сбоя', () => {
    expect(upscaledCaption(h3, version(1, { width: 2528, height: 1440 }))).toBe('Версия 1 · 1440p · 2528×1440');
    expect(upscaledCaption(h3, version(1, { width: 1344, height: 768, upscaleError: 'упал' }))).toBeNull();
    expect(upscaledCaption(h3, version(1, { width: 1344, height: 768 }))).toBeNull();
  });
});
