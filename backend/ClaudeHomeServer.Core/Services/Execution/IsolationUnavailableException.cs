namespace ClaudeHomeServer.Services.Execution;

// Изоляция включена и обёртка применима, но изолированный запуск сейчас невозможен:
// fail-closed — процесс НЕ запускается без изоляции (OOM-инцидент 2026-09-19 — ровно то,
// ради чего scope существует; пуск в cgroup прода и есть защищаемый отказ).
//
// Reason=SliceFrozen: выбранный slice заморожен (окна бенча, инцидент 21.11/tb21) —
// systemd 259 отказывает запустить юнит в замороженном срезе: новый scope помечается
// FREEZER_FROZEN_BY_PARENT, unit_start() отдаёт -EDEADLK, журнал юнита говорит
// «Cannot start frozen unit», а stderr systemd-run — «Cannot perform operation on frozen
// unit» (проверено на хосте, systemd 259.9). Запуск удастся после оттаивания — вызывающий
// (S2) трактует это как «ресурс недоступен сейчас», а не как сломанную среду.
public sealed class IsolationUnavailableException : Exception
{
    public const string SliceFrozen = "SliceFrozen";

    public string Reason { get; }
    public string Slice { get; }

    public IsolationUnavailableException(string reason, string message, string? slice = null)
        : base(message)
    {
        Reason = reason;
        Slice = slice ?? string.Empty;
    }

    // Классификация отказа по замороженному срезу по тексту — одно правило для пробы (S1)
    // и для stderr живого запуска (страховка S2). Два маркера подтверждены на хосте:
    // серверная формулировка (журнал юнита) и клиентская (stderr systemd-run, без --quiet).
    public static bool IsFrozenUnitRefusal(string? text) =>
        text is not null
        && (text.Contains("Cannot start frozen unit", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Cannot perform operation on frozen unit", StringComparison.OrdinalIgnoreCase));
}
