import { useEffect, useRef, useState } from 'react';
import { Check, Copy, FolderLock } from 'lucide-react';
import { C, FONT, FS, R, SP } from '../../lib/design';
import { Button, Notice } from '../../components/ui';
import { rootsAddCommand } from '../../lib/agentInstall';

// Строка команды для копирования в терминал машины: моноширинный блок и кнопка «Скопировать».
// Блок переносится по словам, а не скроллится вбок — на 360 px команда видна целиком.
export function CopyCommand({ command, label = 'Скопировать' }: { command: string; label?: string }) {
  const [copied, setCopied] = useState(false);
  const timer = useRef<ReturnType<typeof setTimeout> | null>(null);
  useEffect(() => () => { if (timer.current) clearTimeout(timer.current); }, []);

  const copy = () => {
    navigator.clipboard?.writeText(command)
      .then(() => {
        setCopied(true);
        if (timer.current) clearTimeout(timer.current);
        timer.current = setTimeout(() => setCopied(false), 1500);
      })
      .catch(() => { /* буфер недоступен (HTTP, запрет) — строку можно выделить руками */ });
  };

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: SP.sm }}>
      <code style={{
        display: 'block', fontFamily: FONT.mono, fontSize: FS.sm, lineHeight: 1.5,
        color: C.textPrimary, background: C.bgInset, border: `1px solid ${C.borderLight}`,
        borderRadius: R.md, padding: `${SP.sm}px ${SP.md}px`,
        whiteSpace: 'pre-wrap', overflowWrap: 'anywhere', userSelect: 'all',
      }}>
        {command}
      </code>
      <div>
        <Button
          variant="secondary" size="sm" onClick={copy}
          leftIcon={copied ? <Check size={14} strokeWidth={2.4} /> : <Copy size={14} strokeWidth={2.2} />}
        >
          {copied ? 'Скопировано' : label}
        </Button>
      </div>
    </div>
  );
}

// Папка локального проекта не разрешена агенту: команду `roots add` человек выполняет сам на
// машине проекта. Автоматически с сервера корень не разрешаем — это решение человека на
// машине (модель угроз docs/architecture/device-agent-local-api.md)
// bare — без заголовка, когда его уже сказал контейнер (плашка панели)
export function RootsAddHint({ rootPath, platform, bare }: {
  rootPath: string; platform?: string | null; bare?: boolean;
}) {
  return (
    <Notice
      icon={FolderLock} title={bare ? undefined : 'Папка проекта не разрешена агенту'}
      style={{ textAlign: 'left', width: '100%', boxSizing: 'border-box' }}
    >
      <div style={{ marginBottom: SP.sm }}>
        Выполните на компьютере проекта и повторите:
      </div>
      <CopyCommand command={rootsAddCommand(rootPath, platform)} />
    </Notice>
  );
}
