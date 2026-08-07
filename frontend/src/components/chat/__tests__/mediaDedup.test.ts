// Кросс-блочный дедуп медиа по ленте: glif get_job_status (project_update) и view_media
// (media_view) несут одно и то же медиа — рендериться должен ОДИН блок, и выживает
// финальная галерея (media_view). Дефект приёмки: один mp4 показывался двумя плеерами.
import { describe, it, expect } from 'vitest';
import type { ChatItem } from '../../../types';
import { buildMediaVisibility, normalizeMediaUrl } from '../mediaDedup';
import type { MediaItem } from '../MediaBlock';

type ToolItem = Extract<ChatItem, { kind: 'tool_use' }>;

const tool = (id: string, name: string, result: string, over: Partial<ToolItem> = {}): ToolItem =>
  ({ kind: 'tool_use', id, name, input: {}, result, ...over });

// Боевые формы glif: project_update — сводка get_job_status, media_view — финальная галерея
const glifUpdate = (media: Array<{ url: string; kind?: string; title?: string }>) =>
  JSON.stringify({ outputType: 'project_update', projectId: 'p1', jobId: 'j1', status: 'completed', media });
const glifGallery = (media: Array<{ url: string; kind?: string; title?: string }>) =>
  JSON.stringify({ outputType: 'media_view', projectId: 'p1', projectUrl: 'https://glif.app/chat/x', status: 'completed', media });

const IMG = 'https://res.cloudinary.com/dzkwltgyd/image/upload/v1710000000/gen1.png';
const VID = 'https://res.cloudinary.com/dzkwltgyd/video/upload/v1710000000/gen2.mp4';

const visible = (map: Map<string, MediaItem[]>, id: string): MediaItem[] => map.get(id) ?? [];

describe('glif — дедуп project_update + media_view', () => {
  it('один и тот же image+video в двух tool-блоках → один блок, выживает media_view', () => {
    const items = [
      tool('upd', 'mcp__glif__get_job_status', glifUpdate([{ url: IMG, kind: 'image' }, { url: VID, kind: 'video' }])),
      tool('gal', 'mcp__glif__view_media', glifGallery([{ url: IMG, kind: 'image' }, { url: VID, kind: 'video' }])),
    ];
    const map = buildMediaVisibility(items);
    // У project_update всё скрыто — блок деградирует в обычную строку «готово»
    expect(visible(map, 'upd')).toEqual([]);
    // Галерея несёт оба медиа — ровно один img и один video (один плеер на mp4)
    const gal = visible(map, 'gal');
    expect(gal.map(m => m.kind)).toEqual(['image', 'video']);
    expect(gal.map(m => m.url)).toEqual([IMG, VID]);
  });

  it('media_view раньше project_update в ленте — project_update всё равно скрыт', () => {
    const items = [
      tool('gal', 'mcp__glif__view_media', glifGallery([{ url: IMG, kind: 'image' }])),
      tool('upd', 'mcp__glif__get_job_status', glifUpdate([{ url: IMG, kind: 'image' }])),
    ];
    const map = buildMediaVisibility(items);
    expect(visible(map, 'gal')).toHaveLength(1);
    expect(visible(map, 'upd')).toEqual([]);
  });

  it('частичное пересечение: project_update [A, B], media_view [A] → у project_update остаётся B', () => {
    const OTHER = 'https://res.cloudinary.com/dzkwltgyd/image/upload/v1710000001/other.png';
    const items = [
      tool('upd', 'mcp__glif__get_job_status', glifUpdate([{ url: IMG, kind: 'image' }, { url: OTHER, kind: 'image' }])),
      tool('gal', 'mcp__glif__view_media', glifGallery([{ url: IMG, kind: 'image' }])),
    ];
    const map = buildMediaVisibility(items);
    expect(visible(map, 'upd').map(m => m.url)).toEqual([OTHER]);
    expect(visible(map, 'gal')).toHaveLength(1);
  });

  it('одиночный glif project_update без пары — медиа не пропадает', () => {
    const items = [tool('upd', 'mcp__glif__get_job_status', glifUpdate([{ url: IMG, kind: 'image' }, { url: VID, kind: 'video' }]))];
    const map = buildMediaVisibility(items);
    expect(visible(map, 'upd').map(m => m.kind)).toEqual(['image', 'video']);
  });

  it('два media_view с одним URL — выживает первый, у второго дубль скрыт', () => {
    const items = [
      tool('gal1', 'mcp__glif__view_media', glifGallery([{ url: IMG, kind: 'image' }])),
      tool('gal2', 'mcp__glif__view_media', glifGallery([{ url: IMG, kind: 'image' }])),
    ];
    const map = buildMediaVisibility(items);
    expect(visible(map, 'gal1')).toHaveLength(1);
    expect(visible(map, 'gal2')).toEqual([]);
  });

  it('ошибочный tool_use (isError) в дедупе не участвует', () => {
    const items = [
      tool('upd', 'mcp__glif__get_job_status', glifUpdate([{ url: IMG, kind: 'image' }]), { isError: true }),
      tool('gal', 'mcp__glif__view_media', glifGallery([{ url: IMG, kind: 'image' }])),
    ];
    const map = buildMediaVisibility(items);
    expect(map.has('upd')).toBe(false);
    expect(visible(map, 'gal')).toHaveLength(1);
  });
});

describe('fal — регрессия', () => {
  const FAL = JSON.stringify({ images: [{ url: 'https://fal.media/files/abc.png', width: 1024, height: 768, content_type: 'image/png' }], request_id: 'req-1' });

  it('одиночный fal-блок — 1 img на месте', () => {
    const map = buildMediaVisibility([tool('fal1', 'mcp__fal-ai__run_model', FAL)]);
    expect(visible(map, 'fal1')).toHaveLength(1);
    expect(visible(map, 'fal1')[0].url).toBe('https://fal.media/files/abc.png');
  });

  it('fal рядом с glif-галереей — не скрывается (разные URL)', () => {
    const items = [
      tool('fal1', 'mcp__fal-ai__run_model', FAL),
      tool('gal', 'mcp__glif__view_media', glifGallery([{ url: IMG, kind: 'image' }])),
    ];
    const map = buildMediaVisibility(items);
    expect(visible(map, 'fal1')).toHaveLength(1);
    expect(visible(map, 'gal')).toHaveLength(1);
  });

  it('два fal-блока с одним URL — выживает первый (общее правило «один URL = один блок»)', () => {
    const items = [
      tool('fal1', 'mcp__fal-ai__check_job', FAL),
      tool('fal2', 'mcp__fal-ai__get_job_result', FAL),
    ];
    const map = buildMediaVisibility(items);
    expect(visible(map, 'fal1')).toHaveLength(1);
    expect(visible(map, 'fal2')).toEqual([]);
  });
});

describe('normalizeMediaUrl', () => {
  it('trim и финальный слэш', () => {
    expect(normalizeMediaUrl(`  ${IMG} `)).toBe(IMG);
    expect(normalizeMediaUrl(`${IMG}/`)).toBe(IMG);
    expect(normalizeMediaUrl('/')).toBe('/');
  });

  it('дедуп ловит URL с хвостовым слэшом как тот же файл', () => {
    const items = [
      tool('upd', 'mcp__glif__get_job_status', glifUpdate([{ url: `${IMG}/`, kind: 'image' }])),
      tool('gal', 'mcp__glif__view_media', glifGallery([{ url: IMG, kind: 'image' }])),
    ];
    const map = buildMediaVisibility(items);
    expect(visible(map, 'upd')).toEqual([]);
    expect(visible(map, 'gal')).toHaveLength(1);
  });

  it('разные URL не сливаются (query — часть ключа)', () => {
    const items = [
      tool('upd', 'mcp__glif__get_job_status', glifUpdate([{ url: `${IMG}?w=100`, kind: 'image' }])),
      tool('gal', 'mcp__glif__view_media', glifGallery([{ url: `${IMG}?w=200`, kind: 'image' }])),
    ];
    const map = buildMediaVisibility(items);
    expect(visible(map, 'upd')).toHaveLength(1);
    expect(visible(map, 'gal')).toHaveLength(1);
  });
});
