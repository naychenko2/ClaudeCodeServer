import { useEffect, useState, type ReactNode } from 'react';
import { SegmentedControl, TextArea } from '../../components/ui';
import { C, FONT, R } from '../../lib/design';
import { loadPromptSectionsCatalog } from '../../lib/specialties';
import { SectionLabel } from '../tasks/bits';
import {
  backToInherit, modeOf, offBySpecialty, overLimit, setOff, setOwnText, startOwn, textCounter, toOverrides,
  type PersonaPromptSection, type PromptSectionsState, type SectionMode,
} from './promptSectionsModel';

// Вкладка «Промпт» формы персоны (P2b, разрез 2 §2.3): блок «От специальности» — секции с
// переключателем «как у специальности / своё / выключено» — и блок «Свои подразделы»
// (слоты контракта, приезжают снаружи как children: поведение прежнее, только переезд).
// Состояние секций держит форма (оно нужно её сохранению и dirty) — вкладка управляется.

// Есть ли у персоны свои секции — то, что потеряет смена специальности (сервер сбрасывает
// слой при реальной смене, PersonaManager.ResetsPromptSections).
export function hasOwnSections(state: PromptSectionsState): boolean {
  return toOverrides(state).length > 0;
}

// Нужно ли подтверждение смены специальности: свои секции есть и специальность ещё не сменена
// (после первой смены терять уже нечего — слой и так будет сброшен).
export function sectionsResetNeedsConfirm(state: PromptSectionsState, specialtyChanged: boolean): boolean {
  return !specialtyChanged && hasOwnSections(state);
}

export const SECTIONS_RESET_TITLE = 'Сбросить свои секции промпта?';
export const SECTIONS_RESET_TEXT = 'Свои секции промпта будут сброшены: у новой специальности другие правила роли.';

const MODE_OPTIONS: { value: SectionMode; label: string }[] = [
  { value: 'inherit', label: 'Как у специальности' },
  { value: 'own', label: 'Своё' },
  { value: 'off', label: 'Выключено' },
];

function sourceBadge(section: PersonaPromptSection): string {
  return section.textSource === 'code' ? 'по умолчанию' : 'настроено админом';
}

const mutedBox: React.CSSProperties = {
  fontFamily: FONT.sans, fontSize: 13, lineHeight: 1.5, color: C.textMuted,
};

interface Props {
  state: PromptSectionsState;
  onChange: (next: PromptSectionsState) => void;
  // У Specialty=None блок «От специальности» скрыт
  hasSpecialty: boolean;
  // Специальность в форме изменена, но не сохранена: секции новой роли придут после сохранения
  specialtyChanged: boolean;
  // Нет сохранённой персоны (создание): секций ещё нет
  isNew: boolean;
  loadError: string | null;
  disabled?: boolean;
  isMobile: boolean;
  // Слоты контракта («Свои подразделы»)
  children: ReactNode;
}

export function PersonaPromptTab({
  state, onChange, hasSpecialty, specialtyChanged, isNew, loadError, disabled, isMobile, children,
}: Props) {
  const [labels, setLabels] = useState<Record<string, string>>({});
  useEffect(() => {
    let alive = true;
    void loadPromptSectionsCatalog().then(c => {
      if (alive && c) setLabels(Object.fromEntries(c.sections.map(s => [s.id, s.label])));
    });
    return () => { alive = false; };
  }, []);

  const section: React.CSSProperties = { borderTop: `1px solid ${C.borderLight}`, paddingTop: 22 };

  let specialtyBody: ReactNode;
  if (isNew) {
    specialtyBody = <div style={mutedBox}>Секции специальности можно настроить после сохранения персоны.</div>;
  } else if (specialtyChanged) {
    specialtyBody = <div style={mutedBox}>Секции новой специальности появятся после сохранения — свои секции будут сброшены.</div>;
  } else if (loadError) {
    specialtyBody = <div style={{ ...mutedBox, color: C.dangerText }}>{loadError}</div>;
  } else if (state.effective.length === 0) {
    specialtyBody = <div style={mutedBox}>Загрузка секций…</div>;
  } else {
    specialtyBody = (
      <div style={{ display: 'flex', flexDirection: 'column', gap: 18 }}>
        {state.effective.map(s => {
          const mode = modeOf(state, s.id);
          const local = state.overrides[s.id];
          const draft = local && local.mode === 'own' ? local.text : s.text;
          const bySpec = offBySpecialty(s);
          return (
            <div key={s.id} data-testid={`prompt-section-${s.id}`} style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
              <div style={{ display: 'flex', alignItems: 'baseline', gap: 8, flexWrap: 'wrap' }}>
                <span style={{ fontFamily: FONT.sans, fontSize: 14, fontWeight: 600, color: C.textHeading }}>
                  {labels[s.id] ?? s.id}
                </span>
                {mode === 'inherit' && (
                  <span style={{ fontFamily: FONT.sans, fontSize: 11.5, color: C.textMuted }}>
                    {bySpec ? 'выключено специальностью' : sourceBadge(s)}
                  </span>
                )}
              </div>
              <SegmentedControl<SectionMode>
                value={mode}
                options={MODE_OPTIONS}
                touch={isMobile}
                onChange={next => {
                  if (disabled || next === mode) return;
                  if (next === 'own') onChange(startOwn(state, s.id));
                  else if (next === 'off') onChange(setOff(state, s.id));
                  else onChange(backToInherit(state, s.id));
                }}
              />
              {mode === 'inherit' && (
                <div style={{
                  fontFamily: FONT.sans, fontSize: 13, lineHeight: 1.5, color: C.textMuted,
                  background: C.bgPanel, border: `1px solid ${C.borderLight}`, borderRadius: R.lg,
                  padding: '9px 12px', whiteSpace: 'pre-wrap', overflowWrap: 'anywhere',
                }}>
                  {s.text}
                </div>
              )}
              {mode === 'own' && (
                <>
                  <TextArea
                    value={draft}
                    onChange={v => onChange(setOwnText(state, s.id, v))}
                    autoGrow minHeight={90} maxHeight={320} disabled={disabled} isMobile={isMobile}
                  />
                  <div style={{
                    alignSelf: 'flex-end', fontFamily: FONT.sans, fontSize: 11.5,
                    color: overLimit(draft) ? C.dangerText : C.textMuted,
                  }}>
                    {textCounter(draft)}
                  </div>
                </>
              )}
              {mode === 'off' && (
                <div style={mutedBox}>Секция не попадает в промпт этой персоны.</div>
              )}
            </div>
          );
        })}
      </div>
    );
  }

  return (
    <>
      {hasSpecialty && (
        <div style={section} data-testid="prompt-from-specialty">
          <SectionLabel style={{ marginBottom: 12 }}>От специальности</SectionLabel>
          {specialtyBody}
        </div>
      )}
      <div data-testid="prompt-own-slots" style={{ display: 'contents' }}>{children}</div>
    </>
  );
}
