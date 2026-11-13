import { describe, it, expect } from 'vitest';
import { epicMetaLine, stagesMetaLine, progressWidth } from './FeaturePageHeroModel';
import type { FeatureRunMeta } from '../../types/featureRun';

const meta = (o: Partial<FeatureRunMeta> = {}): FeatureRunMeta =>
  ({ stagesDone: 4, stagesTotal: 10, parallelBranches: 2, etaMinutes: 180, progressPct: 52, approx: false, ...o });

describe('epicMetaLine', () => {
  it('короткий id и дд.мм', () => {
    expect(epicMetaLine('57dfe3b7-aaaa', '2026-11-13T10:00:00')).toBe('эпик 57dfe3b7 · 13.11');
  });
  it('без даты — только id; без id — null', () => {
    expect(epicMetaLine('57dfe3b7-aaaa', null)).toBe('эпик 57dfe3b7');
    expect(epicMetaLine(undefined, '2026-11-13T10:00:00')).toBeNull();
  });
});

describe('stagesMetaLine', () => {
  it('полная строка; «≈» только при approx', () => {
    expect(stagesMetaLine(meta())).toBe('4 из 10 этапов · 2 ветки идут параллельно · 3 ч до финала');
    expect(stagesMetaLine(meta({ approx: true }))).toBe('4 из 10 этапов · 2 ветки идут параллельно · ≈ 3 ч до финала');
  });
  it('склонение веток и отсутствие хвостов', () => {
    expect(stagesMetaLine(meta({ parallelBranches: 1, etaMinutes: null }))).toBe('4 из 10 этапов · 1 ветка идёт параллельно');
    expect(stagesMetaLine(meta({ parallelBranches: 5, etaMinutes: 30 }))).toContain('5 веток идут параллельно · 30 мин до финала');
    expect(stagesMetaLine(meta({ parallelBranches: 0, etaMinutes: null }))).toBe('4 из 10 этапов');
  });
  it('легаси (0 этапов) и без meta — строки нет', () => {
    expect(stagesMetaLine(meta({ stagesTotal: 0, stagesDone: 0 }))).toBeNull();
    expect(stagesMetaLine(undefined)).toBeNull();
  });
});

describe('progressWidth', () => {
  it('из progressPct, зажато; null — нет полосы', () => {
    expect(progressWidth(meta())).toBe(52);
    expect(progressWidth(meta({ progressPct: 140 }))).toBe(100);
    expect(progressWidth(meta({ progressPct: null }))).toBeNull();
  });
});
