// Модалка «Загрузить историю решений из репозитория» (ADR-004 §6, этап 4):
// читает ветку ccs/dossiers/v1 plumbing-командами и кладёт паспорта в стор с пометкой
// происхождения (origin=imported, importedAuthor в карточке). Зеркало DossierExportDialog
// по структуре, но другая семантика итогов: здесь нет «push» (ветка read-only), а пустые
// результаты делятся на «ничего нового» и «ветки нет» — последнее не ошибка, а нейтральный
// ответ (статус ветки мог устареть).
//
// Состояния по макету docs/mockups/decision-history-import-v1.html §4 и текстам
// docs/features/decision-history-import-texts.md §2.2:
//  1. confirm          — обычное подтверждение
//  2. loading          — запрос ушёл; диалог НЕ закрывается (иначе git-операция
//                        оставила бы человека без ответа)
//  3. success          — загружено N (плюс «уже было M», если M > 0)
//  4. nothing          — новых нет, ветка прочитана
//  5. noBranch         — ветки в репозитории нет (info-выноска, не danger)
//  6. error            — danger-выноска + повтор
//
// Переходы внутри одного открытого Modal, без закрытия и переоткрытия — карточка
// не моргает. Тексты — окончательные формулировки из decision-history-import-texts.md,
// вариантов на выбор нет.

import { useEffect, useState } from 'react';
import { AlertTriangle, CircleCheck, Download, Info } from 'lucide-react';
import { C, FONT, FS, MODAL_W, R, SP } from '../../lib/design';
import { ICON_STROKE } from '../../components/ui/icons';
import { Button, Modal } from '../../components/ui';
import { api } from '../../lib/api';

// Тексты по постановке — вынесены в константы, чтобы линтер случайно не «поправил».
// Форма «Загружено записей: N» выбрана нарочно: читается верно при любом числе,
// без склонений и без «запись/записи/записей» в коде.
const T = {
  title: 'Загрузить историю решений из репозитория',
  body: 'Из ветки ccs/dossiers/v1 подтянутся записи, которых у вас ещё нет. Ваши записи не изменятся: если по коммиту есть и ваша, и приехавшая — в списке будут обе.',
  successPrefix: 'Загружено записей: ',
  nothing: 'Новых записей нет — всё, что есть в ветке, уже в истории решений.',
  noBranch: 'Загружать пока нечего: выгруженной истории решений в репозитории не нашлось. Возможно, её ещё ни разу не выгружали — или выгрузил коллега, но к вам она ещё не приехала: обновите репозиторий (git fetch) и повторите.',
  error: 'Не удалось загрузить историю решений. Ничего не изменилось — попробуйте ещё раз.',
  btnImport: 'Загрузить',
  btnCancel: 'Отмена',
  btnClose: 'Закрыть',
  btnRetry: 'Повторить',
} as const;

// Ветка одна и та же во всех текстах; единый инлайн-mono-фрагмент, как у sha коммита
// в карточках DossierHistoryPanel. Копия BranchTag из DossierExportDialog — выносить
// в общий файл ради двух строк нерационально.
function BranchTag() {
  return (
    <span style={{
      fontFamily: FONT.mono, fontSize: FS.sm, background: C.bgInset,
      borderRadius: R.sm, padding: `${SP.xxs}px ${SP.xs}px`,
    }}>
      ccs/dossiers/v1
    </span>
  );
}

// Выноска с иконкой слева. Та же геометрия, что у DossierExportDialog.Notice, но
// расширена info-тоном — здесь его использует фаза noBranch (отсутствие данных,
// а не сбой). Палитра C.* — никаких сырых hex.
function Notice({ tone, icon, children }: {
  tone: 'warning' | 'danger' | 'info';
  icon: React.ReactNode;
  children: React.ReactNode;
}) {
  const bg = tone === 'warning' ? C.warningBg : tone === 'danger' ? C.dangerBg : C.infoBg;
  const fg = tone === 'warning' ? C.warningText : tone === 'danger' ? C.dangerText : C.info;
  return (
    <div style={{
      background: bg, color: fg, borderRadius: R.lg,
      padding: `${SP.sm}px ${SP.md}px`,
      display: 'flex', gap: SP.sm, alignItems: 'flex-start',
    }}>
      <span style={{ flexShrink: 0, marginTop: 1, display: 'flex' }}>{icon}</span>
      <span style={{ fontSize: FS.base, lineHeight: 1.45 }}>{children}</span>
    </div>
  );
}

type Phase = 'confirm' | 'loading' | 'success' | 'nothing' | 'noBranch' | 'error';

// added+skipped нужны и в success (для пометки «уже было»), и в nothing (для контекста,
// если бэк вернёт skipped>0 при added=0 — это сигнал, что ветка прочитана не полностью).
// В noBranch эти счётчики нулевые по построению, и в error — не показываем ничего, кроме
// выноски.
interface Props {
  open: boolean;
  onClose: () => void;
  projectId: string;
  // Колбэк «импорт что-то изменил» (success или nothing) — вызывается при нажатии
  // «Закрыть» на финальной карточке. Нужен родителю, чтобы перезагрузить список
  // и подтянуть новые origin='imported' записи. На noBranch и error не зовём —
  // записей не появилось, перезагрузка не нужна.
  onSuccess?: () => void;
}

export function DossierImportDialog({ open, onClose, projectId, onSuccess }: Props) {
  const [phase, setPhase] = useState<Phase>('confirm');
  const [added, setAdded] = useState(0);
  // Флаг «уже было» в success-фазе: показываем «Загружено записей: N. Уже было в истории: M.»
  // только если M > 0 — писать «Уже было в истории: 0» нельзя (тексты §2.2).
  const [alreadyHad, setAlreadyHad] = useState(0);

  useEffect(() => {
    if (open) {
      // eslint-disable-next-line react-hooks/set-state-in-effect -- ресет при каждом открытии модалки
      setPhase('confirm');
      setAdded(0);
      setAlreadyHad(0);
    }
  }, [open]);

  const run = async () => {
    // eslint-disable-next-line react-hooks/set-state-in-effect -- переход перед запросом, чтобы нажатая кнопка показала спиннер
    setPhase('loading');
    try {
      const res = await api.dossiers.importRun(projectId);
      if (res.status === 'noBranch') {
        setPhase('noBranch');
        return;
      }
      setAdded(res.added);
      // Вся ветка прочитана: добавлено added, остальные index.Entries.Count - added ушли в skipped.
      // «Уже было» — это пропуски по дедупу (остальные причины skipped мы не делим на клиенте,
      // и тексты §2.2 их не различают). Берём весь skipped как «уже было» — честная оценка
      // сверху, занижать смысла нет.
      setAlreadyHad(res.skipped);
      if (res.added > 0) setPhase('success');
      else setPhase('nothing');
    } catch {
      setPhase('error');
    }
  };

  const busy = phase === 'loading';
  // Закрытие разрешено в любой фазе, включая loading: запрос уже ушёл, его UI не
  // отменит, но диалог запирать нельзя (QA фиксировал «не закрывается»). При ошибке
  // catch выше переводит phase в 'error', busy сбрасывается, и закрытие работает штатно.
  const handleClose = onClose;

  return (
    <Modal
      width={MODAL_W.confirm}
      title={phase === 'success' || phase === 'nothing' || phase === 'noBranch' || phase === 'error' ? undefined : T.title}
      // closeOnBackdrop=false во время запроса — иначе клик по оверлею посреди
      // git-чтения оставил бы пользователя без ответа.
      closeOnBackdrop={!busy}
      onClose={handleClose}
    >
      {(phase === 'confirm' || phase === 'loading') && (
        <div style={{ display: 'flex', flexDirection: 'column', gap: SP.md }}>
          <p style={{
            margin: 0, fontSize: FS.md, color: C.textPrimary, lineHeight: 1.5,
          }}>
            Из ветки <BranchTag /> подтянутся записи, которых у вас ещё нет. Ваши записи не изменятся: если по коммиту есть и ваша, и приехавшая — в списке будут обе.
          </p>
        </div>
      )}

      {phase === 'success' && (
        <div style={{ display: 'flex', alignItems: 'flex-start', gap: SP.sm }}>
          <CircleCheck size={20} strokeWidth={ICON_STROKE} color={C.success} style={{ flexShrink: 0, marginTop: 1 }} />
          <p style={{ margin: 0, fontSize: FS.md, color: C.textPrimary, lineHeight: 1.5 }}>
            {T.successPrefix}{added}.
            {alreadyHad > 0 && (
              <> Уже было в истории: {alreadyHad}.</>
            )}
          </p>
        </div>
      )}

      {phase === 'nothing' && (
        <div style={{ display: 'flex', alignItems: 'flex-start', gap: SP.sm }}>
          <Info size={20} strokeWidth={ICON_STROKE} color={C.textSecondary} style={{ flexShrink: 0, marginTop: 1 }} />
          <p style={{ margin: 0, fontSize: FS.md, color: C.textPrimary, lineHeight: 1.5 }}>
            {T.nothing}
          </p>
        </div>
      )}

      {phase === 'noBranch' && (
        <Notice
          tone="info"
          icon={<Info size={20} strokeWidth={ICON_STROKE} />}
        >
          {T.noBranch}
        </Notice>
      )}

      {phase === 'error' && (
        <Notice
          tone="danger"
          icon={<AlertTriangle size={20} strokeWidth={ICON_STROKE} color={C.danger} />}
        >
          {T.error}
        </Notice>
      )}

      {/* Футер собираем вручную: те же доли, что у экспорта (ModalActions рассчитан
          на пару), главная — 1.5 */}
      <div style={{ display: 'flex', gap: 10, width: '100%' }}>
        {(phase === 'confirm' || phase === 'loading') && (
          <>
            <div style={{ flex: 1 }}>
              {/* «Отмена» не блокируется на loading — кнопка остаётся рабочим способом
                  выхода из диалога (второй — крестик Modal). Действие само по себе не
                  прерывает git-операцию: Promise в run() дойдёт до конца и переведёт
                  фазу уже на размонтированном компоненте, что безопасно. */}
              <Button variant="ghost" size="md" fullWidth onClick={onClose}>
                {T.btnCancel}
              </Button>
            </div>
            <div style={{ flex: 1.5 }}>
              <Button
                variant="primary"
                size="md"
                fullWidth
                loading={busy}
                disabled={busy}
                leftIcon={!busy ? <Download size={14} strokeWidth={ICON_STROKE} /> : undefined}
                onClick={run}
              >
                {T.btnImport}
              </Button>
            </div>
          </>
        )}
        {phase === 'error' && (
          <>
            <div style={{ flex: 1 }}>
              <Button variant="ghost" size="md" fullWidth onClick={onClose}>
                {T.btnCancel}
              </Button>
            </div>
            <div style={{ flex: 1.5 }}>
              <Button
                variant="primary"
                size="md"
                fullWidth
                leftIcon={<Download size={14} strokeWidth={ICON_STROKE} />}
                onClick={run}
              >
                {T.btnRetry}
              </Button>
            </div>
          </>
        )}
        {(phase === 'success' || phase === 'nothing' || phase === 'noBranch') && (
          <>
            <div style={{ flex: 1 }} />
            <div style={{ flex: 1.5 }}>
              {/* На success/nothing зовём onSuccess (если есть) ДО закрытия — родитель
                  успеет поднять флаг перезагрузки до того, как диалог исчезнет, и при
                  следующем открытии список уже будет с новыми импортированными записями.
                  На noBranch — пусто, onSuccess не нужен. */}
              <Button variant="secondary" size="md" fullWidth onClick={() => {
                if (phase === 'success' || phase === 'nothing') onSuccess?.();
                onClose();
              }}>
                {T.btnClose}
              </Button>
            </div>
          </>
        )}
      </div>
    </Modal>
  );
}