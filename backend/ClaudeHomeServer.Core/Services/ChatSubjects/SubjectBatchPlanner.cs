using ClaudeHomeServer.Services.Llm;

namespace ClaudeHomeServer.Services.ChatSubjects;

// Размер пачки и длина реплик под ПРОФИЛЬ места: окно, лимит вывода и потолок по времени.
// llama-server режет промпт сверх окна молча (только лог), поэтому пачка считается с запасом.
public sealed record SubjectBatchPlan(int BatchSize, int SnippetChars);

public static class SubjectBatchPlanner
{
    // Запас окна: оценка токенов по символам грубая
    public const double WindowReserve = 0.25;
    // Консервативная оценка для кириллицы — символов на токен
    public const double CharsPerToken = 2.5;
    // Вывода на одно назначение с новой темой, токенов
    public const int OutputTokensPerChat = 30;
    // Доля лимита вывода под назначения: остальное — запас на обрамление JSON
    public const double OutputShare = 0.7;
    // Обрамление блока чата (разделители, алиас, переводы строк), символов
    public const int ChatOverheadChars = 60;
    // Строка реестра: алиас, тире, имя
    public const int RegistryLineOverheadChars = 10;
    // Реплик человека на чат: первая и две последние
    public const int SnippetsPerChat = 3;
    // Меньше этого пачка бессмысленна — тогда ужимаем реплики
    public const int MinUsefulBatch = 3;

    // ceiling — потолок пачки по времени: генерация на слабой GPU должна укладываться в
    // TimeoutMs профиля. Из профиля его не вывести (скорость зависит от железа), поэтому он
    // конфигурируется отдельно (ChatSubjects:BatchCeiling, по умолчанию 8 под Text/45 с).
    // BatchSize = 0 — профиль не пускает даже одного чата.
    public static SubjectBatchPlan Plan(CheapProfileSpec spec, int registryCount, int ceiling)
    {
        var budgetTokens = (spec.NumCtx - spec.NumPredict) * (1 - WindowReserve);
        var budgetChars = (int)(budgetTokens * CharsPerToken);
        var registry = Math.Min(Math.Max(registryCount, 0), ChatSubjectRules.MaxSubjectsHuman);
        var fixedChars = SubjectPrompt.RulesLength
            + registry * (ChatSubjectRules.MaxNameLength + RegistryLineOverheadChars);
        var available = budgetChars - fixedChars;

        var snippet = ChatSubjectRules.SnippetMaxChars;
        var byWindow = available / PerChat(snippet);
        if (byWindow < MinUsefulBatch)
        {
            snippet = ChatSubjectRules.SnippetMinChars;
            byWindow = available / PerChat(snippet);
        }

        var byOutput = (int)(OutputShare * spec.NumPredict / OutputTokensPerChat);
        var size = Math.Min(Math.Min(byWindow, byOutput), Math.Max(ceiling, 0));
        return new SubjectBatchPlan(Math.Max(size, 0), snippet);
    }

    private static int PerChat(int snippetChars) =>
        ChatSubjectRules.TitleMaxChars + SnippetsPerChat * snippetChars + ChatOverheadChars;
}
