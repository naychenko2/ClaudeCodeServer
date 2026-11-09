using System.Reflection;
using ClaudeHomeServer.HandsBridge.Policy;
using Xunit;
using Xunit.Sdk;

namespace HandsBridge.Policy.Tests;

/// <summary>
/// Linux-тесты записи файлов в проект (D2d): <c>HandsProjectFiles</c> живёт в мосту
/// (<c>net10.0-windows</c>), поэтому грузится рефлексией из <c>hands-bridge/</c> вывода тестов
/// (тот же путь, что <see cref="HandsBridgeGateRuntimeTests"/>). Все проверки — на временном
/// каталоге: <c>.vN</c> при занятом имени, <c>AppendOwn</c> чужого файла, 101-й файл, путь за
/// корнем, симлинк, без <c>--project-root</c>, расширение вне белого списка.
/// </summary>
[Collection(HandsBridgeGateRuntimeTests.Collection)]
public class HandsProjectFilesTests
{
    private static readonly string Tool = HandsTools.ScreenshotControl;

    /// <summary>Создаёт <c>HandsProjectFiles</c> с корнем в свежем временном каталоге.</summary>
    private static (object Instance, string Root) Create()
    {
        var root = NewTempDir();
        var type = HandsBridgeGateRuntimeTests.Bridge.Value
            .GetType("ClaudeHomeServer.HandsBridge.HandsProjectFiles", throwOnError: true)!;
        var instance = Activator.CreateInstance(type, (object?)root)!;
        return (instance, root);
    }

    /// <summary>Создаёт <c>HandsProjectFiles</c> БЕЗ корня (нет <c>--project-root</c>).</summary>
    private static (object Instance, string Root) CreateNoRoot()
    {
        var type = HandsBridgeGateRuntimeTests.Bridge.Value
            .GetType("ClaudeHomeServer.HandsBridge.HandsProjectFiles", throwOnError: true)!;
        var instance = Activator.CreateInstance(type, (object?)null)!;
        return (instance, "");
    }

    private static (string? Path, string? Reason) WriteNew(object instance, string relativePath, byte[]? bytes = null)
    {
        bytes ??= [1, 2, 3];
        var result = (ValueTuple<string, string>)instance.GetType()
            .GetMethod("WriteNew")!
            .Invoke(instance, [Tool, relativePath, bytes])!;
        return (result.Item1, result.Item2);
    }

    private static (string? Path, string? Reason) AppendOwn(object instance, string relativePath, byte[]? bytes = null)
    {
        bytes ??= [4, 5, 6];
        var result = (ValueTuple<string, string>)instance.GetType()
            .GetMethod("AppendOwn")!
            .Invoke(instance, [Tool, relativePath, bytes])!;
        return (result.Item1, result.Item2);
    }

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hands-d2d-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Занято — следующее свободное .vN: x.png существует → x.v2.png.</summary>
    [Fact]
    public void WriteNew_picks_the_next_free_name_when_taken()
    {
        var (instance, root) = Create();
        File.WriteAllBytes(Path.Combine(root, "x.png"), [9]); // занято

        var (path, reason) = WriteNew(instance, "x.png");

        Assert.Null(reason);
        Assert.Equal("x.v2.png", path);
        Assert.True(File.Exists(Path.Combine(root, "x.v2.png")));
        Assert.False(File.Exists(Path.Combine(root, "x.v1.png")));
    }

    /// <summary>AppendOwn чужого файла (создан не WriteNew в этом ходу) — отказ.</summary>
    [Fact]
    public void AppendOwn_of_a_foreign_file_is_denied()
    {
        var (instance, root) = Create();
        File.WriteAllBytes(Path.Combine(root, "foreign.png"), [1]); // чужой файл

        var (path, reason) = AppendOwn(instance, "foreign.png");

        Assert.Null(path);
        Assert.NotNull(reason);
        Assert.Contains("created itself", reason, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>AppendOwn своего файла (создан WriteNew в этом ходу) — дописывает.</summary>
    [Fact]
    public void AppendOwn_of_an_own_file_appends()
    {
        var (instance, root) = Create();
        var (path, reason) = WriteNew(instance, "own.png");
        Assert.Null(reason);
        Assert.Equal("own.png", path);

        var (appendPath, appendReason) = AppendOwn(instance, "own.png", [7, 8]);
        Assert.Null(appendReason);
        Assert.Equal("own.png", appendPath);

        var content = File.ReadAllBytes(Path.Combine(root, "own.png"));
        Assert.Equal([1, 2, 3, 7, 8], content);
    }

    /// <summary>101-й файл за ход — отказ (потолок 100).</summary>
    [Fact]
    public void The_101st_file_in_a_turn_is_denied()
    {
        var (instance, _) = Create();

        for (var i = 1; i <= 100; i++)
        {
            var (p, r) = WriteNew(instance, $"f{i:000}.png");
            Assert.Null(r);
            Assert.NotNull(p);
        }

        var (path, reason) = WriteNew(instance, "f101.png");
        Assert.Null(path);
        Assert.NotNull(reason);
        Assert.Contains("100", reason, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Путь за пределы корня: «..» и абсолютный — отказ.</summary>
    [Theory]
    [InlineData("../escape.png")]
    [InlineData("sub/../../escape.png")]
    public void Path_outside_the_root_is_denied(string relativePath)
    {
        var (instance, _) = Create();

        var (path, reason) = WriteNew(instance, relativePath);

        Assert.Null(path);
        Assert.NotNull(reason);
    }

    [Fact]
    public void Absolute_path_is_denied()
    {
        var (instance, root) = Create();

        var (path, reason) = WriteNew(instance, root + "/abs.png");

        Assert.Null(path);
        Assert.NotNull(reason);
    }

    /// <summary>Симлинк внутри корня — отказ (на Windows без прав на симлинки — честный skip).</summary>
    [SkippableFact]
    public void Symlink_inside_the_root_is_denied()
    {
        var (instance, root) = Create();

        var outside = NewTempDir();
        var link = Path.Combine(root, "link");
        try
        {
            File.CreateSymbolicLink(link, outside);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Skip.If(true, $"симлинки недоступны на этой машине: {ex.Message}");
        }

        var (path, reason) = WriteNew(instance, "link/shot.png");

        Assert.Null(path);
        Assert.NotNull(reason);
        Assert.Contains("symlink", reason, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Без --project-root любая запись отказывает.</summary>
    [Fact]
    public void Without_project_root_the_write_is_denied()
    {
        var (instance, _) = CreateNoRoot();

        var (path, reason) = WriteNew(instance, "x.png");

        Assert.Null(path);
        Assert.NotNull(reason);
        Assert.Contains("project root", reason, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Расширение не из белого списка (.png/.jpg) — отказ.</summary>
    [Theory]
    [InlineData("x.txt")]
    [InlineData("x.bin")]
    [InlineData("x")]
    public void Extension_not_in_the_whitelist_is_denied(string relativePath)
    {
        var (instance, _) = Create();

        var (path, reason) = WriteNew(instance, relativePath);

        Assert.Null(path);
        Assert.NotNull(reason);
    }

    /// <summary>
    /// Второй уровень защиты (D2d.1): <c>FileMode.CreateNew</c>. Имя, которое <c>NextFreeName</c>
    /// счёл свободным, оказывается занятым в момент записи (файл появляется между проверкой и
    /// открытием) — <c>CreateNew</c> даёт IOException, а не тихую перезапись. Мутация
    /// <c>CreateNew</c>→<c>Create</c> (в шве <c>OpenNew</c>) краснеет: файл перезаписывается.
    /// </summary>
    [Fact]
    public void WriteNew_create_new_does_not_overwrite_a_file_taken_after_the_check()
    {
        var (instance, root) = Create();
        File.WriteAllBytes(Path.Combine(root, "x.png"), [9]); // занято → NextFreeName → x.v2.png (свободно)

        // Оборачиваю дефолтный OpenNew: файл появляется в момент открытия, а не до NextFreeName.
        var openNewField = instance.GetType().GetField("OpenNew", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var defaultOpenNew = (Func<string, FileStream>)openNewField.GetValue(instance)!;
        openNewField.SetValue(instance, (Func<string, FileStream>)(path =>
        {
            File.WriteAllBytes(path, [9, 9, 9]); // файл появляется между проверкой и открытием
            return defaultOpenNew(path);
        }));

        // CreateNew: файл уже есть — IOException (обёрнут рефлексией), а не тихая перезапись.
        try
        {
            WriteNew(instance, "x.png", [1, 2, 3]);
        }
        catch (Exception ex) when (ex is TargetInvocationException { InnerException: IOException })
        {
            // ожидаемо: CreateNew упёрся в появившийся файл
        }

        // Файл НЕ перезаписан: содержимое осталось прежним (мутация на Create дала бы [1,2,3]).
        Assert.Equal([9, 9, 9], File.ReadAllBytes(Path.Combine(root, "x.v2.png")));
    }

    /// <summary>
    /// AppendOwn находит свой файл, записанный под <c>.vN</c> (D2d.1): имя занято, <c>WriteNew</c>
    /// записал <c>x.v2.png</c>; дописывание по исходному имени <c>x.png</c> идёт в ФАКТИЧЕСКИЙ файл,
    /// а не создаёт пустой <c>x.png</c>.
    /// </summary>
    [Fact]
    public void AppendOwn_finds_the_file_written_under_a_versioned_name()
    {
        var (instance, root) = Create();
        File.WriteAllBytes(Path.Combine(root, "x.png"), [9]); // занято → WriteNew запишет x.v2.png

        var (path, reason) = WriteNew(instance, "x.png", [1, 2, 3]);
        Assert.Null(reason);
        Assert.Equal("x.v2.png", path);

        var (appendPath, appendReason) = AppendOwn(instance, "x.png", [7, 8]);
        Assert.Null(appendReason);
        Assert.Equal("x.v2.png", appendPath);

        // Дописано в фактический файл x.v2.png, а не в пустой x.png.
        Assert.Equal([1, 2, 3, 7, 8], File.ReadAllBytes(Path.Combine(root, "x.v2.png")));
        Assert.Equal([9], File.ReadAllBytes(Path.Combine(root, "x.png")));
    }
}
