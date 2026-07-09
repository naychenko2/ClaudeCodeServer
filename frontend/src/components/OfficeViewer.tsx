import { useEffect, useRef, useState } from 'react';
import { C } from '../lib/design';
import { getEffectiveTheme } from '../lib/themeMode';

interface OfficeConfig {
  serverUrl: string;
  document: { fileType: string; key: string; title: string; url: string };
  editorConfig: { mode: string; lang: string };
}

interface Props {
  projectId: string;
  filePath: string;
  mode?: 'view' | 'edit';
  cacheKey?: string;
  onReady?: () => void;
}

declare global {
  interface Window {
    DocsAPI?: {
      DocEditor: new (
        elementId: string,
        config: {
          document: OfficeConfig['document'];
          editorConfig: OfficeConfig['editorConfig'] & { customization?: { logo?: { visible?: boolean } } };
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

// Цвета темы AI Home — пишем в localStorage перед инициализацией OO,
// чтобы themeinit.js внутри iframe применил правильный CSS ещё на старте.
// OO iframe и наш сайт на одном origin → localStorage общий.
const CLAUDE_HOME_THEME = {
  id: 'theme-claude-home',
  type: 'light',
  name: 'AI Home',
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

// Тёмный аналог темы OnlyOffice в тон тёмной теме приложения. Тулбары/панели —
// тёплые тёмные нейтрали; сам лист документа (canvas-content-background) остаётся
// белым — документ печатается на белом, инвертировать его содержимое неправильно.
const CLAUDE_HOME_THEME_DARK = {
  id: 'theme-claude-home-dark',
  type: 'dark',
  name: 'AI Home Dark',
  colors: {
    'toolbar-header-document': '#272320',
    'toolbar-header-spreadsheet': '#272320',
    'toolbar-header-presentation': '#272320',
    'toolbar-header-pdf': '#272320',
    'toolbar-header-visio': '#272320',
    'text-toolbar-header-on-background-document': '#EDE6DB',
    'text-toolbar-header-on-background-spreadsheet': '#EDE6DB',
    'text-toolbar-header-on-background-presentation': '#EDE6DB',
    'text-toolbar-header-on-background-pdf': '#EDE6DB',
    'text-toolbar-header-on-background-visio': '#EDE6DB',
    'background-normal': '#201C18',
    'background-toolbar': '#272320',
    'background-toolbar-tab': '#272320',
    'background-toolbar-additional': '#1B1815',
    'background-primary-dialog-button': '#E38A6A',
    'background-accent-button': '#E38A6A',
    'background-pane': '#272320',
    'background-contrast-popover': '#2E2A25',
    'highlight-button-hover': '#38332D',
    'highlight-button-pressed': '#454037',
    'highlight-button-pressed-hover': '#4F4940',
    'highlight-primary-dialog-button-hover': '#C4623C',
    'highlight-primary-dialog-button-pressed': '#B55530',
    'highlight-header-button-hover': '#38332D',
    'highlight-header-button-pressed': '#454037',
    'highlight-text-select': 'rgba(227,138,106,0.35)',
    'highlight-category-button-hover': 'rgba(227,138,106,0.10)',
    'highlight-category-button-pressed': 'rgba(227,138,106,0.20)',
    'highlight-header-tab-underline-document': '#E38A6A',
    'highlight-header-tab-underline-spreadsheet': '#E38A6A',
    'highlight-header-tab-underline-presentation': '#E38A6A',
    'highlight-header-tab-underline-pdf': '#E38A6A',
    'highlight-header-tab-underline-visio': '#E38A6A',
    'highlight-toolbar-tab-underline-document': '#E38A6A',
    'highlight-toolbar-tab-underline-spreadsheet': '#E38A6A',
    'highlight-toolbar-tab-underline-presentation': '#E38A6A',
    'highlight-toolbar-tab-underline-pdf': '#E38A6A',
    'highlight-toolbar-tab-underline-visio': '#E38A6A',
    'border-toolbar': '#3D3830',
    'border-toolbar-active-panel-top': '#272320',
    'border-divider': '#3D3830',
    'border-regular-control': '#454037',
    'border-preview-hover': '#9A5E48',
    'border-preview-select': '#E38A6A',
    'border-control-focus': '#E38A6A',
    'border-button-pressed-focus': '#E38A6A',
    'border-fill-input-focused': '#E38A6A',
    'border-contrast-popover': '#3D3830',
    'text-normal': 'rgba(237,230,219,0.90)',
    'text-normal-pressed': 'rgba(237,230,219,0.90)',
    'text-secondary': 'rgba(237,230,219,0.62)',
    'text-tertiary': 'rgba(237,230,219,0.42)',
    'text-link': '#E38A6A',
    'text-link-hover': '#EE9E80',
    'text-link-active': '#EE9E80',
    'text-link-visited': '#E38A6A',
    'text-toolbar-header': '#EDE6DB',
    'text-alt-key-hint': 'rgba(237,230,219,0.90)',
    'icon-normal': '#EDE6DB',
    'icon-normal-pressed': '#EDE6DB',
    'icon-toolbar-header': '#EDE6DB',
    'icon-gray-primary': '#EDE6DB',
    'icon-blue-primary': '#E38A6A',
    'icon-blue-secondary': '#4A362C',
    'canvas-background': '#201C18',
    'canvas-content-background': '#FFFFFF',
    'canvas-page-border': '#3D3830',
    'canvas-ruler-background': '#201C18',
    'canvas-ruler-border': '#3D3830',
    'canvas-ruler-margins-background': '#272320',
    'canvas-ruler-mark': '#8A8072',
    'canvas-ruler-handle-border': '#EDE6DB',
    'canvas-ruler-handle-border-disabled': '#4F4940',
    'canvas-cell-title-background': '#272320',
    'canvas-cell-title-background-hover': '#38332D',
    'canvas-cell-title-background-selected': '#454037',
    'canvas-cell-title-border': '#3D3830',
    'canvas-cell-title-border-hover': '#454037',
    'canvas-scroll-thumb': '#45403A',
    'canvas-scroll-thumb-hover': '#57514A',
    'canvas-scroll-thumb-pressed': '#635C53',
    'canvas-scroll-thumb-border': '#45403A',
    'canvas-scroll-thumb-border-hover': '#57514A',
    'canvas-scroll-thumb-border-pressed': '#635C53',
    'slider-track-background-filled': '#E38A6A',
    'slider-thumb-background-normal': '#E38A6A',
    'slider-thumb-background-hover': '#EE9E80',
    'slider-thumb-background-active': '#C4623C',
    'chb-border-normal-focus': '#E38A6A',
    'chb-border-checked-focus': '#E38A6A',
    'rb-border-normal-focus': '#E38A6A',
    'rb-border-checked-focus': '#E38A6A',
    'shadow-fill-input': '0 0 0 1px #E38A6A',
    'shadow-control-focus': 'inset 0 0 0 1px #E38A6A,0 0 0 1px #E38A6A',
  },
};

// Активная тема OO по эффективной теме приложения.
const ooTheme = () => (getEffectiveTheme() === 'dark' ? CLAUDE_HOME_THEME_DARK : CLAUDE_HOME_THEME);

let ooIdCounter = 0;

export function OfficeViewer({ projectId, filePath, mode = 'view', cacheKey, onReady }: Props) {
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
        const cacheParam = cacheKey ? `&cacheKey=${encodeURIComponent(cacheKey)}` : '';
        const res = await fetch(
          `/api/projects/${encodeURIComponent(projectId)}/files/office-config?path=${encodeURIComponent(filePath)}&mode=${mode}${cacheParam}`,
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

      // Тема OO под текущую тему приложения (свет/тьма). Фиксируем на момент
      // инициализации редактора — смена темы применится при следующем открытии.
      const theme = ooTheme();
      // Предзаполняем кеш темы до создания iframe — themeinit.js внутри OO применит наши цвета сразу
      try { localStorage.setItem('ui-theme', JSON.stringify(theme)); } catch { /* игнорируем */ }

      const ext = cfg.document.fileType;
      const docType = DOC_TYPES[ext] ?? 'word';

      let readyCalled = false;
      const callReady = () => {
        if (readyCalled || cancelled) return;
        readyCalled = true;
        onReady?.();
      };

      editorRef.current = new window.DocsAPI.DocEditor(containerId, {
        document: cfg.document,
        editorConfig: { ...cfg.editorConfig, customization: { ...(cfg.editorConfig as any).customization, logo: { visible: false } } },
        documentType: docType,
        height: '100%',
        width: '100%',
        events: {
          // onDocumentReady — основной триггер (работает в любом режиме, не зависит от origin)
          onDocumentReady: callReady,
        },
      });

      // Polling: пробуем инжектировать CSS темы (работает только на том же origin).
      // Если cross-origin (dev-режим), просто тихо выходим. onReady уже обработан через события выше.
      const themeInterval = setInterval(() => {
        if (cancelled) { clearInterval(themeInterval); return; }
        const iframe = document.querySelector<HTMLIFrameElement>('iframe');
        let idoc: Document | null = null;
        try { idoc = iframe?.contentDocument ?? null; } catch { clearInterval(themeInterval); return; }
        if (!idoc?.body || !idoc.head) return;
        // Уже инжектировали — не дублируем
        if (idoc.querySelector('style[data-claude-home]')) { clearInterval(themeInterval); return; }
        clearInterval(themeInterval);
        const css = Object.entries(theme.colors).map(([k, v]) => `--${k}:${v}`).join(';');
        const style = idoc.createElement('style');
        style.setAttribute('data-claude-home', '1');
        // OO вешает на body класс по id темы (.theme-claude-home / .theme-claude-home-dark)
        style.textContent = `.${theme.id}{${css}} #header-logo{display:none!important}`;
        idoc.head.appendChild(style);
      }, 300);
      setTimeout(() => { clearInterval(themeInterval); callReady(); }, 30000);
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
