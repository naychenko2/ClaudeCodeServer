// Записи модуля «Плагины» в ленте чата (шаг 4.5b, ADR-031): module_record
// { module: "plugins", recordType: "plugin_draft", data: { slug }, fallback }.
// Ядро отдаёт её в слот chat-item-tool по ключу `plugins:plugin_draft`; тихие строки
// (нет slug / флаг выключен) модуль рисует из fallback — текст готовит сервер.

import { C, FS } from '../../../lib/design';
import type { ChatItem } from '../../../types';
import type { ChatItemToolCtx } from '../../../lib/subsystems/registryCore';
import { DraftFeedCard } from './DraftFeedCard';

export const MODULE = 'plugins';
export const recordKey = (recordType: string) => `${MODULE}:${recordType}`;

export const RECORD_DRAFT = 'plugin_draft';

export interface ModuleRecordView { recordType: string; data: Record<string, unknown>; fallback: string | null }

// Запись ленты как module_record; другие виды записей — null
export function recordOf(item: ChatItem): ModuleRecordView | null {
  const r = item as unknown as { kind?: unknown; recordType?: unknown; data?: unknown; fallback?: unknown };
  if (r.kind !== 'module_record' || typeof r.recordType !== 'string') return null;
  return {
    recordType: r.recordType,
    data: r.data && typeof r.data === 'object' ? r.data as Record<string, unknown> : {},
    fallback: typeof r.fallback === 'string' ? r.fallback : null,
  };
}

// Нейтральная строка (нет slug в данных записи) — как тихие строки соседних модулей
function FallbackLine({ text }: { text: string }) {
  return (
    <div style={{
      alignSelf: 'center', maxWidth: 420, textAlign: 'center', overflowWrap: 'anywhere',
      fontSize: FS.xs, color: C.textMuted, lineHeight: 1.45,
    }}>{text}</div>
  );
}

// Slug из ленты — недоверенный ввод: ГОЛЫЙ slug черновика (без publisher), грамматика
// PluginId.SlugPattern `^[a-z0-9-]+$` (бэк: PluginId.IsValidSlug, PluginsToolset.Schemas).
// Запись plugin_draft в ленте несёт именно голый slug (бэк пишет data = { slug } из аргумента
// draft_create, чья схема запрещает «/»). Потолок длины 64 — клиентский: на сервере
// (IsValidSlug) потолка нет. Невалидный → нейтральная строка без запросов и кнопок
// (slug в текст не подставляем).
const SLUG_RE = /^[a-z0-9-]+$/;
export function isDraftSlug(s: unknown): s is string {
  return typeof s === 'string' && s.length >= 1 && s.length <= 64 && SLUG_RE.test(s);
}

// Якорь записи черновика: slug из данных → карточка (общий кэш draftsStore, флаг
// plugins-build-yourself проверяет сама карточка). slug нет — нейтральная строка fallback.
export function DraftAnchor({ ctx }: { ctx: ChatItemToolCtx }) {
  const rec = recordOf(ctx.item);
  const raw = rec?.data?.slug;
  const slug = isDraftSlug(raw) ? raw : null;
  if (!slug) {
    if (rec?.fallback) return <FallbackLine text={rec.fallback} />;
    if (raw != null) return <FallbackLine text="Черновик больше недоступен" />;
    return null;
  }
  return <DraftFeedCard slug={slug} fallback={rec?.fallback ?? null} projectId={ctx.projectId} />;
}
