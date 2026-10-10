import { describe, expect, it } from 'vitest';
import { operationOf } from '../toolLabels';

// Типовая операция по инструменту и команде — для иконки по смыслу
describe('operationOf', () => {
  const bash = (command: string) => operationOf('Bash', { command });

  it.each([
    ['dotnet test --filter FullyQualifiedName~X', 'tests'],
    ['cd backend; dotnet test', 'tests'],
    ['cd frontend && npx vitest run', 'tests'],
    ['npm run test:e2e', 'tests'],
    ['npx playwright test --grep офлайн', 'tests'],
    ['cd frontend && npm run build', 'build'],
    ['dotnet build backend/ClaudeHomeServer.slnx', 'build'],
    ['npx tsc -b', 'build'],
    ['  git status --short', 'git'],
    ['Set-Location frontend; git log -5', 'git'],
  ])('консоль «%s» → %s', (command, op) => {
    expect(bash(command)).toBe(op);
  });

  it.each(['ls', 'npm run dev', 'cd frontend', 'gitk', 'echo dotnet test'])('консоль «%s» — не типовая', command => {
    expect(bash(command)).toBeNull();
  });

  it('PowerShell распознаётся так же, как Bash', () => {
    expect(operationOf('PowerShell', { command: 'cd backend; dotnet build' })).toBe('build');
  });

  it.each([
    ['mcp__tests__run_tests', 'tests'],
    ['mcp__dev__build', 'build'],
    ['mcp__dev__start_stand', 'stand'],
    ['mcp__dev__stop_stand', 'stand'],
    ['mcp__fal-ai__submit_job', 'media'],
    ['mcp__fal__run_model', 'media'],
    ['mcp__glif__compose_project', 'media'],
    ['mcp__higgsfield__generate_image', 'media'],
    ['mcp__local-media__local_generate_image', 'media'],
    ['mcp__image-editor__image_generate', 'media'],
    ['mcp__local-media__local_jobs_wait', 'wait'],
    ['mcp__watch__watch_start', 'wait'],
  ])('инструмент %s → %s', (name, op) => {
    expect(operationOf(name, {})).toBe(op);
  });

  it.each(['mcp__glif__whoami', 'mcp__glif__upload_file', 'Read', 'Grep', 'Task'])('%s — не типовая', name => {
    expect(operationOf(name, {})).toBeNull();
  });

  it('MCP с «shell» в имени командой не распознаётся', () => {
    expect(operationOf('mcp__remote-shell__exec', { command: 'dotnet test' })).toBeNull();
  });
});
