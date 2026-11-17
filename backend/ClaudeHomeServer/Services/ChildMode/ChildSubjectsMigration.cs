using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.ChildMode;

// Засев трёх предметов волны 1 существующим профилям детей (срез §1, Т1b): модель предметов
// добавлена профилю в Т1a, и у заведённых ранее детей Subjects пуст — без засева их главная
// осталась бы без плиток, пока админ не добавит предметы руками.
//
// Идемпотентна состоянием, маркер-файла нет: охранитель — сам непустой Subjects. Второй проход
// на старте видит непустые списки и ничего не пишет (в отличие от GlmModelAliasMigration, где
// маркер защищает ручной откат пользователя — здесь повторный проход безвреден по построению).
//
// Проверка «пусто» живёт в одном месте — под локом UserStore (SeedChildSubjectsIfEmpty), здесь
// не дублируется: проверка вне лока гонится с параллельным PUT админа и затёрла бы его список
// (тест Seed_ParallelWithAdminSet_AdminListWins).
//
// Ошибки не роняют старт (best-effort, образец GlmModelAliasMigration): несостоявшийся засев —
// лог, ребёнок до следующего старта живёт с пустым Subjects, это ровно состояние до Т1b.
public class ChildSubjectsMigration : IHostedService
{
    private readonly UserStore _users;
    private readonly ILogger<ChildSubjectsMigration> _log;

    public ChildSubjectsMigration(UserStore users, ILogger<ChildSubjectsMigration> log)
    {
        _users = users;
        _log = log;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            // GetAll() — снимок списка с живыми объектами: здесь только перечисление кандидатов,
            // проверка пустоты и запись — на сторе.
            var seeded = 0;
            foreach (var user in _users.GetAll())
            {
                if (user.ChildMode is not { } profile) continue; // обычный пользователь
                if (_users.SeedChildSubjectsIfEmpty(user.Id, SeedSubjectsOf(profile))) seeded++;
            }
            if (seeded > 0)
                _log.LogInformation("Засев предметов волны 1: обновлено детских профилей {Count}", seeded);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Засев детских предметов не выполнен — старт продолжается");
        }
        return Task.CompletedTask;
    }

    // Тройка и её порядок — плитки главной ребёнка (срез §1): math, russian, english.
    // Маршрут (тьютор, провайдер, модель) берётся из профиля: засев не выдумывает своего
    // маршрута — предмет идёт в те же «Уроки», что у ребёнка уже есть.
    private static List<ChildSubject> SeedSubjectsOf(ChildProfile profile) => [
        new() { Id = "math", Label = "Математика", Glyph = "×÷", Color = "info",
            TutorPersonaId = profile.TutorPersonaId, ProviderKey = profile.ProviderKey, Model = profile.Model },
        new() { Id = "russian", Label = "Русский язык", Glyph = "Аа", Color = "rose",
            TutorPersonaId = profile.TutorPersonaId, ProviderKey = profile.ProviderKey, Model = profile.Model },
        new() { Id = "english", Label = "Английский", Glyph = "Ab", Color = "success",
            TutorPersonaId = profile.TutorPersonaId, ProviderKey = profile.ProviderKey, Model = profile.Model },
    ];

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
