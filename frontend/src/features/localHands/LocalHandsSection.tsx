import { useState } from 'react';
import { Hand, WifiOff } from 'lucide-react';
import { api } from '../../lib/api';
import { C, FS, SP } from '../../lib/design';
import { Notice, Toggle } from '../../components/ui';
import { CapabilityGate } from '../../components/CapabilityGate';
import { AccordionSection } from '../projects/dialogs/AccordionSection';
import { invalidateProjectsCache } from '../projects/useAllProjects';
import { featureReason } from '../../lib/projectCapabilities';
import { handsSectionView } from '../../lib/localHands';
import { ProjectFeature, type Project } from '../../types';

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

  const view = handsSectionView({
    handsEnabled: on,
    refusal: featureReason(project, ProjectFeature.Hands),
    deviceOffline: project.device ? !project.device.online : false,
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
      может видеть и трогать любые окна на этом компьютере, в том числе снимать экран. Руки
      работают с любым провайдером чата: провайдер видит текст этих окон, а если у него есть
      зрение — и снимки экрана.
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
        </CapabilityGate>
        {err && <div style={{ fontSize: FS.xs, color: C.dangerText }}>{err}</div>}
      </div>
    </AccordionSection>
  );
}
