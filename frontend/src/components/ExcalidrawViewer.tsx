import { forwardRef, useEffect, useImperativeHandle, useRef, useState } from 'react';
import { AlertTriangle } from 'lucide-react';
import { C, FONT } from '../lib/design';
import { ICON_SIZE, ICON_STROKE } from './ui/icons';
import { useThemeMode, getEffectiveTheme } from '../lib/themeMode';

// Пакет не реэкспортирует ExcalidrawImperativeAPI из корня — тянем через wildcard-экспорт «./*»
type ExcalidrawModule = typeof import('@excalidraw/excalidraw');
type ExcalidrawAPI = import('@excalidraw/excalidraw/types').ExcalidrawImperativeAPI;

// Код языка интерфейса редактора: пакет использует коды с регионом (ru-RU, ar-SA),
// невалидный код молча падает в английский
const LANG = 'ru-RU';

interface Props {
  // JSON-сцена Excalidraw (содержимое .excalidraw — обычный текст)
  content: string;
  // Режим: просмотр (view-mode, панель инструментов скрыта) или редактирование
  mode: 'view' | 'edit';
  // Вызывается при сохранении с актуальным JSON сцены (из flush())
  onSave: (json: string) => void | Promise<void>;
}

// Императивный хендл: FileViewer вызывает flush() перед выходом из edit/закрытием —
// снимает текущую сцену и сохраняет, чтобы правки не потерялись.
export interface ExcalidrawHandle {
  flush: () => Promise<void>;
}

// Чистая валидация входа: корректный JSON с массивом elements → сцена; пустой файл →
// стартовая пустая сцена; всё остальное (битый JSON / не объект / нет elements) → null,
// вместо падения редактора показываем empty-state. Вынесена из компонента — под юнит-тестом.
export function parseExcalidrawScene(content: string): { elements: unknown[] } | null {
  const trimmed = (content ?? '').trim();
  if (!trimmed) return { elements: [] }; // пустой файл — чистый лист
  try {
    const parsed = JSON.parse(trimmed) as unknown;
    if (typeof parsed !== 'object' || parsed === null || Array.isArray(parsed)) return null;
    const elements = (parsed as { elements?: unknown }).elements;
    if (!Array.isArray(elements)) return null;
    return { elements };
  } catch {
    return null;
  }
}

// Excalidraw-редактор: локальный React-компонент (без iframe и внешнего сервиса, в отличие
// от draw.io). Библиотека тяжёлая — грузится лениво, только когда реально открыт
// .excalidraw-файл (прецедент — lazy mermaid в MermaidDiagram). Пока грузится — спиннер.
// Тема компонента синхронизирована с темой приложения (смена = ремоунт через key).
export const ExcalidrawViewer = forwardRef<ExcalidrawHandle, Props>(function ExcalidrawViewer({ content, mode, onSave }, ref) {
  useThemeMode();
  const dark = getEffectiveTheme() === 'dark';
  const scene = parseExcalidrawScene(content);

  const [Editor, setEditor] = useState<ExcalidrawModule | null>(null);
  const [loadFailed, setLoadFailed] = useState(false);
  useEffect(() => {
    let cancelled = false;
    // index.css пакета нужен самому редактору (стили холста/тулбара) — тянем вместе с кодом
    void Promise.all([
      import('@excalidraw/excalidraw'),
      import('@excalidraw/excalidraw/index.css'),
    ])
      .then(([mod]) => { if (!cancelled) setEditor(mod); })
      .catch(() => { if (!cancelled) setLoadFailed(true); });
    return () => { cancelled = true; };
  }, []);

  // Актуальные значения в ref — flush() всегда сохраняет свежую сцену
  const apiRef = useRef<ExcalidrawAPI | null>(null);
  const modeRef = useRef(mode);
  modeRef.current = mode;
  const onSaveRef = useRef(onSave);
  onSaveRef.current = onSave;

  useImperativeHandle(ref, () => ({
    flush: async () => {
      const api = apiRef.current;
      if (modeRef.current !== 'edit' || !api) return;
      const mod = await import('@excalidraw/excalidraw');
      const json = mod.serializeAsJSON(api.getSceneElements(), api.getAppState(), api.getFiles(), 'local');
      await onSaveRef.current(json);
    },
  }), []);

  // Битый файл — честный empty-state вместо белого экрана
  if (scene === null) {
    return (
      <div style={{ display: 'flex', flexDirection: 'column', alignItems: 'center', justifyContent: 'center', height: '100%', gap: 8, padding: 16, textAlign: 'center' }}>
        <AlertTriangle size={ICON_SIZE.xl} strokeWidth={ICON_STROKE} color={C.textMuted} />
        <div style={{ fontSize: 14, color: C.textPrimary, fontFamily: FONT.sans }}>Не похоже на файл Excalidraw</div>
        <div style={{ fontSize: 12.5, color: C.textMuted, fontFamily: FONT.sans }}>Файл повреждён или создан другой программой. Можно исправить как код и вернуться к просмотру.</div>
      </div>
    );
  }

  if (loadFailed) {
    return (
      <div style={{ display: 'flex', flexDirection: 'column', alignItems: 'center', justifyContent: 'center', height: '100%', gap: 8, padding: 16, textAlign: 'center' }}>
        <AlertTriangle size={ICON_SIZE.xl} strokeWidth={ICON_STROKE} color={C.textMuted} />
        <div style={{ fontSize: 14, color: C.textPrimary, fontFamily: FONT.sans }}>Не удалось загрузить редактор</div>
      </div>
    );
  }

  if (!Editor) {
    return (
      <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'center', height: '100%', gap: 10, color: C.textMuted, fontSize: 13, fontFamily: FONT.sans }}>
        <span style={{ width: 20, height: 20, borderRadius: '50%', border: `2.5px solid ${C.border}`, borderTopColor: C.accent, animation: 'spin 0.7s linear infinite' }} />
        Загрузка редактора…
      </div>
    );
  }

  const { Excalidraw } = Editor;
  return (
    <div style={{ width: '100%', height: '100%' }}>
      <Excalidraw
        // Тема — пропом: библиотека перекрашивается сама, БЕЗ ремоунта (key тут
        // сбрасывал сцену и терял несохранённые правки при переключении темы).
        excalidrawAPI={(api: ExcalidrawAPI) => { apiRef.current = api; }}
        initialData={{ elements: scene.elements as never[] }}
        viewModeEnabled={mode === 'view'}
        theme={dark ? 'dark' : 'light'}
        langCode={LANG}
        UIOptions={{
          canvasActions: {
            loadScene: false,      // свои файлы грузим только через CCS (права/пути)
            saveAsImage: false,    // экспорт картинки не проходит через файловое API
            export: false,
            saveToActiveFile: false,
          },
        }}
      />
    </div>
  );
});
