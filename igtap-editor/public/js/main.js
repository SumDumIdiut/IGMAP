import { state, markDirty }        from './state.js';
import { initCanvas, startRenderLoop } from './canvas.js';
import { buildToolbar, buildGallery,
         bindProperties, refreshProperties,
         setTool }                 from './ui.js';
import { initInput }              from './input.js';
import { saveToServer, loadFromServer,
         listMaps, downloadLevel, openLevelFile } from './levelio.js';
import { pushSnapshot, clearHistory }   from './history.js';
import { loadManifest, loadLayerSprites } from './sprites.js';
import { applyAutoTile, clearAutoTile, applyPack, packIsActive, packCategory,
         syncRotsFromPacks } from './autotile.js';

async function refreshMapList() {
  const sel  = document.getElementById('map-list');
  const maps = await listMaps();
  sel.innerHTML = '<option value="">— maps —</option>';
  for (const m of maps) {
    const opt = document.createElement('option');
    opt.value       = m.name;
    opt.textContent = m.stashed ? `[stash] ${m.name}` : m.name;
    if (m.name === state.currentMapName) opt.selected = true;
    sel.appendChild(opt);
  }
}

async function main() {
  const canvasEl = document.getElementById('main-canvas');
  if (!canvasEl) throw new Error('Canvas element #main-canvas not found');

  initCanvas(canvasEl);
  startRenderLoop();

  buildToolbar();
  bindProperties();
  refreshProperties();

  initInput(canvasEl);

  // Map name input — keep state in sync
  const mapNameEl = document.getElementById('map-name');
  mapNameEl.addEventListener('input', () => {
    state.currentMapName = mapNameEl.value.trim() || 'custommap';
  });

  // Map list dropdown — clicking a map loads it
  document.getElementById('map-list').addEventListener('change', async e => {
    const name = e.target.value;
    if (!name) return;
    if (state.unsaved && !confirm(`Discard unsaved changes and load "${name}"?`)) {
      e.target.value = ''; return;
    }
    await loadFromServer(name);
    mapNameEl.value = name;
  });

  // Topbar buttons
  document.getElementById('btn-load').addEventListener('click', async () => {
    if (state.unsaved && !confirm('Discard unsaved changes and reload?')) return;
    const name = mapNameEl.value.trim() || 'custommap';
    await loadFromServer(name);
    await refreshMapList();
  });
  document.getElementById('btn-save').addEventListener('click', async () => {
    state.currentMapName = mapNameEl.value.trim() || 'custommap';
    await saveToServer();
    await refreshMapList();
  });
  document.getElementById('btn-dl').addEventListener('click', downloadLevel);
  document.getElementById('btn-close-game').addEventListener('click', () => {
    fetch('/api/editor/close', { method: 'POST' }).catch(() => {});
  });
  document.getElementById('btn-open').addEventListener('click', () => {
    const inp = Object.assign(document.createElement('input'), { type: 'file', accept: '.json' });
    inp.onchange = e => { if (e.target.files[0]) openLevelFile(e.target.files[0]); };
    inp.click();
  });
  document.getElementById('btn-new').addEventListener('click', () => {
    if (state.unsaved && !confirm('Clear all map data?')) return;
    state.tiles = new Map(); state.images = []; state.hitboxes = [];
    state.boxes = []; state.swapTriggers = []; state.checkpoints = [];
    state.spawns = []; state.gates = []; state.ends = []; state.uis = [];
    state.activeGroup = 0; state.selection = null; state.multiSel = []; state.marquee = null; state.unsaved = false;
    state.panX = 0; state.panY = 5; state.zoom = 2;
    clearHistory(); pushSnapshot();
    markDirty();
  });

  // Load sprite manifest + layer sprites (and auto-reload if game exports new ones)
  async function reloadSprites() {
    try {
      await loadManifest();
      state._tilesetIndex = null;   // invalidate name→file cache used by tile rendering
      if (state.manifest) await loadLayerSprites(state.manifest);
    } catch (err) {
      console.warn('Sprite load:', err);
    }
  }
  await reloadSprites();

  // ── Auto-tile panel ─────────────────────────────────────────
  const atPanel = document.getElementById('prop-autotile');

  // One pack can be active per CATEGORY (basic ground / blue blocks / orange
  // blocks) — highlight each category's active pack in its color.
  const CAT_OUTLINE = { basic: '#4ade80', blue: '#60a5fa', orange: '#fb923c' };
  function atRefreshSlots() {
    const byFamily = new Map((_packsCache ?? []).map(p => [p.family, p]));
    document.querySelectorAll('.at-pack').forEach(b => {
      const pack = byFamily.get(b.dataset.family);
      b.style.outline = pack && packIsActive(pack)
        ? `2px solid ${CAT_OUTLINE[packCategory(pack)]}` : 'none';
    });
  }

  // Packs: generated defaults (packs.json, from gen_packs.py) merged with
  // user-authored packs (/api/userpacks). User packs are listed first and
  // shadow a default with the same label.
  let _packsCache = null;
  let _userPacks  = null;
  async function getPacks() {
    if (_packsCache) return _packsCache;
    let defaults = [], user = [];
    try { defaults = await (await fetch('/packs.json')).json(); } catch { }
    try { user     = await (await fetch('/api/userpacks')).json(); } catch { }
    if (!Array.isArray(user)) user = [];
    _userPacks = user;
    const userLabels = new Set(user.map(p => p.label.toLowerCase()));
    _packsCache = [
      ...user.map(p => ({ ...p, family: p.family ?? 'user:' + p.label, user: true })),
      ...defaults.filter(p => !userLabels.has(p.label.toLowerCase())),
    ];
    return _packsCache;
  }

  async function saveUserPacks() {
    const res = await fetch('/api/userpacks', {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(_userPacks),
    });
    const data = await res.json();
    _packsCache = null;            // re-merge on next build
    await atBuildPacks();
    return data;
  }

  // Categories present in the current tile selection (null = no selection)
  function selectionCategories() {
    const keys = selectedTileKeys();
    if (!keys) return null;
    const cats = new Set();
    for (const key of keys) {
      const t = state.tiles.get(key);
      if (!t) continue;
      if (t.layer === 3) cats.add('blue');
      else if (t.layer === 4) cats.add('orange');
      else if (t.layer === 0 || t.layer === 1) cats.add('basic');
    }
    return cats.size > 0 ? cats : null;
  }

  let _lastPackFilter = '';
  async function atBuildPacks() {
    const cont = document.getElementById('at-packs');
    const all = await getPacks();
    syncRotsFromPacks(all);   // pick up rot-offset changes for already-active packs
    // only list packs usable for the selected tiles
    const cats = selectionCategories();
    _lastPackFilter = cats ? [...cats].sort().join(',') : '';
    const packs = cats ? all.filter(p => cats.has(packCategory(p))) : all;
    cont.innerHTML = '';
    if (packs.length === 0) {
      cont.innerHTML = '<p class="hint" style="grid-column:1/-1;font-size:10px">' +
        (all.length === 0 ? 'No packs defined (public/packs.json is empty).'
                          : 'No packs match the selected tile types.') + '</p>';
      return;
    }
    for (const pack of packs) {
      const btn = document.createElement('button');
      btn.className = 'at-pack';
      btn.dataset.family = pack.family;
      const cat = packCategory(pack);
      btn.title = `${pack.user ? '★ your pack' : pack.family}\nstyles: ${cat === 'basic' ? 'basic ground' : cat + ' swap blocks'}\ncorner: ${pack.corner}\nedge: ${pack.edge}\nfill: ${pack.fill}`;
      btn.style.cssText = 'display:flex;align-items:center;gap:5px;padding:3px 5px;font-size:10px;background:#1e2030;border:1px solid ' +
                          (cat === 'blue' ? '#1d4ed8' : cat === 'orange' ? '#b45309' : (pack.user ? '#7c5c10' : '#374158')) +
                          ';border-radius:3px;cursor:pointer;color:#c9d1e0';
      const img = document.createElement('img');
      img.src = `/sprites/${encodeURIComponent(pack.edge + '.png')}`;
      img.style.cssText = 'width:24px;height:24px;image-rendering:pixelated;flex-shrink:0';
      btn.appendChild(img);
      const label = document.createElement('span');
      label.textContent = (pack.user ? '★ ' : '') + pack.label;
      btn.appendChild(label);
      btn.addEventListener('click', () => { applyPack(pack); atRefreshSlots(); });
      if (pack.user) {
        btn.addEventListener('contextmenu', async e => {
          e.preventDefault();
          if (!confirm(`Delete your pack "${pack.label}"?`)) return;
          _userPacks = _userPacks.filter(p => p.label !== pack.label);
          await saveUserPacks();
        });
      }
      cont.appendChild(btn);
    }
    atRefreshSlots();
  }

  document.getElementById('btn-autotile').addEventListener('click', () => {
    const open = atPanel.style.display !== 'none';
    atPanel.style.display = open ? 'none' : 'block';
    if (!open) atBuildPacks();
  });
  // While the panel is open, re-filter the pack list when the selection changes
  setInterval(() => {
    if (atPanel.style.display === 'none') return;
    const cats = selectionCategories();
    const f = cats ? [...cats].sort().join(',') : '';
    if (f !== _lastPackFilter) atBuildPacks();
  }, 400);
  // Apply/Clear scope: selected tiles when there's a selection, whole map otherwise
  function selectedTileKeys() {
    const keys = new Set();
    for (const item of state.multiSel ?? []) if (item.type === 'tile') keys.add(item.key);
    if (state.selection?.type === 'tile') keys.add(state.selection.key);
    return keys.size > 0 ? keys : null;
  }
  document.getElementById('at-apply').addEventListener('click', () => {
    const sel = selectedTileKeys();
    const n = applyAutoTile(sel);
    const msg = document.getElementById('status-msg');
    if (msg) msg.textContent = n ? `Auto-tiled ${n} ${sel ? 'selected ' : ''}tiles ✓`
                                 : (sel ? 'Selection has no ground tiles' : 'No ground tiles to auto-tile');
  });
  document.getElementById('at-clear').addEventListener('click', () => clearAutoTile(selectedTileKeys()));

  // Re-check for fresh sprites every 5 s so the editor picks them up automatically
  // when the in-game plugin exports on scene load.
  setInterval(reloadSprites, 5000);

  buildGallery().catch(err => console.warn('Gallery:', err));

  // Load initial map
  const initName = mapNameEl.value.trim() || 'custommap';
  await loadFromServer(initName);
  await refreshMapList();

  if (state.tiles.size === 0 && state.images.length === 0) {
    pushSnapshot();
  }

  console.log('IGTAP Editor ready. 1-6=tiles X=erase G/N/S/U=markers H=hitbox I=image Esc=pointer Q/E=rotate Ctrl=snap Ctrl+Z=undo Ctrl+S=save F=fit');
}

main().catch(err => {
  console.error('Editor init error:', err);
  document.body.style.cssText = 'background:#111;color:#f87171;font-family:monospace;padding:24px;';
  document.body.innerHTML = `<h2>Editor failed to start</h2><pre>${err.stack || err.message}</pre>
    <p style="color:#9ca3af">Check browser console (F12) for details.</p>`;
});
