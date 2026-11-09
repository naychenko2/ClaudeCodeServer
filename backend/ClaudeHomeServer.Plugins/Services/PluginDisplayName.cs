using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Plugins;

/// <summary>
/// Человекочитаемое имя пакета для человека (D2, находка 4c903d8e): поля «отображаемое
/// имя» в манифеста нет, поэтому имя — <c>title</c> первой панели
/// (<c>contributes.panels[].title</c>), то же правило, что у фронта для установленных
/// плагинов (panelNaming.ts: title, иначе humanizeId). ЕДИНАЯ точка вывода имени на бэке:
/// её используют и ответ «Принять» (<c>PluginDraftsController.Accept</c>), и список
/// черновиков (<c>PluginDraftService.List</c>).
///
/// Панелей нет (пакет только навыки/команды) или title пуст → <c>null</c>: фронт остаётся
/// на прежнем fallback humanizeId. Имя никогда не отдаётся пустой строкой. Поле
/// <c>name</c> манифеста при этом не трогается — это slug, требование валидатора.
/// </summary>
public static class PluginDisplayName
{
    public static string? Of(PluginManifest? manifest)
    {
        var title = manifest?.Contributes?.Panels?.FirstOrDefault()?.Title;
        return string.IsNullOrWhiteSpace(title) ? null : title;
    }
}
