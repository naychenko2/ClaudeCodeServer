declare module 'react-syntax-highlighter/dist/esm/prism-light' {
  import type { ComponentType } from 'react';
  const SyntaxHighlighter: ComponentType<{
    language?: string;
    style?: Record<string, React.CSSProperties>;
    customStyle?: React.CSSProperties;
    codeTagProps?: React.HTMLAttributes<HTMLElement>;
    wrapLongLines?: boolean;
    children: string;
  }> & { registerLanguage: (name: string, language: unknown) => void };
  export default SyntaxHighlighter;
}

declare module 'react-syntax-highlighter/dist/esm/styles/prism' {
  const oneLight: Record<string, React.CSSProperties>;
  export { oneLight };
}

// export {} делает файл модулем — без этого declare module 'react' заместил бы
// типы React целиком вместо расширения (augmentation работает только из модуля)
export {};

declare module 'react-syntax-highlighter/dist/esm/languages/prism/*' {
  const language: unknown;
  export default language;
}

declare module 'react' {
  // eslint-disable-next-line @typescript-eslint/no-unused-vars -- общий тип React, T здесь не используется
  interface IframeHTMLAttributes<T> {
    // ADR-006 §3: вторая линия защиты (Chrome) — iframe без кук, даже если в sandbox
    // ошибочно появится allow-same-origin; неподдерживающими браузерами игнорируется
    credentialless?: boolean;
  }
}
