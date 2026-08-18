import assert from 'node:assert/strict'
import { readFile } from 'node:fs/promises'

const sourceUrl = new URL('../src/Noks.Avalonia.Browser/wwwroot/audio.js', import.meta.url)
const workletUrl = new URL('../src/Noks.Avalonia.Browser/wwwroot/audio-worklet.js', import.meta.url)
const source = await readFile(sourceUrl, 'utf8')
const workletSource = await readFile(workletUrl, 'utf8')
const browserPcmSource = await readFile(
    new URL('../src/Noks.Avalonia/BrowserBuzzerAudio.cs', import.meta.url),
    'utf8')
const callMediaSource = await readFile(
    new URL('../src/Noks.Avalonia.Browser/wwwroot/call-media.js', import.meta.url),
    'utf8')

assert.doesNotMatch(
    source,
    /iPhone|iPad|iPod|MacIntel|hardwareConcurrency|deviceMemory|userAgent/,
    'browser audio reliability must use capabilities rather than hardware detection')
assert.doesNotMatch(
    callMediaSource,
    /iPhone|iPad|iPod|MacIntel|hardwareConcurrency|deviceMemory|userAgent/,
    'call audio reliability must use capabilities rather than hardware detection')
assert.match(callMediaSource, /audioSession\.type = type/, 'call media does not coordinate browser audio-session state')
assert.match(callMediaSource, /addEventListener\('pageshow', activateRemotePlayback\)/,
    'Remote call playback did not recover after page restoration.')
assert.doesNotMatch(callMediaSource, /remoteAudio\.hidden\s*=\s*true/,
    'Remote call audio uses display:none. This value is not reliable for media playback.')
const callMediaBeginSource = callMediaSource.slice(
    callMediaSource.indexOf('export function begin'),
    callMediaSource.indexOf('export function activate'))
const callMediaActivateSource = callMediaSource.slice(
    callMediaSource.indexOf('export function activate'),
    callMediaSource.indexOf('export function apply'))
assert.doesNotMatch(callMediaBeginSource, /startMicrophone|getUserMedia|play-and-record/,
    'WebRTC preflight engages the microphone before firmware accepts the call')
assert.match(callMediaActivateSource, /startMicrophone\(call\)/,
    'firmware call acceptance cannot activate the WebRTC microphone')
assert.match(callMediaActivateSource, /!call\.isCaller[^]*createOffer\(\)/,
    'the answering peer cannot renegotiate its initially receive-only media path')
assert.match(browserPcmSource, /Dct3AudioPcmGenerator/,
    'browser audio does not use the shared C# PCM mixer')
assert.match(browserPcmSource, /TakePcmDemandFrames/,
    'browser audio does not poll PCM demand on the UI thread')
assert.doesNotMatch(browserPcmSource, /SetPcmNeededHandler/,
    'browser audio still uses a re-entrant JavaScript callback')
assert.match(browserPcmSource, /StopAnnouncement/,
    'browser audio cannot cancel an operator announcement after early call dismissal')
assert.doesNotMatch(browserPcmSource, /BuzzerFrequencyHz|BuzzerGain|Oscillator1Hz|Oscillator2Hz/,
    'browser backend duplicates firmware tone mapping or synthesis')
assert.doesNotMatch(workletSource, /resonan|frequencyHz|dutyCycle/i,
    'AudioWorklet duplicates buzzer synthesis instead of rendering PCM')
assert.doesNotMatch(source, /createFallbackOscillator|oscillator\.type\s*=\s*['"]square['"]/,
    'browser fallback synthesizes a different square-wave buzzer')
assert.doesNotMatch(source, /ringback|createOscillator|\b440\b|\b480\b/i,
    'browser audio still contains synthetic ringback generation')

const globalListeners = new Map()
const documentListeners = new Map()
const appendedElements = []
const audioElements = []

class MockAudioParam {
    constructor(value = 0) {
        this.value = value
        this.events = []
    }

    setValueAtTime(value, time) {
        this.value = value
        this.events.push({ kind: 'set', value, time })
    }

    linearRampToValueAtTime(value, time) {
        this.value = value
        this.events.push({ kind: 'ramp', value, time })
    }

    cancelScheduledValues() {
    }
}

class MockAudioNode {
    constructor() {
        this.connections = []
    }

    connect(destination) {
        this.connections.push(destination)
        return destination
    }

    disconnect() {
        this.connections = []
    }
}

class MockGainNode extends MockAudioNode {
    constructor() {
        super()
        this.gain = new MockAudioParam(1)
    }
}

class MockAudioBuffer {
    constructor(length, sampleRate) {
        this.length = length
        this.sampleRate = sampleRate
        this.duration = length / sampleRate
        this.channel = null
    }

    copyToChannel(samples) {
        this.channel = new Float32Array(samples)
    }
}

class MockBufferSource extends MockAudioNode {
    constructor() {
        super()
        this.buffer = null
        this.onended = null
        this.startTime = null
        this.stopCount = 0
    }

    start(time = 0) {
        this.startTime = time
    }

    stop() {
        this.stopCount++
    }
}

class MockAudioContext {
    static instances = []

    constructor(options) {
        this.options = options
        this.sampleRate = 48_000
        this.state = 'suspended'
        this.currentTime = 1
        this.destination = new MockAudioNode()
        this.gains = []
        this.mediaDestinations = []
        this.buffers = []
        this.bufferSources = []
        this.listeners = new Map()
        this.resumeCount = 0
        this.suspendCount = 0
        MockAudioContext.instances.push(this)
    }

    addEventListener(kind, handler) {
        addListener(this.listeners, kind, handler)
    }

    emit(kind) {
        for (const handler of this.listeners.get(kind) || []) handler()
    }

    resume() {
        this.resumeCount++
        this.state = 'running'
        this.emit('statechange')
        return Promise.resolve()
    }

    suspend() {
        this.suspendCount++
        this.state = 'suspended'
        this.emit('statechange')
        return Promise.resolve()
    }

    createGain() {
        const gain = new MockGainNode()
        this.gains.push(gain)
        return gain
    }

    createMediaStreamDestination() {
        const destination = new MockAudioNode()
        destination.stream = { id: 'speaker-output' }
        this.mediaDestinations.push(destination)
        return destination
    }

    createBuffer(_channels, length, sampleRate) {
        const buffer = new MockAudioBuffer(length, sampleRate)
        this.buffers.push(buffer)
        return buffer
    }

    createBufferSource() {
        const source = new MockBufferSource()
        this.bufferSources.push(source)
        return source
    }

    decodeAudioData() {
        return Promise.resolve(new MockAudioBuffer(this.sampleRate, this.sampleRate))
    }
}

class MockAudioElement {
    constructor() {
        this.autoplay = false
        this.playsInline = false
        this.muted = false
        this.volume = 1
        this.preload = ''
        this.tabIndex = 0
        this.dataset = {}
        this.style = {}
        this.srcObject = null
        this.paused = true
        this.playCount = 0
        this.pauseCount = 0
        this.rejectNextPlay = false
        this.listeners = new Map()
    }

    addEventListener(kind, handler) {
        addListener(this.listeners, kind, handler)
    }

    setAttribute() {
    }

    play() {
        this.playCount++
        if (this.rejectNextPlay) {
            this.rejectNextPlay = false
            this.paused = true
            return Promise.reject(new Error('simulated autoplay rejection'))
        }
        this.paused = false
        for (const handler of this.listeners.get('playing') || []) handler()
        return Promise.resolve()
    }

    pause() {
        this.pauseCount++
        this.paused = true
        for (const handler of this.listeners.get('pause') || []) handler()
    }
}

function addListener(map, kind, handler) {
    if (!map.has(kind)) map.set(kind, [])
    map.get(kind).push(handler)
}

Object.defineProperty(globalThis, 'navigator', {
    configurable: true,
    value: {
        audioSession: { type: 'auto' },
    },
})
Object.defineProperty(globalThis, 'document', {
    configurable: true,
    value: {
        visibilityState: 'visible',
        body: {
            append(element) {
                appendedElements.push(element)
            },
        },
        createElement(kind) {
            assert.equal(kind, 'audio')
            const element = new MockAudioElement()
            audioElements.push(element)
            return element
        },
        addEventListener(kind, handler) {
            addListener(documentListeners, kind, handler)
        },
    },
})
globalThis.addEventListener = (kind, handler) => addListener(globalListeners, kind, handler)
globalThis.AudioContext = MockAudioContext
globalThis.fetch = () => Promise.resolve({
    ok: true,
    arrayBuffer: () => Promise.resolve(new ArrayBuffer(8)),
})

const audio = await import(`${sourceUrl.href}?audio-lifecycle-test=${Date.now()}`)
assert.ok(globalListeners.has('pointerdown'), 'trusted pointer activation listener is missing')
assert.ok(globalListeners.has('pageshow'), 'page restoration recovery listener is missing')
assert.ok(documentListeners.has('visibilitychange'), 'visibility recovery listener is missing')

assert.equal(await audio.reactivate(), true, 'explicit speaker activation did not succeed')
const context = assertSingle(MockAudioContext.instances)
const speaker = assertSingle(audioElements)
assert.deepEqual(context.options, { latencyHint: 'interactive' })
assert.equal(navigator.audioSession.type, 'playback')
assert.equal(context.state, 'running')
assert.equal(context.mediaDestinations.length, 1)
assert.equal(speaker.srcObject, context.mediaDestinations[0].stream)
assert.equal(speaker.playCount, 1)
assert.equal(appendedElements[0], speaker)

const pcmRequests = []
let disposedViews = 0
audio.setPcmActive(true)
await nextTask()
await nextTask()

const requestedFrames = audio.takePcmDemandFrames()
assert.ok(requestedFrames > 0, 'buffered PCM fallback did not request generated samples')
assert.equal(audio.takePcmDemandFrames(), 0, 'PCM demand was not consumed atomically')
assert.equal(audio.getPcmSampleRate(), context.sampleRate)
pcmRequests.push({ frames: requestedFrames, sampleRate: audio.getPcmSampleRate() })
const samples = new Uint16Array(requestedFrames)
const pattern = [0, 0x7fff, 0x8000, 0xffff]
for (let i = 0; i < samples.length; i++) samples[i] = pattern[i % pattern.length]
const bytes = new Uint8Array(samples.buffer)
audio.enqueuePcm({
    slice: () => bytes.slice(),
    dispose: () => disposedViews++,
})
assert.equal(disposedViews, 1, 'managed PCM view was not released after copying')
const pcmBuffer = assertSingle(context.buffers)
assert.deepEqual(
    Array.from(pcmBuffer.channel.subarray(0, 4)),
    [0, 32_767 / 32_768, -1, -1 / 32_768])
assert.equal(context.bufferSources.length, 1)
assert.equal(context.bufferSources[0].startTime, 1.015)
assert.equal(context.suspendCount, 0, 'idle browser audio was explicitly suspended')

audio.setPcmActive(false)
assert.equal(context.bufferSources[0].stopCount, 1, 'PCM fallback was not stopped with firmware tone state')

const announcementCallId = '12345678-1234-4234-8234-123456789abc'
const endedAnnouncements = []
audio.setAnnouncementEndedHandler(callId => endedAnnouncements.push(callId))
audio.playAnnouncement(0, announcementCallId)
await nextTask()
await nextTask()
const announcementSource = context.bufferSources.at(-1)
assert.notEqual(announcementSource, context.bufferSources[0])
assert.equal(announcementSource.startTime, 0, 'operator announcement did not start')
audio.stopAnnouncement('00000000-0000-0000-0000-000000000000')
assert.equal(announcementSource.stopCount, 0, 'another call stopped the active announcement')
audio.stopAnnouncement(announcementCallId)
assert.equal(announcementSource.stopCount, 1, 'early call dismissal did not stop the announcement')
assert.equal(announcementSource.onended, null, 'canceled announcement retained its completion callback')
assert.deepEqual(endedAnnouncements, [], 'canceled announcement re-terminated an already dismissed call')
audio.clearAnnouncementEndedHandler()

speaker.rejectNextPlay = true
const originalWarn = console.warn
console.warn = () => {}
assert.equal(await audio.reactivate(), false, 'failed media activation was reported as ready')
console.warn = originalWarn
assert.equal(context.gains[1].gain.value, 1, 'direct WebAudio fallback was not restored')
assert.equal(await audio.reactivate(), true, 'Speaker activation failed to recover after a rejection.')
assert.equal(context.gains[1].gain.value, 0, 'successful media output left duplicate direct audio enabled')

const resumeCountBeforeInterruption = context.resumeCount
const playCountBeforeInterruption = speaker.playCount
context.state = 'interrupted'
context.emit('statechange')
await nextTask()
assert.equal(context.state, 'running', 'interrupted AudioContext was not recovered')
assert.ok(context.resumeCount > resumeCountBeforeInterruption)
assert.ok(speaker.playCount > playCountBeforeInterruption)

audio.dispose()
assert.equal(speaker.paused, true)
assert.equal(context.suspendCount, 0)

class MockProcessorPort {
    constructor() {
        this.onmessage = null
        this.sent = []
    }

    postMessage(message) {
        this.sent.push(message)
    }

    receive(data) {
        this.onmessage?.({ data })
    }
}

globalThis.AudioWorkletProcessor = class {
    constructor() {
        this.port = new MockProcessorPort()
    }
}
let registeredProcessor = null
globalThis.registerProcessor = (name, processor) => {
    assert.equal(name, 'noks-pcm-player')
    registeredProcessor = processor
}
await import(`${workletUrl.href}?pcm-render-test=${Date.now()}`)

const processor = new registeredProcessor()
processor.port.receive({ type: 'active', active: true })
processor.port.receive({
    type: 'enqueue',
    samples: new Uint16Array([0, 0x7fff, 0x8000, 0xffff]),
})
const output = [[new Float32Array(128)]]
assert.equal(processor.process([], output), true)
assert.deepEqual(
    Array.from(output[0][0].subarray(0, 4)),
    [0, 32_767 / 32_768, -1, -1 / 32_768])
assert.ok(processor.port.sent.some(message => message.type === 'need'),
    'AudioWorklet did not request more C#-generated PCM')

console.log('PASS: one C# PCM mixer feeds worklet and buffered browser audio paths')

function assertSingle(values) {
    assert.equal(values.length, 1)
    return values[0]
}

function nextTask() {
    return new Promise(resolve => setTimeout(resolve, 0))
}
