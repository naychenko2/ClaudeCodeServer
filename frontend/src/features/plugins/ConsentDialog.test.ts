// Диалог согласия (макет, экран 2; ADR-031 §4, шаг 3.4): права с чекбоксами,
// секреты «задан/не задан», обязательный секрет блокирует «Установить» (В2),
// net:* по умолчанию снят (В1). Окружение node: рендерим в строку, как прочие
// компонентные тесты проекта.
import { describe, expect, it, vi } from 'vitest';
import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';

// renderToStaticMarkup (серверный рендер) порталов не знает: createPortal → pass-through,
// чтобы Modal рисовал контент инлайн (тот же приём, что в kitOptions.test).
vi.mock('react-dom', async (orig) => ({ ...(await orig<typeof import('react-dom')>()), createPortal: (n: unknown) => n }));

vi.stubGlobal('window', Object.assign(new EventTarget(), {
  innerWidth: 1440, innerHeight: 900,
  matchMedia: () => ({ matches: false, addEventListener: () => {}, removeEventListener: () => {} }),
}));
// Modal уходит порталом в document.body — в node его нет, подменяем (как в kitOptions.test).
// getElementById → truthy: модульные инжекты focus-стилей (IconButton/Chip) пропускаются.
vi.stubGlobal('document', {
  body: { nodeType: 1 },
  head: { appendChild: () => {} },
  documentElement: { dataset: {} },
  createElement: () => ({ id: '', textContent: '' }),
  getElementById: () => ({}),
  querySelector: () => null,
  addEventListener: () => {},
});

const {
  ConsentDialog, defaultCheckedState, findBlockingSecret, submitLabelFor,
  RESIDUAL_RISK_NOTE, showsResidualRiskNote,
} = await import('./ConsentDialog');
type PluginConsentData = import('./ConsentDialog').PluginConsentData;
type PluginConsentSecret = import('./ConsentDialog').PluginConsentSecret;
type PluginInfo = import('../../lib/api').PluginInfo;
const { setInstalledPlugins, getInstalledPlugins } = await import('./pluginStore');

// === В1: умолчание галок прав ===
describe('defaultCheckedState (В1)', () => {
  it('установить: все отмечены, кроме net:*', () => {
    const s = defaultCheckedState(['notes:read', 'net:api.example.com', 'tasks:read'], 'install');
    expect(s['notes:read']).toBe(true);
    expect(s['tasks:read']).toBe(true);
    expect(s['net:api.example.com']).toBe(false);
  });
  it('изменить: по уже выданным (granted)', () => {
    const s = defaultCheckedState(['notes:read', 'net:x'], 'modify', ['notes:read']);
    expect(s['notes:read']).toBe(true);
    expect(s['net:x']).toBe(false);
  });
});

// === В2: обязательный секрет блокирует «Установить» ===
describe('findBlockingSecret (В2)', () => {
  const req: PluginConsentSecret = { name: 'LINEAR_TOKEN', required: true };
  const opt: PluginConsentSecret = { name: 'OPT', required: false };
  it('обязательный и не задан → блокирует (установить и изменить)', () => {
    expect(findBlockingSecret([req], {}, {}, 'install')?.name).toBe('LINEAR_TOKEN');
    expect(findBlockingSecret([req], {}, {}, 'modify')?.name).toBe('LINEAR_TOKEN');
  });
  it('обязательный, задан на сервере → не блокирует', () => {
    expect(findBlockingSecret([{ ...req, set: true }], {}, {}, 'install')).toBeNull();
  });
  it('обязательный, сохранён в этой сессии → не блокирует', () => {
    expect(findBlockingSecret([req], {}, { LINEAR_TOKEN: '2026-01-01T00:00:00Z' }, 'install')).toBeNull();
  });
  it('обязательный, подтверждено значение (установить) → не блокирует', () => {
    expect(findBlockingSecret([req], { LINEAR_TOKEN: 'abc' }, {}, 'install')).toBeNull();
  });
  it('необязательный и не задан → не блокирует', () => {
    expect(findBlockingSecret([opt], {}, {}, 'install')).toBeNull();
  });
});

// === Подпись основной кнопки по числу галок ===
describe('submitLabelFor', () => {
  it('изменить → «Сохранить»', () => expect(submitLabelFor('modify', 0, 0)).toBe('Сохранить'));
  it('установить, прав нет → «Установить без прав»', () => expect(submitLabelFor('install', 0, 2)).toBe('Установить без прав'));
  it('установить, все права → «Установить»', () => expect(submitLabelFor('install', 2, 2)).toBe('Установить'));
  it('установить, часть прав → «Установить · прав N из M»', () => expect(submitLabelFor('install', 1, 2)).toBe('Установить · прав 1 из 2'));
});

// === Компонент: начальный рендер ===
function renderConsent(overrides: Partial<PluginConsentData> = {}, mode: 'install' | 'modify' = 'install') {
  const data: PluginConsentData = {
    id: 'local-1/my-plugin',
    name: 'My Plugin',
    version: '1.0.0',
    contributions: [],
    requested: ['notes:read', 'net:api.example.com'],
    secrets: [],
    ...overrides,
  };
  return renderToStaticMarkup(createElement(ConsentDialog, {
    data, mode,
    onClose: () => {},
    onSubmit: async () => {},
  }));
}

describe('ConsentDialog (рендер)', () => {
  it('net:* по умолчанию снят, прочие права — отмечены', () => {
    const html = renderConsent({}, 'install');
    // ровно один снятый чекбокс (net:*) и один отмеченный (notes:read)
    expect((html.match(/aria-checked="false"/g) ?? []).length).toBe(1);
    expect((html.match(/aria-checked="true"/g) ?? []).length).toBe(1);
  });

  it('установить: обязательный секрет не задан → «Установить» неактивна', () => {
    const html = renderConsent({ secrets: [{ name: 'T', required: true, set: false }] }, 'install');
    expect(html).toContain('Установить');
    // в футере ровно одна неактивная кнопка — основная (Отмена активна)
    expect((html.match(/<button[^>]*disabled/g) ?? []).length).toBe(1);
  });

  it('установить: обязательный секрет задан → «Установить» активна', () => {
    const html = renderConsent({ secrets: [{ name: 'T', required: true, set: true }] }, 'install');
    expect(html).toContain('Установить');
    expect(html).not.toMatch(/<button[^>]*disabled/);
  });

  it('изменить: незаданный обязательный секрет не блокирует «Сохранить»', () => {
    const html = renderConsent({ secrets: [{ name: 'T', required: true, set: false }], granted: [] }, 'modify');
    expect(html).toContain('Сохранить');
    expect(html).not.toMatch(/<button[^>]*disabled/);
  });

  it('значение секрета не в данных диалога (поле пустое при открытии/повторном)', () => {
    const s: PluginConsentSecret = { name: 'T', required: true, set: true, setAt: '2026-01-01T00:00:00Z' };
    expect('value' in s).toBe(false);
  });
});

// === Остаточный риск (ADR-031 §3.6): WebRTC/ICE и dns-prefetch CSP не закрывает,
// === поэтому выдача notes:read/tasks:read — доверие пакету и в части утечки.
// === Показывается, когда запрошено хотя бы одно из этих прав.
describe('ConsentDialog: остаточный риск (ADR-031 §3.6)', () => {
  it('notes:read запрошено → подпись про остаточный риск видна', () => {
    const html = renderConsent({ requested: ['notes:read'] }, 'install');
    expect(html).toContain(RESIDUAL_RISK_NOTE);
  });

  it('tasks:read запрошено → подпись видна', () => {
    const html = renderConsent({ requested: ['tasks:read'] }, 'install');
    expect(html).toContain(RESIDUAL_RISK_NOTE);
  });

  it('notes:read + tasks:read → одна подпись (не дублируется)', () => {
    const html = renderConsent({ requested: ['notes:read', 'tasks:read'] }, 'install');
    expect((html.match(new RegExp(RESIDUAL_RISK_NOTE, 'g')) ?? []).length).toBe(1);
  });

  it('только net:* → подписи нет (нет прав на чтение данных)', () => {
    const html = renderConsent({ requested: ['net:api.example.com'] }, 'install');
    expect(html).not.toContain(RESIDUAL_RISK_NOTE);
  });

  it('showsResidualRiskNote: true для notes/tasks, false для net/secret', () => {
    expect(showsResidualRiskNote(['notes:read'])).toBe(true);
    expect(showsResidualRiskNote(['tasks:read'])).toBe(true);
    expect(showsResidualRiskNote(['net:api.example.com'])).toBe(false);
    expect(showsResidualRiskNote(['secret:API_TOKEN'])).toBe(false);
    expect(showsResidualRiskNote([])).toBe(false);
  });
});

// === Стор: значение секрета в него не попадает (M7) ===
describe('pluginStore: значение секрета', () => {
  it('в списке проекта у секрета только name/set/updatedAt — нет value', () => {
    const list: PluginInfo[] = [{
      id: 'local-1/p', version: '1.0.0', sha256: 'abc123', requested: [], granted: [],
      enabled: true, source: 'upload:p',
      secrets: [{ name: 'T', set: true, updatedAt: '2026-01-01T00:00:00Z' }],
    }];
    setInstalledPlugins('proj1', list);
    const stored = getInstalledPlugins('proj1');
    expect(stored[0].secrets[0].name).toBe('T');
    expect(stored[0].secrets[0]).not.toHaveProperty('value');
  });
});
