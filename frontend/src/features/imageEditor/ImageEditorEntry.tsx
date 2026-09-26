// Вход в редактор (экран 1 макета): «Редактировать» у картинки и «Нарисовать картинку»
// у папки. Без флага image-editor входа нет вовсе — кнопка не рендерится.
// Редактор открывается слоем поверх раскладки проекта. Слой живёт в хосте уровня
// приложения, а не у кнопки: уход с экрана проекта размонтирует и дерево файлов, и
// просмотр файла — без хоста спросить про несохранённые варианты было бы некому.

import { useEffect, useLayoutEffect, useRef, useState, useSyncExternalStore, type ComponentType } from 'react';
import { createPortal } from 'react-dom';
import { Pencil, Sparkles } from 'lucide-react';
import { Button, ConfirmDialog, ICON_SIZE, ICON_STROKE, ISLAND, Z, useIsMobile, FLAGS, useFeature, NAV_CHANGE_EVENT, parseHash } from 'aihome_shell/kit';
import { ImageEditor, type ImageEditorTarget } from './ImageEditor';
import { isEditableImage } from './format';
import type { ImageChatSlotProps } from '../../lib/subsystems/registryCore';

interface EntryProps {
  projectId: string;
  projectName: string;
  target: ImageEditorTarget;
  // Чат картинки, с которым открыть редактор (карточка чата в списке)
  sessionId?: string | null;
  // Из ленты полного чата: промпт карточки «✦ Промпт» и задача карточки запуска
  initialPrompt?: string;
  showJob?: { jobId: string; count: number };
  onShowInFiles?: (path: string) => void;
  size?: 'xs' | 'sm';
}

export function ImageEditorEntryButton({ projectId, projectName, target, onShowInFiles, size = 'sm' }: EntryProps) {
  const enabled = useFeature(FLAGS.imageEditor);
  if (!enabled) return null;
  if (target.kind === 'edit' && !isEditableImage(target.path)) return null;
  const Icon = target.kind === 'edit' ? Pencil : Sparkles;
  return (
    <Button size={size} variant={target.kind === 'edit' ? 'secondary' : 'ghost'}
      leftIcon={<Icon size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />}
      onClick={e => { e.stopPropagation(); openImageEditor({ projectId, projectName, target, onShowInFiles }); }}>
      {target.kind === 'edit' ? 'Редактировать' : 'Нарисовать картинку'}
    </Button>
  );
}

function sameScreen(a: string, b: string): boolean {
  const x = parseHash(a), y = parseHash(b);
  return !!x && !!y && x.screen === y.screen && x.projectId === y.projectId;
}

// ── Хост слоя: один на приложение ──
type EditorRequest = Omit<EntryProps, 'size'> & { seq: number };
let request: EditorRequest | null = null;
let seq = 0;
const listeners = new Set<() => void>();
const emit = () => listeners.forEach(fn => fn());

export function openImageEditor(req: Omit<EntryProps, 'size'>): void {
  request = { ...req, seq: ++seq };
  emit();
}

function closeImageEditor(): void {
  request = null;
  emit();
}

// Монтируется в App под авторизацией; размонтирование (выход) закрывает редактор.
// ImageChat — чат картинки ядра из контекста слота app-overlay
export function ImageEditorHost({ ImageChat }: { ImageChat: ComponentType<ImageChatSlotProps> }) {
  const req = useSyncExternalStore(
    fn => { listeners.add(fn); return () => { listeners.delete(fn); }; },
    () => request,
  );
  useEffect(() => closeImageEditor, []);
  if (!req) return null;
  const { seq: key, ...props } = req;
  return <ImageEditorLayer key={key} {...props} ImageChat={ImageChat} onClose={closeImageEditor} />;
}

// Слой на весь экран: редактор на месте рабочей области проекта. Портал в body —
// иначе position: fixed внутри трансформированной панели файлов сжимается до её размеров.
// Уход с экрана (смена адреса) закрывает слой; несохранённые варианты — через подтверждение,
// а «Остаться» возвращает прежний адрес.
function ImageEditorLayer({ onClose, ImageChat, ...props }: Omit<EntryProps, 'size'> & { onClose: () => void; ImageChat: ComponentType<ImageChatSlotProps> }) {
  const enabled = useFeature(FLAGS.imageEditor);
  const mobile = useIsMobile();
  const dirty = useRef(false);
  const [leaveAsk, setLeaveAsk] = useState(false);
  const closeRef = useRef(onClose);
  useLayoutEffect(() => { closeRef.current = onClose; });

  const home = useRef(window.location.hash);
  // После «Остаться» приложение может уточнить восстановленный адрес — это не уход
  const restoringUntil = useRef(0);

  useEffect(() => {
    const onNav = () => {
      const hash = window.location.hash;
      if (hash === home.current) return;
      if (Date.now() < restoringUntil.current && sameScreen(hash, home.current)) { home.current = hash; return; }
      if (dirty.current) setLeaveAsk(true);
      else closeRef.current();
    };
    window.addEventListener(NAV_CHANGE_EVENT, onNav);
    window.addEventListener('popstate', onNav);
    window.addEventListener('hashchange', onNav);
    return () => {
      window.removeEventListener(NAV_CHANGE_EVENT, onNav);
      window.removeEventListener('popstate', onNav);
      window.removeEventListener('hashchange', onNav);
    };
  }, []);

  if (!enabled) return null;
  return createPortal(
    <div style={{
      position: 'fixed', inset: 0, zIndex: Z.overlay, background: ISLAND.canvas,
      padding: mobile ? 0 : ISLAND.pad, display: 'flex', flexDirection: 'column',
    }}>
      <ImageEditor {...props} ImageChat={ImageChat} onClose={onClose} onDirtyChange={d => { dirty.current = d; }}
        onOpenPath={path => openImageEditor({ ...props, sessionId: null, initialPrompt: undefined, showJob: undefined, target: { kind: 'edit', path } })}
        onShowInFiles={props.onShowInFiles ? path => { onClose(); props.onShowInFiles?.(path); } : undefined} />
      {leaveAsk && (
        <ConfirmDialog title="Закрыть редактор?" subtitle="Несохранённые варианты пропадут."
          confirmLabel="Закрыть" confirmVariant="danger" cancelLabel="Остаться"
          onConfirm={onClose}
          onCancel={() => {
            setLeaveAsk(false);
            restoringUntil.current = Date.now() + 1500;
            if (window.location.hash !== home.current) window.location.hash = home.current;
          }} />
      )}
    </div>,
    document.body,
  );
}
