import { useEffect, useState } from 'react';
import { Hand, UserX, WifiOff } from 'lucide-react';
import { api } from '../../lib/api';
import { C, FS, SP } from '../../lib/design';
import { Button, Notice, Toggle } from '../../components/ui';
import { CapabilityGate } from '../../components/CapabilityGate';
import { AccordionSection } from '../projects/dialogs/AccordionSection';
import { invalidateProjectsCache } from '../projects/useAllProjects';
import { featureReason } from '../../lib/projectCapabilities';
import { handsSectionView } from '../../lib/localHands';
import { ProjectFeature, type Project } from '../../types';
import { HandsProvidersModal } from './HandsProvidersModal';

// Секция «Руки на устройстве» в настройках проекта (ADR-016 §7). Снаружи — только флаг
// local-hands; всё остальное решает handsRefusal с сервера через CapabilityGate, без
// проверок локальности руками. Текст отказа — тот, что вернул сервер.
export function LocalHandsSection({ project, onUpdated }: {
  project: Project;
  onUpdated?: (updated: Project) => void;
}) {
  const [on, setOn] = useState(project.handsEnabled ?? false);
  const [busy, setBusy] = useState(false);
  const [err, setErr] = useState('');
  const [noProviders, setNoProviders] = useState<boolean | null>(null);
  const [providersOpen, setProvidersOpen] = useState(false);

  useEffect(() => {
    let alive = true;
    api.meHandsProviders.get()
      .then(v => { if (alive) setNoProviders(v.providers.length === 0); })
      .catch(() => { /* без списка просто не предупреждаем */ });
    return () => { alive = false; };
  }, []);

  const view = handsSectionView({
    handsEnabled: on,
    refusal: featureReason(project, ProjectFeature.Hands),
    deviceOffline: project.device ? !project.device.online : false,
    noProviders,
  });

  const toggle = (checked: boolean) => {
    const prev = on;
    setOn(checked);
    setBusy(true);
    setErr('');
    api.projects.setHands(project.id, checked)
      .then(updated => {
        invalidateProjectsCache();
        onUpdated?.(updated);
      })
      .catch((e: unknown) => {
        setOn(prev);
        setErr(e instanceof Error && e.message ? e.message : 'Не удалось сохранить');
      })
      .finally(() => setBusy(false));
  };

  const toggleRow = (
    <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: SP.md, minHeight: 40 }}>
      <span style={{ fontSize: FS.sm, color: C.textPrimary, minWidth: 0 }}>
        Разрешить чатам этого проекта управлять окнами на устройстве
      </span>
      <Toggle checked={on} onChange={toggle} disabled={busy || !view.canToggle}
        ariaLabel="Руки на устройстве" />
    </div>
  );

  const explain = (
    <div style={{ fontSize: FS.xs, color: C.textMuted, lineHeight: 1.5 }}>
      ИИ сможет открывать на устройстве любые программы, кроме терминалов и интерпретаторов, и
      видит и трогает только окна, которые открыл сам. Провайдер чата видит снимки этих окон.
      Прервать ход у компьютера можно кнопкой «Стоп» в меню значка агента. Начатые чаты
      подхватят изменение со следующего сообщения.
    </div>
  );

  return (
    <AccordionSection icon={Hand} title="Руки на устройстве" summary={view.summary}
      summaryTone={view.tone}>
      <div data-local-hands-section style={{ display: 'flex', flexDirection: 'column', gap: SP.sm }}>
        <CapabilityGate
          project={project}
          feature={ProjectFeature.Hands}
          title="Руки недоступны"
          fallback={(
            <>
              <div data-hands-refusal style={{ fontSize: FS.sm, color: C.textSecondary, lineHeight: 1.5 }}>
                {view.refusal}
              </div>
              {/* Руки остались включены, а матрица их больше не пускает — дать выключить */}
              {view.canToggle && toggleRow}
            </>
          )}
        >
          {toggleRow}
          {explain}
          {view.offlineNote && (
            <Notice icon={WifiOff}>
              Устройство не в сети — настройка вступит в силу, когда оно вернётся.
            </Notice>
          )}
          {view.noProvidersWarning && (
            <Notice icon={UserX} title="Вы не разрешили руки ни одному провайдеру — ходы пойдут без рук.">
              <Button variant="ghostAccent" size="xs" onClick={() => setProvidersOpen(true)}
                style={{ marginTop: SP.xs }}>
                Выбрать провайдеров
              </Button>
            </Notice>
          )}
        </CapabilityGate>
        {err && <div style={{ fontSize: FS.xs, color: C.dangerText }}>{err}</div>}
      </div>
      {providersOpen && (
        <HandsProvidersModal onClose={() => setProvidersOpen(false)}
          onSaved={v => setNoProviders(v.providers.length === 0)} />
      )}
    </AccordionSection>
  );
}
