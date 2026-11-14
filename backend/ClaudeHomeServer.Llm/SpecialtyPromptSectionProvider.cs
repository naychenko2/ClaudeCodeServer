using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Llm;

// Адаптер Core-интерфейса `IPromptSectionProvider` на живой `SpecialtySettingsStore`
// (этап 5, шаг 4, разрыв цикла Turn → Llm). Регистрация — в `LlmSubsystem.Register`
// рядом с `SpecialtySettingsStore`, DI подтянет его по конструктору автоматически.
//
// Вся логика — форвард одного метода. Наследование секций (посекочное от слоя + дефолт
// кода) живёт в `SpecialtySettingsStore` и выполняется ДО отдачи наружу — наружу
// поступает уже развёрнутое состояние ПОЛНОСТЬЮ, со включёнными и выключенными: срез по
// Enabled делает потребитель (контрибьютор), ему нужно и состояние для наложения/выдачи.
public sealed class SpecialtyPromptSectionProvider(SpecialtySettingsStore inner) : IPromptSectionProvider
{
    public IReadOnlyList<EffectivePromptSection> EffectivePromptSectionStates(
        string ownerId, PersonaSpecialty specialty)
        => inner.EffectivePromptSectionStates(ownerId, specialty);
}
