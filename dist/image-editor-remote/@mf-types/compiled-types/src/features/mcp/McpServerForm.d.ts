import type { McpData } from './useMcpData';
import type { McpServer, McpServerCatalogDraft } from '../../types';
export declare function McpServerForm({ data, server, catalogDraft, onDone, onCancel }: {
    data: McpData;
    server: McpServer | null;
    catalogDraft?: McpServerCatalogDraft | null;
    onDone: () => void;
    onCancel: () => void;
}): import("react").JSX.Element;
