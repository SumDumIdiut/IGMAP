import { state, markDirty } from './state.js';

const MAX_HISTORY = 60;
const past   = [];
const future = [];

function levelSnapshot() {
  return {
    tiles:         new Map(state.tiles),
    images:        state.images.map(i => ({ ...i })),
    hitboxes:      state.hitboxes.map(h => ({ ...h })),
    boxes:         state.boxes.map(b => ({ ...b })),
    swapTriggers:  state.swapTriggers.map(s => ({ ...s })),
    checkpoints:   state.checkpoints.map(c => ({ ...c })),
    spawn:         state.spawn ? { ...state.spawn } : null,
    gate:          state.gate  ? { ...state.gate  } : null,
    end:           state.end   ? { ...state.end   } : null,
    ui:            state.ui    ? { ...state.ui    } : null,
  };
}

function applySnapshot(snap) {
  state.tiles        = new Map(snap.tiles);
  state.images       = snap.images.map(i => ({ ...i }));
  state.hitboxes     = snap.hitboxes.map(h => ({ ...h }));
  state.boxes        = snap.boxes.map(b => ({ ...b }));
  state.swapTriggers = snap.swapTriggers.map(s => ({ ...s }));
  state.checkpoints  = snap.checkpoints.map(c => ({ ...c }));
  state.spawn        = snap.spawn ? { ...snap.spawn } : null;
  state.gate         = snap.gate  ? { ...snap.gate  } : null;
  state.end          = snap.end   ? { ...snap.end   } : null;
  state.ui           = snap.ui    ? { ...snap.ui    } : null;
  state.selection    = null;
  markDirty();
}

export function pushSnapshot() {
  past.push(levelSnapshot());
  if (past.length > MAX_HISTORY) past.shift();
  future.length = 0;
  state.unsaved = true;
}

export function undo() {
  if (!past.length) return;
  future.push(levelSnapshot());
  applySnapshot(past.pop());
}

export function redo() {
  if (!future.length) return;
  past.push(levelSnapshot());
  applySnapshot(future.pop());
}

export function clearHistory() {
  past.length = 0;
  future.length = 0;
}
