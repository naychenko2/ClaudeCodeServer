using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Llm;

// Источник значения параметра секции промпта (для UI-бейджа и тестов). Значения User и Owner
// после снятия слоёв (этап 5, ADR-012, версия v5 SpecialtySettingsStore) недостижимы,
// но остаются в enum: он живёт в wire-контракте фронта и в чужих файлах — сужение
// отдельной задачей после мерджа (ADR-012, «Хвосты»).
public enum SectionSource { Code, Global, User, Owner }

// Эффективное состояние секции промпта: enabled и text наследуются КАЖДЫЙ СВОИМ
// параметром (см. EffectivePromptSectionStates).
//
// Этап 5, шаг 4: record-тип переехал в Core для шва `IPromptSectionProvider` (записи
// остаются в Core как DTO промпта; класс `SpecialtySettingsStore` остаётся в Llm
// и реализует шов адаптером `SpecialtyPromptSectionProvider`). Поведение наследования
// (см. комментарий в `Services/Llm/SpecialtySettingsStore.cs`) хранится в реализации
// стора — наружу отдаётся уже развёрнутое состояние.
public sealed record EffectivePromptSection(
    string Id, bool Enabled, string Text,
    SectionSource EnabledSource, SectionSource TextSource);

// Узкий шов под единственный метод `EffectivePromptSections(ownerId, specialty)`
// SpecialtySettingsStore, который контрибьютор секций промпта хода дёргает сейчас через
// прямую ссылку на Llm-сервис.
//
// Контракт повторяет сигнатуру SpecialtySettingsStore.EffectivePromptSections дословно
// (см. Llm/SpecialtySettingsStore.cs:298), чтобы адаптер был форвардом и резолв
// не разъехался. Больше методов в контракт НЕ кладём: цикл разделывается ровно на
// эту сигнатуру, расширять шов «на всякий случай» — снова множить зависимость.
//
// Опциональность (`?`) на стороне потребителя сохранена: `PromptSectionsContributor`
// комментирует, что без DI секция не собирается, — это осознанное поведение, а не
// недосмотр (см. `Services/Turn/PromptSectionContributors/PromptSectionsContributor.cs`).
public interface IPromptSectionProvider
{
    IReadOnlyList<EffectivePromptSection> EffectivePromptSections(
        string ownerId, PersonaSpecialty specialty);
}
