// Г19: закрытие двух непокрытых веток детского пикера предметов (хвосты Г6: в App-тесте
// useIsMobile мокается false и childApi всегда разрешается — эти ветки не проверялись).
// Здесь — сам компонент:
// 1) «Нет связи с сервером» + «Попробовать ещё раз» (ChildSubjectPicker.tsx:81-94): ретрай
//    работоспособен — клик → второй запрос → при успехе плитки (этого раньше не доказывал
//    ни один тест);
// 2) мобильная сетка — 1 колонка, широкая — 3 (ChildSubjectPicker.tsx:121: App-тест мокает
//    useIsMobile=() => false, то есть живёт только в широкой; заявленная мобильная не проверялась).
// Окружение — node + linkedom, как в соседних .test.tsx (ChildRoutePicker): useIsMobile
// настоящий, управляется через window.matchMedia; childApi — мок с журналом запросов и
// флагом «нет связи».
// Мутации (доказаны прогоном в копии дерева, вывод — в отчёте задачи):
// (1) retry() → no-op: «клик по ретраю … второй запрос» краснеет;
// (2) мобильная ветка сетки → 'repeat(3, minmax(0, 1fr))': «мобильная … 1fr» краснеет.

import { describe, it, expect, beforeEach, vi } from 'vitest';
import { act, createElement } from 'react';
import { createRoot } from 'react-dom/client';
import { parseHTML } from 'linkedom';

const { window: win, document: doc } = parseHTML('<!doctype html><html><body><div id="root"></div></body></html>');
// Мобильный флаг: useIsMobile читает window.matchMedia при mount (lib/breakpoints.ts:63-75)
let mobile = false;
Object.assign(win, {
  matchMedia: () => ({ matches: mobile, addEventListener: () => {}, removeEventListener: () => {} }),
});
vi.stubGlobal('window', win);
vi.stubGlobal('document', doc);
(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true;

// ── childApi: журнал запросов + флаг «нет связи»; subjects задаёт тест ──
const apiState = vi.hoisted(() => ({ calls: 0, offline: true, subjects: [] as unknown[] }));
vi.mock('./api', () => ({
  __esModule: true,
  childApi: {
    getSubjects: () => {
      apiState.calls += 1;
      return apiState.offline ? Promise.reject(new Error('offline')) : Promise.resolve(apiState.subjects);
    },
  },
}));

import { ChildSubjectPicker } from './ChildSubjectPicker';
import type { ChildSubjectWithViewers } from './api';

// Форма записи GET /api/child/subjects (ChildSubjectView): включённые, порядок профиля (И5)
const subj = (id: string, label: string): ChildSubjectWithViewers => ({ id, label, glyph: '×÷', color: 'info', tutor: null });

const hasText = (root: Element, text: string) => (root.textContent ?? '').includes(text);

// React держит props узла под __reactProps$…; зовём onClick напрямую (без событийной обвязки)
const reactProps = (el: Element): { onClick?: () => void } | undefined => {
  const key = Object.keys(el).find((k) => k.startsWith('__reactProps$'));
  return key ? (el as unknown as Record<string, unknown>)[key] as { onClick?: () => void } : undefined;
};

async function mountPicker(): Promise<HTMLElement> {
  const host = doc.createElement('div');
  doc.getElementById('root')!.appendChild(host);
  const root = createRoot(host as unknown as HTMLElement);
  await act(async () => { root.render(createElement(ChildSubjectPicker, { onPick: () => {} })); });
  return host as HTMLElement;
}

// Сетка — контейнер плиток (ChildSubjectPicker.tsx:121): её style и есть число колонок
const gridColumns = (host: HTMLElement): string => {
  const tile = host.querySelector('[data-testid="child-subject-tile-s1"]');
  expect(tile, 'плитки предмета нет на экране').not.toBeNull();
  return String((tile!.parentElement as HTMLElement).style.gridTemplateColumns);
};

describe('ChildSubjectPicker (Г19): нет связи → ретрай → предметы; сетка по ширине', () => {
  beforeEach(() => { apiState.calls = 0; apiState.offline = true; apiState.subjects = []; mobile = false; });

  it('нет связи: «Нет связи с сервером» и кнопка; клик по ретраю — второй запрос, при успехе — плитки', async () => {
    apiState.subjects = [subj('s1', 'Математика'), subj('s2', 'История')];
    const host = await mountPicker();

    // Состояние ошибки: текст + кнопка, сетки нет
    expect(hasText(host, 'Нет связи с сервером'), 'текста об отсутствии связи нет').toBe(true);
    expect(hasText(host, 'Попробуй позже.')).toBe(true);
    const retry = host.querySelector('[data-testid="child-subjects-retry"]');
    expect(retry, 'кнопки «Попробовать ещё раз» нет').not.toBeNull();
    expect(hasText(retry!, 'Попробовать ещё раз'), 'подписи на кнопке нет').toBe(true);
    expect(host.querySelector('[data-testid="child-subject-picker"]'), 'сетка при ошибке на экране').toBeNull();
    expect(apiState.calls, 'первого запроса предметов нет').toBe(1);

    // Тот же кнопка, сервер «вернулся»: запрос повторяется, сетка рисуется
    apiState.offline = false;
    await act(async () => { reactProps(retry!)?.onClick?.(); });
    expect(apiState.calls, 'клик по ретраю не послал второй запрос').toBe(2);
    expect(host.querySelector('[data-testid="child-subject-tile-s1"]'), 'плитки после успешного ретрая нет').not.toBeNull();
    expect(host.querySelector('[data-testid="child-subject-tile-s2"]'), 'второй плитки после ретрая нет').not.toBeNull();
    expect(hasText(host, 'Нет связи с сервером'), 'текст ошибки остался после успеха').toBe(false);
  });

  it('мобильная сетка предметов — 1 колонка; широкая — 3', async () => {
    apiState.offline = false;
    apiState.subjects = [subj('s1', 'Математика'), subj('s2', 'История')];

    mobile = true;
    try {
      const host = await mountPicker();
      expect(gridColumns(host), 'мобильная сетка не одна колонка').toBe('1fr');
    } finally { mobile = false; }

    const host = await mountPicker();
    expect(gridColumns(host), 'широкая сетка не три колонки').toBe('repeat(3, minmax(0, 1fr))');
  });
});
