// Секция «Файлы» (изменённые/упомянутые в ходе разговора). Перенесена из ArtifactsPanel verbatim.
import { useState } from 'react';
import { Copy, File } from 'lucide-react';
import { C, FONT, R, SHADOW } from '../../lib/design';
import { ICON_SIZE, ICON_STROKE } from '../ui/icons';
import type { ArtifactFile } from '../../hooks/useSessionArtifacts';
import { basename, dirname } from './shared';

function FileRow({ file, onOpen }: { file: ArtifactFile; onOpen: () => void }) {
  const [hover, setHover] = useState(false);
  const [copied, setCopied] = useState(false);
  const dir = dirname(file.path);
  const showDelta = file.changed && file.hasDelta && (file.added > 0 || file.removed > 0);

  const handleClick = () => {
    if (file.external) {
      // На Windows копируем с обратными слэшами (как ждёт проводник/cmd).
      // Optional chaining до .then включительно — буфер может быть недоступен (http-контекст).
      const toCopy = /^[A-Za-z]:\//.test(file.path) ? file.path.replace(/\//g, '\\') : file.path;
      navigator.clipboard?.writeText(toCopy)?.then(() => {
        setCopied(true);
        setTimeout(() => setCopied(false), 1500);
      })?.catch(() => { /* буфер недоступен — молча */ });
    } else {
      onOpen();
    }
  };

  return (
    <button
      onClick={handleClick}
      onMouseEnter={() => setHover(true)}
      onMouseLeave={() => setHover(false)}
      title={file.external ? `${file.path} — скопировать путь` : file.path}
      style={{
        width: '100%', display: 'flex', alignItems: 'center', gap: 9,
        padding: '9px 12px', border: `1px solid ${hover ? C.accent : C.borderLight}`,
        borderRadius: R.lg, boxShadow: hover ? `0 0 0 1px ${C.accent}` : SHADOW.card,
        cursor: 'pointer', textAlign: 'left', background: C.bgWhite,
        transition: 'border-color .12s, box-shadow .12s',
      }}
    >
      {file.external ? (
        <Copy size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} color={C.textMuted} style={{ flexShrink: 0 }} />
      ) : (
        <File size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} color={C.textMuted} style={{ flexShrink: 0 }} />
      )}
      <div style={{ minWidth: 0, flex: 1 }}>
        <div style={{ fontFamily: FONT.mono, fontSize: 12.5, color: C.textHeading, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
          {basename(file.path)}
        </div>
        {dir && (
          <div style={{ fontFamily: FONT.mono, fontSize: 10.5, color: C.textMuted, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
            {dir}
          </div>
        )}
      </div>
      {showDelta ? (
        <div style={{ display: 'flex', gap: 6, flexShrink: 0, fontFamily: FONT.mono, fontSize: 11, fontWeight: 600 }}>
          {file.added > 0 && <span style={{ color: C.diffAddText }}>+{file.added}</span>}
          {file.removed > 0 && <span style={{ color: C.diffRemText }}>−{file.removed}</span>}
        </div>
      ) : (
        <span style={{ flexShrink: 0, fontFamily: FONT.sans, fontSize: 10, fontWeight: 600, color: copied ? C.successText : C.textMuted, whiteSpace: 'nowrap' }}>
          {copied ? 'скопировано' : file.external ? 'вне проекта' : !file.changed ? 'упомянут' : ''}
        </span>
      )}
    </button>
  );
}

export function FilesSection({ files, onOpenFile }: { files: ArtifactFile[]; onOpenFile?: (path: string) => void }) {
  return (
    <div style={{ flex: 1, overflowY: 'auto', padding: '8px 12px', display: 'flex', flexDirection: 'column', gap: 5 }}>
      {files.map(f => <FileRow key={f.path} file={f} onOpen={() => onOpenFile?.(f.path)} />)}
    </div>
  );
}
