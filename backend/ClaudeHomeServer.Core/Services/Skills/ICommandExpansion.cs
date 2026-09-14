namespace ClaudeHomeServer.Services.Skills;

// Разворачивает команду вида /skill-name [args] в полный текст (как делает Claude Code CLI
// при старте хода — текст заворачивается в <command-message>/<command-name> маркеры).
// Возвращает null, если сообщение не вызов скилла или скилл не найден.
//
// Сейчас закрывает TryExpandSkill SkillsService (Llm зовёт его при отправке хода, чтобы
// модель увидела содержимое скилла). Шов узкий: один метод, один возврат. Llm держит его
// как опциональную зависимость — без него сообщение идёт в ход неизменённым (как у текущего
// кода при skills=null: поведение прежнее, деградация не падает).
public interface ICommandExpansion
{
    string? ExpandSkill(string message);
}
