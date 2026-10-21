// Чистая логика карточки черновика (макет 4, ADR-031, шаг 4.4): вклады, блокировка
// «Принять», срок отклонённого, подписи diff, готовая реплика «Попросить агента исправить».
// Чистые функции — тестируются без рендера (DraftCard.test.ts).

import type {
  PluginDraftPreview, PluginDraftSummary, PluginDraftDiffFile,
} from '../../lib/api';

// Вклад «Что будет после установки» (макет 4): панель/команда/навык — человекочитаемая строка
export interface DraftContribution {
  kind: 'panel' | 'command' | 'skill';
  label: string;
}

// «Что будет после установки»: вклады из предпросмотра (панели, команды, навыки).
// Панель без заголовка — по её id; команда/навык — как есть (путь/имя).
export function draftContributions(preview: PluginDraftPreview): DraftContribution[] {
  const out: DraftContribution[] = [];
  for (const p of preview.panels) out.push({ kind: 'panel', label: p.title || p.id });
  for (const c of preview.commands) out.push({ kind: 'command', label: c });
  for (const s of preview.skills) out.push({ kind: 'skill', label: s });
  return out;
}

// «Принять» заблокирована, пока проверка пакета не пройдена: null — можно, иначе причина.
export function acceptDisabledReason(preview: PluginDraftPreview): string | null {
  if (preview.validation.isValid) return null;
  return 'Сначала исправьте ошибки проверки';
}

// Число ошибок последней проверки (0 — «Проверка пройдена»).
export function problemCount(preview: PluginDraftPreview): number {
  return preview.validation.problems.length;
}

// Отклонённый черновик исчезает по истечении срока (rejectedAt + 30 дней).
// now — ms; без expiresAt (активный) — не истёк.
export function isRejectedExpired(summary: PluginDraftSummary, now: number): boolean {
  if (!summary.rejected || !summary.expiresAt) return false;
  const t = Date.parse(summary.expiresAt);
  return Number.isNaN(t) ? false : t <= now;
}

// Файл diff: бинарный или без содержимого (lines == null) — «изменён, N байт»;
// иначе null (показываем построчный diff).
export function diffFileLabel(file: PluginDraftDiffFile): string | null {
  if (file.binary || file.lines == null) return `изменён, ${file.size} байт`;
  return null;
}

// Готовая реплика для «Попросить агента исправить»: имя плагина + список ошибок проверки.
// Кладётся в поле ввода чата (макет 4, «Править» → «Попросить агента исправить»).
export function buildFixReply(name: string, problems: { code: string; message: string }[]): string {
  if (problems.length === 0) return `Поправь плагин «${name}»: проверка не прошла.`;
  const lines = problems.map(p => `  - ${p.message} (${p.code})`).join('\n');
  return `Поправь плагин «${name}»: проверка не прошла.\n${lines}`;
}
