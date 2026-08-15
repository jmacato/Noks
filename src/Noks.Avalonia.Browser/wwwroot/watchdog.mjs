let telemetryPath = '/telemetry'
let sessionId = ''
let cacheBustVersion = ''
let href = ''
let userAgent = ''
let visibility = 'unknown'
let lastHeartbeatAt = Date.now()
let lastReportedStallAt = 0
let initialized = false
let telemetryUnavailable = false

function sendTelemetry(type, text) {
    if (telemetryUnavailable) {
        return
    }

    const body = JSON.stringify([{
        type,
        text,
        sessionId,
        cacheBustVersion,
        visibility,
        href,
        userAgent,
        timestamp: new Date().toISOString(),
    }])

    fetch(telemetryPath, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body,
        keepalive: true,
    }).then((response) => {
        if (response.status === 404 || response.status === 405) {
            telemetryUnavailable = true
        }
    }).catch(() => {})
}

globalThis.addEventListener('message', (event) => {
    const message = event.data ?? {}
    if (message.kind === 'init') {
        telemetryPath = message.telemetryPath || telemetryPath
        sessionId = message.sessionId || sessionId
        cacheBustVersion = message.cacheBustVersion || cacheBustVersion
        href = message.href || href
        userAgent = message.userAgent || userAgent
        visibility = message.visibility || visibility
        lastHeartbeatAt = Date.now()
        initialized = true
        sendTelemetry('log', 'Noks browser watchdog: started')
        return
    }

    if (message.visibility) {
        visibility = message.visibility
    }

    if (message.kind === 'heartbeat') {
        lastHeartbeatAt = Date.now()
        return
    }

    if (message.kind === 'pagehide') {
        sendTelemetry('log', `Noks browser watchdog: pagehide visibility=${visibility}`)
    }
})

setInterval(() => {
    if (!initialized) {
        return
    }

    const now = Date.now()
    const lastHeartbeatMs = now - lastHeartbeatAt
    if (lastHeartbeatMs < 5_000 || now - lastReportedStallAt < 10_000) {
        return
    }

    lastReportedStallAt = now
    sendTelemetry(
        'warn',
        `Noks browser watchdog: main-thread-stall lastHeartbeatMs=${lastHeartbeatMs} visibility=${visibility}`)
}, 2_000)
