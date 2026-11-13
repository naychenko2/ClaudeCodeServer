// Рендер-тест контейнера «Настройки» (S7d-3, F3): на подвкладке блоки в порядке макета —
// «Ночной разбор» (F2), «Применение», «Отбор чатов».
// Фикстура — та же, что у замера ui_probe: два класса правок, один выключен, список чатов.
//
// Мокан только транспорт (offline.request, signalr) — стор настоящий (тот же приём, что
// WikiApplySettings.test/WikiChatTriageList.test): блоки читают свои ручки сами.
//
// Имя файла — .test.ts, не .test.tsx как в постановке: vitest этого дерева собирает только
// src/**/*.test.ts (vitest.config.ts, include), и компонентные тесты каталога — .test.ts
// с createElement. .test.tsx в прогон не попал бы вовсе.
//
// Мутации этого прогона (вывод — в отчёте задачи):
// (1) в WikiSettings переставить блоки («Отбор чатов» раньше «Применения») → «в порядке макета»
//     краснеет;
// (2) убрать <WikiNightlyBlock /> из контейнера → «встроен первым» краснеет;
// (3) поставить его после «Применения» → «встроен первым» краснеет;
// (4) в WIKI_SUBTABS подставить записи «settings» заглушку (stubNote) или другой component →
//     тест проводки краснеет (доработка по ревью R-S7d-3);
// (5) поменять в WikiSettings местами <WikiChatTriageList /> и <WikiPurgeBlock /> →
//     «последний элемент стопки» краснеет (доработка по ревью М2).

import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { act, createElement } from 'react';
import type { Root } from 'react-dom/client';
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import {
  mount, installFakeDom, uninstallFakeDom, textNodes,
} from '../../../../components/chat/__tests__/fakeDom';
import { WIKI_SUBTABS } from '../wikiTabs';
import { WikiSettings } from '../WikiSettings';

vi.mock('../../../../lib/signalr', async (orig) => ({
  ...await orig<typeof import('../../../../lib/signalr')>(),
  onMessage: vi.fn(() => () => {}),
  onReconnected: vi.fn(() => () => {}),
  joinUser: vi.fn(() => Promise.resolve()),
}));
vi.mock('../../../../lib/offline', async (orig) => ({
  ...await orig<typeof import('../../../../lib/offline')>(),
  request: vi.fn(),
  readStoredUserId: vi.fn(() => 'u1'),
}));

import { request } from '../../../../lib/offline';

type MockFn = ReturnType<typeof vi.fn>;
const req = request as unknown as MockFn;

type Cont = ReturnType<typeof mount>['container'];
type AnyNode = Cont;

// ─── Фикстура «сервера»: та же форма ответов, что в тестах самих блоков ───

const NIGHTLY = {
  enabled: false, windowStartHour: 3, windowEndHour: 6,
  maxPackagesPerNight: 20, maxPackagesPerSection: 6, maxChatsPerSection: 10,
  inputTokenBudget: 700000,
};
const CLS_ON = {
  class: 'session×append', disabled: false, reason: null, disabledAt: null, resumedAt: null,
  applied7: 5, retracted7: 0, share7: 0, applied30: 41, retracted30: 0, share30: 0,
};
const CLS_OFF = {
  class: 'session×create', disabled: true,
  reason: 'в окне последних 50 операций откатили 4 из 38 (10,5 %) при пороге 10 %',
  disabledAt: '2026-10-25T12:00:00Z', resumedAt: null,
  applied7: 0, retracted7: 3, share7: 0, applied30: 38, retracted30: 4, share30: 10.5,
};
const SETTINGS = { autoApplyEnabled: true, classes: [CLS_ON, CLS_OFF], nightly: NIGHTLY };

type Item = {
  sessionId: string; name: string | null; zone: string; score: number | null;
  signals: unknown; reason: string | null; nothingNewStreak: number; triagedAt: string;
  running: boolean; cursor: number; chroniclePath: string | null; mark: string | null;
};
const item = (over: Partial<Item>): Item => ({
  sessionId: 'c1', name: 'План шагов LLM-wiki', zone: 'triage', score: 31,
  signals: null, reason: 'балл 31: формулировки решений (+8), реплики человека (+6)',
  nothingNewStreak: 0, triagedAt: '2026-11-01T12:00:00Z', running: false, cursor: 0,
  chroniclePath: null, mark: null, ...over,
});
const CHATS = [
  item({ sessionId: 'c1' }),
  item({ sessionId: 'c2', name: 'Серая', zone: 'gray', score: 11, reason: 'балл 11: реплики человека (+6)' }),
  item({ sessionId: 'c3', name: 'Пустой', zone: 'nothing_new', score: 4, reason: 'нет новых утверждений' }),
];

function serveAll(): void {
  req.mockImplementation(async (url: string) => {
    if (url === '/wiki/settings') return SETTINGS;
    if (url === '/wiki/chats') return { items: CHATS, counts: {} };
    return null;
  });
}

const roots: Root[] = [];

function render(): Cont {
  installFakeDom();
  const { root, container } = mount(createElement(WikiSettings));
  roots.push(root);
  return container;
}

async function settle(times = 20) {
  for (let i = 0; i < times; i++) await act(async () => { await Promise.resolve(); });
}

function texts(container: AnyNode): string[] {
  return textNodes(container).map(t => t.trim()).filter(Boolean);
}

function has(container: AnyNode, needle: string): boolean {
  return texts(container).join(' ').includes(needle);
}

function countByTestId(container: AnyNode, testId: string): number {
  let n = 0;
  const walk = (x: AnyNode) => {
    if (x.nodeType === 1 && x.getAttribute('data-testid') === testId) n += 1;
    for (const c of x.childNodes) walk(c);
  };
  walk(container);
  return n;
}

beforeEach(() => {
  req.mockReset();
  serveAll();
});

afterEach(() => {
  for (const root of roots.splice(0)) void root.unmount();
  uninstallFakeDom();
});

describe('контейнер «Настройки» (S7d-3)', () => {
  it('подвкладка «Настройки» в реестре ведёт на контейнер настроек', () => {
    const settings = WIKI_SUBTABS.find(t => t.id === 'settings')!;
    expect(settings.label).toBe('Настройки');
    expect(settings.component).toBe(WikiSettings);
    // заглушки нет: подкладка сделана (S7d-3) — WikiTab не должен рисовать на ней EmptyState
    expect(settings.stubNote).toBeUndefined();
  });

  it('блоки «Применение» и «Отбор чатов» на подвкладке в порядке макета', async () => {
    const c = render();
    await settle();
    // оба блока на месте и со своими данными: «Применение» — два класса (один выключен),
    // «Отбор чатов» — строки списка
    expect(has(c, 'Применение без ревью')).toBe(true);
    expect(countByTestId(c, 'wiki-apply-row')).toBe(2);
    expect(has(c, 'выключен')).toBe(true);
    expect(has(c, 'Отбор чатов')).toBe(true);
    expect(countByTestId(c, 'wiki-triage-row')).toBe(3);
    // порядок — как в навигации настроек макета: «Применение» раньше «Отбора чатов»
    const joined = texts(c).join(' ');
    const iApply = joined.indexOf('Применение без ревью');
    const iTriage = joined.indexOf('Отбор чатов', iApply);
    expect(iApply).toBeGreaterThanOrEqual(0);
    expect(iTriage).toBeGreaterThanOrEqual(0);
    expect(iApply).toBeLessThan(iTriage);
  });

  it('«Ночной разбор» встроен первым: перед «Применением» и «Отбором чатов», точки расширения нет', async () => {
    const c = render();
    await settle();
    // блок на месте: его шапка в DOM, и она раньше «Применения» и «Отбора чатов»
    const joined = texts(c).join(' ');
    const iNight = joined.indexOf('Ночной разбор');
    expect(iNight).toBeGreaterThanOrEqual(0);
    expect(iNight).toBeLessThan(joined.indexOf('Применение без ревью'));
    expect(iNight).toBeLessThan(joined.indexOf('Отбор чатов', joined.indexOf('Применение без ревью')));
    // в коде контейнера блок стоит первым, а строки-заглушки нет; поиск без закрытия тега —
    // краснеет на уходе блока из файла, а не на форматной правке `<X />` → `<X/>`
    const src = readFileSync(fileURLToPath(new URL('../WikiSettings.tsx', import.meta.url)), 'utf8');
    expect(src).not.toContain('Точка расширения «Ночного разбора»');
    const iNightSrc = src.indexOf('<WikiNightlyBlock');
    expect(iNightSrc).toBeGreaterThanOrEqual(0);
    expect(iNightSrc).toBeLessThan(src.indexOf('<WikiApplySettings'));
    expect(src.indexOf('<WikiApplySettings')).toBeLessThan(src.indexOf('<WikiChatTriageList'));
  });

  it('полоса «Начать вики заново» — последний элемент стопки (М2/П4): страж порядка в источнике', () => {
    // Ревью М2 (Глеб): инвариант был без стража. Полоса прижимается к нижней кромке
    // тела только оставаясь последним элементом контейнера (см. комментарий WikiSettings.tsx);
    // поднять её в стопку или опустить другой блок ниже — полоса отклеится от низа (А2).
    // Страж по образцу соседнего теста порядка: поиск без закрытия тега, с guard'ом -1
    const src = readFileSync(fileURLToPath(new URL('../WikiSettings.tsx', import.meta.url)), 'utf8');
    const iTriage = src.indexOf('<WikiChatTriageList');
    expect(iTriage).toBeGreaterThanOrEqual(0);
    // мутация (5): поменять местами <WikiChatTriageList /> и <WikiPurgeBlock /> — краснеет;
    // убрать любой из двух блоков из стопки — тоже краснеет (-1 не проходит)
    expect(iTriage).toBeLessThan(src.indexOf('<WikiPurgeBlock'));
  });
});
