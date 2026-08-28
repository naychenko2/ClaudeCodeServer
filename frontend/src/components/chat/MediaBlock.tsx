import { useState, useRef, useContext } from 'react';
import { X } from 'lucide-react';
import { getExplorerCreateInDir } from '../FileExplorer';
import { api } from '../../lib/api';
import { C, FONT, SHADOW } from '../../lib/design';
import { getEffectiveTheme } from '../../lib/themeMode';
import { Modal, ModalActions } from '../ui';
import { proxyUrl } from './MarkdownContent';
import { ChatProjectContext } from './contexts';
import { fmtCredits } from './glifStats';

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

// Домены glif-медиа (подтверждены живым токеном; синхронно с AllowedHosts бэкенд-прокси):
// медиа без типа с них считаем изображением (как fal.media). res.cloudinary.com — их CDN
// для части выдачи. glif.app/glif.xyz здесь нет: там страницы, а не медиа-файлы.
const GLIF_HOSTS = ['glifusercontent.com', 'res.cloudinary.com'];
// Специфичные для glif хосты — по ним определяем источник генерации (cloudinary —
// общий CDN, для детекта источника не используем, только для классификации типа)
const GLIF_SPECIFIC_HOSTS = ['glifusercontent.com'];

function hostMatches(url: string, hosts: string[]): boolean {
  try {
    const h = new URL(url).hostname;
    return hosts.some(x => h === x || h.endsWith('.' + x));
  } catch { return false; }
}

// JSON из результатов MCP-инструментов (fal/glif): форма заранее неизвестна и
// не описывается контрактом, поэтому обходим её с проверками на каждом шаге
// (unknown-глубь вместо any).
type Json = null | boolean | number | string | Json[] | { [key: string]: Json };

// Значение — JSON-объект (не массив и не примитив)?
function asObj(v: Json | undefined): { [key: string]: Json } | null {
  return v != null && typeof v === 'object' && !Array.isArray(v) ? v : null;
}

// Строка, если значение — строка; иначе пустая строка
function str(v: Json | undefined): string {
  return typeof v === 'string' ? v : '';
}

export function classifyUrl(item: Json | undefined): 'image' | 'video' | 'audio' | null {
  const obj = asObj(item);
  if (!obj) return null;
  const url = obj.url ?? obj.uri;
  if (typeof url !== 'string') return null;
  // Явный тип элемента: glif assets — type:"image"|"video"|"audio",
  // glif view_media media[] — kind с теми же значениями
  const t: string = typeof obj.type === 'string' ? obj.type
    : typeof obj.kind === 'string' && ['image', 'video', 'audio'].includes(obj.kind) ? obj.kind
    : '';
  if (t === 'video') return 'video';
  if (t === 'audio') return 'audio';
  if (t === 'image') return 'image';
  // MIME: fal — content_type; glif (MCP resource_link / assets) — mimeType/contentType;
  // запасной вариант — формат файла (glif assets: metadata.format = "png"/"mp4"/…)
  const ct = str(obj.content_type ?? obj.mimeType ?? obj.contentType);
  const metaFmt = asObj(obj.metadata)?.format;
  const fmt = typeof metaFmt === 'string' ? `.${metaFmt.toLowerCase()}` : '';
  if (ct.startsWith('video/') || /\.(mp4|webm|mov|avi|mkv)(\?|$)/i.test(url) || /\.(mp4|webm|mov|avi|mkv)$/.test(fmt)) return 'video';
  if (ct.startsWith('audio/') || /\.(mp3|wav|ogg|flac|aac|m4a|opus|weba)(\?|$)/i.test(url) || /\.(mp3|wav|ogg|flac|aac|m4a|opus|weba)$/.test(fmt)) return 'audio';
  if (ct.startsWith('image/') || /\.(png|jpg|jpeg|gif|webp|svg|avif)(\?|$)/i.test(url) || /\.(png|jpg|jpeg|gif|webp|svg|avif)$/.test(fmt)) return 'image';
  // fal.media без content_type — по умолчанию изображение (совместимость)
  if (url.includes('fal.media') || url.includes('fal.run')) return 'image';
  // glif-хост без mimeType — тоже изображение
  if (hostMatches(url, GLIF_HOSTS)) return 'image';
  return null;
}

// Маркерная строка сплющенного CLI tool_result: `[Resource link: name] <url>`.
// Боевой формат: CLI плющит content-блоки resource_link в текст, JSON-хвост идёт следом —
// целиком такая строка не JSON, поэтому маркеры вытаскиваем regex'ом независимо от JSON.
const RESOURCE_LINK_RE = /\[Resource link:\s*([^\]]+)\]\s*(https?:\/\/\S+)/g;

// Толерантный разбор результата: целиком JSON — отлично; нет — пробуем хвост от первой
// фигурной скобки до последней (сплющенный боевой формат «мусор + {json}»).
// На мусоре не падаем — вернём undefined, медиа из маркеров всё равно покажутся.
// JSON.parse по определению даёт JSON — каст к Json и есть проверка на границе.
function parseLoose(text: string): Json | undefined {
  try { return JSON.parse(text) as Json; } catch { /* не чистый JSON */ }
  const start = text.indexOf('{');
  const end = text.lastIndexOf('}');
  if (start < 0 || end <= start) return undefined;
  try { return JSON.parse(text.slice(start, end + 1)) as Json; } catch { return undefined; }
}

// Извлекает изображения, видео и аудио из результата MCP-инструмента.
// Форматы: fal-ai (массивы images/videos/audio_files в корне/result/data/output,
// одиночные video/audio) и glif (MCP-блоки resource_link/resource в content, assets из
// get_project, media[] из view_media, JSON внутри text-блоков, маркерные строки
// `[Resource link: …] url` сплющенного CLI). Один и тот же файл, пришедший разными
// путями (маркер + media[] + assets), показывается один раз — дедуп по URL.
// Входные референсы пользователя (source="uploaded", glifchat-image-input-production
// в пути) — не результат генерации, не показываем.
export function extractMediaFromResult(result: string): MediaItem[] {
  const items: MediaItem[] = [];

  const push = (item: Json | undefined) => {
    const obj = asObj(item);
    if (!obj) return;
    const url = obj.url ?? obj.uri;
    if (typeof url !== 'string') return;
    // Входные изображения пользователя (uploaded) — не выход генерации
    if (obj.source === 'uploaded' || url.includes('glifchat-image-input-production')) return;
    const kind = classifyUrl(obj);
    if (!kind) return;
    if (items.some(m => m.url === url)) return;
    const fileNameRaw = obj.file_name ?? obj.fileName ?? obj.filename ?? obj.name ?? obj.title;
    const fileName = typeof fileNameRaw === 'string' ? fileNameRaw : undefined;
    // Размеры: fal кладёт в корень элемента, glif assets — в metadata.{width,height};
    // в view_media media[] размеров нет — блок рендерится без них, это ок
    const meta = asObj(obj.metadata);
    const widthRaw = obj.width ?? meta?.width;
    const heightRaw = obj.height ?? meta?.height;
    const width = typeof widthRaw === 'number' ? widthRaw : undefined;
    const height = typeof heightRaw === 'number' ? heightRaw : undefined;
    const duration = typeof obj.duration === 'number' ? obj.duration : undefined;
    if (kind === 'audio') items.push({ kind: 'audio', url, duration, fileName });
    else items.push({ kind, url, width, height, fileName, ...(kind === 'video' ? { duration } : {}) } as MediaItem);
  };

  const scan = (value: Json, depth: number) => {
    if (!value || typeof value !== 'object' || depth > 3) return;
    if (Array.isArray(value)) {
      // Массив MCP-блоков контента (glif): resource_link / resource
      for (const b of value) {
        const block = asObj(b);
        if (!block) continue;
        if (block.type === 'resource_link') push(block);
        else if (block.type === 'resource') push(block.resource);
      }
      return;
    }
    // Массивы медиа (fal + glif assets + glif view_media media[])
    for (const arr of [value.images, value.videos, value.audio_files, value.audios, value.assets, value.media]) {
      if (Array.isArray(arr)) for (const item of arr) push(item);
    }
    // Одиночные объекты
    for (const key of ['video', 'audio', 'audio_file', 'image'] as const) push(value[key]);
    // MCP CallToolResult: content-блоки; text-блоки могут нести JSON (glif-хвосты)
    if (Array.isArray(value.content)) {
      scan(value.content, depth + 1);
      for (const b of value.content) {
        const block = asObj(b);
        if (block?.type === 'text' && typeof block.text === 'string' && block.text.trimStart().startsWith('{')) {
          try { scan(JSON.parse(block.text) as Json, depth + 1); } catch { /* обычный текст */ }
        }
      }
    }
    for (const key of ['result', 'data', 'output', 'project', 'structuredContent'] as const) scan(value[key], depth + 1);
  };

  // 1. Маркерные строки боевого формата — независимо от того, парсится ли JSON-хвост
  for (const m of result.matchAll(RESOURCE_LINK_RE)) {
    push({ name: m[1].trim(), url: m[2] });
  }
  // 2. JSON целиком или хвостом — fal-формат и glif structuredContent
  const parsed = parseLoose(result);
  if (parsed) scan(parsed, 0);

  return items;
}

export interface MediaMeta {
  model?: string;
  inferenceTime?: number;
  // Источник генерации: fal (request_id/endpoint_id) или glif (_meta.glif, project_id+job_id,
  // медиа с glif-хоста). В футере метку показываем только для glif — рендер fal не меняется.
  source?: 'fal' | 'glif';
  outputType?: string;
  // jobId генерации glif — ключ сопоставления с glif_cost (кредиты с backend, GlifCostContext)
  jobId?: string;
  // Стоимость, если доехала в самом tool_result (glif _meta.billing telemetry).
  // Точная стоимость fal берётся отдельно — с backend (см. FalCostContext).
  costUsd?: number;
}

// Поля _meta.glif, в которых billing telemetry несёт сумму. Только точные имена —
// спекулятивные (cost/total/price/…) убраны: чужое одноимённое поле давало фантомную цену.
// Нет поля → нет цены (для glif «считается…» не показываем).
const BILLING_COST_KEYS = ['costUsd', 'cost_usd'];

function findGlifBillingCost(glif: { [key: string]: Json } | null | undefined): number | undefined {
  if (!glif) return undefined;
  for (const bag of [asObj(glif.billing), asObj(glif.billingTelemetry), asObj(glif.billing_telemetry), asObj(glif.telemetry), glif]) {
    if (!bag) continue;
    for (const k of BILLING_COST_KEYS) {
      const v = Number(bag[k]);
      if (Number.isFinite(v) && v > 0) return v;
    }
  }
  return undefined;
}

// Признаки glif-результата: _meta.glif / meta.glif; пара project_id+job_id (get_job_status)
// или camelCase projectId (боевой view_media — без job_id) в корне / structuredContent /
// JSON-хвосте text-блока; outputType вида media_*; либо медиа с glif-хоста
function detectGlif(parsed: Json | undefined, media: MediaItem[]): { glifMeta?: { [key: string]: Json }; isGlif: boolean; outputType?: string } {
  // JSON не разобрался (битый хвост) — источник всё равно определяем по хостам медиа
  if (!parsed || typeof parsed !== 'object')
    return { isGlif: media.some(m => hostMatches(m.url, GLIF_SPECIFIC_HOSTS)) };
  const root = Array.isArray(parsed) ? null : parsed;
  const glifMeta = asObj(asObj(root?._meta)?.glif) ?? asObj(asObj(root?.meta)?.glif);
  if (glifMeta) return { glifMeta, isGlif: true };
  // Кандидатные объекты: сам результат и его structuredContent (хвост view_media)
  const bags = [root, asObj(root?.structuredContent), asObj(root?.result)];
  const isGlifBag = (o: { [key: string]: Json } | null) =>
    typeof o?.projectId === 'string' || // camelCase без job_id — боевой view_media
    (typeof o?.project_id === 'string' && typeof o?.job_id === 'string') ||
    (typeof o?.outputType === 'string' && o.outputType.startsWith('media_'));
  for (const bag of bags) {
    if (bag && isGlifBag(bag)) return { isGlif: true, outputType: typeof bag.outputType === 'string' ? bag.outputType : undefined };
  }
  const blocks: Json[] = Array.isArray(parsed) ? parsed
    : root && Array.isArray(root.content) ? root.content
    : [];
  for (const b of blocks) {
    const block = asObj(b);
    if (block?.type === 'text' && typeof block.text === 'string' && block.text.trimStart().startsWith('{')) {
      try { if (isGlifBag(asObj(JSON.parse(block.text) as Json))) return { isGlif: true }; } catch { /* не JSON */ }
    }
  }
  if (media.some(m => hostMatches(m.url, GLIF_SPECIFIC_HOSTS))) return { isGlif: true };
  return { isGlif: false };
}

// Извлекает метаданные генерации (источник, модель, время, стоимость из JSON) из
// результата MCP-инструмента. media — уже извлечённые элементы (extractMediaFromResult),
// чтобы не парсить результат дважды; без него посчитаются внутри.
export function extractMediaMeta(result: string, media?: MediaItem[]): MediaMeta {
  try {
    const parsed = parseLoose(result);
    const root = asObj(parsed);
    // Имя модели: endpoint_id → берём только короткое имя после последнего / (в результате fal обычно отсутствует)
    const endpointId = typeof root?.endpoint_id === 'string' ? root.endpoint_id : undefined;
    const model = endpointId ? endpointId.split('/').pop() : undefined;
    // Время генерации: ищем в нескольких местах
    const r = asObj(root?.result) ?? root;
    const inferenceTimeRaw =
      asObj(r?.timings)?.inference ??
      asObj(r?.metrics)?.inference_time ??
      asObj(root?.timings)?.inference ??
      asObj(root?.metrics)?.inference_time;

    const items = media ?? extractMediaFromResult(result);
    const { glifMeta, isGlif, outputType: bagOutputType } = detectGlif(parsed, items);
    // jobId glif-генерации: в _meta.glif или в одном из «мешков» результата (snake/camel)
    const jobId: string | undefined = [glifMeta, root, asObj(root?.structuredContent), asObj(root?.result)]
      .map(b => b?.jobId ?? b?.job_id)
      .find((v): v is string => typeof v === 'string');
    const source: MediaMeta['source'] = isGlif
      ? 'glif'
      : (root?.request_id || endpointId || items.some(m => m.url.includes('fal.media') || m.url.includes('fal.run')))
        ? 'fal'
        : undefined;
    const outputType = glifMeta?.outputType ?? glifMeta?.output_type ?? (isGlif ? bagOutputType ?? root?.outputType : undefined);

    return {
      model: model || undefined,
      inferenceTime: inferenceTimeRaw ? Number(inferenceTimeRaw) : undefined,
      source,
      outputType: typeof outputType === 'string' ? outputType : undefined,
      jobId: isGlif ? jobId : undefined,
      costUsd: findGlifBillingCost(glifMeta),
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
  credits,
  source,
  outputType,
  online = true,
}: {
  m: MediaItem;
  filename: string;
  model?: string;
  inferenceTime?: number;
  costUsd?: number;
  costPending?: boolean;
  // Списанные кредиты glif (с backend по jobId через GlifCostContext); нет — не показываем
  credits?: number;
  source?: 'fal' | 'glif';
  outputType?: string;
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
  // Метка источника — только glif: fal-рендер исторически без метки, не меняем
  if (source === 'glif') metaParts.push(outputType ? `glif · ${outputType}` : 'glif');
  if (m.kind !== 'audio' && m.width && m.height) metaParts.push(`${m.width}×${m.height}`);
  if ((m.kind === 'video' || m.kind === 'audio') && m.duration) metaParts.push(`${m.duration.toFixed(1)}с`);
  if (inferenceTime) metaParts.push(`${inferenceTime.toFixed(1)}с`);
  if (model) metaParts.push(model);
  // Стоимость: fal — точная, с backend (billing-events, «считается…» пока ждём);
  // glif — если доехала в JSON результата; не доехала — просто без цены, без вечной метки.
  if (costUsd) metaParts.push(costUsd < 0.01 ? `$${costUsd.toFixed(4)}` : `$${costUsd.toFixed(2)}`);
  else if (costPending) metaParts.push('считается…');
  // Кредиты glif — с backend по jobId (glif_cost); нет данных — ничего не добавляем
  if (credits !== undefined) metaParts.push(fmtCredits(credits));

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
          ? { ...btnBase, background: 'rgba(255,255,255,0.15)', color: C.onDark, borderColor: 'rgba(255,255,255,0.25)', opacity: online ? 1 : 0.4, cursor: online ? 'pointer' : 'not-allowed' }
          : { ...btnBase, background: online && dlHov ? C.accent : 'rgba(237,231,218,0.92)', color: online && dlHov ? C.onAccent : C.textPrimary, borderColor: online && dlHov ? C.accent : C.border, opacity: online ? 1 : 0.4, cursor: online ? 'pointer' : 'not-allowed' }
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
            ? { ...btnBase, background: saveState === 'saved' ? C.success : saveState === 'error' ? C.danger : 'rgba(255,255,255,0.15)', color: C.onDark, borderColor: 'rgba(255,255,255,0.25)', opacity: (!online || saveState === 'saving') ? 0.4 : 1, cursor: online ? 'pointer' : 'not-allowed' }
            : { ...btnBase, background: saveState === 'saved' ? C.success : saveState === 'error' ? C.danger : (online && saveHov ? C.accent : 'rgba(237,231,218,0.92)'), color: (saveState === 'saved' || saveState === 'error' || (online && saveHov)) ? C.onAccent : C.textPrimary, borderColor: saveState === 'saved' ? C.success : saveState === 'error' ? C.danger : (online && saveHov ? C.accent : C.border), opacity: (!online || saveState === 'saving') ? 0.4 : 1, cursor: online ? 'pointer' : 'not-allowed' }
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
              borderRadius: 10, color: C.onDark, fontSize: 18,
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
                autoComplete="off"
                value={saveDialog.baseName}
                onChange={e => setSaveDialog({ ...saveDialog, baseName: e.target.value })}
                placeholder="имя файла"
                // Автофокус осознанный: диалог сохранения сразу ждёт ввода имени файла
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
