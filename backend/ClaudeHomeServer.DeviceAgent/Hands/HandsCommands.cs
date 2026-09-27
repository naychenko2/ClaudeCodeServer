using ClaudeHomeServer.DeviceAgent.Pairing;

namespace ClaudeHomeServer.DeviceAgent.Hands;

/// <summary>
/// Команды машины <c>ai-home-agent hands enable|disable|status</c> — машинный выключатель рук
/// (решение владельца 1в). С сервера недоступны: ставит и убирает компонент только человек у
/// машины. Идущий агент замечает перемену сам и объявляет возможность в hello.
/// </summary>
internal sealed class HandsCommands(
    HandsComponent component,
    Func<Uri?> server,
    HttpClient http,
    string agentVersion,
    TextWriter output,
    TextWriter error,
    bool handsSupported)
{
    public const string Usage = "ai-home-agent hands enable | disable | status";

    public async Task<int> RunAsync(string[] args, CancellationToken ct) => args switch
    {
        ["enable"] => await EnableAsync(ct),
        ["disable"] => Disable(),
        ["status"] or [] => Status(),
        _ => Fail(Usage, 64),
    };

    /// <summary>
    /// Скачать компонент по манифесту: путь, размер и SHA-256 архива сервер прислал агенту в ответ
    /// на hello (канал устройства), сам архив качается анонимной ручкой <c>/agent/…</c>.
    /// </summary>
    private async Task<int> EnableAsync(CancellationToken ct)
    {
        if (!handsSupported)
            return Fail(HandsAttach.UnsupportedText);

        var offer = component.ReadOffer();
        if (offer is null)
            return Fail("Сервер ещё не прислал сведения о компоненте рук: запусти агента, дождись связи с сервером " +
                        "и повтори. Если не помогает — сервер компонент рук не раздаёт.");
        if (!string.Equals(offer.AgentVersion, agentVersion, StringComparison.Ordinal))
            return Fail($"Сведения о компоненте рук — для агента {offer.AgentVersion}, а эта копия — {agentVersion}. " +
                        "Дождись, пока агент переподключится после обновления, и повтори.");
        if (!HandsComponent.IsSafeArchivePath(offer.Path))
            return Fail("Путь архива рук в сведениях сервера недопустим.");

        if (server() is not { } serverUri)
            return Fail("Агент не сопряжён: сначала «ai-home-agent pair --server … --code …».");
        if (!ServerChannel.IsSecure(serverUri))
            return Fail($"{ServerChannel.InsecureError}: сопряги агента заново с https-адресом сервера.");

        var url = new Uri(serverUri, "agent/" + offer.Path);
        var temp = Path.Combine(Path.GetTempPath(), "ai-home-hands-" + Guid.NewGuid().ToString("N")[..8] + ".zip");
        try
        {
            output.WriteLine($"Качаю компонент рук {offer.Path} ({offer.Size / (1024 * 1024)} МБ)…");
            using (var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                if (!response.IsSuccessStatusCode)
                    return Fail($"Сервер не отдал архив рук: {(int)response.StatusCode}.");
                await using var file = File.Create(temp);
                await using var body = await response.Content.ReadAsStreamAsync(ct);
                // Сверх объявленного размера не качаем: сверка всё равно откажет, а диск пожалеем
                var buffer = new byte[1 << 16];
                long total = 0;
                int n;
                while ((n = await body.ReadAsync(buffer, ct)) > 0)
                {
                    total += n;
                    if (total > offer.Size) return Fail("Архив рук длиннее, чем обещал манифест сервера — прерываю.");
                    await file.WriteAsync(buffer.AsMemory(0, n), ct);
                }
            }

            var record = component.Install(temp, offer);
            output.WriteLine($"Руки установлены: {component.BridgePath}");
            output.WriteLine($"SHA-256 моста: {record.BridgeSha256}");
            output.WriteLine("Агент объявит руки серверу сам; включите руки в настройках проекта.");
            return 0;
        }
        catch (HandsInstallException e)
        {
            return Fail(e.Message);
        }
        catch (HttpRequestException e)
        {
            return Fail($"Архив рук не скачался: {e.Message}");
        }
        finally
        {
            try { File.Delete(temp); } catch (IOException) { }
        }
    }

    private int Disable()
    {
        var removed = component.Remove();
        output.WriteLine("Руки выключены на этом устройстве: ходы больше не смогут управлять программами.");
        if (!removed)
            output.WriteLine($"Каталог {component.ComponentDirectory} занят идущим ходом — удалю при следующем «hands disable».");
        return 0;
    }

    private int Status()
    {
        var check = component.Check();
        if (check.Ready)
        {
            output.WriteLine($"Руки установлены и сверены: {component.BridgePath}");
            output.WriteLine($"Версия агента при установке: {check.Record!.AgentVersion}, установлены {check.Record.InstalledAt:yyyy-MM-dd HH:mm} UTC");
            return 0;
        }
        output.WriteLine(check.Problem);
        return 1;
    }

    private int Fail(string message, int code = 1)
    {
        error.WriteLine(message);
        return code;
    }
}
