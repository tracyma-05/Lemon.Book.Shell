// Plays narration clips for WebNarrationAudio.cs. One clip at a time: it is fetched whole and played from a blob URL,
// which avoids Safari's range-request problems with audio from the service worker's cache.
//
// The clip plays in an <audio> element, which can speed up or slow down and keep the voice's pitch (preservesPitch);
// a Web Audio buffer source can only change both together. The element's sound goes through a Web Audio gain node,
// because iOS ignores a media element's own volume.

// A tenth of a second of silence, played once from the first tap: iOS lets a media element play without a tap only
// after it has played from one.
const SILENCE = 'data:audio/wav;base64,UklGRiQAAABXQVZFZm10IBAAAAABAAEAQB8AAIA+AAACABAAZGF0YQAAAAA=';

let context = null;
let gain = null;
let element = null;
let objectUrl = null;
let generation = 0; // A newer play() or stop() abandons a clip still loading.
let state = 'idle'; // idle | loading | waiting (for a tap) | playing | ended
let paused = false;
let rate = 1;
let primed = false;
let failed = null;

function ensurePlayer() {
    if (element === null) {
        element = new Audio();
        element.preload = 'auto';
        element.addEventListener('ended', () => {
            if (state === 'playing') {
                state = 'ended';
            }
        });
        element.addEventListener('error', () => {
            // Clearing the last clip's source reports an error too; only the clip being played counts.
            if (objectUrl !== null && element.getAttribute('src') === objectUrl && (state === 'playing' || state === 'waiting')) {
                console.warn('Narration clip failed while playing', element.error);
                state = 'ended';
                failed?.();
            }
        });
        context = new AudioContext();
        gain = context.createGain();
        context.createMediaElementSource(element).connect(gain);
        gain.connect(context.destination);
    }
    return element;
}

function applyRate() {
    element.playbackRate = rate;
    element.defaultPlaybackRate = rate;
    // Every browser keeps the pitch by default; older Safari and Firefox name the switch with a prefix.
    element.preservesPitch = true;
    element.webkitPreservesPitch = true;
    element.mozPreservesPitch = true;
}

// Starts the loaded clip. Before the first tap the audio is still locked: the clip waits, rather than running on
// silently while its subtitles move on. From a tap it starts at once, which is what lets iOS play it at all.
function start(fromTap = false) {
    if (context.state !== 'running' && !fromTap) {
        state = 'waiting';
        return;
    }
    applyRate();
    const mine = generation;
    state = 'playing';
    element.play().catch(() => {
        if (mine === generation && state === 'playing') {
            // Not allowed yet: the next tap starts it.
            state = 'waiting';
        }
    });
}

// Browsers start audio only from a user gesture: the first tap or key press unlocks it, and a clip that was
// waiting for it starts then.
function unlock() {
    const audio = ensurePlayer();
    if (context.state === 'suspended' && !paused) {
        context.resume().catch(() => {});
    }
    if (state === 'waiting' && !paused) {
        start(true);
    } else if (!primed && state === 'idle') {
        primed = true;
        audio.src = SILENCE;
        audio.play().catch(() => {});
    }
}
for (const type of ['pointerdown', 'keydown', 'touchend']) {
    globalThis.addEventListener(type, unlock, { capture: true, passive: true });
}

export function play(url, volume, onFailed) {
    stop();
    const mine = ++generation;
    const audio = ensurePlayer();
    gain.gain.value = volume;
    failed = onFailed;
    state = 'loading';

    fetch(new URL(url, document.baseURI))
        .then(response => {
            if (!response.ok) {
                throw new Error(`${response.status} ${url}`);
            }
            return response.blob();
        })
        .then(blob => {
            if (mine !== generation) {
                return;
            }
            objectUrl = URL.createObjectURL(blob.type ? blob : new Blob([blob], { type: 'audio/mpeg' }));
            audio.src = objectUrl;
            if (paused) {
                state = 'waiting';
                return;
            }
            start();
        })
        .catch(error => {
            if (mine !== generation) {
                return;
            }
            console.warn('Narration clip not played', error);
            state = 'ended';
            onFailed();
        });
}

export function pause() {
    paused = true;
    element?.pause();
}

export function resume() {
    paused = false;
    if (element !== null && (state === 'playing' || state === 'waiting')) {
        start();
    }
}

export function stop() {
    generation++;
    if (element !== null) {
        element.pause();
        element.removeAttribute('src');
        element.load();
    }
    if (objectUrl !== null) {
        URL.revokeObjectURL(objectUrl);
        objectUrl = null;
    }
    state = 'idle';
    paused = false;
}

export function setVolume(volume) {
    if (gain !== null) {
        gain.gain.value = volume;
    }
}

export function setRate(value) {
    rate = value;
    if (element !== null) {
        applyRate();
    }
}

// Seconds into the clip itself (not wall-clock time), so the clip's sentence timings apply at any speed.
export function position() {
    return element !== null && state !== 'idle' && state !== 'loading' ? element.currentTime : 0;
}

export function isEnded() {
    return state === 'idle' || state === 'ended';
}
