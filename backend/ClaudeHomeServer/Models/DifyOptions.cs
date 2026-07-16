namespace ClaudeHomeServer.Models;

public class DifyOptions
{
    public const string Section = "Dify";
    public string ApiUrl { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string IndexingTechnique { get; set; } = "high_quality";
    // Неймспейс контура в ОБЩЕМ Dify (Dev/Prod на одном инстансе: воркспейсы через API
    // недоступны). Непустой (например "dev") прозрачно добавляет префикс "dev:" ко всем
    // создаваемым датасетам и ограничивает листинг только своим неймспейсом; весь остальной
    // код работает с логическими именами ({username}:…) без префикса.
    public string Namespace { get; set; } = "";
    // Чужие неймспейсы, скрываемые при ПУСТОМ своём: прод без префикса не должен видеть
    // dev-датасеты как публичные (классификация раздела «Знания» идёт по имени).
    public List<string> ForeignNamespaces { get; set; } = [];
}
