import { useEffect, useMemo, useState } from 'react';
import { MessageSquare } from 'lucide-react';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import type { AutomationTriggerType, Persona, PersonaBinding, PersonaMemoryEntry, PersonaMemoryType, Session, Task } from '../../types';
import { api } from '../../lib/api';
import { C, FONT, R } from '../../lib/design';
import { useModelLabel } from '../../lib/models';
import { effortLabel } from '../../lib/effort';
import { ensureTasksLoaded, useTasks } from '../../lib/tasks';
import { relativeTime } from '../projects/projectUtil';
import { SectionLabel } from '../tasks/bits';
import { PersonaAvatar } from './PersonaAvatar';
import { BindingModeBadge, BindingTypeIcon, bindingPlural, bindingsCounter, useBindingLabels } from './bindingMeta';

// Режим «Обзор» студии персоны: read-only визитка со сводкой — кто это,
// как настроена (модель/возможности/память), её характер и недавние разговоры.
// Редактирование живёт в соседнем виде «Профиль» (PersonaForm), сюда не входит.

// Подписи возможностей персоны (ключи как в PersonaForm.TOOL_OPTIONS)
const TOOL_TITLES: Record<string, string> = {
  tasks: 'Задачи',
  notes: 'Заметки',
  web: 'Веб',
};

// Подписи типов триггеров автоматизации (краткая сводка в обзоре «Активность»)
const TRIGGER_LABELS: Record<AutomationTriggerType, string> = {
  timer: 'Таймер',
  file: 'Файлы',
  note: 'Заметки',
  gitCommit: 'Коммиты',
  taskStatus: 'Статус задачи',
  mention: 'Упоминание',
};

// Подписи типов памяти для мини-бейджа у записи
const MEMORY_TYPE_LABEL: Record<PersonaMemoryType, string> = {
  semantic: 'факт',
  episodic: 'эпизод',
  procedural: 'приём',
};

// Порог, после которого длинный характер сворачивается с «Показать полностью»
const CHARACTER_CLAMP_CHARS = 420;
const CHARACTER_CLAMP_LINES = 7;
const CHARACTER_COLLAPSED_MAX = 176; // px ≈ 7 строк при line-height 1.6 / 14.5px

function plural(n: number, one: string, few: string, many: string): string {
  const m10 = n % 10, m100 = n % 100;
  if (m10 === 1 && m100 !== 11) return one;
  if (m10 >= 2 && m10 <= 4 && (m100 < 10 || m100 >= 20)) return few;
  return many;
}

export function PersonaPreview({ persona, accent, onOpenSession, onTalk, talking, onEditProfile, onOpenKnowledge, onOpenTasks, onOpenAutomation, onOpenMemory, isMobile }: {
  persona: Persona;
  // Цвет персоны (уже разрезолвленный из палитры) — тот же, что в тулбаре
  accent: string;
  // Открыть существующий чат персоны (навигация — у родителя: хаб или проект)
  onOpenSession: (s: Session) => void;
  // Начать новый разговор (та же кнопка, что «Поговорить» в тулбаре)
  onTalk: () => void;
  talking?: boolean;
  // Перейти в вид «Профиль» (подсказка при пустом характере)
  onEditProfile?: () => void;
  // Перейти во вкладку «Знания» (секция привязок, фича persona-bindings)
  onOpenKnowledge?: () => void;
  // Перейти во вкладку «Задачи» (поручения персоне-исполнителю)
  onOpenTasks?: () => void;
  // Перейти во вкладку «Проактивность» (правила автоматизации)
  onOpenAutomation?: () => void;
  // Перейти во вкладку «Память» (долгая память персоны)
  onOpenMemory?: () => void;
  isMobile?: boolean;
}) {
  const modelName = useModelLabel(persona.model);

  // Сводка задач персоны-исполнителя (реальные задачи из общего стора)
  const allTasks = useTasks();
  useEffect(() => { void ensureTasksLoaded(); }, []);
  const taskCounts = useMemo(() => {
    const mine = allTasks.filter(t => t.personaId === persona.id);
    return { total: mine.length, active: mine.filter(t => t.status === 'inProgress').length };
  }, [allTasks, persona.id]);

  // Привязки «Знания и правила»; тихий fail → пусто
  const [bindings, setBindings] = useState<PersonaBinding[] | null>(null);
  useEffect(() => {
    let alive = true;
    setBindings(null);
    api.personas.bindings(persona.id)
      .then(list => { if (alive) setBindings(list); })
      .catch(() => { if (alive) setBindings([]); });
    return () => { alive = false; };
  }, [persona.id]);
  const bindingLabelOf = useBindingLabels(bindings);

  // Недавние разговоры: best-effort, тихий fail → пустой список
  const [chats, setChats] = useState<Session[] | null>(null);
  useEffect(() => {
    let alive = true;
    setChats(null);
    api.personas.chats(persona.id)
      .then(list => { if (alive) setChats([...list].sort((a, b) => b.updatedAt.localeCompare(a.updatedAt))); })
      .catch(() => { if (alive) setChats([]); });
    return () => { alive = false; };
  }, [persona.id]);

  // Сводка памяти — только когда память включена (иначе не запрашиваем)
  const [memory, setMemory] = useState<PersonaMemoryEntry[] | null>(null);
  useEffect(() => {
    let alive = true;
    setMemory(null);
    if (!persona.memoryEnabled) return;
    api.personas.memory(persona.id)
      .then(list => { if (alive) setMemory(list); })
      .catch(() => { if (alive) setMemory([]); });
    return () => { alive = false; };
  }, [persona.id, persona.memoryEnabled]);

  // Раскрытие длинного характера
  const [expanded, setExpanded] = useState(false);
  useEffect(() => { setExpanded(false); }, [persona.id]);
  // Раскрытие длинной инструкции роли (в секции «Правила»)
  const [instrOpen, setInstrOpen] = useState(false);
  useEffect(() => { setInstrOpen(false); }, [persona.id]);

  // Характер: слот контракта (P1), для legacy-персон — старый единый systemPrompt
  const character = (persona.contract?.character ?? persona.systemPrompt ?? '').trim();
  const characterLong = character.length > CHARACTER_CLAMP_CHARS
    || character.split('\n').length > CHARACTER_CLAMP_LINES;

  // Подпись возможностей: полный набор/пусто у tools=null — «Задачи · Заметки · Веб»
  const toolKeys = persona.tools ?? Object.keys(TOOL_TITLES);
  const toolsText = toolKeys.length === 0
    ? 'Только чат'
    : Object.keys(TOOL_TITLES).filter(k => toolKeys.includes(k)).map(k => TOOL_TITLES[k]).join(' · ');

  // Подпись памяти: выключена / считаем / N записей
  const memoryText = !persona.memoryEnabled
    ? 'Выключена'
    : memory === null
      ? '…'
      : memory.length === 0
        ? 'Включена, пока пусто'
        : `${memory.length} ${plural(memory.length, 'запись', 'записи', 'записей')}`;
  const memoryTitle = persona.memoryEnabled && memory && memory.length > 0
    ? ['semantic', 'episodic', 'procedural'].map(t => {
        const n = memory.filter(e => e.type === t).length;
        const name = t === 'semantic' ? 'факты' : t === 'episodic' ? 'эпизоды' : 'приёмы';
        return `${name}: ${n}`;
      }).join(' · ')
    : undefined;

  const shownChats = (chats ?? []).slice(0, 5);
  const moreChats = (chats?.length ?? 0) - shownChats.length;

  // === Hero: идентичность ===
  const hero = (
    <div style={{ display: 'flex', gap: 18, alignItems: 'flex-start', flexDirection: isMobile ? 'column' : 'row' }}>
      <div style={{ flexShrink: 0, alignSelf: isMobile ? 'center' : 'flex-start' }}>
        <PersonaAvatar persona={persona} size={80} />
      </div>
      <div style={{
        flex: 1, minWidth: 0, width: isMobile ? '100%' : undefined,
        display: 'flex', flexDirection: 'column', gap: 6,
        textAlign: isMobile ? 'center' : 'left',
      }}>
        <div style={{
          fontFamily: FONT.serif, fontSize: isMobile ? 22 : 26, fontWeight: 600,
          color: accent, lineHeight: 1.25, letterSpacing: '-0.01em', overflowWrap: 'break-word',
        }}>
          {persona.role?.trim() || persona.name}
        </div>
        <div style={{
          display: 'flex', alignItems: 'baseline', gap: 8, flexWrap: 'wrap', minWidth: 0,
          justifyContent: isMobile ? 'center' : 'flex-start',
        }}>
          {persona.role?.trim() && (
            <span style={{ fontSize: 15, fontWeight: 600, color: C.textHeading, fontFamily: FONT.sans }}>
              {persona.name}
            </span>
          )}
          <span style={{ fontSize: 12.5, color: C.textMuted, fontFamily: FONT.sans }}>@{persona.handle}</span>
        </div>
        {persona.description?.trim() && (
          <div style={{ fontSize: 13.5, color: C.textSecondary, fontFamily: FONT.sans, lineHeight: 1.5 }}>
            {persona.description}
          </div>
        )}
      </div>
    </div>
  );

  // === Приветствие — как первая реплика персоны (бабл с мини-аватаром) ===
  const greeting = persona.greeting?.trim() ? (
    <div style={{ display: 'flex', gap: 10, alignItems: 'flex-start', marginTop: 18 }}>
      <PersonaAvatar persona={persona} size={26} />
      <div style={{
        background: `${accent}14`, border: `1px solid ${accent}2E`,
        borderRadius: `4px ${R.modal}px ${R.modal}px ${R.modal}px`,
        padding: '9px 14px', fontSize: 13.5, color: C.textPrimary, fontFamily: FONT.sans,
        lineHeight: 1.5, minWidth: 0,
      }}>
        {persona.greeting}
      </div>
    </div>
  ) : null;

  // Подпись доступа к файлам/инструментам (профиль P6)
  const accessText = persona.access === 'readOnly'
    ? 'Только чтение'
    : persona.access === 'custom'
      ? `Свой список${persona.disallowedTools?.length ? ` (${persona.disallowedTools.length})` : ''}`
      : 'Полный';

  // === Факты-строка: модель / возможности / [умения] / память / доступ (+ происхождение из пантеона) ===
  const facts: { label: string; value: string; title?: string }[] = [
    { label: 'Модель', value: persona.effort ? `${modelName} · ${effortLabel(persona.effort)}` : modelName },
    { label: 'Возможности', value: toolsText },
    {
      label: 'Умения',
      value: bindings === null ? '…' : bindings.length === 0 ? 'нет привязок' : bindingsCounter(bindings),
    },
    {
      label: 'Задачи',
      value: taskCounts.total === 0
        ? 'нет поручений'
        : taskCounts.active > 0
          ? `${taskCounts.active} в работе · ${taskCounts.total} всего`
          : `${taskCounts.total} всего`,
    },
    { label: 'Память', value: memoryText, title: memoryTitle },
    { label: 'Доступ', value: accessText },
    ...(persona.templateKey
      ? [{ label: 'Происхождение', value: 'Пантеон OmO', title: `Подключена из шаблона «${persona.templateKey}»` }]
      : []),
  ];
  const factsRow = (
    <div style={{ ...section, display: 'flex', flexWrap: 'wrap', gap: 10 }}>
      {facts.map(f => (
        <div key={f.label} title={f.title} style={factChip}>
          <span style={{ fontSize: 11, fontWeight: 600, letterSpacing: '0.04em', textTransform: 'uppercase', color: C.textMuted }}>
            {f.label}
          </span>
          <span style={{ fontSize: 13, fontWeight: 600, color: C.textHeading }}>{f.value}</span>
        </div>
      ))}
    </div>
  );

  // === Характер (read-only, длинный — сворачивается с fade) ===
  const characterSection = (
    <div style={section}>
      <SectionLabel style={{ marginBottom: 10 }}>Характер</SectionLabel>
      {character ? (
        <>
          <div style={{ position: 'relative' }}>
            <div style={{
              fontSize: 14.5, lineHeight: 1.6, color: C.textPrimary, fontFamily: FONT.sans,
              whiteSpace: 'pre-wrap', overflowWrap: 'break-word',
              maxHeight: characterLong && !expanded ? CHARACTER_COLLAPSED_MAX : undefined,
              overflow: 'hidden',
            }}>
              {character}
            </div>
            {characterLong && !expanded && (
              <div aria-hidden style={{
                position: 'absolute', left: 0, right: 0, bottom: 0, height: 56, pointerEvents: 'none',
                background: `linear-gradient(transparent, ${C.bgMain})`,
              }} />
            )}
          </div>
          {characterLong && (
            <button type="button" onClick={() => setExpanded(v => !v)} style={linkBtn}>
              {expanded ? 'Свернуть' : 'Показать полностью'}
            </button>
          )}
        </>
      ) : (
        <div style={{ fontSize: 13, color: C.textMuted, fontFamily: FONT.sans, lineHeight: 1.5 }}>
          Характер не задан — персона отвечает как обычный ассистент.
          {onEditProfile && (
            <>
              {' '}
              <button type="button" onClick={onEditProfile} style={{ ...linkBtn, marginTop: 0 }}>
                Задать в профиле →
              </button>
            </>
          )}
        </div>
      )}
    </div>
  );

  // === Правила (контракт роли, read-only): тон / всегда / никогда / формат / инструкция ===
  const contract = persona.contract;
  const hasRules = !!contract && !!(
    contract.tone?.trim() || contract.mustDo?.length || contract.mustNot?.length
    || contract.outputFormat?.trim() || contract.instructions?.trim()
  );
  const rulesSection = hasRules ? (
    <div style={section}>
      <SectionLabel style={{ marginBottom: 12 }}>Правила</SectionLabel>
      <div style={{ display: 'flex', flexDirection: 'column', gap: 14 }}>
        {contract!.tone?.trim() && (
          <div style={ruleText}><span style={ruleKey}>Тон: </span>{contract!.tone}</div>
        )}
        {contract!.mustDo && contract!.mustDo.length > 0 && (
          <div>
            <div style={ruleSub}>Всегда</div>
            <ul style={ruleList}>
              {contract!.mustDo.map((r, i) => <li key={i} style={ruleLi}>{r}</li>)}
            </ul>
          </div>
        )}
        {contract!.mustNot && contract!.mustNot.length > 0 && (
          <div>
            <div style={ruleSub}>Никогда</div>
            <ul style={ruleList}>
              {contract!.mustNot.map((r, i) => <li key={i} style={ruleLi}>{r}</li>)}
            </ul>
          </div>
        )}
        {contract!.outputFormat?.trim() && (
          <div style={ruleText}><span style={ruleKey}>Формат ответов: </span>{contract!.outputFormat}</div>
        )}
        {contract!.instructions?.trim() && (
          <div>
            <div style={ruleSub}>Инструкция</div>
            <div style={{
              ...ruleText, whiteSpace: 'pre-wrap', overflowWrap: 'break-word',
              maxHeight: instrOpen ? undefined : 44, overflow: 'hidden',
            }}>
              {contract!.instructions}
            </div>
            <button type="button" onClick={() => setInstrOpen(v => !v)} style={linkBtn}>
              {instrOpen ? 'Свернуть' : 'Показать инструкцию'}
            </button>
          </div>
        )}
      </div>
    </div>
  ) : null;

  // === Знания и правила (фича persona-bindings): компактная выжимка привязок ===
  const shownBindings = (bindings ?? []).filter(b => b.mode !== 'off').slice(0, 4);
  const moreBindings = (bindings?.length ?? 0) - shownBindings.length;
  const knowledgeSection = (
    <div style={section}>
      <div style={{ display: 'flex', alignItems: 'baseline', justifyContent: 'space-between', gap: 10, marginBottom: 12 }}>
        <SectionLabel>Умения и правила</SectionLabel>
        {bindings !== null && bindings.length > 0 && (
          <span style={{ fontSize: 11.5, color: C.textMuted, fontFamily: FONT.sans, flexShrink: 0 }}>
            {bindingsCounter(bindings)}
          </span>
        )}
      </div>

      {bindings === null ? (
        <div style={{ fontSize: 13, color: C.textMuted, fontFamily: FONT.sans, padding: '6px 0' }}>Загружаю…</div>
      ) : bindings.length === 0 ? (
        // Мини-пустышка: источники не подключены
        <div style={{
          border: `1px dashed ${C.dashed}`, borderRadius: R.xl, padding: '18px 16px',
          display: 'flex', flexDirection: 'column', alignItems: 'center', gap: 10, textAlign: 'center',
        }}>
          <span style={{ fontSize: 12.5, color: C.textSecondary, fontFamily: FONT.sans, lineHeight: 1.5 }}>
            Источники не подключены — персона отвечает по общим знаниям.
          </span>
          {onOpenKnowledge && (
            <button type="button" onClick={onOpenKnowledge} style={{ ...linkBtn, marginTop: 0 }}>
              Подключить умения →
            </button>
          )}
        </div>
      ) : (
        <>
          <div style={{ background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.xl, overflow: 'hidden' }}>
            {shownBindings.map((b, i) => (
              <button
                key={b.id}
                type="button"
                onClick={onOpenKnowledge}
                title="Настроить умения"
                onMouseEnter={e => { e.currentTarget.style.background = C.bgSelected; }}
                onMouseLeave={e => { e.currentTarget.style.background = 'transparent'; }}
                style={{
                  display: 'flex', alignItems: 'center', gap: 10, width: '100%', textAlign: 'left',
                  background: 'transparent', border: 'none', padding: '9px 14px', minHeight: 42,
                  borderTop: i > 0 ? `1px solid ${C.borderLight}` : 'none',
                  cursor: onOpenKnowledge ? 'pointer' : 'default', fontFamily: FONT.sans, boxSizing: 'border-box',
                }}
              >
                <BindingTypeIcon type={b.type} size={24} />
                <span style={{
                  flex: 1, minWidth: 0, fontSize: 13, color: C.textPrimary,
                  overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
                }}>
                  <span style={{ fontWeight: 600, color: C.textHeading }}>{bindingLabelOf(b)}</span>
                  {b.condition && <span style={{ color: C.textSecondary }}> — {b.condition}</span>}
                </span>
                <BindingModeBadge mode={b.mode} />
              </button>
            ))}
          </div>
          {moreBindings > 0 && (
            <div style={{ marginTop: 8, fontSize: 12, color: C.textMuted, fontFamily: FONT.sans, textAlign: 'center' }}>
              и ещё {moreBindings} {bindingPlural(moreBindings)}
            </div>
          )}
          {onOpenKnowledge && (
            <button type="button" onClick={onOpenKnowledge} style={linkBtn}>
              Настроить умения →
            </button>
          )}
        </>
      )}
    </div>
  );

  // === Активность (правила автоматизации): сводка триггеров проактивности ===
  const automationRules = persona.automationRules ?? [];
  const enabledRulesCount = automationRules.filter(r => r.enabled).length;
  const triggerTypes = Array.from(new Set(automationRules.map(r => r.trigger.type)));
  const automationSection = (
    <div style={section}>
      <div style={{ display: 'flex', alignItems: 'baseline', justifyContent: 'space-between', gap: 10, marginBottom: 12 }}>
        <SectionLabel>Активность</SectionLabel>
        {automationRules.length > 0 && (
          <span style={{ fontSize: 11.5, color: C.textMuted, fontFamily: FONT.sans, flexShrink: 0 }}>
            {enabledRulesCount === automationRules.length
              ? `${automationRules.length} ${plural(automationRules.length, 'правило', 'правила', 'правил')}`
              : `${automationRules.length} ${plural(automationRules.length, 'правило', 'правила', 'правил')} · ${enabledRulesCount} активно`}
          </span>
        )}
      </div>

      {automationRules.length === 0 ? (
        <div style={miniEmpty}>
          <span style={miniEmptyText}>
            Правил нет — персона откликается только на ваши сообщения.
          </span>
          {onOpenAutomation && (
            <button type="button" onClick={onOpenAutomation} style={{ ...linkBtn, marginTop: 0 }}>
              Настроить проактивность →
            </button>
          )}
        </div>
      ) : (
        <>
          <div style={{
            background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.xl,
            padding: '12px 14px', display: 'flex', flexWrap: 'wrap', gap: 8,
          }}>
            {triggerTypes.map(t => (
              <span key={t} style={triggerChip}>
                {TRIGGER_LABELS[t] ?? t}
              </span>
            ))}
          </div>
          {onOpenAutomation && (
            <button type="button" onClick={onOpenAutomation} style={linkBtn}>
              Настроить проактивность →
            </button>
          )}
        </>
      )}
    </div>
  );

  // === Задачи (поручения персоне-исполнителю): активные сверху ===
  const myTasks: Task[] = allTasks.filter(t => t.personaId === persona.id);
  const activeTasks = myTasks.filter(t => t.status !== 'done');
  const shownTasks = activeTasks.slice(0, 3);
  const moreTasks = activeTasks.length - shownTasks.length;
  const tasksSection = (
    <div style={section}>
      <div style={{ display: 'flex', alignItems: 'baseline', justifyContent: 'space-between', gap: 10, marginBottom: 12 }}>
        <SectionLabel>Задачи</SectionLabel>
        {taskCounts.total > 0 && (
          <span style={{ fontSize: 11.5, color: C.textMuted, fontFamily: FONT.sans, flexShrink: 0 }}>
            {taskCounts.total} всего{taskCounts.active > 0 ? ` · ${taskCounts.active} в работе` : ''}
          </span>
        )}
      </div>

      {activeTasks.length === 0 ? (
        <div style={miniEmpty}>
          <span style={miniEmptyText}>
            {taskCounts.total === 0
              ? 'Поручений нет — персона ждёт ваших сообщений в чате.'
              : 'Активных поручений нет — всё выполнено.'}
          </span>
          {onOpenTasks && (
            <button type="button" onClick={onOpenTasks} style={{ ...linkBtn, marginTop: 0 }}>
              Все задачи →
            </button>
          )}
        </div>
      ) : (
        <>
          <div style={{ background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.xl, overflow: 'hidden' }}>
            {shownTasks.map((t, i) => (
              <button
                key={t.id}
                type="button"
                onClick={onOpenTasks}
                title="Открыть задачи персоны"
                onMouseEnter={e => { e.currentTarget.style.background = C.bgSelected; }}
                onMouseLeave={e => { e.currentTarget.style.background = 'transparent'; }}
                style={{
                  display: 'flex', alignItems: 'center', gap: 10, width: '100%', textAlign: 'left',
                  background: 'transparent', border: 'none', padding: '10px 14px', minHeight: 40,
                  borderTop: i > 0 ? `1px solid ${C.borderLight}` : 'none',
                  cursor: onOpenTasks ? 'pointer' : 'default', fontFamily: FONT.sans, boxSizing: 'border-box',
                }}
              >
                <span style={{
                  flex: 1, minWidth: 0, fontSize: 13, fontWeight: 500, color: C.textPrimary,
                  overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
                }}>
                  {t.title}
                </span>
                {t.status === 'inProgress' && (
                  <span style={{ fontSize: 11, fontWeight: 600, color: C.accent, flexShrink: 0 }}>в работе</span>
                )}
              </button>
            ))}
          </div>
          {moreTasks > 0 && (
            <div style={{ marginTop: 8, fontSize: 12, color: C.textMuted, fontFamily: FONT.sans, textAlign: 'center' }}>
              и ещё {moreTasks} {plural(moreTasks, 'задача', 'задачи', 'задач')}
            </div>
          )}
          {onOpenTasks && (
            <button type="button" onClick={onOpenTasks} style={linkBtn}>
              Все задачи →
            </button>
          )}
        </>
      )}
    </div>
  );

  // === Память: что персона запомнила (факты/эпизоды/приёмы) ===
  const memoryEntries = memory ?? [];
  const shownMemory = [...memoryEntries]
    .sort((a, b) => b.createdAt.localeCompare(a.createdAt))
    .slice(0, 3);
  const memorySection = (
    <div style={section}>
      <div style={{ display: 'flex', alignItems: 'baseline', justifyContent: 'space-between', gap: 10, marginBottom: 12 }}>
        <SectionLabel>Память</SectionLabel>
        {persona.memoryEnabled && memoryEntries.length > 0 && memoryTitle && (
          <span style={{ fontSize: 11.5, color: C.textMuted, fontFamily: FONT.sans, flexShrink: 0 }}>
            {memoryTitle}
          </span>
        )}
      </div>

      {!persona.memoryEnabled ? (
        <div style={miniEmpty}>
          <span style={miniEmptyText}>
            Память выключена — персона не запоминает контекст между разговорами.
          </span>
          {onOpenMemory && (
            <button type="button" onClick={onOpenMemory} style={{ ...linkBtn, marginTop: 0 }}>
              Открыть память →
            </button>
          )}
        </div>
      ) : memory === null ? (
        <div style={{ fontSize: 13, color: C.textMuted, fontFamily: FONT.sans, padding: '6px 0' }}>Загружаю…</div>
      ) : memoryEntries.length === 0 ? (
        <div style={miniEmpty}>
          <span style={miniEmptyText}>
            Включена, пока пусто — персона запомнит важное по ходу разговоров.
          </span>
          {onOpenMemory && (
            <button type="button" onClick={onOpenMemory} style={{ ...linkBtn, marginTop: 0 }}>
              Открыть память →
            </button>
          )}
        </div>
      ) : (
        <>
          <div style={{ background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.xl, overflow: 'hidden' }}>
            {shownMemory.map((m, i) => (
              <button
                key={m.id}
                type="button"
                onClick={onOpenMemory}
                title="Открыть память"
                onMouseEnter={e => { e.currentTarget.style.background = C.bgSelected; }}
                onMouseLeave={e => { e.currentTarget.style.background = 'transparent'; }}
                style={{
                  display: 'flex', alignItems: 'flex-start', gap: 10, width: '100%', textAlign: 'left',
                  background: 'transparent', border: 'none', padding: '10px 14px', minHeight: 40,
                  borderTop: i > 0 ? `1px solid ${C.borderLight}` : 'none',
                  cursor: onOpenMemory ? 'pointer' : 'default', fontFamily: FONT.sans, boxSizing: 'border-box',
                }}
              >
                <span style={memoryTypeBadge}>{MEMORY_TYPE_LABEL[m.type] ?? m.type}</span>
                <span style={{
                  flex: 1, minWidth: 0, fontSize: 13, color: C.textPrimary, lineHeight: 1.45,
                  display: '-webkit-box', WebkitLineClamp: 2, WebkitBoxOrient: 'vertical', overflow: 'hidden',
                }}>
                  {m.text}
                </span>
              </button>
            ))}
          </div>
          {onOpenMemory && (
            <button type="button" onClick={onOpenMemory} style={linkBtn}>
              Открыть память →
            </button>
          )}
        </>
      )}
    </div>
  );

  // === Недавние разговоры ===
  const chatsSection = (
    <div style={section}>
      <div style={{ display: 'flex', alignItems: 'baseline', justifyContent: 'space-between', gap: 10, marginBottom: 12 }}>
        <SectionLabel>Недавние разговоры</SectionLabel>
        {chats !== null && chats.length > 0 && (
          <span style={{ fontSize: 11.5, color: C.textMuted, fontFamily: FONT.sans, flexShrink: 0 }}>
            {chats.length} {plural(chats.length, 'чат', 'чата', 'чатов')}
          </span>
        )}
      </div>

      {chats === null ? (
        <div style={{ fontSize: 13, color: C.textMuted, fontFamily: FONT.sans, padding: '6px 0' }}>Загружаю…</div>
      ) : shownChats.length === 0 ? (
        <div style={{
          border: `1px dashed ${C.dashed}`, borderRadius: R.xl, padding: '22px 16px',
          display: 'flex', flexDirection: 'column', alignItems: 'center', gap: 10, textAlign: 'center',
        }}>
          <span style={{ fontSize: 13, color: C.textMuted, fontFamily: FONT.sans }}>
            Разговоров ещё не было
          </span>
          <button type="button" onClick={onTalk} disabled={talking} style={{
            background: accent, color: C.onAccent, border: 'none', borderRadius: R.lg,
            padding: '8px 16px', fontSize: 13, fontWeight: 600, fontFamily: FONT.sans,
            cursor: talking ? 'default' : 'pointer', opacity: talking ? 0.6 : 1,
          }}>
            {talking ? 'Создаём…' : 'Начать первый разговор'}
          </button>
        </div>
      ) : (
        <>
          <div style={{ background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.xl, overflow: 'hidden' }}>
            {shownChats.map((s, i) => (
              <button
                key={s.id}
                type="button"
                onClick={() => onOpenSession(s)}
                title="Открыть чат"
                style={{
                  display: 'block', width: '100%', textAlign: 'left', cursor: 'pointer',
                  background: 'transparent', border: 'none', padding: '11px 14px',
                  borderTop: i > 0 ? `1px solid ${C.borderLight}` : 'none',
                  fontFamily: FONT.sans, boxSizing: 'border-box',
                }}
                onMouseEnter={e => { e.currentTarget.style.background = C.bgSelected; }}
                onMouseLeave={e => { e.currentTarget.style.background = 'transparent'; }}
              >
                <div style={{ display: 'flex', alignItems: 'baseline', gap: 10 }}>
                  <span style={{
                    flex: 1, minWidth: 0, fontSize: 13.5, fontWeight: 600, color: C.textHeading,
                    overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
                  }}>
                    {s.name?.trim() || 'Без названия'}
                  </span>
                  <span style={{ fontSize: 11.5, color: C.textMuted, flexShrink: 0 }}>
                    {relativeTime(s.updatedAt)}
                  </span>
                </div>
                {s.lastMessage?.trim() && (
                  <div style={{
                    marginTop: 3, fontSize: 12.5, color: C.textSecondary, lineHeight: 1.45,
                    overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
                  }}>
                    {s.lastMessage}
                  </div>
                )}
              </button>
            ))}
          </div>
          {moreChats > 0 && (
            <div style={{ marginTop: 8, fontSize: 12, color: C.textMuted, fontFamily: FONT.sans, textAlign: 'center' }}>
              и ещё {moreChats} {plural(moreChats, 'разговор', 'разговора', 'разговоров')}
            </div>
          )}
        </>
      )}
    </div>
  );

  return (
    <div style={{ height: '100%', overflowY: 'auto', background: C.bgMain }}>
      <div style={{
        maxWidth: 680, margin: '0 auto', boxSizing: 'border-box',
        padding: isMobile ? '20px 16px 32px' : '26px 32px 40px',
        display: 'flex', flexDirection: 'column', gap: 24,
      }}>
        <div>
          {hero}
          {/* Поговорить — после описания, до фактов и настроек */}
          <button type="button" onClick={onTalk} disabled={talking} style={{
            width: '100%', display: 'flex', alignItems: 'center', justifyContent: 'center', gap: 8,
            background: accent, color: C.onAccent, border: 'none', borderRadius: R.xl,
            padding: '11px 16px', fontSize: 14, fontWeight: 600, fontFamily: FONT.sans,
            cursor: talking ? 'default' : 'pointer', opacity: talking ? 0.6 : 1,
            marginTop: 18,
          }}>
            <MessageSquare size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} style={{ flexShrink: 0 }} />
            {talking ? 'Создаём…' : 'Поговорить'}
          </button>
          {greeting}
        </div>
        {factsRow}
        {characterSection}
        {rulesSection}
        {knowledgeSection}
        {automationSection}
        {tasksSection}
        {memorySection}
        {chatsSection}
      </div>
    </div>
  );
}

// Плоская секция с разделителем сверху — тот же паттерн, что в PersonaForm
const section: React.CSSProperties = {
  borderTop: `1px solid ${C.borderLight}`, paddingTop: 20,
};

const factChip: React.CSSProperties = {
  display: 'flex', flexDirection: 'column', gap: 3,
  background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.lg,
  padding: '8px 13px', fontFamily: FONT.sans, minWidth: 0,
};

const linkBtn: React.CSSProperties = {
  background: 'transparent', border: 'none', color: C.accent, cursor: 'pointer',
  fontSize: 13, fontWeight: 600, fontFamily: FONT.sans, padding: 0, marginTop: 8,
};

// Мини-пустышка для обзорных секций (Активность/Задачи/Память) — пунктирная рамка
const miniEmpty: React.CSSProperties = {
  border: `1px dashed ${C.dashed}`, borderRadius: R.xl, padding: '18px 16px',
  display: 'flex', flexDirection: 'column', alignItems: 'center', gap: 10, textAlign: 'center',
};
const miniEmptyText: React.CSSProperties = {
  fontSize: 12.5, color: C.textSecondary, fontFamily: FONT.sans, lineHeight: 1.5,
};

// Чип типа триггера в сводке «Активность»
const triggerChip: React.CSSProperties = {
  fontSize: 12.5, fontWeight: 600, color: C.textSecondary, fontFamily: FONT.sans,
  background: C.bgSelected, borderRadius: R.md, padding: '4px 10px',
};

// Бейдж типа записи памяти (факт/эпизод/приём)
const memoryTypeBadge: React.CSSProperties = {
  fontSize: 10.5, fontWeight: 600, letterSpacing: '0.03em', textTransform: 'uppercase',
  color: C.textMuted, fontFamily: FONT.sans, background: C.bgSelected, borderRadius: R.sm,
  padding: '3px 8px', flexShrink: 0, marginTop: 1,
};

// Стили секции «Правила» — read-only текст в тоне соседних секций превью
const ruleText: React.CSSProperties = {
  fontSize: 14, lineHeight: 1.55, color: C.textPrimary, fontFamily: FONT.sans,
};
const ruleKey: React.CSSProperties = {
  fontWeight: 600, color: C.textHeading,
};
const ruleSub: React.CSSProperties = {
  fontSize: 12, fontWeight: 600, letterSpacing: '0.03em', textTransform: 'uppercase',
  color: C.textMuted, fontFamily: FONT.sans, marginBottom: 6,
};
const ruleList: React.CSSProperties = {
  margin: 0, paddingLeft: 20, display: 'flex', flexDirection: 'column', gap: 4,
};
const ruleLi: React.CSSProperties = {
  fontSize: 14, lineHeight: 1.5, color: C.textPrimary, fontFamily: FONT.sans,
};
