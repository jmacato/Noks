const targetQueuedFrames = 8_192
const lowWaterFrames = 4_096

class NoksPcmPlayerProcessor extends AudioWorkletProcessor {
    constructor() {
        super()
        this.active = false
        this.queue = []
        this.queueOffset = 0
        this.queuedFrames = 0
        this.requestPending = false
        this.reportedLatencyTraces = new Set()

        this.port.onmessage = event => {
            const data = event.data || {}
            switch (data.type) {
                case 'active':
                    this.active = data.active === true
                    if (!this.active) this.resetQueue()
                    this.requestSamplesIfNeeded()
                    break
                case 'reset':
                    this.resetQueue()
                    this.requestSamplesIfNeeded()
                    break
                case 'enqueue': {
                    if (!this.active) break
                    const samples = data.samples instanceof Uint16Array
                        ? data.samples
                        : new Uint16Array(data.samples || 0)
                    if (samples.length === 0) break
                    const traceId = Number(data.traceId) || 0
                    this.queue.push({ samples, traceId })
                    this.queuedFrames += samples.length
                    this.requestPending = false
                    if (traceId > 0) {
                        this.port.postMessage({
                            type: 'latency-enqueue',
                            traceId,
                            contextTime: currentTime,
                        })
                    }
                    break
                }
            }
        }
    }

    process(_inputs, outputs) {
        const output = outputs[0]
        const firstChannel = output[0]
        if (!firstChannel) return true

        firstChannel.fill(0)
        if (this.active) this.readSamples(firstChannel)

        for (let channelIndex = 1; channelIndex < output.length; channelIndex++) {
            output[channelIndex].set(firstChannel)
        }

        this.requestSamplesIfNeeded()
        return true
    }

    readSamples(destination) {
        let destinationOffset = 0
        while (destinationOffset < destination.length && this.queue.length > 0) {
            const queued = this.queue[0]
            const source = queued.samples
            const available = source.length - this.queueOffset
            const copied = Math.min(available, destination.length - destinationOffset)
            const signedSource = new Int16Array(
                source.buffer,
                source.byteOffset + (this.queueOffset * Uint16Array.BYTES_PER_ELEMENT),
                copied)
            for (let i = 0; i < copied; i++) {
                destination[destinationOffset + i] = signedSource[i] / 32_768
                if (queued.traceId > 0 && signedSource[i] !== 0 &&
                    !this.reportedLatencyTraces.has(queued.traceId)) {
                    this.reportedLatencyTraces.add(queued.traceId)
                    this.port.postMessage({
                        type: 'latency-output',
                        traceId: queued.traceId,
                        contextTime: currentTime + ((destinationOffset + i) / sampleRate),
                    })
                }
            }
            destinationOffset += copied
            this.queueOffset += copied
            this.queuedFrames -= copied

            if (this.queueOffset === source.length) {
                this.queue.shift()
                this.queueOffset = 0
            }
        }
    }

    requestSamplesIfNeeded() {
        if (!this.active || this.requestPending || this.queuedFrames >= lowWaterFrames) return
        this.requestPending = true
        this.port.postMessage({
            type: 'need',
            frames: targetQueuedFrames - this.queuedFrames,
        })
    }

    resetQueue() {
        this.queue.length = 0
        this.queueOffset = 0
        this.queuedFrames = 0
        this.requestPending = false
    }
}

registerProcessor('noks-pcm-player', NoksPcmPlayerProcessor)
