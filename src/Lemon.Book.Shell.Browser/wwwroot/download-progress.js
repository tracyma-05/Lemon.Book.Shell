// The splash's download progress: which file is coming and how much of it has arrived, how much of the whole, the
// speed and the time left. Shared by the Lemon apps' web heads; keep them in step.
//
// The .NET runtime counts files, not bytes, so the boot files are fetched here (its resource loader) and their bytes
// counted as they arrive. download-sizes.json, written at publish (the Browser csproj), gives each boot file's size,
// raw and Brotli. What arrives is counted decoded; a file the server sent compressed is scaled to its Brotli size, so
// the sizes and the speed shown are close to what crosses the network. Without the sizes (a debug run) the bar
// follows the runtime's file count and no time left is shown.

const FINGERPRINTED = /\.[a-z0-9]{10}\.[a-z]+$/;
const ALGORITHMS = { sha256: 'SHA-256', sha384: 'SHA-384', sha512: 'SHA-512' };
const SPEED_WINDOW_MS = 3000;

const TEXT = /^zh/i.test(document.documentElement.lang) || /^zh/i.test(navigator.language)
    ? {
        file: (name, size) => `正在下载 ${name} · ${size}`,
        total: size => `共 ${size}`,
        left: seconds => seconds < 60 ? `还要约 ${seconds} 秒` : `还要约 ${Math.ceil(seconds / 60)} 分钟`,
        starting: '下载完成，正在启动…',
    }
    : {
        file: (name, size) => `${name} · ${size}`,
        total: size => size,
        left: seconds => seconds < 60 ? `about ${seconds} s left` : `about ${Math.ceil(seconds / 60)} min left`,
        starting: 'Downloaded, starting…',
    };

/** Returns the runtime's resource loader and progress callback, both reporting to the splash. */
export function downloadProgress() {
    const bar = document.querySelector('.splash-progress');
    const fileLine = document.querySelector('.splash-file');
    const totalLine = document.querySelector('.splash-total');

    let sizes = null; // path under _framework/ → [raw, brotli]
    fetch('./download-sizes.json', { cache: 'no-cache' })
        .then(response => response.ok ? response.json() : null)
        .then(json => { sizes = json; render(); })
        .catch(() => { });

    const files = new Map(); // path → { received (decoded bytes), compressed, done }
    let compressedSeen = true; // how a file not yet started will probably arrive
    let runtimeLoaded = 0;
    let runtimeTotal = 0;
    let shown = 0;
    const samples = []; // [time, bytes arrived] over the last few seconds, for the speed
    let timer = 0;

    function expected(path, compressed) {
        const size = sizes?.[path];
        return size ? (compressed ? size[1] : size[0]) : null;
    }

    function arrived(path, file) {
        const size = sizes?.[path];
        // Decoded bytes, scaled to what crossed the network when the server compressed the file.
        return size && file.compressed && size[0] > 0 ? file.received * size[1] / size[0] : file.received;
    }

    function totals() {
        let loaded = 0;
        let total = 0;
        for (const [path, file] of files) {
            loaded += arrived(path, file);
            total += expected(path, file.compressed) ?? file.received;
        }
        // Every boot assembly is coming, asked for or not yet; the ICU data (.dat) counts once one is asked for,
        // since the runtime takes only the one its culture needs.
        for (const path of Object.keys(sizes ?? {})) {
            if (!files.has(path) && path.endsWith('.wasm')) {
                total += expected(path, compressedSeen);
            }
        }
        return { loaded, total };
    }

    function speed(now, loaded) {
        samples.push([now, loaded]);
        while (samples.length > 2 && now - samples[1][0] >= SPEED_WINDOW_MS) {
            samples.shift();
        }
        const [since, from] = samples[0];
        return now - since >= 1000 ? (loaded - from) * 1000 / (now - since) : null;
    }

    function current() {
        let best = null;
        for (const [path, file] of files) {
            if (file.done) {
                continue;
            }
            const left = (expected(path, file.compressed) ?? 0) - arrived(path, file);
            if (best === null || left > best.left) {
                best = { path, file, left };
            }
        }
        return best;
    }

    function render() {
        const { loaded, total } = totals();
        const pending = [...files.values()].some(file => !file.done);
        const finished = !pending && runtimeTotal > 0 && runtimeLoaded === runtimeTotal;
        const bytesPerSecond = speed(performance.now(), loaded);

        const byBytes = sizes && total > 0 ? 100 * loaded / total : 0;
        const byFiles = runtimeTotal > 0 ? 100 * runtimeLoaded / runtimeTotal : 0;
        const percent = shown = Math.min(100, Math.max(shown, Math.round(finished ? 100 : sizes ? byBytes : byFiles)));
        const secondsLeft = sizes && bytesPerSecond > 0 ? Math.max(1, Math.ceil((total - loaded) / bytesPerSecond)) : null;

        if (bar) {
            bar.firstElementChild.style.width = `${percent}%`;
            bar.setAttribute('aria-valuenow', String(percent));
            bar.setAttribute('aria-valuetext',
                finished ? TEXT.starting : [`${percent}%`, secondsLeft && TEXT.left(secondsLeft)].filter(Boolean).join(' · '));
        }
        if (fileLine) {
            const coming = current();
            fileLine.textContent = finished ? TEXT.starting : coming === null ? '' : TEXT.file(
                displayName(coming.path),
                sizeOf(arrived(coming.path, coming.file), expected(coming.path, coming.file.compressed)));
        }
        if (totalLine) {
            totalLine.textContent = finished || files.size === 0 ? '' : [
                TEXT.total(sizeOf(loaded, sizes ? total : null)),
                bytesPerSecond !== null && `${bytes(bytesPerSecond)}/s`,
                secondsLeft && TEXT.left(secondsLeft),
            ].filter(Boolean).join(' · ');
        }
        if (!pending && timer) {
            clearInterval(timer);
            timer = 0;
        }
    }

    async function fetchCounted(path, url, integrity) {
        const file = { received: 0, compressed: compressedSeen, done: false };
        files.set(path, file);
        if (!timer) {
            timer = setInterval(render, 500); // also when nothing arrives, so a stall shows as the speed falling
        }

        try {
            const response = await fetch(url, { cache: FINGERPRINTED.test(path) ? 'force-cache' : 'no-cache' });
            if (!response.ok || !response.body) {
                return response; // the runtime reports it, as it would its own fetch
            }
            compressedSeen = file.compressed = /\b(br|gzip|zstd|deflate)\b/.test(response.headers.get('content-encoding') ?? '');

            const reader = response.body.getReader();
            const chunks = [];
            for (;;) {
                const { done, value } = await reader.read();
                if (done) {
                    break;
                }
                chunks.push(value);
                file.received += value.byteLength;
            }

            const body = await new Blob(chunks).arrayBuffer();
            await checkIntegrity(body, integrity, path);
            return new Response(body, {
                status: response.status,
                statusText: response.statusText,
                headers: { 'content-type': response.headers.get('content-type') ?? 'application/octet-stream' },
            });
        } finally {
            file.done = true;
            render();
        }
    }

    return {
        // The runtime's own fetch for the JavaScript modules and the boot manifest; ours, counted, for the rest.
        // Integrity is checked here, as the runtime would, so needs crypto.subtle (https or localhost).
        loadBootResource(type, name, defaultUri, integrity) {
            if (type === 'dotnetjs' || type === 'manifest' || (integrity && !globalThis.crypto?.subtle)) {
                return undefined;
            }
            const pathname = new URL(defaultUri, location.href).pathname;
            const at = pathname.lastIndexOf('/_framework/');
            return fetchCounted(at >= 0 ? pathname.slice(at + '/_framework/'.length) : name, defaultUri, integrity);
        },
        onDownloadResourceProgress(loaded, total) {
            runtimeLoaded = loaded;
            runtimeTotal = total;
            render();
        },
    };
}

async function checkIntegrity(body, integrity, path) {
    const dash = integrity ? integrity.indexOf('-') : -1;
    const algorithm = dash > 0 ? ALGORITHMS[integrity.slice(0, dash)] : undefined;
    if (!algorithm) {
        return; // none, or one the browser would ignore too
    }
    const digest = new Uint8Array(await crypto.subtle.digest(algorithm, body));
    if (btoa(String.fromCharCode(...digest)) !== integrity.slice(dash + 1)) {
        throw new TypeError(`Integrity check failed for ${path}`);
    }
}

// Avalonia.Base.jna1zhkt4o.wasm → Avalonia.Base.wasm: the fingerprint means nothing to a reader.
function displayName(path) {
    return path.slice(path.lastIndexOf('/') + 1).replace(/\.[a-z0-9]{10}(\.[a-z]+)$/, '$1');
}

function sizeOf(loaded, total) {
    return total ? `${bytes(loaded, total)} / ${bytes(total)}` : bytes(loaded);
}

// In the unit of the larger number (`scale`), so "0.4 / 7.5 MB" rather than "400 KB / 7.5 MB".
function bytes(count, scale = count) {
    if (scale >= 1024 * 1024) {
        const megabytes = count / (1024 * 1024);
        return scale === count ? `${megabytes.toFixed(1)} MB` : megabytes.toFixed(1);
    }
    const kilobytes = Math.max(count > 0 ? 1 : 0, Math.round(count / 1024));
    return scale === count ? `${kilobytes} KB` : String(kilobytes);
}
