export interface SegmentOptionState {
    disabled?: boolean;
    muted?: boolean;
    title?: string;
}
export declare const SEG_MUTED_BORDER: string;
interface SegmentedControlProps<T extends string> {
    value: T;
    options: ({
        value: T;
        label: string;
    } & SegmentOptionState)[];
    onChange: (v: T) => void;
    columns?: number;
    touch?: boolean;
}
export declare function SegmentedControl<T extends string>({ value, options, onChange, columns, touch }: SegmentedControlProps<T>): import("react").JSX.Element;
export {};
