// A3b: тело сохранения формы персоны по режиму группы прав. «Как у специальности» —
// inheritsAccess:true без access/tools/disallowedTools (смена роли в другой вкладке не
// должна перезаписываться устаревшей копией); «Своё» — false вместе с полями прав.
// Окружение node: фейк-DOM (createRoot + act), save() — через императивный ref формы.

import { afterAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, createElement, createRef } from 'react';
import type { Persona } from '../../../types';
import type { PersonaFormHandle } from '../PersonaForm';

vi.mock('../../../lib/breakpoints', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../../lib/breakpoints')>();
  return { ...orig, useIsMobile: () => true };
});
vi.mock('../../../lib/aiJobStore', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../../lib/aiJobStore')>();
  return { ...orig, useAiJob: orig.getAiJob };
});

// bumpPersonas — заглушка: иначе после сохранения идёт реальный reloadPersonas
vi.mock('../../../lib/personas', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../../lib/personas')>();
  return { ...orig, bumpPersonas: vi.fn() };
});

const { api } = await import('../../../lib/api');
const { installFakeDom, mount, uninstallFakeDom } = await import('../../../components/chat/__tests__/fakeDom');
const { PersonaForm } = await import('../PersonaForm');

const BASE = {
  id: 'per-1', ownerId: 'o1', name: 'Светлана', role: 'Дизайнер', handle: 'designer',
  scope: 'global', memoryEnabled: false, specialty: 'frontend',
  access: 'readOnly', tools: ['tasks'], disallowedTools: null,
} as Persona;

async function saveBody(p: Partial<Persona>): Promise<Record<string, unknown>> {
  installFakeDom();
  const g = globalThis as { window?: Record<string, unknown>; localStorage?: unknown; sessionStorage?: unknown };
  if (g.window) {
    g.window.history = { state: null, pushState: () => {}, replaceState: () => {} };
    g.window.addEventListener ??= () => {};
    g.window.removeEventListener ??= () => {};
  }
  g.localStorage ??= { getItem: () => null, setItem: () => {}, removeItem: () => {} };
  g.sessionStorage ??= { getItem: () => null, setItem: () => {}, removeItem: () => {} };
  const update = vi.spyOn(api.personas, 'update').mockResolvedValue({ ...BASE, ...p } as Persona);
  vi.spyOn(api.personas, 'avatarCaps').mockResolvedValue({ generate: false } as never);
  vi.spyOn(api.personas, 'memory').mockResolvedValue([]);
  vi.spyOn(api.personas, 'focus').mockResolvedValue(null as never);
  const ref = createRef<PersonaFormHandle>();
  mount(createElement(PersonaForm, {
    ref, persona: { ...BASE, ...p } as Persona, projects: [], onSaved: () => {},
  }));
  await act(async () => { await new Promise(r => setTimeout(r, 0)); });
  await act(async () => { await ref.current!.save(); });
  expect(update).toHaveBeenCalledTimes(1);
  return update.mock.calls[0][1] as Record<string, unknown>;
}

beforeEach(() => { vi.restoreAllMocks(); });
afterAll(uninstallFakeDom);

describe('PersonaForm: тело сохранения по режиму прав (A3b)', () => {
  it('в режиме «как у специальности» тело сохранения не содержит access/tools/disallowedTools', async () => {
    const body = await saveBody({ inheritsAccess: true });
    expect(body.inheritsAccess).toBe(true);
    expect(body).not.toHaveProperty('access', 'readOnly');
    expect(body.access).toBeUndefined();
    expect(body.tools).toBeUndefined();
    expect(body.disallowedTools).toBeUndefined();
  });

  it('в режиме «Своё» уходит inheritsAccess:false вместе с полями прав', async () => {
    const body = await saveBody({ inheritsAccess: false });
    expect(body.inheritsAccess).toBe(false);
    expect(body.access).toBe('readOnly');
    expect(body.tools).toEqual(['tasks']);
    expect(body.disallowedTools).toEqual([]);
  });
});
