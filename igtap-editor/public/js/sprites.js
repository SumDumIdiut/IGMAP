import { state, markDirty } from './state.js';

const imageCache = new Map();   // url → HTMLImageElement (or Promise)

// ── Layer tile sprites ────────────────────────────────────────
// Map<layer (int), OffscreenCanvas | HTMLImageElement>  (pre-tinted)
const layerCanvasCache = new Map();

export async function loadLayerSprites(manifest) {
  if (!manifest?.layerMap) return;

  const loads = [];
  for (const [layerStr, entry] of Object.entries(manifest.layerMap)) {
    const layer = parseInt(layerStr);
    const img = new Image();
    const p = new Promise(resolve => {
      img.onload = () => {
        const tint = entry.tint ?? [1, 1, 1, 1];
        layerCanvasCache.set(layer, buildTintedCanvas(img, tint));
        resolve();
      };
      img.onerror = resolve;
    });
    img.src = `/sprites/${encodeURIComponent(entry.file)}`;
    loads.push(p);
  }
  await Promise.all(loads);
  markDirty();
}

function buildTintedCanvas(img, tint) {
  const [r, g, b] = tint;
  // No tint — return raw image element (fastest path)
  if (r >= 0.999 && g >= 0.999 && b >= 0.999) return img;

  const off = new OffscreenCanvas(img.naturalWidth || img.width, img.naturalHeight || img.height);
  const oc  = off.getContext('2d');
  oc.drawImage(img, 0, 0);
  oc.globalCompositeOperation = 'multiply';
  oc.fillStyle = `rgb(${(r * 255) | 0},${(g * 255) | 0},${(b * 255) | 0})`;
  oc.fillRect(0, 0, off.width, off.height);
  oc.globalCompositeOperation = 'destination-in';
  oc.drawImage(img, 0, 0);
  return off;
}

// Returns the pre-tinted canvas/image for a layer, or null if not loaded
export function getTileCanvas(layer) {
  return layerCanvasCache.get(layer) ?? null;
}

export async function loadManifest() {
  const res  = await fetch('/api/sprites/manifest');
  const data = await res.json();
  state.manifest  = data;
  state.spriteDir = data.spriteDir || '';
}

// Return cached image element, or start loading and return null
export function getImage(urlOrPath) {
  if (!urlOrPath) return null;
  const url = pathToUrl(urlOrPath);
  if (!url) return null;

  if (imageCache.has(url)) {
    const v = imageCache.get(url);
    return (v instanceof HTMLImageElement) ? v : null;
  }

  const img = new Image();
  imageCache.set(url, img);   // immediately put img in cache (incomplete)
  img.onload = () => {
    imageCache.set(url, img);
    markDirty();
  };
  img.onerror = () => imageCache.delete(url);
  img.src = url;
  return null;
}

// Convert a game Windows path or bare filename to a /sprites/... URL
export function pathToUrl(p) {
  if (!p) return null;
  if (p.startsWith('/sprites/') || p.startsWith('http')) return p;
  // Extract just the filename from a Windows absolute path
  const filename = p.replace(/\\/g, '/').split('/').pop();
  return `/sprites/${encodeURIComponent(filename)}`;
}

// Convert a game path to a /sprites/ URL (alias)
export function spriteUrl(p) { return pathToUrl(p); }

// Categorise a group name for gallery tabs
export function categorizeGroup(name) {
  const lo = name.toLowerCase();
  if (/tileset|ground|floor|spike|mossy|platform|@tile|corner|line_|moving_block/.test(lo)) return 'tiles';
  if (/background|wire|circle|tree|fresco|level.?bg|repeating/.test(lo))                    return 'backgrounds';
  if (/flower|plant|thorn|arc|loop|tangle|branch|bush|vine|deco/.test(lo))                  return 'decorations';
  return 'other';
}

// Build a full Windows path from a filename (for saving into level JSON)
export function buildGamePath(filename) {
  if (!filename) return '';
  const dir = state.spriteDir || 'C:\\Users\\macro\\Downloads\\decompiled\\assets\\Sprite';
  return dir + '\\' + filename;
}
