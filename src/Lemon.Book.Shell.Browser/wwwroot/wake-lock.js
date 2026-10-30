// Keeps the screen on while the child is reading: an iPad dims after a minute or two without a touch, which is
// shorter than a page takes to look at or to hear read. The lock is the browser's Screen Wake Lock (Safari 16.4+;
// in an installed home-screen app from iOS 18.4). The browser drops it whenever the page is hidden, so it is asked
// for again on coming back. After a while with no touch or key (a book left open) it is let go, so the screen
// can still dim and the battery is spared; the next touch takes it again.

const IDLE_MS = 10 * 60 * 1000;

export function keepScreenAwake() {
    if (!('wakeLock' in navigator)) {
        return;
    }

    let lock = null;
    let requesting = false;
    let idleTimer = 0;

    async function acquire() {
        if (lock !== null || requesting || document.visibilityState !== 'visible') {
            return;
        }
        requesting = true;
        try {
            lock = await navigator.wakeLock.request('screen');
            lock.addEventListener('release', () => { lock = null; });
        } catch (error) {
            // Refused (low battery mode, a policy, or no tap yet on some browsers): the screen dims as usual.
            console.debug('Screen wake lock not granted', error);
        } finally {
            requesting = false;
        }
    }

    function release() {
        lock?.release().catch(() => {});
        lock = null;
    }

    function active() {
        clearTimeout(idleTimer);
        idleTimer = setTimeout(release, IDLE_MS);
        acquire();
    }

    for (const type of ['pointerdown', 'keydown', 'wheel']) {
        document.addEventListener(type, active, { capture: true, passive: true });
    }
    document.addEventListener('visibilitychange', () => {
        if (document.visibilityState === 'visible') {
            active();
        }
    });
    active();
}
