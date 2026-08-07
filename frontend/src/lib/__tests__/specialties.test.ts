import { describe, it, expect, vi } from 'vitest';

// specialties.ts и presets.ts тянут api (сеть) на уровне модуля — мокаем, как в
// presets.test.ts, тестируем чистую логику сборки слоя
vi.mock('../api', () => ({ api: {} }));

import { emptyLayer, withNewPreset, withTierCell } from '../specialties';
import { presetRoute } from '../presets';

// Регрессия ревью 65d8df66 (CRITICAL 1): inline-сборка цепочки в матрице «Исключений»
// создавала пресет ОДНИМ PUT и тут же — независимо от него — писала ячейку матрицы
// ВТОРЫМ PUT по тому же слою. Оба клона слоя брались из одного и того же устаревшего
// «settings.owner» до создания пресета, поэтому второй ответ (побеждающий по seq)
// перезаписывал первый и стирал только что созданную цепочку. Фикс — PresetOptions
// передаёт СВЕЖИЙ слой (клон + пресет) потребителю через onCreated, а тот дописывает
// ячейку в ТОТ ЖЕ объект и шлёт onSaveLayer ровно один раз (ExceptionsBlock.onPresetCreated).
describe('inline-сборка цепочки в ячейке матрицы — один PUT, не два', () => {
  it('onCreated сливает пресет и ячейку в один клон слоя — onSaveLayer вызывается один раз', () => {
    const onSaveLayer = vi.fn();
    const base = emptyLayer();

    // savePreset (PresetOptions): строит свежий слой с добавленным пресетом
    const layerWithPreset = withNewPreset(base, 'p1', 'Цепочка 1', ['opus', 'sonnet']);

    // onPresetCreated (ExceptionsBlock): дописывает ячейку матрицы в ТОТ ЖЕ layer
    // и сохраняет один раз — это и есть проверяемый инвариант фикса
    const onPresetCreated = (presetId: string, scope: 'global' | 'owner',
      layer: ReturnType<typeof emptyLayer>) => {
      onSaveLayer(scope, withTierCell(layer, 'coding', 'strong', presetRoute(presetId)));
    };
    onPresetCreated('p1', 'owner', layerWithPreset);

    expect(onSaveLayer).toHaveBeenCalledTimes(1);
    const [scope, saved] = onSaveLayer.mock.calls[0] as [string, ReturnType<typeof emptyLayer>];
    expect(scope).toBe('owner');
    expect(saved.presets).toEqual([{ id: 'p1', name: 'Цепочка 1', description: null, steps: ['opus', 'sonnet'] }]);
    expect(saved.specialties.coding?.tierStrong).toBe('preset:p1');
  });

  it('регрессия старого бага: два независимых PUT из устаревшего слоя теряют пресет', () => {
    const onSaveLayer = vi.fn();
    const staleBase = emptyLayer();

    // Старое поведение PresetOptions.savePreset: onSaveLayer пресета отдельным вызовом…
    const layerWithPreset = withNewPreset(staleBase, 'p1', 'Цепочка 1', ['opus']);
    onSaveLayer('owner', layerWithPreset);
    // …и onPick → setCell, который клонирует ячейку от ТОГО ЖЕ устаревшего base —
    // без пресета внутри (именно этот второй ответ побеждал по seq на бэкенде)
    onSaveLayer('owner', withTierCell(staleBase, 'coding', 'strong', presetRoute('p1')));

    expect(onSaveLayer).toHaveBeenCalledTimes(2);
    const secondSaved = onSaveLayer.mock.calls[1][1] as ReturnType<typeof emptyLayer>;
    expect(secondSaved.presets).toEqual([]); // пресет потерян — воспроизводит найденный дефект
  });
});

describe('withNewPreset', () => {
  it('не мутирует базовый слой и добавляет пресет с переданными полями', () => {
    const base = emptyLayer();
    const next = withNewPreset(base, 'p9', 'Моя цепочка', ['opus', 'local']);

    expect(base.presets).toEqual([]);
    expect(next.presets).toEqual([{ id: 'p9', name: 'Моя цепочка', description: null, steps: ['opus', 'local'] }]);
  });
});
