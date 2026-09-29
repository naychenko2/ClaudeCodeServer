using ClaudeHomeServer.HandsBridge.Browser.Launch;
using ClaudeHomeServer.HandsBridge.Policy;
using Xunit;

namespace HandsBridge.Policy.Tests;

/// <summary>Запуск Chrome браузерной руки (план Ш4): поиск, командная строка, занятость профиля.</summary>
public class ChromeLaunchTests
{
    private static Func<string, string?> Env(params (string Name, string Value)[] vars) =>
        name => vars.FirstOrDefault(v => v.Name == name).Value;

    // ---------- поиск Chrome ----------

    [Fact]
    public void App_paths_win_over_install_folders_and_hkcu_over_hklm()
    {
        var env = Env(("ProgramFiles", @"C:\Program Files"), ("LOCALAPPDATA", @"C:\Users\me\AppData\Local"));
        var candidates = ChromeLocator.Candidates(["\"D:\\Chrome\\chrome.exe\"", @"E:\Chrome\chrome.exe"], env);

        Assert.Equal(
        [
            @"D:\Chrome\chrome.exe",
            @"E:\Chrome\chrome.exe",
            Path.Combine(@"C:\Program Files", "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(@"C:\Users\me\AppData\Local", "Google", "Chrome", "Application", "chrome.exe"),
        ], candidates);
    }

    [Fact]
    public void First_existing_candidate_is_taken()
    {
        var env = Env(("ProgramFiles", @"C:\PF"), ("ProgramFiles(x86)", @"C:\PF86"));
        var x86 = Path.Combine(@"C:\PF86", "Google", "Chrome", "Application", "chrome.exe");

        var found = ChromeLocator.Find([null, @"D:\gone\chrome.exe"], env, path => path == x86);

        Assert.Equal(x86, found);
    }

    [Fact]
    public void No_chrome_anywhere_gives_null()
    {
        var found = ChromeLocator.Find([@"D:\gone\chrome.exe"], Env(("LOCALAPPDATA", @"C:\L")), _ => false);

        Assert.Null(found);
        Assert.Contains("install Google Chrome", ChromeLocator.NotFoundRefusal);
    }

    [Fact]
    public void Chrome_path_override_disables_the_search_even_when_missing()
    {
        var env = Env((ChromeLocator.OverrideVariable, @"C:\nowhere\chrome.exe"), ("ProgramFiles", @"C:\PF"));

        Assert.Null(ChromeLocator.Find([@"D:\Chrome\chrome.exe"], env, path => path != @"C:\nowhere\chrome.exe"));
        Assert.Equal(@"C:\nowhere\chrome.exe", ChromeLocator.Find([], env, _ => true));
    }

    [Fact]
    public void Duplicate_candidates_are_dropped_case_insensitively()
    {
        var candidates = ChromeLocator.Candidates([@"C:\PF\Google\Chrome\Application\chrome.exe", @"c:\pf\google\chrome\application\CHROME.EXE"], Env());

        Assert.Single(candidates);
    }

    // ---------- командная строка ----------

    [Fact]
    public void Arguments_carry_pipes_profile_and_quiet_start_flags()
    {
        var args = ChromeCommandLine.Arguments(@"C:\Agent\browser-profiles\abc", "1234", "5678");

        Assert.Equal(
        [
            "--remote-debugging-pipe",
            "--remote-debugging-io-pipes=1234,5678",
            @"--user-data-dir=C:\Agent\browser-profiles\abc",
            "--no-first-run",
            "--no-default-browser-check",
            "--hide-crash-restore-bubble",
            "about:blank",
        ], args);
        // Порт отладки не открывается никогда: подключиться можно только по трубе
        Assert.DoesNotContain(args, a => a.StartsWith("--remote-debugging-port", StringComparison.Ordinal));
    }

    [Fact]
    public void User_ui_language_goes_to_interface_and_accept_language()
    {
        var args = ChromeCommandLine.Arguments(@"C:\Agent\browser-profiles\abc", "1234", "5678", "ru-RU");

        // Без явного языка свежий профиль слал сайтам чужой Accept-Language (Ш8: example.org на арабском)
        Assert.Equal(["--lang=ru-RU", "--accept-lang=ru-RU,ru", "about:blank"], args.TakeLast(3));
        Assert.Single(args, a => a.StartsWith("--lang=", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("ru-RU", new[] { "--lang=ru-RU", "--accept-lang=ru-RU,ru" })]
    [InlineData("en", new[] { "--lang=en", "--accept-lang=en" })]
    [InlineData("zh-Hans-CN", new[] { "--lang=zh-Hans-CN", "--accept-lang=zh-Hans-CN,zh" })]
    [InlineData(null, new string[0])]
    [InlineData("", new string[0])]
    [InlineData("C.UTF-8", new string[0])]
    [InlineData("ru-RU --remote-debugging-port=9222", new string[0])]
    public void Language_arguments_accept_only_language_tags(string? language, string[] expected) =>
        Assert.Equal(expected, ChromeCommandLine.LanguageArguments(language));

    [Theory]
    [InlineData(@"C:\Users\me\AppData\Local\AiHomeAgent\browser-profiles\0123456789abcdef")]
    [InlineData(@"C:\Users\Иван Петров\AppData\Local\AiHomeAgent\browser-profiles\key")]
    [InlineData(@"C:\dir with space\trailing slash\")]
    [InlineData(@"C:\odd ""quote"" dir\\")]
    public void Command_line_splits_back_into_the_same_arguments(string profile)
    {
        const string chrome = @"C:\Program Files\Google\Chrome\Application\chrome.exe";
        var args = ChromeCommandLine.Arguments(profile, "11", "22", "ru-RU");

        var parts = HandsArguments.Split(ChromeCommandLine.Build(chrome, args));

        Assert.Equal([chrome, .. args], parts);
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("", "\"\"")]
    [InlineData("a b", "\"a b\"")]
    [InlineData(@"a\b c\", @"""a\b c\\""")]
    [InlineData("say \"hi\"", @"""say \""hi\""""")]
    public void Quote_follows_command_line_to_argv_rules(string argument, string expected) =>
        Assert.Equal(expected, ChromeCommandLine.Quote(argument));

    [Fact]
    public void Chrome_path_with_quotes_is_rejected() =>
        Assert.Throws<ArgumentException>(() => ChromeCommandLine.Build("C:\\a\"b\\chrome.exe", []));

    // ---------- занятость профиля ----------

    [Fact]
    public void Profile_without_lockfile_and_window_is_free()
    {
        var dir = TempProfile();
        try
        {
            var state = ChromeProfileLock.Check(dir, _ => false);

            Assert.False(state.Busy);
            Assert.Contains("lockfile нет", state.Detail);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Stale_lockfile_left_by_a_killed_chrome_does_not_block()
    {
        var dir = TempProfile();
        try
        {
            File.WriteAllText(Path.Combine(dir, ChromeProfileLock.LockFileName), "");

            Assert.False(ChromeProfileLock.Check(dir, _ => false).Busy);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Held_lockfile_means_busy()
    {
        var dir = TempProfile();
        try
        {
            using (new FileStream(Path.Combine(dir, ChromeProfileLock.LockFileName), FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            {
                var state = ChromeProfileLock.Check(dir, _ => false);

                Assert.True(state.Busy);
                Assert.Contains("lockfile держат", state.Detail);
            }
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Message_window_titled_with_the_profile_means_busy()
    {
        var dir = TempProfile();
        try
        {
            string? asked = null;
            var state = ChromeProfileLock.Check(dir, path => (asked = path) == dir);

            Assert.True(state.Busy);
            Assert.Equal(dir, asked);
            Assert.Contains("close that Chrome window", ChromeProfileLock.BusyRefusal);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    private static string TempProfile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "chrome-profile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
