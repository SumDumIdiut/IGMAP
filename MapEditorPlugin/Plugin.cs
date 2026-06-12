using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Tilemaps;

namespace Doorstop { public class Entrypoint { public static void Start() { } } }

namespace IGTAPMapEditor
{
    internal static class Bootstrap
    {
        [UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Init()
        {
            try
            {
                var go = new UnityEngine.GameObject("[MapEditor]");
                UnityEngine.Object.DontDestroyOnLoad(go);
                go.AddComponent<MapEditorBehaviour>();
                UnityEngine.Debug.Log("[MapEditor] loaded — F10 to open.");
            }
            catch (Exception ex) { UnityEngine.Debug.LogError("[MapEditor] Init failed: " + ex); }
        }
    }
}

namespace IGTAPMapEditor
{
    [Serializable] public class TileEntry        { public int x, y, layer, rot; public string sprite = ""; }
    [Serializable] public class BoxEntry         { public float x, y; public int upgrade, cap; public double price; }
    [Serializable] public class SwapTriggerEntry { public float x, y; }
    [Serializable] public class CheckpointEntry  { public float x, y; }
    [Serializable] public class HitboxEntry      { public float x, y, w, h; }
    [Serializable] public class ImageEntry       { public string path = ""; public float x, y, w = 1f, h = 1f, rot, opacity = 1f, cr = 1f, cg = 1f, cb = 1f; public int depth; }

    [Serializable]
    public class MapFile
    {
        public int version = 3;
        public float baseReward;   // web editor course settings — 0 = not set
        public float cloneMult;
        public float spawnX, spawnY, gateX, gateY, endX, endY, uiX, uiY;
        public BoxEntry[]         boxes        = new BoxEntry[0];
        public SwapTriggerEntry[] swapTriggers = new SwapTriggerEntry[0];
        public CheckpointEntry[]  checkpoints  = new CheckpointEntry[0];
        public HitboxEntry[]      hitboxes     = new HitboxEntry[0];
        public ImageEntry[]       images       = new ImageEntry[0];
        public TileEntry[]        tiles        = new TileEntry[0];
    }

    public enum EditorTool { Select, Tile, Erase, Spawn, Gate, End, Checkpoint, SwapTrigger, Hitbox, Box, UIMarker, Image }

    public class MapEditorBehaviour : MonoBehaviour
    {
        private struct BoxPlacement  { public float x, y; public int upgrade, cap; public double price; }
        private struct ImagePlacement { public float x, y, w, h, rot, opacity, r, g, b; public string path; }

        private enum SelType { None, Tile, Spawn, Gate, End, UIMarker, Checkpoint, SwapTrigger, Hitbox, Box, Image }

        // ── map data ──────────────────────────────────────────────────────────
        private Dictionary<Vector2Int, int> _tiles        = new Dictionary<Vector2Int, int>();
        private Vector2?                    _spawn, _gate, _end, _uiMarker;
        private List<Vector2>               _checkpoints  = new List<Vector2>();
        private List<Vector2>               _swapTriggers = new List<Vector2>();
        private List<Rect>                  _hitboxes     = new List<Rect>();
        private List<BoxPlacement>          _boxes        = new List<BoxPlacement>();
        private List<ImagePlacement>        _images       = new List<ImagePlacement>();

        // ── selection ─────────────────────────────────────────────────────────
        private SelType    _selType       = SelType.None;
        private int        _selIdx        = -1;
        private Vector2Int _selTilePos;
        private bool       _selDragging;
        private Vector2Int _selDragLastGrid;

        // ── selection sync (avoid IMGUI text-field reset while typing) ────────
        private SelType _lastSelType    = SelType.None;
        private int     _lastSelIdx     = -2;
        private int     _editBoxUpg;
        private string  _editBoxCap     = "", _editBoxPrice = "";
        private string  _editImgPath    = "";
        private string  _editImgW       = "", _editImgH = "", _editImgRot = "", _editImgOp = "";
        private string  _editImgR       = "", _editImgG = "", _editImgB = "";

        // ── new-object defaults (left panel) ──────────────────────────────────
        private int    _newBoxUpg       = 2;
        private string _newBoxCap       = "10", _newBoxPrice = "100";
        private string _newImgPath      = "";
        private string _newImgW         = "3", _newImgH = "3";

        // ── editor state ──────────────────────────────────────────────────────
        private bool       _open, _panning, _hbDrag;
        private EditorTool _tool    = EditorTool.Tile;
        private int        _layer   = 0;
        private string     _mapName = "mymap";

        // ── view ──────────────────────────────────────────────────────────────
        private float _originX, _originY, _zoom = 2f;
        private const float CELL = 18f;

        // ── pan ───────────────────────────────────────────────────────────────
        private Vector2 _panMouse0;
        private float   _panOX0, _panOY0;

        // ── hitbox drag ───────────────────────────────────────────────────────
        private Vector2Int _hbStart;
        private Rect       _hbPrev;

        // ── undo ──────────────────────────────────────────────────────────────
        private readonly List<string> _undoList = new List<string>();
        private readonly List<string> _redoList = new List<string>();
        private const int MAX_UNDO = 30;

        // ── map list ──────────────────────────────────────────────────────────
        private bool     _showList;
        private string[] _listFiles = new string[0];
        private Vector2  _listScroll;

        // ── overlay / status ──────────────────────────────────────────────────
        private string _overlayMsg = "", _statusMsg = "";
        private float  _overlayMsgTime, _statusTime;
        private string _activeMapPath;
        private Rect   _canvasRect;

        // ── web editor server ─────────────────────────────────────────────────
        private static System.Diagnostics.Process _editorServer;
        // Editor locations: the development folder wins when present (live
        // editing); otherwise use the copies the installer puts inside the game
        // folder (<game>\MapEditor\...) so an installed mod is self-contained.
        private static string GameRootDir => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        private const string DEV_EDITOR_DIR = @"C:\Users\macro\Downloads\IGTAP\IGMAP\igtap-editor";
        private static string EDITOR_DIR
        {
            get
            {
                if (Directory.Exists(DEV_EDITOR_DIR)) return DEV_EDITOR_DIR;
                return Path.Combine(GameRootDir, "MapEditor", "webeditor");
            }
        }
        private const  string EDITOR_URL = "http://localhost:3000";
        private bool _spritesExported;

        // ── textures ──────────────────────────────────────────────────────────
        private Texture2D[] _layerTex;
        private Texture2D _spawnTex, _gateTex, _endTex, _cpTex, _swTex;
        private Texture2D _boxTex, _selBoxTex, _uiTex;
        private Texture2D _hbFill, _hbEdge;
        private Texture2D _cursorTex, _eraseTex, _selHlTex, _imgPlaceholderTex;
        private Texture2D _darkBg, _panelBg, _topbarBg, _statusBg;
        private Texture2D _gridLine, _majorLine, _originLine, _divider;
        private bool _texOk;

        // ── image cache ───────────────────────────────────────────────────────
        private readonly Dictionary<string, Texture2D> _imgCache = new Dictionary<string, Texture2D>();

        // ── game sprites — tiles ─────────────────────────────────────────────
        private Sprite[] _layerSprite;
        private Rect[]   _layerUV;
        private Color[]  _layerTint;

        // ── game sprites — objects ────────────────────────────────────────────
        private Sprite _sprBox, _sprCp, _sprSw, _sprSpawn, _sprGate, _sprEnd;
        private Rect   _uvBox,  _uvCp,  _uvSw,  _uvSpawn,  _uvGate,  _uvEnd;

        // ── styles ────────────────────────────────────────────────────────────
        private GUIStyle _sBtn, _sBtnClose, _sBtnDanger, _sLabel, _sTitle, _sSectionHdr;
        private GUIStyle _sToolBtn, _sToolActive, _sField, _sFieldSmall;
        private bool _stylesOk;

        // ── layer config ──────────────────────────────────────────────────────
        private static readonly Color[] LayerCol = {
            new Color(0.62f, 0.62f, 0.66f),
            new Color(0.35f, 0.35f, 0.42f),
            new Color(0.88f, 0.15f, 0.15f),
            new Color(0.28f, 0.52f, 1.00f),
            new Color(1.00f, 0.48f, 0.08f),
            new Color(0.42f, 0.68f, 1.00f, 0.85f),
            new Color(1.00f, 0.58f, 0.15f, 0.85f),
        };
        private static readonly Color[] LayerTint = {
            Color.white,
            Color.white,
            Color.white,
            new Color(0.30f, 0.55f, 1.00f),
            new Color(1.00f, 0.50f, 0.10f),
            new Color(0.40f, 0.65f, 1.00f),
            new Color(1.00f, 0.60f, 0.20f),
        };
        private static readonly string[] LayerName = {
            "Ground", "Alt Ground", "Spike",
            "Blue Swap Blk", "Org Swap Blk",
            "Blue Swap Spk", "Org Swap Spk",
        };
        private static readonly string[] UpgradeLabel = {
            "GLOBAL",   "Movement", "CloneCount", "Cash/Loop",
            "FastCln%", "BigCln%",  "Prestige",   "CloneMult",
            "(Plural)", "GreenCln", "Breaker",     "CloneDust",
        };

        // ═════════════════════════════════════════════════════════════════════
        // Lifecycle
        // ═════════════════════════════════════════════════════════════════════
        private void Start()
        {
            BuildTextures();
            SceneManager.sceneLoaded += OnSceneLoaded;
            StartEditorServer();
            // The bootstrap runs AFTER the first scene loaded, so sceneLoaded
            // never fires for the initial scene — handle it here.
            if (SceneManager.GetActiveScene().name == "MainMenu")
                StartCoroutine(MainMenuIntegration());
        }

        private void OnDestroy() { SceneManager.sceneLoaded -= OnSceneLoaded; }

        private void OnApplicationQuit()
        {
            try { if (_electronProc != null && !_electronProc.HasExited) _electronProc.Kill(); } catch { }
            try { if (_editorServer != null && !_editorServer.HasExited) _editorServer.Kill(); } catch { }
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (scene.name == "MainMenu") { StartCoroutine(MainMenuIntegration()); return; }
            _spritesExported = false;
            StartCoroutine(ExtractAndExportCoroutine());
            if (_activeMapPath != null) StartCoroutine(ReloadMapsCoroutine(_activeMapPath));
            else if (Directory.Exists(SaveDir) && Directory.GetFiles(SaveDir, "*.json").Length > 0)
                StartCoroutine(SanitizeFreshLoadCoroutine());
        }

        // On a normal game start (no F10 reload) Saveloader calls TryLoad itself;
        // capture the original world here (sceneLoaded fires before Start) and
        // hide it once the custom map has spawned.
        private IEnumerator SanitizeFreshLoadCoroutine()
        {
            var beforeRen = new HashSet<Renderer>  (UnityEngine.Object.FindObjectsByType<Renderer>  (FindObjectsInactive.Include, FindObjectsSortMode.None));
            var beforeCol = new HashSet<Collider2D>(UnityEngine.Object.FindObjectsByType<Collider2D>(FindObjectsInactive.Include, FindObjectsSortMode.None));

            float deadline = Time.realtimeSinceStartup + 10f;
            while (GameObject.Find("CustomMapRoot") == null && Time.realtimeSinceStartup < deadline)
                yield return null;
            if (GameObject.Find("CustomMapRoot") == null) yield break;   // no custom map actually loaded
            yield return null;

            try
            {
                HideOriginalWorld(beforeRen, beforeCol);
                SanitizeCourseClones();
                ApplyTileOverrides();
                ApplyCourseSettings();
            }
            catch (Exception ex) { Debug.LogError("[MapEditor] SanitizeFreshLoad: " + ex); }
        }

        // SpawnTiles parses TileEntry.rot/.sprite from the JSON but never uses
        // them — apply both here. Rotation is set ABSOLUTELY (not stacked on the
        // vanilla 90° quirk) so the editor preview matches the game 1:1.
        private void ApplyTileOverrides()
        {
            try
            {
                var root = GameObject.Find("CustomMapRoot");
                if (root == null || !Directory.Exists(SaveDir)) return;

                var byName = new Dictionary<string, Transform>();
                foreach (Transform c in root.transform) if (!byName.ContainsKey(c.name)) byName[c.name] = c;

                var grid  = UnityEngine.Object.FindFirstObjectByType<Grid>();
                float cellW = grid != null ? grid.cellSize.x : 32f;
                float cellH = grid != null ? grid.cellSize.y : 32f;

                foreach (var f in Directory.GetFiles(SaveDir, "*.json"))
                {
                    MapFile m = null;
                    try { m = JsonUtility.FromJson<MapFile>(File.ReadAllText(f)); } catch { }
                    if (m?.tiles == null) continue;
                    string mapName = Path.GetFileNameWithoutExtension(f);
                    int applied = 0;
                    foreach (var t in m.tiles)
                    {
                        int rot = ((t.rot % 360) + 360) % 360;
                        bool hasSprite = !string.IsNullOrEmpty(t.sprite);
                        if (rot == 0 && !hasSprite) continue;
                        if (!byName.TryGetValue($"CTile_{mapName}_{t.x}_{t.y}", out var tr)) continue;

                        if (hasSprite)
                        {
                            var sr = tr.GetComponent<SpriteRenderer>();
                            var ns = ResolveTileSprite(t.sprite);
                            if (sr != null && ns != null)
                            {
                                sr.sprite = ns;
                                var b = ns.bounds.size;
                                if (b.x > 0f && b.y > 0f) tr.localScale = new Vector3(cellW / b.x, cellH / b.y, 1f);
                                // swap-block layers get a blue/orange tint from the
                                // loader — pack sprites are already colored, so undo
                                // the tint (keep alpha: BlockSwapListener drives it)
                                if (t.layer >= 3 && t.layer <= 6)
                                    sr.color = new Color(1f, 1f, 1f, sr.color.a);
                            }
                        }
                        tr.localRotation = Quaternion.Euler(0f, 0f, rot);
                        applied++;
                    }
                    if (applied > 0) Debug.Log($"[MapEditor] Applied {applied} tile overrides for '{mapName}'.");
                }
            }
            catch (Exception ex) { Debug.LogError("[MapEditor] ApplyTileOverrides: " + ex); }
        }

        // Apply the editor's per-map course settings (baseReward, cloneMult) —
        // CustomMapLoader's CustomMap class has no such fields, so the game
        // ignores them in the JSON; we inject them into courseScript instead.
        private void ApplyCourseSettings()
        {
            try
            {
                if (!Directory.Exists(SaveDir)) return;
                const BindingFlags BF = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                foreach (var f in Directory.GetFiles(SaveDir, "*.json"))
                {
                    MapFile m = null;
                    try { m = JsonUtility.FromJson<MapFile>(File.ReadAllText(f)); } catch { }
                    if (m == null || (m.baseReward <= 0f && m.cloneMult <= 0f)) continue;

                    var go = GameObject.Find("CustomCourse_" + Path.GetFileNameWithoutExtension(f));
                    var course = go != null ? go.GetComponent("courseScript") : null;
                    if (course == null) continue;
                    var ct = course.GetType();

                    if (m.baseReward > 0f)
                    {
                        ct.GetField("baseReward", BF)?.SetValue(course, (int)m.baseReward);
                        try { ct.GetMethod("UpdateReward", BF)?.Invoke(course, null); }
                        catch (Exception ex) { Debug.LogWarning("[MapEditor] UpdateReward: " + ex.Message); }
                    }

                    if (m.cloneMult > 0f)
                    {
                        // Displayed clone mult = 0.1 + globalLvls*0.2 + localLvls*0.1.
                        // Raise the local level so the course STARTS at the configured
                        // value; player purchases stack on top, and a prestige reset
                        // gets re-floored on the next map load.
                        double floorLevels = Math.Max(0.0, (m.cloneMult - 0.1) / 0.1);
                        var luField = ct.GetField("localUpgradesScript", BF);
                        var lu = luField?.GetValue(course) as Component;
                        var dictObj = lu?.GetType().GetField("localUpgradeDict", BF)?.GetValue(lu);
                        if (dictObj is System.Collections.IDictionary dict)
                        {
                            var enumType = dictObj.GetType().GetGenericArguments()[0];
                            object key = Enum.Parse(enumType, "cloneMult");
                            double cur = dict.Contains(key) ? Convert.ToDouble(dict[key]) : 0.0;
                            if (cur < floorLevels) dict[key] = floorLevels;
                        }
                    }

                    try { ct.GetMethod("fullUpdate", BF)?.Invoke(course, null); } catch { }
                    Debug.Log($"[MapEditor] Course settings applied: baseReward={m.baseReward} cloneMult={m.cloneMult}");
                }
            }
            catch (Exception ex) { Debug.LogError("[MapEditor] ApplyCourseSettings: " + ex); }
        }

        // Resolve a tile sprite by runtime name: use the loaded sprite if present,
        // otherwise build one from the editor sprite dump (filenames there match
        // runtime sprite names and hold the art in its true, unrotated orientation).
        private readonly Dictionary<string, Sprite> _tileSpriteCache = new Dictionary<string, Sprite>();
        private Sprite ResolveTileSprite(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            Sprite cached;
            if (_tileSpriteCache.TryGetValue(name, out cached) && cached != null) return cached;

            Sprite found = null;
            foreach (var s in Resources.FindObjectsOfTypeAll<Sprite>())
                if (s != null && s.name == name) { found = s; break; }

            if (found == null)
            {
                string p = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "IGTAPEditor", "sprites", name + ".png");
                if (File.Exists(p))
                {
                    var tex = LoadImageTex(p);
                    if (tex != null)
                    {
                        tex.filterMode = FilterMode.Point;
                        found = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height),
                                              new Vector2(0.5f, 0.5f), Mathf.Max(1, tex.width));
                    }
                }
            }
            if (found == null) Debug.LogWarning("[MapEditor] Tile sprite not found: " + name);
            _tileSpriteCache[name] = found;
            return found;
        }

        // Hide everything that existed before TryLoad ran (the original world),
        // keeping the player and the scrolling background.
        private void HideOriginalWorld(HashSet<Renderer> beforeRen, HashSet<Collider2D> beforeCol)
        {
            var keep    = new HashSet<Renderer>();
            var keepCol = new HashSet<Collider2D>();
            try {
                foreach (var p in GameObject.FindGameObjectsWithTag("Player")) {
                    foreach (var rr in p.GetComponentsInChildren<Renderer>  (true)) keep   .Add(rr);
                    foreach (var c  in p.GetComponentsInChildren<Collider2D>(true)) keepCol.Add(c);
                }
            } catch { }

            foreach (var rr in beforeRen)
            {
                if (rr == null || keep.Contains(rr)) continue;
                if (rr.GetComponent("backgroundScroller") != null) continue;
                rr.enabled = false;
            }
            foreach (var col in beforeCol)
            {
                if (col == null || keepCol.Contains(col)) continue;
                col.enabled = false;
            }
            foreach (var tmc in UnityEngine.Object.FindObjectsByType<TilemapCollider2D>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                tmc.enabled = false;
        }

        // ActivateCourse clones a full original course and hides its renderers,
        // but leaves every collider alive — including the course's invisible
        // spikes/obstacles at their original world position. Keep only the
        // colliders the custom course needs: gates and upgrade-box triggers.
        private void SanitizeCourseClones()
        {
            int stripped = 0;
            foreach (var go in UnityEngine.Object.FindObjectsByType<GameObject>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (go == null || !go.name.StartsWith("CustomCourse_")) continue;
                foreach (var col in go.GetComponentsInChildren<Collider2D>(true))
                {
                    if (col == null) continue;
                    bool keep = false;
                    var t = col.transform;
                    while (t != null)
                    {
                        if (t.GetComponent("startGate") != null || t.GetComponent("endGate") != null ||
                            t.GetComponent("upgradeBox") != null) { keep = true; break; }
                        if (t.gameObject == go) break;
                        t = t.parent;
                    }
                    // Gates/boxes must be force-ENABLED: the clone inherits the
                    // template's colliders, which HideOriginalWorld disabled on a
                    // previous load — leaving cloned gate/box triggers dead.
                    if (keep) col.enabled = true;
                    else if (col.enabled) { col.enabled = false; stripped++; }
                }

                // The clonesLoD trigger (which flips clones visible when the player
                // is near the ORIGINAL course location) is stripped above and would
                // never fire at the custom map's position anyway — force clones on.
                foreach (var comp in go.GetComponentsInChildren<Component>(true))
                {
                    if (comp == null) continue;
                    string typeName = comp.GetType().Name;
                    if (typeName == "clonesScript")
                    {
                        try
                        {
                            comp.GetType().GetMethod("isOnScreen", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                                ?.Invoke(comp, new object[] { true });
                            Debug.Log("[MapEditor] Forced clonesScript.isOnScreen(true) for " + go.name);
                        }
                        catch (Exception ex) { Debug.LogError("[MapEditor] isOnScreen: " + ex); }
                    }
                    else if (typeName == "upgradeBox")
                    {
                        // deactivate() greys a box but ApplyLoadedState() never restores
                        // the colors for active boxes — template slots that started
                        // deactivated stay grey forever. Restore visuals on active boxes.
                        try { RestoreBoxVisuals(comp); }
                        catch (Exception ex) { Debug.LogError("[MapEditor] RestoreBoxVisuals: " + ex); }
                    }
                }
            }
            if (stripped > 0) Debug.Log($"[MapEditor] Stripped {stripped} leftover course-template colliders.");
        }

        // Undo deactivate()'s grey tint on a box that is currently active:
        // root/box sprite back to the active sprite, all display colors to white.
        private static void RestoreBoxVisuals(Component box)
        {
            // The clone inherits the template's renderer state, which a previous
            // HideOriginalWorld pass may have disabled — re-enable unconditionally.
            foreach (var rr in box.GetComponentsInChildren<Renderer>(true))
                if (rr != null) rr.enabled = true;

            var t = box.GetType();
            var isActiveField = t.GetField("isActive", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (isActiveField == null || !(isActiveField.GetValue(box) is bool active) || !active) return;

            var spritesField = t.GetField("Sprites", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var sprites = spritesField?.GetValue(box) as Sprite[];
            Sprite activeSprite = (sprites != null && sprites.Length > 1) ? sprites[1] : null;

            var rootSr = box.GetComponent<SpriteRenderer>();
            if (rootSr != null)
            {
                if (activeSprite != null) rootSr.sprite = activeSprite;
                rootSr.color = Color.white;
            }
            SetMemberSprite(t, box, "boxSprite", activeSprite);
            SetMemberColorWhite(t, box, "boxSprite");
            SetMemberColorWhite(t, box, "upgradeNameDisplay");
            SetMemberColorWhite(t, box, "tierEffectDisplay");
            SetMemberColorWhite(t, box, "buttonSprite");
        }

        private static void SetMemberColorWhite(Type t, object box, string fieldName)
        {
            var f = t.GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var obj = f?.GetValue(box);
            if (obj == null) return;
            obj.GetType().GetProperty("color")?.SetValue(obj, Color.white, null);
        }

        private static void SetMemberSprite(Type t, object box, string fieldName, Sprite sprite)
        {
            if (sprite == null) return;
            var f = t.GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var obj = f?.GetValue(box);
            if (obj == null) return;
            obj.GetType().GetProperty("sprite")?.SetValue(obj, sprite, null);
        }

        private IEnumerator ExtractAndExportCoroutine()
        {
            yield return null; // let scene objects settle
            try { TryExtractGameSprites(); }
            catch (Exception ex) { Debug.LogError("[MapEditor] ExtractSprites: " + ex); yield break; }

            if (!_spritesExported && _layerSprite != null && _layerSprite[0] != null)
            {
                _spritesExported = true;
                yield return StartCoroutine(ExportSpritesCoroutine());
            }
        }

        private void Update()
        {
            if (_statusTime     > 0f) _statusTime     -= Time.unscaledDeltaTime;
            if (_overlayMsgTime > 0f) _overlayMsgTime -= Time.unscaledDeltaTime;

            // F10 via Win32 — also fires while the embedded editor has focus
            bool f10 = (GetAsyncKeyState(VK_F10) & 0x8000) != 0;
            if (f10 && !_f10Held) ToggleEditorHotkey();
            _f10Held = f10;
            ResizeElectron();

            // the editor's ✕ Close button drops a flag file via the server
            if (_elVisible && File.Exists(CloseFlagPath))
            {
                try { File.Delete(CloseFlagPath); } catch { }
                SetElectronVisible(false);
            }

            // keep keyboard focus on the editor while it's open — clicks on the
            // game window edges (or Unity itself) can pull focus back, after
            // which all keybinds silently go to the paused game instead
            _focusTimer += Time.unscaledDeltaTime;
            if (_elVisible && _focusTimer > 0.3f && _electronHwnd != IntPtr.Zero)
            {
                _focusTimer = 0f;
                try
                {
                    IntPtr gameHwnd = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
                    if (GetForegroundWindow() == gameHwnd)
                    {
                        IntPtr f = GetFocus();
                        if (f != _electronHwnd && !IsChild(_electronHwnd, f))
                            SetFocus(_electronHwnd);
                    }
                }
                catch { }
            }

            if (_open && GUIUtility.keyboardControl == 0)
            {
                float speed = CELL * _zoom * 12f * Time.unscaledDeltaTime;
                if (Input.GetKey(KeyCode.LeftArrow))  _originX += speed;
                if (Input.GetKey(KeyCode.RightArrow)) _originX -= speed;
                if (Input.GetKey(KeyCode.UpArrow))    _originY += speed;
                if (Input.GetKey(KeyCode.DownArrow))  _originY -= speed;
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        // Embedded web editor — the Electron editor's window re-parented INTO
        // the game window via Win32 SetParent. Out-of-process: a failure there
        // can never crash the game (unlike in-process WebView2, whose COM layer
        // crashes under Mono). The IMGUI editor remains as a fallback.
        // ═════════════════════════════════════════════════════════════════════
        private static string ELECTRON_EXE
        {
            get
            {
                const string dev = @"C:\Users\macro\Downloads\IGTAP\IGMAP\MapEditor\electron-dev-removed.exe";
                if (File.Exists(dev)) return dev;
                return Path.Combine(GameRootDir, "MapEditor", "electron", "IGTAP Map Editor.exe");
            }
        }

        private System.Diagnostics.Process _electronProc;
        private IntPtr _electronHwnd = IntPtr.Zero;
        private bool _elVisible, _elLaunching, _f10Held;
        private int  _elW, _elH;
        private float _focusTimer;

        [DllImport("user32.dll")] private static extern short  GetAsyncKeyState(int vKey);
        [DllImport("user32.dll")] private static extern IntPtr SetFocus(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);
        [DllImport("user32.dll")] private static extern bool   MoveWindow(IntPtr hWnd, int x, int y, int w, int h, bool repaint);
        [DllImport("user32.dll")] private static extern bool   ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")] private static extern bool   IsWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool   GetClientRect(IntPtr hWnd, out RECT rect);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")] private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")] private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
        [DllImport("user32.dll")] private static extern IntPtr GetFocus();
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool   IsChild(IntPtr hWndParent, IntPtr hWnd);
        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int L, T, R, B; }

        private static string CloseFlagPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "IGTAPEditor", "close.flag");

        private const int  VK_F10 = 0x79;
        private const int  GWL_STYLE = -16;
        private const long WS_CHILD = 0x40000000L, WS_POPUP = 0x80000000L, WS_CAPTION = 0x00C00000L, WS_THICKFRAME = 0x00040000L;
        private const int  SW_HIDE = 0, SW_SHOW = 5;

        private void ToggleEditorHotkey()
        {
            if (_electronHwnd != IntPtr.Zero && IsWindow(_electronHwnd)) { SetElectronVisible(!_elVisible); return; }
            if (_elLaunching) return;
            if (!File.Exists(ELECTRON_EXE)) { Toggle(); return; }   // classic IMGUI editor as fallback
            StartCoroutine(LaunchElectronCoroutine());
        }

        private IEnumerator LaunchElectronCoroutine()
        {
            _elLaunching = true;
            _overlayMsg = "Starting editor…"; _overlayMsgTime = 6f;
            try
            {
                if (_electronProc == null || _electronProc.HasExited)
                    _electronProc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = ELECTRON_EXE,
                        WorkingDirectory = Path.GetDirectoryName(ELECTRON_EXE),
                        UseShellExecute = true,
                    });
            }
            catch (Exception ex)
            {
                Debug.LogError("[MapEditor] Electron launch failed: " + ex.Message);
                _elLaunching = false;
                Toggle();
                yield break;
            }

            // wait for the editor window to appear
            float deadline = Time.realtimeSinceStartup + 20f;
            IntPtr hwnd = IntPtr.Zero;
            while (Time.realtimeSinceStartup < deadline)
            {
                try
                {
                    _electronProc.Refresh();
                    if (_electronProc.HasExited) break;
                    hwnd = _electronProc.MainWindowHandle;
                }
                catch { }
                if (hwnd != IntPtr.Zero) break;
                yield return new WaitForSecondsRealtime(0.2f);
            }

            _elLaunching = false;
            if (hwnd == IntPtr.Zero)
            {
                Debug.LogError("[MapEditor] Electron window never appeared — using classic editor.");
                Toggle();
                yield break;
            }

            // strip the frame and graft it into the game window
            try
            {
                IntPtr gameHwnd = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
                long style = (long)GetWindowLongPtr(hwnd, GWL_STYLE);
                style = (style & ~(WS_POPUP | WS_CAPTION | WS_THICKFRAME)) | WS_CHILD;
                SetWindowLongPtr(hwnd, GWL_STYLE, (IntPtr)style);
                SetParent(hwnd, gameHwnd);
                // Cross-process child windows don't share focus/activation state —
                // attach the two input queues so SetFocus works and the editor
                // actually receives keyboard input (keybinds, Ctrl+S, typing).
                uint gamePid, elPid;
                uint gameThread = GetWindowThreadProcessId(gameHwnd, out gamePid);
                uint elThread   = GetWindowThreadProcessId(hwnd, out elPid);
                if (gameThread != elThread) AttachThreadInput(gameThread, elThread, true);
                _electronHwnd = hwnd;
                SetElectronVisible(true);
                Debug.Log("[MapEditor] Electron editor embedded into game window.");
            }
            catch (Exception ex)
            {
                Debug.LogError("[MapEditor] Embed failed: " + ex);
                Toggle();
            }
        }

        private void SetElectronVisible(bool visible)
        {
            _elVisible = visible;
            try
            {
                if (visible) try { File.Delete(CloseFlagPath); } catch { }   // clear stale close requests
                ShowWindow(_electronHwnd, visible ? SW_SHOW : SW_HIDE);
                if (visible) { ResizeElectron(true); SetFocus(_electronHwnd); }
                else SetFocus(System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle);
            }
            catch (Exception ex) { Debug.LogWarning("[MapEditor] Electron visibility: " + ex.Message); }
            Time.timeScale = visible ? 0f : 1f;
            if (visible) { Cursor.lockState = CursorLockMode.None; Cursor.visible = true; }
        }

        private void ResizeElectron(bool force = false)
        {
            if (_electronHwnd == IntPtr.Zero || !_elVisible) return;
            try
            {
                RECT rc;
                IntPtr gameHwnd = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
                if (!GetClientRect(gameHwnd, out rc)) return;
                int w = rc.R - rc.L, h = rc.B - rc.T;
                if (!force && w == _elW && h == _elH) return;
                _elW = w; _elH = h;
                MoveWindow(_electronHwnd, 0, 0, w, h, true);
            }
            catch { }
        }

        private void Toggle()
        {
            _open = !_open;
            Time.timeScale = _open ? 0f : 1f;
            if (_open)
            {
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
                if (_originX == 0f && _originY == 0f) CenterView();
                // Retry sprite extraction every time the editor opens in case scene changed
                if (SceneManager.GetActiveScene().name != "MainMenu")
                    StartCoroutine(ExtractSpritesCoroutine());
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        // OnGUI entry
        // ═════════════════════════════════════════════════════════════════════
        private void OnGUI()
        {
            if (!_texOk) return;
            EnsureStyles();
            // (F10 handled globally in Update via GetAsyncKeyState)
            Matrix4x4 saved = GUI.matrix;
            GUI.matrix = Matrix4x4.identity;
            if (!_open) DrawOpenHint();
            else        DrawEditor();
            GUI.matrix = saved;
        }

        // ── overlay (editor closed) — the old mod nav buttons are gone; only
        // the transient status message remains (e.g. "Loading map…")
        private void DrawOpenHint()
        {
            if (_overlayMsgTime > 0f)
            {
                float w = 280f, x = Screen.width - w - 12f, y = 12f;
                GUI.DrawTexture(new Rect(x, y, w, 24f), _panelBg);
                GUI.Label(new Rect(x + 6, y + 4, w - 12, 16), _overlayMsg, _sLabel);
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        // Editor layout
        // ═════════════════════════════════════════════════════════════════════
        private const float TB_H = 44f, SB_H = 26f, LP_W = 178f, RP_W = 176f;

        private void DrawEditor()
        {
            float sw = Screen.width, sh = Screen.height;
            GUI.DrawTexture(new Rect(0, 0, sw, sh), _darkBg, ScaleMode.StretchToFill);
            DrawTopbar(sw);
            float bodyY = TB_H, bodyH = sh - TB_H - SB_H;
            DrawLeftPanel (new Rect(0,         bodyY, LP_W, bodyH));
            DrawRightPanel(new Rect(sw - RP_W, bodyY, RP_W, bodyH));
            _canvasRect = new Rect(LP_W, bodyY, sw - LP_W - RP_W, bodyH);
            DrawCanvas(_canvasRect);
            DrawStatusBar(new Rect(0, sh - SB_H, sw, SB_H));
        }

        private void DrawTopbar(float sw)
        {
            GUI.DrawTexture(new Rect(0, 0, sw, TB_H), _topbarBg, ScaleMode.StretchToFill);
            float x = 8f, y = 8f, bh = 28f;
            Color prev = GUI.backgroundColor;
            GUI.backgroundColor = new Color(0.65f, 0.18f, 0.18f);
            if (GUI.Button(new Rect(x, y, 62, bh), "✕ Close", _sBtnClose)) Toggle();
            GUI.backgroundColor = prev; x += 70;
            if (GUI.Button(new Rect(x, y, 50, bh), "New",  _sBtn)) { if (ConfirmIfDirty()) NewMap(); } x += 57;
            if (GUI.Button(new Rect(x, y, 50, bh), "Load", _sBtn)) { _showList = !_showList; if (_showList) RefreshList(); } x += 57;
            if (GUI.Button(new Rect(x, y, 50, bh), "Save", _sBtn)) SaveMap(); x += 57;
            // Ctrl+S hint
            GUI.Label(new Rect(x, y + 2, 50, bh), "Name:", _sLabel); x += 50;
            _mapName = GUI.TextField(new Rect(x, y, 148, bh), _mapName, _sField); x += 156;
            // Undo / Redo buttons
            GUI.backgroundColor = new Color(0.22f, 0.32f, 0.48f);
            if (GUI.Button(new Rect(x, y, 36, bh), "↩", _sBtn) || (Event.current.type == EventType.Used && false)) { } // placeholder, handled in input
            x += 40;
            if (GUI.Button(new Rect(x, y, 36, bh), "↪", _sBtn)) { } // placeholder
            GUI.backgroundColor = prev; x += 44;
            if (_statusTime > 0f) GUI.Label(new Rect(x, y + 5, 380, bh), _statusMsg, _sLabel);
        }

        // ═════════════════════════════════════════════════════════════════════
        // Left panel
        // ═════════════════════════════════════════════════════════════════════
        private void DrawLeftPanel(Rect r)
        {
            GUI.DrawTexture(r, _panelBg, ScaleMode.StretchToFill);
            float x = r.x + 6f, y = r.y + 6f, bw = r.width - 12f, bh = 24f, gap = 2f;

            SectionHeader(x, ref y, bw, "TOOLS");
            DrawToolBtn(x, ref y, bw, bh, gap, EditorTool.Select,       "  Select          V");
            DrawToolBtn(x, ref y, bw, bh, gap, EditorTool.Tile,         "  Tile             T");
            DrawToolBtn(x, ref y, bw, bh, gap, EditorTool.Erase,        "  Erase            E");

            Divider(x, ref y, bw);
            SectionHeader(x, ref y, bw, "PLACE");
            DrawToolBtn(x, ref y, bw, bh, gap, EditorTool.Spawn,        "  Spawn            S");
            DrawToolBtn(x, ref y, bw, bh, gap, EditorTool.Gate,         "  Gate             G");
            DrawToolBtn(x, ref y, bw, bh, gap, EditorTool.End,          "  End              N");
            DrawToolBtn(x, ref y, bw, bh, gap, EditorTool.UIMarker,     "  UI Marker        U");

            Divider(x, ref y, bw);
            SectionHeader(x, ref y, bw, "OBJECTS");
            DrawToolBtn(x, ref y, bw, bh, gap, EditorTool.Checkpoint,   "  Checkpoint       C");
            DrawToolBtn(x, ref y, bw, bh, gap, EditorTool.SwapTrigger,  "  Swap Trigger     W");
            DrawToolBtn(x, ref y, bw, bh, gap, EditorTool.Hitbox,       "  Hitbox           H");
            DrawToolBtn(x, ref y, bw, bh, gap, EditorTool.Box,          "  Upgrade Box      B");
            DrawToolBtn(x, ref y, bw, bh, gap, EditorTool.Image,        "  Image            I");

            Divider(x, ref y, bw);

            if (_tool == EditorTool.Tile)
            {
                SectionHeader(x, ref y, bw, "LAYER");
                for (int i = 0; i < LayerName.Length; i++)
                {
                    bool active = _layer == i;
                    DrawLayerTile(new Rect(x + 2, y + 2, bh - 4, bh - 4), i);
                    if (GUI.Button(new Rect(x + bh, y, bw - bh, bh), $"[{i}]  {LayerName[i]}", active ? _sToolActive : _sToolBtn))
                        _layer = i;
                    y += bh + gap;
                }
            }
            else if (_tool == EditorTool.Box)
            {
                SectionHeader(x, ref y, bw, "NEW BOX");
                DrawBoxConfig(x, ref y, bw, bh, gap, ref _newBoxUpg, ref _newBoxCap, ref _newBoxPrice);
            }
            else if (_tool == EditorTool.Image)
            {
                SectionHeader(x, ref y, bw, "NEW IMAGE");
                GUI.Label(new Rect(x, y, bw, 14), "Path:", _sTitle); y += 16;
                _newImgPath = GUI.TextField(new Rect(x, y, bw, 22), _newImgPath, _sField); y += 26;
                float hw = (bw - 6f) / 2f;
                GUI.Label(new Rect(x, y, 20, 20), "W", _sLabel);
                _newImgW = GUI.TextField(new Rect(x + 20, y, hw - 20, 22), _newImgW, _sFieldSmall);
                GUI.Label(new Rect(x + hw + 6, y, 20, 20), "H", _sLabel);
                _newImgH = GUI.TextField(new Rect(x + hw + 26, y, hw - 20, 22), _newImgH, _sFieldSmall);
                y += 26;
            }
        }

        private void DrawToolBtn(float x, ref float y, float bw, float bh, float gap, EditorTool t, string label)
        {
            bool active = _tool == t;
            if (GUI.Button(new Rect(x, y, bw, bh), label, active ? _sToolActive : _sToolBtn))
            { _tool = t; if (t != EditorTool.Select) { _selType = SelType.None; _selIdx = -1; } }
            y += bh + gap;
        }

        private void DrawBoxConfig(float x, ref float y, float bw, float bh, float gap,
                                   ref int upgrade, ref string capStr, ref string priceStr)
        {
            GUI.Label(new Rect(x, y, bw, 14), "Upgrade Type:", _sTitle); y += 16;
            float btnW = (bw - 4f) / 2f;
            int rows = (UpgradeLabel.Length + 1) / 2;
            for (int i = 0; i < UpgradeLabel.Length; i++)
            {
                int row = i / 2, col = i % 2;
                if (GUI.Button(new Rect(x + col * (btnW + 4f), y + row * (bh + gap), btnW, bh),
                        UpgradeLabel[i], upgrade == i ? _sToolActive : _sToolBtn))
                    upgrade = i;
            }
            y += rows * (bh + gap) + 4f;
            GUI.Label(new Rect(x, y, 32, 22), "Cap:", _sLabel);
            capStr   = GUI.TextField(new Rect(x + 32, y, bw - 32, 22), capStr, _sField); y += 26;
            GUI.Label(new Rect(x, y, 42, 22), "Price:", _sLabel);
            priceStr = GUI.TextField(new Rect(x + 42, y, bw - 42, 22), priceStr, _sField); y += 26;
        }

        private void SectionHeader(float x, ref float y, float bw, string text)
        {
            GUI.Label(new Rect(x, y, bw, 14), text, _sSectionHdr); y += 16;
        }

        private void Divider(float x, ref float y, float bw)
        {
            GUI.DrawTexture(new Rect(x, y + 3, bw, 1), _divider); y += 8;
        }

        // ═════════════════════════════════════════════════════════════════════
        // Right panel
        // ═════════════════════════════════════════════════════════════════════
        private void DrawRightPanel(Rect r)
        {
            GUI.DrawTexture(r, _panelBg, ScaleMode.StretchToFill);
            float x = r.x + 6f, y = r.y + 6f, bw = r.width - 12f;

            // File list overlay
            if (_showList)
            {
                SectionHeader(x, ref y, bw, "MAPS");
                Color prev = GUI.backgroundColor;
                GUI.backgroundColor = new Color(0.22f, 0.32f, 0.48f);
                if (GUI.Button(new Rect(x, y, bw, 24), "Refresh", _sBtn)) RefreshList(); y += 28;
                GUI.backgroundColor = new Color(0.65f, 0.18f, 0.18f);
                if (GUI.Button(new Rect(x, y, bw, 24), "Close List", _sBtn)) _showList = false; y += 28;
                GUI.backgroundColor = prev;
                float listH = r.yMax - y - 6;
                _listScroll = GUI.BeginScrollView(new Rect(x, y, bw, listH), _listScroll,
                    new Rect(0, 0, bw - 16, Mathf.Max(_listFiles.Length * 28f, listH)));
                for (int i = 0; i < _listFiles.Length; i++)
                    if (GUI.Button(new Rect(0, i * 28, bw - 16, 24), Path.GetFileNameWithoutExtension(_listFiles[i]), _sToolBtn))
                    { LoadFile(_listFiles[i]); _showList = false; }
                GUI.EndScrollView();
                return;
            }

            // Sync edit fields on selection change
            if (_selType != _lastSelType || _selIdx != _lastSelIdx)
            {
                _lastSelType = _selType;
                _lastSelIdx  = _selIdx;
                SyncEditState();
            }

            // Selection-specific panels
            if (_selType == SelType.Box && _selIdx >= 0 && _selIdx < _boxes.Count)
            {
                var b = _boxes[_selIdx];
                SectionHeader(x, ref y, bw, $"BOX #{_selIdx}");
                GUI.Label(new Rect(x, y, bw, 14), $"Pos: ({b.x:F0}, {b.y:F0})", _sTitle); y += 18;
                DrawBoxConfig(x, ref y, bw, 22f, 2f, ref _editBoxUpg, ref _editBoxCap, ref _editBoxPrice);
                int.TryParse(_editBoxCap, out int ec); if (ec < 0) ec = 0;
                double.TryParse(_editBoxPrice, out double ep); if (ep < 0) ep = 0;
                _boxes[_selIdx] = new BoxPlacement { x = b.x, y = b.y, upgrade = _editBoxUpg, cap = ec, price = ep };
                Divider(x, ref y, bw);
                Color prev = GUI.backgroundColor;
                GUI.backgroundColor = new Color(0.65f, 0.18f, 0.18f);
                if (GUI.Button(new Rect(x, y, bw, 24), "Delete Box  [Del]", _sBtn))
                { PushUndo(); _boxes.RemoveAt(_selIdx); _selType = SelType.None; _selIdx = -1; }
                GUI.backgroundColor = prev;
                return;
            }

            if (_selType == SelType.Hitbox && _selIdx >= 0 && _selIdx < _hitboxes.Count)
            {
                var hb = _hitboxes[_selIdx];
                SectionHeader(x, ref y, bw, $"HITBOX #{_selIdx}");
                GUI.Label(new Rect(x, y, bw, 14), $"Pos: ({hb.x:F0}, {hb.y:F0})", _sTitle); y += 18;
                GUI.Label(new Rect(x, y, 22, 22), "W:", _sLabel);
                if (float.TryParse(GUI.TextField(new Rect(x + 22, y, bw - 22, 22), hb.width.ToString("F1"), _sField), out float nw) && nw > 0)
                    _hitboxes[_selIdx] = new Rect(hb.x, hb.y, nw, hb.height); y += 26;
                GUI.Label(new Rect(x, y, 22, 22), "H:", _sLabel);
                if (float.TryParse(GUI.TextField(new Rect(x + 22, y, bw - 22, 22), hb.height.ToString("F1"), _sField), out float nh) && nh > 0)
                    _hitboxes[_selIdx] = new Rect(hb.x, hb.y, hb.width, nh); y += 26;
                Divider(x, ref y, bw);
                Color prev = GUI.backgroundColor;
                GUI.backgroundColor = new Color(0.65f, 0.18f, 0.18f);
                if (GUI.Button(new Rect(x, y, bw, 24), "Delete Hitbox  [Del]", _sBtn))
                { PushUndo(); _hitboxes.RemoveAt(_selIdx); _selType = SelType.None; _selIdx = -1; }
                GUI.backgroundColor = prev;
                return;
            }

            if (_selType == SelType.Image && _selIdx >= 0 && _selIdx < _images.Count)
            {
                var img = _images[_selIdx];
                SectionHeader(x, ref y, bw, $"IMAGE #{_selIdx}");
                GUI.Label(new Rect(x, y, bw, 14), "Path:", _sTitle); y += 16;
                _editImgPath = GUI.TextField(new Rect(x, y, bw, 22), _editImgPath, _sField); y += 26;
                float hw = (bw - 6f) / 2f;
                GUI.Label(new Rect(x, y, 20, 20), "W", _sLabel);
                _editImgW = GUI.TextField(new Rect(x + 20, y, hw - 20, 22), _editImgW, _sFieldSmall);
                GUI.Label(new Rect(x + hw + 6, y, 20, 20), "H", _sLabel);
                _editImgH = GUI.TextField(new Rect(x + hw + 26, y, hw - 20, 22), _editImgH, _sFieldSmall); y += 26;
                GUI.Label(new Rect(x, y, 30, 20), "Rot", _sLabel);
                _editImgRot = GUI.TextField(new Rect(x + 30, y, hw - 24, 22), _editImgRot, _sFieldSmall);
                GUI.Label(new Rect(x + hw + 6, y, 20, 20), "Op", _sLabel);
                _editImgOp = GUI.TextField(new Rect(x + hw + 26, y, hw - 20, 22), _editImgOp, _sFieldSmall); y += 26;
                GUI.Label(new Rect(x, y, bw, 14), "Tint (R G B):", _sTitle); y += 16;
                float tw = (bw - 8f) / 3f;
                _editImgR = GUI.TextField(new Rect(x,              y, tw, 22), _editImgR, _sFieldSmall);
                _editImgG = GUI.TextField(new Rect(x + tw + 4,     y, tw, 22), _editImgG, _sFieldSmall);
                _editImgB = GUI.TextField(new Rect(x + (tw + 4)*2, y, tw, 22), _editImgB, _sFieldSmall); y += 26;

                float.TryParse(_editImgW,  out float ew);  if (ew  <= 0) ew  = img.w;
                float.TryParse(_editImgH,  out float eh);  if (eh  <= 0) eh  = img.h;
                float.TryParse(_editImgRot, out float er);
                float.TryParse(_editImgOp, out float eo);  if (eo  <= 0) eo  = img.opacity;
                float.TryParse(_editImgR,  out float ir);  if (ir  <= 0 && img.r > 0) ir = img.r; if (ir <= 0) ir = 1;
                float.TryParse(_editImgG,  out float ig);  if (ig  <= 0 && img.g > 0) ig = img.g; if (ig <= 0) ig = 1;
                float.TryParse(_editImgB,  out float ib);  if (ib  <= 0 && img.b > 0) ib = img.b; if (ib <= 0) ib = 1;
                _images[_selIdx] = new ImagePlacement { x = img.x, y = img.y, w = ew, h = eh, rot = er, opacity = Mathf.Clamp01(eo), r = Mathf.Clamp01(ir), g = Mathf.Clamp01(ig), b = Mathf.Clamp01(ib), path = _editImgPath };

                Divider(x, ref y, bw);
                Color prev = GUI.backgroundColor;
                GUI.backgroundColor = new Color(0.65f, 0.18f, 0.18f);
                if (GUI.Button(new Rect(x, y, bw, 24), "Delete Image  [Del]", _sBtn))
                { PushUndo(); _images.RemoveAt(_selIdx); _selType = SelType.None; _selIdx = -1; }
                GUI.backgroundColor = prev;
                return;
            }

            if (_selType == SelType.Tile && _tiles.ContainsKey(_selTilePos))
            {
                SectionHeader(x, ref y, bw, "TILE");
                GUI.Label(new Rect(x, y, bw, 14), $"Pos: ({_selTilePos.x}, {_selTilePos.y})", _sTitle); y += 18;
                GUI.Label(new Rect(x, y, bw, 14), "Change Layer:", _sTitle); y += 16;
                float btnW = (bw - 4f) / 2f;
                for (int i = 0; i < LayerName.Length; i++)
                {
                    int col = i % 2, row = i / 2;
                    bool cur = _tiles.ContainsKey(_selTilePos) && _tiles[_selTilePos] == i;
                    if (GUI.Button(new Rect(x + col * (btnW + 4f), y + row * 26f, btnW, 24), LayerName[i], cur ? _sToolActive : _sToolBtn))
                    { PushUndo(); _tiles[_selTilePos] = i; }
                }
                y += ((LayerName.Length + 1) / 2) * 26f + 4;
                Divider(x, ref y, bw);
                Color prev = GUI.backgroundColor;
                GUI.backgroundColor = new Color(0.65f, 0.18f, 0.18f);
                if (GUI.Button(new Rect(x, y, bw, 24), "Delete Tile  [Del]", _sBtn))
                { PushUndo(); _tiles.Remove(_selTilePos); _selType = SelType.None; }
                GUI.backgroundColor = prev;
                return;
            }

            if (_selType != SelType.None)
            {
                SectionHeader(x, ref y, bw, "SELECTED");
                GUI.Label(new Rect(x, y, bw, 14), $"Type: {_selType}", _sTitle); y += 18;
                Vector2? pos = GetSelPos();
                if (pos.HasValue) GUI.Label(new Rect(x, y, bw, 14), $"Pos: ({pos.Value.x:F0}, {pos.Value.y:F0})", _sTitle);
                y += 18;
                GUI.Label(new Rect(x, y, bw, 14), "Drag to move", _sTitle); y += 16;
                GUI.Label(new Rect(x, y, bw, 14), "Del to delete", _sTitle); y += 16;
                return;
            }

            // Default: map info
            SectionHeader(x, ref y, bw, "MAP INFO");
            InfoLine(x, ref y, bw, $"Tiles        {_tiles.Count}");
            InfoLine(x, ref y, bw, $"Boxes        {_boxes.Count}");
            InfoLine(x, ref y, bw, $"Images       {_images.Count}");
            InfoLine(x, ref y, bw, $"Checkpoints  {_checkpoints.Count}");
            InfoLine(x, ref y, bw, $"SwapTriggers {_swapTriggers.Count}");
            InfoLine(x, ref y, bw, $"Hitboxes     {_hitboxes.Count}");
            InfoLine(x, ref y, bw, $"Spawn        {FmtV(_spawn)}");
            InfoLine(x, ref y, bw, $"Gate         {FmtV(_gate)}");
            InfoLine(x, ref y, bw, $"End          {FmtV(_end)}");
            InfoLine(x, ref y, bw, $"UI Marker    {FmtV(_uiMarker)}");

            float hintY = r.yMax - 120f;
            if (hintY > y + 4)
            {
                Divider(x, ref y, bw);
                y = hintY;
                SectionHeader(x, ref y, bw, "CONTROLS");
                InfoLine(x, ref y, bw, "Arrows: Pan");
                InfoLine(x, ref y, bw, "Mid/Alt+drag: Pan");
                InfoLine(x, ref y, bw, "Scroll: Zoom");
                InfoLine(x, ref y, bw, "Ctrl+Z / Ctrl+Y: Undo/Redo");
                InfoLine(x, ref y, bw, "Del: Delete selected");
                InfoLine(x, ref y, bw, "RMB: Erase/Remove");
                InfoLine(x, ref y, bw, "Esc: Close editor");
            }
        }

        private Vector2? GetSelPos()
        {
            switch (_selType)
            {
                case SelType.Tile:      return (Vector2)_selTilePos;
                case SelType.Spawn:     return _spawn;
                case SelType.Gate:      return _gate;
                case SelType.End:       return _end;
                case SelType.UIMarker:  return _uiMarker;
                case SelType.Checkpoint:  return (_selIdx >= 0 && _selIdx < _checkpoints.Count)  ? (Vector2?)_checkpoints[_selIdx]  : null;
                case SelType.SwapTrigger: return (_selIdx >= 0 && _selIdx < _swapTriggers.Count) ? (Vector2?)_swapTriggers[_selIdx] : null;
                case SelType.Box:  return (_selIdx >= 0 && _selIdx < _boxes.Count)  ? (Vector2?)new Vector2(_boxes[_selIdx].x, _boxes[_selIdx].y) : null;
                case SelType.Image: return (_selIdx >= 0 && _selIdx < _images.Count) ? (Vector2?)new Vector2(_images[_selIdx].x, _images[_selIdx].y) : null;
                default: return null;
            }
        }

        private void SyncEditState()
        {
            if (_selType == SelType.Box && _selIdx >= 0 && _selIdx < _boxes.Count)
            {
                var b = _boxes[_selIdx];
                _editBoxUpg   = b.upgrade;
                _editBoxCap   = b.cap.ToString();
                _editBoxPrice = b.price.ToString("F0");
            }
            if (_selType == SelType.Image && _selIdx >= 0 && _selIdx < _images.Count)
            {
                var img = _images[_selIdx];
                _editImgPath = img.path ?? "";
                _editImgW    = img.w.ToString("F1");
                _editImgH    = img.h.ToString("F1");
                _editImgRot  = img.rot.ToString("F1");
                _editImgOp   = img.opacity.ToString("F2");
                _editImgR    = img.r.ToString("F2");
                _editImgG    = img.g.ToString("F2");
                _editImgB    = img.b.ToString("F2");
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        // Canvas
        // ═════════════════════════════════════════════════════════════════════
        private void DrawCanvas(Rect r)
        {
            GUI.BeginGroup(r);
            Rect inner = new Rect(0, 0, r.width, r.height);
            GUI.DrawTexture(inner, _darkBg, ScaleMode.StretchToFill);
            float cell = CELL * _zoom;
            DrawGrid(inner, cell);
            DrawImages(cell, inner);
            DrawTiles(cell, inner);
            DrawHitboxes(cell, inner);
            DrawMarkers(cell, inner);
            DrawSelectionHighlight(cell);
            DrawCursor(inner, cell);
            HandleCanvasInput(inner, cell, r);
            GUI.EndGroup();
        }

        private void DrawGrid(Rect inner, float cell)
        {
            if (cell < 3f) return;
            int gxS = Mathf.FloorToInt(-_originX / cell) - 1;
            int gxE = Mathf.CeilToInt((inner.width - _originX) / cell) + 1;
            for (int gx = gxS; gx <= gxE; gx++)
            {
                float sx = _originX + gx * cell;
                Texture2D lineTex = gx == 0 ? _originLine : (gx % 8 == 0 ? _majorLine : _gridLine);
                GUI.DrawTexture(new Rect(sx, 0, gx == 0 ? 2f : 1f, inner.height), lineTex);
            }
            int gyS = Mathf.FloorToInt((_originY - inner.height) / cell) - 1;
            int gyE = Mathf.CeilToInt(_originY / cell) + 1;
            for (int gy = gyS; gy <= gyE; gy++)
            {
                float sy = _originY - gy * cell;
                Texture2D lineTex = gy == 0 ? _originLine : (gy % 8 == 0 ? _majorLine : _gridLine);
                GUI.DrawTexture(new Rect(0, sy, inner.width, gy == 0 ? 2f : 1f), lineTex);
            }
        }

        private void DrawImages(float cell, Rect inner)
        {
            for (int i = 0; i < _images.Count; i++)
            {
                var img = _images[i];
                float sx = _originX + img.x * cell;
                float sy = _originY - (img.y + img.h) * cell;
                float pw = img.w * cell, ph = img.h * cell;
                if (sx + pw < 0 || sx > inner.width || sy + ph < 0 || sy > inner.height) continue;
                var rect = new Rect(sx, sy, pw, ph);
                var tex = LoadImageTex(img.path);
                Color prev = GUI.color;
                GUI.color = new Color(img.r > 0 ? img.r : 1f, img.g > 0 ? img.g : 1f, img.b > 0 ? img.b : 1f, img.opacity > 0 ? img.opacity : 1f);
                if (tex != null)
                    GUI.DrawTexture(rect, tex, ScaleMode.StretchToFill);
                else
                {
                    GUI.DrawTexture(rect, _imgPlaceholderTex);
                    if (cell > 6 && !string.IsNullOrEmpty(img.path))
                    {
                        GUI.color = new Color(0.9f, 0.9f, 0.9f, 0.8f);
                        int fs = Mathf.Clamp((int)(ph * 0.13f), 7, 13);
                        GUI.Label(new Rect(sx + 2, sy + ph * 0.4f, pw - 4, fs * 1.5f), Path.GetFileName(img.path),
                            new GUIStyle(GUI.skin.label) { fontSize = fs, alignment = TextAnchor.MiddleCenter, normal = { textColor = Color.white } });
                    }
                }
                GUI.color = prev;
            }
        }

        private void DrawTiles(float cell, Rect inner)
        {
            foreach (var kvp in _tiles)
            {
                float sx = TSX(kvp.Key.x, cell), sy = TSY(kvp.Key.y, cell);
                if (sx + cell < 0 || sx > inner.width || sy + cell < 0 || sy > inner.height) continue;
                DrawLayerTile(new Rect(sx, sy, cell, cell), kvp.Value);
            }
        }

        private void DrawHitboxes(float cell, Rect inner)
        {
            for (int i = 0; i < _hitboxes.Count; i++) DrawHbRect(_hitboxes[i], cell, inner);
            if (_hbDrag) DrawHbRect(_hbPrev, cell, inner);
        }

        private void DrawHbRect(Rect hb, float cell, Rect inner)
        {
            float sx = TSX(hb.x, cell), sy = TSY(hb.y + hb.height, cell);
            float pw = hb.width * cell, ph = hb.height * cell;
            if (sx + pw < 0 || sx > inner.width || sy + ph < 0 || sy > inner.height) return;
            GUI.DrawTexture(new Rect(sx, sy, pw, ph), _hbFill);
            GUI.DrawTexture(new Rect(sx, sy, pw, 1f), _hbEdge);
            GUI.DrawTexture(new Rect(sx, sy + ph - 1, pw, 1f), _hbEdge);
            GUI.DrawTexture(new Rect(sx, sy, 1f, ph), _hbEdge);
            GUI.DrawTexture(new Rect(sx + pw - 1, sy, 1f, ph), _hbEdge);
        }

        private void DrawMarkers(float cell, Rect inner)
        {
            DrawMarker(_spawn,    _spawnTex, _sprSpawn, _uvSpawn, "SPAWN", cell, inner);
            DrawMarker(_gate,     _gateTex,  _sprGate,  _uvGate,  "GATE",  cell, inner);
            DrawMarker(_end,      _endTex,   _sprEnd,   _uvEnd,   "END",   cell, inner);
            DrawMarker(_uiMarker, _uiTex,    null,      default,  "UI",    cell, inner);
            foreach (var p in _checkpoints)  DrawAt(p.x, p.y, _cpTex, _sprCp, _uvCp, "CP", cell, inner);
            foreach (var p in _swapTriggers) DrawAt(p.x, p.y, _swTex, _sprSw, _uvSw, "SW", cell, inner);
            for (int i = 0; i < _boxes.Count; i++)
            {
                var b = _boxes[i];
                bool sel = _selType == SelType.Box && _selIdx == i;
                Texture2D fallback = sel ? _selBoxTex : _boxTex;
                string lbl = UpgradeLabel[Mathf.Clamp(b.upgrade, 0, UpgradeLabel.Length - 1)];
                DrawAt(b.x, b.y, fallback, _sprBox, _uvBox, lbl, cell, inner);
            }
        }

        private void DrawMarker(Vector2? pos, Texture2D tex, Sprite spr, Rect uv, string lbl, float cell, Rect inner)
        { if (pos.HasValue) DrawAt(pos.Value.x, pos.Value.y, tex, spr, uv, lbl, cell, inner); }

        private void DrawAt(float gx, float gy, Texture2D fallback, Sprite spr, Rect uv, string lbl, float cell, Rect inner)
        {
            float sx = TSX(gx, cell), sy = TSY(gy, cell);
            if (sx + cell < 0 || sx > inner.width || sy + cell < 0 || sy > inner.height) return;
            var rect = new Rect(sx, sy, cell, cell);
            if (spr != null)
                GUI.DrawTextureWithTexCoords(rect, spr.texture, uv);
            else
                GUI.DrawTexture(rect, fallback);
            if (cell > 14)
            {
                int fs = Mathf.Clamp((int)(cell * 0.22f), 7, 12);
                GUI.Label(rect, lbl,
                    new GUIStyle(GUI.skin.label) { fontSize = fs, fontStyle = FontStyle.Bold,
                        alignment = TextAnchor.MiddleCenter, normal = { textColor = spr != null ? Color.white : Color.black } });
            }
        }

        private void DrawSelectionHighlight(float cell)
        {
            if (_selType == SelType.None) return;
            Rect sr = GetSelScreenRect(cell);
            if (sr.width <= 0) return;
            Color prev = GUI.color;
            GUI.color = new Color(1f, 0.94f, 0.20f, 0.92f);
            float t = 2f;
            GUI.DrawTexture(new Rect(sr.x,          sr.y,          sr.width, t), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(sr.x,          sr.yMax - t,   sr.width, t), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(sr.x,          sr.y,          t, sr.height), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(sr.xMax - t,   sr.y,          t, sr.height), Texture2D.whiteTexture);
            GUI.color = prev;
        }

        private Rect GetSelScreenRect(float cell)
        {
            Vector2? p;
            switch (_selType)
            {
                case SelType.Tile:      return new Rect(TSX(_selTilePos.x, cell), TSY(_selTilePos.y, cell), cell, cell);
                case SelType.Spawn:     p = _spawn;    if (p.HasValue) return new Rect(TSX(p.Value.x, cell), TSY(p.Value.y, cell), cell, cell); break;
                case SelType.Gate:      p = _gate;     if (p.HasValue) return new Rect(TSX(p.Value.x, cell), TSY(p.Value.y, cell), cell, cell); break;
                case SelType.End:       p = _end;      if (p.HasValue) return new Rect(TSX(p.Value.x, cell), TSY(p.Value.y, cell), cell, cell); break;
                case SelType.UIMarker:  p = _uiMarker; if (p.HasValue) return new Rect(TSX(p.Value.x, cell), TSY(p.Value.y, cell), cell, cell); break;
                case SelType.Checkpoint:
                    if (_selIdx >= 0 && _selIdx < _checkpoints.Count)
                        return new Rect(TSX(_checkpoints[_selIdx].x, cell), TSY(_checkpoints[_selIdx].y, cell), cell, cell); break;
                case SelType.SwapTrigger:
                    if (_selIdx >= 0 && _selIdx < _swapTriggers.Count)
                        return new Rect(TSX(_swapTriggers[_selIdx].x, cell), TSY(_swapTriggers[_selIdx].y, cell), cell, cell); break;
                case SelType.Box:
                    if (_selIdx >= 0 && _selIdx < _boxes.Count)
                        return new Rect(TSX(_boxes[_selIdx].x, cell), TSY(_boxes[_selIdx].y, cell), cell, cell); break;
                case SelType.Hitbox:
                    if (_selIdx >= 0 && _selIdx < _hitboxes.Count)
                    { var h = _hitboxes[_selIdx]; return new Rect(TSX(h.x, cell), TSY(h.y + h.height, cell), h.width * cell, h.height * cell); }
                    break;
                case SelType.Image:
                    if (_selIdx >= 0 && _selIdx < _images.Count)
                    { var img = _images[_selIdx]; return new Rect(_originX + img.x * cell, _originY - (img.y + img.h) * cell, img.w * cell, img.h * cell); }
                    break;
            }
            return Rect.zero;
        }

        private void DrawCursor(Rect inner, float cell)
        {
            if (!inner.Contains(Event.current.mousePosition)) return;
            var gc = S2G(Event.current.mousePosition, cell);
            var tileR = new Rect(TSX(gc.x, cell), TSY(gc.y, cell), cell, cell);

            if (_tool == EditorTool.Tile)
            {
                Color prev = GUI.color;
                if (_layerSprite != null && _layerSprite[_layer] != null)
                {
                    GUI.color = new Color(_layerTint[_layer].r, _layerTint[_layer].g, _layerTint[_layer].b, 0.55f);
                    GUI.DrawTextureWithTexCoords(tileR, _layerSprite[_layer].texture, _layerUV[_layer]);
                }
                else
                {
                    GUI.color = new Color(LayerCol[_layer].r, LayerCol[_layer].g, LayerCol[_layer].b, 0.50f);
                    GUI.DrawTexture(tileR, _layerTex[_layer]);
                }
                GUI.color = prev;
            }
            else if (_tool == EditorTool.Erase)
            {
                GUI.DrawTexture(tileR, _eraseTex);
            }
            else if (_tool != EditorTool.Select)
            {
                GUI.DrawTexture(tileR, _cursorTex);
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        // Input
        // ═════════════════════════════════════════════════════════════════════
        private void HandleCanvasInput(Rect inner, float cell, Rect screenRect)
        {
            var e = Event.current;
            bool inC = inner.Contains(e.mousePosition);

            // Scroll zoom
            if (e.type == EventType.ScrollWheel && inC)
            {
                float factor = e.delta.y > 0 ? 0.83f : 1.20f, oldZ = _zoom;
                _zoom    = Mathf.Clamp(_zoom * factor, 0.12f, 30f);
                float r  = _zoom / oldZ;
                _originX = e.mousePosition.x - (e.mousePosition.x - _originX) * r;
                _originY = e.mousePosition.y - (e.mousePosition.y - _originY) * r;
                e.Use(); return;
            }

            // Key handling
            if (e.type == EventType.KeyDown)
            {
                // Ctrl combos
                if (e.control)
                {
                    if (e.keyCode == KeyCode.Z) { if (e.shift) Redo(); else Undo(); e.Use(); return; }
                    if (e.keyCode == KeyCode.Y) { Redo(); e.Use(); return; }
                    if (e.keyCode == KeyCode.S) { SaveMap(); e.Use(); return; }
                }
                // Delete selected
                if (e.keyCode == KeyCode.Delete && _selType != SelType.None)
                { PushUndo(); DeleteSelected(); e.Use(); return; }
                // Tool hotkeys (only when no text field has focus)
                if (GUIUtility.keyboardControl == 0)
                {
                    switch (e.keyCode)
                    {
                        case KeyCode.V:      _tool = EditorTool.Select;       _selType = SelType.None; e.Use(); break;
                        case KeyCode.T: case KeyCode.Alpha1: _tool = EditorTool.Tile;   e.Use(); break;
                        case KeyCode.E: case KeyCode.Alpha2: _tool = EditorTool.Erase;  e.Use(); break;
                        case KeyCode.S:      _tool = EditorTool.Spawn;        e.Use(); break;
                        case KeyCode.G:      _tool = EditorTool.Gate;         e.Use(); break;
                        case KeyCode.N:      _tool = EditorTool.End;          e.Use(); break;
                        case KeyCode.U:      _tool = EditorTool.UIMarker;     e.Use(); break;
                        case KeyCode.C:      _tool = EditorTool.Checkpoint;   e.Use(); break;
                        case KeyCode.W:      _tool = EditorTool.SwapTrigger;  e.Use(); break;
                        case KeyCode.H:      _tool = EditorTool.Hitbox;       e.Use(); break;
                        case KeyCode.B:      _tool = EditorTool.Box;          e.Use(); break;
                        case KeyCode.I:      _tool = EditorTool.Image;        e.Use(); break;
                        case KeyCode.LeftBracket:  if (_layer > 0) _layer--;              e.Use(); break;
                        case KeyCode.RightBracket: if (_layer < LayerName.Length-1) _layer++; e.Use(); break;
                        case KeyCode.Escape: if (_selType != SelType.None) { _selType = SelType.None; _selIdx = -1; } else Toggle(); e.Use(); break;
                    }
                }
                return;
            }

            // Mouse down
            if (e.type == EventType.MouseDown)
            {
                bool isPan = e.button == 2 || (e.button == 0 && e.alt);
                if (isPan && inC) { _panning = true; _panMouse0 = e.mousePosition; _panOX0 = _originX; _panOY0 = _originY; e.Use(); return; }

                if (e.button == 0 && inC)
                {
                    if (_tool == EditorTool.Select)
                    {
                        var gc = S2G(e.mousePosition, cell);
                        if (TrySelectAt(gc)) { _selDragging = true; _selDragLastGrid = gc; }
                        else { _selType = SelType.None; _selIdx = -1; _panning = true; _panMouse0 = e.mousePosition; _panOX0 = _originX; _panOY0 = _originY; }
                    }
                    else { PushUndo(); OnLMB(e.mousePosition, cell); }
                    e.Use();
                }
                if (e.button == 1 && inC) { PushUndo(); OnRMB(e.mousePosition, cell); e.Use(); }
                return;
            }

            // Mouse drag
            if (e.type == EventType.MouseDrag)
            {
                if (_panning) { var d = e.mousePosition - _panMouse0; _originX = _panOX0 + d.x; _originY = _panOY0 + d.y; e.Use(); return; }
                if (_selDragging && e.button == 0)
                {
                    var gc = S2G(e.mousePosition, cell);
                    var delta = gc - _selDragLastGrid;
                    if (delta != Vector2Int.zero) { MoveSelectedBy(delta); _selDragLastGrid = gc; }
                    e.Use(); return;
                }
                if (_hbDrag && e.button == 0) { UpdateHbDrag(e.mousePosition, cell); e.Use(); return; }
                if (e.button == 0 && (_tool == EditorTool.Tile || _tool == EditorTool.Erase)) { OnLMB(e.mousePosition, cell); e.Use(); }
                return;
            }

            // Mouse up
            if (e.type == EventType.MouseUp)
            {
                if (_panning)               { _panning      = false; e.Use(); return; }
                if (_selDragging)           { _selDragging  = false; e.Use(); return; }
                if (_hbDrag && e.button==0) { FinishHbDrag();        e.Use(); return; }
            }
        }

        private void OnLMB(Vector2 mp, float cell)
        {
            var gc = S2G(mp, cell);
            switch (_tool)
            {
                case EditorTool.Tile:        _tiles[gc] = _layer;                          break;
                case EditorTool.Erase:       EraseAt(gc);                                  break;
                case EditorTool.Spawn:       _spawn    = new Vector2(gc.x, gc.y);          break;
                case EditorTool.Gate:        _gate     = new Vector2(gc.x, gc.y);          break;
                case EditorTool.End:         _end      = new Vector2(gc.x, gc.y);          break;
                case EditorTool.UIMarker:    _uiMarker = new Vector2(gc.x, gc.y);          break;
                case EditorTool.Checkpoint:  _checkpoints.Add(new Vector2(gc.x, gc.y));   break;
                case EditorTool.SwapTrigger: _swapTriggers.Add(new Vector2(gc.x, gc.y));  break;
                case EditorTool.Hitbox:
                    _hbDrag = true; _hbStart = gc; _hbPrev = new Rect(gc.x, gc.y, 1, 1);
                    break;
                case EditorTool.Box:
                {
                    int bi = BoxAt(gc.x, gc.y);
                    if (bi >= 0) { _selType = SelType.Box; _selIdx = bi; }
                    else
                    {
                        int.TryParse(_newBoxCap, out int cap); if (cap < 0) cap = 0;
                        double.TryParse(_newBoxPrice, out double price); if (price < 0) price = 0;
                        _boxes.Add(new BoxPlacement { x = gc.x, y = gc.y, upgrade = _newBoxUpg, cap = cap, price = price });
                        _selType = SelType.Box; _selIdx = _boxes.Count - 1;
                    }
                    break;
                }
                case EditorTool.Image:
                {
                    float.TryParse(_newImgW, out float iw); if (iw <= 0) iw = 3;
                    float.TryParse(_newImgH, out float ih); if (ih <= 0) ih = 3;
                    _images.Add(new ImagePlacement { x = gc.x, y = gc.y, w = iw, h = ih, opacity = 1, r = 1, g = 1, b = 1, path = _newImgPath });
                    _selType = SelType.Image; _selIdx = _images.Count - 1;
                    break;
                }
            }
        }

        private void OnRMB(Vector2 mp, float cell)
        {
            var gc = S2G(mp, cell);
            switch (_tool)
            {
                case EditorTool.Tile:  _tiles.Remove(gc); break;
                case EditorTool.Erase: EraseAt(gc);       break;
                case EditorTool.Spawn:       _spawn    = null; break;
                case EditorTool.Gate:        _gate     = null; break;
                case EditorTool.End:         _end      = null; break;
                case EditorTool.UIMarker:    _uiMarker = null; break;
                case EditorTool.Checkpoint:
                    _checkpoints.RemoveAll(p => Mathf.RoundToInt(p.x)==gc.x && Mathf.RoundToInt(p.y)==gc.y); break;
                case EditorTool.SwapTrigger:
                    _swapTriggers.RemoveAll(p => Mathf.RoundToInt(p.x)==gc.x && Mathf.RoundToInt(p.y)==gc.y); break;
                case EditorTool.Hitbox:
                { int hi = HbAt(gc.x, gc.y); if (hi >= 0) { _hitboxes.RemoveAt(hi); if (_selType==SelType.Hitbox && _selIdx==hi) { _selType=SelType.None; _selIdx=-1; } } break; }
                case EditorTool.Box:
                { int bi = BoxAt(gc.x, gc.y); if (bi >= 0) { _boxes.RemoveAt(bi); if (_selType==SelType.Box && _selIdx==bi) { _selType=SelType.None; _selIdx=-1; } } break; }
                case EditorTool.Image:
                { int ii = ImageAt(gc.x, gc.y); if (ii >= 0) { _images.RemoveAt(ii); if (_selType==SelType.Image && _selIdx==ii) { _selType=SelType.None; _selIdx=-1; } } break; }
                case EditorTool.Select:
                { EraseAt(gc); if (_selType != SelType.None) { _selType = SelType.None; _selIdx = -1; } break; }
            }
        }

        // ── Select tool helpers ───────────────────────────────────────────────
        private bool TrySelectAt(Vector2Int gc)
        {
            if (MatchV2(_spawn,    gc)) { _selType = SelType.Spawn;    _selIdx = -1; return true; }
            if (MatchV2(_gate,     gc)) { _selType = SelType.Gate;     _selIdx = -1; return true; }
            if (MatchV2(_end,      gc)) { _selType = SelType.End;      _selIdx = -1; return true; }
            if (MatchV2(_uiMarker, gc)) { _selType = SelType.UIMarker; _selIdx = -1; return true; }
            int bi = BoxAt(gc.x, gc.y);          if (bi >= 0) { _selType = SelType.Box;          _selIdx = bi; return true; }
            int ci = CheckpointAt(gc.x, gc.y);   if (ci >= 0) { _selType = SelType.Checkpoint;   _selIdx = ci; return true; }
            int si = SwapTrigAt(gc.x, gc.y);     if (si >= 0) { _selType = SelType.SwapTrigger;  _selIdx = si; return true; }
            int hi = HbAt(gc.x, gc.y);           if (hi >= 0) { _selType = SelType.Hitbox;        _selIdx = hi; return true; }
            int ii = ImageAt(gc.x, gc.y);        if (ii >= 0) { _selType = SelType.Image;         _selIdx = ii; return true; }
            if (_tiles.ContainsKey(gc))          { _selType = SelType.Tile; _selTilePos = gc;      return true; }
            return false;
        }

        private static bool MatchV2(Vector2? v, Vector2Int gc) =>
            v.HasValue && Mathf.RoundToInt(v.Value.x) == gc.x && Mathf.RoundToInt(v.Value.y) == gc.y;

        private void MoveSelectedBy(Vector2Int d)
        {
            switch (_selType)
            {
                case SelType.Tile:
                    if (_tiles.TryGetValue(_selTilePos, out int layer))
                    { _tiles.Remove(_selTilePos); _selTilePos += d; _tiles[_selTilePos] = layer; }
                    break;
                case SelType.Spawn:    if (_spawn.HasValue)    _spawn    = _spawn.Value    + (Vector2)d; break;
                case SelType.Gate:     if (_gate.HasValue)     _gate     = _gate.Value     + (Vector2)d; break;
                case SelType.End:      if (_end.HasValue)      _end      = _end.Value      + (Vector2)d; break;
                case SelType.UIMarker: if (_uiMarker.HasValue) _uiMarker = _uiMarker.Value + (Vector2)d; break;
                case SelType.Box:
                    if (_selIdx >= 0 && _selIdx < _boxes.Count)
                    { var b = _boxes[_selIdx]; _boxes[_selIdx] = new BoxPlacement { x = b.x + d.x, y = b.y + d.y, upgrade = b.upgrade, cap = b.cap, price = b.price }; }
                    break;
                case SelType.Checkpoint:
                    if (_selIdx >= 0 && _selIdx < _checkpoints.Count)   _checkpoints[_selIdx]  += (Vector2)d; break;
                case SelType.SwapTrigger:
                    if (_selIdx >= 0 && _selIdx < _swapTriggers.Count)  _swapTriggers[_selIdx] += (Vector2)d; break;
                case SelType.Hitbox:
                    if (_selIdx >= 0 && _selIdx < _hitboxes.Count)
                    { var h = _hitboxes[_selIdx]; _hitboxes[_selIdx] = new Rect(h.x + d.x, h.y + d.y, h.width, h.height); }
                    break;
                case SelType.Image:
                    if (_selIdx >= 0 && _selIdx < _images.Count)
                    { var img = _images[_selIdx]; img.x += d.x; img.y += d.y; _images[_selIdx] = img; }
                    break;
            }
        }

        private void DeleteSelected()
        {
            switch (_selType)
            {
                case SelType.Tile:        _tiles.Remove(_selTilePos);                             break;
                case SelType.Spawn:       _spawn    = null;                                       break;
                case SelType.Gate:        _gate     = null;                                       break;
                case SelType.End:         _end      = null;                                       break;
                case SelType.UIMarker:    _uiMarker = null;                                       break;
                case SelType.Checkpoint:  if (_selIdx >= 0) _checkpoints.RemoveAt(_selIdx);       break;
                case SelType.SwapTrigger: if (_selIdx >= 0) _swapTriggers.RemoveAt(_selIdx);      break;
                case SelType.Hitbox:      if (_selIdx >= 0) _hitboxes.RemoveAt(_selIdx);          break;
                case SelType.Box:         if (_selIdx >= 0) _boxes.RemoveAt(_selIdx);             break;
                case SelType.Image:       if (_selIdx >= 0) _images.RemoveAt(_selIdx);            break;
            }
            _selType = SelType.None; _selIdx = -1;
        }

        // ── hitbox drag ───────────────────────────────────────────────────────
        private void UpdateHbDrag(Vector2 mp, float cell)
        {
            var gc = S2G(mp, cell);
            _hbPrev = new Rect(Mathf.Min(_hbStart.x, gc.x), Mathf.Min(_hbStart.y, gc.y),
                               Mathf.Abs(gc.x - _hbStart.x) + 1, Mathf.Abs(gc.y - _hbStart.y) + 1);
        }

        private void FinishHbDrag()
        {
            _hbDrag = false;
            if (_hbPrev.width > 0 && _hbPrev.height > 0)
            { _hitboxes.Add(_hbPrev); _selType = SelType.Hitbox; _selIdx = _hitboxes.Count - 1; }
        }

        // ── pick helpers ──────────────────────────────────────────────────────
        private int HbAt(int gx, int gy)
        {
            for (int i = 0; i < _hitboxes.Count; i++)
            { var h = _hitboxes[i]; if (gx >= h.x && gx < h.x+h.width && gy >= h.y && gy < h.y+h.height) return i; }
            return -1;
        }
        private int BoxAt(int gx, int gy)
        {
            for (int i = 0; i < _boxes.Count; i++)
            { var b = _boxes[i]; if (Mathf.RoundToInt(b.x)==gx && Mathf.RoundToInt(b.y)==gy) return i; }
            return -1;
        }
        private int CheckpointAt(int gx, int gy)
        {
            for (int i = 0; i < _checkpoints.Count; i++)
            { var p = _checkpoints[i]; if (Mathf.RoundToInt(p.x)==gx && Mathf.RoundToInt(p.y)==gy) return i; }
            return -1;
        }
        private int SwapTrigAt(int gx, int gy)
        {
            for (int i = 0; i < _swapTriggers.Count; i++)
            { var p = _swapTriggers[i]; if (Mathf.RoundToInt(p.x)==gx && Mathf.RoundToInt(p.y)==gy) return i; }
            return -1;
        }
        private int ImageAt(float gx, float gy)
        {
            for (int i = 0; i < _images.Count; i++)
            { var img = _images[i]; if (gx >= img.x && gx < img.x+img.w && gy >= img.y && gy < img.y+img.h) return i; }
            return -1;
        }

        // ── status bar ────────────────────────────────────────────────────────
        private void DrawStatusBar(Rect r)
        {
            GUI.DrawTexture(r, _statusBg, ScaleMode.StretchToFill);
            float cell = CELL * _zoom;
            Vector2 lm = Event.current.mousePosition - new Vector2(_canvasRect.x, _canvasRect.y);
            var gc = S2G(lm, cell);
            float x = r.x + 8f, y = r.y + 4f;
            GUI.Label(new Rect(x, y, 250, 18), $"Grid: {gc.x}, {gc.y}  |  Zoom: {_zoom:F1}×  |  {_tool}", _sLabel); x += 270;
            if (GUI.Button(new Rect(x, y, 26, 18), "−", _sBtn)) _zoom = Mathf.Max(0.12f, _zoom * 0.77f); x += 28;
            if (GUI.Button(new Rect(x, y, 26, 18), "+", _sBtn)) _zoom = Mathf.Min(30f,   _zoom * 1.30f); x += 30;
            if (GUI.Button(new Rect(x, y, 38, 18), "Fit", _sBtn)) FitView();
            x += 44;
            if (_undoList.Count > 0) GUI.Label(new Rect(x, y, 120, 18), $"Undo: {_undoList.Count}", _sTitle);
        }

        // ═════════════════════════════════════════════════════════════════════
        // Coordinate helpers
        // ═════════════════════════════════════════════════════════════════════
        private float TSX(float gx, float cell) => _originX + gx * cell;
        private float TSY(float gy, float cell) => _originY - (gy + 1) * cell;

        private Vector2Int S2G(Vector2 sp, float cell) =>
            new Vector2Int(Mathf.FloorToInt((sp.x - _originX) / cell),
                           Mathf.FloorToInt((_originY - sp.y) / cell));

        private void CenterView() { _originX = Screen.width / 2f; _originY = Screen.height / 2f; }

        private void FitView()
        {
            void Ex(ref int minX, ref int minY, ref int maxX, ref int maxY, int x, int y)
            { if (x < minX) minX = x; if (y < minY) minY = y; if (x > maxX) maxX = x; if (y > maxY) maxY = y; }

            int mnX = int.MaxValue, mnY = int.MaxValue, mxX = int.MinValue, mxY = int.MinValue;
            foreach (var k in _tiles.Keys)                             Ex(ref mnX, ref mnY, ref mxX, ref mxY, k.x, k.y);
            foreach (var b in _boxes)                                  Ex(ref mnX, ref mnY, ref mxX, ref mxY, Mathf.RoundToInt(b.x), Mathf.RoundToInt(b.y));
            if (_spawn.HasValue)    Ex(ref mnX, ref mnY, ref mxX, ref mxY, Mathf.RoundToInt(_spawn.Value.x),    Mathf.RoundToInt(_spawn.Value.y));
            if (_gate.HasValue)     Ex(ref mnX, ref mnY, ref mxX, ref mxY, Mathf.RoundToInt(_gate.Value.x),     Mathf.RoundToInt(_gate.Value.y));
            if (_end.HasValue)      Ex(ref mnX, ref mnY, ref mxX, ref mxY, Mathf.RoundToInt(_end.Value.x),      Mathf.RoundToInt(_end.Value.y));
            if (_uiMarker.HasValue) Ex(ref mnX, ref mnY, ref mxX, ref mxY, Mathf.RoundToInt(_uiMarker.Value.x), Mathf.RoundToInt(_uiMarker.Value.y));

            if (mnX == int.MaxValue) { CenterView(); return; }

            float mapW = mxX - mnX + 1, mapH = mxY - mnY + 1;
            float cw = _canvasRect.width  > 10 ? _canvasRect.width  : Screen.width  - LP_W - RP_W;
            float ch = _canvasRect.height > 10 ? _canvasRect.height : Screen.height - TB_H - SB_H;
            _zoom = Mathf.Clamp(Mathf.Min(cw / (mapW * CELL), ch / (mapH * CELL)) * 0.8f, 0.12f, 30f);
            float cell = CELL * _zoom;
            _originX = cw / 2f - (mnX + mapW / 2f) * cell;
            _originY = ch / 2f + (mnY + mapH / 2f) * cell;
        }

        // ═════════════════════════════════════════════════════════════════════
        // Undo / Redo
        // ═════════════════════════════════════════════════════════════════════
        private void PushUndo()
        {
            _undoList.Add(SerializeMap(BuildMapFile()));
            if (_undoList.Count > MAX_UNDO) _undoList.RemoveAt(0);
            _redoList.Clear();
        }

        private void Undo()
        {
            if (_undoList.Count == 0) { Status("Nothing to undo."); return; }
            _redoList.Add(SerializeMap(BuildMapFile()));
            string s = _undoList[_undoList.Count - 1]; _undoList.RemoveAt(_undoList.Count - 1);
            ApplyMapFile(JsonUtility.FromJson<MapFile>(s));
            Status("Undone.");
        }

        private void Redo()
        {
            if (_redoList.Count == 0) { Status("Nothing to redo."); return; }
            _undoList.Add(SerializeMap(BuildMapFile()));
            string s = _redoList[_redoList.Count - 1]; _redoList.RemoveAt(_redoList.Count - 1);
            ApplyMapFile(JsonUtility.FromJson<MapFile>(s));
            Status("Redone.");
        }

        // ═════════════════════════════════════════════════════════════════════
        // File operations
        // ═════════════════════════════════════════════════════════════════════
        private string SaveDir  => Path.Combine(Application.persistentDataPath, "Savedata", "customcourses");
        private string StashDir => Path.Combine(Application.persistentDataPath, "Savedata", "customcourses", ".stash");

        private bool ConfirmIfDirty() => true; // future: could prompt if tiles > 0

        private void NewMap()
        {
            _tiles.Clear(); _spawn = _gate = _end = _uiMarker = null;
            _checkpoints.Clear(); _swapTriggers.Clear(); _hitboxes.Clear();
            _boxes.Clear(); _images.Clear();
            _selType = SelType.None; _selIdx = -1;
            _undoList.Clear(); _redoList.Clear();
            Status("New map — ready.");
        }

        private void SaveMap()
        {
            if (string.IsNullOrWhiteSpace(_mapName)) { Status("Set a map name first!"); return; }
            try
            {
                if (!Directory.Exists(SaveDir)) Directory.CreateDirectory(SaveDir);
                File.WriteAllText(Path.Combine(SaveDir, _mapName.Trim() + ".json"), SerializeMap(BuildMapFile()), Encoding.UTF8);
                Status($"Saved → {_mapName}.json  ({_tiles.Count} tiles)");
            }
            catch (Exception ex) { Status($"Save error: {ex.Message}"); Debug.LogError("[MapEditor] Save: " + ex); }
        }

        private MapFile BuildMapFile() => new MapFile
        {
            spawnX = _spawn?.x ?? 0,    spawnY = _spawn?.y ?? 0,
            gateX  = _gate?.x  ?? 0,    gateY  = _gate?.y  ?? 0,
            endX   = _end?.x   ?? 0,    endY   = _end?.y   ?? 0,
            uiX    = _uiMarker?.x ?? 0, uiY    = _uiMarker?.y ?? 0,
            tiles        = _tiles.Select(kv => new TileEntry { x = kv.Key.x, y = kv.Key.y, layer = kv.Value }).ToArray(),
            checkpoints  = _checkpoints.Select(c => new CheckpointEntry  { x = c.x, y = c.y }).ToArray(),
            swapTriggers = _swapTriggers.Select(s => new SwapTriggerEntry { x = s.x, y = s.y }).ToArray(),
            hitboxes     = _hitboxes.Select(h => new HitboxEntry { x = h.x, y = h.y, w = h.width, h = h.height }).ToArray(),
            boxes        = _boxes.Select(b => new BoxEntry { x = b.x, y = b.y, upgrade = b.upgrade, cap = b.cap, price = b.price }).ToArray(),
            images       = _images.Select(img => new ImageEntry { x = img.x, y = img.y, w = img.w, h = img.h, rot = img.rot, opacity = img.opacity, cr = img.r, cg = img.g, cb = img.b, path = img.path ?? "" }).ToArray(),
        };

        private void ApplyMapFile(MapFile map)
        {
            if (map == null) return;
            _tiles.Clear();
            foreach (var t in map.tiles ?? new TileEntry[0])
                _tiles[new Vector2Int(t.x, t.y)] = Mathf.Clamp(t.layer, 0, 6);
            _spawn    = (map.spawnX != 0 || map.spawnY != 0) ? (Vector2?)new Vector2(map.spawnX, map.spawnY) : null;
            _gate     = (map.gateX  != 0 || map.gateY  != 0) ? (Vector2?)new Vector2(map.gateX,  map.gateY)  : null;
            _end      = (map.endX   != 0 || map.endY   != 0)  ? (Vector2?)new Vector2(map.endX,   map.endY)   : null;
            _uiMarker = (map.uiX    != 0 || map.uiY    != 0)  ? (Vector2?)new Vector2(map.uiX,    map.uiY)    : null;
            _checkpoints  = map.checkpoints?.Select(c => new Vector2(c.x, c.y)).ToList()        ?? new List<Vector2>();
            _swapTriggers = map.swapTriggers?.Select(s => new Vector2(s.x, s.y)).ToList()       ?? new List<Vector2>();
            _hitboxes     = map.hitboxes?.Select(h => new Rect(h.x, h.y, h.w, h.h)).ToList()    ?? new List<Rect>();
            _boxes        = map.boxes?.Select(b => new BoxPlacement { x=b.x, y=b.y, upgrade=b.upgrade, cap=b.cap, price=b.price }).ToList() ?? new List<BoxPlacement>();
            _images       = map.images?.Select(img => new ImagePlacement { x=img.x, y=img.y, w=img.w>0?img.w:1, h=img.h>0?img.h:1, rot=img.rot, opacity=img.opacity>0?img.opacity:1, r=img.cr>0?img.cr:1, g=img.cg>0?img.cg:1, b=img.cb>0?img.cb:1, path=img.path??"" }).ToList() ?? new List<ImagePlacement>();
            _selType = SelType.None; _selIdx = -1;
        }

        private void LoadFile(string path)
        {
            try
            {
                var map = JsonUtility.FromJson<MapFile>(File.ReadAllText(path, Encoding.UTF8));
                if (map == null) { Status("Invalid map file."); return; }
                ApplyMapFile(map);
                _mapName = Path.GetFileNameWithoutExtension(path);
                _undoList.Clear(); _redoList.Clear();
                FitView();
                Status($"Loaded: {_mapName}  ({_tiles.Count} tiles, {_boxes.Count} boxes)");
            }
            catch (Exception ex) { Status($"Load error: {ex.Message}"); Debug.LogError("[MapEditor] Load: " + ex); }
        }

        private void RefreshList()
        {
            var active = Directory.Exists(SaveDir)  ? Directory.GetFiles(SaveDir,  "*.json") : new string[0];
            var stash  = Directory.Exists(StashDir) ? Directory.GetFiles(StashDir, "*.json") : new string[0];
            // dedupe by filename — an active map shadows any stale stash copy
            var activeNames = new HashSet<string>(active.Select(Path.GetFileName), StringComparer.OrdinalIgnoreCase);
            _listFiles = active.Concat(stash.Where(f => !activeNames.Contains(Path.GetFileName(f))))
                               .OrderBy(Path.GetFileName).ToArray();
        }

        // ═════════════════════════════════════════════════════════════════════
        // Main-menu integration: "Play <map>" label, map-scoped save delete,
        // native menu buttons for the map list and editor.
        // ═════════════════════════════════════════════════════════════════════
        private int _delClicks; private float _delClickTime;

        private string StagedMapDisplayName()
        {
            try
            {
                if (Directory.Exists(SaveDir))
                {
                    var files = Directory.GetFiles(SaveDir, "*.json");
                    if (files.Length > 0) return Path.GetFileNameWithoutExtension(files[0]);
                }
            }
            catch { }
            return "Base Game";
        }

        private static void SetLabel(object tmp, string text)
        {
            if (tmp == null) return;
            try { tmp.GetType().GetProperty("text")?.SetValue(tmp, text, null); } catch { }
        }
        private static void SetLabelColor(object tmp, Color c)
        {
            if (tmp == null) return;
            try { tmp.GetType().GetProperty("color")?.SetValue(tmp, c, null); } catch { }
        }
        private static string GetLabel(object tmp)
        {
            try { return tmp?.GetType().GetProperty("text")?.GetValue(tmp, null) as string; } catch { return null; }
        }

        private static Component FindButtonOn(Component label)
        {
            for (var t = label.transform; t != null; t = t.parent)
            {
                var b = t.GetComponent("Button");
                if (b != null) return b;
            }
            return null;
        }

        // Disable the button's serialized listeners and install our own handler
        private static void HijackButton(Component button, UnityEngine.Events.UnityAction action)
        {
            var onClick = button.GetType().GetProperty("onClick")?.GetValue(button, null);
            if (onClick is UnityEngine.Events.UnityEventBase ueb)
                for (int i = 0; i < ueb.GetPersistentEventCount(); i++)
                    ueb.SetPersistentListenerState(i, UnityEngine.Events.UnityEventCallState.Off);
            if (onClick is UnityEngine.Events.UnityEvent ue)
            { ue.RemoveAllListeners(); ue.AddListener(action); }
        }

        private IEnumerator MainMenuIntegration()
        {
            yield return null; yield return null;   // let the menu finish building
            _mainMenuPanel = _loadMapPanel = _miniPanel = null;   // scene reload invalidates old refs
            _lmBtns.Clear(); _lmLabels.Clear();

            object startLabel = null, deleteLabel = null;
            Component deleteButton = null, settingsButton = null, quitButton = null;

            // The menu's pauseMenuScript references the delete label directly
            foreach (var mb in UnityEngine.Object.FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (mb == null || mb.GetType().Name != "pauseMenuScript") continue;
                deleteLabel = mb.GetType().GetField("deleteSaveButton", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(mb);
                break;
            }
            if (deleteLabel is Component dl) deleteButton = FindButtonOn(dl);

            // Locate the other menu rows by their labels
            object titleLabel = null, subtitleLabel = null;
            foreach (var mb in UnityEngine.Object.FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None))
            {
                if (mb == null || !mb.GetType().Name.Contains("TextMeshPro")) continue;
                string txt = GetLabel(mb) ?? "";
                if (txt.IndexOf("Start Demo", StringComparison.OrdinalIgnoreCase) >= 0) startLabel = mb;
                else if (txt.Trim() == "IGTAP") titleLabel = mb;
                else if (txt.IndexOf("Incremental", StringComparison.OrdinalIgnoreCase) >= 0) subtitleLabel = mb;
                else if (txt.IndexOf("Settings", StringComparison.OrdinalIgnoreCase) >= 0 && settingsButton == null) settingsButton = FindButtonOn(mb);
                else if (txt.IndexOf("Quit", StringComparison.OrdinalIgnoreCase) >= 0 && quitButton == null) quitButton = FindButtonOn(mb);
                else if (deleteLabel == null && txt.IndexOf("Delete", StringComparison.OrdinalIgnoreCase) >= 0)
                { deleteLabel = mb; deleteButton = FindButtonOn(mb); }
            }

            Debug.Log($"[MapEditor] MainMenu integration: start={startLabel != null} delete={deleteLabel != null} " +
                      $"deleteBtn={deleteButton != null} settings={settingsButton != null} quit={quitButton != null}");

            // Stop localization from overwriting our relabels (both the string
            // event AND the per-locale property variants driver)
            foreach (var lbl in new[] { startLabel, deleteLabel })
                if (lbl is Component c) KillLocalizers(c.gameObject);

            // Map-scoped delete instead of nuking the whole Savedata directory
            if (deleteButton != null && deleteLabel != null)
            {
                var lblRef = deleteLabel;
                HijackButton(deleteButton, () => ScopedDeleteClicked(lblRef));
            }

            // Build the Load Map submenu from the pristine menu panel
            if (quitButton != null)
            {
                var panel = FindAncestorWithImage(quitButton.transform.parent);
                _mainMenuPanel = panel != null ? panel.gameObject : null;
                if (panel != null) BuildLoadMapPanel(panel);
            }

            // Repurpose the Discord / Steam-wishlist corner buttons into
            // Load Map and Map Editor; the original links move into Settings.
            Component discordLink = null, steamLink = null;
            foreach (var mb in UnityEngine.Object.FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None))
            {
                if (mb == null || mb.GetType().Name != "LinkOpener") continue;
                string url = mb.GetType().GetField("link", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(mb) as string ?? "";
                if (url.IndexOf("discord", StringComparison.OrdinalIgnoreCase) >= 0) discordLink = mb;
                else if (steamLink == null) steamLink = mb;
            }
            Debug.Log($"[MapEditor] Link buttons: discord={discordLink != null} steam={steamLink != null}");

            MoveLinksToSettings(discordLink, steamLink);   // clone with link intact, BEFORE repurposing
            if (discordLink != null) RepurposeLinkButton(discordLink, "Discord",  steamLink,   "Load Map",   "player", ToggleLoadMapPanel);
            if (steamLink   != null) RepurposeLinkButton(steamLink,   "Wishlist", discordLink, "Map Editor", "pencil", ToggleEditorHotkey);

            // Retitle to IGMAP and drop the subtitle (AFTER the MAPS panel clone,
            // which keys off the original "IGTAP"/"Incremental..." texts)
            if (titleLabel is Component tc)
            {
                KillLocalizers(tc.gameObject);
                SetLabel(titleLabel, "IGMAP");
            }
            if (subtitleLabel is Component sc) sc.gameObject.SetActive(false);

            // Keep labels current while the menu is open
            while (SceneManager.GetActiveScene().name == "MainMenu")
            {
                string disp = StagedMapDisplayName();
                SetLabel(startLabel, "Play " + disp);
                if (_delClicks > 0 && Time.unscaledTime - _delClickTime > 4f) _delClicks = 0;
                if (_delClicks == 0) SetLabel(deleteLabel, "Delete " + disp + " save");
                yield return new WaitForSecondsRealtime(0.3f);
            }
        }

        private GameObject MakeMenuButton(Component template, Vector2 offset, string label, Action onClick, float scale = 1f)
        {
            try
            {
                var go = UnityEngine.Object.Instantiate(template.gameObject, template.transform.parent);
                go.name = "MapEditor_" + label.Replace(" ", "");
                var rt = go.GetComponent<RectTransform>();
                var trt = template.GetComponent<RectTransform>();
                if (rt != null && trt != null)
                {
                    rt.anchoredPosition = trt.anchoredPosition + offset;
                    rt.localScale = trt.localScale * scale;
                }
                KillLocalizers(go);
                foreach (var comp in go.GetComponentsInChildren<Behaviour>(true))
                    if (comp != null && comp.GetType().Name.Contains("TextMeshPro")) SetLabel(comp, label);
                var btn = go.GetComponent("Button");
                if (btn != null) HijackButton(btn, () => onClick());
                return go;
            }
            catch (Exception ex) { Debug.LogWarning("[MapEditor] MakeMenuButton: " + ex.Message); return null; }
        }

        private static Transform FindAncestorWithImage(Transform t)
        {
            for (; t != null; t = t.parent)
                if (t.GetComponent("Image") != null) return t;
            return null;
        }

        // A small themed box behind the two added rows so they sit on panel art
        // instead of floating over the background.
        private GameObject MakeMiniPanel(Transform panel, GameObject btnA, GameObject btnB)
        {
            try
            {
                var mini = UnityEngine.Object.Instantiate(panel.gameObject, panel.parent);
                mini.name = "MapEditor_MiniPanel";
                for (int i = mini.transform.childCount - 1; i >= 0; i--)
                    UnityEngine.Object.DestroyImmediate(mini.transform.GetChild(i).gameObject);
                foreach (var b in mini.GetComponents<Behaviour>())
                    if (b != null && b.GetType().Name != "Image" && b.GetType().Name != "CanvasRenderer")
                        UnityEngine.Object.Destroy(b);

                var aR = btnA.GetComponent<RectTransform>();
                var bR = btnB.GetComponent<RectTransform>();
                var mrt = mini.GetComponent<RectTransform>();

                // Fixed center anchors so sizeDelta is authoritative (the source
                // panel may stretch with its parent, which ignores sizeDelta).
                mrt.anchorMin = mrt.anchorMax = new Vector2(0.5f, 0.5f);
                mrt.pivot = new Vector2(0.5f, 0.5f);

                // Bounding box of both buttons in world space, plus padding
                var corners = new Vector3[4];
                Vector3 wMin = new Vector3(float.MaxValue, float.MaxValue);
                Vector3 wMax = new Vector3(float.MinValue, float.MinValue);
                foreach (var r in new[] { aR, bR })
                {
                    r.GetWorldCorners(corners);
                    foreach (var c in corners)
                    {
                        wMin = Vector3.Min(wMin, c);
                        wMax = Vector3.Max(wMax, c);
                    }
                }
                mini.transform.position = (wMin + wMax) * 0.5f;
                float lsX = Mathf.Abs(mini.transform.lossyScale.x); if (lsX < 1e-4f) lsX = 1f;
                float lsY = Mathf.Abs(mini.transform.lossyScale.y); if (lsY < 1e-4f) lsY = 1f;
                mrt.sizeDelta = new Vector2((wMax.x - wMin.x) / lsX * 1.25f, (wMax.y - wMin.y) / lsY * 1.35f);
                mini.transform.SetSiblingIndex(panel.GetSiblingIndex());   // render behind the menu/buttons
                return mini;
            }
            catch (Exception ex) { Debug.LogWarning("[MapEditor] MakeMiniPanel: " + ex.Message); return null; }
        }

        // Disable Unity Localization drivers on an object tree. LocalizeStringEvent
        // rewrites texts; GameObjectLocalizer re-applies per-locale property
        // variants (text, sprites, even RectTransforms) on every language change,
        // clobbering anything we modified.
        private static void KillLocalizers(GameObject root)
        {
            foreach (var comp in root.GetComponentsInChildren<Behaviour>(true))
            {
                if (comp == null) continue;
                string tn = comp.GetType().Name;
                if (tn == "LocalizeStringEvent" || tn == "GameObjectLocalizer") comp.enabled = false;
            }
        }

        // Override a Button's hover/press/select tint colors (reflection — the
        // plugin has no compile-time UnityEngine.UI reference)
        private static void TintButton(Component btn, Color c)
        {
            try
            {
                var prop = btn.GetType().GetProperty("colors");
                if (prop == null) return;
                object cb = prop.GetValue(btn, null);
                var t = cb.GetType();
                t.GetProperty("highlightedColor")?.SetValue(cb, c, null);
                t.GetProperty("selectedColor")  ?.SetValue(cb, c, null);
                t.GetProperty("pressedColor")   ?.SetValue(cb, new Color(c.r * 0.75f, c.g * 0.75f, c.b * 0.85f, 1f), null);
                prop.SetValue(btn, cb, null);
            }
            catch { }
        }

        private static Transform LowestCommonAncestor(Transform a, Transform b)
        {
            var seen = new HashSet<Transform>();
            for (var t = a; t != null; t = t.parent) seen.Add(t);
            for (var t = b; t != null; t = t.parent) if (seen.Contains(t)) return t;
            return null;
        }

        // The widget root = lowest common ancestor of the LinkOpener and the
        // label text ("Join the Discord" / "Wishlist on Steam") — the frame,
        // label and logo are often siblings of the clickable object, not children.
        private static GameObject FindLinkRoot(Component linkOpener, string textHint, Component otherLink)
        {
            Transform best = null;
            foreach (var mb in UnityEngine.Object.FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None))
            {
                if (mb == null || !mb.GetType().Name.Contains("TextMeshPro")) continue;
                string txt = GetLabel(mb) ?? "";
                if (txt.IndexOf(textHint, StringComparison.OrdinalIgnoreCase) < 0) continue;
                var lca = LowestCommonAncestor(linkOpener.transform, mb.transform);
                if (lca == null) continue;
                // reject roots so high they also contain the OTHER link widget
                if (otherLink != null && otherLink.transform.IsChildOf(lca)) continue;
                if (best == null || lca.IsChildOf(best)) best = lca;   // keep the deepest
            }
            if (best != null) return best.gameObject;
            var btn = FindButtonOn(linkOpener);
            return (btn != null ? btn : linkOpener).gameObject;
        }

        private static void DumpWidget(GameObject root, string tag)
        {
            try
            {
                var sb = new StringBuilder("[MapEditor] widget '" + tag + "':");
                DumpNode(root.transform, sb, 0);
                Debug.Log(sb.ToString());
            }
            catch { }
        }
        private static void DumpNode(Transform t, StringBuilder sb, int depth)
        {
            if (depth > 4) return;
            sb.Append('\n').Append(new string(' ', depth * 2)).Append(t.name).Append(" [");
            foreach (var c in t.GetComponents<Component>()) if (c != null) sb.Append(c.GetType().Name).Append(' ');
            sb.Append(']');
            foreach (Transform ch in t) DumpNode(ch, sb, depth + 1);
        }

        // Turn a Discord/Steam corner button into one of ours: relabel the text,
        // swap the logo for our icon, kill the link, install our handler.
        // NOTE: the logo Image is the Button's raycast target — it must stay
        // ENABLED or the button stops receiving clicks entirely.
        private void RepurposeLinkButton(Component linkOpener, string textHint, Component otherLink, string label, string iconKind, Action onClick)
        {
            try
            {
                // Idempotency: the menu canvas may survive scene changes, and the
                // integration runs per menu visit — never re-apply (icon scaling
                // would compound, labels would re-relabel).
                var btnGo = (FindButtonOn(linkOpener) != null ? FindButtonOn(linkOpener) : linkOpener).gameObject;
                bool alreadyDone = btnGo.name.EndsWith("[ME]");
                if (!alreadyDone) btnGo.name += " [ME]";

                var root = alreadyDone ? btnGo.transform.parent != null ? btnGo.transform.parent.gameObject : btnGo
                                       : FindLinkRoot(linkOpener, textHint, otherLink);
                KillLocalizers(root);

                if (!alreadyDone)
                {
                    foreach (var comp in root.GetComponentsInChildren<Behaviour>(true))
                    {
                        if (comp == null) continue;
                        string tn = comp.GetType().Name;
                        if (tn.Contains("TextMeshPro")) SetLabel(comp, label);
                        else if (tn == "Image" || tn == "RawImage" || tn == "SVGImage")
                        {
                            var t = comp.GetType();
                            t.GetProperty("sprite")?.SetValue(comp, GetIcon(iconKind), null);
                            t.GetProperty("color") ?.SetValue(comp, Color.white, null);
                            t.GetProperty("preserveAspect")?.SetValue(comp, true, null);
                            comp.enabled = true;
                            var crt = comp.GetComponent<RectTransform>();
                            // player sprite fills its rect via preserveAspect — any
                            // upscale overflows the frame; the drawn icon can take a bit
                            if (crt != null && iconKind != "player") crt.localScale = crt.localScale * 1.2f;
                        }
                    }
                }
                if (linkOpener is Behaviour lb) lb.enabled = false;
                var btn = FindButtonOn(linkOpener);
                if (btn != null) HijackButton(btn, () => onClick());
            }
            catch (Exception ex) { Debug.LogWarning("[MapEditor] RepurposeLinkButton: " + ex.Message); }
        }

        // Icons for the repurposed buttons — the real player sprite where
        // possible, a detailed drawn pencil otherwise.
        private readonly Dictionary<string, Sprite> _iconCache = new Dictionary<string, Sprite>();
        private Sprite GetIcon(string kind)
        {
            Sprite s;
            if (_iconCache.TryGetValue(kind, out s) && s != null) return s;

            if (kind == "player")
            {
                try
                {
                    var p = GameObject.FindGameObjectWithTag("Player");
                    var sr = p != null ? p.GetComponentInChildren<SpriteRenderer>(true) : null;
                    if (sr != null && sr.sprite != null) { _iconCache[kind] = sr.sprite; return sr.sprite; }
                }
                catch { }
                foreach (var spr in Resources.FindObjectsOfTypeAll<Sprite>())
                    if (spr != null && spr.name.StartsWith("Dot idle", StringComparison.OrdinalIgnoreCase))
                    { _iconCache[kind] = spr; return spr; }
                // fall through to the drawn pencil as a last resort
            }

            const int S = 32;
            var tex = new Texture2D(S, S, TextureFormat.RGBA32, false);
            tex.filterMode = FilterMode.Point;
            var px = new Color[S * S];
            for (int i = 0; i < px.Length; i++) px[i] = new Color(0, 0, 0, 0);
            Action<int, int, int, int, Color> rect = (x0, y0, w, h, c) =>
            {
                for (int y = y0; y < y0 + h; y++)
                    for (int x = x0; x < x0 + w; x++)
                        if (x >= 0 && x < S && y >= 0 && y < S) px[y * S + x] = c;
            };
            Color mid  = new Color(0.80f, 0.80f, 0.80f, 1f);
            Color hi   = Color.white;
            Color dim  = new Color(1f, 1f, 1f, 0.30f);

            // tile-editor icon: 3×3 tile grid; one slot empty (outline), the
            // "held" tile floating above it — axis-aligned so it scales crisp
            for (int gy = 0; gy < 3; gy++)
                for (int gx = 0; gx < 3; gx++)
                {
                    int x = 3 + gx * 9, y = 3 + gy * 9;
                    if (gx == 2 && gy == 2)
                    {
                        // empty slot: thin outline
                        rect(x, y, 8, 1, dim); rect(x, y + 7, 8, 1, dim);
                        rect(x, y, 1, 8, dim); rect(x + 7, y, 1, 8, dim);
                    }
                    else rect(x, y, 8, 8, mid);
                }
            rect(24, 24, 8, 8, hi);   // the held tile, offset up-right of the gap
            tex.SetPixels(px);
            tex.Apply();
            s = Sprite.Create(tex, new Rect(0, 0, S, S), new Vector2(0.5f, 0.5f), S);
            _iconCache[kind] = s;
            return s;
        }

        // Clone the original link buttons (frame art + working link) into Settings
        private void MoveLinksToSettings(Component discord, Component steam)
        {
            try
            {
                GameObject settings = null;
                foreach (var mb in UnityEngine.Object.FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                    if (mb != null && mb.GetType().Name == "SettingsScript") { settings = mb.gameObject; break; }
                if (settings == null) { Debug.LogWarning("[MapEditor] Settings panel not found — links not moved."); return; }
                if (settings.transform.Find("MapEditor_Link_Discord") != null) return;   // already integrated

                int i = 0;
                foreach (var src in new[] { discord, steam })
                {
                    if (src == null) { i++; continue; }
                    var clone = UnityEngine.Object.Instantiate(
                        FindLinkRoot(src, i == 0 ? "Discord" : "Wishlist", i == 0 ? steam : discord),
                        settings.transform);
                    clone.name = "MapEditor_Link_" + (i == 0 ? "Discord" : "Steam");
                    // language changes must not re-apply locale variants (they'd
                    // restore the original corner position/size onto the clone)
                    KillLocalizers(clone);
                    var rt = clone.GetComponent<RectTransform>();
                    if (rt != null)
                    {
                        // the empty gap between "Debug" and "Accept & Close" in the
                        // left column — fractional anchors so panel units don't matter
                        // (0.33 sat on top of the Debug row — keep both below it)
                        rt.anchorMin = rt.anchorMax = new Vector2(0.2f, i == 0 ? 0.25f : 0.17f);
                        rt.pivot = new Vector2(0.5f, 0.5f);
                        rt.localScale = Vector3.one * 0.55f;
                        rt.anchoredPosition = Vector2.zero;
                    }
                    // restack: icon LEFT of the text instead of below it
                    Component cloneBtn = null;
                    foreach (var c in clone.GetComponentsInChildren<Behaviour>(true))
                        if (c != null && c.GetType().Name == "Button") { cloneBtn = c; break; }
                    var brt = cloneBtn != null ? cloneBtn.GetComponent<RectTransform>() : null;
                    if (brt != null)
                    {
                        brt.anchorMin = brt.anchorMax = new Vector2(0f, 0.5f);
                        brt.pivot = new Vector2(1f, 0.5f);
                        brt.anchoredPosition = new Vector2(-6f, 0f);
                    }
                    i++;
                }

                AddBrowseToSettings(settings);
            }
            catch (Exception ex) { Debug.LogWarning("[MapEditor] MoveLinksToSettings: " + ex.Message); }
        }

        // "Browse Files" button in Settings — opens the game's save folder
        // (persistentDataPath: playerdata, profiles, customcourses) in Explorer.
        private void AddBrowseToSettings(GameObject settings)
        {
            try
            {
                Component acceptBtn = null;
                foreach (var comp in settings.GetComponentsInChildren<Behaviour>(true))
                {
                    if (comp == null || !comp.GetType().Name.Contains("TextMeshPro")) continue;
                    string txt = GetLabel(comp) ?? "";
                    if (txt.IndexOf("Accept", StringComparison.OrdinalIgnoreCase) >= 0)
                    { acceptBtn = FindButtonOn(comp); break; }
                }
                if (acceptBtn == null) { Debug.LogWarning("[MapEditor] Accept button not found — no Browse button."); return; }

                var clone = UnityEngine.Object.Instantiate(acceptBtn.gameObject, settings.transform);
                clone.name = "MapEditor_Browse";
                KillLocalizers(clone);
                foreach (var comp in clone.GetComponentsInChildren<Behaviour>(true))
                    if (comp != null && comp.GetType().Name.Contains("TextMeshPro")) SetLabel(comp, "Browse Files");
                var rt = clone.GetComponent<RectTransform>();
                if (rt != null)
                {
                    rt.anchorMin = rt.anchorMax = new Vector2(0.75f, 0.085f);   // bottom of the right column
                    rt.pivot = new Vector2(0.5f, 0.5f);
                    rt.anchoredPosition = Vector2.zero;
                    rt.localScale = rt.localScale * 0.8f;
                }
                Component btn = clone.GetComponent("Button");
                if (btn == null)
                    foreach (var c in clone.GetComponentsInChildren<Behaviour>(true))
                        if (c != null && c.GetType().Name == "Button") { btn = c; break; }
                if (btn != null) HijackButton(btn, () =>
                {
                    try { System.Diagnostics.Process.Start("explorer.exe", Application.persistentDataPath.Replace('/', '\\')); }
                    catch (Exception ex) { Debug.LogWarning("[MapEditor] Browse: " + ex.Message); }
                });
            }
            catch (Exception ex) { Debug.LogWarning("[MapEditor] AddBrowseToSettings: " + ex.Message); }
        }

        // ── "Load Map" submenu: a re-skinned clone of the menu panel ──────────
        private GameObject _mainMenuPanel, _loadMapPanel, _miniPanel;
        private GameObject _lmPrevGo, _lmNextGo;
        private object _lmPrevLbl, _lmNextLbl;
        private readonly List<Component> _lmBtns = new List<Component>();
        private readonly List<object>    _lmLabels = new List<object>();
        private int _lmPage;

        private void BuildLoadMapPanel(Transform panel)
        {
            try
            {
                _lmBtns.Clear(); _lmLabels.Clear(); _lmPage = 0;
                // a persistent canvas would accumulate clones across menu visits
                var stale = GameObject.Find("MapEditor_LoadMapPanel");
                if (stale != null) UnityEngine.Object.Destroy(stale);
                _loadMapPanel = UnityEngine.Object.Instantiate(panel.gameObject, panel.parent);
                _loadMapPanel.name = "MapEditor_LoadMapPanel";

                KillLocalizers(_loadMapPanel);
                foreach (var comp in _loadMapPanel.GetComponentsInChildren<Behaviour>(true))
                    if (comp != null && comp.GetType().Name == "Animator") comp.enabled = false;

                // menu rows become map slots — sorted top-to-bottom so slot order
                // matches what the player sees (hierarchy order can differ)
                foreach (var comp in _loadMapPanel.GetComponentsInChildren<Behaviour>(true))
                {
                    if (comp == null || comp.GetType().Name != "Button") continue;
                    object label = null;
                    foreach (var c in comp.GetComponentsInChildren<Behaviour>(true))
                        if (c != null && c.GetType().Name.Contains("TextMeshPro")) { label = c; break; }
                    if (label == null) continue;
                    _lmBtns.Add(comp); _lmLabels.Add(label);
                }
                var order = Enumerable.Range(0, _lmBtns.Count)
                    .OrderByDescending(k => _lmBtns[k].transform.position.y).ToList();
                var sortedBtns   = order.Select(k => _lmBtns[k]).ToList();
                var sortedLabels = order.Select(k => _lmLabels[k]).ToList();
                _lmBtns.Clear();   _lmBtns.AddRange(sortedBtns);
                _lmLabels.Clear(); _lmLabels.AddRange(sortedLabels);

                // uniform blue hover/press tint — rows cloned from Play/Delete/etc.
                // otherwise keep those buttons' green/red highlight colors
                foreach (var b in _lmBtns) TintButton(b, new Color(0.38f, 0.64f, 1f));

                // side-by-side page buttons (replace the second-to-last row when paging)
                _lmPrevGo = _lmNextGo = null;
                if (_lmBtns.Count >= 3)
                {
                    var pagerRowGo = _lmBtns[_lmBtns.Count - 2].gameObject;
                    _lmPrevGo = MakePagerHalf(pagerRowGo, true,  out _lmPrevLbl);
                    _lmNextGo = MakePagerHalf(pagerRowGo, false, out _lmNextLbl);
                }

                // retitle the header texts (skip texts that belong to buttons)
                foreach (var comp in _loadMapPanel.GetComponentsInChildren<Behaviour>(true))
                {
                    if (comp == null || !comp.GetType().Name.Contains("TextMeshPro")) continue;
                    bool onButton = false;
                    for (var t = comp.transform; t != null && t.gameObject != _loadMapPanel; t = t.parent)
                        if (t.GetComponent("Button") != null) { onButton = true; break; }
                    if (onButton) continue;
                    string txt = GetLabel(comp) ?? "";
                    if (txt.IndexOf("IGTAP", StringComparison.OrdinalIgnoreCase) >= 0)            SetLabel(comp, "MAPS");
                    else if (txt.IndexOf("Incremental", StringComparison.OrdinalIgnoreCase) >= 0) SetLabel(comp, "choose a map to play");
                }

                _loadMapPanel.SetActive(false);
                Debug.Log($"[MapEditor] Load Map submenu built with {_lmBtns.Count} rows.");
            }
            catch (Exception ex) { Debug.LogWarning("[MapEditor] BuildLoadMapPanel: " + ex.Message); _loadMapPanel = null; }
        }

        private void ToggleLoadMapPanel()
        {
            if (_loadMapPanel == null || _mainMenuPanel == null) return;   // only available on the main menu
            if (_loadMapPanel.activeSelf) { CloseLoadMapPanel(); return; }
            _lmPage = 0;
            RefreshLoadMapRows();
            _loadMapPanel.SetActive(true);
            _mainMenuPanel.SetActive(false);
            if (_miniPanel != null) _miniPanel.SetActive(false);
        }

        private void CloseLoadMapPanel()
        {
            if (_loadMapPanel != null) _loadMapPanel.SetActive(false);
            if (_mainMenuPanel != null) _mainMenuPanel.SetActive(true);
            if (_miniPanel != null) _miniPanel.SetActive(true);
        }

        private GameObject MakePagerHalf(GameObject rowSrc, bool left, out object label)
        {
            label = null;
            try
            {
                var go = UnityEngine.Object.Instantiate(rowSrc, rowSrc.transform.parent);
                go.name = left ? "MapEditor_PgPrev" : "MapEditor_PgNext";
                var rt = go.GetComponent<RectTransform>();
                var srcRt = rowSrc.GetComponent<RectTransform>();
                if (rt != null && srcRt != null)
                {
                    rt.anchorMin = new Vector2(left ? 0.06f : 0.54f, srcRt.anchorMin.y);
                    rt.anchorMax = new Vector2(left ? 0.46f : 0.94f, srcRt.anchorMax.y);
                    rt.sizeDelta = new Vector2(0f, srcRt.sizeDelta.y);
                    rt.anchoredPosition = new Vector2(0f, srcRt.anchoredPosition.y);
                }
                foreach (var c in go.GetComponentsInChildren<Behaviour>(true))
                    if (c != null && c.GetType().Name.Contains("TextMeshPro")) { label = c; break; }
                var btn = go.GetComponent("Button");
                if (btn != null)
                {
                    TintButton(btn, new Color(0.38f, 0.64f, 1f));
                    if (left) HijackButton(btn, () => { _lmPage--; RefreshLoadMapRows(); });
                    else      HijackButton(btn, () => { _lmPage++; RefreshLoadMapRows(); });
                }
                go.SetActive(false);
                return go;
            }
            catch (Exception ex) { Debug.LogWarning("[MapEditor] MakePagerHalf: " + ex.Message); return null; }
        }

        private void RefreshLoadMapRows()
        {
            RefreshList();
            var names   = new List<string>();
            var actions = new List<Action>();
            names.Add("Base Game"); actions.Add(() => { SelectBaseGame(); CloseLoadMapPanel(); });
            foreach (var f in _listFiles)
            {
                string ff = f;
                names.Add(Path.GetFileNameWithoutExtension(ff));
                actions.Add(() => { SelectForPlay(ff); CloseLoadMapPanel(); });
            }

            int n = _lmBtns.Count;
            if (n < 2) return;
            bool hasPager = _lmPrevGo != null && _lmNextGo != null;
            int mapSlots  = n - 1;                               // last row is always Back
            bool paged    = names.Count > mapSlots && hasPager;
            int per       = paged ? n - 2 : mapSlots;            // pager replaces a row
            if (per < 1) per = 1;
            int pages = Mathf.Max(1, Mathf.CeilToInt(names.Count / (float)per));
            if (_lmPage >= pages) _lmPage = 0;
            if (_lmPage < 0) _lmPage = pages - 1;
            int start = _lmPage * per;

            for (int i = 0; i < n - 1; i++)
            {
                var btnGo = _lmBtns[i].gameObject;
                if (paged && i == n - 2)
                {
                    // center of the pager row: page counter only (not clickable,
                    // and not blocking the < > halves' raycasts)
                    btnGo.SetActive(true);
                    SetLabel(_lmLabels[i], $"{_lmPage + 1}/{pages}");
                    if (_lmBtns[i] is Behaviour pb) pb.enabled = false;
                    (_lmLabels[i] as Component)?.GetType().GetProperty("raycastTarget")
                        ?.SetValue(_lmLabels[i], false, null);
                    continue;
                }
                int src = start + i;
                if (src < names.Count)
                {
                    btnGo.SetActive(true);
                    if (_lmBtns[i] is Behaviour rb) rb.enabled = true;   // row may have been the page counter
                    SetLabel(_lmLabels[i], names[src]);
                    var act = actions[src];
                    HijackButton(_lmBtns[i], () => act());
                }
                else btnGo.SetActive(false);
            }

            if (hasPager)
            {
                _lmPrevGo.SetActive(paged);
                _lmNextGo.SetActive(paged);
                if (paged)
                {
                    SetLabel(_lmPrevLbl, "<");
                    SetLabel(_lmNextLbl, ">");
                }
            }

            SetLabel(_lmLabels[n - 1], "Back");
            // idle stays the menu orange; hovering/pressing tints it red
            TintButton(_lmBtns[n - 1], new Color(1f, 0.30f, 0.30f, 1f));
            HijackButton(_lmBtns[n - 1], CloseLoadMapPanel);
            _lmBtns[n - 1].gameObject.SetActive(true);
        }

        // Two-click confirm, then delete ONLY the active map's data: its live
        // global save files, its profile snapshot, and its course data.
        private void ScopedDeleteClicked(object label)
        {
            if (Time.unscaledTime - _delClickTime > 4f) _delClicks = 0;
            _delClickTime = Time.unscaledTime;
            _delClicks++;
            string prof;
            try { prof = File.Exists(ProfileMarker) ? File.ReadAllText(ProfileMarker).Trim() : "_default"; }
            catch { prof = "_default"; }
            string disp = prof == "_default" ? "Base Game" : prof;

            if (_delClicks == 1) { SetLabel(label, "Really delete " + disp + " save? (click again)"); return; }
            _delClicks = 0;

            try
            {
                if (Directory.Exists(SavedataDir))
                    foreach (var f in Directory.GetFiles(SavedataDir, "*.txt"))
                        if (Path.GetFileName(f).IndexOf("keybind", StringComparison.OrdinalIgnoreCase) < 0)
                            File.Delete(f);
                string pd = Path.Combine(ProfilesDir, SafeFileName(prof));
                if (Directory.Exists(pd)) Directory.Delete(pd, true);
                if (prof != "_default" && Directory.Exists(SaveDir))
                    foreach (var suffix in new[] { "_coursedata.txt", "_mapstamp.txt" })
                    { string p = Path.Combine(SaveDir, prof + suffix); if (File.Exists(p)) File.Delete(p); }

                ZeroGlobalStats();
                PlayerPrefs.SetInt("UsingCheckpoints", 0);
                PlayerPrefs.Save();
                Debug.Log("[MapEditor] Deleted save data for '" + disp + "'.");
                SceneManager.LoadScene(SceneManager.GetActiveScene().name);
            }
            catch (Exception ex) { Debug.LogError("[MapEditor] ScopedDelete: " + ex); }
        }

        // The vanilla delete also zeroes the in-memory static dictionaries —
        // they survive scene reloads, so we must too.
        private void ZeroGlobalStats()
        {
            try
            {
                Type gs = null;
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                { gs = a.GetType("globalStats"); if (gs != null) break; }
                if (gs == null) return;
                foreach (var fname in new[] { "currencyLookup", "globalUpgradeDict" })
                {
                    if (gs.GetField(fname, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null)
                        is System.Collections.IDictionary dict)
                    {
                        var keys = new List<object>();
                        foreach (var k in dict.Keys) keys.Add(k);
                        foreach (var k in keys) dict[k] = 0.0;
                    }
                }
            }
            catch (Exception ex) { Debug.LogWarning("[MapEditor] ZeroGlobalStats: " + ex.Message); }
        }

        // Vanilla campaign: stash every custom map (so TryLoad loads nothing and
        // the original world stays intact) and switch to the "_default" profile.
        private void SelectBaseGame()
        {
            try
            {
                if (!Directory.Exists(StashDir)) Directory.CreateDirectory(StashDir);
                if (Directory.Exists(SaveDir))
                    foreach (var f in Directory.GetFiles(SaveDir, "*.json"))
                    { string sd = Path.Combine(StashDir, Path.GetFileName(f)); if (File.Exists(sd)) File.Delete(sd); File.Move(f, sd); }

                _activeMapPath = null;
                SwapPlayerProfile("_default");
                if (SceneManager.GetActiveScene().name == "MainMenu")
                { _overlayMsg = "Base game staged — click Start Demo"; _overlayMsgTime = 8f; }
                else
                {
                    // Always reload: the custom map and the hidden original world
                    // can only be undone by restarting the scene.
                    _overlayMsg = "Loading base game…"; _overlayMsgTime = 5f;
                    SceneManager.LoadScene(SceneManager.GetActiveScene().name);
                }
            }
            catch (Exception ex) { _overlayMsg = "Base game error: " + ex.Message; _overlayMsgTime = 5f; Debug.LogError("[MapEditor] SelectBaseGame: " + ex); }
        }

        private void SelectForPlay(string activePath)
        {
            try
            {
                if (!Directory.Exists(SaveDir))  Directory.CreateDirectory(SaveDir);
                if (!Directory.Exists(StashDir)) Directory.CreateDirectory(StashDir);

                string destActive = Path.Combine(SaveDir, Path.GetFileName(activePath));
                if (!File.Exists(destActive)) File.Copy(activePath, destActive);
                // selecting a stashed map: remove the stash copy so it doesn't
                // linger as a stale duplicate in the list
                if (!string.Equals(activePath, destActive, StringComparison.OrdinalIgnoreCase) &&
                    activePath.StartsWith(StashDir, StringComparison.OrdinalIgnoreCase) && File.Exists(activePath))
                    File.Delete(activePath);

                var toStash = Directory.GetFiles(SaveDir, "*.json")
                    .Where(f => !string.Equals(f, destActive, StringComparison.OrdinalIgnoreCase)).ToArray();
                foreach (string f in toStash)
                { string sd = Path.Combine(StashDir, Path.GetFileName(f)); if (File.Exists(sd)) File.Delete(sd); File.Move(f, sd); }

                _activeMapPath = activePath;
                string mapName = Path.GetFileNameWithoutExtension(activePath);
                bool swapped = SwapPlayerProfile(mapName);
                bool onMenu = SceneManager.GetActiveScene().name == "MainMenu";
                if (onMenu)
                { _overlayMsg = $"Staged: {mapName} — click Start Demo"; _overlayMsgTime = 8f; }
                else if (swapped)
                {
                    // Different map = different player profile: the swapped save files
                    // only take effect through a full scene reload (Saveloader reads
                    // them in Awake; in-memory state would otherwise autosave over them).
                    _overlayMsg = $"Loading {mapName} (own save)…"; _overlayMsgTime = 5f;
                    SceneManager.LoadScene(SceneManager.GetActiveScene().name);
                }
                else
                { _overlayMsg = $"Loading {mapName}…"; _overlayMsgTime = 5f; StartCoroutine(ReloadMapsCoroutine(activePath)); }
            }
            catch (Exception ex) { _overlayMsg = $"Stage error: {ex.Message}"; _overlayMsgTime = 5f; Debug.LogError("[MapEditor] SelectForPlay: " + ex); }
        }

        // ═════════════════════════════════════════════════════════════════════
        // Per-map player profiles — each map gets its own playerdata/save files.
        // Global saves (Savedata/*.txt) are snapshotted into profiles/<map>/ on
        // switch; the marker file records which profile the live files belong to.
        // ═════════════════════════════════════════════════════════════════════
        private static string SavedataDir => Path.Combine(Application.persistentDataPath, "Savedata");
        private static string ProfilesDir => Path.Combine(SavedataDir, "profiles");
        private static string ProfileMarker => Path.Combine(ProfilesDir, "active.txt");

        // Returns true if the active profile changed (scene reload required).
        private bool SwapPlayerProfile(string newProfile)
        {
            try
            {
                Directory.CreateDirectory(ProfilesDir);
                string current = File.Exists(ProfileMarker) ? File.ReadAllText(ProfileMarker).Trim() : "_default";
                if (string.Equals(current, newProfile, StringComparison.OrdinalIgnoreCase)) return false;

                // Flush in-memory state so the snapshot is current (game scene only)
                ForceSaveAll();

                // Snapshot the live global save files into the outgoing profile
                string curDir = Path.Combine(ProfilesDir, SafeFileName(current));
                Directory.CreateDirectory(curDir);
                foreach (var f in Directory.GetFiles(SavedataDir, "*.txt"))
                {
                    if (Path.GetFileName(f).IndexOf("keybind", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    File.Copy(f, Path.Combine(curDir, Path.GetFileName(f)), true);
                    File.Delete(f);
                }

                // Restore the incoming profile's files (none = fresh start)
                string newDir = Path.Combine(ProfilesDir, SafeFileName(newProfile));
                if (Directory.Exists(newDir))
                    foreach (var f in Directory.GetFiles(newDir, "*.txt"))
                        File.Copy(f, Path.Combine(SavedataDir, Path.GetFileName(f)), true);

                File.WriteAllText(ProfileMarker, newProfile);
                Debug.Log($"[MapEditor] Player profile swapped: '{current}' -> '{newProfile}'");
                return true;
            }
            catch (Exception ex) { Debug.LogError("[MapEditor] SwapPlayerProfile: " + ex); return false; }
        }

        private void ForceSaveAll()
        {
            try
            {
                foreach (var mb in UnityEngine.Object.FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None))
                {
                    if (mb == null) continue;
                    bool saveable = false;
                    for (var bt = mb.GetType(); bt != null; bt = bt.BaseType)
                        if (bt.Name == "SaveableObject") { saveable = true; break; }
                    if (!saveable) continue;
                    try { mb.GetType().GetMethod("save", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.Invoke(mb, null); }
                    catch { }
                }
            }
            catch (Exception ex) { Debug.LogWarning("[MapEditor] ForceSaveAll: " + ex.Message); }
        }

        private IEnumerator ReloadMapsCoroutine(string activePath)
        {
            yield return null;
            var existing = GameObject.Find("CustomMapRoot");
            if (existing != null) Destroy(existing);
            // Stale course clones and reset sensors from a previous TryLoad live
            // outside CustomMapRoot — destroy them too or each reload stacks an
            // extra (invisible) course with live gate triggers.
            foreach (var go in UnityEngine.Object.FindObjectsByType<GameObject>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (go != null && (go.name.StartsWith("CustomCourse_") || go.name.StartsWith("CourseResetSensor_")))
                    Destroy(go);
            yield return null;

            try
            {
                Assembly gameAsm = null;
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                    if (a.GetName().Name == "Assembly-CSharp") { gameAsm = a; break; }

                Type cml = null;
                if (gameAsm != null)
                {
                    cml = gameAsm.GetType("CustomMapLoader");
                    if (cml == null)
                    {
                        Type[] types = null;
                        try { types = gameAsm.GetTypes(); } catch (ReflectionTypeLoadException rtle) { types = rtle.Types; } catch { }
                        if (types != null) foreach (var t in types) if (t != null && t.Name == "CustomMapLoader") { cml = t; break; }
                    }
                }

                if (cml == null) { _overlayMsg = gameAsm == null ? "AsmCSharp missing" : "CustomMapLoader not found"; _overlayMsgTime = 5f; yield break; }

                var tryLoad = cml.GetMethod("TryLoad", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (tryLoad == null) { _overlayMsg = "TryLoad not found"; _overlayMsgTime = 5f; yield break; }

                var before    = new HashSet<Renderer>  (UnityEngine.Object.FindObjectsByType<Renderer>  (FindObjectsInactive.Include, FindObjectsSortMode.None));
                var beforeCol = new HashSet<Collider2D>(UnityEngine.Object.FindObjectsByType<Collider2D>(FindObjectsInactive.Include, FindObjectsSortMode.None));
                tryLoad.Invoke(null, null);

                HideOriginalWorld(before, beforeCol);
                SanitizeCourseClones();
                ApplyTileOverrides();
                ApplyCourseSettings();

                _overlayMsg = $"Loaded: {Path.GetFileNameWithoutExtension(activePath)}"; _overlayMsgTime = 4f;
            }
            catch (Exception ex) { _overlayMsg = $"Load error: {ex.Message}"; _overlayMsgTime = 5f; Debug.LogError("[MapEditor] ReloadMaps: " + ex); }
        }

        // ═════════════════════════════════════════════════════════════════════
        // Erase
        // ═════════════════════════════════════════════════════════════════════
        private void EraseAt(Vector2Int gc)
        {
            _tiles.Remove(gc);
            _checkpoints.RemoveAll(p  => Mathf.RoundToInt(p.x) == gc.x  && Mathf.RoundToInt(p.y) == gc.y);
            _swapTriggers.RemoveAll(p => Mathf.RoundToInt(p.x) == gc.x  && Mathf.RoundToInt(p.y) == gc.y);
            int bi = BoxAt(gc.x, gc.y);   if (bi >= 0) { _boxes.RemoveAt(bi);    if (_selType==SelType.Box    && _selIdx==bi) { _selType=SelType.None; _selIdx=-1; } }
            int hi = HbAt(gc.x, gc.y);   if (hi >= 0) { _hitboxes.RemoveAt(hi); if (_selType==SelType.Hitbox && _selIdx==hi) { _selType=SelType.None; _selIdx=-1; } }
            int ii = ImageAt(gc.x, gc.y); if (ii >= 0) { _images.RemoveAt(ii);   if (_selType==SelType.Image  && _selIdx==ii) { _selType=SelType.None; _selIdx=-1; } }
            if (MatchV2(_spawn,    gc)) _spawn    = null;
            if (MatchV2(_gate,     gc)) _gate     = null;
            if (MatchV2(_end,      gc)) _end      = null;
            if (MatchV2(_uiMarker, gc)) _uiMarker = null;
        }

        // ═════════════════════════════════════════════════════════════════════
        // Serializer
        // ═════════════════════════════════════════════════════════════════════
        private static string SerializeMap(MapFile m)
        {
            var sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine($"  \"version\": {m.version},");
            sb.AppendLine($"  \"spawnX\": {F(m.spawnX)}, \"spawnY\": {F(m.spawnY)},");
            sb.AppendLine($"  \"gateX\":  {F(m.gateX)},  \"gateY\":  {F(m.gateY)},");
            sb.AppendLine($"  \"endX\":   {F(m.endX)},   \"endY\":   {F(m.endY)},");
            sb.AppendLine($"  \"uiX\":    {F(m.uiX)},    \"uiY\":    {F(m.uiY)},");
            AppendArr(sb, "boxes",        m.boxes.Select(b => $"{{\"x\":{F(b.x)},\"y\":{F(b.y)},\"upgrade\":{b.upgrade},\"cap\":{b.cap},\"price\":{b.price}}}"), true);
            AppendArr(sb, "images",       m.images.Select(img => $"{{\"path\":{Jstr(img.path)},\"x\":{F(img.x)},\"y\":{F(img.y)},\"w\":{F(img.w)},\"h\":{F(img.h)},\"rot\":{F(img.rot)},\"opacity\":{F(img.opacity)},\"cr\":{F(img.cr)},\"cg\":{F(img.cg)},\"cb\":{F(img.cb)},\"depth\":0}}"), true);
            AppendArr(sb, "swapTriggers", m.swapTriggers.Select(s => $"{{\"x\":{F(s.x)},\"y\":{F(s.y)}}}"), true);
            AppendArr(sb, "checkpoints",  m.checkpoints.Select(c  => $"{{\"x\":{F(c.x)},\"y\":{F(c.y)}}}"), true);
            AppendArr(sb, "hitboxes",     m.hitboxes.Select(h     => $"{{\"x\":{F(h.x)},\"y\":{F(h.y)},\"w\":{F(h.w)},\"h\":{F(h.h)}}}"), true);
            AppendArr(sb, "tiles",        m.tiles.Select(t        => $"{{\"x\":{t.x},\"y\":{t.y},\"layer\":{t.layer},\"rot\":0,\"sprite\":\"\"}}"), false);
            sb.AppendLine("}");
            return sb.ToString();
        }

        private static string F(float v)    => v.ToString("G", System.Globalization.CultureInfo.InvariantCulture);
        private static string Jstr(string s) => "\"" + (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

        private static void AppendArr(StringBuilder sb, string key, IEnumerable<string> items, bool comma)
        {
            var arr = items.ToArray();
            sb.Append($"  \"{key}\": [");
            for (int i = 0; i < arr.Length; i++) { sb.Append(i == 0 ? "\n" : ",\n"); sb.Append("    " + arr[i]); }
            sb.AppendLine(arr.Length > 0 ? (comma ? "\n  ]," : "\n  ]") : (comma ? "]," : "]"));
        }

        // ═════════════════════════════════════════════════════════════════════
        // Sprite extraction (unchanged)
        // ═════════════════════════════════════════════════════════════════════
        private IEnumerator ExtractSpritesCoroutine()
        {
            yield return null;
            try { TryExtractGameSprites(); }
            catch (Exception ex) { Debug.LogError("[MapEditor] ExtractSprites: " + ex); }
        }

        private void TryExtractGameSprites()
        {
            // ── tile sprites from tilemaps ────────────────────────────────────
            var tilemaps = UnityEngine.Object.FindObjectsByType<Tilemap>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            Sprite spikeSprite = null;
            var solids = new List<Tilemap>();
            foreach (var tm in tilemaps)
            {
                string n = tm.gameObject.name.ToLower();
                bool isSpike = n.Contains("spike") || n.Contains("hazard") || n.Contains("danger");
                var rend = tm.GetComponent<TilemapRenderer>();
                bool hasColl = tm.GetComponent<TilemapCollider2D>() != null;
                if (isSpike) { if (spikeSprite == null) spikeSprite = GetFirstTileSprite(tm); }
                else if (rend != null && rend.enabled && hasColl) solids.Add(tm);
            }
            solids.Sort((a, b) => b.GetUsedTilesCount().CompareTo(a.GetUsedTilesCount()));
            Sprite groundSprite = solids.Count > 0 ? GetFirstTileSprite(solids[0]) : null;
            Sprite altSprite    = solids.Count > 1 ? GetFirstTileSprite(solids[1]) : null;
            if (groundSprite != null || spikeSprite != null) ApplyGameSprites(groundSprite, altSprite, spikeSprite);

            // ── object sprites from scene SpriteRenderers ─────────────────────
            FindObjectSprites();
        }

        private void FindObjectSprites()
        {
            var allSR = UnityEngine.Object.FindObjectsByType<SpriteRenderer>(FindObjectsInactive.Include, FindObjectsSortMode.None);

            // Pass 1: match by component type name (most reliable)
            foreach (var mb in UnityEngine.Object.FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                string typeName = mb.GetType().Name;
                SpriteRenderer sr = mb.GetComponent<SpriteRenderer>()
                                 ?? mb.GetComponentInChildren<SpriteRenderer>(true);
                if (sr == null || sr.sprite == null) continue;
                Sprite s = sr.sprite;

                if (_sprBox   == null && typeName == "upgradeBox")    { _sprBox   = s; _uvBox   = SpriteUV(s); }
                if (_sprCp    == null && typeName == "checkpointScript") { _sprCp = s; _uvCp    = SpriteUV(s); }
                if (_sprSw    == null && (typeName.Contains("SwapTrigger") || typeName.Contains("swapTrigger") || typeName == "colouredBlockSwapper")) { _sprSw = s; _uvSw = SpriteUV(s); }
                if (_sprGate  == null && (typeName.Contains("Gate") || typeName.Contains("gate"))) { _sprGate = s; _uvGate = SpriteUV(s); }
                if (_sprSpawn == null && (typeName.Contains("Spawn") || typeName.Contains("spawn") || typeName.Contains("Respawn"))) { _sprSpawn = s; _uvSpawn = SpriteUV(s); }
                if (_sprEnd   == null && typeName.Contains("End") && !typeName.Contains("Render")) { _sprEnd = s; _uvEnd = SpriteUV(s); }
            }

            // Pass 2: fallback — match by GameObject / parent name heuristics
            foreach (var sr in allSR)
            {
                if (sr.sprite == null) continue;
                Sprite s = sr.sprite;
                string n  = sr.gameObject.name.ToLower();
                string pn = sr.transform.parent != null ? sr.transform.parent.gameObject.name.ToLower() : "";

                if (_sprBox   == null && (n.Contains("upgrade") || pn.Contains("upgrade") || n.Contains("box"))) { _sprBox   = s; _uvBox   = SpriteUV(s); }
                if (_sprCp    == null && (n.Contains("checkpoint") || pn.Contains("checkpoint")))                 { _sprCp    = s; _uvCp    = SpriteUV(s); }
                if (_sprSw    == null && (n.Contains("swap") || pn.Contains("swap")))                             { _sprSw    = s; _uvSw    = SpriteUV(s); }
                if (_sprGate  == null && (n.Contains("gate") || pn.Contains("gate")))                             { _sprGate  = s; _uvGate  = SpriteUV(s); }
                if (_sprSpawn == null && (n.Contains("spawn") || n.Contains("respawn") || pn.Contains("spawn"))) { _sprSpawn = s; _uvSpawn = SpriteUV(s); }
                if (_sprEnd   == null && (n.Contains("end") && !n.Contains("render")))                            { _sprEnd   = s; _uvEnd   = SpriteUV(s); }
            }
        }

        private static Sprite GetFirstTileSprite(Tilemap tm)
        {
            if (tm.GetUsedTilesCount() == 0) return null;
            var buf = new TileBase[1];
            tm.GetUsedTilesNonAlloc(buf);
            TileBase tb = buf[0];
            if (tb == null) return null;
            if (tb is Tile tile && tile.sprite != null) return tile.sprite;
            try { return tb.GetType().GetField("sprite", BindingFlags.Public | BindingFlags.Instance)?.GetValue(tb) as Sprite; } catch { return null; }
        }

        private void ApplyGameSprites(Sprite ground, Sprite alt, Sprite spike)
        {
            _layerSprite = new Sprite[7];
            _layerUV     = new Rect[7];
            _layerTint   = (Color[])LayerTint.Clone();
            _layerSprite[0] = ground; _layerSprite[1] = alt ?? ground; _layerSprite[2] = spike;
            _layerSprite[3] = ground; _layerSprite[4] = ground; _layerSprite[5] = spike; _layerSprite[6] = spike;
            if (alt == null && ground != null) _layerTint[1] = new Color(0.78f, 0.78f, 0.82f);
            for (int i = 0; i < 7; i++) if (_layerSprite[i] != null) _layerUV[i] = SpriteUV(_layerSprite[i]);
        }

        private static Rect SpriteUV(Sprite s) => new Rect(
            s.rect.x / s.texture.width,  s.rect.y / s.texture.height,
            s.rect.width / s.texture.width, s.rect.height / s.texture.height);

        private void DrawLayerTile(Rect r, int layer)
        {
            if (_layerSprite != null && _layerSprite[layer] != null)
            {
                Color prev = GUI.color;
                GUI.color = _layerTint[layer];
                GUI.DrawTextureWithTexCoords(r, _layerSprite[layer].texture, _layerUV[layer]);
                GUI.color = prev;
            }
            else
                GUI.DrawTexture(r, _layerTex[layer]);
        }

        // ═════════════════════════════════════════════════════════════════════
        // Web editor server
        // ═════════════════════════════════════════════════════════════════════
        private static void StartEditorServer()
        {
            if (_editorServer != null && !_editorServer.HasExited) return;
            if (!Directory.Exists(EDITOR_DIR)) { Debug.LogWarning("[MapEditor] igtap-editor not found at " + EDITOR_DIR); return; }
            try
            {
                _editorServer = new System.Diagnostics.Process
                {
                    StartInfo = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName         = "node",
                        Arguments        = "server.js",
                        WorkingDirectory = EDITOR_DIR,
                        UseShellExecute  = false,
                        CreateNoWindow   = true,
                    }
                };
                _editorServer.Start();
                Debug.Log("[MapEditor] Web editor server started → " + EDITOR_URL);
            }
            catch (Exception ex) { Debug.LogWarning("[MapEditor] Could not start editor server: " + ex.Message); }
        }

        private static void OpenEditorInBrowser()
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                { FileName = EDITOR_URL, UseShellExecute = true });
            }
            catch (Exception ex) { Debug.LogWarning("[MapEditor] Could not open browser: " + ex.Message); }
        }

        // ═════════════════════════════════════════════════════════════════════
        // Sprite export (writes PNGs + manifest.json → %AppData%/IGTAPEditor/sprites)
        // ═════════════════════════════════════════════════════════════════════
        private IEnumerator ExportSpritesCoroutine()
        {
            yield return new WaitForEndOfFrame();

            string exportDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "IGTAPEditor", "sprites");
            try { Directory.CreateDirectory(exportDir); }
            catch (Exception ex)
            {
                _overlayMsg = "Export failed: " + ex.Message; _overlayMsgTime = 4f;
                Debug.LogError("[MapEditor] Export dir: " + ex); yield break;
            }

            var spriteToFile = new Dictionary<Sprite, string>();

            Sprite ground = _layerSprite != null ? _layerSprite[0] : null;
            Sprite alt    = (_layerSprite != null && _layerSprite[1] != _layerSprite[0]) ? _layerSprite[1] : null;
            Sprite spike  = _layerSprite != null ? _layerSprite[2] : null;

            TrySaveSprite(exportDir, ground, "tile_ground.png", spriteToFile);
            TrySaveSprite(exportDir, alt,    "tile_alt.png",    spriteToFile);
            TrySaveSprite(exportDir, spike,  "tile_spike.png",  spriteToFile);
            TrySaveSprite(exportDir, _sprBox,   "obj_box.png",         spriteToFile);
            TrySaveSprite(exportDir, _sprCp,    "obj_checkpoint.png",  spriteToFile);
            TrySaveSprite(exportDir, _sprSw,    "obj_swaptrigger.png", spriteToFile);
            TrySaveSprite(exportDir, _sprSpawn, "obj_spawn.png",       spriteToFile);
            TrySaveSprite(exportDir, _sprGate,  "obj_gate.png",        spriteToFile);
            TrySaveSprite(exportDir, _sprEnd,   "obj_end.png",         spriteToFile);

            var layerFiles = new Dictionary<int, string>();
            if (_layerSprite != null)
                for (int i = 0; i < _layerSprite.Length; i++)
                    if (_layerSprite[i] != null && spriteToFile.TryGetValue(_layerSprite[i], out string lf))
                        layerFiles[i] = lf;

            var objFiles = new Dictionary<string, string>();
            if (_sprBox   != null && spriteToFile.TryGetValue(_sprBox,   out var bf))  objFiles["upgradebox"]  = bf;
            if (_sprCp    != null && spriteToFile.TryGetValue(_sprCp,    out var cpf)) objFiles["checkpoint"]  = cpf;
            if (_sprSw    != null && spriteToFile.TryGetValue(_sprSw,    out var swf)) objFiles["swaptrigger"] = swf;
            if (_sprSpawn != null && spriteToFile.TryGetValue(_sprSpawn, out var spf)) objFiles["spawn"]       = spf;
            if (_sprGate  != null && spriteToFile.TryGetValue(_sprGate,  out var gf))  objFiles["gate"]        = gf;
            if (_sprEnd   != null && spriteToFile.TryGetValue(_sprEnd,   out var ef))  objFiles["end"]         = ef;

            // Tileset pieces come from the editor's sprite dump (filenames there are
            // the true runtime sprite names) — no atlas export needed: atlas pages
            // pack unrelated sprites together, so texture matching grabs garbage.
            WriteManifest(exportDir, layerFiles, objFiles);

            _overlayMsg = $"Exported {spriteToFile.Count} sprites";
            _overlayMsgTime = 5f;
            Debug.Log($"[MapEditor] Sprites exported → {exportDir}");
        }

        private static void TrySaveSprite(string dir, Sprite spr, string filename, Dictionary<Sprite, string> done)
        {
            if (spr == null || done.ContainsKey(spr)) return;
            try
            {
                var tex = ReadSpriteAsTexture(spr);
                if (tex == null) return;
                var png = EncodeToPNG(tex);
                Destroy(tex);
                if (png == null || png.Length == 0) return;
                File.WriteAllBytes(Path.Combine(dir, filename), png);
                done[spr] = filename;
            }
            catch (Exception ex) { Debug.LogWarning($"[MapEditor] Export {filename}: {ex.Message}"); }
        }

        private static Texture2D ReadSpriteAsTexture(Sprite spr)
        {
            var src  = spr.texture;
            var rect = spr.textureRect;
            int w = Mathf.Max(1, (int)rect.width);
            int h = Mathf.Max(1, (int)rect.height);

            // CPU path — works when texture is Read/Write enabled
            try
            {
                var pix = src.GetPixels((int)rect.x, (int)rect.y, w, h);
                var t   = new Texture2D(w, h, TextureFormat.RGBA32, false);
                t.SetPixels(pix); t.Apply();
                return t;
            }
            catch { }

            // GPU path — blit full atlas to RenderTexture, then crop
            var rt   = RenderTexture.GetTemporary(src.width, src.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default);
            var prev = RenderTexture.active;
            Graphics.Blit(src, rt);
            RenderTexture.active = rt;

            var full = new Texture2D(src.width, src.height, TextureFormat.RGBA32, false);
            full.ReadPixels(new Rect(0, 0, src.width, src.height), 0, 0);
            full.Apply();

            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);

            var crop = new Texture2D(w, h, TextureFormat.RGBA32, false);
            crop.SetPixels(full.GetPixels((int)rect.x, (int)rect.y, w, h));
            crop.Apply();
            Destroy(full);
            return crop;
        }

        private static MethodInfo _encodePngMI;
        private static byte[] EncodeToPNG(Texture2D tex)
        {
            if (_encodePngMI == null)
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    var t = asm.GetType("UnityEngine.ImageConversion");
                    if (t != null) { _encodePngMI = t.GetMethod("EncodeToPNG", new[] { typeof(Texture2D) }); break; }
                }
            return _encodePngMI?.Invoke(null, new object[] { tex }) as byte[];
        }

        private void WriteManifest(string dir, Dictionary<int, string> layerFiles, Dictionary<string, string> objFiles,
                                   List<KeyValuePair<string, string>> tilesetEntries = null)
        {
            var sb = new StringBuilder();
            sb.Append("{\n  \"spriteDir\": "); sb.Append(Jstr(dir)); sb.Append(",\n  \"layerMap\": {\n");
            bool first = true;
            for (int i = 0; i < 7; i++)
            {
                if (!layerFiles.TryGetValue(i, out string fn)) continue;
                if (!first) sb.Append(",\n"); first = false;
                Color t = _layerTint != null ? _layerTint[i] : LayerTint[i];
                sb.Append($"    \"{i}\": {{\"file\":{Jstr(fn)},\"tint\":[{F(t.r)},{F(t.g)},{F(t.b)},{F(t.a)}],\"name\":{Jstr(LayerName[i])}}}");
            }
            sb.Append("\n  },\n  \"objectMap\": {\n");
            first = true;
            foreach (var kv in objFiles)
            {
                if (!first) sb.Append(",\n"); first = false;
                sb.Append($"    {Jstr(kv.Key)}: {Jstr(kv.Value)}");
            }
            sb.Append("\n  },\n  \"tileset\": [\n");
            first = true;
            if (tilesetEntries != null)
                foreach (var kv in tilesetEntries)
                {
                    if (!first) sb.Append(",\n"); first = false;
                    sb.Append($"    {{\"name\":{Jstr(kv.Key)},\"file\":{Jstr(kv.Value)}}}");
                }
            sb.Append("\n  ]\n}");
            File.WriteAllText(Path.Combine(dir, "manifest.json"), sb.ToString(), Encoding.UTF8);
        }

        private static string SafeFileName(string name)
        {
            var sb = new StringBuilder(name.Length);
            foreach (char c in name)
                sb.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? '_' : c);
            return sb.ToString();
        }

        // ═════════════════════════════════════════════════════════════════════
        // Image loading
        // ═════════════════════════════════════════════════════════════════════
        private static System.Reflection.MethodInfo _loadImageMI;

        private Texture2D LoadImageTex(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            if (_imgCache.TryGetValue(path, out var cached)) return cached;
            if (!File.Exists(path)) return null;
            try
            {
                // Resolve ImageConversion.LoadImage via reflection (avoids netstandard 2.1 ref constraint)
                if (_loadImageMI == null)
                {
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        var t = asm.GetType("UnityEngine.ImageConversion");
                        if (t != null) { _loadImageMI = t.GetMethod("LoadImage", new[] { typeof(Texture2D), typeof(byte[]) }); break; }
                    }
                }
                if (_loadImageMI == null) return null;
                var data = File.ReadAllBytes(path);
                var tex  = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                bool ok  = (bool)_loadImageMI.Invoke(null, new object[] { tex, data });
                if (ok) { tex.hideFlags = HideFlags.HideAndDontSave; _imgCache[path] = tex; return tex; }
                Destroy(tex); return null;
            }
            catch { return null; }
        }

        // ═════════════════════════════════════════════════════════════════════
        // Textures & Styles
        // ═════════════════════════════════════════════════════════════════════
        private void BuildTextures()
        {
            _layerTex = new Texture2D[LayerCol.Length];
            for (int i = 0; i < LayerCol.Length; i++) _layerTex[i] = Tex1(LayerCol[i]);

            _spawnTex         = Tex1(new Color(0.20f, 0.88f, 0.30f));
            _gateTex          = Tex1(new Color(1.00f, 0.88f, 0.08f));
            _endTex           = Tex1(new Color(0.88f, 0.10f, 0.88f));
            _cpTex            = Tex1(new Color(0.10f, 0.88f, 0.88f));
            _swTex            = Tex1(new Color(0.68f, 0.20f, 0.88f));
            _boxTex           = Tex1(new Color(0.88f, 0.72f, 0.12f, 0.92f));
            _selBoxTex        = Tex1(new Color(1.00f, 0.96f, 0.28f, 0.98f));
            _uiTex            = Tex1(new Color(0.48f, 0.18f, 0.80f, 0.90f));
            _hbFill           = Tex1(new Color(1.00f, 0.25f, 0.25f, 0.18f));
            _hbEdge           = Tex1(new Color(1.00f, 0.30f, 0.30f, 0.88f));
            _cursorTex        = Tex1(new Color(1.00f, 1.00f, 1.00f, 0.15f));
            _eraseTex         = Tex1(new Color(1.00f, 0.10f, 0.10f, 0.28f));
            _selHlTex         = Tex1(new Color(1.00f, 0.94f, 0.20f, 0.92f));
            _imgPlaceholderTex= Tex1(new Color(0.40f, 0.40f, 0.52f, 0.35f));
            _darkBg           = Tex1(new Color(0.07f, 0.07f, 0.10f));
            _panelBg          = Tex1(new Color(0.09f, 0.09f, 0.13f));
            _topbarBg         = Tex1(new Color(0.07f, 0.07f, 0.11f));
            _statusBg         = Tex1(new Color(0.05f, 0.05f, 0.08f));
            _gridLine         = Tex1(new Color(0.20f, 0.20f, 0.26f, 0.40f));
            _majorLine        = Tex1(new Color(0.28f, 0.28f, 0.38f, 0.55f));
            _originLine       = Tex1(new Color(0.40f, 0.40f, 0.55f, 0.75f));
            _divider          = Tex1(new Color(0.22f, 0.22f, 0.30f, 0.60f));
            _texOk            = true;
        }

        private static Texture2D Tex1(Color c)
        {
            var t = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            t.SetPixel(0, 0, c); t.Apply();
            t.hideFlags = HideFlags.HideAndDontSave;
            return t;
        }

        private void EnsureStyles()
        {
            if (_stylesOk) return;
            _sBtn       = new GUIStyle(GUI.skin.button)  { fontSize = 13, normal = { textColor = new Color(0.90f, 0.90f, 0.90f) } };
            _sBtnClose  = new GUIStyle(_sBtn)            { fontStyle = FontStyle.Bold, normal = { textColor = Color.white } };
            _sBtnDanger = new GUIStyle(_sBtn)            { normal = { textColor = new Color(1f, 0.4f, 0.4f) } };
            _sLabel     = new GUIStyle(GUI.skin.label)   { fontSize = 13, normal = { textColor = new Color(0.82f, 0.82f, 0.86f) } };
            _sTitle     = new GUIStyle(GUI.skin.label)   { fontSize = 11, normal = { textColor = new Color(0.58f, 0.58f, 0.68f) } };
            _sSectionHdr= new GUIStyle(GUI.skin.label)   { fontSize = 10, fontStyle = FontStyle.Bold, normal = { textColor = new Color(0.48f, 0.48f, 0.62f) } };
            _sToolBtn   = new GUIStyle(GUI.skin.button)  { fontSize = 12, alignment = TextAnchor.MiddleLeft, normal = { textColor = new Color(0.80f, 0.80f, 0.86f) } };
            _sToolActive= new GUIStyle(_sToolBtn)        { fontStyle = FontStyle.Bold, normal = { textColor = new Color(0.18f, 0.95f, 0.50f) } };
            _sField     = new GUIStyle(GUI.skin.textField){ fontSize = 13, normal = { textColor = new Color(0.92f, 0.92f, 0.92f) } };
            _sFieldSmall= new GUIStyle(_sField)          { fontSize = 11 };
            _stylesOk   = true;
        }

        // ═════════════════════════════════════════════════════════════════════
        // Misc helpers
        // ═════════════════════════════════════════════════════════════════════
        private void Status(string msg) { _statusMsg = msg; _statusTime = 3.5f; }
        private static string FmtV(Vector2? v) => v.HasValue ? $"({v.Value.x:F0},{v.Value.y:F0})" : "—";
        private void InfoLine(float x, ref float y, float bw, string text)
        { GUI.Label(new Rect(x, y, bw, 18), text, _sLabel); y += 19; }
    }
}
