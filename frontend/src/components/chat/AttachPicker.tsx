import { useState, useEffect, useRef } from 'react';
import { Check, Upload } from 'lucide-react';
import type { FileEntry } from '../../types';
import { api } from '../../lib/api';
import { C, FONT, FS, R, SP, MODAL_W } from '../../lib/design';
import { useIsMobile } from '../../lib/breakpoints';
import { Button, Modal } from '../ui';
import { ICON_SIZE, ICON_STROKE } from '../ui/icons';

// Модальный пикер вложений
interface AttachPickerProps {
  projectId: string;
  selected: string[];
  onToggle: (path: string) => void;
  onClose: () => void;
  // Загрузка файлов с устройства (кнопка в шапке). Не задан — кнопки нет:
  // пикер работает только по файлам проекта (так он открывается из карточки задачи)
  onUpload?: (files: File[]) => Promise<void>;
  // Заголовок диалога. Не задан — «Прикрепить файлы» (родная роль пикера);
  // контекст чата зовёт его для «Указать заново…», и слово «прикрепить» там
  // означало бы вложение — другую сущность
  title?: string;
}

export function AttachPicker({ projectId, selected, onToggle, onClose, onUpload, title }: AttachPickerProps) {
  const [query, setQuery] = useState('');
  const [files, setFiles] = useState<FileEntry[]>([]);
  const [loading, setLoading] = useState(true);
  const isMobile = useIsMobile();
  // Загрузка с устройства: пикер после неё НЕ закрываем — можно добавить ещё файлов,
  // прикреплённое видно по счётчику «Выбрано» внизу
  const uploadInputRef = useRef<HTMLInputElement>(null);
  const [uploading, setUploading] = useState(false);
  const [uploaded, setUploaded] = useState(0);

  useEffect(() => {
    // eslint-disable-next-line react-hooks/set-state-in-effect -- загрузка списка файлов по фильтру
    setLoading(true);
    const t = setTimeout(() => {
      api.files.search(projectId, query)
        .then(setFiles)
        .finally(() => setLoading(false));
    }, query ? 200 : 0);
    return () => clearTimeout(t);
  }, [projectId, query]);

  return (
    <Modal
      title={title ?? 'Прикрепить файлы'}
      width={MODAL_W.form}
      onClose={onClose}
      cardStyle={{ maxHeight: '70vh' }}
    >
      {/* Загрузка с устройства — отдельной строкой над поиском: в одной строке с полем
          кнопка с полной подписью съедала бы поиск (диалог всего MODAL_W.form шириной) */}
      {onUpload && (
        <div style={{ marginBottom: SP.sm, display: 'flex', alignItems: 'center', gap: SP.sm, flexWrap: 'wrap' }}>
          <input
            ref={uploadInputRef}
            type="file"
            multiple
            style={{ display: 'none' }}
            onChange={async e => {
              const picked = Array.from(e.target.files ?? []);
              e.target.value = '';
              if (!picked.length) return;
              setUploading(true);
              try { await onUpload(picked); setUploaded(picked.length); } finally { setUploading(false); }
            }}
          />
          <Button
            variant="ghost"
            size={isMobile ? 'md' : 'sm'}
            loading={uploading}
            fullWidth={isMobile}
            leftIcon={<Upload size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />}
            onClick={() => uploadInputRef.current?.click()}
            style={{ flexShrink: 0, whiteSpace: 'nowrap' }}
          >
            {uploading ? 'Загружаем…' : 'Загрузить с устройства'}
          </Button>
          {/* Загруженное уходит в служебную папку вложений — в списке файлов проекта его нет,
              поэтому подтверждаем словами, а не галочкой в списке */}
          {uploaded > 0 && !uploading && (
            <span style={{ fontSize: FS.sm, color: C.textMuted }}>
              Прикреплено: {uploaded}
            </span>
          )}
        </div>
      )}
      <div style={{ marginBottom: SP.sm }}>
        <input
          type="search"
          autoComplete="off"
          autoFocus
          value={query}
          onChange={e => setQuery(e.target.value)}
          placeholder="Поиск по имени файла…"
          style={{
            width: '100%', boxSizing: 'border-box',
            padding: '8px 10px', borderRadius: R.md, border: `1px solid ${C.border}`,
            background: C.bgMain, color: C.textPrimary, fontSize: 13,
            fontFamily: FONT.mono, outline: 'none',
          }}
        />
      </div>
      <div style={{ margin: '-4px -8px', maxHeight: '46vh', overflowY: 'auto' }}>
        {loading && (
          <div style={{ padding: 16, color: C.textMuted, fontSize: 13, textAlign: 'center' }}>
            Загрузка…
          </div>
        )}
        {!loading && files.map(f => {
          const isSelected = selected.includes(f.path);
          return (
            <div
              key={f.path}
              onClick={() => onToggle(f.path)}
              style={{
                padding: '8px 12px', cursor: 'pointer', fontSize: 13, borderRadius: R.md,
                color: C.textPrimary, display: 'flex', alignItems: 'center', gap: 8,
                background: isSelected ? C.accentLight : 'transparent',
              }}
              onMouseEnter={e => { if (!isSelected) e.currentTarget.style.background = C.bgInset; }}
              onMouseLeave={e => { e.currentTarget.style.background = isSelected ? C.accentLight : 'transparent'; }}
            >
              <span style={{
                width: 14, height: 14, flexShrink: 0, borderRadius: 3, border: `1.5px solid ${isSelected ? C.accent : C.border}`,
                background: isSelected ? C.accent : 'transparent', display: 'flex', alignItems: 'center', justifyContent: 'center',
              }}>
                {isSelected && <Check size={9} color={C.onAccent} strokeWidth={3} style={{ flexShrink: 0 }} />}
              </span>
              <span style={{ flex: 1, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', fontFamily: FONT.mono }}>
                {f.path}
              </span>
            </div>
          );
        })}
        {!loading && files.length === 0 && (
          <div style={{ padding: 16, color: C.textMuted, fontSize: 13, textAlign: 'center' }}>
            Файлы не найдены
          </div>
        )}
      </div>
      <div style={{ marginTop: 10, display: 'flex', justifyContent: 'flex-end', gap: 8 }}>
        {selected.length > 0 && (
          <span style={{ fontSize: 12, color: C.textMuted, alignSelf: 'center' }}>
            Выбрано: {selected.length}
          </span>
        )}
        <button
          onClick={onClose}
          style={{
            padding: '7px 16px', borderRadius: R.md, border: 'none', cursor: 'pointer',
            background: C.accent, color: C.onAccent, fontSize: 13, fontWeight: 600,
            fontFamily: FONT.sans,
          }}
        >
          Готово
        </button>
      </div>
    </Modal>
  );
}
