// «Макс» у local MiniMax H3 (ADR-025, решение 1 Андрея): съёмка 1344×768, затем латентное увеличение до 1440p —
// две задачи GPU. Чистые функции карточки съёмки: стадии «Снимаем» и «Увеличиваем», пометка «увеличение не
// удалось». Тексты — из записки макета (сценарий 10). Размеры и подписи берутся из каталога модели, своих нет.

import type { VideoCatalog, VideoClipVersion } from '../api';
import { etaText } from '../context/format';
import { findModel, findProvider } from '../scene/model';
import type { JobProgress } from '../store/videoStore';

export interface UpscaleInfo { shotLabel: string; shotSize: string; targetLabel: string; targetSize: string }

const size = (s: { w: number; h: number } | undefined) => (s ? `${s.w}×${s.h}` : '');

// Ступень модели с увеличением следом и ступень, которую снимает первый прогон (тот же wireValue без PostStep)
export function upscaleInfo(catalog: VideoCatalog | null, providerKey: string | undefined, modelId: string | undefined, aspect: string | undefined): UpscaleInfo | null {
  const caps = findModel(findProvider(catalog, providerKey), modelId)?.format;
  const max = caps?.tiers.find(t => t.postStep === 'upscale');
  if (!caps || !max) return null;
  const shot = caps.tiers.find(t => !t.postStep && t.wireValue === max.wireValue);
  const a = aspect && max.sizes[aspect] ? aspect : caps.aspects[0] ?? '';
  return { shotLabel: shot?.label ?? '', shotSize: size(shot?.sizes[a]), targetLabel: max.label, targetSize: size(max.sizes[a]) };
}

export interface CardStage {
  n: 1 | 2;
  text: string;
  badge?: string;
  state: 'now' | 'wait' | 'done';
  // Что отменяет кнопка в строке: весь запуск или только увеличение (съёмка 768p останется версией)
  cancel?: 'job' | 'upscale';
}

// Две строки хода: идёт съёмка с увеличением следом, либо идёт само увеличение
export function stagesOf(u: UpscaleInfo | null, job: JobProgress | null): CardStage[] | null {
  if (!u || !job) return null;
  if (job.stage === 'upscaling') {
    return [
      { n: 1, text: `Съёмка · ${u.shotSize}`, badge: 'готово', state: 'done' },
      { n: 2, text: `Увеличиваем до ${u.targetLabel}…`, ...badge('ещё', job.etaSeconds), state: 'now', cancel: 'upscale' },
    ];
  }
  if (job.upscaleEtaSeconds === null) return null;
  return [
    { n: 1, text: `Снимаем · ${u.shotSize}…`, ...badge('', job.etaSeconds), state: job.stage === 'queued' ? 'wait' : 'now', cancel: 'job' },
    { n: 2, text: `Увеличение до ${u.targetLabel} · ${u.targetSize}`, ...badge('следом ·', job.upscaleEtaSeconds), state: 'wait' },
  ];
}

const badge = (prefix: string, sec: number | null | undefined) => {
  const t = etaText(sec);
  const text = [prefix, t].filter(Boolean).join(' ');
  return text ? { badge: text } : {};
};

// «Увеличение до 1440p не удалось. Съёмка 768p сохранена версией 1 — в 1440p её нет.»
export function upscaleFailText(u: UpscaleInfo, version: Pick<VideoClipVersion, 'number'>): string {
  return `Увеличение до ${u.targetLabel} не удалось. Съёмка ${u.shotLabel} сохранена версией ${version.number} — в ${u.targetLabel} её нет.`;
}

// «Версия 1 · 1440p · 2528×1440» под превью готового увеличенного клипа
export function upscaledCaption(u: UpscaleInfo | null, v: Pick<VideoClipVersion, 'number' | 'width' | 'height' | 'upscaleError'>): string | null {
  if (!u || v.upscaleError || !v.width || !v.height || `${v.width}×${v.height}` !== u.targetSize) return null;
  return `Версия ${v.number} · ${u.targetLabel} · ${u.targetSize}`;
}
