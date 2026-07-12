import { useState } from 'react';
import { Check } from 'lucide-react';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import type { Persona, PantheonTemplate } from '../../types';
import { C, FONT, R } from '../../lib/design';
import { Modal, ModalActions, TextArea } from '../../components/ui';
import { personaTitleLines } from '../../lib/personas';
import { api } from '../../lib/api';
import { showToast } from '../../lib/toast';
import { agentDotColor } from '../../components/AgentSelector';
import { PersonaAvatar } from './PersonaAvatar';
import { usePantheon, materializePantheon } from './usePantheon';

// Режимы командной работы:
//  - discuss — ведущая персона сама опрашивает участников через persona_ask и сводит итог
//    (промпт-обвязка обычного сообщения, бэкенд не участвует);
//  - meeting — совещание P7 (флаг persona-group-chats): независимые позиции →
//    перекрёстная критика → синтез; оркестрирует бэкенд (POST /chats/{id}/meeting);
//  - pipeline — конвейер пантеона (флаг persona-pipeline): анализ → план → ревью →
//    авто-исполнение фиксированными ролями (POST /chats/{id}/pipeline).
type DiscussMode = 'discuss' | 'meeting' | 'pipeline';

// Роли-исполнители финальной фазы конвейера
const EXECUTORS: { key: string; label: string; desc: string }[] = [
  { key: 'omo-hephaestus', label: 'Мастер (Гефест)', desc: 'Автономный исполнитель: доводит план до конца' },
  { key: 'omo-sisyphus', label: 'Оркестратор (Сизиф)', desc: 'Делегирует части плана субагентам и проверяет' },
];

export function DiscussTeamDialog({ candidates, chatPersona, sessionId, meetingEnabled, pipelineEnabled, onSend, onClose }: {
  candidates: Persona[];
  // Персона самого чата — ведущая совещания (первая в списке участников)
  chatPersona?: Persona | null;
  // Чат, в котором запускается совещание/конвейер (для POST /chats/{id}/…)
  sessionId?: string;
  // Доступен ли режим «Совещание» (флаг persona-group-chats)
  meetingEnabled?: boolean;
  // Доступен ли режим «Конвейер» (флаг persona-pipeline)
  pipelineEnabled?: boolean;
  // Отправить собранное сообщение в чат (обычный send) — режим «Обсуждение»
  onSend: (text: string) => void;
  onClose: () => void;
}) {
  // Виртуальные роли пантеона — участниками дискуссии/совещания (материализуются при выборе)
  const { virtual: virtualPantheon } = usePantheon();
  const [materializing, setMaterializing] = useState<string | null>(null);
  // Локально материализованные роли — чтобы показать их как обычных кандидатов
  const [extraCandidates, setExtraCandidates] = useState<Persona[]>([]);
  const allCandidates = [...candidates, ...extraCandidates];

  // Доступные режимы: обсуждение/совещание нужны участники; конвейер — только чат
  const canDiscuss = allCandidates.length > 0 || virtualPantheon.length > 0;
  const availableModes: DiscussMode[] = [
    ...(canDiscuss ? (['discuss'] as DiscussMode[]) : []),
    ...(meetingEnabled && sessionId && canDiscuss ? (['meeting'] as DiscussMode[]) : []),
    ...(pipelineEnabled && sessionId ? (['pipeline'] as DiscussMode[]) : []),
  ];
  const [mode, setMode] = useState<DiscussMode>(availableModes[0] ?? 'discuss');

  const [selected, setSelected] = useState<string[]>(
    candidates.length === 1 ? [candidates[0].id] : []
  );
  const [question, setQuestion] = useState('');
  const [executorKey, setExecutorKey] = useState(EXECUTORS[0].key);
  const [starting, setStarting] = useState(false);
  // Обсуждение — до 2 собеседников; совещание — до 3 (плюс ведущая = максимум 4)
  const max = mode === 'meeting' ? 3 : 2;

  const toggle = (id: string) =>
    setSelected(prev => prev.includes(id)
      ? prev.filter(x => x !== id)
      : prev.length >= max ? prev : [...prev, id]);

  // Выбор виртуальной роли участником: материализуем и добавляем в кандидаты + выбор
  const toggleVirtual = async (t: PantheonTemplate) => {
    if (selected.length >= max) return;
    setMaterializing(t.key);
    try {
      const persona = await materializePantheon(t.key);
      setExtraCandidates(prev => prev.some(p => p.id === persona.id) ? prev : [...prev, persona]);
      setSelected(prev => prev.includes(persona.id) || prev.length >= max ? prev : [...prev, persona.id]);
    } catch (e) {
      showToast('Пантеон OmO', e instanceof Error ? e.message : 'Не удалось подключить роль', 'info');
    } finally {
      setMaterializing(null);
    }
  };

  const canSend = mode === 'pipeline'
    ? question.trim().length > 0 && !starting
    : selected.length > 0 && question.trim().length > 0 && !starting;
  // Участники совещания: ведущая (персона чата) + выбранные
  const meetingCount = selected.length + (chatPersona ? 1 : 0);

  // Подгруппы кандидатов (как в CompanionSelector): проектные → глобальные → пантеон.
  // Пантеонные материализованные персоны (с templateKey) идут отдельной группой внизу
  // вместе с ещё не подключёнными виртуальными ролями.
  const projectCandidates = allCandidates.filter(p => p.scope === 'project');
  const globalCandidates = allCandidates.filter(p => p.scope === 'global' && !p.templateKey);
  const pantheonCandidates = allCandidates.filter(p => p.templateKey);
  const hasPantheonGroup = pantheonCandidates.length > 0 || virtualPantheon.length > 0;
  const candidateGroups = (projectCandidates.length > 0 ? 1 : 0)
    + (globalCandidates.length > 0 ? 1 : 0) + (hasPantheonGroup ? 1 : 0);
  const showGroupHeaders = candidateGroups > 1;

  // Заголовок-разделитель подгруппы участников
  const groupHeader = (text: string) => (
    <div key={`h-${text}`} style={{
      padding: '8px 2px 2px', fontSize: 10.5, fontWeight: 700, color: C.textMuted,
      textTransform: 'uppercase', letterSpacing: '0.06em', fontFamily: FONT.sans,
    }}>{text}</div>
  );

  // Пункт-кандидат (обычная персона) с чекбоксом
  const candidateItem = (p: Persona) => {
    const active = selected.includes(p.id);
    const disabled = !active && selected.length >= max;
    return (
      <button
        key={p.id}
        type="button"
        onClick={() => toggle(p.id)}
        disabled={disabled}
        style={{
          display: 'flex', alignItems: 'center', gap: 10, textAlign: 'left',
          padding: '8px 10px', borderRadius: R.lg, cursor: disabled ? 'default' : 'pointer',
          border: `1.5px solid ${active ? C.accent : C.border}`,
          background: active ? C.accentLight : C.bgWhite,
          opacity: disabled ? 0.5 : 1, fontFamily: FONT.sans,
        }}
      >
        <span style={{
          flexShrink: 0, width: 18, height: 18, borderRadius: 5,
          border: `2px solid ${active ? C.accent : C.border}`,
          background: active ? C.accent : C.bgWhite,
          display: 'flex', alignItems: 'center', justifyContent: 'center',
        }}>
          {active && (
            <Check size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} color={C.onAccent} style={{ flexShrink: 0 }} />
          )}
        </span>
        <PersonaAvatar persona={p} size={30} />
        <span style={{ flex: 1, minWidth: 0 }}>
          <span style={{ display: 'block', fontSize: 13, fontWeight: 600, color: C.textHeading }}>
            {personaTitleLines(p).primary}
          </span>
          {p.description && (
            <span style={{ display: 'block', fontSize: 11.5, color: C.textMuted, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
              {p.description}
            </span>
          )}
        </span>
      </button>
    );
  };

  // Пункт виртуальной роли пантеона (материализуется при выборе)
  const virtualItem = (t: PantheonTemplate) => {
    const disabled = materializing !== null || selected.length >= max;
    return (
      <button
        key={`v-${t.key}`}
        type="button"
        onClick={() => void toggleVirtual(t)}
        disabled={disabled}
        style={{
          display: 'flex', alignItems: 'center', gap: 10, textAlign: 'left',
          padding: '8px 10px', borderRadius: R.lg, cursor: disabled ? 'default' : 'pointer',
          border: `1.5px solid ${C.border}`, background: C.bgWhite,
          opacity: disabled && materializing !== t.key ? 0.5 : 1, fontFamily: FONT.sans,
        }}
      >
        <span style={{
          flexShrink: 0, width: 18, height: 18, borderRadius: 5,
          border: `2px solid ${C.border}`, background: C.bgWhite,
        }} />
        <span style={{
          width: 30, height: 30, borderRadius: R.full, flexShrink: 0,
          background: agentDotColor(t.color), color: '#fff',
          display: 'flex', alignItems: 'center', justifyContent: 'center', fontSize: 13, fontWeight: 700,
        }}>{t.role.slice(0, 1)}</span>
        <span style={{ flex: 1, minWidth: 0 }}>
          <span style={{ display: 'block', fontSize: 13, fontWeight: 600, color: C.textHeading }}>{t.role}</span>
          <span style={{ display: 'block', fontSize: 11.5, color: C.textMuted, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
            {materializing === t.key ? 'Подключаю…' : t.description}
          </span>
        </span>
      </button>
    );
  };

  const submit = async () => {
    if (!canSend || !sessionId) {
      // Обсуждение работает и без sessionId (обычный send), остальное требует чат
      if (mode !== 'discuss') return;
    }
    const picked = allCandidates.filter(p => selected.includes(p.id));

    if (mode === 'pipeline' && sessionId) {
      try {
        setStarting(true);
        await api.chats.startPipeline(sessionId, question.trim(), executorKey);
        onClose();
      } catch (e) {
        showToast('Конвейер', e instanceof Error ? e.message : 'Не удалось запустить конвейер', 'info');
      } finally {
        setStarting(false);
      }
      return;
    }

    if (mode === 'meeting' && sessionId) {
      try {
        setStarting(true);
        const ids = [...(chatPersona ? [chatPersona.id] : []), ...picked.map(p => p.id)];
        await api.chats.startMeeting(sessionId, question.trim(), ids);
        onClose();
      } catch (e) {
        showToast('Совещание', e instanceof Error ? e.message : 'Не удалось запустить совещание', 'info');
      } finally {
        setStarting(false);
      }
      return;
    }

    // Обвязка — язык cross-examination из hyperplan OmO: атака по существу,
    // «УСТОЯЛ — причина» для сильных тезисов, дистилляция только обоснованного
    const mentions = picked.map(p => `@${p.handle}`).join(' и ');
    const text =
      `Обсуди со мной и командой вопрос: ${question.trim()}\n\n` +
      `Спроси мнение ${mentions} через persona_ask (вопрос формулируй самодостаточно, ` +
      `с нужным контекстом). Собери позиции. При разногласиях устрой один раунд перекрёстной ` +
      `проверки: слабый тезис атакуй конкретным контраргументом и перешли автору на защиту, ` +
      `сильный отмечай «УСТОЯЛ — причина». Дистиллируй итог, оставив только обоснованное: ` +
      `к чему пришли, что осталось спорным (с аргументами сторон). Заверши своим взвешенным выводом.`;
    onSend(text);
    onClose();
  };

  const modeLabels: Record<DiscussMode, { title: string; desc: string }> = {
    discuss: { title: 'Обсуждение', desc: 'Ведущая опрашивает участников и сводит итог. Быстро.' },
    meeting: { title: 'Совещание', desc: 'Независимые позиции + перекрёстная критика. Глубже, но дольше.' },
    pipeline: { title: 'Конвейер', desc: 'Анализ → план → ревью → авто-исполнение. Роли пантеона.' },
  };

  const modeCard = (m: DiscussMode) => {
    const active = mode === m;
    const { title, desc } = modeLabels[m];
    return (
      <button
        key={m}
        type="button"
        onClick={() => { setMode(m); if (m !== 'meeting') setSelected(prev => prev.slice(0, 2)); }}
        style={{
          flex: 1, textAlign: 'left', padding: '8px 10px', borderRadius: R.lg,
          border: `1.5px solid ${active ? C.accent : C.border}`,
          background: active ? C.accentLight : C.bgWhite, cursor: 'pointer', fontFamily: FONT.sans,
        }}
      >
        <span style={{ display: 'block', fontSize: 12.5, fontWeight: 700, color: active ? C.accent : C.textHeading }}>
          {title}
        </span>
        <span style={{ display: 'block', fontSize: 11, color: C.textMuted, marginTop: 2, lineHeight: 1.35 }}>
          {desc}
        </span>
      </button>
    );
  };

  const subtitle = mode === 'pipeline'
    ? 'Задача пройдёт эстафету ролей пантеона: Аналитик, Планировщик, Ревьюер — и уйдёт исполнителю'
    : mode === 'meeting'
      ? 'Участники независимо выскажутся, раскритикуют позиции друг друга, ведущий сведёт итог'
      : 'Выбери до двух участников — ведущий соберёт их мнения и сведёт итог';

  const confirmLabel = mode === 'pipeline' ? 'Запустить конвейер'
    : mode === 'meeting' ? 'Созвать совещание' : 'Начать обсуждение';

  return (
    <Modal width={460} title="Обсудить с командой" subtitle={subtitle} onClose={onClose}
      footer={<ModalActions
        confirmLabel={confirmLabel}
        onConfirm={submit} confirmDisabled={!canSend} loading={starting} onCancel={onClose} />}>
      <div style={{ display: 'flex', flexDirection: 'column', gap: 14 }}>
        {/* Переключатель режима — когда доступно больше одного */}
        {availableModes.length > 1 && (
          <div style={{ display: 'flex', gap: 8 }}>
            {availableModes.map(modeCard)}
          </div>
        )}

        {/* Конвейер: выбор роли-исполнителя финальной фазы */}
        {mode === 'pipeline' ? (
          <div>
            <div style={{ fontSize: 12.5, color: C.textSecondary, fontFamily: FONT.sans, marginBottom: 6 }}>
              Кто исполнит одобренный план
            </div>
            <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
              {EXECUTORS.map(ex => {
                const active = executorKey === ex.key;
                return (
                  <button
                    key={ex.key}
                    type="button"
                    onClick={() => setExecutorKey(ex.key)}
                    style={{
                      display: 'flex', alignItems: 'center', gap: 10, textAlign: 'left',
                      padding: '8px 10px', borderRadius: R.lg, cursor: 'pointer',
                      border: `1.5px solid ${active ? C.accent : C.border}`,
                      background: active ? C.accentLight : C.bgWhite, fontFamily: FONT.sans,
                    }}
                  >
                    <span style={{
                      flexShrink: 0, width: 18, height: 18, borderRadius: R.full,
                      border: `2px solid ${active ? C.accent : C.border}`,
                      background: active ? C.accent : C.bgWhite,
                    }} />
                    <span style={{ flex: 1, minWidth: 0 }}>
                      <span style={{ display: 'block', fontSize: 13, fontWeight: 600, color: C.textHeading }}>{ex.label}</span>
                      <span style={{ display: 'block', fontSize: 11.5, color: C.textMuted }}>{ex.desc}</span>
                    </span>
                  </button>
                );
              })}
            </div>
            <div style={{ fontSize: 11, color: C.textMuted, fontFamily: FONT.sans, marginTop: 8, lineHeight: 1.4 }}>
              Роли пантеона (Аналитик, Планировщик, Ревьюер, исполнитель) подключатся автоматически.
              После одобрения плана включится цикл «до готово».
            </div>
          </div>
        ) : (
          <>
            {/* Ведущий — персона этого чата: собирает мнения и сводит итог */}
            {chatPersona && (
              <div style={{ display: 'flex', alignItems: 'center', gap: 10, padding: '8px 10px',
                borderRadius: R.lg, background: C.bgPanel, fontFamily: FONT.sans }}>
                <PersonaAvatar persona={chatPersona} size={30} />
                <span style={{ flex: 1, minWidth: 0 }}>
                  <span style={{ display: 'block', fontSize: 13, fontWeight: 600, color: C.textHeading }}>
                    {personaTitleLines(chatPersona).primary}
                  </span>
                  <span style={{ display: 'block', fontSize: 11.5, color: C.textMuted }}>
                    {mode === 'meeting' ? 'Ведущий — выскажется и сведёт итог' : 'Ведущий — опросит участников и сводит итог'}
                  </span>
                </span>
                <span style={{
                  flexShrink: 0, fontSize: 10, fontWeight: 700, letterSpacing: 0.3, textTransform: 'uppercase',
                  padding: '2px 8px', borderRadius: R.pill, background: C.accentLight, color: C.accent,
                }}>
                  ведущий
                </span>
              </div>
            )}

            <div>
              <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'baseline', marginBottom: 6 }}>
                <span style={{ fontSize: 12.5, color: C.textSecondary, fontFamily: FONT.sans }}>Кого спросить</span>
                <span style={{ fontSize: 11, color: C.textMuted, fontFamily: FONT.sans }}>выбрано {selected.length} из {max}</span>
              </div>
              {/* Список участников подгруппами (проектные → глобальные → пантеон);
                  ограничен по высоте со скроллом, чтобы не распирать диалог */}
              <div style={{ display: 'flex', flexDirection: 'column', gap: 6, maxHeight: 340, overflowY: 'auto' }}>
                {showGroupHeaders && projectCandidates.length > 0 && groupHeader('Команда проекта')}
                {projectCandidates.map(candidateItem)}
                {showGroupHeaders && globalCandidates.length > 0 && groupHeader('Глобальные')}
                {globalCandidates.map(candidateItem)}
                {hasPantheonGroup && showGroupHeaders && groupHeader('Пантеон OmO')}
                {pantheonCandidates.map(candidateItem)}
                {virtualPantheon.map(virtualItem)}
              </div>
              {selected.length === 0 && (
                <div style={{ fontSize: 11, color: C.textMuted, fontFamily: FONT.sans, marginTop: 6 }}>
                  Отметь хотя бы одного участника — без этого обсуждение не начать.
                </div>
              )}
            </div>
          </>
        )}

        <div>
          <div style={{ fontSize: 12.5, color: C.textSecondary, fontFamily: FONT.sans, marginBottom: 6 }}>
            {mode === 'pipeline' ? 'Задача для конвейера' : 'Вопрос для обсуждения'}
          </div>
          <TextArea value={question} onChange={setQuestion} minHeight={72} autoGrow
            placeholder={mode === 'pipeline'
              ? 'Например: сделать экспорт отчётов в PDF и Excel'
              : 'Например: как лучше организовать онбординг новых пользователей?'} />
          {mode === 'meeting' && meetingCount >= 2 && (
            <div style={{ fontSize: 11, color: C.textMuted, fontFamily: FONT.sans, marginTop: 6, lineHeight: 1.4 }}>
              Участников: {meetingCount} (ведущий — {chatPersona ? personaTitleLines(chatPersona).primary : 'персона чата'}).
              Стоимость ≈ {2 * meetingCount + 1} вызовов модели — заметно дольше обычного ответа.
            </div>
          )}
        </div>
      </div>
    </Modal>
  );
}
