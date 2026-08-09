import { describe, it, expect, vi } from 'vitest';

// specialties.ts и presets.ts тянут api (сеть) на уровне модуля — мокаем, как в
// presets.test.ts, тестируем чистую логику сборки слоя
vi.mock('../api', () => ({ api: {} }));

import { effectiveDefaultTier, effectiveSpecialtyRecord, emptyLayer, mergePresetIntoCell, withDefaultTier, withNewPreset, withTierCell } from '../specialties';
import { presetRoute } from '../presets';

// Регрессия ревью 65d8df66 (CRITICAL 1): inline-сборка цепочки в матрице «Исключений»
// создавала пресет ОДНИМ PUT и тут же — независимо от него — писала ячейку матрицы
// ВТОРЫМ PUT по тому же слою. Оба клона слоя брались из одного и того же устаревшего
// «settings.owner» до создания пресета, поэтому второй ответ (побеждающий по seq)
// перезаписывал первый и стирал только что созданную цепочку. Фикс — PresetOptions
// передаёт СВЕЖИЙ слой (клон + пресет) потребителю через onCreated, а тот дописывает
// ячейку в ТОТ ЖЕ объект через mergePresetIntoCell (специально вынесена из
// ExceptionsBlock.onPresetCreated продакшн-функцией, чтобы тест звал тот же код,
// что и компонент — ревью d23231bd: старая версия теста реимплементировала слияние
// прямо в тесте и не сторожила регресс в самой ExceptionsBlock).
describe('inline-сборка цепочки в ячейке матрицы — один PUT, не два', () => {
  it('mergePresetIntoCell (продакшн-функция ExceptionsBlock.onPresetCreated) сливает пресет и ячейку в один клон слоя', () => {
    const onSaveLayer = vi.fn();
    const base = emptyLayer();

    // savePreset (PresetOptions): строит свежий слой с добавленным пресетом
    const layerWithPreset = withNewPreset(base, 'p1', 'Цепочка 1', ['opus', 'sonnet']);

    // onPresetCreated (ExceptionsBlock) зовёт РОВНО mergePresetIntoCell — тот же вызов,
    // что и в продакшне, и сохраняет результат один раз
    onSaveLayer('owner', mergePresetIntoCell(layerWithPreset, 'coding', 'strong', 'p1'));

    expect(onSaveLayer).toHaveBeenCalledTimes(1);
    const [scope, saved] = onSaveLayer.mock.calls[0] as [string, ReturnType<typeof emptyLayer>];
    expect(scope).toBe('owner');
    expect(saved.presets).toEqual([{ id: 'p1', name: 'Цепочка 1', description: null, steps: ['opus', 'sonnet'] }]);
    expect(saved.specialties.coding?.tierStrong).toBe('preset:p1');
  });

  // Документирующий тест (не зовёт продакшн-код): воспроизводит СТАРЫЙ баг руками, чтобы
  // объяснить, ПОЧЕМУ слияние в один клон (тест выше) обязательно — раздельные PUT от
  // одного и того же устаревшего base гонятся, и второй ответ стирает только что
  // созданный пресет. Регресс этого сценария сторожит только тест выше.
  it('документирует старый баг: два независимых PUT из устаревшего слоя теряют пресет', () => {
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

// План model-settings-reset.md (шаг 3, MINOR 5): маркер строки «есть запись без уровней»
// в ExceptionsBlock (recShadowed/recConfigured) считается ИЗ ДАННЫХ слоя — запись есть,
// все три ячейки уровня пусты, — а не из shadowed-ответа серверного reset. Ответ reset
// живёт один рендер и пропадает при переоткрытии модалки; данные слоя переживают его.
// Именно такую «пустую» запись оставляет серверный reset у специальности с расходящимися
// правами (шаг 2 плана) — она обязана попасть и в счётчик, и в фильтр «С настройками»,
// а не потеряться как «неотличима от отсутствия записи».
describe('shadowed-запись (есть запись, все три ячейки уровня пусты) — отличима от «записи нет»', () => {
  it('effectiveSpecialtyRecord возвращает объект записи (не null) для owner-слоя без единой заполненной ячейки', () => {
    const owner = emptyLayer();
    // Права сохранены (как оставляет их серверный reset), но ни один уровень не задан —
    // ровно то состояние, из которого ExceptionsBlock считает маркер «запись без уровней»
    owner.specialties.coding = { access: 'full', tools: null, disallowedTools: null };
    const global = emptyLayer();

    const rec = effectiveSpecialtyRecord(global, owner, 'coding');

    // Запись «есть» в слое — не унаследована к null, значит попадает и в счётчик исключений,
    // и (при её включении в предикат configured) в фильтр «С настройками»
    expect(rec).not.toBeNull();
    // …но ни одна ячейка уровня не заполнена — это и есть «запись без уровней»
    expect(rec?.tierStrong).toBeFalsy();
    expect(rec?.tierMedium).toBeFalsy();
    expect(rec?.tierWeak).toBeFalsy();
  });

  it('специальность без записи в обоих слоях отличима от shadowed-записи — эффективная запись null', () => {
    const owner = emptyLayer();
    owner.specialties.coding = { access: 'full', tools: null, disallowedTools: null };
    const global = emptyLayer();

    // Соседняя специальность без записи вовсе — не должна ложно получить маркер
    expect(effectiveSpecialtyRecord(global, owner, 'reviewer')).toBeNull();
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

// Контракт effectiveDefaultTier — зеркало бэкенд SpecialtySettingsStore.SpecialtyDefaultTier:
// 1) запись специальности owner, 2) запись специальности global,
// 3) defaultSpecialty owner, 4) defaultSpecialty global. Источник идёт в подсказку «уровень
// по умолчанию: Сильная · общая/своя», поэтому ошибка порядка → путаница в UI.
describe('effectiveDefaultTier — порядок резолва и источник', () => {
  it('владелец перекрывает общую запись (порядок 1 > 2)', () => {
    const owner = emptyLayer();
    owner.specialties.coding = { ...defaults(), defaultTier: 'strong' };
    const global = emptyLayer();
    global.specialties.coding = { ...defaults(), defaultTier: 'weak' };

    expect(effectiveDefaultTier(global, owner, 'coding')).toEqual({ tier: 'strong', source: 'owner' });
  });

  it('при отсутствии у владельца берётся общая запись (порядок 2)', () => {
    const owner = emptyLayer();
    const global = emptyLayer();
    global.specialties.coding = { ...defaults(), defaultTier: 'medium' };

    expect(effectiveDefaultTier(global, owner, 'coding')).toEqual({ tier: 'medium', source: 'global' });
  });

  it('если в обеих записях специальности пусто, уровень берётся из defaultSpecialty владельца (порядок 3)', () => {
    const owner = emptyLayer();
    owner.defaultSpecialty = { ...defaults(), defaultTier: 'weak' };
    const global = emptyLayer();
    global.defaultSpecialty = { ...defaults(), defaultTier: 'strong' };

    expect(effectiveDefaultTier(global, owner, 'coding')).toEqual({ tier: 'weak', source: 'owner' });
  });

  it('если ничего выше не задано, уровень берётся из defaultSpecialty global (порядок 4)', () => {
    const owner = emptyLayer();
    const global = emptyLayer();
    global.defaultSpecialty = { ...defaults(), defaultTier: 'medium' };

    expect(effectiveDefaultTier(global, owner, 'coding')).toEqual({ tier: 'medium', source: 'global' });
  });

  it('возвращает null, если нигде ничего не задано', () => {
    expect(effectiveDefaultTier(emptyLayer(), emptyLayer(), 'coding')).toBeNull();
  });

  it('для ключа "any" резолвит defaultSpecialty в обоих слоях', () => {
    const owner = emptyLayer();
    const global = emptyLayer();
    global.defaultSpecialty = { ...defaults(), defaultTier: 'strong' };

    expect(effectiveDefaultTier(global, owner, 'any')).toEqual({ tier: 'strong', source: 'global' });
  });

  it('withDefaultTier иммутабельно записывает уровень по умолчанию в запись слоя', () => {
    const base = emptyLayer();
    const next = withDefaultTier(base, 'coding', 'medium', null);

    expect(base.specialties.coding).toBeUndefined();
    expect(next.specialties.coding?.defaultTier).toBe('medium');
  });

  it('withDefaultTier("") очищает уровень', () => {
    const base = emptyLayer();
    base.specialties.coding = { ...defaults(), defaultTier: 'weak' };
    const next = withDefaultTier(base, 'coding', '', null);

    expect(next.specialties.coding?.defaultTier).toBeNull();
  });
});

// Дефолты для записи уровня — те же, что в recordOf() для НЕ-any ключа.
function defaults() {
  return { access: 'full' as const, tools: null, disallowedTools: null };
}
