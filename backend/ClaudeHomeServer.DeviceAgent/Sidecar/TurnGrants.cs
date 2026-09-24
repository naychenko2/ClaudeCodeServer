using System.Collections.Concurrent;
using System.Security.Cryptography;
using ClaudeHomeServer.DeviceAgent.Exec;

namespace ClaudeHomeServer.DeviceAgent.Sidecar;

/// <summary>
/// Выдачи шлюза живых ходов — только в памяти агента. Ключ в адресе сайдкара
/// (<c>/t/{ключ}/…</c>) — собственная случайность агента, а не id хода сервера: по нему
/// другой пользователь машины не подберёт чужой ход, и серверу он ничего не говорит.
/// </summary>
internal sealed class TurnGrants
{
    private readonly ConcurrentDictionary<string, ExecGatewayGrant?> _byKey = new(StringComparer.Ordinal);

    /// <summary>Регистрирует ход; grant = null — сервер шлюз не выдал, сайдкар ответит отказом.</summary>
    public string Register(ExecGatewayGrant? grant)
    {
        var key = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        _byKey[key] = grant;
        return key;
    }

    public bool TryGet(string key, out ExecGatewayGrant? grant) => _byKey.TryGetValue(key, out grant);

    public void Remove(string key) => _byKey.TryRemove(key, out _);

    public int Count => _byKey.Count;
}
