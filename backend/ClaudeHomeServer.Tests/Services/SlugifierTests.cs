using System.Text;
using ClaudeHomeServer.Services;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Сторож переноса `Slugify` в спину (Этап 3): три независимые копии транслитерации
/// (`PersonaManager`, `DossierGitExporter`, `GitServerService.SlugifyUsername`) сведены
/// к одному `Slugifier`. Здесь — доказательство, что выход НЕ поехал: старые алгоритмы
/// воспроизведены здесь дословно и сравниваются с новым на общем наборе строк.
///
/// Копии оказались НЕ тождественны ровно в одной букве: «х» → «h» у персон, «kh»
/// у летописи. Оба выхода персистятся (handle в personas.json и @упоминаниях, имена
/// файлов в ветке летописи), поэтому расхождение сохранено параметром `XStyle`,
/// а не «выправлено» до одной буквы.
/// </summary>
public class SlugifierTests
{
    // Дословная копия PersonaManager.Slugify до переноса (х→h, `char.IsLetterOrDigit(ch) && ch < 128`,
    // дефис только при непустом буфере).
    private static string LegacyPersonaSlugify(string s)
    {
        var translit = LegacyTranslit("h");
        var sb = new StringBuilder();
        var prevDash = false;
        foreach (var ch in s.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch) && ch < 128)
            {
                sb.Append(ch);
                prevDash = false;
            }
            else if (translit.TryGetValue(ch, out var tr))
            {
                if (tr.Length > 0) { sb.Append(tr); prevDash = false; }
            }
            else if (!prevDash && sb.Length > 0)
            {
                sb.Append('-');
                prevDash = true;
            }
        }
        return sb.ToString().Trim('-');
    }

    // Дословная копия DossierGitExporter.Slugify до переноса (х→kh, потолок 48, фолбэк "dossier").
    private static string LegacyDossierSlugify(string subject, int maxSlugChars = 48)
    {
        var translit = LegacyTranslit("kh");
        var sb = new StringBuilder();
        var lastDash = false;
        foreach (var ch in subject.ToLowerInvariant())
        {
            if (translit.TryGetValue(ch, out var piece))
            {
                if (piece.Length == 0) continue;
                sb.Append(piece);
                lastDash = false;
            }
            else if (char.IsAsciiLetterOrDigit(ch))
            {
                sb.Append(ch);
                lastDash = false;
            }
            else if (!lastDash)
            {
                sb.Append('-');
                lastDash = true;
            }
        }
        var slug = sb.ToString().Trim('-');
        if (slug.Length > maxSlugChars) slug = slug[..maxSlugChars].Trim('-');
        return slug.Length == 0 ? "dossier" : slug;
    }

    private static Dictionary<char, string> LegacyTranslit(string x) => new()
    {
        ['а'] = "a", ['б'] = "b", ['в'] = "v", ['г'] = "g", ['д'] = "d", ['е'] = "e", ['ё'] = "e",
        ['ж'] = "zh", ['з'] = "z", ['и'] = "i", ['й'] = "y", ['к'] = "k", ['л'] = "l", ['м'] = "m",
        ['н'] = "n", ['о'] = "o", ['п'] = "p", ['р'] = "r", ['с'] = "s", ['т'] = "t", ['у'] = "u",
        ['ф'] = "f", ['х'] = x, ['ц'] = "ts", ['ч'] = "ch", ['ш'] = "sh", ['щ'] = "sch",
        ['ъ'] = "", ['ы'] = "y", ['ь'] = "", ['э'] = "e", ['ю'] = "yu", ['я'] = "ya",
    };

    // Кириллица, смесь, краевые случаи разделителей, буквы вне обеих таблиц (ёлка/ъ/ь),
    // не-латинские алфавиты (старая персон-копия пропускала их через IsLetterOrDigit
    // только при ch < 128 — то есть тоже в дефис), длинные строки для потолка летописи.
    public static TheoryData<string> Samples =>
    [
        "Обсуждение архитектуры",
        "Стратсессия",
        "Мухин Хохлов Хабаровск",
        "объект подъезд съёмка",
        "Маша Аналитик",
        "  Пробелы по краям  ",
        "Двойные   пробелы--и дефисы",
        "!!!",
        "",
        "  ",
        "ASCII only name",
        "Смешанный Mixed 123 текст",
        "Ёлка ёжик ЙОД щавель цирк чай шум эхо юла яма",
        "---",
        "-начало и конец-",
        "Проект №1 (важный) / v2.0",
        "Ünïcödé nöt cyrillic",
        "日本語テキスト",
        "Очень длинный заголовок паспорта изменений про транслитерацию и вынос примитива в спину",
        "Х",
        "х",
        "ъь",
        "a",
        "Персона: Кира — фронтенд-разработчик",
    ];

    [Theory]
    [MemberData(nameof(Samples))]
    public void СтильH_СовпадаетСоСтаройКопиейPersonaManager(string input)
    {
        Slugifier.Slugify(input, Slugifier.XStyle.H)
            .Should().Be(LegacyPersonaSlugify(input),
                "выход handle персистится в personas.json и @упоминаниях — расхождение переименует персон");
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public void ФасадPersonaManager_СовпадаетСоСтаройКопией(string input)
    {
        PersonaManager.Slugify(input).Should().Be(LegacyPersonaSlugify(input));
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public void СтильKh_СовпадаетСоСтаройКопиейDossierGitExporter(string input)
    {
        var slug = Slugifier.Slugify(input, Slugifier.XStyle.Kh, 48);
        var expected = LegacyDossierSlugify(input);

        (slug.Length == 0 ? "dossier" : slug)
            .Should().Be(expected,
                "выход — имя файла в ветке летописи, расхождение переименует существующие паспорта");
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public void ФасадDossierGitExporter_СовпадаетСоСтаройКопией(string input)
    {
        ClaudeHomeServer.Services.Dossiers.DossierGitExporter.Slugify(input)
            .Should().Be(LegacyDossierSlugify(input));
    }

    // Единственное осознанное расхождение стилей — буква «х». Проверяем предметно,
    // чтобы «выправление» до одной буквы не прошло молча.
    [Fact]
    public void РасхождениеСтилей_ТолькоБукваХ()
    {
        Slugifier.Slugify("Хохлов", Slugifier.XStyle.H).Should().Be("hohlov");
        Slugifier.Slugify("Хохлов", Slugifier.XStyle.Kh).Should().Be("khokhlov");

        // Все прочие буквы алфавита в обоих стилях дают одно и то же
        foreach (var ch in "абвгдеёжзийклмнопрстуфцчшщъыьэюя")
        {
            var s = ch.ToString();
            Slugifier.Slugify(s, Slugifier.XStyle.H)
                .Should().Be(Slugifier.Slugify(s, Slugifier.XStyle.Kh), $"буква «{ch}»");
        }
    }

    [Fact]
    public void Потолок_СнимаетКраевойДефисПослеРеза()
    {
        // Рез ровно на дефисе: хвостовой дефис обязан уйти после обрезки
        Slugifier.Slugify("abcde fghij", Slugifier.XStyle.H, maxChars: 6).Should().Be("abcde");
        // 0 и меньше — без потолка
        Slugifier.Slugify("abcde fghij", Slugifier.XStyle.H, maxChars: 0).Should().Be("abcde-fghij");
    }

    [Fact]
    public void ПустойВыход_ОтдаётсяКакЕсть_ПолитикаФолбэкаУВызывающего()
    {
        Slugifier.Slugify("!!!", Slugifier.XStyle.H).Should().BeEmpty();
        Slugifier.Slugify("ъь", Slugifier.XStyle.Kh).Should().BeEmpty();
    }

    // GitServerService.SlugifyUsername до переноса была ASCII-only без транслита:
    // КАЖДЫЙ кириллический логин вырождался в пустую строку → общий фолбэк «user»,
    // и разные пользователи цеплялись к одному аккаунту Forgejo. Тест фиксирует,
    // что теперь логины расходятся.
    [Fact]
    public void ЛогиныForgejo_КириллическиеБольшеНеСхлопываютсяВОдин()
    {
        var andrey = Slugifier.Slugify("Андрей", Slugifier.XStyle.H);
        var maria = Slugifier.Slugify("Мария", Slugifier.XStyle.H);

        andrey.Should().Be("andrey");
        maria.Should().Be("mariya");
        andrey.Should().NotBe(maria);

        // Старая копия обоих отправляла в один фолбэк
        LegacyUsernameSlugify("Андрей").Should().Be(LegacyUsernameSlugify("Мария"));
    }

    private static string LegacyUsernameSlugify(string username)
    {
        var sb = new StringBuilder();
        foreach (var c in username.ToLowerInvariant())
            sb.Append(char.IsAsciiLetterOrDigit(c) ? c : '-');
        var slug = sb.ToString().Trim('-');
        return slug.Length > 0 ? slug : "user";
    }
}
