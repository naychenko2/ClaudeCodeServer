// Вкладка «Проактивность» персоны: правила «событие → действие». Персона сама
// реагирует на события (таймер, файлы, заметки, коммиты, смена статуса задач,
// @упоминания). Правила живут в Persona.AutomationRules; создание и редактирование —
// ИНЛАЙН (без модалки), по образцу вкладки «Умения и правила» (PersonaBindingsPanel):
// карточка раскрывается на месте для редактирования, добавление — степпер под списком.
//
// Отличие от привязок в модели сохранения: у формы правила ~20 полей (в т.ч. текстовые),
// поэтому вместо мгновенного автосохранения по каждому полю — явные кнопки
// «Сохранить»/«Отмена» у раскрытой карточки и «Добавить правило»/«Отмена» у степпера.
// Черновик (FormState) живёт локально, пока карточка/степпер открыты; PUT/POST уходит
// только по кнопке. Общие рендер-блоки формы (тип+параметры триггера/действие/TTL/
// троттлинг) — в ./automationForm, степпер — в ./stepperUi (общий с привязками).

import { useRef, useState } from 'react';
import type { CSSProperties } from 'react';
import {
  Plus, Zap, FlaskConical, Pencil, Trash2, CheckCircle2, Power, X, Check as CheckIcon,
} from 'lucide-react';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import type { AutomationRuleDto, Persona, PersonaAutomationRule, Project } from '../../types';
import { C, FONT, R } from '../../lib/design';
import { api } from '../../lib/api';
import { bumpPersonas } from '../../lib/personas';
import { Button, Field, Menu, MenuItem, SegmentedControl, TextField, Toggle } from '../../components/ui';
import { SectionLabel } from '../tasks/bits';
import { TRIGGER_META, ACTION_META, triggerDetails, rulesCounter } from './automationMeta';
import { Stepper, Crumb } from './stepperUi';
import type { FormState } from './automationForm';
import {
  TRIGGER_OPTIONS, initialForm, buildDto, draftSummary, triggerSummary, requiresMissingProject,
  TriggerTypeGrid, TriggerParamsFields, ActionFields, TtlFields, ThrottleDisclosure,
} from './automationForm';

// Состояние инлайн-степпера добавления: шаг + черновик формы
interface AddState {
  step: 1 | 2 | 3;
  draft: FormState;
}

// Кандидаты AI-подбора с чекбоксами (по образцу PersonaBindingsPanel.SuggestState)
interface SuggestState {
  loading: boolean;
  candidates?: (PersonaAutomationRule & { on: boolean })[];
  error?: string;
  adding?: boolean;
}

// Кандидат → DTO создания правила (поля 1:1 с PersonaAutomationRule — сервер уже
// возвращает валидированную модель, конвертация чисто структурная)
function candidateToDto(c: PersonaAutomationRule): AutomationRuleDto {
  return {
    name: c.name,
    enabled: c.enabled,
    triggerType: c.trigger.type,
    triggerArgs: c.trigger.args ?? {},
    conditionOnlyIf: c.condition?.onlyIf ?? null,
    quietFrom: c.condition?.quietFrom ?? null,
    quietTo: c.condition?.quietTo ?? null,
    minIntervalMinutes: c.condition?.minIntervalMinutes ?? null,
    actionWeight: c.action.weight,
    actionInstruction: c.action.instruction,
    rememberInHistory: c.action.rememberInHistory,
    actionExpiresAfterMinutes: c.action.expiresAfterMinutes ?? null,
  };
}

export function PersonaAutomationPanel({ persona, projects, accent, isMobile }: {
  persona: Persona; projects: Project[]; accent: string; isMobile?: boolean;
}) {
  const rules = persona.automationRules ?? [];

  // Редактирование раскрытой карточки — черновик локален, PUT только по «Сохранить»
  const [expandedId, setExpandedId] = useState<string | null>(null);
  const [draft, setDraft] = useState<FormState | null>(null);
  const [saving, setSaving] = useState(false);
  const [testBusyId, setTestBusyId] = useState<string | null>(null);
  const [menuId, setMenuId] = useState<string | null>(null);
  const [hoveredId, setHoveredId] = useState<string | null>(null);
  const [confirmId, setConfirmId] = useState<string | null>(null);
  const confirmTimer = useRef<number | null>(null);

  // Добавление — инлайн-степпер под списком
  const [adding, setAdding] = useState<AddState | null>(null);
  const [addSaving, setAddSaving] = useState(false);

  // AI-подбор правил — кандидаты с чекбоксами (по образцу привязок)
  const [suggest, setSuggest] = useState<SuggestState | null>(null);

  const setDraftField = <K extends keyof FormState>(key: K, value: FormState[K]) =>
    setDraft(prev => prev ? { ...prev, [key]: value } : prev);

  function armDelete(ruleId: string) {
    setConfirmId(ruleId);
    if (confirmTimer.current) window.clearTimeout(confirmTimer.current);
    confirmTimer.current = window.setTimeout(() => setConfirmId(null), 3000);
  }

  function openEdit(rule: PersonaAutomationRule) {
    setMenuId(null);
    setAdding(null);
    setConfirmId(null);
    setExpandedId(rule.id);
    setDraft(initialForm(rule, projects));
  }
  function closeEdit() {
    setExpandedId(null);
    setDraft(null);
    setConfirmId(null);
  }

  async function saveEdit(rule: PersonaAutomationRule) {
    if (!draft) return;
    setSaving(true);
    try {
      await api.personas.updateAutomation(persona.id, rule.id, buildDto(draft));
      // Дожидаемся обновления стора персон ДО закрытия — иначе повторное открытие
      // «Редактировать» может увидеть снимок до сохранения (гонка с realtime personas_changed)
      await bumpPersonas();
      closeEdit();
    } catch (e: any) {
      window.alert(e?.message ?? 'Не удалось сохранить правило');
    } finally { setSaving(false); }
  }

  async function toggleEnabled(rule: PersonaAutomationRule, enabled: boolean) {
    try { await api.personas.updateAutomation(persona.id, rule.id, { enabled }); }
    catch { /* realtime вернёт актуальное состояние */ }
  }
  async function test(rule: PersonaAutomationRule) {
    setTestBusyId(rule.id);
    try { await api.personas.testAutomation(persona.id, rule.id); }
    catch { /* молча: ход пойдёт в фоне */ }
    finally { setTestBusyId(null); }
  }
  async function remove(rule: PersonaAutomationRule) {
    try {
      await api.personas.removeAutomation(persona.id, rule.id);
      setConfirmId(null);
      if (expandedId === rule.id) closeEdit();
    } catch { /* noop */ }
  }
  function onAskDelete(rule: PersonaAutomationRule) {
    if (confirmId === rule.id) { void remove(rule); return; }
    armDelete(rule.id);
  }

  function openAdd() {
    closeEdit();
    setSuggest(null);
    setAdding({ step: 1, draft: initialForm(null, projects) });
  }
  async function commitAdd() {
    if (!adding) return;
    setAddSaving(true);
    try {
      await api.personas.addAutomation(persona.id, buildDto(adding.draft));
      await bumpPersonas();
      setAdding(null);
    } catch (e: any) {
      window.alert(e?.message ?? 'Не удалось создать правило');
    } finally { setAddSaving(false); }
  }

  // «✨ Подобрать автоматически» — правила под роль персоны, ничего не сохраняется,
  // пока не подтверждено (по образцу PersonaBindingsPanel.runSuggest)
  async function runSuggest() {
    closeEdit();
    setAdding(null);
    setSuggest({ loading: true });
    try {
      const { candidates } = await api.personas.suggestAutomation(persona.id);
      setSuggest({ loading: false, candidates: candidates.map(c => ({ ...c, on: true })) });
    } catch (e: any) {
      setSuggest({ loading: false, error: e?.message ?? 'Не удалось подобрать правила. Попробуйте ещё раз.' });
    }
  }

  function toggleCandidate(id: string) {
    setSuggest(s => s?.candidates
      ? { ...s, candidates: s.candidates.map(c => c.id === id ? { ...c, on: !c.on } : c) }
      : s);
  }

  async function acceptSuggest() {
    const picked = (suggest?.candidates ?? []).filter(c => c.on);
    if (picked.length === 0) { setSuggest(null); return; }
    setSuggest(s => s ? { ...s, adding: true } : s);
    try {
      for (const c of picked) await api.personas.addAutomation(persona.id, candidateToDto(c));
      await bumpPersonas();
      setSuggest(null);
    } catch (e: any) {
      window.alert(e?.message ?? 'Не удалось добавить правила.');
      setSuggest(null);
    }
  }

  return (
    <div style={{ height: '100%', overflowY: 'auto', background: C.bgMain }}>
      <div style={{
        maxWidth: 680, margin: '0 auto', boxSizing: 'border-box',
        padding: isMobile ? '20px 16px 32px' : '26px 32px 40px',
        display: 'flex', flexDirection: 'column', gap: 0, fontFamily: FONT.sans,
      }}>
        {/* Заголовок секции + счётчик + подзаголовок (как в «Умениях и правилах») */}
        <div style={{ display: 'flex', alignItems: 'baseline', justifyContent: 'space-between', gap: 10 }}>
          <SectionLabel>Проактивность</SectionLabel>
          <span style={{ fontSize: 11.5, color: C.textMuted, flexShrink: 0 }}>
            {rulesCounter(rules)}
          </span>
        </div>
        <div style={{ fontSize: 12.5, color: C.textMuted, lineHeight: 1.5, marginTop: 4 }}>
          Персона сама реагирует на события — таймер, файлы, заметки, коммиты, смену статуса задач и @упоминания.
        </div>

        {/* Список правил */}
        {rules.length > 0 && (
          <div style={{ display: 'flex', flexDirection: 'column', gap: 8, marginTop: 14 }}>
            {rules.map(rule => (
              <RuleCard
                key={rule.id}
                rule={rule}
                accent={accent}
                projects={projects}
                isMobile={isMobile}
                open={expandedId === rule.id}
                draft={expandedId === rule.id ? draft : null}
                setDraftField={setDraftField}
                saving={saving && expandedId === rule.id}
                testBusy={testBusyId === rule.id}
                menuOpen={menuId === rule.id}
                hovered={hoveredId === rule.id}
                confirming={confirmId === rule.id}
                onHover={v => setHoveredId(h => (v ? rule.id : (h === rule.id ? null : h)))}
                onToggleCard={() => (expandedId === rule.id ? closeEdit() : openEdit(rule))}
                onToggleEnabled={v => { setMenuId(null); void toggleEnabled(rule, v); }}
                onTest={() => void test(rule)}
                onEdit={() => openEdit(rule)}
                onAskDelete={() => onAskDelete(rule)}
                onToggleMenu={() => setMenuId(m => m === rule.id ? null : rule.id)}
                onCloseMenu={() => setMenuId(null)}
                onCancelEdit={closeEdit}
                onSaveEdit={() => void saveEdit(rule)}
              />
            ))}
          </div>
        )}

        {/* Пустое состояние — внутри 680-контейнера, в духе привязок */}
        {rules.length === 0 && !adding && !suggest && (
          <EmptyState onCreate={openAdd} onSuggest={() => void runSuggest()} />
        )}

        {/* Кнопки под списком (скрыты, пока открыт степпер/подбор) */}
        {rules.length > 0 && !adding && !suggest && (
          <div style={{ display: 'flex', gap: 10, flexWrap: 'wrap', marginTop: 12 }}>
            <AddRuleButton onClick={openAdd} accent={accent} />
            <Button variant="ghostAccent" size="sm" onClick={() => void runSuggest()}>
              ✨ Подобрать автоматически
            </Button>
          </div>
        )}

        {/* Инлайн-степпер добавления: ① Событие → ② Параметры → ③ Правило */}
        {adding && (
          <AddRulePanel
            state={adding}
            accent={accent}
            isMobile={isMobile}
            projects={projects}
            saving={addSaving}
            onChange={setAdding}
            onClose={() => setAdding(null)}
            onCommit={() => void commitAdd()}
          />
        )}

        {/* AI-подбор: спиннер / кандидаты с чекбоксами (по образцу привязок) */}
        {suggest && (
          <div style={{ borderTop: `1px solid ${C.borderLight}`, marginTop: 14, paddingTop: 18 }}>
            {suggest.loading ? (
              <div style={{ display: 'flex', alignItems: 'center', gap: 8, fontSize: 12.5, color: C.textMuted }}>
                <span style={pulseDot} />
                Подбираю правила под роль персоны — до минуты
                <style>{'@keyframes cc-auto-pulse { 0%, 100% { opacity: 0.35; } 50% { opacity: 1; } }'}</style>
              </div>
            ) : suggest.error ? (
              <div style={{ fontSize: 12.5, color: C.dangerText }}>
                {suggest.error}{' '}
                <button onClick={() => void runSuggest()} style={linkBtn}>Повторить</button>{' '}
                <button onClick={() => setSuggest(null)} style={{ ...linkBtn, color: C.textMuted }}>Закрыть</button>
              </div>
            ) : (suggest.candidates ?? []).length === 0 ? (
              <div style={{ fontSize: 12.5, color: C.textMuted }}>
                Ничего подходящего не нашлось — попробуйте создать правило вручную.{' '}
                <button onClick={() => setSuggest(null)} style={linkBtn}>Закрыть</button>
              </div>
            ) : (
              <>
                <div style={{ display: 'flex', alignItems: 'baseline', justifyContent: 'space-between', gap: 10 }}>
                  <SectionLabel>Правила подобраны автоматически</SectionLabel>
                  <span style={{ fontSize: 11.5, color: C.textMuted, flexShrink: 0 }}>
                    {suggest.candidates!.filter(c => c.on).length} из {suggest.candidates!.length}
                  </span>
                </div>
                <div style={{ display: 'flex', flexDirection: 'column', gap: 8, marginTop: 12 }}>
                  {suggest.candidates!.map(c => {
                    const trig = TRIGGER_META[c.trigger.type] ?? TRIGGER_META.timer;
                    const details = triggerDetails(c, projects);
                    return (
                      <div
                        key={c.id}
                        onClick={() => toggleCandidate(c.id)}
                        style={{
                          display: 'flex', alignItems: 'center', gap: 12, cursor: 'pointer',
                          background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.xl,
                          padding: '10px 14px', opacity: c.on ? 1 : 0.5,
                        }}
                      >
                        <SuggestCheck on={c.on} />
                        <TriggerTypeIcon type={c.trigger.type} />
                        <div style={{ flex: 1, minWidth: 0 }}>
                          <div style={{ fontSize: 13.5, fontWeight: 600, color: C.textHeading }}>{c.name}</div>
                          <div style={{ fontSize: 12, color: C.textSecondary, marginTop: 1, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                            {trig.label}{details ? ' · ' + details : ''} · {c.action.instruction}
                          </div>
                        </div>
                      </div>
                    );
                  })}
                </div>
                <div style={{ display: 'flex', alignItems: 'center', gap: 8, marginTop: 12, fontSize: 12, color: C.textMuted }}>
                  ✨ Предложено ИИ по роли персоны. Ничего не сохранено, пока вы не подтвердите.
                </div>
                <div style={{ display: 'flex', justifyContent: 'flex-end', gap: 8, marginTop: 14 }}>
                  <Button variant="ghost" size="sm" disabled={suggest.adding} onClick={() => setSuggest(null)}>Отмена</Button>
                  <Button variant="primary" size="sm" loading={suggest.adding}
                    disabled={suggest.adding || suggest.candidates!.every(c => !c.on)}
                    onClick={() => void acceptSuggest()}>
                    {suggest.adding ? 'Добавляю…' : 'Добавить выбранные'}
                  </Button>
                </div>
              </>
            )}
          </div>
        )}
      </div>
    </div>
  );
}

// ─── Карточка правила: свёрнутая строка / раскрытое редактирование на месте ────

function RuleCard({
  rule, accent, projects, isMobile, open, draft, setDraftField, saving, testBusy,
  menuOpen, hovered, confirming, onHover, onToggleCard, onToggleEnabled, onTest,
  onEdit, onAskDelete, onToggleMenu, onCloseMenu, onCancelEdit, onSaveEdit,
}: {
  rule: PersonaAutomationRule;
  accent: string;
  projects: Project[];
  isMobile?: boolean;
  open: boolean;
  draft: FormState | null;
  setDraftField: <K extends keyof FormState>(key: K, value: FormState[K]) => void;
  saving: boolean;
  testBusy: boolean;
  menuOpen: boolean;
  hovered: boolean;
  confirming: boolean;
  onHover: (v: boolean) => void;
  onToggleCard: () => void;
  onToggleEnabled: (v: boolean) => void;
  onTest: () => void;
  onEdit: () => void;
  onAskDelete: () => void;
  onToggleMenu: () => void;
  onCloseMenu: () => void;
  onCancelEdit: () => void;
  onSaveEdit: () => void;
}) {
  const trig = TRIGGER_META[rule.trigger.type] ?? TRIGGER_META.timer;
  const act = ACTION_META[rule.action.weight] ?? ACTION_META.gate;
  const savedDetails = triggerDetails(rule, projects);
  const savedSubtitle = `${trig.label}${savedDetails ? ' · ' + savedDetails : ''} · ${act.label}`;
  const liveSubtitle = open && draft ? draftSummary(draft, projects) : savedSubtitle;
  const dim = !rule.enabled && !open;

  return (
    <div
      onMouseEnter={() => onHover(true)}
      onMouseLeave={() => onHover(false)}
      style={{
        background: C.bgWhite,
        border: `1px solid ${open || hovered || menuOpen ? accent : C.border}`,
        borderRadius: R.xl, padding: '10px 14px',
        transition: 'border-color 0.15s, background 0.6s',
      }}
    >
      {/* Шапка: свёрнуто — текст, раскрыто — поля имя+тумблер (без дублирующего блока) */}
      <div
        onClick={!open ? onToggleCard : undefined}
        style={{ display: 'flex', alignItems: 'center', gap: 12, cursor: open ? 'default' : 'pointer' }}
      >
        <TriggerTypeIcon type={rule.trigger.type} dim={dim} />

        <div style={{ flex: 1, minWidth: 0, opacity: dim ? 0.55 : 1 }}>
          {open && draft ? (
            <TextField value={draft.name} onChange={v => setDraftField('name', v)} placeholder="Без названия" autoFocus />
          ) : (
            <div style={{
              fontSize: 13.5, fontWeight: 600, color: C.textHeading,
              overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
            }}>
              {rule.name || 'Без названия'}
            </div>
          )}
          <div style={{
            fontSize: 12, color: C.textSecondary, marginTop: open ? 6 : 1,
            overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
          }}>
            {liveSubtitle}
          </div>
        </div>

        <div
          style={{ display: 'flex', alignItems: 'center', gap: 8, flexShrink: 0 }}
          onClick={e => e.stopPropagation()}
        >
          {open && draft ? (
            <ToggleLabel checked={draft.enabled} onChange={v => setDraftField('enabled', v)} />
          ) : (
            <RuleStatusBadge enabled={rule.enabled} />
          )}
          {!open && (
            <div style={{ position: 'relative' }}>
              <button
                onClick={onToggleMenu}
                aria-label="Действия"
                disabled={testBusy}
                style={{
                  width: isMobile ? 36 : 28, height: isMobile ? 36 : 28, border: 'none',
                  background: 'transparent', borderRadius: R.md, cursor: 'pointer',
                  color: C.textMuted, fontSize: 16, lineHeight: 1,
                  visibility: isMobile || hovered || menuOpen ? 'visible' : 'hidden',
                }}
              >⋯</button>
              {menuOpen && (
                <Menu onClose={onCloseMenu} align="right" top={30} minWidth={180}>
                  <MenuItem
                    icon={<Pencil size={15} strokeWidth={ICON_STROKE} />}
                    label="Редактировать"
                    onClick={onEdit}
                  />
                  <MenuItem
                    icon={<FlaskConical size={15} strokeWidth={ICON_STROKE} />}
                    label={testBusy ? 'Запускаю…' : 'Проверить'}
                    disabled={testBusy}
                    onClick={() => { onCloseMenu(); onTest(); }}
                  />
                  <MenuItem
                    icon={rule.enabled
                      ? <Power size={15} strokeWidth={ICON_STROKE} />
                      : <CheckCircle2 size={15} strokeWidth={ICON_STROKE} />}
                    label={rule.enabled ? 'Выключить' : 'Включить'}
                    onClick={() => onToggleEnabled(!rule.enabled)}
                  />
                  <div style={{ height: 1, background: C.borderLight, margin: '4px 6px' }} />
                  <MenuItem
                    danger
                    icon={<Trash2 size={15} strokeWidth={ICON_STROKE} />}
                    label="Удалить"
                    onClick={() => { onCloseMenu(); onEdit(); onAskDelete(); }}
                  />
                </Menu>
              )}
            </div>
          )}
        </div>
      </div>

      {/* Раскрытое тело — редактирование по месту, сохранение явной кнопкой */}
      {open && draft && (
        <div style={{ borderTop: `1px solid ${C.borderLight}`, marginTop: 10, paddingTop: 14 }}>
          <div style={{ display: 'flex', flexDirection: 'column', gap: 16 }}>
            <Field label="Событие" hint="Что запускает реакцию персоны.">
              <SegmentedControl
                value={draft.triggerType}
                options={TRIGGER_OPTIONS}
                onChange={v => setDraftField('triggerType', v)}
                columns={isMobile ? 2 : 3}
              />
            </Field>
            <TriggerParamsFields f={draft} set={setDraftField} projects={projects} isMobile={isMobile} />
            <ActionFields f={draft} set={setDraftField} />
            <TtlFields f={draft} set={setDraftField} isMobile={isMobile} />
            <ThrottleDisclosure f={draft} set={setDraftField} />
          </div>

          <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginTop: 16, flexWrap: 'wrap', gap: 8 }}>
            <button onClick={onAskDelete} style={delLink}>
              {confirming ? 'Точно удалить?' : 'Удалить правило'}
            </button>
            <div style={{ display: 'flex', gap: 8, flexWrap: 'wrap' }}>
              <Button variant="ghost" size="sm" disabled={testBusy} onClick={onTest}>
                {testBusy ? 'Запускаю…' : 'Проверить'}
              </Button>
              <Button variant="ghost" size="sm" onClick={onCancelEdit}>Отмена</Button>
              <Button variant="primary" size="sm" loading={saving} onClick={onSaveEdit}>Сохранить</Button>
            </div>
          </div>
        </div>
      )}
    </div>
  );
}

// ─── Инлайн-степпер «Добавить правило»: ① Событие → ② Параметры → ③ Правило ────

function AddRulePanel({ state, accent, isMobile, projects, saving, onChange, onClose, onCommit }: {
  state: AddState;
  accent: string;
  isMobile?: boolean;
  projects: Project[];
  saving: boolean;
  onChange: (next: AddState) => void;
  onClose: () => void;
  onCommit: () => void;
}) {
  const { step, draft } = state;
  const set = <K extends keyof FormState>(key: K, value: FormState[K]) =>
    onChange({ step, draft: { ...draft, [key]: value } });
  const trig = TRIGGER_META[draft.triggerType];

  return (
    <div style={{ borderTop: `1px solid ${C.borderLight}`, marginTop: 14, paddingTop: 18 }}>
      <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between' }}>
        <span style={{ fontSize: 13.5, fontWeight: 600, color: C.textHeading }}>Добавить правило</span>
        <button onClick={onClose} aria-label="Закрыть" style={xBtn}><X size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} /></button>
      </div>

      <Stepper
        step={step}
        accent={accent}
        steps={[{ n: 1, label: 'Событие' }, { n: 2, label: 'Параметры' }, { n: 3, label: 'Правило' }]}
        // Назад — не сбрасываем черновик: параметры триггера трудоёмкие, терять их
        // при возврате на шаг раньше (в отличие от шага «Цель» у привязок) не нужно
        onStep={s => { if (s < step) onChange({ step: s as 1 | 2 | 3, draft }); }}
      />

      {step === 1 && (
        <TriggerTypeGrid
          value={draft.triggerType}
          isMobile={isMobile}
          onPick={t => onChange({ step: 2, draft: { ...draft, triggerType: t } })}
        />
      )}

      {step === 2 && (
        <div style={{ marginTop: 14 }}>
          <TriggerParamsFields f={draft} set={set} projects={projects} isMobile={isMobile} />
          <div style={{ display: 'flex', justifyContent: 'flex-end', marginTop: 14 }}>
            <Button
              variant="primary" size="sm"
              disabled={requiresMissingProject(draft)}
              onClick={() => onChange({ step: 3, draft })}
            >
              Далее
            </Button>
          </div>
        </div>
      )}

      {step === 3 && (
        <div style={{ marginTop: 14, display: 'flex', flexDirection: 'column', gap: 16 }}>
          <Crumb onClick={() => onChange({ step: 2, draft })}>
            <trig.Icon size={13} strokeWidth={ICON_STROKE} /> {triggerSummary(draft, projects)}
          </Crumb>
          <Field label="Название правила" hint="Необязательно — можно оставить пустым.">
            <TextField value={draft.name} onChange={v => set('name', v)} placeholder="Напр. «Следить за релизами»" />
          </Field>
          <ActionFields f={draft} set={set} />
          <TtlFields f={draft} set={set} isMobile={isMobile} />
          <ThrottleDisclosure f={draft} set={set} />

          <div style={{ display: 'flex', justifyContent: 'flex-end', gap: 8 }}>
            <Button variant="ghost" size="sm" onClick={onClose}>Отмена</Button>
            <Button variant="primary" size="sm" loading={saving} onClick={onCommit}>Добавить правило</Button>
          </div>
        </div>
      )}
    </div>
  );
}

// ─── Круглая иконка типа триггера ───────────────────────────────────────────────

function TriggerTypeIcon({ type, size = 32, dim }: { type: PersonaAutomationRule['trigger']['type']; size?: number; dim?: boolean }) {
  const meta = TRIGGER_META[type] ?? TRIGGER_META.timer;
  return (
    <span style={{
      width: size, height: size, borderRadius: R.full, flexShrink: 0,
      display: 'flex', alignItems: 'center', justifyContent: 'center',
      background: dim ? C.bgSelected : meta.bg, color: dim ? C.textMuted : meta.fg,
    }}>
      <meta.Icon size={size >= 32 ? 16 : 13} strokeWidth={ICON_STROKE} />
    </span>
  );
}

// ─── Бейдж статуса правила ──────────────────────────────────────────────────────

function RuleStatusBadge({ enabled }: { enabled: boolean }) {
  const bg = enabled ? C.accentLight : C.bgSelected;
  const fg = enabled ? C.accent : C.textMuted;
  return (
    <span style={{
      borderRadius: R.pill, padding: '2px 8px', fontSize: 10.5, fontWeight: 600,
      letterSpacing: '0.02em', background: bg, color: fg, flexShrink: 0, whiteSpace: 'nowrap',
    }}>
      {enabled ? 'активно' : 'выкл'}
    </span>
  );
}

// Toggle + подпись «вкл/выкл» — используется в шапке раскрытой карточки
function ToggleLabel({ checked, onChange }: { checked: boolean; onChange: (v: boolean) => void }) {
  return (
    <div style={{ display: 'flex', flexDirection: 'column', alignItems: 'center', gap: 5, flexShrink: 0 }}>
      <Toggle checked={checked} onChange={onChange} />
      <span style={{ fontSize: 11, color: C.textMuted, fontFamily: FONT.sans }}>{checked ? 'вкл' : 'выкл'}</span>
    </div>
  );
}

// ─── Empty-state ────────────────────────────────────────────────────────────────

function EmptyState({ onCreate, onSuggest }: { onCreate: () => void; onSuggest: () => void }) {
  return (
    <div style={{
      marginTop: 14, border: `1.5px dashed ${C.dashed}`, borderRadius: R.xl,
      padding: '24px 22px', textAlign: 'center',
      display: 'flex', flexDirection: 'column', alignItems: 'center', gap: 12,
    }}>
      <div style={{ color: C.textMuted }}>
        <Zap size={26} strokeWidth={1.5} />
      </div>
      <div style={{ fontSize: 13.5, fontWeight: 600, color: C.textHeading }}>
        Подключи первое правило
      </div>
      <div style={{ maxWidth: 360, fontSize: 12.5, color: C.textSecondary, lineHeight: 1.5 }}>
        Персона будет сама реагировать на события: таймер, изменения файлов и заметок,
        новые коммиты, смену статуса задач и @упоминания. По умолчанию она сначала решает,
        стоит ли вмешиваться.
      </div>
      <div style={{ display: 'flex', gap: 10, flexWrap: 'wrap', justifyContent: 'center' }}>
        <button onClick={onCreate} style={emptyAddBtn}>
          <Plus size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} style={{ flexShrink: 0 }} />
          Создать правило
        </button>
        <Button variant="ghostAccent" size="sm" onClick={onSuggest}>
          ✨ Подобрать автоматически
        </Button>
      </div>
    </div>
  );
}

// Квадратный чекбокс кандидата подбора (копия PersonaBindingsPanel.Check)
function SuggestCheck({ on }: { on: boolean }) {
  return (
    <span style={{
      width: 18, height: 18, borderRadius: 5, flexShrink: 0,
      border: `1.5px solid ${on ? C.accent : C.border}`,
      background: on ? C.accent : C.bgWhite,
      display: 'flex', alignItems: 'center', justifyContent: 'center', color: '#fff',
    }}>
      {on && <CheckIcon size={12} strokeWidth={ICON_STROKE} />}
    </span>
  );
}

// Пунктирная кнопка «+ Добавить правило» (как AddBindingButton)
function AddRuleButton({ onClick, accent }: { onClick: () => void; accent: string }) {
  return (
    <button
      onClick={onClick}
      onMouseEnter={e => { e.currentTarget.style.borderColor = accent; e.currentTarget.style.color = accent; }}
      onMouseLeave={e => { e.currentTarget.style.borderColor = C.dashed; e.currentTarget.style.color = C.textSecondary; }}
      style={{
        display: 'inline-flex', alignItems: 'center', gap: 6,
        border: `1.5px dashed ${C.dashed}`, background: 'transparent', color: C.textSecondary,
        borderRadius: R.lg, padding: '7px 14px', fontSize: 12.5, fontWeight: 600,
        cursor: 'pointer', fontFamily: FONT.sans, transition: 'border-color 0.15s, color 0.15s',
      }}
    >
      <Plus size={14} strokeWidth={ICON_STROKE} style={{ flexShrink: 0 }} />
      Добавить правило
    </button>
  );
}

// ─── Стили ──────────────────────────────────────────────────────────────────────

const emptyAddBtn: CSSProperties = {
  display: 'inline-flex', alignItems: 'center', gap: 7,
  border: `1px solid ${C.accent}`, background: C.accent, color: C.onAccent,
  borderRadius: R.lg, padding: '8px 14px', cursor: 'pointer',
  fontFamily: FONT.sans, fontSize: 13, fontWeight: 600,
};

const delLink: CSSProperties = {
  border: 'none', background: 'none', fontSize: 12.5, fontWeight: 600,
  color: C.dangerText, padding: '4px 0', cursor: 'pointer', fontFamily: FONT.sans,
};

const xBtn: CSSProperties = {
  width: 28, height: 28, border: 'none', background: 'transparent', borderRadius: R.md,
  color: C.textMuted, cursor: 'pointer',
  display: 'flex', alignItems: 'center', justifyContent: 'center',
};

const linkBtn: CSSProperties = {
  background: 'transparent', border: 'none', color: C.accent, cursor: 'pointer',
  fontSize: 13, fontFamily: FONT.sans, textDecoration: 'underline', padding: 0,
};

const pulseDot: CSSProperties = {
  width: 8, height: 8, borderRadius: R.full, background: C.accent, flexShrink: 0,
  animation: 'cc-auto-pulse 1.2s ease-in-out infinite',
};
