// F31: текст секции длиннее 1024 в режиме «Своё» блокирует «Сохранить» формы персоны (P2b).
// Форма сообщает canSave через onStatus (его читает тулбар) и футером; драйвим фейк-DOM:
// вкладка «Промпт» → «Своё» у секции → длинный текст в textarea.

import { act, createElement } from 'react';
import { afterAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { PersonaForm, type PersonaFormStatus } from '../PersonaForm';
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
const { installFakeDom, mount, uninstallFakeDom, findNodeByAttr, reactProps, textNodes } =
  await import('../../../components/chat/__tests__/fakeDom');

const BASE = {
  id: 'per-1', ownerId: 'o1', name: 'Светлана', role: 'Дизайнер', handle: 'designer',
  scope: 'global', memoryEnabled: false, specialty: 'frontend',
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

describe('PersonaForm: блокировка сохранения по лимиту секции (F31)', () => {
  async function mountOnPromptTab() {
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
    const statuses: PersonaFormStatus[] = [];
    const { container } = mount(createElement(PersonaForm, {
      persona: BASE, projects: [], onSaved: () => {}, onCancelEdit: () => {}, onStatus: s => statuses.push(s),
    }));
    await act(async () => { await new Promise(r => setTimeout(r, 0)); });
    await act(async () => { (reactProps(findButton(container, 'Промпт')) as { onClick: () => void }).onClick(); });
    await act(async () => { (reactProps(findButton(container, 'Своё')) as { onClick: () => void }).onClick(); });
    const type = async (text: string) => {
      const area = findNodeByAttr(container, 'aria-label', 'Свой текст секции «role»');
      expect(area, 'textarea секции нет').toBeTruthy();
      await act(async () => { (reactProps(area!) as { onChange: (e: unknown) => void }).onChange({ target: { value: text } }); });
    };
    const footerSave = () => {
      const footer = findNodeByAttr(container, 'data-testid', 'persona-form-footer');
      return findButton(footer!, 'Сохранить');
    };
    return { container, statuses, type, footerSave };
  }

  it('≤ 1024 — «Сохранить» доступна; > 1024 — canSave=false и кнопка футера disabled; сократили — снова доступна', async () => {
    const { statuses, type, footerSave, container } = await mountOnPromptTab();
    await type('а'.repeat(1024));
    expect(statuses.at(-1)?.canSave).toBe(true);
    expect((reactProps(footerSave()) as { disabled?: boolean }).disabled).toBeFalsy();

    await type('а'.repeat(1025));
    expect(statuses.at(-1)?.canSave).toBe(false);
    expect((reactProps(footerSave()) as { disabled?: boolean }).disabled).toBe(true);
    expect(textNodes(container).join('')).toContain('Длиннее 1024 символов');
    const area = findNodeByAttr(container, 'aria-label', 'Свой текст секции «role»');
    expect(area!.getAttribute('aria-invalid')).toBe('true');

    await type('а'.repeat(1000));
    expect(statuses.at(-1)?.canSave).toBe(true);
  });
});
