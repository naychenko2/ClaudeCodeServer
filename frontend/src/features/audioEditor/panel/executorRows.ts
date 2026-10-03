// Строки списка «Исполнитель» панели «Звук» (вариант А, docs/mockups/audio-panel-v3-proposal.md):
// поставщик и модель одним списком — «Авто» сверху, затем «Бесплатно на своей видеокарте» и
// «Облако», цена справа, бейджи RU / лицензия / тяжёлая в строке. Рисует общий ExecutorList;
// здесь только чистые функции под тестом executorRows.test.ts.

import type { ExecutorBadge, ExecutorRow } from 'aihome_shell/kit';
import type { AudioCatalog, AudioModelInfo, AudioOp, AudioProvider, AudioStemSet } from '../api';
import { opInfo } from '../ops';
import { autoOrder } from '../strip/summary';
import { providerUnit, unitLabel, type PanelState, type SettingsPatch } from './model';

export const AUTO_EXECUTOR = 'auto';

// Модели в id бывают со слешами («fal-ai/minimax/speech»): разделитель — «|»
const SEP = '|';
const rowId = (provider: string, model: string) => `${provider}${SEP}${model}`;

const onOwnGpu = (p: AudioProvider) => p.priceUnit === 'free';
const supports = (m: AudioModelInfo, op: AudioOp) => m.caps.ops.includes(op);
const isAutoModel = (id: string | null | undefined, catalog: AudioCatalog) => !id || id === catalog.autoModelId;

// Кого сейчас возьмёт «Авто»: порядок перебора задаёт сервер (с флагом local-media-default
// локальные первыми), модель — первая у поставщика, умеющая операцию
// stemSet — набор «Стемов»: «Авто» берёт только модель, что умеет именно его
export function autoPick(catalog: AudioCatalog, op: AudioOp, stemSet: AudioStemSet | null = null): { provider: AudioProvider; model: AudioModelInfo | null } | null {
  const fits = (m: AudioModelInfo) => supports(m, op) && (!stemSet || m.caps.stemSet === stemSet);
  const provider = autoOrder(catalog).find(p => p.available && p.models.some(fits));
  return provider ? { provider, model: provider.models.find(fits) ?? null } : null;
}

// «локально · Qwen3-TTS», «fal · MiniMax Speech»
const where = (p: AudioProvider, m: AudioModelInfo | null) => [onOwnGpu(p) ? 'локально' : p.label, m?.label].filter((x): x is string => !!x);

// Бесплатна ли строка: флагом для окраски чипа, подпись цены остаётся в rowPrice
export const rowFree = (p: AudioProvider, m: AudioModelInfo | null) => p.priceUnit === 'free' || m?.caps.priceUnit === 'free';

export function rowPrice(p: AudioProvider, m: AudioModelInfo | null): string {
  return unitLabel(p, m) ?? providerUnit(p);
}

function badges(m: AudioModelInfo, op: AudioOp): ExecutorBadge[] {
  const out: ExecutorBadge[] = [];
  if (!m.caps.languageNeutral && m.caps.languages.length) {
    out.push(m.caps.languages.includes('ru') ? { label: 'RU', tone: 'success' } : { label: 'без RU' });
  }
  // Свободная лицензия ничего не меняет для человека — в строке только та, что ограничивает
  const lic = m.caps.license;
  if (lic && lic.kind !== 'permissive' && lic.label) out.push({ label: lic.label });
  if (m.caps.heavyOps?.includes(op)) out.push({ label: 'тяжёлая', tone: 'warning' });
  return out;
}

// У fal в каталоге не весь его зоопарк, а отобранные модели: подпись встаёт у первой строки fal
export const FAL_NOTE = 'отобранные · остальные — по запросу';

// Строки списка: «Авто», затем свои видеокарты, затем облако; внутри — порядок каталога.
// Поставщик без модели под операцию в список не попадает; недоступный — серый с причиной.
// В личном чате «Локально» закрыт самим местом — строка с замком, причина остаётся
export function executorRows(catalog: AudioCatalog, op: AudioOp, personal = false, stemSet: AudioStemSet | null = null): ExecutorRow[] {
  const auto = autoPick(catalog, op, stemSet);
  const rows: ExecutorRow[] = [{
    id: AUTO_EXECUTOR, group: 'auto', name: 'Авто',
    sub: auto ? `сейчас: ${where(auto.provider, auto.model).join(' · ')}` : 'сейчас некому — нет поставщика с этой операцией',
    ...(auto ? { now: where(auto.provider, auto.model).join(' · ') } : null),
    price: auto ? rowPrice(auto.provider, auto.model) : '—',
    free: !!auto && rowFree(auto.provider, auto.model),
    ...(auto ? null : { disabled: true, reason: 'Нет доступного поставщика для этой операции' }),
  }];
  const providers = catalog.providers;
  const ordered = [...providers.filter(onOwnGpu), ...providers.filter(p => !onOwnGpu(p))];
  for (const p of ordered) {
    const why = p.available ? null : p.reason ?? 'Поставщик сейчас недоступен';
    const locked = personal && p.key === 'local';
    p.models.filter(x => supports(x, op) && (!stemSet || x.caps.stemSet === stemSet)).forEach((m, i) => {
      const b = badges(m, op);
      rows.push({
        id: rowId(p.key, m.id),
        group: onOwnGpu(p) ? 'local' : 'cloud',
        name: m.label,
        sub: onOwnGpu(p) ? undefined : p.key === 'fal' && i === 0 ? `${p.label} · ${FAL_NOTE}` : p.label,
        price: rowPrice(p, m),
        free: rowFree(p, m),
        ...(b.length ? { badges: b } : null),
        ...(why ? { disabled: true, reason: why } : null),
        ...(locked ? { locked: true } : null),
      });
    });
  }
  return rows;
}

// Что подсвечено: «Авто» — только без явного поставщика и модели. Явный поставщик с моделью
// «Авто» подсвечивает модель, которой реально пойдёт запуск
export function executorValue(catalog: AudioCatalog, state: Pick<PanelState, 'providerKey' | 'modelId' | 'provider' | 'model'>): string {
  const autoModel = isAutoModel(state.modelId, catalog);
  if (!state.providerKey && autoModel) return AUTO_EXECUTOR;
  const pk = state.providerKey ?? state.provider?.key;
  const mid = autoModel ? state.model?.id : state.modelId;
  return pk && mid ? rowId(pk, mid) : AUTO_EXECUTOR;
}

// Выбор строки → правка настроек: поставщик и модель вместе
export function executorPatch(id: string): SettingsPatch {
  if (id === AUTO_EXECUTOR) return { provider: null, model: null };
  const i = id.indexOf(SEP);
  return { provider: id.slice(0, i), model: id.slice(i + 1) };
}

// Свёрнутая строка «Чем: **Авто** · локально · Qwen3-TTS»: у «Авто» — кого он взял сейчас
export function executorSummary(
  catalog: AudioCatalog, state: Pick<PanelState, 'providerKey' | 'modelId' | 'provider' | 'model' | 'op'>,
): { name: string; parts: string[] } {
  const p = state.provider;
  if (!state.providerKey && isAutoModel(state.modelId, catalog)) {
    return { name: 'Авто', parts: p ? where(p, state.model) : ['нет поставщика для «' + (opInfo(state.op)?.label ?? state.op) + '»'] };
  }
  if (!p) return { name: state.providerKey ?? 'Авто', parts: ['поставщика нет в каталоге'] };
  if (isAutoModel(state.modelId, catalog)) return { name: `${p.label} · Авто`, parts: state.model ? [state.model.label] : [] };
  const name = state.model?.label ?? state.modelId;
  return onOwnGpu(p) ? { name, parts: ['локально'] } : { name: `${p.label} · ${name}`, parts: [] };
}
