using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>Тесты инференса запускаемых сервисов из манифестов проекта.</summary>
public class ProjectServiceDiscoveryTests : IDisposable
{
    private readonly string _dir;
    private readonly ProjectServiceDiscovery _svc;
    private readonly LaunchConfigService _launch;

    public ProjectServiceDiscoveryTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "psd_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
        _launch = new LaunchConfigService(new Mock<ILogger<LaunchConfigService>>().Object);
        _svc = new ProjectServiceDiscovery(_launch, new Mock<ILogger<ProjectServiceDiscovery>>().Object);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private Project Project() => new() { RootPath = _dir, OwnerId = "u", Name = "t" };

    private void Write(string relPath, string content)
    {
        var full = Path.Combine(_dir, relPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    [Fact]
    public async Task Ids_AreUnique_WhenSlugsCollide()
    {
        // Регрессия боя 26.08: слаг оставляет только латиницу и цифры, поэтому у двух
        // конфигураций с чисто кириллическими именами Id совпадал. Панель «Сервисы»
        // отвечала 500 (ToDictionary по Id → ArgumentException), и запустить нельзя было
        // ни один сервис проекта — падал весь список.
        Write(".claude/launch.json", """
        {
          "configurations": [
            { "name": "Панель хостов",  "runtimeExecutable": "npm", "runtimeArgs": ["run", "hosts"] },
            { "name": "Панель сессий",  "runtimeExecutable": "npm", "runtimeArgs": ["run", "sessions"] }
          ]
        }
        """);

        var svcs = await _svc.DiscoverAsync(Project());

        svcs.Should().HaveCount(2, "разные запуски не схлопываются — теряется кнопка");
        svcs.Select(s => s.Id).Should().OnlyHaveUniqueItems(
            "Id — ключ сервиса во всём API: по нему идут запуск, остановка и реестр запущенных");
        svcs.Select(s => s.Name).Should().BeEquivalentTo(["Панель хостов", "Панель сессий"],
            "имена остаются человеческими — суффикс уходит только в Id");
    }

    [Fact]
    public async Task Ids_StayStable_AcrossCalls()
    {
        // Суффикс считается от сигнатуры запуска, а не от порядкового номера: иначе
        // добавление третьей конфигурации переставило бы Id уже запущенным сервисам,
        // и реестр процессов (он ключуется Id) осиротел бы.
        Write(".claude/launch.json", """
        {
          "configurations": [
            { "name": "Панель хостов",  "runtimeExecutable": "npm", "runtimeArgs": ["run", "hosts"] },
            { "name": "Панель сессий",  "runtimeExecutable": "npm", "runtimeArgs": ["run", "sessions"] }
          ]
        }
        """);
        var first = (await _svc.DiscoverAsync(Project())).Select(s => s.Id).ToList();

        _svc.Invalidate(Project().Id);
        var second = (await _svc.DiscoverAsync(Project())).Select(s => s.Id).ToList();

        second.Should().Equal(first);
    }

    [Fact]
    public async Task PackageJson_ServerScriptsOnly()
    {
        Write("package.json", """{ "scripts": { "dev": "vite", "build": "vite build", "predev": "x", "serve": "vite preview" } }""");
        var svcs = await _svc.DiscoverAsync(Project());

        svcs.Should().Contain(s => s.Source == "npm" && s.Args.Contains("dev"));
        svcs.Should().Contain(s => s.Source == "npm" && s.Args.Contains("serve"));
        svcs.Should().NotContain(s => s.Args.Contains("build"));
        svcs.Should().NotContain(s => s.Args.Contains("predev"));
        svcs.Where(s => s.Source == "npm").Should().OnlyContain(s => s.Command == "npm");
    }

    [Fact]
    public async Task PackageJson_DetectsPnpm()
    {
        Write("package.json", """{ "scripts": { "dev": "vite" } }""");
        Write("pnpm-lock.yaml", "lockfileVersion: '9.0'");
        var svcs = await _svc.DiscoverAsync(Project());

        var dev = svcs.First(s => s.Source == "npm");
        dev.Command.Should().Be("pnpm");
        dev.Args.Should().Equal("dev"); // pnpm <script>, без "run"
    }

    [Fact]
    public async Task LaunchSettings_ExtractsHttpPort()
    {
        Write("src/Api/Api.csproj", "<Project></Project>");
        Write("src/Api/Properties/launchSettings.json", """
        {
          "profiles": {
            "http": { "commandName": "Project", "applicationUrl": "https://localhost:7001;http://localhost:5005" },
            "IIS": { "commandName": "IISExpress" }
          }
        }
        """);
        var svcs = await _svc.DiscoverAsync(Project());

        var dotnet = svcs.Where(s => s.Source == "dotnet").ToList();
        dotnet.Should().ContainSingle(); // только commandName=Project
        dotnet[0].Command.Should().Be("dotnet");
        dotnet[0].SuggestedPort.Should().Be(5005); // http предпочтительнее https
        dotnet[0].Args.Should().Contain("--project");
    }

    [Fact]
    public async Task Compose_ExtractsFirstHostPort()
    {
        Write("docker-compose.yml", """
        services:
          web:
            image: nginx
            ports:
              - "8080:80"
          db:
            image: postgres
            ports:
              - "127.0.0.1:5433:5432"
        """);
        var svcs = await _svc.DiscoverAsync(Project());

        var compose = svcs.Where(s => s.Source == "docker-compose").ToList();
        compose.Should().Contain(s => s.Name.StartsWith("web") && s.SuggestedPort == 8080);
        compose.Should().Contain(s => s.Name.StartsWith("db") && s.SuggestedPort == 5433);
        compose.Should().OnlyContain(s => s.Command == "docker");
    }

    [Fact]
    public async Task Procfile_ParsesProcessTypes()
    {
        Write("Procfile", "web: node server.js\nworker: node worker.js");
        var svcs = await _svc.DiscoverAsync(Project());

        var proc = svcs.Where(s => s.Source == "procfile").ToList();
        proc.Should().Contain(s => s.Command == "node" && s.Args.Contains("server.js"));
    }

    [Fact]
    public async Task Makefile_ServerTargetsOnly()
    {
        Write("Makefile", "run:\n\tdotnet run\nbuild:\n\tdotnet build\ndev-server:\n\tnpm run dev\n");
        var svcs = await _svc.DiscoverAsync(Project());

        var make = svcs.Where(s => s.Source == "makefile").ToList();
        make.Should().Contain(s => s.Args.Contains("run"));
        make.Should().Contain(s => s.Args.Contains("dev-server"));
        make.Should().NotContain(s => s.Args.Contains("build"));
    }

    [Fact]
    public async Task SavedLaunchConfig_MarkedSaved_AndPreferredOverInferred()
    {
        Write("package.json", """{ "scripts": { "dev": "vite" } }""");
        await _launch.WriteAsync(Project(), new List<LaunchConfigEntry>
        {
            new() { Name = "custom-web", RuntimeExecutable = "npm", RuntimeArgs = ["run", "dev"], Port = 4000 }
        });
        _svc.Invalidate(Project().Id); // на всякий (разные Id у Project() — кэш по Id)

        var svcs = await _svc.DiscoverAsync(Project());
        svcs.Should().Contain(s => s.Saved && s.Name == "custom-web" && s.SuggestedPort == 4000);
        // Инференсный "npm run dev" имеет ту же сигнатуру → отброшен в пользу saved.
        svcs.Count(s => s.Command == "npm" && s.Args.SequenceEqual(new[] { "run", "dev" })).Should().Be(1);
    }

    // ── Конфигурации Rider ───────────────────────────────────────────────────
    // XML в тестах — с реальных файлов (backend/.run этого репозитория и соседних
    // проектов), а не выдуманный: у Rider у каждого типа своя схема.

    [Fact]
    public async Task Rider_LaunchSettingsProfile_BecomesDotnetRun()
    {
        Write("src/App/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        Write("src/App/Properties/launchSettings.json", """
            { "profiles": { "http": { "commandName": "Project", "applicationUrl": "http://localhost:5111" } } }
            """);
        Write(".run/Backend.run.xml", """
            <component name="ProjectRunConfigurationManager">
              <configuration default="false" name="Backend" type="LaunchSettings" factoryName=".NET Launch Settings Profile">
                <option name="LAUNCH_PROFILE_PROJECT_FILE_PATH" value="$PROJECT_DIR$/src/App/App.csproj" />
                <option name="LAUNCH_PROFILE_NAME" value="http" />
              </configuration>
            </component>
            """);

        var svcs = await _svc.DiscoverAsync(Project());
        var rider = svcs.Single(s => s.Source == "rider");

        rider.Name.Should().Be("Backend");
        rider.Command.Should().Be("dotnet");
        rider.Args.Should().Equal("run", "--project", "src/App/App.csproj", "--launch-profile", "http");
        // Порт подтянут из launchSettings.json того профиля, на который ссылается конфигурация
        rider.SuggestedPort.Should().Be(5111);
    }

    [Fact]
    public async Task Rider_LaunchSettings_DeduplicatesWithInferredDotnet()
    {
        Write("src/App/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        Write("src/App/Properties/launchSettings.json", """
            { "profiles": { "http": { "commandName": "Project", "applicationUrl": "http://localhost:5111" } } }
            """);
        Write(".run/Backend.run.xml", """
            <component name="ProjectRunConfigurationManager">
              <configuration default="false" name="Backend" type="LaunchSettings" factoryName=".NET Launch Settings Profile">
                <option name="LAUNCH_PROFILE_PROJECT_FILE_PATH" value="$PROJECT_DIR$/src/App/App.csproj" />
                <option name="LAUNCH_PROFILE_NAME" value="http" />
              </configuration>
            </component>
            """);

        var svcs = await _svc.DiscoverAsync(Project());
        // Тот же запуск нашёлся бы и разбором launchSettings.json — в списке он один,
        // и это версия Rider (у неё осмысленное имя)
        svcs.Count(s => s.Command == "dotnet" && s.Args.Contains("--launch-profile")).Should().Be(1);
        svcs.Should().NotContain(s => s.Source == "dotnet");
    }

    [Fact]
    public async Task Rider_NpmConfiguration_BecomesPackageManagerRun()
    {
        Write("web/package.json", """{ "scripts": { "dev": "vite" } }""");
        Write(".run/Frontend.run.xml", """
            <component name="ProjectRunConfigurationManager">
              <configuration default="false" name="Frontend" type="js.build_tools.npm">
                <package-json value="$PROJECT_DIR$/web/package.json" />
                <command value="run" />
                <scripts>
                  <script value="dev" />
                </scripts>
                <node-interpreter value="project" />
              </configuration>
            </component>
            """);

        var svcs = await _svc.DiscoverAsync(Project());
        var rider = svcs.Single(s => s.Source == "rider");

        rider.Name.Should().Be("Frontend");
        rider.Command.Should().Be("npm");
        rider.Args.Should().Equal("run", "dev");
        rider.Cwd.Should().Be("web");
    }

    [Fact]
    public async Task Rider_DockerDeploy_BecomesComposeUpWithProfile()
    {
        Write(".run/Docker.run.xml", """
            <component name="ProjectRunConfigurationManager">
              <configuration default="false" name="Docker Run Base" type="docker-deploy" factoryName="docker-compose.yml">
                <deployment type="docker-compose.yml">
                  <settings>
                    <option name="profiles">
                      <list>
                        <option value="base" />
                      </list>
                    </option>
                    <option name="sourceFilePath" value=".docker/docker-compose.yaml" />
                  </settings>
                </deployment>
              </configuration>
            </component>
            """);
        Write(".docker/docker-compose.yaml", "services:\n  web:\n    image: nginx\n");

        var svcs = await _svc.DiscoverAsync(Project());
        var rider = svcs.Single(s => s.Source == "rider");

        rider.Command.Should().Be("docker");
        rider.Args.Should().Equal("compose", "-f", ".docker/docker-compose.yaml", "--profile", "base", "up");
    }

    [Fact]
    public async Task Rider_Multilaunch_BecomesGroupOfMembers()
    {
        Write("web/package.json", """{ "scripts": { "dev": "vite" } }""");
        Write("src/App/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        Write(".run/Frontend.run.xml", """
            <component name="ProjectRunConfigurationManager">
              <configuration default="false" name="Frontend" type="js.build_tools.npm">
                <package-json value="$PROJECT_DIR$/web/package.json" />
                <command value="run" />
                <scripts><script value="dev" /></scripts>
              </configuration>
            </component>
            """);
        Write(".run/Backend.run.xml", """
            <component name="ProjectRunConfigurationManager">
              <configuration default="false" name="Backend" type="LaunchSettings" factoryName=".NET Launch Settings Profile">
                <option name="LAUNCH_PROFILE_PROJECT_FILE_PATH" value="$PROJECT_DIR$/src/App/App.csproj" />
                <option name="LAUNCH_PROFILE_NAME" value="http" />
              </configuration>
            </component>
            """);
        Write(".run/Compound.run.xml", """
            <component name="ProjectRunConfigurationManager">
              <configuration default="false" name="Backend + Frontend" type="com.intellij.execution.configurations.multilaunch" factoryName="MultiLaunchConfiguration">
                <rows>
                  <ExecutableRowSnapshot>
                    <option name="executable">
                      <ExecutableSnapshot>
                        <option name="id" value="runConfig:.NET Launch Settings Profile.Backend" />
                      </ExecutableSnapshot>
                    </option>
                  </ExecutableRowSnapshot>
                  <ExecutableRowSnapshot>
                    <option name="executable">
                      <ExecutableSnapshot>
                        <option name="id" value="runConfig:npm.Frontend" />
                      </ExecutableSnapshot>
                    </option>
                  </ExecutableRowSnapshot>
                </rows>
              </configuration>
            </component>
            """);

        var svcs = await _svc.DiscoverAsync(Project());
        var group = svcs.Single(s => s.Members is { Length: > 0 });

        group.Name.Should().Be("Backend + Frontend");
        group.Command.Should().BeEmpty();          // у группы своей команды нет
        group.Members!.Should().HaveCount(2);

        var members = group.Members!.Select(id => svcs.Single(s => s.Id == id)).ToList();
        members.Select(m => m.Name).Should().Equal("Backend", "Frontend");
    }

    [Fact]
    public async Task Rider_Multilaunch_WithoutResolvableMembers_IsSkipped()
    {
        // Ссылки ведут на типы, которые мы не поддерживаем → группе нечего запускать
        Write(".run/Compound.run.xml", """
            <component name="ProjectRunConfigurationManager">
              <configuration default="false" name="Скрипты" type="com.intellij.execution.configurations.multilaunch" factoryName="MultiLaunchConfiguration">
                <rows>
                  <ExecutableRowSnapshot>
                    <option name="executable">
                      <ExecutableSnapshot>
                        <option name="id" value="runConfig:PowerShell.serve" />
                      </ExecutableSnapshot>
                    </option>
                  </ExecutableRowSnapshot>
                </rows>
              </configuration>
            </component>
            """);
        Write(".run/Script.run.xml", """
            <component name="ProjectRunConfigurationManager">
              <configuration default="false" name="serve" type="PowerShellRunType" factoryName="PowerShell" scriptUrl="$PROJECT_DIR$/docs/serve.ps1" />
            </component>
            """);

        var svcs = await _svc.DiscoverAsync(Project());
        svcs.Should().NotContain(s => s.Source == "rider");
    }

    [Fact]
    public async Task Rider_MultilaunchRef_PrefersLongestNameMatch()
    {
        // «Backend» — суффикс имени «Telemetry Backend»: без выбора самого длинного
        // совпадения ссылка ушла бы не в ту конфигурацию
        Write("src/App/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        Write(".run/Two.run.xml", """
            <component name="ProjectRunConfigurationManager">
              <configuration default="false" name="Backend" type="LaunchSettings" factoryName=".NET Launch Settings Profile">
                <option name="LAUNCH_PROFILE_PROJECT_FILE_PATH" value="$PROJECT_DIR$/src/App/App.csproj" />
                <option name="LAUNCH_PROFILE_NAME" value="http" />
              </configuration>
              <configuration default="false" name="Telemetry Backend" type="LaunchSettings" factoryName=".NET Launch Settings Profile">
                <option name="LAUNCH_PROFILE_PROJECT_FILE_PATH" value="$PROJECT_DIR$/src/App/App.csproj" />
                <option name="LAUNCH_PROFILE_NAME" value="telemetry" />
              </configuration>
              <configuration default="false" name="Всё сразу" type="com.intellij.execution.configurations.multilaunch" factoryName="MultiLaunchConfiguration">
                <rows>
                  <ExecutableRowSnapshot>
                    <option name="executable">
                      <ExecutableSnapshot>
                        <option name="id" value="runConfig:.NET Launch Settings Profile.Telemetry Backend" />
                      </ExecutableSnapshot>
                    </option>
                  </ExecutableRowSnapshot>
                </rows>
              </configuration>
            </component>
            """);

        var svcs = await _svc.DiscoverAsync(Project());
        var group = svcs.Single(s => s.Members is { Length: > 0 });
        var member = svcs.Single(s => s.Id == group.Members![0]);

        member.Name.Should().Be("Telemetry Backend");
    }

    [Fact]
    public async Task Rider_ScriptConfigurations_AreSkipped()
    {
        Write(".run/Script.run.xml", """
            <component name="ProjectRunConfigurationManager">
              <configuration default="false" name="serve" type="PowerShellRunType" factoryName="PowerShell" scriptUrl="$PROJECT_DIR$/docs/serve.ps1" />
            </component>
            """);

        var svcs = await _svc.DiscoverAsync(Project());
        svcs.Should().NotContain(s => s.Source == "rider");
    }

    [Fact]
    public async Task Rider_PathOutsideProject_IsSkipped()
    {
        // Конфигурация ссылается за пределы корня — запускать такое мы права не имеем
        Write(".run/Outside.run.xml", """
            <component name="ProjectRunConfigurationManager">
              <configuration default="false" name="Outside" type="js.build_tools.npm">
                <package-json value="$PROJECT_DIR$/../elsewhere/package.json" />
                <command value="run" />
                <scripts>
                  <script value="dev" />
                </scripts>
              </configuration>
            </component>
            """);

        var svcs = await _svc.DiscoverAsync(Project());
        svcs.Should().NotContain(s => s.Source == "rider");
    }

    [Fact]
    public async Task Rider_IdeaRunConfigurations_AreFound()
    {
        // У Rider путь бывает вложенным: .idea/.idea.<Solution>/.idea/runConfigurations
        Write("web/package.json", """{ "scripts": { "start": "node server.js" } }""");
        Write(".idea/.idea.Sln/.idea/runConfigurations/Web.xml", """
            <component name="ProjectRunConfigurationManager">
              <configuration default="false" name="Web" type="js.build_tools.npm">
                <package-json value="$PROJECT_DIR$/web/package.json" />
                <command value="run" />
                <scripts>
                  <script value="start" />
                </scripts>
              </configuration>
            </component>
            """);

        var svcs = await _svc.DiscoverAsync(Project());
        svcs.Should().Contain(s => s.Source == "rider" && s.Name == "Web");
    }

    [Fact]
    public async Task Rider_ConfigInSubdirectory_IsFound()
    {
        // .run лежит рядом с solution, а не в корне репозитория (как backend/.run у нас)
        Write("backend/App/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        Write("backend/.run/Backend.run.xml", """
            <component name="ProjectRunConfigurationManager">
              <configuration default="false" name="Backend" type="LaunchSettings" factoryName=".NET Launch Settings Profile">
                <option name="LAUNCH_PROFILE_PROJECT_FILE_PATH" value="$PROJECT_DIR$/App/App.csproj" />
                <option name="LAUNCH_PROFILE_NAME" value="http" />
              </configuration>
            </component>
            """);

        var svcs = await _svc.DiscoverAsync(Project());
        var rider = svcs.Single(s => s.Source == "rider");
        rider.Args.Should().Equal("run", "--project", "backend/App/App.csproj", "--launch-profile", "http");
    }

    [Fact]
    public async Task Rider_BrokenXml_DoesNotBreakDiscovery()
    {
        Write("package.json", """{ "scripts": { "dev": "vite" } }""");
        Write(".run/Broken.run.xml", "<component name=\"ProjectRunConfigurationManager\"><configuration");

        var svcs = await _svc.DiscoverAsync(Project());
        // Битый файл пропущен, остальной инференс работает
        svcs.Should().Contain(s => s.Source == "npm" && s.Args.Contains("dev"));
    }
}
