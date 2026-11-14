// W4-FB1, доработка 2 (дефект b505fa3d): «Новый чат» в пустом центре проекта (handleCreateSession)
// и «Зафиксировать через чат» (handleCommitVia) собирают контекст для createChatWithContextPersona
// через projectChatContextFor. Без myRole в литерале резолвер не видит, что проект общий:
// владелец общего проекта без руководителя молча получает личную персону — бэк отказывает
// личную (глобальную) персону в общем чате (global_persona_in_shared). Путь теперь тот же,
// что у SessionList (SessionList.tsx:439 передаёт полный Project).
//
// Тест: общий проект без руководителя, владелец → myRole доходит до резолвера, личная персона
// НЕ выбрана. Мутация (убрать myRole из литерала в projectChatContextFor) → первый кейс
// красный: резолвер возвращает personal-default.

import { beforeEach, describe, expect, it, vi } from 'vitest';
import { projectChatContextFor } from './WorkspacePage';
import { clearMe, getContextDefaultPersonaId, setMeFromServer } from '../lib/defaultPersona';
import type { Me, Project } from '../types';

// signalr — no-op: setMeFromServer wire-ит подписку personas_changed, SignalR в node нет
// (относительный URL /hubs/session не резолвится).
vi.mock('../lib/signalr', () => ({ onMessage: () => () => {} }));

// localStorage — vi.hoisted: модули графа страницы читают его при импорте (как в deeplinkChat-тесте)
vi.hoisted(() => {
  (globalThis as any).self = globalThis;
  const store = new Map<string, string>();
  (globalThis as any).localStorage = {
    getItem: (k: string) => store.get(k) ?? null,
    setItem: (k: string, v: string) => { store.set(k, String(v)); },
    removeItem: (k: string) => store.delete(k),
    clear: () => store.clear(),
    key: () => null,
    get length() { return store.size; },
  } as Storage;
});

const me = (defaultPersonaId: string | null): Me => ({
  userId: 'u1', username: 'test', role: 'user', defaultPersonaId,
});

// Минимальный проект: сборка контекста читает только id, ownerId, defaultPersonaId, myRole
const baseProject = {
  id: 'p1', name: 'Проект', rootPath: '/p', createdAt: '2026-01-01T00:00:00Z', updatedAt: '2026-01-01T00:00:00Z',
} as Project;

describe('WorkspacePage: контекст чата проекта (b505fa3d)', () => {
  beforeEach(() => clearMe());

  it('общий проект без руководителя, владелец: myRole доходит до резолвера, личная персона не выбрана', () => {
    setMeFromServer(me('personal-default'));
    const ctx = projectChatContextFor({ ...baseProject, ownerId: 'u1', myRole: 'owner' as const }, null);
    expect(ctx.myRole, 'myRole отсутствует в литерале — резолвер не увидит общий проект').toBe('owner');
    expect(getContextDefaultPersonaId(ctx), 'личная персона в общем проекте недопустима').toBeNull();
  });

  it('тот же проект для редактора: myRole доходит, личная персона не выбрана', () => {
    setMeFromServer(me('personal-default'));
    const ctx = projectChatContextFor({ ...baseProject, ownerId: 'u1', myRole: 'editor' as const }, null);
    expect(ctx.myRole).toBe('editor');
    expect(getContextDefaultPersonaId(ctx)).toBeNull();
  });

  it('обычный проект (myRole нет): поведение прежнее — фолбэк на личную персону владельца', () => {
    setMeFromServer(me('personal-default'));
    const ctx = projectChatContextFor({ ...baseProject, ownerId: 'u1' }, null);
    expect(ctx.myRole).toBeUndefined();
    expect(getContextDefaultPersonaId(ctx)).toBe('personal-default');
  });

  it('живой дефолт из ref важнее сохранённой копии проекта', () => {
    const ctx = projectChatContextFor(
      { ...baseProject, ownerId: 'u1', myRole: 'editor' as const, defaultPersonaId: 'stale-lead' }, 'live-lead');
    expect(ctx.defaultPersonaId).toBe('live-lead');
    expect(getContextDefaultPersonaId(ctx)).toBe('live-lead');
  });
});
