const databaseName = 'noks-profile'
const databaseVersion = 1
const storeName = 'profiles'
const slot = new URL(globalThis.location.href).searchParams.get('slot') || 'primary'
const pendingDataReplacementKey = 'noks.data-replacement-v1'
const mockWakuStoreKey = 'noks-waku-transport.store-v1'
const persistenceDatabaseName = 'noks-persistence'
const persistenceStoreName = 'snapshots'
const maximumBackupBytes = 2 * 1024 * 1024

let databasePromise = null

function openDatabase() {
    if (databasePromise !== null) {
        return databasePromise
    }

    databasePromise = new Promise((resolve, reject) => {
        const request = indexedDB.open(databaseName, databaseVersion)
        request.onupgradeneeded = () => {
            const database = request.result
            if (!database.objectStoreNames.contains(storeName)) {
                database.createObjectStore(storeName)
            }
        }
        request.onsuccess = () => resolve(request.result)
        request.onerror = () => reject(request.error || new Error('Profile database failed to open'))
        request.onblocked = () => reject(new Error('Profile database upgrade is blocked'))
    })

    return databasePromise
}

export async function loadProfile() {
    const database = await openDatabase()
    return new Promise((resolve, reject) => {
        const transaction = database.transaction(storeName, 'readonly')
        const request = transaction.objectStore(storeName).get(slot)
        request.onsuccess = () => resolve(typeof request.result === 'string' ? request.result : null)
        request.onerror = () => reject(request.error || new Error('Profile failed to load'))
    })
}

export async function saveProfile(value) {
    if (typeof value !== 'string') {
        throw new TypeError('Profile storage accepts text only')
    }
    const database = await openDatabase()
    await new Promise((resolve, reject) => {
        const transaction = database.transaction(storeName, 'readwrite')
        transaction.objectStore(storeName).put(value, slot)
        transaction.oncomplete = () => resolve()
        transaction.onabort = () => reject(transaction.error || new Error('Profile save was aborted'))
        transaction.onerror = () => reject(transaction.error || new Error('Profile failed to save'))
    })
}

function clearPersistenceSnapshots() {
    return new Promise((resolve, reject) => {
        const request = indexedDB.open(persistenceDatabaseName, 1)
        request.onupgradeneeded = () => {
            const database = request.result
            if (!database.objectStoreNames.contains(persistenceStoreName)) {
                database.createObjectStore(persistenceStoreName)
            }
        }
        request.onerror = () => reject(request.error || new Error('Phone persistence database failed to open'))
        request.onblocked = () => reject(new Error('Phone persistence database is blocked'))
        request.onsuccess = () => {
            const database = request.result
            const transaction = database.transaction(persistenceStoreName, 'readwrite')
            transaction.objectStore(persistenceStoreName).clear()
            transaction.oncomplete = () => {
                database.close()
                resolve()
            }
            transaction.onabort = () => {
                const error = transaction.error || new Error('Phone persistence reset was aborted')
                database.close()
                reject(error)
            }
            transaction.onerror = () => {}
        }
    })
}

async function replaceStoredProfile(profileJson, clearAllProfiles) {
    const database = await openDatabase()
    await new Promise((resolve, reject) => {
        const transaction = database.transaction(storeName, 'readwrite')
        const profiles = transaction.objectStore(storeName)
        if (clearAllProfiles) {
            profiles.clear()
        }
        profiles.put(profileJson, slot)
        transaction.oncomplete = () => resolve()
        transaction.onabort = () => reject(transaction.error || new Error('Profile replacement was aborted'))
        transaction.onerror = () => {}
    })
}

export async function applyPendingDataReplacement() {
    const encoded = localStorage.getItem(pendingDataReplacementKey)
    if (encoded === null) {
        return
    }

    let operation
    try {
        operation = JSON.parse(encoded)
        if (operation?.version !== 1 || typeof operation.profileJson !== 'string' ||
            typeof operation.clearAllProfiles !== 'boolean' ||
            JSON.parse(operation.profileJson)?.version === undefined) {
            throw new TypeError('Invalid pending phone data replacement')
        }
    } catch (error) {
        localStorage.removeItem(pendingDataReplacementKey)
        console.error('Discarding invalid pending phone data replacement', error)
        return
    }

    // This journal is deliberately replayable. If the browser closes between
    // databases, the next load repeats both idempotent writes before booting.
    await clearPersistenceSnapshots()
    await replaceStoredProfile(operation.profileJson, operation.clearAllProfiles)
    localStorage.removeItem(mockWakuStoreKey)
    localStorage.removeItem(pendingDataReplacementKey)
}

export function downloadJson(fileName, value) {
    const text = String(value)
    const blob = new Blob([text.endsWith('\n') ? text : `${text}\n`], { type: 'application/json' })
    const url = URL.createObjectURL(blob)
    const anchor = document.createElement('a')
    anchor.href = url
    anchor.download = String(fileName || 'noks-waku-data.json')
    anchor.style.display = 'none'
    document.body.append(anchor)
    anchor.click()
    anchor.remove()
    setTimeout(() => URL.revokeObjectURL(url), 1_000)
}

export async function pickJsonFile() {
    return await new Promise((resolve, reject) => {
        const input = document.createElement('input')
        input.type = 'file'
        input.accept = 'application/json,.json'
        input.style.display = 'none'
        const finish = value => {
            input.remove()
            resolve(value)
        }
        input.addEventListener('cancel', () => finish(null), { once: true })
        input.addEventListener('change', async () => {
            const file = input.files?.[0]
            if (!file) {
                finish(null)
                return
            }
            if (file.size > maximumBackupBytes) {
                input.remove()
                reject(new RangeError('Waku backup exceeds the 2 MiB limit'))
                return
            }
            try {
                finish(await file.text())
            } catch (error) {
                input.remove()
                reject(error)
            }
        }, { once: true })
        document.body.append(input)
        input.click()
    })
}

export function confirmDataImport() {
    return globalThis.confirm(
        'CAUTION: Select Cancel to keep the current data. Restore this Waku JSON backup? ' +
        'The restore replaces the current identity, contacts, messages, pairings, SIM, EEPROM, and flash state. ' +
        'Then it restarts the phone.')
}

export function confirmFullReset() {
    return globalThis.confirm(
        'CAUTION: Select Cancel to keep all Noks data. Reset all Noks data? ' +
        'The reset permanently replaces the Waku identity. ' +
        'It deletes every saved profile, contact, message, pairing, SIM, EEPROM, and flash snapshot from this browser. ' +
        'Published encrypted Waku envelopes remain on Waku. The new identity cannot read them.')
}

export function stageDataReplacementAndReload(profileJson, clearAllProfiles) {
    if (typeof profileJson !== 'string') {
        throw new TypeError('Profile replacement accepts JSON text only')
    }
    JSON.parse(profileJson)
    localStorage.setItem(pendingDataReplacementKey, JSON.stringify({
        version: 1,
        profileJson,
        clearAllProfiles: Boolean(clearAllProfiles),
    }))
    globalThis.location.reload()
}

export async function copyText(value) {
    await navigator.clipboard.writeText(String(value))
}
