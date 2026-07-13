using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClaudeHomeServer.Models;

// Источник события правила автоматизации персоны (Phase 1 — шесть источников).
// Таймер — по расписанию; File/Note/GitCommit/TaskStatus — poll-snapshot-дифф на тике;
// Mention — push (подписка на SessionManager.OnUserMessage).
public enum AutomationTriggerType { Timer, File, Note, GitCommit, TaskStatus, Mention }

// Тяжесть действия: Gate — one-shot гейт «стоит ли реагировать?» и сообщение в чат правила;
// Work — полный агентский ход (~15с старт CLI, полный MCP: правит файлы, создаёт задачи/заметки).
public enum AutomationActionWeight { Gate, Work }

// Триггер правила: «когда случилось {Type} с параметрами {Args}».
// Args — гибкий JSON-мешок, ключи зависят от Type (на фронте — discriminated union):
//   Timer:     schedule { type:"daily"|"weekdays"|"weekly"|"interval",
//                          time:"HH:mm", weekdays:[1..7], intervalMinutes } , tz?
//   File:      projectId, glob:"src/**/*.ts", kinds:["created","changed"]
//   Note:      source:"personal"|projectId, tags?:["#тег"], section?:папка
//   GitCommit: projectId, paths?:["src/**"]
//   TaskStatus:projectId?, from?:<status>, to?:<status>, assignee?:"me"|"claude"
//   Mention:   {} (детектится автоматически по handle персоны-владельца правила)
public class AutomationTrigger
{
    public AutomationTriggerType Type { get; set; } = AutomationTriggerType.Timer;
    public Dictionary<string, JsonElement>? Args { get; set; }
}

// Условие фильтрации срабатывания (опционально, поверх триггера). All-clean → null.
public class AutomationCondition
{
    // Свободный текст-предикат для LLM-гейта («реагируй, только если касается деплоя»).
    // null/пусто — без доп. условия; гейт всё равно спрашивает «стоит ли реагировать».
    public string? OnlyIf { get; set; }
    // Тихие часы (per-rule, местное время владельца): не срабатывать в диапазоне [From..To]
    public string? QuietFrom { get; set; }   // "23:00"
    public string? QuietTo { get; set; }     // "07:00" (допускается переход через полночь)
    // Минимальный интервал между срабатываниями — троттлинг per-rule (null → дефолт сервиса)
    public int? MinIntervalMinutes { get; set; }

    [JsonIgnore]
    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(OnlyIf)
        && string.IsNullOrWhiteSpace(QuietFrom)
        && string.IsNullOrWhiteSpace(QuietTo)
        && MinIntervalMinutes is null;
}

// Действие правила: что делать, когда триггер сработал.
public class AutomationAction
{
    public AutomationActionWeight Weight { get; set; } = AutomationActionWeight.Gate;
    // Инструкция персоне на реакцию (подмешивается к gate-промпту и к полному ходу)
    public string Instruction { get; set; } = "";
    // Записать карточку-итог срабатывания в историю чата (AppendStoredAsync)
    public bool RememberInHistory { get; set; }
}

// Правило автоматизации персоны. Конфигурация (не runtime-состояние): персистится в
// Persona.AutomationRules через PersonaManager.UpdateRules. Состояние срабатываний
// (LastFiredAt, счётчики, снапшоты) — отдельно в data/persona-automation-state.json.
public class PersonaAutomationRule
{
    public string Id { get; init; } = Guid.NewGuid().ToString();
    public bool Enabled { get; set; } = true;
    // Человекочитаемое имя («Следить за релизами») — для списка и заголовка чата правила
    public string Name { get; set; } = "";
    public AutomationTrigger Trigger { get; set; } = new();
    public AutomationCondition? Condition { get; set; }
    public AutomationAction Action { get; set; } = new();
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
