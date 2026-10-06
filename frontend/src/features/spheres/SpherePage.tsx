import { useCallback, useEffect, useState } from 'react';
import type { CSSProperties, ReactNode } from 'react';
import { Laptop, MoreVertical, SquarePen, Trash2, UserPlus } from 'lucide-react';
import type { DesktopDevice, ProjectGroup, SphereOverview } from '../../types';
import { api } from '../../lib/api';
import { C, FONT, FS, R, SHADOW } from '../../lib/design';
import { plural } from '../../lib/plural';
import { PRIORITY_COLOR, PRIORITY_LABEL } from '../../lib/tasks';
import { useIsMobile } from '../../lib/breakpoints';
import { BackButton, Badge, Button, IconButton, Menu, MenuItem } from '../../components/ui';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import { SphereTile } from './SphereTile';
import { SphereDialog } from './SphereDialog';
import { SphereDeleteDialog } from './SphereDeleteDialog';
import { SphereMemorySection } from './SphereMemory';
import { showToast } from '../../lib/toast';
import { PersonaWizard } from '../personas/PersonaWizard';
import { describeLoadError } from './sphereLogic';

interface Props {
  sphere: ProjectGroup;
  existingCount: number;
  onBack: () => void;
  onOpenProject: (projectId: string) => void;
  onChanged: (sphere: ProjectGroup) => void;
  onDeleted: (id: string) => void;
}

const TASKS_SHOWN = 5;
const CHARTER_COLLAPSED = 160;

const card: CSSProperties = {
  background: C.bgCard, border: `1px solid ${C.borderLight}`, borderRadius: R.xl, boxShadow: SHADOW.card,
  padding: '14px 16px', display: 'flex', flexDirection: 'column', gap: 10, boxSizing: 'border-box',
};

function Card({ title, subtitle, children }: { title: string; subtitle?: string; children: ReactNode }) {
  return (
    <section style={card}>
      <div>
        <div style={{ fontFamily: FONT.serif, fontSize: FS.xl, fontWeight: 500, color: C.textHeading }}>{title}</div>
        {subtitle && <div style={{ fontSize: FS.sm, color: C.textMuted, marginTop: 2 }}>{subtitle}</div>}
      </div>
      {children}
    </section>
  );
}

const hint: CSSProperties = { fontSize: FS.base, color: C.textMuted, lineHeight: 1.5 };

// Страница сферы: проекты (локальный — с бейджем устройства и причиной из capabilities),
// команда, память (две полки) и открытые задачи со всех проектов сферы.
export function SpherePage({ sphere, existingCount, onBack, onOpenProject, onChanged, onDeleted }: Props) {
  const isMobile = useIsMobile();
  const [data, setData] = useState<SphereOverview | null>(null);
  const [error, setError] = useState<{ kind: 'notFound' | 'network'; text: string } | null>(null);
  // Живой счётчик записей сферы: память на странице меняется без перезагрузки сводки
  const [memoryCount, setMemoryCount] = useState<number | null>(null);
  const [devices, setDevices] = useState<DesktopDevice[]>([]);
  const [charterOpen, setCharterOpen] = useState(false);
  const [menu, setMenu] = useState<DOMRect | null>(null);
  const [dialog, setDialog] = useState<'edit' | 'delete' | null>(null);
  const [showAllTasks, setShowAllTasks] = useState(false);
  // Создание персоны сразу с зоной этой сферы (мастер вместо страницы)
  const [creatingPersona, setCreatingPersona] = useState(false);

  const load = useCallback(() => {
    api.spheres.overview(sphere.id)
      .then(o => { setData(o); setError(null); })
      .catch(e => setError(describeLoadError(e)));
  }, [sphere.id]);

  useEffect(() => { load(); }, [load]);
  // Имена устройств для бейджей локальных проектов; сбой не мешает странице
  useEffect(() => { api.devices.list().then(setDevices).catch(() => {}); }, []);

  const projectName = (id: string | null) => data?.projects.find(p => p.id === id)?.name;
  const today = new Date().toISOString().slice(0, 10);
  const charter = data?.sphere.charter ?? sphere.charter ?? '';
  const longCharter = charter.length > CHARTER_COLLAPSED;
  const tasks = data?.openTasks ?? [];
  const visibleTasks = showAllTasks ? tasks : tasks.slice(0, TASKS_SHOWN);

  const counts = data ? [
    `${data.projects.length} ${plural(data.projects.length, 'проект', 'проекта', 'проектов')}`,
    `${data.team.length} ${plural(data.team.length, 'персона', 'персоны', 'персон')}`,
    `${memoryCount ?? data.memory.count} ${plural(memoryCount ?? data.memory.count, 'запись памяти', 'записи памяти', 'записей памяти')}`,
    `${data.openTasks.length} ${plural(data.openTasks.length, 'открытая задача', 'открытые задачи', 'открытых задач')}`,
  ].join(' · ') : '';

  const projectsCard = (
    <Card title="Проекты">
      {data && data.projects.length === 0 && (
        <div style={hint}>В сфере пока нет проектов. Перенесите проект через его меню или создайте новый.</div>
      )}
      {data?.projects.map(p => {
        const caps = p.capabilities;
        const local = caps.host === 'device';
        const reason = caps.files.reason ?? caps.exec.reason ?? caps.platform.reason ?? caps.serverContent.reason;
        const deviceName = devices.find(d => d.id === caps.deviceId)?.name;
        return (
          <div key={p.id}
            style={{ display: 'flex', flexDirection: 'column', gap: 4, padding: '8px 0', borderTop: `1px solid ${C.divider}` }}>
            <div style={{ display: 'flex', alignItems: 'center', gap: 8, flexWrap: 'wrap' }}>
              <button type="button" onClick={() => onOpenProject(p.id)}
                style={{ background: 'none', border: 'none', padding: 0, cursor: 'pointer', fontFamily: FONT.sans, fontSize: FS.md, fontWeight: 600, color: C.textHeading, textAlign: 'left', minHeight: isMobile ? 40 : undefined }}>
                {p.name}
              </button>
              {local && (
                <Badge tone={reason ? 'warning' : 'neutral'} size="xs" icon={<Laptop size={ICON_SIZE.xs} />}>
                  {deviceName ? `На устройстве «${deviceName}»` : 'На устройстве'}
                </Badge>
              )}
            </div>
            {local && reason && (
              <div style={{ fontSize: FS.sm, color: C.warningText, lineHeight: 1.45 }}>
                {reason}. Пока устройство не вернётся, персоны сферы не видят файлы проекта и не могут работать
                в его чатах; задачи и память проекта доступны.
              </div>
            )}
          </div>
        );
      })}
    </Card>
  );

  const teamCard = (
    <Card title="Команда" subtitle="Работают во всех проектах сферы и пишут в её память.">
      <Button variant="secondary" size="sm" leftIcon={<UserPlus size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />}
        onClick={() => setCreatingPersona(true)}>
        Добавить персону в команду
      </Button>
      {data && data.team.length === 0 && (
        <div style={hint}>Персон пока нет. Персона сферы работает во всех её проектах и помнит решения соседних.</div>
      )}
      {data?.team.map(m => (
        <div key={m.id} style={{ display: 'flex', alignItems: 'baseline', gap: 8, flexWrap: 'wrap', padding: '6px 0', borderTop: `1px solid ${C.divider}` }}>
          <span style={{ fontSize: FS.md, fontWeight: 600, color: C.textHeading }}>
            {m.role ? `${m.role} (${m.name})` : m.name}
          </span>
          <span style={{ fontSize: FS.sm, color: C.textMuted }}>@{m.handle}</span>
        </div>
      ))}
    </Card>
  );

  const tasksCard = (
    <Card title="Открытые задачи" subtitle="Из всех проектов сферы: сначала срочные, потом по сроку.">
      {data && tasks.length === 0 && <div style={hint}>Открытых задач нет.</div>}
      {visibleTasks.map(t => {
        const overdue = !!t.dueDate && t.dueDate < today;
        const pName = projectName(t.projectId);
        return (
          <div key={t.id} style={{ display: 'flex', alignItems: 'center', gap: 8, padding: '6px 0', borderTop: `1px solid ${C.divider}` }}>
            <span title={PRIORITY_LABEL[t.priority]}
              style={{ width: 8, height: 8, borderRadius: '50%', background: PRIORITY_COLOR[t.priority], flexShrink: 0 }} />
            <span style={{ flex: 1, minWidth: 0, fontSize: FS.base, color: C.textPrimary, overflowWrap: 'anywhere' }}>{t.title}</span>
            {pName && <Badge tone="neutral" size="xs">{pName}</Badge>}
            {t.dueDate && (
              <span style={{ fontSize: FS.xs, flexShrink: 0, color: overdue ? C.dangerText : C.textMuted }}>{t.dueDate}</span>
            )}
          </div>
        );
      })}
      {tasks.length > TASKS_SHOWN && !showAllTasks && (
        <Button variant="ghostAccent" size="sm" onClick={() => setShowAllTasks(true)} style={{ alignSelf: 'flex-start' }}>
          и ещё {tasks.length - TASKS_SHOWN}
        </Button>
      )}
    </Card>
  );

  const memoryCard = (
    <Card title="Память" subtitle="В проекте сферы персоны помнят обе полки.">
      <SphereMemorySection sphereId={sphere.id} onCountChange={setMemoryCount} />
    </Card>
  );

  // Мастер создания: после «Готово» персона уже в команде сферы — закрываем и обновляем сводку
  const finishCreate = (p: { name: string }) => {
    setCreatingPersona(false);
    load();
    showToast('Персоны', `«${p.name}» добавлена в команду сферы «${sphere.name}».`);
  };
  if (creatingPersona) {
    return (
      <PersonaWizard
        scope="sphere"
        sphereId={sphere.id}
        projects={[]}
        onOpenStudio={finishCreate}
        onStartChat={finishCreate}
        onCancel={() => setCreatingPersona(false)}
        onBack={() => setCreatingPersona(false)}
        isMobile={isMobile}
      />
    );
  }

  return (
    <div style={{ flex: 1, minHeight: 0, overflowY: 'auto', padding: isMobile ? '12px 16px 18px' : '14px 26px 18px', display: 'flex', flexDirection: 'column', gap: 14 }}>
      <div style={{ display: 'flex', flexDirection: 'column', gap: 12, paddingBottom: 12, borderBottom: `2px solid ${sphere.color || C.border}` }}>
        <BackButton onClick={onBack}>Проекты</BackButton>
        <div style={{ display: 'flex', alignItems: 'center', gap: 12 }}>
          <SphereTile sphere={sphere} size={44} />
          <div style={{ flex: 1, minWidth: 0 }}>
            <div style={{ fontFamily: FONT.serif, fontSize: isMobile ? FS.h2 : FS.h1, fontWeight: 500, color: C.textHeading, letterSpacing: '-0.01em', overflowWrap: 'anywhere' }}>
              {sphere.name}
            </div>
            <div style={{ fontSize: FS.sm, color: C.textMuted }}>{counts || ' '}</div>
          </div>
          {!isMobile && (
            <Button variant="secondary" size="md" onClick={() => setDialog('edit')}>Изменить</Button>
          )}
          <IconButton title="Действия" ariaLabel="Действия со сферой" size={isMobile ? 'lg' : 'sm'} active={!!menu}
            onClick={e => { const r = e.currentTarget.getBoundingClientRect(); setMenu(prev => (prev ? null : r)); }}>
            <MoreVertical size={ICON_SIZE.sm} fill="currentColor" />
          </IconButton>
          {menu && (
            <Menu anchor={menu} onClose={() => setMenu(null)} maxHeight={2 * 34 + 10} gap={4}>
              <MenuItem label="Изменить сферу" onClick={() => { setMenu(null); setDialog('edit'); }}
                icon={<SquarePen size={15} strokeWidth={ICON_STROKE} />} />
              <MenuItem label="Удалить сферу" danger onClick={() => { setMenu(null); setDialog('delete'); }}
                icon={<Trash2 size={15} strokeWidth={ICON_STROKE} />} />
            </Menu>
          )}
        </div>
        {charter && (
          <div style={{ fontSize: FS.base, color: C.textSecondary, lineHeight: 1.5, whiteSpace: 'pre-wrap', overflowWrap: 'anywhere' }}>
            {longCharter && !charterOpen ? `${charter.slice(0, CHARTER_COLLAPSED).trimEnd()}…` : charter}
            {longCharter && (
              <>
                {' '}
                <Button variant="ghostAccent" size="sm" onClick={() => setCharterOpen(o => !o)}>
                  {charterOpen ? 'Свернуть' : 'Показать полностью'}
                </Button>
              </>
            )}
          </div>
        )}
        {data && !charter && data.projects.length === 0 && data.team.length === 0 && (
          <div style={hint}>Пока пусто</div>
        )}
      </div>

      {error && (
        <div role="alert" style={{ ...hint, color: C.dangerText }}>
          {error.text}{' '}
          {error.kind === 'notFound' ? (
            <Button variant="ghostAccent" size="sm" onClick={onBack}>К списку проектов</Button>
          ) : (
            <Button variant="ghostAccent" size="sm" onClick={load}>Повторить</Button>
          )}
        </div>
      )}

      {/* Две колонки на flex-wrap с базисами 380 и 320; на узком экране — одна: проекты, команда, задачи, память */}
      {isMobile ? (
        <div style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
          {projectsCard}{teamCard}{tasksCard}{memoryCard}
        </div>
      ) : (
        <div style={{ display: 'flex', flexWrap: 'wrap', gap: 14, alignItems: 'flex-start' }}>
          <div style={{ flex: '1 1 380px', minWidth: 0, display: 'flex', flexDirection: 'column', gap: 14 }}>
            {projectsCard}{tasksCard}
          </div>
          <div style={{ flex: '1 1 320px', minWidth: 0, display: 'flex', flexDirection: 'column', gap: 14 }}>
            {teamCard}{memoryCard}
          </div>
        </div>
      )}

      {dialog === 'edit' && (
        <SphereDialog
          sphere={sphere}
          existingCount={existingCount}
          onSaved={s => { setDialog(null); onChanged(s); load(); }}
          onClose={() => setDialog(null)}
        />
      )}
      {dialog === 'delete' && (
        <SphereDeleteDialog
          sphere={sphere}
          onDeleted={id => { setDialog(null); onDeleted(id); }}
          onClose={() => setDialog(null)}
        />
      )}
    </div>
  );
}
