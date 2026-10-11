export declare const RAIL_HAT_H = 13;
export declare function RailHat({ side, label, title, collapse, railHover }: {
    side: 'left' | 'right';
    label: string;
    title?: string;
    collapse?: {
        collapsed: boolean;
        onToggle: () => void;
    };
    railHover?: boolean;
}): import("react").JSX.Element;
