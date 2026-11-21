using System.IO;
using System.Text;
using ClaudeHomeServer.Services.ChildMode;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services.ChildMode;

/// <summary>
/// Сторож функции путей И8 (Б10a, ADR-041 ред. 6 §12 «Папки и права»): таблица путей
/// против вердикта — минимум по одной строке на каждое правило × актор × операцию.
///
/// Пути таблицы синтетические: Canonical оставляет несуществующий путь путём (разыменование
/// ссылок трогает диск только когда элемент на диске есть), поэтому таблице ФС не нужна.
/// Реальная ФС нужна там, где правила живут на диске: развёртка симлинков (наружу,
/// в «Материалы»), неразобранный цикл ссылок, NFC/NFD имена.
///
/// Мутации (критерий c2):
/// (а) без канонизации (получение пути как есть) — краснеют строки «../» и «Материалы/../.claude»,
///     симлинк-тесты и NFD-тест;
/// (б) без разворота симлинка — SymlinkOutward_*, SymlinkIntoMaterials_*;
/// (в) ребёнку видна «Для репетитора/» — все Hidden-строки этой папки;
/// (г) запись в «.claude/» разрешена — все Write/Delete-строки «.claude»;
/// (д) регистрозависимое сравнение имён папок — строки «материалы/» и «для репетитора/»
///     плюс прямой тест CaseInsensitive_ProtectedNames (IsProtectedSegment);
/// (е) корень предмета Allow для любой операции (состояние до доработки 111e3b4f, п. 1) —
///     Write/Delete-строки корня;
/// (ё) защита только по segments[0] (состояние до доработки, п. 2) — строки «Заметки/CLAUDE.md»,
///     «Заметки/.claude/x», «a/b/.mcp.json», «a/.hidden/x»;
/// (ж) без fail-closed по входу (п. 3) — UnresolvedInput_DeniesWithoutThrow (NUL, пустой,
///     null, корень «/»);
/// (з) копия судится как «.ccs-text» без разворота в оригинал (состояние до решения 91,
///     дефект 64996f49) — строки «.ccs-text/Для репетитора/…» и «Заметки/.ccs-text/…».
/// </summary>
public class ChildPathPolicyTests
{
    // Синтетический корень: каталога нет на диске — Canonical оставляет его путём как есть.
    private const string Root = "/ccs-subject-9f3a";

    public static TheoryData<ChildActor, ChildPathOp, string, string, ChildPathVerdict> Rows()
    {
        var data = new TheoryData<ChildActor, ChildPathOp, string, string, ChildPathVerdict>();
        void Add(ChildActor a, ChildPathOp o, string p, ChildPathVerdict v) => data.Add(a, o, Root + "/" + p, Root, v);

        // Правило 1: всё вне папки предмета — Deny любому актору. «..» сворачивается
        // после разыменования; абсолютный путь в папку другого ребёнка — тот же отказ.
        Add(ChildActor.Child, ChildPathOp.Read, "../outside.txt", ChildPathVerdict.Deny);
        Add(ChildActor.Child, ChildPathOp.Write, "../outside.txt", ChildPathVerdict.Deny);
        Add(ChildActor.Tutor, ChildPathOp.Read, "../outside.txt", ChildPathVerdict.Deny);
        Add(ChildActor.Curator, ChildPathOp.Read, "../outside.txt", ChildPathVerdict.Deny);
        Add(ChildActor.Curator, ChildPathOp.Write, "../outside.txt", ChildPathVerdict.Deny);
        Add(ChildActor.Child, ChildPathOp.Read, "Материалы/../../взрослый/секрет.txt", ChildPathVerdict.Deny);
        Add(ChildActor.Tutor, ChildPathOp.Write, "Для репетитора/../../другой-ребёнка/конспект.md", ChildPathVerdict.Deny);
        // Абсолютный hostPath прямо в папку другого ребёнка (тот же корень, чужой подпуть).
        data.Add(ChildActor.Child, ChildPathOp.Read, "/ccs-subject-9f3a-other/конспект.md", Root, ChildPathVerdict.Deny);
        data.Add(ChildActor.Curator, ChildPathOp.Write, "/ccs-subject-9f3a-other/подготовка.md", Root, ChildPathVerdict.Deny);

        // Правило 1a (доработка 111e3b4f, п. 1): сама папка предмета читается и перечисляется
        // всеми; записывать и удалять её как файл не может никто, включая куратора.
        Add(ChildActor.Child, ChildPathOp.Read, ".", ChildPathVerdict.Allow);
        Add(ChildActor.Tutor, ChildPathOp.List, ".", ChildPathVerdict.Allow);
        Add(ChildActor.Curator, ChildPathOp.Read, ".", ChildPathVerdict.Allow);
        data.Add(ChildActor.Child, ChildPathOp.Write, Root, Root, ChildPathVerdict.Deny);
        data.Add(ChildActor.Tutor, ChildPathOp.Delete, Root, Root, ChildPathVerdict.Deny);
        data.Add(ChildActor.Curator, ChildPathOp.Write, Root, Root, ChildPathVerdict.Deny);
        data.Add(ChildActor.Curator, ChildPathOp.Delete, Root, Root, ChildPathVerdict.Deny);

        // Правило 2: «Материалы/» — ребёнку и репетитору только чтение, куратору всё (решение 65).
        Add(ChildActor.Child, ChildPathOp.Read, "Материалы/контур.png", ChildPathVerdict.Allow);
        Add(ChildActor.Child, ChildPathOp.List, "Материалы", ChildPathVerdict.Allow);
        Add(ChildActor.Child, ChildPathOp.Write, "Материалы/конспект.md", ChildPathVerdict.Deny);
        Add(ChildActor.Child, ChildPathOp.Delete, "Материалы/старое.png", ChildPathVerdict.Deny);
        Add(ChildActor.Tutor, ChildPathOp.Read, "Материалы/контур.png", ChildPathVerdict.Allow);
        Add(ChildActor.Tutor, ChildPathOp.List, "Материалы", ChildPathVerdict.Allow);
        Add(ChildActor.Tutor, ChildPathOp.Write, "Материалы/конспект.md", ChildPathVerdict.Deny);
        Add(ChildActor.Tutor, ChildPathOp.Delete, "Материалы/старое.png", ChildPathVerdict.Deny);
        Add(ChildActor.Curator, ChildPathOp.Read, "Материалы/контур.png", ChildPathVerdict.Allow);
        Add(ChildActor.Curator, ChildPathOp.List, "Материалы", ChildPathVerdict.Allow);
        Add(ChildActor.Curator, ChildPathOp.Write, "Материалы/подготовка.md", ChildPathVerdict.Allow);
        Add(ChildActor.Curator, ChildPathOp.Delete, "Материалы/старое.png", ChildPathVerdict.Allow);

        // Правило 3: «Для репетитора/» — ребёнку Hidden (ни списка, ни чтения; запись — тоже
        // отказ: Hidden описывает видимость, а не право писать), репетитор читает, пишет куратор.
        Add(ChildActor.Child, ChildPathOp.Read, "Для репетитора/ключи.md", ChildPathVerdict.Hidden);
        Add(ChildActor.Child, ChildPathOp.List, "Для репетитора", ChildPathVerdict.Hidden);
        Add(ChildActor.Child, ChildPathOp.Write, "Для репетитора/ключи.md", ChildPathVerdict.Deny);
        Add(ChildActor.Child, ChildPathOp.Delete, "Для репетитора/старые-ключи.md", ChildPathVerdict.Deny);
        Add(ChildActor.Tutor, ChildPathOp.Read, "Для репетитора/ключи.md", ChildPathVerdict.Allow);
        Add(ChildActor.Tutor, ChildPathOp.List, "Для репетитора", ChildPathVerdict.Allow);
        Add(ChildActor.Tutor, ChildPathOp.Write, "Для репетитора/ключи.md", ChildPathVerdict.Deny);
        Add(ChildActor.Tutor, ChildPathOp.Delete, "Для репетитора/старые-ключи.md", ChildPathVerdict.Deny);
        Add(ChildActor.Curator, ChildPathOp.Read, "Для репетитора/ключи.md", ChildPathVerdict.Allow);
        Add(ChildActor.Curator, ChildPathOp.List, "Для репетитора", ChildPathVerdict.Allow);
        Add(ChildActor.Curator, ChildPathOp.Write, "Для репетитора/ключи.md", ChildPathVerdict.Allow);
        Add(ChildActor.Curator, ChildPathOp.Delete, "Для репетитора/старые-ключи.md", ChildPathVerdict.Allow);

        // Правило 4: запись в настройки CLI и служебное закрыта всем, включая куратора;
        // чтение скрытого — ребёнку и репетитору Hidden, куратору .claude/.mcp.json доступны,
        // CLAUDE.md — Hidden всем (его не читает ни одна детская ручка).
        Add(ChildActor.Child, ChildPathOp.Write, ".claude/settings.json", ChildPathVerdict.Deny);
        Add(ChildActor.Child, ChildPathOp.Delete, ".claude/settings.json", ChildPathVerdict.Deny);
        Add(ChildActor.Child, ChildPathOp.Read, ".claude/settings.json", ChildPathVerdict.Hidden);
        Add(ChildActor.Child, ChildPathOp.List, ".claude", ChildPathVerdict.Hidden);
        Add(ChildActor.Tutor, ChildPathOp.Write, ".claude/settings.json", ChildPathVerdict.Deny);
        Add(ChildActor.Tutor, ChildPathOp.Read, ".claude/settings.json", ChildPathVerdict.Hidden);
        Add(ChildActor.Curator, ChildPathOp.Write, ".claude/settings.json", ChildPathVerdict.Deny);
        Add(ChildActor.Curator, ChildPathOp.Read, ".claude/settings.json", ChildPathVerdict.Allow);
        Add(ChildActor.Curator, ChildPathOp.List, ".claude", ChildPathVerdict.Allow);
        Add(ChildActor.Child, ChildPathOp.Write, ".mcp.json", ChildPathVerdict.Deny);
        Add(ChildActor.Tutor, ChildPathOp.Write, ".mcp.json", ChildPathVerdict.Deny);
        Add(ChildActor.Curator, ChildPathOp.Write, ".mcp.json", ChildPathVerdict.Deny);
        Add(ChildActor.Curator, ChildPathOp.Read, ".mcp.json", ChildPathVerdict.Allow);
        Add(ChildActor.Child, ChildPathOp.Read, ".mcp.json", ChildPathVerdict.Hidden);
        Add(ChildActor.Child, ChildPathOp.Write, "CLAUDE.md", ChildPathVerdict.Deny);
        Add(ChildActor.Tutor, ChildPathOp.Write, "CLAUDE.md", ChildPathVerdict.Deny);
        Add(ChildActor.Curator, ChildPathOp.Write, "CLAUDE.md", ChildPathVerdict.Deny);
        Add(ChildActor.Curator, ChildPathOp.Write, "CLAUDE.local.md", ChildPathVerdict.Deny);
        Add(ChildActor.Child, ChildPathOp.Write, "CLAUDE.local.md", ChildPathVerdict.Deny);
        Add(ChildActor.Child, ChildPathOp.Read, "CLAUDE.md", ChildPathVerdict.Hidden);
        Add(ChildActor.Curator, ChildPathOp.Read, "CLAUDE.md", ChildPathVerdict.Hidden);
        Add(ChildActor.Child, ChildPathOp.Write, ".черновик.md", ChildPathVerdict.Deny);
        Add(ChildActor.Child, ChildPathOp.Read, ".черновик.md", ChildPathVerdict.Hidden);
        Add(ChildActor.Curator, ChildPathOp.Read, ".черновик.md", ChildPathVerdict.Allow);
        Add(ChildActor.Curator, ChildPathOp.Write, ".черновик.md", ChildPathVerdict.Deny);
        Add(ChildActor.Child, ChildPathOp.Write, ".ccs-trash/2026-11-19/конспект.md", ChildPathVerdict.Deny);
        Add(ChildActor.Child, ChildPathOp.Read, ".ccs-trash/2026-11-19/конспект.md", ChildPathVerdict.Hidden);
        Add(ChildActor.Curator, ChildPathOp.Read, ".ccs-trash/2026-11-19/конспект.md", ChildPathVerdict.Allow);
        Add(ChildActor.Curator, ChildPathOp.Delete, ".ccs-trash/2026-11-19/конспект.md", ChildPathVerdict.Deny);

        // Правило 5 (решение 91): .ccs-text — Read/List всем (текстовую копию читает и
        // ребёнок, решение 67), но копия судится по своему оригиналу: копия ключа
        // репетитора остаётся ребёнку Hidden, копия «Материалов» — только чтение.
        // Write/Delete — Deny всем, включая куратора: копии пишет только сервер.
        // .ccs-trash — ребёнку Hidden (строки выше).
        Add(ChildActor.Child, ChildPathOp.Read, ".ccs-text/скан.md", ChildPathVerdict.Allow);
        Add(ChildActor.Child, ChildPathOp.List, ".ccs-text", ChildPathVerdict.Allow);
        Add(ChildActor.Tutor, ChildPathOp.Read, ".ccs-text/скан.md", ChildPathVerdict.Allow);
        Add(ChildActor.Curator, ChildPathOp.Read, ".ccs-text/скан.md", ChildPathVerdict.Allow);
        Add(ChildActor.Child, ChildPathOp.Write, ".ccs-text/скан.md", ChildPathVerdict.Deny);
        Add(ChildActor.Tutor, ChildPathOp.Delete, ".ccs-text/скан.md", ChildPathVerdict.Deny);
        Add(ChildActor.Curator, ChildPathOp.Write, ".ccs-text/скан.md", ChildPathVerdict.Deny);
        // Ключа копия судится как ключ: ребёнку Hidden, репетитору и куратору открыта (дефект 64996f49).
        Add(ChildActor.Child, ChildPathOp.Read, ".ccs-text/Для репетитора/k.pdf.txt", ChildPathVerdict.Hidden);
        Add(ChildActor.Child, ChildPathOp.List, ".ccs-text/Для репетитора/k.pdf.txt", ChildPathVerdict.Hidden);
        Add(ChildActor.Tutor, ChildPathOp.Read, ".ccs-text/Для репетитора/k.pdf.txt", ChildPathVerdict.Allow);
        Add(ChildActor.Curator, ChildPathOp.Read, ".ccs-text/Для репетитора/k.pdf.txt", ChildPathVerdict.Allow);
        // Копия «Материалов»: чтение разрешено, запись — нет.
        Add(ChildActor.Child, ChildPathOp.Read, ".ccs-text/Материалы/a.pdf.txt", ChildPathVerdict.Allow);
        Add(ChildActor.Child, ChildPathOp.Write, ".ccs-text/Материалы/a.pdf.txt", ChildPathVerdict.Deny);
        // Копия на глубине — обычный скрытый сегмент, не верхняя копия: ребёнку Hidden.
        Add(ChildActor.Child, ChildPathOp.Read, "Заметки/.ccs-text/x.txt", ChildPathVerdict.Hidden);
        // Двойная копия: разворот ровно на один уровень — внутренний .ccs-text скрытый сегмент.
        Add(ChildActor.Child, ChildPathOp.Read, ".ccs-text/.ccs-text/x.txt", ChildPathVerdict.Hidden);

        // Правило 6: всё остальное внутри папки — Allow всем троим (мягкая граница:
        // что ребёнок записал, куратор прочтёт). Сама папка предмета — своя.
        Add(ChildActor.Child, ChildPathOp.Read, "конспект.md", ChildPathVerdict.Allow);
        Add(ChildActor.Child, ChildPathOp.Write, "конспект.md", ChildPathVerdict.Allow);
        Add(ChildActor.Child, ChildPathOp.Delete, "черновик.md", ChildPathVerdict.Allow);
        Add(ChildActor.Tutor, ChildPathOp.Write, "урок-1.md", ChildPathVerdict.Allow);
        Add(ChildActor.Curator, ChildPathOp.Write, "подготовка.md", ChildPathVerdict.Allow);
        Add(ChildActor.Child, ChildPathOp.List, ".", ChildPathVerdict.Allow);

        // Регистр имени папки — не способ обойти границу: «материалы/» для политики
        // та же «Материалы/» (на Linux это другой каталог на диске — граница держится
        // по имени, иначе переименование в нижний регистр открывал бы запись).
        Add(ChildActor.Child, ChildPathOp.Write, "материалы/конспект.md", ChildPathVerdict.Deny);
        Add(ChildActor.Curator, ChildPathOp.Write, "материалы/подготовка.md", ChildPathVerdict.Allow);
        Add(ChildActor.Child, ChildPathOp.Read, "для репетитора/ключи.md", ChildPathVerdict.Hidden);
        Add(ChildActor.Tutor, ChildPathOp.Read, "МАТЕРИАЛЫ/контур.png", ChildPathVerdict.Allow);

        // Защита на ЛЮБОЙ глубине (решение Софьи, доработка 111e3b4f, п. 2): «CLAUDE*.md»,
        // «.claude», «.mcp.json» и любой скрытый сегмент проверяются по всем сегментам пути;
        // запрет записи действует на всех, включая куратора. Видимость чтения на глубине —
        // та же, что на верхнем уровне (скрытое: куратору видно, остальным Hidden).
        Add(ChildActor.Child, ChildPathOp.Write, "Заметки/CLAUDE.md", ChildPathVerdict.Deny);
        Add(ChildActor.Tutor, ChildPathOp.Write, "Заметки/CLAUDE.md", ChildPathVerdict.Deny);
        Add(ChildActor.Curator, ChildPathOp.Write, "Заметки/CLAUDE.md", ChildPathVerdict.Deny);
        Add(ChildActor.Curator, ChildPathOp.Delete, "Заметки/CLAUDE.local.md", ChildPathVerdict.Deny);
        Add(ChildActor.Child, ChildPathOp.Read, "Заметки/CLAUDE.md", ChildPathVerdict.Hidden);
        Add(ChildActor.Curator, ChildPathOp.Read, "Заметки/CLAUDE.md", ChildPathVerdict.Hidden);
        Add(ChildActor.Curator, ChildPathOp.Write, "Заметки/.claude/x", ChildPathVerdict.Deny);
        Add(ChildActor.Child, ChildPathOp.Write, "Заметки/.claude/x", ChildPathVerdict.Deny);
        Add(ChildActor.Child, ChildPathOp.Read, "Заметки/.claude/x", ChildPathVerdict.Hidden);
        Add(ChildActor.Curator, ChildPathOp.Read, "Заметки/.claude/x", ChildPathVerdict.Allow);
        Add(ChildActor.Tutor, ChildPathOp.Write, "a/b/.mcp.json", ChildPathVerdict.Deny);
        Add(ChildActor.Curator, ChildPathOp.Write, "a/b/.mcp.json", ChildPathVerdict.Deny);
        Add(ChildActor.Child, ChildPathOp.Write, "a/.hidden/x", ChildPathVerdict.Deny);
        Add(ChildActor.Tutor, ChildPathOp.Write, "a/.hidden/x", ChildPathVerdict.Deny);
        Add(ChildActor.Curator, ChildPathOp.Write, "a/.hidden/x", ChildPathVerdict.Deny);
        Add(ChildActor.Child, ChildPathOp.Read, "a/.hidden/x", ChildPathVerdict.Hidden);
        Add(ChildActor.Curator, ChildPathOp.Read, "a/.hidden/x", ChildPathVerdict.Allow);
        // «Материалы» и «Для репетитора» — только верхний уровень: вложенная папка с тем же
        // именем — обычная папка ребёнка, не защищённая.
        Add(ChildActor.Child, ChildPathOp.Write, "Заметки/Материалы/конспект.md", ChildPathVerdict.Allow);
        Add(ChildActor.Child, ChildPathOp.Read, "Заметки/Для репетитора/ключи.md", ChildPathVerdict.Allow);

        // Смешанный путь через защищённое: «Материалы/../.claude/settings.json» канонизируется
        // в «.claude/settings.json» — вердикт по нему, а не по строке пути.
        Add(ChildActor.Child, ChildPathOp.Write, "Материалы/../.claude/settings.json", ChildPathVerdict.Deny);
        Add(ChildActor.Child, ChildPathOp.Read, "Материалы/../.claude/settings.json", ChildPathVerdict.Hidden);
        Add(ChildActor.Curator, ChildPathOp.Read, "Материалы/../.claude/settings.json", ChildPathVerdict.Allow);
        Add(ChildActor.Child, ChildPathOp.Write, "Материалы/../конспект.md", ChildPathVerdict.Allow);

        return data;
    }

    [Theory]
    [MemberData(nameof(Rows))]
    public void Decide_TableRules(ChildActor actor, ChildPathOp op, string hostPath, string subjectRoot, ChildPathVerdict expected)
    {
        var result = ChildPathPolicy.Decide(actor, op, hostPath, subjectRoot);

        result.Verdict.Should().Be(expected);
        if (expected == ChildPathVerdict.Deny)
            result.Reason.Should().NotBeNullOrWhiteSpace("у отказа всегда есть причина — её показывает рельса");
        if (expected != ChildPathVerdict.Deny)
            result.Reason.Should().BeNull();
    }

    // Fail-closed по входу (миноры ревью 111e3b4f, п. 3): NUL в пути, пустой или null путь/
    // корень, корень «/» — Deny с причиной, без исключения наружу (Path.* на таких входах
    // бросает ArgumentException — он не должен выходить за границу функции).
    [Theory]
    [InlineData(ChildActor.Child, ChildPathOp.Write, "a\0b.md", "/ccs-subject-9f3a")]
    [InlineData(ChildActor.Curator, ChildPathOp.Read, "a\0b.md", "/ccs-subject-9f3a")]
    [InlineData(ChildActor.Child, ChildPathOp.Read, "Заметки/CLAUDE\0.md", "/ccs-subject-9f3a")]
    [InlineData(ChildActor.Child, ChildPathOp.Write, "", "/ccs-subject-9f3a")]
    [InlineData(ChildActor.Curator, ChildPathOp.Read, null, "/ccs-subject-9f3a")]
    [InlineData(ChildActor.Child, ChildPathOp.Write, "конспект.md", "")]
    [InlineData(ChildActor.Curator, ChildPathOp.Read, "конспект.md", null)]
    [InlineData(ChildActor.Curator, ChildPathOp.List, "конспект.md", "/")]
    public void UnresolvedInput_DeniesWithoutThrow(ChildActor actor, ChildPathOp op, string? hostPath, string? subjectRoot)
    {
        var act = () => ChildPathPolicy.Decide(actor, op, hostPath!, subjectRoot!);

        act.Should().NotThrow("несобранный вход — отказ, а не исключение наружу");
        act().Verdict.Should().Be(ChildPathVerdict.Deny);
        act().Reason.Should().NotBeNullOrWhiteSpace();
    }

    // Мутация (д): сравнение имён защищённых папок регистронезависимо и по NFC-составу.
    [Theory]
    [InlineData("Материалы", true)]
    [InlineData("материалы", true)]
    [InlineData("МАТЕРИАЛЫ", true)]
    [InlineData("Матеріалы", false)] // другая буква «і» — не тот символ и не та папка
    [InlineData("Для репетитора", true)]
    [InlineData("для репетитора", true)]
    [InlineData(".claude", true)]
    [InlineData(".MCP.json", true)]
    [InlineData("CLAUDE.md", true)]
    [InlineData("claude.local.md", true)]
    [InlineData(".ccs-trash", true)]
    [InlineData(".ccs-text", true)]
    [InlineData(".ccs-attachments", true)]
    [InlineData("conftest", false)]
    public void IsProtectedSegment_CaseInsensitiveNames(string segment, bool expected) =>
        ChildPathPolicy.IsProtectedSegment(segment).Should().Be(expected);

    // Форма копии (решение 90): одна функция для генератора копии и для всех, кто её судит.
    [Theory]
    [InlineData("лист.pdf", ".ccs-text/лист.pdf.txt")]
    [InlineData("Задачи/лист.pdf", ".ccs-text/Задачи/лист.pdf.txt")]
    [InlineData("Материалы\\конспект.docx", ".ccs-text/Материалы/конспект.docx.txt")] // «\» → «/»
    [InlineData("/лист.pdf", ".ccs-text/лист.pdf.txt")] // ведущий «/» срезается
    [InlineData("Для репетитора/ключи.md", ".ccs-text/Для репетитора/ключи.md.txt")]
    [InlineData(".ccs-text/лист.pdf.txt", ".ccs-text/лист.pdf.txt")] // копия сама — как есть
    public void TextCopyOf_Form(string rel, string expected) =>
        ChildPathPolicy.TextCopyOf(rel).Should().Be(expected);

    // Расширение оригинала сохраняется: копии разных файлов не схлопываются в одну.
    [Fact]
    public void TextCopyOf_DifferentOriginals_DifferentCopies() =>
        ChildPathPolicy.TextCopyOf("лист.pdf").Should().NotBe(ChildPathPolicy.TextCopyOf("лист.png"));

    // OriginalOf — обратная к TextCopyOf: круг возвращается на путях любой защищённости.
    [Theory]
    [InlineData("лист.pdf")]
    [InlineData("Задачи/лист.pdf")]
    [InlineData("Материалы/конспект.docx")]
    [InlineData("Для репетитора/ключи.md")]
    public void OriginalOf_TextCopyOf_RoundTrip(string original) =>
        ChildPathPolicy.OriginalOf(ChildPathPolicy.TextCopyOf(original)).Should().Be(original);

    // Всё, что не является формой копии, — null: судящий решает по факту пути, не додумывает оригинал.
    [Theory]
    [InlineData("Заметки/x.txt")]
    [InlineData(".ccs-text/x.pdf")] // нет суффикса .txt
    [InlineData(".ccs-text")]
    [InlineData(".ccs-text/")]
    [InlineData(".ccs-text/.txt")] // после среза имя пустое
    [InlineData("x.txt")]
    [InlineData("")]
    public void OriginalOf_NotACopy_IsNull(string path) =>
        ChildPathPolicy.OriginalOf(path).Should().BeNull();

    // Симлинк наружу: ссылка внутри папки, цель — за её пределами. Без развёртки
    // путь выглядел бы «внутри», и граница молчала бы (тот же пролом, что закрыл S1a в гейте вики).
    [Fact]
    public void SymlinkOutward_DeniesEveryActor()
    {
        var (subject, outside) = CreateSubjectWith("outside");
        try
        {
            CreateLink(subject, "link", outside);
            foreach (var actor in new[] { ChildActor.Child, ChildActor.Tutor, ChildActor.Curator })
                ChildPathPolicy.Decide(actor, ChildPathOp.Read, Path.Combine(subject, "link/секрет.txt"), subject)
                    .Verdict.Should().Be(ChildPathVerdict.Deny, $"актор {actor} читает через ссылку за папкой");
        }
        finally
        {
            Cleanup(subject, outside);
        }
    }

    // Симлинк внутрь «Материалы»: та же цель на диске — те же права. Запись ребёнка
    // через ссылку — отказ, хотя строка пути «uris/…» не похожа на «Материалы/…».
    [Fact]
    public void SymlinkIntoMaterials_ChildWriteDenies()
    {
        var (subject, _) = CreateSubjectWith();
        try
        {
            Directory.CreateDirectory(Path.Combine(subject, "Материалы"));
            CreateLink(subject, "uris", "Материалы");
            ChildPathPolicy.Decide(ChildActor.Child, ChildPathOp.Write, Path.Combine(subject, "uris/конспект.md"), subject)
                .Verdict.Should().Be(ChildPathVerdict.Deny);
            ChildPathPolicy.Decide(ChildActor.Curator, ChildPathOp.Write, Path.Combine(subject, "uris/конспект.md"), subject)
                .Verdict.Should().Be(ChildPathVerdict.Allow);
        }
        finally
        {
            Cleanup(subject);
        }
    }

    // Висячая ссылка (цели ещё нет) — тоже ссылка: её цель и есть путь.
    [Fact]
    public void DanglingSymlinkOutward_StillDenies()
    {
        var (subject, _) = CreateSubjectWith();
        try
        {
            CreateLink(subject, "gone", "../вне-папки/файл.txt");
            ChildPathPolicy.Decide(ChildActor.Child, ChildPathOp.Write, Path.Combine(subject, "gone/новый.md"), subject)
                .Verdict.Should().Be(ChildPathVerdict.Deny);
        }
        finally
        {
            Cleanup(subject);
        }
    }

    // Цикл ссылок a→b→a: разбор не удался — fail-closed, Deny (пути нет — и права нет).
    [Fact]
    public void LinkCycle_UnresolvablePathDenies()
    {
        var (subject, _) = CreateSubjectWith();
        try
        {
            CreateLink(subject, "a", "b");
            CreateLink(subject, "b", "a");
            ChildPathPolicy.Decide(ChildActor.Curator, ChildPathOp.Read, Path.Combine(subject, "a/x.txt"), subject)
                .Verdict.Should().Be(ChildPathVerdict.Deny);
        }
        finally
        {
            Cleanup(subject);
        }
    }

    // NFC/NFD. Имя «Для репетитора» стабильно: кириллица в NFD не разлагается (тот же
    // контрольный факт, что в тесте гейта вики), поэтому именно на этом имени способа
    // обойти границу составом символа нет — фиксируем это как факт. Сам механизм
    // нормализации (Canonical приводит хвост к NFC) показан на имени с акцентом:
    // «Á» = A + U+0301, NFD-форма пути против того же корня в NFC — та же папка.
    [Fact]
    public void TutorDirName_IsNfcStable() =>
        "Для репетитора".Normalize(NormalizationForm.FormD)
            .Should().Be("Для репетитора", "кириллица в NFD не разлагается — stable name");

    [Fact]
    public void NfdPathAgainstNfcRoot_SameFolderForPolicy()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "ccs-childpath-" + Guid.NewGuid().ToString("N"));
        var rootNfc = Path.Combine(baseDir, "Ápprent");
        Directory.CreateDirectory(rootNfc);
        try
        {
            var nfd = rootNfc.Normalize(NormalizationForm.FormD);
            nfd.Should().NotBe(rootNfc, "контрольный факт: формы действительно разные");

            ChildPathPolicy.Decide(ChildActor.Child, ChildPathOp.Write, Path.Combine(nfd, "конспект.md"), rootNfc)
                .Verdict.Should().Be(ChildPathVerdict.Allow, "NFD-форма того же пути — та же папка предмета");
            ChildPathPolicy.Decide(ChildActor.Child, ChildPathOp.Write, Path.Combine(nfd, "../вне.txt"), rootNfc)
                .Verdict.Should().Be(ChildPathVerdict.Deny, "нормализация не отменяет границу");
        }
        finally
        {
            Directory.Delete(baseDir, recursive: true);
        }
    }

    private static (string Subject, string Outside) CreateSubjectWith(params string[] extraDirs)
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "ccs-childpath-" + Guid.NewGuid().ToString("N"));
        var subject = Path.Combine(baseDir, "subject");
        Directory.CreateDirectory(subject);
        foreach (var d in extraDirs)
            Directory.CreateDirectory(Path.Combine(baseDir, d));
        return (subject, Path.Combine(baseDir, extraDirs.Length > 0 ? extraDirs[0] : "outside"));
    }

    private static void CreateLink(string subject, string name, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(subject, name), target);
        }
        catch (IOException)
        {
            // Windows без права CreateSymbolicLink (Developer Mode/админ) — пропуск, не провал:
            // в CI (Linux) и на машине разработки с правом ссылка создаётся, там тест полный.
            if (!OperatingSystem.IsWindows()) throw;
        }
    }

    private static void Cleanup(params string[] dirs)
    {
        foreach (var d in dirs)
        {
            try
            {
                var parent = Path.GetDirectoryName(d);
                if (parent is not null && Path.GetFileName(parent).StartsWith("ccs-childpath-"))
                    Directory.Delete(parent, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
