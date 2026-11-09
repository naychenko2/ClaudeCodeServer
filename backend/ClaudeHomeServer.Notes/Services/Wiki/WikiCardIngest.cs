namespace ClaudeHomeServer.Services.Notes.Wiki;

// Общая склейка «карточка → заявка» (S7b0b): один код для двух входов — инструмента
// wiki_ingest (NotesToolset) и REST POST api/wiki/ingest/note (WikiController). До этого
// шаг жил приватным в тулсете; второй вход просил бы второго себя, а две формулы дома и
// origin когда-нибудь разошлись бы: их поставляет карточка, поставленная захватом (S6h).
//
// Границы: захват (CaptureNote/CaptureFile) — вызывающего: он знает, что за источник, и
// формулирует отказ захвата; очередь разбора и её исходы — WikiIngestService. Здесь ровно
// то, что между ними: прочесть карточку, взять с неё origin/ref, восстановить дом по месту
// карточки, собрать заявку. Отказ чтения карточки — IngestResult с OutcomeRejected и
// причиной: отдельного «исхода карточки» нет, у вызывающих одна мера отказа.
public static class WikiCardIngest
{
    /// <summary>Заявка по уже захваченной карточке. cap обязан быть успешным (Ok) — это
    /// проверяет вызывающий, он же называет причину отказа захвата. Origin и ref — с самой
    /// карточки: их поставил захват, вторая формула здесь обязана бы когда-нибудь разойтись
    /// с первой. Дом — по месту карточки: его тоже выбрал захват.</summary>
    public static async Task<WikiIngestService.IngestResult> RunAsync(
        WikiIngestService ingest, WikiSourceCapture.Result cap, string root, string ownerId, CancellationToken ct)
    {
        if (WikiFileWriter.Read(root, "pages/" + cap.CardRelPath!) is not { } cardText
            || WikiFrontmatter.Parse(cardText).Model is not { } card
            || card.Origin is not { Length: > 0 } origin
            || card.Ref is not { Length: > 0 } sourceRef)
            return new WikiIngestService.IngestResult(
                WikiIngestService.OutcomeRejected, "карточка источника не читается — заявки нет", null);
        if (HomeOfCard(cap.CardRelPath!) is not { } home)
            return new WikiIngestService.IngestResult(
                WikiIngestService.OutcomeRejected, "карточка источника встала вне раздела вики — заявки нет", null);

        return await ingest.IngestAsync(ownerId, new WikiIngestService.Request(
            origin, sourceRef, cap.CardRelPath!, cap.SourceText!, home.Home, home.Target), ct);
    }

    /// <summary>Дом источника для заявки — по разделу, где легла карточка. В «Общее»
    /// карточка могла лечь только как явный target личного источника (ADR-036, решение 3),
    /// поэтому target восстанавливается вместе с домом.</summary>
    public static (string Home, string? Target)? HomeOfCard(string cardRel) => WikiSections.Parse(cardRel) switch
    {
        { } key when key.StartsWith(WikiSections.ProjectsPrefix, StringComparison.Ordinal) => (key, null),
        WikiSections.Common => (WikiSections.Personal, WikiSections.Common),
        WikiSections.Personal => (WikiSections.Personal, null),
        _ => null,
    };
}
