// Модель пилюли (buildPillModel) — F2, макет v4 §П2 «Мой шаг». Окружение node:
// чистая функция + мок стора персон.
//
// Пилюля в режиме фичи говорит про ШАГ ЭТОГО ЧАТА («Ш8 · идёт · 24 мин»); чат без
// шага — «фича · {pct} %»; тревога фичи заменяет текст (warn/danger). Фичу целиком
// показывает строка шапки В2 — пилюля её не повторяет.

import { describe, expect, it, vi } from 'vitest';
import { buildPillModel } from './pillModel';
import { formatClock } from '../../feature/FeatureStatsBits';
import type { FeatureRun } from '../../../types/featureRun';

const { personaMap } = vi.hoisted(() => ({ personaMap: new Map<string, string>([['p-gleb', 'Глеб']]) }));
vi.mock('../../../lib/personas', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../../lib/personas')>();
  return { ...orig, getPersonaById: (id: string) => (personaMap.has(id) ? { id, name: personaMap.get(id) } : undefined) };
});

function run(over: Partial<FeatureRun> = {}): FeatureRun {
  return {
    featureId: 'epic-1', state: 'running',
    epic: { id: 'epic-1', title: 'Фича', status: 'todo' },
    steps: [], reviews: [], qa: [], final: null, merge: null,
    verdicts: { accepted: 0, acceptedWithRemarks: 0, returned: 0, withdrawn: 0, loops: 0 },
    progress: 0, ready: false, now: [], attention: null, closed: false, summary: null,
    ...over,
  } as FeatureRun;
}

describe('buildPillModel (F2): чат шага', () => {
  it('идущий шаг: «Ш8 · идёт · 24 мин» (stepRef обычный, статус — бэковое слово, жирное)', () => {
    const r = run({ stepChains: [{ stepRef: 'Ш8', title: 'Вёрстка', taskIds: ['t-8'], outcome: 'running', status: 'идёт', durationMinutes: 24, rounds: [], chatId: null } as never] });
    const m = buildPillModel([r], 't-8', false, null)!;
    expect(m.pre).toBe('Ш8 · ');
    expect(m.bold).toBe('идёт');
    expect(m.post).toBe(' · 24 мин');
    expect(m.tone).toBe('neutral');
  });

  it('шаг принят: «принят {HH:MM}» без разделителя (один факт)', () => {
    const r = run({ stepChains: [{ stepRef: 'Ш8', title: 'Вёрстка', taskIds: ['t-8'], outcome: 'accepted', status: 'принят', acceptedAt: '2026-10-29T13:40:00Z', rounds: [], chatId: null } as never] });
    const m = buildPillModel([r], 't-8', false, null)!;
    expect(m.bold).toBe('принят');
    expect(m.post).toBe(` ${formatClock('2026-10-29T13:40:00Z')}`);
  });
});

describe('buildPillModel (F2): чат без шага', () => {
  it('нет цепочки по задаче чата — «фича · {pct} %» (процент по трекеру)', () => {
    const r = run({ stepProgress: { planState: 'cut', total: 9, accepted: 5, review: 1, work: 2, notCreated: 1, withdrawn: 0, unlinkedReworks: 0 } });
    const m = buildPillModel([r], 't-чата-без-шага', false, null)!;
    expect(m.pre).toBe('фича · ');
    expect(m.bold).toBe('56 %');
  });

  it('нет stepProgress (старые данные) — «фича» без числа', () => {
    const m = buildPillModel([run()], 't-x', false, null)!;
    expect(m.pre).toBe('фича · ');
    expect(m.bold).toBe('');
  });
});

describe('buildPillModel (F2): тревога фичи и состояния запроса', () => {
  it('waiting — «Ждёт вас · {Имя}» тоном wait + aria-сигнал', () => {
    const r = run({ state: 'waiting', attention: { kind: 'wait', taskId: 't-1', since: null, reason: null, personaId: 'p-gleb', linkedSessionId: 'sess-1' } });
    const m = buildPillModel([r], 't-1', false, null)!;
    expect(m.tone).toBe('wait');
    expect(m.pre).toBe('Ждёт вас · Глеб');
    expect(m.ariaExtra).toBe('фича ждёт вас');
  });

  it('stopped — «Стоит · {Имя}» тоном stop', () => {
    const r = run({ state: 'stopped', attention: { kind: 'stopped', taskId: 't-1', since: null, reason: null, personaId: 'p-gleb', linkedSessionId: null } });
    const m = buildPillModel([r], 't-1', false, null)!;
    expect(m.tone).toBe('stop');
    expect(m.pre).toBe('Стоит · Глеб');
  });

  it('merge_pending — «Ждёт согласия на мерж»', () => {
    const m = buildPillModel([run({ state: 'merge_pending' })], null, false, null)!;
    expect(m.tone).toBe('wait');
    expect(m.pre).toBe('Ждёт согласия на мерж');
  });

  it('загрузка/ошибка — «фича · …»; фич нет и запроса нет — null (пилюля скрыта)', () => {
    expect(buildPillModel([], null, true, null)!.pre).toBe('фича · …');
    expect(buildPillModel([], null, false, 'нет связи')!.pre).toBe('фича · …');
    expect(buildPillModel([], null, false, null)).toBeNull();
  });
});
