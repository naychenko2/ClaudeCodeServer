export interface TocRowProps {
    text: string;
    level: number;
    onJump: () => void;
    onQuote?: () => void;
    active?: boolean;
}
export declare function TocRow({ text, level, onJump, onQuote, active }: TocRowProps): import("react").JSX.Element;
