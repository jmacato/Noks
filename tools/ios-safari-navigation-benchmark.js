const parameters = new URLSearchParams(location.search)

if (parameters.get('ios-benchmark') === '1') {
    runBenchmark().catch(error => {
        postResult({
            source: 'safari-window',
            runId: parameters.get('benchmark-run') ?? 'unknown',
            error: String(error?.stack ?? error),
            passed: false,
        })
    })
}

async function runBenchmark() {
    const runId = parameters.get('benchmark-run') ?? crypto.randomUUID()
    const durationSeconds = Math.max(35, Math.min(180, Number(parameters.get('benchmark-seconds') ?? 60)))
    const navigation = performance.getEntriesByType('navigation')[0]
    const errors = []
    const inputToFrameMs = []
    const longTasks = []

    addEventListener('error', event => errors.push(String(event.error?.stack ?? event.message)))
    addEventListener('unhandledrejection', event => errors.push(String(event.reason?.stack ?? event.reason)))
    addEventListener('pointerdown', event => {
        if (!event.isTrusted) return
        const receivedAt = performance.now()
        requestAnimationFrame(paintedAt => inputToFrameMs.push(paintedAt - receivedAt))
    }, { capture: true })

    if (typeof PerformanceObserver === 'function') {
        try {
            new PerformanceObserver(list => {
                for (const entry of list.getEntries()) longTasks.push(entry.duration)
            }).observe({ type: 'longtask', buffered: true })
        } catch {
            // Safari versions without the Long Tasks API still report frame pacing.
        }
    }

    const canvas = await waitForCanvas(180_000)
    const canvasReadyMs = performance.now()
    await waitForCondition(() => !document.getElementById('splash'), 180_000)
    const appReadyMs = performance.now()
    const intervals = await sampleAnimationFrames(durationSeconds * 1000)
    const nominalMs = detectNominalInterval(intervals)
    const jitterLimit = nominalMs * 1.5
    const averageMs = average(intervals)

    await postResult({
        source: 'safari-window',
        runId,
        userAgent: navigator.userAgent,
        crossOriginIsolated,
        sharedArrayBuffer: typeof SharedArrayBuffer === 'function',
        screen: {
            width: screen.width,
            height: screen.height,
            devicePixelRatio,
        },
        canvas: {
            width: canvas.width,
            height: canvas.height,
            cssWidth: canvas.getBoundingClientRect().width,
            cssHeight: canvas.getBoundingClientRect().height,
        },
        navigation: navigation ? {
            responseEndMs: navigation.responseEnd,
            domContentLoadedMs: navigation.domContentLoadedEventEnd,
            loadMs: navigation.loadEventEnd,
        } : null,
        canvasReadyMs,
        appReadyMs,
        durationMs: intervals.reduce((sum, value) => sum + value, 0),
        frameCount: intervals.length,
        nominalFps: 1000 / nominalMs,
        measuredFps: averageMs === 0 ? 0 : 1000 / averageMs,
        medianMs: percentile(intervals, 0.50),
        p95Ms: percentile(intervals, 0.95),
        p99Ms: percentile(intervals, 0.99),
        maximumMs: Math.max(0, ...intervals),
        jitterFrames: intervals.filter(value => value > jitterLimit).length,
        droppedFrames: intervals.reduce(
            (sum, value) => sum + Math.max(0, Math.round(value / nominalMs) - 1), 0),
        longTasks: {
            count: longTasks.length,
            totalMs: longTasks.reduce((sum, value) => sum + value, 0),
            maximumMs: Math.max(0, ...longTasks),
        },
        inputToFrameMs,
        errors,
        passed: errors.length === 0,
    })
}

async function sampleAnimationFrames(durationMs) {
    const intervals = []
    let previous = performance.now()
    const endAt = previous + durationMs

    await new Promise(resolve => {
        function sample(now) {
            intervals.push(now - previous)
            previous = now
            if (now >= endAt) {
                resolve()
                return
            }

            requestAnimationFrame(sample)
        }

        requestAnimationFrame(sample)
    })

    return intervals.slice(1)
}

function detectNominalInterval(intervals) {
    const median = percentile(intervals.slice(0, 120), 0.5)
    if (median <= 12.5) return 1000 / 120
    if (median <= 25) return 1000 / 60
    return median
}

function percentile(values, fraction) {
    if (values.length === 0) return 0
    const sorted = [...values].sort((left, right) => left - right)
    return sorted[Math.ceil((sorted.length - 1) * fraction)]
}

function average(values) {
    return values.length === 0 ? 0 : values.reduce((sum, value) => sum + value, 0) / values.length
}

async function waitForCanvas(timeoutMs) {
    await waitForCondition(() => document.querySelector('canvas.avalonia-canvas'), timeoutMs)
    return document.querySelector('canvas.avalonia-canvas')
}

async function waitForCondition(condition, timeoutMs) {
    const startedAt = performance.now()
    while (!condition()) {
        if (performance.now() - startedAt >= timeoutMs) throw new Error('Benchmark startup timed out')
        await new Promise(resolve => setTimeout(resolve, 50))
    }
}

async function postResult(result) {
    const response = await fetch('/__benchmark/results', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(result),
    })
    if (!response.ok) throw new Error(`Benchmark report failed: ${response.status}`)
}
