import { Modal } from '../ui/Modal';
import { ModalActions } from '../ui/ModalActions';
import { C, R, FS, FONT, MODAL_W } from '../../lib/design';
import type { FeatureMergeRisk } from '../../types/featureRun';

// Общая модалка подтверждения мержа (волна 3, Ф6). Её берут и страница фичи,
// и попап «Исполнение фичи» (Ф-pop-a) — один компонент, один вид.
//
// Макет: заголовок «Смержить «{название}» в master?», пояснение
// «Марк получит задачу мержа и сольёт ветку {ветка}.», кнопки «Отмена»/«Мерж».
// На 360 — шторка снизу (Modal), тач-цели ≥ 40 (кит-кнопки).
// Контролы — только из кита: Modal (карточка/шторка) + ModalActions (кнопки).
//
// Состояния (управляет вызывающий):
//   pending — запрос в полёте: кнопка «Запускаю…» заблокирована (loading);
//   error   — НЕ-409 сбой: сообщение сервера в теле + «Повторить» (красный тон);
//   409     — «мерж уже идёт»: вызывающий сам закрывает диалог и обновляет
//             состояние (красной ошибки нет — это не сбой, а идемпотентность).
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
  // Фронт ничего не считает: печатает список как пришёл.
  risks?: FeatureMergeRisk[];
  // «Отмена» (и клик по оверлею/крестику): закрывает диалог, API не шлёт.
  onCancel: () => void;
  // «Мерж»/«Запускаю…»/«Повторить»: подтверждение (вызывающий шлёт POST).
  onConfirm: () => void;
}

// Подпись вида риска (Ш8): слово по kind; неизвестный вид — сам ключ (фолбэк,
// как stageName в FeatureTimelineBits).
const RISK_LABEL: Record<FeatureMergeRisk['kind'], string> = {
  not_created: 'Не заведено шагов',
  open: 'Заведены, но не приняты',
  tracker: 'Трекер расходится',
};

export function FeatureMergeDialog({ title, branch, pending, error, risks, onCancel, onConfirm }: FeatureMergeDialogProps) {
  // Пояснение: ветка в тексте; ветки нет — нейтральная фраза (без «ветку null»).
  const subtitle = branch
    ? `Марк получит задачу мержа и сольёт ветку ${branch}.`
    : 'Марк получит задачу мержа и сольёт ветку фичи.';

  // Подпись кнопки подтверждения: pending → «Запускаю…», error → «Повторить», иначе → «Мерж».
  const confirmLabel = pending ? 'Запускаю…' : (error ? 'Повторить' : 'Мерж');

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
          onConfirm={onConfirm}
          onCancel={onCancel}
        />
      }
    >
      {/* Красная ошибка — только при НЕ-409 сбое (409 закрывает диалог без неё). */}
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

      {/* Риски мержа (Ш8): не пустой список — предупреждающий блок над кнопками.
          Текст — по элементам списка, через « · »; ничего не добавляем и не сортируем. */}
      {risks && risks.length > 0 && (
        <div
          data-testid="feature-merge-risks"
          style={{
            background: C.warningBg, color: C.warningText, borderRadius: R.sm,
            padding: '8px 10px', fontSize: FS.sm, fontFamily: FONT.sans, lineHeight: 1.45,
          }}
        >
          {risks.map((r) => `${RISK_LABEL[r.kind] ?? r.kind}: ${r.count}`).join(' · ')}
        </div>
      )}
    </Modal>
  );
}
