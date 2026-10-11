import { type HTMLAttributes, type PointerEvent as ReactPointerEvent } from 'react';
import type { PanelKey, Zone } from './panelCatalog';
import { endPanelDrag } from './panelDrag';
export declare function usePanelDnd({ zone, enabled, accepts, onSwap }: {
    zone: Zone;
    enabled: boolean;
    accepts?: (k: PanelKey) => boolean;
    onSwap: (from: PanelKey, to: PanelKey) => void;
}): {
    from: "plan" | "files" | "terminal" | "skills" | "tasks" | "notes" | "knowledge" | "dossiers" | "docs" | "video" | "context" | "chatContext" | "chats" | "agents" | "characters" | "changes" | "graph" | "arch" | "team" | "preview" | "toc" | "voices" | "notesList" | "notesGraph" | "knowledgeList" | "personasList" | "projectGroups" | null;
    fromZone: Zone | null;
    fromTucked: boolean;
    active: boolean;
    accepting: boolean;
    moved: "plan" | "files" | "terminal" | "skills" | "tasks" | "notes" | "knowledge" | "dossiers" | "docs" | "video" | "context" | "chatContext" | "chats" | "agents" | "characters" | "changes" | "graph" | "arch" | "team" | "preview" | "toc" | "voices" | "notesList" | "notesGraph" | "knowledgeList" | "personasList" | "projectGroups" | null;
    end: typeof endPanelDrag;
    isOver: (tag: string) => boolean;
    guideProps: (tag: string, onDropAt: (from: PanelKey) => void) => {
        over: boolean;
        onDragOver: (e: {
            preventDefault: () => void;
            dataTransfer: DataTransfer;
        }) => void;
        onDragLeave: () => void;
        onDrop: (e: {
            preventDefault: () => void;
        }) => void;
    };
    panelProps: (k: PanelKey) => {
        draggable: boolean;
        dragged: boolean;
        dropTarget: boolean;
        rootProps: HTMLAttributes<HTMLDivElement>;
        headerProps: HTMLAttributes<HTMLDivElement> & {
            draggable?: boolean;
        };
    };
    dragSourceProps: (k: PanelKey, opts?: {
        tucked?: boolean;
        rail?: boolean;
    }) => HTMLAttributes<HTMLElement> & {
        draggable?: boolean;
    };
};
export declare function usePanelWidthDrag(width: number, setWidth: (n: number) => void, side: 'left' | 'right', columns?: number): {
    dragging: boolean;
    onPointerDown: (e: ReactPointerEvent) => void;
};
export declare function usePanelRowResize<K extends string>(weights: Partial<Record<K, number>>, setWeights: (next: Partial<Record<K, number>>) => void): {
    panelRefs: import("react").RefObject<Partial<Record<K, HTMLDivElement | null>>>;
    rowDragging: string | null;
    handleRowDrag: (aKey: K, bKey: K, tag: string) => (e: ReactPointerEvent) => void;
};
export declare function usePanelColResize(colFlex: number[], setColFlex: (next: number[]) => void): {
    colRefs: import("react").RefObject<Record<number, HTMLDivElement | null>>;
    colDragging: number | null;
    handleColDrag: (aCi: number, bCi: number, sign: 1 | -1) => (e: ReactPointerEvent) => void;
};
