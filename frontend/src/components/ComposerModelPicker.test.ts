// Композер: локальные провайдеры (isLocal с бэка) сведены в одну группу «Локальные»,
// облачные — по провайдеру, как раньше. Мутация: группировать по provider — у Flash-Next
// и Gemma окажутся две разные группы, первый assert краснеет; снять подсказку переноса
// у локальной группы — краснеет последний.

import { describe, expect, it, vi } from 'vitest';
import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import type { ComposerMenuGroup } from './ComposerMenu';

const captured = vi.hoisted(() => ({ groups: [] as ComposerMenuGroup[] }));
vi.mock('./ComposerMenu', () => ({
  ComposerMenu: (props: { groups?: ComposerMenuGroup[] }) => { captured.groups = props.groups ?? []; return null; },
}));
vi.mock('../lib/api', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../lib/api')>();
  const caps = (displayName: string, isLocal: boolean) => ({
    displayName, isLocal, supportsPlanMode: false, supportsCompact: true, supportsMcp: true,
    supportsEffort: false, supportsPermissionModes: true, supportsImages: false, supportsAgents: true,
  });
  return {
    ...orig,
    api: {
      ...orig.api,
      models: {
        ...orig.api.models,
        list: async () => ({
          models: [
            { value: 'sonnet', displayName: 'Sonnet', provider: 'claude' },
            { value: 'qwen3.8-flash-500k', displayName: 'Flash-Next · окно 512K', provider: 'local-qwen' },
            { value: 'gemma-4-26b-a4b', displayName: 'Gemma 4 26B-A4B · окно 256K', provider: 'local-video-gemma' },
            { value: 'MiniMax-M3.1-Flash[1m]', displayName: 'MiniMax M3.1 Flash', provider: 'minimax' },
          ],
          providers: {
            'local-qwen': { provider: 'local-qwen', ...caps('Локальная Flash-Next (Strata)', true) },
            'local-video-gemma': { provider: 'local-video-gemma', ...caps('Локальная Gemma 4 (Video)', true) },
            minimax: { provider: 'minimax', ...caps('MiniMax', false) },
          },
        }),
      },
    },
  };
});

const { loadModels } = await import('../lib/models');
const { ComposerModelPicker } = await import('./ComposerModelPicker');

describe('ComposerModelPicker: группа «Локальные»', () => {
  it('Flash-Next и Gemma — одна группа «Локальные»; облачные — по провайдеру', async () => {
    await loadModels();
    renderToStaticMarkup(createElement(ComposerModelPicker, { value: 'sonnet', onChange: () => {}, started: false }));
    const local = captured.groups.filter(g => g.label === 'Локальные');
    expect(local).toHaveLength(1);
    expect(local[0].items.map(i => i.value)).toEqual(['qwen3.8-flash-500k', 'gemma-4-26b-a4b']);
    expect(captured.groups.map(g => g.label)).toEqual(expect.arrayContaining(['Claude', 'MiniMax', 'Локальные']));
    expect(captured.groups.some(g => g.label?.startsWith('Локальная '))).toBe(false);
  });

  it('начатый чат на Flash-Next: у «Локальных» подсказка переноса (там есть Gemma другого провайдера)', async () => {
    await loadModels();
    renderToStaticMarkup(createElement(ComposerModelPicker, { value: 'qwen3.8-flash-500k', onChange: () => {}, started: true }));
    const local = captured.groups.find(g => g.label === 'Локальные')!;
    expect(local.note).toBe('Перенесёт чат к другой модели — контекст сохранится');
  });
});
