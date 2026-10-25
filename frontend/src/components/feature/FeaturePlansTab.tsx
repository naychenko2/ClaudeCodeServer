// Вкладка «Планы и макеты» страницы фичи (волна 4, вариант 5В точки 5).
//
// Раскладка: сетка карточек-превью — группы «Документы» и «Макеты» (3 колонки на
// десктопе, 1 на 360). Клик по карточке — читалка или карточка макета на всю ширину
// с «‹ Все документы» для возврата к сетке.
//
// Документы (plans): порядок cut → plan → consult (макет); карточка — значок роли,
// заголовок, автор, время, «без текста». Читалка — FeaturePlanReader (markdown через
// общий рендер). Макеты — FeatureMockups (карточки-превью с миниатюрой-заглушкой,
// карточка, «Прошлые версии»).
//
// Состояния: документов нет, макеты есть → «Планов пока нет…»; mockupsUnavailable='device'
// → «Макеты локального проекта видны только на устройстве»; нет ничего → общее пустое.

import { useMemo, useState } from 'react';
import { ChevronLeft, MessageCircle } from 'lucide-react';
import { C, R, FS, FONT, SP } from '../../lib/design';
import { ICON_SIZE, ICON_STROKE } from '../ui/icons';
import { getPersonaById } from '../../lib/personas';
import { dateOnly, hhmm } from '../chat/feature/featureTime';
import { splitMockups, isCurrentOfGroup } from '../../lib/mockupVersions';
import { recordFeatureReturn } from '../../lib/featureBackNav';
import { openTaskInSection } from '../../lib/tasks';
import { RoleGlyph } from './FeaturePlanReader';
import { FeaturePlanReader } from './FeaturePlanReader';
import { Button } from '../ui/Button';
import { buildNavMarks, type NavMarks } from '../../lib/featureNavMarks';
import { MockupCard, MockupListSection, MockupDeviceNotice } from './FeatureMockups';
import type { FeaturePage } from '../../types/featureIndex';
import type { FeaturePlan, FeatureMockup } from '../../types/featurePlans';

// Выбран в детали: документ (plan) или макет (mockup). null — сетка.
type Selection = { kind: 'plan'; id: string } | { kind: 'mockup'; path: string } | null;

export interface FeaturePlansTabProps {
  // Данные страницы (plans / mockups / mockupsUnavailable).
  page: FeaturePage;
  isMobile?: boolean;
  // Открыть файл проекта (существующий просмотр, HTML в песочнице).
  onOpenFile?: (path: string) => void;
  // Открыть задачу (автор → «задача»).
  onOpenTask?: (taskId: string) => void;
  // Пометки «задача закрыта» (страница даёт свои; без пропа — из run страницы данных).
  marks?: NavMarks;
  // «Открыть ключевой чат» (общее пустое).
  onOpenKeyChat?: () => void;
  // Фича (W4-LINK-F): резолв featureId для чтения файлов дерева фичи (W4-5B2) и
  // проект — область запроса. Без них ссылки между документами не оживают.
  featureId?: string | null;
  projectId?: string | null;
}

// Колонки сетки: 3 на десктопе, 1 на 360 (вариант 5В: «на 360 карточки в одну колонку»).
const gridColumns = (isMobile: boolean) => (isMobile ? '1fr' : 'repeat(3, minmax(0, 1fr))');

// Карточка-превью документа (сетка): значок роли · заголовок · автор · время · «без текста».
// Высота карточки не зависит от ширины: сетка сама сужает колонки на 360 (одна колонка).
function PlanCard({ plan, onSelect }: {
  plan: FeaturePlan; onSelect: () => void;
}) {
  const [hover, setHover] = useState(false);
  const persona = plan.authorPersonaId ? getPersonaById(plan.authorPersonaId) : undefined;
  const authorName = persona?.name ?? (plan.authorPersonaId ? plan.authorPersonaId : null);
  const time = plan.completedAt ? `${dateOnly(plan.completedAt)} · ${hhmm(plan.completedAt)}` : null;
  return (
    <div
      data-testid="plan-row"
      role="button"
      tabIndex={0}
      onClick={onSelect}
      onKeyDown={e => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); onSelect(); } }}
      onMouseEnter={() => setHover(true)}
      onMouseLeave={() => setHover(false)}
      title={plan.title}
      style={{
        background: hover ? C.bgSelected : C.bgCard,
        border: `1px solid ${C.border}`,
        borderRadius: R.md,
        padding: `${SP.md}px ${SP.md}px`,
        display: 'flex', flexDirection: 'column', gap: SP.sm,
        cursor: 'pointer',
      }}
    >
      <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm }}>
        <RoleGlyph role={plan.role} />
        {/* Заголовок — до двух строк (макет): line-clamp 2, строка растёт (minHeight). */}
        <span style={{ fontFamily: FONT.sans, fontSize: FS.sm, fontWeight: 600, color: C.textHeading, display: '-webkit-box', WebkitLineClamp: 2, WebkitBoxOrient: 'vertical', overflow: 'hidden', textOverflow: 'ellipsis' }}>
          {plan.title}
        </span>
      </div>
      <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm, flexWrap: 'wrap' }}>
        <span style={{ fontFamily: FONT.mono, fontSize: FS.xs, color: C.textMuted, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
          {authorName ? `${authorName} · ` : ''}{time ?? '—'}
        </span>
        {!plan.hasText && (
          <span data-testid="plan-row-no-text" style={{ fontFamily: FONT.sans, fontSize: 10, color: C.textMuted, border: '1px dashed ' + C.dashed, borderRadius: R.sm, padding: `0 ${SP.xs}px`, whiteSpace: 'nowrap', flexShrink: 0 }}>
            без текста
          </span>
        )}
      </div>
    </div>
  );
}

// Заголовок группы (FS.xs, капс, C.textMuted) + счётчик.
function GroupHeader({ label, count, isMobile }: { label: string; count: number; isMobile: boolean }) {
  return (
    <div data-testid="plans-group-header" style={{ display: 'flex', alignItems: 'center', gap: SP.xs, padding: `${isMobile ? SP.xs : 2}px ${SP.sm}px` }}>
      <span style={{ fontFamily: FONT.sans, fontSize: FS.xs, fontWeight: 700, letterSpacing: '0.04em', textTransform: 'uppercase', color: C.textMuted }}>{label}</span>
      <span style={{ fontFamily: FONT.mono, fontSize: FS.xs, color: C.textMuted }}>{count}</span>
    </div>
  );
}

export function FeaturePlansTab({ page, isMobile = false, onOpenFile, onOpenTask, onOpenKeyChat, marks: marksProp, featureId = null, projectId = null }: FeaturePlansTabProps) {
  const marks = useMemo(() => marksProp ?? buildNavMarks(page.run, page.chats), [marksProp, page]);
  // Проект эпика — область перехода в задачу; нет эпика — проект страницы.
  const taskProjectId = page.run.epic?.projectId ?? projectId;
  // Переход в задачу документа/макета: точка возврата «← к фиче · Планы» + адрес с проектом эпика.
  const openTask = (taskId: string) => {
    if (featureId) recordFeatureReturn(featureId, { kind: 'task', id: taskId }, page.run.epic?.title ?? null, 'plans');
    openTaskInSection({ id: taskId, projectId: taskProjectId ?? undefined });
  };
  // Документы: порядок cut → plan → consult (макет). Дедуп планов (решение Андрея):
  // переписанный план — показываем ПОСЛЕДНИЙ по createdAt; разрезы и консультации — все.
  const plans = useMemo(() => {
    const all = page.plans ?? [];
    const byRole = (role: FeaturePlan['role']) => all.filter(p => p.role === role);
    // Последний план по createdAt: сортируем по возрастанию и берём хвост.
    const planTasks = [...byRole('plan')].sort((a, b) => a.createdAt.localeCompare(b.createdAt));
    const latestPlan = planTasks.length > 0 ? planTasks[planTasks.length - 1] : null;
    return [
      ...byRole('cut'),
      ...(latestPlan ? [latestPlan] : []),
      ...byRole('consult'),
    ];
  }, [page.plans]);
  const mockups: FeatureMockup[] = page.mockups ?? [];
  const device = page.mockupsUnavailable === 'device';

  // Сетка — состояние по умолчанию (5В: «сначала сетка карточек»). Если выбранный
  // документ/макет исчез из данных (дедуп, обновление) — selectedPlan/selectedMockup
  // null → снова сетка, пустой детали нет.
  const [selected, setSelected] = useState<Selection>(null);
  const selectedPlan = selected?.kind === 'plan' ? plans.find(p => p.taskId === selected.id) ?? null : null;
  const selectedMockup = selected?.kind === 'mockup' ? mockups.find(m => m.path === selected.path) ?? null : null;
  // Выбранный макет — прошлая версия (не текущая в группе) → чип «заменён» в карточке.
  const selectedMockupReplaced = selectedMockup ? !isCurrentOfGroup(selectedMockup, mockups) : false;

  const hasAnything = plans.length > 0 || mockups.length > 0;
  // «Документов и макетов пока нет» — только когда пусто И макеты не «на устройстве»:
  // при mockupsUnavailable='device' макеты ЕСТЬ (на устройстве), пустота бы врала —
  // показываем честную причину (MockupDeviceNotice) независимо от наличия планов.
  const showNoDocuments = !hasAnything && !device;

  // === Выбран документ или макет: деталь на всю ширину, возврат к сетке ===
  if (selectedPlan || selectedMockup) {
    return (
      <div data-testid="plans-detail-full" style={{ display: 'flex', flexDirection: 'column', gap: SP.md }}>
        <Button
          variant="ghost"
          size="sm"
          dataTestId="plans-back-to-list"
          leftIcon={<ChevronLeft size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />}
          onClick={() => setSelected(null)}
          style={{ alignSelf: 'flex-start', height: 44, color: C.textSecondary, border: 'none' }}
        >
          Все документы
        </Button>
        {selectedPlan
          ? <FeaturePlanReader plan={selectedPlan} mockups={mockups} isMobile={isMobile} onOpenMockup={p => setSelected({ kind: 'mockup', path: p })} onOpenTask={onOpenTask ?? openTask} marks={marks} featureId={featureId} projectId={projectId} taskProjectId={taskProjectId} documents={plans} onOpenDocument={id => setSelected({ kind: 'plan', id })} />
          : <MockupCard mockup={selectedMockup!} plans={plans} onOpenFile={onOpenFile} isMobile={isMobile} replaced={selectedMockupReplaced} marks={marks} taskProjectId={taskProjectId} onOpenTask={openTask} />}
      </div>
    );
  }

  // === Сетка карточек-превью: группы «Документы» и «Макеты» ===
  return (
    <div data-testid="plans-grid" style={{ display: 'flex', flexDirection: 'column', gap: SP.lg }}>
      {showNoDocuments ? (
        <NoDocuments hasKeyChat={!!onOpenKeyChat} onOpenKeyChat={onOpenKeyChat} isMobile={isMobile} />
      ) : (
        <>
          <div style={{ display: 'flex', flexDirection: 'column', gap: SP.sm }}>
            <GroupHeader label="Документы" count={plans.length} isMobile={isMobile} />
            {plans.length === 0
              ? <div data-testid="plans-empty-docs" style={{ border: `1px dashed ${C.dashed}`, borderRadius: R.md, padding: `${isMobile ? SP.md : SP.sm}px ${SP.md}px`, fontFamily: FONT.sans, fontSize: FS.sm, color: C.textHeading }}>Планов пока нет…</div>
              : <div data-testid="plans-grid-docs" style={{ display: 'grid', gridTemplateColumns: gridColumns(isMobile), gap: SP.md }}>
                {plans.map(p => (
                  <PlanCard key={p.taskId} plan={p} onSelect={() => setSelected({ kind: 'plan', id: p.taskId })} />
                ))}
              </div>}
          </div>
          {device
            ? <MockupDeviceNotice isMobile={isMobile} />
            : <MockupListSection mockups={mockups} onSelect={p => setSelected({ kind: 'mockup', path: p })} isMobile={isMobile} />}
        </>
      )}
    </div>
  );
}

// Общее пустое: «Документов и макетов пока нет» + «Открыть ключевой чат».
function NoDocuments({ hasKeyChat, onOpenKeyChat, isMobile }: { hasKeyChat: boolean; onOpenKeyChat?: () => void; isMobile: boolean }) {
  return (
    <div data-testid="plans-all-empty" style={{ display: 'flex', flexDirection: 'column', alignItems: 'center', gap: SP.md, padding: `${SP.xl}px 0`, textAlign: 'center' }}>
      <span style={{ display: 'inline-flex', alignItems: 'center', gap: SP.xs, fontFamily: FONT.sans, fontSize: FS.sm, color: C.textSecondary }}>
        <MessageCircle size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
        Документов и макетов пока нет
      </span>
      {hasKeyChat && (
        <Button
          variant="ghost"
          size="sm"
          dataTestId="plans-open-key-chat"
          onClick={onOpenKeyChat}
          style={{ color: C.accent, background: C.bgWhite, height: isMobile ? 44 : 32 }}
        >
          Открыть ключевой чат
        </Button>
      )}
    </div>
  );
}

// Счётчик вкладки «Планы и макеты»: документы + живые макеты (без отклонённых).
export function plansTabCount(page: FeaturePage): number {
  const plans = (page.plans ?? []).length;
  const live = splitMockups(page.mockups ?? []).live.length;
  return plans + live;
}
