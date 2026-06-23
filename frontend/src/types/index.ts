export interface Project {
  id: string;
  name: string;
  rootPath: string;
  createdAt: string;
  updatedAt: string;
  sessionCount?: number;
}

export interface Session {
  id: string;
  projectId: string;
  claudeSessionId?: string;
  mode: 'auto' | 'plan' | 'ask';
  status: 'starting' | 'working' | 'active' | 'waiting' | 'finished' | 'error';
  lastMessage?: string;
  messageCount: number;
  createdAt: string;
  updatedAt: string;
  name?: string;
  model?: string;
}

export interface FileEntry {
  name: string;
  path: string;
  isDirectory: boolean;
  size?: number;
  modified: string;
  isModified: boolean;
  // Состояние синхронизации для офлайна: помечен сам / по наследству от папки / нет
  synced?: 'direct' | 'inherited' | null;
}

export interface SyncMark {
  path: string;
  isDirectory: boolean;
}

// WebSocket сообщения от сервера — sessionId присутствует во всех типах
export type ServerMessage = { sessionId: string } & (
  | { type: 'session_started'; claudeSessionId: string; isResume: boolean; model: string; mode: string; cwd?: string; toolCount?: number; mcpServers?: { name: string; status: string }[] }
  | { type: 'text_delta'; text: string }
  | { type: 'thinking_delta'; text: string }
  | { type: 'tool_use'; id: string; name: string; input: unknown; parentToolUseId?: string }
  | { type: 'tool_input_delta'; toolUseId: string; partialJson: string }
  | { type: 'tool_result'; toolUseId: string; content: string; isError: boolean }
  | { type: 'permission_request'; requestId: string; toolName: string; toolInput: unknown }
  | { type: 'ask_question'; toolUseId: string; input: unknown }
  | { type: 'file_changed'; path: string; added: number; removed: number }
  | { type: 'result'; subtype: string; durationMs: number; numTurns: number; usage?: UsageInfo; totalCostUsd?: number; apiErrorStatus?: string; permissionDenials?: string[] }
  | { type: 'error'; text: string }
  | { type: 'rate_limit'; limitType: string; resetsAt?: string; status?: string }
  | { type: 'compact_boundary'; trigger: string; preTokens?: number }
  | { type: 'truncated' }
  | { type: 'redacted_thinking' }
  | { type: 'exited' }
  | { type: 'status_changed'; status: string; lastMessage?: string; messageCount?: number }
);

export interface UsageInfo {
  inputTokens: number;
  outputTokens: number;
  cacheReadTokens: number;
  cacheCreationTokens: number;
}

// Элементы чата
export type ChatItem =
  | { kind: 'user_message'; text: string; attachedPaths?: string[] }
  | { kind: 'session_started'; model: string; mode: string; cwd?: string; toolCount?: number; mcpServers?: { name: string; status: string }[] }
  | { kind: 'text'; text: string }
  | { kind: 'thinking'; text: string; expanded: boolean }
  | { kind: 'tool_use'; id: string; name: string; input: unknown; result?: string; isError?: boolean; parentToolUseId?: string; streamingArg?: string }
  | { kind: 'permission_request'; requestId: string; toolName: string; toolInput: unknown; resolved: boolean }
  | { kind: 'ask_question'; toolUseId: string; input: unknown; resolved: boolean }
  | { kind: 'file_changed'; path: string; added: number; removed: number }
  | { kind: 'result'; subtype: string; durationMs: number; numTurns: number; usage?: UsageInfo; totalCostUsd?: number; apiErrorStatus?: string; permissionDenials?: string[] }
  | { kind: 'rate_limit'; limitType: string; resetsAt?: string; status?: string }
  | { kind: 'compact_boundary'; trigger: string; preTokens?: number }
  | { kind: 'truncated' }
  | { kind: 'redacted_thinking' }
  | { kind: 'interrupted' }
  | { kind: 'resumed' }
  | { kind: 'session_ended' }
  | { kind: 'error'; text: string; canRetry?: boolean };

export interface AuthState {
  serverUrl: string;
  apiKey: string;
}
