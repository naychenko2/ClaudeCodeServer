// Подтверждение публикации (git push) — общее для бара над композером (ProjectGitBar)
// и панели «Изменения» (GitChangesRail): оба звали push из одинаковых модалок, а логика
// «а не разошлась ли ветка» нетривиальна, дублировать её в двух местах нельзя.
//
// Зачем проверка при открытии: `behind` в статусе git считает относительно ЛОКАЛЬНОЙ копии
// origin/<branch>, а её обновляет только fetch. Без него расхождение вскрывалось бы лишь
// отказом push с сырым текстом git. Поэтому диалог сначала тихо делает fetch, и уже по
// свежим данным решает, что предлагать: обычную публикацию или «Подтянуть и опубликовать»
// (rebase на origin + push одним действием, эндпоинт /git/sync).
import { useEffect, useState } from 'react';
import { C, FONT, FS, MODAL_W, R, SP } from '../lib/design';
import { useGitState, gitFetch, gitPush, gitSync, loadUnpushedLog } from '../lib/git';
import { relTime } from '../lib/gitFormat';
import { prefillComposer } from '../lib/ai/startChat';
import { Modal, ModalActions, useIsMobileModal } from './ui';

// Высота тела диалога фиксирована: содержимое меняется на лету (проверка → список
// коммитов → текст расхождения → ошибка с файлами конфликта), и без фиксации окно
// прыгало бы по высоте прямо под курсором. Всё лишнее скроллится внутри.
const BODY_H = 300;
const BODY_H_MOBILE = 240;

// Дата коммита в строке списка: коротко, но с временем — соседние коммиты одного дня
// различаются только им
const fmtWhen = (iso: string): string => {
  const t = Date.parse(iso);
  if (Number.isNaN(t)) return '';
  return new Date(t).toLocaleString('ru-RU', {
    day: '2-digit', month: '2-digit', year: '2-digit', hour: '2-digit', minute: '2-digit',
  });
};

// Тултип строки: то, что не влезло в неё саму — автор, полная дата и полный хеш
const commitTitle = (c: { subject: string; author: string; date: string; sha: string }): string => {
  const t = Date.parse(c.date);
  const full = Number.isNaN(t)
    ? c.date
    : new Date(t).toLocaleString('ru-RU', { day: 'numeric', month: 'long', year: 'numeric', hour: '2-digit', minute: '2-digit' });
  return `${c.subject}\n${c.author} · ${full}${relTime(c.date) ? ` (${relTime(c.date)})` : ''}\n${c.sha}`;
};

export function PublishDialog({ projectId, onClose }: { projectId: string; onClose: () => void }) {
  const st = useGitState(projectId);
  const status = st.status;
  const isMobile = useIsMobileModal();
  const [checking, setChecking] = useState(true);

  // Тихая сверка с сервером при открытии. Ошибку fetch не прячем: если до origin не
  // достучались, публиковать всё равно некуда — честнее показать причину сразу.
  // Список неопубликованных перечитываем ПОСЛЕ fetch: до него он считается от
  // устаревшей локальной копии origin/<branch>.
  useEffect(() => {
    let alive = true;
    void gitFetch(projectId)
      .then(() => loadUnpushedLog(projectId))
      .finally(() => { if (alive) setChecking(false); });
    return () => { alive = false; };
  }, [projectId]);

  const behind = status?.behind ?? 0;
  const publishN = (status?.ahead ?? 0) || st.unpushed.length;
  // Подтягивать нужно, когда origin ушёл вперёд — либо когда push уже отклонён как
  // расхождение (origin мог уехать в зазор между нашим fetch и push)
  const needSync = behind > 0 || st.diverged;
  // Автослияние уже спотыкнулось на конкретных файлах: повторять его бессмысленно —
  // тот же конфликт повторится. Главным действием становится «Разобрать в чате»
  const conflicts = st.conflictFiles;
  const hasConflict = conflicts.length > 0;

  // Задача агенту: пусть сам запустит ребейз и разрулит конфликт. Ребейз не оставляем
  // «висеть» (SyncAsync его откатывает) — агент начинает с чистого дерева, поэтому
  // просим выполнить подтягивание заново. Публикацию не поручаем: отправит владелец,
  // когда проверит результат слияния.
  const askChat = () => {
    prefillComposer(
      'Ветка разошлась с origin, автоматическое слияние не прошло — правки конфликтуют.\n\n'
      + `Конфликтуют файлы:\n${conflicts.map(f => `- ${f}`).join('\n')}\n\n`
      + 'Выполни `git pull --rebase`, разреши конфликты (сохрани смысл обеих сторон — '
      + 'и локальных правок, и пришедших с сервера), заверши ребейз. '
      + 'Публиковать не нужно — опубликую сам после проверки.\n',
    );
    onClose();
  };

  const run = async () => {
    const ok = needSync ? await gitSync(projectId) : await gitPush(projectId);
    if (!ok) return;   // остаёмся в диалоге: ниже покажется ошибка, действие сменится на sync
    // Панель «Изменения» (если открыта) возвращаем на «Не зафиксировано»:
    // опубликованные коммиты ушли из её селектора
    window.dispatchEvent(new CustomEvent('cc-git-open-working'));
    onClose();
  };

  return (
    <Modal
      width={MODAL_W.wide}
      onClose={onClose}
      title={hasConflict ? 'Изменения конфликтуют' : needSync ? 'Ветка разошлась с сервером' : 'Опубликовать изменения'}
      subtitle={<span>Отправить {publishN} коммит(ов) на сервер</span>}
      footer={
        <ModalActions
          confirmLabel={hasConflict ? 'Разобрать в чате' : needSync ? 'Подтянуть и опубликовать' : 'Опубликовать'}
          loading={st.busy}
          confirmDisabled={checking || st.busy}
          onConfirm={hasConflict ? askChat : () => void run()}
          onCancel={onClose}
        />
      }
    >
      {/* Тело постоянной высоты: пояснение закреплено сверху, всё остальное
          (список коммитов, ошибка) скроллится — окно не меняет размер */}
      <div style={{
        height: isMobile ? BODY_H_MOBILE : BODY_H, minHeight: 0, flexShrink: 0,
        display: 'flex', flexDirection: 'column', gap: SP.md,
      }}>
        <div style={{ flexShrink: 0, fontSize: 13, color: C.textSecondary, fontFamily: FONT.sans, lineHeight: 1.5 }}>
          {checking ? (
            'Проверяю, нет ли новых коммитов на сервере…'
          ) : hasConflict ? (
            <>
              Твои правки и пришедшие с сервера меняют одни и те же места — сами git их не сведёт.
              Ничего не потеряно: подтягивание откачено, репозиторий в исходном состоянии.
              Задачу на разбор можно отдать в чат — агент подтянет изменения и сведёт правки.
            </>
          ) : needSync ? (
            <>
              На сервере есть коммиты, которых нет локально
              {behind > 0 && <> (<span style={{ fontFamily: FONT.mono, color: C.textPrimary }}>{behind}</span>)</>}.
              Твои коммиты будут перенесены поверх них (rebase) и отправлены одним действием.
            </>
          ) : (
            <>
              Локальные коммиты ветки <span style={{ fontFamily: FONT.mono, color: C.textPrimary }}>{status?.branch}</span> будут
              отправлены в удалённый репозиторий (git push).
            </>
          )}
        </div>

        <div style={{ flex: 1, minHeight: 0, overflowY: 'auto' }}>
          {/* Что именно уйдёт: перед публикацией видно поимённо, а не только счётчик.
              Заголовки не режем многоточием — длинную строку видно целиком по
              горизонтальному скроллу (minWidth: max-content тянет ВСЕ строки до самой
              широкой, поэтому колонка даты остаётся выровненной) */}
          {st.unpushed.length > 0 && (
            <div style={{ overflowX: 'auto' }}>
              <div style={{ display: 'flex', flexDirection: 'column', minWidth: 'max-content' }}>
                {st.unpushed.map((c, i) => (
                  <div
                    key={c.sha}
                    title={commitTitle(c)}
                    style={{
                      display: 'flex', alignItems: 'baseline', gap: SP.md,
                      padding: '6px 0', whiteSpace: 'nowrap',
                      borderTop: i === 0 ? 'none' : `1px solid ${C.borderLight}`,
                    }}
                  >
                    <span style={{ fontFamily: FONT.mono, fontSize: 11, color: C.textMuted, flexShrink: 0 }}>{c.shortSha}</span>
                    <span style={{ flex: 1, fontFamily: FONT.sans, fontSize: FS.sm, color: C.textPrimary }}>
                      {c.subject}
                    </span>
                    <span style={{ fontFamily: FONT.mono, fontSize: 11, color: C.textMuted, flexShrink: 0 }}>
                      {fmtWhen(c.date)}
                    </span>
                  </div>
                ))}
              </div>
            </div>
          )}

          {/* Ошибка операции — прямо в диалоге: закрывать его ради текста ошибки не надо,
              а при расхождении действие выше уже сменилось на «Подтянуть и опубликовать» */}
          {st.error && (
            <div style={{
              marginTop: st.unpushed.length > 0 ? SP.md : 0,
              padding: `${SP.sm}px ${SP.md}px`, borderRadius: R.md,
              background: C.bgInset, fontSize: 12.5, color: C.dangerText,
              fontFamily: FONT.sans, lineHeight: 1.45,
            }}>
              {st.error}
              {/* Поимённо: без этого «не удалось слить» не говорит, куда смотреть */}
              {hasConflict && (
                <div style={{ marginTop: SP.sm, display: 'flex', flexDirection: 'column', gap: 2 }}>
                  {conflicts.map(f => (
                    <span key={f} style={{ fontFamily: FONT.mono, fontSize: 12, color: C.textPrimary }}>{f}</span>
                  ))}
                </div>
              )}
            </div>
          )}
        </div>
      </div>
    </Modal>
  );
}
