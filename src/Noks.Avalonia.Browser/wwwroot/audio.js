let context = null
let workletNode = null
let nodePromise = null
let pcmFallbackEnabled = false
let pcmActive = false
let pcmDemandFrames = 0
let fallbackNextStartTime = 0
let fallbackRequestTimer = 0
const fallbackSources = new Set()
let outputNode = null
let directOutputNode = null
let mediaOutputDestination = null
let speakerAudio = null
let directOutputActive = true
let unlocked = false
let announcementSource = null
let announcementRequest = null
let pendingAnnouncement = null
let announcementEndedHandler = null
let audioSessionConfigured = false
let recoveryTimer = 0
const announcementBuffers = new Map()
const announcementPaths = [
    new URL('./audio/invalid-number.wav', import.meta.url).href,
    new URL('./audio/emergency-calls-unsupported.wav', import.meta.url).href,
]
const cacheBustVersion = new URL(import.meta.url).searchParams.get('v')
const latencyParameters = new URL(globalThis.location?.href ?? 'http://localhost/').searchParams
const latencyEnabled = latencyParameters.get('audio-latency') === '1'
const latencyRunId = latencyParameters.get('latency-run') ?? 'unknown'
const latencyInputQueue = []
const latencyTraces = new Map()
let nextLatencyTraceId = 1

// Unlock before the keypad's capture handlers suppress browser gestures.
// Once unlocked, keep the context alive: deliberately suspending it reintroduces
// the browser autoplay gate and can permanently lose later firmware buzzer notes.
if (latencyEnabled) {
    globalThis.addEventListener('pointerdown', captureLatencyInput, { passive: true, capture: true })
}
globalThis.addEventListener('pointerdown', unlock, { passive: true, capture: true })
globalThis.addEventListener('keydown', unlock, { passive: true, capture: true })
globalThis.addEventListener('touchstart', unlock, { passive: true, capture: true })
globalThis.addEventListener('focus', recoverAudio, { passive: true })
globalThis.addEventListener('pageshow', recoverAudio, { passive: true })
document.addEventListener('visibilitychange', recoverAudio, { passive: true })

function captureLatencyInput(event) {
    if (!event.isTrusted) {
        return
    }

    const canvas = document.querySelector('canvas.avalonia-canvas')
    if (canvas === null) {
        return
    }

    const traceId = nextLatencyTraceId++
    const bounds = canvas.getBoundingClientRect()
    latencyTraces.set(traceId, {
        traceId,
        pointerAt: performance.now(),
        pointerType: event.pointerType,
        x: event.clientX - bounds.left,
        y: event.clientY - bounds.top,
    })
    globalThis.setTimeout(() => reportIncompleteLatencyTrace(traceId), 2_500)
    latencyInputQueue.push(traceId)
    if (latencyInputQueue.length > 32) {
        const discarded = latencyInputQueue.shift()
        latencyTraces.delete(discarded)
    }
}

export function takeLatencyInputId() {
    if (!latencyEnabled || latencyInputQueue.length === 0) {
        return 0
    }

    const traceId = latencyInputQueue.pop()
    latencyInputQueue.length = 0
    const trace = latencyTraces.get(traceId)
    if (trace) trace.csharpReceivedAt = performance.now()
    return traceId
}

function unlock() {
    unlocked = true

    try {
        configureAudioSession()
        ensureContext()
        ensureOutputRoute()
        void resumeContext()
        void primeSpeakerOutput()

        void ensureNodes().then(() => {
            startPcmSink()
            playPendingAnnouncement()
            void resumeContext()
        }).catch(error => {
            console.warn('Noks browser audio failed to unlock.', error)
        })
    } catch (error) {
        console.warn('Noks browser audio failed to initialize.', error)
    }
}

function recoverAudio() {
    if (!unlocked || document.visibilityState === 'hidden') {
        return
    }
    scheduleRecovery()
}

function scheduleRecovery() {
    if (recoveryTimer !== 0) {
        return
    }
    recoveryTimer = globalThis.setTimeout(() => {
        recoveryTimer = 0
        if (!unlocked || document.visibilityState === 'hidden') {
            return
        }

        try {
            configureAudioSession()
            ensureContext()
            ensureOutputRoute()
            void resumeContext()
            void primeSpeakerOutput()
            void ensureNodes().then(() => {
                startPcmSink()
                playPendingAnnouncement()
            })
        } catch (error) {
            console.warn('Noks browser audio recovery failed', error)
        }
    }, 0)
}

function resumeContext() {
    if (context === null || context.state === 'running') {
        return Promise.resolve(true)
    }
    return context.resume()
        .then(() => context.state === 'running')
        .catch(() => false)
}

export async function reactivate() {
    unlocked = true

    try {
        configureAudioSession()
        ensureContext()
        ensureOutputRoute()
        const resumePromise = resumeContext()
        const speakerPromise = primeSpeakerOutput()
        const nodesPromise = ensureNodes()
        const [contextReady, speakerReady] = await Promise.all([resumePromise, speakerPromise])
        await nodesPromise
        startPcmSink()
        playPendingAnnouncement()
        return contextReady && speakerReady && context.state === 'running'
    } catch (error) {
        console.warn('Noks speaker audio failed to reactivate.', error)
        return false
    }
}

function ensureContext() {
    if (context !== null) {
        return
    }

    const AudioContext = globalThis.AudioContext || globalThis.webkitAudioContext

    if (!AudioContext) {
        throw new Error('WebAudio is not available')
    }

    try {
        context = new AudioContext({ latencyHint: 'interactive' })
    } catch {
        context = new AudioContext()
    }

    context.addEventListener('statechange', () => {
        if (context?.state !== 'running') {
            scheduleRecovery()
        }
    })
}

function configureAudioSession() {
    if (audioSessionConfigured || !navigator.audioSession) {
        return
    }

    try {
        navigator.audioSession.type = 'playback'
        audioSessionConfigured = true
    } catch {
        // A later trusted interaction will retry.
    }
}

function ensureOutputRoute() {
    if (outputNode !== null) {
        return
    }

    ensureContext()
    outputNode = context.createGain()
    directOutputNode = context.createGain()
    outputNode.connect(directOutputNode)
    directOutputNode.connect(context.destination)

    // If the browser supports MediaStream output, use a live media element.
    // It participates in the browser speaker and media session.
    // The direct path remains active until play() succeeds. It is also the capability fallback.
    if (typeof context.createMediaStreamDestination !== 'function') {
        return
    }

    try {
        mediaOutputDestination = context.createMediaStreamDestination()
        speakerAudio = document.createElement('audio')
        speakerAudio.autoplay = true
        speakerAudio.playsInline = true
        speakerAudio.muted = false
        speakerAudio.volume = 1
        speakerAudio.preload = 'auto'
        speakerAudio.tabIndex = -1
        speakerAudio.dataset.noksSpeakerOutput = 'media'
        speakerAudio.setAttribute('aria-hidden', 'true')
        speakerAudio.style.cssText =
            'position:fixed;width:1px;height:1px;opacity:0;pointer-events:none;left:-10px;top:-10px'
        speakerAudio.srcObject = mediaOutputDestination.stream
        speakerAudio.addEventListener('playing', () => setDirectOutputEnabled(false))
        speakerAudio.addEventListener('pause', () => setDirectOutputEnabled(true))
        outputNode.connect(mediaOutputDestination)
        document.body.append(speakerAudio)
    } catch (error) {
        mediaOutputDestination = null
        speakerAudio = null
        setDirectOutputEnabled(true)
        console.warn('Noks media-element output route is unavailable. Direct WebAudio is active.', error)
    }
}

function primeSpeakerOutput() {
    ensureOutputRoute()
    if (speakerAudio === null) {
        return Promise.resolve(true)
    }

    speakerAudio.muted = false
    speakerAudio.volume = 1
    try {
        return Promise.resolve(speakerAudio.play())
            .then(() => {
                setDirectOutputEnabled(false)
                return true
            })
            .catch(error => {
                setDirectOutputEnabled(true)
                console.warn('Noks media-element output failed to activate.', error)
                return false
            })
    } catch (error) {
        setDirectOutputEnabled(true)
        console.warn('Noks media-element output failed to activate.', error)
        return Promise.resolve(false)
    }
}

function setDirectOutputEnabled(enabled) {
    if (directOutputNode === null || context === null) {
        return
    }
    const gain = enabled ? 1 : 0
    directOutputActive = enabled
    try {
        directOutputNode.gain.setValueAtTime(gain, context.currentTime)
    } catch {
        directOutputNode.gain.value = gain
    }
}

function getOutputNode() {
    ensureOutputRoute()
    return outputNode
}

function cacheBustedPath(path) {
    if (!cacheBustVersion) {
        return path
    }

    return `${path}?v=${encodeURIComponent(cacheBustVersion)}`
}

async function ensureNodes() {
    if (workletNode !== null || pcmFallbackEnabled) {
        return
    }

    if (nodePromise !== null) {
        await nodePromise
        return
    }

    ensureContext()
    if (!context.audioWorklet || typeof globalThis.AudioWorkletNode !== 'function') {
        enablePcmFallback()
        return
    }

    nodePromise = createWorkletNode()
        .catch(error => {
            console.warn('Noks AudioWorklet failed to start. Buffered PCM output is active.', error)
            enablePcmFallback()
        })
        .finally(() => {
            nodePromise = null
        })
    await nodePromise
}

async function createWorkletNode() {
    const modulePromise = context.audioWorklet.addModule(
        cacheBustedPath(new URL('./audio-worklet.js', import.meta.url).href))
    await promiseWithTimeout(modulePromise, 2_000, 'AudioWorklet startup timed out')
    workletNode = new AudioWorkletNode(context, 'noks-pcm-player')
    workletNode.port.onmessage = event => {
        const data = event.data || {}
        if (data.type === 'need') {
            demandPcm(data.frames)
        } else if (data.type === 'latency-enqueue') {
            const trace = latencyTraces.get(Number(data.traceId) || 0)
            if (trace && trace.workletEnqueueContextTime == null) {
                trace.workletEnqueueContextTime = Number(data.contextTime)
                trace.workletEnqueueMessageAt = performance.now()
            }
        } else if (data.type === 'latency-output') {
            completeLatencyTrace(
                Number(data.traceId) || 0,
                Number(data.contextTime),
                'worklet')
        }
    }
    workletNode.connect(getOutputNode())
    workletNode.port.postMessage({ type: 'active', active: pcmActive })
}

function promiseWithTimeout(promise, milliseconds, message) {
    let timer = 0
    const timeout = new Promise((_, reject) => {
        timer = globalThis.setTimeout(() => reject(new Error(message)), milliseconds)
    })
    return Promise.race([promise, timeout]).finally(() => globalThis.clearTimeout(timer))
}

function enablePcmFallback() {
    pcmFallbackEnabled = true
    startPcmSink()
}

export function takePcmDemandFrames() {
    const frames = pcmDemandFrames
    pcmDemandFrames = 0
    return frames
}

export function getPcmSampleRate() {
    return Math.round(context?.sampleRate || 0)
}

export function getPcmDiagnostics() {
    return {
        active: pcmActive,
        demandFrames: pcmDemandFrames,
        backend: workletNode !== null
            ? 'worklet'
            : pcmFallbackEnabled
                ? 'buffered'
                : 'pending',
        fallbackQueuedSources: fallbackSources.size,
    }
}

export function setPcmActive(active) {
    pcmActive = active === true
    resetPcmSink()

    if (!pcmActive || !unlocked) {
        return
    }

    void ensureNodes().then(() => {
        startPcmSink()
        void resumeContext()
        void primeSpeakerOutput()
    })
}

export function enqueuePcm(samples, latencyTraceId = 0, latencyMetadata = '') {
    if (!pcmActive || samples == null) {
        return
    }

    let pcm16
    try {
        const copied = typeof samples.slice === 'function' ? samples.slice() : samples
        if (copied instanceof Uint16Array) {
            pcm16 = new Uint16Array(copied)
        } else {
            const bytes = copied instanceof Uint8Array
                ? new Uint8Array(copied)
                : Uint8Array.from(copied)
            if ((bytes.length % Uint16Array.BYTES_PER_ELEMENT) !== 0) {
                throw new RangeError('PCM16 data must contain complete samples')
            }
            pcm16 = new Uint16Array(bytes.buffer)
        }
    } finally {
        samples.dispose?.()
    }

    if (pcm16.length === 0) {
        return
    }

    const traceId = Number(latencyTraceId) || 0
    if (latencyEnabled && traceId > 0) {
        const trace = latencyTraces.get(traceId)
        if (trace && trace.pcmJsEnqueueAt == null) {
            trace.pcmJsEnqueueAt = performance.now()
            trace.frames = pcm16.length
            try {
                trace.csharp = JSON.parse(latencyMetadata)
            } catch {
                trace.csharp = null
            }
        }
    }

    if (workletNode !== null) {
        workletNode.port.postMessage({
            type: 'enqueue',
            samples: pcm16,
            traceId,
        }, [pcm16.buffer])
        return
    }

    if (pcmFallbackEnabled) {
        enqueueFallbackPcm(pcm16, traceId)
    }
}

function startPcmSink() {
    if (!pcmActive) {
        return
    }

    if (workletNode !== null) {
        workletNode.port.postMessage({ type: 'active', active: true })
    } else if (pcmFallbackEnabled) {
        scheduleFallbackRequest(0)
    }
}

function demandPcm(frames) {
    if (!pcmActive) {
        return
    }

    const requestedFrames = Math.max(128, Math.min(8_192, Math.ceil(Number(frames) || 0)))
    pcmDemandFrames = Math.max(pcmDemandFrames, requestedFrames)
}

function enqueueFallbackPcm(pcm16, latencyTraceId) {
    ensureContext()
    const signedPcm = new Int16Array(pcm16.buffer, pcm16.byteOffset, pcm16.length)
    const pcm = Float32Array.from(signedPcm, sample => sample / 32_768)
    const buffer = context.createBuffer(1, pcm.length, context.sampleRate)
    buffer.copyToChannel(pcm, 0)

    const source = context.createBufferSource()
    source.buffer = buffer
    source.connect(getOutputNode())

    const now = context.currentTime
    const startTime = Math.max(fallbackNextStartTime, now + 0.015)
    fallbackNextStartTime = startTime + buffer.duration
    fallbackSources.add(source)
    source.onended = () => {
        fallbackSources.delete(source)
        source.disconnect()
        scheduleFallbackRequest(0)
    }
    source.start(startTime)
    completeLatencyTrace(latencyTraceId, startTime, 'buffered')

    const remainingLead = Math.max(0, fallbackNextStartTime - now)
    scheduleFallbackRequest(Math.max(0, (remainingLead - 0.06) * 1_000))
}

function completeLatencyTrace(traceId, outputContextTime, backend) {
    if (!latencyEnabled || traceId <= 0 || !Number.isFinite(outputContextTime)) {
        return
    }

    const trace = latencyTraces.get(traceId)
    if (!trace || trace.completed) {
        return
    }

    trace.completed = true
    const completedAt = performance.now()
    const outputEstimate = estimateOutputPerformanceTime(outputContextTime)
    const csharpStages = trace.csharp?.stagesMs ?? {}
    const fromPointer = timestamp => Number.isFinite(timestamp)
        ? timestamp - trace.pointerAt
        : null
    const fromCSharp = stage => Number.isFinite(Number(csharpStages[stage])) &&
        Number.isFinite(trace.csharpReceivedAt)
        ? trace.csharpReceivedAt - trace.pointerAt + Number(csharpStages[stage])
        : null

    const report = {
        source: 'ios-input-audio-latency',
        runId: latencyRunId,
        traceId,
        trustedInput: true,
        pointer: {
            type: trace.pointerType,
            x: trace.x,
            y: trace.y,
        },
        key: trace.csharp?.key ?? null,
        backend,
        route: speakerAudio !== null && !directOutputActive ? 'media-element' : 'direct-webaudio',
        sampleRate: context?.sampleRate ?? 0,
        frames: trace.frames ?? 0,
        baseLatencyMs: Number.isFinite(context?.baseLatency) ? context.baseLatency * 1_000 : null,
        outputLatencyMs: Number.isFinite(context?.outputLatency) ? context.outputLatency * 1_000 : null,
        csharpStagesMs: csharpStages,
        latencyMs: {
            browserPointerToCSharp: fromPointer(trace.csharpReceivedAt),
            inputQueued: fromCSharp('inputQueued'),
            workerDequeued: fromCSharp('workerDequeued'),
            matrixApplied: fromCSharp('matrixApplied'),
            keypadInterrupt: fromCSharp('keypadInterrupt'),
            firmwareAudioState: fromCSharp('audioStatePublished'),
            audioEventRaised: fromCSharp('audioEventRaised'),
            audioUiDispatch: fromCSharp('audioUiDispatch'),
            backendUpdate: fromCSharp('backendUpdate'),
            pcmDemand: fromCSharp('pcmDemand'),
            pcmRenderStarted: fromCSharp('pcmRenderStarted'),
            pcmRenderCompleted: fromCSharp('pcmRenderCompleted'),
            jsPcmEnqueue: fromPointer(trace.pcmJsEnqueueAt),
            workletEnqueueMessage: fromPointer(trace.workletEnqueueMessageAt),
            workletOutputRendered: outputEstimate.performanceTime - trace.pointerAt,
            reportReceived: completedAt - trace.pointerAt,
        },
        outputTimeEstimate: outputEstimate.method,
        contextState: context?.state ?? 'missing',
        passed: Number.isFinite(outputEstimate.performanceTime) &&
            Number.isFinite(trace.csharpReceivedAt) &&
            Number.isFinite(Number(csharpStages.pcmRenderCompleted)),
    }
    void postLatencyResult(report)
}

function estimateOutputPerformanceTime(contextTime) {
    if (context && typeof context.getOutputTimestamp === 'function') {
        const timestamp = context.getOutputTimestamp()
        if (Number.isFinite(timestamp?.contextTime) && Number.isFinite(timestamp?.performanceTime)) {
            return {
                performanceTime: timestamp.performanceTime +
                    ((contextTime - timestamp.contextTime) * 1_000),
                method: 'getOutputTimestamp',
            }
        }
    }

    const baseLatency = Number.isFinite(context?.baseLatency) ? context.baseLatency : 0
    const outputLatency = Number.isFinite(context?.outputLatency) ? context.outputLatency : 0
    return {
        performanceTime: performance.now() +
            ((contextTime - (context?.currentTime ?? contextTime) + baseLatency + outputLatency) * 1_000),
        method: 'context-clock-plus-reported-latency',
    }
}

async function postLatencyResult(report) {
    try {
        await fetch('/__benchmark/results', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(report),
        })
    } catch (error) {
        console.warn('Noks input audio latency report failed.', error)
    }
}

function reportIncompleteLatencyTrace(traceId) {
    const trace = latencyTraces.get(traceId)
    if (!trace || trace.completed || trace.timeoutReported) {
        return
    }

    trace.timeoutReported = true
    void postLatencyResult({
        source: 'ios-input-audio-latency-timeout',
        runId: latencyRunId,
        traceId,
        trustedInput: true,
        pointer: {
            type: trace.pointerType,
            x: trace.x,
            y: trace.y,
        },
        browserPointerToCSharpMs: Number.isFinite(trace.csharpReceivedAt)
            ? trace.csharpReceivedAt - trace.pointerAt
            : null,
        pcmReachedJavaScript: Number.isFinite(trace.pcmJsEnqueueAt),
        passed: false,
    })
}

function scheduleFallbackRequest(delayMilliseconds) {
    if (!pcmActive || !pcmFallbackEnabled || fallbackRequestTimer !== 0) {
        return
    }

    fallbackRequestTimer = globalThis.setTimeout(() => {
        fallbackRequestTimer = 0
        if (!pcmActive || !pcmFallbackEnabled || context === null) {
            return
        }

        const leadSeconds = Math.max(0, fallbackNextStartTime - context.currentTime)
        const neededFrames = Math.ceil(Math.max(0.12 - leadSeconds, 0.04) * context.sampleRate)
        demandPcm(neededFrames)
    }, delayMilliseconds)
}

function resetPcmSink() {
    pcmDemandFrames = 0
    workletNode?.port.postMessage({ type: 'reset' })
    workletNode?.port.postMessage({ type: 'active', active: pcmActive })

    if (fallbackRequestTimer !== 0) {
        globalThis.clearTimeout(fallbackRequestTimer)
        fallbackRequestTimer = 0
    }

    for (const source of fallbackSources) {
        source.onended = null
        try {
            source.stop()
        } catch {
        }
        source.disconnect()
    }
    fallbackSources.clear()
    fallbackNextStartTime = context?.currentTime || 0
}

export function setAnnouncementEndedHandler(handler) {
    if (typeof handler !== 'function') {
        throw new TypeError('An announcement-ended handler is required')
    }
    announcementEndedHandler = handler
}

export function clearAnnouncementEndedHandler() {
    announcementEndedHandler = null
}

export function playAnnouncement(kind, callId) {
    if (!Number.isInteger(kind) || kind < 0 || kind >= announcementPaths.length ||
        typeof callId !== 'string' || callId.length > 64) {
        return
    }

    const request = { kind, callId }
    announcementRequest = request
    runAnnouncement(request)
}

function runAnnouncement(request) {
    void startAnnouncement(request).catch(error => {
        if (announcementRequest !== request) {
            return
        }
        console.error('Call announcement failed', error)
        announcementSource = null
        announcementRequest = null
        notifyAnnouncementEnded(request.callId)
    })
}

function playPendingAnnouncement() {
    if (pendingAnnouncement === null) {
        return
    }
    const request = pendingAnnouncement
    pendingAnnouncement = null
    runAnnouncement(request)
}

async function startAnnouncement(request) {
    if (!unlocked) {
        pendingAnnouncement = request
        return
    }

    ensureContext()
    void resumeContext()
    void primeSpeakerOutput()

    let buffer = announcementBuffers.get(request.kind)
    if (!buffer) {
        const response = await fetch(cacheBustedPath(announcementPaths[request.kind]), { cache: 'force-cache' })
        if (!response.ok) {
            throw new Error(`Call announcement failed to load (${response.status})`)
        }

        buffer = await context.decodeAudioData(await response.arrayBuffer())
        announcementBuffers.set(request.kind, buffer)
    }

    if (announcementRequest !== request) {
        return
    }

    if (announcementSource !== null) {
        announcementSource.onended = null
        try {
            announcementSource.stop()
        } catch {
        }
    }

    const source = context.createBufferSource()
    announcementSource = source
    source.buffer = buffer
    source.connect(getOutputNode())
    source.onended = () => {
        if (announcementSource === source && announcementRequest === request) {
            announcementSource = null
            announcementRequest = null
            notifyAnnouncementEnded(request.callId)
        }
    }
    source.start()
}

export function stopAnnouncement(callId) {
    if (typeof callId !== 'string' || callId.length > 64 ||
        announcementRequest?.callId !== callId) {
        return
    }

    const request = announcementRequest
    announcementRequest = null
    if (pendingAnnouncement === request) {
        pendingAnnouncement = null
    }

    const source = announcementSource
    announcementSource = null
    if (source !== null) {
        source.onended = null
        try {
            source.stop()
        } catch {
        }
        source.disconnect()
    }
}

function notifyAnnouncementEnded(callId) {
    if (typeof announcementEndedHandler !== 'function') {
        return
    }
    try {
        announcementEndedHandler(callId)
    } catch (error) {
        console.warn('Noks announcement completion failed to release the call.', error)
    }
}

export function dispose() {
    unlocked = false
    pcmActive = false
    resetPcmSink()
    announcementRequest = null
    pendingAnnouncement = null
    if (announcementSource !== null) {
        announcementSource.onended = null
        try {
            announcementSource.stop()
        } catch {
        }
        announcementSource = null
    }
    speakerAudio?.pause()
    setDirectOutputEnabled(true)
}
