using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Prompts;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services.Prompts;

// Промпт «Мастера настройки» (OnboardingPrompts.UserMaster).
// Живая заготовка (assistantPersonaId задан) — мастер дорабатывает её через personas_update,
// а не создаёт новую (соответствует серверному предохранителю в PersonasController.Create).
// Мёртвая заготовка (id null — резолв не удался) — деградация к пути «создай персону».
// Знакомство необязательное (план: «знакомство вместо обязательного онбординга») — текст не
// должен требовать ответить немедленно и не должен запрещать пропустить его насовсем.
public class OnboardingPromptsTests
{
    [Fact]
    public void UserMaster_СЖивойЗаготовкой_НеПредлагаетСоздатьНовую()
    {
        var text = OnboardingPrompts.UserMaster("Андрей", "persona-live-123", "Ассистент");

        text.Should().Contain("persona-live-123", "id заготовки должен попасть в инструкцию");
        text.Should().Contain("mcp__personas__personas_update",
            "живую заготовку дорабатывают через update, а не создают заново");
        text.Should().NotContain("2. По итогам создай персону инструментом mcp__personas__personas_create",
            "путь создания — только для деградации без заготовки");
        text.Should().Contain("НЕ вызывай personas_create",
            "явный запрет создавать дубликат при живой заготовке");
    }

    [Fact]
    public void UserMaster_БезЖивойЗаготовки_ПредлагаетСоздать()
    {
        var text = OnboardingPrompts.UserMaster("Андрей", assistantPersonaId: null);

        text.Should().Contain("mcp__personas__personas_create",
            "деградация: заготовка мертва — мастер создаёт персону с нуля");
        text.Should().NotContain("Дорабатывай уже созданного ассистента");
    }

    [Fact]
    public void UserMaster_ЗнакомствоНеобязательное_МожноОтложитьБезЗапрета()
    {
        var text = OnboardingPrompts.UserMaster("Андрей", "persona-live-123", "Ассистент");

        text.Should().Contain("прервать в любой момент",
            "знакомство — необязательный шаг, а не блокирующий гейт");
        text.Should().NotContain("нельзя пропустить");
        text.Should().NotContain("обязательно ответь");
    }

    // Развод затравки (Знакомство v2, п.0): личная — интервью о пользователе, проектная —
    // о проекте; в проектной не должно быть вопросов о самом пользователе (он уже знаком
    // с ассистентом в личном онбординге).
    [Fact]
    public void KickoffDirectiveFor_ВыбираетДирективуПоТипуЗнакомства()
    {
        OnboardingPrompts.KickoffDirectiveFor(OnboardingKinds.User)
            .Should().Be(OnboardingPrompts.KickoffDirectiveUser);
        OnboardingPrompts.KickoffDirectiveFor(OnboardingKinds.Project)
            .Should().Be(OnboardingPrompts.KickoffDirectiveProject);
        OnboardingPrompts.KickoffDirectiveFor(null)
            .Should().Be(OnboardingPrompts.KickoffDirectiveUser, "неизвестный тип — дефолт мастера");
    }

    [Fact]
    public void KickoffDirectiveProject_НеСпрашиваетОПользователе()
    {
        var project = OnboardingPrompts.KickoffDirectiveProject;

        project.Should().Contain("проект", "проектная затравка знакомится с проектом");
        project.Should().Contain("не спрашивай",
            "о пользователе спрашивать нельзя — он уже знаком с ассистентом");
        project.Should().NotContain("как обращаться к пользователю",
            "это вопрос личного знакомства — в проектном он запрещён");
        project.Should().NotContain("чем он занимается");
    }

    // Оверлей проектного знакомства (знакомство v2, п.5): сценарий «тип проекта →
    // интервью → каркас → команда → руководитель». Тексты — дословно из заметки
    // «Тексты — Знакомство с проектом v2».
    public class ProjectOnboardingOverlayTests
    {
        private const string Pending = "pending";

        [Fact]
        public void Оверлей_НачинаетсяСВопросаОТипеПроекта_БезУгадывания()
        {
            var text = OnboardingPrompts.ProjectOnboardingOverlay("Банк", Pending, personasAvailable: true);

            // Первый шаг сценария — прямой вопрос о типе проекта, не о пользователе
            text.Should().Contain("## Шаг 1. Тип проекта");
            text.Should().Contain("этот проект больше про документы",
                "вопрос о типе — дословно из заметки");
            text.Should().Contain("личное дело", "третий вариант типа назван словами");
            text.Should().Contain("НЕ угадывай тип",
                "тип определяет только человек — угадывание по названию/папке запрещено");
            text.IndexOf("## Шаг 1", StringComparison.Ordinal)
                .Should().BeLessThan(text.IndexOf("## Шаг 2", StringComparison.Ordinal),
                    "вопрос о типе идёт раньше интервью о сути");
        }

        [Fact]
        public void Оверлей_ИнтервьюОСути_ТриЧетыреВопроса()
        {
            var text = OnboardingPrompts.ProjectOnboardingOverlay("Банк", Pending, personasAvailable: true);

            text.Should().Contain("3–4 вопроса о сути проекта");
            text.Should().Contain("что человек хочет в нём делать");
        }

        [Fact]
        public void Оверлей_Pending_НесётМаркерИСоставКаталогаПресетов()
        {
            var text = OnboardingPrompts.ProjectOnboardingOverlay("Банк", Pending, personasAvailable: true);

            text.Should().Contain("<project-preset key=\"ключ\"/>",
                "инструкция показывает формат маркера — по нему фронт рисует карточку");
            // Состав каркасов доезжает генерацией из каталога: каждый ключ и каждая папка
            // каталога обязаны попасть в промпт (иначе модель придумает свои названия)
            foreach (var preset in PresetCatalog.All)
            {
                text.Should().Contain($"\"{preset.Key}\"", $"ключ пресета {preset.Key} — из каталога");
                foreach (var folder in preset.Folders)
                    text.Should().Contain($"`{folder}`", $"папка {folder} пресета {preset.Key} — из каталога");
            }
        }

        [Fact]
        public void Оверлей_НеПредлагаетЧужихПапок_ИПроговариваетНепустуюПапку()
        {
            var text = OnboardingPrompts.ProjectOnboardingOverlay("Банк", Pending, personasAvailable: true);

            // «Команда» убрана из пресета docs — в промпте её быть не должно
            text.Should().NotContain("`Команда`");
            // Перед карточкой модель проверяет содержимое папки и предупреждает
            text.Should().Contain("создам только то, чего не хватает, ничего не перезапишу");
        }

        [Theory]
        [InlineData("docs")]     // применён
        [InlineData("none")]     // человек отказался
        [InlineData(null)]       // проект создан до фичи
        public void Оверлей_ПослеРешенияПоКаркасу_НеПредлагаетМаркер(string? presetKey)
        {
            var text = OnboardingPrompts.ProjectOnboardingOverlay("Банк", presetKey, personasAvailable: true);

            text.Should().NotContain("<project-preset",
                "маркер предлагается только при PresetKey == pending");
            text.Should().Contain("не возвращайся к папкам и маркерам",
                "модель обязана продолжить с команды, а не заново предлагать каркас");
        }

        [Fact]
        public void Оверлей_ШагКоманды_ПорядокРуководительПоследним()
        {
            var text = OnboardingPrompts.ProjectOnboardingOverlay("Банк", Pending, personasAvailable: true);

            text.Should().Contain("mcp__personas__personas_ai_team",
                "состав команды предлагает ai_team — он читает CLAUDE.md проекта");
            text.Should().Contain("mcp__personas__personas_create");
            text.Should().Contain("mcp__personas__personas_set_default");
            // Ключевой мягкий инвариант: досев прав идёт последней созданной персоне,
            // поэтому руководитель создаётся ПОСЛЕДНИМ — это держит промпт, не код
            text.Should().Contain("руководителя, если он создаётся новым, создавай ПОСЛЕДНИМ");
            text.Should().Contain("права молча достанутся не ему");
        }

        [Fact]
        public void Оверлей_СуществующийРуководитель_БезПовышенияПрав()
        {
            var text = OnboardingPrompts.ProjectOnboardingOverlay("Банк", Pending, personasAvailable: true);

            text.Should().Contain("Права у неё остались прежние",
                "дословная формулировка из заметки для выбора существующей персоны");
        }

        [Fact]
        public void Оверлей_СерверПерсонВыключен_ЧестноеОграничениеСловами()
        {
            var text = OnboardingPrompts.ProjectOnboardingOverlay("Банк", Pending, personasAvailable: false);

            text.Should().NotContain("mcp__personas__personas_create",
                "инструкции звать недоступные инструменты быть не должно");
            text.Should().Contain("отключён доступ к персонам",
                "ограничение проговаривается, а не выглядит сбоем");
            text.Should().Contain("руководителя назначите в панели «Команда»",
                "человеку дана альтернатива — дословно из заметки");
        }

        [Fact]
        public void КусокКаталога_ГенерируетсяИзPresetCatalog_ССовпадающимиКлючамиИПапками()
        {
            var block = OnboardingPrompts.BuildPresetCatalogBlock();

            foreach (var preset in PresetCatalog.All)
            {
                block.Should().Contain($"\"{preset.Key}\"");
                foreach (var folder in preset.Folders)
                    block.Should().Contain(folder);
            }
            // Сторож от ручного редактирования: кусок не должен знать папок вне каталога
            block.Should().NotContain("Команда");
        }

        // Условие показа надстройки (знакомство v2, п.5): «нет руководителя ИЛИ PresetKey ==
        // "pending"». Назначение руководителя в первом ходе НЕ гасит остаток сценария,
        // пока каркас не решён; после применения/отказа надстройка умирает.
        [Theory]
        [InlineData(null, null, true)]          // руководителя нет (проект до фичи)
        [InlineData(null, "docs", true)]        // руководителя нет — сценарий недоведён
        [InlineData(null, "pending", true)]     // руководителя нет, каркас ждёт решения
        [InlineData("p1", "pending", true)]     // руководитель есть, каркас ждёт решения
        [InlineData("p1", null, false)]         // руководитель есть, проект до фичи
        [InlineData("p1", "docs", false)]       // руководитель есть, каркас применён
        [InlineData("p1", "none", false)]       // руководитель есть, человек отказался
        public void УсловиеПоказаНадстройки_НетРуководителяИлиКаркасОжидает(
            string? defaultPersonaId, string? presetKey, bool expected)
        {
            var project = new Project { DefaultPersonaId = defaultPersonaId, PresetKey = presetKey };

            OnboardingPrompts.ProjectOverlayActive(project).Should().Be(expected);
        }
    }
}
