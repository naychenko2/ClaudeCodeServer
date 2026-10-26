using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Spend;
using Moq;

namespace ClaudeHomeServer.Tests.Services;

// Ключевая логика аналитики расхода: rollup и границы детального окна (SpendStore),
// авторизация admin/mine (SpendAccess), группировка pivot (GroupRaw), дедуп backfill (Spread)
public class SpendAnalyticsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "spend-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private SpendStore NewStore(int detailDays = 30) => new(_dir, detailDays);

    // Сервис аналитики с пустыми реестрами: для проверок контракта (класс цены) имена
    // разрезов не важны, а подменять их живыми менеджерами смысла нет.
    private static SpendAnalyticsService NewAnalytics(SpendStore store)
    {
        var sessions = new Mock<ISessionDirectory>();
        sessions.Setup(s => s.GetById(It.IsAny<string>())).Returns((Session?)null);
        var projects = new Mock<IProjectManager>();
        projects.Setup(p => p.GetById(It.IsAny<string>())).Returns((Project?)null);
        var tasks = new Mock<ITaskLookup>();
        tasks.Setup(t => t.GetById(It.IsAny<string>())).Returns((TaskItem?)null);
        var personas = new Mock<IPersonaLookup>();
        personas.Setup(p => p.GetByIdInternal(It.IsAny<string>())).Returns((Persona?)null);
        var users = new Mock<IUserStore>();
        users.Setup(u => u.GetById(It.IsAny<string>())).Returns((User?)null);
        var llm = new Mock<IModelResolver>();
        llm.Setup(l => l.ResolveModelOrDefault(It.IsAny<string?>(), It.IsAny<string?>()))
            .Returns((string? model, string? provider) => model ?? $"{provider}-default");
        return new SpendAnalyticsService(store, sessions.Object, projects.Object, tasks.Object,
            personas.Object, users.Object, llm.Object);
    }

    private static SpendRecord Rec(DateTime ts, string owner = "u1", string? project = "p1",
        string? session = "s1", string provider = "claude", string? model = "opus",
        string source = SpendSources.ChatTurn, long input = 10, long output = 5,
        long cacheRead = 100, long cacheCreate = 20, int generations = 0, string? id = null) => new()
        {
            Id = id ?? Guid.NewGuid().ToString("N"),
            Timestamp = ts,
            OwnerId = owner,
            ProjectId = project,
            SessionId = session,
            Provider = provider,
            Model = model,
            Source = source,
            InputTokens = input,
            OutputTokens = output,
            CacheReadTokens = cacheRead,
            CacheCreationTokens = cacheCreate,
            Generations = generations,
        };

    // --- rollup и границы окна ---

    [Fact]
    public void Rollup_СтарыеДниСворачиваются_ДеньНаГраницеОстаётсяДетальным()
    {
        var store = NewStore();
        var cutoff = new DateOnly(2026, 7, 10);
        var oldDay = new DateTime(2026, 7, 5, 12, 0, 0, DateTimeKind.Utc);
        var edgeDay = new DateTime(2026, 7, 10, 12, 0, 0, DateTimeKind.Utc);

        store.Record(Rec(oldDay));
        store.Record(Rec(oldDay, model: "sonnet"));
        store.Record(Rec(edgeDay));

        store.RollupOlderThan(cutoff);

        // Старый день ушёл в агрегаты (по строке на модель), детали удалены вместе с jsonl
        var daily = store.DailyBetween(new DateOnly(2026, 7, 5), new DateOnly(2026, 7, 5));
        Assert.Equal(2, daily.Count);
        Assert.Empty(store.DetailsBetween(new DateOnly(2026, 7, 5), new DateOnly(2026, 7, 5)));
        Assert.False(File.Exists(Path.Combine(_dir, "turns-2026-07-05.jsonl")));
        Assert.True(store.IsAggregated(new DateOnly(2026, 7, 5)));

        // День ровно на cutoff — строго старше не является, остаётся детальным
        Assert.Single(store.DetailsBetween(cutoff, cutoff));
        Assert.False(store.IsAggregated(cutoff));
    }

    [Fact]
    public void Rollup_Идемпотентен_ПовторНеДублирует()
    {
        var store = NewStore();
        var oldDay = new DateTime(2026, 7, 5, 12, 0, 0, DateTimeKind.Utc);
        store.Record(Rec(oldDay));
        var cutoff = new DateOnly(2026, 7, 10);

        store.RollupOlderThan(cutoff);
        store.RollupOlderThan(cutoff);

        var daily = store.DailyBetween(new DateOnly(2026, 7, 5), new DateOnly(2026, 7, 5));
        var row = Assert.Single(daily);
        Assert.Equal(1, row.Turns);
        Assert.Equal(10, row.InputTokens);
        Assert.Equal(135, row.InputTokens + row.OutputTokens + row.CacheReadTokens + row.CacheCreationTokens);
    }

    [Fact]
    public void Store_ПереживаетПерезапуск_ДеталиИАгрегаты()
    {
        var ts = new DateTime(2026, 7, 20, 12, 0, 0, DateTimeKind.Utc);
        var oldTs = new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc);
        var store = NewStore();
        store.Record(Rec(ts));
        store.Record(Rec(oldTs));
        store.RollupOlderThan(new DateOnly(2026, 7, 10));

        var reloaded = NewStore();
        Assert.Single(reloaded.DetailsBetween(new DateOnly(2026, 7, 20), new DateOnly(2026, 7, 20)));
        Assert.Single(reloaded.DailyBetween(new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 1)));
    }

    [Fact]
    public void Rollup_PersistDailyПадает_СырьёНеУдаляется()
    {
        var dir = Path.Combine(_dir, "spend_persist_fail");
        Directory.CreateDirectory(dir);
        var store = new SpendStore(dir, detailDays: 30);

        var oldDay = new DateTime(2026, 7, 5, 12, 0, 0, DateTimeKind.Utc);
        store.Record(Rec(oldDay));
        store.Record(Rec(oldDay, model: "sonnet"));

        // Создаём daily.json.tmp как DIRECTORY — File.WriteAllText к нему упадёт
        var tmpPath = Path.Combine(dir, "daily.json.tmp");
        Directory.CreateDirectory(tmpPath);

        var cutoff = new DateOnly(2026, 7, 10);
        store.RollupOlderThan(cutoff);

        // Сырьё НЕ удалено: daily.json не записан, rollup откатился
        var turnsFile = Path.Combine(dir, "turns-2026-07-05.jsonl");
        Assert.True(File.Exists(turnsFile), "turns-*.jsonl остаётся на диске после сбоя PersistDaily");
        Assert.Equal(2, store.DetailsBetween(new DateOnly(2026, 7, 5), new DateOnly(2026, 7, 5)).Count);
        Assert.False(store.IsAggregated(new DateOnly(2026, 7, 5)), "день ещё не в агрегатах");

        // Исправляем файловую систему: удаляем блокирующий каталог
        Directory.Delete(tmpPath);

        // Повторный проход: PersistDaily теперь succeeds, сырьё удаляется
        store.RollupOlderThan(cutoff);
        Assert.False(File.Exists(turnsFile), "после успешной записи daily.json сырьё удалено");
        Assert.Empty(store.DetailsBetween(new DateOnly(2026, 7, 5), new DateOnly(2026, 7, 5)));
        Assert.True(store.IsAggregated(new DateOnly(2026, 7, 5)));

        // Идемпотентность: следующий проход по пустому _details — no-op, агрегаты не дублируются
        store.RollupOlderThan(cutoff);
        Assert.Equal(2, store.DailyBetween(new DateOnly(2026, 7, 5), new DateOnly(2026, 7, 5)).Count);
    }

    [Fact]
    public void Record_ПустаяЗаписьОтбрасывается()
    {
        var store = NewStore();
        store.Record(new SpendRecord { Timestamp = DateTime.UtcNow });
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        Assert.Empty(store.DetailsBetween(today, today));
    }

    [Fact]
    public void Aggregate_СуммируетПоСоставномуКлючу()
    {
        var day = new DateTime(2026, 7, 5, 10, 0, 0, DateTimeKind.Utc);
        var rows = SpendStore.Aggregate("2026-07-05",
        [
            Rec(day), Rec(day), Rec(day, model: "sonnet"),
            Rec(day, source: SpendSources.Fal, input: 0, output: 0, cacheRead: 0, cacheCreate: 0, generations: 1),
        ]);
        Assert.Equal(3, rows.Count);
        var opus = rows.Single(r => r.Model == "opus" && r.Source == SpendSources.ChatTurn);
        Assert.Equal(2, opus.Turns);
        Assert.Equal(20, opus.InputTokens);
        Assert.Equal(1, rows.Single(r => r.Source == SpendSources.Fal).Generations);
    }

    [Fact]
    public void Aggregate_РубляхСуммируетОтдельноОтДолларов()
    {
        // Озвучка тарифицируется в рублях, ходы моделей — в долларах. Свёртка дня обязана
        // держать валюты врозь: сложенные в одно поле, они дали бы бессмысленное число.
        var day = new DateTime(2026, 7, 5, 10, 0, 0, DateTimeKind.Utc);
        var rows = SpendStore.Aggregate("2026-07-05",
        [
            new SpendRecord
            {
                Timestamp = day, OwnerId = "u1", Provider = "yandex", Model = "marina",
                Source = SpendSources.Tts, Generations = 3, CostRub = 0.4878,
            },
            new SpendRecord
            {
                Timestamp = day, OwnerId = "u1", Provider = "yandex", Model = "marina",
                Source = SpendSources.Tts, Generations = 1, CostRub = 0.1626,
            },
            Rec(day),
        ]);

        var tts = rows.Single(r => r.Source == SpendSources.Tts);
        Assert.Equal(4, tts.Generations);
        Assert.Equal(0.6504, tts.CostRub, 4);
        Assert.Equal(0, tts.CostUsd);
        Assert.Equal(0, rows.Single(r => r.Source == SpendSources.ChatTurn).CostRub);
    }

    [Fact]
    public void Tokenless_ИсточникиБезТокенов_ОпознаютсяВсе()
    {
        // По этому признаку источники без токенов вылетают из рейтинга «дорогих ходов»:
        // там сортировка по токенам, а у них их нет — висели бы внизу вечным нулём
        Assert.True(SpendSources.IsTokenless(SpendSources.Tts));
        Assert.True(SpendSources.IsTokenless(SpendSources.Fal));
        Assert.True(SpendSources.IsTokenless(SpendSources.Glif));
        Assert.True(SpendSources.IsTokenless(SpendSources.Higgsfield));
        Assert.False(SpendSources.IsTokenless(SpendSources.ChatTurn));
        Assert.False(SpendSources.IsTokenless(SpendSources.Free));
    }

    // --- авторизация admin/mine ---

    [Fact]
    public void Access_НеАдмин_ScopeAll_Запрещён()
    {
        var res = SpendAccess.Resolve(isAdmin: false, "u1", scope: "all",
            null, null, null, null, null, null, null, null);
        Assert.NotNull(res.Error);
    }

    [Fact]
    public void Access_НеАдмин_ЧужойUserФильтр_Запрещён()
    {
        var res = SpendAccess.Resolve(isAdmin: false, "u1", scope: null,
            user: "u2", null, null, null, null, null, null, null);
        Assert.NotNull(res.Error);
    }

    [Fact]
    public void Access_НеАдмин_РазрезUser_Запрещён()
    {
        var res = SpendAccess.Resolve(isAdmin: false, "u1", scope: null,
            null, null, null, null, null, null, null, null, groupBy: "user");
        Assert.NotNull(res.Error);
    }

    [Fact]
    public void Access_НеАдмин_ВладелецПринудительный()
    {
        var res = SpendAccess.Resolve(isAdmin: false, "u1", scope: null,
            null, project: "p1", null, null, null, null, null, null);
        Assert.Null(res.Error);
        Assert.False(res.AllUsers);
        Assert.Equal("u1", res.Filter.Owner);
        Assert.Equal("p1", res.Filter.Project);
    }

    [Fact]
    public void Access_Админ_ScopeAll_БезВладельца_ИСужениеФильтром()
    {
        var all = SpendAccess.Resolve(isAdmin: true, "admin", scope: "all",
            null, null, null, null, null, null, null, null);
        Assert.Null(all.Error);
        Assert.True(all.AllUsers);
        Assert.Null(all.Filter.Owner);

        var narrowed = SpendAccess.Resolve(isAdmin: true, "admin", scope: "all",
            user: "u2", null, null, null, null, null, null, null);
        Assert.Equal("u2", narrowed.Filter.Owner);

        // Админ без scope=all — тот же режим «моё», что у всех
        var mine = SpendAccess.Resolve(isAdmin: true, "admin", scope: null,
            null, null, null, null, null, null, null, null);
        Assert.Equal("admin", mine.Filter.Owner);
        Assert.False(mine.AllUsers);
    }

    // --- группировка pivot ---

    [Fact]
    public void Pivot_ГруппируетПоРазрезуИСортируетПоОбъёму()
    {
        var d = new DateOnly(2026, 7, 20);
        List<SpendSlice> slices =
        [
            new(d, "u1", "p1", "s1", null, null, "claude", "opus", SpendSources.ChatTurn,
                100, 50, 0, 0, 0, 0, 0, 1, Detailed: true),
            new(d, "u1", "p1", "s2", null, null, "claude", "opus", SpendSources.ChatTurn,
                10, 5, 0, 0, 0, 0, 0, 1, Detailed: true),
            // Свёрнутый день той же модели и день другой модели
            new(d.AddDays(-40), "u1", "p1", "s1", null, null, "claude", "opus", SpendSources.ChatTurn,
                1000, 0, 0, 0, 0, 0, 0, 7, Detailed: false),
            new(d, "u1", "p1", "s1", null, null, "deepseek", "deepseek-chat", SpendSources.ChatTurn,
                1, 1, 0, 0, 0, 0, 0, 1, Detailed: true),
        ];

        var byModel = SpendAnalyticsService.GroupRaw(slices, "model");
        Assert.Equal(2, byModel.Count);
        Assert.Equal("opus", byModel[0].Key); // крупнее — первым
        Assert.Equal(1165, byModel[0].Tokens.Total);
        Assert.Equal(9, byModel[0].Turns);
        Assert.True(byModel[0].HasDetail);

        var byChat = SpendAnalyticsService.GroupRaw(slices, "chat");
        Assert.Equal(2, byChat.Count);
        Assert.Equal("s1", byChat[0].Key);
    }

    // --- резолв дефолтной модели (SpendRecord.Model никогда не пустой) ---

    [Fact]
    public void Pivot_ДефолтнаяМодель_ГруппируетсяБезПустогоКлюча()
    {
        // После резолва null-модели в точке записи slice всегда несёт конкретный id
        // (дефолт подписки "default" или id модели провайдера) — пустого ключа и группы
        // «Модель по умолчанию» в pivot быть не должно.
        var d = new DateOnly(2026, 7, 20);
        List<SpendSlice> slices =
        [
            new(d, "u1", "p1", "s1", null, null, "claude", "default", SpendSources.ChatTurn,
                100, 50, 0, 0, 0, 0, 0, 1, Detailed: true),
            new(d, "u1", "p1", "s2", null, null, "claude", "default", SpendSources.ChatTurn,
                10, 5, 0, 0, 0, 0, 0, 1, Detailed: true),
            new(d, "u1", "p1", "s3", null, null, "claude", "opus", SpendSources.ChatTurn,
                5, 5, 0, 0, 0, 0, 0, 1, Detailed: true),
        ];

        var byModel = SpendAnalyticsService.GroupRaw(slices, "model");

        Assert.Equal(2, byModel.Count);
        Assert.DoesNotContain(byModel, n => n.Key.Length == 0); // нет пустой «Модели по умолчанию»
        var def = byModel.Single(n => n.Key == "default");
        Assert.Equal(2, def.Turns);
        Assert.Equal(165, def.Tokens.Total);
    }

    // --- ценовой класс расхода (SpendCostTiers) ---

    [Fact]
    public void CostTier_ПравилоКласса_ПоИсточникуПровайдеруИМодели()
    {
        // Единственная точка правды правила: claude — дорого, остальное облако — дёшево
        Assert.Equal(SpendCostTiers.Expensive, SpendCostTiers.Of(SpendSources.ChatTurn, "claude", "opus"));
        // Подписки claude (sub-*) — тот же провайдер claude после NormalizeProvider
        Assert.Equal(SpendCostTiers.Expensive, SpendCostTiers.Of(SpendSources.ChatTurn, "sub-work", "opus"));
        Assert.Equal(SpendCostTiers.Cheap, SpendCostTiers.Of(SpendSources.ChatTurn, "minimax", "minimax-m1"));
        Assert.Equal(SpendCostTiers.Cheap, SpendCostTiers.Of(SpendSources.ChatTurn, "glm", "glm-4.6"));
        // Источник free важнее провайдера: запись о бесплатной модели claude — бесплатная
        Assert.Equal(SpendCostTiers.Free, SpendCostTiers.Of(SpendSources.Free, "claude", "opus"));
        // Бесплатные исполнители: локальный движок, «*:free»
        Assert.Equal(SpendCostTiers.Free, SpendCostTiers.Of(SpendSources.ChatTurn, "ollama", "qwen3:14b"));
        Assert.Equal(SpendCostTiers.Free, SpendCostTiers.Of(SpendSources.ChatTurn, "openrouter", "nvidia/nemotron:free"));
        // «*-direct» считается по базовому ключу: openrouter — дёшево
        Assert.Equal(SpendCostTiers.Cheap, SpendCostTiers.Of(SpendSources.ChatTurn, "openrouter-direct", "any"));
    }

    // Пары из живых данных расхода (26.10): правило владельца, одно на запись и каталог.
    // Мутации «убрать ветку claude-<N>» и «убрать снятие суффикса -direct» → краснеет.
    [Theory]
    [InlineData("claude", "chat-turn", SpendCostTiers.Expensive)]
    [InlineData("claude-2", "chat-turn", SpendCostTiers.Expensive)]
    [InlineData("claude-3", "chat-turn", SpendCostTiers.Expensive)]
    [InlineData("sub-x", "chat-turn", SpendCostTiers.Expensive)]
    [InlineData("claude-direct", "chat-turn", SpendCostTiers.Expensive)]
    [InlineData("ollama-direct", "chat-turn", SpendCostTiers.Free)]
    [InlineData("minimax-direct", "free", SpendCostTiers.Cheap)]
    [InlineData("minimax-direct", "chat-turn", SpendCostTiers.Cheap)]
    [InlineData("minimax-m31-direct", "chat-turn", SpendCostTiers.Cheap)]
    [InlineData("minimax", "chat-turn", SpendCostTiers.Cheap)]
    [InlineData("ghostcli", "chat-turn", SpendCostTiers.Cheap)]
    [InlineData("deepseek", "chat-turn", SpendCostTiers.Cheap)]
    [InlineData("freellmapi-direct", "chat-turn", SpendCostTiers.Free)]
    [InlineData("freellmapi-direct", "free", SpendCostTiers.Free)]
    [InlineData("local-qwen", "free", SpendCostTiers.Free)]
    [InlineData("llama-server", "chat-turn", SpendCostTiers.Free)]
    public void CostTier_ПарыИзЖивыхДанных(string provider, string source, string expected)
    {
        Assert.Equal(expected, SpendCostTiers.Of(source, provider, null));
        // Каталог: «local-qwen» бесплатен по флажку isLocal (в Core ключ не перечислен)
        var catalog = SpendCostTiers.OfProvider(provider, isLocal: provider == "local-qwen");
        Assert.Equal(expected, catalog);
    }

    // Неизвестный провайдер (пустой ключ) → «дорого» в ОБОИХ местах: цена неизвестна,
    // занижать её нельзя. Правило одно на каталог (/api/models) и аналитику.
    // Мутация «в OfProvider пустой ключ → cheap» → краснеет.
    [Fact]
    public void CostTier_НеизвестныйПровайдер_Дорого_ВКаталогеИВТратах()
    {
        Assert.Equal(SpendCostTiers.Expensive, SpendCostTiers.Of(SpendSources.ChatTurn, "", "any"));
        Assert.Equal(SpendCostTiers.Expensive, SpendCostTiers.OfProvider("", isLocal: false));
        Assert.Equal(SpendCostTiers.Expensive, SpendCostTiers.OfProvider("   ", isLocal: false));
        // Локальный движок с пустым ключом остаётся бесплатным: признак важнее ключа
        Assert.Equal(SpendCostTiers.Free, SpendCostTiers.OfProvider("", isLocal: true));
    }

    // «:free» — признак МОДЕЛИ, а не провайдера: в тратах такая модель бесплатна, а класс
    // провайдера в каталоге остаётся «дёшево» (провайдер с такими моделями отдаёт и платные).
    // Расхождение уровней осознанное и зафиксировано тестом.
    // Мутация «убрать признак ':free' из SpendSources.IsFree» → краснеет (в Of ожидается free).
    [Fact]
    public void CostTier_МодельFree_БесплатнаВТратах_ПровайдерВКаталогеДёшевый()
    {
        Assert.Equal(SpendCostTiers.Free,
            SpendCostTiers.Of(SpendSources.ChatTurn, "openrouter", "nvidia/nemotron:free"));
        Assert.Equal(SpendCostTiers.Cheap, SpendCostTiers.OfProvider("openrouter", isLocal: false));
    }

    // Провайдеры «freellm*» (в т.ч. «freellmapi» — локальный прокси на свою GPU) бесплатны.
    // Источник правды — Core (SpendCostTiers.IsFreeProviderKey); фронт берёт готовый класс
    // с бэка (costTier в /api/models) и своего второго правила не имеет. Локальная копия
    // «freellm*» во фронте (lib/spendSources.ts) — только откат на случай неизвестного класса,
    // поэтому при расширении правила в Core откат надо продублировать.
    // Мутация «удалить правило freellm*» → free ожидаем, а придёт cheap → краснеет.
    [Fact]
    public void CostTier_ПровайдерFreellm_Бесплатный()
    {
        Assert.Equal(SpendCostTiers.Free, SpendCostTiers.Of(SpendSources.ChatTurn, "freellmapi", "auto:fast"));
        Assert.Equal(SpendCostTiers.Free, SpendCostTiers.Of(SpendSources.ChatTurn, "freellm", "any"));
        // Каталог провайдеров (/api/models) — тот же класс по одному провайдеру
        Assert.Equal(SpendCostTiers.Free, SpendCostTiers.OfProvider("freellmapi", isLocal: false));
        Assert.Equal(SpendCostTiers.Free, SpendCostTiers.OfProvider("freellm", isLocal: false));
        // Признак источника — то же правило, что класс (Р1(а), решение Андрея 26.10):
        // freellm* бесплатен и в источнике, и в классе — две точки не должны разъезжаться.
        Assert.True(SpendSources.IsFree("freellmapi", "auto:fast"));
    }

    // Смешанная группа: класс узла — класс с наибольшей суммой токенов. Живая форма:
    // 95% токенов дешёвые (minimax) и 5% с провайдером claude на той же модели → cheap.
    // Мутация «побеждает самый дорогой присутствующий класс (Rank)» → expensive ≠ cheap → краснеет.
    [Fact]
    public void Tier_УзлаPivot_СмешаннаяГруппа_БерётсяБольшинствоТокенов()
    {
        var day = new DateOnly(2026, 7, 20);
        var cheapMajor = new SpendSlice(day, "u1", null, null, null, null, "minimax", "MiniMax-M3",
            SpendSources.ChatTurn, 950, 0, 0, 0, 0, 0, 0, 1, Detailed: true);
        var expensiveMinor = new SpendSlice(day, "u1", null, null, null, null, "claude", "MiniMax-M3",
            SpendSources.ChatTurn, 50, 0, 0, 0, 0, 0, 0, 1, Detailed: true);

        var byModel = SpendAnalyticsService.GroupRaw([cheapMajor, expensiveMinor], "model");

        Assert.Equal(SpendCostTiers.Cheap, byModel.Single(n => n.Key == "MiniMax-M3").Tier);
    }

    // Равенство токенов: побеждает более дорогой класс.
    [Fact]
    public void Tier_УзлаPivot_РавныеТокены_БерётсяБолееДорогой()
    {
        var day = new DateOnly(2026, 7, 20);
        var cheap = new SpendSlice(day, "u1", null, null, null, null, "minimax", "m",
            SpendSources.ChatTurn, 100, 0, 0, 0, 0, 0, 0, 1, Detailed: true);
        var expensive = new SpendSlice(day, "u1", null, null, null, null, "claude", "m",
            SpendSources.ChatTurn, 100, 0, 0, 0, 0, 0, 0, 1, Detailed: true);

        Assert.Equal(SpendCostTiers.Expensive,
            SpendAnalyticsService.GroupRaw([cheap, expensive], "model").Single().Tier);
    }

    // Локальная генерация медиа (provider=local, source=local) — бесплатно, одним правилом
    // с остальными локальными ключами. Мутация «убрать "local" из ByKey» → cheap → краснеет.
    [Fact]
    public void Of_ПровайдерLocal_Бесплатно()
    {
        Assert.Equal(SpendCostTiers.Free, SpendCostTiers.Of("local", "local", "qwen-image"));
        Assert.Equal(SpendCostTiers.Free, SpendCostTiers.OfProvider("local", isLocal: false));
    }

    // ByTierOf: слайс с пустым Tier (Tier по умолчанию "") даёт фолбэк на SliceTier —
    // лишнего четвёртого ключа не появляется, сумма трёх ключей равна дневному итогу.
    // Мутация «убрать фолбэк SliceTier в ByTierOf» → четвёртый ключ "" → краснеет.
    [Fact]
    public void ByTierOf_ПустойTier_ФолбэкНаSliceTier_БезЛишнегоКлюча()
    {
        var day = new DateOnly(2026, 7, 20);
        var slice = new SpendSlice(day, "u1", null, null, null, null, "claude", "opus",
            SpendSources.ChatTurn, 10, 5, 100, 20, 0, 0, 0, 1, Detailed: true);

        var byTier = SpendAnalyticsService.ByTierOf([slice]);

        Assert.Equal(3, byTier.Count);
        Assert.DoesNotContain("", byTier.Keys);
        Assert.Equal(135, byTier[SpendCostTiers.Expensive]);
        Assert.Equal(135, byTier.Values.Sum());
    }

    [Fact]
    public void ByTier_ВОбзоре_СуммыПоКлассамИСуммаКлючейРавнаИтогуДня()
    {
        var store = NewStore();
        var ts = new DateTime(2026, 7, 20, 12, 0, 0, DateTimeKind.Utc);
        // Три класса в один день: дорого (claude), дёшево (minimax), бесплатно (источник free)
        store.Record(Rec(ts, provider: "claude", model: "opus", input: 10, output: 5, cacheRead: 100, cacheCreate: 20));
        store.Record(Rec(ts, provider: "minimax", model: "minimax-m1", input: 1, output: 2, cacheRead: 3, cacheCreate: 4));
        store.Record(Rec(ts, provider: "claude", model: "haiku", source: SpendSources.Free,
            input: 7, output: 8, cacheRead: 9, cacheCreate: 10));

        var analytics = NewAnalytics(store);
        var day = new DateOnly(2026, 7, 20);
        var overview = analytics.Overview(day, day, new SpendFilter(), allUsers: false, currentUserId: "u1");

        var byDay = Assert.Single(overview.ByDay);
        Assert.Equal(179, byDay.Total);
        Assert.Equal(135, byDay.ByTier[SpendCostTiers.Expensive]);
        Assert.Equal(10, byDay.ByTier[SpendCostTiers.Cheap]);
        Assert.Equal(34, byDay.ByTier[SpendCostTiers.Free]);
        // Ключи всегда полные, даже если класс не встречался, и их сумма равна итогу дня
        Assert.Equal(3, byDay.ByTier.Count);
        Assert.Equal(byDay.Total, byDay.ByTier.Values.Sum());
    }

    [Fact]
    public void ByTier_КлючиВсегдаПолные_ДажеБезТратыЭтогоКласса()
    {
        var store = NewStore();
        var ts = new DateTime(2026, 7, 20, 12, 0, 0, DateTimeKind.Utc);
        store.Record(Rec(ts, provider: "claude", model: "opus"));

        var analytics = NewAnalytics(store);
        var day = new DateOnly(2026, 7, 20);
        var overview = analytics.Overview(day, day, new SpendFilter(), allUsers: false, currentUserId: "u1");

        var byDay = Assert.Single(overview.ByDay);
        Assert.Equal(3, byDay.ByTier.Count);
        Assert.Contains(SpendCostTiers.Free, byDay.ByTier.Keys);
        Assert.Contains(SpendCostTiers.Cheap, byDay.ByTier.Keys);
        Assert.Contains(SpendCostTiers.Expensive, byDay.ByTier.Keys);
        Assert.Equal(0, byDay.ByTier[SpendCostTiers.Free]);
        Assert.Equal(0, byDay.ByTier[SpendCostTiers.Cheap]);
    }

    [Fact]
    public void Tier_Карточек_ЗаполненУМоделейИПровайдеров_УОстальныхРазрезовПусто()
    {
        var store = NewStore();
        var ts = new DateTime(2026, 7, 20, 12, 0, 0, DateTimeKind.Utc);
        store.Record(Rec(ts, provider: "claude", model: "opus"));
        store.Record(Rec(ts, provider: "minimax", model: "minimax-m1"));

        var analytics = NewAnalytics(store);
        var day = new DateOnly(2026, 7, 20);
        var overview = analytics.Overview(day, day, new SpendFilter(), allUsers: false, currentUserId: "u1");

        var models = overview.Cards["models"];
        Assert.Equal(SpendCostTiers.Expensive, models.Single(r => r.Key == "opus").Tier);
        Assert.Equal(SpendCostTiers.Cheap, models.Single(r => r.Key == "minimax-m1").Tier);

        var providers = overview.Cards["providers"];
        Assert.Equal(SpendCostTiers.Expensive, providers.Single(r => r.Key == "claude").Tier);
        Assert.Equal(SpendCostTiers.Cheap, providers.Single(r => r.Key == "minimax").Tier);

        // Остальные разрезы — не «чем тратили», класс у них не заполняется
        Assert.All(overview.Cards["sources"], r => Assert.Null(r.Tier));
        Assert.All(overview.Cards["projects"], r => Assert.Null(r.Tier));
        Assert.All(overview.Cards["chats"], r => Assert.Null(r.Tier));
    }

    [Fact]
    public void Tier_УзлаPivot_ЗаполненУПровайдераИМодели_УПользователяПусто()
    {
        var store = NewStore();
        var ts = new DateTime(2026, 7, 20, 12, 0, 0, DateTimeKind.Utc);
        store.Record(Rec(ts, provider: "claude", model: "opus"));
        store.Record(Rec(ts, provider: "minimax", model: "minimax-m1"));

        var analytics = NewAnalytics(store);
        var day = new DateOnly(2026, 7, 20);
        var filter = new SpendFilter();

        var byProvider = analytics.Pivot("provider", day, day, filter);
        Assert.Equal(SpendCostTiers.Expensive, byProvider.Single(n => n.Key == "claude").Tier);
        Assert.Equal(SpendCostTiers.Cheap, byProvider.Single(n => n.Key == "minimax").Tier);

        var byModel = analytics.Pivot("model", day, day, filter);
        Assert.Equal(SpendCostTiers.Expensive, byModel.Single(n => n.Key == "opus").Tier);
        Assert.Equal(SpendCostTiers.Cheap, byModel.Single(n => n.Key == "minimax-m1").Tier);

        Assert.All(analytics.Pivot("user", day, day, filter), n => Assert.Null(n.Tier));
        Assert.All(analytics.Pivot("source", day, day, filter), n => Assert.Null(n.Tier));
    }

    [Fact]
    public void Tier_Хода_ЗаполненВсегда()
    {
        var store = NewStore();
        var ts = new DateTime(2026, 7, 20, 12, 0, 0, DateTimeKind.Utc);
        store.Record(Rec(ts, provider: "claude", model: "opus"));
        store.Record(Rec(ts, provider: "minimax", model: "minimax-m1"));

        var analytics = NewAnalytics(store);
        var day = new DateOnly(2026, 7, 20);
        var overview = analytics.Overview(day, day, new SpendFilter(), allUsers: false, currentUserId: "u1");

        Assert.All(overview.TopTurns, t => Assert.Contains(t.Tier,
            new[] { SpendCostTiers.Free, SpendCostTiers.Cheap, SpendCostTiers.Expensive }));
    }

    // --- классификация источников ---

    [Fact]
    public void Sources_БесплатныеИПодписки()
    {
        Assert.True(SpendSources.IsFree("ollama", "qwen3:14b"));
        // Платный провайдер через прямой адаптер не бесплатен (решение Андрея, 26.10):
        // бесплатен «любой *-direct» было ошибкой источника
        Assert.False(SpendSources.IsFree("openrouter-direct", "any"));
        Assert.False(SpendSources.IsFree("minimax-direct", "MiniMax-M3"));
        Assert.False(SpendSources.IsFree("kimi-direct", "kimi-k3"));
        // freellm* — бесплатный и с «-direct», и без него (развилка Р1(а)): локальный прокси
        Assert.True(SpendSources.IsFree("freellmapi-direct", "auto:fast"));
        Assert.True(SpendSources.IsFree("freellmapi", "auto:fast"));
        Assert.True(SpendSources.IsFree("openrouter", "nvidia/nemotron:free"));
        Assert.False(SpendSources.IsFree("claude", "opus"));
        // Локальный провайдер из конфига (IsLocal) — бесплатный по определению: ключ
        // машин-specific ("local-qwen"), в перечислении его нет
        Assert.True(SpendSources.IsFree("local-qwen", "qwen3.8-27b", isLocal: true));
        Assert.False(SpendSources.IsFree("local-qwen", "qwen3.8-27b"));
        Assert.Equal("claude", SpendSources.NormalizeProvider("sub-work"));
        Assert.Equal("deepseek", SpendSources.NormalizeProvider("deepseek"));
    }

    // --- дедуп backfill ---

    [Fact]
    public void Record_ПовторныйIdОтбрасывается_ИПослеПерезапускаСтора()
    {
        var ts = new DateTime(2026, 7, 20, 12, 0, 0, DateTimeKind.Utc);
        var day = new DateOnly(2026, 7, 20);
        var id = SpendMaintenanceService.BackfillId("cs1", 0);

        var store = NewStore();
        store.Record(Rec(ts, id: id));
        store.Record(Rec(ts, id: id));
        Assert.Single(store.DetailsBetween(day, day));

        // Перезапуск: индекс Id восстанавливается из jsonl — дубль не проходит и после него
        var reloaded = NewStore();
        reloaded.Record(Rec(ts, id: id));
        Assert.Single(reloaded.DetailsBetween(day, day));

        // Запись с другим Id — не дубль
        reloaded.Record(Rec(ts, id: SpendMaintenanceService.BackfillId("cs1", 1)));
        Assert.Equal(2, reloaded.DetailsBetween(day, day).Count);
    }

    [Fact]
    public void BackfillId_ДетерминированПоСессииИИндексу()
    {
        Assert.Equal(SpendMaintenanceService.BackfillId("cs1", 0), SpendMaintenanceService.BackfillId("cs1", 0));
        Assert.NotEqual(SpendMaintenanceService.BackfillId("cs1", 0), SpendMaintenanceService.BackfillId("cs1", 1));
        Assert.NotEqual(SpendMaintenanceService.BackfillId("cs1", 0), SpendMaintenanceService.BackfillId("cs2", 0));
        // Формат совпадает с Guid.ToString("N") живых записей
        Assert.Equal(32, SpendMaintenanceService.BackfillId("cs1", 0).Length);
    }

    [Fact]
    public void Spread_РаспределяетВнутриИнтервалаИНеПересекаетT0()
    {
        var from = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);
        var to = new DateTime(2026, 7, 10, 0, 0, 0, DateTimeKind.Utc);

        var first = SpendMaintenanceService.Spread(from, to, 0, 3);
        var last = SpendMaintenanceService.Spread(from, to, 2, 3);
        Assert.True(first > from && last < to);
        Assert.True(first < last);

        // Вырожденные случаи: пустой интервал → from (и гарантированно < t0 при t0 > from)
        Assert.Equal(from, SpendMaintenanceService.Spread(from, from, 0, 5));
    }
}
