import { state }                    from './state.js';
import { tileSize, worldToScreen }   from './coords.js';
import { getImage, getTileCanvas }   from './sprites.js';

// ── Group color palette ───────────────────────────────────────
export const GROUP_PALETTE = [
  { fill: '#0891b2', stroke: '#22d3ee' }, // 0 cyan
  { fill: '#7c3aed', stroke: '#a78bfa' }, // 1 violet
  { fill: '#16a34a', stroke: '#4ade80' }, // 2 green
  { fill: '#b45309', stroke: '#fbbf24' }, // 3 amber
  { fill: '#be185d', stroke: '#f472b6' }, // 4 pink
  { fill: '#0d9488', stroke: '#2dd4bf' }, // 5 teal
  { fill: '#dc2626', stroke: '#f87171' }, // 6 red
  { fill: '#1d4ed8', stroke: '#60a5fa' }, // 7 blue
];
export function groupColor(g) { return GROUP_PALETTE[((g ?? 0) % GROUP_PALETTE.length + GROUP_PALETTE.length) % GROUP_PALETTE.length]; }

// ── Tile layer colors
const LAYER_COLOR = {
  0: '#4a7c3a',   // Ground  – green
  2: '#9ca3af',   // Spike   – gray
  3: '#3b6fd4',   // Blue block
  4: '#d47a2b',   // Orange block
  5: '#5577cc',   // Blue spike
  6: '#cc8833',   // Orange spike
};
const LAYER_BORDER = {
  0: '#2d5c22', 2: '#6b7280', 3: '#1d4ed8', 4: '#b45309', 5: '#3352aa', 6: '#a06420',
};


export function initCanvas(canvasEl) {
  state.canvas = canvasEl;
  state.ctx    = canvasEl.getContext('2d');

  function resize() {
    const w = canvasEl.offsetWidth  || canvasEl.parentElement?.clientWidth  || 800;
    const h = canvasEl.offsetHeight || canvasEl.parentElement?.clientHeight || 600;
    if (canvasEl.width !== w || canvasEl.height !== h) {
      canvasEl.width  = w;
      canvasEl.height = h;
      state.dirtyRender = true;
    }
  }

  const ro = new ResizeObserver(resize);
  ro.observe(canvasEl);
  ro.observe(canvasEl.parentElement);
  // Fallback: force resize after a frame in case layout isn't ready yet
  requestAnimationFrame(() => { resize(); state.dirtyRender = true; });
  resize();
}

export function startRenderLoop() {
  function loop() {
    if (state.dirtyRender) {
      state.dirtyRender = false;
      render();
    }
    requestAnimationFrame(loop);
  }
  requestAnimationFrame(loop);
}

function render() {
  const { ctx, canvas } = state;
  if (!ctx || !canvas || canvas.width === 0 || canvas.height === 0) return;
  const W = canvas.width, H = canvas.height;
  const ts = tileSize();

  ctx.clearRect(0, 0, W, H);

  // ── 1. Background fill ──────────────────────────────────────
  ctx.fillStyle = '#0f172a';
  ctx.fillRect(0, 0, W, H);

  // ── 2. Grid ─────────────────────────────────────────────────
  if (ts >= 4) drawGrid(ts, W, H);

  // ── 3. Images depth < 0 ─────────────────────────────────────
  const sorted = [...state.images].map((img, idx) => ({ img, idx }));
  sorted.sort((a, b) => a.img.depth - b.img.depth);

  for (const { img, idx } of sorted) {
    if (img.depth < 0) drawImage(img, idx, ts);
  }

  // ── 4. Tiles ────────────────────────────────────────────────
  for (const t of state.tiles.values()) drawTile(t, ts);

  // ── 5. Images depth >= 0 ────────────────────────────────────
  for (const { img, idx } of sorted) {
    if (img.depth >= 0) drawImage(img, idx, ts);
  }

  // ── 6. Hitboxes ─────────────────────────────────────────────
  for (let i = 0; i < state.hitboxes.length; i++) drawHitbox(state.hitboxes[i], i, ts);

  // ── 7. Markers ──────────────────────────────────────────────
  drawMarkers(ts);

  // ── 8. Selection / multi-select / marquee ───────────────────
  drawSelection(ts);
  drawMultiSelect(ts);
  drawMarquee();

  // ── 9. Ghost / preview ──────────────────────────────────────
  drawGhost(ts);

  // ── 10. HUD ─────────────────────────────────────────────────
  drawHUD(W, H);
}

// ── Grid ──────────────────────────────────────────────────────
function drawGrid(ts, W, H) {
  const ctx = state.ctx;
  ctx.save();
  ctx.strokeStyle = 'rgba(55,95,145,0.35)';
  ctx.lineWidth   = 0.5;
  ctx.beginPath();

  // Visible world range
  const x0 = Math.floor(state.panX - (W / 2) / ts) - 1;
  const x1 = Math.ceil( state.panX + (W / 2) / ts) + 1;
  const y0 = Math.floor(state.panY - (H / 2) / ts) - 1;
  const y1 = Math.ceil( state.panY + (H / 2) / ts) + 1;

  for (let tx = x0; tx <= x1; tx++) {
    const { sx } = worldToScreen(tx, 0);
    ctx.moveTo(sx, 0); ctx.lineTo(sx, H);
  }
  for (let ty = y0; ty <= y1; ty++) {
    const { sy } = worldToScreen(0, ty);
    ctx.moveTo(0, sy); ctx.lineTo(W, sy);
  }
  ctx.stroke();
  ctx.restore();
}

// ── Tile ──────────────────────────────────────────────────────
// Tileset piece sprites live in the sprite dump under "<runtime name>.png"
function tilesetFileFor(name) {
  return name ? name + '.png' : null;
}

function drawTile(t, ts) {
  const ctx = state.ctx;
  // Tile bottom-left is world (t.x, t.y); top is (t.x, t.y+1)
  const { sx, sy } = worldToScreen(t.x, t.y + 1);  // screen top-left

  // Per-tile sprite override (auto-tiled pieces) — drawn with rotation
  const ovFile = t.sprite ? tilesetFileFor(t.sprite) : null;
  const ovImg  = ovFile ? getImage(ovFile) : null;
  const baseImg = ovImg || getTileCanvas(t.layer);

  if (baseImg) {
    const rot = ((t.rot ?? 0) % 360 + 360) % 360;
    if (rot !== 0) {
      ctx.save();
      ctx.translate(sx + ts / 2, sy + ts / 2);
      ctx.rotate(-rot * Math.PI / 180);   // CCW positive, matching Unity z-rotation
      ctx.drawImage(baseImg, -ts / 2, -ts / 2, ts, ts);
      ctx.restore();
    } else {
      ctx.drawImage(baseImg, sx, sy, ts, ts);
    }
    // Subtle edge to help distinguish adjacent tiles
    ctx.strokeStyle = 'rgba(0,0,0,0.18)';
    ctx.lineWidth   = 0.5;
    ctx.strokeRect(sx + 0.25, sy + 0.25, ts - 0.5, ts - 0.5);
    return;
  }

  // Fallback: solid color + spike triangle
  const fill   = LAYER_COLOR[t.layer]  || '#888';
  const stroke = LAYER_BORDER[t.layer] || '#555';
  ctx.fillStyle   = fill;
  ctx.fillRect(sx, sy, ts, ts);
  ctx.strokeStyle = stroke;
  ctx.lineWidth   = 0.5;
  ctx.strokeRect(sx, sy, ts, ts);

  if (t.layer === 2 || t.layer === 5 || t.layer === 6) {
    ctx.save();
    ctx.fillStyle = (t.layer === 5) ? '#93c5fd' : (t.layer === 6) ? '#fcd34d' : '#e5e7eb';
    ctx.beginPath();
    ctx.moveTo(sx + ts * 0.5, sy + 2);
    ctx.lineTo(sx + ts - 2,   sy + ts - 2);
    ctx.lineTo(sx + 2,        sy + ts - 2);
    ctx.closePath();
    ctx.fill();
    ctx.restore();
  }
}

// ── Image ─────────────────────────────────────────────────────
function drawImage(img, idx, ts, alpha = 1) {
  const ctx  = state.ctx;
  const imgEl = getImage(img.path);
  if (!imgEl && !img.path) return;

  const { sx, sy } = worldToScreen(img.x, img.y);
  const pw = img.w * ts;
  const ph = img.h * ts;

  ctx.save();
  ctx.translate(sx, sy);
  ctx.rotate(-img.rot * Math.PI / 180);
  ctx.globalAlpha = (img.opacity ?? 1) * alpha;

  if (imgEl) {
    ctx.drawImage(imgEl, -pw / 2, -ph / 2, pw, ph);
    // Color tint via multiply + destination-in
    if ((img.cr !== 1 || img.cg !== 1 || img.cb !== 1) &&
        img.cr != null) {
      const off = new OffscreenCanvas(Math.ceil(pw), Math.ceil(ph));
      const oc  = off.getContext('2d');
      oc.drawImage(imgEl, 0, 0, off.width, off.height);
      oc.globalCompositeOperation = 'multiply';
      oc.fillStyle = `rgb(${(img.cr * 255) | 0},${(img.cg * 255) | 0},${(img.cb * 255) | 0})`;
      oc.fillRect(0, 0, off.width, off.height);
      oc.globalCompositeOperation = 'destination-in';
      oc.drawImage(imgEl, 0, 0, off.width, off.height);
      // Re-draw tinted result
      ctx.clearRect(-pw / 2, -ph / 2, pw, ph);
      ctx.drawImage(off, -pw / 2, -ph / 2, pw, ph);
    }
  } else {
    // Placeholder while loading
    ctx.fillStyle = 'rgba(96,96,128,0.6)';
    ctx.fillRect(-pw / 2, -ph / 2, pw, ph);
    ctx.strokeStyle = '#6366f1';
    ctx.lineWidth = 1;
    ctx.strokeRect(-pw / 2, -ph / 2, pw, ph);
  }

  ctx.restore();
}

// ── Hitbox ────────────────────────────────────────────────────
function drawHitbox(h, idx, ts, alpha = 1) {
  const ctx = state.ctx;
  const sel = state.selection?.type === 'hitbox' && state.selection?.index === idx;
  const { sx: x0, sy: y0 } = worldToScreen(h.x,       h.y + h.h);
  const { sx: x1, sy: y1 } = worldToScreen(h.x + h.w, h.y);
  const w = x1 - x0, ht = y1 - y0;

  ctx.save();
  ctx.globalAlpha = alpha;
  ctx.fillStyle   = sel ? 'rgba(255,100,100,0.35)' : 'rgba(255,60,60,0.2)';
  ctx.fillRect(x0, y0, w, ht);
  ctx.strokeStyle = sel ? '#ff6666' : '#ff4444';
  ctx.lineWidth   = sel ? 2 : 1;
  ctx.strokeRect(x0, y0, w, ht);

  if (sel && tileSize() >= 8) {
    ctx.fillStyle = '#ff9999';
    ctx.font      = '10px system-ui';
    ctx.fillText(`${h.w.toFixed(1)}×${h.h.toFixed(1)}`, x0 + 3, y0 + 12);
  }
  ctx.restore();
}

// ── In-game footprints (tile units, relative to the stored entry coords) ──
// All markers are anchored at the cell CORNER in-game (TileToWorld adds no
// half-cell offset); checkpoints/swap-triggers are centered on the cell.
export const BOX_EXTENT = { left: 1, right: 1, up: 1.6, down: 2.1 }; // upgradeBox prefab, measured in-game
export const UI_EXTENT  = { halfW: 3, halfH: 1.3 };                  // course HUD panel ≈ 6×2.6
const BEAM_W = 0.125;                                                 // AddVerticalBeam: 8 world px / 64-unit cell

// Gate/End beam vertical extent — replicates ActivateCourse: bottom = lowest
// tile row, top = max(firstTile.y, highest box y) + 1.
function beamExtent() {
  const first = state.tiles.values().next().value;
  if (!first) return null;
  let minY = first.y;
  for (const t of state.tiles.values()) if (t.y < minY) minY = t.y;
  let topY = first.y + 1;
  for (const b of state.boxes) if (b.y + 1 > topY) topY = b.y + 1;
  return { minY, topY };
}

// ── Markers ───────────────────────────────────────────────────
function drawMarkers(ts) {
  const ctx = state.ctx;
  const r   = Math.max(5, ts * 0.4);
  const showLabel = ts >= 10;

  ctx.save();

  // Draw link lines between same-group gate↔end (solid) and spawn→gate (dashed)
  if (ts >= 3) {
    const groups = new Set([
      ...state.gates.map(m => m.group),
      ...state.ends.map(m => m.group),
      ...state.spawns.map(m => m.group),
    ]);
    for (const g of groups) {
      const col = groupColor(g);
      const gGates  = state.gates.filter(m => m.group === g);
      const gEnds   = state.ends.filter(m => m.group === g);
      const gSpawns = state.spawns.filter(m => m.group === g);
      // Gate ↔ End (solid)
      for (const gate of gGates) {
        for (const end of gEnds) {
          const a = worldToScreen(gate.x, gate.y);
          const b = worldToScreen(end.x,  end.y);
          ctx.strokeStyle = col.stroke + '99';
          ctx.lineWidth   = 1.5;
          ctx.setLineDash([]);
          ctx.beginPath(); ctx.moveTo(a.sx, a.sy); ctx.lineTo(b.sx, b.sy); ctx.stroke();
        }
      }
      // Spawn → nearest gate (dashed)
      for (const sp of gSpawns) {
        if (gGates.length === 0) continue;
        const nearest = gGates.reduce((best, g2) => {
          const d = (g2.x - sp.x)**2 + (g2.y - sp.y)**2;
          return d < best.d ? { g: g2, d } : best;
        }, { g: gGates[0], d: Infinity }).g;
        const a = worldToScreen(sp.x,      sp.y);
        const b = worldToScreen(nearest.x, nearest.y);
        ctx.strokeStyle = col.stroke + '66';
        ctx.lineWidth   = 1;
        ctx.setLineDash([4, 4]);
        ctx.beginPath(); ctx.moveTo(a.sx, a.sy); ctx.lineTo(b.sx, b.sy); ctx.stroke();
      }
      ctx.setLineDash([]);
    }
  }

  // Gate / End — full-height vertical beams exactly as the game spawns them
  const ext = beamExtent();
  if (ext) {
    const beam = (wx, fill) => {
      const a = worldToScreen(wx - BEAM_W / 2, ext.topY);
      const b = worldToScreen(wx + BEAM_W / 2, ext.minY);
      ctx.fillStyle = fill;
      ctx.fillRect(a.sx, a.sy, Math.max(2, b.sx - a.sx), b.sy - a.sy);
    };
    for (const m of state.gates) beam(m.x, 'rgba(51,255,51,0.85)');
    for (const m of state.ends)  beam(m.x, 'rgba(255,51,25,0.85)');
  }

  // Spawn — player-sized box CENTERED on the anchor (the player's transform
  // pivot is its body center in-game)
  for (const m of state.spawns) {
    const col = groupColor(m.group ?? 0);
    const a = worldToScreen(m.x - 0.5, m.y + 0.5);
    const b = worldToScreen(m.x + 0.5, m.y - 0.5);
    const w = b.sx - a.sx, h = b.sy - a.sy;
    ctx.fillStyle   = col.fill + '88';
    ctx.fillRect(a.sx, a.sy, w, h);
    ctx.strokeStyle = col.stroke;
    ctx.lineWidth   = 1.5;
    ctx.setLineDash([3, 2]);
    ctx.strokeRect(a.sx, a.sy, w, h);
    ctx.setLineDash([]);
    // exact anchor point (the player's feet)
    const p = worldToScreen(m.x, m.y);
    ctx.fillStyle = col.stroke;
    ctx.beginPath(); ctx.arc(p.sx, p.sy, 3, 0, Math.PI * 2); ctx.fill();
    if (showLabel && h > 12) {
      ctx.fillStyle = '#fff'; ctx.font = '8px system-ui';
      ctx.textAlign = 'center'; ctx.textBaseline = 'middle';
      ctx.fillText('S' + ((m.group ?? 0) > 0 ? m.group : ''), a.sx + w / 2, a.sy + h / 2);
      ctx.textBaseline = 'alphabetic';
    }
  }

  // UI marker — course HUD panel outline, centered on the anchor point
  for (const m of state.uis) {
    const a = worldToScreen(m.x - UI_EXTENT.halfW, m.y + UI_EXTENT.halfH);
    const b = worldToScreen(m.x + UI_EXTENT.halfW, m.y - UI_EXTENT.halfH);
    const w = b.sx - a.sx, h = b.sy - a.sy;
    ctx.fillStyle   = 'rgba(45,48,58,0.55)';
    ctx.fillRect(a.sx, a.sy, w, h);
    ctx.strokeStyle = '#94a3b8';
    ctx.lineWidth   = 1;
    ctx.strokeRect(a.sx, a.sy, w, h);
    if (showLabel && h > 16) {
      ctx.fillStyle = '#4ade80'; ctx.font = `${Math.max(8, ts * 0.35) | 0}px monospace`;
      ctx.textAlign = 'center'; ctx.textBaseline = 'middle';
      ctx.fillText('Reward HUD', a.sx + w / 2, a.sy + h / 2);
    }
  }

  // Small handle glyphs at the exact anchor point (cell corner, like in-game)
  function drawMarkerShape(wx, wy, group, shape, label, idx) {
    const { sx, sy } = worldToScreen(wx, wy);
    const col = groupColor(group);
    const rf = r;
    ctx.fillStyle   = col.fill;
    ctx.strokeStyle = col.stroke;
    ctx.lineWidth   = 1.5;

    if (shape === 'gate') {
      // Diamond
      ctx.beginPath();
      ctx.moveTo(sx, sy - rf);
      ctx.lineTo(sx + rf * 0.8, sy);
      ctx.lineTo(sx, sy + rf);
      ctx.lineTo(sx - rf * 0.8, sy);
      ctx.closePath(); ctx.fill(); ctx.stroke();
    } else if (shape === 'end') {
      // Square
      ctx.fillRect(sx - rf * 0.85, sy - rf * 0.85, rf * 1.7, rf * 1.7);
      ctx.strokeRect(sx - rf * 0.85, sy - rf * 0.85, rf * 1.7, rf * 1.7);
    } else if (shape === 'ui') {
      // Hollow circle with cross
      ctx.beginPath(); ctx.arc(sx, sy, rf, 0, Math.PI * 2);
      ctx.fillStyle = col.fill + '55'; ctx.fill();
      ctx.strokeStyle = col.stroke; ctx.stroke();
      ctx.beginPath(); ctx.moveTo(sx - rf * 0.5, sy); ctx.lineTo(sx + rf * 0.5, sy); ctx.stroke();
      ctx.beginPath(); ctx.moveTo(sx, sy - rf * 0.5); ctx.lineTo(sx, sy + rf * 0.5); ctx.stroke();
    }

    if (showLabel) {
      ctx.fillStyle = '#fff';
      ctx.font      = '8px system-ui';
      ctx.textAlign = 'center';
      ctx.textBaseline = 'middle';
      const tag = label + (group > 0 ? group : '');
      ctx.fillText(tag, sx, sy + (shape === 'ui' ? rf + 6 : 0));
    }
  }

  state.gates.forEach((m,i)  => drawMarkerShape(m.x, m.y, m.group ?? 0, 'gate',  'G', i));
  state.ends.forEach((m,i)   => drawMarkerShape(m.x, m.y, m.group ?? 0, 'end',   'E', i));
  state.uis.forEach((m,i)    => drawMarkerShape(m.x, m.y, m.group ?? 0, 'ui',    'UI', i));

  ctx.textBaseline = 'alphabetic';

  // Checkpoints — 1×3 column CENTERED on the cell (game: pos = cell center, scale cellW × cellH*3)
  for (const c of state.checkpoints) {
    const { sx: cx0, sy: cy0 } = worldToScreen(c.x,     c.y + 2);
    const { sx: cx1, sy: cy1 } = worldToScreen(c.x + 1, c.y - 1);
    const cw = cx1 - cx0, ch = cy1 - cy0;
    ctx.fillStyle   = 'rgba(255,216,0,0.40)';
    ctx.fillRect(cx0, cy0, cw, ch);
    ctx.strokeStyle = '#fbbf24';
    ctx.lineWidth   = 1.5;
    ctx.strokeRect(cx0, cy0, cw, ch);
    if (showLabel && ch > 14) {
      ctx.fillStyle = '#fff'; ctx.font = '9px system-ui'; ctx.textAlign = 'center';
      ctx.fillText('CP', cx0 + cw / 2, cy0 + ch / 2 + 3);
    }
  }

  // Swap triggers — 1×3 trigger zone CENTERED on the cell (game: pos = cell center, size cellW × cellH*3)
  for (const s of state.swapTriggers) {
    const { sx: sx0, sy: sy0 } = worldToScreen(s.x,     s.y + 2);
    const { sx: sx1, sy: sy1 } = worldToScreen(s.x + 1, s.y - 1);
    const sw = sx1 - sx0, sh = sy1 - sy0;
    ctx.fillStyle   = 'rgba(251,146,60,0.28)';
    ctx.fillRect(sx0, sy0, sw, sh);
    ctx.strokeStyle = '#fb923c';
    ctx.lineWidth   = 1.5;
    ctx.strokeRect(sx0, sy0, sw, sh);
    if (showLabel && sh > 14) {
      ctx.fillStyle = '#fff'; ctx.font = '9px system-ui'; ctx.textAlign = 'center';
      ctx.fillText('SW', sx0 + sw / 2, sy0 + sh / 2 + 3);
    }
  }

  // Upgrade boxes — kiosk prefab footprint: 2 wide, 1.6 up / 2.1 down from pivot
  for (const b of state.boxes) drawBoxKiosk(b, ts, 1, showLabel);

  ctx.textAlign = 'left';
  ctx.restore();
}

// Stylized upgrade-box kiosk matching the in-game prefab proportions
function drawBoxKiosk(b, ts, alpha, showLabel) {
  const ctx = state.ctx;
  const { sx: x0, sy: y0 } = worldToScreen(b.x - BOX_EXTENT.left,  b.y + BOX_EXTENT.up);
  const { sx: x1, sy: y1 } = worldToScreen(b.x + BOX_EXTENT.right, b.y - BOX_EXTENT.down);
  const w = x1 - x0, h = y1 - y0;

  ctx.save();
  ctx.globalAlpha = alpha;
  // body
  ctx.fillStyle = 'rgba(108,112,120,0.92)';
  ctx.fillRect(x0, y0, w, h);
  // green status light strip (top)
  ctx.fillStyle = '#65a30d';
  ctx.fillRect(x0 + w * 0.18, y0 + h * 0.03, w * 0.64, h * 0.10);
  // price screen
  ctx.fillStyle = '#1c1e26';
  ctx.fillRect(x0 + w * 0.12, y0 + h * 0.18, w * 0.76, h * 0.30);
  // orange base
  ctx.fillStyle = '#ea7c1c';
  ctx.fillRect(x0, y0 + h * 0.88, w, h * 0.12);
  // outline
  ctx.strokeStyle = '#f59e0b';
  ctx.lineWidth   = 1.5;
  ctx.strokeRect(x0, y0, w, h);
  // pivot tick (the stored coordinate)
  const p = worldToScreen(b.x, b.y);
  ctx.fillStyle = '#fde68a';
  ctx.fillRect(p.sx - 2, p.sy - 2, 4, 4);

  if (showLabel) {
    ctx.fillStyle = '#fef3c7'; ctx.font = '9px system-ui'; ctx.textAlign = 'center';
    ctx.fillText('B' + b.upgrade, x0 + w / 2, y0 + h * 0.33 + 3);
  }
  ctx.restore();
}


// ── Selection bounding box ────────────────────────────────────
function selOutline(ctx, ts, item) {
  const P = 3; // padding px
  if (item.type === 'image') {
    const img = state.images[item.index];
    if (!img) return;
    const { sx, sy } = worldToScreen(img.x, img.y);
    const pw = img.w * ts, ph = img.h * ts;
    ctx.save();
    ctx.translate(sx, sy);
    ctx.rotate(-img.rot * Math.PI / 180);
    ctx.strokeRect(-pw / 2 - P, -ph / 2 - P, pw + P * 2, ph + P * 2);
    ctx.restore();
  } else if (item.type === 'hitbox') {
    const h = state.hitboxes[item.index];
    if (!h) return;
    const { sx: x0, sy: y0 } = worldToScreen(h.x, h.y + h.h);
    const { sx: x1, sy: y1 } = worldToScreen(h.x + h.w, h.y);
    ctx.strokeRect(x0 - P, y0 - P, (x1 - x0) + P * 2, (y1 - y0) + P * 2);
  } else if (item.type === 'box') {
    const b = state.boxes[item.index];
    if (!b) return;
    const { sx: x0, sy: y0 } = worldToScreen(b.x - BOX_EXTENT.left,  b.y + BOX_EXTENT.up);
    const { sx: x1, sy: y1 } = worldToScreen(b.x + BOX_EXTENT.right, b.y - BOX_EXTENT.down);
    ctx.strokeRect(x0 - P, y0 - P, (x1 - x0) + P * 2, (y1 - y0) + P * 2);
  } else if (item.type === 'checkpoint') {
    const c = state.checkpoints[item.index];
    if (!c) return;
    const { sx: x0, sy: y0 } = worldToScreen(c.x, c.y + 2);
    const { sx: x1, sy: y1 } = worldToScreen(c.x + 1, c.y - 1);
    ctx.strokeRect(x0 - P, y0 - P, (x1 - x0) + P * 2, (y1 - y0) + P * 2);
  } else if (item.type === 'swaptrig') {
    const s = state.swapTriggers[item.index];
    if (!s) return;
    const { sx: x0, sy: y0 } = worldToScreen(s.x, s.y + 2);
    const { sx: x1, sy: y1 } = worldToScreen(s.x + 1, s.y - 1);
    ctx.strokeRect(x0 - P, y0 - P, (x1 - x0) + P * 2, (y1 - y0) + P * 2);
  } else if (item.type === 'marker') {
    const arr = state[item.markerType + 's'];
    const m = arr?.[item.index];
    if (!m) return;
    if (item.markerType === 'spawn') {
      const { sx: x0, sy: y0 } = worldToScreen(m.x - 0.5, m.y + 0.5);
      const { sx: x1, sy: y1 } = worldToScreen(m.x + 0.5, m.y - 0.5);
      ctx.strokeRect(x0 - P, y0 - P, (x1 - x0) + P * 2, (y1 - y0) + P * 2);
      return;
    }
    const r = Math.max(6, ts * 0.4) + P + 2;
    const { sx, sy } = worldToScreen(m.x, m.y);
    ctx.beginPath(); ctx.arc(sx, sy, r, 0, Math.PI * 2); ctx.stroke();
  } else if (item.type === 'tile') {
    const t = state.tiles.get(item.key);
    if (!t) return;
    const { sx: x0, sy: y0 } = worldToScreen(t.x, t.y + 1);
    const { sx: x1, sy: y1 } = worldToScreen(t.x + 1, t.y);
    ctx.strokeRect(x0 - P, y0 - P, (x1 - x0) + P * 2, (y1 - y0) + P * 2);
  }
}

function drawSelection(ts) {
  const { ctx } = state;
  ctx.save();
  ctx.strokeStyle = '#60a5fa';
  ctx.lineWidth   = 1.5;
  ctx.setLineDash([5, 3]);

  if (state.selection) selOutline(ctx, ts, state.selection);

  ctx.restore();
}

function drawMultiSelect(ts) {
  if (!state.multiSel || state.multiSel.length === 0) return;
  const { ctx } = state;
  ctx.save();
  ctx.strokeStyle = '#93c5fd';
  ctx.lineWidth   = 1;
  ctx.setLineDash([4, 3]);
  for (const item of state.multiSel) selOutline(ctx, ts, item);
  ctx.restore();
}

function drawMarquee() {
  if (!state.marquee) return;
  const { ctx } = state;
  const { x0, y0, x1, y1 } = state.marquee;
  const a = worldToScreen(x0, y0);
  const b = worldToScreen(x1, y1);
  ctx.save();
  ctx.strokeStyle = '#60a5fa';
  ctx.fillStyle   = 'rgba(96,165,250,0.07)';
  ctx.lineWidth   = 1;
  ctx.setLineDash([4, 3]);
  const rx = Math.min(a.sx, b.sx), ry = Math.min(a.sy, b.sy);
  const rw = Math.abs(b.sx - a.sx), rh = Math.abs(b.sy - a.sy);
  ctx.fillRect(rx, ry, rw, rh);
  ctx.strokeRect(rx, ry, rw, rh);
  ctx.restore();
}

// ── Ghost / preview ───────────────────────────────────────────
function drawGhost(ts) {
  const { ctx } = state;
  if (!state.ghostPos) return;
  const { wx, wy } = state.ghostPos;

  ctx.save();
  ctx.globalAlpha = 0.45;

  if (['ground','spike','blue','orange','bluespike','orangespike'].includes(state.activeTool)) {
    const tx = Math.floor(wx), ty = Math.floor(wy);
    const { sx, sy } = worldToScreen(tx, ty + 1);
    const layer = { ground: 0, spike: 2, blue: 3, orange: 4, bluespike: 5, orangespike: 6 }[state.activeTool];
    const tileImg = getTileCanvas(layer);
    if (tileImg) {
      ctx.drawImage(tileImg, sx, sy, ts, ts);
    } else {
      ctx.fillStyle = LAYER_COLOR[layer] || '#888';
      ctx.fillRect(sx, sy, ts, ts);
    }
  } else if (state.activeTool === 'checkpoint') {
    const tx = Math.floor(wx), ty = Math.floor(wy);
    const { sx: x0, sy: y0 } = worldToScreen(tx, ty + 2);
    const { sx: x1, sy: y1 } = worldToScreen(tx + 1, ty - 1);
    ctx.fillStyle = 'rgba(255,216,0,0.40)';
    ctx.fillRect(x0, y0, x1 - x0, y1 - y0);
    ctx.strokeStyle = '#fbbf24'; ctx.lineWidth = 1.5;
    ctx.strokeRect(x0, y0, x1 - x0, y1 - y0);
  } else if (state.activeTool === 'swaptrig') {
    const tx = Math.floor(wx), ty = Math.floor(wy);
    const { sx: x0, sy: y0 } = worldToScreen(tx, ty + 2);
    const { sx: x1, sy: y1 } = worldToScreen(tx + 1, ty - 1);
    ctx.fillStyle = 'rgba(251,146,60,0.28)';
    ctx.fillRect(x0, y0, x1 - x0, y1 - y0);
    ctx.strokeStyle = '#fb923c'; ctx.lineWidth = 1.5;
    ctx.strokeRect(x0, y0, x1 - x0, y1 - y0);
  } else if (state.activeTool === 'box') {
    const tx = Math.floor(wx), ty = Math.floor(wy);
    drawBoxKiosk({ x: tx, y: ty, upgrade: state.boxUpgrade ?? '' }, ts, 0.7, false);
  } else if (state.activeTool === 'imageobj' && state.pendingImagePath) {
    const x = state.snapMode ? Math.floor(wx) + 0.5 : wx;
    const y = state.snapMode ? Math.floor(wy) + 0.5 : wy;
    const imgData = {
      path: state.pendingImagePath,
      x, y,
      w: state.pendingImageW, h: state.pendingImageH,
      rot: state.pendingImageRot, opacity: state.pendingImageOpacity,
      cr: state.pendingImageCr, cg: state.pendingImageCg, cb: state.pendingImageCb,
      depth: state.pendingImageDepth,
    };
    ctx.globalAlpha = 0.55;
    drawImage(imgData, -1, ts);
  } else if (state.activeTool === 'hitbox' && state.hitboxDrag) {
    const { x0, y0 } = state.hitboxDrag;
    let hx, hy, hw, hh;
    if (state.snapMode) {
      hx = Math.floor(Math.min(x0, wx));
      hy = Math.floor(Math.min(y0, wy));
      hw = Math.floor(Math.max(x0, wx)) + 1 - hx;
      hh = Math.floor(Math.max(y0, wy)) + 1 - hy;
    } else {
      hx = Math.min(x0, wx); hy = Math.min(y0, wy);
      hw = Math.abs(wx - x0) || 1; hh = Math.abs(wy - y0) || 1;
    }
    const ghost = { x: hx, y: hy, w: hw, h: hh };
    ctx.globalAlpha = 0.5;
    drawHitbox(ghost, -1, ts);
  }

  ctx.restore();
}

// ── HUD ───────────────────────────────────────────────────────
function drawHUD(W, H) {
  const { ctx } = state;
  ctx.save();

  // Snap badge
  if (state.snapMode) {
    ctx.fillStyle   = 'rgba(21,128,61,0.85)';
    ctx.fillRect(W - 62, 4, 58, 18);
    ctx.fillStyle = '#4ade80';
    ctx.font      = '11px system-ui';
    ctx.textAlign = 'center';
    ctx.fillText('SNAP ON', W - 33, 17);
  }

  ctx.textAlign = 'left';
  ctx.restore();

  // Update status bar
  const coordEl = document.getElementById('status-coords');
  const zoomEl  = document.getElementById('status-zoom');
  if (coordEl && state.ghostPos) {
    coordEl.textContent = `${state.ghostPos.wx.toFixed(1)}, ${state.ghostPos.wy.toFixed(1)}`;
  }
  if (zoomEl) zoomEl.textContent = `Zoom: ${state.zoom.toFixed(2)}×`;
}

// ── Fit viewport to all content ───────────────────────────────
export function fitViewToContent() {
  if (!state.canvas || state.canvas.width === 0) return;
  let x0 = Infinity, x1 = -Infinity, y0 = Infinity, y1 = -Infinity;
  const push = (x, y) => {
    if (!isFinite(x) || !isFinite(y)) return;
    x0 = Math.min(x0, x); x1 = Math.max(x1, x);
    y0 = Math.min(y0, y); y1 = Math.max(y1, y);
  };

  for (const t of state.tiles.values())  push(t.x + 0.5,   t.y + 0.5);
  for (const h of state.hitboxes)        push(h.x + h.w/2, h.y + h.h/2);
  for (const img of state.images)        push(img.x,       img.y);
  for (const b of state.boxes)           push(b.x,         b.y - 0.25);
  for (const c of state.checkpoints)     push(c.x + 0.5,   c.y + 0.5);
  for (const s of state.swapTriggers)    push(s.x + 0.5,   s.y + 0.5);
  for (const m of state.spawns)  push(m.x, m.y);
  for (const m of state.gates)   push(m.x, m.y);
  for (const m of state.ends)    push(m.x, m.y);
  for (const m of state.uis)     push(m.x, m.y);

  if (!isFinite(x0)) return;
  const pad = 5;
  x0 -= pad; x1 += pad; y0 -= pad; y1 += pad;
  state.panX = (x0 + x1) / 2;
  state.panY = (y0 + y1) / 2;
  const W = state.canvas.width, H = state.canvas.height;
  const zx = W / ((x1 - x0) * 12);
  const zy = H / ((y1 - y0) * 12);
  state.zoom = Math.max(0.08, Math.min(12, 0.85 * Math.min(zx, zy)));
  markDirty();
}

// Expose drawImage for ghost use from tools
export { drawImage, drawHitbox };
