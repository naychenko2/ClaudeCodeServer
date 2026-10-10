import { useEffect, useState } from 'react';
import { AlertCircle, Layers, Sparkles } from 'lucide-react';
import type { Project, ProjectSubject, Session } from '../../../types';
import { api } from '../../../lib/api';
import { C, FS, SP } from '../../../lib/design';
import { Button, ConfirmDialog, Notice } from '../../../components/ui';
import { ICON_SIZE, ICON_STROKE } from '../../../components/ui/icons';
import { SubjectRunBar } from '../../../components/ChatSubjects';
import { chatCountWord, isArchivedChat } from '../../../lib/chatFilters';
import { rootResolver } from '../../../lib/chatTree';
import { onSubjectRunFinished, startSubjectRun, useSubjectRun } from '../../../lib/chatSubjects';
import { AccordionSection, type AccordionSummaryTone } from './AccordionSection';

// Секция «Темы чатов» настроек проекта: сводка в заголовке и запуск
// разбора — «Разложить сейчас» (все кандидаты, включая залежи) и «Разложить заново»
// (снять назначения модели и разложить с нуля; закрепления человека остаются).
// Состояние прохода — общий стор с кнопкой в списке чатов (lib/chatSubjects)
export function ChatSubjectsSection({ project, online }: { project: Project; online: boolean }) {
  const run = useSubjectRun(project.id, true);
  const [subjects, setSubjects] = useState<ProjectSubject[] | null>(null);
  const [sessions, setSessions] = useState<Session[] | null>(null);
  const [askReset, setAskReset] = useState(false);

  // Сводка грузится на открытие настроек и заново на конце прохода разбора — иначе
  // «N тем · K без темы» осталась бы цифрами до разбора
  useEffect(() => {
    let cancelled = false;
    const load = () => {
      api.projects.subjects.list(project.id).then(r => { if (!cancelled) setSubjects(r); }).catch(() => {});
      api.sessions.list(project.id).then(r => { if (!cancelled) setSessions(r); }).catch(() => {});
    };
    load();
    const off = onSubjectRunFinished(pid => { if (pid === project.id) load(); });
    return () => { cancelled = true; off(); };
  }, [project.id]);

  // «Без темы» — корневые неархивные чаты без темы или с висячей темой
  const unassigned = subjects && sessions ? (() => {
    const known = new Set(subjects.map(s => s.id));
    const rootOf = rootResolver(sessions);
    return sessions.filter(c => !isArchivedChat(c) && rootOf(c).id === c.id && !(c.subjectId && known.has(c.subjectId))).length;
  })() : null;

  const lastError = !run.running ? run.state?.lastError : null;
  const summary: { text: string; tone: AccordionSummaryTone } | undefined = run.running
    ? { text: 'Идёт разбор', tone: 'neutral' }
    : lastError
      ? { text: 'Разбор не удался', tone: 'err' }
      : subjects
        ? {
          text: `${subjects.length} ${plural(subjects.length, 'тема', 'темы', 'тем')}`
            + (unassigned !== null ? ` · ${unassigned} ${chatCountWord(unassigned)} без темы` : ''),
          tone: 'neutral',
        }
        : undefined;
  const busyTitle = run.running ? 'Разбор уже идёт' : !online ? 'Нет связи с сервером' : undefined;

  return (
    <>
      <AccordionSection icon={Layers} title="Темы чатов" summary={summary?.text} summaryTone={summary?.tone}>
        <div style={{ fontSize: FS.base, color: C.textSecondary, lineHeight: 1.5 }}>
          Локальная модель раскладывает чаты проекта по темам. Новые чаты — сама, старые — кнопкой.
          В облако ничего не уходит.
        </div>
        {run.running && (
          <div style={{ marginTop: SP.sm }}>
            <SubjectRunBar run={run} />
          </div>
        )}
        {lastError && (
          <Notice tone="danger" icon={AlertCircle} title="Разбор не удался" style={{ marginTop: SP.sm }}
            action={<Button variant="ghost" size="sm" disabled={!online} onClick={() => { void startSubjectRun(project.id); }}>Попробовать снова</Button>}>
            {lastError}
          </Notice>
        )}
        <div style={{ display: 'flex', flexWrap: 'wrap', gap: SP.sm, marginTop: SP.md }}>
          <Button variant="primary" size="sm" loading={run.starting} disabled={run.running || !online} title={busyTitle}
            leftIcon={<Sparkles size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />}
            onClick={() => { void startSubjectRun(project.id); }}>
            Разложить сейчас
          </Button>
          <Button variant="ghost" size="sm" disabled={run.running || run.starting || !online} title={busyTitle}
            onClick={() => setAskReset(true)}>
            Разложить заново
          </Button>
        </div>
      </AccordionSection>
      {askReset && (
        <ConfirmDialog
          title="Разложить чаты заново?"
          subtitle="Снять темы, которые расставил AI, и разложить чаты заново? Ваши закрепления останутся."
          confirmLabel="Разложить заново"
          onConfirm={async () => { await startSubjectRun(project.id, true); setAskReset(false); }}
          onCancel={() => setAskReset(false)}
        />
      )}
    </>
  );
}

function plural(n: number, one: string, few: string, many: string): string {
  const m10 = n % 10, m100 = n % 100;
  if (m10 === 1 && m100 !== 11) return one;
  if (m10 >= 2 && m10 <= 4 && (m100 < 10 || m100 >= 20)) return few;
  return many;
}
