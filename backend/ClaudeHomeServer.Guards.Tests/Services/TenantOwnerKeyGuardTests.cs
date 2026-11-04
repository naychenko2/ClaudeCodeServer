using System.Text.RegularExpressions;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Сторож ключа хранилищ: в <c>ProjectAccessService</c> второй аргумент <c>new RequestScope(…)</c>
/// (TenantOwnerId) берётся от владельца проекта/чата, но не от вызывающего.
///
/// Зачем: по TenantOwnerId адресуются хранилища (задачи, заметки, память). Подмена его на
/// <c>callerUserId</c> в общем проекте молча разведёт данные участника и владельца — а
/// поведенческий тест с «владелец = вызывающий» такую мутацию не краснит (ревью S0-2, M1).
/// Сторож текстовый: читает исходник с того же дерева, что собирается.
/// </summary>
public class TenantOwnerKeyGuardTests
{
    private const string ServicePath = "backend/ClaudeHomeServer/Services/ProjectAccessService.cs";

    private static readonly Regex Construction = new(@"new\s+RequestScope\s*\(", RegexOptions.Compiled);
    private static readonly Regex CallerIdentifier = new(@"\b[Cc]allerUserId\b", RegexOptions.Compiled);

    [Fact]
    public void ProjectAccessService_TenantOwnerId_НеБерётсяОтВызывающего()
    {
        var text = StripComments(File.ReadAllText(Path.Combine(FindRepoRoot(), ServicePath)));
        var constructions = Construction.Matches(text);

        // Вакуумно-зелёный сторож хуже отсутствующего: в сервисе их ровно два (Resolve, ResolveSession)
        constructions.Count.Should().BeGreaterThanOrEqualTo(2, "сторож обязан видеть сборки RequestScope");

        foreach (Match m in constructions)
        {
            var args = SplitArgs(text, m.Index + m.Length);
            args.Count.Should().Be(3, $"RequestScope(Caller, TenantOwner, Role): «{Snippet(text, m.Index)}»");
            CallerIdentifier.IsMatch(args[1]).Should().BeFalse(
                $"TenantOwnerId — владелец проекта/чата, не вызывающий: «{Snippet(text, m.Index)}»");
        }
    }

    private static string Snippet(string text, int index) =>
        text.Substring(index, Math.Min(90, text.Length - index)).ReplaceLineEndings(" ");

    // Аргументы вызова по запятым вне вложенных скобок до закрывающей `)` конструктора
    private static List<string> SplitArgs(string text, int start)
    {
        var parts = new List<string>();
        var depth = 0;
        var begin = start;
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '(') depth++;
            else if (c == ')')
            {
                if (depth == 0) { parts.Add(text[begin..i].Trim()); return parts; }
                depth--;
            }
            else if (c == ',' && depth == 0) { parts.Add(text[begin..i].Trim()); begin = i + 1; }
        }
        return parts;
    }

    private static string StripComments(string text) =>
        Regex.Replace(text, @"//[^\r\n]*", "");

    // .git бывает файлом (worktree), поэтому проверяем и каталог, и файл
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var git = Path.Combine(dir.FullName, ".git");
            if ((Directory.Exists(git) || File.Exists(git)) &&
                Directory.Exists(Path.Combine(dir.FullName, "backend"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException(
            "Не найден корень репозитория: подъём от " + AppContext.BaseDirectory + " не встретил .git и backend/.");
    }
}
