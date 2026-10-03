namespace ClaudeHomeServer.Services.ChatContext;

// Коды ошибок ручек контекста чата (ADR-023 §2.1): поле error в теле ответа.
public static class ChatContextErrors
{
    // 409: ревизия клиента устарела, тело — свежий ChatContextDto
    public const string ContextChanged = "context_changed";
    // 400: вид не зарегистрирован (вертикаль выключена или опечатка)
    public const string KindUnknown = "kind_unknown";
    // 400: Ref не прошёл Validate провайдера
    public const string RefInvalid = "ref_invalid";
    // 400: основной объект не принимает референс с такой ролью
    public const string RoleNotAccepted = "role_not_accepted";
    // 400: объект этого вида не может быть основным (персонаж, голос, файл проекта — только референсы)
    public const string KindNotPrimary = "kind_not_primary";
    // 400: референсов больше MaxRefs
    public const string RefsLimit = "refs_limit";
    // 400: локальный проект (ADR-016) — проектные виды отказывают до диска
    public const string ProjectLocalUnsupported = "project_local_unsupported";

    // Потолок референсов (потолок local_edit_image)
    public const int MaxRefs = 16;
}
