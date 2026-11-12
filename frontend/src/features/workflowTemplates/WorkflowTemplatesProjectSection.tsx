// Секция «Шаблоны процесса» в настройках проекта (C1b): проектные шаблоны фич.
// Писать может только владелец проекта (временно, до ответа на вопрос 3 Андрею).
import { Workflow } from 'lucide-react';
import { useMe } from '../../lib/defaultPersona';
import { AccordionSection } from '../projects/dialogs/AccordionSection';
import { canWriteProjectTemplates } from './workflowEditorLogic';
import { WorkflowTemplatesEditor } from './WorkflowTemplatesEditor';
import type { Project } from '../../types';

export function WorkflowTemplatesProjectSection({ project }: { project: Project }): React.ReactElement {
  const me = useMe();
  return (
    <AccordionSection icon={Workflow} title="Шаблоны процесса">
      <WorkflowTemplatesEditor
        projectId={project.id}
        canWrite={canWriteProjectTemplates(project.ownerId, me.userId)}
        isAdmin={me.role === 'admin'}
        mcpChoices={project.mcpServersOn ?? []}
      />
    </AccordionSection>
  );
}
