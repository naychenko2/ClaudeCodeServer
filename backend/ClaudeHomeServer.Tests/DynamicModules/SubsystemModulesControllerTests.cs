using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using ClaudeHomeServer.Controllers;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.DynamicModules;

// N4: контракт /api/subsystem-modules (SubsystemModulesController). Контроллер читает секцию
// DynamicModules и отдаёт ТОЛЬКО модули с непустым Frontend:RemoteUrl в виде { id, remoteUrl,
// exposedModule } (exposedModule с дефолтом ./subsystem); модули без Frontend (Backend-only
// сценария Б) в ответ не попадают. Юнит без подъёма хоста: контроллеру нужен только IConfiguration.
public class SubsystemModulesControllerTests
{
    // Конфиг из двух модулей: 0 = notes (Frontend заполнен), 1 = backend-only (Frontend нет).
    // Это реальный сценарий N1: notes — фронт-only MF-remote, __stub — Backend-only (и Disabled).
    private static IConfiguration TwoModuleConfig() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DynamicModules:0:Key"] = "notes",
            ["DynamicModules:0:Title"] = "Notes",
            ["DynamicModules:0:Version"] = "1.0.0",
            ["DynamicModules:0:Enabled"] = "true",
            ["DynamicModules:0:Frontend:RemoteUrl"] = "/notes-remote/remoteEntry.js",
            ["DynamicModules:0:Frontend:ExposedModule"] = "./subsystem",
            ["DynamicModules:1:Key"] = "__stub",
            ["DynamicModules:1:Title"] = "Test Stub Module",
            ["DynamicModules:1:Version"] = "0.0.1",
            ["DynamicModules:1:Enabled"] = "false",
            ["DynamicModules:1:Backend:AssemblyPath"] = "modules/__stub/StubModule.dll",
        }).Build();

    private static JsonElement ParseItems(OkObjectResult result)
    {
        // Controller отдаёт анонимный объект { items = [...] } — сериализуем и читаем.
        var json = JsonSerializer.Serialize(result.Value);
        return JsonDocument.Parse(json).RootElement.GetProperty("items");
    }

    [Fact]
    public void ЗаполненныйFrontend_КонтроллерВозвращает_Id_RemoteUrl_ExposedModule()
    {
        var controller = new SubsystemModulesController(TwoModuleConfig());

        var result = (OkObjectResult)controller.List();
        var items = ParseItems(result);

        items.GetArrayLength().Should().Be(1,
            "в ответ попадает только модуль с Frontend; Backend-only (__stub) не должен");
        var item = items[0];
        item.GetProperty("id").GetString().Should().Be("notes");
        item.GetProperty("remoteUrl").GetString().Should().Be("/notes-remote/remoteEntry.js");
        item.GetProperty("exposedModule").GetString().Should().Be("./subsystem");
    }

    [Fact]
    public void ПустойFrontend_МодульВОтветНеПопадает()
    {
        // Только Backend-only модуль (Frontend-секции нет) → items пуст.
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DynamicModules:0:Key"] = "__stub",
            ["DynamicModules:0:Title"] = "Test Stub Module",
            ["DynamicModules:0:Version"] = "0.0.1",
            ["DynamicModules:0:Enabled"] = "true",
            ["DynamicModules:0:Backend:AssemblyPath"] = "modules/__stub/StubModule.dll",
        }).Build();

        var controller = new SubsystemModulesController(config);

        var result = (OkObjectResult)controller.List();
        ParseItems(result).GetArrayLength().Should().Be(0,
            "модуль без Frontend:RemoteUrl в список subsystem remotes не попадает");
    }

    [Fact]
    public void ВыключенныйМодульСFrontend_ВОтветНеПопадает()
    {
        // Enabled=false + Frontend заполнен → модуль не должен попасть в список (гейт подсистем).
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DynamicModules:0:Key"] = "spend",
            ["DynamicModules:0:Title"] = "Spend",
            ["DynamicModules:0:Version"] = "1.0.0",
            ["DynamicModules:0:Enabled"] = "false",
            ["DynamicModules:0:Frontend:RemoteUrl"] = "/spend-remote/remoteEntry.js",
            ["DynamicModules:0:Frontend:ExposedModule"] = "./subsystem",
        }).Build();

        var controller = new SubsystemModulesController(config);

        var result = (OkObjectResult)controller.List();
        ParseItems(result).GetArrayLength().Should().Be(0,
            "выключенный (Enabled=false) модуль с Frontend.RemoteUrl не отдаётся фронту");
    }

    // H2 (зоид живого хоста 2026-09-15): SubsystemModulesController фильтровал только по
    // `DynamicModules:Enabled`, а второй рубильник `Subsystems:{Key}:Enabled` игнорировал.
    // В итоге фронт продолжал грузить remote выключенной подсистемы, а ModuleLoader
    // хоста оказывался в расхождении со снимком /api/admin/subsystems. Тест ниже
    // фиксирует, что оба рубильника уважаются единообразно.
    [Fact]
    public void ПодсистемныйГейтВыключен_МодульНеПопадаетВСписок()
    {
        // DynamicModules:Enabled=true, Subsystems:Key:Enabled=false — типичная конфигурация
        // «модуль сконфигурирован, но выключен админом». Фронт не должен грузить remote.
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DynamicModules:0:Key"] = "spend",
            ["DynamicModules:0:Enabled"] = "true",
            ["DynamicModules:0:Frontend:RemoteUrl"] = "/spend-remote/remoteEntry.js",
            ["DynamicModules:0:Frontend:ExposedModule"] = "./subsystem",
            ["Subsystems:spend:Enabled"] = "false",
        }).Build();

        var controller = new SubsystemModulesController(config);

        var result = (OkObjectResult)controller.List();
        ParseItems(result).GetArrayLength().Should().Be(0,
            "выключенный по Subsystems:{Key}:Enabled модуль НЕ должен попадать в выдачу — " +
            "иначе фронт будет грузить remote выключенной подсистемы, и ModuleLoader хоста " +
            "окажется в расхождении со снимком /api/admin/subsystems");
    }
}
