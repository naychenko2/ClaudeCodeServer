import { type ImageEditCatalog } from '../api';
export declare const getCatalog: (projectId: string) => ImageEditCatalog | null;
export declare function loadCatalog(projectId: string): Promise<ImageEditCatalog | null>;
export declare function useCatalog(projectId: string | null): ImageEditCatalog | null;
