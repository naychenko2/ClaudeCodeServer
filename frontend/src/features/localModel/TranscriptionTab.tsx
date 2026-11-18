// Вкладка «Встречи»: верхняя часть (Ф3) — баннер, конвейер, «Сейчас», очередь.
// Макет: docs/mockups/local-model-transcription-v1.html (состояние «Идёт пачка»),
// тексты и смысл — docs/mockups/local-model-transcription-proposal.md.
// Тексты и флаги считает transcriptionFormat (чистые функции, тесты без DOM); здесь —
// только отрисовка вида-модели токенами дизайн-системы. Нижняя часть (сбои, встречи,
// сверка, зависимости) — шаг Ф4.
//
// Порядок блоков — по убыванию срочности: тревога → состояние → сейчас → очередь.
// В норме баннера нет. При «Завис?»/«Остановлен» значения «Сейчас» и «Очереди» гаснут
// до C.textMuted, «идёт» краснеет (макет .stale).
//
// Снимка ещё нет (первый опрос в пути или сеть не отвечает) — скелеты C.bgInset вместо
// значений, без нулей и без выдуманных статусов (макет «Загрузка»).

import { useState } from 'react';
import type { ReactNode } from 'react';
import { AudioLines, Clock, TriangleAlert } from 'lucide-react';
import { C, FONT, FS, R, SP } from '../../lib/design';
import { Badge } from '../../components/ui/Badge';
import { Notice } from '../../components/ui/Notice';
import { PressableArea } from '../../components/ui/PressableArea';
import { transTabView, type TransTabView } from './transcriptionFormat';
import type { TranscriptionSnapshot } from './types';

// === Оболочка секции: та же карточка, что у соседних вкладок панели (MediaTab/TextTab):
// C.bgWhite, рамка C.borderLight, R.xl, SP.md; заголовок 11/700/uppercase ===
function Card({ title, right, children }: { title: string; right?: ReactNode; children?: ReactNode }) {
  return (
    <section style={{ background: C.bgWhite, border: `1px solid ${C.borderLight}`, borderRadius: R.xl, padding: SP.md }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm, marginBottom: SP.sm }}>
        <h3 style={{
          margin: 0, fontSize: FS.xs, fontWeight: 700, textTransform: 'uppercase',
          letterSpacing: '0.06em', color: C.textSecondary, flex: 1, minWidth: 0,
          whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis',
        }}>{title}</h3>
        {right}
      </div>
      {children}
    </section>
  );
}

// kv-строка: подпись C.textSecondary слева, mono-значение справа (макет .kv).
// alarm — красный (красное «Обновлено» при «Завис?»/«Остановлен»), warn — жёлтый
// («Пишет в» не туда). Карточка конвейера при гашении НЕ тускнеет: она текущая
// (в макете .stale гасит значения «Сейчас» и «Очереди», не kv-строки)
function KvRow({ label, value, tone }: {
  label: string; value: ReactNode; tone?: 'alarm' | 'warn';
}) {
  const color = tone === 'alarm' ? C.dangerText : tone === 'warn' ? C.warningText : C.textPrimary;
  return (
    <div style={{ display: 'flex', justifyContent: 'space-between', gap: SP.sm, fontSize: FS.sm, padding: `${SP.xxs}px 0`, minWidth: 0 }}>
      <span style={{ color: C.textSecondary, whiteSpace: 'nowrap', flex: 'none' }}>{label}</span>
      <span style={{ fontFamily: FONT.mono, fontWeight: 500, color, textAlign: 'right', minWidth: 0, overflowWrap: 'anywhere' }}>{value}</span>
    </div>
  );
}

// Плашка папки: C.infoBg / C.info, моно 10 px (макет .p-fold)
function FolderBadge({ folder, muted }: { folder: string; muted?: boolean }) {
  return (
    <Badge tone="info" size="xs" style={{ fontFamily: FONT.mono, fontSize: FS.xxs, ...(muted ? { color: C.textMuted } : {}) }}>
      {folder}
    </Badge>
  );
}

// === Конвейер ===
function PipelineCard({ view }: { view: TransTabView }) {
  const p = view.pipeline;
  // «Ожидание» — по предложению: C.bgInset / C.success. У Badge такой пары нет
  // (success-тон — successBg/successText); берём neutral-подложку + цвет success.
  const badge = p.badge.tone === 'success'
    ? <Badge tone="neutral" size="xs" style={{ color: C.success }}>{p.badge.label}</Badge>
    : <Badge tone={p.badge.tone} size="xs">{p.badge.label}</Badge>;
  return (
    <Card title="Конвейер" right={badge}>
      <KvRow label="Обновлено" value={p.updatedText} tone={p.updatedAlarm ? 'alarm' : undefined} />
      <KvRow
        label="Где работает"
        value={<>
          {p.whereText} · {p.pidText ?? <span style={{ fontStyle: 'italic', fontWeight: 400, color: C.textMuted }}>нет процесса</span>}
        </>}
      />
      <KvRow label="Пишет в" value={p.writesText} tone={p.writesWarn ? 'warn' : undefined} />
    </Card>
  );
}

// === Сейчас ===
function NowCard({ view }: { view: TransTabView }) {
  const n = view.now;
  const muted = view.stale;
  if (n.kind === 'empty') {
    return (
      <Card title="Сейчас">
        <div style={{ fontSize: FS.sm, color: C.textSecondary }}>
          <span style={{ color: C.textHeading, fontWeight: 600, display: 'block' }}>Ничего не обрабатывается</span>
          {n.note}
        </div>
      </Card>
    );
  }
  return (
    <Card
      title="Сейчас"
      right={n.batchText ? <span style={{ fontSize: FS.xs, color: C.textMuted, whiteSpace: 'nowrap' }}>{n.batchText}</span> : undefined}
    >
      {/* Файл + папка */}
      <div style={{ display: 'flex', alignItems: 'center', gap: SP.xs, minWidth: 0 }}>
        <AudioLines size={14} strokeWidth={2} color={C.textSecondary} aria-hidden style={{ flex: 'none' }} />
        <span
          title={n.file}
          style={{
            fontFamily: FONT.mono, fontSize: FS.sm, fontWeight: 600,
            color: muted ? C.textMuted : C.textHeading,
            overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', minWidth: 0, flex: 1,
          }}
        >{n.file}</span>
        {n.folder && <FolderBadge folder={n.folder} muted={muted} />}
      </div>
      {/* Полоса этапов: пройден C.success, текущий C.accent, впереди C.track;
          при гашении текущий — C.track/C.textMuted (макет .stale .step.cur) */}
      <div role="list" aria-label="Этап" style={{ display: 'flex', gap: SP.xs, margin: `${SP.sm}px 0 ${SP.xs}px` }}>
        {n.steps.map((st, i) => {
          const bar = st.state === 'done' ? C.success : st.state === 'cur' && !muted ? C.accent : C.track;
          const text = st.state === 'done' ? C.textSecondary : st.state === 'cur' && !muted ? C.accent : C.textMuted;
          return (
            <div key={i} role="listitem" style={{ flex: 1, minWidth: 0, fontSize: FS.xs, color: muted ? C.textMuted : text, fontWeight: st.state === 'cur' && !muted ? 600 : 400 }}>
              <span style={{ display: 'block', height: 4, borderRadius: R.max, background: bar, marginBottom: SP.xs }} />
              <span style={{ display: 'block', whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>{st.label}</span>
            </div>
          );
        })}
      </div>
      {/* Факты: «идёт» краснеет при «Завис?»/«Остановлен» (макет runLv) */}
      <div style={{ display: 'flex', flexWrap: 'wrap', gap: `${SP.xxs}px ${SP.md}px`, fontSize: FS.xs, color: C.textSecondary }}>
        {n.runningText != null && (
          <span>идёт <b style={{ fontFamily: FONT.mono, fontWeight: 600, color: muted ? C.dangerText : C.textHeading }}>{n.runningText}</b></span>
        )}
      </div>
    </Card>
  );
}

// === Очередь: плитки (C.bgWhite, рамка C.borderLight, R.lg, SP.sm; значение mono 20 px)
// и раскрывающийся список файлов. HwTile здесь не подошёл: его view-модель несёт unit
// (°C/%), мини-график и подпись датчика — у счётчиков очереди ничего из этого нет;
// плитка повторяет .tile макета теми же токенами, что HwTile. ===
function QueueCard({ view }: { view: TransTabView }) {
  const q = view.queue;
  const muted = view.stale;
  const [open, setOpen] = useState(false);
  const tile = (label: string, value: number, note: string) => (
    <div style={{ border: `1px solid ${C.borderLight}`, background: C.bgWhite, borderRadius: R.lg, padding: SP.sm, minWidth: 0 }}>
      <div style={{ fontSize: FS.xs, color: C.textSecondary, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>{label}</div>
      <div style={{ fontFamily: FONT.mono, fontSize: 20, fontWeight: 600, lineHeight: 1.2, color: muted ? C.textMuted : C.textHeading }}>{value}</div>
      <div style={{ fontSize: FS.xs, color: C.textMuted, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>{note}</div>
    </div>
  );
  return (
    <Card title="Очередь">
      <div style={{ display: 'grid', gridTemplateColumns: 'repeat(2, minmax(0, 1fr))', gap: SP.sm }}>
        {tile('Ждут распознавания', q.asrCount, q.asrNote)}
        {tile('Ждут сводку', q.sumCount, q.sumNote)}
      </div>
      {q.files.length > 0 && (
        <>
          <PressableArea
            onPress={() => setOpen(o => !o)}
            feedback
            aria-expanded={open}
            style={{ display: 'flex', alignItems: 'center', minHeight: 32, fontSize: FS.sm, color: C.textSecondary, marginTop: SP.xs }}
          >
            Что в очереди · {q.files.length}
          </PressableArea>
          {open && q.files.map((f, i) => (
            <div key={i} style={{ display: 'flex', gap: SP.xs, alignItems: 'center', padding: `${SP.xs}px 0`, minWidth: 0, borderTop: `1px solid ${C.borderLight}` }}>
              <Clock size={12} strokeWidth={2} color={C.textMuted} aria-hidden style={{ flex: 'none' }} />
              <span title={f.file} style={{ fontFamily: FONT.mono, fontSize: FS.xs, color: muted ? C.textMuted : C.textPrimary, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', flex: 1, minWidth: 0 }}>{f.file}</span>
              <span style={{ fontSize: FS.xs, color: C.textMuted, whiteSpace: 'nowrap' }}>{f.waitText}</span>
            </div>
          ))}
        </>
      )}
    </Card>
  );
}

// === Скелеты: ждём первый снимок (C.bgInset, без нулей и выдуманных статусов) ===
function SkeletonCard({ title }: { title: string }) {
  const bar = (w: string) => <div style={{ background: C.bgInset, borderRadius: R.sm, height: 12, margin: `${SP.sm}px 0`, width: w }} />;
  return (
    <section aria-busy="true" style={{ background: C.bgWhite, border: `1px solid ${C.borderLight}`, borderRadius: R.xl, padding: SP.md }}>
      <h3 style={{ margin: 0, fontSize: FS.xs, fontWeight: 700, textTransform: 'uppercase', letterSpacing: '0.06em', color: C.textSecondary }}>{title}</h3>
      {bar('70%')}
      {bar('45%')}
    </section>
  );
}

// data — последний снимок (null до первого успеха); loading здесь не нужен: между
// попытками опроса поллера он false, а данные всё ещё не пришли — ожидание одно.
// now — «сейчас» для относительных времён («обновлено N назад»); по умолчанию текущий
// момент (пересбор при каждом опросе), в тесте — фиксированный.
export function TranscriptionTab({ data, now = Date.now() }: { data: TranscriptionSnapshot | null; now?: number }) {
  // Снимка нет — состояние ожидания (поллер опрашивает каждые 5 с): скелеты вместо
  // значений; нулей и выдуманных статусов нет.
  if (data == null) {
    return (
      <>
        <SkeletonCard title="Конвейер" />
        <SkeletonCard title="Сейчас" />
        <SkeletonCard title="Очередь" />
      </>
    );
  }
  const view = transTabView(data, now);
  return (
    <>
      {view.banner && (
        <Notice tone={view.banner.tone} icon={TriangleAlert} title={view.banner.title}>
          {view.banner.body}
        </Notice>
      )}
      <PipelineCard view={view} />
      <NowCard view={view} />
      <QueueCard view={view} />
    </>
  );
}
