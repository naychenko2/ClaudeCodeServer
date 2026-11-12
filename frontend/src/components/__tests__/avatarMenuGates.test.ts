import { describe, it, expect } from 'vitest';
import { AVATAR_MENU_SUBSYSTEM, gateAvatarMenu } from '../avatarMenuGates';

// Форма пропсов — фрагмент Props AvatarMenu: только поля, которых касается гейт.
interface MenuProps {
  username: string;
  onOpenKnowledge?: () => void;
  onOpenSpend?: () => void;
  onShowHistory?: () => void;
  onShowModelsSpend?: () => void;
  onShowDeploy?: () => void;
  // проп вне карты (за фич-флагом mcp-registry) — гейт его не трогает
  onShowMcpServers?: () => void;
}

const cb = () => undefined;

const fullProps = (): MenuProps => ({
  username: 'an',
  onOpenKnowledge: cb,
  onOpenSpend: cb,
  onShowHistory: cb,
  onShowModelsSpend: cb,
  onShowDeploy: cb,
  onShowMcpServers: cb,
});

describe('gateAvatarMenu: карта и поведение', () => {
  it('карта — ровно пять заявленных ключа (сторож против правки строки)', () => {
    expect({ ...AVATAR_MENU_SUBSYSTEM }).toEqual({
      onOpenKnowledge: 'knowledge',
      onOpenSpend: 'spend',
      onShowHistory: 'changelog',
      onShowModelsSpend: 'llm',
      onShowDeploy: 'deploy',
    });
  });

  it('выключенный ключ → проп undefined — по каждому пункту карты', () => {
    for (const [prop, key] of Object.entries(AVATAR_MENU_SUBSYSTEM)) {
      const gated = gateAvatarMenu(fullProps(), (k) => k !== key);
      expect((gated as Record<string, unknown>)[prop]).toBeUndefined();
    }
  });

  it('включённый ключ → тот же колбэк', () => {
    const props = fullProps();
    const gated = gateAvatarMenu(props, () => true);
    expect(gated.onOpenKnowledge).toBe(cb);
    expect(gated.onOpenSpend).toBe(cb);
    expect(gated.onShowHistory).toBe(cb);
    expect(gated.onShowModelsSpend).toBe(cb);
    expect(gated.onShowDeploy).toBe(cb);
  });

  it('пропы вне карты не меняются', () => {
    const gated = gateAvatarMenu(fullProps(), () => false);
    expect(gated.onShowMcpServers).toBe(cb);
    expect(gated.username).toBe('an');
  });
});
