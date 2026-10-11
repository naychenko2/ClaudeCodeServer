export declare function Stepper({ value, min, max, onChange, maxHint, ariaLabel, touch }: {
    value: number;
    min?: number;
    max: number;
    onChange: (v: number) => void;
    maxHint?: string;
    ariaLabel?: string;
    touch?: boolean;
}): import("react").JSX.Element;
