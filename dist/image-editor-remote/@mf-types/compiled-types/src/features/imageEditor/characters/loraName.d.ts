export declare function loraDisplayName(name: string): string;
export declare function loraLabels(names: string[], trained?: Record<string, string>, nameMax?: number): Map<string, string>;
export declare function labelOrShortName(labels: Map<string, string>, name: string): string;
export declare function ownLoraLabel(own: string, ownTrainedAt: string | null | undefined, list: {
    available: boolean;
    items: string[];
    trained?: Record<string, string>;
} | null): string;
export declare function loraLabelParts(label: string): {
    name: string;
    rest: string;
};
