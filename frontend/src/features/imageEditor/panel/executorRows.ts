// Строки списка «Исполнитель» панели «Картинки» v5 (флаг image-panel-v5, макет
// image-panel-v5.html): «Авто» сверху, затем «Бесплатно на своей видеокарте» и «Облако»,
// цена справа. Модель, которая не возьмёт текущую задачу, серая с причиной modelBlockReason.
// Рисует общий ExecutorList; здесь только чистые функции под тестом executorRows.test.ts.

import type { ExecutorRow } from 'aihome_shell/kit';
import { AUTO_MODEL, type ImageEditCatalog, type ImageEditModel, type ImageEditOp, type ImageEditProvider } from '../api';
import { effectiveProvider, isFreeUnit, modelBlockReason, unavailableMark } from '../format';
import { quickAvailability, type QuickAction } from '../editorInputs';
import type { ImageThreadSettings } from '../thread/threadsApi';

export const AUTO_EXECUTOR = 'auto';

// Модели в id бывают со слешами («fal-ai/flux-pro/kontext»): разделитель — «|»
const SEP = '|';
const rowId = (provider: string, model: string) => `${provider}${SEP}${model}`;

// Что сейчас выбрано: без поставщика и модели — «Авто» (как в настройках администратора)
export function executorValue(catalog: ImageEditCatalog, s: Pick<ImageThreadSettings, 'provider' | 'model'>): string {
  if (!s.provider && !s.model) return AUTO_EXECUTOR;
  const pv = effectiveProvider(catalog, s.provider ?? 'settings');
  if (!pv) return AUTO_EXECUTOR;
  return rowId(pv.key, s.model ?? (pv.key === catalog.default.provider ? catalog.default.model : AUTO_MODEL));
}

// Выбор строки → поля настроек
export function executorSettings(id: string): Pick<ImageThreadSettings, 'provider' | 'model'> {
  if (id === AUTO_EXECUTOR) return { provider: null, model: null };
  const i = id.indexOf(SEP);
  return { provider: id.slice(0, i), model: id.slice(i + 1) };
}

const isLocal = (pv: ImageEditProvider) => isFreeUnit(pv.priceUnit);

// Цена строки: «бесплатно», «$0.04 / шт.», «2 кр. / шт.»; без ориентира — единица поставщика
export function rowPrice(pv: ImageEditProvider, m: ImageEditModel | null): string {
  if (isLocal(pv)) return 'бесплатно';
  const h = m?.priceHint;
  if (h && !isFreeUnit(h.unit)) {
    return h.unit === 'usd' ? `$${h.amount.toFixed(2)} / шт.` : `${Math.round(h.amount * 100) / 100} кр. / шт.`;
  }
  if (h) return 'бесплатно';
  return pv.priceUnit === 'usd' ? '$ за картинку' : pv.priceUnit === 'credits' ? 'кредиты' : '';
}

const rowName = (pv: ImageEditProvider, m: ImageEditModel) =>
  m.id === AUTO_MODEL ? `${pv.label} · Авто` : isLocal(pv) ? m.label : `${pv.label} · ${m.label}`;

// Задача, под которую серим модели: операция запуска, есть ли картинка и маска; quick —
// операция без промпта (её ведёт модель поставщика, которая умеет, а не выбранная)
export interface ExecutorTask { op: ImageEditOp; hasImage: boolean; hasMask: boolean; quick: QuickAction | null }

function blockReason(catalog: ImageEditCatalog, pv: ImageEditProvider, m: ImageEditModel, t: ExecutorTask): string {
  if (t.quick) {
    const q = quickAvailability(t.quick, catalog, pv.key, m.id, 1);
    return q.route ? '' : q.reason;
  }
  const fromScratch = t.op === 'generate';
  return modelBlockReason(m, !fromScratch && t.hasImage, t.op === 'inpaint' && t.hasMask, t.op);
}

// «сейчас fal», «сейчас локально · Qwen-Image 2.1»
function autoNow(catalog: ImageEditCatalog): string {
  const pv = catalog.providers.find(p => p.key === catalog.default.provider);
  if (!pv) return '';
  const m = catalog.default.model !== AUTO_MODEL ? pv.models.find(x => x.id === catalog.default.model) : null;
  return [isLocal(pv) ? 'локально' : pv.label, m?.label].filter(Boolean).join(' · ');
}

export function executorRows(catalog: ImageEditCatalog, task: ExecutorTask): ExecutorRow[] {
  const admin = catalog.providers.find(p => p.key === catalog.default.provider) ?? null;
  const adminModel = admin?.models.find(m => m.id === catalog.default.model) ?? null;
  const rows: ExecutorRow[] = [];
  if (admin) {
    const now = autoNow(catalog);
    rows.push({ id: AUTO_EXECUTOR, group: 'auto', name: 'Авто', sub: now ? `как в настройках · сейчас ${now}` : 'как в настройках', price: rowPrice(admin, adminModel) });
  }
  // Сначала своя видеокарта, потом облако; внутри — порядок каталога
  const ordered = [...catalog.providers.filter(isLocal), ...catalog.providers.filter(p => !isLocal(p))];
  for (const pv of ordered) {
    const down = unavailableMark(pv);
    for (const m of pv.models) {
      const why = blockReason(catalog, pv, m, task);
      rows.push({
        id: rowId(pv.key, m.id),
        group: isLocal(pv) ? 'local' : 'cloud',
        name: rowName(pv, m),
        sub: m.id === AUTO_MODEL ? 'подберём модель под задачу' : undefined,
        price: rowPrice(pv, m.id === AUTO_MODEL ? null : m),
        ...(down ? { badges: [{ label: down, tone: 'warning' as const }] } : null),
        ...(why ? { disabled: true, reason: why } : null),
      });
    }
  }
  return rows;
}

// Свёрнутая строка «Чем: **Авто** · локально · Qwen-Image Edit»; done — модель, которой
// реально пойдёт запуск (у операции без промпта — модель её маршрута)
export function executorSummary(
  catalog: ImageEditCatalog, s: Pick<ImageThreadSettings, 'provider' | 'model'>, done: { provider: string; model: string } | null,
): { name: string; parts: string[] } {
  const value = executorValue(catalog, s);
  const dpv = done ? catalog.providers.find(p => p.key === done.provider) ?? null : null;
  const dm = dpv && done && done.model !== AUTO_MODEL ? dpv.models.find(m => m.id === done.model) ?? null : null;
  if (value === AUTO_EXECUTOR) {
    const now = dpv ? [isLocal(dpv) ? 'локально' : dpv.label, dm?.label] : [autoNow(catalog)];
    return { name: 'Авто', parts: now.filter((x): x is string => !!x) };
  }
  const { provider, model } = executorSettings(value);
  const pv = catalog.providers.find(p => p.key === provider);
  const m = pv?.models.find(x => x.id === model);
  if (!pv || !m) return { name: 'Авто', parts: [] };
  const name = rowName(pv, m);
  // Выбрана одна модель, а пойдёт другая (операцию без промпта ведёт умеющая)
  const other = dm && dm.id !== m.id ? `сделает ${dm.label}` : null;
  return { name, parts: [isLocal(pv) && m.id !== AUTO_MODEL ? 'локально' : null, other].filter((x): x is string => !!x) };
}
