// Синхронизация редактора с состоянием чата картинки на сервере (ADR-018 §2, «Как ход меняет
// открытый редактор»): что редактор пишет в PUT state, что применяет из image_chat_state агента
// и как называет изменения в строке «Изменил: …». Здесь только чистые функции — их гоняет vitest.

import type {
  EditMode, ImageChatReference, ImageChatState, ImageChatStateChange, ImageEditCatalog, ImageEditInitiator,
} from '../api';
import { AUTO_MODEL } from '../api';
import { effectiveProvider, type ProviderChoice } from '../format';

// Поля состояния, которыми владеет редактор. Журнал, ревизия и lastSentRevision — не его
export interface EditorSettings {
  prompt: string;
  promptAuthor: ImageEditInitiator;
  provider: ProviderChoice;
  model: string;
  count: number;
  references: ImageChatReference[];
  characterSlug: string | null;
  matchSourceSize: boolean;
  marks: unknown;
  canvasRevision: string | null;
  currentStepId: string | null;
}

// «Как в настройках» на сервере — null: там поставщик по умолчанию берётся из настройки места
export const providerToState = (p: ProviderChoice): string | null => (p === 'settings' ? null : p);
export const providerFromState = (p: string | null | undefined): ProviderChoice => p || 'settings';

export function settingsToState(base: ImageChatState, s: EditorSettings, mode: EditMode = 'auto'): ImageChatState {
  return {
    ...base,
    prompt: s.prompt,
    promptAuthor: s.promptAuthor,
    provider: providerToState(s.provider),
    model: s.model,
    mode,
    count: s.count,
    references: s.references,
    characterSlug: s.characterSlug,
    marks: s.marks ?? null,
    canvasRevision: s.canvasRevision,
    currentStepId: s.currentStepId,
    matchSourceSize: s.matchSourceSize,
  };
}

// Отпечаток полей редактора: пишем на сервер, только если он разошёлся с последним известным
export function settingsKey(s: ImageChatState): string {
  return JSON.stringify([
    s.prompt, s.promptAuthor, s.provider ?? null, s.model ?? null, s.count, s.references, s.characterSlug ?? null,
    s.marks ?? null, s.canvasRevision ?? null, s.currentStepId ?? null, s.matchSourceSize,
  ]);
}

// Настройки, которые кроме редактора пишет агент. Холст (пометки, ревизия, шаг) — только
// редактора: при конфликте в нём прав тот, у кого он открыт
const REMOTE_FIELDS = ['prompt', 'promptAuthor', 'provider', 'model', 'count', 'references', 'characterSlug', 'matchSourceSize'] as const;

// Что поменялось на сервере между известным редактору состоянием и свежим (409)
export function remoteChanges(known: ImageChatState, fresh: ImageChatState): ImageChatStateChange[] {
  return REMOTE_FIELDS
    .filter(f => JSON.stringify(known[f] ?? null) !== JSON.stringify(fresh[f] ?? null))
    .map(f => ({ field: f, from: known[f], to: fresh[f] }));
}

// Своё поверх свежего: чужие правки полей берём с сервера, остальное — из редактора
export function mergeRemote(s: EditorSettings, fresh: ImageChatState, changes: ImageChatStateChange[]): EditorSettings {
  const next = { ...s };
  for (const c of changes) {
    switch (c.field) {
      case 'prompt': next.prompt = fresh.prompt; break;
      case 'promptAuthor': next.promptAuthor = fresh.promptAuthor; break;
      case 'provider': next.provider = providerFromState(fresh.provider); break;
      case 'model': next.model = fresh.model || AUTO_MODEL; break;
      case 'count': next.count = fresh.count; break;
      case 'references': next.references = fresh.references; break;
      case 'characterSlug': next.characterSlug = fresh.characterSlug; break;
      case 'matchSourceSize': next.matchSourceSize = fresh.matchSourceSize; break;
    }
  }
  return next;
}

// Какие настройки поменял агент — по ним метки «✦ … Claude» и рамка у «Вариантов».
// Ручная правка поля снимает его метку
export interface AgentMarks { prompt: boolean; model: boolean; count: boolean }
export const NO_AGENT_MARKS: AgentMarks = { prompt: false, model: false, count: false };

export interface AgentApplied {
  // Что поставить в редактор; поля без изменений не трогаем
  patch: Partial<Pick<EditorSettings, 'prompt' | 'promptAuthor' | 'provider' | 'model' | 'count' | 'references' | 'characterSlug' | 'matchSourceSize'>>;
  marks: AgentMarks;
  // Текст человека, который агент заменил: редактор держит его черновиком («Вернуть мой текст»)
  draft: string | null;
}

// Применение image_chat_state от агента к открытому редактору. Решение Софьи по ревью Глеба:
// метка появляется, если агент поменял хоть что-то, — промпт или любую настройку
export function applyAgentChanges(
  local: { prompt: string; promptAuthor: ImageEditInitiator },
  marks: AgentMarks,
  state: ImageChatState,
  changes: ImageChatStateChange[],
): AgentApplied {
  const fields = new Set(changes.map(c => c.field));
  const patch: AgentApplied['patch'] = {};
  const next = { ...marks };
  let draft: string | null = null;
  if (fields.has('prompt')) {
    patch.prompt = state.prompt;
    patch.promptAuthor = 'agent';
    next.prompt = true;
    if (local.promptAuthor === 'human' && local.prompt.trim() && local.prompt !== state.prompt) draft = local.prompt;
  }
  if (fields.has('provider') || fields.has('model')) {
    patch.provider = providerFromState(state.provider);
    patch.model = state.model || AUTO_MODEL;
    next.model = true;
  }
  if (fields.has('count')) {
    patch.count = state.count;
    next.count = true;
  }
  if (fields.has('references')) patch.references = state.references;
  if (fields.has('characterSlug')) patch.characterSlug = state.characterSlug;
  if (fields.has('matchSourceSize')) patch.matchSourceSize = state.matchSourceSize;
  return { patch, marks: next, draft };
}

// Подписи поставщика и модели по каталогу; каталога нет — сырые ключи
export function providerLabel(catalog: ImageEditCatalog | null, key: string | null | undefined): string {
  if (!catalog) return key || 'как в настройках';
  return effectiveProvider(catalog, providerFromState(key))?.label ?? key ?? '';
}

export function modelLabel(catalog: ImageEditCatalog | null, provider: string | null | undefined, model: string | null | undefined): string {
  const id = model || AUTO_MODEL;
  const pv = catalog ? effectiveProvider(catalog, providerFromState(provider)) : null;
  const found = pv?.models.find(m => m.id === id);
  if (found) return found.label;
  return id === AUTO_MODEL ? 'Авто' : id;
}

const MODE_LABEL: Record<string, string> = { auto: 'авто', fast: 'быстро', precise: 'точно', photoreal: 'фотореализм' };

// «промпт, модель → FLUX Fill, вариантов: 2» из changes[] результата image_generate
export function describeChanges(changes: ImageChatStateChange[], catalog: ImageEditCatalog | null): string[] {
  const byField = new Map(changes.map(c => [c.field, c]));
  const parts: string[] = [];
  const str = (v: unknown) => (typeof v === 'string' ? v : null);
  if (byField.has('prompt')) parts.push('промпт');
  const pv = byField.get('provider');
  if (pv && providerLabel(catalog, str(pv.from)) !== providerLabel(catalog, str(pv.to))) {
    parts.push(`поставщик → ${providerLabel(catalog, str(pv.to))}`);
  }
  const md = byField.get('model');
  if (md) {
    const provider = str(pv?.to) ?? null;
    parts.push(`модель → ${modelLabel(catalog, provider, str(md.to))}`);
  }
  const ct = byField.get('count');
  if (ct && typeof ct.to === 'number') parts.push(`вариантов: ${ct.to}`);
  const mode = byField.get('mode');
  if (mode && typeof mode.to === 'string') parts.push(`режим → ${MODE_LABEL[mode.to] ?? mode.to}`);
  const refs = byField.get('references');
  if (refs) parts.push(Array.isArray(refs.to) && refs.to.length ? `образцы: ${refs.to.length}` : 'образцы убраны');
  const ch = byField.get('characterSlug');
  if (ch) parts.push(str(ch.to) ? `персонаж → ${str(ch.to)}` : 'персонаж отключён');
  const size = byField.get('matchSourceSize');
  if (size) parts.push(size.to ? 'вернуть размер оригинала' : 'без возврата размера');
  return parts;
}

// Строка карточки: «Изменил: …» от лица Claude, у персоны — безличное «Изменено: …»
// (рода персоны продукт не знает)
export function changedLine(parts: string[], persona: boolean): string | null {
  if (!parts.length) return null;
  return `${persona ? 'Изменено' : 'Изменил'}: ${parts.join(', ')}`;
}
