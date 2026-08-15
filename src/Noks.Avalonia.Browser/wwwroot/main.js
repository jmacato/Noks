const isBrowser = typeof window !== 'undefined'
const assetBasePath = new URL('.', import.meta.url).pathname
const requestedCacheBustVersion = new URL(import.meta.url).searchParams.get('v')
const cacheBustVersion = requestedCacheBustVersion ?? `${Date.now()}`
const sessionId = globalThis.crypto?.randomUUID?.() ?? `${Date.now().toString(36)}-${Math.random().toString(36).slice(2)}`

if (!isBrowser) {
    throw new Error('Expected to run in a browser')
}

function requireThreadedRuntime() {
    if (!globalThis.crossOriginIsolated || typeof SharedArrayBuffer !== 'function') {
        throw new Error('Threaded WebAssembly requires HTTPS with COOP/COEP isolation headers')
    }

    try {
        new WebAssembly.Memory({ initial: 1, maximum: 1, shared: true })
    } catch (error) {
        throw new Error('This browser does not support threaded WebAssembly', { cause: error })
    }
}

const telemetryQueue = []
let telemetryFlushScheduled = false
let telemetryUnavailable = false
let watchdogWorker = null
const maxTelemetryBatchSize = 32
const maxTelemetryQueueSize = 256
const loadingProgress = {
    root: document.getElementById('splash'),
    bar: document.querySelector('.splash-progress'),
    fill: document.querySelector('.splash-progress-fill'),
    status: document.querySelector('.splash-status'),
}

function describeError(error) {
    if (error instanceof Error) {
        return `${error.name}: ${error.message}${error.stack ? `\n${error.stack}` : ''}`
    }

    if (error && typeof error === 'object') {
        const message = 'message' in error ? String(error.message) : ''
        const stack = 'stack' in error ? String(error.stack) : ''
        if (message || stack) {
            return `${message}${stack ? `\n${stack}` : ''}`
        }

        try {
            return JSON.stringify(error, Object.getOwnPropertyNames(error))
        } catch {
            return String(error)
        }
    }

    return String(error)
}

function formatTelemetryArg(arg) {
    if (typeof arg === 'string') {
        return arg
    }

    if (arg instanceof Error || (arg && typeof arg === 'object' && ('message' in arg || 'stack' in arg))) {
        return describeError(arg)
    }

    try {
        return JSON.stringify(arg)
    } catch {
        return String(arg)
    }
}

function loadingFailureStatus(error) {
    const text = describeError(error)
    if (/Threaded WebAssembly|threaded WebAssembly/i.test(text)) {
        return 'Threaded WebAssembly unavailable'
    }

    if (/mono_download_assets|download .* failed|Load failed|Importing a module script is canceled/i.test(text)) {
        return 'Runtime asset download failed'
    }

    if (/firmware/i.test(text)) {
        return 'Firmware load failed'
    }

    return 'Load failed'
}

function clampProgress(value) {
    return Math.min(1, Math.max(0, value))
}

function setLoadingProgress(value, status) {
    const progress = clampProgress(value)
    const percent = Math.round(progress * 100)

    loadingProgress.fill?.style.setProperty('width', `${percent}%`)
    loadingProgress.bar?.setAttribute('aria-valuenow', String(percent))

    if (status && loadingProgress.status) {
        loadingProgress.status.textContent = status
    }
}

function waitForPaint() {
    return new Promise((resolve) => requestAnimationFrame(() => resolve()))
}

function dismissSplashWhenAvaloniaStarts() {
    const host = document.getElementById('out')
    if (!host || !loadingProgress.root) {
        return
    }

    const observer = new MutationObserver(() => {
        if (!host.querySelector(':scope > canvas.avalonia-canvas')) {
            return
        }

        observer.disconnect()
        loadingProgress.root?.remove()
    })

    observer.observe(host, { childList: true })
}

async function readFirmwareBytes(response) {
    const contentLength = Number(response.headers.get('content-length') ?? 0)

    if (!response.body || !Number.isFinite(contentLength) || contentLength <= 0) {
        setLoadingProgress(0.58, 'Loading firmware')
        return new Uint8Array(await response.arrayBuffer())
    }

    const reader = response.body.getReader()
    const chunks = []
    let received = 0

    while (true) {
        const { done, value } = await reader.read()
        if (done) {
            break
        }

        chunks.push(value)
        received += value.length
        setLoadingProgress(0.42 + Math.min(received / contentLength, 1) * 0.32, 'Loading firmware')
    }

    const bytes = new Uint8Array(received)
    let offset = 0
    for (const chunk of chunks) {
        bytes.set(chunk, offset)
        offset += chunk.length
    }

    return bytes
}

async function firmwareBytesToBinaryString(firmwareBytes) {
    let firmwareBinary = ''
    const chunkSize = 0x8000

    for (let i = 0; i < firmwareBytes.length; i += chunkSize) {
        firmwareBinary += String.fromCharCode(...firmwareBytes.subarray(i, i + chunkSize))

        if (i % (chunkSize * 8) === 0) {
            setLoadingProgress(0.76 + (i / firmwareBytes.length) * 0.14, 'Preparing firmware')
            await waitForPaint()
        }
    }

    return firmwareBinary
}

function queueTelemetry(type, args) {
    if (telemetryUnavailable) {
        return
    }

    const text = args.map(formatTelemetryArg).join(' ')

    telemetryQueue.push({
        type,
        text,
        sessionId,
        cacheBustVersion,
        visibility: document.visibilityState,
        href: globalThis.location.href,
        userAgent: navigator.userAgent,
        timestamp: new Date().toISOString(),
    })
    if (telemetryQueue.length > maxTelemetryQueueSize) {
        telemetryQueue.splice(0, telemetryQueue.length - maxTelemetryQueueSize)
    }

    if (!telemetryFlushScheduled) {
        telemetryFlushScheduled = true
        setTimeout(flushTelemetry, 250)
    }
}

function flushTelemetry() {
    telemetryFlushScheduled = false
    const next = telemetryQueue.splice(0, telemetryQueue.length)
    if (telemetryUnavailable) {
        return
    }

    for (let i = 0; i < next.length; i += maxTelemetryBatchSize) {
        const body = JSON.stringify(next.slice(i, i + maxTelemetryBatchSize))
        fetch('/telemetry', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body,
            keepalive: true,
        }).then((response) => {
            if (response.status === 404 || response.status === 405) {
                telemetryUnavailable = true
                telemetryQueue.length = 0
            }
        }).catch(() => {})
    }
}

for (const type of ['log', 'warn', 'error']) {
    const original = console[type].bind(console)
    console[type] = (...args) => {
        original(...args)
        queueTelemetry(type, args)
    }
}

globalThis.addEventListener('error', (event) => {
    queueTelemetry('error', [`window error: ${describeError(event.error ?? event.message)}`])
})

globalThis.addEventListener('unhandledrejection', (event) => {
    queueTelemetry('error', [`unhandled rejection: ${describeError(event.reason)}`])
})

globalThis.addEventListener('pagehide', flushTelemetry)

async function startMainThreadWatchdog() {
    if (!globalThis.Worker) {
        console.warn('Noks browser watchdog unavailable: Worker not supported')
        return
    }

    try {
        const watchdogUrl = new URL('./watchdog.mjs', import.meta.url)
        watchdogUrl.searchParams.set('v', cacheBustVersion)
        const response = await fetch(watchdogUrl)
        if (!response.ok) {
            throw new Error(`Watchdog download failed: ${response.status}`)
        }

        const workerUrl = URL.createObjectURL(new Blob([await response.text()], { type: 'text/javascript' }))
        watchdogWorker = new Worker(workerUrl, { type: 'module' })
        URL.revokeObjectURL(workerUrl)
        watchdogWorker.postMessage({
            kind: 'init',
            telemetryPath: '/telemetry',
            sessionId,
            cacheBustVersion,
            href: globalThis.location.href,
            userAgent: navigator.userAgent,
            visibility: document.visibilityState,
        })
        setInterval(() => {
            watchdogWorker?.postMessage({
                kind: 'heartbeat',
                visibility: document.visibilityState,
                timestamp: new Date().toISOString(),
            })
        }, 1_000)
        globalThis.addEventListener('visibilitychange', () => {
            watchdogWorker?.postMessage({
                kind: 'visibility',
                visibility: document.visibilityState,
                timestamp: new Date().toISOString(),
            })
        })
        globalThis.addEventListener('pagehide', () => {
            watchdogWorker?.postMessage({
                kind: 'pagehide',
                visibility: document.visibilityState,
                timestamp: new Date().toISOString(),
            })
        })
    } catch (error) {
        console.warn('Noks browser watchdog unavailable:', error)
    }
}

let cacheBustReloading = false
let cacheBustUnavailable = false

async function checkServerCacheBust() {
    if (!requestedCacheBustVersion || cacheBustReloading || cacheBustUnavailable) {
        return
    }

    try {
        const cacheBustUrl = new URL('./cache-bust.json', import.meta.url)
        cacheBustUrl.searchParams.set('t', `${Date.now()}`)
        const response = await fetch(cacheBustUrl, { cache: 'no-store' })
        if (response.status === 404 || response.status === 410) {
            cacheBustUnavailable = true
            return
        }

        if (!response.ok) {
            return
        }

        const server = await response.json()
        if (server?.version && server.version !== cacheBustVersion) {
            cacheBustReloading = true
            console.warn(`Noks browser cache bust changed: client=${cacheBustVersion} server=${server.version}`)
            flushTelemetry()
            globalThis.location.reload()
        }
    } catch {
        // Cache-bust polling can fail. Boot and emulation continue offline.
    }
}

function startCacheBustWatcher() {
    setTimeout(checkServerCacheBust, 5_000)
    setInterval(checkServerCacheBust, 30_000)
    globalThis.addEventListener('pageshow', checkServerCacheBust)
    globalThis.addEventListener('visibilitychange', () => {
        if (!document.hidden) {
            checkServerCacheBust()
        }
    })
}

async function boot() {
    requireThreadedRuntime()
    await startMainThreadWatchdog()
    startCacheBustWatcher()
    setLoadingProgress(0.06, 'Loading runtime')
    await waitForPaint()

    const { dotnet } = await import(`./_framework/dotnet.js?v=${encodeURIComponent(cacheBustVersion)}`)
    const dotnetRuntime = await dotnet
        .withDiagnosticTracing(false)
        .withConfig({
            jsThreadBlockingMode: 'WarnWhenBlockingWait',
        })
        .withApplicationArgumentsFromQuery()
        .create()

    setLoadingProgress(0.28, 'Initializing runtime')

    const config = dotnetRuntime.getConfig()
    setLoadingProgress(0.40, 'Loading firmware')
    const firmwareResponse = await fetch(`./firmware/default.fls?v=${encodeURIComponent(cacheBustVersion)}`)

    if (!firmwareResponse.ok) {
        throw new Error(`Failed to load firmware: ${firmwareResponse.status}`)
    }

    const firmwareBytes = await readFirmwareBytes(firmwareResponse)
    setLoadingProgress(0.76, 'Preparing firmware')
    const firmwareBinary = await firmwareBytesToBinaryString(firmwareBytes)

    setLoadingProgress(0.94, 'Starting emulator')
    await waitForPaint()

    dismissSplashWhenAvaloniaStarts()
    await dotnetRuntime.runMain(config.mainAssemblyName, [
        globalThis.location.href,
        `--asset-base=${assetBasePath}`,
        `--cache-bust=${cacheBustVersion}`,
        `--firmware-base64=${btoa(firmwareBinary)}`,
        ...(new URLSearchParams(globalThis.location.search).get('pqc-rendezvous') === '1'
            ? ['--pqc-rendezvous']
            : []),
    ])
}

try {
    await boot()
} catch (error) {
    setLoadingProgress(1, loadingFailureStatus(error))
    console.error('Noks browser load failed:', error)
    flushTelemetry()
    throw error
}
