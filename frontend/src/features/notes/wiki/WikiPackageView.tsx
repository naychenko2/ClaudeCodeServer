// Экран пакета «Входящих» (S7a, макет llm-wiki-v2 §5): человек решает только спорные
// операции (decision=inbox); auto здесь не показывается вовсе — их решает политика, не
// человек (контракт S4a). Дифф называем «предполагаемое изменение»: DTO несёт НАМЕРЕНИЕ
// операции (op.Text, op.Target) и хэши базы, отрендеренного результата нет — он появится
// только при применении (S4g). Поэтому никакого обещания результата в подписях нет.
//
// Решение замкнуто применением (контракт S4g): decide/accept-all отвечают {package, apply};
// последнее решение, оставившее ноль нерешённых спорных и хотя бы одну принятую, применяет
// пакет в этом же запросе. Отдельной кнопки «Применить» нет. apply_failed/stale конечны:
// показываем причину бэка, кнопки «повторить» нет — повторный разбор даёт новый пакет.
// Отказ без причины невозможен: бэк отвечает 409 wiki_pkg_reason_required, поэтому кнопка
// «Подтвердить отказ» активна только с написанной причиной.
//
// Дифф рендерится только через общий DiffView (c3): свой код здесь — лишь сборка строк
// намерения в текст для DiffView, не его рендер.
// Кнопки блокируются на время запроса (урок S5b/S5e); состояние действия локально экрану.

import { useState } from 'react';
import { AlertTriangle, Check, ChevronLeft, Inbox } from 'lucide-react';
import {
  C, FONT, FS, R, SP, useIsMobile,
  acceptAllWikiPackage, decideWikiPackage, useWikiTree, WikiError,
  type WikiApplyOutcomeDto, type WikiDecisionDto,
  type WikiPackageDto, type WikiPackageOperationDto,
} from 'aihome_shell/kit';
import { Badge, type BadgeTone } from '../../../components/ui/Badge';
import { Button } from '../../../components/ui/Button';
import { TextArea } from '../../../components/ui/Field';
import { Notice } from '../../../components/ui/Notice';
import { DiffView } from '../../../components/DiffView';
import { ICON_SIZE, ICON_STROKE } from '../../../components/ui/icons';
// Цикл импорта WikiInbox ↔ WikiPackageView: словари читаются в рендере, не при сборке
// модуля, — к тому времени оба уже вычислены
import { pendingContested, pkgErrorText } from './WikiInbox';
import { sectionPrefix } from './WikiTab';
import { ORIGIN_META } from './wikiLabels';

// op — пять, удаления нет (WikiPackage.OpValues)
const OP_META: Record<string, { text: string; tone: BadgeTone }> = {
  create: { text: 'новая страница', tone: 'accent' },
  append: { text: 'дописать', tone: 'neutral' },
  supersede: { text: 'замещение', tone: 'warning' },
  conflict: { text: 'противоречие', tone: 'warning' },
  link: { text: 'связь', tone: 'neutral' },
};

// Статусы пакета (WikiPackage.StatusValues) — то, что человек видит в шапке после решения
const PKG_STATUS: Record<string, { tone: BadgeTone; text: string }> = {
  checking: { tone: 'neutral', text: 'проверяется' },
  proposed: { tone: 'warning', text: 'ждёт решения' },
  rejected_by_checks: { tone: 'danger', text: 'отбит проверками' },
  accepted: { tone: 'info', text: 'решён' },
  partially_accepted: { tone: 'info', text: 'решён частично' },
  rejected: { tone: 'neutral', text: 'отклонён' },
  applying: { tone: 'info', text: 'применяется' },
  applied: { tone: 'success', text: 'применён' },
  apply_failed: { tone: 'danger', text: 'не применён' },
  stale: { tone: 'neutral', text: 'устарел' },
  reverted: { tone: 'neutral', text: 'откатан' },
};

// Итог применения: бэк несёт причину человеческим текстом — показываем её как есть;
// фолбэк по коду — только если текста почему-то нет
function applyOutcomeText(a: WikiApplyOutcomeDto): string {
  if (a.reason) return a.reason;
  switch (a.code) {
    case 'wiki_apply_stale': return 'Страницы изменились — пакет устарел';
    case 'wiki_apply_source_unavailable': return 'Источник недоступен';
    case 'wiki_apply_gate': return 'Проверки кода не пропущены';
    case 'wiki_apply_drift': return 'Страница в дрейфе — сначала решите его';
    case 'wiki_apply_internal': return 'Внутренний сбой применения';
  }
  return 'Применение не удалось';
}

// Строки намерения для общего DiffView — НЕ diff страницы и не рендер: «−» — что
// намерение замещает/оспаривает, «+» — что добавляет. Отрендеренный файл существует
// только после применения (S4g), здесь его никто не обещает.
function intentLines(op: WikiPackageOperationDto): string {
  const lines: string[] = [];
  if (op.op === 'supersede' || op.op === 'conflict') {
    if (op.target) lines.push(`- ${op.target}`);
  }
  if (op.op === 'link' && op.target) lines.push(`+ связь: ${op.target}`);
  if (op.text) {
    for (const l of op.text.split('\n')) { if (l.trim()) lines.push(`+ ${l}`); }
  }
  if (!lines.length && op.target) lines.push(`- ${op.target}`);
  return lines.join('\n');
}

// Страница операции всегда её раздела пакета (проверено формой пакета): показываем путь
// без префикса раздела — префикс уже в шапке экрана
function pageName(pkg: WikiPackageDto, page: string): string {
  const prefix = `${pkg.section}/`;
  return page.startsWith(prefix) ? page.slice(prefix.length) : page;
}

function fmtDateTime(iso: string): string {
  const t = Date.parse(iso);
  if (!Number.isFinite(t)) return '';
  const d = new Date(t);
  const p = (n: number) => String(n).padStart(2, '0');
  return `${p(d.getDate())}.${p(d.getMonth() + 1)} ${p(d.getHours())}:${p(d.getMinutes())}`;
}

// ─── Карточка спорной операции ───

function OperationCard({
  pkg, op, busy, decidable, draft, onDraft, onAccept, onRejectOpen, onRejectConfirm, onRejectCancel, rejecting,
}: {
  pkg: WikiPackageDto;
  op: WikiPackageOperationDto;
  busy: boolean;
  decidable: boolean;
  draft: string;
  onDraft: (v: string) => void;
  onAccept: () => void;
  onRejectOpen: () => void;
  onRejectConfirm: () => void;
  onRejectCancel: () => void;
  rejecting: boolean;
}) {
  const isMobile = useIsMobile();
  const meta = OP_META[op.op];
  const decidedBadge = op.humanDecision === 'accepted'
    ? <Badge tone="success" size="xs">принята</Badge>
    : op.humanDecision === 'rejected'
      ? <Badge tone="danger" size="xs">отклонена</Badge>
      : null;
  const diff = intentLines(op);
  return (
    <div
      data-testid="wiki-op-row"
      data-decision="inbox"
      style={{ border: `1px solid ${C.border}`, borderRadius: R.md, padding: SP.sm, marginTop: SP.sm }}
    >
      <div style={{ display: 'flex', alignItems: 'center', gap: SP.xs, flexWrap: 'wrap' }}>
        <span style={{ fontFamily: FONT.mono, fontSize: FS.xs, color: C.textMuted }}>№{op.index + 1}</span>
        {meta && <Badge tone={meta.tone} size="xs">{meta.text}</Badge>}
        <span style={{ fontSize: FS.sm, color: C.textSecondary, overflow: 'hidden', textOverflow: 'ellipsis' }}>
          {pageName(pkg, op.page)}
        </span>
        <span style={{ marginLeft: 'auto', display: 'flex', gap: SP.xs, alignItems: 'center' }}>
          {decidedBadge}
          {!decidedBadge && decidable && !rejecting && (
            <>
              <Button variant="ghost" size="sm" dataTestId={`wiki-op-reject-${op.index}`} disabled={busy} onClick={onRejectOpen}>Отклонить</Button>
              <Button variant="secondary" size="sm" dataTestId={`wiki-op-accept-${op.index}`} disabled={busy} onClick={onAccept}>Принять</Button>
            </>
          )}
        </span>
      </div>
      {/* «Почему здесь» — причина спорности от кода (для inbox она обязательна формой
          пакета): человек решает спорное, зная почему оно спорное */}
      {op.reason && (
        <div data-testid="wiki-op-why" style={{ display: 'flex', gap: SP.xs, alignItems: 'flex-start', marginTop: SP.xs, fontSize: FS.xs, color: C.textSecondary }}>
          <AlertTriangle size={12} strokeWidth={ICON_STROKE} style={{ flexShrink: 0, marginTop: 2 }} />
          <span><b style={{ color: C.textHeading, fontWeight: 600 }}>Почему здесь:</b> {op.reason}</span>
        </div>
      )}
      {op.target && (op.op === 'supersede' || op.op === 'conflict' || op.op === 'link') && (
        <div style={{ marginTop: SP.xs, fontSize: FS.sm, color: C.textSecondary }}>
          {op.op === 'link' ? 'Связь с: ' : op.op === 'conflict' ? 'Оспаривает: ' : 'Замещает: '}
          <span style={{ color: C.textPrimary }}>{op.target}</span>
        </div>
      )}
      {op.text && (
        <div style={{ marginTop: SP.xs, fontSize: FS.base, color: C.textHeading }}>{op.text}</div>
      )}
      {diff && (
        <div style={{ marginTop: SP.sm, border: `1px solid ${C.border}`, borderRadius: R.sm, overflow: 'hidden' }}>
          <div style={{ padding: `${SP.xs}px ${SP.sm}px`, background: C.bgInset, fontSize: FS.xs, color: C.textMuted }}>
            Предполагаемое изменение — намерение Хранителя, не итоговый текст страницы
          </div>
          <DiffView diff={diff} />
        </div>
      )}
      {op.humanDecision === 'rejected' && op.humanReason && (
        <div style={{ marginTop: SP.xs, fontSize: FS.xs, color: C.textSecondary }}>Причина отказа: {op.humanReason}</div>
      )}
      {rejecting && (
        <div style={{ marginTop: SP.sm }}>
          <TextArea
            value={draft}
            onChange={onDraft}
            placeholder="Почему отклоняете? Причина уйдёт Хранителю как правило-кандидат."
            autoGrow
            minHeight={44}
            maxHeight={160}
            disabled={busy}
            isMobile={isMobile}
          />
          <div style={{ display: 'flex', gap: SP.sm, marginTop: SP.xs, alignItems: 'center' }}>
            {/* Отказ без причины невозможен (409 wiki_pkg_reason_required) — кнопка активна
                только с написанной причиной */}
            <Button
              variant="danger" size="sm" dataTestId={`wiki-op-reject-confirm-${op.index}`}
              disabled={busy || !draft.trim()} onClick={onRejectConfirm}
            >
              Подтвердить отказ
            </Button>
            <Button variant="ghost" size="sm" dataTestId={`wiki-op-reject-cancel-${op.index}`} disabled={busy} onClick={onRejectCancel}>Отмена</Button>
          </div>
        </div>
      )}
    </div>
  );
}

// ─── Экран ───

export function WikiPackageView({ pkg: initialPkg, onBack }: { pkg: WikiPackageDto; onBack: () => void }) {
  const tree = useWikiTree();
  const [pkg, setPkg] = useState(initialPkg);
  const [busy, setBusy] = useState(false);
  const [actionError, setActionError] = useState<unknown>(null);
  // Исход решения этого просмотра: null — решений ещё не выносилось; {apply} — ответ
  // последнего decide/accept-all (apply=null ровно когда применять нечего)
  const [decided, setDecided] = useState<{ apply: WikiApplyOutcomeDto | null } | null>(null);
  const [rejectIdx, setRejectIdx] = useState<number | null>(null);
  const [drafts, setDrafts] = useState<Record<number, string>>({});

  const section = tree?.sections.find(s => s.key === pkg.section);
  const src = pkg.sources[0];
  const originMeta = src ? ORIGIN_META[src.origin] : undefined;
  const st = PKG_STATUS[pkg.status];

  const contested = pkg.operations.filter(o => o.decision === 'inbox');
  const pending = pendingContested(pkg);
  const decidable = pkg.status === 'proposed';

  const runDecision = (fn: () => Promise<WikiDecisionDto>) => {
    setBusy(true);
    setActionError(null);
    void fn()
      .then(res => {
        // Карточка и исход — из ответа бэка, не из предположения: статус после применения
        // читается с диска (S4g)
        setPkg(res.package);
        setDecided({ apply: res.apply });
        setRejectIdx(null);
      })
      .catch(e => setActionError(e))
      .finally(() => setBusy(false));
  };

  const outcomeNotice = (() => {
    if (!decided) return null;
    if (decided.apply?.applied) {
      // tone 'success' у Notice нет (NoticeTone — warning/danger/info): применённое —
      // спокойная info-плашка, статус-бейдж «применён» в шапке уже зелёный
      return (
        <Notice tone="info" icon={Check} title="Изменения применены" style={{ marginTop: SP.sm }}>
          Страницы вики обновлены. Откатить пакет можно будет в «Журнале».
        </Notice>
      );
    }
    if (decided.apply) {
      return (
        <Notice tone="danger" icon={AlertTriangle} title={applyOutcomeText(decided.apply)} style={{ marginTop: SP.sm }}>
          {/* apply_failed конечен: повторного применения здесь нет, и кнопки «повторить»
              нет тоже — повторный разбор даёт новый пакет */}
          {decided.apply.violations.length > 0 && (
            <ul style={{ margin: `${SP.xs}px 0 0`, paddingLeft: 18 }}>
              {decided.apply.violations.map(v => <li key={v}>{v}</li>)}
            </ul>
          )}
        </Notice>
      );
    }
    if (pkg.status === 'rejected') {
      return (
        <Notice tone="info" icon={Inbox} title="Все спорные отклонены — пакет закрыт" style={{ marginTop: SP.sm }}>
          В вики ничего не записано. Причины отказов остались в операции — по ним Хранитель
          будет осторожнее в следующий раз.
        </Notice>
      );
    }
    if (!decided.apply && pkg.status !== 'rejected' && pending.length > 0) {
      // apply=null ровно когда применять нечего: здесь часть спорных ещё не решена, статус
      // остался proposed. Подтверждаем, что решение записано, и говорим, сколько осталось:
      // человек продолжает решать на этом же экране (оставшиеся операции — ниже плашки).
      return (
        <Notice tone="info" icon={Inbox} title={`Решение записано, осталось ${pending.length} спорных`} style={{ marginTop: SP.sm }}>
          Остальные спорные операции ниже — решите их, и пакет применится.
        </Notice>
      );
    }
    return null;
  })();

  return (
    <div style={{ padding: `0 ${SP.md}px ${SP.md}px` }}>
      <div style={{ paddingTop: SP.sm }}>
        <Button
          variant="ghost" size="sm" dataTestId="wiki-pkg-back" ariaLabel="Вернуться к списку «Входящих»"
          leftIcon={<ChevronLeft size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />}
          onClick={onBack}
        >
          Назад
        </Button>
      </div>

      <div style={{ fontFamily: FONT.mono, fontSize: FS.xs, color: C.textMuted, marginTop: SP.sm }}>
        Пакет · {originMeta?.label ?? src?.origin ?? 'источник'} · {fmtDateTime(pkg.createdAt)}
      </div>
      <div style={{ fontSize: FS.sm, color: C.textMuted, marginTop: 2 }}>
        {section ? sectionPrefix(section) : pkg.section}
      </div>
      <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm, marginTop: SP.xs, flexWrap: 'wrap' }}>
        <span style={{ fontFamily: FONT.serif, fontSize: FS.lg, fontWeight: 700, color: C.textHeading }}>
          Пакет изменений
        </span>
        {st && <Badge tone={st.tone} size="sm">{st.text}</Badge>}
      </div>
      {src && (
        <div style={{ fontFamily: FONT.mono, fontSize: FS.xs, color: C.textMuted, marginTop: 2, wordBreak: 'break-all' }}>
          {src.ref}{pkg.sources.length > 1 ? ` +${pkg.sources.length - 1}` : ''}
        </div>
      )}

      {actionError ? (
        <div style={{ marginTop: SP.sm }}>
          <Notice
            tone="danger" icon={AlertTriangle}
            title={actionError instanceof WikiError ? pkgErrorText(actionError) : 'Не удалось выполнить действие'}
            action={<Button variant="ghost" size="sm" onClick={() => setActionError(null)}>Закрыть</Button>}
          >
            Решения, вынесенные до отказа, сохранены — список обновится при возврате.
          </Notice>
        </div>
      ) : null}

      {/* statusReason — причина отказа кодов/устаревания/сбоя применения: она часть
          состояния пакета, показываем её рядом со статусом */}
      {pkg.statusReason && !decided && (
        <Notice tone="warning" icon={AlertTriangle} title={pkg.statusReason} style={{ marginTop: SP.sm }} />
      )}

      {outcomeNotice}

      {contested.length === 0 ? (
        <div style={{ marginTop: SP.md, fontSize: FS.sm, color: C.textMuted }}>
          В этом пакете нет операций на ваше решение.
        </div>
      ) : (
        contested.map(op => (
          <OperationCard
            key={op.index}
            pkg={pkg}
            op={op}
            busy={busy}
            decidable={decidable}
            draft={drafts[op.index] ?? ''}
            onDraft={v => setDrafts(d => ({ ...d, [op.index]: v }))}
            rejecting={rejectIdx === op.index}
            onRejectOpen={() => setRejectIdx(op.index)}
            onRejectCancel={() => setRejectIdx(null)}
            onAccept={() => runDecision(() => decideWikiPackage(pkg.id, op.index, true, null))}
            onRejectConfirm={() => runDecision(() => decideWikiPackage(pkg.id, op.index, false, drafts[op.index] ?? null))}
          />
        ))
      )}

      {decidable && contested.length > 0 && (
        <div style={{
          display: 'flex', alignItems: 'center', gap: SP.sm, marginTop: SP.md,
          borderTop: `1px solid ${C.border}`, paddingTop: SP.sm, flexWrap: 'wrap',
        }}>
          {pending.length > 1 && (
            <Button variant="ghost" size="sm" dataTestId="wiki-pkg-accept-all" disabled={busy} onClick={() => runDecision(() => acceptAllWikiPackage(pkg.id))}>
              Принять всё
            </Button>
          )}
          <span style={{ fontSize: FS.sm, color: C.textSecondary, flex: 1, minWidth: 120 }}>
            решено {contested.length - pending.length} из {contested.length}
          </span>
        </div>
      )}
    </div>
  );
}
