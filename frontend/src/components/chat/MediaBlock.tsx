import { useState, useRef, useContext } from 'react';
import { X } from 'lucide-react';
import { getExplorerCreateInDir } from '../FileExplorer';
import { api } from '../../lib/api';
import { C, FONT, SHADOW } from '../../lib/design';
import { getEffectiveTheme } from '../../lib/themeMode';
import { Modal, ModalActions } from '../ui';
import { proxyUrl } from './MarkdownContent';
import { ChatProjectContext } from './contexts';

export function mediaLabel(items: MediaItem[]): string {
  const imgCount = items.filter(m => m.kind === 'image').length;
  const vidCount = items.filter(m => m.kind === 'video').length;
  const fmt = (n: number, one: string, few: string, many: string) =>
    n === 1 ? `1 ${one}` : n < 5 ? `${n} ${few}` : `${n} ${many}`;
  const parts = [];
  if (vidCount > 0) parts.push(fmt(vidCount, 'видео', 'видео', 'видео'));
  if (imgCount > 0) parts.push(fmt(imgCount, 'изображение', 'изображения', 'изображений'));
  return parts.join(' + ');
}


export type MediaItem =
  | { kind: 'image'; url: string; width?: number; height?: number; fileName?: string }
  | { kind: 'video'; url: string; width?: number; height?: number; duration?: number; fileName?: string }
  | { kind: 'audio'; url: string; duration?: number; fileName?: string };

function classifyUrl(item: any): 'image' | 'video' | 'audio' | null {
  if (typeof item?.url !== 'string') return null;
  const ct: string = item.content_type ?? '';
  if (ct.startsWith('video/') || /\.(mp4|webm|mov|avi|mkv)(\?|$)/i.test(item.url)) return 'video';
  if (ct.startsWith('audio/') || /\.(mp3|wav|ogg|flac|aac|m4a|opus|weba)(\?|$)/i.test(item.url)) return 'audio';
  if (ct.startsWith('image/') || /\.(png|jpg|jpeg|gif|webp|svg|avif)(\?|$)/i.test(item.url)) return 'image';
  // fal.media без content_type — по умолчанию изображение (совместимость)
  if (item.url.includes('fal.media') || item.url.includes('fal.run')) return 'image';
  return null;
}

// Извлекает изображения и видео из JSON-результата MCP-инструмента (fal-ai и аналогичные)
export function extractMediaFromResult(result: string): MediaItem[] {
  try {
    const parsed = JSON.parse(result);
    const items: MediaItem[] = [];

    // Массив images/videos/audio в разных местах ответа
    for (const root of [parsed, parsed?.result, parsed?.data, parsed?.output]) {
      if (!root) continue;
      for (const arr of [root.images, root.videos, root.audio_files, root.audios]) {
        if (Array.isArray(arr)) {
          for (const item of arr) {
            const kind = classifyUrl(item);
            if (kind === 'audio') items.push({ kind: 'audio', url: item.url, duration: item.duration, fileName: item.file_name });
            else if (kind) items.push({ kind, url: item.url, width: item.width, height: item.height, fileName: item.file_name, ...(kind === 'video' ? { duration: item.duration } : {}) } as MediaItem);
          }
        }
      }
      // Одиночный объект video
      if (root.video && typeof root.video?.url === 'string') {
        const v = root.video;
        items.push({ kind: 'video', url: v.url, width: v.width, height: v.height, duration: v.duration, fileName: v.file_name });
      }
      // Одиночный объект audio / audio_file
      for (const key of ['audio', 'audio_file']) {
        const a = root[key];
        if (a && typeof a?.url === 'string') {
          items.push({ kind: 'audio', url: a.url, duration: a.duration, fileName: a.file_name });
        }
      }
    }

    return items;
  } catch {
    return [];
  }
}

// Извлекает метаданные генерации (модель, время) из JSON-результата MCP-инструмента.
// Стоимость берётся отдельно — точная, с backend (см. FalCostContext).
export function extractMediaMeta(result: string): { model?: string; inferenceTime?: number } {
  try {
    const parsed = JSON.parse(result);
    // Имя модели: endpoint_id → берём только короткое имя после последнего / (в результате fal обычно отсутствует)
    const endpointId: string | undefined = parsed?.endpoint_id;
    const model = endpointId ? endpointId.split('/').pop() : undefined;
    // Время генерации: ищем в нескольких местах
    const r = parsed?.result ?? parsed;
    const inferenceTime: number | undefined =
      r?.timings?.inference ??
      r?.metrics?.inference_time ??
      parsed?.timings?.inference ??
      parsed?.metrics?.inference_time ??
      undefined;
    return {
      model: model || undefined,
      inferenceTime: inferenceTime ? Number(inferenceTime) : undefined,
    };
  } catch {
    return {};
  }
}

// Один медиа-блок (изображение или видео).
// Футер: метаданные (размер, модель, время, цена) + кнопки «Скачать» и «В проект».
// Тач-устройства: тап по изображению открывает лайтбокс с навигацией назад.
export function MediaBlock({
  m,
  filename,
  model,
  inferenceTime,
  costUsd,
  costPending,
  online = true,
}: {
  m: MediaItem;
  filename: string;
  model?: string;
  inferenceTime?: number;
  costUsd?: number;
  costPending?: boolean;
  online?: boolean;
}) {
  const project = useContext(ChatProjectContext);
  const [lightbox, setLightbox] = useState(false);
  const [saveState, setSaveState] = useState<'idle' | 'saving' | 'saved' | 'error'>('idle');
  const [saveDialog, setSaveDialog] = useState<{ baseName: string; ext: string } | null>(null);
  const [dlHov, setDlHov] = useState(false);
  const [saveHov, setSaveHov] = useState(false);

  // Определяем тач-устройство один раз при монтировании
  const isTouch = useRef(
    typeof window !== 'undefined' &&
    ('ontouchstart' in window || window.matchMedia('(pointer: coarse)').matches)
  );

  const handleImageClick = (e: React.MouseEvent<HTMLAnchorElement>) => {
    if (isTouch.current) {
      e.preventDefault();
      setLightbox(true);
    }
  };

  const doSave = async (customName: string) => {
    if (!project) return;
    const dir = getExplorerCreateInDir(project.id);
    const path = dir ? `${dir}/${customName}` : customName;
    setSaveState('saving');
    try {
      await api.files.saveFromUrl(project.id, m.url, path);
      setSaveState('saved');
      setTimeout(() => setSaveState('idle'), 3000);
    } catch {
      setSaveState('error');
      setTimeout(() => setSaveState('idle'), 3000);
    }
  };

  const openSaveDialog = (e: React.MouseEvent) => {
    e.stopPropagation();
    if (!project || saveState === 'saving') return;
    const dotIdx = filename.lastIndexOf('.');
    const baseName = dotIdx > 0 ? filename.slice(0, dotIdx) : filename;
    const ext = dotIdx > 0 ? filename.slice(dotIdx) : '';
    setSaveDialog({ baseName, ext });
  };

  // Строка метаданных
  const metaParts: string[] = [];
  if (m.kind !== 'audio' && m.width && m.height) metaParts.push(`${m.width}×${m.height}`);
  if ((m.kind === 'video' || m.kind === 'audio') && m.duration) metaParts.push(`${m.duration.toFixed(1)}с`);
  if (inferenceTime) metaParts.push(`${inferenceTime.toFixed(1)}с`);
  if (model) metaParts.push(model);
  // Точная стоимость с backend (billing-events). Пока не пришла — «считается…».
  if (costUsd) metaParts.push(costUsd < 0.01 ? `$${costUsd.toFixed(4)}` : `$${costUsd.toFixed(2)}`);
  else if (costPending) metaParts.push('считается…');

  const btnBase: React.CSSProperties = {
    display: 'inline-flex', alignItems: 'center', gap: 4,
    padding: '4px 10px', borderRadius: 6,
    fontSize: 11, fontFamily: FONT.sans, fontWeight: 500,
    lineHeight: 1, cursor: 'pointer', textDecoration: 'none',
    border: `1px solid ${C.border}`,
    boxShadow: SHADOW.card,
    transition: 'background 0.15s, color 0.15s, border-color 0.15s',
  };

  const saveBtnLabel =
    saveState === 'saved' ? '✓ Сохранено'
    : saveState === 'error' ? '✗ Ошибка'
    : 'Добавить в проект';

  const renderButtons = (dark = false) => (
    <div style={{ display: 'flex', gap: 6, flexShrink: 0 }}>
      <a
        href={online ? proxyUrl(m.url) : undefined}
        download={online ? filename : undefined}
        onClick={e => { if (!online) { e.preventDefault(); return; } e.stopPropagation(); }}
        onMouseEnter={() => { if (online) setDlHov(true); }}
        onMouseLeave={() => setDlHov(false)}
        style={dark
          ? { ...btnBase, background: 'rgba(255,255,255,0.15)', color: '#fff', borderColor: 'rgba(255,255,255,0.25)', opacity: online ? 1 : 0.4, cursor: online ? 'pointer' : 'not-allowed' }
          : { ...btnBase, background: online && dlHov ? C.accent : 'rgba(237,231,218,0.92)', color: online && dlHov ? '#fff' : C.textPrimary, borderColor: online && dlHov ? C.accent : C.border, opacity: online ? 1 : 0.4, cursor: online ? 'pointer' : 'not-allowed' }
        }
      >
        ↓ Скачать
      </a>
      {project && (
        <button
          onClick={openSaveDialog}
          disabled={!online || saveState === 'saving'}
          onMouseEnter={() => { if (online) setSaveHov(true); }}
          onMouseLeave={() => setSaveHov(false)}
          style={dark
            ? { ...btnBase, background: saveState === 'saved' ? '#4CAF50' : saveState === 'error' ? '#e05252' : 'rgba(255,255,255,0.15)', color: '#fff', borderColor: 'rgba(255,255,255,0.25)', opacity: (!online || saveState === 'saving') ? 0.4 : 1, cursor: online ? 'pointer' : 'not-allowed' }
            : { ...btnBase, background: saveState === 'saved' ? '#4CAF50' : saveState === 'error' ? '#e05252' : (online && saveHov ? C.accent : 'rgba(237,231,218,0.92)'), color: (saveState === 'saved' || saveState === 'error' || (online && saveHov)) ? '#fff' : C.textPrimary, borderColor: saveState === 'saved' ? '#4CAF50' : saveState === 'error' ? '#e05252' : (online && saveHov ? C.accent : C.border), opacity: (!online || saveState === 'saving') ? 0.4 : 1, cursor: online ? 'pointer' : 'not-allowed' }
          }
        >
          {saveState === 'saving'
            ? <><div className="tool-spinner" style={{ width: 10, height: 10, borderWidth: '1.5px' }} /><span style={{ marginLeft: 3 }}>Копируется…</span></>
            : saveBtnLabel}
        </button>
      )}
    </div>
  );

  return (
    <div>
      {m.kind === 'audio' ? (
        /* Аудиоплеер — карточка в стиле дизайн-системы */
        <div style={{
          background: C.bgPanel, borderRadius: 10, border: `1px solid ${C.border}`,
          padding: '10px 12px', display: 'flex', flexDirection: 'column', gap: 8,
          minWidth: 260, maxWidth: 400,
        }}>
          {/* Шапка: иконка + имя файла */}
          <div style={{ display: 'flex', alignItems: 'center', gap: 7 }}>
            <span style={{ fontSize: 16, lineHeight: 1, flexShrink: 0 }}>🎵</span>
            <span style={{
              fontFamily: FONT.mono, fontSize: 12, color: C.textPrimary,
              overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', flex: 1,
            }}>{filename}</span>
          </div>
          {/* Нативный плеер — обёртка с overflow:hidden обрезает углы shadow DOM */}
          <div style={{ borderRadius: 6, overflow: 'hidden' }}>
            <audio controls style={{ width: '100%', height: 36, outline: 'none', display: 'block' }}>
              <source src={proxyUrl(m.url)} />
            </audio>
          </div>
          {/* Метаданные + кнопки */}
          <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
            <span style={{ flex: 1, fontSize: 10, color: C.textMuted, fontFamily: FONT.mono, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
              {metaParts.join(' · ')}
            </span>
            {renderButtons(getEffectiveTheme() === 'dark')}
          </div>
        </div>
      ) : (
        <>
          <div style={{ display: 'inline-block', maxWidth: '100%' }}>
            {m.kind === 'image' ? (
              <a href={proxyUrl(m.url)} target="_blank" rel="noopener noreferrer"
                 style={{ display: 'block' }} onClick={handleImageClick}>
                <img src={proxyUrl(m.url)} alt="" loading="lazy"
                  style={{ maxWidth: '100%', height: 'auto', display: 'block',
                    borderRadius: 8, border: `1px solid ${C.border}`, cursor: 'pointer' }} />
              </a>
            ) : (
              <video controls style={{ maxWidth: '100%', height: 'auto', display: 'block',
                borderRadius: 8, border: `1px solid ${C.border}` }}>
                <source src={proxyUrl(m.url)} />
              </video>
            )}
          </div>

          {/* Футер: метаданные слева (flex:1, обрезается), кнопки прижаты вправо */}
          <div style={{ display: 'flex', alignItems: 'center', gap: 8, marginTop: 5 }}>
            <span style={{ flex: 1, fontSize: 10, color: C.textMuted, fontFamily: FONT.mono, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
              {metaParts.join(' · ')}
            </span>
            {renderButtons(getEffectiveTheme() === 'dark')}
          </div>
        </>
      )}

      {/* Лайтбокс — только тач/мобайл, pop-up с кнопкой закрытия */}
      {lightbox && (
        <div
          onClick={() => setLightbox(false)}
          style={{
            position: 'fixed', inset: 0, zIndex: 9999,
            background: 'rgba(0,0,0,0.92)',
            display: 'flex', flexDirection: 'column',
            alignItems: 'center', justifyContent: 'center', padding: 16,
          }}
        >
          <button
            onClick={e => { e.stopPropagation(); setLightbox(false); }}
            style={{
              position: 'absolute', top: 16, right: 16,
              background: 'rgba(255,255,255,0.15)',
              border: '1px solid rgba(255,255,255,0.3)',
              borderRadius: 10, color: '#fff', fontSize: 18,
              width: 44, height: 44, cursor: 'pointer',
              display: 'flex', alignItems: 'center', justifyContent: 'center',
              lineHeight: 1, fontWeight: 300,
            }}
          >
            <X size={20} strokeWidth={2} />
          </button>
          <img
            src={proxyUrl(m.url)}
            alt=""
            onClick={e => e.stopPropagation()}
            style={{ maxWidth: '92vw', maxHeight: '76vh', objectFit: 'contain',
                     borderRadius: 8, display: 'block' }}
          />
          <div onClick={e => e.stopPropagation()} style={{ marginTop: 16 }}>
            {renderButtons(true)}
          </div>
        </div>
      )}

      {/* Диалог «Добавить в проект» */}
      {saveDialog && project && (
        <Modal
          title="Добавить в проект"
          onClose={() => setSaveDialog(null)}
          footer={
            <ModalActions
              confirmLabel="Сохранить"
              cancelLabel="Отмена"
              onCancel={() => setSaveDialog(null)}
              onConfirm={() => {
                const name = (saveDialog.baseName.trim() + saveDialog.ext);
                if (!saveDialog.baseName.trim()) return;
                setSaveDialog(null);
                doSave(name);
              }}
              confirmDisabled={!saveDialog.baseName.trim()}
            />
          }
        >
          <div style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
            {/* Split-input: редактируемое имя + залоченное расширение */}
            <div style={{
              display: 'flex', alignItems: 'stretch',
              border: `1.5px solid ${C.border}`, borderRadius: 8,
              overflow: 'hidden', background: C.bgMain,
            }}>
              <input
                value={saveDialog.baseName}
                onChange={e => setSaveDialog({ ...saveDialog, baseName: e.target.value })}
                placeholder="имя файла"
                // eslint-disable-next-line jsx-a11y/no-autofocus
                autoFocus
                onKeyDown={e => {
                  if (e.key === 'Enter' && saveDialog.baseName.trim()) {
                    setSaveDialog(null);
                    doSave(saveDialog.baseName.trim() + saveDialog.ext);
                  }
                }}
                style={{
                  flex: 1, padding: '9px 10px', border: 'none', outline: 'none',
                  fontFamily: FONT.sans, fontSize: 14, background: 'transparent',
                  color: C.textPrimary, minWidth: 0,
                }}
              />
              {saveDialog.ext && (
                <div style={{
                  padding: '9px 11px', background: C.bgPanel,
                  color: C.textMuted, fontFamily: FONT.mono, fontSize: 13,
                  borderLeft: `1px solid ${C.border}`, userSelect: 'none',
                  flexShrink: 0, display: 'flex', alignItems: 'center',
                }}>
                  {saveDialog.ext}
                </div>
              )}
            </div>
            {(() => {
              const dir = getExplorerCreateInDir(project.id);
              return dir ? (
                <span style={{ fontSize: 11, color: C.textMuted, fontFamily: FONT.mono }}>
                  Папка: {dir}/
                </span>
              ) : null;
            })()}
          </div>
        </Modal>
      )}
    </div>
  );
}
