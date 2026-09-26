using ClaudeHomeServer.DeviceAgent.Credentials;
using ClaudeHomeServer.DeviceAgent.Hosting;
using ClaudeHomeServer.DeviceAgent.Pairing;
using ClaudeHomeServer.DeviceAgent.Supervision;

namespace ClaudeHomeServer.DeviceAgent.Install;

/// <summary>
/// Коды выхода <c>install</c> — по ним скрипты установки (AD-5s) выбирают, что сказать.
/// 2 и 64 — общие с остальными командами агента.
/// </summary>
internal static class InstallExitCodes
{
    public const int Ok = 0;
    public const int Failed = 1;
    public const int PairingFailed = 2;
    /// <summary>Установлен, но сейчас не запущен: окно установки не отпустило процесс (Job без breakaway).</summary>
    public const int StartDeferred = 20;
    /// <summary>Установлен и запущен, но первого успешного hello за 30 с не было.</summary>
    public const int NoHelloYet = 21;
    public const int Usage = 64;
}

internal sealed record InstallRequest(Uri Server, string Code, string DeviceName, bool AlwaysOn);

/// <summary>
/// <c>install --server --code [--name] [--always-on]</c>: вся логика установки, скрипт лишь
/// распаковал версию в <c>versions/{v}</c> и вызвал её (Р10). Шаги:
/// сопряжение → выбор хранилища токена → указатель <c>active</c> → команда <c>ai-home-agent</c>
/// по имени → автозапуск → отсоединённый запуск супервизора → ожидание первого успешного
/// hello, не дольше 30 с.
/// Переустановка поверх работающего агента — новое сопряжение, старый супервизор гасится.
/// </summary>
internal sealed class AgentInstaller(
    AgentPaths paths,
    AgentLayout layout,
    string ownVersion,
    IAutostart autostart,
    ICommandShim shim,
    ISupervisorControl supervisors,
    Func<InstallRequest, IDeviceTokenStore, Task<DeviceRegistration>> pair,
    TextWriter output,
    ISupervisorClock? clock = null)
{
    public static readonly TimeSpan HelloTimeout = TimeSpan.FromSeconds(30);

    private readonly ISupervisorClock _clock = clock ?? SystemSupervisorClock.Instance;

    public async Task<int> RunAsync(InstallRequest request, CancellationToken ct)
    {
        if (!layout.IsInstalled(ownVersion))
        {
            output.WriteLine($"Нет {layout.ExeOf(ownVersion)}: install запускается из каталога версии, который готовит скрипт установки");
            return InstallExitCodes.Failed;
        }

        // 1–2. Сопряжение, токен — в хранилище, выбранное с учётом --always-on
        var (store, kind) = DeviceTokenStores.Choose(paths.ConfigDirectory, request.AlwaysOn);
        var registration = await pair(request, store) with { TokenStore = kind };
        registration.Save(paths.RegistrationFile);
        output.WriteLine($"Устройство «{registration.DeviceName}» сопряжено с {registration.ServerUrl}; токен — {store.Describe}");

        // Старый супервизор (переустановка) держал бы блокировку и прежнюю версию
        if (supervisors.Stop()) output.WriteLine("Прежний супервизор агента остановлен");

        // 3. Указатель active; маркеры сбрасываются, чтобы «первый hello» был именно этого сопряжения
        layout.ClearMarkers(ownVersion);
        layout.SetActive(ownVersion);
        output.WriteLine($"Активная версия — {ownVersion}");

        // 4. Шим после active: на Linux он смотрит в симлинк current, который SetActive и ставит
        foreach (var note in CommandShimNotes.Install(shim)) output.WriteLine(note);

        // 5. Автозапуск
        var registered = autostart.Register(ownVersion, request.AlwaysOn);
        if (registered.Registered) output.WriteLine($"Автозапуск: {autostart.Describe}");
        foreach (var note in registered.Notes) output.WriteLine(note);

        // 6. Супервизор — отсоединённо от окна и сессии установки
        var start = autostart.StartNow(ownVersion);
        output.WriteLine(start.Message);
        switch (start.Status)
        {
            case SupervisorStartStatus.Deferred: return InstallExitCodes.StartDeferred;
            case SupervisorStartStatus.Manual: return InstallExitCodes.Ok;
        }

        // 7. Первый успешный hello — дочерний пишет маркер healthy после ack сервера
        if (await WaitHealthyAsync(ct))
        {
            output.WriteLine("Агент на связи с сервером");
            return InstallExitCodes.Ok;
        }
        output.WriteLine($"Агент запущен, но за {HelloTimeout.TotalSeconds:0} с сервер его не принял. " +
            $"Он продолжит попытки сам; журнал — {layout.LogDirectory}");
        return InstallExitCodes.NoHelloYet;
    }

    private async Task<bool> WaitHealthyAsync(CancellationToken ct)
    {
        var deadline = _clock.Now + HelloTimeout;
        while (!layout.IsHealthy(ownVersion))
        {
            if (_clock.Now >= deadline) return false;
            await _clock.Delay(TimeSpan.FromMilliseconds(500), ct);
        }
        return true;
    }
}

/// <summary>
/// <c>uninstall [--purge]</c> (Р12): снять автозапуск, погасить супервизор, отозвать своё
/// устройство на сервере, стереть токен, снять команду <c>ai-home-agent</c>. <c>--purge</c> — ещё и данные: копии CLI, журнал,
/// профиль CLI с транскриптами, версии агента.
/// </summary>
internal sealed class AgentUninstaller(
    AgentPaths paths,
    AgentLayout layout,
    IAutostart autostart,
    ICommandShim shim,
    ISupervisorControl supervisors,
    ISelfRevoke revoke,
    Func<string?, IDeviceTokenStore> openStore,
    TextWriter output)
{
    public const string PurgeWarning =
        "Внимание: --purge удаляет данные агента — версии, копии CLI, журнал и профиль CLI с транскриптами " +
        "локальных чатов. Восстановить их будет нельзя.";

    public async Task<int> RunAsync(bool purge, CancellationToken ct)
    {
        if (purge) output.WriteLine(PurgeWarning);

        // Сначала автозапуск: под systemd убитый процесс иначе поднялся бы снова (Restart=on-failure)
        autostart.Unregister();
        output.WriteLine($"Автозапуск снят: {autostart.Describe}");
        if (supervisors.Stop()) output.WriteLine("Супервизор агента остановлен");

        if (DeviceRegistration.Load(paths.RegistrationFile) is { } registration)
        {
            var store = openStore(registration.TokenStore);
            if (store.Read(registration.DeviceId) is { } token)
            {
                var (status, error) = await revoke.RevokeAsync(new Uri(registration.ServerUrl), token, registration.Fingerprint, ct);
                output.WriteLine(status switch
                {
                    SelfRevokeStatus.Revoked => $"Устройство «{registration.DeviceName}» отозвано на {registration.ServerUrl}",
                    SelfRevokeStatus.AlreadyGone => "Сервер уже не знает этот токен — устройство отозвано раньше",
                    _ => $"Устройство на сервере не отозвано ({error}): отзови его в веб-интерфейсе, раздел «Устройства»",
                });
            }
            store.Delete(registration.DeviceId);
            File.Delete(paths.RegistrationFile);
            output.WriteLine("Токен устройства стёрт");
        }
        else
        {
            output.WriteLine("Агент не был сопряжён — отзывать на сервере нечего");
        }

        try
        {
            shim.Remove();
            output.WriteLine($"Команда ai-home-agent снята: {shim.Location}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            output.WriteLine($"Команда ai-home-agent не снята ({e.Message}) — удали {shim.Location} руками");
        }

        if (purge) Purge();
        output.WriteLine("Агент удалён");
        return 0;
    }

    private void Purge()
    {
        foreach (var dir in new[] { layout.Root, paths.DataDirectory, paths.ConfigDirectory }.Distinct())
        {
            if (!Directory.Exists(dir)) continue;
            try
            {
                Directory.Delete(dir, recursive: true);
                output.WriteLine($"Удалено: {dir}");
            }
            // Windows не даёт удалить каталог запущенной версии — это не ошибка удаления агента
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                output.WriteLine($"Удалено не всё в {dir} ({e.Message}) — удали остаток руками после выхода");
            }
        }
    }
}

internal static class CommandShimNotes
{
    /// <summary>
    /// Поставить шим, не роняя вызывающего: без команды по имени агент работает, просто
    /// подсказки UI придётся выполнять по полному пути.
    /// </summary>
    public static IReadOnlyList<string> Install(ICommandShim shim)
    {
        try
        {
            return shim.Install();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [$"Команда ai-home-agent по имени не поставлена ({e.Message}); агент работает, запускай его по полному пути"];
        }
    }
}
