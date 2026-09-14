namespace ClaudeHomeServer.Services.CodeGraph;

// Узкий шов инспекции графа кода (Этап 5, волна 2, разрез Dossiers↔CodeGraph).
// Контракт одного намерения — «прочитать снимок графа для якорения паспортов».
// Dossiers (DossierCaptureService, DossierRecallService) — единственный потребитель,
// два метода: синхронный снапшот (узлы + BuiltAt) и неблокирующий триггер перестроения.
//
// Реализация — `CodeGraphInspector` (Main, тонкий форвардер на `CodeGraphService`).
// По той же причине, что и `IGitRefSnapshotStore`/`IGitCommitInspector`: контракт
// живёт в Core, а не в вертикали CodeGraph, чтобы вертикаль-потребитель не получала
// запрещённую сторожем границ связь «вертикаль → вертикаль».
//
// Снимок урезан до того, что Dossiers реально использует (узлы для FQN-якорения +
// BuiltAt как сигнатура для кеша статусов); полный DTO (`CodeGraphSnapshotDto`,
// рёбра, god-узлы, метаданные файлов) остаётся в Services.CodeGraph — REST-контроллеру
// и MCP-тулсету этого контракта не нужно.

// Один узел графа: минимальный набор полей для якорения паспортов по FQN (ADR-004 §3).
// FQN — стабильный идентификатор типа; SourceFile нужен чтобы матчить изменённые файлы
// коммита с узлами графа.
// Имя типа — `DossierCodeGraphNode` (не `CodeGraphNode`), чтобы не конфликтовать с
// внутренним `CodeGraphNode` из вертикали CodeGraph (`Services.CodeGraph.Core`).
public sealed record DossierCodeGraphNode(string FullyQualifiedName, string SourceFile);

// Снимок графа для Dossiers: BuiltAt — сигнатура для ленивого пересчёта статусов
// (те же HEAD/сигнатура, что у DossierRecallService); Nodes — узлы для якорения FQN
// файлов коммита. IsStale — фронт может красить, но Dossiers не использует.
public sealed record CodeGraphSnapshot(
    DateTimeOffset BuiltAt,
    IReadOnlyList<DossierCodeGraphNode> Nodes,
    bool IsStale);

public interface ICodeGraphInspector
{
    // Снимок графа для указанного дерева. null — граф ещё не построен (CodeGraphService
    // строит лениво по первому запросу). Возвращать null не ошибка — вызывающий
    // сам решает, триггерить ли перестроение фоном.
    Task<CodeGraphSnapshot?> GetSnapshotAsync(string rootPath, CancellationToken ct);

    // Фоновая сборка графа, если сейчас никто не строит: дёргает один раз без
    // блокировки. Используется DossierCaptureService, когда снапшот пуст (граф ещё
    // не построен для этого дерева) — лучше не задерживать захват, а фоном запросить
    // построение: следующие коммиты этого дерева получат символы.
    void StartRebuildIfIdle(string rootPath);
}