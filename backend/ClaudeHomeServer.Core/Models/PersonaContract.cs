using System.Text.Json.Serialization;

namespace ClaudeHomeServer.Models;

// Профиль доступа персоны (P6): Full — без ограничений; ReadOnly — смотрит и советует,
// но ничего не меняет (без правок файлов, Bash и мутаций задач/заметок/персон);
// Custom — свой список запрещённых инструментов (Persona.DisallowedTools).
public enum PersonaAccess { Full, ReadOnly, Custom }

// Специальность персоны — функциональная роль для оркестрации (НЕ путать с Persona.Role —
// то отображаемое имя «Роль (Имя)», это машинный тег способности). Используется: голосом
// брифинга (Secretary), группировкой/статусом команды и роутингом памяти команды.
// None — не задана: оркестрация берёт явные слоты либо дефолт каталога; работает
// с любыми персонами, не только OmO.
// BackendExecutor/FrontendExecutor/DevopsExecutor — профильные исполнители рядом с
// универсальным (Executor): ведут себя как исполнитель во всех механизмах (write-набор
// сабагента, роутинг oh-my-claudecode, git-секция), но различаются подписью и шаблоном
// прав (SpecialtyCatalog). Подписи и шаблоны — в SpecialtyCatalog, значения добавлены
// в конец без миграции данных.
public enum PersonaSpecialty
{
    None, Analyst, Planner, Reviewer, Executor, Secretary,
    Coordinator, Mentor, Designer, Consultant, Librarian, Tester,
    BackendExecutor, FrontendExecutor, DevopsExecutor
}

// Структурированный контракт персоны (P1): характер разложен по слотам, каждый слот
// становится своей секцией системного промпта (PersonaPromptBuilder). null у персоны —
// legacy-режим: весь характер живёт единым текстом в Persona.SystemPrompt.
public class PersonaContract
{
    // Характер и манера общения — основной свободный текст
    public string? Character { get; set; }
    // Тон (краткая формула: «тепло и на равных», «сухо и по делу»)
    public string? Tone { get; set; }
    // Правила «всегда делай …» — по пункту на строку
    public List<string>? MustDo { get; set; }
    // Правила «никогда не …»
    public List<string>? MustNot { get; set; }
    // Требования к формату ответов (структура, длина, оформление)
    public string? OutputFormat { get; set; }
    // Примеры реплик персоны — образцы стиля (не готовые ответы)
    public List<string>? SpeechExamples { get; set; }
    // Полный регламент роли (длинный markdown, сотни строк) — для «тяжёлых» ролей
    // вроде пантеона OmO; короткие слоты выше остаются визиткой для карточек UI
    public string? Instructions { get; set; }

    // Все слоты пустые — контракт эквивалентен отсутствию (нормализуется в null)
    [JsonIgnore]
    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(Character)
        && string.IsNullOrWhiteSpace(Tone)
        && (MustDo is null || MustDo.All(string.IsNullOrWhiteSpace))
        && (MustNot is null || MustNot.All(string.IsNullOrWhiteSpace))
        && string.IsNullOrWhiteSpace(OutputFormat)
        && (SpeechExamples is null || SpeechExamples.All(string.IsNullOrWhiteSpace))
        && string.IsNullOrWhiteSpace(Instructions);
}
