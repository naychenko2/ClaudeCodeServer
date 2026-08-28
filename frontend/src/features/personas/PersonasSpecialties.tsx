// Экран «Специальности» — центральная зона раздела «Персоны» в режиме specialties
// (волна 4 «Персонализация специальностей», волна «specialties-personas-parity»).
// Роутер между тремя экранами:
//
//   - список ролей (нет roleKey):       SpecialtyListView
//   - визитка роли (roleKey):            SpecialtyRoleView
//   - настройка роли (viewMode === 'edit'): SpecialtyEditView
//
// Шапку раздела (PillSwitch) рисует PersonasPage — здесь только контент:
// hero-заголовок + баннер ошибок стора + активный экран. Контент лежит поверх
// дудл-фона (PageCanvas) в собственном скроллере по образцу PersonasHub.tsx:60.
//
// С переходом на единый глобальный слой (f8e7d0e0): данные пишутся и
// читаются только в global, аватарки персон показываются всегда.

import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { C, FONT, FS, CONTENT_MAX_W } from '../../lib/design';
import { useProviderData, type TierKey } from '../../lib/modelProvidersShared';
import {
  saveLayer, useSpecialtySettings, useSaveState,
} from '../../lib/presets';
import type { LayerReducer } from '../../lib/presets';
import {
  getPromptSectionsCatalog, loadPromptSectionsCatalog, reloadSpecialties,
  useSpecialtyCatalog,
} from '../../lib/specialties';
import { useMe } from '../../lib/defaultPersona';
import { usePersonas } from '../../lib/personas';
import type { Persona, SpecialtyPromptSectionsCatalog, SpecialtySettingsResponse } from '../../types';
import { SpecialtyListView } from './SpecialtyListView';
import { SpecialtyRoleView } from './SpecialtyRoleView';
import { SpecialtyEditView } from './SpecialtyEditView';
import { SPECIALTIES_TITLE, SPECIALTIES_SUBTITLE } from './personaSpecialtyShared';
import { useSpecialtiesCoverage } from './useSpecialtiesCoverage';

export interface PersonasSpecialtiesProps {
  roleKey?: string | null;
  viewMode?: 'list' | 'role' | 'edit';
  onNavigateList?: () => void;
  onNavigateRole?: (key: string) => void;
  onNavigateEdit?: (key: string) => void;
}

export function PersonasSpecialties(props: PersonasSpecialtiesProps): React.ReactElement {
  const me = useMe();
  const isAdmin = me.role === 'admin';

  const catalog = useSpecialtyCatalog();
  const settingsAll = useSpecialtySettings();

  // Каталог секций промптов (для RolePresetsBlock и RolePeopleSlice). Грузится
  // лениво по требованию, общий кэш — несколько экранов и волны делят один запрос.
  const [promptSectionsCatalog, setPromptSectionsCatalog] =
    useState<SpecialtyPromptSectionsCatalog | null>(getPromptSectionsCatalog());
  useEffect(() => {
    let cancelled = false;
    void loadPromptSectionsCatalog().then(c => {
      if (!cancelled && c) setPromptSectionsCatalog(c);
    });
    return () => { cancelled = true; };
  }, []);

  // Полный список персон — единый источник стопок аватаров. На общем слое
  // аватарки показываются всегда (не гейтятся слоем).
  const personas: Persona[] = usePersonas();

  const onSaveLayer = useCallback(
    async (reducer: LayerReducer): Promise<void> => {
      await saveLayer('global', reducer, null);
      reloadSpecialties();
    },
    [],
  );

  // Глобальный слой — единственная запись, на которую смотрит весь раздел.
  const layerSettings = settingsAll?.global ?? null;

  // Охват «N из M» — сколько ролей каталога уже настроено. Раньше висел бейджем на
  // переключателе «Персоны | Специальности»; переключателя больше нет, показываем
  // рядом с заголовком раздела — там, где им и пользуются.
  const coverage = useSpecialtiesCoverage(isAdmin);

  const data = useProviderData(isAdmin, null);
  const { settingsError } = useSaveState();
  // Сейчас вычисления моделей по уровням не нужны — они появятся в этапе
  // «Матрицы моделей на экране роли». Заглушка, чтобы импорт оставался.
  const _tierModels = useMemo<Record<TierKey, string>>(() => ({
    strong: data.effectiveTierModel('strong'),
    medium: data.effectiveTierModel('medium'),
    weak: data.effectiveTierModel('weak'),
  }), [data]);
  void _tierModels;

  // === Роутинг viewMode ===
  const viewMode = props.viewMode ?? 'list';
  const roleKey = props.roleKey ?? null;

  // Прямой хеш .../edit на не-админе режется тут: даунгрейд до визитки. Дополнительно
  // к useEffect в PersonasPage — здесь срабатывает мгновенно при первом рендере,
  // пока me ещё не загрузился (me.role === '' означает «не админ»).
  const effectiveViewMode: 'list' | 'role' | 'edit' =
    viewMode === 'edit' && !isAdmin ? 'role' : viewMode;

  const goList = useCallback(() => props.onNavigateList?.(), [props]);
  const goRole = useCallback((key: string) => props.onNavigateRole?.(key), [props]);
  const goEdit = useCallback((key: string) => props.onNavigateEdit?.(key), [props]);

  // Сброс прокрутки при переходах витрина → визитка → форма. Без сброса визитка
  // открывается с середины длинного списка ролей, и аватар/тулбар оказываются
  // за верхним краем. Тот же приём, что в PersonasHub.tsx:35.
  const scrollRef = useRef<HTMLDivElement>(null);
  useEffect(() => {
    scrollRef.current?.scrollTo({ top: 0 });
  }, [viewMode, roleKey]);

  // Витрина живёт на сетке раздела (CONTENT_MAX_W по центру), визитка и форма —
  // резиновые: их шапка-тулбар тянется во всю ширину центра, как у персоны
  // (PersonasPage → PersonaStudio), а полотно центрируется уже внутри экрана.
  const detail = effectiveViewMode !== 'list';

  return (
    // Фон прозрачный: под центром виден дудл-фон страницы (CanvasBackdrop).
    // Скролл — свой, как у PersonasHub (см. PersonasHub.tsx:60): иначе на длинном
    // списке ролей десктоп не прокручивался, а мобила подменю проваливалась за
    // нижнюю кромку. Нижний ориентир ширины — 360 CSS.
    <div ref={scrollRef} style={{ height: '100%', overflowY: 'auto', padding: detail ? 0 : '0 32px' }}>
      <div style={{
        maxWidth: detail ? undefined : CONTENT_MAX_W, margin: '0 auto',
        padding: detail ? '0 0 60px' : '28px 0 60px',
      }}>
        {/* Hero-заголовок раздела — только на витрине. На визитке и форме заголовком
            служит тулбар (как у персоны): лишний hero отжимал контент за первый экран
            и дублировал тулбар. Тот же язык, что у PersonasHub: serif 28/500 +
            текст 14 C.textMuted + разделительная линия снизу. */}
        {effectiveViewMode === 'list' && (
          <div style={{
            paddingBottom: 26,
            borderBottom: `1px solid ${C.borderLight}`,
            marginBottom: 28,
          }}>
            <div style={{
              display: 'flex', alignItems: 'baseline', gap: 10, flexWrap: 'wrap',
              marginBottom: 12,
            }}>
              <div style={{
                fontFamily: FONT.serif, fontSize: 28, fontWeight: 500,
                color: C.textHeading, lineHeight: 1.28, letterSpacing: '-0.01em',
              }}>{SPECIALTIES_TITLE}</div>
              {coverage && (
                <span
                  title={`Охват специальностей: ${coverage}`}
                  style={{
                    fontFamily: FONT.mono, fontSize: FS.xs, fontWeight: 700,
                    color: C.textSecondary, background: C.bgSelected,
                    padding: '2px 8px', borderRadius: 12,
                  }}
                >{coverage}</span>
              )}
            </div>
            <div style={{
              fontSize: FS.md, color: C.textMuted, lineHeight: 1.65,
              maxWidth: 560,
            }}>{SPECIALTIES_SUBTITLE}</div>
          </div>
        )}

        {settingsError && (
          <div style={{
            margin: detail ? '0 16px 12px' : '0 0 12px',
            padding: '7px 10px', borderRadius: 8, fontSize: FS.xs,
            color: C.dangerText, background: C.dangerBg, border: `1px solid ${C.dangerBorder}`,
          }}>{settingsError}</div>
        )}

        {effectiveViewMode === 'list' && (
          <SpecialtyListView
            catalog={catalog}
            layerSettings={layerSettings}
            personas={personas}
            onOpenRole={(k) => goRole(k)}
          />
        )}

        {effectiveViewMode === 'role' && roleKey && (
          <SpecialtyRoleView
            roleKey={roleKey}
            catalog={catalog ?? []}
            layerSettings={layerSettings}
            promptSectionsCatalog={promptSectionsCatalog}
            personas={personas.filter(p => p.specialty === roleKey)}
            onBack={goList}
            onEdit={isAdmin ? () => goEdit(roleKey) : undefined}
            isAdmin={isAdmin}
          />
        )}

        {effectiveViewMode === 'edit' && roleKey && (
          <SpecialtyEditView
            roleKey={roleKey}
            catalog={catalog ?? []}
            layerSettings={layerSettings}
            onBack={() => goRole(roleKey)}
            onSave={onSaveLayer}
          />
        )}
      </div>
    </div>
  );
}

// SpecialtySettingsResponse — локальный тип для компилятора. Полный набор
// полей (maxSubstitutions, presets, user, userId) не используется здесь.
export type { SpecialtySettingsResponse };
