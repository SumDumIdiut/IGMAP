import { state, markDirty } from './state.js';
import { clearHistory, pushSnapshot } from './history.js';
import { getImage } from './sprites.js';
import { computePrices } from './utils.js';
import { fitViewToContent } from './canvas.js';

export function serializeLevel() {
  const tiles = [];
  for (const t of state.tiles.values()) {
    tiles.push({ x: t.x, y: t.y, layer: t.layer, rot: t.rot ?? 0, sprite: t.sprite ?? '' });
  }
  // Keep single-value fields for backwards compat (first of each group 0, or first overall)
  const g0spawn = state.spawns.find(m => m.group === 0) ?? state.spawns[0];
  const g0gate  = state.gates.find(m => m.group === 0)  ?? state.gates[0];
  const g0end   = state.ends.find(m => m.group === 0)   ?? state.ends[0];
  const g0ui    = state.uis.find(m => m.group === 0)    ?? state.uis[0];
  return {
    version:    3,
    baseReward: state.baseReward,
    cloneMult:  state.cloneMult,
    spawnX: g0spawn?.x ?? 0, spawnY: g0spawn?.y ?? 0,
    gateX:  g0gate?.x  ?? 0, gateY:  g0gate?.y  ?? 0,
    endX:   g0end?.x   ?? 0, endY:   g0end?.y   ?? 0,
    uiX:    g0ui?.x    ?? 0, uiY:    g0ui?.y    ?? 0,
    spawns: state.spawns.map(m => ({ x: m.x, y: m.y, group: m.group ?? 0 })),
    gates:  state.gates.map(m => ({ x: m.x, y: m.y, group: m.group ?? 0 })),
    ends:   state.ends.map(m => ({ x: m.x, y: m.y, group: m.group ?? 0 })),
    uis:    state.uis.map(m => ({ x: m.x, y: m.y, group: m.group ?? 0 })),
    tiles,
    boxes: state.boxes.map(b => {
      const prices = b.prices ?? computePrices(b.formula ?? '10', b.cap);
      return { x: b.x, y: b.y, upgrade: b.upgrade, cap: b.cap, group: b.group ?? 0,
               formula: b.formula ?? '10', price: prices[0] ?? 0, prices };
    }),
    swapTriggers: state.swapTriggers.map(s => ({ x: s.x, y: s.y })),
    checkpoints:  state.checkpoints.map(c => ({ x: c.x, y: c.y })),
    hitboxes:     state.hitboxes.map(h => ({ x: h.x, y: h.y, w: h.w, h: h.h })),
    images:       state.images.map(i => ({
      path: i.path, x: i.x, y: i.y, w: i.w, h: i.h,
      rot: i.rot, opacity: i.opacity, cr: i.cr, cg: i.cg, cb: i.cb, depth: i.depth
    })),
  };
}

export function deserializeLevel(json) {
  state.tiles = new Map();
  (json.tiles || []).forEach(t => {
    state.tiles.set(`${t.x},${t.y},${t.layer}`,
      { x: t.x, y: t.y, layer: t.layer, rot: t.rot ?? 0, sprite: t.sprite ?? '' });
  });

  state.images   = (json.images   || []).map(i => ({ ...i }));
  state.hitboxes = (json.hitboxes || []).map(h => ({ ...h }));
  state.boxes    = (json.boxes    || []).map(b => {
    if (!b.formula) {
      const p = b.price ?? 0, a = b.addFactor ?? 0;
      b = { ...b, formula: a ? `${p} + ${a} * (n - 1)` : `${p}` };
    }
    if (!b.prices) b = { ...b, prices: computePrices(b.formula, b.cap) };
    b = { ...b, group: b.group ?? 0 };
    return b;
  });
  state.swapTriggers = (json.swapTriggers || []).map(s => ({ ...s }));
  state.checkpoints  = (json.checkpoints  || []).map(c => ({ ...c }));

  // Load multi-marker arrays; fall back to old single-value format
  const migrateMarker = (arr, xField, yField) => {
    if (arr && arr.length > 0) return arr.map(m => ({ x: m.x, y: m.y, group: m.group ?? 0 }));
    if (json[xField] != null) return [{ x: json[xField], y: json[yField], group: 0 }];
    return [];
  };
  state.spawns = migrateMarker(json.spawns, 'spawnX', 'spawnY');
  state.gates  = migrateMarker(json.gates,  'gateX',  'gateY');
  state.ends   = migrateMarker(json.ends,   'endX',   'endY');
  state.uis    = migrateMarker(json.uis,    'uiX',    'uiY');

  state.baseReward  = json.baseReward ?? 10;
  state.cloneMult   = json.cloneMult  ?? 1.0;
  state.activeGroup = 0;
  state.selection   = null;
  state.unsaved     = false;

  for (const img of state.images) getImage(img.path);

  clearHistory();
  pushSnapshot();
  markDirty();
}

export async function saveToServer() {
  const name  = state.currentMapName || 'custommap';
  const level = serializeLevel();
  try {
    const res = await fetch(`/api/maps/${encodeURIComponent(name)}`, {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(level),
    });
    const data = await res.json();
    if (data.ok) {
      state.unsaved = false;
      showMsg(`Saved "${name}" ✓`);
    } else {
      showMsg('Save error: ' + (data.error || '?'));
    }
  } catch (e) {
    showMsg('Save failed: ' + e.message);
  }
}

export async function loadFromServer(name) {
  const mapName = name || state.currentMapName || 'custommap';
  try {
    const res  = await fetch(`/api/maps/${encodeURIComponent(mapName)}`);
    const json = await res.json();
    deserializeLevel(json);
    state.currentMapName = mapName;
    requestAnimationFrame(() => fitViewToContent());
    showMsg(`Loaded "${mapName}" ✓`);
    return true;
  } catch (e) {
    showMsg('Load failed: ' + e.message);
    return false;
  }
}

export async function listMaps() {
  try {
    const res = await fetch('/api/maps');
    return await res.json();
  } catch {
    return [];
  }
}

export function downloadLevel() {
  const json = JSON.stringify(serializeLevel(), null, 2);
  const blob = new Blob([json], { type: 'application/json' });
  const url  = URL.createObjectURL(blob);
  const a    = document.createElement('a');
  a.href     = url;
  a.download = 'custommap.json';
  a.click();
  URL.revokeObjectURL(url);
}

export function openLevelFile(file) {
  const reader = new FileReader();
  reader.onload = e => {
    try {
      const json = JSON.parse(e.target.result);
      deserializeLevel(json);
      requestAnimationFrame(() => fitViewToContent());
      showMsg('Opened: ' + file.name);
    } catch {
      showMsg('Invalid JSON file');
    }
  };
  reader.readAsText(file);
}

function showMsg(text) {
  const el = document.getElementById('status-msg');
  if (!el) return;
  el.textContent = text;
  clearTimeout(el._t);
  el._t = setTimeout(() => { el.textContent = ''; }, 3000);
}
