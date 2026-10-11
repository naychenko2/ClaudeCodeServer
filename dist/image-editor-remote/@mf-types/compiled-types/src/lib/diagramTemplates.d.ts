export type DiagramKind = 'excalidraw' | 'drawio' | 'mermaid';
export interface DiagramMeta {
    /** Расширение файла (без точки) */
    ext: string;
    /** Название в диалоге */
    label: string;
    /** Короткая подсказка-описание */
    hint: string;
    /** Стартовое содержимое файла */
    template: string;
}
export declare const DIAGRAM_META: Record<DiagramKind, DiagramMeta>;
export declare const DIAGRAM_KINDS: DiagramKind[];
/** Дефолтное имя файла для типа (без папки) */
export declare function diagramFileName(kind: DiagramKind): string;
/**
 * Заменить расширение в имени на расширение выбранного типа — но только если
 * текущий хвост совпадает с расширением одного из типов (т.е. имя ещё не правили
 * руками под своё). Пользовательское расширение («schema.v2.drawio») не трогаем.
 */
export declare function retargetDiagramExt(name: string, kind: DiagramKind): string;
