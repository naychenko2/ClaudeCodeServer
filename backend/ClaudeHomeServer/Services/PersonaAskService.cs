using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Llm;

namespace ClaudeHomeServer.Services;

// One-shot ответ персоны от её лица: слой персоны (роль + характер) + recall её долгой
// памяти + вопрос; модель — модель персоны. Вынесен из PersonasController.Ask, чтобы
// совещания (PersonaMeetingService) спрашивали персон без HTTP. Анти-рекурсия по
// построению: one-shot идёт без MCP-серверов — «спросить третью персону» изнутри нельзя.
public sealed class PersonaAskService(
    PersonaMemoryService memory,
    PersonaPromptBuilder promptBuilder,
    IOneShotRunner oneShot,
    IConfiguration config,
    ILogger<PersonaAskService> log)
{
    public async Task<string> AskAsync(string ownerId, Persona persona, string question,
        string? context, CancellationToken ct = default)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(promptBuilder.Build(persona, persona.Model, greeted: false));

        // Релевантная память — best-effort: без неё ответ всё равно валиден
        if (persona.MemoryEnabled)
        {
            try
            {
                // Шкала скоринга — взвешенная сумма (PersonaMemoryScorer), порог ~0.30
                var minScore = double.TryParse(config["Persona:RecallMinScore"],
                    System.Globalization.CultureInfo.InvariantCulture, out var ms) ? ms : 0.30;
                var recall = await memory.BuildRecallAsync(ownerId, persona.Id, question, topK: 5, minScore);
                if (!string.IsNullOrWhiteSpace(recall?.Text)) sb.AppendLine().AppendLine(recall.Text);
            }
            catch (Exception ex) { log.LogWarning(ex, "persona_ask: recall памяти {Persona}", persona.Id); }
        }

        sb.AppendLine();
        sb.AppendLine("Тебя спрашивает ассистент пользователя из другого разговора. Этот разговор ты не видишь — " +
                      "отвечай по вопросу и переданному контексту, от своего лица и в своём характере, по существу.");
        if (!string.IsNullOrWhiteSpace(context))
            sb.AppendLine($"\nКонтекст: {context.Trim()}");
        sb.AppendLine($"\nВопрос: {question.Trim()}");

        var timeout = TimeSpan.FromMilliseconds(
            int.TryParse(config["Persona:AskTimeoutMs"], out var t) ? t : 120_000);
        var answer = await oneShot.RunAsync(sb.ToString(), oneShot.NormalizeModel(persona.Model), timeout, ct);
        if (string.IsNullOrWhiteSpace(answer))
            throw new InvalidOperationException("Персона не ответила (пустой ответ модели)");
        return answer;
    }
}
