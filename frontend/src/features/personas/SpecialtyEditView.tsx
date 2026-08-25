// Экран «Настройка роли» (адресуется как #/personas/specialties/{roleKey}?edit=1).
// Содержит блок «Пресеты для роли» и секцию моделей по уровням; имена и описания
// ролей берутся из каталога и не персонализируются.
//
// Модели правятся через существующий PUT слоя: редьюсер onSave расширяет слой
// спредом (см. lib/presets.ts:367).

import { useMemo } from 'react';
import { ChevronLeft } from 'lucide-react';
import { C, FONT, FS, R, SP } from '../../lib/design';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import { useIsMobile } from '../../lib/breakpoints';
import type { LayerReducer } from '../../lib/presets';
import type {
  Persona, SpecialtyCatalogEntry, SpecialtySettingsLayer, SpecialtyTemplateSettings,
} from '../../types';
import type { Scope } from './personaSpecialtyShared';
import { LayerSwitch } from './personaSpecialtyShared';

// === Основной экран ===
export interface SpecialtyEditViewProps {
  roleKey: string;
  catalog: SpecialtyCatalogEntry[];
  layer: Scope;
  layerSettings: SpecialtySettingsLayer | null;
  userLayer: SpecialtySettingsLayer | null;
  personas: Persona[];
  contextUserId: string | null;
  onBack: () => void;
  onSave: (reducer: LayerReducer) => Promise<void>;
}

export function SpecialtyEditView({
  roleKey, catalog, layer, layerSettings, userLayer, personas, contextUserId,
  onBack, onSave,
}: SpecialtyEditViewProps): React.ReactElement {
  const isMobile = useIsMobile();
  const role = useMemo(() => catalog.find(r => r.key === roleKey) ?? null, [catalog, roleKey]);

  if (!role) {
    return (
      <div style={{ display: 'flex', flexDirection: 'column', gap: SP.md }}>
        <BackRow onBack={onBack} />
        <div style={{
          border: `1px dashed ${C.dashed}`, borderRadius: R.xl,
          padding: '22px 18px', textAlign: 'center',
          color: C.textSecondary, fontSize: FS.sm, lineHeight: 1.55,
        }}>Роль не найдена в каталоге.</div>
      </div>
    );
  }

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: SP.md }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm }}>
        <button type="button" onClick={onBack} style={{
          display: 'inline-flex', alignItems: 'center', gap: 6,
          font: 'inherit', fontFamily: FONT.sans, fontSize: FS.sm, fontWeight: 600,
          color: C.textHeading, background: 'none', border: 'none',
          padding: 0, cursor: 'pointer',
        }}>
          <ChevronLeft size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
          <span>{role.label}</span>
        </button>
        <span style={{ flex: 1 }} />
        <button type="button" onClick={onBack} style={{
          font: 'inherit', fontFamily: FONT.sans, fontSize: FS.sm, fontWeight: 600,
          color: C.textSecondary, background: 'none', border: 'none',
          padding: '6px 8px', cursor: 'pointer',
        }}>Отмена</button>
      </div>

      {/* Переключатель слоёв — переключение сбрасывает несохранённые правки */}
      <div style={{
        display: 'flex', alignItems: 'center', gap: 10, flexWrap: 'wrap',
      }}>
        <div style={{
          display: 'flex', gap: 2, background: C.bgSelected, borderRadius: R.pill, padding: 2,
          width: isMobile ? '100%' : undefined, flexWrap: isMobile ? 'wrap' : undefined,
        }}>
          <LayerSwitch
            scope={layer}
            onScope={() => { /* слой меняется через navPush/PersonasSpecialties */ }}
            isAdmin={true}
            isMobile={isMobile}
          />
        </div>
        <span style={{ fontSize: FS.xs, color: C.textMuted, lineHeight: 1.45 }}>
          Слой определяет, кого коснётся правило.
        </span>
      </div>

      <div style={{
        background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.xl,
        padding: isMobile ? SP.md : SP.lg,
        display: 'flex', flexDirection: 'column', gap: SP.md,
      }}>
        {/* Модели по уровням — без гейта флагом (это базовая функциональность,
            а не персонализация). Правка через тот же onSave (T-редьюсер). */}
        <ModelsSection
          roleKey={roleKey}
          layer={layer}
          layerSettings={layerSettings}
          userLayer={userLayer}
          contextUserId={contextUserId}
          personas={personas}
          onSave={onSave}
        />
      </div>

      {/* Подсказка под формой: «Поле персоны сильнее правила специальности» —
          общий для всего раздела инвариант, переехал из SpecialRulesTab. */}
      <div style={{
        fontSize: FS.xs, color: C.textMuted, lineHeight: 1.5, padding: '0 2px',
      }}>
        Поле персоны сильнее правила специальности; специальность без правила
        наследует «Любая специальность» → «Модели по умолчанию».
      </div>
    </div>
  );
}

// === Заглушка секции моделей (расширение T-слоя — часть следующих волн) ===
//
// Сейчас фронт получил инфраструктуру трёх экранов + Display-запись. Секция
// моделей пока отдаёт read-only картину по уровням с плейсхолдером P24;
// правка моделей на этой странице подключится в волне 4 «Спес моделей»
// (этап 4 «Матрицы моделей»). Здесь — минимальный read-only показ, чтобы
// критерий «на просмотре нет ни одного поля ввода и тумблера» не сломался
// из-за пустой страницы.
function ModelsSection({ roleKey, layer, layerSettings, userLayer, onSave }: {
  roleKey: string;
  layer: Scope;
  layerSettings: SpecialtySettingsLayer | null;
  userLayer: SpecialtySettingsLayer | null;
  contextUserId: string | null;
  personas: Persona[];
  onSave: (reducer: LayerReducer) => Promise<void>;
}): React.ReactElement {
  const rec = layerSettings?.specialties?.[roleKey] ?? null;
  const triple: [string, string, string] = rec
    ? [rec.tierStrong ?? '', rec.tierMedium ?? '', rec.tierWeak ?? '']
    : ['', '', ''];
  const hasAny = triple.some(v => !!v);

  const setCell = (tier: 'strong' | 'medium' | 'weak', value: string) => {
    void onSave((cur) => {
      const baseLayer = layer === 'user' ? (userLayer ?? cur) : cur;
      const curRec: SpecialtyTemplateSettings = baseLayer.specialties[roleKey] ?? {
        access: 'full', tools: null, disallowedTools: null,
      };
      const nextRec = { ...curRec };
      const cell = value.trim() || null;
      if (tier === 'strong') nextRec.tierStrong = cell;
      else if (tier === 'medium') nextRec.tierMedium = cell;
      else nextRec.tierWeak = cell;
      const next: SpecialtySettingsLayer = { ...baseLayer };
      next.specialties = { ...baseLayer.specialties, [roleKey]: nextRec };
      // Если все три ячейки пустые и запись «пустая» — удаляем ключ,
      // чтобы не оставлять затенение (бэкенд сделал бы StripTiers, но запись
      // без полей продолжала бы перекрывать нижний слой, что плохо).
      const emptyAll = !nextRec.tierStrong && !nextRec.tierMedium && !nextRec.tierWeak
        && !nextRec.promptSections?.length && !nextRec.defaultBindings?.length;
      if (emptyAll) {
        const { [roleKey]: _drop, ...rest } = next.specialties;
        next.specialties = rest;
      }
      // После saveLayer вызывающий сценарий перечитывает каталог.
      // Здесь дёргать reloadSpecialties не нужно — каталог не зависит от моделей.
      return next;
    });
  };

  const clearCell = (tier: 'strong' | 'medium' | 'weak') => setCell(tier, '');

  return (
    <div style={{
      paddingTop: SP.md, marginTop: SP.sm,
      borderTop: `1px solid ${C.borderLight}`,
    }}>
      <div style={{
        fontFamily: FONT.sans, fontSize: FS.xs, fontWeight: 700,
        color: C.textMuted, textTransform: 'uppercase', letterSpacing: '0.07em',
        marginBottom: SP.sm,
      }}>Модели по уровням</div>
      <div style={{
        display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(150px, 1fr))',
        gap: SP.sm,
      }}>
        {([
          { tier: 'Сильная' as const, value: triple[0], key: 'strong' as const },
          { tier: 'Средняя' as const, value: triple[1], key: 'medium' as const },
          { tier: 'Слабая' as const, value: triple[2], key: 'weak' as const },
        ]).map(({ tier, value, key }) => (
          <div key={key} style={{
            padding: '8px 10px',
            background: C.bgCard, borderRadius: R.md,
            border: `1px solid ${C.borderLight}`,
            fontFamily: FONT.sans, fontSize: FS.xs,
          }}>
            <div style={{ fontWeight: 700, color: C.textHeading, marginBottom: 2 }}>{tier}</div>
            <div style={{ color: value ? C.textPrimary : C.textMuted }}>
              {value || 'Как «Модели по умолчанию»'}
            </div>
            {value && (
              <button type="button" onClick={() => clearCell(key)} style={{
                font: 'inherit', fontFamily: FONT.sans, fontSize: FS.xs, fontWeight: 600,
                color: C.accent, background: 'none', border: 'none', padding: 0,
                cursor: 'pointer', textDecoration: 'underline', textUnderlineOffset: 2,
                marginTop: 4,
              }}>Очистить</button>
            )}
          </div>
        ))}
      </div>
      {!hasAny && (
        <div style={{
          fontSize: FS.xs, color: C.textMuted, marginTop: SP.sm, lineHeight: 1.5,
        }}>
          Правил нет — персоны роли работают по «Моделям по умолчанию».
        </div>
      )}
    </div>
  );
}

function BackRow({ onBack }: { onBack: () => void }): React.ReactElement {
  return (
    <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm }}>
      <button type="button" onClick={onBack} style={{
        display: 'inline-flex', alignItems: 'center', gap: 6,
        font: 'inherit', fontFamily: FONT.sans, fontSize: FS.sm, fontWeight: 600,
        color: C.textHeading, background: 'none', border: 'none',
        padding: 0, cursor: 'pointer',
      }}>
        <ChevronLeft size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
        <span>Специальности</span>
      </button>
    </div>
  );
}