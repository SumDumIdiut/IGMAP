import { state } from './state.js';

export function tileSize() {
  return Math.max(0.5, 12 * state.zoom);
}

export function worldToScreen(wx, wy) {
  const ts = tileSize();
  const cx = state.canvas.width  / 2;
  const cy = state.canvas.height / 2;
  return {
    sx: cx + (wx - state.panX) * ts,
    sy: cy - (wy - state.panY) * ts    // Y-flip: world-up → screen-up
  };
}

export function screenToWorld(sx, sy) {
  const ts = tileSize();
  const cx = state.canvas.width  / 2;
  const cy = state.canvas.height / 2;
  return {
    wx: (sx - cx) / ts + state.panX,
    wy: -(sy - cy) / ts + state.panY
  };
}

// Integer tile the point (wx,wy) falls in (bottom-left of tile = tile coord)
export function snapToTile(wx, wy) {
  return { tx: Math.floor(wx), ty: Math.floor(wy) };
}

// Snap float world position to the center of its tile
export function snapToTileCenter(wx, wy) {
  return { wx: Math.floor(wx) + 0.5, wy: Math.floor(wy) + 0.5 };
}

// Zoom keeping a world point fixed under the cursor
export function zoomAround(factor, screenX, screenY) {
  const before = screenToWorld(screenX, screenY);
  state.zoom = Math.max(0.08, Math.min(12, state.zoom * factor));
  const after  = screenToWorld(screenX, screenY);
  state.panX += before.wx - after.wx;
  state.panY += before.wy - after.wy;
}
