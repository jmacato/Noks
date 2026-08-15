const databaseName = 'noks-persistence'
const storeName = 'snapshots'
const databaseVersion = 1

let databasePromise

function openDatabase() {
    if (databasePromise) {
        return databasePromise
    }

    databasePromise = new Promise((resolve, reject) => {
        const request = indexedDB.open(databaseName, databaseVersion)

        request.onupgradeneeded = () => {
            request.result.createObjectStore(storeName)
        }

        request.onsuccess = () => resolve(request.result)
        request.onerror = () => reject(request.error ?? new Error('Failed to open IndexedDB'))
        request.onblocked = () => reject(new Error('IndexedDB upgrade was blocked'))
    })

    return databasePromise
}

export async function loadText(key) {
    const database = await openDatabase()

    return await new Promise((resolve, reject) => {
        const transaction = database.transaction(storeName, 'readonly')
        const request = transaction.objectStore(storeName).get(key)
        request.onsuccess = () => resolve(request.result ?? null)
        request.onerror = () => reject(request.error ?? new Error('Failed to load persistence snapshot'))
    })
}

export async function saveText(key, value) {
    const database = await openDatabase()

    await new Promise((resolve, reject) => {
        const transaction = database.transaction(storeName, 'readwrite')
        transaction.objectStore(storeName).put(value, key)
        transaction.oncomplete = () => resolve()
        transaction.onabort = () => reject(transaction.error ?? new Error('Persistence transaction was aborted'))
        transaction.onerror = () => reject(transaction.error ?? new Error('IndexedDB transaction failed'))
    })
}
