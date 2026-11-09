export declare function isPushSupported(): boolean;
/** Подписано ли текущее устройство. */
export declare function isPushEnabled(): Promise<boolean>;
/** Запросить разрешение, подписаться и зарегистрировать подписку на сервере. */
export declare function enablePush(): Promise<void>;
/** Отписать устройство и удалить подписку на сервере. */
export declare function disablePush(): Promise<void>;
