const express = require('express');
const fs      = require('fs');
const path    = require('path');

const app = express();
const PORT = 3000;

const SPRITE_DIR = 'C:\\Users\\macro\\AppData\\Roaming\\IGTAPEditor\\sprites';
const SAVE_DIR   = 'C:\\Users\\macro\\AppData\\LocalLow\\Pepper tango games\\IGTAPsnfDemo\\Savedata\\customcourses';

// ── Sprite manifest ───────────────────────────────────────────
let manifest = null;

function buildManifest() {
  // Flat file scan for gallery groups
  let files = [];
  try {
    files = fs.readdirSync(SPRITE_DIR).filter(f => /\.(png|jpg|jpeg)$/i.test(f));
  } catch {
    console.warn('Sprite dir not found:', SPRITE_DIR);
  }

  const groupMap = new Map();
  for (const file of files) {
    const stem  = path.basename(file, path.extname(file));
    const group = stem.replace(/_\d+$/, '') || stem;
    if (!groupMap.has(group)) groupMap.set(group, []);
    groupMap.get(group).push(file);
  }
  const groups = [];
  for (const [name, grpFiles] of groupMap) {
    grpFiles.sort((a, b) => {
      const na = parseInt(a.match(/_(\d+)\b/)?.[1] ?? '0') || 0;
      const nb = parseInt(b.match(/_(\d+)\b/)?.[1] ?? '0') || 0;
      return na - nb;
    });
    groups.push({ name, files: grpFiles });
  }
  groups.sort((a, b) => a.name.localeCompare(b.name));

  // Read structured manifest written by in-game plugin
  let layerMap = null, objectMap = null, tileset = null;
  const structuredPath = path.join(SPRITE_DIR, 'manifest.json');
  if (fs.existsSync(structuredPath)) {
    try {
      const m = JSON.parse(fs.readFileSync(structuredPath, 'utf8'));
      layerMap  = m.layerMap  ?? null;
      objectMap = m.objectMap ?? null;
      tileset   = m.tileset   ?? null;
    } catch (e) {
      console.warn('manifest.json parse error:', e.message);
    }
  }

  manifest = { spriteDir: SPRITE_DIR, groups, layerMap, objectMap, tileset };
  console.log(`Manifest: ${groups.length} groups, ${files.length} sprites` +
    (layerMap ? `, ${Object.keys(layerMap).length} layer sprites` : ''));
}

buildManifest();

app.use(express.json({ limit: '10mb' }));
app.use(express.static(path.join(__dirname, 'public')));

// ── Sprite serving ────────────────────────────────────────────
app.get('/sprites/:filename', (req, res) => {
  const fn = req.params.filename;
  if (fn.includes('..') || /[/\\]/.test(fn)) return res.status(400).end();
  res.setHeader('Cache-Control', 'max-age=86400');
  res.sendFile(path.join(SPRITE_DIR, fn), err => { if (err) res.status(404).end(); });
});

app.get('/api/sprites/manifest', (req, res) => {
  buildManifest(); // reload in case plugin just exported
  res.json(manifest);
});

// ── In-game editor close request ─────────────────────────────
// The embedded (in-game) editor can't close its own window; it drops a flag
// file that the game plugin watches and hides the editor when it appears.
const CLOSE_FLAG = path.join(path.dirname(SPRITE_DIR), 'close.flag');

app.post('/api/editor/close', (req, res) => {
  try {
    fs.writeFileSync(CLOSE_FLAG, String(Date.now()), 'utf8');
    res.json({ ok: true });
  } catch (e) { res.status(500).json({ error: String(e) }); }
});

// ── User-authored tileset packs ──────────────────────────────
// Stored separately from the generated packs.json so they survive
// re-runs of gen_packs.py; merged client-side (user packs first).
const USERPACKS_PATH = path.join(__dirname, 'public', 'userpacks.json');

app.get('/api/userpacks', (req, res) => {
  try {
    if (!fs.existsSync(USERPACKS_PATH)) return res.json([]);
    res.json(JSON.parse(fs.readFileSync(USERPACKS_PATH, 'utf8').replace(/^﻿/, '')));
  } catch (e) { res.status(500).json({ error: String(e) }); }
});

app.put('/api/userpacks', (req, res) => {
  const packs = req.body;
  if (!Array.isArray(packs)) return res.status(400).json({ error: 'Expected an array of packs' });
  for (const p of packs) {
    if (!p || typeof p.label !== 'string' || !p.corner || !p.edge || !p.fill)
      return res.status(400).json({ error: 'Pack needs label, corner, edge, fill' });
  }
  try {
    fs.writeFileSync(USERPACKS_PATH, JSON.stringify(packs, null, 2), 'utf8');
    res.json({ ok: true, count: packs.length });
  } catch (e) { res.status(500).json({ error: String(e) }); }
});

// ── Multi-map API ─────────────────────────────────────────────
function ensureSaveDir() {
  if (!fs.existsSync(SAVE_DIR)) fs.mkdirSync(SAVE_DIR, { recursive: true });
}

// List all maps (active + stashed)
app.get('/api/maps', (req, res) => {
  const stashDir = path.join(SAVE_DIR, '.stash');
  let maps = [];
  try {
    ensureSaveDir();
    maps = fs.readdirSync(SAVE_DIR)
      .filter(f => f.endsWith('.json'))
      .map(f => ({ name: path.basename(f, '.json'), file: f, stashed: false }));
  } catch { }
  try {
    if (fs.existsSync(stashDir)) {
      const stashed = fs.readdirSync(stashDir)
        .filter(f => f.endsWith('.json'))
        .map(f => ({ name: path.basename(f, '.json'), file: f, stashed: true }));
      maps = maps.concat(stashed);
    }
  } catch { }
  maps.sort((a, b) => a.name.localeCompare(b.name));
  res.json(maps);
});

// Load a named map
app.get('/api/maps/:name', (req, res) => {
  const name = req.params.name.replace(/[/\\:*?"<>|]/g, '_');
  const filePath = path.join(SAVE_DIR, name + '.json');
  const stashPath = path.join(SAVE_DIR, '.stash', name + '.json');
  const src = fs.existsSync(filePath) ? filePath : fs.existsSync(stashPath) ? stashPath : null;
  if (!src) {
    return res.json({
      version: 3, tiles: [], hitboxes: [], images: [],
      boxes: [], swapTriggers: [], checkpoints: [],
      spawnX: 0, spawnY: 0, gateX: 0, gateY: 0,
      endX: 0, endY: 0, uiX: 0, uiY: 0
    });
  }
  // strip UTF-8 BOM — PowerShell and some tools write one, JSON.parse rejects it
  try { res.json(JSON.parse(fs.readFileSync(src, 'utf8').replace(/^﻿/, ''))); }
  catch (e) { res.status(500).json({ error: String(e) }); }
});

// Save a named map
app.put('/api/maps/:name', (req, res) => {
  const name = req.params.name.replace(/[/\\:*?"<>|]/g, '_');
  const level = req.body;
  if (!level || level.version !== 3) return res.status(400).json({ error: 'Invalid level' });
  try {
    ensureSaveDir();
    const filePath = path.join(SAVE_DIR, name + '.json');
    const tmp = filePath + '.tmp';
    fs.writeFileSync(tmp, JSON.stringify(level, null, 2), 'utf8');
    fs.renameSync(tmp, filePath);
    res.json({ ok: true });
  } catch (e) {
    res.status(500).json({ error: String(e) });
  }
});

// ── Legacy single-file endpoints (kept for backwards compat) ──
const LEGACY_FILE = path.join(SAVE_DIR, 'custommap.json');

app.get('/api/level/load', (req, res) => {
  try { res.json(JSON.parse(fs.readFileSync(LEGACY_FILE, 'utf8'))); }
  catch {
    res.json({
      version: 3, tiles: [], hitboxes: [], images: [],
      boxes: [], swapTriggers: [], checkpoints: [],
      spawnX: 0, spawnY: 0, gateX: 0, gateY: 0,
      endX: 0, endY: 0, uiX: 0, uiY: 0
    });
  }
});

app.post('/api/level/save', (req, res) => {
  const level = req.body;
  if (!level || level.version !== 3) return res.status(400).json({ error: 'Invalid level' });
  try {
    ensureSaveDir();
    const tmp = LEGACY_FILE + '.tmp';
    fs.writeFileSync(tmp, JSON.stringify(level, null, 2), 'utf8');
    fs.renameSync(tmp, LEGACY_FILE);
    res.json({ ok: true });
  } catch (e) {
    res.status(500).json({ error: String(e) });
  }
});

const server = app.listen(PORT, () => console.log(`IGTAP Editor → http://localhost:${PORT}`));
server.on('error', e => {
  // Another instance (e.g. the in-game plugin's server) already owns the port —
  // that's fine, the window/client just talks to it instead.
  if (e.code === 'EADDRINUSE') console.log(`Port ${PORT} already served — reusing existing server.`);
  else throw e;
});
