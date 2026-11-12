// Блок «Начать вики заново» настроек вики (J-F4; ADR-039 решение 10; ручка J-P2, стор J-F0):
// разрушающая чистка ИИ-страниц. Инициатор — только человек этой кнопкой и только после
// предпросмотра: сначала previewWikiPurge() (dryRun — на диске не тронуто ничего), его числа
// показываются в модалке, и только подтверждение человека запускает runWikiPurge().
// Всё удалённое уходит в zip-архив (pages/ + keeper/ берётся перед удалением) — безвозвратного
// ничего нет; свои страницы и приёмыши чистка не трогает (таблица классов ADR-039).
//
// Состояния: кнопка активна всегда (нулевой состав — не отказ: предпросмотр отдаёт 200 с
// нулями, и «Удалять нечего» человек должен увидеть сам); отказ предпросмотра — плашка у
// кнопки (причина бэка в message; сетевой сбой — общий текст); N=0 — подтверждение в модалке
// выключено; отказ чистки — плашка в модалке, она остаётся в состоянии предпросмотра
// (повторить можно); итог — число удалённых, путь архива и warning бэка, когда шаг
// перезапуска разбора не доведён (200 с предупреждением, не потеря).
//
// Классы состава — предикаты включения бэка ('ai' | 'together' | 'chronicle' | 'source');
// на экран выходят русские подписи по таблице ADR-039, сырой ключ — только неизвестному
// классу (аддитивность: нового предиката здесь может не быть). Имена разделов — ключи
// countsBySection: это папки вики владельца, они и в дереве по-русски.

import { useState } from 'react';
import { AlertTriangle } from 'lucide-react';
import {
  C, FONT, FS, MODAL_W, SP,
  WikiError,
  previewWikiPurge, runWikiPurge,
  type WikiPurgeDto,
} from 'aihome_shell/kit';
import { Button } from '../../../components/ui/Button';
import { Modal } from '../../../components/ui/Modal';
import { ModalActions } from '../../../components/ui/ModalActions';
import { Notice } from '../../../components/ui/Notice';

// ─── Разбор состава предпросмотра ───

const CLASS_LABEL: Record<string, string> = {
  ai: 'написанные ИИ',
  together: 'написанные вместе с вами',
  chronicle: 'хроники',
  source: 'карточки источников',
};

const sum = (v: Record<string, number>): number => Object.values(v).reduce((s, c) => s + c, 0);
const totalCount = (counts: Record<string, Record<string, number>>): number =>
  Object.values(counts).reduce((s, byClass) => s + sum(byClass), 0);

// Склонение числительного с числом: «1 страницу», «2 страницы», «5 страниц», «11 страниц»
const pagesWord = (n: number): string => {
  const teen = Math.abs(n) % 100 >= 11 && Math.abs(n) % 100 <= 14;
  const last = Math.abs(n) % 10;
  if (teen || last === 0 || last >= 5) return 'страниц';
  if (last === 1) return 'страницу';
  return 'страницы';
};

// Причина отказа — своя формулировка Хранителя в message (WikiController.PurgeRefusal);
// сетевой сбой/таймаут — не отказ контракта, у него русской причины нет
const refusalText = (e: unknown): string =>
  e instanceof WikiError && e.message
    ? e.message
    : 'Не удалось достучаться до сервера — попробуйте ещё раз.';

// ─── Модалка: предпросмотр → подтверждение → итог ───

function PreviewBody({ data }: { data: WikiPurgeDto }) {
  const n = totalCount(data.countsBySection);
  const rows = Object.entries(data.countsBySection)
    .map(([section, byClass]) => ({ section, total: sum(byClass), byClass }))
    .filter(r => r.total > 0)
    .sort((a, b) => a.section.localeCompare(b.section, 'ru'));
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: SP.sm }}>
      {n === 0 ? (
        <div style={{ fontSize: FS.base, color: C.textPrimary }}>Удалять нечего.</div>
      ) : (
        <div style={{ display: 'flex', flexDirection: 'column', gap: SP.xs }}>
          {/* Строка целиком, не JSX-вставками: DOM тогда даёт один текстовый узел —
              и тест, и скринридер читают её как сказанную */}
          <div style={{ fontSize: FS.base, color: C.textPrimary }}>
            {`Будет удалено ${n} ${pagesWord(n)}:`}
          </div>
          {rows.map(({ section, total, byClass }) => (
            <div key={section} data-testid={`wiki-purge-section-${section}`} style={{ padding: `${SP.xs}px 0 ${SP.xs}px ${SP.sm}px`, borderLeft: `2px solid ${C.border}` }}>
              <div style={{ fontSize: FS.base, color: C.textHeading, overflowWrap: 'anywhere' }}>
                {`${section} — ${total}`}
              </div>
              <div style={{ fontSize: FS.sm, color: C.textMuted, marginTop: 2 }}>
                {Object.entries(byClass).filter(([, c]) => c > 0)
                  .map(([cls, c]) => `${CLASS_LABEL[cls] ?? cls} — ${c}`)
                  .join(', ')}
              </div>
            </div>
          ))}
        </div>
      )}
      <div style={{ fontSize: FS.sm, color: C.textSecondary }}>
        Страницы, написанные вами, и приёмыши остаются. Всё удалённое сохранится в zip-архиве.
      </div>
    </div>
  );
}

function ResultBody({ data }: { data: WikiPurgeDto }) {
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: SP.sm }}>
      <div style={{ fontSize: FS.base, color: C.textPrimary }}>
        {`Удалено ${data.deleted} ${pagesWord(data.deleted)}.`}
      </div>
      {data.archivePath && (
        <div style={{ fontSize: FS.sm, color: C.textSecondary, overflowWrap: 'anywhere' }}>
          Архив: <span style={{ fontFamily: FONT.mono }}>{data.archivePath}</span>
        </div>
      )}
      {data.warning && (
        <Notice tone="warning" icon={AlertTriangle}>{data.warning}</Notice>
      )}
    </div>
  );
}

// ─── Блок ───

export function WikiPurgeBlock() {
  const [preview, setPreview] = useState<WikiPurgeDto | null>(null);
  const [result, setResult] = useState<WikiPurgeDto | null>(null);
  const [previewBusy, setPreviewBusy] = useState(false);
  const [runBusy, setRunBusy] = useState(false);
  const [errorText, setErrorText] = useState<string | null>(null);

  const openPreview = async (): Promise<void> => {
    setErrorText(null);
    setPreviewBusy(true);
    try {
      setPreview(await previewWikiPurge());
    } catch (e) {
      setErrorText(refusalText(e));
    } finally {
      setPreviewBusy(false);
    }
  };

  const close = (): void => {
    setPreview(null);
    setResult(null);
    setErrorText(null);
  };

  const confirmRun = async (): Promise<void> => {
    setErrorText(null);
    setRunBusy(true);
    try {
      // стор за нас перечитает дерево и открытые каталоги (runWikiPurge, J-F0)
      setResult(await runWikiPurge());
    } catch (e) {
      setErrorText(refusalText(e));
    } finally {
      setRunBusy(false);
    }
  };

  const n = preview ? totalCount(preview.countsBySection) : 0;

  return (
    <div style={{ padding: `${SP.sm}px ${SP.md}px ${SP.md}px` }}>
      <h2 style={{ margin: `${SP.xs}px 0 0`, fontFamily: FONT.serif, fontSize: FS.lg, color: C.textHeading }}>
        Начать вики заново
      </h2>
      <div style={{ fontSize: FS.sm, color: C.textSecondary, marginTop: SP.xs }}>
        Удалить все страницы, написанные ИИ: хроники, карточки источников и страницы,
        написанные вместе с вами. Ваши страницы и приёмыши останутся, всё удалённое уйдёт
        в zip-архив.
      </div>

      {errorText && !preview && (
        <div style={{ marginTop: SP.md }}>
          <Notice tone="danger" icon={AlertTriangle}>{errorText}</Notice>
        </div>
      )}

      <div style={{ marginTop: SP.md }}>
        <Button
          variant="danger"
          dataTestId="wiki-purge-open"
          loading={previewBusy}
          onClick={() => void openPreview()}
        >
          Начать вики заново
        </Button>
      </div>

      {preview && (
        <Modal
          title="Начать вики заново?"
          width={MODAL_W.confirm}
          onClose={close}
          // на время чистки оверлей окно не закрывает: человек ждёт итог разрушающей операции
          closeOnBackdrop={!runBusy}
          footer={
            result ? (
              <div style={{ width: '100%' }}>
                <Button variant="secondary" size="md" fullWidth onClick={close}>
                  Закрыть
                </Button>
              </div>
            ) : (
              <ModalActions
                confirmLabel={`Удалить ${n} ${pagesWord(n)}`}
                confirmVariant="danger"
                confirmDisabled={n === 0}
                loading={runBusy}
                onConfirm={() => void confirmRun()}
                onCancel={close}
              />
            )
          }
        >
          {result ? <ResultBody data={result} /> : <PreviewBody data={preview} />}
          {errorText && !result && (
            <Notice tone="danger" icon={AlertTriangle}>{errorText}</Notice>
          )}
        </Modal>
      )}
    </div>
  );
}
