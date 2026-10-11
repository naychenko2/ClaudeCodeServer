import type { McpData } from './useMcpData';
import type { McpServer } from '../../types';
export declare function McpAccessTab({ data, onClose, onAdd, onEdit }: {
    data: McpData;
    onClose: () => void;
    onAdd: () => void;
    onEdit: (server: McpServer) => void;
}): import("react").JSX.Element;
