// Плитки «Пульса» под героем (волна 5, ST-F5; вариант A макета feature-stats-5.html):
// «Работали», «Ждали вас», «Возвраты», «Расход». Числа — из tilesModel (ST-F3), здесь
// только рендер: ничего не пересчитываем. Плитки «Расход» в DOM нет, если бэк не дал
// stats.spend (макет §A, п. 3) — она не стоит пустой. На 360 — сетка 2 колонки.
// Ряд ✓/↩ в «Возвраты» — пометка на каждый учтённый виток: их даёт roundMarks (ST-F3),
// withdrawn и running в них не входят.

import { type CSSProperties } from 'react';
import { C, R, FS, SP, FONT, SHADOW } from '../../lib/design';
import { formatDuration, kindBackground } from './FeatureStatsBits';
import { formatUsd, formatTokens } from './FeatureStatsBlocks';
import { tilesModel, type StatTile } from './FeatureStatsPulseModel';
import type { FeatureRun } from '../../types/featureRun';
import type { FeatureStats } from '../../types/featureStats';

export interface FeatureStatsTilesProps {
  run: FeatureRun;
  stats: FeatureStats;
  isMobile?: boolean;
}

// Склонение числительных: 1 возврат / 2 возврата / 5 возвратов. Формы даёт вызывающий.
function pluralRu(n: number, one: string, few: string, many: string): string {
  const mod10 = n % 10;
  const mod100 = n % 100;
  if (mod100 >= 11 && mod100 <= 14) return many;
  if (mod10 === 1) return one;
  if (mod10 >= 2 && mod10 <= 4) return few;
  return many;
}

// Сетка: четыре в ряд, на 360 — две колонки (макет .a-kpis и .w360 .a-kpis).
function gridStyle(isMobile: boolean): CSSProperties {
  return {
    display: 'grid',
    gridTemplateColumns: isMobile ? 'repeat(2, 1fr)' : 'repeat(4, 1fr)',
    gap: isMobile ? SP.sm : SP.md,
  };
}

// Карточка плитки: как герой — bgCard, borderLight, SHADOW.card, R.lg; поля на 360 плотнее.
function tileStyle(isMobile: boolean): CSSProperties {
  return {
    background: C.bgCard,
    border: `1px solid ${C.borderLight}`,
    borderRadius: R.lg,
    boxShadow: SHADOW.card,
    padding: isMobile ? `${SP.md}px` : `${SP.lg}px`,
  };
}

// Подпись плитки: FS.sm, textSecondary; у работы и ожидания — точка вида (тот же
// цвет, что в полосе состава героя).
const LABEL: CSSProperties = {
  display: 'flex', alignItems: 'center', gap: SP.xs,
  fontSize: FS.sm, fontWeight: 600, color: C.textSecondary, fontFamily: FONT.sans,
};

// Число плитки: FONT.serif, FS.h2 (на 360 — FS.xl) — как в макете §Токены.
function bigStyle(isMobile: boolean): CSSProperties {
  return {
    fontFamily: FONT.serif, fontSize: isMobile ? FS.xl : FS.h2, color: C.textHeading,
    lineHeight: 1.2, margin: `${SP.xs}px 0 ${SP.xs}px`,
  };
}

// Вторичная строка: FS.xs, textMuted.
const HINT: CSSProperties = { fontSize: FS.xs, color: C.textMuted, fontFamily: FONT.sans };

// Пометка витка: ✓ — successBg/successText, ↩ — dangerBg/dangerText (макет §Токены).
const MARK: CSSProperties = {
  width: 14, height: 14, borderRadius: R.sm,
  display: 'inline-flex', alignItems: 'center', justifyContent: 'center',
  fontSize: FS.xxs, fontWeight: 700,
};

// Что стоит в плитке: большое число и подпись. Порядок слов — из макета. Числительные
// склоняются по числу («1 возврат», «2 возврата», «5 возвратов»).
function tileContent(tile: StatTile): { big: string; hint: string | null } {
  switch (tile.key) {
    case 'work':
      return {
        big: formatDuration(tile.minutes),
        hint: tile.agentsSum == null
          ? 'агенты суммарно — не считалось'
          : `агенты суммарно ${formatDuration(tile.agentsSum)}`,
      };
    case 'waiting': {
      const count = tile.count ?? 0;
      return {
        big: tile.minutes == null ? '—' : formatDuration(tile.minutes),
        hint: count > 0
          ? `${count} ${pluralRu(count, 'раз', 'раза', 'раз')}` +
            (tile.longest != null ? ` · дольше всего ${formatDuration(tile.longest)}` : '')
          : 'ожиданий не было',
      };
    }
    case 'loops': {
      const src = tile.bySource
        ? ` · ревью ${tile.bySource.review.count} · проверка ${tile.bySource.qa.count} · финал ${tile.bySource.final.count}`
        : '';
      return {
        big: String(tile.loops),
        hint: tile.loops > 0
          ? `${tile.loops} ${pluralRu(tile.loops, 'возврат', 'возврата', 'возвратов')}${src}`
          : 'возвратов не было',
      };
    }
    case 'spend':
      return {
        // Частичный учёт — цена не настоящая: число не показываем, говорим прямо.
        big: tile.costPartial ? '—' : formatUsd(tile.costUsd),
        hint: `${formatTokens(tile.tokens)} ${pluralRu(tile.tokens, 'токен', 'токена', 'токенов')}` +
          (tile.costPartial ? ' · часть моделей без цены' : ''),
      };
  }
}

export function FeatureStatsTiles({ run, stats, isMobile = false }: FeatureStatsTilesProps) {
  const tiles = tilesModel(run, stats);

  return (
    <div data-testid="stats-tiles" style={gridStyle(isMobile)}>
      {tiles.map((tile) => {
        const { big, hint } = tileContent(tile);
        const kind = tile.key === 'work' ? 'work' : tile.key === 'waiting' ? 'waiting' : null;
        return (
          <div key={tile.key} data-testid={`tile-${tile.key}`} style={tileStyle(isMobile)}>
            <div style={LABEL}>
              {kind && (
                <span
                  aria-hidden
                  style={{ width: 8, height: 8, borderRadius: R.full, background: kindBackground(kind), flexShrink: 0 }}
                />
              )}
              {tile.label}
            </div>
            <div data-testid={`tile-${tile.key}-big`} style={bigStyle(isMobile)}>{big}</div>
            {hint && <div data-testid={`tile-${tile.key}-hint`} style={HINT}>{hint}</div>}

            {/* Ряд витков: значок на каждый учтённый ход; без них ряда нет. */}
            {tile.key === 'loops' && tile.rounds.length > 0 && (
              <div
                data-testid="tile-rounds"
                style={{ display: 'flex', gap: SP.xs, marginTop: SP.xs, flexWrap: 'wrap' }}
              >
                {tile.rounds.map((mark, i) => (
                  <i
                    key={i}
                    data-testid="round-mark"
                    aria-hidden
                    style={{
                      ...MARK,
                      background: mark === 'ok' ? C.successBg : C.dangerBg,
                      color: mark === 'ok' ? C.successText : C.dangerText,
                    }}
                  >
                    {mark === 'ok' ? '✓' : '↩'}
                  </i>
                ))}
              </div>
            )}
          </div>
        );
      })}
    </div>
  );
}
