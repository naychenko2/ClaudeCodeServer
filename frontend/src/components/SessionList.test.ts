// Кламп groupBy в SessionList (Минор 1 по R4): при выключенном флаге features-overview
// сохранённый в localStorage groupBy='features' не должен уводить список в режим фич
// (иначе — EmptyState «Фич пока нет» без пункта «Фичи» в тулбаре). Клампим к 'days',
// как 'subjects' в глобальном списке (ChatList.tsx); при включённом флаге значение
// из хранилища снова работает.
//
// Окружение node (без jsdom): renderToStaticMarkup (SSR) — effects не идут, поэтому
// sessions остаются пустыми; различие режимов ловим по строке «Фич пока нет», которая
// есть ТОЛЬКО в режиме фич (и зависит от индекса фич, а не от sessions).

import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { SessionList } from './SessionList';
import { setAllFlags } from '../lib/featureFlags';
import { __test as featuresIndexTest } from '../lib/featuresIndex';
import type { Project } from '../types';

// useSubjectRun — useSyncExternalStore БЕЗ getServerSnapshot: legacy-SSR-рендерер
// (renderToStaticMarkup) его роняет. В моих кейсах subjectsMode всегда false, поэтому
// значение хука не потребляется — достаточно заглушки (остальные экспорты сохраняем).
vi.mock('../lib/chatSubjects', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../lib/chatSubjects')>();
  return { ...orig, useSubjectRun: () => null as never };
});

// Окружение node — localStorage нет; минимальная реализация на Map (как в chatFilters.test.ts)
const store = new Map<string, string>();
globalThis.localStorage = {
  getItem: (k: string) => store.get(k) ?? null,
  setItem: (k: string, v: string) => { store.set(k, String(v)); },
  removeItem: (k: string) => { store.delete(k); },
  clear: () => store.clear(),
  key: () => null,
  get length() { return store.size; },
} as Storage;

// Минимальный проект: SessionList в рендере читает id, tagRegistry, subjectRegistry,
// subjectsSweepSince (остальные поля эффектов — в SSR не идут).
const project: Project = {
  id: 'p1', name: 'Проект', rootPath: '/p', createdAt: '2026-01-01T00:00:00Z', updatedAt: '2026-01-01T00:00:00Z',
  tagRegistry: [], subjectRegistry: [], subjectsSweepSince: null,
} as Project;

// Рендер списка: флаг features-overview + groupBy, сохранённый в хранилище.
// Индекс фич сбрасываем в EMPTY — в режиме фич это даёт «Фич пока нет».
function renderList(featuresOn: boolean, storedGroupBy: string): string {
  store.clear();
  setAllFlags({ 'features-overview': featuresOn });
  featuresIndexTest.reset();
  store.set('cc_chat_filters:p1', JSON.stringify({ groupBy: storedGroupBy, sortOrder: 'newest' }));
  return renderToStaticMarkup(
    createElement(SessionList, { project, activeSession: null, onSelect: () => {} }),
  );
}

beforeEach(() => {
  store.clear();
  setAllFlags({});
  featuresIndexTest.reset();
});

describe('SessionList: кламп groupBy при выключенном флаге (Минор 1)', () => {
  it('флаг выключен + groupBy=features в хранилище → режим «Дни», «Фич пока нет» нет', () => {
    const html = renderList(false, 'features');
    // Режим фичи не включился: её пустого состояния нет
    expect(html).not.toContain('Фич пока нет');
  });

  it('флаг включён + groupBy=features → режим фич («Фич пока нет» при пустом индексе)', () => {
    const html = renderList(true, 'features');
    // Режим фичи включился: пустой индекс → её пустое состояние
    expect(html).toContain('Фич пока нет');
  });

  it('флаг выключен + groupBy=days (обычное значение) → режим «Дни», без «Фич пока нет»', () => {
    const html = renderList(false, 'days');
    expect(html).not.toContain('Фич пока нет');
  });
});
