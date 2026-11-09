export declare const LOCAL_JOBS_WAIT_TOOL = "mcp__local-media__local_jobs_wait";
export declare function localJobsWaitArg(input: unknown): string;
export declare function toolLabel(name: string): string;
export declare function isConsoleTool(name: string): boolean;
export declare function consoleCaption(name: string, input: unknown, streamingArg?: string | null): {
    description: string;
    command: string;
} | null;
export declare const RUN_TESTS_TOOL = "mcp__tests__run_tests";
export declare function testRunKindLabel(input: unknown): string;
export declare const BUILD_TOOL = "mcp__dev__build";
export declare const START_STAND_TOOL = "mcp__dev__start_stand";
export declare function buildKindLabel(input: unknown): string;
export declare function buildArg(input: unknown): string;
export declare function toolCardLabel(name: string, input: unknown): string;
export declare function testRunArg(input: unknown): string;
export type Operation = 'tests' | 'build' | 'stand' | 'git' | 'media' | 'wait';
export declare function operationOf(name: string, input: unknown): Operation | null;
export declare function toolWord(n: number): string;
