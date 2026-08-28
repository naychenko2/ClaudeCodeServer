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
import { Button } from '../ui/Button';
import { IconButton } from '../ui/IconButton';
import { InlineSegmented } from '../ui/InlineSegmented';

// Иконка режима «План» — прямоугольник с линиями (как ModeIcon plan в Composer)
function PlanIcon({ size = 13, color = 'currentColor', strokeWidth = 2 }: { size?: number; color?: string; strokeWidth?: number }) {
  return <ClipboardList size={size} color={color} strokeWidth={strokeWidth} style={{ flexShrink: 0 }} />;
}

// Свёрнутый блок исходного плана (disclosure) — для решённых состояний карточки
function CollapsedPlanBody({ plan }: { plan: string }) {
  const [open, setOpen] = useState(false);
  return (
    <div style={{ marginTop: SP.xs }}>
      <Button
        variant="ghost"
        size="xs"
        onClick={() => setOpen(o => !o)}
        leftIcon={
          <span style={{
            display: 'inline-block',
            transform: open ? 'rotate(180deg)' : 'rotate(0deg)',
            transition: 'transform 0.2s',
            fontSize: 12, lineHeight: 1,
          }}>▾</span>
        }
      >
        {open ? 'Скрыть план' : 'Показать план'}
      </Button>
      {open && (
        <div style={{
          marginTop: SP.xs, background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.lg,
          padding: '10px 12px', maxHeight: 320, overflow: 'auto', fontSize: FS.sm, color: C.textHeading, wordBreak: 'break-word',
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
  return (
    <span style={{ display: 'inline-flex', alignItems: 'center', gap: SP.xxs, marginLeft: 'auto', flexShrink: 0 }}>
      {savedId && (
        <Button variant="ghost" size="xs" onClick={() => openNoteById(savedId)}
          style={{ color: C.successText, fontSize: FS.xs }}>
          Открыть
        </Button>
      )}
      <IconButton
        size="xs"
        tone="muted"
        ariaLabel={savedId ? 'Сохранено в заметки' : 'Сохранить план в заметку'}
        disabled={busy}
        onClick={save}
      >
        {savedId
          ? <Check size={14} color={C.success} strokeWidth={3} style={{ flexShrink: 0 }} />
          : <IconNotes size={14} />}
      </IconButton>
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
      // 204 — карту собрать не вышло: возвращаем вид на текст, плашка с тем же
      // сообщением покажется над телом (см. ниже). Раньше карточка оставалась
      // в режиме «Схемой» и показывала пустоту.
      if (m === null) setSchemeView('text');
    } catch (e) {
      const err = e as Error & { status?: number };
      setSchemeError(err.message || 'Не удалось собрать схему');
      setSchemeStatus('failed');
      // Исключение из POST /api/plans/build-map: тоже уходим на текст.
      setSchemeView('text');
    }
  }
  function handleSchemeViewChange(v: 'text' | 'scheme') {
    setSchemeView(v);
    // После отказа человек мог сам вернуться в «Схемой». Сбрасываем
    // статус в idle, чтобы тело показало «Нажмите собрать», а не плашку.
    if (v === 'scheme' && schemeStatus === 'failed') {
      setSchemeStatus('idle');
      setSchemeError(null);
    }
  }
  function retryScheme() {
    setSchemeView('scheme');
    void buildScheme();
  }
  // fade-оверлей снизу появляется только если контент плана не помещается в maxHeight.
  // В deps — schemeView: при переключении «Текстом ↔ Схемой» контейнер ref тот же,
  // но контент и его высота другие.
  const [overflowing, setOverflowing] = useState(false);
  useEffect(() => {
    const el = planBodyRef.current;
    if (!el) return;
    setOverflowing(el.scrollHeight - el.clientHeight > 8);
  }, [plan, rejecting, schemeView]);

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
          <div style={{ marginTop: SP.xs, paddingTop: SP.xs, borderTop: `1px solid ${C.success}`, fontSize: FS.sm, color: C.textSecondary }}>
            <div style={{ marginBottom: SP.xs }}>Чат остаётся в режиме «План» — следующие задачи тоже будут согласованы. Выйти и выполнять в:</div>
            <div style={{ display: 'flex', gap: SP.xs }}>
              {(['acceptEdits', 'auto'] as Mode[]).map(m => (
                <Button
                  key={m}
                  variant="ghostFilled"
                  size="xs"
                  onClick={() => onSwitchMode(m)}
                  leftIcon={<span style={{ display: 'flex', color: C.accent }}><ModeIcon mode={m} /></span>}
                >
                  {MODE_META[m].label}
                </Button>
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
            display: 'flex', alignItems: 'center', gap: SP.xs, marginBottom: SP.xs, flexWrap: 'wrap',
          }}>
            <InlineSegmented
              value={schemeView}
              onChange={handleSchemeViewChange}
              options={[
                { value: 'text', label: 'Текстом', icon: <FileText size={12} />,
                  tone: { bg: C.plan, fg: C.onAccent } },
                { value: 'scheme', label: 'Схемой', icon: <Network size={12} />,
                  tone: { bg: C.plan, fg: C.onAccent } },
              ]}
            />
            {schemeView === 'scheme' && schemeStatus !== 'ready' && (
              <Button
                variant="ghostFilled"
                size="sm"
                loading={schemeStatus === 'building'}
                onClick={buildScheme}
                leftIcon={schemeStatus === 'building'
                  ? <Loader2 size={12} />
                  : <Network size={12} />}
              >
                {schemeStatus === 'building' ? 'Собираю схему…' : 'Собрать схему'}
              </Button>
            )}
          </div>
        )}

        {schemeView === 'scheme' && visualPlanEnabled ? (
          schemeStatus === 'ready' && map ? (
            // Схема в карточке ленты — с maxHeight и fade снизу (как у текста
            // плана): иначе длинная карта растягивала бы карточку на весь чат.
            <div style={{ position: 'relative' }}>
              <div ref={planBodyRef} style={{
                background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.lg,
                padding: '12px 14px', maxHeight: 360, overflow: 'auto',
                fontSize: FS.md, color: C.textHeading, wordBreak: 'break-word',
              }}>
                {/* Исходный план нужен PlanScheme: useHeadings берёт заголовки
                    из реального DOM, и без него резолв блоков возвращает пустой
                    список (карта вырождается в жанр/фразу/числа). Скрываем
                    position:absolute+1×1+opacity:0 — узлы остаются в DOM и
                    доступны querySelectorAll, но не ломают раскладку карточки
                    (visibility:hidden сохранил бы высоту и сдвинул схему).
                    aria-hidden снимает со скринридеров: контент уже виден
                    через схему. */}
                <div aria-hidden="true" style={{
                  position: 'absolute', top: 0, left: 0, width: 1, height: 1,
                  opacity: 0, overflow: 'hidden', pointerEvents: 'none',
                }}>
                  <MarkdownContent text={plan || '_(пустой план)_'} />
                </div>
                <PlanScheme map={map} planText={plan} contentRef={planBodyRef} />
              </div>
              {overflowing && (
                <div style={{
                  position: 'absolute', left: 1, right: 1, bottom: 1, height: 40, borderRadius: `0 0 ${R.lg}px ${R.lg}px`,
                  background: `linear-gradient(to bottom, transparent, ${C.bgCard})`,
                  pointerEvents: 'none',
                }} />
              )}
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
            {/* При отказе сборки — плашка над текстом, а не вместо него.
                Вид уже на «Текстом» (см. buildScheme/handleSchemeViewChange),
                человек видит и сообщение, и сам план — расхождения нет. */}
            {visualPlanEnabled && schemeStatus === 'failed' && (
              <div style={{
                marginBottom: SP.xs,
                background: C.warningBg, border: `1px solid ${C.border}`, borderRadius: R.lg,
                padding: '10px 12px', display: 'flex', alignItems: 'flex-start', gap: SP.sm,
                fontSize: FS.sm, color: C.textHeading, fontFamily: FONT.sans,
              }}>
                <AlertCircle size={14} style={{ color: C.textMuted, flexShrink: 0, marginTop: 2 }} />
                <div>
                  <div style={{ fontWeight: 600 }}>
                    {schemeError || 'Схему собрать не удалось — план открыт текстом, замечания работают.'}
                  </div>
                  <Button variant="ghostFilled" size="xs" onClick={retryScheme} style={{ marginTop: SP.xs }}>
                    Попробовать снова
                  </Button>
                </div>
              </div>
            )}
            <div ref={planBodyRef} style={{
              background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.lg,
              padding: '10px 12px', maxHeight: 360, overflow: 'auto',
              fontSize: FS.md, color: C.textHeading, wordBreak: 'break-word',
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
        <div style={{ fontSize: FS.sm, color: C.textMuted }}>Недоступно офлайн</div>
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
            {/* Кнопка одобрения в режиме «есть замечания» — вторичная: пока
                пользователь не отправил их на доработку, нажатие этой кнопки
                потеряет их без предупреждения. Тон — planBorder как нейтральный
                accent плана, чтобы «не главное» читалось по обводке. */}
            <Button
              fullWidth
              variant="ghostFilled"
              size="md"
              onClick={() => onRespond(item.requestId, true)}
              style={{ color: C.planText, borderColor: C.planBorder }}
              leftIcon={<Check size={16} color={C.planText} strokeWidth={2.4} />}
            >
              Одобрить и выполнить
            </Button>
            <div style={{
              marginTop: SP.xs, fontSize: FS.sm, color: C.textMuted, textAlign: 'center', lineHeight: 1.35,
            }}>
              замечания ({remarksCount}) не отправятся
            </div>
          </div>
        ) : (
          // Primary одобрения без замечаний — основное действие карточки, ему
          // нужны focus-ring и shadow из коробки (Box variant="primary" + glow).
          // Раньше был самодельный rgba — линтер не видел, и focus-ring отсутствовал.
          <Button
            fullWidth
            variant="primary"
            size="md"
            glow
            onClick={() => onRespond(item.requestId, true)}
            leftIcon={<Check size={16} color={C.onAccent} strokeWidth={2.6} />}
          >
            Одобрить и выполнить
          </Button>
        )
      ) : rejecting ? (
        <div>
          <div style={{ fontSize: FS.sm, color: C.textSecondary, marginBottom: SP.xs }}>
            {asstName} учтёт это и предложит новый план
          </div>
          <textarea
            value={feedback}
            onChange={e => setFeedback(e.target.value)}
            autoFocus
            placeholder="Что поправить в плане? (необязательно)"
            rows={3}
            style={{
              width: '100%', boxSizing: 'border-box', borderRadius: R.lg,
              border: `1px solid ${C.border}`, background: C.bgWhite,
              padding: '8px 10px', fontSize: FS.sm, color: C.textHeading,
              fontFamily: 'inherit', resize: 'none', outline: 'none', marginBottom: SP.xs,
            }}
          />
          <div style={{ display: 'flex', gap: SP.xs }}>
            <Button
              fullWidth
              variant="primary"
              size="md"
              onClick={() => onRespond(item.requestId, false, feedback.trim() || undefined)}
            >
              Переработать план
            </Button>
            <Button
              variant="ghostFilled"
              size="md"
              onClick={() => { setRejecting(false); setFeedback(''); }}
            >
              Назад
            </Button>
          </div>
        </div>
      ) : (
        <div style={{ display: 'flex', gap: SP.xs }}>
          <Button
            fullWidth
            variant="primary"
            size="md"
            glow
            onClick={() => onRespond(item.requestId, true)}
            leftIcon={<Check size={16} color={C.onAccent} strokeWidth={2.6} />}
          >
            Одобрить и выполнить
          </Button>
          <Button
            variant="ghostAccent"
            size="md"
            onClick={() => setRejecting(true)}
            style={{ color: C.planText, borderColor: C.planBorder }}
          >
            Отклонить
          </Button>
        </div>
      )}
      {/* Слой замечаний: рендерится ВСЕГДА у pending-плана, чтобы кнопки у
          заголовков жили вне ленточной прокрутки; сам по себе компонент ничего
          не рисует, если status!='pending' или флаг выключен */}
      {visualPlanEnabled && (
        <PlanRemarks
          contentRef={planBodyRef}
          planText={plan}
          containerToken={schemeView}
          status="pending"
          onSubmit={feedback => onRespond(item.requestId, false, feedback || undefined)}
          onCountChange={setRemarksCount}
        />
      )}
    </div>
  );
}

// Сегмент-переключатель: общий InlineSegmented с тоном плана (см. PlanSection).
// Активный сегмент на C.plan, не C.accent — режим «План» живёт своей гаммой.
