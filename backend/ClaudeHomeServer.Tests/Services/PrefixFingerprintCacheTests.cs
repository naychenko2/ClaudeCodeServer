using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Tests.Services;

// Кэш отпечатков префиксов истории (Перф-3 B8): массив AllPrefixFingerprints считается один
// раз на версию истории и переиспользуется. Версия = LastWriteUtc файла + длина списка.
// Тесты (Перф-3 B10, п.1): поздняя правка tool_use прошлого хода (число записей неизменно)
// меняет LastWriteUtc → кэш пересчитан; мутация «убрать проверку LastWriteUtc» красит тест.
public class PrefixFingerprintCacheTests
{
    // Управляемая версия: тест подменяет LastWriteUtc без реального файла истории
    private sealed class FakeHistory : ChatHistoryService
    {
        public FakeHistory() : base(new ConfigurationBuilder().Build()) { }
        public DateTime? FakeVersion { get; set; }
        public override DateTime? LastWriteUtc(string claudeSessionId) => FakeVersion;
    }

    private static (List<StoredMessage> History, StoredToolUseMessage ToolUse) BuildHistory()
    {
        var tu = new StoredToolUseMessage { Id = "t1", Name = "Task", Input = "{}" };
        var history = new List<StoredMessage>
        {
            new StoredUserMessage("в"),
            tu,
            new StoredResultMessage("success", 10, 1),
        };
        return (history, tu);
    }

    // Дефект, который ловит тест: аккумулятор дописывает result в tool_use прошлого хода
    // УЖЕ ПОСЛЕ его конца (число записей не меняется), и файл истории переписывается.
    // Кэш обязан увидеть новую версию (LastWriteUtc) и пересчитать отпечатки.
    [Fact]
    public void GetOrCompute_ПоздняяПравкаToolUse_ПересчитываетКэш()
    {
        var (history, tu) = BuildHistory();
        var fake = new FakeHistory();
        var cache = new PrefixFingerprintCache(fake);
        const string csid = "csid-1";

        // Первый запрос: версия T1, считаем и кэшируем
        fake.FakeVersion = new DateTime(2026, 10, 19, 10, 0, 0, DateTimeKind.Utc);
        var fps1 = cache.GetOrCompute(csid, history);

        // Поздняя правка tool_use (result) при НЕИЗМЕННЁМ числе записей + новая запись файла
        tu.Result = "агент закончил";
        fake.FakeVersion = new DateTime(2026, 10, 19, 10, 5, 0, DateTimeKind.Utc);
        var fps2 = cache.GetOrCompute(csid, history);

        // Кэш пересчитан: отпечатки префиксов, включающих tool_use (индексы 1 и 2), изменились
        fps2[1].Should().NotBe(fps1[1]);
        fps2[2].Should().NotBe(fps1[2]);
    }

    // Та же версия и та же длина — кэш не пересчитан: отдаётся тот же массив (по ссылке)
    [Fact]
    public void GetOrCompute_ТотЖеФайлИБезПравки_КэшПереиспользуется()
    {
        var (history, _) = BuildHistory();
        var fake = new FakeHistory();
        var cache = new PrefixFingerprintCache(fake);
        const string csid = "csid-1";

        fake.FakeVersion = new DateTime(2026, 10, 19, 10, 0, 0, DateTimeKind.Utc);
        var fps1 = cache.GetOrCompute(csid, history);
        var fps2 = cache.GetOrCompute(csid, history);

        fps2.Should().BeSameAs(fps1);
    }
}
