import { useEffect, useRef, useState } from 'react';
import { C } from '../lib/design';

interface OfficeConfig {
  serverUrl: string;
  document: { fileType: string; key: string; title: string; url: string };
  editorConfig: { mode: string; lang: string };
}

interface Props {
  projectId: string;
  filePath: string;
}

declare global {
  interface Window {
    DocsAPI?: {
      DocEditor: new (
        elementId: string,
        config: {
          document: OfficeConfig['document'];
          editorConfig: OfficeConfig['editorConfig'];
          documentType?: string;
          height?: string;
          width?: string;
          events?: Record<string, () => void>;
        }
      ) => { destroyEditor: () => void };
    };
  }
}

const DOC_TYPES: Record<string, string> = {
  docx: 'word', doc: 'word',
  xlsx: 'cell', xls: 'cell',
  pptx: 'slide', ppt: 'slide',
};

// Цвета темы Claude Home — пишем в localStorage перед инициализацией OO,
// чтобы themeinit.js внутри iframe применил правильный CSS ещё на старте.
// OO iframe и наш сайт на одном origin → localStorage общий.
const CLAUDE_HOME_THEME = {
  id: 'theme-claude-home',
  type: 'light',
  name: 'Claude Home',
  colors: {
    'toolbar-header-document': '#EDE7DC',
    'toolbar-header-spreadsheet': '#EDE7DC',
    'toolbar-header-presentation': '#EDE7DC',
    'toolbar-header-pdf': '#EDE7DC',
    'toolbar-header-visio': '#EDE7DC',
    'text-toolbar-header-on-background-document': '#39332B',
    'text-toolbar-header-on-background-spreadsheet': '#39332B',
    'text-toolbar-header-on-background-presentation': '#39332B',
    'text-toolbar-header-on-background-pdf': '#39332B',
    'text-toolbar-header-on-background-visio': '#39332B',
    'background-normal': '#F4F0E8',
    'background-toolbar': '#EDE7DC',
    'background-toolbar-tab': '#EDE7DC',
    'background-toolbar-additional': '#E0D7C8',
    'background-primary-dialog-button': '#D97757',
    'background-accent-button': '#D97757',
    'background-pane': '#EDE7DC',
    'background-contrast-popover': '#F4F0E8',
    'highlight-button-hover': '#E0D7C8',
    'highlight-button-pressed': '#D2C8B8',
    'highlight-button-pressed-hover': '#C4B8A8',
    'highlight-primary-dialog-button-hover': '#C4623C',
    'highlight-primary-dialog-button-pressed': '#B55530',
    'highlight-header-button-hover': '#E0D7C8',
    'highlight-header-button-pressed': '#D2C8B8',
    'highlight-text-select': 'rgba(217,119,87,0.35)',
    'highlight-category-button-hover': 'rgba(217,119,87,0.08)',
    'highlight-category-button-pressed': 'rgba(217,119,87,0.16)',
    'highlight-header-tab-underline-document': '#D97757',
    'highlight-header-tab-underline-spreadsheet': '#D97757',
    'highlight-header-tab-underline-presentation': '#D97757',
    'highlight-header-tab-underline-pdf': '#D97757',
    'highlight-header-tab-underline-visio': '#D97757',
    'highlight-toolbar-tab-underline-document': '#D97757',
    'highlight-toolbar-tab-underline-spreadsheet': '#D97757',
    'highlight-toolbar-tab-underline-presentation': '#D97757',
    'highlight-toolbar-tab-underline-pdf': '#D97757',
    'highlight-toolbar-tab-underline-visio': '#D97757',
    'border-toolbar': '#E0D7C8',
    'border-toolbar-active-panel-top': '#EDE7DC',
    'border-divider': '#E0D7C8',
    'border-regular-control': '#D2C8B8',
    'border-preview-hover': '#E0B090',
    'border-preview-select': '#D97757',
    'border-control-focus': '#D97757',
    'border-button-pressed-focus': '#D97757',
    'border-fill-input-focused': '#D97757',
    'border-contrast-popover': '#E0D7C8',
    'text-normal': 'rgba(57,51,43,0.85)',
    'text-normal-pressed': 'rgba(57,51,43,0.85)',
    'text-secondary': 'rgba(57,51,43,0.6)',
    'text-tertiary': 'rgba(57,51,43,0.4)',
    'text-link': '#D97757',
    'text-link-hover': '#C4623C',
    'text-link-active': '#B55530',
    'text-link-visited': '#D97757',
    'text-toolbar-header': '#39332B',
    'text-alt-key-hint': 'rgba(57,51,43,0.85)',
    'icon-normal': '#39332B',
    'icon-normal-pressed': '#39332B',
    'icon-toolbar-header': '#39332B',
    'icon-gray-primary': '#39332B',
    'icon-blue-primary': '#D97757',
    'icon-blue-secondary': '#F4E4D8',
    'canvas-background': '#F4F0E8',
    'canvas-content-background': '#FFFFFF',
    'canvas-page-border': '#E0D7C8',
    'canvas-ruler-background': '#F4F0E8',
    'canvas-ruler-border': '#E0D7C8',
    'canvas-ruler-margins-background': '#EDE7DC',
    'canvas-ruler-mark': '#9C8E7E',
    'canvas-ruler-handle-border': '#39332B',
    'canvas-ruler-handle-border-disabled': '#C8BEB0',
    'canvas-cell-title-background': '#EDE7DC',
    'canvas-cell-title-background-hover': '#E0D7C8',
    'canvas-cell-title-background-selected': '#D2C8B8',
    'canvas-cell-title-border': '#E0D7C8',
    'canvas-cell-title-border-hover': '#D2C8B8',
    'canvas-scroll-thumb': '#E0D7C8',
    'canvas-scroll-thumb-hover': '#C8BEB0',
    'canvas-scroll-thumb-pressed': '#B8AEA0',
    'canvas-scroll-thumb-border': '#E0D7C8',
    'canvas-scroll-thumb-border-hover': '#C8BEB0',
    'canvas-scroll-thumb-border-pressed': '#B8AEA0',
    'slider-track-background-filled': '#D97757',
    'slider-thumb-background-normal': '#D97757',
    'slider-thumb-background-hover': '#C4623C',
    'slider-thumb-background-active': '#B55530',
    'chb-border-normal-focus': '#D97757',
    'chb-border-checked-focus': '#D97757',
    'rb-border-normal-focus': '#D97757',
    'rb-border-checked-focus': '#D97757',
    'shadow-fill-input': '0 0 0 1px #D97757',
    'shadow-control-focus': 'inset 0 0 0 1px #D97757,0 0 0 1px #D97757',
  },
};

let ooIdCounter = 0;

export function OfficeViewer({ projectId, filePath }: Props) {
  // React управляет только этим wrapper-div.
  // Div для OO создаём через нативный DOM — React о нём не знает
  // и не пытается делать removeChild на дочерних элементах которые OO добавил.
  const wrapperRef = useRef<HTMLDivElement>(null);
  const editorRef = useRef<{ destroyEditor: () => void } | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    const containerId = `oo-${++ooIdCounter}`;
    const wrapper = wrapperRef.current;
    if (!wrapper) return;

    const container = document.createElement('div');
    container.id = containerId;
    container.style.cssText = 'width:100%;height:100%';
    wrapper.appendChild(container);

    async function init() {
      setError(null);

      const token = typeof localStorage !== 'undefined'
        ? (localStorage.getItem('cc_token') || sessionStorage.getItem('cc_token'))
        : null;
      const headers: Record<string, string> = token ? { Authorization: `Bearer ${token}` } : {};

      let cfg: OfficeConfig;
      try {
        const res = await fetch(
          `/api/projects/${encodeURIComponent(projectId)}/files/office-config?path=${encodeURIComponent(filePath)}`,
          { headers }
        );
        if (!res.ok) throw new Error(`config ${res.status}`);
        cfg = await res.json();
      } catch (e) {
        if (!cancelled) setError('Не удалось получить конфиг документа');
        return;
      }

      if (!window.DocsAPI) {
        const apiScript = `${cfg.serverUrl}/web-apps/apps/api/documents/api.js`;
        await new Promise<void>((resolve, reject) => {
          const s = document.createElement('script');
          s.src = apiScript;
          s.onload = () => resolve();
          s.onerror = () => reject(new Error('Не удалось загрузить OnlyOffice API'));
          document.head.appendChild(s);
        }).catch(e => { if (!cancelled) setError((e as Error).message); return Promise.reject(e); });
      }

      if (cancelled || !window.DocsAPI) return;

      // Предзаполняем кеш темы до создания iframe — themeinit.js внутри OO применит наши цвета сразу
      try { localStorage.setItem('ui-theme', JSON.stringify(CLAUDE_HOME_THEME)); } catch { /* игнорируем */ }

      const ext = cfg.document.fileType;
      const docType = DOC_TYPES[ext] ?? 'word';

      editorRef.current = new window.DocsAPI.DocEditor(containerId, {
        document: cfg.document,
        editorConfig: cfg.editorConfig,
        documentType: docType,
        height: '100%',
        width: '100%',
      });

      // Polling: ждём пока OO создаст iframe и body получит класс темы, затем инжектируем наш CSS.
      // Прямая инъекция <style> надёжнее чем Themes.setTheme (у setTheme race condition при init).
      const themeInterval = setInterval(() => {
        if (cancelled) { clearInterval(themeInterval); return; }
        const iframe = document.querySelector<HTMLIFrameElement>('iframe');
        const idoc = iframe?.contentDocument;
        if (!idoc?.body || !idoc.head) return;
        // Ждём пока OO установит класс темы на body
        if (!idoc.body.classList.contains('theme-claude-home')) return;
        // Уже инжектировали — не дублируем
        if (idoc.querySelector('style[data-claude-home]')) { clearInterval(themeInterval); return; }
        clearInterval(themeInterval);
        const css = Object.entries(CLAUDE_HOME_THEME.colors).map(([k, v]) => `--${k}:${v}`).join(';');
        const style = idoc.createElement('style');
        style.setAttribute('data-claude-home', '1');
        style.textContent = `.theme-claude-home{${css}}`;
        idoc.head.appendChild(style);
      }, 300);
      setTimeout(() => clearInterval(themeInterval), 30000);
    }

    init();

    return () => {
      cancelled = true;
      // destroyEditor в try/catch — он может бросить если OO уже внутри что-то сломалось
      if (editorRef.current) {
        try { editorRef.current.destroyEditor(); } catch { /* игнорируем */ }
        editorRef.current = null;
      }
      // Убираем OO-div через тот же нативный DOM которым добавляли —
      // React ничего не знает об этом div и не конфликтует.
      if (wrapper.contains(container)) {
        wrapper.removeChild(container);
      }
    };
  }, [projectId, filePath]);

  if (error) {
    return (
      <div style={{ padding: 24, color: C.danger, fontSize: 13 }}>
        OnlyOffice недоступен: {error}
      </div>
    );
  }

  return <div ref={wrapperRef} style={{ width: '100%', height: '100%' }} />;
}
