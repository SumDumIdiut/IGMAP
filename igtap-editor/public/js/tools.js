import { state, markDirty }         from './state.js';
import { screenToWorld, snapToTile,
         snapToTileCenter }          from './coords.js';
import { pushSnapshot }              from './history.js';
import { buildGamePath }             from './sprites.js';
import { refreshProperties }         from './ui.js';
import { computePrices }             from './utils.js';

// ── Tile tool helpers ─────────────────────────────────────────
const TOOL_LAYER = {
  ground: 0, spike: 2, blue: 3, orange: 4, bluespike: 5, orangespike: 6,
};

function paintTile(wx, wy) {
  const { tx, ty } = snapToTile(wx, wy);
  const layer = TOOL_LAYER[state.activeTool];
  if (layer == null) return;
  state.tiles.set(`${tx},${ty},${layer}`, { x: tx, y: ty, layer });
  markDirty();
}

function eraseTile(wx, wy) {
  const { tx, ty } = snapToTile(wx, wy);
  for (const key of [...state.tiles.keys()]) {
    const t = state.tiles.get(key);
    if (t.x === tx && t.y === ty) state.tiles.delete(key);
  }
  markDirty();
}

// ── Hit detection helpers ─────────────────────────────────────
// Footprints mirror the in-game sizes (markers anchor at the cell corner;
// checkpoints/swap-triggers are 1×3 centered on the cell; boxes are the
// kiosk prefab: 2 wide, 1.6 up / 2.1 down from pivot).
const MARKER_RADIUS = 1.2;
function hitMarker(wx, wy) {
  function near(m) { return (m.x - wx)**2 + (m.y - wy)**2 <= MARKER_RADIUS**2; }
  for (let i = state.spawns.length - 1; i >= 0; i--) if (near(state.spawns[i])) return { markerType: 'spawn', index: i };
  for (let i = state.gates.length  - 1; i >= 0; i--) if (near(state.gates[i]))  return { markerType: 'gate',  index: i };
  for (let i = state.ends.length   - 1; i >= 0; i--) if (near(state.ends[i]))   return { markerType: 'end',   index: i };
  for (let i = state.uis.length    - 1; i >= 0; i--) if (near(state.uis[i]))    return { markerType: 'ui',    index: i };
  return null;
}

function hitBox(wx, wy) {
  for (let i = state.boxes.length - 1; i >= 0; i--) {
    const b = state.boxes[i];
    if (wx >= b.x - 1 && wx < b.x + 1 && wy >= b.y - 2.1 && wy < b.y + 1.6) return i;
  }
  return -1;
}

function hitCheckpoint(wx, wy) {
  for (let i = state.checkpoints.length - 1; i >= 0; i--) {
    const c = state.checkpoints[i];
    if (wx >= c.x && wx < c.x + 1 && wy >= c.y - 1 && wy < c.y + 2) return i;
  }
  return -1;
}

function hitSwaptrig(wx, wy) {
  for (let i = state.swapTriggers.length - 1; i >= 0; i--) {
    const s = state.swapTriggers[i];
    if (wx >= s.x && wx < s.x + 1 && wy >= s.y - 1 && wy < s.y + 2) return i;
  }
  return -1;
}

function getItemArray(item) {
  if (item.type === 'marker') return state[item.markerType + 's'];
  const map = { image: state.images, hitbox: state.hitboxes, box: state.boxes,
                checkpoint: state.checkpoints, swaptrig: state.swapTriggers };
  return map[item.type];
}

function itemsInMarquee(mx0, my0, mx1, my1) {
  const x0 = Math.min(mx0, mx1), x1 = Math.max(mx0, mx1);
  const y0 = Math.min(my0, my1), y1 = Math.max(my0, my1);
  const items = [];
  state.boxes.forEach((b, i) => {
    if (b.x + 1 > x0 && b.x - 1 < x1 && b.y + 1.6 > y0 && b.y - 2.1 < y1) items.push({ type: 'box', index: i });
  });
  state.checkpoints.forEach((c, i) => {
    if (c.x + 1 > x0 && c.x < x1 && c.y + 2 > y0 && c.y - 1 < y1) items.push({ type: 'checkpoint', index: i });
  });
  state.swapTriggers.forEach((s, i) => {
    if (s.x + 1 > x0 && s.x < x1 && s.y + 2 > y0 && s.y - 1 < y1) items.push({ type: 'swaptrig', index: i });
  });
  state.hitboxes.forEach((h, i) => {
    if (h.x + h.w > x0 && h.x < x1 && h.y + h.h > y0 && h.y < y1) items.push({ type: 'hitbox', index: i });
  });
  state.images.forEach((img, i) => {
    const hw = img.w / 2, hh = img.h / 2;
    if (img.x + hw > x0 && img.x - hw < x1 && img.y + hh > y0 && img.y - hh < y1) items.push({ type: 'image', index: i });
  });
  [['spawns','spawn'],['gates','gate'],['ends','end'],['uis','ui']].forEach(([arr, t]) => {
    state[arr].forEach((m, i) => {
      if (m.x >= x0 && m.x <= x1 && m.y >= y0 && m.y <= y1)
        items.push({ type: 'marker', markerType: t, index: i });
    });
  });
  for (const [key, t] of state.tiles) {
    if (t.x + 1 > x0 && t.x < x1 && t.y + 1 > y0 && t.y < y1)
      items.push({ type: 'tile', key });
  }
  return items;
}

function hitAnyMultiSel(wx, wy) {
  for (const item of state.multiSel) {
    if (item.type === 'image') {
      const img = state.images[item.index];
      if (img && wx >= img.x - img.w/2 && wx <= img.x + img.w/2 && wy >= img.y - img.h/2 && wy <= img.y + img.h/2) return true;
    } else if (item.type === 'hitbox') {
      const h = state.hitboxes[item.index];
      if (h && wx >= h.x && wx <= h.x + h.w && wy >= h.y && wy <= h.y + h.h) return true;
    } else if (item.type === 'box') {
      const b = state.boxes[item.index];
      if (b && wx >= b.x - 1 && wx < b.x + 1 && wy >= b.y - 2.1 && wy < b.y + 1.6) return true;
    } else if (item.type === 'checkpoint') {
      const c = state.checkpoints[item.index];
      if (c && wx >= c.x && wx < c.x + 1 && wy >= c.y - 1 && wy < c.y + 2) return true;
    } else if (item.type === 'swaptrig') {
      const s = state.swapTriggers[item.index];
      if (s && wx >= s.x && wx < s.x + 1 && wy >= s.y - 1 && wy < s.y + 2) return true;
    } else if (item.type === 'marker') {
      const arr = state[item.markerType + 's'];
      const m = arr?.[item.index];
      if (m && (m.x - wx)**2 + (m.y - wy)**2 <= MARKER_RADIUS**2) return true;
    } else if (item.type === 'tile') {
      const t = state.tiles.get(item.key);
      if (t && wx >= t.x && wx < t.x + 1 && wy >= t.y && wy < t.y + 1) return true;
    }
  }
  return false;
}

// ── Hit detection ─────────────────────────────────────────────
function hitImage(wx, wy) {
  // Check reverse order (topmost first)
  const sorted = state.images.map((img, idx) => ({ img, idx }));
  sorted.sort((a, b) => b.img.depth - a.img.depth);

  for (const { img, idx } of sorted) {
    // Rotate the world point into image-local coords
    const ang = -img.rot * Math.PI / 180;
    const dx  = wx - img.x, dy = wy - img.y;
    const lx  = dx * Math.cos(-ang) - dy * Math.sin(-ang);
    const ly  = dx * Math.sin(-ang) + dy * Math.cos(-ang);
    if (Math.abs(lx) <= img.w / 2 && Math.abs(ly) <= img.h / 2) return idx;
  }
  return -1;
}

function hitHitbox(wx, wy) {
  for (let i = state.hitboxes.length - 1; i >= 0; i--) {
    const h = state.hitboxes[i];
    if (wx >= h.x && wx <= h.x + h.w && wy >= h.y && wy <= h.y + h.h) return i;
  }
  return -1;
}

function nearestMarker(wx, wy, list) {
  let best = -1, bestD = Infinity;
  for (let i = 0; i < list.length; i++) {
    const d = (list[i].x - wx) ** 2 + (list[i].y - wy) ** 2;
    if (d < bestD) { bestD = d; best = i; }
  }
  return best;
}

// ── Mouse down ────────────────────────────────────────────────
export function onMouseDown(e, sx, sy) {
  const { wx, wy } = screenToWorld(sx, sy);
  const tool = state.activeTool;
  const isRight = e.button === 2;

  // ── Pan (MMB or Space+LMB) ───────────────────────────────
  if (e.button === 1 || (e.button === 0 && e.getModifierState('Space'))) {
    state.isPanning = true;
    state.panStartMouse = { mx: sx, my: sy };
    state.panStartPan   = { panX: state.panX, panY: state.panY };
    return;
  }

  if (e.button !== 0 && !isRight) return;

  // ── Tile tools ────────────────────────────────────────────
  if (TOOL_LAYER[tool] != null) {
    if (isRight) { pushSnapshot(); eraseTile(wx, wy); }
    else         { paintTile(wx, wy); state.isPainting = true; }
    return;
  }
  if (tool === 'erase') {
    pushSnapshot(); eraseAll(wx, wy); return;
  }

  // ── Pointer / select ──────────────────────────────────────
  if (tool === 'pointer') {
    if (isRight) return;

    // If click lands inside existing multi-selection → start moving it
    if (state.multiSel.length > 0 && hitAnyMultiSel(wx, wy)) {
      state.multiSelDragStart = { wx, wy };
      state.multiSelOrigPos = state.multiSel.map(item => {
        if (item.type === 'tile') {
          const t = state.tiles.get(item.key);
          return t ? { x: t.x, y: t.y, layer: t.layer, rot: t.rot ?? 0, sprite: t.sprite ?? '' }
                   : { x: 0, y: 0, layer: 0, rot: 0, sprite: '' };
        }
        const arr = getItemArray(item);
        return { x: arr[item.index].x, y: arr[item.index].y };
      });
      state.multiSelTileDelta = { dx: 0, dy: 0 };
      markDirty(); return;
    }

    // Single-item hit test
    const imgIdx    = hitImage(wx, wy);
    const hbIdx     = hitHitbox(wx, wy);
    const markerHit = hitMarker(wx, wy);
    const boxIdx    = hitBox(wx, wy);
    const cpIdx     = hitCheckpoint(wx, wy);
    const swIdx     = hitSwaptrig(wx, wy);

    state.multiSel = [];
    // Smaller footprints take priority over the large box kiosk so overlapping
    // checkpoints/swap-triggers stay clickable.
    if      (imgIdx >= 0)  state.selection = { type: 'image',      index: imgIdx };
    else if (hbIdx  >= 0)  state.selection = { type: 'hitbox',     index: hbIdx  };
    else if (markerHit)    state.selection = { type: 'marker', markerType: markerHit.markerType, index: markerHit.index };
    else if (cpIdx  >= 0)  state.selection = { type: 'checkpoint', index: cpIdx  };
    else if (swIdx  >= 0)  state.selection = { type: 'swaptrig',   index: swIdx  };
    else if (boxIdx >= 0)  state.selection = { type: 'box',        index: boxIdx };
    else {
      // Nothing hit → start marquee
      state.selection = null;
      state.marquee   = { x0: wx, y0: wy, x1: wx, y1: wy };
      markDirty(); return;
    }

    // Start drag for the selected item
    const sel = state.selection;
    state.isDragging     = true;
    state.dragStartWorld = { wx, wy };
    if (sel.type === 'image') {
      const img = state.images[sel.index];
      state.dragOrigPos = { x: img.x, y: img.y };
    } else if (sel.type === 'hitbox') {
      const h = state.hitboxes[sel.index];
      state.dragOrigPos = { x: h.x, y: h.y };
    } else {
      const arr = getItemArray(sel);
      state.dragOrigPos = { x: arr[sel.index].x, y: arr[sel.index].y };
    }

    refreshProperties();
    markDirty();
    return;
  }

  // ── Marker tools ──────────────────────────────────────────
  const { tx, ty } = snapToTile(wx, wy);

  if (['gate','end','spawn','ui'].includes(tool)) {
    if (isRight) {
      const arr = state[tool + 's'];
      const i = nearestMarker(wx, wy, arr);
      if (i >= 0) { pushSnapshot(); arr.splice(i, 1); markDirty(); }
    } else {
      pushSnapshot();
      // Spawn is stored at the cell CENTER (both axes): the player's transform
      // pivot is its body center in-game, so this puts the player inside the
      // clicked tile with feet flush on the tile below (corner-anchoring
      // embeds the lower half in the floor → squish-death loop).
      const isSpawn = tool === 'spawn';
      state[tool + 's'].push({ x: isSpawn ? tx + 0.5 : tx, y: isSpawn ? ty + 0.5 : ty, group: state.activeGroup });
      markDirty();
    }
    return;
  }
  if (isRight) return;

  if (tool === 'swaptrig') {
    pushSnapshot();
    state.swapTriggers.push({ x: tx, y: ty });
    markDirty(); return;
  }
  if (tool === 'checkpoint') {
    pushSnapshot();
    state.checkpoints.push({ x: tx, y: ty });
    markDirty(); return;
  }
  if (tool === 'box') {
    pushSnapshot();
    state.boxes.push({ x: tx, y: ty, upgrade: state.boxUpgrade, cap: state.boxCap, formula: state.boxFormula, prices: computePrices(state.boxFormula, state.boxCap) });
    markDirty(); return;
  }

  // ── HitBox ────────────────────────────────────────────────
  if (tool === 'hitbox') {
    state.hitboxDrag = { x0: wx, y0: wy };
    markDirty(); return;
  }

  // ── ImageObj ─────────────────────────────────────────────
  if (tool === 'imageobj') {
    if (!state.pendingImagePath) return;
    let px = wx, py = wy;
    if (state.snapMode) { px = Math.floor(wx) + 0.5; py = Math.floor(wy) + 0.5; }
    pushSnapshot();
    state.images.push({
      path:    state.pendingImagePath,
      x: px, y: py,
      w:       state.pendingImageW,
      h:       state.pendingImageH,
      rot:     state.pendingImageRot,
      opacity: state.pendingImageOpacity,
      cr:      state.pendingImageCr,
      cg:      state.pendingImageCg,
      cb:      state.pendingImageCb,
      depth:   state.pendingImageDepth,
    });
    markDirty(); return;
  }
}

// ── Mouse move ────────────────────────────────────────────────
export function onMouseMove(e, sx, sy, buttons) {
  const { wx, wy } = screenToWorld(sx, sy);
  state.ghostPos = { wx, wy };

  // Panning
  if (state.isPanning && state.panStartMouse) {
    const { mx, my }     = state.panStartMouse;
    const { panX, panY } = state.panStartPan;
    const ts = 12 * state.zoom;
    state.panX = panX - (sx - mx) / ts;
    state.panY = panY + (sy - my) / ts;
    markDirty(); return;
  }

  // Tile painting
  if (state.isPainting && buttons & 1) {
    paintTile(wx, wy);
    return;
  }
  if (state.activeTool === 'erase' && buttons & 1) {
    eraseAll(wx, wy); return;
  }

  // Marquee draw
  if (state.marquee && buttons & 1) {
    state.marquee.x1 = wx; state.marquee.y1 = wy;
    markDirty(); return;
  }

  // Multi-select drag
  if (state.multiSelDragStart && buttons & 1) {
    const dx = wx - state.multiSelDragStart.wx;
    const dy = wy - state.multiSelDragStart.wy;

    // Tile items move on integer grid — batch: remove all first, then add all
    const newIntDx = Math.round(dx);
    const newIntDy = Math.round(dy);
    const { dx: prevDx, dy: prevDy } = state.multiSelTileDelta;
    if (newIntDx !== prevDx || newIntDy !== prevDy) {
      const tileMoves = [];
      for (let i = 0; i < state.multiSel.length; i++) {
        const item = state.multiSel[i];
        if (item.type !== 'tile') continue;
        const orig = state.multiSelOrigPos[i];
        tileMoves.push({
          item, orig,
          removeKey: `${orig.x + prevDx},${orig.y + prevDy},${orig.layer}`,
          nx: orig.x + newIntDx, ny: orig.y + newIntDy,
        });
      }
      for (const { removeKey } of tileMoves) state.tiles.delete(removeKey);
      for (const { item, orig, nx, ny } of tileMoves) {
        const newKey = `${nx},${ny},${orig.layer}`;
        state.tiles.set(newKey, { x: nx, y: ny, layer: orig.layer, rot: orig.rot ?? 0, sprite: orig.sprite ?? '' });
        item.key = newKey;
      }
      state.multiSelTileDelta = { dx: newIntDx, dy: newIntDy };
    }

    // Non-tile items
    for (let i = 0; i < state.multiSel.length; i++) {
      const item = state.multiSel[i];
      if (item.type === 'tile') continue;
      const arr  = getItemArray(item);
      const orig = state.multiSelOrigPos[i];
      if (item.type === 'image') {
        arr[item.index].x = orig.x + dx;
        arr[item.index].y = orig.y + dy;
      } else if (item.type === 'marker' && item.markerType === 'spawn') {
        // Spawns live at the cell center — keep the half-tile offset while dragging
        arr[item.index].x = Math.floor(orig.x + dx) + 0.5;
        arr[item.index].y = Math.floor(orig.y + dy) + 0.5;
      } else {
        arr[item.index].x = Math.floor(orig.x + dx);
        arr[item.index].y = Math.floor(orig.y + dy);
      }
    }
    markDirty(); return;
  }

  // Single-item pointer drag
  if (state.isDragging && buttons & 1 && state.selection) {
    const dx = wx - state.dragStartWorld.wx;
    const dy = wy - state.dragStartWorld.wy;
    const sel = state.selection;
    if (sel.type === 'image') {
      let nx = state.dragOrigPos.x + dx;
      let ny = state.dragOrigPos.y + dy;
      if (state.snapMode) { nx = Math.floor(nx) + 0.5; ny = Math.floor(ny) + 0.5; }
      state.images[sel.index].x = nx;
      state.images[sel.index].y = ny;
    } else if (sel.type === 'hitbox') {
      let nx = state.dragOrigPos.x + dx;
      let ny = state.dragOrigPos.y + dy;
      if (state.snapMode) { nx = Math.floor(nx); ny = Math.floor(ny); }
      state.hitboxes[sel.index].x = nx;
      state.hitboxes[sel.index].y = ny;
    } else {
      const arr = getItemArray(sel);
      if (arr[sel.index]) {
        const isSpawn = sel.type === 'marker' && sel.markerType === 'spawn';
        arr[sel.index].x = Math.floor(state.dragOrigPos.x + dx) + (isSpawn ? 0.5 : 0);
        arr[sel.index].y = Math.floor(state.dragOrigPos.y + dy) + (isSpawn ? 0.5 : 0);
      }
    }
    refreshProperties();
    markDirty(); return;
  }

  markDirty(); // re-render for ghost update
}

// ── Mouse up ──────────────────────────────────────────────────
export function onMouseUp(e, sx, sy) {
  const { wx, wy } = screenToWorld(sx, sy);

  if (state.isPanning) {
    state.isPanning     = false;
    state.panStartMouse = null;
    state.panStartPan   = null;
    return;
  }

  if (state.isPainting) {
    pushSnapshot();
    state.isPainting = false;
    return;
  }

  // Finalize marquee
  if (state.marquee) {
    const { x0, y0, x1, y1 } = state.marquee;
    const dx = Math.abs(x1 - x0), dy = Math.abs(y1 - y0);
    if (dx > 0.2 || dy > 0.2) {
      state.multiSel = itemsInMarquee(x0, y0, x1, y1);
    } else {
      state.multiSel = [];
    }
    state.marquee = null;
    refreshProperties();
    markDirty(); return;
  }

  // Finalize multi-select drag
  if (state.multiSelDragStart) {
    pushSnapshot();
    state.multiSelDragStart  = null;
    state.multiSelOrigPos    = [];
    state.multiSelTileDelta  = { dx: 0, dy: 0 };
    return;
  }

  if (state.isDragging) {
    pushSnapshot();
    state.isDragging    = false;
    state.dragStartWorld = null;
    state.dragOrigPos    = null;
    return;
  }

  if (state.activeTool === 'hitbox' && state.hitboxDrag) {
    const { x0, y0 } = state.hitboxDrag;
    let hx, hy, hw, hh;
    if (state.snapMode) {
      hx = Math.floor(Math.min(x0, wx));
      hy = Math.floor(Math.min(y0, wy));
      hw = Math.floor(Math.max(x0, wx)) + 1 - hx;
      hh = Math.floor(Math.max(y0, wy)) + 1 - hy;
    } else {
      hx = Math.min(x0, wx); hy = Math.min(y0, wy);
      hw = Math.abs(wx - x0); hh = Math.abs(wy - y0);
      if (hw < 0.05 && hh < 0.05) {
        hx = Math.floor(wx); hy = Math.floor(wy); hw = 1; hh = 1;
      }
    }
    if (hw > 0 && hh > 0) {
      pushSnapshot();
      state.hitboxes.push({ x: hx, y: hy, w: hw, h: hh });
    }
    state.hitboxDrag = null;
    markDirty();
  }
}

// ── Right-click erase for multi-markers ───────────────────────
export function onContextMenu(e, sx, sy) {
  const { wx, wy } = screenToWorld(sx, sy);
  const tool = state.activeTool;
  if (tool === 'swaptrig') {
    const i = nearestMarker(wx, wy, state.swapTriggers);
    if (i >= 0) { pushSnapshot(); state.swapTriggers.splice(i, 1); markDirty(); }
  } else if (tool === 'checkpoint') {
    const i = nearestMarker(wx, wy, state.checkpoints);
    if (i >= 0) { pushSnapshot(); state.checkpoints.splice(i, 1); markDirty(); }
  } else if (['gate','end','spawn','ui'].includes(tool)) {
    const arr = state[tool + 's'];
    const i = nearestMarker(wx, wy, arr);
    if (i >= 0) { pushSnapshot(); arr.splice(i, 1); markDirty(); }
  } else if (tool === 'erase') {
    pushSnapshot(); eraseAll(wx, wy);
  }
}

// ── Erase anything at world pos ───────────────────────────────
function eraseAll(wx, wy) {
  state.multiSel = []; state.selection = null;
  // Images (topmost visual layer)
  const imgIdx = hitImage(wx, wy);
  if (imgIdx >= 0) {
    state.images.splice(imgIdx, 1);
    if (state.selection?.index === imgIdx) state.selection = null;
    markDirty(); return;
  }

  // Hitboxes
  const hbIdx = hitHitbox(wx, wy);
  if (hbIdx >= 0) {
    state.hitboxes.splice(hbIdx, 1);
    if (state.selection?.index === hbIdx) state.selection = null;
    markDirty(); return;
  }

  // Markers, checkpoints, swaptriggers, boxes — check within 1-tile radius
  const RADIUS = 1.0;
  function near(mx, my) {
    return (mx + 0.5 - wx) ** 2 + (my + 0.5 - wy) ** 2 <= RADIUS * RADIUS;
  }

  for (const key of ['spawns','gates','ends','uis']) {
    const i = state[key].findIndex(m => near(m.x, m.y));
    if (i >= 0) { state[key].splice(i, 1); markDirty(); return; }
  }

  for (let i = 0; i < state.checkpoints.length; i++) {
    if (near(state.checkpoints[i].x, state.checkpoints[i].y)) {
      state.checkpoints.splice(i, 1); markDirty(); return;
    }
  }
  for (let i = 0; i < state.swapTriggers.length; i++) {
    if (near(state.swapTriggers[i].x, state.swapTriggers[i].y)) {
      state.swapTriggers.splice(i, 1); markDirty(); return;
    }
  }
  for (let i = 0; i < state.boxes.length; i++) {
    if (near(state.boxes[i].x, state.boxes[i].y)) {
      state.boxes.splice(i, 1); markDirty(); return;
    }
  }

  // Tiles (last resort)
  eraseTile(wx, wy);
}

// ── Delete selected ───────────────────────────────────────────
export function deleteSelected() {
  // Multi-selection takes priority
  if (state.multiSel && state.multiSel.length > 0) {
    pushSnapshot();
    const groups = new Map();
    for (const item of state.multiSel) {
      if (item.type === 'tile') { state.tiles.delete(item.key); continue; }
      const arr = getItemArray(item);
      if (!groups.has(arr)) groups.set(arr, []);
      groups.get(arr).push(item.index);
    }
    for (const [arr, indices] of groups) {
      indices.sort((a, b) => b - a);
      for (const i of indices) arr.splice(i, 1);
    }
    state.multiSel = [];
    refreshProperties();
    markDirty(); return;
  }

  const sel = state.selection;
  if (!sel) return;
  pushSnapshot();
  if (sel.type === 'image')      state.images.splice(sel.index, 1);
  if (sel.type === 'hitbox')     state.hitboxes.splice(sel.index, 1);
  if (sel.type === 'marker')     state[sel.markerType + 's'].splice(sel.index, 1);
  if (sel.type === 'box')        state.boxes.splice(sel.index, 1);
  if (sel.type === 'checkpoint') state.checkpoints.splice(sel.index, 1);
  if (sel.type === 'swaptrig')   state.swapTriggers.splice(sel.index, 1);
  state.selection = null;
  refreshProperties();
  markDirty();
}

// ── Move selected items by (dx, dy) tiles ────────────────────
export function moveSelected(dx, dy) {
  const hasSel   = state.selection;
  const hasMulti = state.multiSel && state.multiSel.length > 0;
  if (!hasSel && !hasMulti) return false;

  pushSnapshot();

  if (hasMulti) {
    // Tiles: batch remove-then-add to avoid key collisions mid-loop
    const tileMoves = [];
    for (const item of state.multiSel) {
      if (item.type !== 'tile') continue;
      const t = state.tiles.get(item.key);
      if (t) tileMoves.push({ item, t });
    }
    for (const { item } of tileMoves) state.tiles.delete(item.key);
    for (const { item, t } of tileMoves) {
      const nx = t.x + dx, ny = t.y + dy;
      const newKey = `${nx},${ny},${t.layer}`;
      state.tiles.set(newKey, { x: nx, y: ny, layer: t.layer, rot: t.rot ?? 0, sprite: t.sprite ?? '' });
      item.key = newKey;
    }
    for (const item of state.multiSel) {
      if (item.type === 'tile') continue;
      const arr = getItemArray(item);
      const obj = arr?.[item.index];
      if (!obj) continue;
      obj.x += dx; obj.y += dy;
    }
  } else {
    const sel = state.selection;
    if (sel.type === 'image') {
      state.images[sel.index].x += dx;
      state.images[sel.index].y += dy;
    } else if (sel.type === 'hitbox') {
      state.hitboxes[sel.index].x += dx;
      state.hitboxes[sel.index].y += dy;
    } else {
      const arr = getItemArray(sel);
      if (arr?.[sel.index]) { arr[sel.index].x += dx; arr[sel.index].y += dy; }
    }
    refreshProperties();
  }

  markDirty();
  return true;
}

// ── Rotate selected image ─────────────────────────────────────
export function rotateSelected(deg) {
  if (state.selection?.type === 'image') {
    const img = state.images[state.selection.index];
    img.rot = ((img.rot + deg + 540) % 360) - 180;
    pushSnapshot();
    refreshProperties();
    markDirty();
  } else {
    // Rotate brush
    state.pendingImageRot = ((state.pendingImageRot + deg + 540) % 360) - 180;
    refreshProperties();
    markDirty();
  }
}
