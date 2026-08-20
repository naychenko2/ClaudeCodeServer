import { useState } from 'react';
import { MonitorSmartphone } from 'lucide-react';
import { api } from '../../lib/api';
import { C, FS, SP } from '../../lib/design';
import { Toggle } from '../../components/ui';
import { AccordionSection } from '../projects/dialogs/AccordionSection';
import type { Project } from '../../types';

// Секция «Десктопный агент» в настройках проекта (ADR-008): вторая половина оси выдачи
// грани — «проект + десктопный чат». Ось проектная, а не персональная: привязка персоне
// действовала бы во всех её чатах, включая ночной tasks-executor.
//
// Тумблер — рубильник, а не только состав будущих ходов: выключение гасит живые сеансы
// рук проекта, и об этом мы говорим вслух, а не молча.
export function DesktopFacetSection({ project, onUpdated }: {
  project: Project;
  onUpdated?: (updated: Project) => void;
}) {
  const [on, setOn] = useState(project.desktopAgentEnabled ?? false);
  const [busy, setBusy] = useState(false);
  const [note, setNote] = useState('');
  const [err, setErr] = useState('');

  const toggle = (checked: boolean) => {
    const prev = on;
    setOn(checked);
    setBusy(true);
    setErr('');
    setNote('');
    api.projects.setDesktopAgent(project.id, checked)
      .then(res => {
        onUpdated?.(res.project);
        if (!checked && res.handsStopped > 0) {
          setNote(res.handsStopped === 1
            ? 'Один идущий сеанс рук погашен.'
            : `Погашено сеансов рук: ${res.handsStopped}.`);
        }
      })
      .catch((e: unknown) => {
        setOn(prev);
        setErr(e instanceof Error && e.message ? e.message : 'Не удалось сохранить');
      })
      .finally(() => setBusy(false));
  };

  return (
    <AccordionSection
      icon={MonitorSmartphone}
      title="Десктопный агент"
      summary={on ? 'Грань выдаётся десктопным чатам' : 'Выключен'}
      summaryTone={on ? 'ok' : 'neutral'}
    >
      <div style={{
        display: 'flex', alignItems: 'center', justifyContent: 'space-between',
        gap: SP.md, minHeight: 40,
      }}>
        <span style={{ fontSize: FS.sm, color: C.textPrimary }}>
          Разрешить десктопным чатам этого проекта звать руки на устройстве
        </span>
        <Toggle checked={on} onChange={toggle} disabled={busy}
          ariaLabel="Десктопный агент в этом проекте" />
      </div>

      <div style={{ fontSize: FS.xs, color: C.textMuted, marginTop: SP.xs }}>
        Инструменты грани получают только десктопные чаты этого проекта — и только пока
        человек начал сеанс со своего компьютера. Выключение убирает руки сразу, а не
        со следующего хода.
      </div>

      {note && <div style={{ fontSize: FS.xs, color: C.textSecondary, marginTop: SP.xs }}>{note}</div>}
      {err && <div style={{ fontSize: FS.xs, color: C.dangerText, marginTop: SP.xs }}>{err}</div>}
    </AccordionSection>
  );
}
