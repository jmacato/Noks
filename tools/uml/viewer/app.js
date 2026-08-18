const el = (id) => document.getElementById(id);
const state = {
  model: null,
  byId: new Map(),
  byProject: new Map(),
  byNamespace: new Map(),
  selected: null,
  scope: 'neighbors',
  expanded: new Set(),
  graph: { seed: null, nodes: new Set(), origin: new Map(), members: new Set(), opened: new Set() },
  view: { x: 20, y: 20, k: 1 },
};

const MAX_NODES = 80;

const KIND_GLYPH = { class: 'C', interface: 'I', enum: 'E', record: 'R', struct: 'S', delegate: 'D' };
const ACCESS_GLYPH = { public: '+', private: '-', protected: '#', internal: '~', 'protected internal': '~', 'private protected': '~' };
const MEMBER_GLYPH = { method: 'm', property: 'p', field: 'f', event: 'e', constructor: 'c', enumValue: 'v' };
const MEMBER_GROUP = { constructor: 'Constructors', property: 'Properties', method: 'Methods', event: 'Events', field: 'Fields', enumValue: 'Values' };
const FILL = { interface: '#f3ecfb', enum: '#fdf3e6', record: '#e9f6f2', struct: '#eef6ec', delegate: '#fbeef1', class: '#eef2fb' };

const esc = (s) => String(s).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
const q = (s) => '"' + String(s).replace(/"/g, '\\"') + '"';
const budget = () => Number(el('budget').value);
const isPublic = (m) => m.accessibility === 'public' || m.accessibility === 'protected';

async function boot() {
  status('loading model');
  const res = await fetch('/api/model');
  if (!res.ok) return status(await res.text());
  state.model = await res.json();
  index();
  bind();
  renderTree();
  const seed = pickSeed();
  if (seed) { state.selected = seed; reveal(seed); renderTree(); renderDetails(); seedGraph(); }
  await draw();
}

function pickSeed() {
  const ranked = state.model.types
    .filter((t) => !state.byProject.get(t.project)?.isTest)
    .sort((a, b) => degree(b.id) - degree(a.id));
  return ranked.length ? ranked[0].id : null;
}

function index() {
  const m = state.model;
  state.byId = new Map(m.types.map((t) => [t.id, t]));
  state.byProject = new Map(m.projects.map((p) => [p.id, p]));
  state.byNamespace = new Map(m.namespaces.map((n) => [n.id, n]));
  const push = (map, key, value) => { if (!map.has(key)) map.set(key, []); map.get(key).push(value); };
  state.derived = new Map();
  state.bases = new Map();
  state.usedBy = new Map();
  state.outgoing = new Map();
  state.weight = new Map();
  for (const e of [...m.edges.inherits, ...m.edges.implements]) { push(state.derived, e.to, e.from); push(state.bases, e.from, e.to); }
  for (const e of m.edges.uses) {
    push(state.usedBy, e.to, e.from);
    push(state.outgoing, e.from, e.to);
    state.weight.set(`${e.from}|${e.to}`, e.weight);
  }
}

const neighboursOf = (id) => [
  ...(state.outgoing.get(id) ?? []), ...(state.usedBy.get(id) ?? []),
  ...(state.bases.get(id) ?? []), ...(state.derived.get(id) ?? []),
];
const degree = (id) => new Set(neighboursOf(id)).size;
const edgeWeight = (a, b) => Math.max(state.weight.get(`${a}|${b}`) ?? 0, state.weight.get(`${b}|${a}`) ?? 0);

function status(text, warn = false) {
  el('status').textContent = text;
  el('status').classList.toggle('warn', warn);
}

function visibleProjects() {
  const tests = el('showTests').checked;
  return state.model.projects.filter((p) => (tests || !p.isTest) && p.typeCount > 0);
}

function matches(t, filter) {
  if (!filter) return true;
  return t.name.toLowerCase().includes(filter) || t.members.some((m) => m.name.toLowerCase().includes(filter));
}

function renderTree() {
  const filter = el('search').value.trim().toLowerCase();
  const open = (key, hasMatch) => state.expanded.has(key) || (filter && hasMatch);
  const host = el('tree');
  host.innerHTML = '';
  const groups = new Map();
  for (const p of visibleProjects()) {
    if (!groups.has(p.group)) groups.set(p.group, []);
    groups.get(p.group).push(p);
  }
  for (const [group, projects] of [...groups].sort()) {
    const groupHits = state.model.types.some((t) => projects.some((p) => p.id === t.project) && matches(t, filter));
    host.appendChild(row({ depth: 0, key: `g:${group}`, glyph: '#', kind: 'namespace', label: group, meta: `${projects.length}`, children: true }));
    if (!open(`g:${group}`, groupHits)) continue;
    for (const p of projects.sort((a, b) => a.name.localeCompare(b.name))) {
      const projectHits = state.model.types.some((t) => t.project === p.id && matches(t, filter));
      host.appendChild(row({ depth: 1, key: `p:${p.id}`, glyph: 'P', kind: p.isTest ? 'test' : 'project', label: p.name, meta: `${p.typeCount} types, ${p.loc} loc`, children: true, onSelect: () => selectProject(p) }));
      if (!open(`p:${p.id}`, projectHits)) continue;
      for (const ns of state.model.namespaces.filter((n) => n.project === p.id)) {
        const types = state.model.types
          .filter((t) => t.project === p.id && t.namespace === ns.name && matches(t, filter))
          .sort((a, b) => a.name.localeCompare(b.name));
        host.appendChild(row({ depth: 2, key: `n:${ns.id}`, glyph: '{}', kind: 'namespace', label: ns.name, meta: `${ns.typeCount}`, children: true, onSelect: () => selectNamespace(ns) }));
        if (!open(`n:${ns.id}`, types.length > 0)) continue;
        for (const t of types) {
          host.appendChild(row({ depth: 3, key: `t:${t.id}`, glyph: KIND_GLYPH[t.kind] ?? 'C', kind: t.kind, label: t.name, meta: `${t.memberCount}`, children: true, selected: state.selected === t.id, onSelect: () => select(t.id) }));
          const shown = t.members.filter((m) => el('allMembers').checked || isPublic(m));
          const hits = filter ? shown.filter((m) => m.name.toLowerCase().includes(filter)) : shown;
          if (!open(`t:${t.id}`, hits.length > 0 && !t.name.toLowerCase().includes(filter))) continue;
          for (const mm of (state.expanded.has(`t:${t.id}`) ? shown : hits)) {
            host.appendChild(row({ depth: 4, key: `m:${t.id}:${mm.name}`, glyph: (ACCESS_GLYPH[mm.accessibility] ?? '~') + (MEMBER_GLYPH[mm.kind] ?? ''), kind: t.kind, label: mm.signature, onSelect: () => select(t.id) }));
          }
        }
      }
    }
  }
  if (!host.children.length) host.innerHTML = '<div class="empty">no matches</div>';
}

function row({ depth, key, glyph, kind, label, meta, children, onSelect, selected }) {
  const div = document.createElement('div');
  div.className = 'node-row' + (selected ? ' sel' : '');
  div.style.paddingLeft = `${6 + depth * 13}px`;
  const tw = document.createElement('span');
  tw.className = 'tw';
  tw.textContent = children ? (state.expanded.has(key) ? '▾' : '▸') : '';
  tw.onclick = (e) => { e.stopPropagation(); toggle(key); };
  const g = document.createElement('span');
  g.className = `glyph k-${kind === 'test' ? 'project' : kind}` + (kind === 'test' ? ' is-test' : '');
  g.textContent = glyph;
  const l = document.createElement('span');
  l.className = 'lbl';
  l.textContent = label;
  l.onclick = () => {
    if (children) { if (state.expanded.has(key)) state.expanded.delete(key); else state.expanded.add(key); }
    if (onSelect) onSelect(); else renderTree();
  };
  div.append(tw, g, l);
  if (meta) {
    const m = document.createElement('span');
    m.className = 'meta';
    m.textContent = meta;
    div.appendChild(m);
  }
  return div;
}

function toggle(key) {
  if (state.expanded.has(key)) state.expanded.delete(key); else state.expanded.add(key);
  renderTree();
}

function reveal(id) {
  const t = state.byId.get(id);
  if (!t) return;
  const project = state.byProject.get(t.project);
  if (project) state.expanded.add(`g:${project.group}`);
  state.expanded.add(`p:${t.project}`);
  state.expanded.add(`n:${t.project}:${t.namespace}`);
  const filter = el('search').value.trim().toLowerCase();
  if (filter && !t.name.toLowerCase().includes(filter)) el('search').value = '';
}

function select(id, { reseed = true } = {}) {
  state.selected = id;
  reveal(id);
  renderTree();
  renderDetails();
  if (state.scope === 'projects' || state.scope === 'nsgraph') { state.scope = 'neighbors'; el('scope').value = 'neighbors'; }
  if (reseed || !state.graph.nodes.has(id)) seedGraph();
  else state.keepView = true;
  draw();
}

function selectProject(p) {
  state.scopeProject = p.id;
  renderTree();
  renderProjectDetails(p);
  if (state.scope !== 'projects') { state.scope = 'projects'; el('scope').value = 'projects'; }
  draw();
}

function selectNamespace(ns) {
  state.scopeNamespace = ns.id;
  renderTree();
  renderNamespaceDetails(ns);
  state.scope = 'nsmap';
  el('scope').value = 'nsmap';
  seedGraph();
  draw();
}

const link = (id, text) => `<a data-id="${esc(id)}">${esc(text ?? state.byId.get(id)?.name ?? id)}</a>`;

function renderDetails() {
  const t = state.byId.get(state.selected);
  if (!t) return;
  const bases = state.bases.get(t.id) ?? [];
  const derived = state.derived.get(t.id) ?? [];
  const out = [...new Set(state.outgoing.get(t.id) ?? [])].sort((a, b) => edgeWeight(t.id, b) - edgeWeight(t.id, a));
  const inn = [...new Set(state.usedBy.get(t.id) ?? [])].sort((a, b) => edgeWeight(b, t.id) - edgeWeight(a, t.id));
  el('details').innerHTML = `
    <h2><span class="glyph k-${t.kind}">${KIND_GLYPH[t.kind] ?? 'C'}</span> ${esc(t.name)}</h2>
    <div class="sig">${esc([t.accessibility, ...t.modifiers, t.kind, t.name + (t.typeParameters.length ? '<' + t.typeParameters.join(', ') + '>' : '')].join(' '))}</div>
    ${t.doc ? `<div class="doc">${esc(t.doc)}</div>` : ''}
    <div class="kv"><span>${esc(t.namespace)}</span></div>
    <div class="kv"><b>${t.loc}</b> loc <b>${t.memberCount}</b> members <b>${degree(t.id)}</b> links</div>
    <div class="kv"><a href="vscode://file${esc(state.model.root)}/${esc(t.file)}:${t.line}">${esc(t.file)}:${t.line}</a></div>
    ${bases.length ? `<h3>Base types</h3><ul class="plain">${bases.map((b) => `<li>${link(b)}</li>`).join('')}</ul>` : ''}
    ${derived.length ? `<h3>Derived (${derived.length})</h3><ul class="plain">${derived.map((b) => `<li>${link(b)}</li>`).join('')}</ul>` : ''}
    ${out.length ? `<h3>Uses (${out.length})</h3><ul class="plain">${out.map((b) => `<li>${link(b)} <span class="meta">${edgeWeight(t.id, b)}</span></li>`).join('')}</ul>` : ''}
    ${inn.length ? `<h3>Used by (${inn.length})</h3><ul class="plain">${inn.map((b) => `<li>${link(b)} <span class="meta">${edgeWeight(b, t.id)}</span></li>`).join('')}</ul>` : ''}
    ${Object.keys(MEMBER_GROUP).map((g) => {
      const items = t.members.filter((m) => m.kind === g).filter((m) => el('allMembers').checked || isPublic(m));
      if (!items.length) return '';
      return `<h3>${MEMBER_GROUP[g]} (${items.length})</h3><ul class="plain">${items.map((m) => `<li title="${esc(m.signature)}"><span class="glyph">${ACCESS_GLYPH[m.accessibility] ?? '~'}</span>${m.isStatic ? '<span class="tag">static</span>' : ''}${esc(m.signature)}</li>`).join('')}</ul>`;
    }).join('')}`;
}

function renderProjectDetails(p) {
  const dependents = state.model.projects.filter((x) => x.projectRefs.includes(p.id));
  el('details').innerHTML = `
    <h2><span class="glyph k-project">P</span> ${esc(p.name)}</h2>
    <div class="sig">${esc(p.path)}</div>
    <div class="kv"><b>${p.typeCount}</b> types <b>${p.loc}</b> loc</div>
    <div class="kv">${esc(p.targetFrameworks)}${p.isTest ? ' <span class="tag">test</span>' : ''}</div>
    <h3>References</h3><ul class="plain">${(p.projectRefs.length ? p.projectRefs : ['(none)']).map((r) => `<li>${esc(r)}</li>`).join('')}</ul>
    <h3>Referenced by</h3><ul class="plain">${(dependents.length ? dependents.map((d) => d.name) : ['(none)']).map((r) => `<li>${esc(r)}</li>`).join('')}</ul>
    <h3>Packages</h3><ul class="plain">${(p.packageRefs.length ? p.packageRefs : ['(none)']).map((r) => `<li>${esc(r)}</li>`).join('')}</ul>`;
}

function renderNamespaceDetails(ns) {
  const outward = state.model.edges.namespaces.filter((e) => e.from === ns.id);
  const inward = state.model.edges.namespaces.filter((e) => e.to === ns.id);
  const hubs = state.model.types.filter((t) => `${t.project}:${t.namespace}` === ns.id).sort((a, b) => degree(b.id) - degree(a.id)).slice(0, 10);
  el('details').innerHTML = `
    <h2><span class="glyph k-namespace">{}</span> ${esc(ns.name)}</h2>
    <div class="kv"><b>${ns.typeCount}</b> types <b>${ns.loc}</b> loc in ${esc(ns.project)}</div>
    <h3>Most connected types</h3><ul class="plain">${hubs.map((t) => `<li>${link(t.id)} <span class="meta">${degree(t.id)}</span></li>`).join('')}</ul>
    <h3>Depends on</h3><ul class="plain">${outward.map((e) => `<li>${esc(e.to.split(':')[1])} <span class="meta">${e.weight}</span></li>`).join('') || '<li>(none)</li>'}</ul>
    <h3>Depended on by</h3><ul class="plain">${inward.map((e) => `<li>${esc(e.from.split(':')[1])} <span class="meta">${e.weight}</span></li>`).join('') || '<li>(none)</li>'}</ul>`;
}

function typeLabel(t, focus) {
  const g = state.graph;
  const showMembers = g.members.has(t.id) || el('showMembers').checked;
  const opened = g.opened.has(t.id);
  const fill = focus ? '#ffe9e4' : (FILL[t.kind] ?? '#eef2fb');
  const border = focus ? '#d1493f' : '#9aa5b5';
  const act = (kind) => `noks:${kind}/${encodeURIComponent(t.id)}`;
  const hiddenLinks = [...new Set(neighboursOf(t.id))].filter((n) => !g.nodes.has(n)).length;
  const head = `<TR>`
    + `<TD HREF="${act('members')}" TITLE="${showMembers ? 'hide members' : 'show members'}" BGCOLOR="${fill}"><FONT POINT-SIZE="11">${showMembers ? '&#9662;' : '&#9656;'}</FONT></TD>`
    + `<TD BGCOLOR="${fill}" ALIGN="LEFT"><B>${esc(t.name)}</B><BR ALIGN="LEFT"/><FONT POINT-SIZE="9">${esc(t.kind)} · ${t.loc} loc · ${t.memberCount} members</FONT></TD>`
    + `<TD HREF="${act(opened ? 'collapse' : 'expand')}" TITLE="${opened ? 'collapse neighbours' : `expand ${hiddenLinks} linked types`}" BGCOLOR="${fill}">`
    + `<FONT POINT-SIZE="11" COLOR="${hiddenLinks || opened ? '#3f6fd1' : '#b7bfca'}">${opened ? '&#8722;' : '&#43;'}</FONT>`
    + (hiddenLinks && !opened ? `<BR/><FONT POINT-SIZE="8" COLOR="#7a8699">${hiddenLinks}</FONT>` : '')
    + `</TD></TR>`;
  let rows = head;
  if (showMembers) {
    const visible = t.members.filter(isPublic);
    for (const m of visible.slice(0, 12)) rows += `<TR><TD COLSPAN="3" ALIGN="LEFT"><FONT POINT-SIZE="9">${esc((ACCESS_GLYPH[m.accessibility] ?? '~') + ' ' + m.signature.slice(0, 54))}</FONT></TD></TR>`;
    if (visible.length > 12) rows += `<TR><TD COLSPAN="3" ALIGN="LEFT"><FONT POINT-SIZE="9" COLOR="#7a8699">+${visible.length - 12} more</FONT></TD></TR>`;
  }
  return `<TABLE BORDER="0" CELLBORDER="1" CELLSPACING="0" CELLPADDING="4" BGCOLOR="#ffffff" COLOR="${border}">${rows}</TABLE>`;
}

function classDot(ids, focus, title) {
  const set = new Set(ids);
  const lines = [
    `digraph ${q(title)} {`,
    '  rankdir=LR; bgcolor="transparent"; splines=polyline; concentrate=true; nodesep=0.2; ranksep=0.45; pack=true; packmode="array_c3";',
    '  node [shape=plaintext fontname="Helvetica" fontsize=12];',
    '  edge [fontname="Helvetica" fontsize=9 color="#8a94a6"];',
  ];
  for (const id of set) {
    const t = state.byId.get(id);
    if (t) lines.push(`  ${q(id)} [label=<${typeLabel(t, id === focus)}>];`);
  }
  const m = state.model;
  for (const e of m.edges.inherits) if (set.has(e.from) && set.has(e.to)) lines.push(`  ${q(e.from)} -> ${q(e.to)} [arrowhead=onormal color="#3f6fd1" penwidth=1.4];`);
  for (const e of m.edges.implements) if (set.has(e.from) && set.has(e.to)) lines.push(`  ${q(e.from)} -> ${q(e.to)} [arrowhead=onormal style=dashed color="#8a5fc0" penwidth=1.2];`);
  for (const e of m.edges.uses) {
    if (!set.has(e.from) || !set.has(e.to)) continue;
    lines.push(`  ${q(e.from)} -> ${q(e.to)} [arrowhead=vee style=dotted color="#98a2b3" penwidth=${Math.min(3, 0.7 + e.weight / 4).toFixed(1)}];`);
  }
  lines.push('}');
  return lines.join('\n');
}

function rank(ids, focus) {
  const cap = budget();
  const scored = [...ids].filter((id) => id !== focus).sort((a, b) => {
    const wa = focus ? edgeWeight(focus, a) : 0;
    const wb = focus ? edgeWeight(focus, b) : 0;
    return wb - wa || degree(b) - degree(a);
  });
  const keep = (focus ? [focus] : []).concat(scored.slice(0, Math.max(0, cap - (focus ? 1 : 0))));
  return { keep, total: ids.size ?? ids.length, dropped: Math.max(0, (ids.size ?? ids.length) - keep.length) };
}

function neighbourIds(rootId, depth) {
  const set = new Set([rootId]);
  let frontier = [rootId];
  for (let d = 0; d < depth; d++) {
    const next = [];
    for (const id of frontier) for (const other of neighboursOf(id)) if (!set.has(other)) { set.add(other); next.push(other); }
    frontier = next;
  }
  return set;
}

function inheritanceIds(rootId) {
  const set = new Set([rootId]);
  const walk = (id, map) => { for (const other of map.get(id) ?? []) if (!set.has(other)) { set.add(other); walk(other, map); } };
  walk(rootId, state.bases);
  walk(rootId, state.derived);
  return set;
}

function projectDot() {
  const projects = visibleProjects();
  const names = new Set(projects.map((p) => p.id));
  const lines = [
    'digraph projects {',
    '  rankdir=BT; bgcolor="transparent"; splines=spline; nodesep=0.35; ranksep=0.7;',
    '  node [shape=box style="filled,rounded" fontname="Helvetica" fontsize=11 penwidth=1.3];',
    '  edge [color="#7a8699"];',
  ];
  const groups = new Map();
  for (const p of projects) {
    if (!groups.has(p.group)) groups.set(p.group, []);
    groups.get(p.group).push(p);
  }
  for (const [group, items] of groups) {
    lines.push(`  subgraph ${q('cluster_' + group)} {`);
    lines.push(`    label=${q(group)}; fontname="Helvetica"; fontsize=10; color="#c3ccd8"; style=rounded;`);
    for (const p of items) {
      const color = state.scopeProject === p.id ? '#d1493f' : (p.isTest ? '#7aa06f' : '#5b7fc7');
      lines.push(`    ${q(p.id)} [label=${q(`${p.name}\n${p.typeCount} types | ${p.loc} loc`)} fillcolor="${p.isTest ? '#eef6ec' : '#eef2fb'}" color="${color}"];`);
    }
    lines.push('  }');
  }
  for (const p of projects) for (const r of p.projectRefs) if (names.has(r)) lines.push(`  ${q(p.id)} -> ${q(r)};`);
  lines.push('}');
  return { dot: lines.join('\n'), note: `${projects.length} projects` };
}

function namespaceGraphDot() {
  const tests = el('showTests').checked;
  const keep = new Set(state.model.namespaces.filter((n) => tests || !state.byProject.get(n.project)?.isTest).map((n) => n.id));
  const lines = [
    'digraph namespaces {',
    '  rankdir=BT; bgcolor="transparent"; splines=spline; nodesep=0.3; ranksep=0.8;',
    '  node [shape=box style="filled,rounded" fillcolor="#eef2fb" color="#5b7fc7" fontname="Helvetica" fontsize=10];',
    '  edge [fontname="Helvetica" fontsize=8 color="#8a94a6"];',
  ];
  for (const n of state.model.namespaces) if (keep.has(n.id)) lines.push(`  ${q(n.id)} [label=${q(`${n.name}\n${n.typeCount} types`)}];`);
  for (const e of state.model.edges.namespaces) if (keep.has(e.from) && keep.has(e.to)) lines.push(`  ${q(e.from)} -> ${q(e.to)} [label=${q(e.weight)} penwidth=${Math.min(4, 0.6 + e.weight / 12).toFixed(1)}];`);
  lines.push('}');
  return { dot: lines.join('\n'), note: `${keep.size} namespaces` };
}

function seedGraph() {
  const g = state.graph;
  g.nodes = new Set();
  g.origin = new Map();
  g.opened = new Set();
  g.members = new Set();
  g.pool = 0;

  if (state.scope === 'nsmap') {
    const t = state.byId.get(state.selected);
    const nsId = state.scopeNamespace ?? (t ? `${t.project}:${t.namespace}` : null);
    if (!nsId) return;
    const ids = state.model.types.filter((x) => `${x.project}:${x.namespace}` === nsId).map((x) => x.id);
    const focus = state.selected && ids.includes(state.selected) ? state.selected : null;
    const { keep } = rank(ids, focus);
    g.seed = nsId;
    g.pool = ids.length;
    for (const id of keep) { g.nodes.add(id); g.origin.set(id, '@seed'); }
    if (focus) g.members.add(focus);
    return;
  }

  if (!state.selected) return;
  const focus = state.selected;
  const ids = state.scope === 'inheritance' ? inheritanceIds(focus) : neighbourIds(focus, Number(el('depth').value));
  const { keep } = rank(ids, focus);
  g.seed = focus;
  g.pool = ids.size;
  for (const id of keep) { g.nodes.add(id); g.origin.set(id, '@seed'); }
  g.members.add(focus);
}

function expandNode(id) {
  const g = state.graph;
  const step = budget();
  const candidates = [...new Set(neighboursOf(id))]
    .filter((n) => !g.nodes.has(n))
    .sort((a, b) => edgeWeight(id, b) - edgeWeight(id, a) || degree(b) - degree(a));
  const room = Math.max(0, MAX_NODES - g.nodes.size);
  const added = candidates.slice(0, Math.min(step, room));
  for (const n of added) { g.nodes.add(n); g.origin.set(n, id); }
  g.opened.add(id);
  return { added: added.length, left: candidates.length - added.length, capped: room < Math.min(step, candidates.length) };
}

function collapseNode(id) {
  const g = state.graph;
  for (const [node, origin] of [...g.origin]) {
    if (origin !== id || node === g.seed || node === state.selected) continue;
    if (g.opened.has(node)) continue;
    g.nodes.delete(node);
    g.origin.delete(node);
    g.members.delete(node);
  }
  g.opened.delete(id);
}

function currentDiagram() {
  if (state.scope === 'projects') return projectDot();
  if (state.scope === 'nsgraph') return namespaceGraphDot();

  const g = state.graph;
  if (!g.nodes.size) return null;
  const title = state.byId.get(g.seed)?.name ?? state.byNamespace.get(g.seed)?.name ?? 'diagram';
  const hidden = Math.max(0, g.pool - g.nodes.size);
  const note = g.opened.size
    ? `${title}: ${g.nodes.size} boxes, ${g.opened.size} expanded`
    : `${title}: ${g.nodes.size} of ${g.pool} related types — click + on a box to pull in more`;
  return { dot: classDot([...g.nodes], state.selected, title), note, dropped: g.opened.size ? 0 : hidden };
}

async function draw() {
  const diagram = currentDiagram();
  const surface = el('surface');
  if (!diagram) {
    surface.innerHTML = '<div class="empty">select a type in the tree</div>';
    return status('nothing selected');
  }
  status('rendering');
  const res = await fetch('/api/render', { method: 'POST', body: diagram.dot });
  if (!res.ok) { surface.innerHTML = `<div class="empty">${esc(await res.text())}</div>`; return; }
  surface.innerHTML = await res.text();
  const svg = surface.querySelector('svg');
  if (!svg) return;
  svg.removeAttribute('width');
  svg.removeAttribute('height');
  const vb = svg.getAttribute('viewBox').split(/\s+/).map(Number);
  state.natural = { w: vb[2], h: vb[3] };
  svg.style.width = `${vb[2]}px`;
  svg.style.height = `${vb[3]}px`;
  svg.addEventListener('click', (event) => {
    const anchor = event.target.closest('a');
    const href = anchor?.getAttribute('xlink:href') ?? anchor?.getAttribute('href');
    if (href?.startsWith('noks:')) {
      event.preventDefault();
      event.stopPropagation();
      const [action, raw] = href.slice(5).split('/');
      const id = decodeURIComponent(raw);
      if (action === 'members') {
        state.keepView = true;
        state.graph.members.has(id) ? state.graph.members.delete(id) : state.graph.members.add(id);
      } else if (action === 'expand') {
        const { added, left, capped } = expandNode(id);
        if (!added) return status(capped ? `node limit ${MAX_NODES} reached` : `${state.byId.get(id)?.name} has nothing new to add`, true);
        state.pending = `added ${added} linked type${added === 1 ? '' : 's'}${left ? `, ${left} still hidden` : ''}`;
      } else if (action === 'collapse') {
        collapseNode(id);
      }
      draw();
      return;
    }
    const node = event.target.closest('g.node');
    const id = node?.querySelector('title')?.textContent;
    if (!id) return;
    if (state.byId.has(id)) select(id, { reseed: false });
    else if (state.byProject.has(id)) selectProject(state.byProject.get(id));
    else if (state.byNamespace.has(id)) selectNamespace(state.byNamespace.get(id));
  });
  for (const g of svg.querySelectorAll('g.node')) g.style.cursor = 'pointer';
  if (state.keepView) { applyView(); state.keepView = false; } else fit();
  status(state.pending ? `${diagram.note} · ${state.pending}` : diagram.note, Boolean(state.pending));
  state.pending = null;
}

function applyView() {
  const svg = el('surface').querySelector('svg');
  if (!svg) return;
  svg.style.transform = `translate(${state.view.x}px, ${state.view.y}px) scale(${state.view.k})`;
  el('zoom').textContent = `${Math.round(state.view.k * 100)}%`;
}

function fit() {
  const surface = el('surface');
  if (!state.natural) return;
  const raw = Math.min((surface.clientWidth - 40) / state.natural.w, (surface.clientHeight - 40) / state.natural.h);
  const k = Math.min(1.6, Math.max(0.5, raw));
  state.view = {
    k,
    x: Math.max(20, (surface.clientWidth - state.natural.w * k) / 2),
    y: Math.max(20, (surface.clientHeight - state.natural.h * k) / 2),
  };
  applyView();
}

function zoomBy(factor) {
  const surface = el('surface');
  const cx = surface.clientWidth / 2;
  const cy = surface.clientHeight / 2;
  const k = Math.min(6, Math.max(0.08, state.view.k * factor));
  state.view.x = cx - (cx - state.view.x) * (k / state.view.k);
  state.view.y = cy - (cy - state.view.y) * (k / state.view.k);
  state.view.k = k;
  applyView();
}

function bind() {
  el('scope').onchange = (e) => { state.scope = e.target.value; seedGraph(); draw(); };
  el('showTests').onchange = () => { renderTree(); draw(); };
  el('showMembers').onchange = draw;
  el('allMembers').onchange = () => { renderTree(); renderDetails(); };
  el('depth').onchange = () => { seedGraph(); draw(); };
  el('budget').onchange = () => { if (!state.graph.opened.size) seedGraph(); draw(); };
  el('reseed').onclick = () => { seedGraph(); draw(); };
  el('search').oninput = renderTree;
  el('fit').onclick = fit;
  el('toggleTree').onclick = () => { document.querySelector('main').classList.toggle('no-tree'); requestAnimationFrame(fit); };
  el('toggleDetails').onclick = () => { document.querySelector('main').classList.toggle('no-details'); requestAnimationFrame(fit); };
  el('zoomIn').onclick = () => zoomBy(1.25);
  el('zoomOut').onclick = () => zoomBy(0.8);
  el('regen').onclick = async () => {
    status('regenerating');
    const res = await fetch('/api/regenerate', { method: 'POST' });
    if (!res.ok) return status('regeneration failed', true);
    state.model = await (await fetch('/api/model')).json();
    index();
    renderTree();
    renderDetails();
    draw();
  };
  el('dl').onclick = () => {
    const svg = el('surface').querySelector('svg');
    if (!svg) return;
    const a = document.createElement('a');
    a.href = URL.createObjectURL(new Blob([svg.outerHTML], { type: 'image/svg+xml' }));
    a.download = `noks-${state.scope}.svg`;
    a.click();
  };
  el('details').addEventListener('click', (e) => {
    const id = e.target.dataset?.id;
    if (id) select(id);
  });

  const surface = el('surface');
  let drag = null;
  surface.addEventListener('mousedown', (e) => { drag = { x: e.clientX - state.view.x, y: e.clientY - state.view.y }; surface.classList.add('dragging'); });
  window.addEventListener('mouseup', () => { drag = null; surface.classList.remove('dragging'); });
  window.addEventListener('mousemove', (e) => {
    if (!drag) return;
    state.view.x = e.clientX - drag.x;
    state.view.y = e.clientY - drag.y;
    applyView();
  });
  surface.addEventListener('wheel', (e) => {
    e.preventDefault();
    const rect = surface.getBoundingClientRect();
    const mx = e.clientX - rect.left;
    const my = e.clientY - rect.top;
    const k = Math.min(6, Math.max(0.08, state.view.k * Math.exp(-e.deltaY * 0.0015)));
    state.view.x = mx - (mx - state.view.x) * (k / state.view.k);
    state.view.y = my - (my - state.view.y) * (k / state.view.k);
    state.view.k = k;
    applyView();
  }, { passive: false });
  window.addEventListener('resize', applyView);
}

boot();
