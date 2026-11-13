// Блок «Начать вики заново» настроек вики (J-F4; ADR-039 решение 10; ручка J-P2, стор J-F0;
// полоса — М2, wiki-panels-v1-proposal А2/П4): разрушающая чистка ИИ-страниц. Инициатор —
// только человек этой кнопкой и только после предпросмотра: сначала previewWikiPurge()
// (dryRun — на диске не тронуто ничего), его числа показываются в модалке, и только
// подтверждение человека запускает runWikiPurge(). Всё удалённое уходит в zip-архив
// (pages/ + keeper/ берётся перед удалением) — безвозвратного ничего нет; свои страницы
// и приёмыши чистка не трогает (таблица классов ADR-039).
//
// Полоса (М2/П4): блок живёт закреплённой полосой у нижнего края тела панели —
// `position: sticky; bottom: 0`, фон тела `C.bgWhite`, `borderTop`, без `zIndex`. Полоса
// стоит последним элементом прокручиваемого тела, поэтому при любой длине списков над ней
// она прижата к нижней кромке окна и не уезжает за «Отбор чатов» (А2: раньше — 8–12 экранов).
// Узко = ширина КОНТЕЙНЕРА, а не окна (П1): `useContainerWidth` мерит саму полосу, при
// ширине < 300 подпись скрывается и остаётся одна кнопка на всю ширину (purgeStripCompact).
// Тач-высота кнопки (minHeight 40) — на `useIsMobile`: узкая панель на десктопе — это мышь.
//
// Состояния: кнопка активна всегда (нулевой состав — не отказ: предпросмотр отдаёт 200 с
// нулями, и «Удалять нечего» человек должен увидеть сам); отказ предпросмотра — Notice danger
// в полосе над кнопкой (причина бэка в message; сетевой сбой — общий текст); N=0 — подтверждение
// в модалке выключено; отказ чистки — плашка в модалке, она остаётся в состоянии предпросмотра
// (повторить можно); итог — число удалённых, путь архива и warning бэка, когда шаг перезапуска
// разбора не доведён (200 с предупреждением, не потеря). Итог принадлежит полосе, а не модалке
// (QA J-F4): пока запрос чистки идёт, над рядом — «Чистка выполняется…», после ответа —
// «Удалено N… Архив: …» и warning. Строка видна и при открытом окне, и после его закрытия
// (×/Esc): закрыть окно во время чистки — не потерять путь архива, единственное место, где
// удалённое можно найти. У итога есть «Скрыть» (×): полоса не должна навсегда вырасти на строки.
//
// Классы состава — предикаты включения бэка ('ai' | 'together' | 'chronicle' | 'source');
// на экран выходят русские подписи по таблице ADR-039, сырой ключ — только неизвестному
// классу (аддитивность: нового предиката здесь может не быть). Имена разделов — ключи
// countsBySection: это папки вики владельца, они и в дереве по-русски.

import { useState } from 'react';
import { AlertTriangle, X } from 'lucide-react';
import {
  C, FONT, FS, MODAL_W, SP, useContainerWidth, useIsMobile,
  WikiError,
  previewWikiPurge, runWikiPurge,
  type WikiPurgeDto,
} from 'aihome_shell/kit';
import { Button } from '../../../components/ui/Button';
import { IconButton } from '../../../components/ui/IconButton';
import { Modal } from '../../../components/ui/Modal';
import { ModalActions } from '../../../components/ui/ModalActions';
import { Notice } from '../../../components/ui/Notice';
import { countWord } from './wikiLabels';

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

// Причина отказа — своя формулировка Хранителя в message (WikiController.PurgeRefusal);
// сетевой сбой/таймаут — не отказ контракта, у него русской причины нет
const refusalText = (e: unknown): string =>
  e instanceof WikiError && e.message
    ? e.message
    : 'Не удалось достучаться до сервера — попробуйте ещё раз.';

// Полоса компактна при узком КОНТЕЙНЕРЕ (не окне): ниже 300 px двухчастная полоса
// (подпись + кнопка) не читается — остаётся одна кнопка на всю ширину. До первого замера
// (null) — широкая раскладка: она же и на широком экране, самый частый случай (как в
// useNarrowContainer). Порог 300 — из А2; новых порогов не заводим (П1).
export function purgeStripCompact(width: number | null): boolean {
  return width !== null && width < 300;
}

// ─── Модалка: предпросмотр → подтверждение → итог ───

function PreviewBody({ data }: { data: WikiPurgeDto }) {
  const n = totalCount(data.countsBySection);
  const rows = Object.entries(data.countsBySection)
    .map(([section, byClass]) => ({ section, total: sum(byClass), byClass }))
    .filter(r => r.total > 0)
    .sort((a, b) => a.section.localeCompare(b.section, 'ru'));
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: SP.sm }}>
      {/* Длинное пояснение переехало из блока в модалку (А2): что именно удаляется —
          человек читает ДО подтверждения, а не в полосе */}
      <div style={{ fontSize: FS.sm, color: C.textSecondary }}>
        Удалятся все страницы, написанные ИИ: хроники, карточки источников и страницы,
        написанные вместе с вами.
      </div>
      {n === 0 ? (
        <div style={{ fontSize: FS.base, color: C.textPrimary }}>Удалять нечего.</div>
      ) : (
        <div style={{ display: 'flex', flexDirection: 'column', gap: SP.xs }}>
          {/* Строка целиком, не JSX-вставками: DOM тогда даёт один текстовый узел —
              и тест, и скринридер читают её как сказанную */}
          <div style={{ fontSize: FS.base, color: C.textPrimary }}>
            {`Будет удалено ${countWord(n, 'страницу', 'страницы', 'страниц')}:`}
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
        {`Удалено ${countWord(data.deleted, 'страницу', 'страницы', 'страниц')}.`}
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

// ─── Полоса ───

export function WikiPurgeBlock() {
  const [preview, setPreview] = useState<WikiPurgeDto | null>(null);
  const [result, setResult] = useState<WikiPurgeDto | null>(null);
  // Итог последней чистки принадлежит полосе, а не модалке (QA J-F4): закрытие окна
  // во время чистки не теряет его — строка над рядом живёт и при закрытом окне
  const [lastResult, setLastResult] = useState<WikiPurgeDto | null>(null);
  const [previewBusy, setPreviewBusy] = useState(false);
  const [runBusy, setRunBusy] = useState(false);
  const [errorText, setErrorText] = useState<string | null>(null);

  // Полоса мерит СВОЮ ширину (контейнер, а не окно — П1): ref на корень полосы
  const [stripRef, width] = useContainerWidth<HTMLDivElement>();
  const isMobile = useIsMobile();
  const compact = purgeStripCompact(width);

  const openPreview = async (): Promise<void> => {
    setErrorText(null);
    // Новое окно — снова предпросмотр: итог прошлой чистки живёт строкой над рядом,
    // а не в модалке (иначе закрытое во время чистки окно вернулось бы готовым итогом)
    setResult(null);
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
      const data = await runWikiPurge();
      setResult(data);
      // и в полосу: если окно закрыли ×/Esc во время чистки, запрос доходит до конца
      // и итог выходит строкой над рядом
      setLastResult(data);
    } catch (e) {
      setErrorText(refusalText(e));
    } finally {
      setRunBusy(false);
    }
  };

  const n = preview ? totalCount(preview.countsBySection) : 0;

  return (
    <div
      ref={stripRef}
      style={{
        position: 'sticky',
        bottom: 0,
        background: C.bgWhite,
        borderTop: `1px solid ${C.border}`,
        padding: `${SP.sm}px ${SP.md}px`,
      }}
    >
      {/* Итог/ход чистки — над рядом с кнопкой (А2): виден и при открытом окне, и после
          его закрытия; «Скрыть» (×) снимает итог, чтобы полоса не оставалась расширенной */}
      {(runBusy || lastResult) && (
        <div data-testid="wiki-purge-status" style={{ display: 'flex', alignItems: 'flex-start', gap: SP.sm, marginBottom: SP.sm }}>
          <div style={{ flex: 1, minWidth: 0 }}>
            {runBusy ? (
              <div style={{ fontSize: FS.sm, color: C.textSecondary }}>Чистка выполняется…</div>
            ) : lastResult ? (
              <div style={{ display: 'flex', flexDirection: 'column', gap: 2 }}>
                <div style={{ fontSize: FS.sm, color: C.textPrimary }}>
                  {`Удалено ${countWord(lastResult.deleted, 'страницу', 'страницы', 'страниц')}.`}
                </div>
                {lastResult.archivePath && (
                  <div style={{ fontFamily: FONT.mono, fontSize: FS.xs, color: C.textSecondary, overflowWrap: 'anywhere' }}>
                    {`Архив: ${lastResult.archivePath}`}
                  </div>
                )}
                {lastResult.warning && (
                  <Notice tone="warning" icon={AlertTriangle}>{lastResult.warning}</Notice>
                )}
              </div>
            ) : null}
          </div>
          {lastResult && !runBusy && (
            // Тач-цель (ревью М2): на мобиле × — lg (40), как «Начать заново» (мин. 40);
            // на десктопе остаётся sm (28) — полоса не растёт в высоту под мышью
            <IconButton ariaLabel="Скрыть" title="Скрыть" size={isMobile ? 'lg' : 'sm'} onClick={() => setLastResult(null)}>
              <X size={16} />
            </IconButton>
          )}
        </div>
      )}

      {/* Отказ предпросмотра — Notice danger в полосе над кнопкой (модалки нет) */}
      {errorText && !preview && (
        <div style={{ marginBottom: SP.sm }}>
          <Notice tone="danger" icon={AlertTriangle}>{errorText}</Notice>
        </div>
      )}

      <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm }}>
        {!compact && (
          <div
            style={{
              flex: 1, minWidth: 0, fontSize: FS.sm, color: C.textSecondary,
              display: '-webkit-box', WebkitBoxOrient: 'vertical', WebkitLineClamp: 2, overflow: 'hidden',
            }}
          >
            Удалить страницы ИИ и начать заново
          </div>
        )}
        <Button
          variant="danger"
          size="sm"
          dataTestId="wiki-purge-open"
          fullWidth={compact}
          loading={previewBusy}
          style={isMobile ? { minHeight: 40 } : undefined}
          onClick={() => void openPreview()}
        >
          Начать заново…
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
                confirmLabel={`Удалить ${countWord(n, 'страницу', 'страницы', 'страниц')}`}
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
