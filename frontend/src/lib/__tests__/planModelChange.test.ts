import { describe, it, expect, beforeEach, vi } from 'vitest';
import { planModelChange, loadModels, USAGE } from '../models';
import { api } from '../api';

vi.mock('../api', () => ({ api: { models: { list: vi.fn() } } }));

// Стор моделей наполняется с бэка (/api/models) — в том числе картой assignments
// «место → резолвнутая модель». От неё зависит, какой провайдер стоит за пунктом
// «По умолчанию», поэтому каждый сценарий задаёт её явно.
async function withCatalog(assignments: Record<string, string | null>) {
  vi.mocked(api.models.list).mockResolvedValue({
    models: [
      { value: 'default', displayName: 'По умолчанию' },
      { value: 'opus', displayName: 'Opus', provider: 'claude' },
      { value: 'glm-5.2', displayName: 'GLM 5.2', provider: 'glm' },
    ],
    providers: {
      glm: {
        provider: 'glm', displayName: 'GLM', supportsPlanMode: true, supportsCompact: true,
        supportsMcp: true, supportsEffort: true, supportsPermissionModes: true,
        supportsImages: false, supportsAgents: true,
      },
    },
    assignments,
  });
  await loadModels();
}

const started = { model: 'opus', claudeSessionId: 'abc123' };   // начатый чат на Claude
const fresh = { model: 'opus', claudeSessionId: null };         // ещё не начат

describe('planModelChange — update или миграция при смене модели чата', () => {
  beforeEach(() => vi.clearAllMocks());

  it('регрессия: «По умолчанию» в начатом чате при назначении на чужого провайдера — миграция с КОНКРЕТНОЙ моделью', async () => {
    // Баг «Не указана модель»: чат создан на Opus, назначение места chat-new переведено
    // на GLM. Пустая модель уходила в migrateProvider, а тот её не принимает.
    await withCatalog({ [USAGE.chatNew]: 'glm-5.2' });

    expect(planModelChange('', started)).toEqual({ kind: 'migrate', model: 'glm-5.2' });
  });

  it('назначение места не задано — пустая модель в миграцию не уходит, это сброс через update', async () => {
    // Слот пуст (модель выбирает CLI). Миграция с пустой строкой дала бы тот же тост,
    // от которого чинились, поэтому такой выбор обязан остаться update'ом.
    await withCatalog({ [USAGE.chatNew]: null });

    expect(planModelChange('', started)).toEqual({ kind: 'update' });
  });

  it('тот же провайдер — обычный update, миграцию не трогаем', async () => {
    await withCatalog({ [USAGE.chatNew]: 'opus' });

    expect(planModelChange('', started)).toEqual({ kind: 'update' });   // «По умолчанию» → тоже claude
    expect(planModelChange('opus', started)).toEqual({ kind: 'update' });
  });

  it('чат ещё не начат — чужой провайдер проходит обычным update', async () => {
    // Транскрипта нет, переносить нечего: guard смены провайдера на бэкенде не сработает
    await withCatalog({ [USAGE.chatNew]: 'glm-5.2' });

    expect(planModelChange('glm-5.2', fresh)).toEqual({ kind: 'update' });
    expect(planModelChange('', fresh)).toEqual({ kind: 'update' });
  });

  it('явный выбор чужой модели в начатом чате — миграция на неё', async () => {
    await withCatalog({ [USAGE.chatNew]: 'opus' });

    expect(planModelChange('glm-5.2', started)).toEqual({ kind: 'migrate', model: 'glm-5.2' });
  });

  it('чат уже на стороннем провайдере, возврат к Claude явной моделью — миграция обратно', async () => {
    await withCatalog({ [USAGE.chatNew]: 'glm-5.2' });
    const onGlm = { model: 'glm-5.2', claudeSessionId: 'abc123' };

    expect(planModelChange('opus', onGlm)).toEqual({ kind: 'migrate', model: 'opus' });
  });

  it('чат уже на назначенном провайдере — «По умолчанию» не порождает миграцию в самого себя', async () => {
    // Иначе бэкенд ответил бы «Чат уже на этом провайдере»
    await withCatalog({ [USAGE.chatNew]: 'glm-5.2' });
    const onGlm = { model: 'glm-5.2', claudeSessionId: 'abc123' };

    expect(planModelChange('', onGlm)).toEqual({ kind: 'update' });
  });
});
