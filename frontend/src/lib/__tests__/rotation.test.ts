import { describe, it, expect } from 'vitest';
import { rotationBadgeState } from '../rotation';

// Четыре честных состояния бейджа ротации: цель роутинга × в ротации.
// Сценарий прода 2026-07-25: primary исчерпан (неделя 100%), claude-2 выше порога —
// старый бейдж врал «выведен из ротации, чаты идут на свободные», хотя ВСЕ чаты шли на claude-2.
describe('rotationBadgeState', () => {
  it('цель роутинга и в ротации — зелёный «направляются сюда»', () => {
    const s = rotationBadgeState({ inRotation: true, isTarget: true, utilization: 0.3, threshold: 0.8 });
    expect(s.tone).toBe('ok');
    expect(s.label).toBe('В ротации');
    expect(s.reason).toBe('новые чаты направляются сюда');
  });

  it('цель роутинга, но вне ротации (спилл) — жёлтый «свободных нет» с причиной по нагрузке', () => {
    const s = rotationBadgeState({ inRotation: false, isTarget: true, utilization: 0.91, threshold: 0.8 });
    expect(s.tone).toBe('warn');
    expect(s.label).toBe('Принимает новые чаты');
    expect(s.reason).toBe('свободных аккаунтов нет — нагрузка 5ч 91% ≥ порога 80%');
  });

  it('цель роутинга, вне ротации, сам исчерпан — «все аккаунты исчерпаны»', () => {
    const s = rotationBadgeState({ inRotation: false, isTarget: true, exhausted: true, utilization: 1, threshold: 0.8 });
    expect(s.tone).toBe('warn');
    expect(s.label).toBe('Принимает новые чаты');
    expect(s.reason).toBe('свободных аккаунтов нет — все аккаунты исчерпаны');
  });

  it('не цель, вне ротации, свободные есть — «идут на свободные аккаунты»', () => {
    const s = rotationBadgeState({
      inRotation: false, isTarget: false, exhausted: true, freeAvailable: true, targetName: 'Claude 2',
    });
    expect(s.tone).toBe('warn');
    expect(s.label).toBe('Выведен из ротации');
    expect(s.reason).toBe('лимит исчерпан — новые чаты идут на свободные аккаунты');
  });

  it('не цель, вне ротации, свободных нет — называет цель по имени, а не «свободные»', () => {
    const s = rotationBadgeState({
      inRotation: false, isTarget: false, exhausted: true, freeAvailable: false, targetName: 'Claude 2',
    });
    expect(s.reason).toBe('лимит исчерпан — новые чаты идут на «Claude 2»');
  });

  it('не цель, но в ротации — зелёный «может принимать»', () => {
    const s = rotationBadgeState({ inRotation: true, isTarget: false, utilization: 0.5, threshold: 0.8 });
    expect(s.tone).toBe('ok');
    expect(s.label).toBe('В ротации');
    expect(s.reason).toBe('может принимать новые чаты');
  });
});
