// К5b · «Файлы предмета» — опись загруженных материалов, загрузка, удаление и постоянная
// подсказка про решебники (разрез §2 и §9 «preview в ответе загрузки», макет D2).
// Трасса данных: GET …/materials → опись в состояние, ключованное subjectId → строки списка;
// upload → 201 (строка + preview) → перечитать list + превью под строкой; remove → подтверждение
// в строке → локальное исключение. Ответ прежнего subjectId ни в list, ни в upload, ни в remove
// не применяется (тот же ключ, что у ChildLessonChat: curSid перед каждым set).
// Ручки — админские (ребёнок отбивается классовой ролью контроллера, ChildSubjectMaterialsController.cs:39-42):
// ребёнок материалы не видит, их читает репетитор предмета.
// Отказы — { code, message } на error.body (api.ts:214-251): текст сервера показывается как есть.

import { useCallback, useEffect, useRef, useState } from 'react';
import type { ChangeEvent, ReactNode } from 'react';
import { AlertTriangle, ChevronDown, ChevronUp, FileText, Trash2, Upload } from 'lucide-react';
import { C, FONT, FS, R, SP } from '../../lib/design';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import { Button } from '../../components/ui/Button';
import { EmptyState } from '../../components/ui/EmptyState';
import { IconButton } from '../../components/ui/IconButton';
import { Notice } from '../../components/ui/Notice';
import { useIsMobile } from '../../lib/breakpoints';
import { childApi } from './api';
import type { ChildSubjectMaterial } from './types';

interface Props {
  userId: string;
  subjectId: string;
}

// Типы, которые принимает сервер (ChildSubjectMaterialsController, 415 unsupported_type вне этого).
const ACCEPT = '.pdf,.txt,.md,.docx,image/png,image/jpeg,image/webp';

// Подпись плитки и метки типа: wire-значения kind из стора (types.ts:111-113). 'text' — общий
// для txt/md (сигнатура их не различает), 'image' — распознанная страница.
const KIND_LABEL: Record<ChildSubjectMaterial['kind'], string> = { pdf: 'pdf', text: 'txt', docx: 'docx', image: 'img' };
const KIND_META: Record<ChildSubjectMaterial['kind'], string> = { pdf: 'PDF', text: 'TXT', docx: 'DOCX', image: 'IMG' };

const MAX_UPLOAD_MB = 20; // серверный порог формы (too_large), тот же, что в подсказке под кнопкой

function formatBytes(n: number): string {
  const mb = n / (1024 * 1024);
  if (mb >= 1) return `${mb.toFixed(1).replace('.', ',')} МБ`;
  return `${Math.max(1, Math.round(n / 1024))} КБ`;
}

// Дата локальная (момент загрузки пришёл ISO с оффсетом сервера): день/месяц того пояса,
// в котором сидит админ. Intl тут не нужен — формат один, ru-RU.
function formatDate(iso: string): string {
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return '';
  const p = (x: number) => String(x).padStart(2, '0');
  return `${p(d.getDate())}.${p(d.getMonth() + 1)}.${d.getFullYear()}`;
}

function filesWord(n: number): string {
  const tail = n % 10;
  if (tail === 1 && n % 100 !== 11) return 'файл';
  if (tail >= 2 && tail <= 4 && !(n % 100 >= 12 && n % 100 <= 14)) return 'файла';
  return 'файлов';
}

// Текст отказа — серверный message как есть (в нём уже названа причина: «файл больше 20 МБ»,
// «в PDF нет текста»). Своего текста по кодам нет: сервер держит их формулировки (К5a).
function rejectText(e: unknown): string {
  const body = (e as { body?: { message?: string } } | null)?.body;
  return body?.message || 'Не получилось. Попробуй ещё раз.';
}

const isImageFile = (file: File) => file.type.startsWith('image/');

export function ChildSubjectMaterials({ userId, subjectId }: Props) {
  const isMobile = useIsMobile();
  const fileRef = useRef<HTMLInputElement>(null);
  // Текущий предмет: ответ любого запроса применяется, только если он про него (Контракт
  // «устаревший ответ» — тот же приём, что curSid в ChildLessonChat).
  const curSid = useRef(subjectId);
  // Одна флаговая блокировка на upload/remove: второй вызов во время запроса игнорируется.
  const busyRef = useRef(false);

  // Смена предмета сбрасывает список, превью и ошибки (сброс — в эффекте смены, ниже).
  // Устаревший ответ прежнего subjectId отбивается единственной сверкой curSid в обработчике
  // каждого ответа (тот же приём, что curSid в ChildLessonChat) — состояние не ключуется,
  // чтобы эта сверка была последней линией обороны, а не одной из двух.
  const [items, setItems] = useState<ChildSubjectMaterial[] | null>(null);
  const [listFailed, setListFailed] = useState<string | null>(null);
  // isImage фиксируется по выбранному файлу (его type), а не угадывается по имени: подпись
  // строки «идёт» («Распознаём страницу…» против «достаю текст») обязана соответствовать тому,
  // что реально делает сервер с этим файлом.
  const [busy, setBusy] = useState<{ kind: 'upload' | 'remove'; name: string; isImage?: boolean } | null>(null);
  const [reject, setReject] = useState<string | null>(null);
  const [preview, setPreview] = useState<{ id: string; text: string } | null>(null);
  const [previewOpen, setPreviewOpen] = useState(true);
  const [confirmDel, setConfirmDel] = useState<string | null>(null);

  const load = useCallback(() => {
    const sid = subjectId;
    childApi.materials.list(userId, sid).then(
      (list) => { if (curSid.current === sid) setItems(list); },
      (e) => { if (curSid.current === sid) setListFailed(rejectText(e)); },
    );
  }, [userId, subjectId]);

  // Монтирование и смена предмета: сброс состояния прежнего предмета и чтение нового.
  useEffect(() => {
    curSid.current = subjectId;
    setItems(null);
    setListFailed(null);
    setReject(null);
    setPreview(null);
    setPreviewOpen(true);
    setConfirmDel(null);
    setBusy(null);
    busyRef.current = false;
    load();
  }, [userId, subjectId, load]);

  const upload = useCallback((file: File) => {
    if (busyRef.current) return; // второй «Загрузить» во время запроса игнорируется
    busyRef.current = true;
    const sid = subjectId;
    setBusy({ kind: 'upload', name: file.name, isImage: isImageFile(file) });
    setReject(null);
    setPreview(null);
    childApi.materials.upload(userId, sid, file).then(
      (r) => {
        if (curSid.current !== sid) return; // ответ про прежний предмет — и строка, и превью не нужны
        setPreview({ id: r.id, text: r.preview });
        setPreviewOpen(true);
        load(); // после успеха — перечитать опись (строка придёт из серверного списка)
      },
      (e) => { if (curSid.current === sid) setReject(rejectText(e)); },
    ).finally(() => {
      busyRef.current = false;
      if (curSid.current === sid) setBusy(null);
    });
  }, [userId, subjectId, load]);

  const onPick = (e: ChangeEvent<HTMLInputElement>) => {
    const file = e.target.files?.[0];
    e.target.value = ''; // тот же файл вторично — тот же onchange (value не «запомнил» бы его)
    if (file) upload(file);
  };

  const remove = useCallback((m: ChildSubjectMaterial) => {
    if (busyRef.current) return; // второй «Удалить» во время запроса игнорируется
    busyRef.current = true;
    const sid = subjectId;
    setBusy({ kind: 'remove', name: m.name });
    childApi.materials.remove(userId, sid, m.id).then(
      () => {
        if (curSid.current !== sid) return;
        setItems((prev) => prev?.filter((x) => x.id !== m.id) ?? prev);
        setPreview((prev) => (prev && prev.id === m.id ? null : prev));
        setConfirmDel(null);
      },
      (e) => {
        if (curSid.current !== sid) return;
        setConfirmDel(null);
        setReject(rejectText(e));
      },
    ).finally(() => {
      busyRef.current = false;
      if (curSid.current === sid) setBusy(null);
    });
  }, [userId, subjectId]);

  const list = items;
  const busyHere = busy;
  const rejectHere = reject;
  const listFailedHere = listFailed;
  const previewHere = preview;
  const confirmHere = confirmDel;

  // Строки описи (+ подтверждение удаления вместо строки + строка загрузки) собираются
  // императивно: разделительная граница — у всего, кроме первой строки.
  const rows: ReactNode[] = [];
  const rowBase = { display: 'flex', alignItems: 'center', gap: SP.md, minHeight: 52, padding: `${SP.xs}px ${SP.sm}px ${SP.xs}px ${SP.md}px` } as const;
  const divider = () => (rows.length ? { borderTop: `1px solid ${C.borderLight}` } : {});
  for (const m of list ?? []) {
    if (confirmHere === m.id) {
      // Подтверждение прямо в строке (макет Д2): «Удалить» опасным действием, «Отмена» — закрыть.
      rows.push(
        <div key={m.id} data-testid="material-confirm" style={{ padding: SP.sm, ...divider() }}>
          <Notice
            tone="danger"
            icon={Trash2}
            title={`Удалить «${m.name}»?`}
            action={
              <div style={{ display: 'flex', gap: SP.sm, flexWrap: 'wrap' }}>
                <Button variant="danger" size="md" dataTestId="material-remove-yes" onClick={() => remove(m)}>
                  {busyHere?.kind === 'remove' ? 'Удаляю…' : 'Удалить'}
                </Button>
                <Button variant="ghost" size="md" dataTestId="material-remove-no" onClick={() => setConfirmDel(null)}>
                  Отмена
                </Button>
              </div>
            }
          >
            Репетитор больше не сможет его читать. Вернуть нельзя — только загрузить снова.
          </Notice>
        </div>,
      );
      continue;
    }
    rows.push(
      <div key={m.id} data-testid="material-row" style={{ ...rowBase, ...divider() }}>
        <span
          aria-hidden
          style={{
            width: 28, height: 28, flexShrink: 0, borderRadius: R.md, display: 'grid', placeItems: 'center',
            background: C.bgInset, color: C.textSecondary, fontFamily: FONT.mono, fontSize: FS.xs, fontWeight: 700,
          }}
        >
          {KIND_LABEL[m.kind]}
        </span>
        <div style={{ flex: 1, minWidth: 0, display: 'flex', flexDirection: 'column', gap: SP.xxs }}>
          <span style={{ fontSize: FS.md, fontWeight: 600, color: C.textHeading, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>{m.name}</span>
          <span style={{ fontSize: FS.sm, color: C.textMuted, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>
            {KIND_META[m.kind]} · {formatBytes(m.bytes)} · {formatDate(m.uploadedAt)}
          </span>
        </div>
        <IconButton
          tone="danger"
          size={isMobile ? 'lg' : 'md'}
          ariaLabel={`Удалить файл ${m.name}`}
          onClick={() => setConfirmDel(m.id)}
        >
          <Trash2 size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
        </IconButton>
      </div>,
    );
    // Превью — под строкой загруженного материала (§9): «так репетитор прочтёт эту страницу».
    if (previewHere?.id === m.id) {
      rows.push(
        <div key={`${m.id}-preview`} data-testid="material-preview" style={{ padding: `${SP.sm}px ${SP.md}px`, background: C.bgPanel, ...divider() }}>
          <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: SP.sm }}>
            <span style={{ fontSize: FS.sm, fontWeight: 600, color: C.textSecondary }}>Так репетитор прочтёт эту страницу</span>
            <Button
              variant="ghost"
              size={isMobile ? 'md' : 'sm'}
              dataTestId="preview-toggle"
              leftIcon={previewOpen ? <ChevronUp size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} /> : <ChevronDown size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />}
              onClick={() => setPreviewOpen((o) => !o)}
            >
              {previewOpen ? 'Свернуть' : 'Развернуть'}
            </Button>
          </div>
          {previewOpen && (
            <pre data-testid="material-preview-text" style={{ margin: `${SP.xs}px 0 0`, whiteSpace: 'pre-wrap', wordBreak: 'break-word', fontFamily: FONT.sans, fontSize: FS.sm, color: C.textPrimary, lineHeight: 1.45 }}>
              {previewHere.text}
            </pre>
          )}
        </div>,
      );
    }
  }
  if (busyHere?.kind === 'upload') {
    // Строка «идёт» для картинки держит «Распознаём страницу…» — OCR живёт до 3 минут
    // (потолок клиента 200 с, api.ts:236-243); для текста — «достаю текст».
    rows.push(
      <div key="__upload" data-testid="material-uploading" aria-live="polite" style={{ ...rowBase, ...divider() }}>
        <span
          aria-hidden
          style={{
            width: 28, height: 28, flexShrink: 0, borderRadius: R.md, display: 'grid', placeItems: 'center',
            background: C.bgInset, color: C.textSecondary, fontFamily: FONT.mono, fontSize: FS.xs, fontWeight: 700,
          }}
        >
          …
        </span>
        <div style={{ flex: 1, minWidth: 0, display: 'flex', flexDirection: 'column', gap: SP.xxs }}>
          <span style={{ fontSize: FS.md, fontWeight: 600, color: C.textSecondary, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>{busyHere.name}</span>
          <span style={{ fontSize: FS.sm, color: C.textMuted }}>
            {busyHere.isImage ? 'Распознаём страницу…' : 'Загружаю и достаю текст…'}
          </span>
        </div>
      </div>,
    );
  }

  const uploading = busyHere?.kind === 'upload';

  return (
    <div data-testid="child-subject-materials" style={{ display: 'flex', flexDirection: 'column', gap: SP.sm }}>
      <div style={{ display: 'flex', alignItems: 'baseline', justifyContent: 'space-between', gap: SP.sm, flexWrap: 'wrap' }}>
        <span style={{ fontSize: FS.sm, fontWeight: 600, color: C.textSecondary }}>Файлы предмета</span>
        {list && <span style={{ fontFamily: FONT.mono, fontSize: FS.xs, color: C.textMuted }}>{list.length} {filesWord(list.length)}</span>}
      </div>

      {/* Постоянная подсказка админу (решение Андрея 17.11): защита от готовых ответов — мягкая. */}
      <Notice tone="warning" icon={AlertTriangle} data-testid="materials-warning">
        Не загружайте решебники и файлы с готовыми ответами — репетитор может их пересказать.
      </Notice>

      {listFailedHere && (
        <Notice tone="danger" icon={AlertTriangle} title="Не удалось прочитать список файлов" data-testid="materials-list-failed">
          {listFailedHere}
        </Notice>
      )}

      {list && (list.length || busyHere?.kind === 'upload') ? (
        <div data-testid="materials-list" style={{ border: `1px solid ${C.borderLight}`, borderRadius: R.xl, background: C.bgWhite, overflow: 'hidden' }}>
          {rows}
        </div>
      ) : list ? (
        <div data-testid="materials-empty" style={{ border: `1.5px dashed ${C.dashed}`, borderRadius: R.xl }}>
          <EmptyState
            compact
            inline
            icon={<FileText size={ICON_SIZE.xl} strokeWidth={ICON_STROKE} color={C.textMuted} />}
            title="Файлов пока нет"
            subtitle="Положи сюда учебник или задания — репетитор будет брать из них правила и похожие примеры."
          />
        </div>
      ) : null}

      {rejectHere && (
        <Notice tone="danger" icon={AlertTriangle} title="Файл не принят" data-testid="material-reject">
          {rejectHere}
        </Notice>
      )}

      <input ref={fileRef} type="file" accept={ACCEPT} data-testid="material-file-input" style={{ display: 'none' }} onChange={onPick} />
      <Button
        variant="dashed"
        size="md"
        fullWidth={isMobile}
        dataTestId="material-upload"
        leftIcon={<Upload size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />}
        loading={uploading}
        onClick={() => fileRef.current?.click()}
      >
        {uploading ? 'Загружаю…' : 'Загрузить файл'}
      </Button>
      <span style={{ fontSize: FS.sm, color: C.textMuted }}>
        PDF, TXT, MD, DOCX или фото страницы (PNG, JPG, WEBP), до {MAX_UPLOAD_MB} МБ.
      </span>
    </div>
  );
}
