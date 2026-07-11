using System.Text.Json.Serialization;

namespace ClaudeHomeServer.Models;

// Зона контекста персоны: Global — доступ ко всем данным владельца (заметки/задачи/проекты),
// Project — только к своему проекту.
public enum PersonaScope { Global, Project }

// Тип долгой памяти (таксономия 2026):
// Semantic — устойчивые факты/предпочтения; Episodic — резюме прошлых диалогов;
// Procedural — выученные приёмы/правила поведения.
public enum PersonaMemoryType { Semantic, Episodic, Procedural }

// Вид аватара: Initials — круг с инициалами на цветном фоне; Image — сгенерированная/загруженная картинка.
public enum PersonaAvatarKind { Initials, Image }

// Тип привязки персоны к источнику знаний или правилу (фича persona-bindings):
// Project — проект целиком (файлы через workspace); ProjectPath — папка/файл проекта;
// Knowledge — Dify-датасет (база знаний проекта или заметок); Notes — источник заметок;
// Tool — рубильник инструмента (tasks/notes/web/…); Skill — глобальный скилл (~/.claude/skills).
public enum PersonaBindingType { Project, ProjectPath, Knowledge, Notes, Tool, Skill }

// Режим привязки: Auto — источник в индексе, персона подгружает по условию;
// Always — вдобавок выжимка из источника подмешивается в каждый ход; Off — выключена.
public enum PersonaBindingMode { Auto, Always, Off }

// Привязка персоны: «когда {Condition} — используй {Target}». Target по типам:
// Project/ProjectPath → projectId; Knowledge → datasetId; Notes → sourceKey
// ("personal" | projectId); Tool → ключ инструмента; Skill → имя скилла.
public class PersonaBinding
{
    public string Id { get; init; } = Guid.NewGuid().ToString();
    public PersonaBindingType Type { get; set; }
    public string Target { get; set; } = "";
    // Путь внутри цели: папка/файл проекта (ProjectPath) или папка источника заметок (Notes)
    public string? Path { get; set; }
    // Условие «когда применять источник» — попадает в индекс системного промпта
    public string Condition { get; set; } = "";
    public PersonaBindingMode Mode { get; set; } = PersonaBindingMode.Auto;
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

// Профиль доступа персоны (P6): Full — без ограничений; ReadOnly — смотрит и советует,
// но ничего не меняет (без правок файлов, Bash и мутаций задач/заметок/персон);
// Custom — свой список запрещённых инструментов (Persona.DisallowedTools).
public enum PersonaAccess { Full, ReadOnly, Custom }

// Состояние кропа загруженного аватара: масштаб и смещение центра окна
// от центра картинки (в пикселях исходника) — для «Перекроить» без перезагрузки файла.
public class AvatarCropState
{
    public double Scale { get; set; } = 1;
    public double OffsetX { get; set; }
    public double OffsetY { get; set; }
}

// Внешний вид персоны.
public class PersonaAvatar
{
    public PersonaAvatarKind Kind { get; set; } = PersonaAvatarKind.Initials;
    // Ключ цвета из палитры AGENT_COLORS фронта (yellow/orange/blue/green/purple/red/brown/cyan/pink)
    public string? Color { get; set; }
    // Имя файла картинки в data/personas/{id}/ (когда Kind == Image)
    public string? ImageFile { get; set; }
    // Оригинал загруженного файла (для перекропа); у сгенерированных аватаров — null
    public string? OriginalFile { get; set; }
    // Параметры кропа, которыми получен ImageFile из OriginalFile
    public AvatarCropState? Crop { get; set; }
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

    // Все слоты пустые — контракт эквивалентен отсутствию (нормализуется в null)
    [JsonIgnore]
    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(Character)
        && string.IsNullOrWhiteSpace(Tone)
        && (MustDo is null || MustDo.All(string.IsNullOrWhiteSpace))
        && (MustNot is null || MustNot.All(string.IsNullOrWhiteSpace))
        && string.IsNullOrWhiteSpace(OutputFormat)
        && (SpeechExamples is null || SpeechExamples.All(string.IsNullOrWhiteSpace));
}

// Тип расписания проактивности: каждый день / по будням / по выбранным дням недели.
public enum PersonaScheduleType { Daily, Weekdays, Weekly }

// Проактивность персоны (флаг persona-proactive): «пишет первой» по расписанию.
// Пользовательские поля — Enabled/Type/Weekdays/Time/Instruction; служебные —
// LastFiredAt (идемпотентность срабатываний) и SessionId (закреплённый чат) —
// при обновлении через API не затираются (partial-merge в PersonaManager.Update).
public class PersonaProactiveConfig
{
    public bool Enabled { get; set; }
    public PersonaScheduleType Type { get; set; } = PersonaScheduleType.Daily;
    // ISO-дни недели (1=Пн … 7=Вс) — только для Type == Weekly
    public List<int>? Weekdays { get; set; }
    // Локальное время срабатывания в таймзоне владельца, "HH:mm"
    public string Time { get; set; } = "09:00";
    // Что сделать при срабатывании (пустая — триггер не срабатывает)
    public string Instruction { get; set; } = "";
    // UTC-отметка последнего срабатывания (идемпотентность, переживает рестарт)
    public DateTime? LastFiredAt { get; set; }
    // Чат, в котором персона пишет по расписанию (создаётся при первом срабатывании)
    public string? SessionId { get; set; }
}

// Персона — сущность с именем, внешностью, характером, своей памятью и зоной контекста.
// Не путать с .md-агентами Claude Code (.claude/agents) — те подключаются через Session.AgentName.
public class Persona
{
    public string Id { get; init; } = Guid.NewGuid().ToString();
    public string OwnerId { get; set; } = "";
    public string Name { get; set; } = "";
    // Роль персоны (главная в отображении: «Роль (Имя)»), напр. «Дизайнер», «PM». Опционально.
    public string? Role { get; set; }
    // Короткий slug (латиница/цифры/дефис) — для Dify-датасета памяти и будущих @упоминаний
    public string Handle { get; set; } = "";
    // Краткое «кто это» — для карточки в списке
    public string? Description { get; set; }
    // Характер/роль/стиль — тело персоны, инжектится в системный промпт сессии.
    // Legacy-поле: у персон с Contract != null игнорируется (источник правды — контракт)
    public string? SystemPrompt { get; set; }
    // Структурированный контракт (P1); null — legacy-режим с единым SystemPrompt
    public PersonaContract? Contract { get; set; }
    // Модель CLI (алиас/id любого провайдера); null = дефолт сервера
    public string? Model { get; set; }
    public string? Effort { get; set; }
    public PersonaScope Scope { get; set; } = PersonaScope.Global;
    // Для Scope == Project — id проекта, к которому привязана персона
    public string? ProjectId { get; set; }
    public PersonaAvatar Avatar { get; set; } = new();
    // Возможности персоны (ключи: tasks, notes, web). null — без ограничений
    // (как раньше, по фич-флагам владельца); список — только перечисленные.
    public List<string>? Tools { get; set; }
    // Привязки к источникам знаний и правилам (фича persona-bindings).
    // null — привязок нет (миграция стора не нужна, поведение как раньше).
    public List<PersonaBinding>? Bindings { get; set; }
    // Профиль доступа (P6): Full/ReadOnly/Custom (см. PersonaAccessPolicy)
    public PersonaAccess Access { get; set; } = PersonaAccess.Full;
    // Свой список запрещённых инструментов — только при Access == Custom
    public List<string>? DisallowedTools { get; set; }
    // Первое приветственное сообщение при открытии чата (опционально)
    public string? Greeting { get; set; }
    // Проактивность «пишет первой» (флаг persona-proactive); null — выключена
    public PersonaProactiveConfig? Proactive { get; set; }
    public bool MemoryEnabled { get; set; } = true;
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

// Рабочий фокус персоны (P3) — «что я сейчас делаю»: одна ячейка рабочей памяти.
// Живёт в persona-memory.json (MemState), НЕ является записью памяти и НЕ полем Persona;
// в recall подмешивается первым блоком без скоринга.
public class PersonaWorkingFocus
{
    // Чем занята персона (незавершённое дело)
    public string What { get; set; } = "";
    // Текущий статус дела
    public string Status { get; set; } = "";
    // Следующий шаг (опционально)
    public string? NextStep { get; set; }
    // Сессия, из которой фокус был выставлен (для трассировки)
    public string? SourceSessionId { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

// Запись долгой памяти персоны. Хранится в data/persona-memory-{personaId}.json;
// семантическая часть дублируется в Dify-датасет для векторного retrieve.
public class PersonaMemoryEntry
{
    public string Id { get; init; } = Guid.NewGuid().ToString();
    public string PersonaId { get; set; } = "";
    public PersonaMemoryType Type { get; set; } = PersonaMemoryType.Semantic;
    public string Text { get; set; } = "";
    public List<string>? Tags { get; set; }
    // Значимость записи (0..1), влияет на скоринг при recall
    public double Salience { get; set; } = 1.0;
    // Сессия, из которой факт был запомнен (для трассировки)
    public string? SourceSessionId { get; set; }
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime LastAccessedAt { get; set; } = DateTime.UtcNow;
}
