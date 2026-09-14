namespace ClaudeHomeServer.Models;

// Питание машины из веб-морды — секция «PowerControl». Тот же приём, что у TrayDeployOptions:
// главный замок — конфигурация, а не отсутствие кода. Код уезжает всем, у кого свой инстанс,
// поэтому по умолчанию фича выключена, а включается в appsettings.Local.json (вне git) на той
// машине, хозяин которой действительно хочет гасить её кнопкой.
//
// "PowerControl": {
//   "Enabled": true,
//   "DelaySeconds": 60
// }
public class PowerControlOptions
{
    public const string Section = "PowerControl";

    // Главный рубильник. Выключено — пункта меню нет, а POST отвечает 404.
    public bool Enabled { get; set; }

    // Отсрочка перед выполнением: пункт меню легко задеть промахом, а последствие необратимо и
    // отменять его будет некому — машина стоит дома, а нажавший сидит далеко. Отсчёт ведёт сам
    // сервер (см. PowerControlService), поэтому отмена работает одинаково для всех трёх действий,
    // включая сон, у которого встроенной отсрочки нет вовсе.
    public int DelaySeconds { get; set; } = 60;

    /// <summary>Отсрочка в разумных границах: 0 — сразу, потолок в час от откровенной опечатки.</summary>
    public int EffectiveDelaySeconds => Math.Clamp(DelaySeconds, 0, 3600);
}
