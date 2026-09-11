using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using Newtonsoft.Json;
using ToolBox.Serialization;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;

namespace WalkRoute
{
    /// <summary>
    /// Walk Route: invisible mandatory walking direction for customers and staff, built the way the game's entrance gate does
    /// it, but without the gate, animation and sound. A line on the floor closes the passage on the NavMesh (carving
    /// NavMeshObstacle) and a one-way NavMeshLink straight through the line is the only way across. The only visible part is
    /// an arrow on the floor (in placement mode), so you can find the route again and pick it up. Meant for checkout lanes:
    /// customers no longer walk against the flow past the checkout.
    ///
    /// Controls: P = placement mode on/off, scroll = rotate by 15°, Ctrl+scroll = by 5°, Q/E = by 90°, Shift+scroll =
    /// width, left mouse = place, right mouse on an arrow = pick up, Delete on an arrow = remove. Key help in a panel
    /// cloned from the game's notification panel (same font, color and background), see HelpPanel.
    /// Storage: BepInEx\config\WalkRoute\WalkRoute_Profile_&lt;saveslot&gt;.json (not in the game's save).
    /// </summary>
    [BepInPlugin(GUID, NAME, VERSION)]
    public class WalkRoutePlugin : BaseUnityPlugin
    {
        public const string GUID = "eyonator.megastore.walkroute";
        public const string NAME = "Walk Route";
        public const string VERSION = "1.0.0";
        private const string GameScene = "GameScenePC";
        private const int FloorLayer = 10;              // RayShooter.FLOOR_LAYER
        private const float ArrowDepth = 0.5f;          // depth of the arrow on the floor (meters)
        private const float LinkHalfLength = 0.35f;     // the link starts/ends this far in front of and behind the line (outside the carved zone)

        internal static ManualLogSource Log;
        internal static ConfigEntry<KeyCode> ToggleKey, DeleteKey, ReloadKey, ClearAllKey;
        internal static ConfigEntry<float> DefaultWidth, WidthStep, LinkWidth, ObstacleDepth, Alpha, EditAlpha, MaxDistance;
        internal static ConfigEntry<bool> BothWays, ShowHelp;
        internal static ConfigEntry<Corner> HelpCorner;
        internal static ConfigEntry<float> HelpScale;
        public enum Corner { TopLeft, TopRight, BottomLeft, BottomRight }
        internal static string RootDir;
        private string status = ""; private float statusUntil;

        private readonly List<PlacedRoute> placed = new List<PlacedRoute>();
        private bool placementMode, inGame;
        private float width = 2f, rotation;
        private GameObject preview;
        private static Mesh arrowMesh;
        private static Texture2D arrowTexture;
        private static Material placedMaterial, previewMaterial;

        private void Awake()
        {
            Log = Logger;
            ToggleKey = Config.Bind("Keys", "ToggleKey", KeyCode.P, "Toggle placement mode.");
            DeleteKey = Config.Bind("Keys", "DeleteKey", KeyCode.Delete, "Remove the route you are looking at (placement mode only).");
            ReloadKey = Config.Bind("Keys", "ReloadKey", KeyCode.None, "Rebuild all routes of this save (None = off).");
            ClearAllKey = Config.Bind("Keys", "ClearAllKey", KeyCode.None, "Remove all routes in this save (None = off).");
            DefaultWidth = Config.Bind("Route", "DefaultWidth", 2f, "Width in meters of a new route.");
            WidthStep = Config.Bind("Route", "WidthStep", 0.25f, "Width change in meters per scroll tick with Shift+scroll (range 0.5 to 10 m).");
            LinkWidth = Config.Bind("Route", "LinkWidth", 0f, "Width of the one-way passage in meters. 0 = the full route width (customers may cross anywhere along the line). The game's entrance gate uses 0.6.");
            ObstacleDepth = Config.Bind("Route", "ObstacleDepth", 0.1f, "Thickness in meters of the blocking line on the navigation mesh.");
            BothWays = Config.Bind("Route", "BothWays", false, "Allow crossing in both directions (the route is then only a marker). Off = one-way, for staff too.");
            Alpha = Config.Bind("Look", "Alpha", 0f, "Visibility of the arrows outside placement mode (0 = invisible, not drawn at all; 1 = fully visible). Picking up always works, because that happens in placement mode.");
            EditAlpha = Config.Bind("Look", "EditAlpha", 0.85f, "Visibility of all arrows in placement mode.");
            MaxDistance = Config.Bind("Placement", "MaxDistance", 30f, "Maximum distance in meters to the floor when placing.");
            ShowHelp = Config.Bind("Look", "ShowHelp", true, "Show the key help panel in placement mode (styled like the game's own panels).");
            HelpCorner = Config.Bind("Look", "HelpCorner", Corner.BottomLeft, "Screen corner of the key help panel.");
            HelpScale = Config.Bind("Look", "HelpScale", 1f, "Size of the key help panel (1 = about half the size of the game's notification text).");
            width = Mathf.Clamp(DefaultWidth.Value, 0.5f, 10f);
            RootDir = Path.Combine(Paths.ConfigPath, "WalkRoute");
            Directory.CreateDirectory(RootDir);
            SceneManager.sceneLoaded += OnSceneLoaded;
            Log.LogInfo(NAME + " " + VERSION + " loaded. " + ToggleKey.Value + " = placement mode.");
        }

        private void OnDestroy() { SceneManager.sceneLoaded -= OnSceneLoaded; }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            inGame = scene.name == GameScene;
            placementMode = false; DestroyPreview();
            if (inGame) StartCoroutine(InitAfterLoad());
            else placed.Clear();
        }

        private IEnumerator InitAfterLoad()
        {
            yield return new WaitForSeconds(2f);
            Reload();
        }

        private void Reload()
        {
            EnsureAssets();
            SaveStore.Bind();
            DestroySpawned();
            LoadPlaced();
            Log.LogInfo("Ready: " + placed.Count + " walk route(s) active.");
        }

        // ---------- controls ----------

        private void Update()
        {
            if (!inGame) return;
            if (ReloadKey.Value != KeyCode.None && Input.GetKeyDown(ReloadKey.Value)) { Reload(); Say("Looproutes opnieuw opgebouwd", "Walk routes rebuilt"); }
            if (Input.GetKeyDown(ToggleKey.Value)) TogglePlacement();
            if (ClearAllKey.Value != KeyCode.None && Input.GetKeyDown(ClearAllKey.Value)) ClearAll();
            if (!placementMode) return;

            if (Input.GetKeyDown(DeleteKey.Value)) TryDelete();
            if (Input.GetKeyDown(KeyCode.Q)) rotation += 90f;   // not D: that is walking (WASD)
            if (Input.GetKeyDown(KeyCode.E)) rotation -= 90f;
            float scroll = Input.GetAxis("Mouse ScrollWheel");
            if (!Mathf.Approximately(scroll, 0f))
            {
                bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
                bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
                if (shift) width = Mathf.Clamp(width + Mathf.Sign(scroll) * WidthStep.Value, 0.5f, 10f);
                else rotation += Mathf.Sign(scroll) * (ctrl ? 5f : 15f);
            }
            if (Input.GetMouseButtonDown(0)) TryPlace();
            if (Input.GetMouseButtonDown(1)) TryPickUp();
            UpdatePreview();
            if (ShowHelp.Value) HelpPanel.SetText(HelpText());
        }

        private string HelpText()
        {
            bool nl = HelpPanel.Nl;
            if (Time.unscaledTime > statusUntil) status = "";
            string t = nl
                ? "<b>Looproute</b>  ·  " + placed.Count + " geplaatst\n" +
                  "Breedte " + width.ToString("0.00") + " m  ·  richting " + Mathf.Repeat(rotation, 360f).ToString("0") + "°\n" +
                  "<b>Scroll</b> draaien (Ctrl fijn, Q/E 90°)   <b>Shift+scroll</b> breedte\n" +
                  "<b>Links</b> neerleggen   <b>Rechts</b> oppakken   <b>" + DeleteKey.Value + "</b> weghalen   <b>" + ToggleKey.Value + "</b> klaar\n" +
                  "Klanten mogen alleen in de richting van de pijl door de streep."
                : "<b>Walk route</b>  ·  " + placed.Count + " placed\n" +
                  "Width " + width.ToString("0.00") + " m  ·  direction " + Mathf.Repeat(rotation, 360f).ToString("0") + "°\n" +
                  "<b>Scroll</b> rotate (Ctrl fine, Q/E 90°)   <b>Shift+scroll</b> width\n" +
                  "<b>Left</b> place   <b>Right</b> pick up   <b>" + DeleteKey.Value + "</b> remove   <b>" + ToggleKey.Value + "</b> done\n" +
                  "Customers may only cross the line in the direction of the arrow.";
            if (status.Length > 0) t += "\n<color=#FFD24A>" + status + "</color>";
            return t;
        }

        private void Say(string nl, string en) { status = HelpPanel.Nl ? nl : en; statusUntil = Time.unscaledTime + 4f; }

        private void TogglePlacement()
        {
            placementMode = !placementMode;
            if (placementMode) { EnsureAssets(); if (preview == null) CreatePreview(); status = ""; if (ShowHelp.Value) HelpPanel.Show(HelpText()); }
            else { DestroyPreview(); HelpPanel.Hide(); }
            SetPlacedAlpha(placementMode ? EditAlpha.Value : Alpha.Value);
        }

        // ---------- preview ----------

        private void CreatePreview()
        {
            DestroyPreview();
            preview = new GameObject("WalkRoutePreview");
            preview.AddComponent<MeshFilter>().sharedMesh = arrowMesh;
            var mr = preview.AddComponent<MeshRenderer>(); mr.sharedMaterial = previewMaterial; mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            preview.SetActive(false);
        }

        private void DestroyPreview() { if (preview != null) { Destroy(preview); preview = null; } }

        private void UpdatePreview()
        {
            if (preview == null) return;
            if (!AimAtFloor(out var hit)) { preview.SetActive(false); return; }
            preview.SetActive(true);
            preview.transform.SetPositionAndRotation(hit.point + Vector3.up * 0.004f, Quaternion.Euler(0f, rotation, 0f));
            preview.transform.localScale = new Vector3(width, 1f, ArrowDepth);
        }

        private bool AimAtFloor(out RaycastHit hit)
        {
            hit = default;
            var cam = Camera.main; if (cam == null) return false;
            var ray = cam.ScreenPointToRay(Input.mousePosition);
            // the store floor first, otherwise any other horizontal solid surface (outside, storage room)
            if (Physics.Raycast(ray, out hit, MaxDistance.Value, 1 << FloorLayer, QueryTriggerInteraction.Ignore)) return true;
            if (!Physics.Raycast(ray, out hit, MaxDistance.Value, ~((1 << 2) | (1 << 5)), QueryTriggerInteraction.Ignore)) return false;
            return Vector3.Dot(hit.normal, Vector3.up) > 0.9f;
        }

        private PlacedRoute AimAtPlaced()
        {
            var cam = Camera.main; if (cam == null) return null;
            var ray = cam.ScreenPointToRay(Input.mousePosition);
            PlacedRoute best = null; float bestD = float.MaxValue;
            foreach (var h in Physics.RaycastAll(ray, MaxDistance.Value, ~0, QueryTriggerInteraction.Collide))
            {
                var pr = h.collider.GetComponentInParent<PlacedRoute>();
                if (pr != null && h.distance < bestD) { best = pr; bestD = h.distance; }
            }
            if (best == null) return null;
            // something solid in front of it (wall, furniture)? then not; the floor itself lies 4 mm behind it and does not count
            if (Physics.Raycast(ray, out var solid, bestD - 0.02f, ~((1 << 2) | (1 << 5)), QueryTriggerInteraction.Ignore)) return null;
            return best;
        }

        // ---------- place, pick up, remove ----------

        private void TryPlace()
        {
            if (!AimAtFloor(out var hit)) return;
            var rec = new SaveStore.Record { id = Guid.NewGuid().ToString("N"), X = hit.point.x, Y = hit.point.y, Z = hit.point.z, RotY = rotation, Width = width };
            Spawn(rec, EditAlpha.Value);
            SaveStore.AddOrUpdate(rec);
            Say("Looproute neergelegd", "Walk route placed");
        }

        private PlacedRoute Spawn(SaveStore.Record rec, float alpha)
        {
            var go = new GameObject("WalkRoute_" + rec.id);
            go.transform.SetPositionAndRotation(new Vector3(rec.X, rec.Y, rec.Z), Quaternion.Euler(0f, rec.RotY, 0f));
            // arrow on the floor (the visible part, also the surface for picking up)
            var arrow = new GameObject("Arrow");
            arrow.transform.SetParent(go.transform, false);
            arrow.transform.localPosition = Vector3.up * 0.004f;
            arrow.transform.localScale = new Vector3(rec.Width, 1f, ArrowDepth);
            arrow.AddComponent<MeshFilter>().sharedMesh = arrowMesh;
            var mr = arrow.AddComponent<MeshRenderer>(); mr.sharedMaterial = placedMaterial; mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            var bc = arrow.AddComponent<BoxCollider>(); bc.isTrigger = true; bc.center = new Vector3(0f, 0.01f, 0f); bc.size = new Vector3(1f, 0.02f, 1f);
            // close the line on the NavMesh: carving obstacle across the full width (like the ENTRANCE_GATE child of the gate)
            var obs = go.AddComponent<NavMeshObstacle>();
            obs.shape = NavMeshObstacleShape.Box;
            obs.center = new Vector3(0f, 0.5f, 0f);
            obs.size = new Vector3(rec.Width, 1f, Mathf.Max(0.02f, ObstacleDepth.Value));
            obs.carving = true; obs.carveOnlyStationary = true;
            var pr = go.AddComponent<PlacedRoute>(); pr.Id = rec.id; pr.Width = rec.Width; pr.RotY = rec.RotY;
            pr.AddLink();
            placed.Add(pr);
            StartCoroutine(CheckCarve(pr));
            return pr;
        }

        /// <summary>Check in the log: after one and a half seconds, does the line really carve the NavMesh? (No mesh may remain on the line.)</summary>
        private IEnumerator CheckCarve(PlacedRoute pr)
        {
            yield return new WaitForSeconds(1.5f);
            if (pr == null) yield break;
            Vector3 p = pr.transform.position;
            bool onLine = NavMesh.SamplePosition(p, out var h1, 0.04f, NavMesh.AllAreas);
            bool before = NavMesh.SamplePosition(p - pr.transform.forward * 0.5f, out var h2, 0.3f, NavMesh.AllAreas);
            bool after = NavMesh.SamplePosition(p + pr.transform.forward * 0.5f, out var h3, 0.3f, NavMesh.AllAreas);
            Log.LogInfo("Walk route " + pr.Id.Substring(0, 8) + ": mesh on the line " + (onLine ? "YES (not carved, y=" + h1.position.y.ToString("0.000") + ")" : "no (carved)") +
                        ", before " + (before ? "yes y=" + h2.position.y.ToString("0.000") : "NO") + ", after " + (after ? "yes y=" + h3.position.y.ToString("0.000") : "NO") +
                        ", own y=" + p.y.ToString("0.000") + ", link " + (pr.HasLink ? "ok" : "MISSING"));
        }

        private void TryDelete()
        {
            var pr = AimAtPlaced(); if (pr == null) return;
            SaveStore.Remove(pr.Id); placed.Remove(pr); Destroy(pr.gameObject);
            Say("Looproute weggehaald", "Walk route removed");
        }

        private void TryPickUp()
        {
            var pr = AimAtPlaced(); if (pr == null) return;
            SaveStore.Remove(pr.Id); placed.Remove(pr);
            width = pr.Width; rotation = pr.RotY;
            Destroy(pr.gameObject);
            if (preview == null) CreatePreview();
            Say("Looproute opgepakt; klik om opnieuw neer te leggen", "Walk route picked up; click to place it again");
        }

        private void ClearAll()
        {
            DestroySpawned(); SaveStore.Clear();
            Say("Alle looproutes weggehaald", "All walk routes removed");
        }

        private void DestroySpawned()
        {
            foreach (var pr in placed) if (pr != null) Destroy(pr.gameObject);
            placed.Clear();
            foreach (var pr in FindObjectsOfType<PlacedRoute>()) Destroy(pr.gameObject);
        }

        private void LoadPlaced()
        {
            foreach (var rec in SaveStore.Records()) Spawn(rec, Alpha.Value);
            SetPlacedAlpha(placementMode ? EditAlpha.Value : Alpha.Value);
            if (placed.Count > 0) Log.LogInfo(placed.Count + " walk route(s) restored.");
        }

        private void SetPlacedAlpha(float a)
        {
            if (placedMaterial == null) return;
            var c = placedMaterial.color; c.a = Mathf.Clamp01(a); placedMaterial.color = c;
            if (placedMaterial.HasProperty("_BaseColor")) placedMaterial.SetColor("_BaseColor", c);
            // at 0, don't draw the arrow at all; the collider stays, so picking up (in placement mode) still works
            bool draw = a > 0.001f;
            foreach (var pr in placed) if (pr != null) foreach (var mr in pr.GetComponentsInChildren<MeshRenderer>(true)) mr.enabled = draw;
        }

        // ---------- mesh, texture, materials ----------

        private static void EnsureAssets()
        {
            if (arrowMesh != null) return;
            // 1 x 1 m square flat on the floor, centered; the arrow points to +z (the direction of the route)
            arrowMesh = new Mesh { name = "WalkRouteArrow" };
            arrowMesh.vertices = new[] { new Vector3(-0.5f, 0f, -0.5f), new Vector3(0.5f, 0f, -0.5f), new Vector3(0.5f, 0f, 0.5f), new Vector3(-0.5f, 0f, 0.5f) };
            arrowMesh.uv = new[] { new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(0f, 1f) };
            arrowMesh.normals = new[] { Vector3.up, Vector3.up, Vector3.up, Vector3.up };
            arrowMesh.triangles = new[] { 0, 2, 1, 0, 3, 2 };
            arrowMesh.RecalculateBounds();

            string custom = Path.Combine(RootDir, "arrow.png");
            arrowTexture = File.Exists(custom) ? LoadPng(custom) : null;
            if (arrowTexture == null) arrowTexture = MakeArrowTexture(256);
            arrowTexture.wrapMode = TextureWrapMode.Repeat;

            var shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Unlit/Transparent") ?? Shader.Find("Standard");
            placedMaterial = MakeMaterial(shader, "WalkRoutePlaced", Alpha.Value);
            previewMaterial = MakeMaterial(shader, "WalkRoutePreview", 0.9f);
        }

        private static Material MakeMaterial(Shader shader, string name, float alpha)
        {
            var m = new Material(shader) { name = name, mainTexture = arrowTexture };
            m.color = new Color(1f, 1f, 1f, alpha);
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", new Color(1f, 1f, 1f, alpha));
            if (m.HasProperty("_BaseMap")) m.SetTexture("_BaseMap", arrowTexture);
            if (shader.name.StartsWith("Universal Render Pipeline/"))
            {
                m.SetFloat("_Surface", 1f); m.SetFloat("_Blend", 0f);
                m.SetInt("_SrcBlend", 5); m.SetInt("_DstBlend", 10); m.SetInt("_ZWrite", 0);
                m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT"); m.renderQueue = 3000;
            }
            return m;
        }

        /// <summary>Tileable arrow (chevrons) in blue on a transparent background; the arrow points to +v (= +z).</summary>
        private static Texture2D MakeArrowTexture(int size)
        {
            var t = new Texture2D(size, size, TextureFormat.RGBA32, false) { name = "WalkRouteArrowTex" };
            var blue = new Color(0.05f, 0.15f, 0.55f, 1f); var white = new Color(1f, 1f, 1f, 1f); var none = new Color(0f, 0f, 0f, 0f);
            var px = new Color[size * size];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    // v flipped: the arms of the chevron lie lower than the tip, so the arrow points to +v (= +z, the walking direction)
                    float u = (x + 0.5f) / size, v = 1f - (y + 0.5f) / size;
                    // light band across the full width, with three chevrons on top that point to +v
                    Color c = new Color(1f, 1f, 1f, 0.35f);
                    float ax = Mathf.Abs(u - 0.5f);           // 0 in the middle, 0.5 at the edges
                    for (int k = 0; k < 3; k++)
                    {
                        float baseV = 0.12f + k * 0.28f;      // bottom of the chevron tip in the middle
                        float tip = baseV + 0.20f;            // tip of the chevron in the middle
                        float vv = v - ax * 0.35f;            // slanted legs: the farther from the middle, the lower
                        if (vv > baseV && vv < tip && ax < 0.42f) c = blue;
                        else if (vv > baseV - 0.03f && vv < tip + 0.03f && ax < 0.45f && c.a < 0.9f) c = white;
                    }
                    px[y * size + x] = c;
                    if (u < 0.02f || u > 0.98f) px[y * size + x] = none;
                }
            t.SetPixels(px); t.Apply();
            return t;
        }

        private static Texture2D LoadPng(string path)
        {
            try
            {
                var t = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                var mi = typeof(ImageConversion).GetMethod("LoadImage", new[] { typeof(Texture2D), typeof(byte[]), typeof(bool) });
                if (mi != null && (bool)mi.Invoke(null, new object[] { t, File.ReadAllBytes(path), false })) return t;
            }
            catch (Exception ex) { Log.LogWarning("arrow.png not loaded: " + ex.Message); }
            return null;
        }
    }

    /// <summary>
    /// Key help in the game's style: a clone of the game's notification panel (MiddleTooltipUI: background Image and
    /// TextMeshPro text with the game's font, color and outline) under the same canvas, smaller and in a corner.
    /// If that panel cannot be found (for example in another game version), no help panel is shown.
    /// </summary>
    internal static class HelpPanel
    {
        private static GameObject root;
        private static RectTransform rect;
        private static TMPro.TextMeshProUGUI text;
        private static float baseFontSize = 36f;
        private static string lastText;
        private static WalkRoutePlugin.Corner lastCorner; private static float lastScale = -1f;

        internal static bool Nl
        {
            get
            {
                string l = DFTGames.Localization.Locale.CurrentLanguage ?? "";
                return l.IndexOf("Dutch", StringComparison.OrdinalIgnoreCase) >= 0 || l.IndexOf("Nederlands", StringComparison.OrdinalIgnoreCase) >= 0;
            }
        }

        private static bool Build()
        {
            if (root != null) return true;
            try
            {
                var tip = SingletonBehaviour<MiddleTooltipUI>.Instance;
                if (tip == null) return false;
                var canvas = tip.GetComponentInParent<Canvas>();
                if (canvas == null) return false;
                // clone into a disabled holder, so Awake of the cloned MiddleTooltipUI does not run (singleton)
                root = new GameObject("WalkRouteHelp", typeof(RectTransform));
                root.transform.SetParent(canvas.transform, false);
                root.SetActive(false);
                var rr = root.GetComponent<RectTransform>(); rr.anchorMin = Vector2.zero; rr.anchorMax = Vector2.one; rr.offsetMin = Vector2.zero; rr.offsetMax = Vector2.zero;
                var clone = UnityEngine.Object.Instantiate(tip.gameObject, root.transform, false);
                clone.name = "Panel";
                foreach (var mb in clone.GetComponents<MonoBehaviour>()) if (mb is MiddleTooltipUI) UnityEngine.Object.DestroyImmediate(mb);
                var cg = clone.GetComponent<CanvasGroup>(); if (cg != null) { cg.alpha = 1f; cg.blocksRaycasts = false; cg.interactable = false; }
                rect = clone.GetComponent<RectTransform>();
                text = clone.GetComponentInChildren<TMPro.TextMeshProUGUI>(true);
                if (text == null) { UnityEngine.Object.Destroy(root); root = null; return false; }
                baseFontSize = text.fontSize > 0 ? text.fontSize : 36f;
                text.enableAutoSizing = false;
                text.richText = true;
                text.alignment = TMPro.TextAlignmentOptions.TopLeft;
                text.enableWordWrapping = false;
                text.overflowMode = TMPro.TextOverflowModes.Overflow;
                var tr = text.rectTransform; tr.anchorMin = Vector2.zero; tr.anchorMax = Vector2.one; tr.offsetMin = new Vector2(22f, 14f); tr.offsetMax = new Vector2(-22f, -14f);
                var img = clone.GetComponentInChildren<UnityEngine.UI.Image>(true);
                WalkRoutePlugin.Log.LogInfo("Help panel: clone of the game's notification panel (font " + (text.font != null ? text.font.name : "?") + ", font size " + baseFontSize + ", background " + (img != null ? (img.sprite != null ? img.sprite.name : "color") : "none") + ").");
                return true;
            }
            catch (Exception ex)
            {
                WalkRoutePlugin.Log.LogWarning("Help panel not created: " + ex.Message);
                if (root != null) { UnityEngine.Object.Destroy(root); root = null; }
                return false;
            }
        }

        internal static void Show(string t)
        {
            if (!Build()) return;
            lastText = null; lastScale = -1f;
            root.SetActive(true);
            SetText(t);
        }

        internal static void Hide() { if (root != null) root.SetActive(false); }

        internal static void SetText(string t)
        {
            if (root == null || !root.activeSelf || text == null) return;
            var corner = WalkRoutePlugin.HelpCorner.Value; float scale = WalkRoutePlugin.HelpScale.Value;
            if (t == lastText && corner == lastCorner && Mathf.Approximately(scale, lastScale)) return;
            lastText = t; lastCorner = corner; lastScale = scale;
            text.fontSize = baseFontSize * 0.5f * scale;
            text.text = t;
            // size the panel to the text, in the chosen corner
            Vector2 pref = text.GetPreferredValues(t);
            rect.sizeDelta = new Vector2(pref.x + 44f, pref.y + 28f);
            bool right = corner == WalkRoutePlugin.Corner.TopRight || corner == WalkRoutePlugin.Corner.BottomRight;
            bool top = corner == WalkRoutePlugin.Corner.TopLeft || corner == WalkRoutePlugin.Corner.TopRight;
            var a = new Vector2(right ? 1f : 0f, top ? 1f : 0f);
            rect.anchorMin = a; rect.anchorMax = a; rect.pivot = a;
            rect.anchoredPosition = new Vector2(right ? -24f : 24f, top ? -24f : 24f);
        }
    }

    /// <summary>
    /// A placed walk route: maintains the one-way link on the NavMesh (like the NavMeshLink on the Obstacle child of the
    /// entrance gate: start 0.33 m in front of the line, end 0.34 m behind it, bidirectional = false, area Walkable, agent type 0).
    /// Created with NavMesh.AddLink, so Unity.AI.Navigation.dll is not needed; the link is removed together with the route.
    /// </summary>
    public class PlacedRoute : MonoBehaviour
    {
        public string Id;
        public float Width, RotY;
        private NavMeshLinkInstance link;
        private bool hasLink;
        public bool HasLink => hasLink;

        public void AddLink()
        {
            RemoveLink();
            var fwd = transform.forward;
            float w = WalkRoutePlugin.LinkWidth.Value > 0f ? Mathf.Min(WalkRoutePlugin.LinkWidth.Value, Width) : Width;
            var data = new NavMeshLinkData
            {
                startPosition = transform.position - fwd * 0.35f,
                endPosition = transform.position + fwd * 0.35f,
                width = Mathf.Max(0.1f, w - 0.1f),
                bidirectional = WalkRoutePlugin.BothWays.Value,
                area = 0,
                agentTypeID = 0,
                costModifier = -1f,
            };
            link = NavMesh.AddLink(data);
            hasLink = NavMesh.IsLinkValid(link);
            if (!hasLink) WalkRoutePlugin.Log.LogWarning("Walk route " + Id + ": NavMeshLink not created (is the line on the navigation mesh?).");
        }

        private void RemoveLink()
        {
            if (hasLink) { NavMesh.RemoveLink(link); hasLink = false; }
        }

        private void OnDestroy() { RemoveLink(); }
    }

    /// <summary>JSON per save slot in BepInEx\config\WalkRoute\WalkRoute_Profile_&lt;n&gt;.json.</summary>
    public static class SaveStore
    {
        [Serializable]
        public class Record { public string id; public float X, Y, Z, RotY, Width; }
        private class Wrapper { public List<Record> Items = new List<Record>(); }

        private static readonly List<Record> items = new List<Record>();
        private static string file;
        private static readonly FieldInfo ProfileField = typeof(DataSerializer).GetField("_currentProfileIndex", BindingFlags.Static | BindingFlags.NonPublic);

        public static void Bind()
        {
            int profile = -1;
            try { if (ProfileField != null) profile = (int)ProfileField.GetValue(null); } catch { }
            file = Path.Combine(WalkRoutePlugin.RootDir, "WalkRoute_Profile_" + (profile >= 0 ? profile.ToString() : "unknown") + ".json");
            items.Clear();
            try
            {
                if (File.Exists(file))
                {
                    var w = JsonConvert.DeserializeObject<Wrapper>(File.ReadAllText(file));
                    if (w?.Items != null) items.AddRange(w.Items);
                }
            }
            catch (Exception ex) { WalkRoutePlugin.Log.LogError("Save file unreadable: " + file + " (" + ex.Message + ")"); }
            WalkRoutePlugin.Log.LogInfo("Save file: " + file + " (" + items.Count + " walk routes)");
        }

        public static IEnumerable<Record> Records() => items.ToList();
        public static void AddOrUpdate(Record r) { items.RemoveAll(x => x.id == r.id); items.Add(r); Write(); }
        public static void Remove(string id) { items.RemoveAll(x => x.id == id); Write(); }
        public static void Clear() { items.Clear(); Write(); }

        private static void Write()
        {
            if (file == null) return;
            try { File.WriteAllText(file, JsonConvert.SerializeObject(new Wrapper { Items = items }, Formatting.Indented)); }
            catch (Exception ex) { WalkRoutePlugin.Log.LogError("Saving failed: " + ex.Message); }
        }
    }
}
