import { describe, expect, it } from 'vitest';
import { agentInstallCommand, guessAgentOs, isRootNotAllowed, rootsAddCommand } from '../agentInstall';

describe('agentInstallCommand', () => {
  it('Windows — scriptblock с параметрами -Server/-Code', () => {
    expect(agentInstallCommand('windows', 'https://home.example.com/', 'ABCD2345')).toBe(
      '& ([scriptblock]::Create((irm https://home.example.com/agent/install.ps1))) -Server https://home.example.com -Code ABCD2345');
  });

  it('Linux — curl | sh -s -- с --server/--code', () => {
    expect(agentInstallCommand('linux', 'https://home.example.com', 'ABCD2345')).toBe(
      'curl -fsSL https://home.example.com/agent/install.sh | sh -s -- --server https://home.example.com --code ABCD2345');
  });
});

describe('guessAgentOs', () => {
  it('Linux-браузер — linux, Android и прочие — windows', () => {
    expect(guessAgentOs('Mozilla/5.0 (X11; Linux x86_64)')).toBe('linux');
    expect(guessAgentOs('Mozilla/5.0 (Linux; Android 14)')).toBe('windows');
    expect(guessAgentOs('Mozilla/5.0 (Windows NT 10.0; Win64; x64)')).toBe('windows');
  });
});

describe('isRootNotAllowed', () => {
  it('узнаёт отказ агента по тексту причины', () => {
    expect(isRootNotAllowed('Папка проекта не под разрешёнными корнями этой машины: добавь её командой «ai-home-agent roots add <путь>»')).toBe(true);
    expect(isRootNotAllowed('AgentPathRefused')).toBe(true);
    expect(isRootNotAllowed('Путь с «..» за пределы проекта')).toBe(false);
    expect(isRootNotAllowed(null)).toBe(false);
  });
});

describe('rootsAddCommand', () => {
  it('Windows: экранирует $ и обратную кавычку по-PowerShell, обратный слеш не трогает', () => {
    expect(rootsAddCommand('C:\\Users\\me\\$pr`oj', 'windows')).toBe('ai-home-agent roots add "C:\\Users\\me\\`$pr``oj"');
  });

  it('Linux: экранирует \\ " $ ` по-sh', () => {
    expect(rootsAddCommand('/home/me/my "$proj`\\x', 'linux')).toBe('ai-home-agent roots add "/home/me/my \\"\\$proj\\`\\\\x"');
  });

  it('без платформы узнаёт Windows по букве диска', () => {
    expect(rootsAddCommand('D:\\work\\$x', null)).toBe('ai-home-agent roots add "D:\\work\\`$x"');
    expect(rootsAddCommand('/srv/$x', null)).toBe('ai-home-agent roots add "/srv/\\$x"');
  });
});
