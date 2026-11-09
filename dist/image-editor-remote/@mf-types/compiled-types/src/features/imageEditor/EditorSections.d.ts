import type { ReactNode } from 'react';
import { Brush } from 'lucide-react';
import type { Tool } from './marks';
export declare const TOOLS: {
    id: Tool;
    icon: typeof Brush;
    label: string;
    title: string;
}[];
export declare function SectionHint({ children }: {
    children: ReactNode;
}): import("react").JSX.Element;
export declare function MarksTools({ tool, onTool, marksCount, onClear, disabled }: {
    tool: Tool;
    onTool: (t: Tool) => void;
    marksCount: number;
    onClear: () => void;
    disabled: boolean;
}): import("react").JSX.Element;
export declare function MobileToolbar({ tool, onTool }: {
    tool: Tool;
    onTool: (t: Tool) => void;
}): import("react").JSX.Element;
