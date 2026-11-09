using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Потолок контракта персоны: весь характер уезжает в --append-system-prompt, а командная строка
// Windows ограничена 32767 символами вместе с остальным промптом хода. Гейт стоит на записи
// (PersonasController), но уже сохранённые раздутые персоны обязаны продолжать работать.
public class PersonaContractLimitTests
{
    private static PersonaContract ContractOf(int instructionsLength) => new()
    {
        Character = "Ты — тестовая персона",
        MustDo = ["коротко"],
        Instructions = new string('и', instructionsLength),
    };

    [Fact]
    public void ContractSize_СуммируетВсеСлотыИLegacySystemPrompt()
    {
        var contract = new PersonaContract
        {
            Character = "12345",
            Tone = "123",
            MustDo = ["12", "1"],
            MustNot = ["1"],
            OutputFormat = "1234",
            SpeechExamples = ["12"],
            Instructions = "123456",
        };

        PersonaManager.ContractSize(contract, systemPrompt: "1234567890").Should().Be(34);
        PersonaManager.ContractSize(null, systemPrompt: "12345").Should().Be(5);
        PersonaManager.ContractSize(null, null).Should().Be(0);
    }

    [Fact]
    public void ExceedsContractLimit_Создание_ВПределахПотолка_Пропускает()
    {
        var contract = ContractOf(PersonaManager.MaxContractChars - 100);

        PersonaManager.ExceedsContractLimit(contract, null, current: 0, out _).Should().BeFalse();
    }

    [Fact]
    public void ExceedsContractLimit_Создание_СверхПотолка_Отклоняет()
    {
        var contract = ContractOf(PersonaManager.MaxContractChars + 1);

        PersonaManager.ExceedsContractLimit(contract, null, current: 0, out var error).Should().BeTrue();
        error.Should().Contain(PersonaManager.MaxContractChars.ToString());
    }

    [Fact]
    public void ExceedsContractLimit_РаздутаяПерсона_СокращениеРазрешено()
    {
        // Персона уже сверх потолка (пантеон OmO, легаси-импорт): правка, которая уменьшает
        // контракт, обязана проходить — иначе её вообще нельзя привести в порядок
        var current = PersonaManager.MaxContractChars * 3;
        var shrunk = ContractOf(PersonaManager.MaxContractChars * 2);

        PersonaManager.ExceedsContractLimit(shrunk, null, current, out _).Should().BeFalse();
    }

    [Fact]
    public void ExceedsContractLimit_РаздутаяПерсона_РостОтклоняется()
    {
        var current = PersonaManager.MaxContractChars * 2;
        var grown = ContractOf(PersonaManager.MaxContractChars * 3);

        PersonaManager.ExceedsContractLimit(grown, null, current, out _).Should().BeTrue();
    }

    // Регрессия: 12 000 — это не «правило из ниоткуда», а контракт с TurnPromptAssembler
    // (задача dc641949). Уменьшение подкрутит потолок и при длинных инструкциях часть чатов
    // упрётся в лимит командной строки ДО срезки нестабильных секций; увеличение
    // пройдёт мимо проверки на записи (PersonasController → UI / MCP personas_update),
    // и раздутая персона уронит процесс при Process.Start. Стоит как явный тест, чтобы
    // правка комментария или «оптимизация» числа не прошла тихо.
    [Fact]
    public void MaxContractChars_РегрессияНа12Тысяч()
        => PersonaManager.MaxContractChars.Should().Be(12_000);
}
