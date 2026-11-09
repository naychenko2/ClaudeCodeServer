using System.Reflection;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Services.Images;
using ClaudeHomeServer.Services.Images.LocalMedia;
using ClaudeHomeServer.Services.Media;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace ClaudeHomeServer.Tests.Services;

// Регрессионный DI-тест (ревью шага 26б, critical): ComfyQueueMonitor должен резолвиться ПО ТИПУ
// (singleton), а не только как IHostedService. Старая регистрация AddGatedHostedService<ComfyQueueMonitor>
// вешала монитор лишь на IHostedService, поэтому GetService<ComfyQueueMonitor>() = null: адаптеры
// получали monitor=null (Gpu всегда null), а ComfyStats (обязательный конструктор ComfyQueueMonitor)
// в DI не строился — /api/local-model/stats/media всегда отдавал {configured:false}. Мутация «вернуть
// старую регистрацию» краснит этот тест.
public class LocalMediaDiTests
{
    // Поднимает ServiceCollection с регистрацией подсистемы images, как делает реальный Program.cs
    // (AddSubsystems → ImagesSubsystem.Register → AddLocalMedia). subsystemEnabled: null — дефолт true.
    private static (IServiceCollection services, IConfiguration config) NewHost(string? subsystemEnabled)
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                // Testing без EnableHostedServices: hosted-сервисы гейтятся, но singleton-типы
                // регистрируются — именно это и проверяем (монитор доступен по типу, а не только как IHostedService)
                ["ASPNETCORE_ENVIRONMENT"] = "Testing",
                ["Testing:EnableHostedServices"] = "false",
                ["LocalMedia:Enabled"] = "true",
                ["LocalMedia:ComfyUrl"] = "http://comfy.test:8188",
                ["Subsystems:images:Enabled"] = subsystemEnabled,
            })
            .Build();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(config);
        services.AddSingleton<IHostEnvironment>(new HostEnvironmentStub());
        // ComfyClient принимает IHttpClientFactory в конструкторе — без него резолв упадёт по ложной причине
        services.AddHttpClient();
        return (services, config);
    }

    [Fact]
    public void Включенная_подсистема_монитор_singleton_ComfyStats_и_адаптеры_резолвятся()
    {
        var (services, config) = NewHost(null);
        services.AddSubsystems(config, new ImagesSubsystem());
        using var provider = services.BuildServiceProvider();

        // Монитор — singleton, видимый по типу (а не только как IHostedService)
        var monitor = provider.GetService<ComfyQueueMonitor>();
        monitor.Should().NotBeNull("ComfyQueueMonitor должен резолвиться по типу, а не только как IHostedService");

        // ComfyStats (обязательный конструктор ComfyQueueMonitor) строится
        provider.GetRequiredService<IComfyStats>().Should().NotBeNull("ComfyStats должен резолвиться, когда монитор — singleton");

        // Адаптеры получают тот же экземпляр монитора (резолвим через швы ILocal*Media —
        // аудио/image-адаптеры в DI зарегистрированы только через интерфейсы, не по типу)
        MonitorOf(provider.GetRequiredService<ILocalVideoMedia>()).Should().BeSameAs(monitor, "видео-адаптер — тот же монитор");
        MonitorOf(provider.GetRequiredService<ILocalAudioMedia>()).Should().BeSameAs(monitor, "аудио-адаптер — тот же монитор");
        MonitorOf(provider.GetRequiredService<ILocalImageMedia>()).Should().BeSameAs(monitor, "image-адаптер — тот же монитор");
    }

    [Fact]
    public void Выключенная_подсистема_ComfyStats_и_монитор_null_без_исключений()
    {
        var (services, config) = NewHost("false");
        services.AddSubsystems(config, new ImagesSubsystem());
        using var provider = services.BuildServiceProvider();

        // Подсистема выключена → AddLocalMedia не зовётся → ни монитор, ни ComfyStats не регистрируются
        provider.GetService<ComfyQueueMonitor>().Should().BeNull("подсистема выключена — монитор не регистрируется");
        provider.GetService<IComfyStats>().Should().BeNull("подсистема выключена — ComfyStats не регистрируется");
    }

    // Частное поле-монитор адаптера: ищем поле типа ComfyQueueMonitor (имена primary-constructor-полей не стабильны)
    private static object? MonitorOf(object adapter) =>
        adapter.GetType()
            .GetFields(BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(f => f.FieldType == typeof(ComfyQueueMonitor))
            ?.GetValue(adapter);

    // Заглушка IHostEnvironment — ImagesSubsystem.Register резолвит её (и IConfiguration) в графе
    private sealed class HostEnvironmentStub : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "ClaudeHomeServer.Images.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
