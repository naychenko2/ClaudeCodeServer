using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClaudeHomeServer.Models;

public class User
{
    public string Id { get; init; } = Guid.NewGuid().ToString();
    public string Username { get; set; } = "";
    // Отображаемое имя («Григорий») — показывается вместо логина в приветствии и меню
    // аватара. null/пусто — показываем Username. Логин остаётся идентификатором входа.
    public string? DisplayName { get; set; }
    public string PasswordHash { get; set; } = "";
    // Версия сессий: растёт при КАЖДОЙ смене пароля (своей и админского сброса). Выданный
    // токен несёт её в claim tv, и токен со старой версией отвергается — так смена пароля
    // отзывает входы на других устройствах. У записей, созданных до появления поля, — 0.
    public int TokenVersion { get; set; }
    public string Role { get; set; } = "user";
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    // NT-хэш пароля (легаси-поле NtHash) здесь больше не хранится: NTLM WebDAV делает
    // Negotiate/SSPI и хэш никто не читал, а users.json уезжает в облачный бэкап —
    // MD4(UTF-16LE(password)) ломается перебором и годится для pass-the-hash.
    // Остатки поля вычищает разовая миграция в UserStore.Load.
    // Per-user override фич-флагов поверх дефолтов из FeatureFlagCatalog; null/отсутствует — все по дефолту
    public Dictionary<string, bool>? FeatureFlags { get; set; }
    // Per-user слоты тиров моделей. null/пусто — наследовать глобальный слот инстанса.
    public string? ModelTierStrong { get; set; }
    public string? ModelTierMedium { get; set; }
    public string? ModelTierWeak { get; set; }
    // Per-user пороги индикатора заполнения контекста (проценты); null — дефолты фронта
    public ContextThresholds? ContextThresholds { get; set; }
    // Per-user порог «кэш промпта скоро остынет» (минуты остатка до остывания); null —
    // дефолты фронта (5 мин у часового, пятая доля срока у 5-минутного, F-1a).
    // Nullable: старые записи users.json читаются штатно, формат стора не меняется
    public CacheWarnThresholds? CacheWarnThresholds { get; set; }
    // Глобальный (per-user) промпт AI-генерации сообщения коммита; null/пусто — дефолт.
    // Проект может переопределить своим Project.CommitPromptOverride.
    public string? GitCommitPrompt { get; set; }
    // IANA-таймзона пользователя (например "Europe/Moscow") — фронт присылает при старте;
    // нужна планировщику для перевода локальных сроков задач в UTC. null — считаем UTC
    public string? TimeZone { get; set; }
    // Среда исполнения процессов пользователя (claude, терминал, dev-серверы):
    // local — на машине сервера с полным доступом; container — в общей Docker-песочнице.
    // Меняется только пока у пользователя нет чатов (корни проектов и профили сред различаются)
    public string ExecutionEnvironment { get; set; } = ExecutionEnvironments.Local;
    // Аккаунт на локальном git-сервере Forgejo (провижнится лениво при первом git/init).
    // Токен — персональный PAT со scope write:repository; хранится открыто (решение
    // владельца, консистентно с остальным users.json), в git/логи не попадает
    public string? ForgejoUsername { get; set; }
    public string? ForgejoToken { get; set; }
    // Пароль веб-входа в Forgejo (открыто, как токен) — приватные репо анониму отдают 404
    public string? ForgejoPassword { get; set; }
    // Личная дефолт-персона. «Дефолт есть» больше не равно «знакомство пройдено»:
    // заготовка-ассистент создаётся и назначается дефолтом сразу при провижне,
    // а знакомство превращает её в свою персону. Момент
    // знакомства — отдельное поле IntroCompletedAt. null — дефолта нет (или он осиротел).
    public string? DefaultPersonaId { get; set; }
    // Момент завершения знакомства с ассистентом (UTC). null — знакомство не пройдено
    // (предложение ещё актуально). Проставляется финализацией интервью, а пользователям,
    // уже обзаведшимся дефолтом к моменту появления фичи — миграцией UserStore.Load.
    public DateTime? IntroCompletedAt { get; set; }
    // Id нетронутой заготовки-ассистента. != null — персона создана провижном,
    // но интервью/ручной правкой ещё не превращена в свою.
    // Метка «Ваш ассистент пока стандартный» горит ⇔ IntroCompletedAt == null
    // && DefaultPersonaId == AssistantPersonaId && id резолвится в живую персону.
    // Снимается любым превращением: интервью, ручная правка Name/Role/характера, удаление,
    // назначение дефолтом другой персоны. Мёртвый id всюду резолвится, а не проверяется на
    // null — иначе висячий id запирал бы знакомство навсегда.
    public string? AssistantPersonaId { get; set; }
    // Сессия незавершённого онбординга пользователя — для резюма прерванного интервью.
    // Чистится при финализации онбординга (назначении дефолт-персоны из этой сессии).
    // Сирота (сессию удалили через DELETE /api/chats/{id}) НЕ ломает повторный вход:
    // OnboardingController.StartUser нормализует ссылку на чтении — GetOwned по
    // несуществующему id даёт null и создаёт свежую сессию (каскада на удаление не нужно).
    public string? OnboardingSessionId { get; set; }
    // Состав «Стены» (фича wall): id чатов в порядке монет рельсы. null/пусто — стена
    // не настроена. Мёртвые id (чат удалён/протух) не каскадятся — фильтруются лениво
    // при чтении и вычищаются при следующем сохранении (MyWallController).
    public List<string>? WallChatIds { get; set; }
    // Личный порог автоправила архивации чатов (флаг chat-auto-archive): он же дефолт
    // для проектов без своего Project.ArchiveAfterDays, он же правило для чатов вне
    // проектов. null — правило для такой сферы не настроено. Nullable с дефолтом:
    // старые записи users.json читаются штатно, стор уже в бэкапе.
    public int? ArchiveAfterDays { get; set; }
    // Момент первого прохода правила («Применить сейчас» в настройке, UTC). null —
    // кнопка ещё не нажималась: накопившиеся старые чаты правило САМО не разгребает
    // (решение человека, план v4 шаг 6), фоновый тик владельца не архивирует ничего,
    // пока первый проход не запущен явно.
    public DateTime? ArchiveRuleFirstRunAt { get; set; }
    // Быстрые фразы композера: готовые сообщения, отправляемые одним нажатием.
    // Порядок списка = порядок в попапе, необязательная Group даёт второй уровень.
    // null/пусто — фразы не заведены (попап предложит завести). Один набор на все чаты
    // владельца — привязки к проекту сознательно нет. Старые записи (список голых строк)
    // читает QuickPhraseJsonConverter.
    public List<QuickPhrase>? QuickPhrases { get; set; }
    // Избранные каналы раздела «Видео»: ключи вида "smotrim:1" в порядке показа в полосе.
    // null и пустой список — РАЗНОЕ: null значит «не настраивал» (полоса берёт дефолт
    // VideoFavorites.Defaults), пустой — «снял все звёздочки» и полоса честно пуста.
    // Мёртвые ключи (канал пропал из каталога) не каскадятся — фронт их просто не находит.
    public List<string>? FavoriteVideoChannels { get; set; }
}

// Значения User.ExecutionEnvironment
public static class ExecutionEnvironments
{
    public const string Local = "local";
    public const string Container = "container";
    public static bool IsValid(string? value) => value is Local or Container;
}

// Пороги подсветки индикатора контекста: warn — янтарь, danger — красный
public record ContextThresholds(int WarnPct, int DangerPct);

// Порог «остывает» циферблата кэша промпта: за сколько минут остатка кэш становится
// «остывающим» — отдельно для часового и 5-минутного срока. Дефолты живут на фронте
// (EXPIRING_THRESHOLD_MS в frontend/src/lib/promptCache.ts), здесь — только границы
// валидации. Порог обязан быть строго меньше срока: при ≥ TTL кэш был бы «остывающим»
// с момента записи.
// Контракт хранилища (users.json): {"warn1hMin": 1–59, "warn5mMin": 1–4} — минуты.
// На чтении нормализует CacheWarnThresholdsJsonConverter: старые имена origin-формата
// в секундах (warn1hSec/warn5mSec) переводятся в минуты (round) и зажимаются в диапазон,
// прямые значения вне диапазона и любое несоответствующее тело — null, то есть
// «не настроено».
[JsonConverter(typeof(CacheWarnThresholdsJsonConverter))]
public record CacheWarnThresholds(int Warn1hMin, int Warn5mMin)
{
    public const int MinMin = 1;
    public const int Max1hMin = 59;
    public const int Max5mMin = 4;

    // Метод, а не свойство: свойство уехало бы в users.json и ответ /me
    public static bool InRange(int warn1hMin, int warn5mMin) =>
        warn1hMin is >= MinMin and <= Max1hMin && warn5mMin is >= MinMin and <= Max5mMin;
}

/// <summary>
/// Читает stored-запись порога: старые имена origin-формата в секундах (warn1hSec/warn5mSec)
/// конвертируются в минуты (round) и зажимаются в диапазон — настройка человека не теряется
/// даже на краях перевода (30 с → 1 мин, 3600 с → 59 мин); всё остальное (прямые минуты
/// вне диапазона, полпары, мусор) — null, «не настроено», фронт применит дефолты.
/// Пишем всегда каноническую форму в минутах. Конвертер на самом типе, а не в опциях:
/// users.json пишется своими JsonSerializerOptions в UserStore, а API-ответы — чужими,
/// и разъехаться они не должны (тот же приём, что QuickPhraseJsonConverter).
/// </summary>
public class CacheWarnThresholdsJsonConverter : JsonConverter<CacheWarnThresholds>
{
    public override CacheWarnThresholds? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("Порог кэша — объект { warn1hMin, warn5mMin } в минутах");

        int? min1h = null;
        int? min5m = null;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject) break;
            if (reader.TokenType != JsonTokenType.PropertyName) continue;

            var name = reader.GetString();
            reader.Read();
            if (string.Equals(name, "warn1hMin", StringComparison.OrdinalIgnoreCase))
                min1h = ReadMinutes(reader);
            else if (string.Equals(name, "warn5mMin", StringComparison.OrdinalIgnoreCase))
                min5m = ReadMinutes(reader);
            else if (string.Equals(name, "warn1hSec", StringComparison.OrdinalIgnoreCase))
                min1h = ReadSecondsToMinutes(reader, CacheWarnThresholds.Max1hMin);
            else if (string.Equals(name, "warn5mSec", StringComparison.OrdinalIgnoreCase))
                min5m = ReadSecondsToMinutes(reader, CacheWarnThresholds.Max5mMin);
            else
                reader.Skip();
        }

        // Полпара или полупустая запись — не настройка: без обеих границ циферблат
        // не построить, отдаём null (дефолты фронта)
        return min1h is not null && min5m is not null && CacheWarnThresholds.InRange(min1h.Value, min5m.Value)
            ? new CacheWarnThresholds(min1h.Value, min5m.Value)
            : null;
    }

    private static int? ReadMinutes(Utf8JsonReader reader) =>
        reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out int value) ? value : null;

    // 300 с → 5 мин. Нечетные секунды округляются к ближайшей минуте, а результат
    // зажимается в диапазон, а не выбрасывается: край перевода 30 с → 0 мин — это
    // намерение «предупредить сразу», а не отсутствие настройки, поэтому нижняя граница
    // (1 мин) честнее null; верхняя — потолок срока (3600 с → 59 мин). Отрицательные
    // секунды — мусор, а не край перевода: их отсекаем до clamp, иначе −5 с зажимались
    // бы в 1 мин и стали валидной настройкой
    private static int? ReadSecondsToMinutes(Utf8JsonReader reader, int max) =>
        reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out int seconds) && seconds >= 0
            ? Math.Clamp((int)Math.Round(seconds / 60.0), CacheWarnThresholds.MinMin, max)
            : null;

    public override void Write(Utf8JsonWriter writer, CacheWarnThresholds value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteNumber("warn1hMin", value.Warn1hMin);
        writer.WriteNumber("warn5mMin", value.Warn5mMin);
        writer.WriteEndObject();
    }
}
