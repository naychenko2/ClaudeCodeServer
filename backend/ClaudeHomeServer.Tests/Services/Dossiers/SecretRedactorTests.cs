using ClaudeHomeServer.Services.Dossiers;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services.Dossiers;

// SecretRedactor — чистая статическая логика (ADR-004 §3): точные значения инстанса,
// PEM/SSH-блоки целиком, известные форматы токенов, присваивания похожие на секрет.
public class SecretRedactorTests
{
    [Fact]
    public void ТочноеЗначение_ДлиннееПорога_Вычищается()
    {
        // Провайдерский ключ без узнаваемого префикса ("AWS-ключ без префикса") —
        // ловится ТОЛЬКО точным совпадением, ни одна regex-форма его не узнает
        const string providerKey = "custom-provider-key-9f8e7d6c5b4a3210";
        var text = $"использую ключ {providerKey} для деплоя";

        var redacted = SecretRedactor.Redact(text, [providerKey]);

        redacted.Should().NotContain(providerKey);
        redacted.Should().Contain("[REDACTED:instance-secret]");
    }

    [Fact]
    public void OAuthТокенПодписки_ВычищаетсяТочнымСовпадением()
    {
        const string oauthToken = "sk-oauth-claude-subscription-token-abcdef123456";
        var prompt = $"CLAUDE_CODE_OAUTH_TOKEN={oauthToken} — вот значение из env";

        var redacted = SecretRedactor.Redact(prompt, [oauthToken]);

        redacted.Should().NotContain(oauthToken);
    }

    [Fact]
    public void Jwt_ВычищаетсяРегуляркой()
    {
        var jwt = "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dQw4w9WgXcQ_rDolTZmLd-2N5tqi";
        var text = $"токен: {jwt}";

        var redacted = SecretRedactor.Redact(text);

        redacted.Should().NotContain(jwt);
        redacted.Should().Contain("[REDACTED:jwt]");
    }

    [Fact]
    public void PemБлок_ВычищаетсяЦеликом_НеПострочно()
    {
        var pem = "-----BEGIN RSA PRIVATE KEY-----\n" +
                  "MIIEpAIBAAKCAQEA1c7+9z5Pad7OejecsQ0bu3aumc4jr5oM3Y2t9RwlGpUOOOOO\n" +
                  "OOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOO\n" +
                  "-----END RSA PRIVATE KEY-----";
        var text = "агент вставил ключ при отладке доступов:\n" + pem + "\nвот и всё";

        var redacted = SecretRedactor.Redact(text);

        redacted.Should().NotContain("MIIEpAIBAAKCAQEA1c7");
        redacted.Should().NotContain("-----BEGIN RSA PRIVATE KEY-----");
        redacted.Should().Contain("[REDACTED:private-key]");
        redacted.Should().Contain("вот и всё");   // текст вокруг блока не пострадал
    }

    [Fact]
    public void ИзвестныйПрефиксПровайдера_Anthropic_Вычищается()
    {
        var key = "sk-ant-api03-" + new string('a', 30);
        var text = $"ApiKey: {key}";

        var redacted = SecretRedactor.Redact(text);

        redacted.Should().NotContain(key);
    }

    [Fact]
    public void ПустойApiKey_НеРежетТекст()
    {
        var text = "ApiKey настроен для DeepSeek, но пока пустой — используем дефолт claude";

        var redacted = SecretRedactor.Redact(text, [""]);

        redacted.Should().Be(text);
    }

    [Fact]
    public void КороткоеЗначение_НижеПорога_НеРежетПосторонийТекст()
    {
        // 4-символьный ApiKey из тестового конфига — иначе подстрока "test" изрешетила бы
        // половину обычного текста
        var text = "test план на завтра: протестировать фичу и написать тест-кейсы";

        var redacted = SecretRedactor.Redact(text, ["test"]);

        redacted.Should().Be(text);
    }

    [Fact]
    public void ПустойТекст_НеПадает()
    {
        SecretRedactor.Redact(null).Should().Be("");
        SecretRedactor.Redact("").Should().Be("");
    }

    [Fact]
    public void ПрисваиваниеПохожееНаСекрет_ВычищаетсяИзDiff()
    {
        var text = "diff:\n+ api_key = \"raw-value-from-diff\"\n+ другая строка";

        var redacted = SecretRedactor.Redact(text);

        redacted.Should().NotContain("raw-value-from-diff");
        redacted.Should().Contain("[REDACTED:secret]");
    }

    // Over-redaction (ревью Глеба): присваивающий паттерн маскировал очевидные заглушки —
    // пустые кавычки, null/true/false, маску *** и уже-замаскированное. Это визуальный мусор
    // без пользы для защиты. Реальный секрет при этом режется по-прежнему — fail-closed.

    [Theory]
    [InlineData("password = \"\"")]
    [InlineData("token: null")]
    [InlineData("secret = true")]
    [InlineData("api_key = false")]
    [InlineData("token = undefined")]
    [InlineData("password: <your-key>")]
    [InlineData("secret = ***")]
    public void Присваивание_ОчевиднаяЗаглушка_НеРежется(string line)
    {
        SecretRedactor.Redact(line).Should().Be(line,
            "пустое/null/булев/маска/плейсхолдер — не секрет, маска здесь только мусорит");
    }

    [Fact]
    public void Присваивание_УжеЗамаскированноеЗначение_НеДёргаетсяПовторно()
    {
        // KnownPrefix уже превратил токен в [REDACTED:token]; присваивающий проход не должен
        // пере-редactить его в [REDACTED:secret], теряя более точную метку
        var text = "api_key: sk-ant-api03-" + new string('a', 30);

        var redacted = SecretRedactor.Redact(text);

        redacted.Should().Contain("[REDACTED:token]");
        redacted.Should().NotContain("[REDACTED:secret]");
    }

    [Fact]
    public void Присваивание_РеальныйТокен_ВсёЕщеРежется_FailClosed()
    {
        // Гарантия защиты не ослаблена: непрозрачное значение-кандидат маскируется как раньше
        var text = "password = raw-deploy-key-9f8e7d6c5b";

        var redacted = SecretRedactor.Redact(text);

        redacted.Should().NotContain("raw-deploy-key-9f8e7d6c5b");
        redacted.Should().Contain("[REDACTED:secret]");
    }

    [Fact]
    public void ПорядокПрименения_ТочноеЗначениеПерекрываетКороткийRegexШум()
    {
        // Значение длиннее AWS access key id, но одного формата с ним — точное совпадение
        // должно сработать раньше и не оставить хвост для regex-прохода
        const string secret = "AKIAABCDEFGHIJKLMNOP";
        var text = $"key={secret}";

        var redacted = SecretRedactor.Redact(text, [secret]);

        redacted.Should().NotContain(secret);
    }
}
