// Вкладка «Встречи»: верхняя часть (Ф3) — баннер, конвейер, «Сейчас», очередь;
// нижняя (Ф4) — сбои за сутки, последние встречи, сверка с Dify, зависимости.
// Макет: docs/mockups/local-model-transcription-v1.html (состояние «Идёт пачка»),
// тексты и смысл — docs/mockups/local-model-transcription-proposal.md.
// Тексты и флаги считает transcriptionFormat (чистые функции, тесты без DOM); здесь —
// только отрисовка вида-модели токенами дизайн-системы.
//
// Порядок блоков — по убыванию срочности: тревога → состояние → сейчас → очередь →
// сбои → встречи → сверка → зависимости (макет tab()).
// В норме баннера нет. При «Завис?»/«Остановлен» значения «Сейчас» и «Очереди» гаснут
// до C.textMuted, «идёт» краснеет (макет .stale).
//
// Снимка ещё нет (первый опрос в пути или сеть не отвечает) — скелеты C.bgInset вместо
// значений, без нулей и без выдуманных статусов (макет «Загрузка»).

import { useState } from 'react';
import type { CSSProperties, ReactNode } from 'react';
import { AudioLines, Clock, FileText, TriangleAlert } from 'lucide-react';
import { C, FONT, FS, R, SP } from '../../lib/design';
import { useNow } from '../../lib/useNow';
import { Badge } from '../../components/ui/Badge';
import { Dot } from '../../components/ui/Dot';
import { Notice } from '../../components/ui/Notice';
import { PressableArea } from '../../components/ui/PressableArea';
import { DEP_WORDS, transTabView, type TransDepRowView, type TransDifyRowView, type TransMeetingRowView, type TransTabView } from './transcriptionFormat';
import { openMeetingNote } from './openMeetingNote';
import { TRANSCRIPTION_POLL_MS } from './useTranscription';
import type { TranscriptionSnapshot } from './types';

// «Последние встречи»: изначально пять строк, «Показать ещё» раскрывает остальные
// (полный список — не больше 20, лимит приходит с бэкенда, на фронте не режем)
const MEETINGS_INITIAL = 5;

// === Оболочка секции: та же карточка, что у соседних вкладок панели (MediaTab/TextTab):
// C.bgWhite, рамка C.borderLight, R.xl, SP.md; заголовок 11/700/uppercase.
// tone="danger" — карточка сбоев макета (.card.danger): C.dangerBg / C.dangerBorder,
// заголовок C.dangerText ===
function Card({ title, right, tone, children }: {
  title: string; right?: ReactNode; tone?: 'danger'; children?: ReactNode;
}) {
  const danger = tone === 'danger';
  return (
    <section style={{
      background: danger ? C.dangerBg : C.bgWhite,
      border: `1px solid ${danger ? C.dangerBorder : C.borderLight}`,
      borderRadius: R.xl, padding: SP.md,
    }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm, marginBottom: SP.sm }}>
        <h3 style={{
          margin: 0, fontSize: FS.xs, fontWeight: 700, textTransform: 'uppercase',
          letterSpacing: '0.06em', color: danger ? C.dangerText : C.textSecondary, flex: 1, minWidth: 0,
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
      {/* Факты (макет .facts): «идёт» краснеет при «Завис?»/«Остановлен» (runLv); «запись» —
          длина записи, значения в контракте нет → «—» (не 0 и не выдуманное число) */}
      <div style={{ display: 'flex', flexWrap: 'wrap', gap: `${SP.xxs}px ${SP.md}px`, fontSize: FS.xs, color: C.textSecondary }}>
        {n.runningText != null && (
          <span>идёт <b style={{ fontFamily: FONT.mono, fontWeight: 600, color: muted ? C.dangerText : C.textHeading }}>{n.runningText}</b></span>
        )}
        <span>запись <b style={{ fontFamily: FONT.mono, fontWeight: 600, color: muted ? C.textMuted : C.textHeading }}>{n.lengthText}</b></span>
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

// === Строка встречи/сбоя (макет .mt): точка → имя → время; ниже — папка, длина,
// спикеры, «.md»; комментарий — своей строкой. Касание открывает сводку только у
// строки с mdPath (openMeetingNote — единственная точка входа, см. openMeetingNote.ts);
// строка сбоя не открывается вовсе (макет .nolink), комментарий её — целиком и красный ===
function MeetingRow({ row, alarm, top }: { row: TransMeetingRowView; alarm?: boolean; top?: boolean }) {
  const grid: CSSProperties = {
    display: 'grid', gridTemplateColumns: 'auto minmax(0,1fr) auto', gap: `${SP.xxs}px ${SP.sm}px`,
    alignItems: 'center', padding: SP.sm, margin: `0 -${SP.sm}px`, borderRadius: R.md,
    minHeight: 44, minWidth: 0,
    ...(top ? { borderTop: `1px solid ${C.borderLight}`, borderRadius: `0 0 ${R.md}px ${R.md}px` } : {}),
  };
  const body = (
    <>
      <Dot color={alarm ? C.danger : C.success} size={8} />
      {/* Полное имя (с расширением) — в title: на узкой ленте имя режется многоточием */}
      <span title={row.file} style={{
        fontSize: FS.sm, fontWeight: 600, color: C.textHeading,
        overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', minWidth: 0,
      }}>{row.name}</span>
      <span style={{ fontFamily: FONT.mono, fontSize: FS.xs, color: C.textMuted, whiteSpace: 'nowrap' }}>{row.timeText}</span>
      <span style={{ gridColumn: '2 / span 2', display: 'flex', flexWrap: 'wrap', gap: `${SP.xxs}px ${SP.sm}px`, alignItems: 'center', fontSize: FS.xs, color: C.textSecondary, minWidth: 0 }}>
        {row.folder && <FolderBadge folder={row.folder} />}
        {row.lengthText != null && <span>{row.lengthText}</span>}
        {row.speakersText != null && <span>{row.speakersText}</span>}
        {row.openable && (
          <span style={{ display: 'inline-flex', alignItems: 'center', gap: SP.xs, color: C.accent, fontWeight: 600 }}>
            <FileText size={12} strokeWidth={2} aria-hidden />.md
          </span>
        )}
      </span>
      {row.comment != null && (
        <span style={{
          gridColumn: '2 / span 2', fontSize: FS.xs, minWidth: 0,
          ...(alarm
            ? { color: C.dangerText, whiteSpace: 'normal', overflowWrap: 'anywhere' } // комментарий журнала — целиком
            : { color: C.textMuted, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }),
        }}>{row.comment}</span>
      )}
    </>
  );
  if (!row.openable) return <div style={grid}>{body}</div>;
  return (
    <PressableArea
      onPress={() => openMeetingNote(row.id)}
      aria-label={`Открыть сводку: ${row.name}`}
      feedback
      style={grid}
    >{body}</PressableArea>
  );
}

// === Сбои за сутки: красная карточка, видна только когда сбои есть (их не больше 20 —
// лимит держит бэк); заголовок и рамка — danger-токены (макет .card.danger) ===
function FailuresCard({ view }: { view: TransTabView }) {
  if (view.failures.length === 0) return null;
  return (
    <Card title={`Сбои за сутки · ${view.failures.length}`} tone="danger">
      {view.failures.map((r, i) => <MeetingRow key={r.id} row={r} alarm top={i > 0} />)}
    </Card>
  );
}

// === Последние встречи: пять строк и «Показать ещё» (раскрывает остальные ≤20);
// пустого состояния нет: встреч за сутки не было — блока нет (тот же порядок, что у
// «Сбоев»: список существует, только когда есть что показать) ===
function RecentCard({ view }: { view: TransTabView }) {
  const [showAll, setShowAll] = useState(false);
  const rows = view.meetings;
  if (rows.length === 0) return null;
  const shown = showAll ? rows : rows.slice(0, MEETINGS_INITIAL);
  return (
    <Card title="Последние встречи" right={<span style={{ fontSize: FS.xs, color: C.textMuted, whiteSpace: 'nowrap' }}>касание — открыть сводку</span>}>
      {shown.map((r, i) => <MeetingRow key={r.id} row={r} top={i > 0} />)}
      {!showAll && rows.length > MEETINGS_INITIAL && (
        <PressableArea
          onPress={() => setShowAll(true)}
          feedback
          style={{ display: 'flex', alignItems: 'center', minHeight: 32, fontSize: FS.sm, color: C.accent, fontWeight: 600, marginTop: SP.xs }}
        >
          Показать ещё
        </PressableArea>
      )}
    </Card>
  );
}

// === Сверка с Dify: папка / «Локально» / «В Dify» / плашка «сходится» или «не хватает N».
// Строки без inDify нет вовсе (дефицит из неизвестного не считается), и карточки нет,
// если строк нет: до первой проверки блока на экране нет (свежесть — «N назад · раз в
// 30 мин» по checkedAt). Числа заходят в колонки 56/56 px, плашка — 96 (макет .tbl) ===
function DifyCard({ view }: { view: TransTabView }) {
  if (view.difyRows.length === 0) return null;
  const pill = (r: TransDifyRowView) => r.deficit == null
    // «сходится» — та же пара, что «Ожидание» у конвейера: neutral-подложка + цвет success
    ? (r.local === r.inDify ? <Badge tone="neutral" size="xs" style={{ color: C.success }}>сходится</Badge> : null)
    : <Badge tone="warning" size="xs" title={`В Dify не хватает ${r.deficit}`}>не хватает {r.deficit}</Badge>;
  return (
    <Card title="Сверка с Dify" right={view.difyAgeText ? <span style={{ fontSize: FS.xs, color: C.textMuted, whiteSpace: 'nowrap' }}>{view.difyAgeText} · раз в 30 мин</span> : undefined}>
      <table style={{ width: '100%', borderCollapse: 'collapse', fontSize: FS.sm, tableLayout: 'fixed' }}>
        <colgroup>
          <col /><col style={{ width: 56 }} /><col style={{ width: 56 }} /><col style={{ width: 96 }} />
        </colgroup>
        <thead>
          <tr>
            {['Папка', 'Локально', 'В Dify', ''].map((h, i) => (
              <th key={i} style={{
                fontSize: FS.xxs, fontWeight: 600, color: C.textMuted, padding: `0 0 ${SP.xs}px`,
                textAlign: i === 0 ? 'left' : 'right',
              }}>{h}</th>
            ))}
          </tr>
        </thead>
        <tbody>
          {view.difyRows.map((r) => (
            <tr key={r.folder}>
              <td title={r.folder} style={{ padding: `${SP.xs}px 0`, borderTop: `1px solid ${C.borderLight}`, textAlign: 'left', fontSize: FS.xs, color: C.textPrimary, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{r.folder}</td>
              <td style={{ padding: `${SP.xs}px 0`, borderTop: `1px solid ${C.borderLight}`, textAlign: 'right', fontFamily: FONT.mono, color: C.textPrimary }}>{r.local}</td>
              <td style={{ padding: `${SP.xs}px 0`, borderTop: `1px solid ${C.borderLight}`, textAlign: 'right', fontFamily: FONT.mono, color: C.textPrimary }}>{r.inDify}</td>
              <td style={{ padding: `${SP.xs}px 0`, borderTop: `1px solid ${C.borderLight}`, textAlign: 'right' }}>{pill(r)}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </Card>
  );
}

// === Зависимости: точка и слово по каждому пункту. Цвета — по состоянию (макет .dep):
// «в норме» — точка C.success, слово C.textSecondary; «медленно» — C.warning/C.warningText;
// «не отвечает» — C.danger/C.dangerText; «не проверялось» — C.track/C.textMuted ===
const DEP_TONE: Record<TransDepRowView['state'], { dot: string; word: string }> = {
  ok: { dot: C.success, word: C.textSecondary },
  slow: { dot: C.warning, word: C.warningText },
  down: { dot: C.danger, word: C.dangerText },
  unknown: { dot: C.track, word: C.textMuted },
};
function DepsCard({ view }: { view: TransTabView }) {
  if (view.deps.length === 0) return null;
  return (
    <Card title="Зависимости">
      {view.deps.map((d, i) => {
        const t = DEP_TONE[d.state];
        return (
          <div key={d.key} style={{ display: 'grid', gridTemplateColumns: 'auto minmax(0,1fr) auto', gap: `0 ${SP.sm}px`, alignItems: 'center', padding: `${SP.xs}px 0`, borderTop: i > 0 ? `1px solid ${C.borderLight}` : undefined, minWidth: 0 }}>
            <Dot color={t.dot} size={8} />
            <span style={{ fontSize: FS.sm, fontWeight: 600, color: C.textHeading, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', minWidth: 0 }}>{d.name}</span>
            <span style={{ fontSize: FS.xs, color: t.word, whiteSpace: 'nowrap' }}>{DEP_WORDS[d.state]}</span>
          </div>
        );
      })}
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
// now — «сейчас» для относительных времён («обновлено N назад»): в тесте приходит явно и
// фиксировано. В приложении его нет — берём живое состояние useNow по такту поллера,
// а не Date.now() в фазе рендера (нечистый рендер по react-hooks/purity; паттерн
// FeatureTimelineTab/TeamCommandCenter). Интервал живёт, только пока есть данные.
export function TranscriptionTab({ data, now }: { data: TranscriptionSnapshot | null; now?: number }) {
  const tick = useNow(TRANSCRIPTION_POLL_MS, data != null);
  // Снимка нет — состояние ожидания (поллер опрашивает каждые 5 с): скелеты вместо
  // значений; нулей и выдуманных статусов нет.
  if (data == null) {
    return (
      <>
        <SkeletonCard title="Конвейер" />
        <SkeletonCard title="Сейчас" />
        <SkeletonCard title="Очередь" />
        <SkeletonCard title="Последние встречи" />
      </>
    );
  }
  const view = transTabView(data, now ?? tick);
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
      <FailuresCard view={view} />
      <RecentCard view={view} />
      <DifyCard view={view} />
      <DepsCard view={view} />
    </>
  );
}
