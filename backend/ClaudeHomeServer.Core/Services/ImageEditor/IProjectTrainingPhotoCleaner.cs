namespace ClaudeHomeServer.Services.ImageEditor;

// Шов «уборка обучающих фото при удалении проекта» (ADR-026, раздел 5): фото персонажей для LoRA
// лежат в data/lora-training/<owner>/<project>/ вне проекта, а удаляет проект Main, который на
// вертикаль ImageEditor не ссылается. Реализация — в ImageEditor; нет подсистемы — нет и
// регистрации, контроллер получает null и пропускает шаг. Нет каталога — не ошибка.
public interface IProjectTrainingPhotoCleaner
{
    void PurgeProject(string ownerId, string projectId);
}
