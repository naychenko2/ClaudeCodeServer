namespace ClaudeHomeServer.Services.Backup;

// Паспорт архива: кладётся внутрь zip и рядом с ним отдельным sidecar-файлом
// ({архив}.manifest.json), чтобы список бэкапов читался без распаковки.
public class BackupManifest
{
    // Отпечаток инстанса, снявшего архив (data/instance-id.txt). Гейт восстановления:
    // архив с чужим id в чужой инстанс не раскатывается — там абсолютные RootPath,
    // свои пользователи и своя карта датасетов Dify.
    public string InstanceId { get; set; } = "";

    // Версия формата сторов. Восстановление архива, снятого более новой версией
    // приложения, запрещено: незнакомые значения enum роняют десериализацию, а
    // JsonFileStore на этом молча отдаёт пустой стор (см. гейты в BackupCore).
    public int SchemaVersion { get; set; }

    public string AppVersion { get; set; } = "";
    public string Environment { get; set; } = "";
    public string DataPath { get; set; } = "";
    public string DifyNamespace { get; set; } = "";
    public DateTime CreatedAt { get; set; }

    // Чьи данные внутри — показывается при восстановлении, чтобы «архив prod, владелец
    // admin» нельзя было раскатать по невнимательности.
    public List<BackupOwner> Owners { get; set; } = [];

    public List<BackupFile> Files { get; set; } = [];

    // Человекочитаемый состав: по нему виджет показывает «12 чатов · 5 персон · 34 задачи»
    // без чтения архива.
    public BackupSummary Summary { get; set; } = new();
}

public class BackupOwner
{
    public string Id { get; set; } = "";
    public string Username { get; set; } = "";
}

public class BackupFile
{
    // Путь относительно корня data (разделитель — '/', чтобы zip читался одинаково везде)
    public string Path { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long Size { get; set; }
}

public class BackupSummary
{
    public int Chats { get; set; }
    public int Personas { get; set; }
    public int Tasks { get; set; }
    public int Notes { get; set; }
    public int Projects { get; set; }
    public int Users { get; set; }
    public long TotalBytes { get; set; }
}
