import { useState } from 'react';
import type { KnowledgeBaseSummary } from '../../types';
import { api } from '../../lib/api';
import { bumpKnowledge } from '../../lib/knowledge';
import { Modal, ModalActions, Field, TextField, TextArea } from '../../components/ui';
import { PillSwitch } from '../../components/Toolbar';
import { C, FONT, MODAL_W, R } from '../../lib/design';
import { IconTextDoc, IconUpload } from './shared';

// Диалог добавления документа в базу: текстом (название + содержимое) или файлом.
export function AddDocumentDialog({ kb, onClose, onAdded }: {
  kb: KnowledgeBaseSummary;
  onClose: () => void;
  onAdded: () => void;
}) {
  const [tab, setTab] = useState<'text' | 'file'>('text');
  const [name, setName] = useState('');
  const [text, setText] = useState('');
  const [file, setFile] = useState<File | null>(null);
  const [busy, setBusy] = useState(false);
  const [err, setErr] = useState<string | null>(null);

  const canAdd = tab === 'text' ? name.trim().length > 0 && text.trim().length > 0 : !!file;

  const add = async () => {
    setBusy(true); setErr(null);
    try {
      if (tab === 'text') {
        await api.knowledgeBases.addDocumentText(kb.id, name.trim(), text);
      } else if (file) {
        await api.knowledgeBases.addDocumentFile(kb.id, file, name.trim() || undefined);
      }
      bumpKnowledge();
      onAdded();
    } catch (e) {
      setErr(e instanceof Error ? e.message : 'Не удалось добавить');
    } finally {
      setBusy(false);
    }
  };

  return (
    <Modal
      width={MODAL_W.form}
      title="Добавить документ"
      subtitle={<>В базу «<strong style={{ color: 'var(--c-text-primary)', fontWeight: 600 }}>{kb.title}</strong>»</>}
      onClose={onClose}
      footer={
        <ModalActions
          confirmLabel="Добавить"
          confirmDisabled={!canAdd || busy}
          loading={busy}
          onConfirm={add}
          onCancel={onClose}
        />
      }
    >
      <Field label="Способ">
        <PillSwitch<'text' | 'file'>
          fill
          value={tab}
          onChange={setTab}
          options={[
            { value: 'text', label: 'Текст', icon: <IconTextDoc size={14} /> },
            { value: 'file', label: 'Файл', icon: <IconUpload size={14} /> },
          ]}
        />
      </Field>

      {tab === 'text' ? (
        <>
          <Field label="Название документа">
            <TextField value={name} onChange={setName} placeholder="напр. метод-ретроспективы.md" mono autoFocus onEnter={add} />
          </Field>
          <Field label="Содержимое">
            <TextArea value={text} onChange={setText} placeholder="Вставьте текст — он проиндексируется и будет доступен поиску…" minHeight={140} />
          </Field>
        </>
      ) : (
        <Field label="Файл" hint=".md .txt .pdf .docx .csv — индексируется автоматически">
          <label style={{
            display: 'flex', flexDirection: 'column', alignItems: 'center', justifyContent: 'center', gap: 6,
            border: `1.5px dashed ${file ? C.accent : C.border}`, borderRadius: R.xl, padding: '24px',
            cursor: 'pointer', background: file ? C.accentMuted : C.bgCard, color: file ? C.accent : C.textSecondary,
            fontSize: 13, fontFamily: FONT.sans, transition: 'border-color .1s, background .1s',
          }}>
            <IconUpload size={22} />
            <span style={{ fontWeight: 600, color: file ? C.accent : C.textPrimary }}>{file ? file.name : 'Выберите файл'}</span>
            {!file && <span style={{ fontSize: 11.5, color: C.textMuted }}>Перетащите сюда или нажмите для выбора</span>}
            <input type="file" style={{ display: 'none' }} onChange={e => setFile(e.target.files?.[0] ?? null)} />
          </label>
          {file && (
            <TextField style={{ marginTop: 8 }} value={name} onChange={setName} placeholder="Имя документа (необязательно) — по умолчанию имя файла" />
          )}
        </Field>
      )}

      {err && <div style={{ color: 'var(--c-danger)', fontSize: 13 }}>{err}</div>}
    </Modal>
  );
}
