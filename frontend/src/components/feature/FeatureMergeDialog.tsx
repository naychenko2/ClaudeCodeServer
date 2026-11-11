import { Modal } from '../ui/Modal';
import { ModalActions } from '../ui/ModalActions';
import { SegmentedControl } from '../ui/Segmented';
import { useIsMobileModal } from '../ui/useIsMobileModal';
import { C, R, FS, FONT, MODAL_W } from '../../lib/design';
import type { FeatureMergeRisk, MergeTailDecision } from '../../types/featureRun';

// Общая модалка подтверждения мержа (волна 3, Ф6). Её берут и страница фичи,
// и попап «Исполнение фичи» (Ф-pop-a) — один компонент, один вид.
//
// Макет: заголовок «Смержить «{название}» в master?», пояснение
// «Марк получит задачу мержа и сольёт ветку {ветка}.», кнопки «Отмена»/«Мерж».
// На 360 — шторка снизу (Modal), тач-цели ≥ 40 (кит-кнопки).
// Контролы — только из кита: Modal (карточка/шторка) + ModalActions (кнопки)
// + SegmentedControl (выбор решения о хвосте).
//
// Состояния (управляет вызывающий):
//   pending — запрос в полёте: кнопка «Запускаю…» заблокирована (loading);
//   error   — НЕ-409 сбой: сообщение сервера в теле + «Повторить» (красный тон);
//   409 без кода — «мерж уже идёт»: вызывающий сам закрывает диалог и обновляет
//             состояние (красной ошибки нет — это не сбой, а идемпотентность);
//   409 tail_decision_required / tail_withdraw_not_applied (FS-XF1, §8.3) — диалог
//             НЕ закрывается: вызывающий перечитывает risks (перезапрос страницы),
//             сбрасывает tailChoice и при втором ставит tailNotice; это не ошибка
//             сети и не красная ошибка.
//
// Закрытый хвост (FS-X1/FS-XF1, §8.3): риск kind='tail' из attention.mergeRisks —
// сданные, но не принятые шаги. Он НЕ печатается строкой, как живые риски: диалог
// показывает его поимённо (ключи — run.closedTailKeys, порядок бэка как есть) и
// требует решения: «Снять хвост и мержить» / «Мержить, хвост оставить». Пока
// решения нет, «Мерж» неактивен; выбор передаётся вызывающему (он кладёт его в
// тело POST merge). Состав хвоста — только из данных бэка: фронт ничего не считает.
export interface FeatureMergeDialogProps {
  // Название фичи (в заголовок).
  title: string;
  // Ветка (в пояснение); null — ветки нет (нейтральная фраза).
  branch: string | null;
  // Запрос в полёте: кнопка «Запускаю…» заблокирована.
  pending: boolean;
  // Сообщение сервера при НЕ-409 сбое; null — ошибки нет.
  error: string | null;
  // Риски мержа (из attention.mergeRisks, Ш8): undefined/пусто — блока нет.
  // Фронт ничего не считает: печатает список как пришёл. Исключение — 'tail':
  // он уходит в отдельный блок решения, не в текстовую строку рисков.
  risks?: FeatureMergeRisk[];
  // Ключи закрытого хвоста (run.closedTailKeys, бэк: FeatureRun.ClosedTailKeys):
  // показываются поимённо в порядке бэка; состав фронт не выводит.
  tailKeys?: string[];
  // Решение о хвосте (состояние держит вызывающий): null — выбора нет,
  // «Мерж» неактивен при непустом хвосте.
  tailChoice: MergeTailDecision | null;
  // Клик по варианту решения: вызывающий запоминает и шлёт в теле POST merge.
  onTailChoice: (v: MergeTailDecision) => void;
  // Предупреждение о хвосте (не красная ошибка): «хвост изменился, проверь список»
  // при 409 tail_withdraw_not_applied. null — нет.
  tailNotice?: string | null;
  // «Отмена» (и клик по оверлею/крестику): закрывает диалог, API не шлёт.
  onCancel: () => void;
  // «Мерж»/«Запускаю…»/«Повторить»: подтверждение (вызывающий шлёт POST,
  // с tail = выбранным решением).
  onConfirm: () => void;
}

// Подпись вида риска (Ш8): слово по kind; неизвестный вид — сам ключ (фолбэк,
// как stageName в FeatureTimelineBits). 'tail' здесь не печатается (свой блок
// решения), ключ держится для полноты Record.
const RISK_LABEL: Record<FeatureMergeRisk['kind'], string> = {
  not_created: 'Не заведено шагов',
  open: 'Заведены, но не приняты',
  tail: 'Закрытые непринятые шаги',
  tracker: 'Трекер расходится',
};

export function FeatureMergeDialog({ title, branch, pending, error, risks, tailKeys, tailChoice, onTailChoice, tailNotice, onCancel, onConfirm }: FeatureMergeDialogProps) {
  const isMobile = useIsMobileModal();

  // Пояснение: ветка в тексте; ветки нет — нейтральная фраза (без «ветку null»).
  const subtitle = branch
    ? `Марк получит задачу мержа и сольёт ветку ${branch}.`
    : 'Марк получит задачу мержа и сольёт ветку фичи.';

  // Подпись кнопки подтверждения: pending → «Запускаю…», error → «Повторить», иначе → «Мерж».
  const confirmLabel = pending ? 'Запускаю…' : (error ? 'Повторить' : 'Мерж');

  // Риски: текстовая строка — живые и structural (не 'tail'); 'tail' — отдельный
  // блок решения. Порядок и состав — как пришли (бэк).
  const listRisks = (risks ?? []).filter((r) => r.kind !== 'tail');
  const tailRisk = (risks ?? []).find((r) => r.kind === 'tail') ?? null;
  // При закрытом хвосте мерж без решения не идёт (бэк ответил бы 409
  // tail_decision_required): кнопка активна только после выбора.
  const confirmDisabled = tailRisk !== null && tailChoice === null;

  return (
    <Modal
      title={`Смержить «${title}» в master?`}
      subtitle={subtitle}
      width={MODAL_W.confirm}
      onClose={onCancel}
      footer={
        <ModalActions
          confirmLabel={confirmLabel}
          loading={pending}
          confirmDisabled={confirmDisabled}
          onConfirm={onConfirm}
          onCancel={onCancel}
        />
      }
    >
      {/* Красная ошибка — только при НЕ-409 сбое (409 без кода закрывает диалог без неё). */}
      {error && (
        <div
          data-testid="feature-merge-error"
          role="alert"
          style={{
            background: C.dangerBg, color: C.dangerText, borderRadius: R.sm,
            padding: '8px 10px', fontSize: FS.sm, fontFamily: FONT.sans, lineHeight: 1.45,
          }}
        >
          {error}
        </div>
      )}

      {/* Предупреждение о хвосте (409 tail_withdraw_not_applied): не сбой сети —
          состояние фичи изменилось, решение сбрасывается, риски перечитываются. */}
      {tailNotice && (
        <div
          data-testid="feature-merge-tail-notice"
          role="status"
          style={{
            background: C.warningBg, color: C.warningText, borderRadius: R.sm,
            padding: '8px 10px', fontSize: FS.sm, fontFamily: FONT.sans, lineHeight: 1.45,
          }}
        >
          {tailNotice}
        </div>
      )}

      {/* Риски мержа (Ш8): не пустой список — предупреждающий блок над кнопками.
          Текст — по элементам списка, через « · »; ничего не добавляем и не сортируем. */}
      {listRisks.length > 0 && (
        <div
          data-testid="feature-merge-risks"
          style={{
            background: C.warningBg, color: C.warningText, borderRadius: R.sm,
            padding: '8px 10px', fontSize: FS.sm, fontFamily: FONT.sans, lineHeight: 1.45,
          }}
        >
          {listRisks.map((r) => `${RISK_LABEL[r.kind] ?? r.kind}: ${r.count}`).join(' · ')}
        </div>
      )}

      {/* Закрытый хвост (FS-XF1): поимённо (ключи бэка, порядок как есть) и выбор
          решения. Длинный список — прокрутка внутри блока: футер диалога с кнопками
          и выбором остаётся на месте (Modal прижимает его). На 360 варианты в столбик,
          тач-цель ≥ 40 (SegmentedControl touch). */}
      {tailRisk && (
        <div
          data-testid="feature-merge-tail"
          style={{
            background: C.warningBg, color: C.warningText, borderRadius: R.sm,
            padding: '8px 10px', fontFamily: FONT.sans, lineHeight: 1.45,
          }}
        >
          <div style={{ fontSize: FS.sm }}>
            {`${RISK_LABEL.tail}: ${tailRisk.count}`}
          </div>
          {tailKeys && tailKeys.length > 0 && (
            <ol
              data-testid="feature-merge-tail-keys"
              style={{
                margin: '6px 0 0', paddingLeft: 18, maxHeight: '40vh', overflowY: 'auto',
                WebkitOverflowScrolling: 'touch',
              }}
            >
              {tailKeys.map((k) => (
                <li key={k} style={{ fontSize: FS.sm, fontFamily: FONT.mono, wordBreak: 'break-all' }}>
                  {k}
                </li>
              ))}
            </ol>
          )}
          <div style={{ marginTop: 8 }}>
            <SegmentedControl<MergeTailDecision>
              // выбора нет → пустая строка не совпадёт ни с одним value — ни один
              // сегмент не подсвечен (активного нет, пока решения нет)
              value={(tailChoice ?? '') as unknown as MergeTailDecision}
              options={[
                { value: 'withdraw', label: 'Снять хвост и мержить' },
                { value: 'keep', label: 'Мержить, хвост оставить' },
              ]}
              onChange={onTailChoice}
              columns={isMobile ? 1 : 2}
              touch={isMobile}
            />
          </div>
        </div>
      )}
    </Modal>
  );
}
