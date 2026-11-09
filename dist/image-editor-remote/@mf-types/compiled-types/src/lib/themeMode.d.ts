export type ThemeMode = 'light' | 'dark' | 'system';
export type EffectiveTheme = 'light' | 'dark';
export declare function getEffectiveTheme(): EffectiveTheme;
export declare function getThemeMode(): ThemeMode;
export declare function setThemeMode(mode: ThemeMode): void;
export declare function subscribeThemeMode(fn: () => void): () => void;
export declare function useThemeMode(): ThemeMode;
