// Свод генераций glif по ленте (чип в шапке чата): дедуп по jobId, разбивка
// по outputType, сумма кредитов. Дедуп критичен: compose_project и get_job_status
// одной генерации несут один jobId, плюс возможен повтор из истории.
import { describe, it, expect } from 'vitest';
import type { ChatItem } from '../../../types';
import { computeGlifGenStats, fmtCredits } from '../glifStats';

type GlifItem = Extract<ChatItem, { kind: 'glif_cost' }>;

const glif = (jobId: string, over: Partial<GlifItem> = {}): ChatItem =>
  ({ kind: 'glif_cost', jobId, mediaCount: 1, ...over });

describe('computeGlifGenStats', () => {
  it('пустая лента и лента без glif_cost → нули', () => {
    expect(computeGlifGenStats([])).toEqual({ count: 0, credits: 0, hasCredits: false, byType: new Map() });
    const items: ChatItem[] = [{ kind: 'text', text: 'привет' }];
    expect(computeGlifGenStats(items).count).toBe(0);
  });

  it('дедуп по jobId: повтор одной генерации не удваивает счётчик', () => {
    const items: ChatItem[] = [
      glif('j1', { outputType: 'image', credits: 5 }),
      glif('j1', { outputType: 'image', credits: 5 }), // повтор из истории
      glif('j2', { outputType: 'video' }),
    ];
    const s = computeGlifGenStats(items);
    expect(s.count).toBe(2);
    expect(s.credits).toBe(5); // кредиты дубля не суммируются
  });

  it('разбивка по outputType; без типа — ключ «media»', () => {
    const items: ChatItem[] = [
      glif('j1', { outputType: 'image' }),
      glif('j2', { outputType: 'image' }),
      glif('j3', { outputType: 'video' }),
      glif('j4'),
    ];
    const s = computeGlifGenStats(items);
    expect(s.byType.get('image')).toBe(2);
    expect(s.byType.get('video')).toBe(1);
    expect(s.byType.get('media')).toBe(1);
  });

  it('credits: сумма только по генерациям с billing, hasCredits=true', () => {
    const items: ChatItem[] = [
      glif('j1', { credits: 4 }),
      glif('j2', { credits: 2.5 }),
      glif('j3'), // billing не доехал — поля нет
    ];
    const s = computeGlifGenStats(items);
    expect(s.credits).toBe(6.5);
    expect(s.hasCredits).toBe(true);
  });

  it('credits нет ни у одной генерации → hasCredits=false, сумма 0', () => {
    const s = computeGlifGenStats([glif('j1'), glif('j2')]);
    expect(s.credits).toBe(0);
    expect(s.hasCredits).toBe(false);
  });
});

describe('fmtCredits', () => {
  it('целые — без дробной части, дробные — один знак', () => {
    expect(fmtCredits(12)).toBe('12 кр.');
    expect(fmtCredits(12.5)).toBe('12.5 кр.');
    expect(fmtCredits(0)).toBe('0 кр.');
  });
});
