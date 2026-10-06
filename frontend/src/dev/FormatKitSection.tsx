import { useState } from 'react';
import { Island, IslandHeader } from '../components/ui';
import { C, FS, ISLAND, R, SP } from '../lib/design';
import { FormatChip } from '../components/generation/format/FormatChip';
import { FormatBody } from '../components/generation/format/FormatBody';
import { generateChip } from '../lib/chatContext/formatText';
import type { FormatEditModel, FormatModel, FormatTierOption, MediaFormatState } from '../lib/chatContext/formatTypes';
import heroPng from '../assets/hero.png';

// Витрина «Формата» (ADR-025, макет media-format-v1): все состояния свёрнутого чипа из таблицы записки
// и тело всплывашки в разных случаях. Данные — фикстуры, как их отдаёт ручка …/format.

const noop = () => {};
const now = '2026-10-06T12:00:00Z';
const SIZES = { '1:1': { w: 1408, h: 1408 }, '16:9': { w: 1792, h: 1024 }, '9:16': { w: 1024, h: 1792 }, '4:3': { w: 1536, h: 1152 } };

const tiers = (): FormatTierOption[] => [
  { tier: 'standard', label: '1 Мп', size: { w: 1344, h: 768 }, price: { amount: 0.04, unit: 'usd' } },
  { tier: 'high', label: '2 Мп', size: { w: 1792, h: 1024 }, price: { amount: 0.08, unit: 'usd' } },
  { tier: 'max', label: '4 Мп', size: { w: 2688, h: 1536 }, price: { amount: 0.16, unit: 'usd', approx: true } },
];
const GREY = 'Модель рисует один из трёх размеров около 1,6 Мп — «Высокое» и «Макс» у неё недоступны';

const FLUX: MediaFormatState = {
  caps: {
    modelLabel: 'FLUX', aspects: ['1:1', '16:9', '9:16', '4:3'],
    tiers: [{ tier: 'standard', label: '1 Мп', sizes: SIZES }],
    exact: { step: 64, minSide: 256, maxSide: 2048, maxMegapixels: 4 },
  },
  wish: { mode: 'aspect', aspect: '16:9', tier: 'high', by: 'human', at: now },
  resolved: { mode: 'aspect', aspect: '16:9', tier: 'high', width: 1792, height: 1024, substituted: false, rounded: false },
  tiers: tiers(), exact: { step: 64, minSide: 256, maxSide: 2048 },
};
const GPT: MediaFormatState = {
  caps: { modelLabel: 'GPT Image', aspects: ['1:1', '3:2', '2:3'], tiers: [{ tier: 'standard', label: '1 Мп', sizes: SIZES }], nativeNote: 'один из трёх размеров около 1,6 Мп' },
  wish: { mode: 'aspect', aspect: '16:9', tier: 'high', by: 'human', at: now },
  resolved: { mode: 'aspect', aspect: '3:2', tier: 'standard', width: 1536, height: 1024, substituted: true, rounded: false, note: '16:9 нет у GPT Image — взяли 3:2.' },
  tiers: [
    { tier: 'standard', label: '1 Мп', size: { w: 1536, h: 1024 }, price: { amount: 0.04, unit: 'usd' } },
    { tier: 'high', label: 'нет у модели', disabledReason: GREY },
    { tier: 'max', label: 'нет у модели', disabledReason: GREY },
  ],
  exactUnavailableReason: '«Точно…» недоступно: GPT Image рисует только три размера — 1024×1024, 1536×1024 и 1024×1536.',
};
const EXACT: MediaFormatState = {
  ...FLUX,
  wish: { mode: 'exact', width: 1920, height: 1080, by: 'human', at: now },
  resolved: { mode: 'exact', width: 1920, height: 1088, substituted: false, rounded: true, note: 'Модель рисует кратно 64: 1920×1088.' },
};
const NOCAPS: MediaFormatState = { caps: null, wish: null, resolved: { mode: 'model', substituted: false, rounded: false }, tiers: [] };

const gen = (state: MediaFormatState, over: Partial<FormatModel> = {}): FormatModel => ({
  kind: 'generate', title: 'Формат новой картинки', state, by: state.wish?.by ?? null, chip: generateChip(state),
  footer: p => `Запомнится для картинок проекта «${p ?? 'AI Home'}».`, hint: 'Higgsfield не сообщил, какие размеры умеет Soul, — рисуем её размером по умолчанию.',
  onChange: noop, ...over,
});

const source = { width: 1600, height: 1200, aspect: '4:3', thumb: heroPng };
const edit = (over: Partial<FormatEditModel> = {}): FormatEditModel => ({
  source, aspect: null, how: null, anchor: 'center',
  outpaint: { detail: 'ИИ · FLUX.1 Fill · $0.04 · 1600×1600', size: '1600×1600', ready: true },
  crop: { detail: 'Без ИИ · бесплатно · 1200×1200 · новой версией', size: '1200×1200', ready: true },
  onSource: noop, onAspect: noop, onHow: noop, onAnchor: noop, onCancel: noop, cropping: false, ...over,
});
const editModel = (e: FormatEditModel, chip: FormatModel['chip'], over: Partial<FormatModel> = {}): FormatModel => ({
  kind: 'edit', title: 'Формат · кот.png', state: FLUX, by: null, chip, edit: e,
  footer: () => 'Только для этой правки — исходник не меняется, пока не сохраните.', onChange: noop, ...over,
});

const CHIPS: { caption: string; m: FormatModel }[] = [
  { caption: 'Новая картинка', m: gen(FLUX) },
  { caption: 'Точный размер (округлено — жёлтый)', m: gen(EXACT) },
  { caption: 'Правка: как у исходника', m: editModel(edit(), { main: 'Как у исходника', sub: '· 4:3', tone: 'normal' }) },
  { caption: 'Правка → дорисовать', m: editModel(edit({ aspect: '1:1', how: 'outpaint' }), { main: '1:1', sub: '· дорисовать · 1600×1600', subShort: '· дорисовать', tone: 'normal' }) },
  { caption: '«Увеличить» (приглушён)', m: { kind: 'readonly', title: 'Формат · кот.png', state: null, by: null, chip: { main: 'Размер задаёт операция', subShort: '', tone: 'dim' }, readonlyNote: 'У «Увеличить» размер задаёт сама операция. Пропорции не меняются.', footer: () => '', onChange: noop } },
  { caption: 'Видео-сцена (подрезка кадров ⚠)', m: gen(FLUX, { chip: { main: '9:16', sub: '· 720p', tone: 'normal' }, mismatch: 'Кадры A и B — 16:9, сцена — 9:16: кадры будут подрезаны по бокам до модели.' }) },
  { caption: 'Сцена фильма 🔒', m: gen(FLUX, { chip: { main: '16:9', sub: '· 720p', tone: 'normal' }, locked: { filmPath: 'video/утро/film.film', aspect: '16:9' } }) },
  { caption: 'Сцена не в формате фильма', m: gen(FLUX, { chip: { main: '9:16', sub: '· 720p', tone: 'warn' }, mismatch: 'Сцена в 9:16, а фильм «утро-в-горах» — 16:9. При склейке пойдут чёрные полосы.' }) },
  { caption: 'Формат поставил агент ✦', m: gen({ ...FLUX, wish: { ...FLUX.wish!, by: 'agent' } }) },
  { caption: 'Модель не сообщила размеры', m: gen(NOCAPS) },
];

const BODIES: { caption: string; m: FormatModel }[] = [
  { caption: 'Новая картинка: пропорции, ступени с ценой, «Точно…»', m: gen(FLUX) },
  { caption: 'Смена модели: подмена с объяснением, серые ступени, «Точно…» недоступно', m: gen(GPT) },
  { caption: '«Точно…»: округление с пометкой', m: gen(EXACT) },
  { caption: 'Правка: «Как у исходника» и другие пропорции', m: editModel(edit(), { main: 'Как у исходника', sub: '· 4:3', tone: 'normal' }) },
  { caption: 'Правка 4:3 → 1:1: «Дорисовать края» или «Обрезать»', m: editModel(edit({ aspect: '1:1' }), { main: '1:1', sub: '· выберите способ', tone: 'normal' }) },
  { caption: 'Пустое состояние: «Как у модели»', m: gen(NOCAPS) },
  { caption: 'Формат агента ✦ (низ всплывашки)', m: gen({ ...FLUX, wish: { ...FLUX.wish!, by: 'agent' } }) },
];

function Card({ caption, children, px = 400 }: { caption: string; children: React.ReactNode; px?: number }) {
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: SP.xs }}>
      <div style={{ fontSize: FS.sm, color: C.textMuted }}>{caption}</div>
      <div style={{ width: px, maxWidth: '100%', boxSizing: 'border-box', border: `1px solid ${C.border}`, borderRadius: R.lg, background: C.bgCard }}>{children}</div>
    </div>
  );
}

export function FormatKitSection() {
  const [open, setOpen] = useState<string | null>(null);
  return (
    <Island>
      <IslandHeader title="Формат (чип и всплывашка)" />
      <div style={{ display: 'flex', flexDirection: 'column', gap: ISLAND.gap, padding: SP.md }}>
        <div style={{ fontSize: FS.base, fontWeight: 600, color: C.textHeading }}>Свёрнутый чип — полная форма и телефон</div>
        <div style={{ display: 'flex', flexDirection: 'column', gap: SP.sm }}>
          {CHIPS.map(c => (
            <div key={c.caption} style={{ display: 'flex', alignItems: 'center', gap: SP.lg, flexWrap: 'wrap' }}>
              <span style={{ width: 260, fontSize: FS.sm, color: C.textMuted }}>{c.caption}</span>
              <FormatChip m={c.m} short={false} open={open === c.caption} onOpen={() => setOpen(o => (o === c.caption ? null : c.caption))} />
              <FormatChip m={c.m} short open={false} onOpen={noop} />
            </div>
          ))}
        </div>
        <div style={{ fontSize: FS.base, fontWeight: 600, color: C.textHeading }}>Тело всплывашки</div>
        <div style={{ display: 'flex', flexWrap: 'wrap', gap: SP.lg, alignItems: 'flex-start' }}>
          {BODIES.map(b => (
            <Card key={b.caption} caption={b.caption}>
              <FormatBody m={b.m} isMobile={false} projectName="AI Home" onOpenExec={noop} />
            </Card>
          ))}
        </div>
        <div style={{ fontSize: FS.base, fontWeight: 600, color: C.textHeading }}>Телефон (360): то же тело в шторке</div>
        <div style={{ display: 'flex', flexWrap: 'wrap', gap: SP.lg, alignItems: 'flex-start' }}>
          <Card caption="Новая картинка" px={360}><FormatBody m={gen(FLUX)} isMobile projectName="AI Home" /></Card>
          <Card caption="Правка 4:3 → 1:1" px={360}>
            <FormatBody m={editModel(edit({ aspect: '1:1' }), { main: '1:1', tone: 'normal' })} isMobile projectName="AI Home" />
          </Card>
        </div>
      </div>
    </Island>
  );
}
