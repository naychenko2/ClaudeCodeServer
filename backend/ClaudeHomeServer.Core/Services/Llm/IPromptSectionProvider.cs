using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Llm;

// Источник значения параметра секции промпта (для UI-бейджа и тестов). Значения User и Owner
// после снятия слоёв (этап 5, ADR-012, версия v5 SpecialtySettingsStore) недостижимы,
// но остаются в enum: он живёт в wire-контракте фронта и в чужих файлах — сужение
// отдельной задачей после мерджа (ADR-012, «Хвосты»).
// Persona — слой персоны (разрез 2, Г6; добавлен в конец: числа enum — wire-контракт фронта,
// вставка в середину сдвинула бы существующие значения).
public enum SectionSource { Code, Global, User, Owner, Persona }

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

// Переопределение секции для функции наложения PromptSectionOverlay.Apply (разрез 2, Г6):
// параметр «задан» только когда имеет значение — null остаётся базовым; Text null или
// пустой — «не задан» (типовой текст базы). Id — из каталога; переопределение с id,
// которого нет в базе, Apply игнорирует.
public sealed record PromptSectionOverride(string Id, bool? Enabled, string? Text);

// Узкий шов под единственный метод `EffectivePromptSectionStates(ownerId, specialty)`
// SpecialtySettingsStore, который контрибьютор секций промпта дёргает вместо прямой ссылки
// на Llm-сервис (этап 5, шаг 4: разрыв цикла Turn → Llm).
//
// Контракт повторяет сигнатуру SpecialtySettingsStore.EffectivePromptSectionStates дословно
// (см. Llm/SpecialtySettingsStore.cs:261), чтобы адаптер был форвардом и резолв не
// разъехался. Больше методов в контракт НЕ кладём: цикл разделывается ровно на эту
// сигнатуру, расширять шов «на всякий случай» — снова множить зависимость.
//
// ШОВ ОТДАЁТ ПОЛНОЕ эффективное состояние — с флагом Enabled и источниками, а не только
// включённые секции (прежняя версия шва). Причина не в удобстве: стор применяет наложение
// слоя админа ко ВСЕМ состояниям каталога и лишь потом сечёт включённые
// (SpecialtySettingsStore.cs:261-286), поэтому слой персоны поверх «уже отсечённой» базы был
// бы слабее слоя админа — персона не могла бы включить то, что выключила специальность, а
// редактор (P2a/P2b) не видел бы «выключено специальностью» и его текст. Срез по Enabled —
// обязанность того, кто пишет текст хода (PromptSectionsContributor.BuildAsync), а не шва:
// одному читателю нужно состояние, другому — только включённые.
//
// Опциональность (`?`) на стороне потребителя сохранена: `PromptSectionsContributor`
// без DI не собирает секцию хода и отдаёт пустой список на чтении — это осознанное
// поведение, а не недосмотр (см. `Services/Turn/PromptSectionsContributor.cs`).
public interface IPromptSectionProvider
{
    IReadOnlyList<EffectivePromptSection> EffectivePromptSectionStates(
        string ownerId, PersonaSpecialty specialty);
}
