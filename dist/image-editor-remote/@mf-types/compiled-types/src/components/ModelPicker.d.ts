import type { ModelOption } from '../lib/models';
import { type UsageKey } from '../lib/models';
interface Props {
    value: string;
    options: ModelOption[];
    onChange: (v: string) => void;
    columns?: number;
    collapsible?: boolean;
    includeDirect?: boolean;
    hideDefault?: boolean;
    usage?: UsageKey;
}
export declare function WindowBadge({ tokens }: {
    tokens?: number;
}): import("react").JSX.Element | null;
export declare function ModelPicker({ value, options: allOptions, onChange, collapsible, includeDirect, hideDefault, usage }: Props): import("react").JSX.Element;
export {};
