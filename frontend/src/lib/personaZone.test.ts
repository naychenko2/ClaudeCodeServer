// Чистая логика зоны персоны: отказ хода, значение селекта зоны, подсказка о последствиях.
import { describe, it, expect } from 'vitest';
import { isZoneRefusal, zoneFromValue, zoneHint, zoneToValue } from './personaZone';

describe('isZoneRefusal', () => {
  it('узнаёт машинный признак action', () => {
    expect(isZoneRefusal({ text: 'любой текст', action: 'change-companion' })).toBe(true);
  });

  it('узнаёт отказ по тексту, когда action не пришёл', () => {
    expect(isZoneRefusal({ text: 'Проект больше не в сфере «Работа» — смените собеседника' })).toBe(true);
    expect(isZoneRefusal({ text: '  Проект больше не в сфере «Дом» — смените собеседника \n', action: null })).toBe(true);
  });

  it('не принимает обычные ошибки и чужие действия', () => {
    expect(isZoneRefusal({ text: 'Сбой сети' })).toBe(false);
    expect(isZoneRefusal({ text: 'Сбой сети', action: 'window-1m-drop' })).toBe(false);
    expect(isZoneRefusal({ text: 'Проект больше не в сфере — смените собеседника' })).toBe(false);
    expect(isZoneRefusal({ text: 'Проект больше не в сфере «Работа» — смените собеседника. Подробности' })).toBe(false);
  });
});

describe('zoneToValue / zoneFromValue', () => {
  it('кодирует зоны единым значением', () => {
    expect(zoneToValue({ scope: 'global' })).toBe('global');
    expect(zoneToValue({ scope: 'sphere', sphereId: 's1' })).toBe('sphere:s1');
    expect(zoneToValue({ scope: 'project', projectId: 'p1' })).toBe('project:p1');
  });

  it('пустой id даёт пустой хвост, а не undefined', () => {
    expect(zoneToValue({ scope: 'sphere' })).toBe('sphere:');
    expect(zoneToValue({ scope: 'project' })).toBe('project:');
  });

  it('раскодирует обратно', () => {
    expect(zoneFromValue('sphere:s1')).toEqual({ scope: 'sphere', sphereId: 's1' });
    expect(zoneFromValue('project:p1')).toEqual({ scope: 'project', projectId: 'p1' });
    expect(zoneFromValue('global')).toEqual({ scope: 'global' });
    expect(zoneFromValue('что-то незнакомое')).toEqual({ scope: 'global' });
  });

  it('round-trip сохраняет зону', () => {
    for (const z of [{ scope: 'sphere', sphereId: 'a:b' }, { scope: 'project', projectId: 'p' }, { scope: 'global' }] as const) {
      expect(zoneFromValue(zoneToValue(z))).toEqual(z);
    }
  });
});

describe('zoneHint', () => {
  it('сфера: число проектов и память сферы', () => {
    const h = zoneHint({ scope: 'sphere' }, 3);
    expect(h).toContain('сейчас их 3');
    expect(h).toContain('память сферы');
  });

  it('проект и глобальная зона описывают разное', () => {
    expect(zoneHint({ scope: 'project' }, 0)).toBe('Персона видит только этот проект.');
    expect(zoneHint({ scope: 'global' }, 0)).toContain('все ваши проекты');
  });
});
