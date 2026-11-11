// Карточка «Срок» страницы фичи (FS-FE): один срок из `brief.eta` — остаток словами, дата
// готовности, источник оценки и состояния часов («часы стоят» / «оценка до остановки»).
// Презентационный компонент: ничего не выбирает и не досчитывает — выбор срока сделал бэк
// (FS-V2), фолбэков на note.estimate/estimate.remainingMinutes здесь нет. Форматтеры общие:
// длительность — formatDuration (lib/chatGroups), дата — readyAtText и давность — agoText
// (FeatureBriefModel: те же, чем строка «Срок» сводки выводит etaEndsAt). Полоса времени и
// строка «из ≈ 3 д» — величины «Б», не рисуются (записка §8). Встраивание — шаг FS-FS.

import { type CSSProperties } from 'react';
import { C, FONT, FS, R, SP } from '../../lib/design';
import { formatDuration } from '../../lib/chatGroups';
import type { FeatureEta } from '../../lib/featurePage';
import { agoText, genitiveName, readyAtText, type PersonaNameResolver } from './FeatureBriefModel';

export interface FeatureEtaCardProps {
  // Единственный источник срока (бэк FeatureBrief.BriefEta); null — ни одной оценки нет.
  eta: FeatureEta | null;
  // Давность и автор оценки — из brief.note как есть; карточка их не выводит из eta.
  noteAt?: string | null;
  noteByPersonaId?: string | null;
  noteStale?: boolean;
  // «прошло N» — brief.elapsedMinutes (объединение work-интервалов, бэк WorkWallMinutes).
  elapsedMinutes?: number | null;
  nameOf?: PersonaNameResolver;
  isMobile: boolean;
}

const CARD: CSSProperties = {
  background: C.bgCard, border: `1px solid ${C.borderLight}`, borderRadius: R.lg,
  padding: SP.lg, display: 'flex', flexDirection: 'column', gap: SP.sm, boxSizing: 'border-box', alignSelf: 'stretch',
};

const MAIN: CSSProperties = {
  fontFamily: FONT.sans, fontWeight: 600, color: C.textHeading,
};

// «часы стоят…» — жёлтая метка ожидания (тот же C.warningText, что предупреждения карточек).
const CLOCK_WAIT_TEXT = 'часы стоят, пока ждём тебя · даты нет';

type CaptionPart = { text: string; warn?: boolean };

export function FeatureEtaCard({
  eta, noteAt, noteByPersonaId, noteStale, elapsedMinutes, nameOf, isMobile,
}: FeatureEtaCardProps) {
  const nowMs = Date.now();

  if (!eta) {
    // Оценки нет вовсе: пустое состояние вместо числа (тот же язык, что «Оценки пока нет» сводки).
    return (
      <div data-testid="eta-card" style={CARD}>
        <span data-testid="eta-main" title="мало похожих фич" style={{ ...MAIN, fontSize: isMobile ? FS.xl : FS.h2, color: C.textSecondary }}>
          срок —
        </span>
      </div>
    );
  }

  // Порядок строки: крупное число → дата/источник → «прошло» (записка §2, столбец «Срок»).
  const main = eta.minutes < 1 ? 'меньше минуты' : `≈ ${formatDuration(eta.minutes)}`;

  const parts: CaptionPart[] = [];
  if (eta.clockStopped === 'wait') {
    // Ожидание человека: даты нет — вместо неё жёлтая метка, почему срок не идёт.
    parts.push({ text: CLOCK_WAIT_TEXT, warn: true });
  } else {
    const at = readyAtText(eta.endsAt, nowMs);
    if (at) parts.push({ text: at });
  }
  // Пауза: дата как была и честная подпись — оценка сделана до остановки.
  if (eta.clockStopped === 'stopped') parts.push({ text: 'оценка до остановки' });
  if (eta.source === 'note') {
    const name = noteByPersonaId ? nameOf?.(noteByPersonaId) ?? null : null;
    const ago = agoText(noteAt ?? null, nowMs);
    parts.push({ text: `оценка ${name ? genitiveName(name) : ''}${ago ? ` ${ago}` : ''}`.trim() });
    if (noteStale) parts.push({ text: 'устарела', warn: true });
  } else {
    parts.push({ text: 'по похожим фичам' });
  }

  const elapsed = elapsedMinutes != null && Math.round(elapsedMinutes) > 0 ? formatDuration(elapsedMinutes) : null;

  return (
    <div data-testid="eta-card" style={CARD}>
      <div style={{ display: 'flex', alignItems: 'baseline', gap: 6, flexWrap: 'wrap' }}>
        <span data-testid="eta-main" style={{ ...MAIN, fontSize: isMobile ? FS.xl : FS.h2 }}>{main}</span>
        <span style={{ fontSize: FS.md, color: C.textSecondary }}>осталось</span>
      </div>
      {parts.length > 0 && (
        <div data-testid="eta-caption" style={{ display: 'flex', flexWrap: 'wrap', columnGap: 6, fontSize: FS.sm, color: C.textSecondary }}>
          {parts.map((p, i) => (
            <span key={p.text} style={p.warn ? { color: C.warningText } : undefined}>{i > 0 ? `· ${p.text}` : p.text}</span>
          ))}
        </div>
      )}
      {elapsed && (
        <div data-testid="eta-elapsed" style={{ fontSize: FS.sm, color: C.textSecondary }}>прошло {elapsed}</div>
      )}
    </div>
  );
}
