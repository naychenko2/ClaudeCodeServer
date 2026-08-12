// Диалог настройки промпта AI-описания коммита («стиль сообщений»).
// Два уровня редактируются в одном окне: «Общий» (per-user) и «Промпт этого проекта».
// Тог выбирает активный уровень (какой применяется к ✨-генерации) и что редактируется.
// Живёт отдельным файлом, потому что открывается из двух мест: меню настроек панели
// «Изменения» (доступно всегда) и попапа кнопки «Зафиксировать» в git-баре над композером.
import { useEffect, useState } from 'react';
import { Wand2 } from 'lucide-react';
import type { Project } from '../types';
import { api } from '../lib/api';
import { C, R, FONT } from '../lib/design';
import { Modal, ModalActions, TextArea } from './ui';
import { ICON_STROKE } from './ui/icons';

export function CommitPromptDialog({ project, onClose }: { project: Project; onClose: () => void }) {
  const [globalText, setGlobalText] = useState('');
  const [projectText, setProjectText] = useState('');
  const [level, setLevel] = useState<'global' | 'project'>('global');
  const [busy, setBusy] = useState(false);
  const [detecting, setDetecting] = useState(false);

  useEffect(() => {
    let alive = true;
    void api.git.getCommitPrompt(project.id).then(i => {
      if (!alive) return;
      setGlobalText(i.global ?? '');
      setProjectText(i.projectOverride ?? '');
      setLevel(i.useProject ? 'project' : 'global');
    }).catch(() => {});
    return () => { alive = false; };
  }, [project.id]);

  const isProject = level === 'project';
  const text = isProject ? projectText : globalText;
  const setText = isProject ? setProjectText : setGlobalText;

  const detect = async () => {
    setDetecting(true);
    try { setText((await api.git.detectCommitStyle(project.id)).prompt); }
    catch { /* мало истории/ошибка — оставляем поле */ }
    finally { setDetecting(false); }
  };

  const save = async () => {
    setBusy(true);
    // Global пишем всегда; project override — только когда активен проектный уровень
    try { await api.git.setCommitPrompt(project.id, globalText.trim(), projectText.trim(), isProject); onClose(); }
    catch { setBusy(false); }
  };

  const seg = (val: 'global' | 'project', label: string) => (
    <button
      onClick={() => setLevel(val)}
      style={{
        flex: 1, padding: '7px 12px', fontSize: 12.5, fontWeight: 600, cursor: 'pointer', border: 'none',
        fontFamily: FONT.sans, background: level === val ? C.accent : 'transparent',
        color: level === val ? C.onAccent : C.textSecondary, transition: 'background 0.12s',
      }}
    >{label}</button>
  );

  return (
    <Modal
      width={560}
      onClose={onClose}
      title="Промпт коммита"
      subtitle="Правила стиля для ✨-генерации сообщения. Активен выбранный уровень."
      footer={<ModalActions confirmLabel="Сохранить" onConfirm={save} loading={busy} onCancel={onClose} />}
    >
      <div style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
        {/* Два тога: какой уровень редактируем и применяем */}
        <div style={{ display: 'flex', border: `1px solid ${C.border}`, borderRadius: R.lg, overflow: 'hidden' }}>
          {seg('global', 'Общий промпт')}
          <div style={{ width: 1, background: C.border }} />
          {seg('project', 'Промпт этого проекта')}
        </div>
        <TextArea
          value={text}
          onChange={setText}
          placeholder={isProject
            ? 'Пусто — для этого проекта используется общий промпт'
            : 'Пусто — сообщения в стиле по умолчанию (Conventional Commits на русском). Опишите свои правила стиля…'}
          minHeight={180}
          maxHeight={340}
          autoGrow
        />
        <button
          onClick={() => { if (!detecting) void detect(); }}
          disabled={detecting}
          style={{ alignSelf: 'flex-start', display: 'flex', alignItems: 'center', gap: 6, padding: '6px 12px', borderRadius: R.md, cursor: 'pointer', border: `1.5px dashed ${C.dashed}`, background: 'none', color: C.accent, fontSize: 12.5, fontFamily: FONT.sans }}
        >
          <Wand2 size={14} strokeWidth={ICON_STROKE} />
          {detecting ? 'Анализирую историю…' : 'Определить стиль AI'}
        </button>
      </div>
    </Modal>
  );
}
