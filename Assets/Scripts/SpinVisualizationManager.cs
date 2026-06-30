// SpinVisualizationManager.cs
// Research-grade spin visualization system for VR table tennis.
//
// Extends BallDepthPerceptionCue with three new subsystems:
//
//   Spin markers      — Pooled discrete fading discs spawned every 0.03 s.
//                       All markers use one consistent green colour to avoid
//                       introducing unintended colour-based interpretations.
//                       No Instantiate/Destroy during gameplay.
//
//   Predicted path    — LineRenderer projecting 1–2 s of future ball flight using
//                       simplified gravity + Magnus integration.  Updates every frame.
//
//   Landing marker    — Circle at predicted first bounce location on the table.
//                       Fades out after ballbounce.OnTableBounce fires.
//
// Visualization modes (Inspector enum):
//   Off | ShadowOnly | ShadowAndTableTint | MotionTrail |
//   SpinVisualization | FullTrainingAssist
//
// Safety:
//   Does NOT touch gameplay.cs, ballbounce.cs, serve logic, physics, or scoring.
//   Subscribes to ballbounce.OnTableBounce (read-only event) for landing feedback.
//
// Quest performance:
//   All GameObjects pre-created in Awake — zero Instantiate/Destroy at runtime.
//   Unlit vertex-colour shaders — no lighting calculations.
//   All per-frame buffers pre-allocated — zero heap allocation in Update/LateUpdate.
//
// Scene hookup:
//   Add to any persistent GameObject.
//   Assign ballRb, tableCollider, and depthCue in the Inspector.
//   Select mode and set toggles.  Press Play.

using UnityEngine;

[DisallowMultipleComponent]
public class SpinVisualizationManager : MonoBehaviour
{
    // ─────────────────────────────────────────────────────────────────────────
    // Visualization mode enum
    // ─────────────────────────────────────────────────────────────────────────

    public enum VisualizationMode
    {
        Off                 = 0,
        ShadowOnly          = 1,
        ShadowAndTableTint  = 2,
        MotionTrail         = 3,
        SpinVisualization   = 4,
        FullTrainingAssist  = 5,
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Inspector — References
    // ─────────────────────────────────────────────────────────────────────────

    [Header("References — Required")]
    [Tooltip("Rigidbody of the ball.")]
    public Rigidbody ballRb;

    [Tooltip("Collider whose bounds.max.y is the table surface height.")]
    public Collider tableCollider;

    [Tooltip("The BallDepthPerceptionCue on the same or sibling GameObject.")]
    public BallDepthPerceptionCue depthCue;

    // ─────────────────────────────────────────────────────────────────────────
    // Inspector — Visualization mode (changes individual toggles)
    // ─────────────────────────────────────────────────────────────────────────

    [Header("Visualization Mode")]
    [Tooltip("Selects a research-defined preset.  Overrides individual toggles when changed.")]
    public VisualizationMode mode = VisualizationMode.Off;   // OFF until SetCondition is called

    // ─────────────────────────────────────────────────────────────────────────
    // Inspector — Individual feature toggles
    // (Can be set independently or via ApplyMode / SetCondition)
    // ─────────────────────────────────────────────────────────────────────────

    [Header("Individual Feature Toggles")]
    public bool EnableShadowCue         = false;   // OFF until SetCondition is called
    public bool EnableTableTint         = false;
    public bool EnableMotionTrail       = false;
    public bool EnableSpinVisualization = false;   // OFF until SetCondition is called

    // ─────────────────────────────────────────────────────────────────────────
    // Inspector — Spin marker settings
    // ─────────────────────────────────────────────────────────────────────────

    [Header("Spin Markers")]
    [Tooltip("Diameter of each discrete marker disc (m).")]
    public float MarkerSize = 0.022f;

    [Tooltip("How long each marker stays visible (seconds) before fully fading.")]
    public float MarkerLifetime = 0.55f;

    [Tooltip("Maximum markers active at once.  Also equals pool size.")]
    public int MarkerPoolSize = 20;

    [Range(0f, 1f)]
    [Tooltip("Peak opacity of spin markers (multiplied by per-marker age fade).")]
    public float VisualizationOpacity = 0.85f;

    [Tooltip("Minimum ball speed (m/s) to spawn spin markers.")]
    public float MarkerMinSpeed = 0.5f;

    // ─────────────────────────────────────────────────────────────────────────
    // Inspector — Prediction path
    // ─────────────────────────────────────────────────────────────────────────

    [Header("Prediction Path")]
    [Tooltip("Number of points on the predicted flight path LineRenderer.")]
    public int PredictionPointCount = 28;

    [Tooltip("Duration of the prediction window (seconds ahead).")]
    public float PredictionDuration = 1.5f;

    [Tooltip("Line width of the prediction path (m).")]
    public float PredictionLineWidth = 0.005f;

    [Range(0f, 1f)]
    [Tooltip("Opacity of the prediction path line.")]
    public float PredictionOpacity = 0.45f;

    // ─────────────────────────────────────────────────────────────────────────
    // Inspector — Landing marker
    // ─────────────────────────────────────────────────────────────────────────

    [Header("Landing Marker")]
    [Tooltip("Diameter of the predicted bounce circle on the table (m).")]
    public float LandingMarkerSize = 0.065f;

    [Tooltip("Seconds the landing marker takes to fade after the actual bounce.")]
    public float LandingFadeDuration = 0.55f;

    // ─────────────────────────────────────────────────────────────────────────
    // Spin colours (constant)
    // ─────────────────────────────────────────────────────────────────────────

    private static readonly Color COLOR_TOPSPIN  = new Color(0f, 1f, 0f);   // pure green
    private static readonly Color COLOR_BACKSPIN = new Color(0f, 1f, 0f);   // pure green
    private static readonly Color COLOR_SIDESPIN = new Color(0f, 1f, 0f);   // pure green
    private static readonly Color COLOR_NEUTRAL  = new Color(0f, 1f, 0f);   // pure green

    // Spin classification thresholds (rad/s) — velocity-direction-relative
    private const float THRESH_TOPSPIN  = 4.0f;
    private const float THRESH_SIDESPIN = 3.0f;

    // Physics constants matching ballbounce.cs exactly
    private const float MAGNUS_COEFF = 5.5e-6f;   // [ballbounce.cs: MAGNUS_COEFF]
    private const float BALL_MASS    = 0.0027f;    // [gameplay.cs: _ballRb.mass]
    private const float ANG_DAMPING  = 0.30f;      // [gameplay.cs: angularDamping]

    // Minimum ball speed for prediction line display
    private const float PRED_MIN_SPEED = 0.3f;

    // ─────────────────────────────────────────────────────────────────────────
    // Marker pool
    // ─────────────────────────────────────────────────────────────────────────

    private struct MarkerEntry
    {
        public GameObject obj;
        public MeshRenderer mr;
        public Material mat;
        public float spawnTime;
        public Color baseColor;
        public bool  active;
    }

    private MarkerEntry[] _pool;
    private int           _poolNext    = 0;   // round-robin write head
    private float         _lastSpawnT  = 0f;
    private const float   SPAWN_INTERVAL = 0.03f;

    // ─────────────────────────────────────────────────────────────────────────
    // Prediction path
    // ─────────────────────────────────────────────────────────────────────────

    private LineRenderer _predLr;
    private Material     _predMat;
    private Vector3[]    _predPoints;          // pre-allocated, PredictionPointCount
    private bool         _predActive = false;

    // ─────────────────────────────────────────────────────────────────────────
    // Landing marker
    // ─────────────────────────────────────────────────────────────────────────

    private GameObject   _landingObj;
    private MeshRenderer _landingMr;
    private Material     _landingMat;
    private bool         _landingShowing = false;
    private float        _landingFadeStart;
    private bool         _landingFading  = false;

    // ─────────────────────────────────────────────────────────────────────────
    // Mode tracking (detect Inspector changes)
    // ─────────────────────────────────────────────────────────────────────────

    private VisualizationMode _lastAppliedMode;
    private bool  _initialized      = false;
    private float _trailDiagLastLog = -10f;   // throttle for [TRAIL-DIAG] runtime log

    // ─────────────────────────────────────────────────────────────────────────
    // Lifecycle
    // ─────────────────────────────────────────────────────────────────────────

    void Awake()
    {
        // Auto-lookup scene references when not assigned in the Inspector.
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
        if (depthCue == null)
            depthCue = FindObjectOfType<BallDepthPerceptionCue>();

        if (ballRb == null)
        {
            Debug.LogError("[SpinViz] ballRb not found in scene — disabling.");
            enabled = false; return;
        }
        if (tableCollider == null)
        {
            Debug.LogError("[SpinViz] tableCollider not found in scene — disabling.");
            enabled = false; return;
        }

        _predPoints = new Vector3[PredictionPointCount];

        BuildMarkerPool();
        BuildPredictionLine();
        BuildLandingMarker();

        _initialized = true;

        // Apply initial mode
        ApplyMode(mode);
        _lastAppliedMode = mode;
    }

    void OnEnable()
    {
        ballbounce.OnTableBounce += OnBallBounced;
    }

    void OnDisable()
    {
        ballbounce.OnTableBounce -= OnBallBounced;

        // Hide everything when disabled
        if (_initialized)
        {
            HideAllMarkers();
            SetPredActive(false);
            SetLandingVisible(false);
        }

        // Reset depthCue to all-off
        PushTogglesToDepthCue(false, false, false);
    }

    void OnDestroy()
    {
        // Materials were instanced — release them
        if (_pool != null)
            foreach (var e in _pool)
                if (e.mat != null) Destroy(e.mat);
        if (_predMat    != null) Destroy(_predMat);
        if (_landingMat != null) Destroy(_landingMat);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Per-frame update
    // ─────────────────────────────────────────────────────────────────────────

    void LateUpdate()
    {
        // Re-apply mode if changed via Inspector at runtime
        if (mode != _lastAppliedMode)
        {
            ApplyMode(mode);
            _lastAppliedMode = mode;
        }

        // Push shadow/tint/trail state to BallDepthPerceptionCue
        PushTogglesToDepthCue(EnableShadowCue, EnableTableTint, EnableMotionTrail);

        if (ballRb == null) return;

        bool    isKinematic = ballRb.isKinematic;
        Vector3 ballPos     = ballRb.position;
        float   tableTopY   = tableCollider.bounds.max.y;
        bool    validVizArea = IsBallInPlayableArea(ballPos);

        if (isKinematic || !validVizArea)
        {
            // Ball is held (serve phase) — clear all spin visuals
            HideAllMarkers();
            SetPredActive(false);
            SetLandingVisible(false);
            return;
        }

        Vector3 vel   = ballRb.linearVelocity;
        Vector3 omega = ballRb.angularVelocity;
        float   speed = vel.magnitude;

        // ── Runtime trail diagnostic (throttled) ──────────────────────────────
        if (Time.time > _trailDiagLastLog + 2f)
        {
            _trailDiagLastLog = Time.time;
            string reason;
            if (!EnableSpinVisualization)         reason = "SpinViz disabled (wrong condition group)";
            else if (speed < MarkerMinSpeed)      reason = $"speed below threshold ({speed:F1} < {MarkerMinSpeed:F1} m/s)";
            else                                  reason = "active";
            Debug.Log(
                $"[TRAIL-DIAG] spin={omega.magnitude:F1} rad/s  speed={speed:F1} m/s" +
                $"  trailVisible={_predActive.ToString().ToUpper()}" +
                $"  reason={reason}"
            );
        }

        // ── Spin markers ──────────────────────────────────────────────────────
        bool validMarkerViz = EnableSpinVisualization &&
                              speed >= MarkerMinSpeed &&
                              validVizArea;

        if (validMarkerViz)
        {
            TickMarkers(ballPos, vel, omega);
        }
        else
        {
            HideAllMarkers();
        }

        // Fade existing markers every frame regardless of enable toggle
        // (so markers spawned before a mid-flight toggle-off still fade correctly)
        UpdateMarkerFade();

        // ── Predicted flight path + landing marker ────────────────────────────
        bool validPredictionViz = EnableSpinVisualization &&
                                  speed >= PRED_MIN_SPEED &&
                                  validVizArea;

        if (validPredictionViz)
        {
            Color spinColor = ClassifySpin(vel, omega);
            int   used      = ComputePrediction(ballPos, vel, omega, tableTopY,
                                                 spinColor, out Vector3 landingPos);
            SetPredActive(true);
            _predLr.positionCount = used;
            _predLr.SetPositions(_predPoints);

            // Landing marker: show predicted position unless already fading
            if (!_landingFading && landingPos != Vector3.zero)
                ShowLandingMarker(landingPos, spinColor, tableTopY);
            else if (landingPos == Vector3.zero)
                SetLandingVisible(false);
        }
        else
        {
            SetPredActive(false);
            if (!_landingFading) SetLandingVisible(false);
        }

        // ── Landing marker fade tick ──────────────────────────────────────────
        TickLandingFade();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Playable-area guard
    // ─────────────────────────────────────────────────────────────────────────

    bool IsBallInPlayableArea(Vector3 pos)
    {
        if (tableCollider == null) return false;

        Bounds b = tableCollider.bounds;

        // Allow a small margin so the cue does not flicker when the ball is
        // barely outside the table edge, but clear it once it is clearly on the
        // floor or away from the playable table area.
        const float marginX = 0.30f;
        const float marginZ = 0.50f;
        const float floorClearanceBelowTable = 0.15f;

        bool insideXZ =
            pos.x >= b.min.x - marginX &&
            pos.x <= b.max.x + marginX &&
            pos.z >= b.min.z - marginZ &&
            pos.z <= b.max.z + marginZ;

        bool notOnFloor = pos.y > b.min.y - floorClearanceBelowTable;

        return insideXZ && notOnFloor;
    }

    bool IsPointOverTableXZ(Vector3 pos)
    {
        if (tableCollider == null) return false;

        Bounds b = tableCollider.bounds;
        return pos.x >= b.min.x && pos.x <= b.max.x &&
               pos.z >= b.min.z && pos.z <= b.max.z;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Event: ball bounced on table — start landing marker fade-out
    // ─────────────────────────────────────────────────────────────────────────

    void OnBallBounced(string tag, Vector3 pos)
    {
        if (!_landingShowing) return;
        _landingFading   = true;
        _landingFadeStart = Time.time;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Spin classification (velocity-direction relative)
    // ─────────────────────────────────────────────────────────────────────────

    // Returns the colour representing the dominant spin type.
    // Uses velocity direction as the reference axis so classification is
    // independent of table orientation.
    //
    //   velFlat = horizontal component of velocity (normalised)
    //   right   = 90° CW of velFlat in world XZ plane = Cross(velFlat, up)
    //   topspin = Dot(omega, right)   > 0  → ball top moving forward
    //   backspin= Dot(omega, right)   < 0  → ball top moving backward
    //   sidespin= Dot(omega, worldUp) != 0 → ball spinning around vertical axis
    Color ClassifySpin(Vector3 vel, Vector3 omega)
    {
        Vector3 velFlat = new Vector3(vel.x, 0f, vel.z);
        if (velFlat.sqrMagnitude < 0.001f) return COLOR_NEUTRAL;

        velFlat.Normalize();
        Vector3 right = Vector3.Cross(velFlat, Vector3.up);  // points rightward relative to travel dir

        float topspin  = Vector3.Dot(omega, right);   // + = topspin, − = backspin
        float sidespin = Vector3.Dot(omega, Vector3.up);

        float absTopspin  = Mathf.Abs(topspin);
        float absSidespin = Mathf.Abs(sidespin);

        // Dominant axis wins; sidespin beats topspin only if clearly larger
        if (absTopspin >= THRESH_TOPSPIN && absTopspin >= absSidespin)
            return topspin > 0f ? COLOR_TOPSPIN : COLOR_BACKSPIN;

        if (absSidespin >= THRESH_SIDESPIN)
            return COLOR_SIDESPIN;

        return COLOR_NEUTRAL;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Spin markers — pool management
    // ─────────────────────────────────────────────────────────────────────────

    void TickMarkers(Vector3 ballPos, Vector3 vel, Vector3 omega)
    {
        if (Time.time - _lastSpawnT < SPAWN_INTERVAL) return;
        _lastSpawnT = Time.time;

        Color col = ClassifySpin(vel, omega);

        // Wrap round-robin pool
        ref MarkerEntry e = ref _pool[_poolNext];
        _poolNext = (_poolNext + 1) % MarkerPoolSize;

        // Reuse or activate this slot
        e.spawnTime = Time.time;
        e.baseColor = col;
        e.active    = true;

        if (e.obj == null) return;
        e.obj.transform.position = ballPos;
        e.obj.SetActive(true);

        // Immediately apply full opacity color
        e.mat.color = new Color(col.r, col.g, col.b, VisualizationOpacity);
    }

    void UpdateMarkerFade()
    {
        float now = Time.time;
        for (int i = 0; i < MarkerPoolSize; i++)
        {
            ref MarkerEntry e = ref _pool[i];
            if (!e.active) continue;

            float age = now - e.spawnTime;
            if (age >= MarkerLifetime)
            {
                e.active = false;
                if (e.obj != null) e.obj.SetActive(false);
                continue;
            }

            // Linear fade: full at age 0, zero at age = MarkerLifetime
            float alpha = VisualizationOpacity * (1f - age / MarkerLifetime);
            e.mat.color = new Color(e.baseColor.r, e.baseColor.g, e.baseColor.b, alpha);
        }
    }

    void HideAllMarkers()
    {
        for (int i = 0; i < MarkerPoolSize; i++)
        {
            ref MarkerEntry e = ref _pool[i];
            if (!e.active) continue;
            e.active = false;
            if (e.obj != null) e.obj.SetActive(false);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Prediction — Euler integration matching ballbounce.cs physics
    // ─────────────────────────────────────────────────────────────────────────

    // Returns number of valid prediction points written to _predPoints.
    // Writes the predicted first-bounce world position into landingPos
    // (or Vector3.zero if no bounce within the window).
    int ComputePrediction(Vector3 startPos, Vector3 startVel, Vector3 startOmega,
                          float tableTopY, Color spinColor, out Vector3 landingPos)
    {
        float   predDt  = PredictionDuration / PredictionPointCount;
        Vector3 pos     = startPos;
        Vector3 vel     = startVel;
        Vector3 omega   = startOmega;
        float   angDecay = Mathf.Max(0f, 1f - ANG_DAMPING * predDt);
        landingPos      = Vector3.zero;
        int     written = 0;

        for (int i = 0; i < PredictionPointCount; i++)
        {
            _predPoints[written++] = pos;

            // Magnus acceleration: a = F/m = (MAGNUS_COEFF/m) * (omega × vel)
            // Matches ballbounce.cs: AddForce(MAGNUS_COEFF * Cross(angVel, linVel))
            Vector3 magnusAcc = (MAGNUS_COEFF / BALL_MASS) * Vector3.Cross(omega, vel);

            vel.x += magnusAcc.x * predDt;
            vel.y += (Physics.gravity.y + magnusAcc.y) * predDt;
            vel.z += magnusAcc.z * predDt;

            // Angular decay matching Unity's angularDamping per step
            omega *= angDecay;

            pos.x += vel.x * predDt;
            pos.y += vel.y * predDt;
            pos.z += vel.z * predDt;

            // First table crossing.  Only show a landing marker if the predicted
            // bounce is on the tabletop, not on the floor or outside the table.
            if (landingPos == Vector3.zero &&
                pos.y <= tableTopY &&
                vel.y < 0f &&
                IsPointOverTableXZ(new Vector3(pos.x, tableTopY, pos.z)))
            {
                landingPos = new Vector3(pos.x, tableTopY + 0.001f, pos.z);

                // Fill remaining prediction points at landing height so the
                // line doesn't tunnel underground or jump to (0,0,0)
                Vector3 fill = landingPos;
                for (int j = written; j < PredictionPointCount; j++)
                    _predPoints[j] = fill;
                written = PredictionPointCount;
                break;
            }
        }

        return written;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Landing marker helpers
    // ─────────────────────────────────────────────────────────────────────────

    void ShowLandingMarker(Vector3 worldPos, Color spinColor, float tableTopY)
    {
        _landingFading = false;
        _landingShowing = true;

        _landingObj.transform.position = new Vector3(worldPos.x, tableTopY + 0.002f, worldPos.z);
        float s = LandingMarkerSize;
        _landingObj.transform.localScale = new Vector3(s, s, s);

        _landingMat.color = new Color(spinColor.r, spinColor.g, spinColor.b,
                                      VisualizationOpacity * 0.6f);
        SetLandingVisible(true);
    }

    void TickLandingFade()
    {
        if (!_landingFading) return;

        float age = Time.time - _landingFadeStart;
        if (age >= LandingFadeDuration)
        {
            _landingFading  = false;
            _landingShowing = false;
            SetLandingVisible(false);
            return;
        }

        float alpha = VisualizationOpacity * 0.6f * (1f - age / LandingFadeDuration);
        Color c     = _landingMat.color;
        _landingMat.color = new Color(c.r, c.g, c.b, alpha);
    }

    void SetLandingVisible(bool on)
    {
        if (_landingObj != null) _landingObj.SetActive(on);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Prediction line enable guard
    // ─────────────────────────────────────────────────────────────────────────

    void SetPredActive(bool on)
    {
        if (_predActive == on) return;
        if (_predLr != null) _predLr.enabled = on;
        _predActive = on;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Propagate shadow/tint/trail toggles to BallDepthPerceptionCue
    // ─────────────────────────────────────────────────────────────────────────

    void PushTogglesToDepthCue(bool shadow, bool tint, bool trail)
    {
        if (depthCue == null) return;
        depthCue.EnableShadowCue   = shadow;
        depthCue.EnableTableTint   = tint;
        depthCue.EnableMotionTrail = trail;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Mode → toggles mapping
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Apply a visualization preset.  Sets individual toggles and pushes them
    /// to BallDepthPerceptionCue.  Call this or set toggles directly.
    /// </summary>
    public void ApplyMode(VisualizationMode m)
    {
        switch (m)
        {
            case VisualizationMode.Off:
                EnableShadowCue = false; EnableTableTint = false;
                EnableMotionTrail = false; EnableSpinVisualization = false;
                break;

            case VisualizationMode.ShadowOnly:
                EnableShadowCue = true;  EnableTableTint = false;
                EnableMotionTrail = false; EnableSpinVisualization = false;
                break;

            case VisualizationMode.ShadowAndTableTint:
                EnableShadowCue = true;  EnableTableTint = true;
                EnableMotionTrail = false; EnableSpinVisualization = false;
                break;

            case VisualizationMode.MotionTrail:
                EnableShadowCue = true;  EnableTableTint = false;
                EnableMotionTrail = true; EnableSpinVisualization = false;
                break;

            case VisualizationMode.SpinVisualization:
                EnableShadowCue = true;  EnableTableTint = false;
                EnableMotionTrail = false; EnableSpinVisualization = true;
                break;

            case VisualizationMode.FullTrainingAssist:
                EnableShadowCue = true;  EnableTableTint = true;
                EnableMotionTrail = false; EnableSpinVisualization = true;
                break;
        }

        PushTogglesToDepthCue(EnableShadowCue, EnableTableTint, EnableMotionTrail);
        _lastAppliedMode = m;
    }

    /// <summary>
    /// Map study group to mode (matches BallDepthPerceptionCue.SetCondition convention):
    ///   1 = Standard VR  → Off (no augmentation)
    ///   2 = Visual       → FullTrainingAssist (shadow + tint + green spin markers)
    ///   3 = Multimodal   → FullTrainingAssist (identical visual set to group 2;
    ///                        haptics are added on top by WoojerHapticManager only)
    /// </summary>
    public void SetCondition(int group)
    {
        switch (group)
        {
            case 1: ApplyMode(VisualizationMode.Off);                break;
            case 2: ApplyMode(VisualizationMode.FullTrainingAssist); break;
            case 3: ApplyMode(VisualizationMode.FullTrainingAssist); break;
        }
        mode = _lastAppliedMode;
        // Always log — used for condition-assignment verification
        Debug.Log($"[SpinViz] Mode={mode} SpinVisualization={EnableSpinVisualization}");

        // ── Startup trail diagnostic ──────────────────────────────────────────
        bool visualEnabled   = group >= 2;
        bool multimodal      = group == 3;
        bool trailObjActive  = EnableSpinVisualization;
        bool rendererEnabled = _predLr != null && _predLr.enabled;
        Debug.Log(
            $"[TRAIL-DIAG] group={group}" +
            $" visualEnabled={visualEnabled.ToString().ToUpper()}" +
            $" multimodal={multimodal.ToString().ToUpper()}" +
            $" trailObjActive={trailObjActive.ToString().ToUpper()}" +
            $" rendererEnabled={rendererEnabled.ToString().ToUpper()}" +
            $" materialAlpha={VisualizationOpacity:F2}" +
            $" spinThreshold={MarkerMinSpeed:F1}"
        );
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Construction — all scene objects are created once in Awake
    // ─────────────────────────────────────────────────────────────────────────

    void BuildMarkerPool()
    {
        Shader sh = FindUnlitShader();
        Mesh   diskMesh = BallDepthPerceptionCue.CreateCircleMesh(12);

        _pool = new MarkerEntry[MarkerPoolSize];
        for (int i = 0; i < MarkerPoolSize; i++)
        {
            var go = new GameObject($"[SpinViz] Marker_{i:D2}");

            var mf = go.AddComponent<MeshFilter>();
            mf.mesh = diskMesh;   // shared read-only mesh — no per-instance copy

            var mr = go.AddComponent<MeshRenderer>();
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows    = false;

            // Per-instance material — each marker has an independent color
            var mat = new Material(sh)
            {
                color       = Color.clear,
                renderQueue = 3001,
            };
            mr.material = mat;

            float s = MarkerSize;
            go.transform.localScale = new Vector3(s, s, s);
            go.SetActive(false);

            _pool[i] = new MarkerEntry
            {
                obj       = go,
                mr        = mr,
                mat       = mat,
                spawnTime = 0f,
                baseColor = COLOR_NEUTRAL,
                active    = false,
            };
        }
    }

    void BuildPredictionLine()
    {
        var go = new GameObject("[SpinViz] PredictionPath");

        _predLr = go.AddComponent<LineRenderer>();
        _predLr.positionCount        = PredictionPointCount;
        _predLr.useWorldSpace        = true;
        _predLr.generateLightingData = false;
        _predLr.shadowCastingMode    = UnityEngine.Rendering.ShadowCastingMode.Off;
        _predLr.receiveShadows       = false;
        _predLr.widthMultiplier      = PredictionLineWidth;

        // Constant width — no width curve needed
        _predLr.widthCurve = AnimationCurve.Constant(0f, 1f, 1f);

        // Gradient: visible near ball, transparent at far end — pure green throughout
        var grad = new Gradient();
        grad.SetKeys(
            new GradientColorKey[]
            {
                new GradientColorKey(Color.green, 0f),
                new GradientColorKey(Color.green, 1f),
            },
            new GradientAlphaKey[]
            {
                new GradientAlphaKey(PredictionOpacity, 0f),
                new GradientAlphaKey(0f,                1f),
            });
        _predLr.colorGradient = grad;

        Shader sh = FindUnlitShader();
        _predMat         = new Material(sh) { renderQueue = 3000 };
        _predMat.color   = Color.green;
        _predLr.material = _predMat;

        // Pre-fill to avoid LineRenderer seeing uninitialised data
        for (int i = 0; i < PredictionPointCount; i++) _predPoints[i] = Vector3.zero;
        _predLr.SetPositions(_predPoints);
        _predLr.enabled = false;
        _predActive     = false;
    }

    void BuildLandingMarker()
    {
        _landingObj = new GameObject("[SpinViz] LandingMarker");

        var mf = _landingObj.AddComponent<MeshFilter>();
        mf.mesh = BallDepthPerceptionCue.CreateCircleMesh(20);  // smoother ring

        var mr = _landingObj.AddComponent<MeshRenderer>();
        mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        mr.receiveShadows    = false;
        _landingMr           = mr;

        Shader sh   = FindUnlitShader();
        _landingMat = new Material(sh) { renderQueue = 3002, color = Color.clear };
        mr.material = _landingMat;

        float s = LandingMarkerSize;
        _landingObj.transform.localScale = new Vector3(s, s, s);
        _landingObj.SetActive(false);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Shader lookup helper (same fallback chain as BallDepthPerceptionCue)
    // ─────────────────────────────────────────────────────────────────────────

    static Shader FindUnlitShader()
    {
        return Shader.Find("Sprites/Default")
            ?? Shader.Find("Unlit/Transparent")
            ?? Shader.Find("Unlit/Color");
    }
}
