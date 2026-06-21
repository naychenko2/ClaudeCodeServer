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

declare module 'react-syntax-highlighter/dist/esm/languages/prism/*' {
  const language: unknown;
  export default language;
}
