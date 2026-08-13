import { useEffect, useMemo, useState } from 'react';
import type { NoteSource } from '../../types';
import { api } from '../../lib/api';
import { bumpNotes } from '../../lib/notes';
import { showToast } from '../../lib/toast';
import { prefillComposer, startChatInProject } from '../../lib/ai/startChat';
import { getChatContext } from '../../lib/ai/chatContext';
import { disableUiInspector } from '../../lib/uiInspector';
import { useAllProjects, openProjectViaEvent } from '../projects/useAllProjects';
import { Modal, Button, Field, TextField, TextArea, SegmentedControl } from '../../components/ui';
import { C, FONT, MODAL_W, R } from '../../lib/design';
import { buildChatPrompt, defaultChainIndex, srcFile, type ChainLevel } from './uiChain';

// Последний выбранный источник — приоритетнее дефолта от открытого проекта
const SOURCE_KEY = 'cc_ui_note_source';
// Последний выбранный режим действия формы («заметка» или «в чат»)
const ACTION_KEY = 'cc_ui_note_action';

type NoteAction = 'note' | 'chat';

// Тег: срезать ведущий #, пробелы → дефис (иначе «ui note» инлайн-парсер бэка
// молча превратит в «ui»); пустой ввод откатывается к дефолту
function normalizeTag(raw: string): string {
  const t = raw.trim().replace(/^#+/, '').replace(/\s+/g, '-');
  return t || 'ui-note';
}

// Форма аннотации UI-инспектора: комментарий к выбранному уровню цепочки → заметка
// в папку ui выбранного источника, привязка к исходнику — frontmatter-блоком прямо
// в content (конвенция UI, см. комментарий у CreateNoteDto в types/index.ts).
export function UiNoteDialog({ chain, onClose }: {
  chain: ChainLevel[];   // от глубокого к корню, непустая (гейт — в оверлее)
  onClose: () => void;
}) {
  const [levelIdx, setLevelIdx] = useState(() => defaultChainIndex(chain));
  const [comment, setComment] = useState('');
  const [tag, setTag] = useState('ui-note');
  // Режим действия: заметка (как раньше) или отправка контекста элемента в чат
  const [action, setAction] = useState<NoteAction>(
    () => localStorage.getItem(ACTION_KEY) === 'chat' ? 'chat' : 'note');
  // Селект проекта для «Нового чата»: нужны ПОЛНЫЕ Project-объекты (defaultPersonaId,
  // канал открытия) — notes-sources не годятся
  const projects = useAllProjects();
  const [projectId, setProjectId] = useState('');
  // Кнопка «В текущий чат» видна только при смонтированном композере; снимок на открытии
  // формы — пока модалка открыта, чат под ней не сменится
  const [chatActive] = useState(() => getChatContext().active);
  const [sources, setSources] = useState<NoteSource[]>([{ key: 'personal', label: 'Личный' }]);
  // Дефолт 'personal', а не '': при отказе sources() бэк всё равно положит в личный
  // vault — селект и предупреждение о потере привязки должны говорить правду
  const [source, setSource] = useState('personal');
  const [busy, setBusy] = useState(false);
  // Роут фиксируем на момент открытия формы — hash не изменится, пока она открыта,
  // но так намерение явное
  const [route] = useState(() => window.location.hash || '#/');

  useEffect(() => {
    api.notes.sources().then(list => {
      if (!list.length) return;
      setSources(list);
      // Дефолт: сохранённый выбор → текущий открытый проект → первый не-personal
      const saved = localStorage.getItem(SOURCE_KEY);
      if (saved && list.some(s => s.key === saved)) { setSource(saved); return; }
      try {
        const open = JSON.parse(localStorage.getItem('cc_open_project') || 'null') as { id?: string } | null;
        if (open?.id && list.some(s => s.key === open.id)) { setSource(open.id); return; }
      } catch { /* битый JSON — идём к fallback */ }
      setSource((list.find(s => s.key !== 'personal') ?? list[0]).key);
    }).catch(() => {});
  }, []);

  // Дефолт селекта проекта (когда список догрузился): текущий открытый проект → первый.
  // Пока список пуст, projectId остаётся '' и «Новый чат» задизейблен.
  useEffect(() => {
    if (projectId || !projects.length) return;
    let def = '';
    try {
      const open = JSON.parse(localStorage.getItem('cc_open_project') || 'null') as { id?: string } | null;
      if (open?.id && projects.some(p => p.id === open.id)) def = open.id;
    } catch { /* битый JSON — идём к fallback */ }
    setProjectId(def || projects[0].id);
  }, [projects, projectId]);

  const level = chain[levelIdx] ?? chain[0];
  const file = srcFile(level.src);
  const baseName = file.split('/').pop() ?? file;

  // ui_chain во frontmatter — от корня к глубокому (читается как путь по дереву)
  const chainLine = useMemo(() => [...chain].reverse().map(l => l.src).join(' > '), [chain]);

  // Значение quoted-скаляра YAML: ParseFrontmatter бэка построчный — переносы и
  // кавычки из живого DOM-атрибута сломали бы блок
  const yamlQuoted = (v: string) => v.replace(/[\r\n"]/g, ' ');

  const create = async () => {
    if (!comment.trim() || busy) return;
    setBusy(true);
    const cleanTag = normalizeTag(tag);
    // Заголовок без двоеточий: он же имя .md-файла (SanitizeFileName), и «:» в YAML
    // title сломал бы plain-скаляр
    const excerpt = comment.trim().replace(/\s+/g, ' ').replace(/:/g, '').slice(0, 40);
    const title = `UI — ${baseName} — ${excerpt}`;
    const content = [
      '---',
      `title: ${title}`,
      `file: ${file}`,
      `ui_route: "${yamlQuoted(route)}"`,
      `ui_chain: "${yamlQuoted(chainLine)}"`,
      '---',
      comment.trim(),
      '',
      `Элемент: ${level.label}`,
      '',
      `#${cleanTag}`,
      '',
    ].join('\n');
    try {
      await api.notes.create({ title, content, source, folder: 'ui' });
      localStorage.setItem(SOURCE_KEY, source);
      bumpNotes();
      showToast('Заметка создана', title);
      onClose();   // режим инспектора остаётся включённым
    } catch (e) {
      showToast('Не удалось создать заметку', e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  };

  // Смена режима — с запоминанием (следующее открытие формы стартует в нём же)
  const switchAction = (a: NoteAction) => {
    setAction(a);
    localStorage.setItem(ACTION_KEY, a);
  };

  // Комментарий в режиме «в чат» необязателен: контекст элемента ценен сам по себе
  const chatPrompt = () => buildChatPrompt(level, chain, route, comment);

  // «В текущий чат»: prefill синхронный и не падает — сразу гасим режим и закрываем.
  // Гасить надо ПОСЛЕ действия: после onClose оверлей поднял бы capture-перехват
  // обратно и не дал кликать по чату.
  const toCurrentChat = () => {
    if (busy) return;
    prefillComposer(chatPrompt());
    disableUiInspector();
    onClose();
  };

  // «Новый чат»: успех → погасить режим и закрыть; ошибка (тост показан внутри) —
  // форма остаётся открытой с набранным комментарием. Гасить режим ДО await нельзя:
  // App размонтирует оверлей вместе с диалогом.
  const toNewChat = async () => {
    const p = projects.find(x => x.id === projectId);
    if (!p || busy) return;
    setBusy(true);
    const ok = await startChatInProject(chatPrompt(), p, openProjectViaEvent);
    setBusy(false);
    if (ok) { disableUiInspector(); onClose(); }
  };

  return (
    <Modal
      width={MODAL_W.form}
      title={action === 'chat' ? 'Элемент в чат' : 'Заметка об элементе'}
      // Пока идёт создание чата, закрывать нельзя: размонтированный диалог вернул бы
      // capture-перехват оверлея, а промис потом «внезапно» открыл бы чат
      onClose={() => { if (!busy) onClose(); }}
      footer={
        <>
          <Button variant="ghost" size="sm" disabled={busy} onClick={onClose}>Отмена</Button>
          {action === 'note' ? (
            <Button size="sm" loading={busy} disabled={!comment.trim()} onClick={create}>Создать</Button>
          ) : (
            <>
              {chatActive && (
                <Button variant="secondary" size="sm" disabled={busy} onClick={toCurrentChat}>
                  В текущий чат
                </Button>
              )}
              <Button size="sm" loading={busy} disabled={!projectId} onClick={toNewChat}>Новый чат</Button>
            </>
          )}
        </>
      }
    >
      <div style={{ display: 'flex', flexDirection: 'column', gap: 14 }}>
        <SegmentedControl
          value={action}
          options={[{ value: 'note', label: 'Заметка' }, { value: 'chat', label: 'В чат' }]}
          onChange={switchAction}
        />
        <Field label="Элемент" hint="Уровень цепочки: от кликнутого элемента к родителям">
          <select value={levelIdx} onChange={e => setLevelIdx(Number(e.target.value))} style={selectStyle}>
            {chain.map((l, i) => (
              <option key={i} value={i}>{l.src} — {l.label}</option>
            ))}
          </select>
        </Field>
        <Field label="Комментарий">
          <TextArea value={comment} onChange={setComment} autoFocus autoGrow
            minHeight={80} maxHeight={220}
            placeholder={action === 'chat'
              ? 'Вопрос или задача про элемент (необязательно)'
              : 'Что не так или что улучшить'} />
        </Field>
        {action === 'note' ? (
          <>
            <Field label="Тег">
              <TextField value={tag} onChange={setTag} placeholder="ui-note" />
            </Field>
            <Field label="Куда"
              hint="Путь к исходнику резолвится, только если корневая папка проекта — корень репозитория">
              <select value={source} onChange={e => setSource(e.target.value)} style={selectStyle}>
                {sources.map(s => <option key={s.key} value={s.key}>{s.label}</option>)}
              </select>
            </Field>
            {source === 'personal' && (
              <div style={{
                fontSize: 12, color: C.warningText, background: C.warningBg,
                borderRadius: R.md, padding: '7px 10px',
              }}>
                В личном vault привязка к файлу не сохранится — выбери источник-проект
              </div>
            )}
          </>
        ) : (
          <Field label="Проект" hint="«Новый чат» создастся в этом проекте">
            <select value={projectId} onChange={e => setProjectId(e.target.value)} style={selectStyle}>
              {projects.map(p => <option key={p.id} value={p.id}>{p.name}</option>)}
            </select>
          </Field>
        )}
      </div>
    </Modal>
  );
}

// Селект в стиле полей форм (готового Select в ui-ките нет — как в NewNoteDialog)
const selectStyle: React.CSSProperties = {
  width: '100%', boxSizing: 'border-box', background: C.bgWhite,
  border: `1px solid ${C.border}`, borderRadius: R.xl, padding: '9px 12px',
  fontSize: 13, fontFamily: FONT.sans, color: C.textHeading, outline: 'none', cursor: 'pointer',
};
