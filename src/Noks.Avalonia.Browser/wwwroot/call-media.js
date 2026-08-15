const mediaEventKind = Object.freeze({
    sdpOffer: 0,
    sdpAnswer: 1,
    iceCandidate: 2,
    connected: 3,
    failed: 4,
})

const signalEventKind = Object.freeze({
    sdpOffer: 40,
    sdpAnswer: 41,
    iceCandidate: 42,
})

const rtcConfiguration = Object.freeze({
    iceServers: [{
        urls: [
            'stun:global.stun.twilio.com:3478',
            'stun:singapore.stun.twilio.com:3478',
            'stun:tokyo.stun.twilio.com:3478',
        ],
    }],
    iceCandidatePoolSize: 2,
    iceTransportPolicy: 'all',
})

const maximumSignalBytes = 256 * 1024
const silentWavDataUrl =
    'data:audio/wav;base64,UklGRiYAAABXQVZFZm10IBAAAAABAAEAQB8AAEAfAAABAAgAZGF0YQEAAACAAA=='
const textEncoder = new TextEncoder()
const textDecoder = new TextDecoder('utf-8', { fatal: true })
let eventHandler = null
let active = null
let operations = Promise.resolve()
let remoteAudio = null
let playbackActivationInstalled = false

export function start(handler) {
    if (typeof handler !== 'function') {
        throw new TypeError('A call-media event handler is required')
    }

    eventHandler = handler
    ensureRemoteAudio()
    installPlaybackActivation()
}

export function begin(attemptId, isCaller) {
    return enqueue(async () => {
        validateAttemptId(attemptId)
        closeActive()

        const call = {
            attemptId,
            isCaller: Boolean(isCaller),
            peer: null,
            audioSender: null,
            localStream: null,
            microphonePromise: null,
            microphoneOfferSent: false,
            pendingCandidates: [],
            localCandidateBatch: [],
            localCandidateTimer: 0,
            disconnectedTimer: 0,
            failed: false,
            playbackWarningShown: false,
        }
        active = call
        call.peer = new RTCPeerConnection(rtcConfiguration)
        installPeerHandlers(call)
        call.audioSender = call.peer.addTransceiver('audio', { direction: 'sendrecv' }).sender
        if (call.isCaller) {
            await call.peer.setLocalDescription(await call.peer.createOffer())
            emitJson(call, mediaEventKind.sdpOffer, call.peer.localDescription)
        }
    })
}

export function activate(attemptId) {
    return enqueue(async () => {
        validateAttemptId(attemptId)
        const call = active
        if (call?.attemptId === attemptId) {
            const microphoneStarted = await startMicrophone(call)
            if (microphoneStarted &&
                active === call &&
                !call.isCaller &&
                !call.microphoneOfferSent &&
                call.peer.signalingState === 'stable') {
                call.microphoneOfferSent = true
                await call.peer.setLocalDescription(await call.peer.createOffer())
                emitJson(call, mediaEventKind.sdpOffer, call.peer.localDescription)
            }
        }
    })
}

export function apply(attemptId, eventKind, payloadBase64) {
    return enqueue(async () => {
        const call = active
        if (!call || call.attemptId !== attemptId) {
            return
        }

        const payload = decodeJsonPayload(payloadBase64)
        switch (eventKind) {
            case signalEventKind.sdpOffer:
                if (payload.type !== 'offer' || call.peer.signalingState !== 'stable') {
                    return
                }
                await call.peer.setRemoteDescription(payload)
                await flushPendingCandidates(call)
                await call.peer.setLocalDescription(await call.peer.createAnswer())
                emitJson(call, mediaEventKind.sdpAnswer, call.peer.localDescription)
                break
            case signalEventKind.sdpAnswer:
                if (payload.type !== 'answer' || call.peer.signalingState !== 'have-local-offer') {
                    return
                }
                await call.peer.setRemoteDescription(payload)
                await flushPendingCandidates(call)
                break
            case signalEventKind.iceCandidate:
                for (const candidate of Array.isArray(payload) ? payload : [payload]) {
                    if (call.peer.remoteDescription) {
                        await call.peer.addIceCandidate(candidate)
                    } else {
                        call.pendingCandidates.push(candidate)
                    }
                }
                break
            default:
                break
        }
    })
}

export function end(attemptId) {
    return enqueue(async () => {
        if (active?.attemptId === attemptId) {
            closeActive()
        }
    })
}

export function dispose() {
    eventHandler = null
    closeActive()
    uninstallPlaybackActivation()
}

function enqueue(operation) {
    const next = operations.then(operation, operation)
    operations = next.catch(error => {
        failActive(error)
    })
    return operations
}

function installPeerHandlers(call) {
    call.peer.addEventListener('icecandidate', event => {
        if (active !== call) {
            return
        }
        if (event.candidate) {
            call.localCandidateBatch.push(event.candidate.toJSON())
            scheduleLocalCandidateFlush(call)
        } else {
            flushLocalCandidates(call)
        }
    })
    call.peer.addEventListener('track', event => {
        if (active !== call) {
            return
        }
        const stream = event.streams[0] || new MediaStream([event.track])
        const audio = ensureRemoteAudio()
        audio.srcObject = stream
        attemptRemotePlayback(call, audio)
    })
    call.peer.addEventListener('connectionstatechange', () => {
        if (active !== call) {
            return
        }
        if (call.peer.connectionState === 'connected') {
            clearDisconnectedTimer(call)
            emit(call, mediaEventKind.connected, new Uint8Array())
        } else if (call.peer.connectionState === 'failed') {
            failActive(new Error('Direct WebRTC audio failed to establish a route.'))
        } else if (call.peer.connectionState === 'disconnected' && call.disconnectedTimer === 0) {
            call.disconnectedTimer = globalThis.setTimeout(() => {
                call.disconnectedTimer = 0
                if (active === call && call.peer.connectionState === 'disconnected') {
                    failActive(new Error('Direct WebRTC audio remained disconnected'))
                }
            }, 10_000)
        }
    })
}

async function tryGetMicrophone() {
    if (!navigator.mediaDevices?.getUserMedia) {
        return null
    }
    try {
        return await navigator.mediaDevices.getUserMedia({
            audio: {
                echoCancellation: true,
                noiseSuppression: true,
                autoGainControl: true,
            },
            video: false,
        })
    } catch (error) {
        console.warn('Noks call microphone is unavailable. The call continues in receive-only mode.', error)
        return null
    }
}

function startMicrophone(call) {
    if (call.microphonePromise) {
        return call.microphonePromise
    }
    setAudioSessionType('play-and-record')
    call.microphonePromise = tryGetMicrophone().then(async stream => {
        if (active !== call) {
            stopStream(stream)
            return false
        }
        if (!stream) {
            setAudioSessionType('playback')
        }
        call.localStream = stream
        const track = stream?.getAudioTracks()[0] || null
        if (track && call.audioSender) {
            await call.audioSender.replaceTrack(track)
        }
        return track !== null
    }).catch(error => {
        if (active === call) {
            setAudioSessionType('playback')
            console.warn('Noks call microphone activation failed', error)
        }
        return false
    })
    return call.microphonePromise
}

async function flushPendingCandidates(call) {
    while (call.pendingCandidates.length > 0 && active === call) {
        await call.peer.addIceCandidate(call.pendingCandidates.shift())
    }
}

function scheduleLocalCandidateFlush(call) {
    if (call.localCandidateTimer !== 0) {
        return
    }
    call.localCandidateTimer = globalThis.setTimeout(() => {
        call.localCandidateTimer = 0
        flushLocalCandidates(call)
    }, 100)
}

function flushLocalCandidates(call) {
    if (active !== call || call.localCandidateBatch.length === 0) {
        return
    }
    const candidates = call.localCandidateBatch.splice(0)
    emitJson(call, mediaEventKind.iceCandidate, candidates)
}

function emitJson(call, kind, value) {
    emit(call, kind, textEncoder.encode(JSON.stringify(value)))
}

function emit(call, kind, bytes) {
    if (active !== call || typeof eventHandler !== 'function') {
        return
    }
    eventHandler(call.attemptId, kind, bytesToBase64(bytes))
}

function failActive(error) {
    const call = active
    if (!call || call.failed) {
        return
    }
    call.failed = true
    console.error('Noks direct call media failed', error)
    emit(call, mediaEventKind.failed, new Uint8Array())
    closeActive()
}

function closeActive() {
    const call = active
    active = null
    if (!call) {
        return
    }
    clearDisconnectedTimer(call)
    if (call.localCandidateTimer !== 0) {
        globalThis.clearTimeout(call.localCandidateTimer)
        call.localCandidateTimer = 0
    }
    for (const track of call.localStream?.getTracks() || []) {
        track.stop()
    }
    setAudioSessionType('playback')
    call.peer?.close()
    call.pendingCandidates.length = 0
    call.localCandidateBatch.length = 0
    if (remoteAudio) {
        remoteAudio.pause()
        remoteAudio.srcObject = null
        remoteAudio.removeAttribute('src')
        remoteAudio.load()
    }
}

function clearDisconnectedTimer(call) {
    if (call.disconnectedTimer !== 0) {
        globalThis.clearTimeout(call.disconnectedTimer)
        call.disconnectedTimer = 0
    }
}

function stopStream(stream) {
    for (const track of stream?.getTracks() || []) {
        track.stop()
    }
}

function ensureRemoteAudio() {
    if (remoteAudio) {
        return remoteAudio
    }
    remoteAudio = document.createElement('audio')
    remoteAudio.autoplay = true
    remoteAudio.playsInline = true
    remoteAudio.muted = false
    remoteAudio.volume = 1
    remoteAudio.tabIndex = -1
    remoteAudio.dataset.noksCallMedia = 'remote'
    remoteAudio.setAttribute('aria-hidden', 'true')
    remoteAudio.style.cssText =
        'position:fixed;width:1px;height:1px;opacity:0;pointer-events:none;left:-10px;top:-10px'
    document.body.append(remoteAudio)
    return remoteAudio
}

function installPlaybackActivation() {
    if (playbackActivationInstalled) {
        return
    }
    playbackActivationInstalled = true
    globalThis.addEventListener('keydown', activateRemotePlayback, { capture: true })
    globalThis.addEventListener('focus', activateRemotePlayback)
    globalThis.addEventListener('pageshow', activateRemotePlayback)
    document.addEventListener('visibilitychange', activateRemotePlaybackIfVisible)
    if ('PointerEvent' in globalThis) {
        globalThis.addEventListener('pointerdown', activateRemotePlayback, {
            capture: true,
            passive: true,
        })
    } else {
        globalThis.addEventListener('touchstart', activateRemotePlayback, {
            capture: true,
            passive: true,
        })
    }
}

function uninstallPlaybackActivation() {
    if (!playbackActivationInstalled) {
        return
    }
    playbackActivationInstalled = false
    globalThis.removeEventListener('keydown', activateRemotePlayback, { capture: true })
    globalThis.removeEventListener('focus', activateRemotePlayback)
    globalThis.removeEventListener('pageshow', activateRemotePlayback)
    document.removeEventListener('visibilitychange', activateRemotePlaybackIfVisible)
    if ('PointerEvent' in globalThis) {
        globalThis.removeEventListener('pointerdown', activateRemotePlayback, { capture: true })
    } else {
        globalThis.removeEventListener('touchstart', activateRemotePlayback, { capture: true })
    }
}

function activateRemotePlayback() {
    void reactivatePlayback()
}

function activateRemotePlaybackIfVisible() {
    if (document.visibilityState !== 'hidden') {
        activateRemotePlayback()
    }
}

export function reactivatePlayback() {
    const audio = ensureRemoteAudio()
    const call = active
    setAudioSessionType(call?.localStream ? 'play-and-record' : 'playback')
    audio.muted = false
    audio.volume = 1
    if (audio.srcObject && call) {
        return audio.play().then(() => {
            if (active === call) {
                call.playbackWarningShown = false
            }
            return true
        }).catch(error => {
        console.warn('Noks remote call audio failed to reactivate.', error)
            return false
        })
    }

    if (!audio.hasAttribute('src')) {
        audio.src = silentWavDataUrl
    }
    audio.currentTime = 0
    return audio.play().then(() => true).catch(error => {
        console.warn('Noks remote call audio failed to prime.', error)
        return false
    })
}

function setAudioSessionType(type) {
    if (!navigator.audioSession) {
        return
    }
    try {
        navigator.audioSession.type = type
    } catch {
    }
}

function attemptRemotePlayback(call, audio) {
    void audio.play().then(() => {
        if (active === call) {
            call.playbackWarningShown = false
        }
    }).catch(error => {
        if (active !== call || call.playbackWarningShown) {
            return
        }
        call.playbackWarningShown = true
        console.warn(
            'Noks remote call audio is blocked. Press a phone key or tap the keypad to enable it.',
            error)
    })
}

function validateAttemptId(value) {
    if (typeof value !== 'string' ||
        !/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(value)) {
        throw new TypeError('A valid call attempt ID is required')
    }
}

function decodeJsonPayload(value) {
    if (typeof value !== 'string' || value.length > 400_000) {
        throw new TypeError('Invalid call signal')
    }
    const bytes = base64ToBytes(value)
    if (bytes.length === 0 || bytes.length > maximumSignalBytes) {
        throw new TypeError('Invalid call signal length')
    }
    return JSON.parse(textDecoder.decode(bytes))
}

function bytesToBase64(bytes) {
    let binary = ''
    const chunkSize = 0x8000
    for (let offset = 0; offset < bytes.length; offset += chunkSize) {
        binary += String.fromCharCode(...bytes.subarray(offset, offset + chunkSize))
    }
    return btoa(binary)
}

function base64ToBytes(value) {
    const binary = atob(value)
    const bytes = new Uint8Array(binary.length)
    for (let index = 0; index < binary.length; index++) {
        bytes[index] = binary.charCodeAt(index)
    }
    return bytes
}
