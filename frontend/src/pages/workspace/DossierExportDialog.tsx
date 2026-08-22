// Модалка «Выгрузить историю решений в репозиторий» (ADR-004 §6, этап 3):
// уводит паспорта изменений в ветку ccs/dossiers/v1 через git-плюминг.
//
// Шесть состояний по макету docs/mockups/dossier-export-dialog.md:
//  1. confirm         — обычное подтверждение
//  2. confirmShared   — то же + предупреждение об общей папке
//  3. loading         — запрос ушёл; диалог НЕ закрывается (иначе git-операция
//                       оставила бы человека без ответа)
//  4. success         — финальная карточка «выгружено N паспортов»
//  5. empty           — финальная карточка «нечего выгружать» (бэк вернул ноль)
//  6. error           — финальная карточка + возврат к действию с теми же кнопками
//
// Переходы внутри одного открытого Modal, без закрытия и переоткрытия — карточка
// не моргает.
//
// Тексты дословно из постановки задачи 370c5081-c52d-49fd-a655-0217d0e0ee78.

import { useEffect, useState } from 'react';
import { AlertTriangle, CircleCheck, Info } from 'lucide-react';
import { C, FONT, FS, MODAL_W, R, SP } from '../../lib/design';
import { ICON_STROKE } from '../../components/ui/icons';
import { Button, Modal } from '../../components/ui';
import { api } from '../../lib/api';

// Тексты по постановке — вынесены в константы, чтобы линтер случайно не «поправил».
const T = {
  title: 'Выгрузить историю решений в репозиторий',
  warning: 'Эту папку как проект подключил ещё один пользователь. После отправки ветки историю решений увидит и он.',
  successPrefix: 'История решений выгружена в ветку ccs/dossiers/v1 — паспортов: ',
  empty: 'Всё уже выгружено — новых паспортов с прошлого раза нет.',
  error: 'Не удалось выгрузить историю решений. Рабочее дерево не тронуто — попробуйте ещё раз.',
  btnExport: 'Выгрузить',
  btnExportPush: 'Выгрузить и отправить',
  btnCancel: 'Отмена',
  btnClose: 'Закрыть',
} as const;

// Ветка одна и та же во всех текстах; единый инлайн-mono-фрагмент, как у sha коммита
// в карточках DossierHistoryPanel.
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

// Выноска с иконкой слева: warning — общая папка, danger — ошибка экспорта.
// Геометрия и тон — из палитры C.* (ADR §6: «все поверхности — только семантические
// пары токенов»), без сырых hex.
function Notice({ tone, icon, children }: {
  tone: 'warning' | 'danger';
  icon: React.ReactNode;
  children: React.ReactNode;
}) {
  const bg = tone === 'warning' ? C.warningBg : C.dangerBg;
  const fg = tone === 'warning' ? C.warningText : C.dangerText;
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

type Phase = 'confirm' | 'confirmShared' | 'loading' | 'success' | 'empty' | 'error';

// Какое именно действие запустило запрос — нужно для повтора в состоянии «ошибка»:
// если пользователь жал «Выгрузить и отправить», при повторе должна быть та же кнопка,
// а не безопасная «Выгрузить».
type LastAction = 'export' | 'exportPush';

interface Props {
  open: boolean;
  onClose: () => void;
  projectId: string;
  sharedFolder: boolean;
}

export function DossierExportDialog({ open, onClose, projectId, sharedFolder }: Props) {
  // Локальный phase живёт в модалке: при переоткрытии стартуем с подтверждения
  // (не возвращаемся в success/error прошлого запуска). loading держит диалог открытым
  // — closeOnBackdrop=false + no-op onClose внутри запроса.
  const [phase, setPhase] = useState<Phase>('confirm');
  const [lastAction, setLastAction] = useState<LastAction>('export');
  const [count, setCount] = useState(0);

  // При каждом открытии сбрасываемся в подтверждение — иначе после успеха/ошибки
  // повторный клик по кнопке тулбара привёл бы к финальному состоянию прошлого запуска.
  useEffect(() => {
    if (open) {
      // eslint-disable-next-line react-hooks/set-state-in-effect -- ресет при каждом открытии модалки
      setPhase(sharedFolder ? 'confirmShared' : 'confirm');
      setCount(0);
    }
  }, [open, sharedFolder]);

  // Закрыт — не рендерим карточку. Эта проверка нужна на случай, когда родитель
  // держит DossierExportDialog смонтированным постоянно (двойное монтирование
  // панели): без неё диалог жил бы в DOM даже при open=false, плюс активный
  // реестр Modal увидел бы «занято» и не дал открыть второй.
  if (!open) return null;

  const run = async (action: LastAction) => {
    setLastAction(action);
    // eslint-disable-next-line react-hooks/set-state-in-effect -- переход перед запросом, чтобы нажатая кнопка показала спиннер
    setPhase('loading');
    try {
      const res = await api.dossiers.exportRun(projectId, action === 'exportPush');
      if (res.status === 'nothingToExport') {
        setPhase('empty');
      } else {
        setCount(res.count);
        setPhase('success');
      }
    } catch {
      setPhase('error');
    }
  };

  const busy = phase === 'loading';
  // Закрытие разрешено в любой фазе, включая loading: запрос уже ушёл, его UI не
  // отменит, но диалог запирать нельзя (QA фиксировал «не закрывается»). При ошибке
  // catch выше переводит phase в 'error', busy сбрасывается, и закрытие работает штатно.
  const handleClose = onClose;
  const mainLabel = lastAction === 'exportPush' ? T.btnExportPush : T.btnExport;

  return (
    <Modal
      width={MODAL_W.confirm}
      title={phase === 'success' || phase === 'empty' || phase === 'error' ? undefined : T.title}
      // closeOnBackdrop=false во время запроса — иначе клик по оверлею посреди
      // git-плюминга оставил бы пользователя без ответа.
      closeOnBackdrop={!busy}
      onClose={handleClose}
    >
      {phase === 'confirm' || phase === 'confirmShared' || phase === 'loading' ? (
        <div style={{ display: 'flex', flexDirection: 'column', gap: SP.md }}>
          <p style={{
            margin: 0, fontSize: FS.md, color: C.textPrimary, lineHeight: 1.5,
          }}>
            Паспорта изменений уедут в ветку <BranchTag />. Ваше рабочее дерево не изменится, а ветка останется локальной, пока вы не отправите её сами.
          </p>
          {phase === 'confirmShared' && (
            <Notice
              tone="warning"
              icon={<AlertTriangle size={16} strokeWidth={ICON_STROKE} color={C.warning} />}
            >
              {T.warning}
            </Notice>
          )}
        </div>
      ) : phase === 'success' ? (
        <div style={{ display: 'flex', alignItems: 'flex-start', gap: SP.sm }}>
          <CircleCheck size={20} strokeWidth={ICON_STROKE} color={C.success} style={{ flexShrink: 0, marginTop: 1 }} />
          <p style={{
            margin: 0, fontSize: FS.md, color: C.textPrimary, lineHeight: 1.5,
          }}>
            {T.successPrefix}{count}.
          </p>
        </div>
      ) : phase === 'empty' ? (
        <div style={{ display: 'flex', alignItems: 'flex-start', gap: SP.sm }}>
          <Info size={20} strokeWidth={ICON_STROKE} color={C.textSecondary} style={{ flexShrink: 0, marginTop: 1 }} />
          <p style={{
            margin: 0, fontSize: FS.md, color: C.textPrimary, lineHeight: 1.5,
          }}>
            {T.empty}
          </p>
        </div>
      ) : (
        // error
        <div style={{ display: 'flex', flexDirection: 'column', gap: SP.md }}>
          <Notice
            tone="danger"
            icon={<AlertTriangle size={20} strokeWidth={ICON_STROKE} color={C.danger} />}
          >
            {T.error}
          </Notice>
        </div>
      )}

      {/* Футер собираем вручную: три действия (ModalActions рассчитан на пару),
          доля главной — 1.5 (как в ModalActions) */}
      <div style={{ display: 'flex', gap: 10, width: '100%' }}>
        {(phase === 'confirm' || phase === 'confirmShared' || phase === 'loading') && (
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
            <div style={{ flex: 1 }}>
              <Button
                variant="ghostAccent"
                size="md"
                fullWidth
                loading={busy && lastAction === 'export'}
                disabled={busy}
                onClick={() => run('export')}
              >
                {T.btnExport}
              </Button>
            </div>
            <div style={{ flex: 1.5 }}>
              <Button
                variant="primary"
                size="md"
                fullWidth
                loading={busy && lastAction === 'exportPush'}
                disabled={busy}
                onClick={() => run('exportPush')}
              >
                {T.btnExportPush}
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
                onClick={() => run(lastAction)}
              >
                {mainLabel}
              </Button>
            </div>
          </>
        )}
        {(phase === 'success' || phase === 'empty') && (
          <>
            <div style={{ flex: 1 }} />
            <div style={{ flex: 1.5 }}>
              <Button variant="secondary" size="md" fullWidth onClick={onClose}>
                {T.btnClose}
              </Button>
            </div>
          </>
        )}
      </div>
    </Modal>
  );
}
