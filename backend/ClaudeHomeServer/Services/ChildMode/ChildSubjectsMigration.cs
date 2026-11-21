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
//
// Третий проход — миграция предметов волн 2–3 в проекты-предметы (Б17b, ADR-041 ред. 7,
// решение 75, разрез Александра §13): см. MigrateSubjectsToProjectsAsync ниже.
public class ChildSubjectsMigration : IHostedService
{
    // Первый предмет тройки — он же досев старых детских чатов (тот же id, что в SeedSubjectsOf):
    // два прохода пишут один и тот же ключ, разночтения между ними быть не должно.
    public const string DefaultSubjectId = "math";

    private readonly UserStore _users;
    private readonly SessionManager _sessions;
    // Миграция предметов волн 2–3 (Б17b): провижер проекта (Б8b), сторы персон и проектов —
    // те же экземпляры, что у точек записи профиля (Singleton), и тот же статический реестр
    // замков childId:subjectId, что у ChildSubjectProjectProvisioner / ChildTutorProvisioner.
    private readonly ChildSubjectProjectProvisioner _subjectProjects;
    private readonly PersonaManager _personas;
    private readonly ProjectManager _projects;
    private readonly ILogger<ChildSubjectsMigration> _log;

    public ChildSubjectsMigration(UserStore users, SessionManager sessions,
        ChildSubjectProjectProvisioner subjectProjects, PersonaManager personas,
        ProjectManager projects, ILogger<ChildSubjectsMigration> log)
    {
        _users = users;
        _sessions = sessions;
        _subjectProjects = subjectProjects;
        _personas = personas;
        _projects = projects;
        _log = log;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
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

        // Отдельный try: несостоявшийся засев не отменяет перенос предметов, перенесённые
        // предметы — не зависят от засева (у волн 2–3 Subjects непустой, засев их не трогает).
        try
        {
            await MigrateSubjectsToProjectsAsync();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Миграция предметов волн 2–3 не выполнена — старт продолжается");
        }
        return;
    }

    // ——— Миграция предметов волн 2–3 (Б17b, ADR-041 ред. 7, решение 75, разрез §13) ———
    // Для каждого непомеченного предмета: проект-предмета (провижер Б8b) → маркер Migrated.
    // Прежний репетитор и модель предмета (репетитор и модель предмета) сняты Б17c: руководителя
    // проекта ставит живой провижер (Б17a) на PUT профиля и создании урока, перенос старой персоны
    // здесь не делается. Копия материалов волны 3 в «Для репетитора/» снята (Х-W4-19, хвост
    // решения 68): данных нет — каталога child-subjects нет ни на проде, ни на стендах (Х-W4-18),
    // переносить нечего.
    //
    // Порядок записи — порядок сбоя: маркер пишется последним, поэтому непомеченный предмет =
    // несостоявшийся перенос, и следующий старт повторяет его с начала. Все шаги идемпотентны:
    // проект — по маркеру ChildSubjectId.
    //
    // Старые уроки (детские чаты вне проекта) миграция не трогает вовсе: они остаются
    // «старыми уроками только для чтения» (отказ Б9a), в проект не переносятся — транскрипт
    // CLI привязан к cwd; PrivateToUserId им не ставится (решение Андрея 19.11: приватности
    // уроков нет).
    private async Task MigrateSubjectsToProjectsAsync()
    {
        var migrated = 0;
        foreach (var user in _users.GetAll())
        {
            if (user.ChildMode is not { } profile) continue; // обычный пользователь
            // Снимок списка: параллельный PUT заменяет объект профиля; работа идёт по ключу
            // под локом с перечитыванием живого предмета.
            foreach (var candidate in profile.Subjects.ToArray())
            {
                if (candidate is null || candidate.Migrated) continue; // повтор пропускает по маркеру
                if (await MigrateSubjectAsync(user.Id, candidate.Id)) migrated++;
            }
        }
        if (migrated > 0)
            _log.LogInformation("Детский режим: миграция предметов волн 2–3: перенесено предметов {Count}", migrated);
    }

    // Один предмет: true — перенесён и помечен; false — остался на старых полях до следующего
    // старта (профиль/предмет исчезли, дом не настроен, живой руководитель уже другой, живого
    // тьютора нет). Сбой шага — лог и false: исключение наружу не выходит (по умолчанию .NET
    // гасит хост по отказавшему IHostedService — старт не должен умирать от одного предмета).
    // internal — тестовая точка гонки (ChildSubjectsMigrationTests): тест вызывает ровно тот
    // метод, который зовёт старт, а не копию его логики.
    internal async Task<bool> MigrateSubjectAsync(string childId, string subjectId)
    {
        if (string.IsNullOrWhiteSpace(subjectId)) return false;

        var me0 = _users.GetById(childId);
        if (me0?.ChildMode is not { } profile0) return false;
        if (profile0.Subjects.FirstOrDefault(s => s.Id == subjectId) is not { } candidate) return false;
        if (candidate.Migrated) return false; // повтор пропускает по маркеру

        // 1. Проект-предмета (Б8b) — ДО своего лока, ровно как зовёт его ChildTutorProvisioner
        // (тот держит свой замок и вызывает EnsureSubjectProjectAsync под ним; семафор
        // не-реентерабелен — под своим же ключом он встал бы ждать сам себя). Идемпотентность
        // проекта держится собственным замком провижера; наш лока — следующий шаг: та же
        // точка правды, что у точек записи профиля (ChildTutorProvisioner.LockFor, общий
        // статический словарь, ключ childId:subjectId): PUT профиля / создание урока и
        // миграция одного предмета не провятся параллельно — иначе у предмета встают два
        // руководителя (мутация «снять лока» краснит тест гонки).
        if (await _subjectProjects.EnsureSubjectProjectAsync(childId, candidate) is not { } ensured)
            return false; // дом не настроен или предмета нет — предмет на старых полях

        var gate = ChildTutorProvisioner.LockFor(childId + ":" + subjectId);
        await gate.WaitAsync();
        try
        {
            // Перечитываем под локом: параллельный PUT профиля или параллельный провижер того
            // же ключа мог уже провить предмет или удалить его. Что было до семафора — не аргумент.
            var me = _users.GetById(childId);
            if (me?.ChildMode is not { } child) return false;
            if (child.Subjects.FirstOrDefault(s => s.Id == subjectId) is not { } subject) return false;
            if (subject.Migrated) return false;
            var project = _projects.GetById(ensured.Id);
            if (project is null) return false; // проект снесли между шагами — на старых полях

            // 2. Маркер — последним: предмет помечен только когда все шаги состоялись.
            _users.MarkChildSubjectMigrated(childId, subjectId);
            return true;
        }
        catch (Exception ex)
        {
            // Сбой любого шага (диск, запись): предмет не помечен — следующий
            // старт повторит; остальные предметы продолжаются.
            _log.LogWarning(ex, "Детский режим: предмет не перенесён в проект-предмет (ребёнок {Owner}, предмет {Subject})",
                childId, subjectId);
            return false;
        }
        finally
        {
            gate.Release();
        }
    }

    // Тройка и её порядок — плитки главной ребёнка (срез §1): math, russian, english.
    // Маршрута у предмета нет (Б17c): репетитора и модель задаёт проект-предмет.
    private static List<ChildSubject> SeedSubjectsOf(ChildProfile profile) => [
        new() { Id = DefaultSubjectId, Label = "Математика", Glyph = "×÷", Color = "info" },
        new() { Id = "russian", Label = "Русский язык", Glyph = "Аа", Color = "rose" },
        new() { Id = "english", Label = "Английский", Glyph = "Ab", Color = "success" },
    ];

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
