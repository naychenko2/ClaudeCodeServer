import { forwardRef, useEffect, useImperativeHandle, useRef, useState } from 'react';
import { C } from '../lib/design';
import { useThemeMode, getEffectiveTheme } from '../lib/themeMode';

interface Props {
  // XML диаграммы (содержимое .drawio/.dio — обычный текст)
  content: string;
  // Режим: просмотр (read-only) или редактирование
  mode: 'view' | 'edit';
  // Вызывается при сохранении из редактора draw.io с актуальным XML (только в edit)
  onSave: (xml: string) => void | Promise<void>;
}

// Императивный хендл: FileViewer вызывает flush() перед выходом из edit/закрытием —
// запрашивает у draw.io актуальный XML и сохраняет его, чтобы правки не потерялись.
export interface DrawioHandle {
  flush: () => Promise<void>;
}

// Сообщения embed-протокола draw.io (proto=json). Приходят строкой JSON в event.data.
interface DrawioEvent {
  event?: string;
  xml?: string;
  exit?: boolean;
  modified?: boolean;
}

// CSS-брендинг UI draw.io под тему проекта. Отдаётся редактору через configure-протокол.
// Перекрашиваем тулбар/меню/панели, primary-кнопки (Save), подложку холста в цвета проекта.
// Сам лист диаграммы не трогаем.
function themeCss(dark: boolean): string {
  const accent = dark ? '#E38A6A' : '#D97757';
  const accentHover = dark ? '#EE9E80' : '#C4623C';
  const bar = dark ? '#272320' : '#EDE7DA';
  const barText = dark ? '#EDE6DB' : '#39332B';
  const border = dark ? '#3D3830' : '#E0D7C8';
  const canvas = dark ? '#201C18' : '#F4F0E8';
  const onAccent = '#FFFFFF';
  return `
    .geMenubarContainer, .geToolbarContainer, .geFormatContainer, .geSidebarContainer,
    .geMenubar, .geToolbar, .mxWindowTitle {
      background-color: ${bar} !important;
      color: ${barText} !important;
      border-color: ${border} !important;
    }
    /* Подложка холста (не сам лист) — тёплый фон в тон приложению */
    .geEditor { background-color: ${canvas} !important; }
    .geMenubar a, .geToolbar a, .geTitle { color: ${barText} !important; }
    .geBtn.gePrimaryBtn, button.gePrimaryBtn, .geBigStandardButtons .gePrimaryBtn {
      background: ${accent} !important;
      border-color: ${accent} !important;
      color: ${onAccent} !important;
    }
    .geBtn.gePrimaryBtn:hover, button.gePrimaryBtn:hover {
      background: ${accentHover} !important;
      border-color: ${accentHover} !important;
    }
    /* Активные вкладки формат-панели и активная страница — акцент */
    .geFormatSection .geFormatTab.geActiveTab, .geTabActive, .mxPopupMenuItemHover {
      border-color: ${accent} !important;
    }
    a.geItem.geActivePage, .geTabContainer .geActiveTab {
      border-bottom-color: ${accent} !important;
      color: ${accent} !important;
    }
    .mxPopupMenuItem:hover, .geMenuItem:hover { background-color: ${accent}22 !important; }
    input:focus, textarea:focus, select:focus { border-color: ${accent} !important; }
  `;
}

// Self-hosted draw.io (контейнер jgraph/drawio) проксируется YARP на /drawio/ — один
// origin с фронтом. Встраиваем в iframe и общаемся по postMessage:
//   configure → шлём {action:'configure', config:{css}} — тема проекта
//   init      → шлём {action:'load', xml}
//   save/autosave (edit) → onSave(xml)
//   export    → ответ на flush(): отдаём XML на сохранение (гарантия при выходе из edit)
// view: chrome=0 — read-only просмотр; edit: полный редактор + noExitBtn (закрытие — в FileViewer).
export const DrawioViewer = forwardRef<DrawioHandle, Props>(function DrawioViewer({ content, mode, onSave }, ref) {
  // Тема фиксируется в URL/конфиге при создании iframe; смена темы приложения пересобирает src.
  useThemeMode();
  const dark = getEffectiveTheme() === 'dark';
  const iframeRef = useRef<HTMLIFrameElement>(null);
  const [loading, setLoading] = useState(true);
  // Держим актуальные значения в ref — обработчик message создаётся один раз
  const contentRef = useRef(content);
  contentRef.current = content;
  const onSaveRef = useRef(onSave);
  onSaveRef.current = onSave;
  const modeRef = useRef(mode);
  modeRef.current = mode;
  const saveTimer = useRef<number | null>(null);
  // Резолвер незавершённого flush() — вызывается, когда пришёл export и XML сохранён
  const pendingFlush = useRef<(() => void) | null>(null);

  const post = (msg: unknown) => iframeRef.current?.contentWindow?.postMessage(JSON.stringify(msg), '*');

  const params = mode === 'view'
    ? `embed=1&proto=json&configure=1&chrome=0`
    : `embed=1&proto=json&configure=1&spin=1&noExitBtn=1&noSaveBtn=0`;
  const src = `/drawio/?${params}${dark ? '&dark=1&ui=dark' : ''}`;

  // flush: запрашиваем текущий XML у draw.io и сохраняем. Резолвится после записи
  // (или по таймауту — не блокируем UI, если редактор молчит).
  useImperativeHandle(ref, () => ({
    flush: () => new Promise<void>((resolve) => {
      if (modeRef.current !== 'edit' || !iframeRef.current?.contentWindow) { resolve(); return; }
      pendingFlush.current = resolve;
      post({ action: 'export', format: 'xml' });
      window.setTimeout(() => {
        if (pendingFlush.current) { pendingFlush.current = null; resolve(); }
      }, 3000);
    }),
  }), []);

  useEffect(() => {
    const iframe = iframeRef.current;
    if (!iframe) return;

    const postMsg = (msg: unknown) => iframe.contentWindow?.postMessage(JSON.stringify(msg), '*');

    // Инкрементальное сохранение автосейва с дебаунсом — фоновая подстраховка
    const flushSave = (xml: string) => {
      if (saveTimer.current) window.clearTimeout(saveTimer.current);
      saveTimer.current = window.setTimeout(() => { void onSaveRef.current(xml); }, 700);
    };

    const resolvePending = () => {
      const r = pendingFlush.current;
      pendingFlush.current = null;
      r?.();
    };

    const onMessage = (e: MessageEvent) => {
      if (e.source !== iframe.contentWindow) return;
      if (typeof e.data !== 'string' || !e.data.startsWith('{')) return;

      let data: DrawioEvent;
      try { data = JSON.parse(e.data); } catch { return; }

      switch (data.event) {
        case 'configure':
          postMsg({ action: 'configure', config: { css: themeCss(dark) } });
          break;
        case 'init':
          postMsg({ action: 'load', xml: contentRef.current ?? '', autosave: modeRef.current === 'edit' ? 1 : 0 });
          setLoading(false);
          break;
        case 'save':
          if (typeof data.xml === 'string') {
            if (saveTimer.current) window.clearTimeout(saveTimer.current);
            Promise.resolve(onSaveRef.current(data.xml)).finally(() => postMsg({ action: 'status', modified: false }));
          }
          break;
        case 'autosave':
          if (typeof data.xml === 'string') flushSave(data.xml);
          break;
        case 'export':
          // Ответ на flush() — сохраняем актуальный XML и резолвим ожидание
          if (typeof data.xml === 'string') {
            Promise.resolve(onSaveRef.current(data.xml)).finally(resolvePending);
          } else {
            resolvePending();
          }
          break;
      }
    };

    window.addEventListener('message', onMessage);
    return () => {
      window.removeEventListener('message', onMessage);
      if (saveTimer.current) window.clearTimeout(saveTimer.current);
    };
  }, [src, dark]);

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
});
