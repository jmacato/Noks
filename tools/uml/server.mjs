#!/usr/bin/env node
import { createServer } from 'node:http';
import { spawn } from 'node:child_process';
import { readFile, stat } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { dirname, join, extname, resolve } from 'node:path';

const here = dirname(fileURLToPath(import.meta.url));
const repoRoot = resolve(here, '..', '..');
const viewerDir = join(here, 'viewer');
const modelPath = join(repoRoot, 'artifacts', 'uml', 'model.json');
const port = Number(process.env.NOKS_UML_PORT ?? process.argv[2] ?? 5252);

const mime = {
  '.html': 'text/html; charset=utf-8',
  '.js': 'text/javascript; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.json': 'application/json; charset=utf-8',
  '.svg': 'image/svg+xml; charset=utf-8',
};

function run(cmd, cmdArgs, input) {
  return new Promise((done) => {
    const child = spawn(cmd, cmdArgs, { cwd: repoRoot });
    let out = '';
    let err = '';
    child.stdout.on('data', (d) => (out += d));
    child.stderr.on('data', (d) => (err += d));
    child.on('error', (e) => done({ code: -1, out, err: e.message }));
    child.on('close', (code) => done({ code, out, err }));
    if (input !== undefined) {
      child.stdin.end(input);
    }
  });
}

async function readBody(req) {
  const chunks = [];
  for await (const chunk of req) chunks.push(chunk);
  return Buffer.concat(chunks).toString('utf8');
}

function send(res, status, body, type = 'text/plain; charset=utf-8') {
  res.writeHead(status, { 'content-type': type, 'cache-control': 'no-store' });
  res.end(body);
}

async function serveStatic(res, path) {
  try {
    const body = await readFile(path);
    send(res, 200, body, mime[extname(path)] ?? 'application/octet-stream');
  } catch {
    send(res, 404, 'not found');
  }
}

async function regenerate() {
  return run('dotnet', ['run', 'tools/uml/NoksUml.cs']);
}

const server = createServer(async (req, res) => {
  const url = new URL(req.url, `http://${req.headers.host}`);
  const path = url.pathname;

  if (path === '/api/model') {
    try {
      await stat(modelPath);
    } catch {
      const result = await regenerate();
      if (result.code !== 0) return send(res, 500, `model generation failed:\n${result.err || result.out}`);
    }
    return serveStatic(res, modelPath);
  }

  if (path === '/api/regenerate' && req.method === 'POST') {
    const result = await regenerate();
    return send(res, result.code === 0 ? 200 : 500, result.out + result.err);
  }

  if (path === '/api/render' && req.method === 'POST') {
    const dot = await readBody(req);
    const result = await run('dot', ['-Tsvg'], dot);
    if (result.code !== 0) {
      return send(res, 500, `graphviz failed (is 'dot' installed?):\n${result.err}`);
    }
    return send(res, 200, result.out, mime['.svg']);
  }

  if (path === '/api/dot' && req.method === 'POST') {
    return send(res, 200, await readBody(req), 'text/vnd.graphviz; charset=utf-8');
  }

  if (path === '/' || path === '') return serveStatic(res, join(viewerDir, 'index.html'));

  const candidate = resolve(viewerDir, '.' + path);
  if (!candidate.startsWith(viewerDir)) return send(res, 403, 'forbidden');
  return serveStatic(res, candidate);
});

server.listen(port, () => {
  console.log(`Noks UML browser: http://localhost:${port}`);
  console.log(`model: ${modelPath}`);
});
