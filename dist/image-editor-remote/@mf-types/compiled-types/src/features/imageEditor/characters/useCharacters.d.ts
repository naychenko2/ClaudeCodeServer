import { type ImageEditCharacter } from '../api';
export declare function reloadCharacters(projectId: string): Promise<void>;
export declare function putCharacter(projectId: string, c: ImageEditCharacter): void;
export declare function dropCharacter(projectId: string, slug: string): void;
export declare function useCharacters(projectId: string | null): {
    list: ImageEditCharacter[] | null;
    error: boolean;
};
