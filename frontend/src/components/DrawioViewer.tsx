import { useEffect, useRef, useState } from 'react';
import { C } from '../lib/design';
import { useThemeMode, getEffectiveTheme } from '../lib/themeMode';

interface Props {
  // XML диаграммы (содержимое .drawio/.dio — обычный текст)
  content: string;
  // Вызывается при сохранении из редактора draw.io с актуальным XML
  onSave: (xml: string) => void | Promise<void>;
}

// Сообщения embed-протокола draw.io (proto=json). Приходят строкой JSON в event.data.
interface DrawioEvent {
  event?: string;
  xml?: string;
  exit?: boolean;
  modified?: boolean;
}

// Self-hosted draw.io (контейнер jgraph/drawio) проксируется YARP на /drawio/ — один
// origin с фронтом. Встраиваем редактор в iframe и общаемся по postMessage:
//   init  → шлём {action:'load', xml}
//   save  → onSave(xml) и {action:'status', modified:false} (снять флаг «изменён»)
// noExitBtn — прячем кнопку выхода: закрытие файла управляется тулбаром FileViewer.
export function DrawioViewer({ content, onSave }: Props) {
  // Тема фиксируется в URL при создании iframe; смена темы приложения пересобирает src.
  useThemeMode();
  const dark = getEffectiveTheme() === 'dark';
  const iframeRef = useRef<HTMLIFrameElement>(null);
  const [loading, setLoading] = useState(true);
  // Держим актуальный XML в ref — обработчик message создаётся один раз, а content меняется
  const contentRef = useRef(content);
  contentRef.current = content;

  const src = `/drawio/?embed=1&proto=json&spin=1&noExitBtn=1&noSaveBtn=0${dark ? '&dark=1&ui=dark' : ''}`;

  useEffect(() => {
    const iframe = iframeRef.current;
    if (!iframe) return;

    const post = (msg: unknown) => {
      iframe.contentWindow?.postMessage(JSON.stringify(msg), '*');
    };

    const onMessage = (e: MessageEvent) => {
      // Принимаем только сообщения от нашего iframe
      if (e.source !== iframe.contentWindow) return;
      if (typeof e.data !== 'string' || !e.data.startsWith('{')) return;

      let data: DrawioEvent;
      try { data = JSON.parse(e.data); } catch { return; }

      switch (data.event) {
        case 'init':
          // Редактор готов — грузим текущую диаграмму
          post({ action: 'load', xml: contentRef.current ?? '', autosave: 1 });
          setLoading(false);
          break;
        case 'save':
          // Пользователь нажал «Сохранить» — пишем XML на диск, снимаем флаг «изменён»
          if (typeof data.xml === 'string') {
            Promise.resolve(onSave(data.xml)).finally(() => post({ action: 'status', modified: false }));
          }
          break;
      }
    };

    window.addEventListener('message', onMessage);
    return () => window.removeEventListener('message', onMessage);
    // Пересоздаём обработчик только при смене src (темы) — content читаем через ref
  }, [src, onSave]);

  return (
    <div style={{ position: 'relative', width: '100%', height: '100%' }}>
      <iframe
        ref={iframeRef}
        key={src}
        src={src}
        style={{ width: '100%', height: '100%', border: 'none', display: 'block' }}
        title="draw.io"
      />
      {loading && (
        <div style={{ position: 'absolute', inset: 0, background: C.bgMain, display: 'flex', alignItems: 'center', justifyContent: 'center', zIndex: 10, pointerEvents: 'none' }}>
          <span style={{ width: 32, height: 32, borderRadius: '50%', border: `3px solid ${C.border}`, borderTopColor: C.accent, animation: 'spin 0.7s linear infinite' }} />
        </div>
      )}
    </div>
  );
}
