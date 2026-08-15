let active = false

function vibrationSupported() {
    return typeof navigator !== 'undefined' && typeof navigator.vibrate === 'function'
}

export function update(enabled, control) {
    if (!vibrationSupported()) {
        active = false
        return
    }

    if (!enabled) {
        if (active) {
            navigator.vibrate(0)
        }

        active = false
        return
    }

    active = true
    const strength = Math.max(0, Math.min(31, Number(control) & 0x1f))
    const duration = 70 + Math.round((strength / 31) * 130)
    navigator.vibrate(duration)
}

export function dispose() {
    if (vibrationSupported()) {
        navigator.vibrate(0)
    }

    active = false
}
