using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Dossiers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace ClaudeHomeServer.Tests.Services.Dossiers;

// DossierStore (ADR-004 §4): JSON-стор паспортов по образцу team-memory.json. Покрываем
// ключ уникальности и идемпотентность (commitSha + supersededSha), переякорение при squash,
// потолок MaxEntries с вытеснением в архивный JSONL, изоляцию per-owner и каскады удаления.
// Без KnowledgeService — Available=false, синк в Dify не планируется (QueueSync no-op).
public class DossierStoreTests : IDisposable
{
    private readonly string _temp;
    private readonly DossierStore _store;
    private const string Owner = "ownerA";
    private const string Project = "project1";

    public DossierStoreTests()
    {
        _temp = Path.Combine(Path.GetTempPath(), "dossier_store_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_temp);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(_temp, "projects.json"),
            ["Dossiers:MaxEntries"] = "2",
        }).Build();
        _store = new DossierStore(config, null);
    }

    public void Dispose()
    {
        try { Directory.Delete(_temp, recursive: true); } catch { /* best-effort */ }
    }

    private static ChangeDossier New(string sha, string subject = "subj",
        string? owner = Owner, string? projectId = Project, DateTimeOffset? committedAt = null) => new()
    {
        OwnerId = owner!,
        ProjectId = projectId!,
        CommitSha = sha,
        CommitSubject = subject,
        CommittedAt = committedAt ?? DateTimeOffset.UtcNow,
        Why = "почему",
        Decisions = ["решили так"],
    };

    [Fact]
    public void Add_ЗатемList_ВозвращаетЗапись()
    {
        _store.Add(New("aaaa1111"));

        _store.List(Owner, Project).Should().ContainSingle(d => d.CommitSha == "aaaa1111");
    }

    [Fact]
    public void ИзоляцияВладельцев_ПаспортОдногоНеВиденДругому()
    {
        _store.Add(New("aaaa1111", owner: "ownerA"));
        _store.Add(New("bbbb2222", owner: "ownerB"));

        _store.List("ownerA", Project).Should().ContainSingle(d => d.CommitSha == "aaaa1111");
        _store.List("ownerB", Project).Should().ContainSingle(d => d.CommitSha == "bbbb2222");
        // Один и тот же projectId у разных владельцев — не пересекаются
        _store.List("ownerA", Project).Should().NotContain(d => d.CommitSha == "bbbb2222");
    }

    [Fact]
    public void FindByAnyCommitSha_СовпадениеПоТекущемуSha()
    {
        _store.Add(New("aaaa1111"));

        _store.FindByAnyCommitSha(Owner, Project, "aaaa1111").Should().NotBeNull();
        _store.FindByAnyCommitSha(Owner, Project, "zzzz9999").Should().BeNull();
    }

    // Идемпотентность (§4, §7): после переякорения старый sha остаётся «известным» стору
    // через supersededSha — повторный захват того же коммита (например, после restore
    // устаревшего state.json) не должен завести дубль. FindByAnyCommitSha закрывает оба случая.
    [Fact]
    public void Переякорение_СтарыйShaНайдётсяЧерезSupersededSha()
    {
        var d = New("aaa");
        _store.Add(d);

        // CaptureService мутирует поля записи, затем зовёт Reanchor (та же ссылка живёт в _store)
        d.SupersededSha.Add(d.CommitSha);   // прежний sha «aaa»
        d.CommitSha = "bbb";
        _store.Reanchor(d);

        _store.FindByAnyCommitSha(Owner, Project, "aaa").Should().NotBeNull(
            "старый sha остаётся известным через supersededSha — повторный захват не должен дать дубль");
        _store.FindByAnyCommitSha(Owner, Project, "bbb").Should().NotBeNull();
        _store.List(Owner, Project).Should().ContainSingle("запись одна, не две");
    }

    [Fact]
    public void Find_ПоФайлуИСимволу()
    {
        var d = New("aaaa1111");
        d.Files = ["src/Program.cs", "README.md"];
        d.Symbols = ["ClaudeHomeServer.Services.Foo"];
        _store.Add(d);

        _store.Find(Owner, Project, file: "src/Program.cs").Should().ContainSingle();
        _store.Find(Owner, Project, file: "README.md").Should().ContainSingle();
        _store.Find(Owner, Project, file: "other.cs").Should().BeEmpty();
        _store.Find(Owner, Project, symbol: "ClaudeHomeServer.Services.Foo").Should().ContainSingle();
    }

    [Fact]
    public void Find_ПоКоммиту_УчитываетSupersededSha()
    {
        var d = New("aaa");
        _store.Add(d);
        d.SupersededSha.Add("aaa");
        d.CommitSha = "bbb";
        _store.Reanchor(d);

        _store.Find(Owner, Project, commit: "aaa").Should().ContainSingle("squash-предок всё ещё матчится");
        _store.Find(Owner, Project, commit: "bbb").Should().ContainSingle();
    }

    // Потолок Dossiers:MaxEntries (дефолт 5000, в тесте 2) — вытесняем самые старые в архивный
    // JSONL (append-only, из recall не участвует). Растёт линейно с коммитами — потолок нужен.
    [Fact]
    public void ПотолокMaxEntries_ВытесняетСтарыеВАрхив()
    {
        _store.Add(New("sha1", "первый", committedAt: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)));
        _store.Add(New("sha2", "второй", committedAt: new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero)));
        _store.Add(New("sha3", "третий", committedAt: new DateTimeOffset(2026, 1, 3, 0, 0, 0, TimeSpan.Zero)));

        // В основном сторе — потолок (2), самый старый ушёл
        var live = _store.List(Owner, Project);
        live.Should().HaveCount(2);
        live.Should().NotContain(d => d.CommitSha == "sha1", "самый старый вытеснен");

        // Архивный JSONL существует и содержит вытесненную запись
        var archivePath = Path.Combine(_temp, "dossiers", Owner, Project + ".archive.jsonl");
        File.Exists(archivePath).Should().BeTrue();
        var archiveText = File.ReadAllText(archivePath);
        archiveText.Should().Contain("sha1");
        archiveText.Should().NotContain("sha3");
    }

    // Ровно потолок — не вытесняем: граница count == max не должна запускать ротацию, а панель
    // не должна терять свежие записи, пока превышения нет. Архивного файла при этом не возникает.
    [Fact]
    public void ПотолокMaxEntries_РовноПотолок_НеВытесняет()
    {
        _store.Add(New("sha1", "первый", committedAt: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)));
        _store.Add(New("sha2", "второй", committedAt: new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero)));

        _store.List(Owner, Project).Should().HaveCount(2, "ровно потолок — обе записи на месте");

        var archivePath = Path.Combine(_temp, "dossiers", Owner, Project + ".archive.jsonl");
        File.Exists(archivePath).Should().BeFalse("превышения нет — архив не создаётся");
    }

    [Fact]
    public async Task DeleteProjectDossiersAsync_ЧиститСтор()
    {
        _store.Add(New("aaaa1111"));
        _store.Add(New("bbbb2222"));

        await _store.DeleteProjectDossiersAsync(Owner, Project);

        _store.List(Owner, Project).Should().BeEmpty();
    }

    [Fact]
    public void DeleteOwnerDossiers_ЧиститВсеПроектыВладельца()
    {
        _store.Add(New("aaaa1111", projectId: "p1"));
        _store.Add(New("bbbb2222", projectId: "p2"));

        _store.DeleteOwnerDossiers(Owner);

        _store.List(Owner, "p1").Should().BeEmpty();
        _store.List(Owner, "p2").Should().BeEmpty();
    }

    // CapturedAt (спринт Г): момент снятия паспорта. Own-записи получают UtcNow в Add,
    // импортированные (AddImportedRange) остаются null, старый JSON без поля читается без ошибок.
    [Fact]
    public void Add_ЗаполняетCapturedAt_ИПерсистит()
    {
        var before = DateTimeOffset.UtcNow;
        var added = _store.Add(New("aaaa1111"));
        var after = DateTimeOffset.UtcNow;

        added.CapturedAt.Should().NotBeNull();
        var captured = added.CapturedAt!.Value;
        captured.Should().BeOnOrAfter(before);
        captured.Should().BeOnOrBefore(after);

        // Персист: новый стор поверх того же data-каталога читает поле с диска
        var reloaded = new DossierStore(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_temp, "projects.json"),
                ["Dossiers:MaxEntries"] = "2",
            }).Build());
        reloaded.List(Owner, Project).Should().ContainSingle(d => d.CommitSha == "aaaa1111")
            .Which.CapturedAt.Should().NotBeNull();
    }

    [Fact]
    public void AddImportedRange_НеЗаполняетCapturedAt()
    {
        var imported = New("imp1");
        imported.Origin = DossierOrigin.Imported;
        imported.ImportedAuthor = "author";
        imported.ImportedFromBranch = "ccs/dossiers/v1";

        _store.AddImportedRange(Owner, Project, [imported]);

        _store.List(Owner, Project).Should().ContainSingle(d => d.CommitSha == "imp1")
            .Which.CapturedAt.Should().BeNull("момент захвата импортированной записи не известен");
    }

    [Fact]
    public void СтарыйJsonБезCapturedAt_ДесериализуетсяБезОшибок()
    {
        var dir = Path.Combine(_temp, "dossiers", Owner);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "legacy.json"),
            """
            [{"Id":"legacy1","OwnerId":"ownerA","ProjectId":"legacy","CommitSha":"old1111","CommitSubject":"старая запись","CommittedAt":"2026-01-01T00:00:00Z"}]
            """);

        var d = _store.List(Owner, "legacy").Should().ContainSingle(x => x.CommitSha == "old1111").Subject;
        d.CapturedAt.Should().BeNull();
    }
}
