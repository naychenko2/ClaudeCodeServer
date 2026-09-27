using System.Net;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Services.Spend;
using ClaudeHomeServer.Tests.Helpers;
using ClaudeHomeServer.Tests.ImageEditor.Fakes;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.ImageEditor.Spending;

// Траты редактора картинок лежат в общем учёте (ADR-017 §4, ревью M1): настоящий исполнитель
// пишет через ISpendCollector в SpendStore, а сводка «Модели и расход» (/api/spend) показывает
// владельцу только его траты, админу — всех с разбивкой по пользователям
public class ImageEditSpendVisibilityTests : IDisposable
{
    private readonly TestWebApplicationFactory _factory = new()
    {
        ExtraServices = services =>
        {
            services.AddSingleton<IImageEditor>(new PaidEditor("higgsfield", ImageEditPriceUnits.Credits, 1.5));
            // У fal прайса нет: сумма станет известна только из ответа поставщика
            services.AddSingleton<IImageEditor>(new PaidEditor("fal", ImageEditPriceUnits.Usd, null));
        },
    };

    public void Dispose()
    {
        _factory.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Траты_ВидныВСводке_ВладельцуСвои_АдминуВсе()
    {
        var users = _factory.Services.GetRequiredService<UserStore>();
        var adminId = users.FindByUsername(TestWebApplicationFactory.TestUsername)!.Id;
        var userId = users.FindByUsername(TestWebApplicationFactory.SecondUsername)!.Id;
        var jobs = _factory.Services.GetRequiredService<IImageEditJobs>();

        await RunAsync(jobs, userId, "higgsfield", count: 2);
        await RunAsync(jobs, adminId, "fal", count: 1);

        var user = _factory.CreateAuthenticatedClient(TestWebApplicationFactory.SecondUsername,
            TestWebApplicationFactory.SecondPassword);
        var admin = _factory.CreateAuthenticatedClient();

        // Пользователь видит только свою трату Higgsfield — чужой fal админа в его сводке нет
        var own = await Nodes(user, "groupBy=source");
        own.Should().ContainSingle();
        own[0].GetProperty("key").GetString().Should().Be(SpendSources.Higgsfield);
        own[0].GetProperty("falGenerations").GetInt32().Should().Be(2);
        (await user.GetAsync("/api/spend/pivot?groupBy=source&scope=all")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await user.GetAsync($"/api/spend/pivot?groupBy=source&user={adminId}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // Админ в своей сводке видит только свои траты, во «всех» — разбивку по пользователям
        (await Nodes(admin, "groupBy=source")).Select(n => n.GetProperty("key").GetString())
            .Should().Equal(SpendSources.Fal);
        var byUser = await Nodes(admin, "groupBy=user&scope=all");
        byUser.Should().Contain(n => n.GetProperty("key").GetString() == userId
            && n.GetProperty("falGenerations").GetInt32() == 2);
        byUser.Should().Contain(n => n.GetProperty("key").GetString() == adminId);

        // Суммы в своих валютах: кредиты Higgsfield по котировке, доллары fal — догоняющей записью
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var records = _factory.Services.GetRequiredService<SpendStore>().DetailsBetween(today.AddDays(-1), today.AddDays(1))
            .Where(r => r.Label == ImageEditJobService.SpendLabel).ToList();
        records.Should().ContainSingle(r => r.OwnerId == userId && r.CostCredits == 3.0 && r.CostUsd == null);
        records.Where(r => r.OwnerId == adminId).Sum(r => r.CostUsd ?? 0).Should().Be(0.1);
        records.Where(r => r.OwnerId == adminId).Should().OnlyContain(r => r.CostCredits == null);
    }

    private static async Task RunAsync(IImageEditJobs jobs, string ownerId, string provider, int count)
    {
        var quote = await jobs.QuoteAsync(ownerId, "p1",
            new ImageEditQuoteRequest(provider, "auto", EditMode.Fast, ImageEditOp.Edit, count, false, 0, false, null, null), default);
        quote.Value.Should().NotBeNull(quote.Error);
        var started = await jobs.StartAsync(ownerId, "p1", new ImageEditJobInput(quote.Value!.QuoteId, "убрать провод", null,
            new ImageBytes(TestImages.Png(4, 4), "image/png"), null, null, [], "images/hero.png"), default);
        started.Value.Should().NotBeNull(started.Error);
        for (var i = 0; i < 500; i++)
        {
            if (jobs.Get(ownerId, "p1", started.Value!.JobId)!.Status == ImageEditJobStatus.Completed) return;
            await Task.Delay(10);
        }
        throw new TimeoutException("задача не завершилась");
    }

    private static async Task<List<JsonElement>> Nodes(HttpClient client, string query)
    {
        var resp = await client.GetAsync("/api/spend/pivot?" + query);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
        return [.. body.GetProperty("nodes").EnumerateArray()];
    }

    // Платный драйвер: принимает задачу и возвращает столько вариантов, сколько заказано;
    // без прайса сообщает фактическую цену $0.10 в ответе
    private sealed class PaidEditor(string key, string unit, double? price) : IImageEditor
    {
        private readonly ImageEditModelInfo _model = new("m-" + key, "M",
            new ImageEditCaps([ImageEditOp.Edit], MaskSupport.AsReference, 3, 4, true),
            price is { } p ? new ImageEditPriceHint(p, unit, "image") : null);

        public string Key => key;
        public string Label => key;
        public string PriceUnit => unit;
        public bool Enabled => true;
        public IReadOnlyList<ImageEditModelInfo> Models => [_model];
        public ImageEditModelInfo? PickModel(ImageEditOp op, EditMode mode, EditTraits traits) => _model;

        public Task<ImageEditResult> RunAsync(ImageEditRequest req, IProgress<EditProgress> progress, CancellationToken ct)
        {
            progress.Report(new EditProgress(EditStage.Running));
            var images = Enumerable.Range(0, req.Count).Select(_ => new EditedImage(TestImages.Png(2, 2), "image/png")).ToList();
            var actual = price is null ? new EditCost(0.1, unit) : null;
            return Task.FromResult(new ImageEditResult(EditOutcome.Ok, images, actual, true, "r", null));
        }

        public Task<bool> CancelRemoteAsync(string remoteId, CancellationToken ct) => Task.FromResult(false);
    }
}
