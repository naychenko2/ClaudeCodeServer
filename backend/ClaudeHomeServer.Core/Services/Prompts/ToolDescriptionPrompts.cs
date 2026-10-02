namespace ClaudeHomeServer.Services.Prompts;

// Правило про поле description у вызовов инструментов: класс-константа по образцу
// WatchPrompts — шов под юнит-тест, сборка секций внутри ClaudeSession шва не имеет.
// Поле доезжает до фронта в item.input и показывается живой подписью к идущему
// инструменту (шапка карточки, индикатор ожидания) — поэтому по-русски и коротко.
// Живёт в Core, а не в ClaudeHomeServer.Prompts: сборка Llm на Prompts не ссылается.
public static class ToolDescriptionPrompts
{
    // Секция системного промпта хода (ключ "tool-descriptions")
    public const string SectionText =
        "Поле `description` у вызовов инструментов (Bash, PowerShell, Agent и др.) пиши по-русски: " +
        "3–8 слов, что делает действие, без повторения самой команды. " +
        "Пример: \"Синхронизирую транскрипты в sub-claude\".";
}
