// Панель «История решений» (change-dossiers, этап 1): у файла — записи «зачем
// менялся код, что решили, что отвергли, какие грабли», рождающиеся из чата/задачи
// при коммите. Того же класса, что «Документация»/«Чтение» — рельса, drag-and-drop,
// сплиттер, мобильная шторка достаются бесплатно от PanelZone; здесь только тело.
//
// Источники: макет docs/mockups/decision-history-v1.html + предложение
// docs/mockups/decision-history-proposal.md (Майя), тексты — заметка «Тексты —
// Паспорта изменений (UI, empty-state, «Что нового»)», контракт — ADR-004 §4/§6/§8.
// Бэкенд (GET /api/projects/{id}/dossiers) — Денис; до его готовности панель строит
// запросы по контракту и просто получает пустой список/ошибку сети.
//
// Этап 3 (ADR §6): кнопка «Выгрузить в репозиторий» и модалка подтверждения.
// Этап 4: кнопка-зеркало «Загрузить из репозитория» (Download слева от Upload),
// модалка импорта, бейдж происхождения в метастроке импортированной записи,
// заголовок группы «Две записи об одном коммите» между своей и импортированной
// парой. Оба действия видны при включённом флаге change-dossiers-recall И когда
// проект — git-репозиторий. Тумблер opt-out DossierOptOutButton живёт в шапке
// чата и сюда не ходит.

import { useCallback, useEffect, useMemo, useState, type CSSProperties, type ReactNode } from 'react';
import {
  AlertTriangle, Bot, ChevronDown, ChevronRight, ClipboardList, Download, File as FileIcon, GitCompare, History, Info,
  Lightbulb, MessageCircle, Search, Upload, X,
} from 'lucide-react';
import type { DossierEntry, Persona } from '../../types';
import { displayNameOf, type AuthState } from '../../types';
import { api } from '../../lib/api';
import { basename } from '../../lib/paths';
import { C, FONT, FS, R, SP } from '../../lib/design';
import { FLAGS, useFeature } from '../../lib/featureFlags';
import { ICON_STROKE } from '../../components/ui/icons';
import { Button, Dot, EmptyState, IconButton, TextField } from '../../components/ui';
import { PanelHeaderSlot, useHasPanelHeader } from '../../components/ui';
import { useNow } from '../../lib/useNow';
import { DossierExportDialog } from './DossierExportDialog';
import { DossierImportDialog } from './DossierImportDialog';

interface Props {
  project: { id: string };
  auth: AuthState;
  // Файл, открытый в центре сейчас — панель фильтрует по нему, пока пользователь
  // не снимет фильтр сам (крестик на чипе)
  activeFilePath?: string | null;
  // Текущий чат помечен «не сохранять решения» (opt-out): панель, открытая из него,
  // показывает нейтральную заметку-объяснение, почему записей нет / не будет
  chatExcludedFromDossiers?: boolean;
  onOpenChat: (sessionId: string) => void;
  onOpenTask: (taskId: string) => void;
  onOpenCommit: (sha: string, filePath?: string) => void;
}

// Записи не старше этого срока показываются полными карточками; более старые —
// свёрнуты в группы по месяцам. Тот же порог, что у ранжирования active/degraded
// в ADR §5 — panel заимствует его для группировки, не изобретая свой
const RECENT_DAYS = 30;
// Выравнивающий отступ контента карточки под аватаром: ширина аватара (26) + gap
// card-top (8). Не из шкалы SP — это расчётное выравнивание, как marginTop аватара.
const AVATAR_INDENT = 34;

// Тексты панели — в одном месте файла. Подсказка автовыгрузки появляется только
// при активной автовыгрузке (тот же признак, что у кнопок выгрузки): флаг
// change-dossiers-recall включён и проект — git-репозиторий. Содержательно
// объясняет, почему конспекты обсуждений не приезжают сами — после правки
// «Автовыгрузка не должна молча тратить модель на конспекты» фон везёт только
// записи, а конспекты снимаются явной командой человека.
const T = {
  autoExportHint: 'Решения выгружаются в ветку сами; конспекты обсуждений снимаются по кнопке «Выгрузить»',
} as const;

function monthKey(iso: string): string {
  const d = new Date(iso);
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}`;
}
function monthLabel(iso: string): string {
  const s = new Date(iso).toLocaleDateString('ru-RU', { month: 'long', year: 'numeric' });
  return s.charAt(0).toUpperCase() + s.slice(1);
}
function formatDate(iso: string): string {
  return new Date(iso).toLocaleDateString('ru-RU', { day: 'numeric', month: 'long', year: 'numeric' });
}
// Только время (HH:mm): «снято 14:32» под метастрокой own-записи. У импортированных
// capturedAt == null — строку не рисуем, форматтер тут ни при чём.
function formatTime(iso: string): string {
  return new Date(iso).toLocaleTimeString('ru-RU', { hour: '2-digit', minute: '2-digit' });
}
function initials(name: string): string {
  const parts = name.trim().split(/\s+/).filter(Boolean);
  if (parts.length === 0) return '?';
  if (parts.length === 1) return parts[0].slice(0, 2).toUpperCase();
  return (parts[0][0] + parts[1][0]).toUpperCase();
}
function recordWord(n: number): string {
  const m10 = n % 10, m100 = n % 100;
  if (m10 === 1 && m100 !== 11) return 'запись';
  if (m10 >= 2 && m10 <= 4 && (m100 < 10 || m100 >= 20)) return 'записи';
  return 'записей';
}

type Author = { name: string; persona: boolean };

// Бейдж происхождения записи (этап 4, макет §1/§2): показывается только у
// импортированных — своя запись остаётся чистой (90% списка, лишняя пилюля
// превращает ленту в шум). Гамма info — нейтральная атрибуция, не статус ошибки.
function OriginBadge({ author }: { author: string | null }) {
  const label = author ? `Из репозитория · ${author}` : 'Из репозитория';
  const tooltip = author
    ? `Запись приехала из ветки ccs/dossiers/v1 — её выгрузил ${author}.`
    : 'Запись приехала из ветки ccs/dossiers/v1.';
  return (
    <span
      title={tooltip}
      style={{
        display: 'inline-flex', alignItems: 'center', gap: 4, flexShrink: 0,
        fontFamily: FONT.sans, fontSize: FS.xs, fontWeight: 600, lineHeight: 1,
        color: C.info, background: C.infoBg,
        borderRadius: R.max, padding: `${SP.xxs}px ${SP.xs}px ${SP.xxs}px 5px`,
      }}
    >
      <Download size={11} strokeWidth={2} />
      {label}
    </span>
  );
}

// Компактная иконка для свёрнутой месячной группы — пилюля не помещается в одну
// строку, остаётся значок с title (макет §2, последний sample).
function OriginIcon() {
  return (
    <span
      title="Из репозитория"
      style={{ display: 'inline-flex', alignItems: 'center', flexShrink: 0, color: C.info }}
    >
      <Download size={11} strokeWidth={2} />
    </span>
  );
}

// Вторая в паре по одному коммиту — заголовок группы по текстам §2.4:
// «Две записи об одном коммите / Ваша запись сохранена как есть — загрузка ничего
// не перезаписывает. Рядом — как тот же коммит описали в репозитории.»
function PairHeader() {
  return (
    <div style={{
      margin: `${SP.xs}px 0 ${SP.xs}px ${AVATAR_INDENT}px`,
      padding: `${SP.xs}px ${SP.sm}px`,
      background: C.bgSelected, borderRadius: R.md,
      display: 'flex', flexDirection: 'column', gap: 2,
    }}>
      <span style={{ fontSize: FS.xs, fontWeight: 700, color: C.textHeading }}>Две записи об одном коммите</span>
      <span style={{ fontSize: FS.xs, color: C.textSecondary, lineHeight: 1.45 }}>
        Ваша запись сохранена как есть — загрузка ничего не перезаписывает. Рядом — как тот же коммит описали в репозитории.
      </span>
    </div>
  );
}

// Предикат: эта запись — imported, и предыдущая по тому же sha — own. Используется
// в обоих местах рендера (recent и раскрытые месячные группы), чтобы показать
// PairHeader между своей и импортированной записями.
function isSecondInPair(entry: DossierEntry, prev: DossierEntry | null): boolean {
  return entry.origin === 'imported'
    && prev !== null
    && prev.commitSha === entry.commitSha
    && prev.origin === 'own';
}

function Avatar({ author, size = 26 }: { author: Author; size?: number }) {
  if (author.persona) {
    return (
      <div style={{
        width: size, height: size, borderRadius: '50%', flexShrink: 0, marginTop: 1,
        background: C.accentLight, color: C.accent, display: 'flex', alignItems: 'center', justifyContent: 'center',
        boxShadow: `0 0 0 2px ${C.bgCard}, 0 0 0 3px ${C.accentMuted}`,
      }}>
        <Bot size={Math.round(size * 0.5)} strokeWidth={2} />
      </div>
    );
  }
  return (
    <div style={{
      width: size, height: size, borderRadius: '50%', flexShrink: 0, marginTop: 1,
      background: C.bgSelected, color: C.textSecondary, display: 'flex', alignItems: 'center', justifyContent: 'center',
      fontSize: FS.xs, fontWeight: 700,
    }}>
      {initials(author.name)}
    </div>
  );
}

function Subsection({ title, items }: { title: string; items: string[] }) {
  return (
    <div style={{ marginBottom: SP.sm }}>
      <p style={{ margin: `0 0 ${SP.xs}px`, fontSize: FS.xs, textTransform: 'uppercase', letterSpacing: '0.03em', fontWeight: 700, color: C.textMuted }}>
        {title}
      </p>
      <ul style={{ margin: 0, paddingLeft: 16 }}>
        {items.map((it, i) => (
          <li key={i} style={{ fontSize: FS.base, color: C.textPrimary, lineHeight: 1.5, marginBottom: SP.xs }}>{it}</li>
        ))}
      </ul>
    </div>
  );
}

const linkBtnStyle: CSSProperties = {
  display: 'inline-flex', alignItems: 'center', gap: SP.xs, fontFamily: FONT.sans, fontSize: FS.sm, cursor: 'pointer',
  border: `1px solid ${C.border}`, background: C.bgWhite, color: C.textPrimary,
  borderRadius: R.max, padding: `${SP.xs}px ${SP.sm}px`,
};

function LinkButton({ icon, label, onClick }: { icon: ReactNode; label: string; onClick: () => void }) {
  return <button onClick={onClick} style={linkBtnStyle}>{icon}{label}</button>;
}

function StaleLink({ label }: { label: string }) {
  return (
    <span style={{ display: 'inline-flex', alignItems: 'center', fontSize: FS.sm, color: C.textMuted, fontStyle: 'italic', padding: `${SP.xs}px ${SP.xs}px` }}>
      {label}
    </span>
  );
}

function DossierCard({ entry, author, open, onToggle, onOpenChat, onOpenTask, onOpenCommit, fallbackFile }: {
  entry: DossierEntry;
  author: Author;
  open: boolean;
  onToggle: () => void;
  onOpenChat: (id: string) => void;
  onOpenTask: (id: string) => void;
  onOpenCommit: (sha: string, filePath?: string) => void;
  fallbackFile: string | null;
}) {
  const hasBody = entry.decisions.length > 0 || entry.rejected.length > 0 || entry.pitfalls.length > 0;
  const previewLine = !open && !entry.summaryFailed ? entry.decisions[0] : null;
  return (
    <div style={{
      background: C.bgCard, border: `1px solid ${C.borderLight}`, borderRadius: R.xl,
      padding: `${SP.sm}px ${SP.md}px`, marginBottom: SP.sm,
      opacity: entry.status === 'degraded' ? 0.92 : 1,
    }}>
      <div onClick={onToggle} style={{ display: 'flex', alignItems: 'flex-start', gap: SP.sm, cursor: 'pointer' }}>
        <Avatar author={author} />
        <div style={{ flex: 1, minWidth: 0 }}>
          <p style={{
            margin: `0 0 ${SP.xs}px`, fontSize: FS.base, lineHeight: 1.4,
            fontWeight: entry.summaryFailed ? 400 : 600,
            fontStyle: entry.summaryFailed ? 'italic' : 'normal',
            color: entry.summaryFailed ? C.textSecondary : C.textHeading,
          }}>
            {entry.summaryFailed ? 'Не удалось собрать описание — сохранён только сам факт изменения.' : entry.why}
          </p>
          <div style={{ display: 'flex', alignItems: 'center', gap: SP.xs, flexWrap: 'wrap', fontSize: FS.xs, color: C.textMuted }}>
            <span style={{ color: author.persona ? C.accent : C.textSecondary, fontWeight: 600 }}>{author.name}</span>
            {author.persona && <span style={{ color: C.textMuted, fontWeight: 400 }}>· персона</span>}
            <Dot color={C.textMuted} size={3} />
            <span style={{ fontFamily: FONT.mono }}>{entry.commitSha.slice(0, 7)}</span>
            <Dot color={C.textMuted} size={3} />
            <span>{formatDate(entry.committedAt)}</span>
            {/* Бейдж происхождения — только у импортированных записей (свои остаются
                чистыми: 90% списка — свои, лишняя пилюля превращает ленту в шум) */}
            {entry.origin === 'imported' && <OriginBadge author={entry.importedAuthor} />}
            {entry.status === 'degraded' && (
              <>
                <Dot color={C.textMuted} size={3} />
                <span style={{ display: 'inline-flex', alignItems: 'center', gap: SP.xs }}>
                  <Info size={11} strokeWidth={2} />
                  Код с тех пор заметно менялся
                </span>
              </>
            )}
          </div>
          {/* Время снятия — только у own-записей (у импортированных capturedAt == null,
              см. ChangeDossier и блок Г спринта): импортированные живут без метки
              времени, и строка для них была бы чужой пустышкой. */}
          {entry.capturedAt && (
            <p style={{ margin: `${SP.xxs}px 0 0`, fontSize: FS.xs, color: C.textMuted, lineHeight: 1.4 }}>
              снято {formatTime(entry.capturedAt)}
            </p>
          )}
          {previewLine && (
            <p style={{
              margin: `${SP.xs}px 0 0`, fontSize: FS.sm, color: C.textSecondary, lineHeight: 1.4,
              overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
            }}>
              Решили: {previewLine}
            </p>
          )}
        </div>
        {!entry.summaryFailed && hasBody && (
          <ChevronDown size={14} strokeWidth={ICON_STROKE} style={{
            color: C.textMuted, marginTop: SP.xs, flexShrink: 0,
            transform: open ? 'rotate(180deg)' : 'none', transition: 'transform .15s ease-out',
          }} />
        )}
      </div>
      {open && hasBody && (
        <div style={{ margin: `${SP.sm}px 0 2px ${AVATAR_INDENT}px` }}>
          {entry.decisions.length > 0 && <Subsection title="Что решили" items={entry.decisions} />}
          {entry.rejected.length > 0 && <Subsection title="Что отвергли и почему" items={entry.rejected} />}
          {entry.pitfalls.length > 0 && <Subsection title="Грабли" items={entry.pitfalls} />}
        </div>
      )}
      <div style={{ display: 'flex', gap: SP.xs, flexWrap: 'wrap', marginTop: open ? SP.sm : SP.xs, marginLeft: AVATAR_INDENT }}>
        {entry.sessionId && (
          entry.linksStale
            ? <StaleLink label="Чат удалён" />
            : <LinkButton icon={<MessageCircle size={12} strokeWidth={ICON_STROKE} />} label="Открыть чат" onClick={() => onOpenChat(entry.sessionId!)} />
        )}
        {entry.taskId && (
          entry.linksStale
            ? <StaleLink label="Задача удалена" />
            : <LinkButton icon={<ClipboardList size={12} strokeWidth={ICON_STROKE} />} label="Открыть задачу" onClick={() => onOpenTask(entry.taskId!)} />
        )}
        <LinkButton
          icon={<GitCompare size={12} strokeWidth={ICON_STROKE} />} label="Показать изменения"
          onClick={() => onOpenCommit(entry.commitSha, fallbackFile ?? entry.files[0])}
        />
      </div>
    </div>
  );
}

function CompactRow({ entry, author, onClick }: { entry: DossierEntry; author: Author; onClick: () => void }) {
  const [hover, setHover] = useState(false);
  return (
    <div
      onClick={onClick}
      onMouseEnter={() => setHover(true)}
      onMouseLeave={() => setHover(false)}
      style={{
        display: 'flex', alignItems: 'center', gap: SP.sm, padding: `${SP.xs}px ${SP.sm}px ${SP.xs}px 30px`, borderRadius: R.md, cursor: 'pointer',
        background: hover ? C.bgSelected : 'transparent',
      }}
    >
      <Avatar author={author} size={16} />
      <span style={{ flex: 1, minWidth: 0, fontSize: FS.sm, color: C.textPrimary, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>
        {entry.summaryFailed ? 'Не удалось собрать описание.' : entry.why}
      </span>
      {/* В компактной строке пилюля не помещается — только значок (макет §2) */}
      {entry.origin === 'imported' && <OriginIcon />}
      <span style={{ flexShrink: 0, fontSize: FS.xs, color: C.textMuted, fontFamily: FONT.mono }}>{entry.commitSha.slice(0, 5)}</span>
    </div>
  );
}

function GroupHeader({ label, count, open, onToggle }: { label: string; count: number; open: boolean; onToggle: () => void }) {
  const [hover, setHover] = useState(false);
  return (
    <div
      onClick={onToggle}
      onMouseEnter={() => setHover(true)}
      onMouseLeave={() => setHover(false)}
      style={{
        display: 'flex', alignItems: 'center', gap: SP.sm, padding: `${SP.sm}px ${SP.sm}px`, marginBottom: SP.xs, borderRadius: R.md, cursor: 'pointer',
        color: C.textSecondary, fontSize: FS.sm, fontWeight: 600, background: hover ? C.bgSelected : 'transparent',
      }}
    >
      <ChevronRight size={12} strokeWidth={2.4} style={{ color: C.textMuted, transform: open ? 'rotate(90deg)' : 'none', transition: 'transform .15s' }} />
      {label} <span style={{ color: C.textMuted, fontWeight: 400 }}>· {count} {recordWord(count)}</span>
    </div>
  );
}

function SkeletonCard() {
  return (
    <div style={{ background: C.bgCard, border: `1px solid ${C.borderLight}`, borderRadius: R.xl, padding: `${SP.sm}px ${SP.md}px`, marginBottom: SP.sm }}>
      <div style={{ display: 'flex', gap: SP.sm, alignItems: 'flex-start' }}>
        <div className="cc-skel" style={{ width: 26, height: 26, borderRadius: '50%', flexShrink: 0 }} />
        <div style={{ flex: 1 }}>
          <div className="cc-skel" style={{ width: '78%', height: 11, borderRadius: 6, marginBottom: SP.sm }} />
          <div className="cc-skel" style={{ width: '40%', height: 9, borderRadius: 6 }} />
        </div>
      </div>
    </div>
  );
}

const chipStyle: CSSProperties = {
  display: 'inline-flex', alignItems: 'center', gap: SP.xs, fontSize: FS.xs, color: C.textHeading,
  background: C.bgCard, border: `1px solid ${C.border}`, borderRadius: R.max, padding: `${SP.xs}px ${SP.sm}px`,
  fontFamily: FONT.mono,
};

export function DossierHistoryPanel({ project, auth, activeFilePath, chatExcludedFromDossiers, onOpenChat, onOpenTask, onOpenCommit }: Props) {

  // Заметка-объяснение для чата-исключения: первым блоком тела панели, спокойным
  // нейтральным тоном (не warning) — это выбранный человеком режим, а не неполадка.
  // Текст — из макета docs/mockups/chat-decisions-optout-v1.html (раздел 4)
  const exclusionNote = chatExcludedFromDossiers ? (
    <div style={{
      margin: `${SP.sm}px ${SP.md}px 0`, padding: `${SP.sm}px`,
      background: C.bgCard, border: `1px solid ${C.borderLight}`, borderRadius: R.lg,
      display: 'flex', gap: SP.sm, alignItems: 'flex-start', flexShrink: 0,
    }}>
      <History size={14} strokeWidth={ICON_STROKE} style={{ color: C.accent, marginTop: 1, flexShrink: 0 }} />
      <span style={{ fontSize: 11.5, color: C.textSecondary, lineHeight: 1.45 }}>
        Решения из этого чата не сохраняются. Изменить можно в шапке чата — кнопка с иконкой истории.
      </span>
    </div>
  ) : null;

  // Фильтр «файл»: идёт за открытым в центре, пока пользователь не снимет его сам
  // крестиком — тогда список показывает весь проект, пока не откроют другой файл.
  // Подстройка состояния под изменившийся проп — прямо в рендере (без эффекта):
  // как только activeFilePath разъезжается с последним засинканным значением,
  // тут же подтягиваем фильтр; explicit-очистка крестиком меняет только fileFilter,
  // trackedFile остаётся прежним, и повторного досинка не происходит
  const [fileFilter, setFileFilter] = useState<string | null>(activeFilePath ?? null);
  const [trackedFile, setTrackedFile] = useState<string | null>(activeFilePath ?? null);
  if ((activeFilePath ?? null) !== trackedFile) {
    setTrackedFile(activeFilePath ?? null);
    setFileFilter(activeFilePath ?? null);
  }

  const [entries, setEntries] = useState<DossierEntry[] | null>(null);
  // Метрика охвата (блок В): окно periodDays, всего коммитов и паспортов в выдаче.
  // null — ответ пришёл без coverage (старая сборка бэка) или запрос ещё не ушёл;
  // subheader тогда просто не рисует строку «Охвачено N из M».
  const [coverage, setCoverage] = useState<{ periodDays: number; commits: number; dossiers: number } | null>(null);
  const [loadError, setLoadError] = useState(false);
  const [reloadTick, setReloadTick] = useState(0);
  const [personas, setPersonas] = useState<Map<string, Persona>>(new Map());
  const [openId, setOpenId] = useState<string | null>(null);
  const [openMonths, setOpenMonths] = useState<Set<string>>(new Set());
  const [searchOpen, setSearchOpen] = useState(false);
  const [query, setQuery] = useState('');

  // Этап 3 — экспорт в ветку ccs/dossiers/v1. Гейт видимости кнопки:
  //   флаг change-dossiers-recall включён
  //   И проект — git-репозиторий (isGitRepo из /dossiers/export/status)
  // sharedFolder приезжает тем же запросом и подсовывается модалке для выноски.
  // hasDossierBranch — наличие локальной refs/heads/ccs/dossiers/v1: пока ветки нет,
  // импорт из неё бессмыслен, и «Загрузить» гейтим отдельно от «Выгрузить» —
  // выгрузить можно и когда ветки ещё нет, именно так она и создаётся.
  const recallEnabled = useFeature(FLAGS.changeDossiersRecall);
  const [exportStatus, setExportStatus] = useState<{ isGitRepo: boolean; sharedFolder: boolean; hasDossierBranch: boolean } | null>(null);
  const [exportOpen, setExportOpen] = useState(false);
  const [importOpen, setImportOpen] = useState(false);

  useEffect(() => {
    if (!recallEnabled) {
      // Сбрасываем на null и закрываем модалку, если флаг мигнул во время открытого диалога
      setExportStatus(null);
      setExportOpen(false);
      return;
    }
    let cancelled = false;
    api.dossiers.exportStatus(project.id)
      .then(s => { if (!cancelled) setExportStatus(s); })
      .catch(() => { if (!cancelled) setExportStatus({ isGitRepo: false, sharedFolder: false, hasDossierBranch: false }); });
    return () => { cancelled = true; };
  }, [project.id, recallEnabled]);

  const showExportButton = recallEnabled && exportStatus?.isGitRepo === true;
  // Импорт дополнительно требует существующую ветку ccs/dossiers/v1: без неё
  // «Загрузить» упрётся в «Загружать пока нечего». «Выгрузить» этой зависимости
  // не имеет — выгрузка как раз и создаёт ветку.
  const showImportButton = showExportButton && exportStatus?.hasDossierBranch === true;
  const hasHeader = useHasPanelHeader();

  // Стабильные onClose для диалогов — иначе каждая перерисовка панели (например,
  // обновление useNow в этом же компоненте) даёт свежую стрелочную функцию,
  // Modal пересоздаёт обработчик Escape и ререндерит IconButton без нужды.
  const closeExport = useCallback(() => setExportOpen(false), []);
  const closeImport = useCallback(() => setImportOpen(false), []);

  useEffect(() => {
    let cancelled = false;
    // eslint-disable-next-line react-hooks/set-state-in-effect -- сброс перед новым запросом (сменился фильтр/проект) — иначе список чужого фильтра мигнёт перед загрузкой
    setEntries(null);
    setLoadError(false);
    setCoverage(null);
    api.dossiers.list(project.id, fileFilter ? { file: fileFilter } : undefined)
      .then(res => {
        if (cancelled) return;
        // archived показываем только по явному запросу (заметка с текстами) —
        // в общем списке им не место
        const visible = res.entries.filter(e => e.status !== 'archived');
        setEntries(visible);
        setCoverage(res.coverage ?? null);
        setOpenId(visible[0]?.id ?? null);
      })
      .catch(() => { if (!cancelled) { setEntries([]); setLoadError(true); } });
    return () => { cancelled = true; };
  }, [project.id, fileFilter, reloadTick]);

  useEffect(() => {
    let cancelled = false;
    api.personas.list({ scope: 'context', projectId: project.id })
      .then(list => { if (!cancelled) setPersonas(new Map(list.map(p => [p.id, p]))); })
      .catch(() => { /* без имён персон — покажем «Персона» */ });
    return () => { cancelled = true; };
  }, [project.id]);

  const authorOf = (entry: DossierEntry): Author => {
    if (entry.personaId) return { name: personas.get(entry.personaId)?.name ?? 'Персона', persona: true };
    return { name: displayNameOf(auth), persona: false };
  };

  const filtered = useMemo(() => {
    if (!entries) return null;
    const q = query.trim().toLowerCase();
    if (!q) return entries;
    return entries.filter(e =>
      e.commitSha.toLowerCase().startsWith(q) ||
      e.commitSubject.toLowerCase().includes(q) ||
      e.why.toLowerCase().includes(q));
  }, [entries, query]);

  // Состояние, а не Date.now() в рендере (purity) — граница «последние 30 дней» и
  // так не требует секундной точности, минутного тика более чем достаточно
  const now = useNow(60_000);
  const { recent, groups } = useMemo(() => {
    if (!filtered) return { recent: [] as DossierEntry[], groups: [] as { key: string; label: string; entries: DossierEntry[] }[] };
    const cutoff = now - RECENT_DAYS * 24 * 60 * 60 * 1000;
    // Внутри одной даты (тот же коммит) своя запись должна идти РАНЬШЕ импортированной —
    // тексты §2.4: «панель показывает обе — своя сверху». Бэкенд сортирует только по
    // committedAt desc и для одинаковой даты порядок не детерминирован: own < imported
    // вторичным ключом делает это стабильным без потери основной сортировки.
    const byShaAndOrigin = (a: DossierEntry, b: DossierEntry) => {
      const dt = Date.parse(b.committedAt) - Date.parse(a.committedAt);
      if (dt !== 0) return dt;
      return a.origin === b.origin ? 0 : (a.origin === 'own' ? -1 : 1);
    };
    const recentList: DossierEntry[] = [];
    const byMonth = new Map<string, DossierEntry[]>();
    for (const e of filtered) {
      const t = Date.parse(e.committedAt);
      if (!isNaN(t) && t >= cutoff) { recentList.push(e); continue; }
      const k = monthKey(e.committedAt);
      const list = byMonth.get(k) ?? [];
      list.push(e);
      byMonth.set(k, list);
    }
    recentList.sort(byShaAndOrigin);
    const groupList = [...byMonth.entries()]
      .sort((a, b) => b[0].localeCompare(a[0]))
      .map(([key, list]) => ({ key, label: monthLabel(list[0].committedAt), entries: list.sort(byShaAndOrigin) }));
    return { recent: recentList, groups: groupList };
  }, [filtered, now]);

  const toggleMonth = (key: string) => {
    setOpenMonths(prev => {
      const next = new Set(prev);
      if (!next.delete(key)) next.add(key);
      return next;
    });
  };

  const clearFilter = () => setFileFilter(null);

  const subheader = (
    <div style={{ padding: `${SP.sm}px ${SP.md}px ${SP.xs}px`, borderBottom: `1px solid ${C.borderLight}`, flexShrink: 0 }}>
      <p style={{ margin: `0 0 ${SP.sm}px`, fontSize: FS.sm, color: C.textSecondary, lineHeight: 1.5 }}>
        {fileFilter ? 'Зачем менялся этот файл и что при этом решили' : 'История решений по проекту'}
      </p>
      {/* Метрика охвата (блок В): показываем, только если бэк прислал coverage и
          коммитов в окне больше нуля — иначе строка «0 из 0» создаёт шум и ни о чём
          не говорит (нет окна — нечего мерить). */}
      {coverage && coverage.commits > 0 && (
        <p style={{ margin: `0 0 ${SP.sm}px`, fontSize: FS.xs, color: C.textMuted, lineHeight: 1.4 }}>
          Охвачено {coverage.dossiers} из {coverage.commits} коммитов за неделю
        </p>
      )}
      {/* Подсказка автовыгрузки: тот же гейт, что у кнопок «Выгрузить»/«Загрузить»
          (showExportButton) — фича включена и проект — git-репозиторий. Без подсказки
          человек ждал бы конспектов, которых автовыгрузка принципиально не везёт
          (после правки «Автовыгрузка не должна молча тратить модель на конспекты»).
          Текст — единая константа T.autoExportHint в начале файла. */}
      {showExportButton && (
        <p style={{ margin: `0 0 ${SP.sm}px`, fontSize: FS.xs, color: C.textMuted, lineHeight: 1.4 }}>
          {T.autoExportHint}
        </p>
      )}
      <div style={{ display: 'flex', alignItems: 'center', gap: SP.xs, flexWrap: 'wrap' }}>
        {fileFilter ? (
          <span style={{ ...chipStyle, background: C.accentLight, borderColor: 'transparent', color: C.accent }}>
            <FileIcon size={11} strokeWidth={ICON_STROKE} />
            {basename(fileFilter)}
            <button
              onClick={clearFilter} title="Показать весь проект"
              style={{ border: 'none', background: 'transparent', color: 'inherit', cursor: 'pointer', display: 'flex', padding: 0, marginLeft: 2 }}
            >
              <X size={11} strokeWidth={ICON_STROKE} />
            </button>
          </span>
        ) : (
          <span style={{ ...chipStyle, color: C.textMuted }}>Весь проект</span>
        )}
        <button
          onClick={() => setSearchOpen(v => !v)}
          style={{ ...chipStyle, cursor: 'pointer', color: searchOpen ? C.accent : C.textHeading, borderColor: searchOpen ? 'transparent' : C.border, background: searchOpen ? C.accentLight : C.bgCard }}
        >
          <Search size={11} strokeWidth={ICON_STROKE} />
          sha или текст
        </button>
      </div>
      {searchOpen && (
        <div style={{ marginTop: SP.sm, display: 'flex', gap: SP.xs, alignItems: 'center' }}>
          <TextField value={query} onChange={setQuery} placeholder="sha или текст" autoFocus style={{ height: 30, fontSize: FS.sm }} />
          <button
            onClick={() => { setQuery(''); setSearchOpen(false); }} title="Закрыть поиск"
            style={{ border: 'none', background: 'transparent', color: C.textMuted, cursor: 'pointer', display: 'flex', flexShrink: 0 }}
          >
            <X size={14} strokeWidth={ICON_STROKE} />
          </button>
        </div>
      )}
    </div>
  );

  // Тело панели вычисляется один раз и используется в ЕДИНОМ return ниже —
  // так и кнопки тулбара, и диалоги импорта/экспорта монтируются один раз и
  // остаются в дереве во всех состояниях (загрузка/ошибка/пусто/пустой поиск/
  // непустой список). Раньше каждый ранний return уносил с собой PanelHeaderSlot
  // и весь JSX диалогов: в пустой истории кнопки «Загрузить»/«Выгрузить»
  // исчезали, а сами диалоги (запланированные ниже на :758-776) существовали
  // каждый в своём экземпляре панели — при двойном монтировании конфликтовали.
  let body: ReactNode;

  if (entries === null) {
    // Загрузка — скелетон повторяет форму карточки, чтобы список не «прыгал»
    body = (
      <div style={{ flex: 1, overflow: 'auto', padding: `${SP.sm}px ${SP.md}px` }}>
        <SkeletonCard /><SkeletonCard /><SkeletonCard />
      </div>
    );
  } else if (loadError) {
    body = (
      <EmptyState
        compact
        icon={<AlertTriangle size={20} strokeWidth={ICON_STROKE} />}
        title="Не удалось загрузить историю решений"
        subtitle="Проверьте соединение и попробуйте ещё раз."
        action={<Button variant="secondary" size="sm" onClick={() => setReloadTick(t => t + 1)}>Повторить</Button>}
      />
    );
  } else if (entries.length === 0) {
    body = fileFilter ? (
      <EmptyState
        compact
        icon={<FileIcon size={20} strokeWidth={ICON_STROKE} />}
        title="Этот файл пока не попадал в историю решений"
        subtitle="Она собирается автоматически из коммитов с трейлером чата."
      />
    ) : (
      <EmptyState
        compact
        icon={<Lightbulb size={20} strokeWidth={ICON_STROKE} />}
        title="Здесь появится история решений"
        subtitle="Когда код меняют из чата или задачи, AI Home сохранит, зачем это делалось и что при этом решили. Дальше — просто работайте как обычно."
        // В пустой истории без фильтра по файлу даём кнопку загрузки из репозитория:
        // для нового проекта без своих записей и без ветки ccs/dossiers/v1 иначе
        // человек видит только заголовок и не понимает, как подтянуть историю
        // коллег. Кнопка открывает тот же диалог импорта, что и иконка в шапке —
        // она уже под рукой у того, кто догадался навести курсор; здесь — для тех,
        // кто ищет подсказку в теле панели.
        action={showImportButton ? (
          <Button
            variant="secondary"
            size="sm"
            leftIcon={<Download size={12} strokeWidth={ICON_STROKE} />}
            title="Загрузить из репозитория"
            onClick={() => setImportOpen(true)}
          >
            Загрузить из репозитория
          </Button>
        ) : undefined}
      />
    );
  } else if (filtered && filtered.length === 0) {
    // Поиск по sha/тексту съел весь список — без этого состояния тело панели
    // рендерилось бы пустым, и было бы не отличить от зависшей загрузки (ревью Майи)
    body = (
      <EmptyState
        compact
        icon={<Search size={20} strokeWidth={ICON_STROKE} />}
        title={`Ничего не найдено по «${query.trim()}»`}
        subtitle="Проверьте sha коммита или часть текста записи."
        action={<Button variant="secondary" size="sm" onClick={() => setQuery('')}>Сбросить поиск</Button>}
      />
    );
  } else {
    body = (
      <div style={{ flex: 1, overflow: 'auto', padding: `${SP.sm}px ${SP.md}px ${SP.lg}px` }}>
        {recent.map((entry, i) => (
          <div key={entry.id}>
            {/* Заголовок «Две записи об одном коммите» — только между своей и
                импортированной записями по одному sha (тексты §2.4). Связь читается
                по одинаковому mono-sha, отдельной рамки-обёртки не вводим. */}
            {isSecondInPair(entry, i > 0 ? recent[i - 1] : null) && <PairHeader />}
            <DossierCard
              entry={entry} author={authorOf(entry)}
              open={openId === entry.id}
              onToggle={() => setOpenId(prev => prev === entry.id ? null : entry.id)}
              onOpenChat={onOpenChat} onOpenTask={onOpenTask} onOpenCommit={onOpenCommit}
              fallbackFile={fileFilter}
            />
          </div>
        ))}
        {groups.length > 0 && (
          <p style={{ fontSize: FS.xs, textTransform: 'uppercase', letterSpacing: '0.03em', fontWeight: 700, color: C.textMuted, margin: `2px 0 ${SP.xs}px` }}>
            Более 30 дней назад
          </p>
        )}
        {groups.map((group, gi) => {
          const open = openMonths.has(group.key);
          // Предыдущая запись для заголовка пары — последняя из recent (если recent не пуст)
          // или последняя из предыдущей раскрытой группы, иначе null.
          const prevAtStart = gi === 0
            ? (recent.length > 0 ? recent[recent.length - 1] : null)
            : (() => {
                for (let g = gi - 1; g >= 0; g--) {
                  if (openMonths.has(groups[g].key) && groups[g].entries.length > 0) {
                    return groups[g].entries[groups[g].entries.length - 1];
                  }
                }
                return recent.length > 0 ? recent[recent.length - 1] : null;
              })();
          return (
            <div key={group.key}>
              <GroupHeader label={group.label} count={group.entries.length} open={open} onToggle={() => toggleMonth(group.key)} />
              {open && group.entries.map((entry, ei) => {
                const prev = ei === 0 ? prevAtStart : group.entries[ei - 1];
                return (
                  <div key={entry.id}>
                    {isSecondInPair(entry, prev) && <PairHeader />}
                    {openId === entry.id ? (
                      <DossierCard
                        entry={entry} author={authorOf(entry)} open
                        onToggle={() => setOpenId(null)}
                        onOpenChat={onOpenChat} onOpenTask={onOpenTask} onOpenCommit={onOpenCommit}
                        fallbackFile={fileFilter}
                      />
                    ) : (
                      <CompactRow entry={entry} author={authorOf(entry)} onClick={() => setOpenId(entry.id)} />
                    )}
                  </div>
                );
              })}
            </div>
          );
        })}
      </div>
    );
  }

  return (
    <>
      <div style={{ display: 'flex', flexDirection: 'column', height: '100%', minHeight: 0 }}>
        {subheader}
        {exclusionNote}
        {/* Кнопки тулбара (ADR §6): только когда у панели есть шапка. Без pinned —
            ни одна из них не главное действие панели, в покое шапка должна оставаться
            чистой. Импорт (Download) ставится слева от выгрузки (Upload) — пара
            «забрать / отдать» читается по направлению стрелок. Импорт гейтится
            отдельно (showImportButton): пока ветки ccs/dossiers/v1 ещё нет, чип
            «Загрузить» не показывается — одиночка упрётся в «Загружать пока нечего».
            «Выгрузить» показывается по showExportButton: выгрузить можно и до создания
            ветки, именно так она и появляется.
            Слот поднят выше ранних return — теперь кнопки доступны во всех состояниях
            панели (загрузка/ошибка/пусто/пустой поиск/непустой список). */}
        {(showImportButton || showExportButton) && hasHeader && (
          <PanelHeaderSlot side="right">
            {showImportButton && (
              <IconButton size="sm" title="Загрузить из репозитория" onClick={() => setImportOpen(true)}>
                <Download size={14} strokeWidth={ICON_STROKE} />
              </IconButton>
            )}
            {showExportButton && (
              <IconButton size="sm" title="Выгрузить в репозиторий" onClick={() => setExportOpen(true)}>
                <Upload size={14} strokeWidth={ICON_STROKE} />
              </IconButton>
            )}
            {/* В DesktopWorkspace PanelZone всегда завёрнут в PanelShell, который даёт hasHeader=true,
                поэтому fallback-ветка !hasHeader удалена как недостижимая. На мобильной раскладке
                PanelShell тоже присутствует. */}
          </PanelHeaderSlot>
        )}
        {body}
      </div>
      {/* Диалоги монтируются в единственном экземпляре на панель. Условный рендеринг
          открытого диалога (exportOpen/importOpen) гарантирует, что в один момент
          времени в DOM жива максимум одна модалка — закрытая размонтируется, а её
          место освободится. */}
      {showExportButton && (
        <DossierExportDialog
          open={exportOpen}
          onClose={closeExport}
          projectId={project.id}
          sharedFolder={exportStatus?.sharedFolder === true}
        />
      )}
      {showImportButton && (
        <DossierImportDialog
          open={importOpen}
          onClose={closeImport}
          projectId={project.id}
          // Импорт добавил записи — дёргаем список, чтобы они появились с бейджем
          // «Из репозитория» и парами по коммитам. Дёргается на закрытии success/nothing,
          // а не на каждой фазе — иначе при ошибке получим лишний запрос.
          onSuccess={() => setReloadTick(t => t + 1)}
        />
      )}
    </>
  );
}
