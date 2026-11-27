import type { Event, UnlistenFn } from "@tauri-apps/api/event";

// Registration can finish after an effect has already been cleaned up (StrictMode or navigation).
export function subscribeAsync(register: (active: () => boolean) => Promise<UnlistenFn>): UnlistenFn {
  let disposed = false;
  let unlisten: UnlistenFn | undefined;
  void register(() => !disposed).then((stop) => {
    if (disposed) stop();
    else unlisten = stop;
  }).catch((error) => {
    if (!disposed) console.debug("Event subscription failed", error);
  });
  return () => {
    disposed = true;
    unlisten?.();
  };
}

export function subscribeEvent<T>(name: string, handler: (event: Event<T>) => void): UnlistenFn {
  return subscribeAsync(async (active) => {
    const { listen } = await import("@tauri-apps/api/event");
    return listen<T>(name, (event) => { if (active()) handler(event); });
  });
}
