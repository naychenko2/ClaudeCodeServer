// Секция «Кто работал» вкладки «Разбор» (FS-FR4). Рендер на fakeDom: порядок таблиц
// персоны → модели → роли; пустые оси не рисуются; ось с одной группой — таблица (c2);
// при isMobile колонки токенов нет (на десктопе — есть); мера — время, в шапке нет кнопок;
// подписи — из моков персон/моделей/ролей, а не сырые ключи; переходы в чаты — через nav.
//
// Мутации (проверены прогоном): (1) снять guard пустой оси — «все оси пусты» краснеет;
// (2) hideTokens={false} вместо isMobile — «мобильная» краснеет;
// (3) убрать measure="time"/readOnlyMeasure — возвращаются кнопки сортировки,
// «без кнопок» и порядок строк краснеют; (4) labelOf без оси (всегда personaGroupLabel) —
// «подписи» краснеет на модели.

import { act, createElement } from 'react';
import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { FeatureReviewWho } from './FeatureReviewWho';
import { getBackNav } from '../../lib/featureBackNav';
import { mount, installFakeDom, uninstallFakeDom, findNodeByAttr, textNodes, reactProps } from '../chat/__tests__/fakeDom';
import type { FeatureStats, FeatureBreakdownGroup } from '../../types/featureStats';
import type { FeatureRun } from '../../types/featureRun';

const mockPersonas = vi.hoisted(() => [
  { id: 'p-kira', name: 'Кира', role: 'Frontend' },
  { id: 'p-timur', name: 'Тимур', role: 'Backend' },
]);
const mockModelLabel = vi.hoisted(() => (v: string | null | undefined): string => {
  if (v === 'claude-opus-4-8') return 'Opus';
  if (v === 'Qwen') return 'Qwen';
  return v ?? '';
});
const mockOpenChat = vi.hoisted(() => ({ calls: [] as string[] }));
vi.mock('../../lib/personas', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../lib/personas')>();
  return { ...orig, usePersonas: () => mockPersonas, getPersonaById: (id: string) => mockPersonas.find(p => p.id === id) };
});
vi.mock('../../lib/models', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../lib/models')>();
  return { ...orig, useModels: () => [], modelLabel: mockModelLabel };
});
vi.mock('../../lib/openChat', () => ({
  openChatById: (id: string) => { mockOpenChat.calls.push(id); return Promise.resolve(true); },
}));

function mkGroup(key: string, minutes: number, tokens: number | null, costUsd: number | null, chatIds: string[] = []): FeatureBreakdownGroup {
  return { key, minutes, tokens, costUsd, chatIds };
}

function mkRun(): FeatureRun {
  return {
    featureId: 'f-1', state: 'running', steps: [], reviews: [], qa: [], final: null, merge: null,
    verdicts: { accepted: 0, acceptedWithRemarks: 0, returned: 0, withdrawn: 0, loops: 0 },
    progress: 0, ready: false,
  };
}

function mkStats(over: Partial<FeatureStats> = {}): FeatureStats {
  return {
    source: 'approx',
    truncated: false,
    timeline: {
      from: '2026-11-01T00:00:00Z', to: '2026-11-01T10:00:00Z', stages: [], segments: [], current: null,
      totals: {
        elapsedMinutes: 0, workWallMinutes: 0, agentsSumMinutes: 0, notWorkingMinutes: 0,
        waitingMinutes: null, idleMinutes: null, waitingCount: null, longestWaitMinutes: null,
        errorCount: null, errorMinutes: null,
      },
    },
    breakdown: { personas: [], models: [], roles: [] },
    longestNow: null,
    spend: null,
    ...over,
  };
}

// Фикстура c2: персоны — 2 группы, модели — 1, роли — 0. Порядок входа персон — НЕ по
// времени (Тимур первым): сортировка по мере проверяется, а не совпадает с входом.
const PERSONAS = [mkGroup('p-timur', 60, 3000, 5.0, ['c1']), mkGroup('p-kira', 120, 1000, 0, ['c2'])];
const MODELS = [mkGroup('claude-opus-4-8', 180, 4000, 12.0)];

function mountWho(stats: FeatureStats, isMobile = false) {
  return mount(createElement(FeatureReviewWho, { run: mkRun(), stats, isMobile }));
}

function textOf(node: unknown): string {
  return textNodes(node as never).join('');
}

// Корни таблиц в секции (внутренние div таблицы и блок чатов — с другими testid).
function tables(container: ReturnType<typeof mount>['container']) {
  return container.querySelectorAll('div').filter(d => d.getAttribute('data-testid') === 'feature-breakdown-table');
}

function headTexts(table: ReturnType<typeof tables>[number]): string[] {
  return table.querySelectorAll('th').map(t => textOf(t));
}

function rowOrder(table: ReturnType<typeof tables>[number]): string[] {
  return table
    .querySelectorAll('tr')
    .filter(r => (r.getAttribute('data-testid') ?? '').startsWith('breakdown-row-'))
    .map(r => (r.getAttribute('data-testid') as string).slice('breakdown-row-'.length));
}

function click(node: ReturnType<typeof findNodeByAttr> | null) {
  if (!node) return;
  act(() => { (reactProps(node) as { onClick: () => void }).onClick(); });
}

beforeEach(() => { installFakeDom(); });
afterEach(() => { uninstallFakeDom(); });

describe('оси и порядок (c2)', () => {
  it('непустые оси — таблицы в порядке персоны → модели → роли; пустой оси нет', () => {
    const { container } = mountWho(mkStats({ breakdown: { personas: PERSONAS, models: MODELS, roles: [] } }));
    expect(findNodeByAttr(container, 'data-testid', 'review-who')).toBeTruthy();
    const ts = tables(container);
    expect(ts.length, 'ожидались ровно 2 таблицы (2-я группа и 1-группная; роли — нет)').toBe(2);
    expect(headTexts(ts[0])[0]).toBe('персоны');
    expect(headTexts(ts[1])[0]).toBe('модели');
  });

  it('все оси пусты — ни одной таблицы', () => {
    const { container } = mountWho(mkStats());
    expect(tables(container).length).toBe(0);
  });
});

describe('мера и колонки', () => {
  it('мера — время: строки по убыванию времени; в шапке нет кнопок сортировки', () => {
    const { container } = mountWho(mkStats({ breakdown: { personas: PERSONAS, models: [], roles: [] } }));
    const t = tables(container)[0];
    expect(rowOrder(t)).toEqual(['p-kira', 'p-timur']); // вход — Тимур первым; решает время
    expect(t.querySelectorAll('th').flatMap(th => th.querySelectorAll('button')).length).toBe(0);
  });

  it('isMobile=true — в секции нет колонок токенов; на десктопе — у обеих таблиц', () => {
    const stats = mkStats({ breakdown: { personas: PERSONAS, models: MODELS, roles: [] } });
    const m = mountWho(stats, true);
    const headsM = tables(m.container).flatMap(t => headTexts(t));
    expect(headsM.filter(h => h.includes('токены')).length).toBe(0);
    expect(headsM.filter(h => h.includes('время')).length).toBe(2);
    const d = mountWho(stats, false);
    const headsD = tables(d.container).flatMap(t => headTexts(t));
    expect(headsD.filter(h => h.includes('токены')).length).toBe(2);
  });

  it('costPartial из spend: «Всего» — деньги с пометкой «не по всем»', () => {
    const { container } = mountWho(mkStats({
      breakdown: { personas: PERSONAS, models: [], roles: [] },
      spend: {
        inputTokens: 100, outputTokens: 10, cacheReadTokens: 0, cacheCreationTokens: 0,
        costUsd: 5, costPartial: true, hiddenCostModels: ['Qwen'], from: '2026-11-01T00:00:00Z', shared: null,
      },
    }));
    const total = findNodeByAttr(container, 'data-testid', 'breakdown-total')!;
    expect(textOf(total)).toContain('$5,00');
    expect(findNodeByAttr(total, 'data-testid', 'breakdown-total-partial')).toBeTruthy();
  });
});

describe('подписи и переходы', () => {
  it('подписи: персона — имя, модель — modelLabel, роль — roleGroupLabel; сырых ключей нет', () => {
    const { container } = mountWho(mkStats({
      breakdown: {
        personas: PERSONAS,
        models: MODELS,
        roles: [mkGroup('step', 100, 1000, 1), mkGroup('review', 40, 200, 0.5)],
      },
    }));
    const ts = tables(container);
    expect(ts.length).toBe(3);
    expect(textOf(ts[0])).toContain('Кира');
    expect(textOf(ts[0])).not.toContain('p-kira');
    expect(textOf(ts[1])).toContain('Opus');
    expect(textOf(ts[1])).not.toContain('claude-opus-4-8');
    expect(textOf(ts[2])).toContain('шаг');
    expect(textOf(ts[2])).toContain('ревью');
  });

  it('nav доходит до чатов: клик по чату раскрытой строки пишет возврат фичи и открывает чат', () => {
    const { container } = mount(
      createElement(FeatureReviewWho, {
        run: mkRun(),
        stats: mkStats({ breakdown: { personas: PERSONAS, models: [], roles: [] } }),
        nav: { featureId: 'f-1', featureTitle: 'Тестовая фича' },
      }),
    );
    click(findNodeByAttr(container, 'data-testid', 'breakdown-chats-toggle-p-kira'));
    const chatBtn = findNodeByAttr(container, 'data-testid', 'nav-target-chat');
    expect(chatBtn, 'кнопки перехода в чат нет').toBeTruthy();
    click(chatBtn);
    expect(mockOpenChat.calls).toContain('c2');
    expect(getBackNav()?.to).toEqual({ kind: 'chat', id: 'c2' });
  });
});
