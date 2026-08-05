// Ввод названия при создании документа или раздела в панели «Документы».
//
// Устроен как создание файла и папки в «Файлах»: главное действие панели — залитая
// кнопка «Новый» в шапке, по ней меню с видами, дальше вот эта модалка с одним полем.
// Расходиться этим двум панелям нельзя — жест один и тот же, и разный порядок шагов
// читался бы как разные возможности.
//
// Отличие от «Файлов» только в поле: там вводят ИМЯ ФАЙЛА («name.py»), здесь —
// НАЗВАНИЕ страницы. Имя файла из него делает бэкенд по правилам wiki (пробелы →
// дефисы), а само название становится заголовком первой строки. Что получится файлом,
// показываем тут же: путь уедет в git, и увидеть его лучше до, а не из git status.

import { useState } from 'react';
import { api } from '../../lib/api';
import { C, FONT, FS, MODAL_W, R, SP } from '../../lib/design';
import { Modal, ModalActions, TextField } from '../../components/ui';

interface Props {
  projectId: string;
  // Куда создаём. Папка приходит из панели: активная группа, первая папка области либо
  // выбранная в меню. Пустая строка — корень репозитория (там живут файлы корня области)
  folder: string;
  // Раздел — это ПАРА «страница + папка»: в code wiki раздел существует только так,
  // папка без парного файла открывается пустой страницей. Обе половины создаёт бэкенд
  kind: 'doc' | 'section';
  onClose: () => void;
  // Путь созданного документа — панель открывает его сразу
  onCreated: (path: string) => void;
}

// Предпросмотр имени файла: те же правила, что на бэкенде (DocsIndexService.DocFileName).
// Дублируется намеренно — показать результат нужно ДО запроса, а решает всё равно сервер
function fileNameOf(title: string): string {
  return title.trim().replace(/ /g, '-');
}

export function DocsCreateDialog({ projectId, folder, kind, onClose, onCreated }: Props) {
  const [name, setName] = useState('');
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const preview = fileNameOf(name);
  const section = kind === 'section';

  const create = async () => {
    if (!preview || saving) return;
    setSaving(true);
    setError(null);
    try {
      const res = await api.docs.create(projectId, folder, name, kind);
      onCreated(res.path);
    } catch (e) {
      // Текст с сервера: занятое имя, недопустимый символ, зарезервированное имя Windows —
      // всё это объясняется словами, и подменять их общим «не удалось» незачем
      setError(e instanceof Error ? e.message : 'Не удалось создать документ');
      setSaving(false);
    }
  };

  return (
    <Modal
      width={MODAL_W.form}
      title={section ? 'Новый раздел' : 'Новый документ'}
      subtitle={folder
        ? <>В папке <span style={{ fontFamily: FONT.mono, color: C.textPrimary }}>{folder}/</span></>
        // Файл корня попадёт в область поимённо — продукт допишет его в настройку сам,
        // и сказать об этом надо здесь: это правка настройки, а не только новый файл
        : 'В корне репозитория — файл будет добавлен в «файлы корня» области'}
      onClose={onClose}
      footer={
        <ModalActions
          confirmLabel="Создать"
          onConfirm={create}
          loading={saving}
          confirmDisabled={!preview}
          onCancel={onClose}
        />
      }
    >
      <TextField
        value={name}
        onChange={setName}
        placeholder={section ? 'Журнал решений' : 'Бизнес-описание'}
        autoFocus
        onEnter={create}
      />

      {/* Путь файла: пробелы станут дефисами, а у раздела рядом появится одноимённая
          папка — увидеть это до коммита полезнее, чем узнать из git status */}
      {preview && (
        <div style={{
          padding: `${SP.xs}px ${SP.sm}px`, borderRadius: R.md, background: C.bgInset,
          fontFamily: FONT.mono, fontSize: FS.xs, color: C.textSecondary,
          overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
        }}>
          {folder && `${folder}/`}{preview}.md{section ? ` + ${preview}/` : ''}
        </div>
      )}

      {error && <div style={{ fontSize: FS.sm, color: C.danger }}>{error}</div>}
    </Modal>
  );
}
