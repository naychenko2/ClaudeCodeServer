using System.Reflection;
using System.Runtime.CompilerServices;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.AudioEditor.Chats;
using ClaudeHomeServer.Services.CodeGraph;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.ImageEditor.Chats;
using ClaudeHomeServer.Services.Images.LocalMedia;
using ClaudeHomeServer.Services.Notes;
using ClaudeHomeServer.Services.Tasks;
using ClaudeHomeServer.Services.TestRuns;
using ClaudeHomeServer.Services.Turn;
using ClaudeHomeServer.Services.VideoEditor.Chats;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// KM-B6c, доработка по R-KM-B6c2 (п.2): recall детского хода — fail-closed. Каждый реализатор
// IPromptSectionContributor из всех сборок обязан иметь ЯВНОЕ детское решение: либо «безопасен для
// ребёнка» (allow-list ниже, с причиной), либо «закрывается при IsChild» (закрытые проверяются
// поведением). Новый контрибьютор без решения красит сторож: молча попасть в детский ход он не может.
//
// Секции специальности (PromptSectionsContributor) заносятся в «безопасные» явно: они несут
// шаблон специальности персоны, а не личные данные владельца.
public class PromptSectionContributorsChildGuardTests
{
    // Форс-загрузка сборок: без неё сторож проходит вакуумно (набор реализаторов пуст). typeof ниже
    // тянет сборки Turn, Notes, CodeGraph, Tasks, TestRuns, Images и три редактора.
    static PromptSectionContributorsChildGuardTests()
    {
        _ = typeof(IPromptSectionContributor).Assembly;
        _ = typeof(PersonaLayerContributor).Assembly;
        _ = typeof(NotesRecallContributor).Assembly;
        _ = typeof(CodeGraphContributor).Assembly;
        _ = typeof(LocalExecutorsContributor).Assembly;
        _ = typeof(BuildHintContributor).Assembly;
        _ = typeof(LocalMediaDefaultContributor).Assembly;
        _ = typeof(ImageEditorStateContributor).Assembly;
        _ = typeof(AudioEditorStateContributor).Assembly;
        _ = typeof(VideoEditorStateContributor).Assembly;
    }

    // Безопасные для ребёнка: тип → почему. Секция не несёт личных данных владельца и не открывает
    // ребёнку то, что политика волны 1 закрывает (долгая память, заметки, досье, граф кода).
    private static readonly Dictionary<Type, string> SafeForChild = new()
    {
        [typeof(PersonaLayerContributor)] = "слой персоны — носитель самого детского слоя (правила тьютора)",
        [typeof(PromptSectionsContributor)] = "секции специальности персоны (шаблон, не данные владельца)",
        [typeof(TurnContextContributor)] = "состояние строки контекста самого чата ребёнка",
        [typeof(DossierTrailerContributor)] = "только трейлер коммита из id сессии и задачи",
        [typeof(SphereCharterContributor)] = "устав сферы по владельцу-ребёнку: своих сфер у него нет",
        [typeof(FeatureVoiceReminderContributor)] = "напоминание по задачам самого владельца-ребёнка",
        [typeof(LocalExecutorsContributor)] = "счётчик загрузки исполнителей, без содержимого",
        [typeof(LocalMediaDefaultContributor)] = "правило выбора локальной модели, не данные",
        [typeof(TestRunsHintContributor)] = "статичная подсказка про инструмент, едет только при его наличии",
        [typeof(BuildHintContributor)] = "статичная подсказка про инструмент, едет только при его наличии",
        [typeof(ImageEditorStateContributor)] = "состояние картинок самого чата ребёнка",
        [typeof(AudioEditorStateContributor)] = "состояние звука самого чата ребёнка",
        [typeof(VideoEditorStateContributor)] = "состояние видео самого чата ребёнка",
    };

    // Закрываются при IsChild: тип → подготовка бессодержательного экземпляра, при которой взрослый
    // контекст даёт true (иначе проверка «ребёнку false» вакуумна — false выходил бы из null-поля).
    private static readonly Dictionary<Type, Action<object>> ClosedForChild = new()
    {
        [typeof(PersonaRecallContributor)] = _ => { },
        [typeof(NotesRecallContributor)] = _ => { },
        [typeof(PersonaBindingsContributor)] = _ => { },
        [typeof(CodeGraphContributor)] = o =>
        {
            Set(o, "_provider", RuntimeHelpers.GetUninitializedObject(typeof(CodeGraphPromptProvider)));
            Set(o, "_toolGate", new AllowAllToolGate());
        },
        [typeof(ConsumersTailContributor)] = o => Set(o, "_toolGate", new AllowAllToolGate()),
    };

    private sealed class AllowAllToolGate : IPersonaServerToolGate
    {
        public bool IsServerToolEnabled(string? ownerId, Persona? persona, string toolKey) => true;
    }

    private static void Set(object target, string field, object value) =>
        target.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(target, value);

    private static IReadOnlyList<Type> AllImplementers() =>
        AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && a.GetName().Name is { } n && !n.EndsWith(".Tests", StringComparison.Ordinal))
            .SelectMany(a =>
            {
                try { return a.GetTypes(); }
                catch (ReflectionTypeLoadException e) { return e.Types.OfType<Type>().ToArray(); }
            })
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IPromptSectionContributor).IsAssignableFrom(t))
            .OrderBy(t => t.FullName, StringComparer.Ordinal)
            .ToList();

    [Fact]
    public void КаждыйКонтрибьютор_ИмеетЯвноеДетскоеРешение()
    {
        var all = AllImplementers();
        all.Count.Should().BeGreaterThanOrEqualTo(SafeForChild.Count + ClosedForChild.Count,
            "сборки не догружены или набор реализаторов пуст — сторож проходил бы вакуумно");

        var undecided = all.Where(t => !SafeForChild.ContainsKey(t) && !ClosedForChild.ContainsKey(t))
            .Select(t => t.FullName).ToList();
        undecided.Should().BeEmpty(
            "у каждого IPromptSectionContributor должно быть явное детское решение: либо SafeForChild (с причиной), " +
            "либо ClosedForChild (IsEnabled=false при IsChild) — иначе recall молча попадёт в ход ребёнка");

        var both = SafeForChild.Keys.Intersect(ClosedForChild.Keys).Select(t => t.FullName).ToList();
        both.Should().BeEmpty("решение одно: тип не может быть и безопасным, и закрытым");

        var stale = SafeForChild.Keys.Concat(ClosedForChild.Keys).Except(all).Select(t => t.FullName).ToList();
        stale.Should().BeEmpty("в списках решений остался тип, который перестал быть контрибьютором");
    }

    [Fact]
    public void ЗакрытыеКонтрибьюторы_ВзросломуВключены_РебёнкуНет()
    {
        foreach (var (type, prepare) in ClosedForChild)
        {
            var contributor = (IPromptSectionContributor)RuntimeHelpers.GetUninitializedObject(type);
            prepare(contributor);

            contributor.IsEnabled(Ctx(isChild: false)).Should().BeTrue(
                $"{type.Name}: взрослому проба обязана быть включена, иначе проверка ребёнку вакуумна");
            contributor.IsEnabled(Ctx(isChild: true)).Should().BeFalse(
                $"{type.Name}: детскому ходу recall закрыт (ADR-041 §4)");
        }
    }

    // Контекст, при котором взрослому включены все закрытые контрибьюторы: MCP заметок и памяти,
    // персона, исполнение задачи на первом сообщении (ConsumersTail), корень проекта.
    private static PromptSessionContext Ctx(bool isChild) =>
        new(new Session { Id = "chat-1", OwnerId = "owner-1", PersonaId = "p-1", TaskExecution = true, MessageCount = 1 },
            "owner-1", new Persona { Id = "p-1", Name = "Тьютор" }, "/tmp/proj",
            HasNotesMcp: true, HasMemoryMcp: true, IsChild: isChild);
}
