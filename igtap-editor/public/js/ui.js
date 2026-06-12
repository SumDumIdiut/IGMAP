import { state, markDirty }    from './state.js';
import { pushSnapshot }         from './history.js';
import { loadManifest, categorizeGroup,
         buildGamePath }         from './sprites.js';
import { deleteSelected,
         rotateSelected }        from './tools.js';
import { evalBoxFormula,
         computePrices }         from './utils.js';
import { GROUP_PALETTE, groupColor } from './canvas.js';

// Tool definitions
export const TOOLS = [
  { id: 'pointer',     label: 'Ptr',   key: 'Esc', icon: '↖',  group: false },
  null, // divider
  { id: 'ground',      label: 'Gnd',   key: '1',   icon: '▪',   group: false },
  { id: 'spike',       label: 'Spk',   key: '2',   icon: '▲',   group: false },
  { id: 'blue',        label: 'Blu',   key: '3',   icon: '◼',   group: false },
  { id: 'orange',      label: 'Org',   key: '4',   icon: '◼',   group: false },
  { id: 'bluespike',   label: 'BSpk',  key: '5',   icon: '△',   group: false },
  { id: 'orangespike', label: 'OSpk',  key: '6',   icon: '△',   group: false },
  { id: 'erase',       label: 'Ers',   key: 'X',   icon: '✕',   group: false },
  null,
  { id: 'gate',        label: 'Gate',  key: 'G',   icon: '⬡',   group: false },
  { id: 'end',         label: 'End',   key: 'N',   icon: '⬡',   group: false },
  { id: 'spawn',       label: 'Spwn',  key: 'S',   icon: '●',   group: false },
  { id: 'ui',          label: 'UI',    key: 'U',   icon: '□',   group: false },
  null,
  { id: 'swaptrig',    label: 'Swap',  key: 'T',   icon: '⇄',   group: false },
  { id: 'checkpoint',  label: 'ChkP',  key: 'C',   icon: '⚑',   group: false },
  { id: 'box',         label: 'Box',   key: 'B',   icon: '◧',   group: false },
  null,
  { id: 'hitbox',      label: 'Hitb',  key: 'H',   icon: '⬜',   group: false },
  { id: 'imageobj',    label: 'Img',   key: 'I',   icon: '🖼',   group: false },
];

export function buildToolbar() {
  const bar = document.getElementById('toolbar');
  for (const t of TOOLS) {
    if (!t) { bar.appendChild(Object.assign(document.createElement('div'), { className: 'tool-group' })); continue; }
    const btn = document.createElement('button');
    btn.className = 'tool-btn' + (t.id === state.activeTool ? ' active' : '');
    btn.dataset.tool = t.id;
    btn.title = `${t.label} [${t.key}]`;
    btn.innerHTML = `<span class="icon">${t.icon}</span><span>${t.label}</span><span class="key-badge">${t.key}</span>`;
    btn.addEventListener('click', () => setTool(t.id));
    bar.appendChild(btn);
  }
}

export function setTool(id) {
  state.activeTool = id;
  document.querySelectorAll('.tool-btn').forEach(b => {
    b.classList.toggle('active', b.dataset.tool === id);
  });
  const toolEl = document.getElementById('status-tool');
  if (toolEl) toolEl.textContent = id.charAt(0).toUpperCase() + id.slice(1);
  refreshProperties();
  markDirty();
}

const MARKER_TOOLS = new Set(['spawn','gate','end','ui']);

// ── Properties panel ──────────────────────────────────────────
export function refreshProperties() {
  const imgPanel    = document.getElementById('prop-image');
  const boxPanel    = document.getElementById('prop-box');
  const hbPanel     = document.getElementById('prop-hitbox');
  const markerPanel = document.getElementById('prop-marker');
  const emptyPanel  = document.getElementById('prop-empty');

  const multiSelPanel = document.getElementById('prop-multisel');
  const objSelPanel   = document.getElementById('prop-objsel');

  const showImage    = state.activeTool === 'imageobj' || state.selection?.type === 'image';
  const showBox      = state.activeTool === 'box' || state.selection?.type === 'box';
  const showHitbox   = state.selection?.type === 'hitbox';
  const showMarker   = MARKER_TOOLS.has(state.activeTool) || state.selection?.type === 'marker';
  const showObjSel   = state.selection?.type === 'checkpoint' || state.selection?.type === 'swaptrig';
  const showMultiSel = state.multiSel?.length > 0;

  imgPanel.style.display     = showImage    ? 'flex' : 'none';
  boxPanel.style.display     = showBox      ? 'flex' : 'none';
  hbPanel.style.display      = showHitbox   ? 'flex' : 'none';
  markerPanel.style.display  = showMarker   ? 'flex' : 'none';
  objSelPanel.style.display  = showObjSel   ? 'flex' : 'none';
  multiSelPanel.style.display = showMultiSel ? 'flex' : 'none';
  document.getElementById('prop-course').style.display = 'none';
  emptyPanel.style.display  = (!showImage && !showBox && !showHitbox && !showMarker && !showObjSel && !showMultiSel) ? 'flex' : 'none';

  if (showImage) {
    const sel = state.selection?.type === 'image';
    const img = sel ? state.images[state.selection.index] : null;
    const w   = img ? img.w       : state.pendingImageW;
    const h   = img ? img.h       : state.pendingImageH;
    const rot = img ? img.rot     : state.pendingImageRot;
    const op  = img ? img.opacity : state.pendingImageOpacity;
    const cr  = img ? img.cr      : state.pendingImageCr;
    const cg  = img ? img.cg      : state.pendingImageCg;
    const cb  = img ? img.cb      : state.pendingImageCb;
    const dep = img ? img.depth   : state.pendingImageDepth;

    document.getElementById('img-w').value   = w;
    document.getElementById('img-h').value   = h;
    document.getElementById('img-rot').value = rot;
    document.getElementById('img-opacity').value = op;
    document.getElementById('img-opacity-val').textContent = op.toFixed(2);
    document.getElementById('img-depth').value = dep;
    document.getElementById('img-cr').value = cr;
    document.getElementById('img-cg').value = cg;
    document.getElementById('img-cb').value = cb;
    document.getElementById('img-cr-val').textContent = cr.toFixed(2);
    document.getElementById('img-cg-val').textContent = cg.toFixed(2);
    document.getElementById('img-cb-val').textContent = cb.toFixed(2);
    document.getElementById('tint-preview').style.background =
      `rgb(${(cr*255)|0},${(cg*255)|0},${(cb*255)|0})`;
    document.getElementById('img-delete').style.display = sel ? 'block' : 'none';
  }

  if (showHitbox) {
    const h = state.hitboxes[state.selection.index];
    document.getElementById('hb-x').value = h.x.toFixed(2);
    document.getElementById('hb-y').value = h.y.toFixed(2);
    document.getElementById('hb-w').value = h.w.toFixed(2);
    document.getElementById('hb-h').value = h.h.toFixed(2);
  }

  if (showBox) {
    const selBox = state.selection?.type === 'box' ? state.boxes[state.selection.index] : null;
    document.getElementById('box-upgrade').value = selBox ? selBox.upgrade : state.boxUpgrade;
    document.getElementById('box-cap').value     = selBox ? selBox.cap     : state.boxCap;
    document.getElementById('box-formula').value = selBox ? selBox.formula : state.boxFormula;
    document.getElementById('box-delete').style.display = selBox ? 'block' : 'none';
    requestAnimationFrame(drawBoxFormulaGraph);
  }

  if (showObjSel) {
    const LABELS = { checkpoint: 'Checkpoint', swaptrig: 'Swap Trigger' };
    document.getElementById('objsel-title').textContent = LABELS[state.selection.type] ?? 'Object';
  }

  if (showMultiSel) {
    document.getElementById('multisel-count').textContent =
      state.multiSel.length + ' object' + (state.multiSel.length === 1 ? '' : 's') + ' selected';
  }

  if (showMarker) {
    const sel = state.selection?.type === 'marker' ? state.selection : null;
    const markerType = sel?.markerType ?? state.activeTool;
    const arr = state[markerType + 's'];
    const marker = sel ? arr[sel.index] : null;
    const group = marker?.group ?? state.activeGroup;

    const NAMES = { spawn: 'Spawn', gate: 'Gate', end: 'End', ui: 'UI' };
    document.getElementById('marker-type-label').textContent = NAMES[markerType] ?? markerType;
    document.getElementById('marker-group').value = group;
    updateGroupSwatch(group);

    document.getElementById('marker-base-reward').value = state.baseReward;
    document.getElementById('marker-clone-mult').value  = state.cloneMult;
    document.getElementById('marker-delete').style.display = sel ? 'block' : 'none';
  }
}

function updateGroupSwatch(g) {
  const swatch = document.getElementById('group-swatch');
  if (!swatch) return;
  const col = groupColor(g);
  swatch.style.background = col.fill;
  swatch.style.boxShadow = `0 0 0 2px ${col.stroke}`;
}

export function bindProperties() {
  // Image panel
  function imgSet(field, parse) {
    return e => {
      const v = parse(e.target.value);
      if (isNaN(v)) return;
      if (state.selection?.type === 'image') {
        state.images[state.selection.index][field] = v;
        pushSnapshot();
      } else {
        state['pendingImage' + field.charAt(0).toUpperCase() + field.slice(1)] = v;
      }
      markDirty();
    };
  }

  document.getElementById('img-w').addEventListener('change', imgSet('w', parseFloat));
  document.getElementById('img-h').addEventListener('change', imgSet('h', parseFloat));
  document.getElementById('img-rot').addEventListener('change', imgSet('rot', parseFloat));
  document.getElementById('img-depth').addEventListener('change', imgSet('depth', parseInt));

  function bindSlider(id, valId, field) {
    const el  = document.getElementById(id);
    const lbl = document.getElementById(valId);
    el.addEventListener('input', e => {
      const v = parseFloat(e.target.value);
      if (lbl) lbl.textContent = v.toFixed(2);
      const lo = field.toLowerCase();
      if (lo === 'opacity') {
        if (state.selection?.type === 'image') state.images[state.selection.index].opacity = v;
        else state.pendingImageOpacity = v;
      } else {
        const key = 'pendingImage' + field;
        if (state.selection?.type === 'image') state.images[state.selection.index][lo] = v;
        else state[key] = v;
      }
      if (field === 'Cr' || field === 'Cg' || field === 'Cb') {
        const cr = parseFloat(document.getElementById('img-cr').value);
        const cg = parseFloat(document.getElementById('img-cg').value);
        const cb = parseFloat(document.getElementById('img-cb').value);
        document.getElementById('tint-preview').style.background =
          `rgb(${(cr*255)|0},${(cg*255)|0},${(cb*255)|0})`;
      }
      markDirty();
    });
  }
  bindSlider('img-opacity', 'img-opacity-val', 'Opacity');
  bindSlider('img-cr',      'img-cr-val',      'Cr');
  bindSlider('img-cg',      'img-cg-val',      'Cg');
  bindSlider('img-cb',      'img-cb-val',      'Cb');

  document.getElementById('img-rot-ccw').addEventListener('click', () => rotateSelected(-90));
  document.getElementById('img-rot-cw').addEventListener('click',  () => rotateSelected(90));

  document.getElementById('img-delete').addEventListener('click', deleteSelected);

  // Marker panel
  document.getElementById('marker-group').addEventListener('change', e => {
    const g = Math.max(0, parseInt(e.target.value) || 0);
    state.activeGroup = g;
    if (state.selection?.type === 'marker') {
      const arr = state[state.selection.markerType + 's'];
      if (arr[state.selection.index]) { arr[state.selection.index].group = g; pushSnapshot(); }
    }
    updateGroupSwatch(g);
    markDirty();
  });
  document.getElementById('marker-group-dec').addEventListener('click', () => {
    const el = document.getElementById('marker-group');
    el.value = Math.max(0, (parseInt(el.value) || 0) - 1);
    el.dispatchEvent(new Event('change'));
  });
  document.getElementById('marker-group-inc').addEventListener('click', () => {
    const el = document.getElementById('marker-group');
    el.value = (parseInt(el.value) || 0) + 1;
    el.dispatchEvent(new Event('change'));
  });
  document.getElementById('marker-delete').addEventListener('click', deleteSelected);
  document.getElementById('marker-base-reward').addEventListener('change', e => {
    const v = parseInt(e.target.value);
    if (!isNaN(v) && v >= 1) { state.baseReward = v; state.unsaved = true; }
  });
  document.getElementById('marker-clone-mult').addEventListener('change', e => {
    const v = parseFloat(e.target.value);
    if (!isNaN(v) && v >= 0) { state.cloneMult = v; state.unsaved = true; }
  });

  // Checkpoint / swap trigger single-select panel
  document.getElementById('objsel-delete').addEventListener('click', deleteSelected);

  // Multi-selection panel
  document.getElementById('multisel-delete').addEventListener('click', deleteSelected);

  // Hitbox panel
  function hbSet(field) {
    return e => {
      const v = parseFloat(e.target.value);
      if (isNaN(v) || !state.selection) return;
      state.hitboxes[state.selection.index][field] = v;
      pushSnapshot();
      markDirty();
    };
  }
  document.getElementById('hb-x').addEventListener('change', hbSet('x'));
  document.getElementById('hb-y').addEventListener('change', hbSet('y'));
  document.getElementById('hb-w').addEventListener('change', hbSet('w'));
  document.getElementById('hb-h').addEventListener('change', hbSet('h'));
  document.getElementById('hb-delete').addEventListener('click', deleteSelected);

  // Box panel — writes to selected box if one is selected, else brush state
  function getSelBox() { return state.selection?.type === 'box' ? state.boxes[state.selection.index] : null; }
  document.getElementById('box-upgrade').addEventListener('change', e => {
    const v = parseInt(e.target.value);
    const b = getSelBox();
    if (b) { b.upgrade = v; pushSnapshot(); markDirty(); } else { state.boxUpgrade = v; }
  });
  document.getElementById('box-cap').addEventListener('change', e => {
    const v = Math.max(1, parseInt(e.target.value) || 1);
    const b = getSelBox();
    if (b) { b.cap = v; b.prices = null; pushSnapshot(); markDirty(); } else { state.boxCap = v; }
    drawBoxFormulaGraph();
  });
  document.getElementById('box-formula').addEventListener('input', e => {
    const v = e.target.value;
    const b = getSelBox();
    if (b) { b.formula = v; b.prices = null; markDirty(); } else { state.boxFormula = v; }
    drawBoxFormulaGraph();
  });
  document.getElementById('box-delete').addEventListener('click', deleteSelected);

  // Course settings panel
  document.getElementById('course-base-reward').addEventListener('change', e => {
    const v = parseInt(e.target.value);
    if (!isNaN(v) && v >= 1) { state.baseReward = v; state.unsaved = true; }
  });
  document.getElementById('course-clone-mult').addEventListener('change', e => {
    const v = parseFloat(e.target.value);
    if (!isNaN(v) && v >= 0) { state.cloneMult = v; state.unsaved = true; }
  });
}

// ── Box formula graph ─────────────────────────────────────────
function fmtN(v) {
  if (!isFinite(v)) return '?';
  const a = Math.abs(v);
  if (a >= 1e9) return (v / 1e9).toPrecision(3) + 'B';
  if (a >= 1e6) return (v / 1e6).toPrecision(3) + 'M';
  if (a >= 1e3) return (v / 1e3).toPrecision(3) + 'K';
  if (a > 0 && a < 0.01) return v.toExponential(2);
  return Number.isInteger(v) ? String(v) : v.toPrecision(3);
}

function drawBoxFormulaGraph() {
  const canvas = document.getElementById('box-formula-graph');
  if (!canvas) return;

  const W = Math.max(canvas.clientWidth || 176, 80);
  const H = 80;
  canvas.width  = W;
  canvas.height = H;

  const ctx     = canvas.getContext('2d');
  const selBox  = state.selection?.type === 'box' ? state.boxes[state.selection.index] : null;
  const formula = selBox ? selBox.formula : state.boxFormula;
  const cap     = Math.max(1, (selBox ? selBox.cap : state.boxCap) | 0);
  const PAD     = { top: 6, right: 5, bottom: 16, left: 32 };
  const cW      = W - PAD.left - PAD.right;
  const cH      = H - PAD.top  - PAD.bottom;

  ctx.fillStyle = '#0d1117';
  ctx.fillRect(0, 0, W, H);

  const statsEl = document.getElementById('box-formula-stats');

  if (!formula?.trim()) {
    ctx.fillStyle = '#4b5563';
    ctx.font = '10px monospace';
    ctx.textAlign = 'center';
    ctx.textBaseline = 'middle';
    ctx.fillText('Enter a formula above', W / 2, H / 2);
    if (statsEl) statsEl.textContent = '';
    return;
  }

  // Validate: check if any value is non-finite or negative
  let anyBad = false;
  for (let n = 1; n <= cap; n++) {
    const raw = evalBoxFormula(formula, n, cap);
    if (!isFinite(raw) || isNaN(raw) || raw < 0) { anyBad = true; break; }
  }

  if (anyBad) {
    ctx.fillStyle = '#ef4444';
    ctx.font = '10px monospace';
    ctx.textAlign = 'center';
    ctx.textBaseline = 'middle';
    ctx.fillText('Invalid formula', W / 2, H / 2 - 8);
    ctx.font = '9px monospace';
    ctx.fillStyle = '#9ca3af';
    ctx.fillText('n = 1…cap, result must be ≥ 0', W / 2, H / 2 + 6);
    if (statsEl) statsEl.textContent = '';
    return;
  }

  const prices = computePrices(formula, cap);
  const maxP   = Math.max(...prices, 1);
  const minP   = Math.min(...prices, 0);
  const range  = maxP - minP || 1;

  const xOf = i => PAD.left + (cap <= 1 ? cW / 2 : (i / (cap - 1)) * cW);
  const yOf = p => PAD.top + cH - ((p - minP) / range) * cH;

  // Grid
  ctx.strokeStyle = '#1a2332';
  ctx.lineWidth = 1;
  for (let r = 0; r <= 3; r++) {
    const y = PAD.top + cH * r / 3;
    ctx.beginPath(); ctx.moveTo(PAD.left, y); ctx.lineTo(W - PAD.right, y); ctx.stroke();
  }

  // Y-axis labels
  ctx.fillStyle = '#475569';
  ctx.font = '8px monospace';
  ctx.textAlign = 'right';
  ctx.textBaseline = 'middle';
  ctx.fillText(fmtN(maxP),            PAD.left - 3, PAD.top + 2);
  ctx.fillText(fmtN((maxP + minP)/2), PAD.left - 3, PAD.top + cH / 2);
  ctx.fillText(fmtN(minP),            PAD.left - 3, PAD.top + cH - 1);

  // X-axis labels
  ctx.textAlign = 'center';
  ctx.textBaseline = 'alphabetic';
  ctx.fillText('1', xOf(0), H - 2);
  if (cap > 1) ctx.fillText(String(cap), xOf(cap - 1), H - 2);

  // Area fill
  ctx.beginPath();
  ctx.moveTo(xOf(0), PAD.top + cH);
  for (let i = 0; i < cap; i++) ctx.lineTo(xOf(i), yOf(prices[i]));
  ctx.lineTo(xOf(cap - 1), PAD.top + cH);
  ctx.closePath();
  ctx.fillStyle = 'rgba(249,115,22,0.13)';
  ctx.fill();

  // Line
  ctx.beginPath();
  ctx.lineJoin = 'round';
  for (let i = 0; i < cap; i++) {
    i === 0 ? ctx.moveTo(xOf(i), yOf(prices[i])) : ctx.lineTo(xOf(i), yOf(prices[i]));
  }
  ctx.strokeStyle = '#f97316';
  ctx.lineWidth = 1.5;
  ctx.stroke();

  // Dots (skip when cap is very large)
  if (cap <= 60) {
    const r = cap <= 15 ? 2.5 : cap <= 30 ? 1.5 : 1;
    ctx.fillStyle = '#f97316';
    for (let i = 0; i < cap; i++) {
      ctx.beginPath();
      ctx.arc(xOf(i), yOf(prices[i]), r, 0, Math.PI * 2);
      ctx.fill();
    }
  }

  if (statsEl) {
    statsEl.textContent = `#1: ${fmtN(prices[0])}  →  #${cap}: ${fmtN(prices[cap - 1])}`;
  }
}

// ── Sprite gallery ────────────────────────────────────────────
let allGroups = [];
let activeCategory = 'all';
let searchQuery    = '';
const observers = new Map();

export async function buildGallery() {
  await loadManifest();
  allGroups = state.manifest?.groups || [];

  document.getElementById('gallery-search').addEventListener('input', e => {
    searchQuery = e.target.value.toLowerCase();
    renderGallery();
  });

  document.querySelectorAll('.tab-btn').forEach(btn => {
    btn.addEventListener('click', () => {
      document.querySelectorAll('.tab-btn').forEach(b => b.classList.remove('active'));
      btn.classList.add('active');
      activeCategory = btn.dataset.cat;
      renderGallery();
    });
  });

  renderGallery();
}

function renderGallery() {
  const grid = document.getElementById('gallery-grid');
  // Disconnect old observers
  for (const [el, obs] of observers) { obs.disconnect(); }
  observers.clear();
  grid.innerHTML = '';

  let groups = allGroups;
  if (activeCategory !== 'all') {
    groups = groups.filter(g => categorizeGroup(g.name) === activeCategory);
  }
  if (searchQuery) {
    groups = groups.filter(g => g.name.toLowerCase().includes(searchQuery) ||
      g.files.some(f => f.toLowerCase().includes(searchQuery)));
  }

  for (const group of groups) {
    // Find best thumbnail (first file in group)
    const thumbFile = group.files[0];
    if (!thumbFile) continue;

    const slot = document.createElement('div');
    slot.className = 'sprite-thumb';
    slot.title     = group.name;
    slot.dataset.thumbFile = thumbFile;
    slot.dataset.groupName = group.name;

    const imgBox = document.createElement('div');
    imgBox.className = 'thumb-img';
    slot.appendChild(imgBox);

    const nameEl = document.createElement('div');
    nameEl.className   = 'thumb-name';
    nameEl.textContent = group.name;
    slot.appendChild(nameEl);

    // Click: select frame 0 of this group (or show frame picker)
    slot.addEventListener('click', () => selectSprite(slot, group, thumbFile));

    grid.appendChild(slot);

    // Lazy-load thumbnail with IntersectionObserver
    const obs = new IntersectionObserver(entries => {
      for (const entry of entries) {
        if (!entry.isIntersecting) continue;
        const file = entry.target.dataset.thumbFile;
        const img  = document.createElement('img');
        img.src    = `/sprites/${encodeURIComponent(file)}`;
        img.alt    = '';
        imgBox.appendChild(img);
        obs.unobserve(entry.target);
        observers.delete(entry.target);
      }
    }, { root: document.getElementById('gallery-grid'), threshold: 0 });
    obs.observe(slot);
    observers.set(slot, obs);
  }
}

function selectSprite(slot, group, filename) {
  // Deselect previous
  document.querySelectorAll('.sprite-thumb.selected').forEach(el => el.classList.remove('selected'));
  slot.classList.add('selected');

  // If group has multiple frames and was already selected, open frame picker
  if (state.gallerySelected?.groupName === group.name && group.files.length > 1) {
    openFramePicker(slot, group);
    return;
  }

  const gamePath = buildGamePath(filename);
  state.gallerySelected = { groupName: group.name, filename, url: `/sprites/${encodeURIComponent(filename)}` };
  state.pendingImagePath = gamePath;

  // Auto-switch to image tool
  setTool('imageobj');
  markDirty();
}

function openFramePicker(anchorSlot, group) {
  // Remove existing picker
  document.getElementById('frame-picker')?.remove();

  const picker = document.createElement('div');
  picker.id    = 'frame-picker';
  picker.style.cssText = `
    position: fixed;
    background: #1f2937;
    border: 1px solid #374151;
    border-radius: 6px;
    padding: 6px;
    display: flex;
    flex-wrap: wrap;
    gap: 3px;
    max-width: 350px;
    max-height: 250px;
    overflow-y: auto;
    z-index: 9999;
    box-shadow: 0 4px 16px rgba(0,0,0,0.6);
  `;

  const rect = anchorSlot.getBoundingClientRect();
  picker.style.left   = Math.min(rect.left, window.innerWidth  - 360) + 'px';
  picker.style.bottom = (window.innerHeight - rect.top + 4) + 'px';

  for (const file of group.files) {
    const thumb = document.createElement('div');
    thumb.style.cssText = 'width:48px;height:48px;background:#374151;border-radius:3px;cursor:pointer;display:flex;align-items:center;justify-content:center;';
    thumb.title = file;
    const img = document.createElement('img');
    img.src    = `/sprites/${encodeURIComponent(file)}`;
    img.style.cssText = 'max-width:46px;max-height:46px;image-rendering:pixelated;';
    thumb.appendChild(img);
    thumb.addEventListener('click', () => {
      const gamePath = buildGamePath(file);
      state.gallerySelected = { groupName: group.name, filename: file, url: `/sprites/${encodeURIComponent(file)}` };
      state.pendingImagePath = gamePath;
      import('./ui.js').then(m => m.setTool('imageobj'));
      picker.remove();
      markDirty();
    });
    picker.appendChild(thumb);
  }

  document.body.appendChild(picker);
  setTimeout(() => document.addEventListener('click', () => picker.remove(), { once: true }), 0);
}
