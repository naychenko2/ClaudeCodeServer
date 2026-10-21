// Группа «Макеты» вкладки «Планы и макеты» (волна 3).
//
// Строки (список): миниатюра-заглушка 32×24, название, «дата · N версий», статус-чип,
// чип «не в master». Карточка (деталь): метка «макет», название, статус, автор «чат ·
// задача» (при taskId), путь, большая заглушка с «Открыть макет» (первый существующий
// html → onOpenFile; exists:false → disabled «макет появится после мержа»), «Файлы» (чипы),
// плашка у отклонённого, подвал, обратная строка «Упомянут в».
//
// «Прошлые версии · N» — свёрнутый блок под живыми (там же rejected).
// mockupsUnavailable='device' — локальный проект: «Макеты локального проекта видны
// только на устройстве».

import { useState } from 'react';
import { ChevronDown, ExternalLink } from 'lucide-react';
import { C, R, FS, FONT, SP } from '../../lib/design';
import { ICON_SIZE, ICON_STROKE } from '../ui/icons';
import { Button } from '../ui/Button';
import { NavTargetButton } from './NavTargetButton';
import { EMPTY_NAV_MARKS, type NavMarks } from '../../lib/featureNavMarks';
import { openTaskInSection } from '../../lib/tasks';
import { getPersonaById } from '../../lib/personas';
import { dateOnly } from '../chat/feature/featureTime';
import { MOCKUP_STATUS_LABEL, MOCKUP_STATUS_STYLE, mainHtmlFile, versionCounts, isCurrentOfGroup } from '../../lib/mockupVersions';
import type { FeatureMockup, FeatureMockupStatus, FeaturePlan } from '../../types/featurePlans';

// Статус-чип макета (R.sm, FS.xs полужирный; цвета — макет §состояния).
// replaced — версия из «Прошлых версий», не текущая в группе: чип «заменён»
// (C.bgSelected/C.textMuted) вместо статуса из шапки записки (макет §состояния).
export function MockupStatusChip({ status, isMobile = false, replaced = false }: { status: FeatureMockupStatus | null; isMobile?: boolean; replaced?: boolean }) {
  if (replaced) {
    return (
      <span
        data-testid="mockup-status-chip"
        style={{
          display: 'inline-flex', alignItems: 'center',
          height: isMobile ? 24 : 18, padding: '0 6px', borderRadius: R.sm,
          background: C.bgSelected, color: C.textMuted,
          fontFamily: FONT.sans, fontSize: FS.xs, fontWeight: 600, whiteSpace: 'nowrap', flexShrink: 0,
        }}
      >
        заменён
      </span>
    );
  }
  if (!status) return null;
  const s = MOCKUP_STATUS_STYLE[status];
  return (
    <span
      data-testid="mockup-status-chip"
      style={{
        display: 'inline-flex', alignItems: 'center',
        height: isMobile ? 24 : 18, padding: '0 6px', borderRadius: R.sm,
        background: s.bg, color: s.fg,
        fontFamily: FONT.sans, fontSize: FS.xs, fontWeight: 600, whiteSpace: 'nowrap', flexShrink: 0,
      }}
    >
      {MOCKUP_STATUS_LABEL[status]}
    </span>
  );
}

// Миниатюра-заглушка макета (рисунок из токенов, не скриншот):
// small 32×24 (R.sm), large 100%×160 (на 360 — 128, R.lg).
export function MockupThumb({ large, isMobile = false }: { large?: boolean; isMobile?: boolean }) {
  if (large) {
    return (
      <div
        data-testid="mockup-thumb-large"
        style={{
          width: '100%', height: isMobile ? 128 : 160, borderRadius: R.lg,
          border: `1px solid ${C.border}`, background: C.bgCard,
          display: 'flex', alignItems: 'center', justifyContent: 'center', overflow: 'hidden', position: 'relative',
        }}
      >
        {/* полосы-«рисунок» из токенов */}
        <div style={{ position: 'absolute', inset: 0, display: 'flex', flexDirection: 'column', gap: 8, padding: 16, opacity: 0.5 }}>
          <div style={{ height: 8, width: '60%', background: C.accentMuted, borderRadius: R.sm }} />
          <div style={{ height: 8, width: '80%', background: C.border, borderRadius: R.sm }} />
          <div style={{ height: 8, width: '45%', background: C.border, borderRadius: R.sm }} />
        </div>
      </div>
    );
  }
  return (
    <span
      data-testid="mockup-thumb"
      aria-hidden
      style={{
        width: 32, height: 24, borderRadius: R.sm, flexShrink: 0,
        border: `1px solid ${C.border}`, background: C.bgCard,
        display: 'inline-flex', alignItems: 'center', justifyContent: 'center', gap: 2, padding: '0 4px', overflow: 'hidden',
      }}
    >
      <span style={{ width: 6, height: 6, borderRadius: R.sm, background: C.accentMuted }} />
      <span style={{ width: 6, height: 6, borderRadius: R.sm, background: C.border }} />
    </span>
  );
}

// «не в master»: есть html, но ни один не существует (макет появится после мержа).
function notInMaster(m: FeatureMockup): boolean {
  const hasHtml = m.files.some(f => f.path.endsWith('.html'));
  const hasExistingHtml = m.files.some(f => f.path.endsWith('.html') && f.exists);
  return hasHtml && !hasExistingHtml;
}

// === Строки макетов (список) ===
export interface MockupListSectionProps {
  // Все макеты (live + past уже разделены вызывающим? нет — здесь сами делим).
  mockups: FeatureMockup[];
  // Выбранный макет (подсветка строки).
  selectedPath?: string | null;
  onSelect?: (path: string) => void;
  isMobile?: boolean;
}

// Раздел «Макеты · N» в списке: живые строки + «Прошлые версии · N» (свёрнуто).
export function MockupListSection({ mockups, selectedPath, onSelect, isMobile = false }: MockupListSectionProps) {
  const [pastOpen, setPastOpen] = useState(false);
  const counts = versionCounts(mockups);
  // Живые = текущая версия группы, не отклонённые; прошлые — остальное.
  const live = mockups.filter(m => m.status !== 'rejected' && isCurrentOfGroup(m, mockups));
  const past = mockups.filter(m => !live.includes(m));

  return (
    <div data-testid="mockup-list-section" style={{ display: 'flex', flexDirection: 'column', gap: SP.xs }}>
      <SectionHeader label="Макеты" count={live.length} isMobile={isMobile} />
      {mockups.length === 0 ? (
        <EmptyRow isMobile={isMobile} text="Макетов пока нет. Макет появится, когда у его записки в docs/mockups/ будет шапка «Фича · Статус · Файлы»" />
      ) : (
        <>
          {live.map(m => (
            <MockupRow key={m.path} m={m} versions={counts.get(m.path) ?? 1} selected={selectedPath === m.path} isMobile={isMobile} onSelect={() => onSelect?.(m.path)} />
          ))}
          {past.length > 0 && (
            <div style={{ borderTop: `1px solid ${C.borderLight}`, paddingTop: SP.xs }}>
              <button
                type="button"
                data-testid="mockup-past-toggle"
                aria-expanded={pastOpen}
                onClick={() => setPastOpen(v => !v)}
                style={{
                  width: '100%', display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: SP.sm,
                  height: isMobile ? 44 : 32, padding: `0 ${SP.sm}px`, border: 'none', background: 'transparent',
                  cursor: 'pointer', fontFamily: FONT.sans, fontSize: FS.sm, fontWeight: 600, color: C.textSecondary,
                }}
              >
                <span>Прошлые версии · {past.length}</span>
                <ChevronDown size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} style={{ transform: pastOpen ? 'rotate(180deg)' : 'none', transition: 'transform 0.15s' }} />
              </button>
              {pastOpen && past.map(m => (
                // «заменён» — версия не текущая в группе; текущая, но отклонённая — «отклонён» (свой статус).
                <MockupRow key={m.path} m={m} versions={counts.get(m.path) ?? 1} dimmed replaced={!isCurrentOfGroup(m, mockups)} selected={selectedPath === m.path} isMobile={isMobile} onSelect={() => onSelect?.(m.path)} />
              ))}
            </div>
          )}
        </>
      )}
    </div>
  );
}

// mockupsUnavailable='device' — локальный проект: записки docs/mockups/ на устройстве,
// страница фичи их не видит. «Макеты · —» + поясняющая строка.
export function MockupDeviceNotice({ isMobile = false }: { isMobile?: boolean }) {
  return (
    <div data-testid="mockup-device" style={{ display: 'flex', flexDirection: 'column', gap: SP.xs }}>
      <div data-testid="mockup-section-header" style={{ display: 'flex', alignItems: 'center', gap: SP.xs, padding: `${isMobile ? SP.xs : 2}px ${SP.sm}px` }}>
        <span style={{ fontFamily: FONT.sans, fontSize: FS.xs, fontWeight: 700, letterSpacing: '0.04em', textTransform: 'uppercase', color: C.textMuted }}>Макеты</span>
        <span style={{ fontFamily: FONT.mono, fontSize: FS.xs, color: C.textMuted }}>—</span>
      </div>
      <div style={{ border: `1px dashed ${C.dashed}`, borderRadius: R.md, padding: `${isMobile ? SP.md : SP.sm}px ${SP.md}px`, fontFamily: FONT.sans, fontSize: FS.sm, color: C.textSecondary, lineHeight: 1.45 }}>
        Макеты локального проекта видны только на устройстве. Записки docs/mockups/ лежат на вашем устройстве, и страница фичи их не видит.
      </div>
    </div>
  );
}

// Заголовок группы (FS.xs, капс, C.textMuted) + счётчик.
function SectionHeader({ label, count, isMobile }: { label: string; count: number; isMobile: boolean }) {
  return (
    <div data-testid="mockup-section-header" style={{ display: 'flex', alignItems: 'center', gap: SP.xs, padding: `${isMobile ? SP.xs : 2}px ${SP.sm}px` }}>
      <span style={{ fontFamily: FONT.sans, fontSize: FS.xs, fontWeight: 700, letterSpacing: '0.04em', textTransform: 'uppercase', color: C.textMuted }}>{label}</span>
      <span style={{ fontFamily: FONT.mono, fontSize: FS.xs, color: C.textMuted }}>{count}</span>
    </div>
  );
}

// Пустая строка группы (пунктирная рамка, R 8).
function EmptyRow({ text, isMobile }: { text: string; isMobile: boolean }) {
  return (
    <div
      data-testid="mockup-empty"
      style={{
        border: `1px dashed ${C.dashed}`, borderRadius: R.md, padding: `${isMobile ? SP.md : SP.sm}px ${SP.md}px`,
        fontFamily: FONT.sans, fontSize: FS.sm, color: C.textHeading, lineHeight: 1.45,
      }}
    >
      {text}
    </div>
  );
}

// Строка макета в списке: заглушка · название · «дата · N версий» · статус-чип · «не в master».
function MockupRow({ m, versions, selected, dimmed, replaced, isMobile, onSelect }: {
  m: FeatureMockup; versions: number; selected?: boolean; dimmed?: boolean; replaced?: boolean; isMobile: boolean; onSelect: () => void;
}) {
  const [hover, setHover] = useState(false);
  const date = m.modifiedAt ? dateOnly(m.modifiedAt) : null;
  const nMaster = notInMaster(m);
  return (
    <div
      data-testid="mockup-row"
      role="button"
      tabIndex={0}
      onClick={onSelect}
      onKeyDown={e => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); onSelect(); } }}
      onMouseEnter={() => setHover(true)}
      onMouseLeave={() => setHover(false)}
      title={m.path}
      style={{
        display: 'flex', alignItems: 'center', gap: SP.sm,
        height: isMobile ? 56 : 44, padding: `0 ${SP.sm}px`,
        background: selected ? C.accentLight : (hover ? C.bgSelected : 'transparent'),
        borderRadius: R.md, cursor: 'pointer',
      }}
    >
      <MockupThumb />
      <div style={{ minWidth: 0, flex: 1, display: 'flex', flexDirection: 'column', gap: 2 }}>
        <span style={{ fontFamily: FONT.sans, fontSize: FS.sm, fontWeight: 600, color: dimmed ? C.textSecondary : C.textHeading, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
          {m.title}
        </span>
        <span style={{ fontFamily: FONT.mono, fontSize: FS.xs, color: C.textMuted, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
          {date ? `${date} · ` : ''}{versions} {versions === 1 ? 'версия' : 'версий'}
        </span>
      </div>
      <MockupStatusChip status={m.status} isMobile={isMobile} replaced={replaced} />
      {nMaster && (
        <span data-testid="mockup-not-in-master" style={{ fontFamily: FONT.mono, fontSize: FS.xs, color: C.textMuted, border: `1px dashed ${C.dashed}`, borderRadius: R.sm, padding: '0 5px', whiteSpace: 'nowrap', flexShrink: 0 }}>
          не в master
        </span>
      )}
    </div>
  );
}

// === Карточка макета (деталь) ===
export interface MockupCardProps {
  mockup: FeatureMockup;
  // Планы фичи — для обратной строки «Упомянут в».
  plans: FeaturePlan[];
  // Открыть файл проекта (существующий просмотр, HTML в песочнице).
  onOpenFile?: (path: string) => void;
  isMobile?: boolean;
  // Версия из «Прошлых версий», не текущая в группе → чип «заменён».
  replaced?: boolean;
  // Пометка «задача закрыта» для задачи макета (из данных страницы).
  marks?: NavMarks;
}

export function MockupCard({ mockup, plans, onOpenFile, isMobile = false, replaced = false, marks = EMPTY_NAV_MARKS }: MockupCardProps) {
  const main = mainHtmlFile(mockup);
  const nMaster = notInMaster(mockup);
  const persona = mockup.authorPersonaId ? getPersonaById(mockup.authorPersonaId) : undefined;
  const authorName = persona?.name ?? (mockup.authorPersonaId ? mockup.authorPersonaId : null);
  // const (не свойство): narrowing `taskId &&` переживает замыкание onOpen.
  const taskId = mockup.taskId;

  // «Упомянут в»: планы, чьи mockupPaths ссылаются на этот макет (path или файлы).
  const mentionedBy = plans.filter(p =>
    p.mockupPaths.some(mp => mp === mockup.path || mockup.files.some(f => f.path === mp))
  );

  return (
    <div data-testid="mockup-card" style={{ display: 'flex', flexDirection: 'column', gap: SP.md, fontFamily: FONT.sans }}>
      {/* Шапка: метка «макет» · название · статус */}
      <div style={{ display: 'flex', flexDirection: 'column', gap: SP.xs }}>
        <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm }}>
          <span data-testid="mockup-card-label" style={{ fontSize: FS.xs, fontWeight: 600, color: C.textMuted, background: C.bgSelected, borderRadius: R.sm, padding: '0 6px' }}>
            макет
          </span>
          <MockupStatusChip status={mockup.status} isMobile={isMobile} replaced={replaced} />
        </div>
        <h1 data-testid="mockup-card-title" style={{ fontFamily: FONT.serif, fontSize: isMobile ? 18 : 20, fontWeight: 700, color: C.textHeading, letterSpacing: '-0.01em', margin: 0, lineHeight: 1.25 }}>
          {mockup.title}
        </h1>
        <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm, flexWrap: 'wrap' }}>
          {authorName && (
            <span style={{ fontSize: FS.sm, color: C.textSecondary }}>{authorName}</span>
          )}
          {taskId && (
            <NavTargetButton
              kind="task"
              taskId={taskId}
              taskClosed={marks.taskClosed(taskId)}
              onOpen={() => openTaskInSection({ id: taskId })}
              title="Открыть задачу макета"
              isMobile={isMobile}
            />
          )}
        </div>
        <div data-testid="mockup-card-path" style={{ fontFamily: FONT.mono, fontSize: FS.xs, color: C.textMuted, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
          {mockup.path}
        </div>
      </div>

      {/* Большая заглушка с «Открыть макет» (нажимается вся). */}
      <div
        data-testid="mockup-open-area"
        role={main ? 'button' : undefined}
        tabIndex={main ? 0 : undefined}
        onClick={() => { if (main && onOpenFile) onOpenFile(main); }}
        onKeyDown={e => { if (main && onOpenFile && (e.key === 'Enter' || e.key === ' ')) { e.preventDefault(); onOpenFile(main); } }}
        style={{
          position: 'relative', borderRadius: R.lg,
          border: main ? 'none' : `1px dashed ${C.dashed}`,
          cursor: main ? 'pointer' : 'default',
        }}
      >
        <MockupThumb large isMobile={isMobile} />
        <div style={{ position: 'absolute', inset: 0, display: 'flex', alignItems: 'center', justifyContent: 'center' }}>
          <Button
            size="sm"
            variant="primary"
            disabled={!main}
            title={nMaster ? 'макет появится после мержа' : undefined}
            onClick={e => { e.stopPropagation(); if (main && onOpenFile) onOpenFile(main); }}
          >
            <ExternalLink size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
            Открыть макет
          </Button>
        </div>
        {nMaster && (
          <div data-testid="mockup-merge-hint" style={{ position: 'absolute', bottom: 8, left: 0, right: 0, textAlign: 'center', fontFamily: FONT.mono, fontSize: FS.xs, color: C.textMuted }}>
            макет появится после мержа
          </div>
        )}
      </div>

      {/* «Не в master»: плашка C.infoBg (макет) — честная причина, почему макет не в master. */}
      {nMaster && (
        <div data-testid="mockup-not-in-master-plate" style={{ background: C.infoBg, border: `1px solid ${C.borderLight}`, borderRadius: R.md, padding: `${SP.sm}px ${SP.md}px`, fontSize: FS.sm, color: C.textSecondary, lineHeight: 1.45 }}>
          Ещё не влит в master. Макет лежит только в ветке фичи; появится после мержа, до этого его видно в чате Майи.
        </div>
      )}

      {/* «Файлы»: чипы (записка + файлы шапки). */}
      <div style={{ display: 'flex', flexDirection: 'column', gap: SP.xs }}>
        <span style={{ fontSize: FS.xs, color: C.textMuted }}>Файлы</span>
        <div style={{ display: 'flex', gap: SP.xs, flexWrap: 'wrap' }}>
          {mockup.files.map(f => (
            <span
              key={f.path}
              data-testid="mockup-file-chip"
              title={f.exists ? f.path : `${f.path} (не в master)`}
              style={{
                display: 'inline-flex', alignItems: 'center', gap: 4,
                height: isMobile ? 40 : 26, padding: '0 8px',
                border: `1px solid ${C.border}`, background: C.bgWhite, borderRadius: R.sm,
                fontFamily: FONT.mono, fontSize: 12, color: f.exists ? C.textSecondary : C.textMuted,
                whiteSpace: 'nowrap',
              }}
            >
              {f.path.split('/').pop()}
            </span>
          ))}
        </div>
      </div>

      {/* Плашка у отклонённого. */}
      {mockup.status === 'rejected' && (
        <div data-testid="mockup-rejected-plate" style={{ border: `1px solid ${C.borderLight}`, background: C.bgSelected, borderRadius: R.md, padding: `${SP.sm}px ${SP.md}px`, fontSize: FS.sm, color: C.textSecondary, lineHeight: 1.45 }}>
          Вариант отклонён. Оставлен для истории: в списке макетов его нет, он лежит в «Прошлых версиях».
        </div>
      )}

      {/* «Упомянут в»: обратная строка по plans[].mockupPaths. */}
      {mentionedBy.length > 0 && (
        <div data-testid="mockup-mentioned-in" style={{ display: 'flex', flexDirection: 'column', gap: SP.xs }}>
          <span style={{ fontSize: FS.xs, color: C.textMuted }}>Упомянут в</span>
          {mentionedBy.map(p => (
            <span key={p.taskId} style={{ fontFamily: FONT.mono, fontSize: FS.xs, color: C.accent, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
              {p.title}
            </span>
          ))}
        </div>
      )}

      {/* Подвал. */}
      <div data-testid="mockup-card-footer" style={{ borderTop: `1px solid ${C.borderLight}`, paddingTop: SP.sm, fontSize: FS.xs, color: C.textMuted, lineHeight: 1.45 }}>
        Статус и файлы — из шапки записки. «Утверждён» ставит Софья по слову Андрея отдельным коммитом.
      </div>
    </div>
  );
}
