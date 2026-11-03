using ClaudeHomeServer.Services.Access;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services.Access;

/// <summary>
/// S2d-6b-1 (разрез 4bc3d04f §1 путь 19, перепостановка после блокера): вложения ПРИВАТНОГО
/// проектного чата пишутся в .cc-attachments/private/{автор}/… — принадлежность определяется
/// по пути, без каталога сессий. Build — формат записи (тот же, что у AttachmentPath, плюс
/// средний сегмент автора); IsForeign — гейт чтения для 6b-2/6b-3: чужое private → true,
/// своё / общее / вне .cc-attachments → false (поведение вызывающих не меняется).
/// Обходы («..», «./», регистр, обратные слеши, %2F/%2E) сравниваются ПОСЛЕ нормализации:
/// для чужого private они не дают лжи.
/// </summary>
public sealed class PrivateAttachmentsTests
{
    // id той же формы, что в сторе (Guid «N»)
    private const string Me = "a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6";
    private const string Other = "0f1e2d3c4b5a69788796a5b4c3d2e1f0";

    private static string GuidN() => Guid.NewGuid().ToString("N");

    // --- Build: формат записи ---

    [Fact]
    public void Build_ТотЖеФорматЧтоУОбщихВложений_ПлюсСреднийСегментАвтора()
    {
        var guid = Guid.Parse(GuidN());
        PrivateAttachments.Build(Me, guid, "отчёт.txt")
            .Should().Be($".cc-attachments/private/{Me}/{guid:N}/отчёт.txt");
        // Имя срезается к basename, как у AttachmentPath: path-сегменты имени («../evil») не путь
        PrivateAttachments.Build(Me, guid, "../evil").Should().Be($".cc-attachments/private/{Me}/{guid:N}/evil");
    }

    [Theory]
    [InlineData("")]
    [InlineData("bad id")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("a*b")]
    [InlineData("a.b")]
    [InlineData("aéb")]
    public void Build_НепрошедшаяСанацияId_ОтказЗаписи(string authorId) =>
        PrivateAttachments.Build(authorId, Guid.NewGuid(), "f.txt").Should().BeNull();

    [Fact]
    public void Build_IdСверх128Символов_ОтказЗаписи() =>
        PrivateAttachments.Build(new string('a', 129), Guid.NewGuid(), "f.txt").Should().BeNull();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("sub/")]
    public void Build_ИмяНеИмяФайла_ОтказЗаписи(string? name) =>
        PrivateAttachments.Build(Me, Guid.NewGuid(), name).Should().BeNull();

    // --- IsForeign: чужое private ---

    /// <Named мутации: «IsForeign всегда false» краснит этот тест (S2d-6b-1 c3).>
    [Fact]
    public void IsForeign_ЧужоеPrivate_Истинно_ОбходыНеДаютЛжи()
    {
        var g = GuidN();
        PrivateAttachments.IsForeign($".cc-attachments/private/{Other}/{g}/отчёт.txt", Me).Should().BeTrue();
        // тот же путь, записанный %-экранированно (%2F, %2E)
        PrivateAttachments.IsForeign($".cc-attachments%2Fprivate%2F{Other}%2F{g}%2Fотчёт.txt", Me).Should().BeTrue();
        PrivateAttachments.IsForeign($"%2Ecc-attachments%2Fprivate%2F{Other}%2F{g}%2Fотчёт.txt", Me).Should().BeTrue();
        // обратные и смешанные слеши
        PrivateAttachments.IsForeign($".cc-attachments\\private\\{Other}\\{g}\\отчёт.txt", Me).Should().BeTrue();
        PrivateAttachments.IsForeign($".cc-attachments/private\\{Other}/{g}/отчёт.txt", Me).Should().BeTrue();
        // регистр имён каталогов: на ОС с регистром несуществующий каталог всё равно даст 404
        // ниже по SafeJoin, но чужое private здесь не должно быть принято своим
        PrivateAttachments.IsForeign($".CC-ATTACHMENTS/PRIVATE/{Other}/{g}/отчёт.txt", Me).Should().BeTrue();
        // «.» и пустые сегменты
        PrivateAttachments.IsForeign($"././/./.cc-attachments/./private/{Other}/{g}/отчёт.txt", Me).Should().BeTrue();
        // «..» внутри пути: после попопа указывает на чужое private
        PrivateAttachments.IsForeign($".cc-attachments/private/{Other}/x/../{g}/отчёт.txt", Me).Should().BeTrue();
        PrivateAttachments.IsForeign($".cc-attachments/private/../private/{Other}/{g}/отчёт.txt", Me).Should().BeTrue();
        // выход за корень: его отклонит SafeJoin у вызывающего, но чужое private
        // не должно быть названо своим и здесь
        PrivateAttachments.IsForeign($"../.cc-attachments/private/{Other}/{g}/отчёт.txt", Me).Should().BeTrue();
    }

    [Fact]
    public void IsForeign_СвоёPrivate_Ложь_ВТЧЧерезОбходыИРегистр()
    {
        var g = GuidN();
        PrivateAttachments.IsForeign($".cc-attachments/private/{Me}/{g}/отчёт.txt", Me).Should().BeFalse();
        PrivateAttachments.IsForeign($".CC-ATTACHMENTS/PRIVATE/{Me}/{g}/отчёт.txt", Me).Should().BeFalse();
        PrivateAttachments.IsForeign($".cc-attachments/private/{Me}/x/../{g}/отчёт.txt", Me).Should().BeFalse();
        // директория чужого автора без файла — тоже чужое private (не показывать)
        PrivateAttachments.IsForeign($".cc-attachments/private/{Other}", Me).Should().BeTrue();
    }

    [Fact]
    public void IsForeign_ОбщиеВложенияИПутиВне_Ложь()
    {
        var g = GuidN();
        // прежняя раскладка общих чатов — поведение вызывающих не меняется
        PrivateAttachments.IsForeign($".cc-attachments/{g}/отчёт.txt", Me).Should().BeFalse();
        // подкаталоги .cc-attachments, не private (test-runs, build-runs, local-media, playwright)
        PrivateAttachments.IsForeign($".cc-attachments/test-runs/{g}/console.log", Me).Should().BeFalse();
        PrivateAttachments.IsForeign(".cc-attachments/local-media/2026-11-03/lm-1.png", Me).Should().BeFalse();
        PrivateAttachments.IsForeign(".cc-attachments/playwright/shot.png", Me).Should().BeFalse();
        // сам корень private без автора — не путь вложения
        PrivateAttachments.IsForeign(".cc-attachments/private", Me).Should().BeFalse();
        // пути вне .cc-attachments
        PrivateAttachments.IsForeign("docs/a.md", Me).Should().BeFalse();
        PrivateAttachments.IsForeign("", Me).Should().BeFalse();
        PrivateAttachments.IsForeign(null, Me).Should().BeFalse();
    }

    [Fact]
    public void IsForeign_БезИдентичности_ЛюбоеПриватноеЧужое()
    {
        var g = GuidN();
        // пустой вызывающий — «своего» нет: fail-closed, как SessionAccess.CanSee
        PrivateAttachments.IsForeign($".cc-attachments/private/{Me}/{g}/файл.txt", null).Should().BeTrue();
        PrivateAttachments.IsForeign($".cc-attachments/private/{Other}/{g}/файл.txt", "").Should().BeTrue();
        // общее без идентичности — как раньше
        PrivateAttachments.IsForeign($".cc-attachments/{g}/файл.txt", null).Should().BeFalse();
    }
}
