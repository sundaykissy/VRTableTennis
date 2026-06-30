using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// VISUAL CONDITION — three independently toggleable augmented feedback components
/// for the VR serve-training study.
///
///   Component 1: Player-side table highlight
///     Semi-transparent blue quad covering the player half of the table.
///     Marks the interaction/boundary zone without affecting physics.
///     Pulses brighter when the paddle is within 0.25 m of the table surface.
///
///   Component 3: Opponent-side depth-reference lines
///     Three thin longitudinal lines (soft cyan-blue, ~17 % opacity) on the
///     opponent half of the table, 3 mm above the surface.  Purely static.
///     Mitigates VR depth-perception limitation — spatial reference only,
///     not a landing-zone or coaching cue.
///
/// Group mapping (set by SetCondition):
///   1 = Standard  → all OFF
///   2 = Visual    → all ON
///   3 = Multimodal→ all ON  (haptics via WoojerHapticManager)
///
/// DO NOT modify ExperimentLogger or physics from this script.
/// </summary>
public class VisualFeedbackManager : MonoBehaviour
{
    public static VisualFeedbackManager Instance { get; private set; }

    // ── Independent component toggles (Inspector-accessible) ─────────────────
    [Header("Independent component toggles — Standard: all off | Visual/Multimodal: all on")]
    [Tooltip("Component 1: Player-side table highlight (boundary / depth cue)")]
    public bool EnableTableHighlight = false;

    [Tooltip("Component 3: Opponent-side reference grid (landing-zone / depth perception)")]
    public bool EnableReferenceGrid = false;

    // ── Colour palette ────────────────────────────────────────────────────────
    // Component 1
    private static readonly Color C1_IDLE   = new Color(0.25f, 0.65f, 1.00f, 0.18f);
    private static readonly Color C1_PULSE  = new Color(0.25f, 0.65f, 1.00f, 0.50f);
    // Component 3 — soft cyan-blue depth-reference lines (low salience, non-instructional)
    private static readonly Color C3_DEPTH      = new Color(0.35f, 0.75f, 1.00f, 0.17f);  // longitudinal
    private static readonly Color C3_TRANSVERSE = new Color(0.35f, 0.75f, 1.00f, 0.12f);  // transverse bands — slightly secondary

    // ── Runtime GameObjects ───────────────────────────────────────────────────
    private GameObject    _tableHighlightObj;
    private MeshRenderer  _tableHighlightMr;
    private GameObject    _referenceGridObj;

    // ── Scene references ──────────────────────────────────────────────────────
    private Transform _paddleTransform;
    private float     _tableTopY = 0.76f;
    private float     _netZ      = 0f;

    private bool _componentsBuilt = false;
    private int  _group          = 1;   // safe default — no augmentation

    // ── Pulse state (Component 1) ─────────────────────────────────────────────
    private const float PULSE_DIST = 0.25f;   // paddle within 25 cm → brighter highlight

    // ─────────────────────────────────────────────────────────────────────────
    // Auto-instantiation
    // ─────────────────────────────────────────────────────────────────────────
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void AutoCreate()
    {
        var go = new GameObject("[VisualFeedbackManager]");
        go.AddComponent<VisualFeedbackManager>();
        DontDestroyOnLoad(go);
    }

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        SceneManager.sceneLoaded += OnSceneLoaded;
    }

    void Start()
    {
        // Handle the case where the game scene is already active at startup
        // (e.g. Editor play-mode starting directly in game__room).
        string active = SceneManager.GetActiveScene().name;
        if (IsGameScene(active) && !_componentsBuilt)
            StartCoroutine(BuildNextFrame());
    }

    void OnDestroy()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Public API
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Configure components for the participant's assigned group.
    ///   1 = Standard  → all three OFF
    ///   2 = Visual    → all three ON
    ///   3 = Multimodal→ all three ON (haptics via WoojerHapticManager)
    /// </summary>
    public void SetCondition(int group)
    {
        _group = group;
        bool on = group >= 2;
        EnableTableHighlight = on;
        EnableReferenceGrid  = on;
        ApplyToggles();
        Debug.Log($"[Condition] Group selected = {group}");
        Debug.Log($"[VisualFeedbackManager] SetCondition group={group} → all_on={on}");

        // If the game scene is already loaded, apply immediately.
        // If not loaded yet, ApplyConditionToDepthSystems() will be called
        // again from BuildComponents() once the scene is ready.
        ApplyConditionToDepthSystems();
    }

    // Propagate the stored group to BallDepthPerceptionCue and
    // SpinVisualizationManager.  Safe to call before those objects exist
    // (FindObjectOfType returns null → calls are skipped).
    void ApplyConditionToDepthSystems()
    {
        // If neither component is in the scene, auto-create them on a new
        // persistent GameObject — no manual scene wiring required.
        var depthCue = FindObjectOfType<BallDepthPerceptionCue>();
        if (depthCue == null)
            depthCue = TryCreateDepthCue();

        if (depthCue != null)
        {
            depthCue.SetCondition(_group);
            Debug.Log($"[Condition] BallDepthCue applied group={_group}");
        }
        else
        {
            Debug.LogWarning("[Condition] BallDepthPerceptionCue not found and could not be created.");
        }

        var spinViz = FindObjectOfType<SpinVisualizationManager>();
        if (spinViz == null)
            spinViz = TryCreateSpinViz(depthCue);

        if (spinViz != null)
        {
            spinViz.SetCondition(_group);
            Debug.Log($"[Condition] SpinViz applied group={_group}");
        }
        else
        {
            Debug.LogWarning("[Condition] SpinVisualizationManager not found and could not be created.");
        }
    }

    // Creates BallDepthPerceptionCue on a new GO, letting Awake auto-find ball + table.
    // Returns null if the ball or table collider are not yet in the scene.
    BallDepthPerceptionCue TryCreateDepthCue()
    {
        // Need the ball rigidbody to exist before we can create the cue.
        if (FindObjectOfType<ballbounce>() == null) return null;
        if (GameObject.Find("playerside") == null) return null;

        var go  = new GameObject("[Auto] BallDepthPerceptionCue");
        var cue = go.AddComponent<BallDepthPerceptionCue>();  // Awake auto-looks-up refs
        return cue.enabled ? cue : null;
    }

    // Creates SpinVisualizationManager on the same GO as depthCue (or a new one).
    SpinVisualizationManager TryCreateSpinViz(BallDepthPerceptionCue depthCue)
    {
        if (FindObjectOfType<ballbounce>() == null) return null;
        if (GameObject.Find("playerside") == null) return null;

        var parent = depthCue != null ? depthCue.gameObject : new GameObject("[Auto] SpinVisualizationManager");
        var viz    = parent.AddComponent<SpinVisualizationManager>(); // Awake auto-looks-up refs
        if (depthCue != null) viz.depthCue = depthCue;
        return viz.enabled ? viz : null;
    }

    /// <summary>
    /// Push the current toggle state to all built components.
    /// Call after manually changing any Enable* field at runtime.
    /// </summary>
    public void ApplyToggles()
    {
        if (!_componentsBuilt) return;
        if (_tableHighlightObj) _tableHighlightObj.SetActive(EnableTableHighlight);
        if (_referenceGridObj)  _referenceGridObj.SetActive(EnableReferenceGrid);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Scene lifecycle
    // ─────────────────────────────────────────────────────────────────────────

    void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (IsGameScene(scene.name))
        {
            _componentsBuilt = false;
            StartCoroutine(BuildNextFrame());
        }
        else
        {
            // Login / other scene — hide everything.
            if (_tableHighlightObj) _tableHighlightObj.SetActive(false);
            if (_referenceGridObj)  _referenceGridObj.SetActive(false);
        }
    }

    IEnumerator BuildNextFrame()
    {
        yield return null;   // wait one frame so scene objects are fully initialised
        BuildComponents();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Component construction
    // ─────────────────────────────────────────────────────────────────────────

    void BuildComponents()
    {
        CacheSceneReferences();

        // Destroy any stale objects from a previous scene load.
        DestroyIfExists(ref _tableHighlightObj);
        DestroyIfExists(ref _referenceGridObj);

        //BuildComponent1_TableHighlight();
        BuildComponent3_ReferenceGrid();

        _componentsBuilt = true;
        ApplyToggles();

        // Re-apply the stored group to depth systems now that scene objects exist.
        // This is the primary dispatch point — LoginManager calls SetCondition()
        // before the game scene loads, so depth-system objects don't exist yet.
        // By the time BuildComponents() runs (one frame after scene load) they do.
        ApplyConditionToDepthSystems();

        Debug.Log($"[VisualFeedbackManager] Scene components built. " +
                  $"Highlight={EnableTableHighlight} " +
                  $"Grid={EnableReferenceGrid}");

        LogConditionDiag();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Startup condition diagnostic
    // ─────────────────────────────────────────────────────────────────────────

    void LogConditionDiag()
    {
        // VFM state
        string vfmState = $"TableHighlight={EnableTableHighlight} Grid={EnableReferenceGrid}";

        // BallDepthPerceptionCue state
        var dc = FindObjectOfType<BallDepthPerceptionCue>();
        string dcState = dc != null
            ? $"Shadow={dc.EnableShadowCue} Tint={dc.EnableTableTint} Trail={dc.EnableMotionTrail}"
            : "NOT FOUND";

        // SpinVisualizationManager state
        var sv = FindObjectOfType<SpinVisualizationManager>();
        string svState = sv != null
            ? $"Mode={sv.mode} SpinViz={sv.EnableSpinVisualization}"
            : "NOT FOUND";

        // WoojerHapticManager state — reads same GroupNumber gate
        var wh = FindObjectOfType<WoojerHapticManager>();
        string whState = wh != null ? $"found (GroupNumber gate={ExperimentLogger.GroupNumber})" : "NOT FOUND";

        // ForwardBoundarySystem state
        var fb = FindObjectOfType<ForwardBoundarySystem>();
        string fbState = fb != null ? $"found (GroupNumber gate={ExperimentLogger.GroupNumber})" : "NOT FOUND";

        // Cross-check: VFM internal group vs ExperimentLogger.GroupNumber
        string groupMatch = (_group == ExperimentLogger.GroupNumber)
            ? "OK"
            : $"MISMATCH (VFM={_group} Logger={ExperimentLogger.GroupNumber})";

        Debug.Log(
            $"[CONDITION-DIAG] ── Group={_group} Logger.GroupNumber={ExperimentLogger.GroupNumber} Match={groupMatch}\n" +
            $"  VFM       : {vfmState}\n" +
            $"  DepthCue  : {dcState}\n" +
            $"  SpinViz   : {svState}\n" +
            $"  Haptics   : {whState}\n" +
            $"  Boundary  : {fbState}"
        );
    }

    void CacheSceneReferences()
    {
        // Table geometry
        _tableTopY = 0.76f;
        _netZ      = 0f;

        var ps = GameObject.Find("playerside");
        var es = GameObject.Find("enemyside");

        if (ps != null)
        {
            var col = ps.GetComponent<Collider>();
            if (col != null) _tableTopY = col.bounds.max.y;
        }
        if (ps != null && es != null)
        {
            _netZ = (ps.transform.position.z + es.transform.position.z) * 0.5f;
        }

        // Paddle reference — read from the gameplay component (public field).
        _paddleTransform = null;
        var gp = FindObjectOfType<gameplay>();
        if (gp != null && gp.paddle != null)
            _paddleTransform = gp.paddle.transform;

        Debug.Log($"[VisualFeedbackManager] tableTopY={_tableTopY:F3} netZ={_netZ:F3} " +
                  $"paddle={((_paddleTransform != null) ? _paddleTransform.name : "NOT FOUND")}");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Component 1: Player-side table highlight
    // ─────────────────────────────────────────────────────────────────────────
    // A semi-transparent blue quad covering the player half of the table (2 mm
    // above the surface).  No collider — physics is completely unaffected.
    // Pulses brighter when the paddle is within PULSE_DIST of the table surface.
    

    // ─────────────────────────────────────────────────────────────────────────
    // Component 3: Opponent-side depth-reference lines
    // ─────────────────────────────────────────────────────────────────────────
    // Three thin flat mesh quads running the full depth of the opponent half,
    // placed at 1/4, 1/2, 3/4 of table width.  They give the player a
    // spatial/depth reference for tracking ball movement — NOT a target or
    // landing-zone cue.  No border, no cross, no zone markings.
    //
    // Why flat quads instead of LineRenderers:
    //   LineRenderer renders billboard quads that rotate to face the camera.
    //   In VR the camera moves every frame (head tracking), so the quad edge
    //   angle changes continuously → sub-pixel aliasing → shimmer.
    //   A flat static mesh on the table surface has no billboard axis and
    //   always presents the same geometry regardless of head position.
    //
    // Why parented to enemyside.transform:
    //   TableResizer moves the table vertically at runtime.  Parenting makes
    //   the quads ride with the table automatically; no per-frame update needed.
    //
    // Visual design: soft cyan-blue, ~17 % opacity, 4 mm wide, 3 mm above surface.
    // Built once at scene load — zero per-frame overhead.
    void BuildComponent3_ReferenceGrid()
    {
        var es = GameObject.Find("enemyside");
        if (es == null) return;

        var col = es.GetComponent<Collider>();
        if (col == null) return;

        // ── Local-space geometry (relative to enemyside transform) ────────────
        // InverseTransformPoint handles any table scale / rotation correctly.
        Bounds b    = col.bounds;
        Vector3 lMin = es.transform.InverseTransformPoint(b.min);
        Vector3 lMax = es.transform.InverseTransformPoint(b.max);

        // 3 mm above the collider top in local space.
        // InverseTransformPoint already folds lossyScale in, so add offset in
        // local units: 0.003 / lossyScale.y gives 3 mm in world space.
        float scaleY = Mathf.Max(0.001f, Mathf.Abs(es.transform.lossyScale.y));
        float lTopY  = lMax.y;                    // local Y of collider top
        float lY     = lTopY + 0.003f / scaleY;   // 3 mm above in local units

        float lW  = lMax.x - lMin.x;              // full width of opponent half

        // Half-width of each line strip in local units (4 mm world → local).
        float scaleX = Mathf.Max(0.001f, Mathf.Abs(es.transform.lossyScale.x));
        float lHalfW = (0.004f / scaleX) * 0.5f;

        // ── Parent object — child of enemyside so it moves with the table ─────
        _referenceGridObj = new GameObject("[VFB_3] DepthRefLines");
        _referenceGridObj.transform.SetParent(es.transform, worldPositionStays: false);

        var mat = MakeAlphaMat(C3_DEPTH);

        // Three longitudinal strips at 25 %, 50 %, 75 % of table width.
        float[] xFractions = { 0.25f, 0.50f, 0.75f };

        for (int i = 0; i < xFractions.Length; i++)
        {
            float lXc = lMin.x + xFractions[i] * lW;   // strip centre X (local)

            // Flat horizontal quad — four corners in local space.
            //   x: centre ± halfW   y: lY (fixed)   z: zMin → zMax
            Vector3 v0 = new Vector3(lXc - lHalfW, lY, lMin.z);
            Vector3 v1 = new Vector3(lXc + lHalfW, lY, lMin.z);
            Vector3 v2 = new Vector3(lXc + lHalfW, lY, lMax.z);
            Vector3 v3 = new Vector3(lXc - lHalfW, lY, lMax.z);

            var mesh = new Mesh { name = $"DepthLine_{i}" };
            mesh.vertices  = new[] { v0, v1, v2, v3 };
            // Both winding orders so the strip is visible from above and below.
            mesh.triangles = new[] { 0, 1, 2,  0, 2, 3,
                                     0, 2, 1,  0, 3, 2 };
            mesh.RecalculateNormals();

            var child = new GameObject($"DepthLine_{i}");
            child.transform.SetParent(_referenceGridObj.transform, false);

            var mf = child.AddComponent<MeshFilter>();
            mf.mesh = mesh;

            var mr = child.AddComponent<MeshRenderer>();
            mr.material          = mat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows    = false;
        }

        // ── Two transverse depth-band lines at 33 % and 66 % depth ───────────
        // Run the full width of the opponent half (X direction).
        // Slightly lower alpha than the longitudinal lines so they read as
        // secondary — depth segmentation (near / middle / far) without forming
        // visible zone boxes.
        float lDepth  = lMax.z - lMin.z;
        float scaleZ  = Mathf.Max(0.001f, Mathf.Abs(es.transform.lossyScale.z));
        float lHalfWz = (0.004f / scaleZ) * 0.5f;   // 4 mm band half-width in Z (local)

        var matT = MakeAlphaMat(C3_TRANSVERSE);
        float[] zFractions = { 1f / 3f, 2f / 3f };

        for (int i = 0; i < zFractions.Length; i++)
        {
            float lZt = lMin.z + zFractions[i] * lDepth;   // band centre Z (local)

            Vector3 t0 = new Vector3(lMin.x, lY, lZt - lHalfWz);
            Vector3 t1 = new Vector3(lMax.x, lY, lZt - lHalfWz);
            Vector3 t2 = new Vector3(lMax.x, lY, lZt + lHalfWz);
            Vector3 t3 = new Vector3(lMin.x, lY, lZt + lHalfWz);

            var mesh = new Mesh { name = $"DepthBand_{i}" };
            mesh.vertices  = new[] { t0, t1, t2, t3 };
            mesh.triangles = new[] { 0, 1, 2,  0, 2, 3,
                                     0, 2, 1,  0, 3, 2 };
            mesh.RecalculateNormals();

            var child = new GameObject($"DepthBand_{i}");
            child.transform.SetParent(_referenceGridObj.transform, false);

            var mf = child.AddComponent<MeshFilter>();
            mf.mesh = mesh;

            var mr = child.AddComponent<MeshRenderer>();
            mr.material          = matT;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows    = false;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Per-frame update
    // ─────────────────────────────────────────────────────────────────────────

    void LateUpdate()
    {
        if (!_componentsBuilt) return;
        UpdateComponent1_Pulse();
    }

    void UpdateComponent1_Pulse()
    {
        if (!EnableTableHighlight || _tableHighlightMr == null || _paddleTransform == null)
            return;

        // Pulse highlight brighter when paddle bottom is within PULSE_DIST of table.
        float paddleY    = _paddleTransform.position.y;
        float dist       = paddleY - _tableTopY;
        float t          = Mathf.InverseLerp(PULSE_DIST, 0f, dist);
        t                = Mathf.Clamp01(t);
        Color c          = Color.Lerp(C1_IDLE, C1_PULSE, t);
        _tableHighlightMr.material.color = c;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Material helpers
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates an alpha-blended material compatible with both Built-in and URP pipelines.
    /// Tries Sprites/Default → Unlit/Transparent → Unlit/Color in that order.
    /// </summary>
    static Material MakeAlphaMat(Color color)
    {
        Shader sh = Shader.Find("Sprites/Default")
                 ?? Shader.Find("Unlit/Transparent")
                 ?? Shader.Find("Unlit/Color");

        var mat = new Material(sh)
        {
            color        = color,
            renderQueue  = 3000,
        };
        return mat;
    }

    static void DestroyIfExists(ref GameObject go)
    {
        if (go != null) { Destroy(go); go = null; }
    }

    static bool IsGameScene(string name)
    {
        return name.Contains("game") || name.Contains("room");
    }
}
