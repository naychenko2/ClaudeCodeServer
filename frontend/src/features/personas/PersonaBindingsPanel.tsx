import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { X, Link, Plus, Search, Check as CheckIcon, SquarePen, CheckCircle2, Power, Trash2, Globe } from 'lucide-react';
import type { BindingTarget, Persona, PersonaBinding, PersonaBindingDto, PersonaBindingMode, PersonaBindingType, ServerMessage, SkillSuggestion } from '../../types';
import { C, FONT, R, SHADOW } from '../../lib/design';
import { api } from '../../lib/api';
import { onMessage } from '../../lib/signalr';
import { bumpPersonas } from '../../lib/personas';
import { showToast } from '../../lib/toast';
import { Button, IconField, Menu, MenuItem, Toggle, WaitingIndicator } from '../../components/ui';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import { useAiJob, runAiJob, patchAiJobResult, resetAiJob } from '../../lib/aiJobStore';
import { SkillSearchDialog } from '../../components/SkillSearchDialog';
import { PillSwitch } from '../../components/Toolbar';
import { SectionLabel } from '../tasks/bits';
import {
  BINDING_ICONS, BINDING_TYPE_META, BINDING_TYPE_ORDER, MODE_HINT,
  BindingModeBadge, BindingTypeIcon, bindingsCounter,
  fetchBindingTargets, useBindingLabels,
} from './bindingMeta';
import { Stepper, Crumb } from './stepperUi';

// Вкладка «Умения» студии персоны (фича persona-bindings): карточки привязок
// «источник + правило, когда им пользоваться». Семантика мгновенного сохранения
// (как PersonaMemoryPanel): каждая правка — сразу PUT/POST/DELETE, без общей формы.

// Примеры условий для шага «Правило»
const CONDITION_EXAMPLES = [
  'когда спрашивают про релизы',
  'когда просят статус задач',
  'в каждом ответе про архитектуру',
];

const MODE_OPTIONS: { value: PersonaBindingMode; label: string }[] = [
  { value: 'auto', label: 'Авто' },
  { value: 'always', label: 'Всегда' },
  { value: 'off', label: 'Выкл' },
];

// Состояние инлайн-панели добавления: степпер ① Тип → ② Цель → ③ Правило
interface AddPanelState {
  step: 1 | 2 | 3;
  type?: PersonaBindingType;
  targetId?: string;
  targetLabel?: string;
  // projectPath: проект выбран, вводим путь; notes: источник выбран, выбираем папку
  path?: string;
  // notes: выбранный источник (второй уровень — папки внутри него)
  notesSource?: { id: string; label: string } | null;
  condition: string;
  mode: PersonaBindingMode;
}

// Кандидаты AI-подбора с чекбоксами: привязки к существующим источникам +
// навыки из реестра (их нужно установить и привязать). Статус/результат живут
// в aiJobStore по ключу персоны — переживают уход со страницы.
interface SuggestResult {
  candidates: (PersonaBinding & { on: boolean })[];
  skills: (SkillSuggestion & { on: boolean })[];
}

export function PersonaBindingsPanel({ persona, accent, isMobile }: {
  persona: Persona;
  // Цвет персоны (разрезолвленный из палитры) — рамки активных карточек и степпер
  accent: string;
  isMobile: boolean;
}) {
  const [bindings, setBindings] = useState<PersonaBinding[] | null>(null);
  const [error, setError] = useState(false);

  // «Доступ ко всем проектам» — персона-уровневый флаг (не привязка), только у глобальных
  // персон. Локальное состояние для мгновенного отклика тумблера; источник правды — persona
  // prop (обновляется через realtime personas_changed после сохранения).
  const [allAccess, setAllAccess] = useState(persona.allProjectsAccess ?? false);
  const [allAccessBusy, setAllAccessBusy] = useState(false);
  useEffect(() => { setAllAccess(persona.allProjectsAccess ?? false); }, [persona.id, persona.allProjectsAccess]);

  const toggleAllAccess = async (next: boolean) => {
    setAllAccess(next);
    setAllAccessBusy(true);
    try {
      await api.personas.update(persona.id, { allProjectsAccess: next });
      bumpPersonas();
    } catch (e) {
      setAllAccess(!next);
      showToast('Умения', e instanceof Error ? e.message : 'Не удалось сохранить настройку.');
    } finally {
      setAllAccessBusy(false);
    }
  };

  // Развёрнутая карточка + черновик её условия (переживает realtime-перезагрузку списка)
  const [expandedId, setExpandedId] = useState<string | null>(null);
  const [condDraft, setCondDraft] = useState('');
  const [confirmDelId, setConfirmDelId] = useState<string | null>(null);
  const confirmTimer = useRef<number | null>(null);
  // Короткая подсветка свежесозданных карточек
  const [flashIds, setFlashIds] = useState<Set<string>>(() => new Set());
  const [hoveredId, setHoveredId] = useState<string | null>(null);
  const [menuId, setMenuId] = useState<string | null>(null);

  const [panel, setPanel] = useState<AddPanelState | null>(null);
  const suggestKey = `personas:${persona.id}:bindings-suggest`;
  const suggestJob = useAiJob<SuggestResult>(suggestKey);
  const [adding, setAdding] = useState(false);
  const [showSkillSearch, setShowSkillSearch] = useState(false);

  const load = useCallback(async () => {
    try {
      const list = await api.personas.bindings(persona.id);
      setBindings(list);
      setError(false);
    } catch {
      setError(true);
      setBindings(prev => prev ?? []);
    }
  }, [persona.id]);

  useEffect(() => {
    setBindings(null);
    void load();
  }, [load]);

  // Realtime: привязки меняются PUT'ом персоны (personas_changed action='updated') —
  // перечитываем список; черновик условия развёрнутой карточки не трогаем.
  const loadRef = useRef(load);
  loadRef.current = load;
  useEffect(() => {
    const off = onMessage((msg: ServerMessage) => {
      if (msg.type === 'personas_changed' && msg.action === 'updated' && msg.personaId === persona.id) {
        void loadRef.current();
      }
    });
    return off;
  }, [persona.id]);

  useEffect(() => () => { if (confirmTimer.current) window.clearTimeout(confirmTimer.current); }, []);

  // Подписи целей — и для сохранённых привязок, и для кандидатов подбора
  const labelSource = useMemo(
    () => [...(bindings ?? []), ...(suggestJob.result?.candidates ?? [])],
    [bindings, suggestJob.result?.candidates],
  );
  const labelOf = useBindingLabels(labelSource);

  const list = bindings ?? [];

  // === Мутации (мгновенное сохранение) ===

  const putBinding = async (b: PersonaBinding, patch: Partial<PersonaBindingDto>) => {
    const dto: PersonaBindingDto = {
      type: b.type, target: b.target, path: b.path ?? undefined,
      condition: b.condition, mode: b.mode, ...patch,
    };
    try {
      const updated = await api.personas.updateBinding(persona.id, b.id, dto);
      setBindings(prev => (prev ?? []).map(x => x.id === b.id ? updated : x));
    } catch (e) {
      showToast('Умения', e instanceof Error ? e.message : 'Не удалось сохранить привязку.');
    }
  };

  // Сохранить черновик условия развёрнутой карточки (blur / «Готово» / смена карточки)
  const commitDraft = async (b: PersonaBinding | undefined) => {
    if (!b) return;
    const cond = condDraft.trim();
    if (cond === b.condition) return;
    await putBinding(b, { condition: cond });
  };

  const expanded = list.find(b => b.id === expandedId);

  const toggleCard = (b: PersonaBinding) => {
    setConfirmDelId(null);
    setMenuId(null);
    if (expandedId === b.id) {
      void commitDraft(expanded);
      setExpandedId(null);
      return;
    }
    void commitDraft(expanded);
    setExpandedId(b.id);
    setCondDraft(b.condition);
    setPanel(null);
  };

  const setMode = (b: PersonaBinding, mode: PersonaBindingMode) => {
    // Развёрнутая карточка: вместе с режимом отправляем и текущий черновик условия
    const cond = expandedId === b.id ? condDraft.trim() : b.condition;
    void putBinding(b, { mode, condition: cond });
  };

  const remove = async (b: PersonaBinding) => {
    try {
      await api.personas.removeBinding(persona.id, b.id);
      setBindings(prev => (prev ?? []).filter(x => x.id !== b.id));
      if (expandedId === b.id) setExpandedId(null);
      setConfirmDelId(null);
    } catch (e) {
      showToast('Умения', e instanceof Error ? e.message : 'Не удалось удалить привязку.');
    }
  };

  // «Удалить привязку» — двойное подтверждение с автосбросом через 3с
  const askDelete = (b: PersonaBinding) => {
    if (confirmDelId === b.id) { void remove(b); return; }
    setConfirmDelId(b.id);
    if (confirmTimer.current) window.clearTimeout(confirmTimer.current);
    confirmTimer.current = window.setTimeout(() => setConfirmDelId(null), 3000);
  };

  const flash = (ids: string[]) => {
    setFlashIds(new Set(ids));
    window.setTimeout(() => setFlashIds(new Set()), 1200);
  };

  // Добавление из панели-степпера
  const commitPanel = async () => {
    if (!panel?.type || !panel.targetId) return;
    try {
      const created = await api.personas.addBinding(persona.id, {
        type: panel.type,
        target: panel.targetId,
        path: panel.path || undefined,
        condition: panel.condition.trim(),
        mode: panel.mode,
      });
      setBindings(prev => [...(prev ?? []), created]);
      setPanel(null);
      flash([created.id]);
    } catch (e) {
      showToast('Умения', e instanceof Error ? e.message : 'Не удалось добавить привязку.');
    }
  };

  // AI-условие «когда пользоваться» — статус и результат живут в aiJobStore по ключу
  // (карточка/панель), поэтому переживают уход со страницы; применяются автоматически,
  // когда готовы, если нужная карточка/панель всё ещё открыта.
  const condKey = `personas:${persona.id}:binding-condition:${expandedId ?? '_none'}`;
  const condJob = useAiJob<string>(condKey);
  const aiBusy = condJob.status === 'running';
  const panelCondKey = `personas:${persona.id}:binding-condition:panel`;
  const panelCondJob = useAiJob<string>(panelCondKey);
  const panelAiBusy = panelCondJob.status === 'running';

  useEffect(() => {
    if (condJob.status === 'done' && condJob.result != null && expanded) {
      const cond = condJob.result;
      setCondDraft(cond);
      void putBinding(expanded, { condition: cond });
      resetAiJob(condKey);
    } else if (condJob.status === 'error') {
      showToast('Умения', condJob.error ?? 'Не удалось сформулировать условие.');
      resetAiJob(condKey);
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [condJob.status]);

  useEffect(() => {
    if (panelCondJob.status === 'done' && panelCondJob.result != null && panel) {
      setPanel({ ...panel, condition: panelCondJob.result });
      resetAiJob(panelCondKey);
    } else if (panelCondJob.status === 'error') {
      showToast('Умения', panelCondJob.error ?? 'Не удалось сформулировать условие.');
      resetAiJob(panelCondKey);
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [panelCondJob.status]);

  const runAiCondition = () => {
    if (!expanded) return;
    runAiJob(condKey, () => api.personas
      .aiBindingCondition({ type: expanded.type, target: expanded.target, path: expanded.path ?? undefined })
      .then(r => r.condition));
  };
  const runPanelAiCondition = () => {
    if (!panel?.type || !panel.targetId) return;
    runAiJob(panelCondKey, () => api.personas
      .aiBindingCondition({ type: panel.type!, target: panel.targetId!, path: panel.path })
      .then(r => r.condition));
  };

  // «✨ Подобрать автоматически» — параллельно: привязки к существующим источникам +
  // релевантные навыки из реестра (их предложим установить и привязать). Статус и
  // результат — в aiJobStore, переживают уход со страницы.
  const runSuggest = () => {
    setPanel(null);
    setExpandedId(null);
    runAiJob<SuggestResult>(suggestKey, async () => {
      const [bindingsRes, skillsRes] = await Promise.allSettled([
        api.personas.suggestBindings(persona.id),
        api.skills.suggest({ personaId: persona.id }),
      ]);
      const candidates = bindingsRes.status === 'fulfilled'
        ? bindingsRes.value.candidates.map(c => ({ ...c, on: true })) : [];
      const skills = skillsRes.status === 'fulfilled'
        ? skillsRes.value.candidates.map(s => ({ ...s, on: true })) : [];
      // Оба источника упали — показываем ошибку; иначе показываем что есть
      if (candidates.length === 0 && skills.length === 0 && bindingsRes.status === 'rejected') {
        throw new Error('Не удалось подобрать. Попробуйте ещё раз.');
      }
      return { candidates, skills };
    });
  };

  const acceptSuggest = async () => {
    const result = suggestJob.result;
    const picked = (result?.candidates ?? []).filter(c => c.on);
    const pickedSkills = (result?.skills ?? []).filter(s => s.on);
    if (picked.length === 0 && pickedSkills.length === 0) { resetAiJob(suggestKey); return; }
    setAdding(true);
    const added: string[] = [];
    try {
      for (const c of picked) {
        const created = await api.personas.addBinding(persona.id, {
          type: c.type, target: c.target, path: c.path ?? undefined,
          condition: c.condition, mode: c.mode,
        });
        added.push(created.id);
        setBindings(prev => [...(prev ?? []), created]);
      }
      // Навыки из реестра: установить глобально + привязать (installForPersona)
      for (const s of pickedSkills)
        await api.skills.installForPersona(persona.id, s.skill.source, s.skill.skill);
      if (pickedSkills.length > 0) await load();
      resetAiJob(suggestKey);
      flash(added);
    } catch (e) {
      showToast('Умения', e instanceof Error ? e.message : 'Не удалось добавить.');
      resetAiJob(suggestKey);
      void load();
    } finally {
      setAdding(false);
    }
  };

  const openAdd = (type?: PersonaBindingType) => {
    void commitDraft(expanded);
    setExpandedId(null);
    resetAiJob(suggestKey);
    setPanel({ step: type ? 2 : 1, type, condition: '', mode: 'auto' });
  };

  return (
    <div style={{ height: '100%', overflowY: 'auto', background: C.bgMain }}>
      <div style={{
        maxWidth: 680, margin: '0 auto', boxSizing: 'border-box',
        padding: isMobile ? '20px 16px 32px' : '26px 32px 40px',
        display: 'flex', flexDirection: 'column', gap: 0, fontFamily: FONT.sans,
      }}>
        {/* Заголовок секции + счётчик + подзаголовок */}
        <div style={{ display: 'flex', alignItems: 'baseline', justifyContent: 'space-between', gap: 10 }}>
          <SectionLabel>Умения и правила</SectionLabel>
          <span style={{ fontSize: 11.5, color: C.textMuted, flexShrink: 0 }}>
            {bindings === null ? 'загрузка…' : bindingsCounter(list)}
          </span>
        </div>
        <div style={{ fontSize: 12.5, color: C.textMuted, lineHeight: 1.5, marginTop: 4 }}>
          Что персона знает и умеет — и когда этим пользоваться. Изменения сохраняются сразу.
        </div>

        {/* Доступ ко всем проектам — персона-уровневый флаг, не привязка (только у глобальных) */}
        {persona.scope === 'global' && (
          <div style={{
            marginTop: 14, display: 'flex', alignItems: 'center', gap: 12,
            background: allAccess ? C.accentLight : C.bgWhite,
            border: `1px solid ${allAccess ? accent : C.border}`,
            borderRadius: R.xl, padding: '12px 14px', opacity: allAccessBusy ? 0.7 : 1,
            transition: 'border-color 0.15s, background 0.15s',
          }}>
            <Globe size={18} strokeWidth={ICON_STROKE} style={{ color: allAccess ? accent : C.textMuted, flexShrink: 0 }} />
            <div style={{ flex: 1, minWidth: 0 }}>
              <div style={{ fontSize: 13.5, fontWeight: 600, color: C.textHeading }}>Доступ ко всем проектам</div>
              <div style={{ fontSize: 12, color: C.textSecondary, marginTop: 1, lineHeight: 1.45 }}>
                {allAccess
                  ? 'Видит файлы всех текущих и будущих проектов. Привязки «Проект» ниже не сужают доступ — работают как подсказка, когда каким пользоваться.'
                  : 'Без привязок «Проект» ниже и так доступны все проекты — но одна такая привязка сузит доступ только до неё. Включите, чтобы зона не сужалась никогда.'}
              </div>
            </div>
            <Toggle checked={allAccess} onChange={toggleAllAccess} disabled={allAccessBusy} />
          </div>
        )}
        {persona.scope === 'global' && (
          <div style={{ fontSize: 11.5, color: C.textMuted, marginTop: 6 }}>
            Заметки персона видит из всех проектов всегда, независимо от этой настройки.
          </div>
        )}

        {bindings === null && !error && (
          <div style={{ padding: 40, textAlign: 'center', color: C.textMuted, fontSize: 13 }}>Загрузка…</div>
        )}
        {error && (
          <div style={{ padding: 40, textAlign: 'center', color: C.dangerText, fontSize: 13 }}>
            Не удалось загрузить привязки.{' '}
            <button onClick={() => { setBindings(null); setError(false); void load(); }} style={linkBtn}>Повторить</button>
          </div>
        )}

        {/* Пустое состояние — приглашение с чипами-примерами */}
        {bindings !== null && !error && list.length === 0 && !panel && suggestJob.status === 'idle' && (
          <div style={{
            marginTop: 14, border: `1.5px dashed ${C.dashed}`, borderRadius: R.xl,
            padding: '24px 22px', textAlign: 'center',
          }}>
            <div style={{ color: C.textMuted, marginBottom: 8, display: 'flex', justifyContent: 'center' }}>
              <Link size={22} strokeWidth={ICON_STROKE} />
            </div>
            <div style={{ fontSize: 13.5, fontWeight: 600, color: C.textHeading }}>
              Подключи персоне источники и инструменты
            </div>
            <div style={{ fontSize: 12.5, color: C.textSecondary, lineHeight: 1.5, marginTop: 5 }}>
              Проекты, базы знаний, заметки и навыки —<br />с правилом, когда ими пользоваться
            </div>
            <div style={{ display: 'flex', gap: 8, justifyContent: 'center', flexWrap: 'wrap', marginTop: 14 }}>
              <ExampleChip label="🗂 файлы проекта" onClick={() => openAdd('project')} />
              <ExampleChip label="📚 база знаний" onClick={() => openAdd('knowledge')} />
              <ExampleChip label="🔧 инструмент чатов" onClick={() => openAdd('tool')} />
              <ExampleChip label="⚡ навык" onClick={() => openAdd('skill')} />
            </div>
            <div style={{ marginTop: 16, display: 'flex', justifyContent: 'center' }}>
              <AddBindingButton onClick={() => openAdd()} />
            </div>
          </div>
        )}

        {/* Карточки привязок */}
        {list.length > 0 && (
          <div style={{ display: 'flex', flexDirection: 'column', gap: 8, marginTop: 14 }}>
            {list.map(b => {
              const open = expandedId === b.id;
              const dim = b.mode === 'off' && !open;
              const flashing = flashIds.has(b.id);
              return (
                <div
                  key={b.id}
                  onMouseEnter={() => setHoveredId(b.id)}
                  onMouseLeave={() => setHoveredId(h => h === b.id ? null : h)}
                  style={{
                    background: flashing ? C.accentLight : C.bgWhite,
                    border: `1px solid ${open || hoveredId === b.id ? accent : C.border}`,
                    borderRadius: R.xl, padding: '10px 14px',
                    transition: 'border-color 0.15s, background 0.6s',
                  }}
                >
                  {/* Свёрнутая строка */}
                  <div
                    onClick={() => toggleCard(b)}
                    style={{ display: 'flex', alignItems: 'center', gap: 12, cursor: 'pointer' }}
                  >
                    <BindingTypeIcon type={b.type} dim={dim} />
                    <div style={{ flex: 1, minWidth: 0, opacity: dim ? 0.55 : 1 }}>
                      <div style={{ fontSize: 13.5, fontWeight: 600, color: C.textHeading }}>{labelOf(b)}</div>
                      {b.condition ? (
                        <div style={{ fontSize: 12, color: C.textSecondary, marginTop: 1, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                          {b.condition}
                        </div>
                      ) : (
                        <div style={{ fontSize: 12, color: C.textMuted, fontStyle: 'italic', marginTop: 1, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                          Всегда под рукой — условие не задано
                        </div>
                      )}
                    </div>
                    <div style={{ display: 'flex', alignItems: 'center', gap: 8, flexShrink: 0 }}>
                      <BindingModeBadge mode={b.mode} />
                      {!open && (
                        <div style={{ position: 'relative' }} onClick={e => e.stopPropagation()}>
                          <button
                            onClick={() => setMenuId(m => m === b.id ? null : b.id)}
                            aria-label="Действия"
                            style={{
                              width: isMobile ? 36 : 28, height: isMobile ? 36 : 28, border: 'none',
                              background: 'transparent', borderRadius: R.md, cursor: 'pointer',
                              color: C.textMuted, fontSize: 16, lineHeight: 1,
                              visibility: isMobile || hoveredId === b.id || menuId === b.id ? 'visible' : 'hidden',
                            }}
                          >⋯</button>
                          {menuId === b.id && (
                            <Menu onClose={() => setMenuId(null)} align="right" top={30} minWidth={180}>
                              <MenuItem
                                icon={<SquarePen size={15} strokeWidth={ICON_STROKE} />}
                                label="Редактировать"
                                onClick={() => { setMenuId(null); toggleCard(b); }}
                              />
                              <MenuItem
                                icon={b.mode === 'off'
                                  ? <CheckCircle2 size={15} strokeWidth={ICON_STROKE} />
                                  : <Power size={15} strokeWidth={ICON_STROKE} />}
                                label={b.mode === 'off' ? 'Включить' : 'Выключить'}
                                onClick={() => { setMenuId(null); setMode(b, b.mode === 'off' ? 'auto' : 'off'); }}
                              />
                              <div style={{ height: 1, background: C.borderLight, margin: '4px 6px' }} />
                              <MenuItem
                                danger
                                icon={<Trash2 size={15} strokeWidth={ICON_STROKE} />}
                                label="Удалить"
                                onClick={() => {
                                  setMenuId(null);
                                  setExpandedId(b.id);
                                  setCondDraft(b.condition);
                                  askDelete(b);
                                }}
                              />
                            </Menu>
                          )}
                        </div>
                      )}
                    </div>
                  </div>

                  {/* Развёрнутое тело — редактирование по месту */}
                  {open && (
                    <div style={{ borderTop: `1px solid ${C.borderLight}`, marginTop: 10, paddingTop: 12 }}>
                      <div style={fLabel}>Когда пользоваться</div>
                      <div style={{ display: 'flex', gap: 8, alignItems: 'flex-start' }}>
                        <CondTextArea
                          value={condDraft}
                          onChange={setCondDraft}
                          onBlur={() => void commitDraft(b)}
                        />
                        <AiConditionButton busy={aiBusy} onClick={runAiCondition} />
                      </div>
                      <div style={{ ...fLabel, marginTop: 14 }}>Режим</div>
                      <PillSwitch<PersonaBindingMode>
                        fill
                        value={b.mode}
                        onChange={m => setMode(b, m)}
                        options={MODE_OPTIONS}
                      />
                      <div style={{ fontSize: 11.5, color: C.textMuted, marginTop: 6 }}>{MODE_HINT[b.mode]}</div>
                      <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginTop: 14 }}>
                        <button onClick={() => askDelete(b)} style={delLink}>
                          {confirmDelId === b.id ? 'Точно удалить?' : 'Удалить привязку'}
                        </button>
                        <Button variant="ghost" size="sm" onClick={() => {
                          void commitDraft(b);
                          setExpandedId(null);
                          setConfirmDelId(null);
                        }}>Готово</Button>
                      </div>
                    </div>
                  )}
                </div>
              );
            })}
          </div>
        )}

        {/* Кнопки под списком (скрыты, пока открыта панель добавления или подбор) */}
        {bindings !== null && !error && list.length > 0 && !panel && suggestJob.status === 'idle' && (
          <div style={{ display: 'flex', gap: 10, flexWrap: 'wrap', marginTop: 12 }}>
            <AddBindingButton onClick={() => openAdd()} />
            <Button variant="ghostAccent" size="sm" onClick={runSuggest}>
              ✨ Подобрать автоматически
            </Button>
            <Button variant="ghost" size="sm" onClick={() => setShowSkillSearch(true)}>
              ⚡ Найти навык
            </Button>
          </div>
        )}
        {bindings !== null && !error && list.length === 0 && !panel && suggestJob.status === 'idle' && (
          <div style={{ display: 'flex', justifyContent: 'center', gap: 10, flexWrap: 'wrap', marginTop: 12 }}>
            <Button variant="ghostAccent" size="sm" onClick={runSuggest}>
              ✨ Подобрать автоматически
            </Button>
            <Button variant="ghost" size="sm" onClick={() => setShowSkillSearch(true)}>
              ⚡ Найти навык
            </Button>
          </div>
        )}

        {showSkillSearch && (
          <SkillSearchDialog
            persona={{ id: persona.id, name: persona.name }}
            onClose={() => setShowSkillSearch(false)}
            onInstalled={() => void load()}
          />
        )}

        {/* AI-подбор: индикатор / кандидаты с чекбоксами — статус в aiJobStore, переживает уход со страницы */}
        {suggestJob.status !== 'idle' && (
          <div style={{ borderTop: `1px solid ${C.borderLight}`, marginTop: 14, paddingTop: 18 }}>
            {suggestJob.status === 'running' ? (
              <WaitingIndicator hint="Подбираю источники и навыки под роль персоны — до минуты" />
            ) : suggestJob.status === 'error' ? (
              <div style={{ fontSize: 12.5, color: C.dangerText }}>
                {suggestJob.error}{' '}
                <button onClick={runSuggest} style={linkBtn}>Повторить</button>{' '}
                <button onClick={() => resetAiJob(suggestKey)} style={{ ...linkBtn, color: C.textMuted }}>Закрыть</button>
              </div>
            ) : (suggestJob.result?.candidates ?? []).length === 0 && (suggestJob.result?.skills ?? []).length === 0 ? (
              <div style={{ fontSize: 12.5, color: C.textMuted }}>
                Ничего подходящего не нашлось — попробуйте добавить привязку вручную.{' '}
                <button onClick={() => resetAiJob(suggestKey)} style={linkBtn}>Закрыть</button>
              </div>
            ) : (
              <>
                {/* Привязки к существующим источникам */}
                {suggestJob.result!.candidates.length > 0 && (
                  <>
                    <div style={{ display: 'flex', alignItems: 'baseline', justifyContent: 'space-between', gap: 10 }}>
                      <SectionLabel>Умения подобраны автоматически</SectionLabel>
                      <span style={{ fontSize: 11.5, color: C.textMuted, flexShrink: 0 }}>
                        {suggestJob.result!.candidates.filter(c => c.on).length} из {suggestJob.result!.candidates.length}
                      </span>
                    </div>
                    <div style={{ display: 'flex', flexDirection: 'column', gap: 8, marginTop: 12 }}>
                      {suggestJob.result!.candidates.map(c => (
                        <div
                          key={c.id}
                          onClick={() => patchAiJobResult<SuggestResult>(suggestKey, prev => ({
                            ...prev, candidates: prev.candidates.map(x => x.id === c.id ? { ...x, on: !x.on } : x),
                          }))}
                          style={{
                            display: 'flex', alignItems: 'center', gap: 12, cursor: 'pointer',
                            background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.xl,
                            padding: '10px 14px', opacity: c.on ? 1 : 0.5,
                          }}
                        >
                          <Check on={c.on} />
                          <BindingTypeIcon type={c.type} />
                          <div style={{ flex: 1, minWidth: 0 }}>
                            <div style={{ fontSize: 13.5, fontWeight: 600, color: C.textHeading }}>{labelOf(c)}</div>
                            <div style={{ fontSize: 12, color: C.textSecondary, marginTop: 1, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                              {c.condition}
                            </div>
                          </div>
                          <BindingModeBadge mode={c.mode} />
                        </div>
                      ))}
                    </div>
                  </>
                )}

                {/* Навыки из реестра — установить и привязать */}
                {suggestJob.result!.skills.length > 0 && (
                  <>
                    <div style={{ display: 'flex', alignItems: 'baseline', justifyContent: 'space-between', gap: 10, marginTop: suggestJob.result!.candidates.length > 0 ? 18 : 0 }}>
                      <SectionLabel>Навыки из реестра</SectionLabel>
                      <span style={{ fontSize: 11.5, color: C.textMuted, flexShrink: 0 }}>
                        {suggestJob.result!.skills.filter(s => s.on).length} из {suggestJob.result!.skills.length}
                      </span>
                    </div>
                    <div style={{ display: 'flex', flexDirection: 'column', gap: 8, marginTop: 12 }}>
                      {suggestJob.result!.skills.map(s => (
                        <div
                          key={`${s.skill.source}@${s.skill.skill}`}
                          onClick={() => patchAiJobResult<SuggestResult>(suggestKey, prev => ({
                            ...prev, skills: prev.skills.map(x => x.skill.skill === s.skill.skill && x.skill.source === s.skill.source ? { ...x, on: !x.on } : x),
                          }))}
                          style={{
                            display: 'flex', alignItems: 'flex-start', gap: 12, cursor: 'pointer',
                            background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.xl,
                            padding: '10px 14px', opacity: s.on ? 1 : 0.5,
                          }}
                        >
                          <div style={{ marginTop: 1 }}><Check on={s.on} /></div>
                          <div style={{ marginTop: 1 }}><BindingTypeIcon type="skill" /></div>
                          <div style={{ flex: 1, minWidth: 0 }}>
                            <div style={{ fontSize: 13.5, fontWeight: 600, color: C.textHeading, fontFamily: FONT.mono }}>{s.skill.skill}</div>
                            <div style={{ fontSize: 12.5, color: C.accent, marginTop: 2, lineHeight: 1.4 }}>{s.reason}</div>
                            {s.skill.description && (
                              <div style={{ fontSize: 12, color: C.textSecondary, marginTop: 2, lineHeight: 1.45 }}>{s.skill.description}</div>
                            )}
                            <div style={{ fontSize: 11, color: C.textMuted, marginTop: 3, fontFamily: FONT.mono }}>{s.skill.source}</div>
                          </div>
                        </div>
                      ))}
                    </div>
                  </>
                )}

                <div style={{ display: 'flex', alignItems: 'center', gap: 8, marginTop: 12, fontSize: 12, color: C.textMuted }}>
                  ✨ Предложено ИИ по роли и доступным источникам. Навыки из реестра будут установлены и привязаны. Ничего не сохранено, пока вы не подтвердите.
                </div>
                <div style={{ display: 'flex', justifyContent: 'flex-end', gap: 8, marginTop: 14 }}>
                  <Button variant="ghost" size="sm" disabled={adding} onClick={() => resetAiJob(suggestKey)}>Отмена</Button>
                  <Button variant="primary" size="sm" loading={adding}
                    disabled={adding || (suggestJob.result!.candidates.every(c => !c.on) && suggestJob.result!.skills.every(s => !s.on))}
                    onClick={() => void acceptSuggest()}>
                    {adding ? 'Добавляю…' : 'Добавить выбранные'}
                  </Button>
                </div>
              </>
            )}
          </div>
        )}

        {/* Инлайн-панель добавления (степпер) */}
        {panel && (
          <AddPanel
            panel={panel}
            accent={accent}
            isMobile={isMobile}
            aiBusy={panelAiBusy}
            onChange={setPanel}
            onClose={() => { resetAiJob(panelCondKey); setPanel(null); }}
            onCommit={() => void commitPanel()}
            onAiCondition={runPanelAiCondition}
          />
        )}
      </div>
    </div>
  );
}

// === Инлайн-панель «Добавить привязку»: ① Тип → ② Цель → ③ Правило ===
function AddPanel({ panel, accent, isMobile, aiBusy, onChange, onClose, onCommit, onAiCondition }: {
  panel: AddPanelState;
  accent: string;
  isMobile: boolean;
  aiBusy: boolean;
  onChange: (p: AddPanelState) => void;
  onClose: () => void;
  onCommit: () => void;
  onAiCondition: () => void;
}) {
  return (
    <div style={{ borderTop: `1px solid ${C.borderLight}`, marginTop: 14, paddingTop: 18 }}>
      <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between' }}>
        <span style={{ fontSize: 13.5, fontWeight: 600, color: C.textHeading }}>Добавить привязку</span>
        <button onClick={onClose} aria-label="Закрыть" style={xBtn}><X size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} /></button>
      </div>

      <Stepper
        step={panel.step}
        accent={accent}
        steps={[{ n: 1, label: 'Тип' }, { n: 2, label: 'Цель' }, { n: 3, label: 'Правило' }]}
        onStep={s => {
          if (s >= panel.step) return;
          // Возврат назад: на шаг «Тип» — сброс цели; на «Цель» — сброс правила
          if (s === 1) onChange({ ...panel, step: 1, type: undefined, targetId: undefined, targetLabel: undefined, path: undefined, notesSource: null });
          else onChange({ ...panel, step: 2, targetId: undefined, targetLabel: undefined, path: undefined, notesSource: null });
        }} />

      {panel.step === 1 && (
        <div style={{ display: 'grid', gridTemplateColumns: isMobile ? '1fr 1fr' : 'repeat(3, 1fr)', gap: 10, marginTop: 14 }}>
          {BINDING_TYPE_ORDER.map(t => {
            const m = BINDING_TYPE_META[t];
            return (
              <button
                key={t}
                onClick={() => onChange({ ...panel, step: 2, type: t, notesSource: null })}
                onMouseEnter={e => { e.currentTarget.style.borderColor = accent; e.currentTarget.style.background = C.bgCard; }}
                onMouseLeave={e => { e.currentTarget.style.borderColor = C.border; e.currentTarget.style.background = C.bgWhite; }}
                style={{
                  textAlign: 'left', background: C.bgWhite, border: `1px solid ${C.border}`,
                  borderRadius: R.xl, padding: 12, cursor: 'pointer', fontFamily: FONT.sans,
                  transition: 'border-color 0.15s',
                }}
              >
                <BindingTypeIcon type={t} />
                <div style={{ fontSize: 13, fontWeight: 600, color: C.textHeading, marginTop: 8 }}>{m.name}</div>
                <div style={{ fontSize: 11.5, color: C.textMuted, lineHeight: 1.4, marginTop: 3 }}>{m.hint}</div>
              </button>
            );
          })}
        </div>
      )}

      {panel.step === 2 && panel.type && (
        <TargetPicker panel={panel} onChange={onChange} />
      )}

      {panel.step === 3 && panel.type && (
        <>
          <Crumb onClick={() => onChange({ ...panel, step: 2, targetId: undefined, targetLabel: undefined, notesSource: null })}>
            {BINDING_ICONS[panel.type](13)} {BINDING_TYPE_META[panel.type].name} · {panel.targetLabel}{panel.path ? ` · ${panel.path}` : ''}
          </Crumb>
          <div style={{ ...fLabel, marginTop: 16 }}>Когда пользоваться</div>
          <div style={{ display: 'flex', gap: 8, alignItems: 'flex-start' }}>
            <CondTextArea
              value={panel.condition}
              onChange={v => onChange({ ...panel, condition: v })}
            />
            <AiConditionButton busy={aiBusy} onClick={onAiCondition} />
          </div>
          <div style={{ fontSize: 11.5, color: C.textMuted, marginTop: 6 }}>Пусто — персона решит сама по ситуации</div>
          <div style={{ display: 'flex', gap: 6, alignItems: 'center', flexWrap: 'wrap', marginTop: 8 }}>
            <span style={{ fontSize: 11.5, color: C.textMuted }}>Примеры:</span>
            {CONDITION_EXAMPLES.map(e => (
              <ExampleChip key={e} label={e} onClick={() => onChange({ ...panel, condition: e })} />
            ))}
          </div>
          <div style={{ ...fLabel, marginTop: 16 }}>Режим</div>
          <PillSwitch<PersonaBindingMode>
            fill
            value={panel.mode}
            onChange={m => onChange({ ...panel, mode: m })}
            options={MODE_OPTIONS}
          />
          <div style={{ fontSize: 11.5, color: C.textMuted, marginTop: 6 }}>{MODE_HINT[panel.mode]}</div>
          <div style={{ display: 'flex', justifyContent: 'flex-end', gap: 8, marginTop: 14 }}>
            <Button variant="ghost" size="sm" onClick={onClose}>Отмена</Button>
            <Button variant="primary" size="sm" onClick={onCommit}>Добавить привязку</Button>
          </div>
        </>
      )}
    </div>
  );
}

// Шаг ② «Цель»: пикер по типу. project/knowledge/tool/skill — один список;
// notes — источник, затем «весь источник» или папка; projectPath — проект,
// затем ручной ввод пути (mono, дерево в v1 не строим).
function TargetPicker({ panel, onChange }: {
  panel: AddPanelState;
  onChange: (p: AddPanelState) => void;
}) {
  const type = panel.type!;
  const [query, setQuery] = useState('');
  const [items, setItems] = useState<BindingTarget[] | null>(null);
  const [loadError, setLoadError] = useState(false);
  // projectPath: выбранный проект (дальше — ввод пути); notes: источник (дальше — папки)
  const [pathInput, setPathInput] = useState('');

  // Каталог: для projectPath цели — проекты; для notes с выбранным источником — папки
  const catalogType = type === 'projectPath' ? 'project' : type;
  const notesSource = panel.notesSource ?? null;

  useEffect(() => {
    let alive = true;
    setItems(null);
    setLoadError(false);
    fetchBindingTargets(catalogType, type === 'notes' && notesSource ? notesSource.id : undefined)
      .then(list => { if (alive) setItems(list); })
      .catch(() => { if (alive) { setItems([]); setLoadError(true); } });
    return () => { alive = false; };
  }, [catalogType, type, notesSource]);

  const q = query.trim().toLowerCase();
  const filtered = (items ?? []).filter(t =>
    !q || t.label.toLowerCase().includes(q) || (t.hint ?? '').toLowerCase().includes(q));

  // projectPath, проект выбран → ручной ввод пути
  const pathStage = type === 'projectPath' && !!panel.targetId;

  return (
    <>
      <Crumb onClick={() => onChange({ ...panel, step: 1, type: undefined, targetId: undefined, targetLabel: undefined, path: undefined, notesSource: null })}>
        {BINDING_ICONS[type](13)} {BINDING_TYPE_META[type].name}
        {pathStage ? ` · ${panel.targetLabel}` : notesSource ? ` · ${notesSource.label}` : ''}
      </Crumb>

      {pathStage ? (
        <div style={{ marginTop: 12 }}>
          <div style={fLabel}>Путь внутри проекта</div>
          <IconField
            mono
            autoFocus
            value={pathInput}
            onChange={setPathInput}
            placeholder="docs/architecture.md"
            height={38}
            radius={R.lg}
            fontSize={12.5}
            onEnter={() => { if (pathInput.trim()) onChange({ ...panel, step: 3, path: pathInput.trim() }); }}
            icon={BINDING_ICONS.projectPath(14)}
          />
          <div style={{ fontSize: 11.5, color: C.textMuted, marginTop: 6 }}>
            Папка или файл относительно корня проекта
          </div>
          <div style={{ display: 'flex', justifyContent: 'flex-end', marginTop: 12 }}>
            <Button variant="primary" size="sm" disabled={!pathInput.trim()}
              onClick={() => onChange({ ...panel, step: 3, path: pathInput.trim() })}>
              Далее
            </Button>
          </div>
        </div>
      ) : (
        <>
          <div style={{ marginTop: 12 }}>
            <IconField
              value={query}
              onChange={setQuery}
              placeholder="Найти…"
              height={38}
              radius={R.lg}
              fontSize={13}
              icon={<Search size={15} strokeWidth={ICON_STROKE} />}
            />
          </div>
          <div style={{ background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.xl, marginTop: 10, overflow: 'hidden' }}>
            {items === null && (
              <div style={{ padding: '14px 14px', fontSize: 12.5, color: C.textMuted }}>Загрузка…</div>
            )}
            {items !== null && loadError && (
              <div style={{ padding: '14px 14px', fontSize: 12.5, color: C.dangerText }}>Не удалось загрузить список.</div>
            )}
            {items !== null && !loadError && filtered.length === 0 && (
              <div style={{ padding: '14px 14px', fontSize: 12.5, color: C.textMuted }}>
                {q ? 'Ничего не найдено' : 'Список пуст'}
              </div>
            )}
            {/* notes, источник выбран: первая строка — «весь источник» */}
            {type === 'notes' && notesSource && items !== null && !loadError && !q && (
              <PickRow
                label={`Весь источник «${notesSource.label}»`}
                hint="все заметки целиком"
                onClick={() => onChange({ ...panel, step: 3, targetId: notesSource.id, targetLabel: notesSource.label, path: undefined })}
              />
            )}
            {filtered.map(t => (
              <PickRow
                key={t.id}
                label={t.label}
                hint={t.hint ?? undefined}
                mono={type === 'notes' && !!notesSource}
                onClick={() => {
                  if (type === 'notes' && !notesSource) {
                    // Первый уровень заметок — выбрали источник, дальше папки
                    onChange({ ...panel, notesSource: { id: t.id, label: t.label } });
                    setQuery('');
                  } else if (type === 'notes' && notesSource) {
                    // Папка источника: target — источник, path — папка
                    onChange({ ...panel, step: 3, targetId: notesSource.id, targetLabel: notesSource.label, path: t.id });
                  } else if (type === 'projectPath') {
                    // Проект выбран — переходим к ручному вводу пути
                    onChange({ ...panel, targetId: t.id, targetLabel: t.label });
                  } else {
                    onChange({ ...panel, step: 3, targetId: t.id, targetLabel: t.label, path: undefined });
                  }
                }}
              />
            ))}
          </div>
        </>
      )}
    </>
  );
}

// Строка пикера целей
function PickRow({ label, hint, meta, mono, onClick }: {
  label: string;
  hint?: string;
  meta?: string;
  mono?: boolean;
  onClick: () => void;
}) {
  return (
    <button
      onClick={onClick}
      onMouseEnter={e => { e.currentTarget.style.background = C.bgSelected; }}
      onMouseLeave={e => { e.currentTarget.style.background = 'transparent'; }}
      style={{
        display: 'flex', alignItems: 'center', gap: 12, width: '100%', textAlign: 'left',
        border: 'none', background: 'transparent', padding: '10px 14px', minHeight: 44,
        borderBottom: `1px solid ${C.borderLight}`, cursor: 'pointer', fontFamily: FONT.sans,
        boxSizing: 'border-box',
      }}
    >
      <span style={{ flex: 1, minWidth: 0 }}>
        <span style={{
          display: 'block', fontSize: mono ? 12.5 : 13.5, fontWeight: mono ? 400 : 600,
          color: C.textHeading, fontFamily: mono ? FONT.mono : FONT.sans,
          overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
        }}>{label}</span>
        {hint && (
          <span style={{ display: 'block', fontSize: 12, color: C.textMuted, marginTop: 1, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
            {hint}
          </span>
        )}
      </span>
      {meta && (
        <span style={{ marginLeft: 'auto', flexShrink: 0, fontFamily: FONT.mono, fontSize: 12, color: C.textMuted }}>{meta}</span>
      )}
    </button>
  );
}

// Textarea условия «когда пользоваться» — стиль полей продукта + сохранение по blur
function CondTextArea({ value, onChange, onBlur }: {
  value: string;
  onChange: (v: string) => void;
  onBlur?: () => void;
}) {
  const [focused, setFocused] = useState(false);
  const ref = useRef<HTMLTextAreaElement>(null);
  // Авто-рост без внутреннего скролла
  useEffect(() => {
    const el = ref.current;
    if (!el) return;
    el.style.height = 'auto';
    el.style.height = `${el.scrollHeight}px`;
  }, [value]);
  return (
    <textarea
      ref={ref}
      value={value}
      rows={2}
      onChange={e => onChange(e.target.value)}
      onFocus={() => setFocused(true)}
      onBlur={() => { setFocused(false); onBlur?.(); }}
      placeholder="Например: когда спрашивают про релизы — читай CHANGELOG.md"
      style={{
        flex: 1, minWidth: 0, background: C.bgWhite, borderRadius: R.xl,
        border: `1px solid ${focused ? C.accent : C.border}`,
        color: C.textHeading, fontSize: 13.5, padding: '9px 12px', outline: 'none',
        resize: 'none', overflow: 'hidden', lineHeight: 1.4, minHeight: 40,
        fontFamily: FONT.sans, boxSizing: 'border-box',
        boxShadow: focused ? SHADOW.focus : 'none',
        transition: 'border-color 0.15s, box-shadow 0.15s',
      }}
    />
  );
}

// ✨-кнопка AI-условия (38×38, пульсирует пока думает)
function AiConditionButton({ busy, onClick }: { busy: boolean; onClick: () => void }) {
  return (
    <button
      onClick={onClick}
      disabled={busy}
      title="Предложить условие по содержимому"
      style={{
        width: 38, height: 38, flexShrink: 0, borderRadius: R.lg,
        border: `1.5px solid ${C.accent}`, background: 'transparent', color: C.accent,
        fontSize: 15, cursor: busy ? 'default' : 'pointer',
        // Своё имя keyframe: одноимённый cc-bind-pulse спиннера подбора имеет другие фазы,
        // при одновременной инжекции двух определений победитель недетерминирован
        animation: busy ? 'cc-bind-btn-pulse 1s infinite' : undefined,
      }}
    >
      ✨
      <style>{'@keyframes cc-bind-btn-pulse { 50% { opacity: 0.4; } }'}</style>
    </button>
  );
}

// Пунктирная кнопка «+ Добавить привязку»
function AddBindingButton({ onClick }: { onClick: () => void }) {
  return (
    <button
      onClick={onClick}
      onMouseEnter={e => { e.currentTarget.style.borderColor = C.accent; e.currentTarget.style.color = C.accent; }}
      onMouseLeave={e => { e.currentTarget.style.borderColor = C.dashed; e.currentTarget.style.color = C.textSecondary; }}
      style={{
        display: 'inline-flex', alignItems: 'center', gap: 6,
        border: `1.5px dashed ${C.dashed}`, background: 'transparent', color: C.textSecondary,
        borderRadius: R.lg, padding: '7px 14px', fontSize: 12.5, fontWeight: 600,
        cursor: 'pointer', fontFamily: FONT.sans, transition: 'border-color 0.15s, color 0.15s',
      }}
    >
      <Plus size={14} strokeWidth={ICON_STROKE} style={{ flexShrink: 0 }} />
      Добавить привязку
    </button>
  );
}

// Чип-пример (пустое состояние и примеры условий)
function ExampleChip({ label, onClick }: { label: string; onClick: () => void }) {
  return (
    <button
      onClick={onClick}
      onMouseEnter={e => { e.currentTarget.style.background = C.accentLight; e.currentTarget.style.borderColor = C.accent; e.currentTarget.style.color = C.textPrimary; }}
      onMouseLeave={e => { e.currentTarget.style.background = C.bgWhite; e.currentTarget.style.borderColor = C.border; e.currentTarget.style.color = C.textSecondary; }}
      style={{
        background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.pill,
        padding: '4px 11px', fontSize: 12, color: C.textSecondary, cursor: 'pointer',
        fontFamily: FONT.sans, transition: 'background 0.12s, border-color 0.12s',
      }}
    >
      {label}
    </button>
  );
}

// Квадратный чекбокс кандидата подбора
function Check({ on }: { on: boolean }) {
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

const fLabel: React.CSSProperties = {
  fontSize: 11, fontWeight: 600, textTransform: 'uppercase', letterSpacing: '0.05em',
  color: C.textSecondary, marginBottom: 6, fontFamily: FONT.sans,
};

const delLink: React.CSSProperties = {
  border: 'none', background: 'none', fontSize: 12.5, fontWeight: 600,
  color: C.dangerText, padding: '4px 0', cursor: 'pointer', fontFamily: FONT.sans,
};

const linkBtn: React.CSSProperties = {
  background: 'transparent', border: 'none', color: C.accent, cursor: 'pointer',
  fontSize: 13, fontFamily: FONT.sans, textDecoration: 'underline', padding: 0,
};

const xBtn: React.CSSProperties = {
  width: 28, height: 28, border: 'none', background: 'transparent', borderRadius: R.md,
  color: C.textMuted, cursor: 'pointer',
  display: 'flex', alignItems: 'center', justifyContent: 'center',
};

