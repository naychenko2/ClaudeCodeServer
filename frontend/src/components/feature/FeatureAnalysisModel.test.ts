// Модель состояния строки анализа (W5-F1). Чистые функции — в node, без DOM.
//
// Покрытие: все 8 состояний таблицы спецификации (done, stuck, nudged, quiet,
// attention, idle, moving, other), приоритет сверху вниз («stuck побеждает quiet»,
// «done» раньше всех), выбор cur (сначала позвал человека, потом оклик), отсутствие
// дат (длительность не пишем), отсутствие чата (кнопки нет — «Анализ» остаётся).
//
// Мутации (в отчёте): (1) поменять местами строки 2 и 4 (stuck и quiet) — краснеет
// «stuck побеждает quiet»; (2) не проверять linkedSessionId у stuck — краснеет
// «без чата — кнопки нет»; (3) «тихо» без пунктира — краснеет «quiet — рамка пунктиром».

import { describe, it, expect, vi } from 'vitest';
import { analysisLineState, stageLabelShort } from './FeatureAnalysisModel';
import { C } from '../../lib/design';
import type { FeatureRun, FeatureNow } from '../../types/featureRun';

// Имена персон — мок: у чистой функции нет живого стора.
vi.mock('../../lib/personas', () => ({
  getPersonaById: (id: string) => (id === 'p-1' ? { id: 'p-1', name: 'Кира' } : undefined),
}));

// Фиксированное «сейчас» для детерминизма.
const NOW = Date.parse('2026-10-20T12:00:00Z');

function mkRun(over: Partial<FeatureRun> = {}): FeatureRun {
  return {
    featureId: 'f-1',
    state: 'running',
    steps: [],
    reviews: [],
    qa: [],
    final: null,
    merge: null,
    verdicts: { accepted: 0, acceptedWithRemarks: 0, returned: 0, withdrawn: 0, loops: 0 },
    progress: 0,
    ready: false,
    ...over,
  };
}

function mkNow(over: Partial<FeatureNow> = {}): FeatureNow {
  return { taskId: 't-1', stage: 'prod', title: 'Тема шага', ...over };
}

describe('analysisLineState — состояния', () => {
  it('done — смержена с итогом: бейдж, дата мержа, счётчики', () => {
    const st = analysisLineState(mkRun({
      state: 'merged',
      summary: { mergedAt: '2026-10-19T10:00:00Z', durationMinutes: 120, steps: 3, loops: 2 },
    }), NOW);
    expect(st.key).toBe('done');
    expect(st.badge?.text).toBe('смержена');
    expect(st.strong).toContain('шла 2 ч');
    expect(st.tail).toBe('3 шагов, 2 возвратов.');
    // Действие одно — «Анализ моделью».
    expect(st.actions.length).toBe(1);
    expect(st.actions[0].kind).toBe('analysis');
  });

  it('done — закрыта без итога: только бейдж, фразы нет', () => {
    const st = analysisLineState(mkRun({ state: 'closed' }), NOW);
    expect(st.key).toBe('done');
    expect(st.badge?.text).toBe('закрыта');
    expect(st.strong).toBe('');
    expect(st.tail).toBe('');
  });

  it('stuck — сторож позвал человека: тон опасности, бейдж «стоит»', () => {
    const st = analysisLineState(mkRun({
      lastActivityAt: '2026-10-20T09:00:00Z',
      now: [mkNow({ stepRef: 'Ш1', personaId: 'p-1', linkedSessionId: 'c-1', staleAlertedAt: '2026-10-19T12:00:00Z' })],
    }), NOW);
    expect(st.key).toBe('stuck');
    expect(st.tone).toBe('danger');
    expect(st.badge?.text).toBe('стоит 3 ч');
    expect(st.strong).toBe('Ш1 без движения');
    expect(st.tail).toContain('Кира не ответил(а) на оклик, сторож позвал вас в');
    expect(st.actions[0]).toEqual({ kind: 'openChat', label: 'Открыть чат Ш1', sessionId: 'c-1', variant: 'primary' });
  });

  it('stuck без чата — кнопки «Открыть чат» нет, «Анализ» остаётся', () => {
    const st = analysisLineState(mkRun({
      now: [mkNow({ stepRef: 'Ш1', title: 'Без чата', staleAlertedAt: '2026-10-19T12:00:00Z' })],
    }), NOW);
    expect(st.key).toBe('stuck');
    expect(st.actions.map(a => a.kind)).toEqual(['analysis']);
  });

  it('stuck без lastActivityAt — бейдж без числа длительности', () => {
    const st = analysisLineState(mkRun({
      now: [mkNow({ stepRef: 'Ш1', staleAlertedAt: '2026-10-19T12:00:00Z' })],
    }), NOW);
    expect(st.badge?.text).toBe('стоит');
  });

  it('cur — первый с staleAlertedAt, а не первый в списке', () => {
    const st = analysisLineState(mkRun({
      now: [
        mkNow({ stepRef: 'Ш2', nudgedAt: '2026-10-19T18:00:00Z' }),
        mkNow({ stepRef: 'Ш1', staleAlertedAt: '2026-10-19T12:00:00Z' }),
      ],
    }), NOW);
    expect(st.key).toBe('stuck');
    expect(st.strong).toBe('Ш1 без движения');
  });

  it('nudged — окликнули, молчит: тон предупреждения, имя персоны', () => {
    const st = analysisLineState(mkRun({
      now: [mkNow({ stepRef: 'Ш1', personaId: 'p-1', linkedSessionId: 'c-1', title: 'Тема', nudgedAt: '2026-10-20T11:00:00Z' })],
    }), NOW);
    expect(st.key).toBe('nudged');
    expect(st.tone).toBe('warning');
    expect(st.badge?.text).toBe('молчит 1 ч');
    expect(st.strong).toBe('Кира не отвечает на Ш1');
    expect(st.tail).toContain('сторож окликнул в');
  });

  it('quiet — run.quiet: пунктирная рамка, фраза с датой, ключевой чат', () => {
    const st = analysisLineState(mkRun({
      quiet: true,
      lastActivityAt: '2026-10-19T12:00:00Z',
      epic: { id: 'e-1', title: 'Эпик', linkedSessionId: 'c-key' },
    }), NOW);
    expect(st.key).toBe('quiet');
    expect(st.tone).toBe('warning');
    expect(st.dashed).toBe(true);
    expect(st.badge?.text).toBe('тихо 24 ч');
    expect(st.strong).toMatch(/^Ни одна задача не идёт с \d{2}\.\d{2} · \d{2}:\d{2}\.$/);
    expect(st.actions[0]).toEqual({ kind: 'openChat', label: 'Открыть ключевой чат', sessionId: 'c-key', variant: 'secondary' });
  });

  it('quiet без даты активности — фраза без «с …», бейдж без числа', () => {
    const st = analysisLineState(mkRun({ quiet: true }), NOW);
    expect(st.key).toBe('quiet');
    expect(st.strong).toBe('Ни одна задача не идёт.');
    expect(st.badge?.text).toBe('тихо');
    // Ключевых чата нет — кнопки перехода нет.
    expect(st.actions.map(a => a.kind)).toEqual(['analysis']);
  });

  it('stuck побеждает quiet', () => {
    const st = analysisLineState(mkRun({
      quiet: true,
      now: [mkNow({ stepRef: 'Ш1', staleAlertedAt: '2026-10-19T12:00:00Z' })],
    }), NOW);
    expect(st.key).toBe('stuck');
  });

  it('attention — тревога бэка: точка C.info, причина из attention', () => {
    const st = analysisLineState(mkRun({
      attention: { kind: 'wait', reason: 'нужно решение', linkedSessionId: 'c-2' },
    }), NOW);
    expect(st.key).toBe('attention');
    expect(st.dot?.color).toBe(C.info);
    expect(st.strong).toBe('Ждёт вас · нужно решение');
    expect(st.actions[0].kind).toBe('openChat');
  });

  it('attention merge — причина «мерж», даже если reason пуст', () => {
    const st = analysisLineState(mkRun({ attention: { kind: 'merge' } }), NOW);
    expect(st.strong).toBe('Ждёт вас · мерж');
  });

  it('idle — нет run.now, нет quiet, state idle: «Ждёт · dur»', () => {
    const st = analysisLineState(mkRun({
      state: 'idle',
      lastActivityAt: '2026-10-20T09:00:00Z',
    }), NOW);
    expect(st.key).toBe('idle');
    expect(st.dot?.color).toBe(C.textMuted);
    expect(st.strong).toBe('Ждёт · 3 ч');
    expect(st.tail).toBe('ни одна задача не идёт.');
  });

  it('moving — задача идёт: акцентная точка, этап и исполнитель', () => {
    const st = analysisLineState(mkRun({
      lastActivityAt: '2026-10-20T10:00:00Z',
      now: [mkNow({ stepRef: 'Ш1', personaId: 'p-1', startedAt: '2026-10-20T10:00:00Z' })],
    }), NOW);
    expect(st.key).toBe('moving');
    expect(st.dot?.color).toBe(C.accent);
    expect(st.dot?.glow).toBe(true);
    expect(st.strong).toBe('Идёт · Производство');
    expect(st.tail).toContain('Ш1 у Кира 2 ч');
    expect(st.tail).toContain('Последнее событие 2 ч назад');
  });

  it('other — черновик: тег статуса как в шапке', () => {
    const st = analysisLineState(mkRun({ state: 'draft' }), NOW);
    expect(st.key).toBe('other');
    expect(st.strong).toBe('черновик');
    expect(st.actions.length).toBe(1);
  });

  it('other — неизвестный state (старые данные): фразы нет, строка не падает', () => {
    const st = analysisLineState(mkRun({ state: 'нет-такого' as FeatureRun['state'] }), NOW);
    expect(st.key).toBe('other');
    expect(st.strong).toBe('');
  });

  it('неизвестная персона — «исполнитель», без имени', () => {
    const st = analysisLineState(mkRun({
      now: [mkNow({ stepRef: 'Ш1', personaId: 'нет-такой', title: 'Тема', nudgedAt: '2026-10-20T11:00:00Z' })],
    }), NOW);
    expect(st.strong).toBe('исполнитель не отвечает на Ш1');
  });
});

describe('stageLabelShort', () => {
  it('_known stages — короткие подписи, unknown — как есть', () => {
    expect(stageLabelShort('prod')).toBe('Производство');
    expect(stageLabelShort('qa')).toBe('Проверка');
    expect(stageLabelShort('custom')).toBe('custom');
  });
});
