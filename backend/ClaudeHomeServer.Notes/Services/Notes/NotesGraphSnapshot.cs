using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Notes;

// Внутренний неизменяемый снимок построенного графа (S3-1 фичи LLM-wiki): нужен
// линтеру (S3-2) — проверять связность раздела, не строя модель заново. Снимок —
// копия данных живой Model на момент выдачи GetGraphSnapshot: последующие изменения
// графа (запись заметки, ренейм, истечение) выданный снимок не трогают.
// internal: это внутренний шов вертикали — публичный контракт графа остаёт GetGraph.

// Узел снимка: заметка или страница вики со своим источником; Ghost=true — несозданная
// цель ссылки (ни файла, ни источника у неё нет — пустые поля, как в NoteGraphNode).
internal sealed record NotesGraphSnapshotNode(
    string Id, string Title, string SourceKey, string SourceLabel, bool Ghost);

// Ребро снимка: id → id. Дедупликация и разрешение целей уже сделаны при построении модели.
internal sealed record NotesGraphSnapshotEdge(string Source, string Target);

// Снимок целиком: все узлы (реальные + призраки), все рёбра, призраки и обратные ссылки.
// В отличие от публичного GetGraph ничего не фильтрует: линтеру нужен полный граф,
// комментарии к документам — такие же узлы. Коллекции — копии на выдаче, а не заморозка:
// словари и списки собраны заново (под интерфейсами IReadOnlyList/IReadOnlyDictionary лежат
// List и Dictionary), элементы — неизменяемые record-ы (NoteBacklinkDto). Поэтому правка
// самой коллекции выданного снимка остаётся правкой этого снимка: модель и следующий снимок
// её не видят — это проверяет тест «ПравкаКоллекцийВыданногоСнимка» (S3-2, M1/M2 ревью S3-1).
internal sealed record NotesGraphSnapshot(
    IReadOnlyList<NotesGraphSnapshotNode> Nodes,
    IReadOnlyList<NotesGraphSnapshotEdge> Edges,
    // ghost id -> отображаемое имя (тот же ключ, что у узлов с Ghost=true)
    IReadOnlyDictionary<string, string> Ghosts,
    // id цели -> входящие ссылки (тот же ключ, что в Model.Backlinks)
    IReadOnlyDictionary<string, IReadOnlyList<NoteBacklinkDto>> Backlinks);
