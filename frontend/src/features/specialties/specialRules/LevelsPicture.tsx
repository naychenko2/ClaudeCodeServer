import { C, FONT, FS, R, SP } from '../../../lib/design';
import { TIERS, routeLabel, type TierKey } from '../../../lib/modelProvidersShared';
import { findPreset, presetIdOf, usePresets } from '../../../lib/presets';
import { rolesWord, type LevelBar } from './model';

// «Картина по уровням» — три пропорциональные полосы поверх карточек: доля каждой
// цепочки внутри уровня. Это ответ на вопрос «как у меня в целом устроено», который
// список из 42 строк не давал вовсе (макет v4, проверка гипотезы 14 → 9).
//
// Знаменатель полосы — ВЕСЬ каталог ролей, поэтому частично заполненный слой честно
// показывает хвост «не задано ×N» (пунктир, без клика). Клик по сегменту выбирает его —
// карточки с этой цепочкой на этом уровне подсвечиваются кольцом ниже по ленте.
//
// Горизонтального скролла нет: дорожка — flex с min-width: 0 у сегментов, подписи
// режутся, полный текст живёт в title.

// Ниже этой доли подпись не влезает — оставляем только счётчик «×N» (как в макете)
const LABEL_MIN_SHARE = 0.14;

export function LevelsPicture({ bars, selected, onSelect, tierModels, ollamaModel, subtitle }: {
  bars: LevelBar[];
  selected: { tier: TierKey; route: string } | null;
  onSelect: (sel: { tier: TierKey; route: string } | null) => void;
  tierModels: Record<TierKey, string>;
  ollamaModel?: string;
  subtitle: string;
}) {
  const presets = usePresets();

  // Имя сегмента: у цепочки — её имя (в полосе важно «чем закрыто», а не состав шагов),
  // у модели — обычная подпись маршрута
  const nameOf = (route: string): string => {
    const preset = findPreset(presets, presetIdOf(route));
    return preset ? preset.name : routeLabel(route, ollamaModel, tierModels);
  };

  return (
    <div style={{
      background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.xl,
      padding: `${SP.md}px 14px`, marginBottom: SP.xs,
    }}>
      <div style={{ fontSize: FS.base, fontWeight: 700, color: C.textHeading }}>
        Картина по уровням
      </div>
      <div style={{ fontSize: FS.xs, color: C.textMuted, margin: '2px 0 10px', lineHeight: 1.45 }}>
        {subtitle}
      </div>
      {bars.map(bar => (
        <div key={bar.tier} style={{
          display: 'flex', alignItems: 'center', gap: SP.sm, marginTop: 6, minWidth: 0,
        }}>
          <span style={{ width: 52, flexShrink: 0, fontSize: FS.xs, color: C.textMuted }}>
            {TIERS[bar.tier].title}
          </span>
          <div style={{
            flex: 1, display: 'flex', minWidth: 0, height: 26,
            borderRadius: 7, overflow: 'hidden', background: C.bgInset,
          }}>
            {bar.segments.map((seg, i) => {
              const share = bar.total > 0 ? seg.count / bar.total : 0;
              const name = nameOf(seg.route);
              const isSel = selected?.tier === bar.tier && selected.route === seg.route;
              const isTop = i === 0;
              return (
                <button
                  key={seg.route}
                  type="button"
                  title={`${name} · ${rolesWord(seg.count)}`}
                  onClick={() => onSelect(isSel ? null : { tier: bar.tier, route: seg.route })}
                  style={{
                    flex: seg.count, minWidth: 0, overflow: 'hidden',
                    display: 'flex', alignItems: 'center', justifyContent: 'center',
                    padding: '0 5px', border: 'none', borderRight: `2px solid ${C.bgWhite}`,
                    fontFamily: FONT.sans, fontSize: 10, fontWeight: 700, whiteSpace: 'nowrap',
                    cursor: 'pointer',
                    background: isTop ? C.accentLight : C.bgSelected,
                    color: isTop ? C.textHeading : C.textSecondary,
                    boxShadow: isSel ? `inset 0 0 0 2px ${C.accent}` : 'none',
                  }}
                >
                  {share >= LABEL_MIN_SHARE ? `${name} ×${seg.count}` : `×${seg.count}`}
                </button>
              );
            })}
            {bar.unset > 0 && (
              <span
                title={`Не задано у ${rolesWord(bar.unset)} — они идут за наследованием`}
                style={{
                  flex: bar.unset, minWidth: 0, overflow: 'hidden',
                  display: 'flex', alignItems: 'center', justifyContent: 'center',
                  padding: '0 5px', fontFamily: FONT.sans, fontSize: 10, fontWeight: 600,
                  whiteSpace: 'nowrap', color: C.textMuted, background: 'transparent',
                }}
              >
                не задано ×{bar.unset}
              </span>
            )}
          </div>
        </div>
      ))}
    </div>
  );
}
