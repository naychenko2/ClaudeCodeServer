// Документ «Архитектура» в центральной зоне workspace — по схеме «Графа»: Toolbar-шапка
// и тело на всю высоту, крестик возвращает центр к чату. В теле — iframe с редактором
// Viaduct (ViaductFrame) над моделью docs/architecture/model.viaduct.json проекта.
// Дизайн — заметка «Viaduct — дизайн раздела «Архитектура»» (Вера, задача 2/9).
//
// Сохранение автоматическое (дебаунс в мосте), кнопки «Сохранить» нет — статус несёт
// один чип в шапке. Конфликт версий — плашка между шапкой и холстом, холст не прячем.
// Мобила: холст не редактируется — список-обзор элементов и «Смотреть схему» в режиме
// только просмотра (родного readOnly у Viaduct нет: правки на холсте просто не пишутся).
import { useEffect, useMemo, useState } from 'react';
import {
  DraftingCompass, X, Check, Loader, AlertTriangle, Eye, Sparkles, Unlink, FileWarning, RefreshCw,
  FileJson, Download, Lock,
} from 'lucide-react';
import {
  C, FONT, FS, SP, Button, BackButton, EmptyState, WaitingIndicator, MetaChip,
  Toolbar, ToolbarIconButton, ToolbarOverflowMenu, type OverflowItem, ICON_SIZE, ICON_STROKE,
  getEffectiveTheme, useThemeMode, gitRelTime as relTime, onFilesChanged,
} from 'aihome_shell/kit';
import {
  useArchitecture, loadArchitecture, checkRemote, onProjectFilesChanged, onFrameDirty, onFrameSave, retrySave,
  takeServerVersion, downloadLocal, setReadOnly, startBlank, generateArchitecture,
  setFrameWindow, POLL_MS, MODEL_PATH, type ArchState,
} from './architectureStore';
import { ViaductFrame, type FrameFailure } from './ViaductFrame';
import { parseOutline, modelTitle, LEVEL_LABEL, type ArchElement, type ArchLevel } from './modelOutline';

interface Props {
  projectId: string;
  projectName: string;
  isMobile: boolean;
  onClose: () => void;
  onShowFile: (path: string) => void;
}

export function ArchitectureDocument({ projectId, projectName, isMobile, onClose, onShowFile }: Props) {
  const s = useArchitecture();
  useThemeMode(); // перерисовка при смене темы — новая тема уйдёт в следующий init фрейма
  const [frameFailed, setFrameFailed] = useState<FrameFailure | null>(null);
  // Мобила: список-обзор по умолчанию, схема — по кнопке
  const [mobileCanvas, setMobileCanvas] = useState(false);

  useEffect(() => { void loadArchitecture(projectId); }, [projectId]);

  // Мобила — только просмотр: пальцем на 360px холст не правят
  useEffect(() => {
    if (isMobile) setReadOnly(true);
    return () => { if (isMobile) setReadOnly(false); };
  }, [isMobile]);

  // Сверка с сервером, пока документ открыт: push по filesChanged проекта (группу проекта
  // держит воркспейс), страховочный опрос и возврат на вкладку. Возврат ловим и через
  // visibilitychange: при фокусе внутри iframe событие focus окна хоста не приходит
  useEffect(() => {
    const tick = () => { if (document.visibilityState === 'visible') void checkRemote(projectId); };
    const id = setInterval(tick, POLL_MS);
    window.addEventListener('focus', tick);
    document.addEventListener('visibilitychange', tick);
    const offFiles = onFilesChanged(({ projectId: p, paths, full }) => {
      if (p === projectId) onProjectFilesChanged(projectId, paths, full);
    });
    return () => {
      clearInterval(id);
      window.removeEventListener('focus', tick);
      document.removeEventListener('visibilitychange', tick);
      offFiles();
    };
  }, [projectId]);

  const elements = useMemo(() => parseOutline(s.localValue ?? s.content), [s.localValue, s.content]);
  const title = modelTitle(elements) ?? projectName;
  const hasModel = s.status === 'ready' && !s.corrupt;
  const showCanvas = (hasModel || (s.status === 'missing' && s.blank)) && !s.generating && !frameFailed
    && (!isMobile || mobileCanvas);

  const retryFrame = () => { setFrameFailed(null); takeServerVersion(); };

  const menu: OverflowItem[] = [
    {
      key: 'generate', label: 'Пересобрать из кода', icon: <Sparkles size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />,
      sublabel: 'Ручные описания и раскладка сохранятся', disabled: s.generating,
      onClick: () => void generateArchitecture(projectId),
    },
    {
      key: 'file', label: 'Показать файл модели', icon: <FileJson size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />,
      disabled: s.status !== 'ready', onClick: () => onShowFile(MODEL_PATH),
    },
    ...(!isMobile ? [{
      key: 'readonly', label: 'Только просмотр', icon: <Eye size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />,
      toggle: s.readOnly, onClick: () => setReadOnly(!s.readOnly),
    }] : []),
  ];

  return (
    // flex: 1 — на мобиле документ лежит во flex-строке оверлея и без него ужимался по содержимому
    <div style={{ display: 'flex', flexDirection: 'column', flex: 1, minWidth: 0, height: '100%', background: C.bgCard, position: 'relative' }}>
      <Toolbar isMobile={isMobile}>
        {isMobile && (
          <BackButton onClick={mobileCanvas ? () => setMobileCanvas(false) : onClose}
            title={mobileCanvas ? 'К списку' : 'К чату'} style={{ height: 32 }}>
            <span style={{ fontSize: 13, fontWeight: 600, color: C.textSecondary }}>{mobileCanvas ? 'Список' : 'Чат'}</span>
          </BackButton>
        )}
        {!isMobile && (
          <ToolbarIconButton isMobile={isMobile} onClick={onClose} title="Закрыть">
            <X size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
          </ToolbarIconButton>
        )}
        <DraftingCompass size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} color={C.textSecondary} style={{ flexShrink: 0 }} />
        <span style={{ fontFamily: FONT.sans, fontWeight: 600, fontSize: 14, color: C.textHeading, flex: 1, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
          {title}
        </span>
        {(s.status === 'ready' || s.blank) && <SaveChip s={s} />}
        {!isMobile && s.updatedAt && (
          <MetaChip title={new Date(s.updatedAt).toLocaleString('ru-RU')}>
            обновил <b style={{ color: C.textHeading, fontWeight: 600 }}>{s.updatedBy ?? '—'}</b>
            · <span style={{ fontFamily: FONT.mono }}>{relTime(s.updatedAt)}</span>
          </MetaChip>
        )}
        <ToolbarOverflowMenu isMobile={isMobile} items={menu} />
      </Toolbar>

      {s.conflict && (
        <ConflictBanner conflict={s.conflict} isMobile={isMobile}
          onReload={takeServerVersion}
          onDownload={() => downloadLocal(`model.viaduct.${new Date().toISOString().slice(0, 16).replace(/[:T]/g, '-')}.json`)} />
      )}

      <div style={{ flex: 1, minHeight: 0, position: 'relative', display: 'flex', flexDirection: 'column' }}>
        {(s.status === 'idle' || s.status === 'loading') && (
          <EmptyState
            icon={<Loader size={ICON_SIZE.xl} strokeWidth={ICON_STROKE} />}
            title="Загружаю архитектуру"
            subtitle="Поднимаю редактор и читаю модель из проекта."
            action={<WaitingIndicator />}
          />
        )}

        {s.status === 'error' && (
          /403|forbidden|доступ/i.test(s.error ?? '')
            ? <EmptyState icon={<Lock size={ICON_SIZE.xl} strokeWidth={ICON_STROKE} />} title="Нет доступа к архитектуре проекта" />
            : (
              <EmptyState
                icon={<Unlink size={ICON_SIZE.xl} strokeWidth={ICON_STROKE} />}
                title="Не удалось прочитать модель"
                subtitle={s.error ?? 'Повторите попытку позже.'}
                action={<Button variant="secondary" size="md" onClick={() => void loadArchitecture(projectId, true)}>Повторить</Button>}
              />
            )
        )}

        {s.generating && (
          <EmptyState
            icon={<Loader size={ICON_SIZE.xl} strokeWidth={ICON_STROKE} />}
            title="Собираю модель из кода"
            subtitle="Документ обновится сам, закрывать его не нужно."
            action={<WaitingIndicator hint="Обычно пара минут" />}
          />
        )}

        {!s.generating && s.generateError && s.status !== 'ready' && !s.corrupt && (
          <EmptyState
            icon={<Unlink size={ICON_SIZE.xl} strokeWidth={ICON_STROKE} />}
            title="Не удалось собрать модель"
            subtitle={s.generateError}
            action={(
              <div style={{ display: 'flex', gap: SP.sm, flexWrap: 'wrap', justifyContent: 'center' }}>
                <Button variant="secondary" size="md" onClick={() => void generateArchitecture(projectId)}>Повторить</Button>
                {!isMobile && <Button variant="ghost" size="md" onClick={startBlank}>Начать с пустого</Button>}
              </div>
            )}
          />
        )}

        {!s.generating && !s.generateError && s.status === 'missing' && !s.blank && (
          <EmptyState
            icon={<DraftingCompass size={ICON_SIZE.xl} strokeWidth={ICON_STROKE} />}
            title="Архитектура ещё не описана"
            subtitle="Соберу стартовую C4-модель из кода проекта: системы, контейнеры и связи между ними. Дальше её можно править руками или поручить персоне."
            action={(
              <div style={{ display: 'flex', gap: SP.sm, flexWrap: 'wrap', justifyContent: 'center' }}>
                <Button variant="primary" size="md" onClick={() => void generateArchitecture(projectId)}
                  leftIcon={<Sparkles size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />}>Собрать из кода</Button>
                {!isMobile && <Button variant="ghost" size="md" onClick={startBlank}>Начать с пустого холста</Button>}
              </div>
            )}
          />
        )}

        {!s.generating && s.corrupt && (
          <EmptyState
            icon={<FileWarning size={ICON_SIZE.xl} strokeWidth={ICON_STROKE} />}
            title="Файл модели повреждён"
            subtitle={`${MODEL_PATH} не разбирается — возможно, его правили руками или это конфликт слияния.`}
            action={(
              <div style={{ display: 'flex', gap: SP.sm, flexWrap: 'wrap', justifyContent: 'center' }}>
                <Button variant="secondary" size="md" onClick={() => onShowFile(MODEL_PATH)}>Показать файл</Button>
                <Button variant="ghost" size="md" onClick={() => void loadArchitecture(projectId, true)}>Повторить</Button>
              </div>
            )}
          />
        )}

        {frameFailed && (
          <EmptyState
            icon={<Unlink size={ICON_SIZE.xl} strokeWidth={ICON_STROKE} />}
            title="Редактор архитектуры не загрузился"
            subtitle={frameFailed === 'not_installed'
              ? 'Модуль Viaduct не установлен на сервере: его ставит скрипт scripts/build-viaduct.ps1.'
              : 'Статика Viaduct недоступна или не ответила вовремя.'}
            action={<Button variant="secondary" size="md" onClick={retryFrame}>Повторить</Button>}
          />
        )}

        {isMobile && !mobileCanvas && hasModel && !s.generating && (
          <MobileOverview elements={elements} onOpenCanvas={() => setMobileCanvas(true)} />
        )}

        {showCanvas && (
          <ViaductFrame
            key={`${projectId}:${s.frameKey}`}
            projectId={projectId}
            frameKey={s.frameKey}
            model={s.status === 'ready' ? s.content : null}
            theme={getEffectiveTheme()}
            focus={s.focus}
            onDirty={onFrameDirty}
            onSave={onFrameSave}
            onFailed={setFrameFailed}
            onWindow={setFrameWindow}
          />
        )}
      </div>
    </div>
  );
}

// Статус сохранения: один чип, четыре значения. «Сохранено» — спокойное, не зелёное.
// Чип — подпись, а не действие: повтор сохранения — отдельная кнопка рядом
function SaveChip({ s }: { s: ArchState }) {
  const icon = { size: ICON_SIZE.xs, strokeWidth: ICON_STROKE };
  const tone = (background: string, color: string) => ({ background, color, border: 'none', fontWeight: 600 });
  const calm = { background: 'transparent', color: C.textMuted, border: 'none', fontWeight: 600 };
  if (s.readOnly && !s.conflict) {
    return <MetaChip style={tone(C.infoBg, C.info)}><Eye {...icon} />Только просмотр</MetaChip>;
  }
  if (s.save === 'failed' || s.conflict) {
    return (
      <>
        <MetaChip style={tone(C.dangerBg, C.dangerText)} title={s.saveError ?? undefined}>
          <AlertTriangle {...icon} />Не сохранено
        </MetaChip>
        {!s.conflict && (
          <Button variant="ghost" size="xs" onClick={retrySave}
            leftIcon={<RefreshCw {...icon} />}>Повторить</Button>
        )}
      </>
    );
  }
  if (s.save === 'saving' || s.dirty) {
    return <MetaChip style={calm}><Loader {...icon} />Сохраняю…</MetaChip>;
  }
  return <MetaChip style={calm}><Check {...icon} />Сохранено в проект</MetaChip>;
}

function ConflictBanner({ conflict, isMobile, onReload, onDownload }: {
  conflict: NonNullable<ArchState['conflict']>; isMobile: boolean; onReload: () => void; onDownload: () => void;
}) {
  const who = conflict.updatedBy ?? 'Кто-то';
  const when = conflict.updatedAt ? ` ${relTime(conflict.updatedAt)}` : '';
  return (
    <div style={{
      flexShrink: 0, display: 'flex', alignItems: 'center', gap: SP.sm, flexWrap: 'wrap',
      padding: `${SP.sm}px ${SP.md}px`, background: C.warningBg, color: C.warningText, fontSize: FS.sm,
    }}>
      <AlertTriangle size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} style={{ flexShrink: 0 }} />
      <span style={{ flex: 1, minWidth: 180 }}>
        {conflict.content === null
          ? <>Файл модели удалили. Ваши правки после этого не сохранены.</>
          : <>Модель изменил <b>{who}</b>{when}. Ваши правки после этого не сохранены.</>}
      </span>
      <Button variant="ghost" size="xs" onClick={onDownload}
        leftIcon={<Download size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />}>{isMobile ? 'Скачать' : 'Скачать мои правки'}</Button>
      <Button variant="primary" size="xs" onClick={onReload}
        leftIcon={<RefreshCw size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />}>Перезагрузить</Button>
    </div>
  );
}

// Мобильный список-обзор: элементы по уровням C4, тап раскрывает описание и связи
function MobileOverview({ elements, onOpenCanvas }: { elements: ArchElement[]; onOpenCanvas: () => void }) {
  const [open, setOpen] = useState<string | null>(null);
  const levels: ArchLevel[] = ['system', 'container', 'component', 'code'];
  return (
    <div style={{ flex: 1, minHeight: 0, overflowY: 'auto', padding: SP.md, display: 'flex', flexDirection: 'column', gap: SP.md }}>
      <Button variant="secondary" size="md" fullWidth onClick={onOpenCanvas}
        leftIcon={<Eye size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />}>Смотреть схему</Button>
      {elements.length === 0 && (
        <span style={{ fontSize: FS.sm, color: C.textMuted }}>В модели пока нет элементов.</span>
      )}
      {levels.map(level => {
        const list = elements.filter(e => e.level === level);
        if (!list.length) return null;
        return (
          <div key={level}>
            <div style={{ fontSize: FS.xs, fontWeight: 600, color: C.textMuted, textTransform: 'uppercase', letterSpacing: '0.4px', marginBottom: SP.xs }}>
              {LEVEL_LABEL[level]} · {list.length}
            </div>
            {list.map(el => (
              <button key={el.id} type="button" onClick={() => setOpen(open === el.id ? null : el.id)}
                style={{
                  display: 'block', width: '100%', textAlign: 'left', minHeight: 44, padding: `${SP.sm}px ${SP.sm}px`,
                  border: 'none', borderBottom: `1px solid ${C.borderLight}`, background: 'transparent', cursor: 'pointer',
                  fontFamily: FONT.sans, color: C.textPrimary,
                }}>
                <div style={{ display: 'flex', alignItems: 'baseline', gap: SP.sm }}>
                  <span style={{ fontWeight: 600, fontSize: FS.sm, color: C.textHeading, flex: 1, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{el.name}</span>
                  {el.technology && <span style={{ fontFamily: FONT.mono, fontSize: FS.xs, color: C.textMuted, flexShrink: 0 }}>{el.technology}</span>}
                </div>
                {el.description && (
                  <div style={{
                    fontSize: FS.xs, color: C.textSecondary, marginTop: 2,
                    ...(open === el.id ? {} : { overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }),
                  }}>{el.description}</div>
                )}
                {open === el.id && (
                  <div style={{ fontSize: FS.xs, color: C.textMuted, marginTop: SP.xs }}>Связей: {el.connections}</div>
                )}
              </button>
            ))}
          </div>
        );
      })}
    </div>
  );
}
