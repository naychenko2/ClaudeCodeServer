namespace ClaudeHomeServer.Services.Llm;

// Откуда взялось значение ячейки уровня при разворачивании (для показа «Сейчас пойдёт»):
// узкая матрица (персоны/специальности) — по индексу в списке, либо личный/глобальный слот.
public enum TierCellOrigin { None, NarrowMatrix, OwnerSlot, InstanceSlot }

// Результат разворота уровня с указанием источника (ModelForWithSource). Value — id модели
// ИЛИ маркер "preset:{id}" (пресет разворачивает вызывающий через SpecialtySettingsStore).
// MatrixIndex — индекс выигравшей матрицы в переданном списке (для NarrowMatrix); -1 для слотов.
public sealed record TierCellResult(string? Value, TierCellOrigin Origin, int MatrixIndex)
{
    public static readonly TierCellResult Empty = new(null, TierCellOrigin.None, -1);
}

// Матрица моделей по уровням (ADR-007 §2): три ячейки сильная/средняя/слабая. Значение ячейки —
// id модели ИЛИ маркер "preset:{id}". Пустая ячейка = «ответа нет, спроси следующую (широкую)
// матрицу». immutable-значение: вызывающие (ModelAssignmentResolver, TaskExecutionService,
// SessionManager) собирают список узких матриц из данных, которые у них под рукой (персона,
// специальность), и передают сюда — сервисные зависимости в резолвер не затягиваются.
public sealed record TierMatrix(string? Strong, string? Medium, string? Weak)
{
    public string? For(ModelTier tier) => tier switch
    {
        ModelTier.Strong => Strong,
        ModelTier.Weak => Weak,
        _ => Medium,
    };

    // Все три ячейки пусты — матрица ничего не говорит ни про один уровень.
    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(Strong)
        && string.IsNullOrWhiteSpace(Medium)
        && string.IsNullOrWhiteSpace(Weak);
}

// Резолвер per-user слотов тиров моделей. Приоритет: личный слот пользователя → глобальный
// слот инстанса (AppSettings). Неизвестный или анонимный ownerId не падает — уходит на общие слоты.
//
// Единственная точка, где уровень (tier) превращается в модель (ADR-007 §2, «инвариант
// единственной точки разворачивания»). Перегрузка с узкими матрицами (персона → специальность)
// обходит их по порядку и только потом падает на слоты; без матриц — сразу слоты (фоновые
// действия, CheapTextRunner — у них нет персоны/специальности).
public sealed class UserModelTierResolver(UserStore users, AppSettingsService appSettings)
{
    public string? ModelFor(ModelTier tier, string? ownerId)
    {
        if (!string.IsNullOrWhiteSpace(ownerId))
        {
            var user = users.GetById(ownerId);
            if (user is not null)
            {
                var slot = tier switch
                {
                    ModelTier.Strong => user.ModelTierStrong,
                    ModelTier.Weak => user.ModelTierWeak,
                    _ => user.ModelTierMedium,
                };
                if (!string.IsNullOrWhiteSpace(slot)) return slot.Trim();
            }
        }

        return appSettings.TierModel(tier);
    }

    // Разворот уровня по узким матрицам, затем по слотам владельца (ADR-007 §2). Возвращает
    // значение выигравшей ячейки (id модели ИЛИ маркер "preset:{id}") — вызывающий разворачивает
    // пресет сам через SpecialtySettingsStore.ExpandChain (стор сюда не затягиваем). Матрицы
    // обходим по порядку; первая непустая ячейка уровня побеждает. Все ячейки пусты — существующий
    // путь «личный слот → глобальный слот» (всегда конкретная модель, не маркер).
    // Эквивалентна вызову с пустым списком матриц — для мест без персоны (фоновые действия).
    public string? ModelFor(ModelTier tier, string? ownerId, IReadOnlyList<TierMatrix> narrowMatrices)
        => ModelForWithSource(tier, ownerId, narrowMatrices).Value;

    // Тот же разворот, что ModelFor(…, matrices), но с указанием источника выигравшей ячейки —
    // для показа «Сейчас пойдёт» (API /models/preview). Единственная точка разворачивания
    // сохраняется: ModelFor(…, matrices) делегирует сюда и берёт Value.
    public TierCellResult ModelForWithSource(ModelTier tier, string? ownerId, IReadOnlyList<TierMatrix> narrowMatrices)
    {
        if (narrowMatrices is { Count: > 0 })
        {
            for (var i = 0; i < narrowMatrices.Count; i++)
            {
                var cell = narrowMatrices[i].For(tier);
                if (!string.IsNullOrWhiteSpace(cell)) return new TierCellResult(cell.Trim(), TierCellOrigin.NarrowMatrix, i);
            }
        }
        if (!string.IsNullOrWhiteSpace(ownerId))
        {
            var user = users.GetById(ownerId);
            if (user is not null)
            {
                var slot = tier switch
                {
                    ModelTier.Strong => user.ModelTierStrong,
                    ModelTier.Weak => user.ModelTierWeak,
                    _ => user.ModelTierMedium,
                };
                if (!string.IsNullOrWhiteSpace(slot)) return new TierCellResult(slot.Trim(), TierCellOrigin.OwnerSlot, -1);
            }
        }
        var global = appSettings.TierModel(tier);
        return global is null ? TierCellResult.Empty : new TierCellResult(global, TierCellOrigin.InstanceSlot, -1);
    }
}
