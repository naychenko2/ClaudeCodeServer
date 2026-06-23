using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace ClaudeCodeServer.Tests.Helpers;

public class TestWebApplicationFactory : WebApplicationFactory<Program>, IDisposable
{
    public const string ApiKey = "test-api-key";

    public string TempDir { get; } = Path.Combine(Path.GetTempPath(), "ccs_tests_" + Guid.NewGuid().ToString("N"));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(TempDir, "projects.json"),
                ["Auth:ApiKey"] = ApiKey
            });
        });
    }

    /// <summary>Клиент с заголовком Authorization: Bearer — для защищённых эндпоинтов.</summary>
    public HttpClient CreateAuthenticatedClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", ApiKey);
        return client;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && Directory.Exists(TempDir))
            Directory.Delete(TempDir, recursive: true);
    }
}
