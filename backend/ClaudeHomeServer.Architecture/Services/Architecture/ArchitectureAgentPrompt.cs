using System.Text;
using ClaudeHomeServer.Services.Composition;

namespace ClaudeHomeServer.Services.Architecture;

/// <summary>
/// Постановка агенту прохода 2 «Собрать архитектуру» (галочка «С агентом»). Текст собирает
/// вертикаль — она знает итог прохода 1; шов <see cref="IArchitectureAgentLauncher"/>
/// получает готовые Title/Description. Скелет — раздел 2.4 плана
/// (docs/research/architecture-build-button-plan.md).
/// </summary>
public static class ArchitectureAgentPrompt
{
    public const string Title = "Собрать архитектуру: проверить кандидатов и дополнить модель";

    /// <summary>Потолок строк списков в постановке — хвост сворачивается в «и ещё N».</summary>
    public const int MaxListed = 40;

    public static ArchitectureAgentBrief Build(ArchitectureGenerateResult pass1, IReadOnlyList<string> manual)
    {
        var tag = ArchitectureModelBuilder.CandidateTag;
        var missingTag = ArchitectureModelMerger.MissingTag;
        var sb = new StringBuilder();
        sb.AppendLine("Детерминированная сборка из кода (проход 1) уже положила модель в "
            + $"`{pass1.ModelPath}`. Твоя задача — выверить и дополнить её инструментами `arch_*` "
            + "(модель читай и правь только через них, файл руками не редактируй).");
        sb.AppendLine();
        sb.AppendLine($"Итог прохода 1: контейнеров {pass1.Containers}, компонентов {pass1.Components}, "
            + $"кандидатов L1 {pass1.Candidates}, с тегом «{missingTag}» {pass1.MarkedMissing}.");
        sb.AppendLine();
        sb.AppendLine("## Что сделать");
        sb.AppendLine();
        sb.AppendLine("1. Открой модель через `arch_context`.");
        sb.AppendLine($"2. Кандидаты L1 помечены тегом `{tag}`. Каждого **проверь по коду** (файл-источник "
            + "указан в описании элемента): подтверждён → сними тег, заполни `technology` и описание, "
            + "подпиши связи смыслом («RAG-поиск», «озвучка ответа»); шум → удали элемент.");
        sb.AppendLine("3. Заведи недостающее для L1: людей (типа «человек» в Viaduct нет — внешняя система "
            + "с тегом `пользователь`) и внешние системы, которых проход 1 не видит.");
        sb.AppendLine("4. Подпиши связи без подписи и пройди контейнеры: заполни пустые `description`/`technology`.");
        sb.AppendLine($"5. Элементы с тегом `{missingTag}` проверь: устарел → предложи удаление в сводке "
            + "(сам НЕ удаляй), переименован → скажи об этом.");
        sb.AppendLine();
        sb.AppendLine("## Нельзя");
        sb.AppendLine();
        sb.AppendLine("- Трогать элементы, заведённые человеком (ни правок, ни удаления, ни тегов). "
            + (manual.Count == 0 ? "На момент постановки таких нет." : "На момент постановки это:"));
        foreach (var name in manual.Take(MaxListed)) sb.AppendLine($"  - {Quote(name)}");
        if (manual.Count > MaxListed) sb.AppendLine($"  - и ещё {manual.Count - MaxListed}");
        sb.AppendLine("- Выдумывать: всё, что добавляешь или подтверждаешь, должно иметь источник в коде.");
        sb.AppendLine();
        sb.AppendLine("## Итог");
        sb.AppendLine();
        sb.AppendLine("Финальная сводка — в итог задачи: что подтвердил, удалил, добавил (с источником "
            + "`файл:строка`), что отбросил и почему, какие элементы «нет в коде» предлагаешь удалить.");

        return new ArchitectureAgentBrief(Title, sb.ToString(), ["viaduct", IArchitectureAgentLauncher.BuildLabel]);
    }

    /// <summary>Потолок длины имени ручного элемента в постановке.</summary>
    public const int MaxNameLength = 120;

    // Имя пишет человек в редакторе — в постановку оно едет данными, а не разметкой:
    // переводы строк → пробел (иначе имя порвёт список и начнёт свой абзац), потолок
    // длины, обратные кавычки (свои кавычки внутри имени — в апостроф, чтобы не закрыть код).
    internal static string Quote(string name)
    {
        var flat = name.Replace('\r', ' ').Replace('\n', ' ').Replace('`', '\'');
        if (flat.Length > MaxNameLength) flat = flat[..MaxNameLength] + "…";
        return $"`{flat}`";
    }
}
