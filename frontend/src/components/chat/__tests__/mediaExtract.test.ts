// Парсинг медиа из результатов MCP-инструментов генерации (fal.ai + glif).
// Фикстуры glif — по подтверждённой живым токеном фактуре: resource_link content-блоки
// (uri + mimeType), assets get_project (uri, type, metadata.{format,width,height}),
// хосты glifusercontent.com / res.cloudinary.com, _meta.glif с billing telemetry.
import { describe, it, expect } from 'vitest';
import { extractMediaFromResult, extractMediaMeta, classifyUrl } from '../MediaBlock';

// ---------- fal.ai — регрессия существующих форматов ----------

const FAL_IMAGES = JSON.stringify({
  images: [{ url: 'https://fal.media/files/abc.png', width: 1024, height: 768, content_type: 'image/png', file_name: 'abc.png' }],
  request_id: 'req-1',
  endpoint_id: 'fal-ai/flux/dev',
  timings: { inference: 3.21 },
});

describe('fal — регрессия', () => {
  it('images в корне с content_type', () => {
    const media = extractMediaFromResult(FAL_IMAGES);
    expect(media).toEqual([{ kind: 'image', url: 'https://fal.media/files/abc.png', width: 1024, height: 768, fileName: 'abc.png' }]);
  });

  it('fal.media без content_type → image', () => {
    const media = extractMediaFromResult(JSON.stringify({ images: [{ url: 'https://fal.media/files/noext' }] }));
    expect(media).toHaveLength(1);
    expect(media[0].kind).toBe('image');
  });

  it('videos / audio_files / одиночные video и audio', () => {
    const media = extractMediaFromResult(JSON.stringify({
      videos: [{ url: 'https://fal.media/files/v.mp4', duration: 5.2 }],
      audio_files: [{ url: 'https://fal.media/files/a.mp3', duration: 12 }],
      video: { url: 'https://fal.media/files/single.webm' },
      audio: { url: 'https://fal.media/files/single.wav' },
    }));
    expect(media.map(m => m.kind)).toEqual(['video', 'audio', 'video', 'audio']);
  });

  it('массивы в result/data/output тоже находятся', () => {
    const media = extractMediaFromResult(JSON.stringify({ result: { data: { images: [{ url: 'https://fal.media/files/deep.png' }] } } }));
    expect(media).toHaveLength(1);
  });

  it('мета: модель из endpoint_id, время из timings, источник fal', () => {
    const meta = extractMediaMeta(FAL_IMAGES);
    expect(meta.model).toBe('dev');
    expect(meta.inferenceTime).toBeCloseTo(3.21);
    expect(meta.source).toBe('fal');
    expect(meta.costUsd).toBeUndefined();
  });

  it('мусор и невалидный JSON не роняют парсер', () => {
    expect(extractMediaFromResult('не json')).toEqual([]);
    expect(extractMediaFromResult('{"foo": 42}')).toEqual([]);
    expect(extractMediaMeta('не json')).toEqual({});
  });
});

// ---------- glif — resource_link content-блоки ----------

const GLIF_RESOURCE_LINK = JSON.stringify({
  content: [
    { type: 'text', text: 'Готово! Вот результат.' },
    { type: 'resource_link', uri: 'https://glifusercontent.com/i:r/abc123.png', mimeType: 'image/png', name: 'abc123.png' },
    { type: 'resource_link', uri: 'https://res.cloudinary.com/glif/video/upload/x1.mp4', mimeType: 'video/mp4' },
  ],
  _meta: { glif: { outputType: 'image', projectId: 'proj-1', jobId: 'job-1', status: 'succeeded' } },
});

describe('glif — resource_link', () => {
  it('извлекает image и video из content-блоков по uri + mimeType', () => {
    const media = extractMediaFromResult(GLIF_RESOURCE_LINK);
    expect(media).toHaveLength(2);
    expect(media[0]).toMatchObject({ kind: 'image', url: 'https://glifusercontent.com/i:r/abc123.png', fileName: 'abc123.png' });
    expect(media[1]).toMatchObject({ kind: 'video', url: 'https://res.cloudinary.com/glif/video/upload/x1.mp4' });
  });

  it('audio resource_link классифицируется как audio', () => {
    const media = extractMediaFromResult(JSON.stringify({
      content: [{ type: 'resource_link', uri: 'https://glifusercontent.com/i:r/song.mp3', mimeType: 'audio/mpeg' }],
    }));
    expect(media).toEqual([{ kind: 'audio', url: 'https://glifusercontent.com/i:r/song.mp3', duration: undefined, fileName: undefined }]);
  });

  it('мета: источник glif, outputType из _meta.glif', () => {
    const meta = extractMediaMeta(GLIF_RESOURCE_LINK);
    expect(meta.source).toBe('glif');
    expect(meta.outputType).toBe('image');
  });

  it('стоимость подхватывается из _meta.glif.billing при появлении', () => {
    const withBilling = JSON.stringify({
      content: [{ type: 'resource_link', uri: 'https://glifusercontent.com/i:r/a.png', mimeType: 'image/png' }],
      _meta: { glif: { outputType: 'image', billing: { costUsd: 0.04 } } },
    });
    expect(extractMediaMeta(withBilling).costUsd).toBe(0.04);
    // Без billing — цены нет (и «считается…» для glif не показывается: request_id нет)
    expect(extractMediaMeta(GLIF_RESOURCE_LINK).costUsd).toBeUndefined();
  });

  it('JSON-хвост project_id+job_id в text-блоке помечает источник glif', () => {
    const meta = extractMediaMeta(JSON.stringify({
      content: [
        { type: 'resource_link', uri: 'https://res.cloudinary.com/glif/image/upload/y.png', mimeType: 'image/png' },
        { type: 'text', text: '{"project_id": "p1", "job_id": "j1"}' },
      ],
    }));
    expect(meta.source).toBe('glif');
  });
});

// ---------- glif — assets из get_project ----------

const GLIF_ASSETS = JSON.stringify({
  project: {
    assets: [
      { assetId: 'a1', uri: 'https://glifusercontent.com/i:r/gen1.png', type: 'image', source: 'generated', filename: 'gen1.png', mimeType: 'image/png', sizeInBytes: 12345, metadata: { format: 'png', width: 512, height: 512 } },
      { assetId: 'a2', uri: 'https://res.cloudinary.com/glif/video/upload/gen2.mp4', type: 'video', source: 'generated', filename: 'gen2.mp4', metadata: { format: 'mp4' } },
    ],
  },
});

describe('glif — assets get_project', () => {
  it('извлекает assets с uri, классификация по type, размеры из metadata', () => {
    const media = extractMediaFromResult(GLIF_ASSETS);
    expect(media).toHaveLength(2);
    expect(media[0]).toMatchObject({ kind: 'image', url: 'https://glifusercontent.com/i:r/gen1.png', width: 512, height: 512, fileName: 'gen1.png' });
    expect(media[1]).toMatchObject({ kind: 'video', url: 'https://res.cloudinary.com/glif/video/upload/gen2.mp4', fileName: 'gen2.mp4' });
  });

  it('assets в корне результата тоже находятся', () => {
    const media = extractMediaFromResult(JSON.stringify({ assets: [{ uri: 'https://glifusercontent.com/i:r/root.png', type: 'image' }] }));
    expect(media).toHaveLength(1);
  });

  it('классификация по metadata.format, когда нет ни type, ни mimeType', () => {
    expect(classifyUrl({ uri: 'https://glifusercontent.com/i:r/x', metadata: { format: 'webp' } })).toBe('image');
    expect(classifyUrl({ uri: 'https://glifusercontent.com/i:r/x', metadata: { format: 'mov' } })).toBe('video');
    expect(classifyUrl({ uri: 'https://glifusercontent.com/i:r/x', metadata: { format: 'ogg' } })).toBe('audio');
  });

  it('JSON с assets внутри text-блока тоже разбирается', () => {
    const media = extractMediaFromResult(JSON.stringify({
      content: [{ type: 'text', text: '{"assets":[{"uri":"https://glifusercontent.com/i:r/t.png","type":"image"}]}' }],
    }));
    expect(media).toHaveLength(1);
    expect(media[0].kind).toBe('image');
  });
});

// ---------- glif — классификация по хостам и дедуп ----------

describe('glif — хосты и дедуп', () => {
  it('glifusercontent.com и res.cloudinary.com без типа → image', () => {
    expect(classifyUrl({ url: 'https://glifusercontent.com/i:r/noext' })).toBe('image');
    expect(classifyUrl({ url: 'https://res.cloudinary.com/glif/image/upload/noext' })).toBe('image');
    // Незнакомый хост без типа — по-прежнему null
    expect(classifyUrl({ url: 'https://example.com/noext' })).toBeNull();
  });

  it('cloudinary не помечает результат как glif (общий CDN)', () => {
    const meta = extractMediaMeta(JSON.stringify({ images: [{ url: 'https://res.cloudinary.com/other/image/upload/x.png' }] }));
    expect(meta.source).toBeUndefined();
  });

  it('glifusercontent помечает источник glif даже без _meta', () => {
    const meta = extractMediaMeta(JSON.stringify({ assets: [{ uri: 'https://glifusercontent.com/i:r/z.png', type: 'image' }] }));
    expect(meta.source).toBe('glif');
  });

  it('один файл из assets и resource_link показывается один раз', () => {
    const media = extractMediaFromResult(JSON.stringify({
      assets: [{ uri: 'https://glifusercontent.com/i:r/dup.png', type: 'image' }],
      content: [{ type: 'resource_link', uri: 'https://glifusercontent.com/i:r/dup.png', mimeType: 'image/png' }],
    }));
    expect(media).toHaveLength(1);
  });

  it('смешанный результат fal + glif: извлекаются оба', () => {
    const media = extractMediaFromResult(JSON.stringify({
      images: [{ url: 'https://fal.media/files/f.png', content_type: 'image/png' }],
      content: [{ type: 'resource_link', uri: 'https://glifusercontent.com/i:r/g.mp4', mimeType: 'video/mp4' }],
    }));
    // fal-массивы сканируются раньше content-блоков — порядок: image, затем video
    expect(media.map(m => m.kind)).toEqual(['image', 'video']);
  });
});

// ---------- glif — боевой сплющенный формат (приёмка Веры на живом токене) ----------
// CLI плющит tool_result view_media в ТЕКСТ: маркерные строки [Resource link: name] url,
// затем text-блок «Showing N media item(s)», затем JSON-хвост structuredContent.
// Целиком это НЕ валидный JSON — старый парсер (JSON.parse всей строки) возвращал [].

const CLOUD = 'https://res.cloudinary.com/dzkwltgyd/image/upload/v1710000000';
const LIVE_MEDIA_ITEMS = [
  { url: `${CLOUD}/sxdbfm9amrqzbuarptpg.jpg`, title: 'bangs-curtain.jpg' },
  { url: `${CLOUD}/bbbbbbbbbbbbbbbbbbbb.jpg`, title: 'bangs-curtain-2.jpg' },
  { url: `${CLOUD}/cccccccccccccccccccc.jpg`, title: 'bangs-curtain-3.jpg' },
  { url: `${CLOUD}/dddddddddddddddddddd.jpg`, title: 'bangs-curtain-4.jpg' },
  { url: `${CLOUD}/eeeeeeeeeeeeeeeeeeee.jpg`, title: 'bangs-curtain-5.jpg' },
];
// Точная форма боевого результата: маркеры \r\n, текст-сводка, JSON-хвост без width/height и job_id
const LIVE_FLATTENED =
  LIVE_MEDIA_ITEMS.map(m => `[Resource link: ${m.title}] ${m.url}`).join('\r\n') +
  '\r\nShowing 5 media item(s). View on Glif: https://glif.app/chat/abc123\r\n' +
  JSON.stringify({
    outputType: 'media_view',
    projectId: 'Gce3vkCh4ZVkUu2xq2Ed13phbBgVr1',
    projectUrl: 'https://glif.app/chat/abc123',
    status: 'completed',
    media: LIVE_MEDIA_ITEMS.map(m => ({ url: m.url, title: m.title, kind: 'image' })),
  });

describe('glif — боевой сплющенный формат', () => {
  it('5 изображений из маркеров + media[], дедуп маркер↔media[] одного URL', () => {
    const media = extractMediaFromResult(LIVE_FLATTENED);
    expect(media).toHaveLength(5);
    expect(media.every(m => m.kind === 'image')).toBe(true);
    expect(media[0]).toMatchObject({ url: LIVE_MEDIA_ITEMS[0].url, fileName: 'bangs-curtain.jpg' });
    // В media[] размеров нет — блок рендерится без них
    const img = media[0] as Extract<typeof media[0], { kind: 'image' }>;
    expect(img.width).toBeUndefined();
    expect(img.height).toBeUndefined();
  });

  it('мета: источник glif по camelCase projectId без job_id, outputType media_view', () => {
    const meta = extractMediaMeta(LIVE_FLATTENED);
    expect(meta.source).toBe('glif');
    expect(meta.outputType).toBe('media_view');
  });

  it('мусор до JSON-хвоста не роняет парсер: маркеры извлекаются в любом случае', () => {
    const brokenTail = '[Resource link: a.png] https://glifusercontent.com/i:r/a.png\r\n{broken json…';
    const media = extractMediaFromResult(brokenTail);
    expect(media).toEqual([{ kind: 'image', url: 'https://glifusercontent.com/i:r/a.png', width: undefined, height: undefined, fileName: 'a.png' }]);
    expect(extractMediaMeta(brokenTail).source).toBe('glif'); // по glif-хосту медиа
  });

  it('structuredContent внутри полного результата (content + structuredContent)', () => {
    const full = JSON.stringify({
      content: [
        { type: 'resource_link', name: 'bangs-curtain.jpg', uri: LIVE_MEDIA_ITEMS[0].url },
        { type: 'text', text: 'Showing 1 media item(s). View on Glif: https://glif.app/chat/abc123' },
      ],
      structuredContent: {
        outputType: 'media_view', projectId: 'Gce3vkCh4ZVkUu2xq2Ed13phbBgVr1',
        projectUrl: 'https://glif.app/chat/abc123', status: 'completed',
        media: [{ url: LIVE_MEDIA_ITEMS[0].url, title: 'bangs-curtain.jpg', kind: 'image' }],
      },
    });
    const media = extractMediaFromResult(full);
    expect(media).toHaveLength(1); // дедуп resource_link ↔ media[]
    const meta = extractMediaMeta(full, media);
    expect(meta.source).toBe('glif');
    expect(meta.outputType).toBe('media_view');
  });

  it('ссылка «View on Glif» (glif.app) не становится медиа-блоком', () => {
    const media = extractMediaFromResult(LIVE_FLATTENED);
    expect(media.some(m => m.url.includes('glif.app'))).toBe(false);
  });
});

// ---------- glif — входные референсы пользователя не показываем ----------
// Правило: source="uploaded" и URL с glifchat-image-input-production — это вход генерации
// (картинка, которую пользователь дал на вход), а не результат; в ленте её не показываем.

describe('glif — входные референсы не показываем', () => {
  it('asset source="uploaded" пропускается, source="generated" — показывается', () => {
    const media = extractMediaFromResult(JSON.stringify({
      assets: [
        { uri: `${CLOUD}/user-input.png`, type: 'image', source: 'uploaded', filename: 'user-input.png' },
        { uri: `${CLOUD}/gen-out.png`, type: 'image', source: 'generated', filename: 'gen-out.png' },
      ],
    }));
    expect(media).toEqual([{ kind: 'image', url: `${CLOUD}/gen-out.png`, width: undefined, height: undefined, fileName: 'gen-out.png' }]);
  });

  it('URL с glifchat-image-input-production пропускается из любого источника (маркер, media[], assets)', () => {
    const inputUrl = `${CLOUD}/glifchat-image-input-production/u1.png`;
    const flattened =
      `[Resource link: input.png] ${inputUrl}\r\n` +
      JSON.stringify({
        outputType: 'media_view', projectId: 'p1',
        media: [
          { url: inputUrl, title: 'input.png', kind: 'image' },
          { url: `${CLOUD}/real-output.png`, title: 'real-output.png', kind: 'image' },
        ],
      });
    const media = extractMediaFromResult(flattened);
    expect(media).toEqual([{ kind: 'image', url: `${CLOUD}/real-output.png`, width: undefined, height: undefined, fileName: 'real-output.png' }]);
  });
});

// ---------- glif — стоимость только по точным billing-полям ----------

describe('glif — стоимость без спекулятивных ключей', () => {
  it('одноимённые чужие поля (cost/price/total/amount/usd) цену не дают', () => {
    const meta = extractMediaMeta(JSON.stringify({
      content: [{ type: 'resource_link', uri: 'https://glifusercontent.com/i:r/a.png', mimeType: 'image/png' }],
      _meta: { glif: { outputType: 'image', cost: 5, price: 3, total: 1, amount: 2, usd: 4, totalCost: 6 } },
    }));
    expect(meta.costUsd).toBeUndefined();
  });

  it('точные поля billing: costUsd и cost_usd', () => {
    expect(extractMediaMeta(JSON.stringify({
      _meta: { glif: { outputType: 'image', billing: { costUsd: 0.04 } } },
    })).costUsd).toBe(0.04);
    expect(extractMediaMeta(JSON.stringify({
      _meta: { glif: { outputType: 'image', billing: { cost_usd: 0.07 } } },
    })).costUsd).toBe(0.07);
  });
});

// ---------- боевой смешанный результат: fal JSON + glif-маркер ----------

describe('смешанный боевой результат fal + glif', () => {
  it('fal JSON в начале и glif-маркер строкой — извлекаются оба', () => {
    const mixed =
      JSON.stringify({ images: [{ url: 'https://fal.media/files/f.png', content_type: 'image/png' }] }) +
      '\r\n[Resource link: g.mp4] https://glifusercontent.com/i:r/g.mp4';
    const media = extractMediaFromResult(mixed);
    expect(media.map(m => m.kind)).toEqual(['video', 'image']); // маркеры идут первым проходом
  });
});
