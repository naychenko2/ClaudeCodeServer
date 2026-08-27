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

  // Ограничения тарифа — третья ось бейджа: не ломают четыре состояния, но ВСЕГДА
  // подмешиваются в reason как «, кроме ходов Opus и 1M». Сценарий прода 2026-08-23:
  // claude-3 с SupportsOpus=false и Supports1M=false показывался «в ротации», а по
  // факту Pick его не отдавал ~2/3 чатов (opus/opus[1m]). Без оговорки бейдж
  // «направляются сюда» врал. Tone и label не двигаем — только reason.
  it('вне ротации + без Opus — reason упоминает ограничение', () => {
    const s = rotationBadgeState({
      inRotation: false, isTarget: false, exhausted: true, freeAvailable: true,
      supportsOpus: false,
    });
    expect(s.tone).toBe('warn');
    expect(s.label).toBe('Выведен из ротации');
    expect(s.reason).toBe('лимит исчерпан — новые чаты идут на свободные аккаунты, кроме ходов Opus');
  });

  it('вне ротации + без Opus и без 1M — обе способности в суффиксе через «и»', () => {
    const s = rotationBadgeState({
      inRotation: false, isTarget: false, exhausted: true, freeAvailable: true,
      supportsOpus: false, supports1M: false,
    });
    expect(s.reason).toBe('лимит исчерпан — новые чаты идут на свободные аккаунты, кроме ходов Opus и 1M');
  });

  it('вне ротации + только без 1M — суффикс упоминает только 1M', () => {
    const s = rotationBadgeState({
      inRotation: false, isTarget: false, exhausted: true, freeAvailable: false,
      targetName: 'Claude 2', supports1M: false,
    });
    expect(s.reason).toBe('лимит исчерпан — новые чаты идут на «Claude 2», кроме ходов 1M');
  });

  it('спилл (цель, но вне ротации) + без Opus — суффикс тоже подмешивается', () => {
    const s = rotationBadgeState({
      inRotation: false, isTarget: true, utilization: 0.91, threshold: 0.8,
      supportsOpus: false,
    });
    expect(s.reason).toBe('свободных аккаунтов нет — нагрузка 5ч 91% ≥ порога 80%, кроме ходов Opus');
  });

  it('в ротации + без Opus — reason ВСЕГДА упоминает ограничение, даже на зелёной ветке', () => {
    const s = rotationBadgeState({
      inRotation: true, isTarget: true, utilization: 0.3, threshold: 0.8,
      supportsOpus: false,
    });
    expect(s.tone).toBe('ok');
    expect(s.label).toBe('В ротации');
    expect(s.reason).toBe('новые чаты направляются сюда, кроме ходов Opus');
  });

  it('в ротации, не цель + без Opus и 1M — оговорка перечисляет оба', () => {
    const s = rotationBadgeState({
      inRotation: true, isTarget: false, utilization: 0.5, threshold: 0.8,
      supportsOpus: false, supports1M: false,
    });
    expect(s.tone).toBe('ok');
    expect(s.reason).toBe('может принимать новые чаты, кроме ходов Opus и 1M');
  });

  it('supportsOpus=true/1M=true — оговорки нет, существующий текст без суффикса', () => {
    const s = rotationBadgeState({
      inRotation: false, isTarget: false, exhausted: true, freeAvailable: true,
      supportsOpus: true, supports1M: true,
    });
    expect(s.reason).toBe('лимит исчерпан — новые чаты идут на свободные аккаунты');
  });

  // Поля флагов не пришли (старый бэкенд или бэкенд без конфигурации пула) — undefined
  // НЕ превращается в «не принимает»: отдавать false «по умолчанию» было бы враньём, бэк
  // шлёт null → фронт мапит в undefined и суффикс не подмешивается.
  it('supportsOpus/supports1M не заданы — оговорки нет ни в одной ветке', () => {
    const ok = rotationBadgeState({ inRotation: true, isTarget: true, utilization: 0.3, threshold: 0.8 });
    expect(ok.reason).toBe('новые чаты направляются сюда');
    const warn = rotationBadgeState({ inRotation: false, isTarget: false, exhausted: true, freeAvailable: true });
    expect(warn.reason).toBe('лимит исчерпан — новые чаты идут на свободные аккаунты');
  });
});
