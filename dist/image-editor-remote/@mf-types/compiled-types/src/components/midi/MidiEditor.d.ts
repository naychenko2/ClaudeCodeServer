export type MidiEditorProps = {
    load: () => Promise<ArrayBuffer>;
    fileName: string;
    variant: 'inline' | 'full';
    onExpand?: () => void;
    onDownload?: () => void;
    onRetry?: () => void;
};
export declare function MidiEditor({ load, fileName, variant, onExpand, onDownload, onRetry }: MidiEditorProps): import("react").JSX.Element;
