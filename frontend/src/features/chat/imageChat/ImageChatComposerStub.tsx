// Композер чата картинки, пока самого чата нет (ADR-018 §1, §6): тот же Composer без
// ChatPanel, черновик под псевдоключом `image:{projectId}:{path}`, файлы с компьютера —
// локальными File. Первое «Отправить» отдаёт всё хозяину: он создаёт чат, грузит вложения
// и монтирует ChatPanel с отложенным сообщением.

import { useEffect, useRef, useState, type ReactNode } from 'react';
import { MessageSquarePlus } from 'lucide-react';
import type { Mode, Persona, Project } from '../../../types';
import { api } from '../../../lib/api';
import { C, FS, R, SP } from '../../../lib/design';
import { Composer } from '../../../components/Composer';
import { AttachPicker } from '../../../components/chat/AttachPicker';
import { ICON_SIZE, ICON_STROKE } from '../../../components/ui/icons';
import type { ImageChatSnapshotChip } from '../../../lib/subsystems/registryCore';
import { SnapshotChip, SnapshotPickerRow } from './SnapshotChip';
import { ImageChatSuggestions } from './ImageChatSuggestions';

// Локальный файл в списке вложений: путь-заглушка, basename даёт имя в чипе
const LOCAL = 'local:';

export interface StubSubmit {
  text: string;
  paths: string[];
  files: File[];
  personaId?: string;
}

export function ImageChatComposerStub({ project, draftKey, leadIn, suggestions, snapshot, isMobile, busy, onSubmit }: {
  project: Project;
  draftKey: string;
  leadIn: ReactNode;
  suggestions?: string[];
  snapshot: ImageChatSnapshotChip | null;
  isMobile: boolean;
  busy: boolean;
  onSubmit: (s: StubSubmit) => void;
}) {
  const [paths, setPaths] = useState<string[]>([]);
  const files = useRef(new Map<string, File>());
  const seq = useRef(0);
  const [mode, setMode] = useState<Mode>('default');
  const [persona, setPersona] = useState<Persona | null>(null);
  const [personas, setPersonas] = useState<Persona[]>([]);
  const [picker, setPicker] = useState(false);

  useEffect(() => {
    let alive = true;
    api.personas.list({ scope: 'context', projectId: project.id })
      .then(list => { if (alive) setPersonas(list); })
      .catch(() => { /* без персон — собеседника выберет сервер */ });
    return () => { alive = false; };
  }, [project.id]);

  const addFiles = (list: File[]) => {
    const added = list.map(f => {
      const key = `${LOCAL}${++seq.current}/${f.name}`;
      files.current.set(key, f);
      return key;
    });
    setPaths(p => [...p, ...added]);
  };

  const submit = (text: string, attachments: string[]) => {
    onSubmit({
      text,
      paths: attachments.filter(p => !p.startsWith(LOCAL)),
      files: attachments.flatMap(p => (p.startsWith(LOCAL) && files.current.has(p) ? [files.current.get(p)!] : [])),
      personaId: persona?.id,
    });
    setPaths([]);
    files.current.clear();
  };

  return (
    <div style={{ height: '100%', minHeight: 0, display: 'flex', flexDirection: 'column' }}>
      <div style={{ flex: 1, minHeight: 0, overflow: 'auto', padding: `${SP.md}px ${SP.md}px ${SP.sm}px`, display: 'flex', flexDirection: 'column', gap: SP.md }}>
        {leadIn}
        <div data-image-chat-stub="" style={{
          display: 'flex', alignItems: 'center', gap: SP.sm, padding: `${SP.sm}px ${SP.md}px`,
          border: `1px dashed ${C.border}`, borderRadius: R.lg, color: C.textMuted, fontSize: FS.sm, lineHeight: 1.4,
        }}>
          <MessageSquarePlus size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} style={{ flexShrink: 0 }} />
          <span>Новый чат картинки · появится в списке после первого сообщения</span>
        </div>
        {!!suggestions?.length && (
          <ImageChatSuggestions items={suggestions} disabled={busy} onPick={text => submit(text, paths)} />
        )}
      </div>
      <div style={{ flex: '0 0 auto', padding: isMobile ? `0 ${SP.sm}px ${SP.md}px` : `0 ${SP.md}px ${SP.md}px` }}>
        <Composer
          key={draftKey}
          sessionId={draftKey}
          project={project}
          onSend={(text, attachments) => submit(text, attachments)}
          onStop={() => {}}
          onAttach={() => setPicker(true)}
          isGenerating={busy}
          mode={mode}
          onModeChange={setMode}
          attachments={paths}
          leadingChips={snapshot?.on ? <SnapshotChip chip={snapshot} /> : undefined}
          onRemoveAttachment={path => { files.current.delete(path); setPaths(p => p.filter(x => x !== path)); }}
          onAttachFiles={addFiles}
          isMobile={isMobile}
          personas={personas}
          selectedPersona={persona}
          onCompanionChange={sel => setPersona(sel.persona ?? null)}
          canPickCompanion
          isProjectChat
        />
      </div>
      {picker && (
        <AttachPicker
          projectId={project.id}
          selected={paths}
          onToggle={path => setPaths(p => (p.includes(path) ? p.filter(x => x !== path) : [...p, path]))}
          onClose={() => setPicker(false)}
          onUpload={async list => { addFiles(list); }}
          extra={snapshot && !snapshot.on ? <SnapshotPickerRow chip={snapshot} onDone={() => setPicker(false)} /> : undefined}
        />
      )}
    </div>
  );
}
