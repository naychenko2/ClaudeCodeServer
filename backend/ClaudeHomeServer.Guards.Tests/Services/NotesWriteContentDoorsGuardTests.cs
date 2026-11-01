using System.Reflection;
using ClaudeHomeServer.Notes.Controllers;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// S2c-7: снимок дверей WriteContent для заметок. Двери NotesService проверяются в сервисе
/// (ResolveRoot с явным правом), а не атрибутом на действии: источник заметки приходит в id
/// и в теле запроса, отдельного projectId в маршруте нет — атрибут ProjectScope здесь не
/// применим. Снимок потому держит соответствие «действие контроллера → право» списком в
/// тесте, а сторож сверяет список с кодом: каждое HTTP-действие записи NotesController
/// (POST/PUT/DELETE) обязано быть названо ровно в одной из трёх групп.
///
/// Новая дверь вне снимка — красная: право записи не открывается само, его открывают
/// поимённо (как в WriteContentDoorsGuardTests для атрибутных дверей). Мёртвая строка —
/// красная: действие снято или переименовано — строку убирают с вердиктом в дифе.
///
/// Сборка Notes форс-загружена в статическом конструкторе + нижняя граница числа действий:
/// изолированный прогон не должен пройти вакуумно по незагруженной сборке (образец —
/// SubsystemBoundaryTests / WriteContentDoorsGuardTests).
/// </summary>
public class NotesWriteContentDoorsGuardTests
{
    static NotesWriteContentDoorsGuardTests()
    {
        _ = typeof(NotesController).Assembly;
    }

    private static readonly Type[] WriteVerbAttributes =
    [
        typeof(HttpPostAttribute), typeof(HttpPutAttribute),
        typeof(HttpDeleteAttribute), typeof(HttpPatchAttribute),
    ];

    /// <summary>Двери, переведённые на WriteContent (S2c-7): редактор общего проекта пишет.
    /// Toc/Translate идут через notes.Update — та же дверь содержимого. Перенос Move между
    /// источниками — не эта дверь: его owner-проверка живёт в ветке newSource != sourceKey
    /// (NotesService.cs, EnsureSourceWrite) и остаётся на Write.</summary>
    private static readonly HashSet<string> WriteContentDoors =
    [
        "Create", "Update", "Delete", "Move",
        "CreateFolder", "DeleteFolder", "MoveFolder",
        "LinkMention", "Annotate", "Repin", "Reply", "SetStatus",
        "Toc", "Translate",
    ];

    /// <summary>Остаются на owner-Write (решение постановки S2c-5, defense —
    /// NotesSideWritePathsTests): мост «чекбокс → задача» и, частично, перенос между
    /// источниками в Move (выше в комментарии к снимку).</summary>
    private static readonly HashSet<string> RemainsOnWrite =
    [
        "PromoteTask", "ToggleTask", "SetTaskDue",
    ];

    /// <summary>Действия записи без проектной двери заметок: только личный vault (Daily,
    /// DailySummary), только индекс (Reindex) или ничего не пишут (AI-предложения —
    /// проверено по NotesAiService: suggest-links/tags/title читают, не пишут).</summary>
    private static readonly HashSet<string> NoProjectNoteDoor =
    [
        "Daily", "DailySummary", "Reindex",
        "SuggestLinks", "SuggestTags", "SuggestTitle",
    ];

    [Fact]
    public void КаждоеДействиеЗаписиЗаметок_НазваноВСнимке()
    {
        var actions = ScanWriteActions();

        // Вакуумно-зелёный сторож хуже отсутствующего: 23 действия записи в контроллере
        // обязаны быть видны — сборка форс-загружена
        actions.Count.Should().BeGreaterThanOrEqualTo(20,
            "сторож обязан видеть действия NotesController — сборка вертикали не загружена");

        var outside = actions.Where(a =>
            !WriteContentDoors.Contains(a) && !RemainsOnWrite.Contains(a) && !NoProjectNoteDoor.Contains(a))
            .ToList();
        outside.Should().BeEmpty(
            "право записи контента открывается поимённо; новое действие записи NotesController — " +
            "только с явной строкой снимка и вердиктом в дифе:\n" +
            string.Join("\n", outside.Select(a => "  • NotesController." + a)));
    }

    [Fact]
    public void Снимок_НеСодержитДействийКоторыхНетВКоде()
    {
        var actions = ScanWriteActions();
        var stale = WriteContentDoors.Concat(RemainsOnWrite).Concat(NoProjectNoteDoor)
            .Where(a => !actions.Contains(a)).ToList();
        stale.Should().BeEmpty(
            "действие снято с контроллера или переименовано — уберите строку снимка с вердиктом " +
            "в дифе:\n" + string.Join("\n", stale.Select(a => "  • NotesController." + a)));
    }

    [Fact]
    public void ГруппыСнимка_НеПересекаются()
    {
        var dup = WriteContentDoors.Intersect(RemainsOnWrite)
            .Concat(WriteContentDoors.Intersect(NoProjectNoteDoor))
            .Concat(RemainsOnWrite.Intersect(NoProjectNoteDoor))
            .ToList();
        dup.Should().BeEmpty("одно действие не может иметь два права одновременно");
    }

    // Имена всех HTTP-действий записи (POST/PUT/DELETE/PATCH) NotesController
    private static HashSet<string> ScanWriteActions()
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var method in typeof(NotesController)
                     .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            if (!method.GetCustomAttributes().Any(a => WriteVerbAttributes.Contains(a.GetType()))) continue;
            result.Add(method.Name);
        }
        return result;
    }
}
