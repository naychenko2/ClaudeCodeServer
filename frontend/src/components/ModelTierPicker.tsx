import { modelLabel, useModels } from '../lib/models';
import { TIER_ORDER, TIER_TITLE, useTierModels, type ModelTierKey } from '../lib/modelTiers';
import { C, R, FONT } from '../lib/design';

// Выбор уровня модели (сильная/средняя/слабая) у задачи и персоны. Строки-карточки
// как в ModelPicker: слева название уровня, справа — модель, которая за ним стоит сейчас
// (личный уровень пользователя поверх общего). Пустое значение = «По умолчанию»:
// модель возьмётся от исполнителя и назначения места.
interface Props {
  value: ModelTierKey | '';
  onChange: (v: ModelTierKey | '') => void;
  // Подпись пункта «По умолчанию» — контекст решает, чей дефолт (место задач / персона)
  defaultHint?: string;
}

export function ModelTierPicker({ value, onChange, defaultHint }: Props) {
  // Подписи моделей приходят из каталога — подписываемся, чтобы обновиться после его загрузки
  useModels();
  const tierModels = useTierModels();

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
      <TierRow
        title="По умолчанию"
        note={defaultHint}
        active={value === ''}
        onClick={() => onChange('')}
      />
      {TIER_ORDER.map(t => (
        <TierRow
          key={t}
          title={TIER_TITLE[t]}
          note={tierModels[t] ? modelLabel(tierModels[t]) : 'не задана — выберет Claude Code сам'}
          strongNote={!!tierModels[t]}
          active={value === t}
          onClick={() => onChange(t)}
        />
      ))}
    </div>
  );
}

// Одна строка-карточка уровня: название + модель за уровнем (или пометка «не задана»)
function TierRow({ title, note, strongNote, active, onClick }: {
  title: string;
  note?: string;
  strongNote?: boolean;
  active: boolean;
  onClick: () => void;
}) {
  return (
    <button
      type="button"
      onClick={onClick}
      style={{
        width: '100%', display: 'flex', alignItems: 'center', gap: 8,
        padding: '8px 11px', borderRadius: R.md, cursor: 'pointer', textAlign: 'left',
        border: `1px solid ${active ? C.accent : C.border}`,
        background: active ? C.accentLight : C.bgWhite,
      }}
    >
      <span style={{
        flex: 1, minWidth: 0, fontFamily: FONT.sans, fontSize: 13, fontWeight: 600,
        color: C.textHeading, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
      }}>
        {title}
      </span>
      {note && (
        <span style={{
          flexShrink: 1, minWidth: 0, fontFamily: strongNote ? FONT.mono : FONT.sans,
          fontSize: 11.5, color: C.textMuted,
          overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
        }}>
          {note}
        </span>
      )}
    </button>
  );
}
