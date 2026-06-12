// Auto-tiler: turns painted ground blobs into proper structures.
// Border tiles get the edge piece rotated to face the open side, convex
// corners get the corner piece, enclosed tiles get the interior fill.
//
// Packs are CATEGORY-AWARE: blue packs style blue swap blocks (layer 3),
// orange packs style orange swap blocks (layer 4), everything else styles
// basic ground (layers 0/1). Each category is auto-tiled as its own mass.
import { state, markDirty } from './state.js';
import { pushSnapshot }     from './history.js';

export const CAT_LAYERS = {
  basic:  [0, 1],
  blue:   [3],
  orange: [4],
};

function loadCat(cat, defs) {
  try {
    const v = JSON.parse(localStorage.getItem('at_cat_' + cat));
    if (v && v.corner && v.edge && v.fill) return v;
  } catch { }
  return defs;
}

// Per-category piece config (legacy single-config keys migrate into "basic")
export const atConfig = {
  basic: loadCat('basic', {
    corner: localStorage.getItem('at_corner') || 'ground1_tileset_56',
    edge:   localStorage.getItem('at_edge')   || 'ground1_tileset_57',
    fill:   localStorage.getItem('at_fill')   || 'ground1_tileset_10',
  }),
  blue:   loadCat('blue',   { corner: 'blue_ground_tileset_56',   edge: 'blue_ground_tileset_57',   fill: 'blue_ground_tileset_10' }),
  orange: loadCat('orange', { corner: 'orange_ground_tileset_56', edge: 'orange_ground_tileset_57', fill: 'orange_ground_tileset_10' }),
};

// Which category the fine-tune slots edit (follows the last applied pack)
export let atActiveCat = 'basic';
export function setActiveCat(cat) { if (CAT_LAYERS[cat]) atActiveCat = cat; }

function saveCat(cat) { localStorage.setItem('at_cat_' + cat, JSON.stringify(atConfig[cat])); }

// A pack belongs to the category its name/pieces mention
export function packCategory(pack) {
  const s = `${pack.family ?? ''} ${pack.label ?? ''} ${pack.edge ?? ''}`.toLowerCase();
  if (s.includes('blue'))   return 'blue';
  if (s.includes('orange')) return 'orange';
  return 'basic';
}

export function packIsActive(pack) {
  const c = atConfig[packCategory(pack)];
  return c.corner === pack.corner && c.edge === pack.edge && c.fill === pack.fill;
}

export function setPiece(slot, name) {
  atConfig[atActiveCat][slot] = name || '';
  saveCat(atActiveCat);
}

export function applyPack(pack) {
  const cat = packCategory(pack);
  atActiveCat = cat;
  atConfig[cat] = {
    corner: pack.corner, edge: pack.edge, fill: pack.fill,
    // per-piece base-orientation correction: some sheets' pieces aren't
    // top-facing / top-left (e.g. Mossy's bottom-facing grass edges)
    cornerRot: pack.cornerRot | 0, edgeRot: pack.edgeRot | 0, fillRot: pack.fillRot | 0,
  };
  saveCat(cat);
}

// Keep stored rot offsets in sync when pack definitions change
export function syncRotsFromPacks(packs) {
  for (const pack of packs ?? []) {
    if (!packIsActive(pack)) continue;
    const c = atConfig[packCategory(pack)];
    const nr = { cornerRot: pack.cornerRot | 0, edgeRot: pack.edgeRot | 0, fillRot: pack.fillRot | 0 };
    if ((c.cornerRot | 0) !== nr.cornerRot || (c.edgeRot | 0) !== nr.edgeRot || (c.fillRot | 0) !== nr.fillRot) {
      Object.assign(c, nr);
      saveCat(packCategory(pack));
    }
  }
}

// onlyKeys: optional Set of tile-map keys — when given, only those tiles are
// restyled. Neighbor detection per category still uses ALL of that category's
// tiles, so a selection bordering unselected same-color ground connects.
export function applyAutoTile(onlyKeys = null) {
  pushSnapshot();
  let count = 0;

  for (const cat of Object.keys(CAT_LAYERS)) {
    const layers = new Set(CAT_LAYERS[cat]);
    const pieces = atConfig[cat];
    const solid = new Set();
    for (const t of state.tiles.values())
      if (layers.has(t.layer)) solid.add(`${t.x},${t.y}`);
    if (solid.size === 0) continue;
    const has = (x, y) => solid.has(`${x},${y}`);

    for (const [key, t] of state.tiles) {
      if (!layers.has(t.layer)) continue;
      if (onlyKeys && !onlyKeys.has(key)) continue;
      const top    = !has(t.x,     t.y + 1);
      const bottom = !has(t.x,     t.y - 1);
      const left   = !has(t.x - 1, t.y);
      const right  = !has(t.x + 1, t.y);
      const exposed = (top ? 1 : 0) + (bottom ? 1 : 0) + (left ? 1 : 0) + (right ? 1 : 0);

      let sprite, rot;
      const isConvexCorner = exposed === 2 &&
        ((top && left) || (top && right) || (bottom && left) || (bottom && right));

      if (exposed === 0) {
        sprite = pieces.fill;
        rot    = (pieces.fillRot | 0) % 360;
      } else if (isConvexCorner && pieces.corner) {
        sprite = pieces.corner;                       // canonical orientation: top-left
        rot    = ((top && left) ? 0 : (bottom && left) ? 90 : (bottom && right) ? 180 : 270);
        rot    = (rot + (pieces.cornerRot | 0)) % 360;
      } else {
        sprite = pieces.edge;                         // canonical orientation: top
        rot    = (top ? 0 : left ? 90 : right ? 270 : 180);
        rot    = (rot + (pieces.edgeRot | 0)) % 360;
      }

      t.sprite = sprite || '';
      t.rot    = (exposed === 0 && !sprite) ? 0 : rot;
      count++;
    }
  }

  state.unsaved = true;
  markDirty();
  return count;
}

export function clearAutoTile(onlyKeys = null) {
  pushSnapshot();
  for (const [key, t] of state.tiles) {
    if (onlyKeys && !onlyKeys.has(key)) continue;
    t.rot = 0; t.sprite = '';
  }
  state.unsaved = true;
  markDirty();
}
