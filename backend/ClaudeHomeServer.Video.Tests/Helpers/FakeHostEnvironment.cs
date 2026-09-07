using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace ClaudeHomeServer.Tests.Helpers;

/// <summary>Минимальный IHostEnvironment для юнит-тестов (по умолчанию — Development).</summary>
public sealed class FakeHostEnvironment(string environmentName = "Development") : IHostEnvironment
{
    public string EnvironmentName { get; set; } = environmentName;
    public string ApplicationName { get; set; } = "ClaudeHomeServer.Tests";
    public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}
