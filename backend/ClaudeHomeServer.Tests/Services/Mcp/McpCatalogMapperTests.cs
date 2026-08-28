using System.Text.Json;
using ClaudeHomeServer.Services.Mcp.Catalog;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services.Mcp;

// Маппер каталога MCP-серверов (план «Каталог MCP-серверов», волна 1, шаг 2):
// декларация server.json → карточка с предзаполнением. Все отказы — Connectable=false
// с Notice, исключений нет. Список кейсов — тест-план плана, он же полный.
public class McpCatalogMapperTests
{
    // Серверная часть декларации + официальный блок _meta; в кейсах ниже части сервера
    // подставляются строковой сборкой (литеральные { } в интерполяции удваиваются)
    private static JsonElement Entry(string serverBody, string metaBody =
        @"{""status"":""active"",""publishedAt"":""2026-05-18T13:28:59Z"",""isLatest"":true}") =>
        JsonSerializer.Deserialize<JsonElement>(
            @"{""server"":{" + serverBody + @"},""_meta"":{""io.modelcontextprotocol.registry/official"":" + metaBody + "}}");

    // --- stdio: сборка argv из packageArguments ---

    [Fact]
    public void Package_аргументы_собираются_в_argv()
    {
        var card = McpCatalogMapper.MapEntry(Entry("""
            "name":"io.github.o/pkg","version":"2.0.0",
            "packages":[{"registryType":"npm","identifier":"pkg-mcp","version":"2.0.1",
            "runtimeHint":"npx","transport":{"type":"stdio"},
            "runtimeArguments":[{"value":"-y","type":"positional"}],
            "packageArguments":[
              {"type":"positional","description":"Разрешённая папка","isRequired":true},
              {"type":"named","name":"mode","default":"fast"},
              {"type":"named","name":"token","description":"Токен"}],
            "environmentVariables":[{"name":"A","description":"Первая"}]}]
            """));
        card.Should().NotBeNull();
        card!.Connectable.Should().BeTrue();
        card.Prefill.Should().NotBeNull();
        card.Prefill!.Transport.Should().Be("stdio");
        card.Prefill.Command.Should().Be("npx");
        // порядок: -y из runtimeArguments, затем pkg@version, затем packageArguments по порядку:
        // позиционный без default → плейсхолдер-поле; named с default → значение; named без → плейсхолдер
        card.Prefill.Args.Should().Equal("-y", "pkg-mcp@2.0.1", "{arg1}", "--mode", "fast", "--token", "{token}");
        card.Prefill.Fields.Should().Contain(f => f.Target == "args" && f.Name == "arg1" && f.Required);
        card.Prefill.Fields.Should().Contain(f => f.Target == "args" && f.Name == "token" && !f.Required);
        card.Prefill.Fields.Should().Contain(f => f.Target == "env" && f.Name == "A");
        card.Version.Should().Be("2.0.1");
    }

    [Fact]
    public void Package_без_runtimeHint_трактуется_как_npx()
    {
        // Живой реестр: 28 из 29 npm-пакетов идут без runtimeHint — спека его не требует.
        // Отсутствие = npx (дефолт npm-экосистемы); любой другой рантайм — отказ
        var card = McpCatalogMapper.MapEntry(Entry("""
            "name":"io.github.o/plain","version":"1.0.0",
            "packages":[{"registryType":"npm","identifier":"plain-mcp","version":"1.0.0",
            "transport":{"type":"stdio"}}]
            """));
        card!.Connectable.Should().BeTrue();
        card.Prefill!.Command.Should().Be("npx");
    }

    [Fact]
    public void Package_runtimeHint_не_npx_отказ()
    {
        var card = McpCatalogMapper.MapEntry(Entry("""
            "name":"io.github.o/dkr","version":"1.0.0",
            "packages":[{"registryType":"npm","identifier":"dkr-mcp","version":"1.0.0",
            "runtimeHint":"docker","transport":{"type":"stdio"}}]
            """));
        card!.Connectable.Should().BeFalse();
        card.Notice.Should().Contain("docker");
    }

    // --- runtimeArguments: жёсткий allow-list ---

    [Theory]
    [InlineData("""{"value":"--registry","type":"named","name":"registry"}""", "подменяет")]
    [InlineData("""{"value":"-p","type":"positional"}""", "подменяет")]
    public void RuntimeArguments_вне_allow_list_отказ(string runtimeArg, string noticePart)
    {
        var card = McpCatalogMapper.MapEntry(Entry(
            "\"name\":\"io.github.o/x\",\"version\":\"1.0.0\"," +
            "\"packages\":[{\"registryType\":\"npm\",\"identifier\":\"x-mcp\",\"version\":\"1.0.0\"," +
            "\"runtimeHint\":\"npx\",\"transport\":{\"type\":\"stdio\"}," +
            "\"runtimeArguments\":[" + runtimeArg + "]}]"));
        card!.Connectable.Should().BeFalse();
        card.Notice.Should().Contain(noticePart);
    }

    [Theory]
    [InlineData("packageArguments", """{"type":"named","name":"token","isSecret":true,"default":"abc"}""")]
    [InlineData("runtimeArguments", """{"value":"hunter2","type":"positional","isSecret":true}""")]
    public void Аргументы_isSecret_отказ(string section, string argument)
    {
        // Args не обходится SecretRefsOf и не маскируется — секрет уехал бы в стор и облако
        var card = McpCatalogMapper.MapEntry(Entry(
            "\"name\":\"io.github.o/sec\",\"version\":\"1.0.0\"," +
            "\"packages\":[{\"registryType\":\"npm\",\"identifier\":\"sec-mcp\",\"version\":\"1.0.0\"," +
            "\"runtimeHint\":\"npx\",\"transport\":{\"type\":\"stdio\"}," +
            "\"" + section + "\":[" + argument + "]}]"));
        card!.Connectable.Should().BeFalse();
        card.Notice.Should().Contain("в резервную копию");
    }

    // --- identifier и версия ---

    [Theory]
    [InlineData("git+https://evil.tld/pkg.git")]
    [InlineData("https://evil.tld/pkg-1.0.0.tgz")]
    [InlineData("file:../local/pkg")]
    [InlineData("npm:evil-pkg@^2")]
    public void Identifier_мусорный_отказ(string identifier)
    {
        var card = McpCatalogMapper.MapEntry(Entry(
            "\"name\":\"io.github.o/i\",\"version\":\"1.0.0\"," +
            "\"packages\":[{\"registryType\":\"npm\",\"identifier\":\"" + identifier + "\"," +
            "\"version\":\"1.0.0\",\"runtimeHint\":\"npx\",\"transport\":{\"type\":\"stdio\"}}]"));
        card!.Connectable.Should().BeFalse();
        card.Notice.Should().Contain("имени пакета");
    }

    [Theory]
    [InlineData("latest")]
    [InlineData("next")]
    [InlineData("^1.0.0")]
    [InlineData("~2.3")]
    [InlineData("")]
    public void Версия_неточная_отказ(string version)
    {
        var card = McpCatalogMapper.MapEntry(Entry(
            "\"name\":\"io.github.o/v\",\"version\":\"1.0.0\"," +
            "\"packages\":[{\"registryType\":\"npm\",\"identifier\":\"v-mcp\",\"version\":\"" + version + "\"," +
            "\"runtimeHint\":\"npx\",\"transport\":{\"type\":\"stdio\"}}]"));
        card!.Connectable.Should().BeFalse();
        card.Notice.Should().Contain("зафиксировал версию");
    }

    [Fact]
    public void Версия_пререлизная_точная_проходит()
    {
        var card = McpCatalogMapper.MapEntry(Entry("""
            "name":"io.github.o/pre","version":"1.0.0",
            "packages":[{"registryType":"npm","identifier":"pre-mcp","version":"3.0.0-rc.1",
            "runtimeHint":"npx","transport":{"type":"stdio"}}]
            """));
        card!.Connectable.Should().BeTrue();
        card.Prefill!.Args.Should().Contain("pre-mcp@3.0.0-rc.1");
    }

    // --- pypi → uvx (волна 2) ---

    // Живой реестр: pypi-записи (io.github.Oncorporation/filesystem-server) идут без
    // runtimeHint и runtimeArguments — отсутствие трактуем как uvx, дефолт экосистемы
    [Fact]
    public void Pypi_без_runtimeHint_запускается_через_uvx()
    {
        var card = McpCatalogMapper.MapEntry(Entry("""
            "name":"io.github.o/py","version":"0.1.3",
            "packages":[{"registryType":"pypi","identifier":"vs-filesystem-mcp-server",
            "version":"0.1.3","transport":{"type":"stdio"}}]
            """));
        card!.Connectable.Should().BeTrue();
        card.Prefill!.Command.Should().Be("uvx");
        card.Prefill.Args.Should().Equal("vs-filesystem-mcp-server@0.1.3");
        card.Version.Should().Be("0.1.3");
    }

    [Fact]
    public void Pypi_runtimeHint_uvx_проходит_аргументы_пакета_собираются()
    {
        var card = McpCatalogMapper.MapEntry(Entry("""
            "name":"io.github.o/py2","version":"1.0.0",
            "packages":[{"registryType":"pypi","identifier":"py2-mcp","version":"1.2.0",
            "runtimeHint":"uvx","transport":{"type":"stdio"},
            "packageArguments":[
              {"type":"named","name":"root","description":"Разрешённая папка","isRequired":true},
              {"type":"named","name":"mode","default":"fast"}],
            "environmentVariables":[{"name":"API_KEY","isSecret":true}]}]
            """));
        card!.Connectable.Should().BeTrue();
        card.Prefill!.Command.Should().Be("uvx");
        card.Prefill.Args.Should().Equal("py2-mcp@1.2.0", "--root", "{root}", "--mode", "fast");
        card.Prefill.Fields.Should().Contain(f => f.Target == "args" && f.Name == "root" && f.Required);
        card.Prefill.Fields.Should().Contain(f => f.Target == "env" && f.Name == "API_KEY" && f.Secret);
    }

    [Fact]
    public void Pypi_runtimeHint_не_uvx_отказ()
    {
        var card = McpCatalogMapper.MapEntry(Entry("""
            "name":"io.github.o/pyd","version":"1.0.0",
            "packages":[{"registryType":"pypi","identifier":"pyd-mcp","version":"1.0.0",
            "runtimeHint":"docker","transport":{"type":"stdio"}}]
            """));
        card!.Connectable.Should().BeFalse();
        card.Notice.Should().Contain("docker");
    }

    // Allow-list рантайм-флагов uvx ПУСТ: у npx есть -y, у uvx нет ни подтверждений,
    // ни безобидных флагов — --from/--with/--index* подменяют источник пакета, как
    // --registry у npx. Разрешённый набор — пустой, всё остальное отказ
    [Theory]
    [InlineData("""{"value":"--from","type":"named","name":"from"}""")]
    [InlineData("""{"value":"--with","type":"named","name":"with"}""")]
    [InlineData("""{"value":"--index","type":"named","name":"index"}""")]
    [InlineData("""{"value":"--index-url","type":"named","name":"index-url"}""")]
    [InlineData("""{"value":"--extra-index-url","type":"named","name":"extra-index-url"}""")]
    [InlineData("""{"value":"--find-links","type":"named","name":"find-links"}""")]
    [InlineData("""{"value":"-y","type":"positional"}""")]
    public void Pypi_любой_рантайм_флаг_отказ(string runtimeArg)
    {
        var card = McpCatalogMapper.MapEntry(Entry(
            "\"name\":\"io.github.o/pyr\",\"version\":\"1.0.0\"," +
            "\"packages\":[{\"registryType\":\"pypi\",\"identifier\":\"pyr-mcp\",\"version\":\"1.0.0\"," +
            "\"runtimeHint\":\"uvx\",\"transport\":{\"type\":\"stdio\"}," +
            "\"runtimeArguments\":[" + runtimeArg + "]}]"));
        card!.Connectable.Should().BeFalse();
        card.Notice.Should().Contain("подключить нельзя");
    }

    [Theory]
    [InlineData("git+https://evil.tld/pkg.git")]
    [InlineData("https://evil.tld/pkg-1.0.0.tar.gz")]
    [InlineData("file:../local/pkg")]
    [InlineData("pkg>=1.0")]
    [InlineData("-leading-dash")]
    [InlineData("trailing-dash-")]
    [InlineData("space in name")]
    public void Pypi_имя_вне_правил_PyPI_отказ(string identifier)
    {
        var card = McpCatalogMapper.MapEntry(Entry(
            "\"name\":\"io.github.o/pyn\",\"version\":\"1.0.0\"," +
            "\"packages\":[{\"registryType\":\"pypi\",\"identifier\":\"" + identifier + "\"," +
            "\"version\":\"1.0.0\",\"runtimeHint\":\"uvx\",\"transport\":{\"type\":\"stdio\"}}]"));
        card!.Connectable.Should().BeFalse();
        card.Notice.Should().Contain("имени пакета");
    }

    [Fact]
    public void Pypi_имя_нормализуется_по_PEP_503()
    {
        // Серии «-_.» и регистр эквивалентны одному «-»: предпросмотр показывает
        // каноническое имя, которое и запустит uvx
        var card = McpCatalogMapper.MapEntry(Entry("""
            "name":"io.github.o/pynorm","version":"1.0.0",
            "packages":[{"registryType":"pypi","identifier":"My_Pkg.js-Server",
            "version":"1.0.0","runtimeHint":"uvx","transport":{"type":"stdio"}}]
            """));
        card!.Connectable.Should().BeTrue();
        card.Prefill!.Args.Should().Contain("my-pkg-js-server@1.0.0");
    }

    [Theory]
    [InlineData("^1.0.0")]
    [InlineData(">=2")]
    [InlineData("latest")]
    [InlineData("")]
    public void Pypi_версия_неточная_отказ(string version)
    {
        var card = McpCatalogMapper.MapEntry(Entry(
            "\"name\":\"io.github.o/pyv\",\"version\":\"1.0.0\"," +
            "\"packages\":[{\"registryType\":\"pypi\",\"identifier\":\"pyv-mcp\",\"version\":\"" + version + "\"," +
            "\"runtimeHint\":\"uvx\",\"transport\":{\"type\":\"stdio\"}}]"));
        card!.Connectable.Should().BeFalse();
        card.Notice.Should().Contain("зафиксировал версию");
    }

    [Fact]
    public void Pypi_env_чёрный_список_тот_же()
    {
        var card = McpCatalogMapper.MapEntry(Entry("""
            "name":"io.github.o/pye","version":"1.0.0",
            "packages":[{"registryType":"pypi","identifier":"pye-mcp","version":"1.0.0",
            "runtimeHint":"uvx","transport":{"type":"stdio"},
            "environmentVariables":[{"name":"UV_INDEX_URL"}]}]
            """));
        card!.Connectable.Should().BeFalse();
        card.Notice.Should().Contain("UV_INDEX_URL");
    }

    // --- env: чёрный список ---

    [Theory]
    [InlineData("NODE_OPTIONS")]
    [InlineData("npm_config_registry")]
    [InlineData("NPM_CONFIG_PREFIX")]
    [InlineData("LD_PRELOAD")]
    [InlineData("DYLD_INSERT_LIBRARIES")]
    [InlineData("PATH")]
    [InlineData("HTTPS_PROXY")]
    [InlineData("SSL_CERT_FILE")]
    [InlineData("GIT_SSH_COMMAND")]
    [InlineData("BASH_ENV")]
    [InlineData("PERL5OPT")]
    [InlineData("RUBYOPT")]
    [InlineData("JAVA_TOOL_OPTIONS")]
    [InlineData("PIP_INDEX_URL")]
    [InlineData("UV_INDEX_URL")]
    [InlineData("ANTHROPIC_API_KEY")]
    [InlineData("CLAUDE_CODE_OAUTH_TOKEN")]
    public void Env_чёрный_список_отказ(string envName)
    {
        var card = McpCatalogMapper.MapEntry(Entry(
            "\"name\":\"io.github.o/e\",\"version\":\"1.0.0\"," +
            "\"packages\":[{\"registryType\":\"npm\",\"identifier\":\"e-mcp\",\"version\":\"1.0.0\"," +
            "\"runtimeHint\":\"npx\",\"transport\":{\"type\":\"stdio\"}," +
            "\"environmentVariables\":[{\"name\":\"" + envName + "\"}]}]"));
        card!.Connectable.Should().BeFalse();
        card.Notice.Should().Contain(envName);
    }

    [Fact]
    public void Env_секрет_поле_формы_без_значения_по_умолчанию()
    {
        var card = McpCatalogMapper.MapEntry(Entry("""
            "name":"io.github.o/s","version":"1.0.0",
            "packages":[{"registryType":"npm","identifier":"s-mcp","version":"1.0.0",
            "runtimeHint":"npx","transport":{"type":"stdio"},
            "environmentVariables":[
              {"name":"SECRET_KEY","isSecret":true,"default":"do-not-take"},
              {"name":"REGION","default":"eu"}]}]
            """));
        card!.Connectable.Should().BeTrue();
        var secret = card.Prefill!.Fields.Single(f => f.Name == "SECRET_KEY");
        secret.Secret.Should().BeTrue();
        secret.Default.Should().BeNull(); // значения секретов из реестра не переносятся никогда
        var plain = card.Prefill.Fields.Single(f => f.Name == "REGION");
        plain.Secret.Should().BeFalse();
        plain.Default.Should().Be("eu");
    }

    // --- remotes[] → Http/Sse ---

    [Fact]
    public void Remote_https_типы_транспорта()
    {
        var card = McpCatalogMapper.MapEntry(Entry("""
            "name":"com.example/remote","version":"0.7.0",
            "remotes":[{"type":"streamable-http","url":"https://api.example.com/mcp"}]
            """));
        card!.Connectable.Should().BeTrue();
        card.Prefill!.Transport.Should().Be("http");
        card.Prefill.Url.Should().Be("https://api.example.com/mcp");
        card.Prefill.Fields.Should().BeEmpty();
        card.Version.Should().Be("0.7.0");
    }

    [Fact]
    public void Remote_sse_заголовки_и_переменные_адреса()
    {
        var card = McpCatalogMapper.MapEntry(Entry("""
            "name":"com.example/sse","version":"1.0.0",
            "remotes":[{"type":"sse","url":"https://api.example.com/{COMPANY}/sse",
            "headers":[{"name":"X-Api-Key","description":"Ключ","isSecret":true}]}]
            """));
        card!.Connectable.Should().BeTrue();
        card.Prefill!.Transport.Should().Be("sse");
        card.Prefill.Url.Should().Be("https://api.example.com/{COMPANY}/sse");
        card.Prefill.Fields.Should().Contain(f => f.Target == "url" && f.Name == "COMPANY" && f.Required);
        card.Prefill.Fields.Should().Contain(f => f.Target == "header" && f.Name == "X-Api-Key" && f.Secret);
    }

    [Fact]
    public void Remote_http_адрес_отказ()
    {
        var card = McpCatalogMapper.MapEntry(Entry("""
            "name":"com.example/insec","version":"1.0.0",
            "remotes":[{"type":"http","url":"http://api.example.com/mcp"}]
            """));
        card!.Connectable.Should().BeFalse();
        card.Notice.Should().Contain("http://");
    }

    [Theory]
    [InlineData("Host")]
    [InlineData("Proxy-Authorization")]
    [InlineData("Connection")]
    [InlineData("Content-Length")]
    [InlineData("Плохо; Имя")]
    public void Remote_запретное_имя_заголовка_отказ(string headerName)
    {
        var card = McpCatalogMapper.MapEntry(Entry(
            "\"name\":\"com.example/h\",\"version\":\"1.0.0\"," +
            "\"remotes\":[{\"type\":\"http\",\"url\":\"https://api.example.com/mcp\"," +
            "\"headers\":[{\"name\":\"" + headerName + "\"}]}]"));
        card!.Connectable.Should().BeFalse();
        card.Notice.Should().Contain("заголовок");
    }

    [Fact]
    public void Remote_битый_первый_годен_второй_берём_годный()
    {
        // Перебор remotes: битый не топит карточку, если рядом есть годный
        var card = McpCatalogMapper.MapEntry(Entry("""
            "name":"com.example/two","version":"1.0.0",
            "remotes":[
              {"type":"http","url":"http://api.example.com/mcp"},
              {"type":"http","url":"https://api.example.com/mcp"}]
            """));
        card!.Connectable.Should().BeTrue();
        card.Prefill!.Url.Should().Be("https://api.example.com/mcp");
    }

    [Fact]
    public void Remote_секрет_в_адресе_отказ()
    {
        // Имя переменной url совпадает с секретным заголовком — секрет в url не импортируем:
        // модель секретов URL не поддерживает
        var card = McpCatalogMapper.MapEntry(Entry("""
            "name":"com.example/securl","version":"1.0.0",
            "remotes":[{"type":"http","url":"https://api.example.com/{API_KEY}/mcp",
            "headers":[{"name":"API_KEY","isSecret":true}]}]
            """));
        card!.Connectable.Should().BeFalse();
        card.Notice.Should().Contain("в резервную копию");
    }

    // --- Ключ карточки ---

    [Theory]
    [InlineData("io.github.modelcontextprotocol/filesystem", "filesystem")]
    [InlineData("com.pulsemcp/remote-filesystem", "remote-filesystem")]
    [InlineData("com.example/pkg.with.dots", "pkg-with-dots")]
    [InlineData("plainname", "plainname")]
    public void Slug_последний_сегмент_нормализуется(string name, string expected)
    {
        McpCatalogMapper.SlugOf(name).Should().Be(expected);
    }

    [Fact]
    public void Slug_режется_до_40_символов()
    {
        var name = "io.github.o/" + new string('a', 80);
        McpCatalogMapper.SlugOf(name).Should().Be(new string('a', 40));
    }

    [Fact]
    public void Prefill_ключ_и_подписи_вычищаются()
    {
        var card = McpCatalogMapper.MapEntry(Entry(
            "\"name\":\"io.github.o/" + new string('k', 60) + "\",\"version\":\"1.0.0\"," +
            "\"title\":\"Заголовок\\nс переводом\\tстроки\"," +
            "\"description\":\"" + new string('d', 700) + "\"," +
            "\"packages\":[{\"registryType\":\"npm\",\"identifier\":\"k-mcp\",\"version\":\"1.0.0\"," +
            "\"transport\":{\"type\":\"stdio\"}}]"));
        card!.Prefill!.Key.Should().Be(new string('k', 40));
        card.Prefill.Label.Should().Be("Заголовок с переводом строки");
        card.Prefill.Description!.Length.Should().Be(600);
        card.Description!.Should().NotContain("\n");
    }

    // --- registryType / status / дедуп ---

    [Fact]
    public void RegistryType_не_npm_pypi_и_не_remote_помеченная_карточка()
    {
        // pypi с волны 2 поддерживается — неподдержанный пример теперь oci из спеки реестра
        var card = McpCatalogMapper.MapEntry(Entry("""
            "name":"io.github.o/oci","version":"1.0.0",
            "packages":[{"registryType":"oci","identifier":"ghcr.io/o/oci-mcp","version":"1.0.0",
            "runtimeHint":"docker","transport":{"type":"stdio"}}]
            """));
        card!.Connectable.Should().BeFalse();
        card.Notice.Should().Contain("Docker-образ");
    }

    [Fact]
    public void Status_deleted_скрывается_в_ответе_поиска()
    {
        var page = McpCatalogMapper.MapSearchResponse(JsonSerializer.Deserialize<JsonElement>("""
            {"servers":[
              {"server":{"name":"io.github.o/dead","version":"1.0.0"},"_meta":
                {"io.modelcontextprotocol.registry/official":{"status":"deleted","isLatest":true}}},
              {"server":{"name":"io.github.o/alive","version":"1.0.0"},"_meta":
                {"io.modelcontextprotocol.registry/official":{"status":"active","isLatest":true}}}],
            "metadata":{"count":2,"nextCursor":"io.github.o/alive:1.0.0"}}
            """));
        page.Items.Should().ContainSingle(c => c.Name == "io.github.o/alive");
        page.NextCursor.Should().Be("io.github.o/alive:1.0.0");
    }

    [Fact]
    public void Status_deprecated_карточка_с_пометкой_без_подключения()
    {
        var card = McpCatalogMapper.MapEntry(Entry("""
            "name":"io.github.o/dep","version":"1.0.0",
            "packages":[{"registryType":"npm","identifier":"dep-mcp","version":"1.0.0",
            "transport":{"type":"stdio"}}]
            """, """{"status":"deprecated","publishedAt":"2026-05-18T13:28:59Z","isLatest":true}"""));
        card!.Connectable.Should().BeFalse();
        card.Prefill.Should().BeNull();
        card.Notice.Should().Contain("устаревшим");
    }

    [Fact]
    public void Дедуп_по_имени_побеждает_isLatest()
    {
        var page = McpCatalogMapper.MapSearchResponse(JsonSerializer.Deserialize<JsonElement>("""
            {"servers":[
              {"server":{"name":"io.github.o/dup","version":"0.9.0"},"_meta":
                {"io.modelcontextprotocol.registry/official":{"status":"active","isLatest":false,
                "publishedAt":"2026-01-01T00:00:00Z"}}},
              {"server":{"name":"io.github.o/dup","version":"1.0.0"},"_meta":
                {"io.modelcontextprotocol.registry/official":{"status":"active","isLatest":true,
                "publishedAt":"2026-06-01T00:00:00Z"}}},
              {"server":{"name":"io.github.o/dup","version":"1.1.0"},"_meta":
                {"io.modelcontextprotocol.registry/official":{"status":"active","isLatest":false,
                "publishedAt":"2026-07-01T00:00:00Z"}}}],
            "metadata":{"count":3}}
            """));
        page.Items.Should().ContainSingle();
        page.Items[0].Version.Should().Be("1.0.0"); // 0.9.0 и 1.1.0 не isLatest — проиграли
    }

    [Fact]
    public void Дедуп_без_isLatest_побеждает_свежая_публикация()
    {
        var page = McpCatalogMapper.MapSearchResponse(JsonSerializer.Deserialize<JsonElement>("""
            {"servers":[
              {"server":{"name":"io.github.o/d2","version":"1.0.0"},"_meta":
                {"io.modelcontextprotocol.registry/official":{"status":"active","isLatest":false,
                "publishedAt":"2026-01-01T00:00:00Z"}}},
              {"server":{"name":"io.github.o/d2","version":"2.0.0"},"_meta":
                {"io.modelcontextprotocol.registry/official":{"status":"active","isLatest":false,
                "publishedAt":"2026-08-01T00:00:00Z"}}}],
            "metadata":{"count":2}}
            """));
        page.Items.Should().ContainSingle();
        page.Items[0].Version.Should().Be("2.0.0");
    }

    // --- мусор не валит маппер ---

    [Fact]
    public void Мусорная_декларация_карточка_без_исключения()
    {
        var card = McpCatalogMapper.MapEntry(JsonSerializer.Deserialize<JsonElement>(
            """{"server":{"name":"io.github.o/junk","packages":"not-an-array"},"_meta":{}}"""));
        card!.Connectable.Should().BeFalse();
        card.Prefill.Should().BeNull();

        var empty = McpCatalogMapper.MapSearchResponse(JsonSerializer.Deserialize<JsonElement>("""{}"""));
        empty.Items.Should().BeEmpty();
        empty.NextCursor.Should().BeNull();
    }
}
