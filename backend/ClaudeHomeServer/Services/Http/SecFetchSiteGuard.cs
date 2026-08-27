namespace ClaudeHomeServer.Services.Http;

/// <summary>
/// Можно ли доверять куке аутентификации в этом запросе.
///
/// Зачем это нужно поверх <c>SameSite=Strict</c>. SameSite считает границей САЙТ
/// (регистрируемый домен), а не адрес: для браузера «svc.example.me» и «example.me» — один
/// сайт, и Strict запрос между ними не останавливает. А код на поддомене нам чужой
/// полностью — там живёт дев-сервер проекта со своими зависимостями.
///
/// Без этой проверки страница поддомена могла бы дёрнуть «/preview/**» или
/// «/telemetry-proxy/**» с credentials: браузер приложил бы куку сам, и запрос выполнился бы
/// от лица владельца. Ответ ей закроет CORS, но действие-то произойдёт — а простой POST
/// уходит вообще без предварительной проверки.
///
/// Заголовок <c>Sec-Fetch-Site</c> ставит сам браузер, и подделать его из скрипта нельзя:
/// он в списке запрещённых для JS.
/// </summary>
public static class SecFetchSiteGuard
{
    public const string HeaderName = "Sec-Fetch-Site";

    /// <summary>
    /// true — куке в этом запросе можно верить.
    ///
    /// Отсутствие заголовка НЕ считается отказом: его не шлют не-браузерные клиенты (curl,
    /// сервисные вызовы) и старые браузеры. Правило «нет заголовка — отказ» сломало бы
    /// законные сценарии ради угрозы, которой без браузера и не существует.
    /// </summary>
    public static bool CookieAuthAllowed(HttpRequest request)
    {
        var site = request.Headers[HeaderName].ToString();
        if (string.IsNullOrEmpty(site)) return true;

        // same-origin — запрос со страницы того же адреса (наш iframe и его сабресурсы).
        // Всё остальное (same-site — соседний поддомен, cross-site — чужой сайт,
        // none — ввод адреса руками) куку использовать не должно.
        return site.Equals("same-origin", StringComparison.OrdinalIgnoreCase);
    }
}
