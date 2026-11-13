// Группа секций промпта формы персоны «как у специальности / своё / выключено»
// (разрез docs/research/ai-fabrika-workflow-cut-2026-11.md §2 «Форма персоны», шаг P2a).
//
// ЧИСТЫЙ модуль: ни DOM, ни запросов, ни стора — читает эффективное состояние, которое
// принёс сервер (GET /api/personas/{id}/prompt-sections — единственная точка наложения:
// PromptSectionsContributor.EffectiveSectionsFor → PromptSectionOverlay.Apply), и готовит
// состояние формы (P2b) и фрагмент записи (PUT promptSections). Фронт наложение НЕ
// повторяет: порядок слоёв code → global → user → owner → persona здесь не резолвится —
// тот же урок, что у A3a (personaInheritModel).

import type { EffectivePromptSection } from '../../lib/specialties';
import type { PersonaPromptSectionOverride } from './types';

// Элемент ответа сервера — тот же тип, что состояние specialty-слоёв (бэковый
// EffectivePromptSection единый для всех чтений). Источник 'persona' бывает только здесь:
// собственный резолв specialty-слоёв (lib/specialties.effectivePromptSection) его не
// производит, и своего слоя фронт не держит — только локальные переходы ниже.
// updatedFromRole (B09) — секция унаследована и её текст обновился правкой админа.
export type PersonaPromptSection = EffectivePromptSection & { updatedFromRole?: boolean };

// ── Режим секции ───────────────────────────────────────────────────────────────────────

export type SectionMode = 'inherit' | 'own' | 'off';

// Режим секции по её эффективному состоянию:
// - 'own' — текст свой (textSource='persona'); сильнее 'off': текст — то, что задала персона;
// - 'off' — выключена слоем самой персоны (enabled=false, enabledSource='persona');
// - 'inherit' — всё остальное. Секция, выключенная СПЕЦИАЛЬНОСТЬЮ (enabled=false,
//   enabledSource='code' или слой настроек), — не «off» персоны: режим 'inherit',
//   признак читается отдельно, через offBySpecialty (§2 «Форма персоны»).
export function sectionMode(section: PersonaPromptSection): SectionMode {
  if (section.textSource === 'persona') return 'own';
  if (!section.enabled && section.enabledSource === 'persona') return 'off';
  return 'inherit';
}

// Режимы всех секций по состоянию сервера (ключ — id) — что показать в строках до
// того, как человек что-то тронул.
export function sectionModes(sections: readonly PersonaPromptSection[]): Record<string, SectionMode> {
  const out: Record<string, SectionMode> = {};
  for (const s of sections) out[s.id] = sectionMode(s);
  return out;
}

// «Выключено специальностью»: секция выключена не волей персоны. Это НЕ её 'off':
// строка остаётся 'inherit' с отдельным признаком (UI показывает его приглушённо).
export function offBySpecialty(section: PersonaPromptSection): boolean {
  return !section.enabled && section.enabledSource !== 'persona';
}

// ── Состояние группы в форме ───────────────────────────────────────────────────────────

// Целое состояние группы: эффективное состояние сервера + локальные переходы человека
// с момента загрузки. Секции нет в overrides — она как с сервера. Меняют состояние
// только функции этого модуля — каждая возвращает новое, прежнее не трогает.
export interface PromptSectionsState {
  // Чьё это состояние: несоответствие id в позднем ответе — ответ отбрасывается
  // (applySections), состояние остаётся как было.
  personaId: string;
  // Эффективное состояние с сервера (порядок и состав — бэковые).
  effective: PersonaPromptSection[];
  // Локальные переходы: {mode:'own', text} — черновик своего текста; {mode:'off'} — выключить;
  // {mode:'inherit'} — явно «как у специальности»: для own/off-секции с сервера это отметка на
  // удаление существующего override (его режим без отметки читался бы с сервера и
  // переписывался сам собой).
  overrides: Record<string, { mode: 'own'; text: string } | { mode: 'off' } | { mode: 'inherit' }>;
}

// Пустое состояние персоны — до прихода секций (запрос начинается с этим id, и его же
// передают applySections; иначе первый ответ был бы отвергнут как «чужой»).
export function emptyState(personaId: string): PromptSectionsState {
  return { personaId, effective: [], overrides: {} };
}

// Режим строки формы: локальный переход сильнее, иначе — серверный.
export function modeOf(state: PromptSectionsState, id: string): SectionMode {
  const local = state.overrides[id];
  if (local) return local.mode;
  const section = state.effective.find(s => s.id === id);
  return section ? sectionMode(section) : 'inherit';
}

// Переход в «своё»: текущий эффективный текст становится черновиком. Работает и для
// секции, выключенной специальностью: сервер отдаёт полный каталог, базовый текст
// выключенной секции в ответе есть. Секции нет в ответе (id не из каталога) — без изменений.
export function startOwn(state: PromptSectionsState, id: string): PromptSectionsState {
  const section = state.effective.find(s => s.id === id);
  if (!section) return state;
  return { ...state, overrides: { ...state.overrides, [id]: { mode: 'own', text: section.text } } };
}

// Правка своего черновика (textarea формы).
export function setOwnText(state: PromptSectionsState, id: string, text: string): PromptSectionsState {
  return { ...state, overrides: { ...state.overrides, [id]: { mode: 'own', text } } };
}

// Явное «выключено» персоной.
export function setOff(state: PromptSectionsState, id: string): PromptSectionsState {
  return { ...state, overrides: { ...state.overrides, [id]: { mode: 'off' } } };
}

// Возврат «как у специальности»: отметить секцию явно унаследованной. Для own/off-секции
// с сервера это удаление override: toOverrides не выдаёт такой id, и целая замена списка
// без него снесёт запись (семантика PUT, PersonasController.cs:2306). Для унаследованной
// — ничего не меняет в записи.
export function backToInherit(state: PromptSectionsState, id: string): PromptSectionsState {
  return { ...state, overrides: { ...state.overrides, [id]: { mode: 'inherit' } } };
}

// ── Фрагмент записи (PUT promptSections) ───────────────────────────────────────────────

// Только отличия, список целиком:
// - 'inherit' → ничего (своего значения нет);
// - 'off' → { id, enabled: false };
// - 'own' с текстом, равным тексту специальности → ничего: override, повторяющий
//   унаследованное значение, бессмысленен и заморозит его (перестанет следовать за
//   специальностью при её следующей правке). Текст специальности известен, только когда
//   его не заменила персона (textSource ≠ 'persona') — слои фронт не резолвит.
// Секция, уже своя на сервере, выдаётся как есть: отсутствие в целой замене молча
// снесло бы существующий override.
// Пустой после trim черновик — не свой текст (та же читка, что у бэка,
// PromptSectionOverlay.Apply:37): override не выдаётся.
export function toOverrides(
  state: PromptSectionsState,
  effective?: readonly PersonaPromptSection[],
): PersonaPromptSectionOverride[] {
  const eff = effective ?? state.effective;
  const out: PersonaPromptSectionOverride[] = [];
  for (const section of eff) {
    const local = state.overrides[section.id];
    const mode = local ? local.mode : sectionMode(section);
    if (mode === 'inherit') continue;
    if (mode === 'off') {
      out.push({ id: section.id, enabled: false });
      continue;
    }
    // 'own': локальный черновик или (для уже своей на сервере, без локального перехода)
    // её серверный текст — он и есть то, что показать и что сохранить.
    const draft = (local && local.mode === 'own' ? local.text : section.text).trim();
    if (!draft) continue;
    if (section.textSource !== 'persona' && draft === section.text) continue;
    out.push({ id: section.id, text: draft });
  }
  return out;
}

// ── Счётчик длины ──────────────────────────────────────────────────────────────────────

// Потолок текста секции — общий с бэком (SpecialtyPromptPresets.SectionTextLimit);
// та же константа живёт у экрана специальностей (RolePresetsBlock).
export const TEXT_LIMIT = 1024;

// Счётчик для UI: 'N / 1024'.
export function textCounter(text: string): string {
  return `${text.length} / ${TEXT_LIMIT}`;
}

export function overLimit(text: string): boolean {
  return text.length > TEXT_LIMIT;
}

// Жёлтый счётчик: от 90 % лимита, пока лимит ещё не превышен.
export function nearLimit(text: string): boolean {
  return !overLimit(text) && text.length >= TEXT_LIMIT * 0.9;
}

// Есть ли среди черновиков «своего» текст длиннее лимита — сохранение блокируется.
export function hasOverLimit(state: PromptSectionsState): boolean {
  return Object.values(state.overrides).some(o => o.mode === 'own' && overLimit(o.text));
}

// ── Подписи слоёв ──────────────────────────────────────────────────────────────────────

const LAYER_LABEL: Record<PersonaPromptSection['textSource'], string> = {
  code: 'код', global: 'глобально', user: 'пользователь', owner: 'владелец', persona: 'персона',
};

export function layerLabel(source: PersonaPromptSection['textSource']): string {
  return LAYER_LABEL[source];
}

// Подпись источника у заголовка секции (макет P38–P55): режим 'own' и 'off' персоны — свои слои,
// «выключено специальностью» называет слой, который выключил; иначе слой текста.
export function sectionCaption(section: PersonaPromptSection, mode: SectionMode): string | null {
  if (mode === 'own') return `своё · ${layerLabel('persona')}`;
  if (mode === 'off') return null;
  if (offBySpecialty(section)) return `выключено специальностью · ${layerLabel(section.enabledSource)}`;
  return section.textSource === 'code'
    ? `по умолчанию · ${layerLabel('code')}`
    : `настроено админом · ${layerLabel(section.textSource)}`;
}

// ── Загрузка ответа ─────────────────────────────────────────────────────────────────────

// Применить ответ GET prompt-sections — только для той персоны, чьё состояние сейчас:
// устаревший ответ (персону сменили, пока запрос шёл) отбрасывается целиком, строки не
// перекрашиваются чужими секциями. Отказ запроса состояние не меняет: форма просто не
// зовёт эту функцию. Локальные черновики сбрасываются: загрузка даёт чистое состояние.
// Массив ответа копируется — состояние не делит его с результатом запроса.
export function applySections(
  state: PromptSectionsState,
  requestPersonaId: string,
  response: readonly PersonaPromptSection[],
): PromptSectionsState {
  if (state.personaId !== requestPersonaId) return state;
  return { personaId: requestPersonaId, effective: [...response], overrides: {} };
}
