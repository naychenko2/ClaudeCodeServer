import type { McpData } from './useMcpData';
import type { McpServer } from '../../types';
export declare function McpServerList({ data, onEdit, onAdd, onCatalog, onOpenAccess, onDelete }: {
    data: McpData;
    onEdit: (server: McpServer) => void;
    onAdd: () => void;
    onCatalog?: () => void;
    onOpenAccess: () => void;
    onDelete: (server: McpServer) => void;
}): import("react").JSX.Element;
