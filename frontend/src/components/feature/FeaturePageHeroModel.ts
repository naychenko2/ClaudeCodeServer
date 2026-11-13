// Модель «шапки в теле» страницы фичи (F09): мета «эпик · дд.мм» и метастрока этапов.
// Чистые функции без React — под vitest.
import type { FeatureRunMeta } from '../../types/featureRun';

// «эпик 57dfe3b7 · 13.11»: короткий id — первые 8 знаков; дата — локальные дд.мм.
// Нет id — строки нет (null); нет/битая даты — только «эпик {id}».
export function epicMetaLine(epicId: string | null | undefined, createdAt: string | null | undefined): string | null {
  if (!epicId) return null;
  const short = epicId.slice(0, 8);
  const t = createdAt ? new Date(createdAt) : null;
  if (!t || Number.isNaN(t.getTime())) return `эпик ${short}`;
  const dd = String(t.getDate()).padStart(2, '0');
  const mm = String(t.getMonth() + 1).padStart(2, '0');
  return `эпик ${short} · ${dd}.${mm}`;
}

function branchesLabel(n: number): string {
  const m10 = n % 10, m100 = n % 100;
  if (m10 === 1 && m100 !== 11) return `${n} ветка идёт параллельно`;
  if (m10 >= 2 && m10 <= 4 && (m100 < 12 || m100 > 14)) return `${n} ветки идут параллельно`;
  return `${n} веток идут параллельно`;
}

function etaLabel(minutes: number): string {
  if (minutes < 60) return `${Math.max(1, Math.round(minutes))} мин`;
  return `${Math.round(minutes / 60)} ч`;
}

// «4 из 10 этапов · 2 ветки идут параллельно · ≈ 3 ч до финала». У легаси-фичи
// (stagesTotal 0) и без meta строки нет. «≈» — при meta.approx.
export function stagesMetaLine(meta: FeatureRunMeta | null | undefined): string | null {
  if (!meta || meta.stagesTotal <= 0) return null;
  const parts = [`${meta.stagesDone} из ${meta.stagesTotal} этапов`];
  if (meta.parallelBranches > 0) parts.push(branchesLabel(meta.parallelBranches));
  if (meta.etaMinutes != null && meta.etaMinutes > 0) parts.push(`${meta.approx ? '≈ ' : ''}${etaLabel(meta.etaMinutes)} до финала`);
  return parts.join(' · ');
}

// Ширина полосы прогресса: из meta.progressPct (B02), зажато в 0..100; нет — полосы нет.
export function progressWidth(meta: FeatureRunMeta | null | undefined): number | null {
  const p = meta?.progressPct;
  if (p == null || !Number.isFinite(p)) return null;
  return Math.min(100, Math.max(0, Math.round(p)));
}
