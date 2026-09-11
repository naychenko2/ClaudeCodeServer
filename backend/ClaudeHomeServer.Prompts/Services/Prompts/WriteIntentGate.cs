using System.Text.RegularExpressions;

namespace ClaudeHomeServer.Services.Prompts;

// Гейт «поднимать ли write-схемы MCP на этот ход» по тексту хода. Вынесен из ClaudeSession
// (адаптер CLI не место для продуктовой политики) и покрыт тестами.
//
// Эвристика КОНСЕРВАТИВНА: ложный пропуск = один ход без write-инструментов (модель попросит
// переформулировать), а не поломка. Требуется совпадение «действие + объект» — голое действие
// или голый объект write-режим не поднимают. Кроме русских основ учитываем частые английские
// (create/make/rename/… + persona/project/…) — иначе «create a persona» не поднимал бы схемы.
public static class WriteIntentGate
{
    // --- Управление командой (personas-server, PERSONAS_WRITE=1) ---
    private static readonly Regex PersonaAction = new(
        @"созда|сотвор|завед|настро|измен|поменя|обнов|отредактир|редактир|удал|снес|переимен|привяж|отвяж|сгенери|автоматиз|назнач" +
        @"|creat|make|set\s?up|configur|updat|edit|delet|remov|renam|generat|assign|automat|bind|attach",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PersonaObject = new(
        @"персон|агент|команд|\bрол(ь|и|ью|ей)|правил|проактив|аватар|привязк" +
        @"|persona|agent|\bteam\b|\brole|avatar|automation|binding",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // --- Запись в рабочее пространство (workspace-server, WORKSPACE_WRITE=1) ---
    // Объект НАМЕРЕННО сужен (без голого «файл/папка»): правки файлов ТЕКУЩЕГО проекта идут
    // встроенными Read/Edit/Write, а wsp files_write — только для ДРУГИХ проектов.
    private static readonly Regex WorkspaceAction = new(
        @"созда|сотвор|завед|запиш|запис|сохран|измен|поменя|обнов|перемест|переимен|удал|добав|индексир|напиш|отправ|перешл" +
        @"|creat|make|writ|sav|updat|mov|renam|delet|remov|add|index|send",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex WorkspaceObject = new(
        @"проект|\bчат|сесси|базу знаний|индекс|директори|рабоч\w* простран" +
        @"|project|\bchat|session|knowledge\s?base|\bindex|director|workspace",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Интент управления командой: действие + объект (персона/агент/роль/правило/аватар/привязка).
    public static bool PersonaManagement(string? turnText) =>
        !string.IsNullOrWhiteSpace(turnText)
        && PersonaAction.IsMatch(turnText) && PersonaObject.IsMatch(turnText);

    // Интент записи в рабочее пространство: действие + объект (проект/чат/сессия/база знаний/…).
    public static bool WorkspaceWrite(string? turnText) =>
        !string.IsNullOrWhiteSpace(turnText)
        && WorkspaceAction.IsMatch(turnText) && WorkspaceObject.IsMatch(turnText);
}
