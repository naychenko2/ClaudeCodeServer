import { useState, useEffect, useRef, type ReactNode } from 'react';
import type { Role, RoleDraft, AgentInfo } from '../types';
import { api } from '../lib/api';
import { MODELS } from '../lib/models';
import { EFFORTS } from '../lib/effort';
import { C, R, MODAL_W } from '../lib/design';
import { Modal, Button, Field, TextField, TextArea, SegmentedControl } from './ui';
import { RoleAvatar } from './RoleAvatar';

// Пресеты для быстрого выбора (можно ввести любой эмодзи руками)
const EMOJI_PRESETS = ['🔧', '🎨', '🧠', '🗄️', '📊', '🧪', '🚀', '📝', '🔬', '🛡️', '⚙️', '🤝'];
const COLOR_PRESETS = ['#D97757', '#6C5CB0', '#3E7CA6', '#5E8B4E', '#C9923E', '#B4452F', '#7A6A58', '#2A8C82'];

const STEPS = ['Лицо', 'Компетенции', 'Параметры'];

// Первое приветствие собеседования — статично (без вызова claude, чтобы не ждать).
// Случайный вариант для разнообразия; все спрашивают имя, чтобы claude продолжил со 2-го вопроса.
const GREETINGS = [
  'Привет! 👋 Рад знакомству. Давай оформим тебя в команду — для начала, как тебя зовут?',
  'О, новенький! 🎉 Добро пожаловать. С кем имею честь — как тебя зовут?',
  'Здорово, что ты с нами! 😎 Начнём с простого: как тебя зовут?',
  'Приветствую в команде! 🤝 Давай знакомиться — как тебя звать?',
  'Хэй! 👋 Будем работать вместе. Для начала скажи, как тебя зовут?',
];

// pick — выбор способа (вручную/диалог), interview — чат, wizard — пошаговая форма
type Phase = 'pick' | 'interview' | 'wizard';
interface InterviewMsg { role: 'assistant' | 'user'; content: string }

// Лёгкий рендер inline-markdown в сообщениях собеседования: **жирный** → <strong>
function renderInline(text: string): ReactNode[] {
  return text.split(/(\*\*[^*]+\*\*)/g).map((p, i) =>
    p.startsWith('**') && p.endsWith('**') && p.length > 4
      ? <strong key={i}>{p.slice(2, -2)}</strong>
      : <span key={i}>{p}</span>
  );
}

interface Props {
  projectId: string;
  role?: Role;                       // задан → режим редактирования
  onSaved: (role: Role) => void;
  onClose: () => void;
}

export function RoleEditorDialog({ projectId, role, onSaved, onClose }: Props) {
  // Редактирование существующей роли — сразу мастер; создание — стартуем с выбора способа
  const [phase, setPhase] = useState<Phase>(role ? 'wizard' : 'pick');
  const [step, setStep] = useState(0);
  const [name, setName] = useState(role?.name ?? '');
  const [title, setTitle] = useState(role?.title ?? '');
  const [avatar, setAvatar] = useState(role?.avatar ?? '');
  const [color, setColor] = useState(role?.color || COLOR_PRESETS[0]);
  const [persona, setPersona] = useState(role?.persona ?? '');
  const [agentNames, setAgentNames] = useState<string[]>(role?.agentNames ?? []);
  const [systemPrompt, setSystemPrompt] = useState(role?.systemPrompt ?? '');
  const [model, setModel] = useState(role?.model ?? '');
  const [effort, setEffort] = useState(role?.effort ?? '');
  const [agents, setAgents] = useState<AgentInfo[]>([]);
  const [memory, setMemory] = useState('');
  const [memoryDirty, setMemoryDirty] = useState(false);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  // Состояние диалога-собеседования
  const [messages, setMessages] = useState<InterviewMsg[]>([]);
  const [input, setInput] = useState('');
  const [busy, setBusy] = useState(false);
  const chatScrollRef = useRef<HTMLDivElement>(null);

  // Автоскролл ленты собеседования вниз при новых сообщениях / индикаторе
  useEffect(() => {
    const el = chatScrollRef.current;
    if (el) el.scrollTop = el.scrollHeight;
  }, [messages, busy]);

  useEffect(() => {
    api.skills.list(projectId).then(d => setAgents(d.agents)).catch(() => {});
  }, [projectId]);

  // Память существует только у сохранённой роли — подгружаем при редактировании
  useEffect(() => {
    if (role) api.roles.getMemory(projectId, role.id).then(d => setMemory(d.content)).catch(() => {});
  }, [projectId, role]);

  const toggleAgent = (fileName: string) => {
    setAgentNames(prev =>
      prev.includes(fileName) ? prev.filter(a => a !== fileName) : [...prev, fileName]
    );
  };

  // --- Интервью ---

  const applyDraft = (d: RoleDraft) => {
    setName(d.name || '');
    setTitle(d.title || '');
    setAvatar(d.avatar || '');
    if (d.color) setColor(d.color);
    setPersona(d.persona || '');
    setAgentNames(d.agentNames || []);
    setSystemPrompt(d.systemPrompt || '');
    setModel(d.model || '');
    setEffort(d.effort || '');
  };

  const runInterview = async (history: InterviewMsg[]) => {
    setBusy(true);
    setError(null);
    try {
      const res = await api.roles.interview(projectId, history);
      if (res.role) {
        applyDraft(res.role);
        setStep(0);
        setPhase('wizard');   // черновик готов — отдаём в мастер на правку
      } else if (res.question) {
        setMessages([...history, { role: 'assistant', content: res.question }]);
      }
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Ошибка интервью');
    } finally {
      setBusy(false);
    }
  };

  // Старт собеседования: первое приветствие показываем сразу (статично, без ожидания claude).
  // Дальше, со второго хода, отвечает claude.
  useEffect(() => {
    if (phase === 'interview' && messages.length === 0 && !busy) {
      const greeting = GREETINGS[Math.floor(Math.random() * GREETINGS.length)];
      setMessages([{ role: 'assistant', content: greeting }]);
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [phase]);

  const sendAnswer = () => {
    const text = input.trim();
    if (!text || busy) return;
    const next: InterviewMsg[] = [...messages, { role: 'user', content: text }];
    setMessages(next);
    setInput('');
    runInterview(next);
  };

  // --- Сохранение ---

  const isLast = step === STEPS.length - 1;

  const goNext = () => {
    if (step === 0 && !name.trim()) { setError('Укажите имя сотрудника'); return; }
    setError(null);
    setStep(s => Math.min(s + 1, STEPS.length - 1));
  };

  const handleSubmit = async () => {
    if (!name.trim()) { setError('Укажите имя сотрудника'); setStep(0); return; }
    setLoading(true);
    setError(null);
    const payload = {
      name: name.trim(), title: title.trim(), avatar: avatar.trim(), color,
      persona, agentNames, systemPrompt: systemPrompt.trim() || undefined,
      model: model || undefined, effort: effort || undefined,
    };
    try {
      const saved = role
        ? await api.roles.update(projectId, role.id, payload)
        : await api.roles.create(projectId, payload);
      if (role && memoryDirty) await api.roles.saveMemory(projectId, saved.id, memory);
      onSaved(saved);
      onClose();
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Ошибка сохранения');
    } finally {
      setLoading(false);
    }
  };

  // --- Заголовок и футер по фазе ---

  const title_ = role ? 'Редактировать сотрудника'
    : phase === 'pick' ? 'Новый член команды'
    : phase === 'interview' ? 'Собеседование'
    : 'Новый член команды';

  let footer: ReactNode;
  if (phase === 'pick') {
    footer = <Button variant="secondary" fullWidth onClick={onClose}>Отмена</Button>;
  } else if (phase === 'interview') {
    footer = (
      <Button variant="secondary" fullWidth onClick={() => { setPhase('pick'); setMessages([]); setError(null); }}>
        ← Назад к выбору
      </Button>
    );
  } else {
    footer = (
      <div style={{ display: 'flex', gap: 10, width: '100%' }}>
        <div style={{ flex: 1 }}>
          <Button variant="secondary" fullWidth
            onClick={step === 0 ? onClose : () => { setStep(s => s - 1); setError(null); }}>
            {step === 0 ? 'Отмена' : 'Назад'}
          </Button>
        </div>
        <div style={{ flex: 1.5 }}>
          {isLast
            ? <Button variant="primary" fullWidth loading={loading} onClick={handleSubmit}>
                {loading ? 'Сохраняем…' : (role ? 'Сохранить' : 'Принять в команду')}
              </Button>
            : <Button variant="primary" fullWidth onClick={goNext}>Далее</Button>}
        </div>
      </div>
    );
  }

  // Степпер мастера
  const stepper = (
    <div style={{ display: 'flex', gap: 6 }}>
      {STEPS.map((label, i) => (
        <div key={label}
          onClick={() => { if (i < step) { setStep(i); setError(null); } }}
          style={{ flex: 1, display: 'flex', flexDirection: 'column', gap: 4, cursor: i < step ? 'pointer' : 'default' }}
        >
          <div style={{ height: 4, borderRadius: 2, background: i <= step ? C.accent : C.border }} />
          <span style={{ fontSize: 11, fontWeight: 600, color: i === step ? C.textHeading : C.textMuted }}>
            {i + 1}. {label}
          </span>
        </div>
      ))}
    </div>
  );

  return (
    <Modal title={title_} width={MODAL_W.form} onClose={onClose} footer={footer}>

      {/* === Фаза: выбор способа === */}
      {phase === 'pick' && (
        <div style={{ display: 'flex', flexDirection: 'column', gap: 10 }}>
          <PickCard emoji="✍️" title="Заполнить вручную"
            desc="Сам пройду по шагам: имя, характер, компетенции, параметры."
            onClick={() => setPhase('wizard')} />
          <PickCard emoji="💬" title="Провести собеседование"
            desc="Отвечу на пару вопросов — оформим нового сотрудника, дальше поправлю."
            onClick={() => setPhase('interview')} />
        </div>
      )}

      {/* === Фаза: интервью === */}
      {phase === 'interview' && (
        <div style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
          <div style={{ fontSize: 12.5, color: C.textSecondary, lineHeight: 1.5, background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.md, padding: '10px 12px' }}>
            Задам пару вопросов о новом члене команды — кем угодно. В конце оформлю карточку, потом поправишь.
          </div>
          <div ref={chatScrollRef} style={{
            display: 'flex', flexDirection: 'column', gap: 8,
            maxHeight: 340, overflowY: 'auto', padding: '2px 2px',
          }}>
            {messages.map((m, i) => (
              <div key={i} style={{
                alignSelf: m.role === 'user' ? 'flex-end' : 'flex-start',
                maxWidth: '85%', padding: '9px 12px', borderRadius: 14, fontSize: 13.5, lineHeight: 1.45,
                background: m.role === 'user' ? C.accent : C.bgWhite,
                color: m.role === 'user' ? C.onAccent : C.textHeading,
                border: m.role === 'user' ? 'none' : `1px solid ${C.border}`,
                whiteSpace: 'pre-wrap', wordBreak: 'break-word',
              }}>
                {renderInline(m.content)}
              </div>
            ))}
            {busy && (
              <div style={{ alignSelf: 'flex-start', display: 'flex', alignItems: 'center', gap: 7, color: C.textMuted, fontSize: 12.5, padding: '4px 2px' }}>
                <span className="tool-spinner" style={{ width: 12, height: 12 }} /> Claude думает…
              </div>
            )}
          </div>

          <div style={{ display: 'flex', gap: 8, alignItems: 'flex-end' }}>
            <div style={{ flex: 1 }}>
              <TextField value={input} onChange={setInput}
                placeholder={busy ? 'Подождите…' : 'Ваш ответ…'}
                onEnter={sendAnswer} disabled={busy} />
            </div>
            <Button variant="primary" onClick={sendAnswer} disabled={busy || !input.trim()}>➤</Button>
          </div>
        </div>
      )}

      {/* === Фаза: мастер === */}
      {phase === 'wizard' && (
        <>
          {stepper}

          {/* Шаг 1: Лицо */}
          {step === 0 && (
            <>
              <div style={{ display: 'flex', alignItems: 'center', gap: 12 }}>
                <RoleAvatar name={name || '?'} avatar={avatar} color={color} size={48} />
                <div style={{ flex: 1, minWidth: 0, display: 'flex', flexDirection: 'column', gap: 8 }}>
                  <TextField value={name} onChange={setName} placeholder="Имя (напр. Игорь)" autoFocus />
                  <TextField value={title} onChange={setTitle} placeholder="Должность (напр. Backend-разработчик)" />
                </div>
              </div>

              <Field label="Аватар (эмодзи)" hint="Пусто → в кружке будут инициалы имени.">
                <div style={{ display: 'flex', flexWrap: 'wrap', gap: 6, alignItems: 'center' }}>
                  {EMOJI_PRESETS.map(e => (
                    <button key={e} type="button" onClick={() => setAvatar(e)}
                      style={{
                        width: 34, height: 34, borderRadius: R.md, cursor: 'pointer', fontSize: 18,
                        background: avatar === e ? C.accentLight : C.bgWhite,
                        border: `1px solid ${avatar === e ? C.accent : C.border}`,
                        display: 'flex', alignItems: 'center', justifyContent: 'center',
                      }}
                    >{e}</button>
                  ))}
                  <button type="button" onClick={() => setAvatar('')}
                    title="Без эмодзи (инициалы)"
                    style={{
                      minWidth: 34, height: 34, padding: '0 10px', borderRadius: R.md, cursor: 'pointer', fontSize: 12,
                      background: avatar === '' ? C.accentLight : C.bgWhite,
                      border: `1px solid ${avatar === '' ? C.accent : C.border}`,
                      color: C.textSecondary, fontWeight: 600,
                    }}
                  >Aa</button>
                </div>
              </Field>

              <Field label="Цвет">
                <div style={{ display: 'flex', flexWrap: 'wrap', gap: 8 }}>
                  {COLOR_PRESETS.map(c => (
                    <button key={c} type="button" onClick={() => setColor(c)}
                      style={{
                        width: 28, height: 28, borderRadius: '50%', cursor: 'pointer', background: c,
                        border: color === c ? `2px solid ${C.textHeading}` : '2px solid transparent',
                        boxShadow: color === c ? '0 0 0 2px #FFF inset' : 'none',
                      }}
                    />
                  ))}
                </div>
              </Field>

              <Field label="Характер и стиль речи" hint="Как сотрудник себя ведёт и разговаривает.">
                <TextArea value={persona} onChange={setPersona} autoGrow minHeight={60}
                  placeholder="Напр.: дотошный, любит чистый код, отвечает по делу, без воды…" />
              </Field>
            </>
          )}

          {/* Шаг 2: Компетенции */}
          {step === 1 && (
            <>
              <Field label="Компетенции (агенты)" hint="Тела выбранных агентов попадут в системный промпт сотрудника. «глоб» — глобальный агент (~/.claude/agents).">
                {agents.length === 0 ? (
                  <span style={{ fontSize: 12.5, color: C.textMuted }}>
                    Нет доступных агентов. Сотрудник будет работать на характере и доп. промпте.
                  </span>
                ) : (
                  <div style={{ display: 'flex', flexDirection: 'column', gap: 4 }}>
                    {agents.map(a => {
                      const checked = agentNames.includes(a.fileName);
                      return (
                        <button key={a.fileName} type="button" onClick={() => toggleAgent(a.fileName)}
                          style={{
                            display: 'flex', alignItems: 'flex-start', gap: 9, padding: '8px 10px',
                            borderRadius: R.md, cursor: 'pointer', textAlign: 'left', width: '100%',
                            border: `1px solid ${checked ? C.accent : C.border}`,
                            background: checked ? C.accentLight : C.bgWhite,
                          }}
                        >
                          <span style={{
                            width: 16, height: 16, borderRadius: 4, flexShrink: 0, marginTop: 1,
                            border: `1.5px solid ${checked ? C.accent : C.dashed}`,
                            background: checked ? C.accent : 'transparent',
                            display: 'flex', alignItems: 'center', justifyContent: 'center',
                          }}>
                            {checked && (
                              <svg width="10" height="10" viewBox="0 0 24 24" fill="none" stroke={C.onAccent} strokeWidth="3.5" strokeLinecap="round" strokeLinejoin="round"><polyline points="20 6 9 17 4 12" /></svg>
                            )}
                          </span>
                          <span style={{ flex: 1, minWidth: 0 }}>
                            <span style={{ display: 'block', fontSize: 13, fontWeight: 600, color: C.textHeading }}>
                              {a.color && <span style={{ display: 'inline-block', width: 7, height: 7, borderRadius: '50%', background: a.color, marginRight: 6 }} />}
                              {a.name}
                              {a.scope === 'user' && (
                                <span style={{ marginLeft: 6, fontSize: 10, fontWeight: 600, color: C.textMuted, background: C.bgSelected, borderRadius: 4, padding: '1px 5px', verticalAlign: 'middle' }}>глоб</span>
                              )}
                            </span>
                            {a.description && (
                              <span style={{ display: 'block', fontSize: 11.5, color: C.textMuted, marginTop: 1, lineHeight: 1.35, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{a.description}</span>
                            )}
                          </span>
                        </button>
                      );
                    })}
                  </div>
                )}
              </Field>

              <Field label="Доп. инструкции" hint="Опционально — свободный промпт поверх агентов.">
                <TextArea value={systemPrompt} onChange={setSystemPrompt} autoGrow minHeight={48}
                  placeholder="Особые правила конкретно для этого сотрудника…" />
              </Field>
            </>
          )}

          {/* Шаг 3: Параметры */}
          {step === 2 && (
            <>
              <Field label="Модель по умолчанию">
                <SegmentedControl value={model} options={MODELS} onChange={setModel} columns={2} />
              </Field>

              <Field label="Усилие рассуждения">
                <SegmentedControl value={effort} options={EFFORTS} onChange={setEffort} columns={3} />
              </Field>

              {role && (
                <Field label="Память сотрудника" hint="Факты и договорённости из прошлых бесед. Сотрудник пополняет её сам ([MEMORY] + авто-summary); можно править вручную.">
                  <TextArea value={memory} onChange={v => { setMemory(v); setMemoryDirty(true); }} autoGrow minHeight={60}
                    placeholder="Память пока пуста — появится по мере общения с сотрудником." />
                </Field>
              )}
            </>
          )}
        </>
      )}

      {error && <p style={{ margin: 0, fontSize: 13, color: C.danger }}>{error}</p>}
    </Modal>
  );
}

// Карточка выбора способа создания роли
function PickCard({ emoji, title, desc, onClick }: { emoji: string; title: string; desc: string; onClick: () => void }) {
  return (
    <button type="button" onClick={onClick}
      style={{
        display: 'flex', alignItems: 'flex-start', gap: 12, padding: '14px 14px', width: '100%',
        textAlign: 'left', cursor: 'pointer', borderRadius: R.xl,
        border: `1px solid ${C.border}`, background: C.bgWhite,
      }}
      onMouseEnter={e => { e.currentTarget.style.borderColor = C.accent; e.currentTarget.style.background = C.accentLight; }}
      onMouseLeave={e => { e.currentTarget.style.borderColor = C.border; e.currentTarget.style.background = C.bgWhite; }}
    >
      <span style={{ fontSize: 24, lineHeight: 1, flexShrink: 0 }}>{emoji}</span>
      <span style={{ flex: 1, minWidth: 0 }}>
        <span style={{ display: 'block', fontSize: 14, fontWeight: 700, color: C.textHeading }}>{title}</span>
        <span style={{ display: 'block', fontSize: 12.5, color: C.textMuted, marginTop: 2, lineHeight: 1.4 }}>{desc}</span>
      </span>
    </button>
  );
}
