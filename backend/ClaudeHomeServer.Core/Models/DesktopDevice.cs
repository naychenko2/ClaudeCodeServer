namespace ClaudeHomeServer.Models;

/// <summary>
/// Устройство десктопного агента (ADR-008, «Аутентификация и транспорт»): машина владельца,
/// на которой живёт клиент с руками. Хранится в data/devices.json — по одной записи на
/// сопряжение, включая отозванные (надгробия, см. <see cref="Revoked"/>).
///
/// Секрета токена в записи нет никогда: только SHA-256 <see cref="TokenHash"/>. Сам токен
/// показывается ровно один раз — в ответе на обмен кода сопряжения.
/// </summary>
public class DesktopDevice
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Владелец: реестр устройств per-owner, чужие записи недостижимы.</summary>
    public string OwnerId { get; set; } = "";

    /// <summary>
    /// Человеческое имя («home», «work») — оно же параметр <c>device</c> у MCP-инструментов,
    /// а не GUID. Уникально у владельца среди неотозванных, сравнение регистронезависимое.
    /// </summary>
    public string Name { get; set; } = "";

    /// <summary>SHA-256 (hex, нижний регистр) от секрета device-токена.</summary>
    public string TokenHash { get; set; } = "";

    /// <summary>
    /// Монотонная версия токена: растёт при каждой выдаче (первое сопряжение, повторное
    /// сопряжение той же машины, отзыв) и никогда не убывает. Версия входит в сам токен,
    /// поэтому токен прошлой выдачи не оживает ни при повторном сопряжении, ни при
    /// восстановлении архива, снятого после отзыва.
    /// </summary>
    public int TokenVersion { get; set; } = 1;

    /// <summary>
    /// Отпечаток машины (SHA-256 от имени машины) — сверяется при каждом обращении
    /// устройства, а не просто хранится. Совпадение с отпечатком хоста бэкенда запрещено
    /// на сопряжении: «руки» на той же машине, где сервер, — это обход изоляции, а не грань.
    /// </summary>
    public string MachineFingerprint { get; set; } = "";

    /// <summary>Версия клиента, назвавшаяся при сопряжении (диагностика поддержки).</summary>
    public string? ClientVersion { get; set; }

    // Сведения агента локальных проектов (ADR-016) — из его Hello. Поля аддитивные: у
    // записей до ADR-016 и у клиентов рук ADR-008 они пустые.

    /// <summary>Платформа агента (win-x64, linux-x64, osx-arm64…) — как её назвал агент.</summary>
    public string? Platform { get; set; }

    /// <summary>Версия агента локальных проектов; null — это не агент (клиент рук ADR-008).</summary>
    public string? AgentVersion { get; set; }

    /// <summary>
    /// Версия управляемой копии CLI в каталоге агента (null — копии нет). С требуемой
    /// версией сервера сверяется вживую, вердикт «харнес не готов» не хранится.
    /// </summary>
    public string? CliVersion { get; set; }

    /// <summary>Возможности агента (<c>exec</c>, <c>files</c>, <c>relay</c>) — только известные значения.</summary>
    public List<string> Capabilities { get; set; } = [];

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Последнее успешное обращение устройства (обновляется с троттлингом).</summary>
    public DateTime? LastSeenAt { get; set; }

    /// <summary>
    /// Отзыв — надгробие, а не удаление записи: удалённая запись вернулась бы живой из любого
    /// архива, снятого до отзыва, а надгробие переживает восстановление вместе со стором.
    /// </summary>
    public bool Revoked { get; set; }

    public DateTime? RevokedAt { get; set; }
}
