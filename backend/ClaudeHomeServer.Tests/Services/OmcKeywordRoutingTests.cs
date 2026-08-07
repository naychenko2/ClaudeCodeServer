using ClaudeHomeServer.Services.Prompts;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Детект «магических слов» oh-my-claudecode: важны и позитив, и НЕГАТИВ —
// ложный запуск workflow из-за слова в обычной речи ломает ход пользователю.
public class OmcKeywordRoutingTests
{
    [Theory]
    [InlineData("запусти ralph", "ralph")]
    [InlineData("запусти ultrawork please", "ultrawork")]
    [InlineData("run ulw", "ultrawork")]
    [InlineData("запусти autopilot", "autopilot")]
    [InlineData("включи full auto", "autopilot")]
    [InlineData("start ccg", "ccg")]
    [InlineData("активируй ralplan", "ralplan")]
    [InlineData("wiki this decision", "wiki")]
    [InlineData("wiki add note", "wiki")]
    public void DetectSkills_МагсловоСГлаголомЗапуска_Распознаётся(string text, string expectedSkill)
    {
        OmcKeywordRouting.DetectSkills(text).Should().Contain(expectedSkill);
    }

    [Theory]
    [InlineData("посмотри на wiki-страницу проекта")]
    [InlineData("открой wiki и почитай")]
    [InlineData("the wiki page is outdated")]
    [InlineData("wiki")]
    public void DetectSkills_ГолоеWiki_НеТриггерит(string text)
    {
        OmcKeywordRouting.DetectSkills(text).Should().NotContain("wiki");
    }

    // Гард 2: без глагола запуска рядом — голое упоминание не команда. Ровно баг B1:
    // слово из названия механики в обсуждении/задаче не должно впрыскивать инструкцию запуска.
    [Theory]
    [InlineData("ralph", "ralph")]
    [InlineData("autopilot", "autopilot")]
    [InlineData("full auto", "autopilot")]
    [InlineData("ultrawork", "ultrawork")]
    [InlineData("ulw", "ultrawork")]
    [InlineData("ccg", "ccg")]
    [InlineData("ralplan", "ralplan")]
    [InlineData("deep-interview", "deep-interview")]
    [InlineData("ai-slop", "ai-slop-cleaner")]
    public void DetectSkills_ГолоеСловоБезГлагола_НеТриггерит(string text, string skill)
    {
        OmcKeywordRouting.DetectSkills(text).Should().NotContain(skill);
    }

    // Гард 3: вопрос или маркер обсуждения рядом гасит совпадение, даже если формальный
    // глагол запуска тоже есть в тексте (реальный сценарий инъекции — обсуждение механики).
    [Theory]
    [InlineData("а как у нас работает autopilot?")]
    [InlineData("что такое ralph?")]
    [InlineData("стоит ли нам запусти ralph")]
    [InlineData("сравни ralph и ultrawork, запусти что-нибудь")]
    public void DetectSkills_ВопросИлиМаркерОбсуждения_НеТриггерит(string text)
    {
        var skills = OmcKeywordRouting.DetectSkills(text);
        skills.Should().NotContain("ralph");
        skills.Should().NotContain("autopilot");
        skills.Should().NotContain("ultrawork");
    }

    // Гард 1 (эхо): наша же подсказка, процитированная в новом сообщении (постановка задачи,
    // цитата из лога хода), не должна реактивировать скилл — иначе самоподдерживающаяся петля.
    // Без стрипки сработал бы гард 2: сам блок подсказки содержит «Немедленно запусти скилл».
    [Fact]
    public void DetectSkills_ЭхоОдиночнойПодсказки_НеТриггерит()
    {
        var hint = OmcKeywordRouting.BuildKeywordHint("запусти ralph");
        OmcKeywordRouting.DetectSkills(hint).Should().NotContain("ralph");
    }

    [Fact]
    public void DetectSkills_ЭхоМножественнойПодсказки_НеТриггерит()
    {
        var hint = OmcKeywordRouting.BuildKeywordHint("запусти ralph и autopilot");
        var skills = OmcKeywordRouting.DetectSkills(hint);
        skills.Should().NotContain("ralph");
        skills.Should().NotContain("autopilot");
    }

    // Ровно сценарий инъекции из живого прогона: эхо-блок процитирован ВНУТРИ более длинного
    // текста постановки задачи — гард должен вырезать его, а не только матчить текст целиком.
    [Fact]
    public void DetectSkills_ЭхоВнутриДлинногоТекста_НеТриггерит()
    {
        var hint = OmcKeywordRouting.BuildKeywordHint("запусти autopilot");
        var quoted = $"Баг: слово из названия механики впрыснуло инструкцию.\n\n{hint}\n\nПочини гард.";
        OmcKeywordRouting.DetectSkills(quoted).Should().NotContain("autopilot");
    }

    [Theory]
    [InlineData("надо отрефакторить код")]           // «раф» внутри — не ralph
    [InlineData("результаты ultrawide монитора")]     // не ultrawork/ulw
    [InlineData("bulwark защита")]                     // ulw внутри слова
    [InlineData("обычное сообщение без магии")]
    [InlineData("")]
    [InlineData(null)]
    public void DetectSkills_ОбычнаяРечь_Пусто(string? text)
    {
        OmcKeywordRouting.DetectSkills(text).Should().BeEmpty();
    }

    [Fact]
    public void DetectSkills_НесколькоМагслов_ВПорядкеПриоритета()
    {
        // ralph раньше ultrawork в таблице приоритетов; короткий текст — оба совпадения
        // попадают в окно ±80 символов одного и того же глагола запуска
        var skills = OmcKeywordRouting.DetectSkills("запусти ralph потом ultrawork");
        skills.Should().Equal("ralph", "ultrawork");
    }

    [Fact]
    public void BuildKeywordHint_ПустойТекст_Null()
    {
        OmcKeywordRouting.BuildKeywordHint("просто текст").Should().BeNull();
    }

    [Fact]
    public void BuildKeywordHint_Магслово_СодержитИнструкциюЗапуска()
    {
        var hint = OmcKeywordRouting.BuildKeywordHint("запусти ralph");
        hint.Should().NotBeNull();
        hint.Should().Contain("/oh-my-claudecode:ralph");
    }
}
