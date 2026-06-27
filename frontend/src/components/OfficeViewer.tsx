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

      const ext = cfg.document.fileType;
      const docType = DOC_TYPES[ext] ?? 'word';

      editorRef.current = new window.DocsAPI.DocEditor(containerId, {
        document: cfg.document,
        editorConfig: cfg.editorConfig,
        documentType: docType,
        height: '100%',
        width: '100%',
      });
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
