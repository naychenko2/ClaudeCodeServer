import { useContext, useEffect, useRef, useState } from 'react';
import { AlertTriangle, Check, CircleHelp, Clock, ListPlus, MessageCircleQuestion, Pause } from 'lucide-react';
import type { ChatItem, TeamEscalationAction, TeamEscalationKind } from '../../types';
import { C, FS, FONT, R, SHADOW, SP } from '../../lib/design';
import {
  teamEscalationTone, teamEscalationNeedsComment, teamEscalationInformational,
  teamEscalationDetailsMarkdown,
  TEAM_ESCALATION_REPLY_PLACEHOLDER, TEAM_ESCALATION_REPLY_HINT_DECISION,
} from '../../lib/teamImplement';
import { MarkdownContent } from './MarkdownContent';
import { VoiceMicButton } from './VoiceMicButton';
import { ensurePersonasLoaded, getPersonaById, usePersonasVersion } from '../../lib/personas';
import { PersonaAvatar } from '../../features/personas/PersonaAvatar';
import { Button } from '../ui/Button';
import { ConfirmDialog } from '../ui/ConfirmDialog';
import { TeamEscalationContext } from './contexts';

// Иконка триггера: проблемы — треугольник, вопрос человеку — «?», зависание — часы,
// пауза по команде — знак паузы, гейт волны — галочка (волна закрылась штатно),
// добавочная волна — список с плюсом (в работу ушли новые под-задачи),
// тупик в волне (Э8) — вопрос в облаке (перекликается с ASK-карточкой интервью)
function KindIcon({ kind, size = 14 }: { kind: TeamEscalationKind; size?: number }) {
  switch (kind) {
    case 'productDecision': return <CircleHelp size={size} strokeWidth={2.2} />;
    case 'needsClarification': return <MessageCircleQuestion size={size} strokeWidth={2.2} />;
    case 'waveStalled': return <Clock size={size} strokeWidth={2.2} />;
    case 'stopped': return <Pause size={size} strokeWidth={2.2} fill="currentColor" />;
    case 'waveGate': return <Check size={size} strokeWidth={2.4} />;
    case 'waveAdded': return <ListPlus size={size} strokeWidth={2.2} />;
    default: return <AlertTriangle size={size} strokeWidth={2.2} />;
  }
}

// Кнопка решения: первая — главное действие, вторая — ghostFilled, дальше — ghost.
// Порядок кнопок задаёт бэкенд (TeamEscalationActions.For), он же порядок таблицы плана.
// У информационной карточки главного действия нет вовсе: работа идёт, «Остановить» —
// это «передумать», и оранжевой кнопкой на всю ширину она звала бы нажать
function actionVariant(index: number, informational: boolean): 'primary' | 'ghostFilled' | 'ghost' {
  if (informational) return index === 0 ? 'ghostFilled' : 'ghost';
  if (index === 0) return 'primary';
  if (index === 1) return 'ghostFilled';
  return 'ghost';
}

// Многострочный details — markdown (состав под-задач, выделения, код): рисуем им же,
// иначе в ленте видны ** и маркеры. Нижний отступ последнего абзаца гасим, чтобы
// не разъехался паддинг карточки
function Details({ text }: { text: string }) {
  const md = teamEscalationDetailsMarkdown(text);
  if (!md) return null;
  return (
    <div style={{ fontSize: FS.sm, color: C.textSecondary, marginBottom: -8, minWidth: 0, wordBreak: 'break-word' }}>
      <MarkdownContent text={md} />
    </div>
  );
}

// Поле ответа: у блокера раскрывается кнопкой «Ответить», у продуктовой развилки
// показано сразу — там кнопки координатора не заменяют своего варианта
function ReplyBox({ hint, onSend, onCancel, sendLabel = 'Отправить' }: {
  hint?: string;
  onSend: (text: string) => void;
  onCancel?: () => void;
  sendLabel?: string;
}) {
  const [text, setText] = useState('');
  const textRef = useRef<HTMLTextAreaElement>(null);
  // Идёт запись голоса — поле замещается рядом индикации (ряд рисует VoiceMicButton)
  const [recording, setRecording] = useState(false);
  return (
    <div style={{
      borderTop: `1px dashed ${C.divider}`, paddingTop: SP.sm,
      display: 'flex', flexDirection: 'column', gap: SP.sm,
    }}>
      {hint && <div style={{ fontSize: FS.sm, color: C.textSecondary, lineHeight: 1.45 }}>{hint}</div>}
      {/* Запись голоса: поле прячется (но остаётся в DOM — по ref в него приезжает
          распознанное), на его месте — ряд индикации от VoiceMicButton */}
      <div style={{ position: 'relative' }}>
        <textarea
          autoComplete="off"
          value={text}
          onChange={e => setText(e.target.value)}
          autoFocus={!!onCancel}
          rows={2}
          ref={textRef}
          placeholder={TEAM_ESCALATION_REPLY_PLACEHOLDER}
          style={{
            display: recording ? 'none' : undefined,
            width: '100%', boxSizing: 'border-box', borderRadius: R.lg,
            border: `1px solid ${C.border}`, background: C.bgWhite, padding: '8px 36px 8px 10px',
            fontSize: FS.base, color: C.textHeading, fontFamily: FONT.sans, resize: 'none', outline: 'none',
          }}
        />
        <VoiceMicButton inputRef={textRef} variant="suffix" recordingRow onListeningChange={setRecording} />
      </div>
      <div style={{ display: 'flex', gap: SP.sm }}>
        <Button size="sm" leftIcon={<Check size={14} strokeWidth={2.6} />}
          disabled={!text.trim()} onClick={() => onSend(text.trim())} style={{ flex: 1 }}>
          {sendLabel}
        </Button>
        {onCancel && (
          <Button size="sm" variant="ghost" onClick={onCancel}>Назад</Button>
        )}
      </div>
    </div>
  );
}

// Карточка остановки режима «Командная реализация» (Э4): один шаблон на все виды.
// Молчаливых пауз в автономном режиме не бывает — любая остановка приходит сюда
// с причиной и кнопками (макет docs/mockups/team-implement-mode.html, секция 4).
// Ось задаётся триггером: warning — проблема ждёт человека, success — гейт волны,
// muted — пауза по его же команде, work — добавочная волна уже в работе (Э5, карточка
// информационная). Решение уходит координатору ходом, карточка гаснет.
export function TeamEscalationView({ item, online }: {
  item: Extract<ChatItem, { kind: 'team_escalation' }>;
  online: boolean;
}) {
  const ctx = useContext(TeamEscalationContext);
  const [replying, setReplying] = useState<TeamEscalationAction | null>(null);
  // Карточка «Снять задачу» (drop) необратима: клик открывает ConfirmDialog, и
  // пока он открыт, кнопка не реагирует на повторные клики. dropAction — отдельный
  // стейт, чтобы не путать с replying (поле ответа) и не ломать параллельный сценарий,
  // когда человек одновременно и отвечает, и передумывает снимать задачу
  const [dropAction, setDropAction] = useState<TeamEscalationAction | null>(null);
  // В не-персон-чате стор персон мог быть не загружен — резолвим автора карточки (Э8)
  usePersonasVersion();
  useEffect(() => { void ensurePersonasLoaded(); }, []);

  const esc = item.escalation;
  const tone = teamEscalationTone(esc.kind);
  const informational = teamEscalationInformational(esc.kind);
  const canAct = online && !!ctx && !esc.resolved;

  // Автор карточки (Э8): координатор ИЗ КАРТОЧКИ — фиксируется в момент публикации,
  // авторство истории не меняется при смене координатора. null — у штаба нет персоны,
  // шапка остаётся прежним обезличенным вариантом (иконка триггера на квадрате)
  const author = esc.personaId ? getPersonaById(esc.personaId) : undefined;

  const accent = tone === 'success' ? C.success
    : tone === 'muted' ? C.textMuted
      : tone === 'work' ? C.accent
        : C.warning;
  const iconStyle = tone === 'success'
    ? { background: C.successBg, color: C.successText }
    : tone === 'muted'
      ? { background: C.bgSelected, color: C.textSecondary }
      : tone === 'work'
        ? { background: C.accentLight, color: C.accent }
        : { background: C.warningBg, color: C.warningText };

  const respond = (actionId?: string, comment?: string) => {
    ctx?.onRespond(esc.id, actionId, comment);
    setReplying(null);
  };

  // === Решено: карточка гаснет, но остаётся в ленте с отметкой выбранного действия ===
  // chosenActionId === "resolvedByStaff" → снято штабом (resolutionNote — пояснение);
  // "message" → человек ответил в чат обычным сообщением; остальное — подпись нажатой
  // кнопки (как раньше). Три ветки вместо одной: раньше единая «Решение: <label>»
  // для снятого штабом и для сообщения звучала одинаково, а исход — разный
  if (esc.resolved) {
    let outcome: string;
    if (esc.chosenActionId === 'resolvedByStaff') {
      const note = (esc.resolutionNote ?? '').trim();
      outcome = note ? `Снят штабом: ${note}` : 'Снят штабом';
    } else if (esc.chosenActionId === 'message') {
      outcome = 'Ответ сообщением';
    } else {
      const chosen = esc.actions.find(a => a.id === esc.chosenActionId)?.label;
      outcome = chosen ? `Решение: ${chosen}` : 'Ответ отправлен координатору';
    }
    return (
      <div style={{
        border: `1px solid ${C.border}`, borderLeft: `3px solid ${C.textMuted}`,
        borderRadius: R.xl, padding: '11px 14px', background: C.bgWhite,
        display: 'flex', flexDirection: 'column', gap: 6,
      }}>
        <div style={{ display: 'flex', alignItems: 'flex-start', gap: 9 }}>
          {author ? (
            <PersonaAvatar persona={author} size={28} />
          ) : (
            <span style={{
              width: 28, height: 28, borderRadius: R.md, flexShrink: 0,
              display: 'flex', alignItems: 'center', justifyContent: 'center',
              background: C.bgSelected, color: C.textMuted,
            }}>
              <KindIcon kind={esc.kind} size={14} />
            </span>
          )}
          <div style={{ minWidth: 0 }}>
            {author && (
              <div style={{ fontSize: FS.xs, color: C.textMuted, marginBottom: 1 }}>
                {author.name} · координатор
              </div>
            )}
            <div style={{ fontSize: FS.base, fontWeight: 600, color: C.textSecondary, lineHeight: 1.3 }}>
              {esc.title}
            </div>
            <div style={{ fontSize: FS.sm, color: C.textMuted, lineHeight: 1.45, marginTop: 2 }}>
              {outcome}
            </div>
          </div>
        </div>
      </div>
    );
  }

  // Пока координатор ведёт ход-реакции по карточке блокера — карточка показывает
  // «Координатор разбирается». Флаг приходит из TeamEscalationChatContext (ChatPanel
  // уже рассчитал его через isWaiting && открытая по стадии), здесь просто рисуем.
  // Спокойный тон ниже полосы с тоном карточки — те же токены, тот же ритм
  const coordinatorHint = ctx?.coordinatorTurnActive === true;

  // === Ждёт решения ===
  return (
    <div style={{
      border: `1px solid ${C.border}`, borderLeft: `4px solid ${accent}`,
      borderRadius: R.xl, padding: '12px 14px', background: C.bgCard, boxShadow: SHADOW.card,
      display: 'flex', flexDirection: 'column', gap: SP.sm,
    }}>
      <div style={{ display: 'flex', alignItems: 'flex-start', gap: 9 }}>
        {author ? (
          <PersonaAvatar persona={author} size={28} />
        ) : (
          <span style={{
            width: 28, height: 28, borderRadius: R.md, flexShrink: 0,
            display: 'flex', alignItems: 'center', justifyContent: 'center', ...iconStyle,
          }}>
            <KindIcon kind={esc.kind} />
          </span>
        )}
        <div style={{ flex: 1, minWidth: 0 }}>
          {author && (
            <div style={{ fontSize: FS.xs, color: C.textMuted, marginBottom: 1 }}>
              {author.name} · координатор
            </div>
          )}
          <div style={{ fontSize: FS.base, fontWeight: 700, color: C.textHeading, lineHeight: 1.3 }}>
            {esc.title}
          </div>
        </div>
        {/* Kind-иконка уходит на правый край, когда шапка занята аватаром персоны —
            цветовая ось (borderLeft + иконка) по-прежнему различает 8 видов остановки */}
        {author && (
          <span style={{ flexShrink: 0, marginTop: 1, color: accent }}>
            <KindIcon kind={esc.kind} size={16} />
          </span>
        )}
      </div>

      {esc.details && <Details text={esc.details} />}

      {/* Полоска «Координатор разбирается»: рисуется только поверх открытой и живой карточки.
          Это сразу за details, чтобы было видно до кнопок — флаг не навязывает паузу,
          он информирует о ходе, который уже идёт. Тон — спокойный (muted), чтобы не
          двоить предупреждение «это ждёт решения», которое уже несёт borderLeft */}
      {coordinatorHint && (
        <div style={{
          fontSize: FS.sm, color: C.textMuted,
          padding: '6px 10px', borderRadius: R.md,
          background: C.bgSelected,
        }}>
          Координатор разбирается
        </div>
      )}

      {!online ? (
        <div style={{ fontSize: FS.sm, color: C.textMuted }}>Недоступно офлайн</div>
      ) : replying ? (
        <ReplyBox
          onSend={text => respond(replying.id, text)}
          onCancel={() => setReplying(null)}
        />
      ) : (
        <>
          {esc.actions.length > 0 && (
            <div style={{ display: 'flex', gap: SP.sm, flexWrap: 'wrap' }}>
              {esc.actions.map((a, i) => (
                <Button
                  key={a.id}
                  size="sm"
                  variant={actionVariant(i, informational)}
                  disabled={!canAct}
                  onClick={() => {
                    // «Ответить» у блокера не решает вопрос сам — раскрываем поле
                    if (a.id === 'answer') setReplying(a);
                    // «Снять задачу» необратимо: открываем подтверждение. Другие
                    // кнопки уходят координатору сразу, как раньше
                    else if (a.id === 'drop') setDropAction(a);
                    else respond(a.id);
                  }}
                  style={i === 0 && !informational ? { flex: 1, minWidth: 130 } : undefined}
                >
                  {/* Снять задачу — единственный id, чью подпись меняем на бэк-нейтральную.
                      Метка приходит с бэкенда — сейчас у нас в этом месте label автора,
                      но для drop она будет «Снять задачу» либо ещё короче. Подменяем
                      здесь, чтобы не зависеть от конкретного label на разных стадиях */}
                  {a.id === 'drop' ? 'Снять задачу у исполнителя' : a.label}
                </Button>
              ))}
            </div>
          )}
          {/* Продуктовая развилка: варианты формулирует координатор, но своё решение
              человек может написать словами — поле открыто сразу, без лишнего клика */}
          {teamEscalationNeedsComment(esc.kind) && esc.kind !== 'blocker' && (
            <ReplyBox
              hint={esc.actions.length > 0 ? TEAM_ESCALATION_REPLY_HINT_DECISION : undefined}
              onSend={text => respond(undefined, text)}
            />
          )}
        </>
      )}

      {/* Подтверждение для drop: одна точка — ConfirmDialog, не самодельный div (требование
          гайда дизайн-системы). Текст последствия дословно из постановки: после подтверждения
          задача исполнителя закрывается необратимо, исполнитель получит отбой. Снимаем
          dialog через onCancel, потому что сам onConfirm тоже ходит в ctx.onRespond и
          карточка погаснет сразу после ответа сервера — закрывать dialog уже нечего */}
      {dropAction && (
        <ConfirmDialog
          title="Снять задачу у исполнителя?"
          subtitle={`Задача «${esc.title}» будет закрыта как снятая, исполнитель получит отбой`}
          confirmLabel="Снять задачу"
          confirmVariant="danger"
          onConfirm={() => { respond(dropAction.id); setDropAction(null); }}
          onCancel={() => setDropAction(null)}
        />
      )}
    </div>
  );
}
