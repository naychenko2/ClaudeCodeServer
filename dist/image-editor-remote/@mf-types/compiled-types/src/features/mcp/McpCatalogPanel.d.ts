import type { McpCatalogServer } from '../../types';
export type CatalogOpenTarget = {
    kind: 'detail';
    server: McpCatalogServer;
} | {
    kind: 'manual';
};
export declare function McpCatalogPanel({ installedNames, onPick, onManual, onClose }: {
    installedNames?: ReadonlySet<string>;
    onPick: (server: McpCatalogServer) => void;
    onManual: () => void;
    onClose: () => void;
}): import("react").JSX.Element;
