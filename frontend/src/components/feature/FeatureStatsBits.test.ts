// Тесты Ш1a: страница не держит свою реализацию формата длительности и свою таблицу
// имён этапов — оба приходят из lib/chatGroups (формат Майи, STAGE_NAME). Мутация,
// возвращающая локальную копию formatDuration, краснит тест идентичности ссылки.
import { describe, it, expect } from 'vitest';
import { formatDuration as chatFormatDuration, STAGE_NAME } from '../../lib/chatGroups';
import { formatDuration, stageLabel } from './FeatureStatsBits';

describe('formatDuration — одна ссылка на lib/chatGroups', () => {
  it('FeatureStatsBits реэкспортирует ту же функцию, а не держит свою копию', () => {
    expect(formatDuration).toBe(chatFormatDuration);
  });

  it('формат Майи: «45 мин» до часа, «1 ч 05 мин» после часа, «2 ч» ровные часы', () => {
    expect(formatDuration(45)).toBe('45 мин');
    expect(formatDuration(65)).toBe('1 ч 05 мин');
    expect(formatDuration(60)).toBe('1 ч');
    expect(formatDuration(120)).toBe('2 ч');
    expect(formatDuration(935)).toBe('15 ч 35 мин');
  });

  it('нижняя граница «1 мин»: нуля в строках нет (тот же пол, что у индекса)', () => {
    expect(formatDuration(0)).toBe('1 мин');
  });

  it('дни от 48 ч: границы 2879/2880 — одно поведение страницы и индекса', () => {
    expect(formatDuration(2879)).toBe('47 ч 59 мин');
    expect(formatDuration(2880)).toBe('2 дн');
    expect(formatDuration(4320)).toBe('3 дн');
    expect(formatDuration(4716)).toBe('3 дн 6 ч');
    expect(formatDuration(1439)).toBe('23 ч 59 мин');
    expect(formatDuration(1440)).toBe('24 ч');
  });
});

describe('stageLabel — из STAGE_NAME, своей таблицы нет', () => {
  it('final — «Финальное ревью» (та же строка, что STAGE_NAME.final)', () => {
    expect(stageLabel('final')).toBe(STAGE_NAME.final);
    expect(stageLabel('final')).toBe('Финальное ревью');
  });

  it('все восемь этапов — ровно то, что отдаёт STAGE_NAME', () => {
    for (const key of Object.keys(STAGE_NAME)) {
      expect(stageLabel(key)).toBe(STAGE_NAME[key as keyof typeof STAGE_NAME]);
    }
  });

  it('неизвестный ключ — сам ключ (новая стадия на бэке не теряет подпись)', () => {
    expect(stageLabel('unknown_stage')).toBe('unknown_stage');
  });
});
