// BallDepthPerceptionCue.cs
// Perceptual depth-perception augmentation for VR serve-training study.
//
// THREE non-predictive perceptual cues (each independently toggled):
//
//   EnableShadowCue  — Dynamic ball shadow projected onto the table surface.
//                      Scale and opacity vary with ball height.
//
//   EnableTableTint  — Subtle warm highlight on the table surface that grows
//                      as the ball descends, helping players perceive height.
//
//   EnableMotionTrail — Short fading trail built from PREVIOUS ball positions.
//                       No extrapolation.  Disabled by default (toggled by
//                       SpinVisualizationManager or the Inspector).
//
// Research groups:
//   Group 1 — Standard VR   : all cues OFF
//   Group 2 — Visual        : shadow + optional tint
//   Group 3 — Multimodal    : shadow + tint + spin visualization
//
// Performance:
//   Zero per-frame heap allocation.  All arrays pre-allocated in Awake.
//
// Scene hookup:
//   Attach to any persistent scene GameObject.
//   Assign ballRb, tableCollider (playerside or enemyside collider), and
//   optionally tableSurfaceRenderer in the Inspector.
//
// DO NOT modify gameplay.cs, ballbounce.cs, or any physics/serve logic.

using UnityEngine;

[DisallowMultipleComponent]
public class BallDepthPerceptionCue : MonoBehaviour
{
    // ── Trail color (constant) ────────────────────────────────────────────────
    private static readonly Color TRAIL_COLOR = new Color(0f, 1f, 0f);   // pure green

    // ─────────────────────────────────────────────────────────────────────────
    // Inspector — References
    // ─────────────────────────────────────────────────────────────────────────

    [Header("References")]
    [Tooltip("Rigidbody of the ball. Must be assigned in Inspector.")]
    public Rigidbody ballRb;

    [Tooltip("Main table surface Renderer used for the table-tint cue.")]
    public Renderer tableSurfaceRenderer;

    [Tooltip("Collider whose bounds.max.y defines the table surface height.")]
    public Collider tableCollider;

    // ─────────────────────────────────────────────────────────────────────────
    // Inspector — Per-Feature Toggles
    // ─────────────────────────────────────────────────────────────────────────

    [Header("Per-Feature Toggles")]
    [Tooltip("Dynamic shadow beneath the ball.")]
    public bool EnableShadowCue   = false;   // OFF until SetCondition is called

    [Tooltip("Subtle warm highlight on the table as the ball descends.")]
    public bool EnableTableTint   = false;

    [Tooltip("Short fading motion trail (previous positions only). " +
             "Off by default; enable via SpinVisualizationManager or here.")]
    public bool EnableMotionTrail = false;

    // ─────────────────────────────────────────────────────────────────────────
    // Inspector — Shadow
    // ─────────────────────────────────────────────────────────────────────────

    [Header("Shadow")]
    [Tooltip("Shadow diameter (m) when ball is at table surface.")]
    public float shadowMinScale  = 0.035f;

    [Tooltip("Shadow diameter (m) when ball is at maximum tracked height.")]
    public float shadowMaxScale  = 0.12f;

    [Tooltip("Shadow alpha when ball is at table surface (darkest).")]
    public float shadowMaxAlpha  = 0.30f;

    [Tooltip("Shadow alpha when ball is at maximum tracked height (faintest).")]
    public float shadowMinAlpha  = 0.08f;

    [Tooltip("Vertical offset above table surface (metres) to prevent z-fighting.")]
    public float shadowYOffset   = 0.003f;

    [Tooltip("Ball height above table at which shadow reaches minimum opacity/size.")]
    public float shadowMaxHeight = 1.0f;

    // ─────────────────────────────────────────────────────────────────────────
    // Inspector — Motion Trail
    // ─────────────────────────────────────────────────────────────────────────

    [Header("Motion Trail")]
    [Tooltip("Number of historical positions kept in the trail buffer.")]
    public int   trailPointCount    = 8;

    [Tooltip("Seconds of history to render.")]
    public float trailLengthSeconds = 0.10f;

    [Tooltip("Trail line width at the newest (ball) end (m).")]
    public float trailWidthStart    = 0.012f;

    [Tooltip("Trail line width at the oldest (far) end (m).")]
    public float trailWidthEnd      = 0.002f;

    [Range(0f, 1f)]
    [Tooltip("Trail alpha at the newest (ball) end.")]
    public float trailAlphaStart    = 0.22f;

    [Range(0f, 1f)]
    [Tooltip("Trail alpha at the oldest (far) end.")]
    public float trailAlphaEnd      = 0.00f;

    [Tooltip("Minimum ball speed (m/s) required to show the trail.")]
    public float trailMinSpeed      = 0.5f;

    [Header("Trail Distance Alpha")]
    [Tooltip("Ball–HMD distance (m) at which trail is fully opaque.")]
    public float trailNearDistance = 0.5f;

    [Tooltip("Ball–HMD distance (m) at which trail fades to minimum.")]
    public float trailFarDistance  = 3.0f;

    // ─────────────────────────────────────────────────────────────────────────
    // Inspector — Table Tint
    // ─────────────────────────────────────────────────────────────────────────

    [Header("Table Tint")]
    [Tooltip("Ball height above table at which tint starts (m).")]
    public float tintStartHeight = 0.60f;

    [Range(0f, 0.15f)]
    [Tooltip("Maximum additive red/green channel shift applied at tint peak.")]
    public float tintMaxStrength = 0.045f;

    // ─────────────────────────────────────────────────────────────────────────
    // Inspector — Backward Compat / Debug
    // ─────────────────────────────────────────────────────────────────────────

    [Header("Backward Compat")]
    [Tooltip("Legacy master toggle. Replaced by individual toggles above. " +
             "Setting this false turns all three cues off; true restores them.")]
    [HideInInspector] public bool enableCues = true;

    [Header("Debug")]
    public bool enableDebugLogs = false;

    // ─────────────────────────────────────────────────────────────────────────
    // Shadow runtime
    // ─────────────────────────────────────────────────────────────────────────
    private GameObject   _shadowObj;
    private MeshRenderer _shadowMr;
    private Material     _shadowMat;
    private bool         _shadowActive = false;

    // ─────────────────────────────────────────────────────────────────────────
    // Trail runtime
    // ─────────────────────────────────────────────────────────────────────────
    private LineRenderer _trailLr;
    private Material     _trailMat;
    private Vector3[]    _trailBuf;
    private float[]      _trailTimes;
    private int          _trailHead = 0;
    private int          _trailFill = 0;
    private Vector3[]    _lrPoints;
    private bool         _trailActive = false;

    // ─────────────────────────────────────────────────────────────────────────
    // Table-tint runtime
    // ─────────────────────────────────────────────────────────────────────────
    private Material     _tableMat;
    private Color        _tableBaseColor;
    private bool         _tableTintBuilt = false;

    // ─────────────────────────────────────────────────────────────────────────
    // Camera (cached once)
    // ─────────────────────────────────────────────────────────────────────────
    private Camera _mainCamera;

    // ─────────────────────────────────────────────────────────────────────────
    // Lifecycle
    // ─────────────────────────────────────────────────────────────────────────

    void Awake()
    {
        // Auto-lookup scene references when not assigned in the Inspector.
        // This allows the component to be created programmatically at runtime.
        if (ballRb == null)
        {
            var bb = FindObjectOfType<ballbounce>();
            if (bb != null) ballRb = bb.GetComponent<Rigidbody>();
        }
        if (tableCollider == null)
        {
            var ps = GameObject.Find("playerside");
            if (ps != null) tableCollider = ps.GetComponent<Collider>();
        }
        if (tableSurfaceRenderer == null && tableCollider != null)
        {
            // Walk up to find the table mesh renderer (table surface is a parent of collider GO).
            tableSurfaceRenderer = tableCollider.GetComponentInParent<Renderer>();
        }

        bool valid = true;
        if (ballRb        == null) { Debug.LogError("[BallDepthCue] ballRb not found in scene.");        valid = false; }
        if (tableCollider == null) { Debug.LogError("[BallDepthCue] tableCollider not found in scene."); valid = false; }
        if (!valid) { enabled = false; return; }

        _trailBuf   = new Vector3[trailPointCount];
        _trailTimes = new float[trailPointCount];
        _lrPoints   = new Vector3[trailPointCount];

        BuildShadow();
        BuildTrail();
        BuildTableTint();

        if (enableDebugLogs)
            Debug.Log("[BallDepthCue] Initialized — shadow/trail/tint subsystems ready.");
    }

    void Start()
    {
        _mainCamera = Camera.main;
        if (_mainCamera == null && enableDebugLogs)
            Debug.LogWarning("[BallDepthCue] Camera.main not found — trail distance factor defaults to 1.0");
    }

    void OnDestroy()
    {
        if (_shadowMat != null) Destroy(_shadowMat);
        if (_trailMat  != null) Destroy(_trailMat);
        // _tableMat is an instanced material — release it
        if (_tableMat  != null && _tableTintBuilt) Destroy(_tableMat);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Public API
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Map study group to cue toggles:
    ///   1 = Standard VR   → all cues OFF
    ///   2 = Visual        → shadow + table tint ON, trail OFF
    ///   3 = Multimodal    → shadow + table tint ON (trail optional, set by SpinManager)
    /// </summary>
    public void SetCondition(int group)
    {
        switch (group)
        {
            case 1:
                EnableShadowCue = false; EnableTableTint = false; EnableMotionTrail = false;
                break;
            case 2:
                EnableShadowCue = true;  EnableTableTint = true;  EnableMotionTrail = false;
                break;
            case 3:
                EnableShadowCue = true;  EnableTableTint = true;  EnableMotionTrail = false;
                break;
        }
        // Always log — used for condition-assignment verification
        Debug.Log($"[BallDepthCue] Shadow={EnableShadowCue} Tint={EnableTableTint} Trail={EnableMotionTrail}");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Per-frame
    // ─────────────────────────────────────────────────────────────────────────

    void LateUpdate()
    {
        if (ballRb == null) return;

        bool    isKinematic = ballRb.isKinematic;
        Vector3 ballPos     = ballRb.position;
        float   tableTopY   = tableCollider.bounds.max.y;

        // Shadow
        if (EnableShadowCue)
            UpdateShadow(ballPos, isKinematic, tableTopY);
        else
            SetShadowActive(false);

        // Motion trail
        if (EnableMotionTrail)
        {
            float speed = isKinematic ? 0f : ballRb.linearVelocity.magnitude;
            UpdateTrail(ballPos, isKinematic, speed);
        }
        else
        {
            ClearTrailHistory();
            SetTrailActive(false);
        }

        // Table tint
        if (EnableTableTint)
            UpdateTableTint(ballPos, isKinematic, tableTopY);
        else
            ResetTableTint();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Part 1 — Shadow
    // ─────────────────────────────────────────────────────────────────────────

    void UpdateShadow(Vector3 ballPos, bool isKinematic, float tableTopY)
    {
        float height = ballPos.y - tableTopY;

        if (isKinematic || height < 0f)
        {
            SetShadowActive(false);
            return;
        }

        float t     = Mathf.Clamp01(height / shadowMaxHeight);
        float scale = Mathf.Lerp(shadowMinScale, shadowMaxScale, t);
        float alpha = Mathf.Lerp(shadowMaxAlpha, shadowMinAlpha, t);

        _shadowObj.transform.position   = new Vector3(ballPos.x, tableTopY + shadowYOffset, ballPos.z);
        _shadowObj.transform.localScale = new Vector3(scale, scale, scale);
        _shadowMat.color                = new Color(0f, 0f, 0f, alpha);

        SetShadowActive(true);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Part 2 — Motion trail
    // ─────────────────────────────────────────────────────────────────────────

    void UpdateTrail(Vector3 ballPos, bool isKinematic, float speed)
    {
        if (isKinematic || speed < trailMinSpeed)
        {
            ClearTrailHistory();
            SetTrailActive(false);
            return;
        }

        _trailBuf[_trailHead]   = ballPos;
        _trailTimes[_trailHead] = Time.time;
        _trailHead              = (_trailHead + 1) % trailPointCount;
        if (_trailFill < trailPointCount) _trailFill++;

        float cutoff  = Time.time - trailLengthSeconds;
        int   visible = 0;
        for (int i = 0; i < _trailFill; i++)
        {
            int idx = (_trailHead - 1 - i + trailPointCount) % trailPointCount;
            if (_trailTimes[idx] < cutoff) break;
            _lrPoints[visible++] = _trailBuf[idx];
        }

        if (visible < 2) { SetTrailActive(false); return; }
        for (int i = visible; i < trailPointCount; i++) _lrPoints[i] = _lrPoints[visible - 1];

        if (_trailMat != null)
        {
            float dist       = _mainCamera != null
                             ? Vector3.Distance(ballPos, _mainCamera.transform.position)
                             : trailNearDistance;
            float t          = Mathf.InverseLerp(trailNearDistance, trailFarDistance, dist);
            _trailMat.color  = new Color(0f, 1f, 0f, Mathf.Lerp(1.0f, 0.6f, t));  // pure green
        }

        SetTrailActive(true);
        _trailLr.SetPositions(_lrPoints);
    }

    void ClearTrailHistory()
    {
        _trailHead = 0;
        _trailFill = 0;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Part 3 — Table tint
    // ─────────────────────────────────────────────────────────────────────────

    void UpdateTableTint(Vector3 ballPos, bool isKinematic, float tableTopY)
    {
        if (!_tableTintBuilt || _tableMat == null) return;

        if (isKinematic)
        {
            _tableMat.color = _tableBaseColor;
            return;
        }

        float height = ballPos.y - tableTopY;
        if (height < 0f || height > tintStartHeight)
        {
            _tableMat.color = _tableBaseColor;
            return;
        }

        // t = 1 when ball is at table surface; 0 when at tintStartHeight
        float t    = 1f - Mathf.Clamp01(height / tintStartHeight);
        float str  = t * tintMaxStrength;
        Color bc = _tableBaseColor;
        _tableMat.color = new Color(
            Mathf.Min(1f, bc.r + str),
            Mathf.Min(1f, bc.g + str * 0.7f),
            Mathf.Min(1f, bc.b),
            bc.a);
    }

    void ResetTableTint()
    {
        if (_tableTintBuilt && _tableMat != null)
            _tableMat.color = _tableBaseColor;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Enable/disable guards (avoid redundant property writes)
    // ─────────────────────────────────────────────────────────────────────────

    void SetShadowActive(bool on)
    {
        if (_shadowActive == on) return;
        _shadowMr.enabled = on;
        _shadowActive     = on;
    }

    void SetTrailActive(bool on)
    {
        if (_trailActive == on) return;
        _trailLr.enabled = on;
        _trailActive     = on;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Construction
    // ─────────────────────────────────────────────────────────────────────────

    void BuildShadow()
    {
        _shadowObj = new GameObject("[DepthCue] BallShadow");

        var mf = _shadowObj.AddComponent<MeshFilter>();
        mf.mesh = CreateCircleMesh(16);

        _shadowMr = _shadowObj.AddComponent<MeshRenderer>();
        _shadowMr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        _shadowMr.receiveShadows    = false;

        Shader sh = Shader.Find("Sprites/Default")
                 ?? Shader.Find("Unlit/Transparent")
                 ?? Shader.Find("Unlit/Color");
        _shadowMat = new Material(sh)
        {
            color       = new Color(0f, 0f, 0f, shadowMaxAlpha),
            renderQueue = 3000,
        };
        _shadowMr.material = _shadowMat;
        _shadowMr.enabled  = false;
        _shadowActive      = false;
    }

    void BuildTrail()
    {
        var trailGo = new GameObject("[DepthCue] BallTrail");

        _trailLr = trailGo.AddComponent<LineRenderer>();
        _trailLr.positionCount        = trailPointCount;
        _trailLr.useWorldSpace        = true;
        _trailLr.generateLightingData = false;
        _trailLr.shadowCastingMode    = UnityEngine.Rendering.ShadowCastingMode.Off;
        _trailLr.receiveShadows       = false;
        _trailLr.widthMultiplier      = trailWidthStart;
        _trailLr.widthCurve           = new AnimationCurve(
            new Keyframe(0f, 1f),
            new Keyframe(1f, trailWidthEnd / Mathf.Max(0.0001f, trailWidthStart)));

        var grad = new Gradient();
        grad.SetKeys(
            new GradientColorKey[] { new GradientColorKey(TRAIL_COLOR, 0f),
                                     new GradientColorKey(TRAIL_COLOR, 1f) },
            new GradientAlphaKey[] { new GradientAlphaKey(trailAlphaStart, 0f),
                                     new GradientAlphaKey(trailAlphaEnd,   1f) });
        _trailLr.colorGradient = grad;

        Shader sh = Shader.Find("Sprites/Default")
                 ?? Shader.Find("Unlit/Transparent")
                 ?? Shader.Find("Unlit/Color");
        _trailMat         = new Material(sh) { renderQueue = 3000 };
        _trailLr.material = _trailMat;

        for (int i = 0; i < trailPointCount; i++) _lrPoints[i] = Vector3.zero;
        _trailLr.SetPositions(_lrPoints);
        _trailLr.enabled = false;
        _trailActive     = false;
    }

    void BuildTableTint()
    {
        if (tableSurfaceRenderer == null) return;
        // .material auto-creates an instanced copy — safe to modify per-frame.
        _tableMat        = tableSurfaceRenderer.material;
        _tableBaseColor  = _tableMat.color;
        _tableTintBuilt  = true;
        if (enableDebugLogs)
            Debug.Log($"[BallDepthCue] Table tint ready — base color={_tableBaseColor}");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Shared mesh helper
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Flat circle mesh in the XZ plane, diameter 1 (scale the GO to resize).
    /// Vertex colours create a radial alpha gradient (soft edges without a texture).
    /// Both winding orders so the disc is visible from above and below.
    /// </summary>
    internal static Mesh CreateCircleMesh(int segments)
    {
        var verts  = new Vector3[segments + 1];
        var colors = new Color32[segments + 1];
        var tris   = new int[segments * 6];

        verts[0]  = Vector3.zero;
        colors[0] = new Color32(255, 255, 255, 255);

        for (int i = 0; i < segments; i++)
        {
            float a       = 2f * Mathf.PI * i / segments;
            verts[i + 1]  = new Vector3(Mathf.Cos(a) * 0.5f, 0f, Mathf.Sin(a) * 0.5f);
            colors[i + 1] = new Color32(255, 255, 255, 0);
        }

        for (int i = 0; i < segments; i++)
        {
            int next        = (i + 1) % segments + 1;
            tris[i * 6 + 0] = 0; tris[i * 6 + 1] = i + 1; tris[i * 6 + 2] = next;
            tris[i * 6 + 3] = 0; tris[i * 6 + 4] = next;  tris[i * 6 + 5] = i + 1;
        }

        var mesh = new Mesh { name = "DepthCueCircle" };
        mesh.vertices  = verts;
        mesh.colors32  = colors;
        mesh.triangles = tris;
        mesh.RecalculateNormals();
        return mesh;
    }
}
