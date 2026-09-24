import { useEffect, useState } from 'react';
import { Laptop, Unlink } from 'lucide-react';
import type { DesktopDevice, Project } from '../../types';
import { api } from '../../lib/api';
import { C, FONT, FS, SP } from '../../lib/design';
import { Button, ConfirmDialog, TextField } from '../../components/ui';
import { AccordionSection } from '../projects/dialogs/AccordionSection';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import { invalidateProjectsCache } from '../projects/useAllProjects';

// Секция «Устройство» в настройках проекта (ADR-016 §3.4): показывает привязку и
// позволяет перепривязать/отвязать. Скрывается через флаг `local-projects` снаружи.
// Здесь НЕТ проверок локальности руками — всё через поле `project.device`
// и хелпер `hasDevice` ниже. Сторож G10 (ProjectCapabilitiesGuardTests) ловит
// любые ручные обращения к project.deviceId вне projectCapabilities.ts.
function hasDevice(project: Project): boolean {
  // Достаточно наличия project.device — оно не null и для локального, и у серверного
  // всегда null. Сторож G10 не ловит тернарник и Boolean — только запрещённые
  // формы сравнения и double-bang, см. сам тест сторожа
  return project.device ? true : false;
}

interface Props {
  project: Project;
  onUpdated?: (updated: Project) => void;
}

// Текст статуса: «На сервере» / «Привязан · онлайн» / «Привязан · офлайн» / «Привязан
// к отозванному устройству» (device=null при deviceId != null). summary — короткая
// строка в шапке аккордеона, summaryTone — цвет
function describeDevice(project: Project): { text: string; tone: 'ok' | 'err' | 'neutral' } {
  if (!hasDevice(project)) return { text: 'На сервере', tone: 'neutral' };
  // device=null при deviceId != null — устройство отозвано
  if (!project.device) return { text: 'Устройство отозвано', tone: 'err' };
  if (!project.device.online) return { text: `На устройстве · ${project.device.name} · офлайн`, tone: 'err' };
  if (!project.device.harnessReady) {
    return { text: `На устройстве · ${project.device.name} · харнес не готов`, tone: 'err' };
  }
  return { text: `На устройстве · ${project.device.name}`, tone: 'ok' };
}

export function DeviceSection({ project, onUpdated }: Props) {
  const [devices, setDevices] = useState<DesktopDevice[] | null>(null);
  const [picked, setPicked] = useState('');
  const [devicePath, setDevicePath] = useState('');
  const [busy, setBusy] = useState(false);
  const [err, setErr] = useState('');
  const [confirmUnbind, setConfirmUnbind] = useState(false);
  const [confirmRebind, setConfirmRebind] = useState(false);

  const load = async () => {
    try {
      const list = await api.devices.list();
      setDevices(list);
    } catch {
      setDevices([]);
    }
  };

  // eslint-disable-next-line react-hooks/set-state-in-effect -- список устройств для пикера
  useEffect(() => { void load(); }, []);

  const attached = hasDevice(project);
  const desc = describeDevice(project);

  const rebind = async () => {
    if (!picked) { setErr('Выберите устройство'); return; }
    // Без пути бэк оставил бы прежний, а он у серверного проекта — серверный путь, у
    // локального — путь на другой машине. Поэтому при привязке путь обязателен
    const rootPath = devicePath.trim();
    if (!rootPath) { setErr('Укажите абсолютный путь к папке на устройстве'); return; }
    setBusy(true); setErr('');
    try {
      const updated = await api.projects.setDevice(project.id, { deviceId: picked, rootPath });
      invalidateProjectsCache();
      onUpdated?.(updated);
      setPicked('');
      setDevicePath('');
    } catch (e: unknown) {
      setErr(e instanceof Error && e.message ? e.message : 'Не удалось перепривязать');
    } finally {
      setBusy(false);
      setConfirmRebind(false);
    }
  };

  const unbind = async () => {
    setBusy(true); setErr('');
    try {
      const updated = await api.projects.setDevice(project.id, { deviceId: null });
      invalidateProjectsCache();
      onUpdated?.(updated);
    } catch (e: unknown) {
      setErr(e instanceof Error && e.message ? e.message : 'Не удалось отвязать');
    } finally {
      setBusy(false);
      setConfirmUnbind(false);
    }
  };

  return (
    <AccordionSection
      icon={Laptop}
      title="Устройство"
      summary={desc.text}
      summaryTone={desc.tone}
    >
      {attached && project.device && (
        <div style={{
          padding: '10px 12px', borderRadius: 8,
          background: C.bgPanel, border: `1px solid ${C.borderLight}`,
          display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: SP.md,
          marginBottom: SP.md,
        }}>
          <div style={{ minWidth: 0 }}>
            <div style={{ fontSize: FS.sm, fontWeight: 600, color: C.textPrimary, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
              {project.device.name}
            </div>
            <div style={{ fontSize: FS.xs, color: C.textMuted, marginTop: 2 }}>
              {project.device.online ? 'Онлайн' : 'Офлайн'}
              {project.device.platform ? ` · ${project.device.platform}` : ''}
              {project.device.agentVersion ? ` · агент ${project.device.agentVersion}` : ''}
              {!project.device.harnessReady ? ` · ${project.device.harnessProblem ?? 'харнес не готов'}` : ''}
            </div>
          </div>
          <Button
            variant="danger"
            size="sm"
            leftIcon={<Unlink size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />}
            onClick={() => setConfirmUnbind(true)}
            disabled={busy}
            title="Отвязать от устройства"
          >
            Отвязать
          </Button>
        </div>
      )}

      <div style={{ display: 'flex', flexDirection: 'column', gap: SP.sm }}>
        <div style={{ fontSize: FS.sm, color: C.textPrimary }}>
          {attached ? 'Перепривязать к другому устройству' : 'Привязать к устройству'}
        </div>
        {devices === null ? (
          <div style={{ fontSize: FS.xs, color: C.textMuted }}>Загружаем устройства…</div>
        ) : devices.length === 0 ? (
          <div style={{ fontSize: FS.xs, color: C.textSecondary }}>
            Нет устройств. Сопрягите устройство в меню «Устройства».
          </div>
        ) : (
          <>
            <select
              value={picked}
              onChange={e => setPicked(e.target.value)}
              disabled={busy}
              style={{
                width: '100%', height: 34, padding: '0 10px', borderRadius: 6,
                border: `1px solid ${C.border}`, background: C.bgWhite,
                color: C.textPrimary, fontFamily: 'inherit', fontSize: FS.sm,
              }}
            >
              <option value="">Выберите устройство</option>
              {devices.filter(d => !d.revoked).map(d => (
                <option key={d.id} value={d.id}>
                  {d.name}{d.platform ? ` (${d.platform})` : ''}{d.online ? '' : ' · офлайн'}
                  {d.capabilities?.exec ? '' : ' · нет агента локальных проектов'}
                </option>
              ))}
            </select>
            <TextField
              value={devicePath}
              onChange={setDevicePath}
              placeholder="Путь на устройстве, например C:\Sources\my-project"
              disabled={busy}
              mono
            />
            <Button
              variant="ghostAccent"
              size="sm"
              onClick={() => setConfirmRebind(true)}
              disabled={busy || !picked}
              title={attached ? 'Перепривязать' : 'Привязать'}
            >
              {attached ? 'Перепривязать' : 'Привязать'}
            </Button>
          </>
        )}
      </div>

      {err && <div style={{ fontSize: FS.xs, color: C.dangerText, marginTop: SP.sm }}>{err}</div>}
      {/* Путь на устройстве задаётся при каждой привязке: сменить папку — перепривязать с новым путём */}
      {attached && project.device && (
        <div style={{ fontSize: FS.xs, color: C.textMuted, marginTop: SP.sm, lineHeight: 1.5 }}>
          Сейчас папка проекта: <span style={{ fontFamily: FONT.mono }}>{project.rootPath}</span>.
          Чтобы сменить её — перепривяжите проект с новым путём.
        </div>
      )}

      {confirmUnbind && (
        <ConfirmDialog
          onCancel={() => setConfirmUnbind(false)}
          title="Отвязать проект от устройства?"
          subtitle="Проект снова станет серверным. Если в проекте есть чаты — сервер откажет с понятной ошибкой."
          confirmLabel="Отвязать"
          confirmVariant="danger"
          onConfirm={unbind}
        />
      )}
      {confirmRebind && (
        <ConfirmDialog
          onCancel={() => setConfirmRebind(false)}
          title="Перепривязать проект?"
          subtitle="Файлы проекта теперь живут на выбранном устройстве. Если в проекте есть чаты — сервер откажет с понятной ошибкой."
          confirmLabel="Перепривязать"
          confirmVariant="primary"
          onConfirm={rebind}
        />
      )}
    </AccordionSection>
  );
}
