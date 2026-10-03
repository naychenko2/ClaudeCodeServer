import { beforeEach, describe, expect, it, vi } from 'vitest';

vi.stubGlobal('window', Object.assign(new EventTarget(), {
  innerWidth: 360, innerHeight: 780,
  matchMedia: () => ({ matches: false, addEventListener: () => {}, removeEventListener: () => {} }),
}));
vi.mock('../../../pages/workspace/panelFill', () => ({ useRequestPanelFill: () => {} }));

const { REVEAL_PANEL_EVENT } = await import('../../../lib/subsystems/registryCore');
const { takeSheetRequest, __resetSheetRequest } = await import('../ContextSheet');

const reveal = (detail: Record<string, unknown>) => window.dispatchEvent(new CustomEvent(REVEAL_PANEL_EVENT, { detail }));

beforeEach(() => __resetSheetRequest());

describe('запрос шторки «Контекст» несёт чат', () => {
  it('просьба чата A не поднимает шторку чата B и не расходуется им', () => {
    reveal({ key: 'chatContext', sessionId: 'A' });
    expect(takeSheetRequest('B')).toBe(false);
    expect(takeSheetRequest('A')).toBe(true);
    expect(takeSheetRequest('A')).toBe(false);
  });

  it('запрос без sessionId берёт любой чат; чужой ключ панели игнорируется', () => {
    reveal({ key: 'files' });
    expect(takeSheetRequest('A')).toBe(false);
    reveal({ key: 'chatContext' });
    expect(takeSheetRequest('B')).toBe(true);
  });
});
