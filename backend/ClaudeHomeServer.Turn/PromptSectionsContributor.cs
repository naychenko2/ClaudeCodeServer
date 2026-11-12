using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Llm;

namespace ClaudeHomeServer.Services.Turn;

// Секции промпта специальности персоны (план «Секции промптов» этап 3): сценарные инструкции «когда и как» по роли — история,
// граф кода, процессы, правила роли. Позиция — между recall-memory и persona-layer
// (контракт плана: «призыв и данные рядом»).
//
// IsEnabled повторяет прежний BuildPromptSectionsProvider: owner != null, есть
// IPromptSectionProvider (без DI — секция не собирается), есть персона с непустой
// специальностью, и сессия не групповая (контракт плана: секции только у персонных
// сессий — в групповом чате секции по роли одного из участников сбивают остальных).
//
// Этап 5, шаг 4: разрыв цикла Turn → Llm через Core-шов `IPromptSectionProvider`.
// Опциональность (`?`) сохранена — без DI секция не собирается, и это осознанное
// поведение, а не недосмотр (см. комментарий в `Core/Services/Llm/IPromptSectionProvider.cs`).
//
// P1c (разрез 2, Г6): EffectiveSectionsFor — единственное место, где зовётся
// IPromptSectionProvider; поверх базовых секций специальности налагается слой персоны
// (persona.PromptSections) через PromptSectionOverlay.Apply. Своего кэша нет: персона
// читается из PromptSessionContext на каждый ход (устаревшее между ходами — следующий
// BuildAsync видит новые секции).
public sealed class PromptSectionsContributor : IPromptSectionContributor
{
    private readonly IPromptSectionProvider? _sections;
    public PromptSectionsContributor(IPromptSectionProvider? sections)
    {
        _sections = sections;
    }

    public string Key => "prompt-sections";
    public string Title => "Инструкции по специальности";
    // После recall-memory (300), до persona-bindings/code-graph (500/600) — порядок
    // проводки, фиксированный golden-фикстурой 1.
    public int Order => 400;
    public string Group => "persona";

    public bool IsEnabled(PromptSessionContext sessionContext) =>
        _sections is not null
        && sessionContext.OwnerId is not null
        && sessionContext.Persona is not null
        && sessionContext.Persona.Specialty != PersonaSpecialty.None
        && (sessionContext.Session.Participants is null || sessionContext.Session.Participants.Count <= 1);

    // Единственная точка чтения эффективных секций для персоны (Г6): базовые секции
    // специальности через IPromptSectionProvider + наложение слоя персоны через
    // PromptSectionOverlay.Apply. Ручка выдачи P1d зовёт этот метод.
    public IReadOnlyList<EffectivePromptSection> EffectiveSectionsFor(string ownerId, Persona persona)
    {
        var baseStates = _sections!.EffectivePromptSections(ownerId, persona.Specialty);
        return PromptSectionOverlay.Apply(baseStates, persona.PromptSections, SectionSource.Persona);
    }

    public Task<PromptSectionContribution?> BuildAsync(
        PromptSessionContext sessionContext, string? turnText)
    {
        if (sessionContext.OwnerId is null || sessionContext.Persona is null || _sections is null)
            return Task.FromResult<PromptSectionContribution?>(null);

        var sections = EffectiveSectionsFor(sessionContext.OwnerId, sessionContext.Persona);
        var enabled = sections.Where(s => s.Enabled).ToList();
        var text = enabled.Count == 0 ? null : string.Join("\n\n", enabled.Select(s => s.Text));
        if (string.IsNullOrWhiteSpace(text))
            return Task.FromResult<PromptSectionContribution?>(null);

        return Task.FromResult<PromptSectionContribution?>(
            new PromptSectionContribution([new PromptSection(Key, text)]));
    }
}