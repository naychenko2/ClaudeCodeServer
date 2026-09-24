import { useEffect, useState } from 'react';
import { Laptop, Unlink } from 'lucide-react';
import type { DesktopDevice, Project } from '../../types';
import { api } from '../../lib/api';
import { C, FONT, FS, R, SP } from '../../lib/design';
import { Button, ConfirmDialog, Select, TextField } from '../../components/ui';
import { AccordionSection } from '../projects/dialogs/AccordionSection';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import { invalidateProjectsCache } from '../projects/useAllProjects';
import { deviceLabel, isLocalProjectDevice, useNoDevicesHint } from './deviceOptions';

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

// Текст статуса: «На сервере» / «Привязан · в сети» / «Привязан · не в сети» / «Привязан
// к отозванному устройству» (device=null при deviceId != null). summary — короткая
// строка в шапке аккордеона, summaryTone — цвет
function describeDevice(project: Project): { text: string; tone: 'ok' | 'err' | 'neutral' } {
  if (!hasDevice(project)) return { text: 'На сервере', tone: 'neutral' };
  // device=null при deviceId != null — устройство отозвано
  if (!project.device) return { text: 'Устройство отозвано', tone: 'err' };
  if (!project.device.online) return { text: `На устройстве · ${project.device.name} · не в сети`, tone: 'err' };
  if (!project.device.harnessReady) {
    return { text: `На устройстве · ${project.device.name} · агент не готов`, tone: 'err' };
  }
  return { text: `На устройстве · ${project.device.name}`, tone: 'ok' };
}

export function DeviceSection({ project, onUpdated }: Props) {
  const [devices, setDevices] = useState<DesktopDevice[] | null>(null);
  const noDevicesHint = useNoDevicesHint();
  const [picked, setPicked] = useState('');
  const [devicePath, setDevicePath] = useState('');
  const [busy, setBusy] = useState(false);
  const [err, setErr] = useState('');
  const [confirmUnbind, setConfirmUnbind] = useState(false);
  const [confirmRebind, setConfirmRebind] = useState(false);

  const load = async () => {
    try {
      const list = await api.devices.list();
      setDevices(list.filter(isLocalProjectDevice));
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
          padding: `${SP.sm}px ${SP.md}px`, borderRadius: R.md,
          background: C.bgPanel, border: `1px solid ${C.borderLight}`,
          display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: SP.md,
          marginBottom: SP.md,
        }}>
          <div style={{ minWidth: 0 }}>
            <div style={{ fontSize: FS.sm, fontWeight: 600, color: C.textPrimary, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
              {project.device.name}
            </div>
            <div style={{ fontSize: FS.xs, color: C.textMuted, marginTop: 2 }}>
              {project.device.online ? 'В сети' : 'Не в сети'}
              {project.device.platform ? ` · ${project.device.platform}` : ''}
              {project.device.agentVersion ? ` · агент ${project.device.agentVersion}` : ''}
              {!project.device.harnessReady ? ` · ${project.device.harnessProblem ?? 'агент устройства не готов'}` : ''}
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
            {noDevicesHint}
          </div>
        ) : (
          <>
            <Select
              value={picked}
              onChange={setPicked}
              disabled={busy}
              placeholder="Выберите устройство"
              options={devices.map(d => ({ value: d.id, label: deviceLabel(d) }))}
            />
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
          subtitle="Проект снова станет серверным. Нельзя, если в проекте уже есть чаты."
          confirmLabel="Отвязать"
          confirmVariant="danger"
          onConfirm={unbind}
        />
      )}
      {confirmRebind && (
        <ConfirmDialog
          onCancel={() => setConfirmRebind(false)}
          title="Перепривязать проект?"
          subtitle="Файлы проекта будут браться с выбранного устройства. Нельзя, если в проекте уже есть чаты."
          confirmLabel="Перепривязать"
          confirmVariant="primary"
          onConfirm={rebind}
        />
      )}
    </AccordionSection>
  );
}
