import { describe, expect, it } from 'vitest';
import { handoffAvailability, handoffWarm } from '../chatHandoff';

// Кнопка «Перенести в новый чат» (docs/features/chat-handoff.md): зеркало серверных отказов
describe('handoffAvailability', () => {
  const plain = { participants: null, teamImplement: null, worktreePath: null };

  it('обычный чат вне хода — кнопка доступна', () => {
    expect(handoffAvailability(plain, false)).toEqual({ enabled: true });
    expect(handoffAvailability({}, false).enabled).toBe(true);
    // Пустой состав — не группа
    expect(handoffAvailability({ ...plain, participants: [] }, false).enabled).toBe(true);
  });

  it('групповой чат — выключена с причиной', () => {
    const a = handoffAvailability({ ...plain, participants: ['p1', 'p2'] }, false);
    expect(a.enabled).toBe(false);
    expect(a.reason).toMatch(/Групповой/);
  });

  it('штаб — выключена с причиной', () => {
    const a = handoffAvailability({ ...plain, teamImplement: {} as never }, false);
    expect(a.enabled).toBe(false);
    expect(a.reason).toMatch(/Командной реализации/);
  });

  it('отдельный worktree — выключена с причиной', () => {
    const a = handoffAvailability({ ...plain, worktreePath: '/tmp/wt' }, false);
    expect(a.enabled).toBe(false);
    expect(a.reason).toMatch(/рабочем дереве/);
  });

  it('идёт ход — выключена до его конца', () => {
    const a = handoffAvailability(plain, true);
    expect(a.enabled).toBe(false);
    expect(a.reason).toMatch(/конца хода/);
  });
});

// Путь переноса по статусу кэша: пока кэш жив — записку пишет сам чат
describe('handoffWarm', () => {
  it('тёплый и остывающий кэш — тёплый путь', () => {
    expect(handoffWarm('warm')).toBe(true);
    expect(handoffWarm('expiring')).toBe(true);
  });

  it('остывший и неизвестный кэш — холодный путь', () => {
    expect(handoffWarm('cold')).toBe(false);
    expect(handoffWarm('unknown')).toBe(false);
  });
});
