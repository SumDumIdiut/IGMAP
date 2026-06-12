export const state = {
  // Level data
  tiles: new Map(),          // "x,y,layer" → {x, y, layer}
  images: [],                // ImageEntry[]
  hitboxes: [],              // HitboxEntry[]
  boxes: [],                 // {x, y, upgrade, cap, price, group}[]
  swapTriggers: [],          // {x, y}[]
  checkpoints: [],           // {x, y}[]
  spawns: [],                // [{x, y, group}] – multiple spawn points
  gates:  [],                // [{x, y, group}]
  ends:   [],                // [{x, y, group}]
  uis:    [],                // [{x, y, group}]
  activeGroup: 0,            // group used when placing markers/boxes

  // Level-wide course settings
  baseReward: 10,
  cloneMult:  1.0,

  // Viewport
  panX: 0, panY: 5,
  zoom: 2.0,

  // Canvas refs (set by main.js)
  canvas: null,
  ctx: null,

  // Active tool
  activeTool: 'ground',      // see TOOLS list
  snapMode: false,

  // Image brush (used by ImageObj tool and properties panel)
  pendingImagePath: null,    // full Windows path string
  pendingImageW: 1,
  pendingImageH: 1,
  pendingImageRot: 0,
  pendingImageOpacity: 1,
  pendingImageCr: 1,
  pendingImageCg: 1,
  pendingImageCb: 1,
  pendingImageDepth: 0,

  // Box brush
  boxUpgrade: 2,
  boxCap: 30,
  boxFormula: '10',

  // Interaction state
  selection: null,           // {type:'image'|'hitbox'|'marker', index, tag?}
  ghostPos: null,            // {wx, wy} world pos under mouse

  hitboxDrag: null,          // {x0, y0} drag start for HitBox tool
  isPainting: false,         // tile paint stroke in progress

  isDragging: false,
  dragStartWorld: null,
  dragOrigPos: null,

  // Multi-selection (marquee)
  multiSel: [],              // [{type, index, markerType?}] or [{type:'tile', key}]
  multiSelOrigPos: [],       // [{x,y}] or [{x,y,layer}] snap-shots when drag starts
  multiSelDragStart: null,   // {wx,wy}
  multiSelTileDelta: {dx:0, dy:0}, // integer grid delta applied to tile items during drag
  marquee: null,             // {x0,y0,x1,y1} world coords while drawing

  isPanning: false,
  panStartMouse: null,       // {mx, my}
  panStartPan: null,         // {panX, panY}

  dirtyRender: true,
  unsaved: false,

  // Current map filename (without .json extension)
  currentMapName: 'custommap',

  // Sprite manifest from server
  manifest: null,
  spriteDir: '',

  // Currently highlighted gallery item
  gallerySelected: null,     // {groupName, filename, url}
};

export function markDirty() {
  state.dirtyRender = true;
}
