import http from 'node:http'
import { appendFile, readFile, stat } from 'node:fs/promises'
import { createReadStream, existsSync, statSync } from 'node:fs'
import { dirname, extname, join, normalize, resolve, sep } from 'node:path'
import { fileURLToPath } from 'node:url'

const toolDirectory = dirname(fileURLToPath(import.meta.url))
const root = resolve(option('--root', 'artifacts/noks-browser-svgdraw'))
const host = option('--host', '0.0.0.0')
const port = Number(option('--port', '5080'))
const resultPath = resolve(option('--results', '/private/tmp/noks-ios-benchmark.ndjson'))
const navigationBenchmark = await readFile(
    resolve(toolDirectory, 'ios-safari-navigation-benchmark.js'),
    'utf8')

const contentTypes = {
    '.css': 'text/css; charset=utf-8',
    '.dat': 'application/octet-stream',
    '.dll': 'application/octet-stream',
    '.fls': 'application/octet-stream',
    '.html': 'text/html; charset=utf-8',
    '.ico': 'image/x-icon',
    '.js': 'text/javascript; charset=utf-8',
    '.json': 'application/json; charset=utf-8',
    '.mjs': 'text/javascript; charset=utf-8',
    '.png': 'image/png',
    '.svg': 'image/svg+xml',
    '.wasm': 'application/wasm',
    '.wav': 'audio/wav',
    '.woff': 'font/woff',
    '.woff2': 'font/woff2',
}

const server = http.createServer(async (request, response) => {
    try {
        await handleRequest(request, response)
    } catch (error) {
        console.error(error)
        if (!response.headersSent) response.writeHead(500)
        response.end('Internal server error')
    }
})

server.listen(port, host, () => {
    console.log(`Serving ${root} on http://${host}:${port}`)
    console.log(`Writing benchmark results to ${resultPath}`)
})

async function handleRequest(request, response) {
    const url = new URL(request.url, 'http://localhost')
    if (request.method === 'POST' && url.pathname === '/telemetry') {
        request.resume()
        response.writeHead(204)
        response.end()
        return
    }

    if (url.pathname === '/__benchmark/results') {
        if (request.method === 'POST') {
            const body = await readBody(request, 1_000_000)
            const parsed = JSON.parse(body)
            await appendFile(resultPath, `${JSON.stringify({ receivedAt: new Date().toISOString(), ...parsed })}\n`)
            console.log(`Benchmark result: ${parsed.runId} ${parsed.source} passed=${parsed.passed}`)
            response.writeHead(204)
            response.end()
            return
        }

        if (request.method === 'GET') {
            const body = existsSync(resultPath) ? await readFile(resultPath) : Buffer.from('')
            sendBuffer(response, body, 'application/x-ndjson; charset=utf-8')
            return
        }
    }

    if (request.method === 'GET' && url.pathname === '/__benchmark/ios-safari.js') {
        sendBuffer(response, Buffer.from(navigationBenchmark), 'text/javascript; charset=utf-8')
        return
    }

    await serveStatic(request, response, url)
}

async function serveStatic(request, response, url) {
    let pathname = decodeURIComponent(url.pathname)
    if (pathname === '/') pathname = '/index.html'
    const relative = normalize(pathname).replace(/^[/\\]+/, '')
    let file = join(root, relative)
    if (file !== root && !file.startsWith(`${root}${sep}`)) {
        response.writeHead(403)
        response.end('Forbidden')
        return
    }

    try {
        if (statSync(file).isDirectory()) file = join(file, 'index.html')
    } catch {
        file = join(root, 'index.html')
    }

    const extension = extname(file)
    if (extension === '.html' && url.searchParams.get('ios-benchmark') === '1') {
        const html = (await readFile(file, 'utf8')).replace(
            '</body>',
            '    <script type="module" src="/__benchmark/ios-safari.js"></script>\n</body>')
        sendBuffer(response, Buffer.from(html), contentTypes[extension])
        return
    }

    let source = file
    let contentEncoding
    const accepted = request.headers['accept-encoding'] ?? ''
    const allowCompressed = extension !== '.html' && extension !== '.json'
    if (allowCompressed && accepted.includes('br') && existsSync(`${file}.br`)) {
        source = `${file}.br`
        contentEncoding = 'br'
    } else if (allowCompressed && accepted.includes('gzip') && existsSync(`${file}.gz`)) {
        source = `${file}.gz`
        contentEncoding = 'gzip'
    }

    const sourceStat = await stat(source)
    const headers = commonHeaders(contentTypes[extension] ?? 'application/octet-stream')
    headers['Content-Length'] = sourceStat.size
    if (contentEncoding) headers['Content-Encoding'] = contentEncoding
    response.writeHead(200, headers)
    if (request.method === 'HEAD') {
        response.end()
        return
    }

    createReadStream(source).on('error', () => response.destroy()).pipe(response)
}

function sendBuffer(response, body, contentType) {
    response.writeHead(200, {
        ...commonHeaders(contentType),
        'Content-Length': body.length,
    })
    response.end(body)
}

function commonHeaders(contentType) {
    return {
        'Cache-Control': 'no-store',
        'Content-Type': contentType,
        'Cross-Origin-Embedder-Policy': 'require-corp',
        'Cross-Origin-Opener-Policy': 'same-origin',
        'Cross-Origin-Resource-Policy': 'same-origin',
        Vary: 'Accept-Encoding',
    }
}

async function readBody(request, limit) {
    const chunks = []
    let length = 0
    for await (const chunk of request) {
        length += chunk.length
        if (length > limit) throw new Error('Request body is too large')
        chunks.push(chunk)
    }
    return Buffer.concat(chunks).toString('utf8')
}

function option(name, fallback) {
    const index = process.argv.indexOf(name)
    return index >= 0 ? process.argv[index + 1] : fallback
}
