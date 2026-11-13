// Вкладка «Планы и макеты» страницы фичи (волна 4, вариант 5В точки 5).
//
// Раскладка: сетка карточек-превью — группы «Документы», «Прочие документы» и «Макеты»
// (3 колонки на десктопе, 1 на 360). Клик по карточке — читалка или карточка макета на
// всю ширину с «‹ Все документы» для возврата к сетке; клик по «прочему документу» —
// открытие файла проекта (onOpenFile).
//
// Документы (plans): порядок cut → design → plan → consult; карточка — текстовый тег роли
// (тот же текст и цвета, что метка читалки), заголовок, «автор · дата», «без текста» (M27).
// Читалка — FeaturePlanReader (markdown через общий рендер). Прочие документы (FS-F1e):
// materials[] из бэка (файлы задач фичи с taskId автора — ADR, спеки, записки): фронт их не
// фильтрует — состав и порядок ровно как пришли; клик — onOpenFile(path, { taskId });
// карточка — «имя · каталог · задача {stepRef}» (M28). Старый ответ (materials нет) —
// прежний путь: plans[].mockupPaths, не покрытые ни одним макетом (M7), карточка без
// ссылки на задачу. Макеты — FeatureMockups (карточки-превью с миниатюрой-заглушкой,
// карточка, «Прошлые версии»).
//
// Состояния: документов нет, макеты есть → «Планов пока нет…»; mockupsUnavailable='device'
// → «Макеты локального проекта видны только на устройстве» (и «прочих документов» нет:
// файлы локального проекта на устройстве); нет ничего → общее пустое.

import { useMemo, useState } from 'react';
import { ChevronLeft, FileText, MessageCircle } from 'lucide-react';
import { C, R, FS, FONT, SP } from '../../lib/design';
import { ICON_SIZE, ICON_STROKE } from '../ui/icons';
import { getPersonaById } from '../../lib/personas';
import { dateTimeLabel } from '../chat/feature/featureTime';
import { splitMockups, isCurrentOfGroup } from '../../lib/mockupVersions';
import { recordFeatureReturn } from '../../lib/featureBackNav';
import { openTaskInSection } from '../../lib/tasks';
import { FeaturePlanReader, PLAN_ROLE_LABEL, PLAN_ROLE_STYLE } from './FeaturePlanReader';
import { Button } from '../ui/Button';
import { buildNavMarks, type NavMarks } from '../../lib/featureNavMarks';
import { MockupCard, MockupListSection, MockupDeviceNotice } from './FeatureMockups';
import type { FeaturePage } from '../../types/featureIndex';
import type { FeaturePlan, FeatureMockup, FeatureMaterial } from '../../types/featurePlans';

// Выбран в детали: документ (plan) или макет (mockup). null — сетка.
type Selection = { kind: 'plan'; id: string } | { kind: 'mockup'; path: string } | null;

export interface FeaturePlansTabProps {
  // Данные страницы (plans / mockups / mockupsUnavailable).
  page: FeaturePage;
  isMobile?: boolean;
  // Материалы задач фичи (FS-B3a): группа «Прочие документы» строится по ним (FS-F1e).
  materials?: FeatureMaterial[];
  // Открыть файл проекта (существующий просмотр, HTML в песочнице).
  onOpenFile?: (path: string, opts?: { taskId?: string; branch?: string }) => void;
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

// Карточка-превью документа (сетка, M27): текстовый тег роли · заголовок · «автор · дата» ·
// «без текста». Тег — тот же текст и цвета, что метка роли в читалке (PLAN_ROLE_*): один
// документ подписан одинаково и в списке, и в читалке.
// Высота карточки не зависит от ширины: сетка сама сужает колонки на 360 (одна колонка).
function PlanCard({ plan, onSelect }: {
  plan: FeaturePlan; onSelect: () => void;
}) {
  const [hover, setHover] = useState(false);
  const persona = plan.authorPersonaId ? getPersonaById(plan.authorPersonaId) : undefined;
  const authorName = persona?.name ?? (plan.authorPersonaId ? plan.authorPersonaId : null);
  // «12.11 14:20» — дата и время одной меткой (формат макета, локальный пояс).
  const time = dateTimeLabel(plan.completedAt);
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
        {/* Текстовый тег роли (M27): та же метка, что в шапке читалки (plan-reader-role). */}
        <span data-testid="plan-role-tag" style={{ fontSize: FS.xs, fontWeight: 600, color: PLAN_ROLE_STYLE[plan.role].fg, background: PLAN_ROLE_STYLE[plan.role].bg, borderRadius: R.sm, padding: '0 6px', whiteSpace: 'nowrap', flexShrink: 0 }}>
          {PLAN_ROLE_LABEL[plan.role]}
        </span>
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

// Видимые документы — ровно тот список, что рисует сетка: порядок cut → design →
// plan → consult; дедуп планов (решение Андрея): переписанный план — показываем
// ПОСЛЕДНИЙ по createdAt; разрезы, дизайны и консультации — все. Неизвестная роль
// в группы не попадает (сетку не роняет). Единый источник и для карточек, и для
// счётчика вкладки: иначе бейдж расходится с числом карточек (хвост ревью R-M3).
export function visiblePlans(plans: FeaturePlan[]): FeaturePlan[] {
  const byRole = (role: FeaturePlan['role']) => plans.filter(p => p.role === role);
  // Последний план по createdAt: сортируем по возрастанию и берём хвост.
  const planTasks = [...byRole('plan')].sort((a, b) => a.createdAt.localeCompare(b.createdAt));
  const latestPlan = planTasks.length > 0 ? planTasks[planTasks.length - 1] : null;
  return [
    ...byRole('cut'),
    ...byRole('design'),
    ...(latestPlan ? [latestPlan] : []),
    ...byRole('consult'),
  ];
}

// «Прочие документы» — прежний путь (M7), работает только когда ответ без materials
// (старый ответ): с FS-F1e группа строится из materials[], состав и порядок которых
// держит бэк (FeatureMaterials.Build: отбор, дедуп, потолок 30) и фронт не трогает.
// Файлы из plans[].mockupPaths, не показанные в группе «Макеты».
// Бэк (M5) несёт в mockupPaths, кроме макетов из текста, файлы, которые задача приложила
// через LinkedFiles (ADR, спеки, записки вне docs/mockups); группа «Макеты» рисует только
// каталог docs/mockups. Путь, не совпавший ни с одним макетом (его path или любой из его
// files), — документ фичи. Порядок: по планам (как их дал бэк), внутри — по mockupPaths;
// дубли схлопнуты регистронезависимо: каталог макетов нормализует регистр пути
// (MockupCatalog.DistinctLinkedHtml), и путь, отличающийся регистром, не должен
// дублировать карточку макета. Список — единый для карточек и для счётчика
// plansTabCount (R-M3: бейдж не должен расходиться с числом карточек).
function otherDocPaths(plans: FeaturePlan[], mockups: FeatureMockup[]): string[] {
  const covered = new Set<string>();
  for (const m of mockups) {
    covered.add(m.path.toLowerCase());
    for (const f of m.files) covered.add(f.path.toLowerCase());
  }
  const out: string[] = [];
  const seen = new Set<string>();
  for (const p of plans) {
    for (const path of p.mockupPaths) {
      const key = path.toLowerCase();
      if (covered.has(key) || seen.has(key)) continue;
      seen.add(key);
      out.push(path);
    }
  }
  return out;
}

// Элемент группы «Прочие документы»: материал из бэка (FS-F1e) или, для старого ответа
// (без materials), путь из plans[].mockupPaths — без автора. taskRef — видимая ссылка на
// задачу-автора (M28): stepRef задачи, иначе её короткий id.
type OtherDocItem = { path: string; taskId?: string; taskRef?: string };

// Карточка «прочего документа» (M28): имя файла + строка «каталог · задача {ref}» (mono).
// Каталог вместо полного пути: путь целиком — в подсказке (title) и в onOpenFile.
// Клик → onOpenFile (когда дан; без него карточка не интерактивна — не обещаем то,
// что не можем). Для материала — с taskId автора (вьюер откроет файл из дерева задачи);
// для пути старого ответа — как прежде, без opts.
function OtherDocCard({ path, taskRef, onOpen }: { path: string; taskRef?: string; onOpen?: () => void }) {
  const [hover, setHover] = useState(false);
  const name = path.split('/').pop() || path;
  const slash = path.lastIndexOf('/');
  const dir = slash > 0 ? path.slice(0, slash) : null;
  const sub = [dir, taskRef ? `задача ${taskRef}` : null].filter(Boolean).join(' · ');
  return (
    <div
      data-testid="other-doc-row"
      role={onOpen ? 'button' : undefined}
      tabIndex={onOpen ? 0 : undefined}
      onClick={onOpen}
      onKeyDown={e => { if (onOpen && (e.key === 'Enter' || e.key === ' ')) { e.preventDefault(); onOpen(); } }}
      onMouseEnter={() => setHover(true)}
      onMouseLeave={() => setHover(false)}
      title={path}
      style={{
        background: hover ? C.bgSelected : C.bgCard,
        border: `1px solid ${C.border}`,
        borderRadius: R.md,
        padding: `${SP.md}px ${SP.md}px`,
        display: 'flex', flexDirection: 'column', gap: SP.sm,
        cursor: onOpen ? 'pointer' : 'default',
      }}
    >
      <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm }}>
        <FileText size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} style={{ color: C.textMuted, flexShrink: 0 }} />
        {/* Имя файла — до двух строк; путь — строкой ниже (mono, muted). */}
        <span style={{ fontFamily: FONT.sans, fontSize: FS.sm, fontWeight: 600, color: C.textHeading, display: '-webkit-box', WebkitLineClamp: 2, WebkitBoxOrient: 'vertical', overflow: 'hidden', textOverflow: 'ellipsis', minWidth: 0, flex: 1 }}>
          {name}
        </span>
      </div>
      {sub && (
        <span style={{ fontFamily: FONT.mono, fontSize: FS.xs, color: C.textMuted, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
          {sub}
        </span>
      )}
    </div>
  );
}

export function FeaturePlansTab({ page, isMobile = false, materials, onOpenFile, onOpenTask, onOpenKeyChat, marks: marksProp, featureId = null, projectId = null }: FeaturePlansTabProps) {
  const marks = useMemo(() => marksProp ?? buildNavMarks(page.run, page.chats), [marksProp, page]);
  // Проект эпика — область перехода в задачу; нет эпика — проект страницы.
  const taskProjectId = page.run.epic?.projectId ?? projectId;
  // Переход в задачу документа/макета: точка возврата «← к фиче · Планы» + адрес с проектом эпика.
  const openTask = (taskId: string) => {
    if (featureId) recordFeatureReturn(featureId, { kind: 'task', id: taskId }, page.run.epic?.title ?? null, 'plans');
    openTaskInSection({ id: taskId, projectId: taskProjectId ?? undefined });
  };
  // Документы — видимый список (тот же, что считает plansTabCount).
  const plans = useMemo(() => visiblePlans(page.plans ?? []), [page.plans]);
  const mockups: FeatureMockup[] = page.mockups ?? [];
  const device = page.mockupsUnavailable === 'device';
  // «Прочие документы» (FS-F1e): материалы из бэка — состав и порядок ровно как пришли
  // (отбор, дедуп и потолок держит FeatureMaterials.Build; фронт не фильтрует и не режет).
  // Старый ответ (materials нет) — прежний путь по ВСЕМ планам (материалы фичи не зависят
  // от того, чья карточка показана). При 'device' группы нет: файлы локального проекта на
  // устройстве, страница их не откроет — та же причина, что у плашки макетов «на устройстве».
  // Ссылки задач для строки «прочих документов» (M28): stepRef из задач этого run
  // (epic/steps/reviews/qa/final/merge/cut/plan); у задачи без stepRef (план, разрез,
  // консультация) или отсутствующей в run (старые ответы) — короткий id, как в других
  // подписях проекта («Приёмка · {id}»): ссылка есть всегда, выдуманного шага нет.
  const taskRefs = useMemo(() => {
    const refs = new Map<string, string>();
    const add = (t?: { id: string; stepRef?: string | null } | null) => { if (t?.stepRef) refs.set(t.id, t.stepRef); };
    add(page.run.epic); add(page.run.final); add(page.run.merge);
    for (const l of [page.run.steps, page.run.reviews, page.run.qa, page.run.cut, page.run.plan]) {
      for (const t of l ?? []) add(t);
    }
    return refs;
  }, [page.run]);
  const otherDocs: OtherDocItem[] = device
    ? []
    : materials
      ? materials.map(m => ({ path: m.path, taskId: m.taskId, taskRef: taskRefs.get(m.taskId) ?? m.taskId.slice(0, 8) }))
      : otherDocPaths(page.plans ?? [], mockups).map(path => ({ path }));

  // Сетка — состояние по умолчанию (5В: «сначала сетка карточек»). Если выбранный
  // документ/макет исчез из данных (дедуп, обновление) — selectedPlan/selectedMockup
  // null → снова сетка, пустой детали нет.
  const [selected, setSelected] = useState<Selection>(null);
  const selectedPlan = selected?.kind === 'plan' ? plans.find(p => p.taskId === selected.id) ?? null : null;
  const selectedMockup = selected?.kind === 'mockup' ? mockups.find(m => m.path === selected.path) ?? null : null;
  // Выбранный макет — прошлая версия (не текущая в группе) → чип «заменён» в карточке.
  const selectedMockupReplaced = selectedMockup ? !isCurrentOfGroup(selectedMockup, mockups) : false;

  const hasAnything = plans.length > 0 || mockups.length > 0 || otherDocs.length > 0;
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
          {otherDocs.length > 0 && (
            <div style={{ display: 'flex', flexDirection: 'column', gap: SP.sm }}>
              <GroupHeader label="Прочие документы" count={otherDocs.length} isMobile={isMobile} />
              <div data-testid="plans-grid-other-docs" style={{ display: 'grid', gridTemplateColumns: gridColumns(isMobile), gap: SP.md }}>
                {otherDocs.map(d => (
                  // Идентичность карточки — path (бэковый дедуп гарантирует уникальность), автор — taskId.
                  <OtherDocCard key={d.path} path={d.path} taskRef={d.taskRef} onOpen={onOpenFile ? () => (d.taskId ? onOpenFile(d.path, { taskId: d.taskId }) : onOpenFile(d.path)) : undefined} />
                ))}
              </div>
            </div>
          )}
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

// Счётчик вкладки «Материалы»: видимые документы (ровно тот список, что
// рисует сетка: дедуп планов и неизвестные роли не считаются — хвост ревью R-M3)
// + «прочие документы» (тот же источник, что рисует сетку: materials — их число,
// старый ответ — otherDocPaths; при 'device' группы нет — 0) + живые макеты (без отклонённых).
export function plansTabCount(page: FeaturePage): number {
  const docs = visiblePlans(page.plans ?? []).length;
  const live = splitMockups(page.mockups ?? []).live.length;
  const other = page.mockupsUnavailable === 'device' ? 0
    : page.materials ? page.materials.length : otherDocPaths(page.plans ?? [], page.mockups ?? []).length;
  return docs + live + other;
}
