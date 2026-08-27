import { useState, useEffect, useRef, useContext } from 'react';
import { ClipboardList, Check, RotateCcw, Network, FileText, AlertCircle, Loader2 } from 'lucide-react';
import type { ChatItem, PlanMap } from '../../types';
import { type Mode, MODE_META, ModeIcon } from '../../lib/modes';
import { C, FONT, R, SHADOW, SP, FS } from '../../lib/design';
import { stripRoot } from '../../lib/paths';
import { ChatProjectContext, useAssistantName } from './contexts';
import { MarkdownContent } from './MarkdownContent';
import { IconNotes } from '../../features/notes/shared';
import { saveChatNote, openNoteById } from '../../features/notes/saveToNote';
import { FLAGS, useFeature } from '../../lib/featureFlags';
import { PlanRemarks } from '../../features/plan/PlanRemarks';
import { PlanScheme } from '../plan/PlanScheme';
import { api } from '../../lib/api';

// Иконка режима «План» — прямоугольник с линиями (как ModeIcon plan в Composer)
function PlanIcon({ size = 13, color = 'currentColor', strokeWidth = 2 }: { size?: number; color?: string; strokeWidth?: number }) {
  return <ClipboardList size={size} color={color} strokeWidth={strokeWidth} style={{ flexShrink: 0 }} />;
}

// Свёрнутый блок исходного плана (disclosure) — для решённых состояний карточки
function CollapsedPlanBody({ plan }: { plan: string }) {
  const [open, setOpen] = useState(false);
  return (
    <div style={{ marginTop: 8 }}>
      <button
        onClick={() => setOpen(o => !o)}
        style={{
          display: 'inline-flex', alignItems: 'center', gap: 5, background: 'none', border: 'none',
          cursor: 'pointer', padding: 0, fontSize: 12, fontWeight: 600, color: C.textSecondary, fontFamily: 'inherit',
        }}
      >
        <span style={{ display: 'inline-block', transform: open ? 'rotate(180deg)' : 'rotate(0deg)', transition: 'transform 0.2s' }}>▾</span>
        {open ? 'Скрыть план' : 'Показать план'}
      </button>
      {open && (
        <div style={{
          marginTop: 8, background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.lg,
          padding: '10px 12px', maxHeight: 320, overflow: 'auto', fontSize: 13, color: C.textHeading, wordBreak: 'break-word',
        }}>
          <MarkdownContent text={plan || '_(пустой план)_'} />
        </div>
      )}
    </div>
  );
}

// Иконка-кнопка «В заметку» — сохранить текст плана в базу заметок
function SavePlanButton({ plan, online }: { plan: string; online: boolean }) {
  const project = useContext(ChatProjectContext);
  const [savedId, setSavedId] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  if (!online) return null;
  const save = () => {
    if (busy || savedId) return;
    setBusy(true);
    saveChatNote({ text: plan, projectId: project?.id, titlePrefix: 'План: ' })
      .then(n => { setSavedId(n.id); setTimeout(() => setSavedId(null), 6000); })
      .catch(() => {})
      .finally(() => setBusy(false));
  };
  const btn: React.CSSProperties = {
    display: 'inline-flex', alignItems: 'center', justifyContent: 'center',
    width: 24, height: 24, borderRadius: 6, border: 'none', background: 'transparent',
    color: C.textMuted, cursor: 'pointer', padding: 0, flexShrink: 0, opacity: busy ? 0.5 : 1,
  };
  return (
    <span style={{ display: 'inline-flex', alignItems: 'center', gap: 4, marginLeft: 'auto', flexShrink: 0 }}>
      {savedId && (
        <button onClick={() => openNoteById(savedId)} title="Открыть созданную заметку"
          style={{ ...btn, width: 'auto', padding: '0 6px', fontSize: 11, fontWeight: 600, color: C.successText }}>
          Открыть
        </button>
      )}
      <button onClick={save} disabled={busy} style={btn}
        title={savedId ? 'Сохранено в заметки' : 'Сохранить план в заметку'} aria-label="Сохранить план в заметку">
        {savedId
          ? <Check size={14} color={C.success} strokeWidth={3} style={{ flexShrink: 0 }} />
          : <IconNotes size={14} />}
      </button>
    </span>
  );
}

// Карточка согласования плана (ExitPlanMode в режиме «План»):
// показывает план и кнопки «Одобрить и выполнить» / «Отклонить» (с комментарием).
export function PlanReviewView({ item, online, onRespond, version, showBadge, showSwitch, onSwitchMode }: {
  item: Extract<ChatItem, { kind: 'plan_review' }>;
  online: boolean;
  onRespond: (requestId: string, approve: boolean, feedback?: string) => void;
  version?: number;
  showBadge?: boolean;
  showSwitch?: boolean;
  onSwitchMode?: (mode: Mode) => void;
}) {
  const [rejecting, setRejecting] = useState(false);
  const [feedback, setFeedback] = useState('');
  // Количество неотправленных замечаний в PlanRemarks. При > 0 акцент
  // кнопок карточки меняется: согласование становится второстепенным с
  // честной подписью про потерю замечаний (задача про молча теряемые
  // замечания — протокол одобрения комментарий не передаёт)
  const [remarksCount, setRemarksCount] = useState(0);
  const asstName = useAssistantName();
  const project = useContext(ChatProjectContext);
  // Фича «Визуальный разворот плана»: контекстные замечания к разделам.
  // Под флагом — кнопка "Отклонить" заменяется на раздел замечаний: счётчик,
  // список заметок, отправка через тот же onRespond(requestId, false, feedback)
  const visualPlanEnabled = useFeature(FLAGS.visualPlan);
  // В тексте плана пути показываем относительно корня проекта
  const plan = stripRoot(item.plan, project?.rootPath);
  const planBodyRef = useRef<HTMLDivElement>(null);

  // Состояние схемы: idle — карты нет; building — идёт POST; ready — есть карта;
  // failed — последний сборка провалилась (план остался на тексте).
  // Сборка ТОЛЬКО по кнопке (см. вики-план «Визуальный разворот плана», часть B §4):
  // не дёргаем модель на каждый mount компонента.
  const [schemeView, setSchemeView] = useState<'text' | 'scheme'>('text');
  const [map, setMap] = useState<PlanMap | null>(null);
  const [schemeStatus, setSchemeStatus] = useState<'idle' | 'building' | 'ready' | 'failed'>('idle');
  const [schemeError, setSchemeError] = useState<string | null>(null);

  // Сброс карты при смене версии/текста плана: старая карта привязана к старому тексту
  useEffect(() => {
    setMap(null);
    setSchemeStatus('idle');
    setSchemeError(null);
    setSchemeView('text');
  }, [plan]);

  async function buildScheme() {
    if (schemeStatus === 'building') return;
    setSchemeStatus('building');
    setSchemeError(null);
    try {
      const m = await api.plans.buildMap(plan);
      setMap(m);
      // m === null → 204, сервер не смог собрать карту: НЕ падаем в ошибку, остаёмся на
      // тексте с работающими замечаниями. План «Визуальный разворот» §4 и §6.
      setSchemeStatus(m === null ? 'failed' : 'ready');
    } catch (e) {
      const err = e as Error & { status?: number };
      setSchemeError(err.message || 'Не удалось собрать схему');
      setSchemeStatus('failed');
    }
  }
  // fade-оверлей снизу появляется только если контент плана не помещается в maxHeight
  const [overflowing, setOverflowing] = useState(false);
  useEffect(() => {
    const el = planBodyRef.current;
    if (!el) return;
    setOverflowing(el.scrollHeight - el.clientHeight > 8);
  }, [plan, rejecting]);

  // === Решённое состояние: одобрено → компактная шапка выполнения ===
  if (item.resolved && item.approved) {
    return (
      <div style={{
        border: `1px solid ${C.successBg}`, borderLeft: `3px solid ${C.success}`,
        borderRadius: R.xl, padding: '11px 14px', background: C.successBg,
      }}>
        <div style={{ display: 'flex', alignItems: 'center', gap: 8, fontSize: 13, fontWeight: 600, color: C.successText }}>
          <svg width="16" height="16" viewBox="0 0 16 16" fill="none"><circle cx="8" cy="8" r="8" fill={C.success} /><path d="M4.5 8.2l2.2 2.2 4.8-4.8" stroke={C.onAccent} strokeWidth="1.6" strokeLinecap="round" strokeLinejoin="round" /></svg>
          План одобрен — выполняется
          <SavePlanButton plan={plan} online={online} />
        </div>
        <CollapsedPlanBody plan={plan} />
        {/* Выход из режима «План» — только у актуального (последнего) одобренного плана.
            Предлагаем выбрать режим исполнения, как в нативном approval Claude Code. */}
        {showSwitch && onSwitchMode && (
          <div style={{ marginTop: 9, paddingTop: 9, borderTop: `1px solid ${C.success}`, fontSize: 12, color: C.textSecondary }}>
            <div style={{ marginBottom: 7 }}>Чат остаётся в режиме «План» — следующие задачи тоже будут согласованы. Выйти и выполнять в:</div>
            <div style={{ display: 'flex', gap: 7 }}>
              {(['acceptEdits', 'auto'] as Mode[]).map(m => (
                <button key={m} onClick={() => onSwitchMode(m)}
                  style={{ display: 'flex', alignItems: 'center', gap: 6, background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.md, cursor: 'pointer', fontSize: 12, fontWeight: 600, color: C.textHeading, padding: '5px 10px' }}>
                  <span style={{ display: 'flex', color: C.accent }}><ModeIcon mode={m} /></span>
                  {MODE_META[m].label}
                </button>
              ))}
            </div>
          </div>
        )}
      </div>
    );
  }

  // === Решённое состояние: отклонено → компактная строка + комментарий ===
  if (item.resolved && item.approved === false) {
    return (
      <div style={{
        border: `1px solid ${C.border}`, borderLeft: `3px solid ${C.textMuted}`,
        borderRadius: R.xl, padding: '11px 14px', background: C.bgWhite,
      }}>
        <div style={{ display: 'flex', alignItems: 'center', gap: 8, fontSize: 13, fontWeight: 600, color: C.textSecondary }}>
          <RotateCcw size={15} color={C.textMuted} strokeWidth={2} style={{ flexShrink: 0 }} />
          План{version ? ` v${version}` : ''} — отклонён
          <SavePlanButton plan={plan} online={online} />
        </div>
        {item.feedback?.trim() && (
          <div style={{ fontSize: 12, color: C.textSecondary, marginTop: 7, whiteSpace: 'pre-wrap' }}>
            Комментарий: {item.feedback}
          </div>
        )}
        <CollapsedPlanBody plan={plan} />
      </div>
    );
  }

  // === На согласовании ===
  return (
    <div style={{
      border: `1px solid ${C.planBorder}`, borderLeft: `4px solid ${C.plan}`,
      borderRadius: R.xl, padding: '14px 16px', background: C.bgCard, boxShadow: SHADOW.card,
    }}>
      <div style={{ display: 'flex', alignItems: 'flex-start', gap: 10, marginBottom: 4 }}>
        <span style={{
          width: 28, height: 28, borderRadius: R.md, background: C.plan, flexShrink: 0,
          display: 'flex', alignItems: 'center', justifyContent: 'center',
        }}>
          <PlanIcon size={15} color={C.onAccent} />
        </span>
        <div style={{ flex: 1, minWidth: 0 }}>
          <div style={{ fontFamily: FONT.serif, fontSize: 15, fontWeight: 700, color: C.textHeading, lineHeight: 1.2 }}>
            План готов
          </div>
          <div style={{ fontSize: 12, color: C.textSecondary, marginTop: 2 }}>
            {asstName} предлагает план. Файлы пока не изменялись.
          </div>
        </div>
        {showBadge && version && (
          <span style={{
            flexShrink: 0, background: C.planLight, color: C.planText, borderRadius: R.sm,
            padding: '2px 8px', fontSize: 11, fontWeight: 600, whiteSpace: 'nowrap',
          }}>
            v{version} · на согласовании
          </span>
        )}
        <SavePlanButton plan={plan} online={online} />
      </div>

      <div style={{ position: 'relative', margin: '12px 0' }}>
        {visualPlanEnabled && (
          // Переключатель «Схемой / Текстом» — сегмент над телом карточки. Сборка
          // карты ТОЛЬКО по кнопке (вики-план часть B §4): иначе любое открытие
          // плана сразу дёргало бы модель. Сюда же вынесена кнопка «Собрать
          // схему», чтобы не терялась за переключателем.
          <div style={{
            display: 'flex', alignItems: 'center', gap: 6, marginBottom: 8, flexWrap: 'wrap',
          }}>
            <SegmentedToggle
              options={[
                { value: 'text' as const, label: 'Текстом', icon: <FileText size={12} /> },
                { value: 'scheme' as const, label: 'Схемой', icon: <Network size={12} /> },
              ]}
              value={schemeView}
              onChange={setSchemeView}
            />
            {schemeView === 'scheme' && schemeStatus !== 'ready' && (
              <button onClick={buildScheme} disabled={schemeStatus === 'building'} style={{
                display: 'inline-flex', alignItems: 'center', gap: 5,
                padding: '5px 10px', borderRadius: R.md,
                border: `1px solid ${C.border}`, background: C.bgWhite,
                color: C.textHeading, cursor: schemeStatus === 'building' ? 'default' : 'pointer',
                fontFamily: FONT.sans, fontSize: FS.sm, fontWeight: 600,
                opacity: schemeStatus === 'building' ? 0.6 : 1,
              }}>
                {schemeStatus === 'building'
                  ? <Loader2 size={12} style={{ animation: 'cc-spin 1s linear infinite' }} />
                  : <Network size={12} />}
                {schemeStatus === 'building' ? 'Собираю схему…' : 'Собрать схему'}
              </button>
            )}
          </div>
        )}

        {schemeView === 'scheme' && visualPlanEnabled ? (
          schemeStatus === 'ready' && map ? (
            <div style={{
              background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.lg,
              padding: '12px 14px',
            }}>
              <PlanScheme map={map} planText={plan} contentRef={planBodyRef} />
            </div>
          ) : schemeStatus === 'failed' ? (
            // Отказ сборки: НЕ падаем в красную ошибку — план остался на тексте с
            // работающими замечаниями. Текст ровно как в плане «Визуальный разворот» §4.
            <div style={{
              background: C.warningBg, border: `1px solid ${C.border}`, borderRadius: R.lg,
              padding: '10px 12px', display: 'flex', alignItems: 'flex-start', gap: SP.sm,
              fontSize: FS.sm, color: C.textHeading, fontFamily: FONT.sans,
            }}>
              <AlertCircle size={14} style={{ color: C.textMuted, flexShrink: 0, marginTop: 2 }} />
              <div>
                <div style={{ fontWeight: 600 }}>
                  {schemeError || 'Схему собрать не удалось — план открыт текстом, замечания работают.'}
                </div>
                <button onClick={buildScheme} style={{
                  marginTop: 6, padding: '4px 10px', borderRadius: R.sm,
                  border: `1px solid ${C.border}`, background: C.bgWhite,
                  color: C.textSecondary, cursor: 'pointer',
                  fontFamily: FONT.sans, fontSize: FS.xs, fontWeight: 600,
                }}>Попробовать снова</button>
              </div>
            </div>
          ) : schemeStatus === 'building' ? (
            <div style={{
              background: C.bgInset, border: `1px dashed ${C.border}`, borderRadius: R.lg,
              padding: '14px', textAlign: 'center',
              fontSize: FS.sm, color: C.textMuted, fontFamily: FONT.sans,
              display: 'flex', alignItems: 'center', justifyContent: 'center', gap: 8,
            }}>
              <Loader2 size={14} style={{ animation: 'cc-spin 1s linear infinite' }} />
              Собираю схему…
            </div>
          ) : (
            <div style={{
              background: C.bgInset, border: `1px dashed ${C.border}`, borderRadius: R.lg,
              padding: '14px', textAlign: 'center',
              fontSize: FS.sm, color: C.textMuted, fontFamily: FONT.sans,
            }}>
              Нажмите «Собрать схему», чтобы построить разворот.
            </div>
          )
        ) : (
          <>
            <div ref={planBodyRef} style={{
              background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.lg,
              padding: '10px 12px', maxHeight: 360, overflow: 'auto',
              fontSize: 13.5, color: C.textHeading, wordBreak: 'break-word',
            }}>
              <MarkdownContent text={plan || '_(пустой план)_'} />
            </div>
            {overflowing && (
              // Градиентный fade снизу — подсказка, что план длиннее видимой области
              <div style={{
                position: 'absolute', left: 1, right: 1, bottom: 1, height: 40, borderRadius: `0 0 ${R.lg}px ${R.lg}px`,
                background: `linear-gradient(to bottom, transparent, ${C.bgCard})`,
                pointerEvents: 'none',
              }} />
            )}
          </>
        )}
      </div>

      {!online ? (
        <div style={{ fontSize: 12, color: C.textMuted }}>Недоступно офлайн</div>
      ) : visualPlanEnabled ? (
        // Под флагом visualPlan — кнопка одобрения + слой замечаний (PlanRemarks)
        // с кнопкой «Отправить на доработку (N)». Когда замечаний нет, одобрение —
        // единственный primary; когда есть — PlanRemarks рисует свою primary-кнопку
        // отправки на доработку, а одобрение становится второстепенным с честной
        // подписью про потерю (задача про молча теряемые замечания: протокол
        // ClaudeSession.RespondPlan при approve=true шлёт `{behavior: "allow"}` без
        // updatedInput, комментарий на уровне протокола отбрасывается).
        remarksCount > 0 ? (
          <div>
            <button onClick={() => onRespond(item.requestId, true)}
              style={{
                width: '100%', minHeight: 42, background: C.bgWhite, color: C.planText,
                border: `1px solid ${C.planBorder}`, borderRadius: R.lg, padding: 9,
                cursor: 'pointer', fontSize: 13.5, fontWeight: 600,
                display: 'flex', alignItems: 'center', justifyContent: 'center', gap: 7,
              }}>
              <Check size={16} color={C.planText} strokeWidth={2.4} style={{ flexShrink: 0 }} />
              Одобрить и выполнить
            </button>
            <div style={{
              marginTop: 6, fontSize: 12, color: C.textMuted, textAlign: 'center', lineHeight: 1.35,
            }}>
              замечания ({remarksCount}) не отправятся
            </div>
          </div>
        ) : (
          <button onClick={() => onRespond(item.requestId, true)}
            style={{
              width: '100%', minHeight: 42, background: C.plan, color: C.onAccent, borderRadius: R.lg,
              padding: 9, border: 'none', cursor: 'pointer', fontSize: 13.5, fontWeight: 700,
              boxShadow: '0 4px 14px rgba(108,92,176,0.30)',
              display: 'flex', alignItems: 'center', justifyContent: 'center', gap: 7,
            }}>
            <Check size={16} color={C.onAccent} strokeWidth={2.6} style={{ flexShrink: 0 }} />
            Одобрить и выполнить
          </button>
        )
      ) : rejecting ? (
        <div>
          <div style={{ fontSize: 12, color: C.textSecondary, marginBottom: 7 }}>
            {asstName} учтёт это и предложит новый план
          </div>
          <textarea
            value={feedback}
            onChange={e => setFeedback(e.target.value)}
            autoFocus
            placeholder="Что поправить в плане? (необязательно)"
            rows={3}
            style={{ width: '100%', boxSizing: 'border-box', borderRadius: R.lg, border: `1px solid ${C.border}`, background: C.bgWhite, padding: '8px 10px', fontSize: 13, color: C.textHeading, fontFamily: 'inherit', resize: 'none', outline: 'none', marginBottom: 8 }}
          />
          <div style={{ display: 'flex', gap: 8 }}>
            <button onClick={() => onRespond(item.requestId, false, feedback.trim() || undefined)}
              style={{ flex: 1, minHeight: 40, background: C.plan, color: C.onAccent, borderRadius: R.lg, padding: 9, border: 'none', cursor: 'pointer', fontSize: 13, fontWeight: 600 }}>
              Переработать план
            </button>
            <button onClick={() => { setRejecting(false); setFeedback(''); }}
              style={{ flex: 'none', minHeight: 40, background: C.bgWhite, border: `1px solid ${C.border}`, color: C.textSecondary, borderRadius: R.lg, padding: '9px 16px', cursor: 'pointer', fontSize: 13 }}>
              Назад
            </button>
          </div>
        </div>
      ) : (
        <div style={{ display: 'flex', gap: 8 }}>
          <button onClick={() => onRespond(item.requestId, true)}
            style={{
              flex: 1, minHeight: 42, background: C.plan, color: C.onAccent, borderRadius: R.lg,
              padding: 9, border: 'none', cursor: 'pointer', fontSize: 13.5, fontWeight: 700,
              boxShadow: '0 4px 14px rgba(108,92,176,0.30)',
              display: 'flex', alignItems: 'center', justifyContent: 'center', gap: 7,
            }}>
            <Check size={16} color={C.onAccent} strokeWidth={2.6} style={{ flexShrink: 0 }} />
            Одобрить и выполнить
          </button>
          <button onClick={() => setRejecting(true)}
            style={{ flex: 'none', minHeight: 42, background: 'transparent', border: `1px solid ${C.planBorder}`, color: C.planText, borderRadius: R.lg, padding: '9px 16px', cursor: 'pointer', fontSize: 13, fontWeight: 600 }}>
            Отклонить
          </button>
        </div>
      )}
      {/* Слой замечаний: рендерится ВСЕГДА у pending-плана, чтобы кнопки у
          заголовков жили вне ленточной прокрутки; сам по себе компонент ничего
          не рисует, если status!='pending' или флаг выключен */}
      {visualPlanEnabled && (
        <PlanRemarks
          contentRef={planBodyRef}
          planText={plan}
          status="pending"
          onSubmit={feedback => onRespond(item.requestId, false, feedback || undefined)}
          onCountChange={setRemarksCount}
        />
      )}
    </div>
  );
}

// Сегмент-переключатель: два варианта, активный — на plan-фоне, неактивный — нейтральный.
// Один общий компонент: «Текстом / Схемой» в PlanReviewView и в PlanSection.
function SegmentedToggle<V extends string>({ options, value, onChange }: {
  options: ReadonlyArray<{ value: V; label: string; icon?: React.ReactNode }>;
  value: V;
  onChange: (v: V) => void;
}) {
  return (
    <div style={{
      display: 'inline-flex', alignItems: 'center',
      border: `1px solid ${C.border}`, borderRadius: R.pill,
      background: C.bgInset, padding: 2,
    }}>
      {options.map(opt => {
        const active = opt.value === value;
        return (
          <button key={opt.value} onClick={() => onChange(opt.value)} style={{
            display: 'inline-flex', alignItems: 'center', gap: 4,
            padding: '4px 10px', borderRadius: R.pill,
            border: 'none',
            background: active ? C.plan : 'transparent',
            color: active ? C.onAccent : C.textSecondary,
            cursor: 'pointer',
            fontFamily: FONT.sans, fontSize: FS.xs, fontWeight: 600,
          }}>
            {opt.icon}
            {opt.label}
          </button>
        );
      })}
    </div>
  );
}
