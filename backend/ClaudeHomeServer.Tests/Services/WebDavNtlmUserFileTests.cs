using System.Text;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.WebDav;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// NTLM WebDAV по основному паролю (вариант A): NT-хэш пишется в файл NTLM_USER_FILE вне
/// data/ в моменты, когда пароль виден, а Negotiate предлагается только по HTTPS и только
/// когда Type3 есть чем проверить. Неудачный Type3 — 401 с одним Basic, а не 500.
/// </summary>
public class WebDavNtlmUserFileTests : IDisposable
{
    // NT-хэш строки "password" — эталонный вектор MD4(UTF-16LE)
    private const string PasswordNtHash = "8846F7EAEE8FB117AD06BDD830B7586C";

    private readonly string _dataDir;
    private readonly string _secretsDir;
    private readonly string _ntlmPath;

    public WebDavNtlmUserFileTests()
    {
        var root = Path.Combine(Path.GetTempPath(), "webdav-ntlm-" + Guid.NewGuid().ToString("N"));
        _dataDir = Path.Combine(root, "data");
        _secretsDir = Path.Combine(root, "secrets");
        _ntlmPath = Path.Combine(_secretsDir, "ntlm_users");
        Directory.CreateDirectory(_dataDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_dataDir)!, recursive: true); } catch { /* временная папка */ }
    }

    private IConfiguration Config(Dictionary<string, string?>? extra = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(_dataDir, "projects.json"),
            ["WebDav:NtlmUserFile"] = _ntlmPath,
            ["WebDav:NtlmDomains:0"] = "WORKGROUP",
            ["WebDav:NtlmDomains:1"] = "AI",
        };
        foreach (var (k, v) in extra ?? []) settings[k] = v;
        return new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
    }

    private NtlmUserFile NtlmFile(IConfiguration config, bool mechanism = true, bool isWindows = false) =>
        new(config, NullLogger<NtlmUserFile>.Instance, isWindows, mechanism, envPath: _ntlmPath);

    private UserStore Store(NtlmUserFile? ntlm, IConfiguration? config = null, string env = "Production") =>
        new(config ?? Config(), new Helpers.FakeHostEnvironment(env), NullLogger<UserStore>.Instance, ntlm: ntlm);

    private string[] Lines() => File.ReadAllLines(_ntlmPath).Where(l => !l.StartsWith('#')).ToArray();

    // Строки одного пользователя: стор на пустом каталоге сам заводит admin, и тот тоже в файле
    private string[] Lines(string user) =>
        Lines().Where(l => l.Split(':')[0].Split('\\')[^1] == user).ToArray();

    private static string HashOf(string password) => Convert.ToHexString(NtlmHelper.ComputeNtHash(password));

    [Fact]
    public void NtHash_СовпадаетСЭталоннымВектором()
    {
        HashOf("password").Should().Be(PasswordNtHash);
    }

    [Fact]
    public void СозданиеПользователя_ПишетСтрокиПоДоменамИПустуюПоследней()
    {
        var store = Store(NtlmFile(Config()));

        store.Add("bob", "password", "user");

        Lines("bob").Should().Equal(
            $"WORKGROUP\\bob:0:AAD3B435B51404EEAAD3B435B51404EE:{PasswordNtHash}:[U          ]:LCT-00000000:",
            $"AI\\bob:0:AAD3B435B51404EEAAD3B435B51404EE:{PasswordNtHash}:[U          ]:LCT-00000000:",
            $"bob:0:AAD3B435B51404EEAAD3B435B51404EE:{PasswordNtHash}:[U          ]:LCT-00000000:");
    }

    [Fact]
    public void Файл_0600_БезВременныхХвостов()
    {
        var store = Store(NtlmFile(Config()));
        store.Add("bob", "password", "user");

        if (!OperatingSystem.IsWindows())
            File.GetUnixFileMode(_ntlmPath).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        Directory.GetFiles(_secretsDir).Should().Equal(_ntlmPath);
    }

    [Fact]
    public void Вход_ЗаполняетФайл_ТемКогоВНёмНет()
    {
        // Пользователь заведён до выкатки: строки у него нет
        var user = Store(null).Add("carol", "s3cret-pw", "user");
        File.Exists(_ntlmPath).Should().BeFalse();

        var store = Store(NtlmFile(Config()));
        store.VerifyPassword(store.GetById(user.Id)!, "wrong").Should().BeFalse();
        File.Exists(_ntlmPath).Should().BeFalse("неверный пароль не пишет ничего");

        store.VerifyPassword(store.GetById(user.Id)!, "s3cret-pw").Should().BeTrue();
        Lines().Should().OnlyContain(l => l.Contains(HashOf("s3cret-pw"))).And.HaveCount(3);
        Lines("carol").Should().HaveCount(3);
    }

    [Fact]
    public void МастерПарольDev_НеПопадаетВФайл()
    {
        var config = Config(new() { ["Auth:DevPassword"] = "dev-master" });
        var user = Store(null, config, "Development").Add("dave", "real-pw", "user");
        var store = Store(NtlmFile(config), config, "Development");

        store.VerifyPassword(store.GetById(user.Id)!, "dev-master").Should().BeTrue();

        File.Exists(_ntlmPath).Should().BeFalse("хэш мастер-пароля открыл бы NTLM всем");
    }

    [Fact]
    public void СменаИСбросПароля_ОбновляютХэш()
    {
        var store = Store(NtlmFile(Config()));
        var user = store.Add("bob", "pw-1", "user");

        store.ChangePassword(user.Id, "pw-1", "pw-2").Should().BeTrue();
        Lines("bob").Should().OnlyContain(l => l.Contains(HashOf("pw-2"))).And.HaveCount(3);

        store.ResetPassword(user.Id, "pw-3").Should().BeTrue();
        Lines("bob").Should().OnlyContain(l => l.Contains(HashOf("pw-3"))).And.HaveCount(3);
    }

    [Fact]
    public void УдалениеИПереименование_УбираютСтроки()
    {
        var store = Store(NtlmFile(Config()));
        var bob = store.Add("bob", "pw-b", "user");
        var eve = store.Add("eve", "pw-e", "user");

        store.Delete(bob.Id).Should().BeTrue();
        Lines("bob").Should().BeEmpty();

        store.Update(eve.Id, "eva", null).Should().BeTrue();
        Lines("eve").Concat(Lines("eva")).Should().BeEmpty(
            "хэш под новым именем без пароля не пересчитать — строка появится при следующем входе");
    }

    [Fact]
    public void Старт_ВыбрасываетСтрокиУдалённыхПользователей()
    {
        var ntlm = NtlmFile(Config());
        var store = Store(ntlm);
        store.Add("bob", "pw-b", "user");
        ntlm.Record("ghost", "pw-g"); // пользователя удалили при остановленном сервере

        Store(NtlmFile(Config()));

        Lines("ghost").Should().BeEmpty();
        Lines("bob").Should().HaveCount(3);
    }

    [Fact]
    public void UsersJson_НеСодержитNtHash()
    {
        var store = Store(NtlmFile(Config()));
        var user = store.Add("bob", "password", "user");
        store.VerifyPassword(user, "password");

        var json = File.ReadAllText(Path.Combine(_dataDir, "users.json"));
        json.Should().NotContain("NtHash").And.NotContain(PasswordNtHash);
    }

    [Fact]
    public void NtlmAvailable_ЗависитОтФайлаМеханизмаИПеременной()
    {
        NtlmUserFile.ComputeAvailable(false, true, _ntlmPath, _ntlmPath, fileExists: false).Should().BeFalse();
        NtlmUserFile.ComputeAvailable(false, false, _ntlmPath, _ntlmPath, fileExists: true).Should().BeFalse();
        NtlmUserFile.ComputeAvailable(false, true, _ntlmPath, envPath: null, fileExists: true).Should().BeFalse();
        NtlmUserFile.ComputeAvailable(false, true, _ntlmPath, _ntlmPath + ".other", fileExists: true).Should().BeFalse();
        NtlmUserFile.ComputeAvailable(false, true, _ntlmPath, _ntlmPath, fileExists: true).Should().BeTrue();
        NtlmUserFile.ComputeAvailable(true, false, null, null, fileExists: false).Should().BeTrue("на Windows — SSPI, как раньше");
    }

    [Fact]
    public void ТолькоПеременнаяСреды_БезКлюча_ФайлНеПишется()
    {
        // Дев-стенд из хода наследует NTLM_USER_FILE прода: писать по нему нельзя
        var config = Config(new() { ["WebDav:NtlmUserFile"] = "" });
        var ntlm = new NtlmUserFile(config, NullLogger<NtlmUserFile>.Instance,
            isWindows: false, mechanismPresent: true, envPath: _ntlmPath);

        Store(ntlm, config).Add("bob", "password", "user");

        ntlm.Path.Should().BeNull();
        File.Exists(_ntlmPath).Should().BeFalse();
        ntlm.NegotiateAvailable.Should().BeFalse();
    }

    [Fact]
    public void NtlmAvailable_БезФайла_False_СФайлом_True()
    {
        var ntlm = NtlmFile(Config());
        ntlm.NegotiateAvailable.Should().BeFalse();

        ntlm.Record("bob", "password");

        ntlm.NegotiateAvailable.Should().BeTrue();
    }

    [Fact]
    public void МеханизмGss_ОпределяетсяПоMechD()
    {
        var gss = Path.Combine(_secretsDir, "gss");
        Directory.CreateDirectory(Path.Combine(gss, "mech.d"));
        NtlmUserFile.DetectMechanism(gss).Should().BeFalse();

        File.WriteAllText(Path.Combine(gss, "mech.d", "mech.ntlmssp.conf"),
            "# NTLMSSP mechanism plugin\ngssntlmssp_v1\t1.3.6.1.4.1.311.2.2.10\t/usr/lib/x86_64-linux-gnu/gssntlmssp/gssntlmssp.so\n");
        NtlmUserFile.DetectMechanism(gss).Should().BeTrue();
    }

    // ── Вызов аутентификации ────────────────────────────────────────────────

    private DefaultHttpContext DavCtx(string scheme, string? authorization, NtlmUserFile ntlm, UserStore store)
    {
        var ctx = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection()
                .AddSingleton(Config())
                .AddSingleton(ntlm)
                .AddSingleton(store)
                .BuildServiceProvider(),
        };
        ctx.Request.Scheme = scheme;
        ctx.Request.Method = "OPTIONS";
        ctx.Request.Path = "/projects/";
        if (authorization is not null) ctx.Request.Headers.Authorization = authorization;
        return ctx;
    }

    private static string Basic(string user, string password) =>
        "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}"));

    [Fact]
    public async Task Https_СПроверяемымNtlm_ПредлагаетNegotiate()
    {
        var ntlm = NtlmFile(Config());
        var store = Store(ntlm);
        store.Add("bob", "password", "user");

        var ctx = DavCtx("https", Basic("bob", "wrong"), ntlm, store);
        await WebDavHandler.HandleAsync(ctx);

        ctx.Response.StatusCode.Should().Be(401);
        ctx.Response.Headers.WWWAuthenticate.ToString().Should().Be("Negotiate, Basic realm=\"ClaudeHomeServer\"");
    }

    [Fact]
    public async Task Http_NegotiateНеПредлагается()
    {
        var ntlm = NtlmFile(Config());
        var store = Store(ntlm);
        store.Add("bob", "password", "user");

        var ctx = DavCtx("http", Basic("bob", "wrong"), ntlm, store);
        await WebDavHandler.HandleAsync(ctx);

        ctx.Response.StatusCode.Should().Be(401);
        ctx.Response.Headers.WWWAuthenticate.ToString().Should().Be("Basic realm=\"ClaudeHomeServer\"");
    }

    [Fact]
    public async Task NegotiateПоHttp_НеПринимается_ТолькоBasic()
    {
        var ntlm = NtlmFile(Config());
        var store = Store(ntlm);
        store.Add("bob", "password", "user");

        var ctx = DavCtx("http", "Negotiate TlRMTVNTUAABAAAAB4IIogAAAAAAAAAAAAAAAAAAAAAKAGFKAAAADw==", ntlm, store);
        await WebDavHandler.HandleAsync(ctx);

        ctx.Response.StatusCode.Should().Be(401);
        ctx.Response.Headers.WWWAuthenticate.ToString().Should().NotContain("Negotiate");
    }

    [Fact]
    public async Task BasicПоОсновномуПаролю_ПускаетИЗаполняетФайл()
    {
        var user = Store(null).Add("bob", "password", "user");
        var ntlm = NtlmFile(Config());
        var store = Store(ntlm);

        var ctx = DavCtx("https", Basic("bob", "password"), ntlm, store);
        await WebDavHandler.HandleAsync(ctx);

        ctx.Response.StatusCode.Should().Be(200);
        ctx.Items["DavUserId"].Should().Be(user.Id);
        Lines().Should().Contain($"bob:0:AAD3B435B51404EEAAD3B435B51404EE:{PasswordNtHash}:[U          ]:LCT-00000000:");
    }

    // ── Неудачный Type3 ─────────────────────────────────────────────────────

    [Fact]
    public async Task НеудачныйNegotiate_401ТолькоBasic_ОтветОбработан()
    {
        var http = new DefaultHttpContext();
        http.Request.Headers.Authorization = "Negotiate " + Convert.ToBase64String(Type3("WORKGROUP", "bob"));
        var scheme = new AuthenticationScheme(NegotiateDefaults.AuthenticationScheme, null, typeof(NegotiateHandler));
        var failed = new AuthenticationFailedContext(http, scheme, new NegotiateOptions())
        {
            Exception = new InvalidOperationException("GenericFailure"),
        };

        await NegotiateFailure.HandleAsync(failed);

        http.Response.StatusCode.Should().Be(401);
        http.Response.Headers.WWWAuthenticate.ToString().Should().Be("Basic realm=\"ClaudeHomeServer\"");
        failed.Result.Should().NotBeNull();
        failed.Result!.Handled.Should().BeTrue("иначе хендлер перекинет исключение и клиент получит 500");
    }

    [Fact]
    public void Type3_ДоменИИмяЧитаютсяДляЛога()
    {
        var header = "Negotiate " + Convert.ToBase64String(Type3("DESKTOP-PC", "andrey"));

        NegotiateFailure.TryReadType3Identity(header).Should().Be(("DESKTOP-PC", "andrey"));
        NegotiateFailure.TryReadType3Identity("Negotiate не-base64").Should().Be(((string?)null, (string?)null));
        NegotiateFailure.TryReadType3Identity(Basic("a", "b")).Should().Be(((string?)null, (string?)null));
    }

    [Fact]
    public void Type3_ФормаДляЛога_ПоказываетMicИCbtИSpn()
    {
        static byte[] Av(ushort id, byte[] value) =>
            [.. BitConverter.GetBytes(id), .. BitConverter.GetBytes((ushort)value.Length), .. value];
        var avPairs = new List<byte>();
        avPairs.AddRange(Av(6, BitConverter.GetBytes(2u)));
        avPairs.AddRange(Av(9, Encoding.Unicode.GetBytes("HTTP/host")));
        avPairs.AddRange(Av(10, Enumerable.Repeat((byte)7, 16).ToArray()));
        avPairs.AddRange(Av(0, []));
        // NTProofStr(16) + заголовок blob(28) + AV-пары
        var nt = new byte[44].Concat(avPairs).ToArray();

        var msg = new byte[64 + nt.Length];
        "NTLMSSP\0"u8.CopyTo(msg);
        BitConverter.GetBytes(3u).CopyTo(msg, 8);
        BitConverter.GetBytes((ushort)nt.Length).CopyTo(msg, 20);
        BitConverter.GetBytes(64u).CopyTo(msg, 24);
        BitConverter.GetBytes(0xE2888235u).CopyTo(msg, 60);
        nt.CopyTo(msg, 64);

        var shape = NegotiateFailure.DescribeType3("Negotiate " + Convert.ToBase64String(msg));

        shape.Should().Contain("NTLMv2").And.Contain("0xE2888235").And.Contain("(MIC)")
            .And.Contain("HTTP/host").And.Contain("CBT задан");
        NegotiateFailure.DescribeType3(Basic("a", "b")).Should().Be("нет");
    }

    // Минимальный NTLM Type3: заголовок 64 байта, домен и имя в UTF-16LE, флаг UNICODE
    private static byte[] Type3(string domain, string user)
    {
        var dom = Encoding.Unicode.GetBytes(domain);
        var usr = Encoding.Unicode.GetBytes(user);
        var msg = new byte[64 + dom.Length + usr.Length];
        "NTLMSSP\0"u8.CopyTo(msg);
        BitConverter.GetBytes(3u).CopyTo(msg, 8);
        void Field(int at, int len, int off)
        {
            BitConverter.GetBytes((ushort)len).CopyTo(msg, at);
            BitConverter.GetBytes((ushort)len).CopyTo(msg, at + 2);
            BitConverter.GetBytes((uint)off).CopyTo(msg, at + 4);
        }
        Field(28, dom.Length, 64);
        Field(36, usr.Length, 64 + dom.Length);
        BitConverter.GetBytes(1u).CopyTo(msg, 60);
        dom.CopyTo(msg, 64);
        usr.CopyTo(msg, 64 + dom.Length);
        return msg;
    }
}
