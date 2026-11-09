export type CharacterEditing = {
    kind: 'new';
} | {
    kind: 'edit';
    slug: string;
} | null;
export declare function CharactersPanel({ projectId, editing, onEditing, contextSessionId }: {
    projectId: string;
    editing: CharacterEditing;
    onEditing: (e: CharacterEditing) => void;
    contextSessionId: string | null;
}): import("react").JSX.Element;
