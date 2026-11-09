using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Tasks.Projector;

namespace ClaudeHomeServer.Tasks.Tests;

// S1 (фича chat-feature-focus): правило привязки чата к фиче для дверей.
// BoundFeatures = FeatureChatMembership ∪ фичи задач с SourceSessionId == чат.
// DenyReason: null — привязки нет или целевая фича среди привязанных; иначе — текст
// одной константы с названием привязанной фичы. Двери (S2) зовут только DenyReason.
public class FeatureChatBindingTests
{
    // ── Хелперы сборки фикстур (как в FeatureIndexMembershipTests) ──────────────────────────

    private static TaskItem Epic(string id, string title, string? sourceSessionId = null,
        string? executorLaunchedBy = null) => new()
    {
        Id = id,
        Title = title,
        Role = TaskRoles.Feature,
        FeatureId = null,
        SourceSessionId = sourceSessionId,
        ExecutorLaunchedBySessionId = executorLaunchedBy,
    };

    private static TaskItem Child(string id, string? featureId,
        string? sourceSessionId = null, string? executorLaunchedBy = null) => new()
    {
        Id = id,
        FeatureId = featureId,
        Role = TaskRoles.Step,
        SourceSessionId = sourceSessionId,
        ExecutorLaunchedBySessionId = executorLaunchedBy,
    };

    private static FeatureIndexSession Session(string id, string? taskId = null) =>
        new(id, taskId, null, false);

    // ── 1. Пустая привязка: свободный чат → отказа нет ──────────────────────────────────────

    // Чат ни в одной фиче (не исполнитель, не куратор, не оркестратор; задач из него нет) →
    // BoundFeatures пусто, DenyReason — null.
    [Fact]
    public void DenyReason_ПустаяПривязка_Null()
    {
        var owner = new List<TaskItem>
        {
            Epic("epicA", "Фича A"),
            Child("tA", "epicA", executorLaunchedBy: "otherChat"), // чат OTHER, не наш
        };
        var sessions = new[] { Session("freeChat") };

        FeatureChatBinding.BoundFeatures(owner, sessions, "freeChat").Should().BeEmpty();
        FeatureChatBinding.DenyReason(owner, sessions, "freeChat", "epicB").Should().BeNull();
    }

    // ── 2. Та же фича: чат ведёт epicA, заводим в epicA → отказа нет ────────────────────────

    // Чат — исполнитель задачи фичы A (executor по TaskId). Целевая фича — та же A → null.
    [Fact]
    public void DenyReason_ТажеФича_Null()
    {
        var owner = new List<TaskItem>
        {
            Epic("epicA", "Фича A"),
            Child("tA", "epicA"),
        };
        var sessions = new[] { Session("chatA", taskId: "tA") };

        FeatureChatBinding.DenyReason(owner, sessions, "chatA", "epicA").Should().BeNull();
    }

    // ── 3. Чужая фича: чат ведёт epicA, заводим в epicB → текст отказа ──────────────────────

    // Кураторский чат epicA хочет задачу в другую фичу epicB → отказ текстом константы
    // с названием привязанной фичы.
    [Fact]
    public void DenyReason_ЧужаяФича_Текст()
    {
        var owner = new List<TaskItem>
        {
            Epic("epicA", "Фича A", sourceSessionId: "chatA"),
            Epic("epicB", "Фича B"),
        };
        var sessions = new[] { Session("chatA") };

        var reason = FeatureChatBinding.DenyReason(owner, sessions, "chatA", "epicB");
        reason.Should().NotBeNull();
        reason.Should().Contain("Этот чат уже ведёт фичу «Фича A»");
        reason.Should().Contain("Одна фича — один чат");
    }

    // ── 4. Легаси {A,B}: чат, заводивший задачи двух фич, свободен в обеих ───────────────────

    // Легаси-ось: задачи созданы из чата (SourceSessionId), ни TaskId, ни оркестрации —
    // FeatureChatMembership чата не видит. BoundFeatures = {A, B} из SourceSessionId-задач;
    // целевая A — среди привязанных → null.
    [Fact]
    public void DenyReason_ЛегасиAB_ТажеФича_Null()
    {
        var owner = new List<TaskItem>
        {
            Epic("epicA", "Фича A"),
            Epic("epicB", "Фича B"),
            Child("tA", "epicA", sourceSessionId: "legacyChat"),
            Child("tB", "epicB", sourceSessionId: "legacyChat"),
        };
        var sessions = new[] { Session("legacyChat") };

        FeatureChatBinding.BoundFeatures(owner, sessions, "legacyChat")
            .Should().BeEquivalentTo(new[] { "epicA", "epicB" });
        FeatureChatBinding.DenyReason(owner, sessions, "legacyChat", "epicA").Should().BeNull();
    }

    // ── 5. Легаси {A,B} + третья фича C → отказ ─────────────────────────────────────────────

    [Fact]
    public void DenyReason_ЛегасиAB_ТретьяФича_Текст()
    {
        var owner = new List<TaskItem>
        {
            Epic("epicA", "Фича A"),
            Epic("epicB", "Фича B"),
            Epic("epicC", "Фича C"),
            Child("tA", "epicA", sourceSessionId: "legacyChat"),
            Child("tB", "epicB", sourceSessionId: "legacyChat"),
        };
        var sessions = new[] { Session("legacyChat") };

        var reason = FeatureChatBinding.DenyReason(owner, sessions, "legacyChat", "epicC");
        reason.Should().NotBeNull();
        // Название — первой привязанной фичы (порядок owner-среза: epicA).
        reason.Should().Contain("Этот чат уже ведёт фичу «Фича A»");
    }

    // ── 6. Привязка только через SourceSessionId дочерней задачи → отказ на чужую ────────────

    // Чат привязан к epicA ТОЛЬКО тем, что дочерняя задача заведена из него (SourceSessionId):
    // в FeatureChatMembership его нет (не исполнитель, эпик не его). Целевая epicB → отказ.
    // Мутация (убрать SourceSessionId-ось из BoundFeatures): привязка пустая → DenyReason
    // null → кейс краснеет.
    [Fact]
    public void DenyReason_ПривязкаТолькоЧерезSourceSessionId_ОтказНаЧужую()
    {
        var owner = new List<TaskItem>
        {
            Epic("epicA", "Фича A"),
            Epic("epicB", "Фича B"),
            Child("tA", "epicA", sourceSessionId: "creatorChat"), // создатель — creatorChat
        };
        var sessions = new[] { Session("creatorChat") };

        FeatureChatBinding.BoundFeatures(owner, sessions, "creatorChat")
            .Should().BeEquivalentTo(new[] { "epicA" });

        var reason = FeatureChatBinding.DenyReason(owner, sessions, "creatorChat", "epicB");
        reason.Should().NotBeNull("привязка через SourceSessionId дочерней задачи — тоже привязка");
        reason.Should().Contain("Фича A");
    }

    // ── Мутация: сама задача-эпик, заведённая из чата, — привязка к своей фиче ───────────────

    // Эпик создан из чата (SourceSessionId) — это уже curator-членство в FeatureChatMembership;
    // вторая ось не должна давать ложного отказа на саму фичу.
    [Fact]
    public void DenyReason_ЭпикИзЧата_СвояФича_Null()
    {
        var owner = new List<TaskItem>
        {
            Epic("epicA", "Фича A", sourceSessionId: "creatorChat"),
        };
        var sessions = new[] { Session("creatorChat") };

        FeatureChatBinding.DenyReason(owner, sessions, "creatorChat", "epicA").Should().BeNull();
    }
}
