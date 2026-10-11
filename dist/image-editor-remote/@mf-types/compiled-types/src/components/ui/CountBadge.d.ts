export type BadgeTone = 'accent' | 'muted' | 'warning';
export declare function CountBadge({ value, inline, tone, bottom }: {
    value: number;
    inline?: boolean;
    tone?: BadgeTone;
    bottom?: boolean;
}): import("react").JSX.Element;
