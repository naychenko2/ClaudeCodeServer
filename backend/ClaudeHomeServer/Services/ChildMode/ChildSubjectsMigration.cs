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
//
// Второй проход — те же id на чатах (Т1b2): детские чаты, заведённые до Т1b, остались без
// ChildSubject, и на запуске хода И1-бис отказывает им «Этот предмет закрыт». Досев ставит
// первый предмет тройки — тот же id, что засев пишет профилю (DefaultSubjectId).
// Идёт ПОСЛЕ засева профилей: предмет
// чата должен существовать в профиле этого ребёнка, иначе чат получил бы id, которого нет.
public class ChildSubjectsMigration : IHostedService
{
    // Первый предмет тройки — он же досев старых детских чатов (тот же id, что в SeedSubjectsOf):
    // два прохода пишут один и тот же ключ, разночтения между ними быть не должно.
    public const string DefaultSubjectId = "math";

    private readonly UserStore _users;
    private readonly SessionManager _sessions;
    private readonly ILogger<ChildSubjectsMigration> _log;

    public ChildSubjectsMigration(UserStore users, SessionManager sessions, ILogger<ChildSubjectsMigration> log)
    {
        _users = users;
        _sessions = sessions;
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
            // Проход по чатам — на сторе сессий (там же и проверка «предмета нет», и запись):
            // параллельное создание детского чата с предметом пишет то же поле.
            var backfilled = _sessions.BackfillChildSubject(DefaultSubjectId);
            if (seeded > 0)
                _log.LogInformation("Засев предметов волны 1: обновлено детских профилей {Count}", seeded);
            if (backfilled > 0)
                _log.LogInformation("Досев предмета старым детским чатам: изменено чатов {Count}", backfilled);
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
        new() { Id = DefaultSubjectId, Label = "Математика", Glyph = "×÷", Color = "info",
            TutorPersonaId = profile.TutorPersonaId, ProviderKey = profile.ProviderKey, Model = profile.Model },
        new() { Id = "russian", Label = "Русский язык", Glyph = "Аа", Color = "rose",
            TutorPersonaId = profile.TutorPersonaId, ProviderKey = profile.ProviderKey, Model = profile.Model },
        new() { Id = "english", Label = "Английский", Glyph = "Ab", Color = "success",
            TutorPersonaId = profile.TutorPersonaId, ProviderKey = profile.ProviderKey, Model = profile.Model },
    ];

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
