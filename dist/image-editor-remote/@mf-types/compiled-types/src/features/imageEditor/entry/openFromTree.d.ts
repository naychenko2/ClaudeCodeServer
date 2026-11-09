import type { ImageEditorOpenRequest } from '../../../lib/subsystems/registryCore';
import type { Session } from '../../../types';
export declare function lastActiveChat(list: Session[]): Session | null;
export declare function openFromTree(req: ImageEditorOpenRequest): Promise<void>;
