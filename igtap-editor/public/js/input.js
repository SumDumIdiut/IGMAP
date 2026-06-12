import { state, markDirty }           from './state.js';
import { zoomAround }                  from './coords.js';
import { undo, redo, clearHistory,
         pushSnapshot }                from './history.js';
import { onMouseDown, onMouseMove,
         onMouseUp, onContextMenu,
         rotateSelected, deleteSelected,
         moveSelected }                   from './tools.js';
import { setTool }                     from './ui.js';
import { saveToServer, loadFromServer,
         downloadLevel, openLevelFile } from './levelio.js';
import { fitViewToContent }            from './canvas.js';

export function initInput(canvasEl) {
  // ── Mouse ────────────────────────────────────────────────────
  canvasEl.addEventListener('mousedown', e => {
    e.preventDefault();
    const r  = canvasEl.getBoundingClientRect();
    const sx = (e.clientX - r.left) * (canvasEl.width  / r.width);
    const sy = (e.clientY - r.top)  * (canvasEl.height / r.height);
    onMouseDown(e, sx, sy);
  });

  canvasEl.addEventListener('mousemove', e => {
    const r  = canvasEl.getBoundingClientRect();
    const sx = (e.clientX - r.left) * (canvasEl.width  / r.width);
    const sy = (e.clientY - r.top)  * (canvasEl.height / r.height);
    onMouseMove(e, sx, sy, e.buttons);
  });

  window.addEventListener('mouseup', e => {
    const r  = canvasEl.getBoundingClientRect();
    const sx = (e.clientX - r.left) * (canvasEl.width  / r.width);
    const sy = (e.clientY - r.top)  * (canvasEl.height / r.height);
    onMouseUp(e, sx, sy);
  });

  canvasEl.addEventListener('wheel', e => {
    e.preventDefault();
    const r  = canvasEl.getBoundingClientRect();
    const sx = (e.clientX - r.left) * (canvasEl.width  / r.width);
    const sy = (e.clientY - r.top)  * (canvasEl.height / r.height);
    const factor = e.deltaY < 0 ? 1.25 : 1 / 1.25;
    zoomAround(factor, sx, sy);
    markDirty();
  }, { passive: false });

  canvasEl.addEventListener('contextmenu', e => {
    e.preventDefault();
    const r  = canvasEl.getBoundingClientRect();
    const sx = (e.clientX - r.left) * (canvasEl.width  / r.width);
    const sy = (e.clientY - r.top)  * (canvasEl.height / r.height);
    onContextMenu(e, sx, sy);
  });

  canvasEl.addEventListener('mouseleave', () => {
    state.ghostPos = null;
    markDirty();
  });

  // ── Keyboard ─────────────────────────────────────────────────
  window.addEventListener('keydown', e => {
    // Skip if focus is in an input/textarea
    if (['INPUT', 'TEXTAREA', 'SELECT'].includes(document.activeElement.tagName)) return;

    const ctrl  = e.ctrlKey || e.metaKey;
    const shift = e.shiftKey;
    const key   = e.key.toLowerCase();

    // ── Ctrl combos ────────────────────────────────────────
    if (ctrl && key === 's') { e.preventDefault(); saveToServer(); return; }
    if (ctrl && key === 'z') { e.preventDefault(); undo(); return; }
    if (ctrl && key === 'y') { e.preventDefault(); redo(); return; }
    if (ctrl && shift && key === 's') { e.preventDefault(); downloadLevel(); return; }
    if (ctrl && key === 'o') {
      e.preventDefault();
      const inp = document.createElement('input');
      inp.type   = 'file';
      inp.accept = '.json';
      inp.onchange = ev => { if (ev.target.files[0]) openLevelFile(ev.target.files[0]); };
      inp.click();
      return;
    }
    if (ctrl && key === 'n') { e.preventDefault(); newLevel(); return; }

    // ── Snap toggle (Ctrl alone caught above) ──────────────
    // Ctrl alone fires on keydown with key='Control'
    if (e.key === 'Control' && !e.repeat) { state.snapMode = !state.snapMode; markDirty(); return; }

    if (ctrl) return;

    // ── Tool shortcuts ─────────────────────────────────────
    const toolMap = {
      'escape':       'pointer',
      '1':            'ground',
      '2':            'spike',
      '3':            'blue',
      '4':            'orange',
      '5':            'bluespike',
      '6':            'orangespike',
      'x':            'erase',
      'g':            'gate',
      'n':            'end',
      's':            'spawn',
      'u':            'ui',
      't':            'swaptrig',
      'c':            'checkpoint',
      'b':            'box',
      'h':            'hitbox',
      'i':            'imageobj',
    };
    if (toolMap[key]) { setTool(toolMap[key]); return; }

    // ── Rotation ────────────────────────────────────────────
    if (key === 'q') { rotateSelected(-90); return; }
    if (key === 'e') { rotateSelected( 90); return; }

    // ── Delete ──────────────────────────────────────────────
    if (key === 'delete' || key === 'backspace') {
      deleteSelected(); return;
    }

    // ── Fit view ────────────────────────────────────────────
    if (key === 'f') { fitViewToContent(); return; }
    if (key === 'r') { state.panX = 0; state.panY = 5; state.zoom = 2; markDirty(); return; }

    // ── Arrow keys: move selection if active, else pan ──────
    if (e.key.startsWith('Arrow')) {
      e.preventDefault();
      const adx = e.key === 'ArrowRight' ? 1 : e.key === 'ArrowLeft' ? -1 : 0;
      const ady = e.key === 'ArrowUp'    ? 1 : e.key === 'ArrowDown' ? -1 : 0;
      if (state.selection || state.multiSel.length > 0) { moveSelected(adx, ady); return; }
      const step = Math.max(2, 8 / Math.max(0.01, state.zoom));
      state.panX += adx * step; state.panY += ady * step; markDirty(); return;
    }

    // ── WASD pan ─────────────────────────────────────────────
    const step = Math.max(2, 8 / Math.max(0.01, state.zoom));
    if (key === 'a') { state.panX -= step; markDirty(); }
    if (key === 'd') { state.panX += step; markDirty(); }
    if (key === 'w') { state.panY += step; markDirty(); }
  });
}

function newLevel() {
  if (state.unsaved && !confirm('Clear all map data?')) return;
  state.tiles        = new Map();
  state.images       = [];
  state.hitboxes     = [];
  state.boxes        = [];
  state.swapTriggers = [];
  state.checkpoints  = [];
  state.spawns = []; state.gates = []; state.ends = []; state.uis = [];
  state.activeGroup  = 0;
  state.selection    = null;
  state.multiSel     = [];
  state.marquee      = null;
  state.unsaved      = false;
  state.panX = 0; state.panY = 5; state.zoom = 2;
  clearHistory();
  pushSnapshot();
  markDirty();
}

