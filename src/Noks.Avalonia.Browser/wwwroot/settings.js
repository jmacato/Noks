export function applyPhoneSettings(simImsi, networkName) {
    const url = new URL(globalThis.location.href)

    if (simImsi) {
        url.searchParams.set('sim-imsi', simImsi)
    } else {
        url.searchParams.delete('sim-imsi')
    }

    if (networkName) {
        url.searchParams.set('network-name', networkName)
    } else {
        url.searchParams.delete('network-name')
    }

    url.searchParams.delete('gsm-network-name')
    globalThis.history.replaceState(null, '', url.toString())
}

export function getBrowserCountry() {
    const languages = globalThis.navigator?.languages ?? [globalThis.navigator?.language]

    for (const language of languages) {
        if (!language) continue

        try {
            const region = new Intl.Locale(language).region
            if (region) return region.toUpperCase()
        } catch {
            const match = /[-_]([a-z]{2})\b/i.exec(language)
            if (match) return match[1].toUpperCase()
        }
    }

    return ''
}
