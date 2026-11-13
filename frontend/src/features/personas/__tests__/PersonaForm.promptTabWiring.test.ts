// F31: связывание вкладки «Промпт» в PersonaForm — имя специальности и признак «админ поправил»
// доходят до PersonaPromptTab пропами. Регрессия, которую ловим: пропы оказались за `>` открывающего
// тега и рендерились литеральным текстом формы (`specialtyTitle=none`).

import { act, createElement } from 'react';
import { afterAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { PersonaForm } from '../PersonaForm';
import { setAllFlags } from '../../../lib/featureFlags';
import type { Persona } from '../../../types';

vi.mock('../../../lib/breakpoints', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../../lib/breakpoints')>();
  return { ...orig, useIsMobile: () => true };
});
vi.mock('../../../lib/aiJobStore', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../../lib/aiJobStore')>();
  return { ...orig, useAiJob: orig.getAiJob };
});
vi.mock('../../../lib/personas', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../../lib/personas')>();
  return { ...orig, bumpPersonas: vi.fn() };
});

const { api } = await import('../../../lib/api');
const { installFakeDom, mount, uninstallFakeDom, reactProps, textNodes } =
  await import('../../../components/chat/__tests__/fakeDom');

const BASE = {
  id: 'per-1', ownerId: 'o1', name: 'Светлана', role: 'Дизайнер', handle: 'designer',
  scope: 'global', memoryEnabled: false, specialty: 'frontend', changedByAdminSince: true,
  access: 'full', tools: ['tasks', 'notes', 'web'], disallowedTools: null,
} as Persona;

type N = ReturnType<typeof mount>['container'];

function findButton(root: N, label: string): N {
  let hit: N | null = null;
  const walk = (x: N) => {
    if (hit) return;
    if (x.nodeName === 'BUTTON' && textNodes(x).join('') === label) { hit = x; return; }
    for (const c of x.childNodes) walk(c);
  };
  walk(root);
  expect(hit, `кнопки «${label}» нет`).toBeTruthy();
  return hit!;
}

beforeEach(() => { setAllFlags({}); });
afterAll(() => { vi.restoreAllMocks(); uninstallFakeDom(); });

describe('PersonaForm: связывание вкладки «Промпт» (F31)', () => {
  it('имя специальности и признак админ-правки доходят до PersonaPromptTab, литеральных пропов в форме нет', async () => {
    installFakeDom();
    const g = globalThis as { window?: Record<string, unknown>; localStorage?: unknown; sessionStorage?: unknown };
    if (g.window) {
      g.window.history = { state: null, pushState: () => {}, replaceState: () => {} };
      g.window.addEventListener ??= () => {};
      g.window.removeEventListener ??= () => {};
    }
    g.localStorage ??= { getItem: () => null, setItem: () => {}, removeItem: () => {} };
    g.sessionStorage ??= { getItem: () => null, setItem: () => {}, removeItem: () => {} };
    vi.spyOn(api.personas, 'avatarCaps').mockResolvedValue({ generate: false } as never);
    vi.spyOn(api.personas, 'memory').mockResolvedValue([]);
    vi.spyOn(api.personas, 'focus').mockResolvedValue(null as never);
    vi.spyOn(api.personas, 'getPromptSections').mockResolvedValue([
      { id: 'role', enabled: true, text: 'Ты — дизайнер.', enabledSource: 'code', textSource: 'code' },
    ]);
    const { container } = mount(createElement(PersonaForm, {
      persona: BASE, projects: [], onSaved: () => {}, onCancelEdit: () => {}, onStatus: () => {},
    }));
    await act(async () => { await new Promise(r => setTimeout(r, 0)); });
    await act(async () => { (reactProps(findButton(container, 'Промпт')) as { onClick: () => void }).onClick(); });

    const text = textNodes(container).join('');
    expect(text).not.toContain('specialtyTitle=');
    expect(text).not.toContain('changedByAdminSince=');
    expect(text).toContain('Админ только что поправил у «frontend» секции');
    expect(text).toContain('специальность «frontend»');
    expect(text).not.toContain('«none»');
  });
});
