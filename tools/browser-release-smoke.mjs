import assert from 'node:assert/strict'
import { createServer } from 'node:http'
import { mkdir, readFile, stat } from 'node:fs/promises'
import { dirname, extname, resolve, sep } from 'node:path'
import { fileURLToPath } from 'node:url'
import { chromium } from '../src/Noks.Avalonia.Browser/node_modules/playwright/index.mjs'

const repositoryRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..')
const rootArgumentIndex = process.argv.indexOf('--root')
const root = resolve(
    repositoryRoot,
    rootArgumentIndex >= 0 ? process.argv[rootArgumentIndex + 1] : 'artifacts/noks-browser-release')
const basePathArgumentIndex = process.argv.indexOf('--base-path')
const requestedBasePath = basePathArgumentIndex >= 0 ? process.argv[basePathArgumentIndex + 1] : ''
const basePath = requestedBasePath === '' || requestedBasePath === '/'
    ? ''
    : `/${requestedBasePath.replace(/^\/+|\/+$/g, '')}`
const callMediaOnly = process.argv.includes('--call-media-only')
const announcementOnly = process.argv.includes('--announcement-only')
const screenshotArgumentIndex = process.argv.indexOf('--screenshots')
const screenshotDirectory = resolve(
    repositoryRoot,
    screenshotArgumentIndex >= 0
        ? process.argv[screenshotArgumentIndex + 1]
        : 'artifacts/browser-smoke')
await mkdir(screenshotDirectory, { recursive: true })

function contentType(path) {
    return {
        '.css': 'text/css; charset=utf-8',
        '.dat': 'application/octet-stream',
        '.fls': 'application/octet-stream',
        '.html': 'text/html; charset=utf-8',
        '.js': 'text/javascript; charset=utf-8',
        '.json': 'application/json; charset=utf-8',
        '.mjs': 'text/javascript; charset=utf-8',
        '.wasm': 'application/wasm',
        '.wav': 'audio/wav',
    }[extname(path)] ?? 'application/octet-stream'
}

const server = createServer(async (request, response) => {
    response.setHeader('Cross-Origin-Opener-Policy', 'same-origin')
    response.setHeader('Cross-Origin-Embedder-Policy', 'require-corp')
    response.setHeader('Cross-Origin-Resource-Policy', 'same-origin')
    response.setHeader('X-Content-Type-Options', 'nosniff')
    try {
        const pathname = decodeURIComponent(new URL(request.url, 'http://localhost').pathname)
        if (pathname === '/telemetry') {
            response.statusCode = 204
            response.end()
            return
        }
        if (pathname === '/favicon.ico') {
            response.statusCode = 204
            response.end()
            return
        }
        if (pathname === '/api/session-region') {
            response.setHeader('Content-Type', 'application/json; charset=utf-8')
            response.setHeader('Cache-Control', 'private, no-store')
            response.end('{"country":""}')
            return
        }
        if (basePath && pathname === basePath) {
            response.statusCode = 308
            response.setHeader('Location', `${basePath}/`)
            response.end()
            return
        }
        if (basePath && !pathname.startsWith(`${basePath}/`)) {
            response.statusCode = 404
            response.end('not found')
            return
        }
        const assetPathname = basePath ? pathname.slice(basePath.length) : pathname
        if (assetPathname === '/call-media-smoke') {
            response.setHeader('Content-Type', 'text/html; charset=utf-8')
            response.end('<!doctype html><meta charset="utf-8"><title>Call media smoke</title>')
            return
        }
        let path = resolve(root, `.${assetPathname === '/' ? '/index.html' : assetPathname}`)
        if (path !== root && !path.startsWith(`${root}${sep}`)) {
            response.statusCode = 403
            response.end('forbidden')
            return
        }
        if ((await stat(path)).isDirectory()) path = resolve(path, 'index.html')
        response.setHeader('Content-Type', contentType(path))
        response.end(await readFile(path))
    } catch {
        response.statusCode = 404
        response.end('not found')
    }
})
await new Promise(resolveListening => server.listen(0, '127.0.0.1', resolveListening))
const baseUrl = `http://127.0.0.1:${server.address().port}`
const browser = await chromium.launch({
    headless: !process.argv.includes('--headed'),
    args: [
        '--use-fake-device-for-media-stream',
        '--use-fake-ui-for-media-stream',
    ],
})

async function smokeDirectCallMedia() {
    const caller = await browser.newPage()
    const receiver = await browser.newPage()
    const pages = [caller, receiver]
    try {
        for (const [index, page] of pages.entries()) {
            await page.context().grantPermissions(['microphone'], { origin: baseUrl })
            await page.goto(`${baseUrl}${basePath}/call-media-smoke`)
            await page.evaluate(async ({ modulePath, moduleSuffix }) => {
                globalThis.callMediaEvents = []
                globalThis.callMediaPlaybackAttempts = []
                globalThis.callMediaMicrophoneRequests = 0
                const originalGetUserMedia = navigator.mediaDevices.getUserMedia.bind(
                    navigator.mediaDevices)
                navigator.mediaDevices.getUserMedia = constraints => {
                    globalThis.callMediaMicrophoneRequests++
                    return originalGetUserMedia(constraints)
                }
                const playbackPermitted = new WeakSet()
                let trustedGestureActive = false
                const beginTrustedGesture = () => {
                    trustedGestureActive = true
                    setTimeout(() => { trustedGestureActive = false }, 0)
                }
                globalThis.addEventListener('keydown', beginTrustedGesture, { capture: true })
                globalThis.addEventListener('pointerdown', beginTrustedGesture, { capture: true })
                const originalPlay = HTMLMediaElement.prototype.play
                HTMLMediaElement.prototype.play = function () {
                    const attempt = {
                        remote: Boolean(this.srcObject),
                        outcome: 'pending',
                    }
                    globalThis.callMediaPlaybackAttempts.push(attempt)
                    if (trustedGestureActive) playbackPermitted.add(this)
                    if (!playbackPermitted.has(this)) {
                        const error = new DOMException(
                            'Playback requires a user gesture',
                            'NotAllowedError')
                        attempt.outcome = error.name
                        return Promise.reject(error)
                    }
                    const promise = originalPlay.call(this)
                    promise.then(
                        () => { attempt.outcome = 'resolved' },
                        error => { attempt.outcome = error.name })
                    return promise
                }
                globalThis.callMediaModule = await import(`${modulePath}?smoke=${moduleSuffix}`)
                globalThis.callMediaModule.start((attemptId, kind, payload) => {
                    globalThis.callMediaEvents.push({ attemptId, kind, payload })
                })
            }, { modulePath: `${basePath}/call-media.js`, moduleSuffix: index })
        }

        await Promise.all(pages.map(page => page.keyboard.press('Enter')))
        const attemptId = '12345678-1234-4234-8234-123456789abc'
        await receiver.evaluate(id => globalThis.callMediaModule.begin(id, false), attemptId)
        await caller.evaluate(id => globalThis.callMediaModule.begin(id, true), attemptId)
        assert.deepEqual(
            await Promise.all(pages.map(page =>
                page.evaluate(() => globalThis.callMediaMicrophoneRequests))),
            [0, 0],
            'WebRTC preflight requested microphone access before firmware acceptance')

        const connected = [false, false]
        const failures = []
        const signalKind = new Map([[0, 40], [1, 41], [2, 42]])
        const routeCallMediaEvents = async () => {
            for (let source = 0; source < pages.length; source++) {
                const events = await pages[source].evaluate(() => globalThis.callMediaEvents.splice(0))
                for (const event of events) {
                    if (event.kind === 3) {
                        connected[source] = true
                    } else if (event.kind === 4) {
                        failures.push(source === 0 ? 'caller' : 'receiver')
                    } else if (signalKind.has(event.kind)) {
                        const target = source === 0 ? receiver : caller
                        await target.evaluate(
                            ({ id, kind, payload }) => globalThis.callMediaModule.apply(id, kind, payload),
                            { id: attemptId, kind: signalKind.get(event.kind), payload: event.payload })
                    }
                }
            }
        }
        const deadline = Date.now() + 20_000
        while ((!connected[0] || !connected[1]) && Date.now() < deadline) {
            await routeCallMediaEvents()
            await new Promise(resolveWait => setTimeout(resolveWait, 50))
        }

        assert.deepEqual(failures, [], 'direct call media reported failure')
        assert.deepEqual(connected, [true, true], 'direct call media did not connect both peers')
        assert.deepEqual(
            await Promise.all(pages.map(page =>
                page.evaluate(() => globalThis.callMediaMicrophoneRequests))),
            [0, 0],
            'WebRTC negotiation requested microphone access before firmware acceptance')
        await Promise.all(pages.map(page =>
            page.evaluate(id => globalThis.callMediaModule.activate(id), attemptId)))
        await Promise.all(pages.map(page => page.waitForFunction(
            () => globalThis.callMediaMicrophoneRequests === 1,
            null,
            { timeout: 5_000 })))
        await Promise.all(pages.map(page =>
            page.evaluate(id => globalThis.callMediaModule.activate(id), attemptId)))
        assert.deepEqual(
            await Promise.all(pages.map(page =>
                page.evaluate(() => globalThis.callMediaMicrophoneRequests))),
            [1, 1],
            'firmware media activation requested the microphone more than once')
        const playbackDeadline = Date.now() + 5_000
        let playback = []
        while (Date.now() < playbackDeadline) {
            await routeCallMediaEvents()
            playback = await Promise.all(pages.map(page => page.evaluate(() => {
                const audio = document.querySelector('audio[data-noks-call-media="remote"]')
                const tracks = audio?.srcObject?.getAudioTracks?.() ?? []
                return {
                    microphoneRequests: globalThis.callMediaMicrophoneRequests,
                    paused: audio?.paused,
                    muted: audio?.muted,
                    volume: audio?.volume,
                    tracks: tracks.map(track => ({
                        muted: track.muted,
                        readyState: track.readyState,
                    })),
                    attempts: globalThis.callMediaPlaybackAttempts,
                }
            })))
            if (playback.every(state =>
                state.paused === false &&
                state.tracks.some(track => track.readyState === 'live' && !track.muted) &&
                state.attempts.some(attempt => attempt.remote && attempt.outcome === 'resolved'))) {
                break
            }
            await new Promise(resolveWait => setTimeout(resolveWait, 50))
        }
        for (const state of playback) {
            assert.equal(state.paused, false,
                `remote call audio remained paused: ${JSON.stringify(playback)}`)
            assert.equal(state.muted, false, 'remote call audio was muted')
            assert.equal(state.volume, 1, 'remote call audio volume was not full')
            assert.ok(
                state.tracks.some(track => track.readyState === 'live' && !track.muted),
                `remote call audio had no live unmuted track: ${JSON.stringify(playback)}`)
            assert.ok(
                state.attempts.some(attempt => !attempt.remote),
                'remote call audio was not primed by the user gesture')
            assert.ok(
                state.attempts.some(attempt => attempt.remote && attempt.outcome === 'resolved'),
                'remote call audio did not start after autoplay gating')
        }
        await Promise.all(pages.map(page =>
            page.evaluate(id => globalThis.callMediaModule.end(id), attemptId)))
        return { callerConnected: connected[0], receiverConnected: connected[1], playback }
    } finally {
        await Promise.all(pages.map(page => page.close()))
    }
}

async function readStoredProfile(page) {
    return page.evaluate(async () => {
        const database = await new Promise((resolveDatabase, rejectDatabase) => {
            const request = indexedDB.open('noks-profile', 1)
            request.onsuccess = () => resolveDatabase(request.result)
            request.onerror = () => rejectDatabase(request.error)
        })
        return new Promise((resolveProfile, rejectProfile) => {
            const transaction = database.transaction('profiles', 'readonly')
            const request = transaction.objectStore('profiles').get(
                new URL(globalThis.location.href).searchParams.get('slot') || 'primary')
            request.onsuccess = () => resolveProfile(JSON.parse(request.result))
            request.onerror = () => rejectProfile(request.error)
        })
    })
}

async function pressPhoneKeys(page, keys) {
    for (const key of keys) {
        await page.keyboard.down(key)
        await page.waitForTimeout(120)
        await page.keyboard.up(key)
        await page.waitForTimeout(300)
    }
}

async function installAnnouncementAudioProbe(page) {
    await page.addInitScript(() => {
        const probe = {
            available: false,
            starts: [],
        }
        globalThis.noksAnnouncementAudioProbe = probe

        const baseAudioContextPrototype = globalThis.BaseAudioContext?.prototype
        const originalCreateBufferSource = baseAudioContextPrototype?.createBufferSource
        if (typeof originalCreateBufferSource !== 'function') {
            return
        }

        probe.available = true
        baseAudioContextPrototype.createBufferSource = function (...createArguments) {
            const source = originalCreateBufferSource.apply(this, createArguments)
            const originalConnect = source.connect
            const originalStart = source.start
            let connectionCount = 0
            let connectedToDestination = false

            source.connect = function (destination, ...connectArguments) {
                connectionCount++
                connectedToDestination ||=
                    typeof globalThis.AudioDestinationNode === 'function' &&
                    destination instanceof globalThis.AudioDestinationNode
                return originalConnect.call(this, destination, ...connectArguments)
            }
            source.start = function (...startArguments) {
                const buffer = this.buffer
                const record = {
                    bufferDuration: buffer?.duration ?? 0,
                    channelCount: buffer?.numberOfChannels ?? 0,
                    sampleRate: buffer?.sampleRate ?? 0,
                    connectionCount,
                    connectedToDestination,
                    contextState: this.context?.state ?? null,
                    startedContextTime: this.context?.currentTime ?? null,
                    ended: false,
                    endedContextTime: null,
                }
                this.addEventListener('ended', () => {
                    record.ended = true
                    record.endedContextTime = this.context?.currentTime ?? null
                }, { once: true })
                probe.starts.push(record)
                return originalStart.apply(this, startArguments)
            }
            return source
        }
    })
}

async function assertCallAnnouncement(
    page,
    digits,
    expectedPath,
    announcementDurationMilliseconds) {
    const startIndex = await page.evaluate(() =>
        globalThis.noksAnnouncementAudioProbe?.starts?.length ?? 0)
    await page.mouse.click(420, 220)
    const requestPromise = page.waitForRequest(
        request => new URL(request.url()).pathname === expectedPath,
        { timeout: 60_000 })
    await pressPhoneKeys(page, [...digits])
    await page.screenshot({
        path: resolve(screenshotDirectory, `dial-${digits}.png`),
        fullPage: true,
    })
    await pressPhoneKeys(page, ['Enter'])
    await page.waitForTimeout(2_000)
    await page.screenshot({
        path: resolve(screenshotDirectory, `call-${digits}.png`),
        fullPage: true,
    })
    await requestPromise
    await page.waitForFunction(index => {
        const record = globalThis.noksAnnouncementAudioProbe?.starts?.[index]
        return record?.bufferDuration > 0 && record.contextState === 'running'
    }, startIndex, { timeout: 10_000 })
    const started = await page.evaluate(index =>
        globalThis.noksAnnouncementAudioProbe.starts[index], startIndex)
    assert.ok(started.bufferDuration > 0, 'announcement decoded to an empty audio buffer')
    assert.ok(
        Math.abs(started.bufferDuration * 1_000 - announcementDurationMilliseconds) < 150,
        `announcement duration ${started.bufferDuration}s does not contain the expected two voice transmissions`)
    assert.ok(started.channelCount > 0, 'announcement audio buffer has no channels')
    assert.ok(started.sampleRate > 0, 'announcement audio buffer has no sample rate')
    assert.ok(started.connectionCount > 0, 'announcement source was never connected')
    assert.equal(
        started.connectedToDestination,
        true,
        'announcement source was not connected to the AudioContext destination')
    assert.equal(started.contextState, 'running', 'announcement started in a suspended AudioContext')
    await page.screenshot({
        path: resolve(screenshotDirectory, `announcement-${digits}.png`),
        fullPage: true,
    })
    await page.waitForFunction(index =>
        globalThis.noksAnnouncementAudioProbe?.starts?.[index]?.ended === true,
        startIndex,
        { timeout: announcementDurationMilliseconds + 3_000 })
    const ended = await page.evaluate(index =>
        globalThis.noksAnnouncementAudioProbe.starts[index], startIndex)
    assert.ok(
        ended.endedContextTime - ended.startedContextTime >= ended.bufferDuration - 0.25,
        'announcement audio source ended before its decoded buffer played')
    // Playback completion now initiates network-side call release. A following
    // scenario can only dial successfully after the firmware returns to idle.
    await page.waitForTimeout(4_000)
}

async function smokeRuntime(path, slot, expectedThreads, settleMilliseconds, announcementScenario = null) {
    const page = await browser.newPage({ viewport: { width: 1440, height: 1100 } })
    await installAnnouncementAudioProbe(page)
    const errors = []
    page.on('console', message => {
        if (message.type() === 'error') errors.push(`console: ${message.text()}`)
    })
    page.on('pageerror', error => errors.push(`pageerror: ${error.message}`))
    page.on('response', response => {
        if (response.status() >= 400) errors.push(`response ${response.status()}: ${response.url()}`)
    })
    page.on('requestfailed', request => errors.push(
        `request failed: ${request.url()} (${request.failure()?.errorText ?? 'unknown'})`))
    await page.goto(`${baseUrl}${basePath}${path}?waku=mock&slot=${slot}&no-ip-operator=1`, {
        waitUntil: 'domcontentloaded',
        timeout: 120_000,
    })
    await page.locator('canvas.avalonia-canvas').waitFor({ timeout: 180_000 })
    await page.locator('#splash').waitFor({ state: 'detached', timeout: 30_000 })
    await page.waitForTimeout(settleMilliseconds)

    const runtime = await page.evaluate(() => ({
        isolated: globalThis.crossOriginIsolated,
        sharedArrayBuffer: typeof SharedArrayBuffer === 'function',
        threads: globalThis.getDotnetRuntime?.(0)?.runtimeBuildInfo?.wasmEnableThreads,
        canvas: (() => {
            const canvas = document.querySelector('canvas.avalonia-canvas')
            return canvas ? { width: canvas.width, height: canvas.height } : null
        })(),
    }))
    assert.equal(runtime.isolated, true, `${path} was not cross-origin isolated`)
    assert.equal(runtime.sharedArrayBuffer, true, `${path} did not expose SharedArrayBuffer`)
    assert.equal(runtime.threads, expectedThreads, `${path} loaded the wrong WASM runtime`)
    assert.ok(runtime.canvas?.width > 0 && runtime.canvas?.height > 0, `${path} canvas is empty`)
    const announcementProbeAvailable = await page.evaluate(() =>
        globalThis.noksAnnouncementAudioProbe?.available === true)
    assert.equal(announcementProbeAvailable, true, `${path} failed to instrument WebAudio playback.`)

    const profile = await readStoredProfile(page)
    assert.equal(profile.version, 3)
    assert.equal(Buffer.from(profile.entropy, 'base64').byteLength, 32)
    assert.match(profile.userName, /^[a-z]+-[a-z0-9]{4}$/)
    assert.match(profile.phoneNumber, /^\d{13}$/)
    assert.equal(profile.phoneNumber.includes('+'), false)
    assert.deepEqual(profile.contacts, [])
    assert.deepEqual(profile.bindings, [])
    assert.deepEqual(profile.rememberedEvents, [])

    if (announcementScenario === 'maximum') {
        await assertCallAnnouncement(
            page,
            '12345678901234567890',
            `${basePath}/audio/invalid-number.wav`,
            12_495)
        await assertCallAnnouncement(page, '112', `${basePath}/audio/emergency-calls-unsupported.wav`, 15_615)
    } else if (announcementScenario === 'short') {
        await assertCallAnnouncement(page, '12345', `${basePath}/audio/invalid-number.wav`, 12_495)
    }

    await page.screenshot({
        path: resolve(screenshotDirectory, `${slot}.png`),
        fullPage: true,
    })
    const unexpectedErrors = errors.filter(error =>
        !error.includes('Failed to create render target for mode 3') &&
        !error.includes('Failed to create render target for mode 2') &&
        !error.includes('/telemetry (net::ERR_ABORTED)') &&
        !error.includes('response 404:') &&
        !error.includes('Failed to load resource: the server responded with a status of 404'))
    assert.deepEqual(unexpectedErrors, [], `${path} emitted browser errors:\n${errors.join('\n')}`)
    await page.close()
    return { profile, runtime }
}

try {
    if (announcementOnly) {
        const announcement = await smokeRuntime('/', 'announcement-audio', true, 25_000, 'short')
        console.log(JSON.stringify({
            result: 'PASS',
            announcement: {
                userName: announcement.profile.userName,
                runtime: announcement.runtime,
            },
            basePath: basePath || '/',
        }))
    } else {
        const callMedia = await smokeDirectCallMedia()
        if (callMediaOnly) {
            console.log(JSON.stringify({ result: 'PASS', callMedia }))
        } else {
            const threadedRuns = [
                await smokeRuntime('/', 'release-threaded-1', true, 25_000, 'maximum'),
                await smokeRuntime('/', 'release-threaded-2', true, 5_000, 'short'),
                await smokeRuntime('/', 'release-threaded-3', true, 5_000),
            ]
            const threadedNumbers = threadedRuns.map(run => run.profile.phoneNumber)
            assert.equal(new Set(threadedNumbers).size, threadedNumbers.length)
            console.log(JSON.stringify({
                result: 'PASS',
                threadedNumbers,
                basePath: basePath || '/',
                callMedia,
                screenshotDirectory,
            }))
        }
    }
} finally {
    await browser.close()
    await new Promise(resolveClose => server.close(resolveClose))
}
