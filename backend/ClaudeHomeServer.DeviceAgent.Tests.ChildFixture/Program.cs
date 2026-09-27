using ClaudeHomeServer.DeviceAgent.Supervision;

// Фикстура «дочерний новой версии». Всё, что относится к контракту супервизора, — литералы:
// это и есть замороженный контракт глазами версии, которой ещё нет.
//
// Поведение — из файла fixture-mode рядом с бинарём:
//   healthy     — записать маркер healthy и жить;
//   broken      — не писать healthy и жить (битая версия);
//   switch:X    — записать healthy, переключить active на X (как обновлятор) и выйти с 75;
//   crash       — сразу выйти с кодом 3.
// Следы для теста: child-argv.txt, child.pid, child-healthy-env.txt.

var versionDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
var root = Path.GetDirectoryName(Path.GetDirectoryName(versionDir))!;
var healthyFile = Environment.GetEnvironmentVariable("AI_HOME_AGENT_HEALTHY_FILE");

File.WriteAllText(Path.Combine(versionDir, "child-argv.txt"), string.Join('\n', args));
File.WriteAllText(Path.Combine(versionDir, "child-healthy-env.txt"), healthyFile ?? "");
File.WriteAllText(Path.Combine(versionDir, "child.pid"), Environment.ProcessId.ToString());

if (int.TryParse(Environment.GetEnvironmentVariable("AI_HOME_AGENT_SUPERVISOR_PID"), out var supervisor)
    && !ParentDeathSignal.Arm(supervisor))
    return 0;
if (args is not ["run"]) return 64;

var modeFile = Path.Combine(versionDir, "fixture-mode");
var mode = File.Exists(modeFile) ? File.ReadAllText(modeFile).Trim() : "healthy";

void MarkHealthy()
{
    if (!string.IsNullOrEmpty(healthyFile)) File.WriteAllText(healthyFile, DateTime.UtcNow.ToString("O"));
}

void WriteAtomic(string name, string content)
{
    var file = Path.Combine(root, name);
    File.WriteAllText(file + ".tmp", content);
    File.Move(file + ".tmp", file, overwrite: true);
}

switch (mode)
{
    case "crash":
        return 3;
    case "broken":
        break;
    case var s when s.StartsWith("switch:"):
        MarkHealthy();
        WriteAtomic("previous", Path.GetFileName(versionDir));
        WriteAtomic("active", s["switch:".Length..]);
        return 75;
    default:
        MarkHealthy();
        break;
}

Thread.Sleep(Timeout.Infinite);
return 0;
