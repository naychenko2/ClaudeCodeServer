namespace ClaudeHomeServer.Services.Llm;

// Точечные операции над именованными пресетами-цепочками (ADR-007 §1) поверх
// SpecialtySettingsStore: переименование и удаление одного пресета без замены слоя
// целиком — прямого API точечного изменения у стора нет (слой-структуру правит другой
// исполнитель волны), поэтому read-modify-write от снимка: клон слоя → мутация →
// SetGlobal/SetOwner/SetUser (все записи идут под write-локом стора и нормализуют
// слой, объекты входа не мутируются). Права слоёв (глобальный и назначенный
// пользователю — только админ) проверяет вызывающий контроллер: стор о ролях не знает.
public static class PresetStore
{
    // Найденный пресет и слой, где он живёт: Global — общий пресет инстанса, User —
    // назначенный пользователю (B9; ключ — тот же ownerId, что у Find, — EffectivePresets
    // WithScope читает Users[ownerId] вызывающего), Owner — личный пресет владельца.
    public sealed record LocatedPreset(ModelRoutePreset Preset, PresetScope Scope);

    // Поиск среди эффективных пресетов владельца (личные раньше глобальных — тот же
    // порядок, что у разворачивания preset:{id} в SpecialtySettingsStore.FindPreset):
    // правится тот пресет, который реально резолвится по ссылке. null — не найден.
    public static LocatedPreset? Find(SpecialtySettingsStore store, string ownerId, string presetId)
    {
        foreach (var (preset, scope) in store.EffectivePresetsWithScope(ownerId))
            if (string.Equals(preset.Id, presetId, StringComparison.OrdinalIgnoreCase))
                return new LocatedPreset(preset, scope);
        return null;
    }

    // Переименовать. null — успех; иначе текст ошибки (контроллер отдаёт его как 400).
    // Новый объект пресета вместо мутации найденного: найденный живёт в снимке стора,
    // который виден читателям до записи — полумутацию они наблюдать не должны.
    public static string? Rename(SpecialtySettingsStore store, string ownerId, string presetId, string name)
    {
        var located = Find(store, ownerId, presetId);
        if (located is null) return "Пресет не найден";
        return MutateLayer(store, located.Scope, ownerId, layer =>
        {
            layer.Presets = layer.Presets.Select(p =>
                string.Equals(p.Id, presetId, StringComparison.OrdinalIgnoreCase)
                    ? new ModelRoutePreset { Id = p.Id, Name = name, Description = p.Description, Steps = [.. p.Steps] }
                    : p)
                .ToList();
        });
    }

    // Удалить. Возвращает удалённый пресет (со слоем), null — не найден. Ссылки
    // preset:{id} в сторах НЕ чистятся: по ADR-007 §3 битая ссылка — fail-open вниз
    // (трактуется как пустая ячейка/маршрут), места использования показывает
    // контроллер до удаления. Слой без одного пресета валиден всегда — ошибки записи
    // быть не может (персист-провал логируется самим стором, в памяти уже применено).
    public static LocatedPreset? Delete(SpecialtySettingsStore store, string ownerId, string presetId)
    {
        var located = Find(store, ownerId, presetId);
        if (located is null) return null;
        MutateLayer(store, located.Scope, ownerId, layer =>
        {
            layer.Presets = layer.Presets
                .Where(p => !string.Equals(p.Id, presetId, StringComparison.OrdinalIgnoreCase))
                .ToList();
        });
        return located;
    }

    private static string? MutateLayer(SpecialtySettingsStore store, PresetScope scope, string ownerId,
        Action<SpecialtySettingsLayer> mutate)
    {
        var file = store.Snapshot;
        // Слой по скоупу найденного пресета: User — назначенный пользователю (B9),
        // ключ — ownerId вызывающего (Find нашёл пресет именно в его Users-слое),
        // Owner — личный. Писать не в тот слой — тихий no-op: пресет «не удалится».
        var layer = scope switch
        {
            PresetScope.Global => file.Global,
            PresetScope.User => file.Users.GetValueOrDefault(ownerId),
            _ => file.Owners.GetValueOrDefault(ownerId),
        };
        if (layer is null) return "Слой пресета не найден";
        var copy = CloneLayer(layer);
        mutate(copy);
        // Пустой слой после удаления последнего пресета снимается самим стором
        // (SetOwner/SetUser удаляют запись — владелец возвращается к нижнему слою).
        return scope switch
        {
            PresetScope.Global => store.SetGlobal(copy),
            PresetScope.User => store.SetUser(ownerId, copy),
            _ => store.SetOwner(ownerId, copy),
        };
    }

    // Копия слоя: словарь и список пересоздаются, записи — те же объекты (SetGlobal/
    // SetOwner нормализуют слой, создавая новые, — вход не мутируется).Comparer словаря
    // берём как у стора (OrdinalIgnoreCase — ключи специальностей канонические).
    private static SpecialtySettingsLayer CloneLayer(SpecialtySettingsLayer layer) => new()
    {
        Specialties = new(layer.Specialties, StringComparer.OrdinalIgnoreCase),
        DefaultSpecialty = layer.DefaultSpecialty,
        Presets = layer.Presets.Select(p => new ModelRoutePreset
        {
            Id = p.Id,
            Name = p.Name,
            Description = p.Description,
            Steps = [.. p.Steps],
        }).ToList(),
    };
}
