using System.Text.Json;
using ClaudeHomeServer.Services.Llm;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Разбор ответа шлюза bailian консоли Alibaba Model Studio (Coding Plan / Token Plan).
// Эндпоинт недокументированный и доступен только web-сессии консоли (cookie, не ApiKey);
// контракт фиксируем тестами по реальному ответу — снимок живого запроса 07.08.2026
// (cookie в фикстуре не участвует, только тело).
public class ProviderBalanceAlibabaTests
{
    // Живой payload (снимок 07.08.2026): глубокая обёртка data.DataV2.data, единственное
    // окно — недельное. per1WeekPercentage = 0.8553… — доля ИЗРАСХОДОВАННОГО (значит остаток ~14.47%)
    private const string RealPayload = """
        {"code":"200","data":{"DataV2":{"data":{"code":"SUCCESS","data":{
        "per1WeekResetTime":1786434900000,
        "per1WeekPercentage":0.85530437880474
        }}}}}
        """;

    private static ProviderBalance? Parse(string json) =>
        ProviderBalanceService.ParseAlibabaUsage(JsonDocument.Parse(json).RootElement);

    [Fact]
    public void ЖивойОтвет_ЕдинственноеОкно_Остаток14_47()
    {
        var b = Parse(RealPayload);

        b.Should().NotBeNull();
        b!.Available.Should().BeTrue();
        b.Currency.Should().Be("%");
        // per1WeekPercentage — ИЗРАСХОДОВАНО 0.855…, показываем остаток: (1 − 0.8553)·100 ≈ 14.47
        b.TotalBalance.Should().Be("14.47");
        b.ResetsAt.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(1786434900000).UtcDateTime);
        b.TrackHistory.Should().BeTrue(); // расходное окно — точки в историю пишем
    }

    [Fact]
    public void ЖивойОтвет_ОдноОкно_НеделяPercent()
    {
        var b = Parse(RealPayload);

        b!.Windows.Should().HaveCount(1);
        b.Windows![0].Label.Should().Be("Неделя");
        b.Windows[0].Value.Should().Be("14.47");
        b.Windows[0].Unit.Should().Be("percent");
        b.Windows[0].ResetsAt.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(1786434900000).UtcDateTime);
    }

    [Fact]
    public void НаправлениеПроцента_ИзрасходованоВОстаток()
    {
        // per1WeekPercentage — ИЗРАСХОДОВАНО (как у соседних percent-парсеров обращаем в остаток).
        // 0.5 = потрачена половина → остаток 50 (НЕ 50 как расход и НЕ 50 как уже-остаток-совпадение:
        // крайние случаи ниже отличают направление однозначно)
        const string half = """
            {"data":{"DataV2":{"data":{"code":"SUCCESS","data":{
            "per1WeekResetTime":1786434900000,"per1WeekPercentage":0.5}}}}}
            """;
        Parse(half)!.TotalBalance.Should().Be("50");

        // Израсходовано всё → остаток 0; израсходовано ничего → остаток 100
        const string spent = """
            {"data":{"DataV2":{"data":{"code":"SUCCESS","data":{
            "per1WeekResetTime":1786434900000,"per1WeekPercentage":1}}}}}
            """;
        Parse(spent)!.TotalBalance.Should().Be("0");

        const string full = """
            {"data":{"DataV2":{"data":{"code":"SUCCESS","data":{
            "per1WeekResetTime":1786434900000,"per1WeekPercentage":0}}}}}
            """;
        Parse(full)!.TotalBalance.Should().Be("100");
    }

    [Fact]
    public void ПроцентВнеДиапазона_Обрезается()
    {
        // > 1 (израсходовано больше 100% — шлюз так умеет на границе сброса) → остаток clamp в 0
        const string over = """
            {"data":{"DataV2":{"data":{"code":"SUCCESS","data":{
            "per1WeekResetTime":1786434900000,"per1WeekPercentage":1.5}}}}}
            """;
        Parse(over)!.TotalBalance.Should().Be("0");
    }

    [Fact]
    public void ЧислоСтрокой_Разбирается()
    {
        // per1WeekPercentage может прийти строкой
        const string json = """
            {"data":{"DataV2":{"data":{"code":"SUCCESS","data":{
            "per1WeekResetTime":"1786434900000","per1WeekPercentage":"0.85530437880474"}}}}}
            """;
        var b = Parse(json);

        b!.TotalBalance.Should().Be("14.47");
        b.ResetsAt.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(1786434900000).UtcDateTime);
    }

    [Fact]
    public void МусорВПроценте_Null()
    {
        // ReadNumber не падает на bool/object — разборщик обязан переживать мусор от шлюза
        const string json = """
            {"data":{"DataV2":{"data":{"code":"SUCCESS","data":{
            "per1WeekResetTime":1786434900000,"per1WeekPercentage":true}}}}}
            """;
        Parse(json).Should().BeNull();
    }

    [Fact]
    public void НетПроцента_Null()
    {
        // code SUCCESS, но per1WeekPercentage отсутствует (провайдер сменил формат) — квоты нет
        const string json = """
            {"data":{"DataV2":{"data":{"code":"SUCCESS","data":{
            "per1WeekResetTime":1786434900000}}}}}
            """;
        Parse(json).Should().BeNull();
    }

    [Fact]
    public void КривойResetTime_ОкноЖивётБезСброса()
    {
        // Непонятный reset не должен гасить окно при полностью разобранном проценте —
        // гасится только ResetsAt (см. ReadUnixTime), не весь баланс
        const string json = """
            {"data":{"DataV2":{"data":{"code":"SUCCESS","data":{
            "per1WeekResetTime":"давно-давно","per1WeekPercentage":0.2}}}}}
            """;
        var b = Parse(json);

        b!.TotalBalance.Should().Be("80");
        b.Windows!.Should().HaveCount(1);
        b.Windows![0].ResetsAt.Should().BeNull();
    }

    [Fact]
    public void SuccessFalse_Null()
    {
        const string json = """
            {"data":{"DataV2":{"data":{"success":false,"code":"SUCCESS","data":{
            "per1WeekResetTime":1786434900000,"per1WeekPercentage":0.1}}}}}
            """;
        Parse(json).Should().BeNull();
    }

    [Fact]
    public void CodeНеУспех_Null()
    {
        // Шлюз отвечает 200 даже на отказ — code != SUCCESS = нет квоты
        const string json = """
            {"data":{"DataV2":{"data":{"code":"SYSTEM_ERROR","data":{
            "per1WeekResetTime":1786434900000,"per1WeekPercentage":0.1}}}}}
            """;
        Parse(json).Should().BeNull();
    }

    [Fact]
    public void NotAuthorised_Null()
    {
        // Протухшая/чужая сессия: отказ авторизации приходит как code
        const string json = """
            {"data":{"DataV2":{"data":{"code":"BailianGateway.Workspace.NotAuthorised","data":{}}}}}
            """;
        Parse(json).Should().BeNull();
    }

    [Fact]
    public void СломанаОбёртка_НетDataV2_Null()
    {
        // Глубокая обёртка нарушена — до per1Week* не добраться
        Parse("""{"code":"200","data":{}}""").Should().BeNull();
        Parse("""{"data":{"DataV2":{}}}""").Should().BeNull();
        Parse("""{}""").Should().BeNull();
    }

    [Fact]
    public void ОтветBadRequest_БезDataV2_Null()
    {
        // Фикстура «A» из бага: шлюз отвергает тело без cornerstoneParam — отвечает 200 с
        // success:false и errorCode «Bad Request», обёртки DataV2 нет → квоты нет
        const string json = """
            {"code":"200","data":{"success":false,"errorCode":"Bad Request","errorMsg":"Bad Request"}}
            """;
        Parse(json).Should().BeNull();
    }

    [Fact]
    public void ТелоЗапроса_СодержитНепустойCornerstoneParam()
    {
        // BuildAlibabaParams собирает form-поле params: {Api, V, Data:{cornerstoneParam}}.
        // cornerstoneParam — обязательный конверт шлюза (без него 200 Bad Request), фиксируем его состав
        var csp = JsonDocument.Parse(ProviderBalanceService.BuildAlibabaParams()).RootElement
            .GetProperty("Data").GetProperty("cornerstoneParam");

        csp.ValueKind.Should().Be(JsonValueKind.Object);
        csp.GetProperty("protocol").GetString().Should().Be("V2");
        csp.GetProperty("console").GetString().Should().Be("ONE_CONSOLE");
        csp.GetProperty("productCode").GetString().Should().Be("p_efm");
        csp.GetProperty("domain").GetString().Should().Be("modelstudio.console.alibabacloud.com");
        // consoleSite дословно с опечаткой шлюза (ALBABACLOUD) — иначе Bad Request
        csp.GetProperty("consoleSite").GetString().Should().Be("MODELSTUDIO_ALBABACLOUD");
        csp.GetProperty("xsp_lang").GetString().Should().Be("en-US");
        csp.EnumerateObject().Should().HaveCount(6); // непустой конверт, ровно нужные поля
    }

    [Fact]
    public void ПричинаОтказаШлюза_BadRequest_ИзТела()
    {
        // Фикстура «A»: success:false, errorCode/errorMsg лежат в data, обёртки DataV2 нет →
        // достаём «Bad Request: Bad Request» для нейтрального сообщения в лог (не «cookie протух»)
        const string json = """
            {"code":"200","data":{"success":false,"errorCode":"Bad Request","errorMsg":"Bad Request"}}
            """;
        ProviderBalanceService.AlibabaDeclineReason(JsonDocument.Parse(json).RootElement)
            .Should().Be("Bad Request: Bad Request");
    }

    [Fact]
    public void ПричинаОтказаШлюза_НетErrorCode_Null()
    {
        // Нет ни errorCode, ни DataV2 — явной причины в теле нет, лог скажет нейтрально «ответ без квоты»
        ProviderBalanceService.AlibabaDeclineReason(JsonDocument.Parse("""{"code":"200","data":{}}""").RootElement)
            .Should().BeNull();
    }
}
