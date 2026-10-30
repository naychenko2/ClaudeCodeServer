using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Plugins;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Plugins;

// QA-фикс O (MAJOR-2): БОЕВАЯ регистрация шва IPersonaPluginBindingPurger. Nullable-шов
// PluginInstallCascade превращает потерю боевой регистрации (Program.cs) в тихий no-op, и ни
// один unit-тест это не ловил (все подставляют стаб-каскад; PluginsSubsystemDisabledTests
// шов не регистрирует). Тест на полном хосте (WebApplicationFactory, пример —
// StubModuleEndpointTests): DI строит PluginUninstall → IPluginInstallCascade →
// IPersonaPluginBindingPurger (реальный PluginBindingPurgerAdapter, Program.cs:480);
// после удаления плагина Skill-привязка персоны plugin:{id}/… исчезает (каскад реально
// зовёт адаптер).
// Мутация: убрать регистрацию шва в Program.cs:480 → DI отдаёт null → каскад no-op →
// привязка не снимается → тест красный.
[Trait("Category", "Host")]
public class PluginBindingPurgeBattleTests : IDisposable
{
    private const string Owner = "owner-a";

    private readonly TestWebApplicationFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public void Uninstall_СнимаетSkillПривязкуПерсона_ЧерезБоевойШов()
    {
        var store = _factory.Services.GetRequiredService<PluginStore>();
        var personas = _factory.Services.GetRequiredService<PersonaManager>();
        var lookup = _factory.Services.GetRequiredService<IPersonaSkillBindingLookup>();
        var uninstall = _factory.Services.GetRequiredService<PluginUninstall>();

        // 1) Установить плагин (пакет + запись) с выведенным id.
        var id = PluginIdentity.DeriveId(Owner, "pkg");
        var pkg = store.PackageDir(Owner, id, "1.0.0");
        Directory.CreateDirectory(Path.Combine(pkg, "skills", "my-skill"));
        File.WriteAllText(Path.Combine(pkg, "skills", "my-skill", "SKILL.md"), "# skill");
        store.SaveInstalled(Owner,
        [
            new PluginInstall { Id = id, Version = "1.0.0", Sha256 = "abc", Enabled = true, Source = "upload" },
        ]);

        // 2) Персона с Skill-привязкой к навыку плагина (target = plugin:{id}/my-skill).
        var persona = personas.Create(Owner, "Тест", null, null, null, null, null,
            PersonaScope.Global, null, null, null, memoryEnabled: false);
        var target = $"plugin:{id}/my-skill";
        personas.UpdateBindings(persona.Id, Owner,
        [
            new PersonaBinding { Type = PersonaBindingType.Skill, Target = target, Condition = "когда нужен", Mode = PersonaBindingMode.Auto },
        ]);

        // Привязка видна ДО удаления.
        lookup.Get(Owner, persona.Id)!.SkillBindingTargets.Should().Contain(target,
            "созданная Skill-привязка навыка плагина видна при сборке навыков персоны");

        // 3) Удаление плагина: боевой каскад (DI) снимает Skill-привязку.
        uninstall.Uninstall(Owner, id).Should().BeTrue("плагин удалён");

        // 4) Привязка исчезла (каскад реально зовёт PluginBindingPurgerAdapter), чтение не падает.
        lookup.Get(Owner, persona.Id).Should().NotBeNull("персона жива — чтение не упало");
        lookup.Get(Owner, persona.Id)!.SkillBindingTargets.Should().NotContain(target,
            "боевой каскад (шва IPersonaPluginBindingPurger из Program.cs) снял Skill-привязку");
    }
}
