using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Composition;

namespace ClaudeHomeServer.Services.Notes.Wiki;

// «Перенести в вики» (ADR-038, §7 и §8; шаг B5): заметка становится страницей вики с
// утверждениями human, а сама заменяется ссылкой на страницу. Копий нет: после переноса текст
// живёт в одном месте.
//
// Порядок и его причины:
//  1. Дом страницы — дом заметки (личный vault → «Личное», notes/ проекта → раздел проекта):
//     человек не выбирает раздел, видимость (ADR-036) остаётся как у заметки.
//  2. Страница создаётся пакетом human через WikiApplyService.EditPageAsync — тем же конвейером,
//     что правка в редакторе: блокировка раздела, гейт, прообразы, журнал, карточка правки со
//     снимком написанного. Свой путь записи в вики здесь не заводится.
//  3. ТОЛЬКО после успеха заметка заменяется ссылкой — и только через NotesService.Update
//     (переименования, события и граф там же): мимо него файл заметки не пишется. Сбой
//     страницы оставляет заметку целой.
//  4. Один перенос заметки за раз (замок по паре владелец+заметка), а страница создаётся с
//     baseHash=null: имя занято — страница не перезаписывается, перенос отказывает. Повторный
//     перенос уже перенесённой заметки (в ней ссылка-заглушка) отказывает, страницы не множатся.
//
// Утверждения — строки заметки без разметки (заголовки, маркеры списка, цитаты снимаются; ссылки
// [[…]] разворачиваются в текст; блоки кода пропускаются). Форматирование и frontmatter заметки при
// замене её ссылкой не сохраняются: сохранённым остаётся то, что превратилось в утверждения.
public sealed class WikiNoteTransfer
{
    /// <summary>Начало заглушки, которой заменена перенесённая заметка: по ней же повторный
    /// перенос узнаёт, что переносить уже нечего.</summary>
    internal const string StubPrefix = "Перенесено в вики: [[";

    public enum TransferOutcome { Transferred, NotFound, NotTransferable, AlreadyTransferred, Exists, Invalid, Refused }

    public readonly record struct TransferResult(
        TransferOutcome Outcome, string? Reason, string? Section = null, string? Page = null,
        string? PackageId = null, IReadOnlyList<string>? Violations = null);

    private static readonly Regex LinkRx = new(@"\[\[([^\]\r\n|#]+)(?:#[^\]\r\n|]*)?(?:\|([^\]\r\n]*))?\]\]", RegexOptions.CultureInvariant);
    private static readonly Regex BulletRx = new(@"^(?:#{1,6}\s+|>\s*|[-*+]\s+(?:\[[ xX]\]\s+)?|\d+[.)]\s+)+", RegexOptions.CultureInvariant);
    private static readonly Regex FootnoteRx = new(@"\[\^[^\]\r\n]+\]", RegexOptions.CultureInvariant);

    private readonly NotesService _notes;
    private readonly WikiApplyService _apply;
    private readonly IProjectManager _projects;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);

    public WikiNoteTransfer(NotesService notes, WikiApplyService apply, IProjectManager projects)
    {
        _notes = notes;
        _apply = apply;
        _projects = projects;
    }

    /// <summary>Тестовые сигналы гонки «двойной перенос»: NoteLockRequestedHook — вызов подошёл к
    /// замку заметки (до ожидания); NoteReadHook — вызов прочёл заметку и ещё ничего не записал.
    /// Тест обязан знать, где стоит каждый вызов, а не мерить это паузой. В проде оба null.</summary>
    internal Action<string>? NoteLockRequestedHook;
    internal Action<string>? NoteReadHook;
    /// <summary>Тестовый шов замены заметки (NotesService запечатан): подмена даёт сбой или null
    /// ровно на нужном вызове. В проде null — работает NotesService.Update.</summary>
    internal Func<string, string, UpdateNoteRequest, NoteDetail?>? ReplaceNoteHook;

    public async Task<TransferResult> TransferAsync(string ownerId, string noteId)
    {
        var gate = _locks.GetOrAdd(ownerId + "|" + noteId, _ => new SemaphoreSlim(1));
        NoteLockRequestedHook?.Invoke(noteId);
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            return await TransferLockedAsync(ownerId, noteId).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<TransferResult> TransferLockedAsync(string ownerId, string noteId)
    {
        var note = _notes.GetDetail(ownerId, noteId);
        NoteReadHook?.Invoke(noteId);
        if (note is null)
            return new(TransferOutcome.NotFound, "заметка не найдена");
        if (note.Source == NotesService.WikiKey)
            return new(TransferOutcome.NotTransferable, "страница вики уже в вики");
        if (note.ExpiresAt is not null)
            return new(TransferOutcome.NotTransferable, "заметка с 'expires' временная: переносить её в вики нельзя");
        if (note.Annotation is not null)
            return new(TransferOutcome.NotTransferable, "комментарий к документу — не заметка для переноса");

        var section = note.Source == WikiSections.Personal
            ? WikiSections.Personal
            : WikiSections.ProjectsPrefix + note.Source;
        if (WikiSections.Folder(section) is not { } folder)
            return new(TransferOutcome.Refused, "дом заметки не раздел вики");
        if (WikiSections.TryProjectId(section, out var projectId))
        {
            var project = _projects.GetById(projectId);
            if (project is null || project.OwnerId != ownerId)
                return new(TransferOutcome.NotFound, "проект заметки не найден");
            var capability = ProjectCapabilities.WikiSectionFor(project);
            if (!capability.Available)
                return new(TransferOutcome.Refused, capability.Reason);
        }

        var body = BodyOf(note.Content);
        if (body.TrimStart().StartsWith(StubPrefix, StringComparison.Ordinal))
            return new(TransferOutcome.AlreadyTransferred, "заметка уже перенесена в вики: в ней ссылка на страницу");
        var statements = Statements(body);
        if (statements.Count == 0)
            return new(TransferOutcome.Invalid, "в заметке нет текста для утверждений: переносить нечего");

        var title = PageTitle(note.Title);
        var page = folder + title + ".md";
        var content = PageText(title, statements);

        // baseHash=null: страница создаётся, а не перезаписывается. Имя занято — Changed; если
        // занявшая страница — недоделанный перенос этой же заметки (сбой между созданием страницы и
        // заменой заметки), перенос просто доводится: второй страницы не будет.
        var res = await _apply.EditPageAsync(ownerId, section, page, null, content).ConfigureAwait(false);
        string? packageId = null;
        switch (res.Outcome)
        {
            case WikiApplyService.HumanEditOutcome.Applied:
                packageId = res.PackageId;
                break;
            case WikiApplyService.HumanEditOutcome.Changed when IsPageOfThisTransfer(res.Content, title, statements):
                break;
            case WikiApplyService.HumanEditOutcome.Changed:
                return new(TransferOutcome.Exists, $"страница «{title}» в разделе уже есть: переименуйте заметку и повторите перенос");
            case WikiApplyService.HumanEditOutcome.Invalid:
                return new(TransferOutcome.Invalid, res.Reason, Violations: res.Violations);
            default:
                return new(TransferOutcome.Refused, res.Reason);
        }

        // Страница на месте — теперь заметка заменяется ссылкой, и только через NotesService.
        // «Перенесено» значит «заметка заменена»: исчезновение заметки и сбой записи — отказ с
        // причиной, а повтор перенос доведёт (страница уже есть и узнаётся выше).
        var stub = StubPrefix + folder.TrimEnd('/') + "/" + title + "]]\n";
        try
        {
            // Update принимает и необязательные ifMatch/byHuman (общие проекты) — группа методов
            // в Func не сводится, поэтому обёртка; поведение по умолчанию прежнее.
            var replace = ReplaceNoteHook ?? ((o, n, r) => _notes.Update(o, n, r));
            if (replace(ownerId, noteId, new UpdateNoteRequest(Content: stub)) is null)
                return new(TransferOutcome.NotFound, "заметка исчезла до замены ссылкой");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or KeyNotFoundException)
        {
            return new(TransferOutcome.Refused,
                $"страница создана, но заметку заменить не удалось ({ex.Message}); повторите перенос — он доведёт замену");
        }
        return new(TransferOutcome.Transferred, null, section, page, packageId);
    }

    // Страница уже создана этим переносом: заголовок тот же, а утверждения «Сводки» (без сносок)
    // совпадают с утверждениями заметки. Страница, которую правили после, не узнаётся — Exists.
    private static bool IsPageOfThisTransfer(string? pageText, string title, IReadOnlyList<string> statements)
    {
        if (pageText is null || WikiPage.Parse(pageText).Model is not { } model || model.Title != title)
            return false;
        var summary = model.Sections.FirstOrDefault(s => s.Name == "Сводка");
        if (summary is null)
            return false;
        var lines = summary.Lines
            .Select(l => string.Join(' ', FootnoteRx.Replace(l, "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)))
            .Where(l => l.Length > 0)
            .ToList();
        return lines.SequenceEqual(statements, StringComparer.Ordinal);
    }

    // Текст заметки без frontmatter.
    private static string BodyOf(string content)
    {
        var text = content.Replace("\r\n", "\n");
        if (!text.StartsWith("---\n", StringComparison.Ordinal))
            return text;
        var end = text.IndexOf("\n---", 4, StringComparison.Ordinal);
        if (end < 0)
            return text;
        var after = text.IndexOf('\n', end + 1);
        return after < 0 ? "" : text[(after + 1)..];
    }

    // Строки заметки → утверждения: без разметки, без пустых и без блоков кода.
    internal static List<string> Statements(string body)
    {
        var result = new List<string>();
        var inFence = false;
        foreach (var raw in body.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("```", StringComparison.Ordinal) || line.StartsWith("~~~", StringComparison.Ordinal))
            {
                inFence = !inFence;
                continue;
            }
            if (inFence || line.Length == 0 || line.All(c => c is '-' or '*' or '_' or '=' or ' '))
                continue;
            line = BulletRx.Replace(line, "");
            line = LinkRx.Replace(line, m => m.Groups[2].Value.Length > 0 ? m.Groups[2].Value : m.Groups[1].Value);
            line = FootnoteRx.Replace(line, "");
            line = string.Join(' ', line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            if (line.Length > 0)
                result.Add(line);
        }
        return result;
    }

    // Заголовок страницы = имя файла: без символов, ломающих ссылку или путь.
    internal static string PageTitle(string title)
    {
        var sb = new StringBuilder();
        foreach (var ch in title.Trim())
            sb.Append(char.IsControl(ch) || "[]|#^/\\:*?\"<>".Contains(ch) ? ' ' : ch);
        var clean = string.Join(' ', sb.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).TrimEnd('.');
        if (clean.Length > 80) clean = clean[..80].TrimEnd();
        return clean.Length == 0 ? "Заметка" : clean;
    }

    private static string PageText(string title, IReadOnlyList<string> statements) =>
        "---\ntype: topic\naliases: []\nowns: []\nsources: []\nstatus: active\n---\n\n"
        + $"# {title}\n\n## Сводка\n{string.Join('\n', statements)}\n\n## Хронология\n\n## Противоречия\n\n## История\n";
}
