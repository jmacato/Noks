import { createHash } from 'node:crypto'
import { execFile } from 'node:child_process'
import { cp, mkdir, readFile, rm, stat, writeFile } from 'node:fs/promises'
import { dirname, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'
import { promisify } from 'node:util'
import { brotliCompress, constants as zlibConstants, gzip } from 'node:zlib'

const execFileAsync = promisify(execFile)
const brotliCompressAsync = promisify(brotliCompress)
const gzipAsync = promisify(gzip)
const root = resolve(dirname(fileURLToPath(import.meta.url)), '..')
const browserDirectory = resolve(root, 'src/Noks.Avalonia.Browser')
const project = resolve(browserDirectory, 'Noks.Avalonia.Browser.csproj')

function option(name, fallback = null) {
    const index = process.argv.indexOf(name)
    if (index < 0) return fallback
    if (index + 1 >= process.argv.length || process.argv[index + 1].startsWith('--')) {
        throw new Error(`${name} requires a value`)
    }
    return process.argv[index + 1]
}

async function run(command, args, cwd = root) {
    await new Promise((resolveRun, rejectRun) => {
        const child = execFile(command, args, {
            cwd,
            env: {
                ...process.env,
                DOTNET_CLI_TELEMETRY_OPTOUT: '1',
            },
        })
        child.stdout?.pipe(process.stdout)
        child.stderr?.pipe(process.stderr)
        child.once('error', rejectRun)
        child.once('exit', code => {
            if (code === 0) resolveRun()
            else rejectRun(new Error(`${command} exited with code ${code}`))
        })
    })
}

async function sha256(path) {
    return createHash('sha256').update(await readFile(path)).digest('hex')
}

async function publish(stage) {
    const publishDirectory = resolve(stage, 'publish')
    const artifactsDirectory = resolve(stage, 'dotnet')
    await run('dotnet', [
        'publish', project,
        '-c', 'Release',
        '-o', publishDirectory,
        '--artifacts-path', artifactsDirectory,
    ])
    return resolve(publishDirectory, 'wwwroot')
}

const firmwareArgument = option('--firmware')
if (firmwareArgument === null) {
    throw new Error(
        'Usage: node tools/build-browser-release.mjs --firmware <dump.fls> ' +
        '[--output artifacts/noks-browser-release]')
}

const firmware = resolve(root, firmwareArgument)
const output = resolve(root, option('--output', 'artifacts/noks-browser-release'))
const stage = `${output}.stage`
const firmwareInfo = await stat(firmware)
if (!firmwareInfo.isFile() || firmwareInfo.size === 0) {
    throw new Error(`Firmware is not a non-empty file: ${firmware}`)
}

await rm(stage, { recursive: true, force: true })
await rm(output, { recursive: true, force: true })
await mkdir(stage, { recursive: true })

const threadedRoot = await publish(stage)
await cp(threadedRoot, output, { recursive: true })
await mkdir(resolve(output, 'firmware'), { recursive: true })
await cp(firmware, resolve(output, 'firmware/default.fls'))

const { stdout: commitOutput } = await execFileAsync('git', ['rev-parse', 'HEAD'], { cwd: root })
const manifest = {
    version: 1,
    commit: commitOutput.trim(),
    builtAtUtc: new Date().toISOString(),
    configuration: 'Release AOT -O3',
    runtimes: ['threaded'],
    firmware: {
        path: 'firmware/default.fls',
        bytes: firmwareInfo.size,
        sha256: await sha256(firmware),
    },
}
const outputIndexPath = resolve(output, 'index.html')
const outputIndex = await readFile(outputIndexPath, 'utf8')
const buildMetaPlaceholder = '<meta name="noks-build" content="development">'
const buildLabelPlaceholder = '>Build development</a>'
const assetVersionPlaceholder = '?v=development'
if (!outputIndex.includes(buildMetaPlaceholder) ||
    !outputIndex.includes(buildLabelPlaceholder) ||
    !outputIndex.includes(assetVersionPlaceholder)) {
    throw new Error('Browser index is missing the build identity placeholders')
}

const shortCommit = manifest.commit.slice(0, 4)
const stampedIndex = outputIndex
    .replaceAll(assetVersionPlaceholder, `?v=${manifest.commit}`)
    .replace(
        buildMetaPlaceholder,
        `<meta name="noks-build" content="${manifest.commit}; ${manifest.configuration}; ${manifest.runtimes.join(', ')}">`)
    .replace(
        buildLabelPlaceholder,
        `>Build ${shortCommit}</a>`)
await writeFile(outputIndexPath, stampedIndex)
await writeFile(resolve(output, 'BUILD-MANIFEST.json'), `${JSON.stringify(manifest, null, 2)}\n`)
await writeFile(
    resolve(output, 'cache-bust.json'),
    `${JSON.stringify({ version: manifest.commit })}\n`)
await Promise.all([
    writeFile(
        `${outputIndexPath}.br`,
        await brotliCompressAsync(stampedIndex, {
            params: {
                [zlibConstants.BROTLI_PARAM_QUALITY]: 11,
            },
        })),
    writeFile(`${outputIndexPath}.gz`, await gzipAsync(stampedIndex, { level: 9 })),
])
await rm(stage, { recursive: true, force: true })

console.log(`Browser release ready: ${output}`)
