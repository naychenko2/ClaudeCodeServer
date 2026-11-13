import { useEffect, useState, type ReactNode } from 'react';
import { Info } from 'lucide-react';
import { Badge, Notice, SegmentedControl, TextArea } from '../../components/ui';
import { C, FONT, R, SP } from '../../lib/design';
import { loadPromptSectionsCatalog } from '../../lib/specialties';
import { SectionLabel } from '../tasks/bits';
import {
  backToInherit, layerLabel, modeOf, nearLimit, offBySpecialty, overLimit, sectionCaption, setOff, setOwnText, startOwn,
  textCounter, toOverrides,
  type PromptSectionsState, type SectionMode,
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

export const OVER_LIMIT_TEXT = 'Длиннее 1024 символов — сократите, иначе не сохранится.';

const CHAIN_LAYERS = ['code', 'global', 'user', 'owner'] as const;

// Цепочка слоёв «Откуда текст» (P38): специальность [код → глобально → пользователь → владелец] → персона.
function LayerChain({ specialtyTitle }: { specialtyTitle: string }) {
  const arrow = <span aria-hidden style={{ color: C.textMuted }}>→</span>;
  return (
    <div
      data-testid="prompt-chain"
      aria-label="Откуда берётся текст секции"
      style={{ display: 'flex', alignItems: 'center', flexWrap: 'wrap', gap: SP.xs, fontFamily: FONT.sans, fontSize: 12.5, color: C.textMuted }}
    >
      <span>Откуда текст:</span>
      <span style={{
        display: 'inline-flex', alignItems: 'center', flexWrap: 'wrap', gap: SP.xs,
        border: `1px dashed ${C.border}`, borderRadius: R.lg, padding: '2px 8px',
      }}>
        <span>специальность «{specialtyTitle}»</span>
        {CHAIN_LAYERS.map((l, i) => (
          <span key={l} style={{ display: 'inline-flex', alignItems: 'center', gap: SP.xs }}>
            {i > 0 && arrow}
            <Badge tone="neutral" size="xs">{layerLabel(l)}</Badge>
          </span>
        ))}
      </span>
      {arrow}
      <Badge tone="accent" size="xs">{layerLabel('persona')}</Badge>
    </div>
  );
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
  // Название специальности персоны — для цепочки слоёв и подсказок
  specialtyTitle?: string;
  // B09: админ менял секции специальности с момента, когда человек их видел
  changedByAdminSince?: boolean | null;
  // Слоты контракта («Свои подразделы»)
  children: ReactNode;
}

export function PersonaPromptTab({
  state, onChange, hasSpecialty, specialtyChanged, isNew, loadError, disabled, isMobile, specialtyTitle = '', changedByAdminSince, children,
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
    const updated = state.effective.filter(s => s.updatedFromRole && modeOf(state, s.id) === 'inherit');
    const names = updated.map(s => `«${labels[s.id] ?? s.id}»`);
    specialtyBody = (
      <div style={{ display: 'flex', flexDirection: 'column', gap: 18 }}>
        {changedByAdminSince === true && (
          <Notice tone="info" icon={Info} data-testid="prompt-admin-changed">
            {names.length > 0
              ? `Админ только что поправил у «${specialtyTitle}» секции. Обновились у этой персоны: ${names.join(', ')} — они наследуются. Свои секции остались как были.`
              : `Админ только что поправил у «${specialtyTitle}» секции. У этой персоны они свои или выключены — текст остался как был.`}
          </Notice>
        )}
        {state.effective.map(s => {
          const mode = modeOf(state, s.id);
          const local = state.overrides[s.id];
          const draft = local && local.mode === 'own' ? local.text : s.text;
          const bySpec = offBySpecialty(s);
          const caption = sectionCaption(s, mode);
          const over = overLimit(draft);
          const sectionName = labels[s.id] ?? s.id;
          return (
            <div key={s.id} data-testid={`prompt-section-${s.id}`} style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
              <div style={{ display: 'flex', alignItems: 'baseline', gap: 8, flexWrap: 'wrap' }}>
                <span style={{ fontFamily: FONT.sans, fontSize: 14, fontWeight: 600, color: C.textHeading }}>
                  {sectionName}
                </span>
                {caption && (
                  <span style={{ fontFamily: FONT.sans, fontSize: 11.5, color: bySpec && mode === 'inherit' ? C.warningText : C.textMuted }}>
                    {caption}
                  </span>
                )}
                {mode === 'own' && <Badge tone="plan" size="xs">своё</Badge>}
                {mode === 'inherit' && s.updatedFromRole && <Badge tone="info" size="xs">обновилось от специальности</Badge>}
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
                  opacity: bySpec ? 0.7 : 1,
                }}>
                  {s.text}
                </div>
              )}
              {mode === 'inherit' && bySpec && (
                <div style={mutedBox} data-testid={`prompt-hint-${s.id}`}>
                  {`Админ выключил её для всех «${specialtyTitle}» — у персоны её нет, пока она наследует. Нужна именно этой персоне — «Своё»: текст специальности станет черновиком, и секция включится только у неё.`}
                </div>
              )}
              {mode === 'inherit' && !bySpec && (
                <div style={mutedBox} data-testid={`prompt-hint-${s.id}`}>
                  «Своё» подставит этот текст черновиком — дальше правите его как свой.
                </div>
              )}
              {mode === 'own' && (
                <>
                  <TextArea
                    value={draft}
                    onChange={v => onChange(setOwnText(state, s.id, v))}
                    autoGrow minHeight={120} maxHeight={320} disabled={disabled} isMobile={isMobile}
                    ariaLabel={`Свой текст секции «${sectionName}»`} invalid={over}
                  />
                  <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'baseline', gap: 8, flexWrap: 'wrap' }}>
                    <span
                      data-testid={`prompt-own-note-${s.id}`}
                      style={{ fontFamily: FONT.sans, fontSize: 12, lineHeight: 1.5, color: over ? C.dangerText : C.textMuted }}
                    >
                      {over
                        ? OVER_LIMIT_TEXT
                        : 'Правка специальности сюда больше не доедет. Вернуть общий текст — «Как у специальности».'}
                    </span>
                    <span
                      data-testid={`prompt-counter-${s.id}`}
                      style={{
                        fontFamily: FONT.sans, fontSize: 11.5, marginLeft: 'auto',
                        color: over ? C.dangerText : nearLimit(draft) ? C.warningText : C.textMuted,
                      }}
                    >
                      {textCounter(draft)}
                    </span>
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
      {!hasSpecialty && (
        <div style={{ ...mutedBox, paddingBottom: 12 }} data-testid="prompt-no-specialty">
          Без специальности секций нет — только свои подразделы ниже.
        </div>
      )}
      {hasSpecialty && (
        <div style={section} data-testid="prompt-from-specialty">
          <SectionLabel style={{ marginBottom: 12 }}>От специальности</SectionLabel>
          {!isNew && !specialtyChanged && (
            <>
              <LayerChain specialtyTitle={specialtyTitle} />
              <div style={{ ...mutedBox, fontSize: 12.5, margin: `${SP.xs}px 0 ${SP.md}px` }}>
                Побеждает последний слой, где секция задана. Подпись у секции говорит, из какого слоя пришёл текст.
              </div>
            </>
          )}
          {specialtyBody}
        </div>
      )}
      <div data-testid="prompt-own-slots" style={{ display: 'contents' }}>{children}</div>
    </>
  );
}
