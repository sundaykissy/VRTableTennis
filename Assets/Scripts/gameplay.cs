using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;
using UnityEngine.InputSystem;
using XRInputDevice  = UnityEngine.XR.InputDevice;
using XRCommonUsages = UnityEngine.XR.CommonUsages;
#pragma warning disable CS0618

public class gameplay : MonoBehaviour
{
    // ── Shared statics ────────────────────────────────────────────────────────
    public static int   playerPaddleCollision = 0;
    public static int   enemyPaddleCollision  = 0;
    // serveReady — cleared at toss, set true after 0.4 s.
    // ballbounce checks this before acting on playerPaddleCollision during
    // playerStart, so a stale flag or phantom hit from the toss arm-motion
    // cannot trigger the playerStart → playerPaddle transition prematurely.
    public static bool  serveReady = false;
    public static float sharedGrip    = 0f;
    public static float sharedTrigger = 0f;
    public static bool  sharedThumb   = false;
    public static Vector3    sharedLeftPos   = Vector3.zero;
    public static Quaternion sharedLeftRot   = Quaternion.identity;
    public static bool       sharedLeftValid = false;

    // ── Haptic event — paddle-ball hit ────────────────────────────────────────
    // Fires once per validated paddle hit, after all hit guards pass.
    // Args: effectiveSpd (m/s), spinMagnitude (rad/s).
    // Consumed by WoojerHapticManager and ControllerHapticManager (Group 3 only).
    // Safe to leave unsubscribed.
    public static event System.Action<float, float> OnValidPaddleHit;

    // ── Haptic event — paddle-table contact ───────────────────────────────────
    // Fires once on the entering edge when the paddle bottom crosses below _tableTopY.
    // Arg: penetration depth (metres, positive).
    // Consumed by WoojerHapticManager and ControllerHapticManager (Group 3 only).
    // Safe to leave unsubscribed.
    public static event System.Action<float> OnPaddleTableContact;

    // ── Per-hit telemetry event for variance audit ────────────────────────────
    // Fired from ApplyPaddleHit immediately after angular velocity is set.
    // Subscribed by RealHitVarianceLogger; safe to leave unsubscribed.
    public struct HitTelemetryData
    {
        public string  hitSource;             // "Layer_ComputePen" or "Layer4_Physics"
        public Vector3 contactPoint;          // world-space contact point
        public Vector3 contactNormal;         // authoritative outward normal
        public float   leverArmMag;           // |contactPt - paddleBoxCenter| (m)
        public Vector3 paddleLinearVelocity;  // EMA-smoothed (or injected) linear vel
        public Vector3 paddleAngVelocity;     // EMA-smoothed (or injected) angular vel
        public float   effectiveSpd;          // |paddleVel + w x leverArm| (m/s)
        public float   outSpeed;              // assigned ball speed (m/s)
        public float   outSpin;              // |_ballRb.angularVelocity| after hit (rad/s)
    }
    public static event System.Action<HitTelemetryData> OnHitTelemetry;

    // ── Inspector ─────────────────────────────────────────────────────────────
    public GameObject paddle;
    public GameObject ball;
    public BoxCollider paddleBox;
    public BoxCollider enemypaddleBox;
    public GameObject enemyPaddle;
    [Tooltip("Table root GameObject — toggled by right grip button")]
    public GameObject table;
    public float speed;
    [Tooltip("OVRCameraRig or avatar root — GetChild(0)=left hand, GetChild(1)=right hand")]
    public GameObject avatar;

    [Header("Paddle offset in controller LOCAL space")]
    public Vector3 paddlePositionOffset = new Vector3(0f, 0f, 0f);
    [Tooltip("Y=180 flips blade face toward opponent")]
    public Vector3 paddleRotationOffset = new Vector3(0f, 180f, 0f);

    [Header("Velocity Pipeline")]
    [Tooltip("EMA smoothing factor for LINEAR paddle velocity (0–1). " +
             "Higher = faster response, more jitter. Lower = smoother, more lag. " +
             "Default 0.35. At 120 Hz, 0.35 ≈ 22 ms time constant.")]
    public float velEmaAlpha = 0.35f;
    [Tooltip("EMA smoothing factor for ANGULAR paddle velocity (0–1). " +
             "Higher = faster wrist-snap response. Lower = more jitter rejection. " +
             "Default 0.25. At 120 Hz, 0.25 ≈ 30 ms time constant.")]
    public float angEmaAlpha = 0.25f;

    [Header("Low-Speed Juggling Stabilization")]
    [Tooltip("Linear paddle speed (m/s) below which stabilization activates. Above this value all three assists are fully off.")]
    public float lowSpeedAssistThreshold = 2.5f;
    [Tooltip("How much angular-velocity contribution is reduced at zero speed. 0=no reduction, 1=full removal. Ramps smoothly to zero effect at threshold.")]
    public float lowSpeedAngularDamping  = 0.85f;
    [Tooltip("How much spin generation is reduced at zero speed. 0=no reduction, 1=full removal. Ramps smoothly to zero effect at threshold.")]
    public float lowSpeedSpinDamping     = 0.6f;
    [Tooltip("Fraction (0–1) by which the spin-decomp normal is blended toward the true paddle face at zero speed. Removes mm-level contact-point jitter. Does NOT alter shot direction.")]
    public float juggleStabilityStrength = 0.3f;

    // ── Device lists ──────────────────────────────────────────────────────────
    private List<XRInputDevice> _rightDevices = new List<XRInputDevice>();
    private List<XRInputDevice> _leftDevices  = new List<XRInputDevice>();

    // ── Controller pose ───────────────────────────────────────────────────────
    private Vector3    _rightPos = Vector3.zero;
    private Quaternion _rightRot = Quaternion.identity;
    private Vector3    _leftPos  = Vector3.zero;
    private Quaternion _leftRot  = Quaternion.identity;
    private bool _rightValid = false;
    private bool _leftValid  = false;

    // ── Paddle velocity tracking ──────────────────────────────────────────────
    private Vector3 _prevWorldPos        = Vector3.zero;
    private Vector3 _paddleVelocity      = Vector3.zero;
    private Vector3 _paddleAngularVelocity = Vector3.zero; // world-space rad/s — wrist rotation
    private Vector3   _prevBallPos      = Vector3.zero;   // swept tunneling check
    private bool      _prevBallPosValid = false;          // guards SphereCast before first valid frame
    private Vector3    _prevPaddlePos      = Vector3.zero;   // paddle-sweep check
    private Quaternion _prevPaddleRot      = Quaternion.identity;
    private Vector3    _currentPaddlePos   = Vector3.zero;   // target pos this step
    private Quaternion _currentPaddleRot   = Quaternion.identity; // target rot this step
    private bool       _prevPaddlePosValid = false;
    private bool       _prevRightValid     = false;       // tracks whether last FixedUpdate had valid tracking
    private Collider  _ballCollider     = null;           // physics collision forwarder
    private float       _lastHitTime  = -1f;
    private int         _lastHitFrame = -1;   // physics frame of last hit (blocks same-step double-fire)
    // ── Hit diagnostics ───────────────────────────────────────────────────────
    private string  _diagLayer           = "none"; // set by each layer before calling ApplyPaddleHit
    private float   _diagSweepDist       = 0f;     // sweep length this step (ball or paddle)
    private float   _diagPrevBallFaceSign = 0f;    // sign(dot(ball-paddleFace,faceNormal)) prev frame — pass-through detection
    // ── Velocity EMA smoothing ────────────────────────────────────────────────
    // Applied after the per-step raw computation in FixedUpdate.
    // Alphas are now inspector-tunable (velEmaAlpha / angEmaAlpha).
    // Angular spike suppression ratio: if a single-step angular velocity is
    // more than ANG_SPIKE_RATIO times the previous smoothed magnitude, it is
    // treated as a tracking glitch and clamped to the smoothed value.
    // Catches medium spikes (5–15 rad/s) that slip under the ANG_VEL_MAX hard cap.
    private const float ANG_SPIKE_RATIO = 3.5f;
    private Vector3     _smoothPaddleVel    = Vector3.zero;
    private Vector3     _smoothPaddleAngVel = Vector3.zero;

    // ── OVR velocity cache (filled in Update, read in FixedUpdate) ────────────
    // OVRInput.Update() runs once per render frame inside Update().
    // Reading OVRInput in FixedUpdate risks returning a stale value when two
    // physics steps fire within the same render frame (120 Hz physics / 72 Hz display).
    // Cache here (world-space) so FixedUpdate always gets this frame's reading.
    private Vector3 _cachedOVRVel   = Vector3.zero;
    private int     _cachedOVRFrame = -1;

    // ── Pre-allocated physics / scratch buffers ───────────────────────────────
    // Eliminates per-step GC allocations from FixedUpdate hot paths.
    // _cornersCache : replaces `new Vector3[8]` in ANGULAR_SWEEP diagnostic
    // _overlapBuffer: used with OverlapBoxNonAlloc in PADDLE-FREEZE diagnostic
    private readonly Vector3[]  _cornersCache  = new Vector3[8];
    private readonly Collider[] _overlapBuffer = new Collider[16];

    // ── Paddle-freeze diagnostics ─────────────────────────────────────────────
    private float      _diagPrevLinVelMag     = 0f;
    private float      _diagPrevAngVelMag     = 0f;
    private Vector3    _diagExpectedPaddlePos = Vector3.zero;
    private Quaternion _diagExpectedPaddleRot = Quaternion.identity;
    private float      _diagFixedUpdateStart  = 0f;
    private float      _diagHitDetStart       = 0f;
    private float      _diagApplyHitStart     = 0f;
    // ── Angular-sweep diagnostics ─────────────────────────────────────────────
    // Per-step state: 0=locked(cooldown/sep), 1=open_no_hit, 2=fired
    private int        _diagL0State               = 0;
    private int        _diagL1State               = 0;
    private int        _diagL2State               = 0;
    private int        _diagL3State               = 0;
    private int        _diagL4State               = 0;
    private bool       _diagApplyHitCalledThisStep  = false;
    // Set true when a geometry finder found a contact point but the unified swing
    // guard rejected it (surfVel < SWING_MIN).  Distinguishes SWING_GUARD_REJECTED
    // from OPEN_BUT_GEOMETRY_MISS in ANGULAR-SWEEP arbitration reports.
    private bool       _diagSwingGuardRejected     = false;
    // Previous-frame values for delta / crossing calculations
    private Quaternion _diagPrevPaddleRotAS    = Quaternion.identity;
    private Vector3    _diagPrevPaddleFwdAS    = Vector3.forward;
    private float      _diagPrevFaceDotAS      = 0f;
    // Last hit's normal/velocity — written in ApplyPaddleHit, read in ANGULAR_SWEEP
    private Vector3    _diagLastRawNormal      = Vector3.zero;
    private Vector3    _diagLastSnappedNormal  = Vector3.zero;
    private bool       _diagLastNormalFlipped  = false;
    private Vector3    _diagLastAssignedVel    = Vector3.zero;
    private Vector3    _diagLastFinalBallVel   = Vector3.zero;

    // ── Table / serve tracking ────────────────────────────────────────────────
    private float   _tableTopY    = 0.76f;      // world-space Y of table surface (cached in Start)
    private float   _netZ         = 0f;         // world-space Z of net centre (cached in Start)
    private Vector3 _tableForward = Vector3.forward; // horizontal unit vector playerside→enemyside (cached in Start)
    // Edge-detect: only fire PaddleTableContact on the entering edge (above → below).
    private bool  _paddleWasAboveTable = true;
    // Per-serve tracking — reset at each HIDDEN→HELD transition.
    private float _serveGrabTime              = -1f;          // Time.time when ball first held
    private float _minPaddleTableDistThisServe = float.MaxValue; // closest paddle got to table
    // Ideal topspin paddle contact normal in world space:
    //   Vector3(0, 0.707, 0.707) = 45° closed toward opponent (+Z is opponent side).
    //   PaddleAngleErrorDeg = Vector3.Angle(contactNormal, IDEAL_SPIN_NORMAL).
    private static readonly Vector3 IDEAL_SPIN_NORMAL =
        new Vector3(0f, 0.707f, 0.707f);

    private const float HIT_COOLDOWN  = 0.08f;   // min time between hits (s)
    private const float HIT_RADIUS    = 0.04f;   // 4 cm — ball radius + reliable skin
    private const float HIT_BASE_SPD  = 2.0f;    // m/s minimum outgoing speed
    private const float HIT_PAD_SCALE = 0.45f;   // paddle-velocity scale (reduced: was 0.65, triple-hit compounding)
    private const float HIT_MAX_SPD   = 12.0f;   // hard cap (m/s)
    // SPIN_COEFF raised 60→75 (calibration pass): tangential brush velocity converts
    // more directly to spin, making topspin/backspin clearly distinguishable.
    private const float SPIN_COEFF    = 75f;     // rad/s per m/s of tangential brush velocity
    private const float MAX_SPIN      = 300f;    // rad/s cap — prevents physics instability over long sessions
    // ── Unified swing guard ───────────────────────────────────────────────────
    // Single minimum paddle surface speed applied identically across ALL layers
    // and Layer 4 physics callback.  Previously each layer defined its own local
    // constant (L0_MIN_SWING_SPD, MIN_SWING_SPD, L4_MIN_SWING_SPD), all = 0.5f,
    // but isolated — a change in one never propagated to others.
    private const float SWING_MIN     = 0.5f;    // m/s — unified across all contact layers
    // ── Tracking dropout spike cap ────────────────────────────────────────────
    // Quest 3 tracking occasionally drops and snaps, producing single-frame
    // rotation deltas that compute as 20–30+ rad/s.  Real wrist snaps peak well
    // below 40 rad/s (~2300°/s).  Hard cap kills ghost surface speeds from dropout
    // corrections while leaving genuine fast wrist flicks entirely unaffected.
    private const float ANG_VEL_MAX   = 40f;    // rad/s — hard cap on angular velocity

    private bool _trackingReady = false;
    private bool _tableScaled   = false;

    // ── AI ────────────────────────────────────────────────────────────────────
    private float       _aiLastHitTime  = -1f;
    private const float AI_HIT_COOLDOWN = 0.35f;
    private const float AI_SPEED_MIN    = 3.0f;
    private const float AI_SPEED_MAX    = 8.0f;

    // ── Components ────────────────────────────────────────────────────────────
    private Rigidbody  _paddleRb;
    private Rigidbody  _ballRb;
    private Renderer[] _paddleRenderers;
    private Renderer[] _ballRenderers;
    private int        _debugFrame = 0;

    // ── Left hand anchor ──────────────────────────────────────────────────────
    // OVRCameraRig exposes leftControllerAnchor as a public property — it is
    // already in world space and updated by the Oculus SDK every frame.
    // We cache the OVRCameraRig component once; the anchor is read per-frame.
    private OVRCameraRig _ovrRig      = null;
    private Transform    _leftAnchor  = null;  // = _ovrRig.leftControllerAnchor
    private Transform    _rightAnchor = null;  // = _ovrRig.rightControllerAnchor

    // Left-hand velocity sampled every frame while ball is Held (for natural toss).
    private Vector3 _leftAnchorPrev   = Vector3.zero;
    private Vector3 _leftHandVelocity = Vector3.zero;

    // Trigger edge detection — only react on the frame trigger goes from 0→1.
    private bool _triggerWasPressed = false;

    // Serve-reset arming — prevents the serve-reset from firing because the player
    // is still physically holding the trigger from the previous serve grab.
    // Set false on toss (→ Dropped); set true once trigger is released.
    // Serve-reset (!inPlayerStart) only fires when BOTH triggerHeld AND this is true.
    private bool  _serveResetArmed     = false;
    // Serve-reset hold timer — the reset only fires after the trigger has been held
    // continuously for SERVE_RESET_HOLD_SEC seconds.  Prevents accidental brief
    // trigger presses during a rally from instantly vanishing the ball.
    private float _serveResetHoldStart = -1f;
    private const float SERVE_RESET_HOLD_SEC = 0.4f;
    private float _serveDiagLastLog = -1f;  // throttle for trigger-held diagnostic
    private float _hitDiagLastLog   = -1f;  // throttle for hit-window diagnostic

    // Contact-session lockout — prevents the same paddle contact from firing
    // multiple hit layers (OverlapBox, proximity, SphereCast, BoxCast, physics).
    // Set false in ApplyPaddleHit; set true once ball has moved > SEPARATION_DIST
    // from the paddle surface.  Works in addition to HIT_COOLDOWN.
    private bool _ballSeparated = true;
    private const float SEPARATION_DIST = 0.08f;  // 8 cm — clears the paddle face on exit

    // ── Serve state ───────────────────────────────────────────────────────────
    //
    //  HIDDEN   : trigger not held → ball invisible, parked underground
    //  HELD     : trigger held     → ball at left hand, gravity off, kinematic
    //  DROPPED  : trigger released → ball free-falls, physics owns it
    //
    private enum ServePhase { Hidden, Held, Dropped }
    private ServePhase _servePhase = ServePhase.Hidden;
    private float      _dropTime   = -10f;

    // testModeActive is always false in production — kept as a static field because
    // RealHitVarianceLogger reads it to skip telemetry events during non-juggling frames.
    [HideInInspector] public static bool testModeActive = false;

    /// <summary>
    /// Set true by RealHitVarianceLogger to instrument real juggling hits
    /// outside the serve/rally state machine.  Bypasses SessionActive and
    /// _hitWindowOpen gates in FixedUpdate and Layer 4 so that every
    /// validated paddle contact is forwarded to OnHitTelemetry.
    /// Cleared automatically when RealHitVarianceLogger finishes collecting.
    /// </summary>
    [HideInInspector] public static bool jugglingMode = false;

    // ── Audio ─────────────────────────────────────────────────────────────────
    // AUDIO-ONLY PATCH: this is the only subsystem changed from the old working-physics file.
    // Paddle-hit sound is played through a dedicated 2D source on the gameplay GameObject.
    // The old paddle-mounted AudioSource is retained only so existing components are not disturbed.
    private AudioSource _gameplayAudio;
    private AudioSource _paddleAudio;
    private AudioClip   _paddleHitClip;

    // ── Per-rally bounce counter ───────────────────────────────────────────────
    // Counts table contacts (playerside / enemyside) since the last paddle hit.
    // Subscribed to ballbounce.OnTableBounce; reset at the start of each hit.
    // Exposed in [PHYSICS-AUDIT] diagnostic so researchers can correlate energy
    // loss with rally length (longer rally → more decay → weaker returns).
    private int _bounceCount = 0;

    // ── Events ────────────────────────────────────────────────────────────────
    void OnEnable()
    {
        InputDevices.deviceConnected    += OnDeviceChange;
        InputDevices.deviceDisconnected += OnDeviceChange;
        ballbounce.OnTableBounce        += OnTableBounceReceived;
    }
    void OnDisable()
    {
        InputDevices.deviceConnected    -= OnDeviceChange;
        InputDevices.deviceDisconnected -= OnDeviceChange;
        ballbounce.OnTableBounce        -= OnTableBounceReceived;
    }

    void OnTableBounceReceived(string colName, Vector3 pos) => _bounceCount++;
    void OnDeviceChange(XRInputDevice d) => RefreshDeviceLists();

    void RefreshDeviceLists()
    {
        _rightDevices.Clear();
        InputDevices.GetDevicesWithCharacteristics(
            InputDeviceCharacteristics.Controller | InputDeviceCharacteristics.Right,
            _rightDevices);
        _leftDevices.Clear();
        InputDevices.GetDevicesWithCharacteristics(
            InputDeviceCharacteristics.Controller | InputDeviceCharacteristics.Left,
            _leftDevices);
    }

    // ── Start ─────────────────────────────────────────────────────────────────
    void Start()
    {
        if (paddle != null)
        {
            _paddleRb = paddle.GetComponent<Rigidbody>();
            if (_paddleRb != null)
            {
                // Kinematic + MovePosition/MoveRotation: physics engine tracks the
                // paddle's displacement each FixedUpdate step and performs a full
                // sweep (CCD) for collision detection.  Without this the paddle is
                // teleported via transform.position — physics sees no displacement,
                // no sweep occurs, and fast swings let the ball pass straight through.
                _paddleRb.isKinematic        = true;
                _paddleRb.useGravity         = false;
                _paddleRb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
            }
            if (paddleBox == null) paddleBox = paddle.GetComponent<BoxCollider>();
            // Enforce a minimum Z thickness on the paddle collider.
            // A thin paddle (< 3 cm) has a speculative bounds too narrow for fast
            // VR swings — the ball can slip through the gap between two steps.
            // 3 cm matches Eleven TT's internal collider thickness.
            if (paddleBox != null)
            {
                Vector3 sz = paddleBox.size;
                if (sz.z < 0.03f)
                {
                    sz.z = 0.03f;
                    paddleBox.size = sz;
                    Debug.Log($"[gameplay] Paddle BoxCollider Z expanded to 0.03 m for reliable CCD.");
                }

                // Give the paddle bounciness=0 with Minimum combine.
                // The ball's material uses Minimum combine (bounciness=0.88).
                // Minimum+Minimum → min(0.88, 0) = 0: zero physics bounce at the paddle.
                // This eliminates the engine-level COR impulse that would otherwise
                // add a second velocity change on top of ApplyPaddleHit's explicit set.
                // Table/floor are unaffected: min(0.88, 0.76)=0.76, min(0.88, 0.55)=0.55.
                var padMat = new PhysicsMaterial("Paddle");
                padMat.bounciness      = 0f;
                padMat.dynamicFriction = 0.5f;
                padMat.staticFriction  = 0.5f;
                padMat.bounceCombine   = PhysicsMaterialCombine.Minimum;
                padMat.frictionCombine = PhysicsMaterialCombine.Average;
                paddleBox.material     = padMat;
                paddleBox.sharedMaterial = padMat;
            }
            _paddleRenderers = paddle.GetComponentsInChildren<Renderer>(true);
            // Always visible — underground (y=-100) until OVRInput gives real tracking.
            SetPaddleVisible(true);
            _trackingReady = true;

            // Physics collision forwarder — catches fast-swing collisions that
            // the proximity check misses.  ContinuousSpeculative on the kinematic
            // paddle generates real OnCollisionEnter events; this component routes
            // them back to gameplay.cs so we can apply our custom hit response.
            var fwd = paddle.GetComponent<PaddleHitForwarder>();
            if (fwd == null) fwd = paddle.AddComponent<PaddleHitForwarder>();
            fwd.owner = this;

            // AUDIO-ONLY PATCH: keep all old hit detection/physics unchanged.
            // Do NOT play paddle-hit audio from the moving paddle's 3D AudioSource.
            // Use a dedicated 2D AudioSource on this gameplay GameObject instead.
            _paddleHitClip = CreatePaddleClickClip();

            _gameplayAudio = gameObject.GetComponent<AudioSource>();
            if (_gameplayAudio == null)
                _gameplayAudio = gameObject.AddComponent<AudioSource>();

            _gameplayAudio.clip                  = null;
            _gameplayAudio.spatialBlend          = 0f;   // 2D: avoids Quest/OpenXR near-field 3D rolloff
            _gameplayAudio.volume                = 1f;
            _gameplayAudio.mute                  = false;
            _gameplayAudio.enabled               = true;
            _gameplayAudio.playOnAwake           = false;
            _gameplayAudio.loop                  = false;
            _gameplayAudio.priority              = 0;
            _gameplayAudio.outputAudioMixerGroup = null;
            _gameplayAudio.bypassEffects         = true;
            _gameplayAudio.bypassReverbZones     = true;
            _gameplayAudio.bypassListenerEffects = true;

            // Retain the old paddle AudioSource reference but do not use it for hit sound.
            _paddleAudio = paddle.GetComponent<AudioSource>();

            var listener = FindObjectOfType<AudioListener>();
            if (listener == null)
                Debug.LogError("[SOUND-DIAG][ERROR] No AudioListener found in scene");
            else
                Debug.Log($"[SOUND-DIAG] AudioListener found on '{listener.gameObject.name}'");

            if (Mathf.Approximately(AudioListener.volume, 0f))
            {
                AudioListener.volume = 1f;
                Debug.Log("[SOUND-DIAG] AudioListener.volume was 0 — reset to 1");
            }

            Debug.Log($"[SOUND-DIAG] Dedicated gameplay audio initialized audioNull={_gameplayAudio == null} clipNull={_paddleHitClip == null}");
        }

        // Auto-find ball
        if (ball == null || ball.GetComponent<ballbounce>() == null)
        {
            var bb = FindObjectOfType<ballbounce>();
            if (bb != null)
            {
                ball = bb.gameObject;
                Debug.Log("[gameplay] Auto-found ball: " + ball.name);
            }
        }

        if (ball != null)
        {
            _ballRb       = ball.GetComponent<Rigidbody>();
            _ballCollider = ball.GetComponent<Collider>();

            // The ball is held in the left hand, which sits inside the OVRPlayerController
            // capsule at release.  When isKinematic flips false the physics solver fires a
            // 0.27 m depenetration impulse sideways — observed as "ball flies sideways on toss".
            // Fix: permanently ignore all collisions between the ball and every collider on
            // the OVRPlayerController hierarchy.  The player body capsule should never affect
            // ball physics — this is safe for the entire session.
            if (_ballCollider != null)
            {
                GameObject ovrPC = GameObject.Find("OVRPlayerController");
                if (ovrPC != null)
                {
                    Collider[] playerCols = ovrPC.GetComponentsInChildren<Collider>(true);
                    foreach (Collider pc in playerCols)
                    {
                        Physics.IgnoreCollision(_ballCollider, pc, true);
                        Debug.Log($"[gameplay] IgnoreCollision: ball ↔ '{pc.gameObject.name}/{pc.GetType().Name}'");
                    }
                    // CharacterController extends Collider but Unity 6 does not always
                    // include it in GetComponentsInChildren<Collider>() results — ignore
                    // it explicitly so the capsule body never kicks the ball sideways.
                    CharacterController cc = ovrPC.GetComponentInChildren<CharacterController>(true);
                    if (cc != null)
                    {
                        Physics.IgnoreCollision(_ballCollider, cc, true);
                        Debug.Log($"[gameplay] IgnoreCollision: ball ↔ CharacterController on '{cc.gameObject.name}' (explicit)");
                    }
                    Debug.Log($"[gameplay] Ball↔OVRPlayerController: ignored {playerCols.Length} collider(s) + CC — depenetration fix applied");
                }
                else
                {
                    Debug.LogWarning("[gameplay] OVRPlayerController not found — ball may still be kicked sideways on toss");
                }
            }

            if (_ballRb != null)
            {
                _ballRb.mass                   = 0.0027f;    // real TT ball: 2.7 g
                _ballRb.linearDamping          = 0.02f;      // very low air resistance
                _ballRb.angularDamping         = 0.30f;
                _ballRb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
                _ballRb.interpolation          = RigidbodyInterpolation.Interpolate;

                // Run physics at 120 Hz.
                // At max ball speed (12 m/s) each step covers only 10 cm.
                // At 90 Hz it was 13 cm — enough for the ball to skip the entire
                // 4 cm hit-radius zone on fast incoming shots.
                // 120 Hz also tightens the paddle sweep window, reducing CCD gaps.
                Time.fixedDeltaTime = 1f / 120f;
            }

            var col = ball.GetComponent<Collider>();
            if (col != null)
            {
                var mat = new PhysicsMaterial("Ball");
                mat.bounciness      = 0.88f;
                mat.dynamicFriction = 0.08f;
                mat.staticFriction  = 0.08f;
                mat.bounceCombine   = PhysicsMaterialCombine.Minimum;
                mat.frictionCombine = PhysicsMaterialCombine.Minimum;
                col.material        = mat;
            }

            _ballRenderers = ball.GetComponentsInChildren<Renderer>(true);
        }

        _prevWorldPos = Vector3.zero;
        RefreshDeviceLists();

        // ── Cache OVRCameraRig and both controller anchors ────────────────────
        if (avatar != null)
            _ovrRig = avatar.GetComponent<OVRCameraRig>();

        if (_ovrRig != null)
        {
            // These properties are set in OVRCameraRig.EnsureGameObjectIntegrity()
            // called from Awake, so they should exist by Start. Fallbacks below handle
            // any edge case where they're still null (script execution order race).
            _leftAnchor  = _ovrRig.leftControllerAnchor  ?? _ovrRig.leftHandAnchor;
            _rightAnchor = _ovrRig.rightControllerAnchor ?? _ovrRig.rightHandAnchor;
            Debug.Log($"[gameplay] OVRCameraRig OK  L={_leftAnchor?.name ?? "NULL"}  R={_rightAnchor?.name ?? "NULL"}");
        }
        else
        {
            Debug.LogWarning("[gameplay] OVRCameraRig NOT found on avatar. " +
                             "Assign the OVRCameraRig GameObject to the Avatar field.");
        }

        // Remove enemy paddle from scene at startup.
        if (enemyPaddle != null)
        {
            enemyPaddle.SetActive(false);
            Debug.Log("[gameplay] Enemy paddle deactivated.");
        }

        // ── Cache table surface Y and net Z ──────────────────────────────────
        GameObject ps = GameObject.Find("playerside");
        GameObject es = GameObject.Find("enemyside");
        if (ps != null)
        {
            Collider psCol = ps.GetComponent<Collider>();
            if (psCol != null) { _tableTopY = psCol.bounds.max.y; }
        }
        if (ps != null && es != null)
        {
            _netZ = (ps.transform.position.z + es.transform.position.z) * 0.5f;
            Vector3 esPs = es.transform.position - ps.transform.position;
            esPs.y = 0f;   // flatten to horizontal plane
            if (esPs.sqrMagnitude > 0.01f) _tableForward = esPs.normalized;
        }
        Debug.Log($"[gameplay] Table top Y={_tableTopY:F3} m  Net Z={_netZ:F3}  tableForward={_tableForward:F3}");
        // ── Auto-create RealHitVarianceLogger if not already in scene ─────────
        if (FindObjectOfType<RealHitVarianceLogger>() == null)
        {
            gameObject.AddComponent<RealHitVarianceLogger>();
            Debug.Log("[gameplay] RealHitVarianceLogger auto-attached.");
        }
    }

    // ── Called by TableAutoPlace after every recenter ─────────────────────────
    // Refreshes all cached table-position values so paddle-table contact
    // detection and serve validation use the table's new world position.
    public void RefreshTableCacheAfterRecenter()
    {
        GameObject ps = GameObject.Find("playerside");
        GameObject es = GameObject.Find("enemyside");

        if (ps != null)
        {
            Collider psCol = ps.GetComponent<Collider>();
            if (psCol != null)
                _tableTopY = psCol.bounds.max.y;
        }

        if (ps != null && es != null)
        {
            _netZ = (ps.transform.position.z + es.transform.position.z) * 0.5f;
            Vector3 esPs = es.transform.position - ps.transform.position;
            esPs.y = 0f;
            if (esPs.sqrMagnitude > 0.01f) _tableForward = esPs.normalized;
        }

        // Reset edge-detect so the very next paddle-table contact fires cleanly.
        _paddleWasAboveTable = true;

        Debug.Log($"[TABLE-CACHE] refreshed after recenter tableTopY={_tableTopY:F4} netZ={_netZ:F4}  tableForward={_tableForward:F3}");
    }

    // ── Lazy anchor resolution ────────────────────────────────────────────────
    // Called every frame while the anchor is null (covers late-init race).
    // Priority: OVRCameraRig property → hierarchy name search.
    void RefreshLeftAnchor()
    {
        if (_leftAnchor != null) return;
        if (_ovrRig == null && avatar != null)
            _ovrRig = avatar.GetComponent<OVRCameraRig>();
        if (_ovrRig != null)
            _leftAnchor = _ovrRig.leftControllerAnchor ?? _ovrRig.leftHandAnchor;
        if (_leftAnchor == null && avatar != null)
            foreach (var path in new[] {
                "TrackingSpace/LeftControllerAnchor",
                "TrackingSpace/LeftHandAnchor",
                "LeftControllerAnchor", "LeftHandAnchor" })
            {
                _leftAnchor = avatar.transform.Find(path);
                if (_leftAnchor != null) { Debug.Log("[gameplay] Left anchor found by path: "+path); break; }
            }
    }


    void SetPaddleVisible(bool v)
    {
        if (_paddleRenderers == null) return;
        foreach (var r in _paddleRenderers) r.enabled = v;
    }

    void SetBallVisible(bool v)
    {
        if (_ballRenderers == null) return;
        foreach (var r in _ballRenderers) r.enabled = v;
    }

    // ── Procedural ping clip ───────────────────────────────────────────────────
    static AudioClip CreatePingClip(float frequency = 1000f, float duration = 0.06f)
    {
        const int sampleRate = 44100;
        int   samples = Mathf.RoundToInt(sampleRate * duration);
        float[] data  = new float[samples];
        for (int i = 0; i < samples; i++)
        {
            float t   = (float)i / sampleRate;
            float env = Mathf.Exp(-t * 40f);
            data[i]   = env * Mathf.Sin(2f * Mathf.PI * frequency * t);
        }
        var clip = AudioClip.Create("Ping_" + (int)frequency, samples, 1, sampleRate, false);
        clip.SetData(data, 0);
        return clip;
    }

    // ── Update ────────────────────────────────────────────────────────────────
    void Update()
    {
        try { OVRInput.Update(); } catch { }

        UpdateControllerPoses();

        // ── Cache OVR controller velocity — world space ───────────────────────
        // OVRInput.Update() was called two lines above: state is guaranteed fresh.
        // OVRInput must NOT be polled in FixedUpdate for velocity — it would return
        // the same stale reading on any second physics step within the same frame.
        // GetLocalControllerVelocity returns velocity in the OVRCameraRig's
        // TrackingSpace local frame.  Multiply by the TrackingSpace world rotation
        // to get a world-space vector that matches the world-space position delta.
        {
            Vector3 rawOVRVel = Vector3.zero;
            try { rawOVRVel = OVRInput.GetLocalControllerVelocity(OVRInput.Controller.RTouch); } catch { }
            if (rawOVRVel.sqrMagnitude > 0.0001f && _rightAnchor != null)
            {
                // _rightAnchor (rightControllerAnchor) is a direct child of TrackingSpace.
                // _rightAnchor.parent.rotation is therefore the TrackingSpace world rotation —
                // the correct local-to-world mapping for OVR velocities.
                Quaternion tsRot = _rightAnchor.parent != null
                                 ? _rightAnchor.parent.rotation
                                 : Quaternion.identity;
                _cachedOVRVel = tsRot * rawOVRVel;
            }
            else
            {
                _cachedOVRVel = Vector3.zero;
            }
            _cachedOVRFrame = Time.frameCount;
        }

        if (ballbounce.enemyFlag) FollowBall();
        if (ballbounce.resetFlag) ResetEnemyPaddle();

        sharedGrip      = GetRightGrip();
        sharedTrigger   = GetRightIndexTrigger();
        sharedThumb     = GetRightThumb();
        sharedLeftPos   = _leftPos;
        sharedLeftRot   = _leftRot;
        sharedLeftValid = _leftValid;

        // Stability gate removed — _trackingReady is set true in Start().
        // Paddle is always visible; GetPaddleWorldPos returns y=-100 until
        // OVRInput provides real controller data.

        // Paddle visual position is driven exclusively by FixedUpdate via
        // MovePosition / MoveRotation so the physics collider and the rendered mesh
        // are always in the same position that the physics engine sees.
        // Writing to transform here (outside FixedUpdate) bypasses the CCD sweep
        // and desynchronises the collider from the Rigidbody, causing tunneling.

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        if (++_debugFrame % 90 == 0)
        {
            Debug.Log($"[gameplay] rightValid={_rightValid} rightPos={_rightPos:F2} " +
                      $"leftValid={_leftValid} leftPos={_leftPos:F2} " +
                      $"state={ballbounce.state} phase={_servePhase} session={ExperimentLogger.SessionActive}");

            if (paddle != null)
                Debug.Log($"[RIGHT PADDLE] tracking={_trackingReady} " +
                          $"rightValid={_rightValid} " +
                          $"pos={paddle.transform.position.ToString("F2")} " +
                          $"anchor={(_rightAnchor != null ? _rightAnchor.name : "none")}");
        }
#endif

        // ── Ball visibility safety ────────────────────────────────────────
        // Ball is always rendered — it is never moved underground any more.
        // The player sees it wherever it lands after each point, then grabs
        // it with the left trigger when ready to serve.
        if (_ballRb != null)
            SetBallVisible(true);

        // ── Right grip — table height toggle ─────────────────────────────
        // SecondaryHandTrigger = right grip button on Oculus Touch.
        if (table != null && OVRInput.GetDown(OVRInput.Button.SecondaryHandTrigger))
        {
            if (!_tableScaled)
            {
                table.transform.localScale = new Vector3(1f, 1.2f, 1f);
                _tableScaled = true;
            }
            else
            {
                table.transform.localScale = new Vector3(1f, 1f, 1f);
                _tableScaled = false;
            }
        }

        // ── Gate: session must be active ──────────────────────────────────
        if (!ExperimentLogger.SessionActive) return;

        // ── Serve handling ────────────────────────────────────────────────
        HandlePlayerServe();

        // ── Player hit detection ──────────────────────────────────────────
        // Allow hits once ball is dropped and post-drop window has passed,
        // OR when ball is already in rally (not playerStart).
        bool inServeDropped = (ballbounce.state == ballbounce.gameState.playerStart
                               && _servePhase == ServePhase.Dropped);
        bool inRally        = (ballbounce.state == ballbounce.gameState.playerPaddle);

        // Arm-motion debounce: open hit window (and allow ballbounce to act on
        // playerPaddleCollision) only after 0.4 s.  Also drives the serveReady
        // static flag that ballbounce checks before transitioning playerStart→playerPaddle.
        if (inServeDropped && !serveReady && Time.time > _dropTime + 0.4f)
        {
            serveReady = true;
            Debug.Log("[SERVE] serveReady=true — hit window open.");
        }

        bool hitWindowOpen = inRally || (inServeDropped && serveReady);
        // hitWindowOpen is consumed by FixedUpdate (see below).
        // Stored in a field so FixedUpdate can read the same value without re-computing.
        _hitWindowOpen = hitWindowOpen;
    }

    // ── FixedUpdate — paddle movement + hit detection ─────────────────────────
    //
    // Paddle placement via MovePosition/MoveRotation:
    //   Physics engine records the displacement from the previous step and sweeps
    //   the collider along that path (CCD).  This prevents the ball tunneling
    //   through fast paddle swings that would occur with transform writes.
    //
    // Hit detection here (not Update) so _ballRb.position is the actual
    // physics-step position — no interpolation offset, no frame-rate mismatch.
    //
    private bool _hitWindowOpen = false;

    void FixedUpdate()
    {
        if (paddle == null || _paddleRb == null) return;
        _diagFixedUpdateStart = Time.realtimeSinceStartup;

        // ── Render-frame stall detection ──────────────────────────────────────
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        if (Time.deltaTime > 0.1f)
            Debug.Log($"[FREEZE-DIAG] FRAME_STALL  deltaTime={Time.deltaTime * 1000f:F1} ms" +
                      $"  frame={Time.frameCount}  fixedTime={Time.fixedTime:F3}");
#endif

        // ── Ball Rigidbody sleep guard ────────────────────────────────────────
        // WakeUp() must run in ALL builds — a sleeping ball ignores collision
        // callbacks regardless of build type.  Only the log is dev-only.
        if (_ballRb != null && _ballRb.IsSleeping())
        {
            _ballRb.WakeUp();
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            Debug.Log($"[FREEZE-DIAG] Ball was SLEEPING — woke up.  frame={Time.frameCount}");
#endif
        }

        // ── Freshen right-hand pose directly in FixedUpdate ───────────────────
        // UpdateControllerPoses() runs in Update() — FixedUpdate can fire 1-2×
        // per render frame, always BEFORE Update() in the same frame.  This means
        // the cached _rightPos/_rightRot are from the PREVIOUS render frame (~11 ms
        // stale at 90 Hz).  On a 50 ms wrist flick that lag is ~13° of paddle-face
        // error.  Re-querying InputTracking here gives a sub-frame pose that is
        // current for this physics step.  OVR is NOT re-queried — OVRInput caches
        // after OVRInput.Update() which won't have run yet; InputTracking/XRDevice
        // queries go directly to the XR subsystem and DO return the latest data.
        {
            Vector3    rp2 = InputTracking.GetLocalPosition(XRNode.RightHand);
            Quaternion rr2 = InputTracking.GetLocalRotation(XRNode.RightHand);
            if (rp2.sqrMagnitude > 0.0001f || rr2 != Quaternion.identity)
            { _rightPos = rp2; _rightRot = rr2; _rightValid = true; }
            else if (_rightDevices.Count > 0)
            {
                foreach (var d in _rightDevices)
                {
                    Vector3 rp3; Quaternion rr3;
                    if (d.TryGetFeatureValue(XRCommonUsages.devicePosition, out rp3) &&
                        d.TryGetFeatureValue(XRCommonUsages.deviceRotation, out rr3) &&
                        rp3 != Vector3.zero)
                    { _rightPos = rp3; _rightRot = rr3; _rightValid = true; break; }
                }
            }
        }

        if (_rightValid)
        {
            Vector3    fp = _rightPos + _rightRot * paddlePositionOffset;
            Quaternion fr = _rightRot  * Quaternion.Euler(paddleRotationOffset);

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (Time.frameCount % 120 == 0 && paddle != null)
            {
                string pbPos       = paddleBox != null ? paddleBox.transform.position.ToString("F4") : "null";
                string pbTpCenter  = paddleBox != null ? paddleBox.transform.TransformPoint(paddleBox.center).ToString("F4") : "null";
                string ovrRigPos   = _ovrRig   != null ? _ovrRig.transform.position.ToString("F4") : "null";
                string rAnchorPos  = _rightAnchor != null ? _rightAnchor.position.ToString("F4") : "null";
                Debug.Log($"[XFORM-AUDIT] frame={Time.frameCount}" +
                          $"\n  _rightValid={_rightValid}" +
                          $"\n  _rightPos={_rightPos:F4}  _rightRot={_rightRot.eulerAngles:F2}" +
                          $"\n  fp={fp:F4}  fr={fr.eulerAngles:F2}" +
                          $"\n  _paddleRb.pos={_paddleRb.position:F4}  _paddleRb.rot={_paddleRb.rotation.eulerAngles:F2}" +
                          $"\n  paddle.tr.pos={paddle.transform.position:F4}" +
                          $"\n  paddleBox.tr.pos={pbPos}  TrPt(c)={pbTpCenter}" +
                          $"\n  OVRCameraRig={ovrRigPos}  rightAnchor={rAnchorPos}" +
                          $"\n  posOffset={paddlePositionOffset:F4}  rotOffset={paddleRotationOffset:F2}");
            }
#endif

            // MovePosition/MoveRotation on a kinematic Rigidbody: the physics
            // engine sweeps the collider from old→new position every step.
            _prevPaddlePos      = _paddleRb.position;   // record BEFORE MovePosition
            _prevPaddleRot      = _paddleRb.rotation;   // record BEFORE MoveRotation
            _currentPaddlePos   = fp;
            _currentPaddleRot   = fr;
            _prevPaddlePosValid = true;
            _paddleRb.MovePosition(fp);
            _paddleRb.MoveRotation(fr);

            // ── Linear velocity — per-physics-step position delta (primary) ─────
            // Primary: fp − _prevWorldPos divided by fixedDeltaTime.
            // _prevWorldPos is updated every FixedUpdate, so this delta reflects the
            // true per-step displacement — it is never stale.
            //
            // Fallback (tracking-dropout recovery only): _cachedOVRVel.
            // Used only when the position delta is invalid — either the previous
            // step lost tracking (_prevRightValid=false) or the step distance
            // exceeds MAX_STEP_M (implausible jump, would produce a ghost hit).
            //
            // OVRInput.GetLocalControllerVelocity() is intentionally NOT called here.
            // It was read in Update() (where OVRInput is fresh), converted to world
            // space, and stored in _cachedOVRVel / _cachedOVRFrame.  Calling it in
            // FixedUpdate would read a stale duplicate on the second physics step
            // within a frame (120 Hz physics / 72 Hz display).
            Vector3 posDelta = fp - _prevWorldPos;
            const float MAX_STEP_M = 0.2f;   // 0.2 m / (1/120 s) = 24 m/s — generous cap
            bool deltaValid = _prevRightValid && posDelta.magnitude <= MAX_STEP_M;
            Vector3 deltaVel = deltaValid
                ? posDelta / Mathf.Max(Time.fixedDeltaTime, 0.0001f)
                : Vector3.zero;
            _paddleVelocity = deltaValid
                ? deltaVel
                : (_cachedOVRVel.sqrMagnitude > 0.0001f ? _cachedOVRVel : Vector3.zero);
            _prevWorldPos   = fp;
            _prevRightValid = true;

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            // ── VEL-PIPE diagnostic — rate-limited, every 60 physics steps ────
            if (Time.frameCount % 60 == 0)
            {
                string src = deltaValid ? "delta" : (_cachedOVRVel.sqrMagnitude > 0.0001f ? "ovrCache" : "zero");
                Debug.Log($"[VEL-PIPE] deltaVel={deltaVel:F3} ({deltaVel.magnitude:F3}m/s)  " +
                          $"ovrCached={_cachedOVRVel:F3} ({_cachedOVRVel.magnitude:F3}m/s)  " +
                          $"ovrFresh={_cachedOVRFrame == Time.frameCount}  " +
                          $"src={src}  selected={_paddleVelocity.magnitude:F3}m/s  " +
                          $"frame={Time.frameCount}");
            }
#endif

            // World-space angular velocity of the right controller (wrist rotation).
            // PRIMARY: delta-rotation method — always reliable.
            //   OVRInput.GetLocalControllerAngularVelocity() returns zero on Quest 3
            //   in this SDK version (confirmed by ANGULAR-SWEEP log: deltaRotDeg=18°
            //   per step while API returns 0 rad/s).  Compute from rotation delta instead:
            //     ω = axis * (angleDeg * Deg2Rad / fixedDeltaTime)
            //   _prevPaddleRot is recorded before MoveRotation, _paddleRb.rotation is
            //   updated immediately after — the difference is exactly this step's rotation.
            {
                // KEY: use `fr` (the target rotation just passed to MoveRotation), NOT
                // _paddleRb.rotation.  For kinematic bodies, MoveRotation() does NOT
                // update Rigidbody.rotation within the same FixedUpdate call — the
                // property still returns the pre-move value until the next physics step.
                // So _paddleRb.rotation × Inverse(_prevPaddleRot) = oldRot × oldRot⁻¹
                // = identity → dDeg = 0 → zero angular velocity every frame.
                // `fr` is the authoritative current-frame rotation: always correct.
                Quaternion dRot = fr * Quaternion.Inverse(_prevPaddleRot);
                dRot.ToAngleAxis(out float dDeg, out Vector3 dAxis);
                // ToAngleAxis can return NaN axis when angle≈0 or quaternion is identity.
                bool axisValid = dAxis.sqrMagnitude > 0.0001f
                              && !float.IsNaN(dAxis.x) && !float.IsNaN(dAxis.y) && !float.IsNaN(dAxis.z);
                _paddleAngularVelocity = axisValid
                    ? dAxis.normalized * (dDeg * Mathf.Deg2Rad / Mathf.Max(Time.fixedDeltaTime, 0.0001f))
                    : Vector3.zero;
            }
            // Spike filter: clamp to ANG_VEL_MAX (40 rad/s).
            // Tracking dropouts produce single-frame rotation snaps that compute as
            // 20–30+ rad/s.  Any real wrist flick peaks well below 40 rad/s.
            // NOTE: OVRInput.GetLocalControllerAngularVelocity() returns zero on Quest 3
            // in this SDK version — the OVR fallback branch has been removed as dead code.
            _paddleAngularVelocity = Vector3.ClampMagnitude(_paddleAngularVelocity, ANG_VEL_MAX);

            // ── Multi-frame angular spike guard ───────────────────────────────
            // The hard cap above kills extreme spikes (> 40 rad/s).
            // This catches medium spikes (5–15 rad/s) that occur when Quest 3
            // tracking glitches produce a single-step rotation snap.
            // A genuine fast wrist flick builds over consecutive steps and will
            // NOT exceed ANG_SPIKE_RATIO × the previous smoothed magnitude.
            // A glitch spike is isolated to one step and will.
            {
                float rawAngMag  = _paddleAngularVelocity.magnitude;
                float prevSmooth = _smoothPaddleAngVel.magnitude;
                if (prevSmooth > 0.5f && rawAngMag > prevSmooth * ANG_SPIKE_RATIO)
                {
                    _paddleAngularVelocity = rawAngMag > 1e-6f
                        ? _paddleAngularVelocity.normalized * prevSmooth
                        : Vector3.zero;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                    Debug.Log($"[VEL-SMOOTH] ANG_SPIKE clamped" +
                              $"  raw={rawAngMag:F2} → {prevSmooth:F2} rad/s" +
                              $"  frame={Time.frameCount}");
#endif
                }
            }

            // ── EMA smoothing ─────────────────────────────────────────────────
            // Replaces both velocity fields with their exponential moving average.
            // All downstream code (ApplyPaddleHit, swing guard, ANGULAR-SWEEP,
            // HIT-DIAG) automatically uses the smoothed values — no changes needed
            // elsewhere.
            // Alphas are now inspector-tunable (velEmaAlpha / angEmaAlpha).
            // Defaults: 0.35 linear (≈22 ms at 120 Hz), 0.25 angular (≈30 ms).
            _smoothPaddleVel    = Vector3.Lerp(_smoothPaddleVel,    _paddleVelocity,        velEmaAlpha);
            _smoothPaddleAngVel = Vector3.Lerp(_smoothPaddleAngVel, _paddleAngularVelocity, angEmaAlpha);
            _paddleVelocity        = _smoothPaddleVel;
            _paddleAngularVelocity = _smoothPaddleAngVel;

            // ── NaN safety guard ──────────────────────────────────────────────
            // A corrupted XR quaternion in the ToAngleAxis path can produce NaN.
            // EMA would then propagate NaN indefinitely.  Detect and clear both
            // fields so ApplyPaddleHit always receives finite values.
            if (float.IsNaN(_paddleVelocity.x)        || float.IsNaN(_paddleVelocity.y)        || float.IsNaN(_paddleVelocity.z) ||
                float.IsNaN(_paddleAngularVelocity.x)  || float.IsNaN(_paddleAngularVelocity.y)  || float.IsNaN(_paddleAngularVelocity.z))
            {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                Debug.LogWarning($"[FREEZE-DIAG] NaN in velocity fields — cleared." +
                                 $"  paddleVel={_paddleVelocity}  paddleAngVel={_paddleAngularVelocity}" +
                                 $"  frame={Time.frameCount}");
#endif
                _paddleVelocity        = Vector3.zero;
                _paddleAngularVelocity = Vector3.zero;
                _smoothPaddleVel       = Vector3.zero;
                _smoothPaddleAngVel    = Vector3.zero;
            }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            // ── PADDLE-FREEZE diagnostics (dev/editor only) ──────────────────
            {
                float pfLinSpd  = _paddleVelocity.magnitude;
                float pfAngSpd  = _paddleAngularVelocity.magnitude;
                float pfBallSpd = (_ballRb != null && !_ballRb.isKinematic)
                                  ? _ballRb.linearVelocity.magnitude : 0f;

                float pfSurfSpd = 0f;
                if (_ballRb != null && paddleBox != null)
                {
                    Vector3 pfBoxCtr = paddleBox.transform.TransformPoint(paddleBox.center);
                    pfSurfSpd = (_paddleVelocity
                                 + Vector3.Cross(_paddleAngularVelocity, _ballRb.position - pfBoxCtr)).magnitude;
                }

                float pfPosErr      = (_paddleRb.position - _diagExpectedPaddlePos).magnitude;
                float pfRotErr      = Quaternion.Angle(_paddleRb.rotation, _diagExpectedPaddleRot);
                float pfRbTrPosDiff = (_paddleRb.position - paddle.transform.position).magnitude;
                float pfRbTrRotDiff = Quaternion.Angle(_paddleRb.rotation, paddle.transform.rotation);
                float pfBallPaddleDist = (_ballRb != null)
                    ? (_ballRb.position - _paddleRb.position).magnitude : float.MaxValue;

                bool pfVelFreeze = _diagPrevLinVelMag > 1.0f && pfLinSpd < 0.3f && pfBallPaddleDist < 0.4f;
                bool pfAngSpike  = pfAngSpd > 20f;

                // NonAlloc OverlapBox — no per-frame Collider[] allocation.
                int     pfOverlapCount = 0;
                bool    pfPenHit       = false;
                float   pfPenDepth     = 0f;
                Vector3 pfPenDir       = Vector3.zero;
                if (paddleBox != null)
                {
                    Vector3 pfBoxCtr2 = paddleBox.transform.TransformPoint(paddleBox.center);
                    pfOverlapCount = Physics.OverlapBoxNonAlloc(
                        pfBoxCtr2, paddleBox.size * 0.5f, _overlapBuffer,
                        _paddleRb.rotation, ~0, QueryTriggerInteraction.Ignore);
                    if (_ballCollider != null && _ballRb != null)
                    {
                        try
                        {
                            pfPenHit = Physics.ComputePenetration(
                                _ballCollider, _ballRb.position, Quaternion.identity,
                                paddleBox, paddleBox.transform.position, paddleBox.transform.rotation,
                                out pfPenDir, out pfPenDepth);
                        }
                        catch { }
                    }
                }

                bool pfHighSpeed   = pfSurfSpd > 5f || (pfBallSpd > 6f && pfBallPaddleDist < 0.4f);
                bool pfPosStall    = pfPosErr > 0.005f;
                bool pfRotStall    = pfRotErr > 5f;
                bool pfDeepPen     = pfPenHit && pfPenDepth > 0.01f;
                bool pfRbTrDiverge = pfRbTrRotDiff > 5f || pfRbTrPosDiff > 0.002f;
                bool pfShouldLog   = pfVelFreeze || pfAngSpike || pfPosStall || pfRotStall
                                   || pfDeepPen  || pfRbTrDiverge || pfHighSpeed;

                if (pfShouldLog)
                {
                    string pfReason = "";
                    if (pfVelFreeze)   pfReason += "VELOCITY_FREEZE ";
                    if (pfAngSpike)    pfReason += "ANG_SPIKE ";
                    if (pfPosStall)    pfReason += "POS_STALL ";
                    if (pfRotStall)    pfReason += "ROT_STALL ";
                    if (pfDeepPen)     pfReason += "DEEP_PEN ";
                    if (pfRbTrDiverge) pfReason += "RBTRANSFORM_DIVERGE ";
                    if (pfHighSpeed)   pfReason += "HIGH_SPEED ";
                    Debug.Log($"[PADDLE-FREEZE] {pfReason}" +
                              $"\n  frame={Time.frameCount}  fixedTime={Time.fixedTime:F4}" +
                              $"\n  targetPos={fp:F4}  targetRot={fr.eulerAngles:F2}" +
                              $"\n  rbPos={_paddleRb.position:F4}  rbRot={_paddleRb.rotation.eulerAngles:F2}" +
                              $"\n  posErr={pfPosErr:F5} m  rotErr={pfRotErr:F2}°  rbTrPos={pfRbTrPosDiff:F5}  rbTrRot={pfRbTrRotDiff:F2}°" +
                              $"\n  linVel={_paddleVelocity:F3} ({pfLinSpd:F3} m/s)  angVel={_paddleAngularVelocity:F3} ({pfAngSpd:F3} r/s)  surf={pfSurfSpd:F3}" +
                              $"\n  ballSpd={pfBallSpd:F3}  dist={pfBallPaddleDist:F3}  overlapCount={pfOverlapCount}  penHit={pfPenHit}  penDepth={pfPenDepth:F5}");
                }

                _diagPrevLinVelMag     = pfLinSpd;
                _diagPrevAngVelMag     = pfAngSpd;
                _diagExpectedPaddlePos = fp;
                _diagExpectedPaddleRot = fr;
            }
            // ── END PADDLE-FREEZE diagnostics ────────────────────────────────
#endif
        }
        else
        {
            _prevRightValid = false;
        }

        // ── Hit detection ─────────────────────────────────────────────────────
        _diagHitDetStart = Time.realtimeSinceStartup;
        if (ball == null || _ballRb == null)
        {
            _prevBallPos = Vector3.zero;
            return;
        }
        // ── Hit-window diagnostic (once per second, fires BEFORE all gates) ─────
        if (Time.time > _hitDiagLastLog + 1f)
        {
            _hitDiagLastLog = Time.time;
            Debug.Log($"[HIT-DIAG] trackingReady={_trackingReady} rightValid={_rightValid} " +
                      $"sessionActive={ExperimentLogger.SessionActive} " +
                      $"hitWindowOpen={_hitWindowOpen} paddleBox={(paddleBox != null ? "ok" : "NULL")} " +
                      $"separated={_ballSeparated} kinematic={_ballRb.isKinematic} " +
                      $"ballY={_ballRb.position.y:F2}");
        }

        if (!_trackingReady || !_rightValid)
        {
            _prevBallPos = _ballRb.position;
            return;
        }
        if ((!jugglingMode && (!ExperimentLogger.SessionActive || !_hitWindowOpen)) || paddleBox == null)
        {
            _prevBallPos = _ballRb.position;
            return;
        }

        // ── Paddle-to-table proximity and contact tracking ────────────────────
        // Recompute table top Y if it's still at default (handles late scene load).
        if (_tableTopY < 0.1f || _tableTopY > 2.5f)
        {
            GameObject psGo = GameObject.Find("playerside");
            if (psGo != null) { Collider c = psGo.GetComponent<Collider>();
                if (c != null) _tableTopY = c.bounds.max.y; }
        }

        if (paddle != null && paddleBox != null && ExperimentLogger.SessionActive)
        {
            // Approximate paddle bottom: Rigidbody centre − half collider height
            // (in world space, accounting for scale and rotation's Y contribution).
            float paddleHalfH   = paddleBox.size.y * 0.5f
                                * Mathf.Abs(paddle.transform.lossyScale.y);
            float paddleBottomY = _paddleRb.position.y - paddleHalfH;
            float distToTable   = paddleBottomY - _tableTopY;   // + = above, - = below

            // ── Track minimum paddle-to-table distance this serve attempt ─────
            if (distToTable < _minPaddleTableDistThisServe)
                _minPaddleTableDistThisServe = distToTable;

            // ── PaddleTableContact: edge-detect — fires on entering edge ─────────
            // Proximity buffer of 0.015 m (1.5 cm): fires when paddle bottom is
            // within 1.5 cm of the table surface (above OR below) so that a light
            // touch triggers haptics without requiring hard pressing.
            // penetration is 0 when paddle is still above but within buffer,
            // and positive when it has crossed below the surface.
            const float PADDLE_TABLE_PROXIMITY = 0.015f;
            bool nowNearOrBelow = distToTable < PADDLE_TABLE_PROXIMITY;
            if (nowNearOrBelow && _paddleWasAboveTable)
            {
                float penetration = Mathf.Max(0f, -distToTable);
                ExperimentLogger.Instance?.LogPaddleTableContact(penetration);
                OnPaddleTableContact?.Invoke(penetration);
                Debug.Log(
                    $"[PADDLE-TABLE] detected" +
                    $" depth={penetration:F4}" +
                    $" distToTable={distToTable:F4}" +
                    $" eventInvoked=true" +
                    $" tableTopY={_tableTopY:F4}" +
                    $" paddleBottomY={paddleBottomY:F4}" +
                    $" sessionActive={ExperimentLogger.SessionActive}" +
                    $" group={ExperimentLogger.GroupNumber}"
                );
            }
            _paddleWasAboveTable = !nowNearOrBelow;
        }

        // ── Belt-and-suspenders: re-enforce CCD settings every frame ─────────────
        // Unity silently resets collisionDetectionMode to Discrete whenever
        // isKinematic changes.  Restoring here ensures both objects stay in the
        // correct CCD mode regardless of what other code touched the Rigidbody.
        if (!_ballRb.isKinematic &&
            _ballRb.collisionDetectionMode != CollisionDetectionMode.ContinuousDynamic)
        {
            _ballRb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        }
        if (_paddleRb != null &&
            _paddleRb.collisionDetectionMode != CollisionDetectionMode.ContinuousSpeculative)
        {
            _paddleRb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
        }

        Vector3 ballPos = _ballRb.position;

        // ── Reset per-step diagnostic state ───────────────────────────────────
        _diagL0State = _diagL1State = _diagL2State = _diagL3State = _diagL4State = 0;
        _diagApplyHitCalledThisStep = false;
        _diagSwingGuardRejected     = false;

        // ── Unified paddle motion model ───────────────────────────────────────
        // Computed ONCE here.  Every geometry finder and ApplyPaddleHit receive
        // paddleBoxCenter directly — no layer re-derives it from Rigidbody pose.
        //
        //   paddleBoxCenter   — world-space centre of the paddle BoxCollider
        //   surfaceVelAt(pt)  — _paddleVelocity + ω × (pt − paddleBoxCenter)
        //
        // _paddleAngularVelocity was already clamped to ANG_VEL_MAX above.
        // TransformPoint(paddleBox.center) is child-safe: correctly handles
        // paddleBox on a child GameObject (unlike _paddleRb.position + rotation * center
        // which ignores the child's world offset).
        Vector3 paddleBoxCenter = paddleBox.transform.TransformPoint(paddleBox.center);

        // ── Ball-to-surface distance — used by separation update below ─────────
        // ClosestPoint returns the ball centre when ball is fully inside (dist=0).
        Vector3 closest = paddleBox.ClosestPoint(ballPos);
        float   dist    = Vector3.Distance(closest, ballPos);

        // ── Session gate ───────────────────────────────────────────────────────
        bool hitOpen = Time.time > _lastHitTime + HIT_COOLDOWN && _ballSeparated;

        // ────────────────────────────────────────────────────────────────────────
        // HIT DETECTION — two-path architecture
        //
        // PRIMARY : OnPaddlePhysicsHit (Layer 4) — physics engine collision callback.
        //           Fires on contact INITIATION via ContinuousSpeculative sweep.
        //           Provides authoritative contact point and normal from the solver.
        //           Runs AFTER FixedUpdate in the same physics step.
        //
        // FALLBACK: ComputePenetration (below) — handles pre-existing overlaps only.
        //           If the ball already overlaps the paddle at the START of this step,
        //           no new contact event fires from ContinuousSpeculative.
        //           Physics.ComputePenetration returns the minimum-separation vector —
        //           the physics engine's own answer to "which direction is out?" —
        //           used directly as the contact normal (no geometry heuristics).
        //
        // All previous geometry finders (OverlapBox, Proximity, SphereCast, BoxCastAll)
        // have been removed.  They reconstructed contact normals from ClosestPoint
        // geometry — an approximation that produced wrong-face snaps on edge/rim
        // contacts and unstable results when the ball was inside the paddle volume.
        // ────────────────────────────────────────────────────────────────────────

        // ── ComputePenetration fallback — pre-existing ball-in-paddle overlap ───
        if (hitOpen && _ballCollider != null && !_ballRb.isKinematic)
        {
            _diagL0State = 1; // open — checking overlap
            Vector3 cpSepDir; float cpSepDist;
            // Use paddleBox.transform.position/rotation — correct when paddleBox is
            // on a child GameObject.  _paddleRb.position ignores child offsets.
            bool overlapping = Physics.ComputePenetration(
                _ballCollider, ballPos,                          Quaternion.identity,
                paddleBox,     paddleBox.transform.position,     paddleBox.transform.rotation,
                out cpSepDir,  out cpSepDist);
            if (overlapping && cpSepDir.sqrMagnitude > 0.5f)
            {
                cpSepDir = cpSepDir.normalized;
                // Face restriction: accept only contacts within 60° of the playing face.
                // The rubber face axis = pForward after paddleRotationOffset is applied.
                // Abs-dot ≥ 0.5 accepts the full face including closed topspin angles;
                // rejects handle, rim, and rear-face contacts.
                Vector3 pFwd      = _paddleRb.rotation * Vector3.forward;
                float   faceAlign = Mathf.Abs(Vector3.Dot(cpSepDir, pFwd));
                if (faceAlign >= 0.5f)
                {
                    Vector3 cpContactPt = ballPos - cpSepDir * cpSepDist;
                    Vector3 cpSurfVel   = _paddleVelocity
                                        + Vector3.Cross(_paddleAngularVelocity,
                                                        cpContactPt - paddleBoxCenter);
                    if (cpSurfVel.magnitude >= SWING_MIN)
                    {
                        _diagL0State   = 2;
                        _diagLayer     = "Layer_ComputePen";
                        _diagSweepDist = cpSepDist;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                        Debug.DrawRay(ballPos,     cpSepDir * (cpSepDist + 0.06f), new Color(1f, 0.5f, 0f), 2f);
                        Debug.DrawRay(cpContactPt, cpSepDir * 0.12f,               new Color(1f, 0.5f, 0f), 2f);
                        Debug.Log($"[HIT-SOURCE] path=Layer_ComputePen  " +
                                  $"faceAlign={faceAlign:F3}  sepDist={cpSepDist:F4}  " +
                                  $"contactPt={cpContactPt:F3}  normal={cpSepDir:F3}  " +
                                  $"ballVelBefore={(_ballRb != null ? _ballRb.linearVelocity.ToString("F3") : "null")}  " +
                                  $"ballSpd={(_ballRb != null ? _ballRb.linearVelocity.magnitude : 0f):F3}m/s  " +
                                  $"paddleVel={_paddleVelocity.magnitude:F3}m/s  frame={Time.frameCount}");
#endif
                        Vector3 cpFaceNormal = SnapToPaddleFaceNormal(cpSepDir, pFwd, "Layer_ComputePen");
                        ApplyPaddleHit(cpContactPt, cpFaceNormal, paddleBoxCenter);
                    }
                    else
                    {
                        _diagSwingGuardRejected = true;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                        Debug.Log($"[HIT-DIAG] SWING_GUARD_REJECT layer=ComputePen" +
                                  $"  surfSpd={cpSurfVel.magnitude:F3} < SWING_MIN={SWING_MIN}" +
                                  $"  faceAlign={faceAlign:F3}  frame={Time.frameCount}");
#endif
                    }
                }
                else
                {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                    Debug.Log($"[HIT-DIAG] FACE_REJECT layer=ComputePen" +
                              $"  faceAlign={faceAlign:F3}  sepDir={cpSepDir:F3}" +
                              $"  frame={Time.frameCount}");
#endif
                }
            }
        }

        // ── Separation update ─────────────────────────────────────────────────
        // Once the ball moves > SEPARATION_DIST from the paddle surface after a
        // hit, unlock the next contact session.  dist was computed above.
        if (!_ballSeparated && dist > SEPARATION_DIST)
            _ballSeparated = true;

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        // ── Pass-through detection ────────────────────────────────────────────
        if (_ballRb != null && !_ballRb.isKinematic && paddleBox != null && _prevPaddlePosValid)
        {
            Vector3 faceNormal  = _paddleRb.rotation * Vector3.forward;
            Vector3 paddleCtr   = paddleBox.transform.TransformPoint(paddleBox.center);
            float   ballFaceSgn = Vector3.Dot(ballPos - paddleCtr, faceNormal);
            float   ptDistToSurf = Vector3.Distance(paddleBox.ClosestPoint(ballPos), ballPos);
            bool hitThisStep = (_lastHitFrame == Time.frameCount);
            if (!hitThisStep && _diagPrevBallFaceSign != 0f
                && Mathf.Sign(ballFaceSgn) != Mathf.Sign(_diagPrevBallFaceSign)
                && ptDistToSurf < 0.05f)
            {
                Debug.Log($"[HIT-DIAG][PASS-THROUGH] Ball crossed paddle face without a hit!" +
                          $"  prevSign={_diagPrevBallFaceSign:F4}  curSign={ballFaceSgn:F4}" +
                          $"  distToSurf={ptDistToSurf:F4}" +
                          $"  ballSpd={_ballRb.linearVelocity.magnitude:F3}" +
                          $"  surf={(_paddleVelocity + Vector3.Cross(_paddleAngularVelocity, ballPos - paddleCtr)).magnitude:F3}" +
                          $"  frame={Time.frameCount}");
            }
            _diagPrevBallFaceSign = ballFaceSgn;
        }

        // ── Rb / Transform rotation divergence warning ────────────────────────
        if (_paddleRb != null && paddle != null)
        {
            float rotDiv = Quaternion.Angle(_paddleRb.rotation, paddle.transform.rotation);
            if (rotDiv > 5f)
                Debug.Log($"[HIT-DIAG][ROT-DIVERGENCE] {rotDiv:F2}°  " +
                          $"rb={_paddleRb.rotation.eulerAngles:F1}  tr={paddle.transform.rotation.eulerAngles:F1}");
        }
        // ── END pass-through / divergence checks ─────────────────────────────
#endif

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        // ── ANGULAR_SWEEP diagnostic ──────────────────────────────────────────
        if (_ballRb != null && !_ballRb.isKinematic && paddleBox != null && _rightValid)
        {
            Vector3 asFwd = _paddleRb.rotation * Vector3.forward;
            Vector3 asCtr = paddleBox.transform.TransformPoint(paddleBox.center);

            float asCurrFaceDot  = Vector3.Dot(ballPos - asCtr, asFwd);
            float asPrevFaceDot  = _diagPrevFaceDotAS;
            bool  asCrossedPlane = (_diagPrevFaceDotAS != 0f)
                && (Mathf.Sign(asCurrFaceDot) != Mathf.Sign(asPrevFaceDot));

            float asDeltaRotDeg = Quaternion.Angle(_diagPrevPaddleRotAS, _paddleRb.rotation);
            float asAngVelMag   = _paddleAngularVelocity.magnitude;

            // Max corner velocity — uses pre-allocated _cornersCache (no per-step array alloc).
            float   asMaxCornerSpd = 0f;
            Vector3 asMaxCornerVel = Vector3.zero;
            {
                Vector3 hs = Vector3.Scale(paddleBox.size * 0.5f,
                                 new Vector3(
                                     Mathf.Abs(paddleBox.transform.lossyScale.x),
                                     Mathf.Abs(paddleBox.transform.lossyScale.y),
                                     Mathf.Abs(paddleBox.transform.lossyScale.z)));
                _cornersCache[0] = new Vector3(+hs.x, +hs.y, +hs.z);
                _cornersCache[1] = new Vector3(+hs.x, +hs.y, -hs.z);
                _cornersCache[2] = new Vector3(+hs.x, -hs.y, +hs.z);
                _cornersCache[3] = new Vector3(+hs.x, -hs.y, -hs.z);
                _cornersCache[4] = new Vector3(-hs.x, +hs.y, +hs.z);
                _cornersCache[5] = new Vector3(-hs.x, +hs.y, -hs.z);
                _cornersCache[6] = new Vector3(-hs.x, -hs.y, +hs.z);
                _cornersCache[7] = new Vector3(-hs.x, -hs.y, -hs.z);
                foreach (var c in _cornersCache)
                {
                    Vector3 wc   = asCtr + _paddleRb.rotation * c;
                    Vector3 cVel = _paddleVelocity + Vector3.Cross(_paddleAngularVelocity, wc - asCtr);
                    float   cSpd = cVel.magnitude;
                    if (cSpd > asMaxCornerSpd) { asMaxCornerSpd = cSpd; asMaxCornerVel = cVel; }
                }
            }

            float   tipHalfY  = paddleBox.size.y * 0.5f * Mathf.Abs(paddleBox.transform.lossyScale.y);
            Vector3 tipOffset = _paddleRb.rotation * new Vector3(0f, tipHalfY, 0f);
            Vector3 asTipVel  = _paddleVelocity + Vector3.Cross(_paddleAngularVelocity, tipOffset);

            float asFwdDeltaDeg = _diagPrevPaddleFwdAS == Vector3.zero
                ? 0f : Vector3.Angle(_diagPrevPaddleFwdAS, asFwd);

            float asDistToSurf = Vector3.Distance(paddleBox.ClosestPoint(ballPos), ballPos);

            const float AS_ANG_VEL_THRESH  = 3.0f;
            const float AS_EDGE_SPD_THRESH = 1.5f;
            const float AS_PROX_NEAR       = 0.05f;
            const float AS_PROX_SWING      = 0.30f;
            bool asFire =
                (asAngVelMag    > AS_ANG_VEL_THRESH  && asDistToSurf < AS_PROX_SWING) ||
                (asMaxCornerSpd > AS_EDGE_SPD_THRESH  && asDistToSurf < AS_PROX_SWING) ||
                (asCrossedPlane && !_diagApplyHitCalledThisStep && asDistToSurf < AS_PROX_NEAR);

            if (asFire)
            {
                string asL0 = _diagL0State == 2 ? "FIRED" : _diagL0State == 1 ? "OPEN_NO_HIT" : "LOCKED";
                string asL1 = _diagL1State == 2 ? "FIRED" : _diagL1State == 1 ? "OPEN_NO_HIT" : "LOCKED";
                string asL2 = _diagL2State == 2 ? "FIRED" : _diagL2State == 1 ? "OPEN_NO_HIT" : "LOCKED";
                string asL3 = _diagL3State == 2 ? "FIRED" : _diagL3State == 1 ? "OPEN_NO_HIT" : "LOCKED";
                string asL4 = _diagL4State == 2 ? "FIRED" : _diagL4State == 1 ? "OPEN_NO_HIT" : "LOCKED";
                string asBlocked = "";
                if (!_diagApplyHitCalledThisStep)
                {
                    if (_diagSwingGuardRejected)
                        asBlocked = "SWING_GUARD_REJECTED";
                    else
                    {
                        bool anyOpen = _diagL0State >= 1 || _diagL1State >= 1
                                    || _diagL2State >= 1 || _diagL3State >= 1 || _diagL4State >= 1;
                        asBlocked = anyOpen ? "OPEN_BUT_GEOMETRY_MISS" : "ALL_LOCKED(cooldown_or_sep)";
                    }
                }
                Debug.Log(
                    "[ANGULAR-SWEEP] " +
                    $"frame={Time.frameCount}  fixedTime={Time.fixedTime:F4}\n" +
                    $"BALL  prevPos={_prevBallPos:F4}  currPos={ballPos:F4}  vel={_ballRb.linearVelocity:F4} ({_ballRb.linearVelocity.magnitude:F3} m/s)\n" +
                    $"PADDLE  prevRot={_diagPrevPaddleRotAS.eulerAngles:F2}  currRot={_paddleRb.rotation.eulerAngles:F2}  dRot={asDeltaRotDeg:F2}  angVel={asAngVelMag:F3} r/s\n" +
                    $"EDGE  tip={asTipVel.magnitude:F3}  max={asMaxCornerSpd:F3} m/s  fwdDelta={asFwdDeltaDeg:F2}°\n" +
                    $"PLANE  prevDot={asPrevFaceDot:F4}  currDot={asCurrFaceDot:F4}  crossed={asCrossedPlane}  dist={asDistToSurf:F4}\n" +
                    $"LAYERS  L0={asL0}  L1={asL1}  L2={asL2}  L3={asL3}  L4={asL4}\n" +
                    $"ARBIT  called={_diagApplyHitCalledThisStep}  blocked={asBlocked}\n" +
                    $"NORMAL  raw={_diagLastRawNormal:F4}  snapped={_diagLastSnappedNormal:F4}  flipped={_diagLastNormalFlipped}\n" +
                    $"FINAL  assigned={_diagLastAssignedVel:F4}  ballVel={_diagLastFinalBallVel:F4}"
                );
            }

            _diagPrevPaddleRotAS = _paddleRb.rotation;
            _diagPrevPaddleFwdAS = asFwd;
            _diagPrevFaceDotAS   = asCurrFaceDot;
        }
        // ── END ANGULAR_SWEEP ─────────────────────────────────────────────────
#endif

        _prevBallPos      = ballPos;
        _prevBallPosValid = !_ballRb.isKinematic;

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        // ── FixedUpdate / hit-detection timing ───────────────────────────────
        float diagHitDetMs     = (Time.realtimeSinceStartup - _diagHitDetStart)     * 1000f;
        float diagFixedTotalMs = (Time.realtimeSinceStartup - _diagFixedUpdateStart) * 1000f;
        if (diagFixedTotalMs > 5f || diagHitDetMs > 3f)
            Debug.Log($"[PADDLE-FREEZE][TIMING] FixedUpdate total={diagFixedTotalMs:F3} ms  " +
                      $"hitDetection={diagHitDetMs:F3} ms  frame={Time.frameCount}");
#endif
    }

    // Called by PaddleHitForwarder when the physics engine detects a collision
    // between the paddle and ball via ContinuousSpeculative sweep.
    // This is Layer 4 — catches any fast swing that slips past all three FixedUpdate layers.
    public void OnPaddlePhysicsHit(Collision col)
    {
        if (!jugglingMode && (!_hitWindowOpen || !ExperimentLogger.SessionActive)) return;
        if (ball == null || col.gameObject != ball) return;
        if (_ballRb == null || Time.time < _lastHitTime + HIT_COOLDOWN) return;
        if (!_ballSeparated) return;
        // Frame guard: OnCollisionEnter fires AFTER FixedUpdate in the same physics
        // step — Time.time is identical so the time-based cooldown cannot block a
        // same-step double-fire.  The frame check closes this gap.
        if (Time.frameCount == _lastHitFrame) return;
        if (col.contactCount == 0) return;

        // ── Physics-engine contact point and normal — primary authority ───────
        // col.GetContact(0).normal points FROM the paddle surface TOWARD the ball
        // centre — the correct outward normal for our reflection formula.
        // This is computed by the ContinuousSpeculative solver and is always more
        // reliable than any manual ClosestPoint / geometry heuristic.
        ContactPoint cp            = col.GetContact(0);
        Vector3      contactPt     = cp.point;
        Vector3      contactNormal = cp.normal.sqrMagnitude > 0.5f
                                     ? cp.normal.normalized
                                     : Vector3.zero;
        if (contactNormal == Vector3.zero)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            Debug.Log($"[gameplay] Layer4 — zero/degenerate contact normal from physics engine, skipping.  frame={Time.frameCount}");
#endif
            return;
        }

        // ── Face restriction ───────────────────────────────────────────────────
        Vector3 pFwd      = _paddleRb.rotation * Vector3.forward;
        float   faceAlign = Mathf.Abs(Vector3.Dot(contactNormal, pFwd));
        if (faceAlign < 0.5f)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            Debug.Log($"[gameplay] Layer4 face-reject — faceAlign={faceAlign:F3}  " +
                      $"normal={contactNormal:F3}  pFwd={pFwd:F3}  frame={Time.frameCount}");
#endif
            return;
        }

        // ── Swing guard ────────────────────────────────────────────────────────
        Vector3 l4BoxCenter  = paddleBox != null
                             ? paddleBox.transform.TransformPoint(paddleBox.center)
                             : _paddleRb.position;
        Vector3 l4SurfaceVel = _paddleVelocity
                             + Vector3.Cross(_paddleAngularVelocity, contactPt - l4BoxCenter);
        if (l4SurfaceVel.magnitude < SWING_MIN)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            Debug.Log($"[gameplay] Layer4 ghost blocked — surfVel={l4SurfaceVel.magnitude:F3} < SWING_MIN={SWING_MIN}");
#endif
            return;
        }

        _diagLayer = "Layer4_Physics"; _diagSweepDist = 0f; _diagL4State = 2;
        Vector3 l4FaceNormal = SnapToPaddleFaceNormal(contactNormal, pFwd, "Layer4_Physics");
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        Debug.DrawRay(contactPt, contactNormal * 0.12f, Color.yellow,          2f);
        Debug.DrawRay(contactPt, l4SurfaceVel  * 0.04f, Color.white,           2f);
        Debug.DrawRay(contactPt, pFwd          * 0.08f, new Color(1f, 0f, 1f), 2f);
        Debug.Log($"[HIT-SOURCE] path=Layer4_Physics  " +
                  $"contactPt={contactPt:F3}  normal={contactNormal:F3}  " +
                  $"ballVelBefore={(_ballRb != null ? _ballRb.linearVelocity.ToString("F3") : "null")}  " +
                  $"ballSpd={(_ballRb != null ? _ballRb.linearVelocity.magnitude : 0f):F3}m/s  " +
                  $"paddleVel={_paddleVelocity.magnitude:F3}m/s  frame={Time.frameCount}");
#endif
        ApplyPaddleHit(contactPt, l4FaceNormal, l4BoxCenter);
    }

    // ── Face-normal snap ──────────────────────────────────────────────────────
    //
    // Called by both Layer_ComputePen and Layer4_Physics BEFORE ApplyPaddleHit.
    //
    // Problem: Physics.ComputePenetration returns the minimum-separation vector —
    // the shortest path OUT of the overlapping volume.  On fast swings the ball
    // can be partially inside the paddle's edge/corner, making the shortest exit
    // direction sideways or downward rather than face-outward.  Even the physics
    // solver's contact normal can deviate when ContinuousSpeculative registers a
    // speculative contact on the rim.  Using these raw normals as the reflection
    // axis sends the ball in the wrong direction and immediately triggers a table
    // bounce sound.
    //
    // Fix: once the face-alignment gate (Abs(Dot(rawNormal, pFwd)) >= 0.5f) has
    // confirmed the contact is on the rubber face region, snap the reflection axis
    // to the exact canonical face normal.
    //
    //   pFwd = _paddleRb.rotation * Vector3.forward  (Rigidbody-authoritative)
    //   Dot(rawNormal, pFwd) > 0  → ball came from front → use  +pFwd
    //   Dot(rawNormal, pFwd) < 0  → ball came from back  → use  -pFwd
    //
    // The contact POINT is intentionally unchanged — it is still correct for
    // leverArm and spin computation in ApplyPaddleHit.
    //
    private Vector3 SnapToPaddleFaceNormal(Vector3 rawNormal, Vector3 pFwd, string source)
    {
        float   dot        = Vector3.Dot(rawNormal, pFwd);
        Vector3 faceNormal = dot >= 0f ? pFwd : -pFwd;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        Debug.Log($"[FACE-NORMAL-FIX] source={source}  " +
                  $"rawNormal={rawNormal:F3}  usedNormal={faceNormal:F3}  " +
                  $"faceAlign={Mathf.Abs(dot):F3}  approach={(dot >= 0f ? "front" : "back")}  " +
                  $"frame={Time.frameCount}");
#endif
        return faceNormal;
    }

    // ── Paddle hit physics ────────────────────────────────────────────────────
    //
    //  contactPt     — physics contact point (from solver or ComputePenetration)
    //  contactNormal — face-snapped outward normal (see SnapToPaddleFaceNormal).
    //                  Always equals ±(_paddleRb.rotation * Vector3.forward).
    //  paddleBoxCenter — world-space centre of paddle BoxCollider (from caller)
    //
    //  Physics reflection:
    //    1. Reflect incoming ball velocity off contactNormal.
    //    2. Blend toward paddle face motion direction at high swing speeds.
    //    3. Speed = effectiveSurfaceSpeed * 1.8 + incomingSpeed * 0.2.
    //    4. Clamp launch angle ≤ 50° above horizontal.
    //    5. Spin from tangential brush velocity.
    //
    void ApplyPaddleHit(Vector3 contactPt, Vector3 contactNormal, Vector3 paddleBoxCenter)
    {
        _diagApplyHitStart          = Time.realtimeSinceStartup;
        _diagApplyHitCalledThisStep = true;

        // Record for ANGULAR-SWEEP diagnostic
        _diagLastRawNormal     = contactNormal;
        _diagLastSnappedNormal = contactNormal;
        _diagLastNormalFlipped = false;

        // ── Reset rally bounce counter on each paddle hit ─────────────────────
        // Bounce count since the last hit is logged in [PHYSICS-AUDIT] BEFORE the
        // reset so the log reflects how many table contacts preceded this hit.
        int hitBounceCount = _bounceCount;
        _bounceCount = 0;

        Vector3 ballVel = _ballRb.linearVelocity;

        // ── Effective contact-point velocity ──────────────────────────────────
        // v_contact = v_linear + ω × leverArm
        // Wrist rotation dominates topspin serves — angular contribution is
        // essential for Eleven-like feel.  Lever arm from box centre to contact point.
        Vector3 leverArm = contactPt - paddleBoxCenter;

        // ── Low-speed angular damping ─────────────────────────────────────────
        // At juggling speeds, XR tracking jitter in angular velocity injects
        // unrealistic energy into effectiveVel.  Ramp the angular contribution
        // smoothly to (1-lowSpeedAngularDamping) as linear speed approaches zero.
        // Proxy: _paddleVelocity.magnitude (EMA-smoothed, already finite).
        // lsBlend = 0 at zero speed → full damping; 1 at threshold → no damping.
        float lsLinearSpd = _paddleVelocity.magnitude;
        float lsBlend     = Mathf.SmoothStep(0f, 1f,
                                lsLinearSpd / Mathf.Max(0.01f, lowSpeedAssistThreshold));
        float effectiveAngularWeight = Mathf.Lerp(1f - lowSpeedAngularDamping, 1f, lsBlend);

        Vector3 effectiveVel = _paddleVelocity
                             + Vector3.Cross(_paddleAngularVelocity * effectiveAngularWeight, leverArm);
        float   effectiveSpd = effectiveVel.magnitude;

        // Step 1 — reflect incoming ball velocity off the contact surface.
        float   normalIn  = Mathf.Max(0f, Vector3.Dot(ballVel, -contactNormal));
        Vector3 reflected = ballVel + 2f * normalIn * contactNormal;

        // Step 2 — compute outgoing DIRECTION.
        // Blend the reflected direction toward the effective surface velocity direction
        // at higher swing speeds.  "Ball goes where the FACE moves" — correct for
        // topspin brushes and wrist flicks where effectiveVel >> arm translation alone.
        // At low speeds (gentle taps) the face normal still dominates.
        Vector3 swingDir  = effectiveSpd > 0.25f ? effectiveVel.normalized : contactNormal;
        // swingBlend cap raised 0.5→0.6 (calibration): paddle face direction contributes
        // up to 60% at full swing speed, improving directional predictability.
        float   swingBlend = Mathf.Clamp01((effectiveSpd - 0.25f) / 2.75f) * 0.6f;
        Vector3 outDir    = Vector3.Slerp(reflected.normalized, swingDir, swingBlend).normalized;

        // ── Speed — effective surface speed is the primary driver ─────────────
        // effectiveVel includes wrist rotation so a hard wrist flick now produces
        // the same speed response as an arm drive.  Incoming ball speed contributes
        // 20% — a genuine "loaded return" feel on fast incoming shots.
        //   gentle wrist tap  effectiveSpd≈0.5 → ≈0.9 m/s ball
        //   normal flick      effectiveSpd≈3.0 → ≈5.4 m/s ball
        //   hard serve flick  effectiveSpd≈7.0 → ≈12.6 → capped to 12 m/s
        // Calibration (raised from 1.8/0.20): EMA at 120 Hz attenuates fast wrist
        // snaps (peak 6 m/s, 30 ms) down to ~2 m/s measured; 2.0× compensates so
        // a real medium swing reliably clears the table.  0.25 incoming contribution
        // gives a more responsive "loaded return" feel on fast shots.
        float spd = effectiveSpd * 2.0f + normalIn * 0.25f;

        // Step 3 — clamp upward launch angle to ≤ 50° above horizontal.
        // Use a normalized directional clamp (not a raw Y chop) so the direction
        // vector stays valid.  sin(50°) ≈ 0.766.
        // 50° allows steep topspin trajectories (vs old 35° which was too aggressive).
        // This replaces the old "if (finalVel.y > 3.0f) finalVel.y = 3.0f" which
        // silently broke the direction on fast upward shots.
        const float MAX_Y_FRACTION = 0.766f;  // sin(50°)
        if (outDir.y > MAX_Y_FRACTION)
        {
            outDir.y = MAX_Y_FRACTION;
            float horizLen = Mathf.Sqrt(1f - MAX_Y_FRACTION * MAX_Y_FRACTION);
            float curHoriz = new Vector2(outDir.x, outDir.z).magnitude;
            if (curHoriz > 1e-5f)
            {
                float scale = horizLen / curHoriz;
                outDir.x *= scale;
                outDir.z *= scale;
            }
            else
            {
                // No horizontal component — push ball forward (away from player, –z).
                outDir.x = 0f;
                outDir.z = -horizLen;
            }
        }

        // ── Forward-bias — prevent sideways launches off the table ────────────
        // Decompose outDir into: forward (toward opponent), vertical, lateral.
        // Rules applied in order:
        //   1. Forward component floored at MIN_FWD — prevents backward/neutral hits.
        //   2. Lateral capped at forward × MAX_LAT_RATIO — max ~45° off table axis.
        // _tableForward = horizontal unit vector playerside→enemyside (cached in Start).
        // Guard: only applies when _tableForward is initialised (sqrMag > 0.5).
        if (_tableForward.sqrMagnitude > 0.5f)
        {
            float   fwdDot = Vector3.Dot(outDir, _tableForward);
            float   yComp  = outDir.y;
            // Lateral = outDir minus its forward and vertical parts.
            // _tableForward is horizontal, so forward/up/lateral are orthogonal.
            Vector3 latVec = outDir - fwdDot * _tableForward - yComp * Vector3.up;
            float   latMag = latVec.magnitude;

            const float MIN_FWD       = 0.20f;   // floor: ensures ball always travels toward opponent
            const float MAX_LAT_RATIO = 1.00f;   // cap: |lateral| ≤ |forward| → ≤ 45° off-axis

            float clampedFwd = Mathf.Max(fwdDot, MIN_FWD);
            float maxLat     = clampedFwd * MAX_LAT_RATIO;
            if (latMag > maxLat && latMag > 1e-5f)
                latVec = latVec * (maxLat / latMag);

            // Rebuild and renormalize. Y is unchanged (already bounded by 50° clamp).
            outDir = (clampedFwd * _tableForward + yComp * Vector3.up + latVec).normalized;
        }

        // Upward-normal softening removed: the verticalBias Lerp previously
        // attenuated speed whenever the contact normal had a large Y component,
        // which occurs during topspin serves (closed face, angled upward-forward).
        // This penalised exactly the contacts we most want to feel responsive.
        //
        // Adaptive minimum speed — calibration pass:
        //   effectiveSpd ≥ 1.0 m/s → floor 3.0 m/s (any real swing clears the net)
        //   effectiveSpd ≥ 0.5 m/s → floor 1.5 m/s (gentle tap zone)
        //   below 0.5            → scale with speed (juggling / resting contact)
        // Original line: float minSpd = effectiveSpd >= 0.8f ? 1.2f : effectiveSpd * 1.5f;
        float minSpd = effectiveSpd >= 1.0f ? 3.0f
                     : effectiveSpd >= 0.5f ? 1.5f
                     : effectiveSpd * 1.5f;
        spd = Mathf.Clamp(spd, minSpd, HIT_MAX_SPD);

        // ── [HIT-DIR] — ungated, fires in release APK builds via adb logcat ───
        // Logs the final outgoing direction AFTER all clamps so forwardDot/lateral
        // reflect exactly what the ball receives.
        {
            float hitFwdDot  = _tableForward.sqrMagnitude > 0.5f
                             ? Vector3.Dot(outDir, _tableForward) : 0f;
            float hitLateral = _tableForward.sqrMagnitude > 0.5f
                             ? (outDir - hitFwdDot * _tableForward - outDir.y * Vector3.up).magnitude
                             : 0f;
            Debug.Log($"[HIT-DIR] outDir={outDir:F3}  tableForward={_tableForward:F3}" +
                      $"  forwardDot={hitFwdDot:F3}  lateral={hitLateral:F3}" +
                      $"  speed={spd:F2}  source={_diagLayer}");
        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        // ── HIT-DIAG block ───────────────────────────────────────────────────
        {
            float diagBallSpd   = _ballRb.linearVelocity.magnitude;
            bool  diagHighSpeed = diagBallSpd > 6f || effectiveSpd > 5f;
            bool  diagBackhand  = Vector3.Dot(_paddleRb.rotation * Vector3.forward, Vector3.forward) < 0f;
            float diagRotDiff   = Quaternion.Angle(_paddleRb.rotation, paddle.transform.rotation);
            float diagPenDepth  = 0f;
            Vector3 diagPenDir  = Vector3.zero;
            bool    diagPenHit  = false;
            if (_ballCollider != null && paddleBox != null)
            {
                try
                {
                    diagPenHit = Physics.ComputePenetration(
                        _ballCollider, _ballRb.position, Quaternion.identity,
                        paddleBox, paddleBox.transform.position, paddleBox.transform.rotation,
                        out diagPenDir, out diagPenDepth);
                }
                catch { }
            }
            string tag = diagHighSpeed ? "[HIT-DIAG][HIGH-SPEED]" : "[HIT-DIAG]";
            Debug.Log($"{tag} layer={_diagLayer}  frame={Time.frameCount}  fixedTime={Time.fixedTime:F4}" +
                      $"\n  ball  pos={_ballRb.position:F4}  vel={_ballRb.linearVelocity:F4} ({diagBallSpd:F3})  angVel={_ballRb.angularVelocity:F3}" +
                      $"\n  paddle  rbPos={_paddleRb.position:F4}  rbRot={_paddleRb.rotation.eulerAngles:F2}  rbTrRot={diagRotDiff:F2}°" +
                      $"\n  surfVel={effectiveVel:F4} ({effectiveSpd:F3})  linVel={_paddleVelocity:F4}  angVel={_paddleAngularVelocity:F4}" +
                      $"\n  contact  pt={contactPt:F4}  normal={contactNormal:F4}" +
                      $"\n  out  dir={outDir:F4}  spd={spd:F3}  vel={outDir * spd:F4}" +
                      $"\n  pen  hit={diagPenHit}  depth={diagPenDepth:F5}" +
                      $"\n  backhand={diagBackhand}  separated={_ballSeparated}");
        }
        // ── END HIT-DIAG ─────────────────────────────────────────────────────
#endif

        _ballRb.linearVelocity = outDir * spd;
        _diagLastAssignedVel   = outDir * spd;
        _diagLastFinalBallVel  = outDir * spd;

        // ── Ball repositioning — face-based placement ─────────────────────────
        // Compute the target position into a local variable FIRST, then assign
        // to _ballRb.position.  The diagnostic must use the same local variable —
        // NOT _ballRb.position — because Unity's physics system queues Rigidbody
        // position writes during FixedUpdate: a read of _ballRb.position after the
        // write returns the OLD value (still inside the paddle) until the next
        // physics step processes the queue.  Reading _ballRb.position for
        // ComputePenetration was the root cause of the "still penetrating" false
        // positives even after repositioning was geometrically correct.
        //
        // Face-based formula:
        //   contactNormal is snapped to a face axis, so its inverse-rotation into
        //   local space is exactly ±(1,0,0), ±(0,1,0), or ±(0,0,±1).
        //   halfExtInFace = dot(scaledHalfExtents, abs(localNormal)) selects the
        //   correct box half-dimension for that face.
        //   faceCentre = paddleBoxCenter + contactNormal * halfExtInFace.
        //   Ball centre placed at faceCentre + contactNormal * BALL_CLEARANCE —
        //   always outside the paddle regardless of penetration depth.
        Vector3 repoBallPos;
        {
            // Use paddleBox.transform.rotation (not _paddleRb.rotation) so the local-space
            // conversion is correct when paddleBox is on a child with its own orientation.
            Vector3 localNormal = Quaternion.Inverse(paddleBox.transform.rotation) * contactNormal;
            Vector3 scaledHalf  = Vector3.Scale(
                paddleBox.size * 0.5f,
                new Vector3(
                    Mathf.Abs(paddleBox.transform.lossyScale.x),
                    Mathf.Abs(paddleBox.transform.lossyScale.y),
                    Mathf.Abs(paddleBox.transform.lossyScale.z)));
            float halfExtInFace = Mathf.Abs(localNormal.x) * scaledHalf.x
                                + Mathf.Abs(localNormal.y) * scaledHalf.y
                                + Mathf.Abs(localNormal.z) * scaledHalf.z;
            // Face centre in world space (paddleBoxCenter already includes paddleBox.center offset).
            // Ball centre = face surface + measured ball world radius (≈2.49 cm) + 1 mm margin.
            // BALL_CLEARANCE 0.027 confirmed against ball SphereCollider world radius ≈0.02486 m
            // (solid radius=0.5 × scale=0.04972, or similar) — previous 0.023 left 1.86 mm residual.
            const float BALL_CLEARANCE = 0.027f;
            repoBallPos = paddleBoxCenter + contactNormal * (halfExtInFace + BALL_CLEARANCE);
        }
        _ballRb.position = repoBallPos;

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        // Confirm no residual penetration after repositioning (dev builds only).
        if (_ballCollider != null && paddleBox != null)
        {
            try
            {
                bool postPen = Physics.ComputePenetration(
                    _ballCollider, repoBallPos,                  Quaternion.identity,
                    paddleBox,     paddleBox.transform.position, paddleBox.transform.rotation,
                    out Vector3 postDir, out float postDepth);
                if (postPen)
                    Debug.Log($"[HIT-DIAG] POST-REPOSITION still penetrating depth={postDepth:F5}  dir={postDir:F4}");
            }
            catch { }
        }

        // ── Debug visualization (Editor Scene view via Link/Air Link) ─────────
        {
            Vector3 finalVel = outDir * spd;
            Debug.DrawRay(contactPt, contactNormal * 0.13f, Color.yellow,          2f);
            Debug.DrawRay(contactPt, ballVel       * 0.04f, Color.cyan,            2f);
            Debug.DrawRay(contactPt, finalVel      * 0.04f, Color.green,           2f);
            Debug.DrawRay(contactPt, effectiveVel  * 0.04f, Color.white,           2f);
            Debug.DrawLine(contactPt, repoBallPos,           new Color(1f,0.4f,0f), 2f);
        }
#endif

        // Step 4 — spin from tangential (brushing) paddle-surface velocity.
        // Use effectiveVel (linear + angular contribution) so wrist brushes
        // generate realistic topspin/backspin even without arm translation.
        // Decompose into normal (push-through, no spin) and tangential (brushing → spin).
        // Cross(normal, tangential) gives the correct axis:
        //   upward brush on upward-facing normal → topspin ✓
        //   downward brush                        → backspin ✓
        //   sideways brush                        → sidespin ✓
        // Blend with existing spin so juggling topspin accumulates naturally.
        // Clamp to MAX_SPIN to prevent physics instability during long sessions —
        // without a cap, repeated hits can push angularVelocity into values that
        // destabilise the physics solver and cause erratic bounces.

        // ── Micro-jitter stabilization (spin normal only) ─────────────────────
        // At low speed, mm-level contact-point noise produces slightly different
        // contact normals each hit, causing spin variation.  Blend the normal used
        // for spin decomposition toward the true paddle face normal.
        // outDir is NOT changed — no aim assist, no shot-direction change.
        //
        // Use _paddleRb.rotation (Rigidbody-authoritative) — the same source used by
        // SnapToPaddleFaceNormal that produced contactNormal.  paddle.transform.forward
        // can lag by one physics step during fast wrist transitions (forehand→backhand),
        // causing a mismatch exactly when a hit fires.  Choose the sign that agrees
        // with contactNormal so both faces are handled symmetrically without a separate
        // flip-guard branch.
        Vector3 rbFwdForSpin = _paddleRb.rotation * Vector3.forward;
        Vector3 paddleFwd    = Vector3.Dot(rbFwdForSpin, contactNormal) >= 0f
                             ? rbFwdForSpin : -rbFwdForSpin;
        float stabFactor  = (1f - lsBlend) * juggleStabilityStrength;
        Vector3 spinNormal = Vector3.Slerp(contactNormal, paddleFwd, stabFactor).normalized;

        Vector3 nComp      = Vector3.Dot(effectiveVel, spinNormal) * spinNormal;
        Vector3 tangential = effectiveVel - nComp;

        // ── Low-speed spin damping ────────────────────────────────────────────
        // Progressively reduce spin generation for soft contacts.
        // Uses effectiveSpd (post-angular-damping) — same energy proxy as the
        // angular ramp, so assists activate and deactivate in lock-step.
        float spinBlend = Mathf.SmoothStep(0f, 1f,
                              effectiveSpd / Mathf.Max(0.01f, lowSpeedAssistThreshold));
        float spinScale = Mathf.Lerp(1f - lowSpeedSpinDamping, 1f, spinBlend);

        Vector3 spinDelta  = Vector3.Cross(spinNormal, tangential) * SPIN_COEFF * spinScale;
        _ballRb.angularVelocity = Vector3.ClampMagnitude(
            _ballRb.angularVelocity * 0.3f + spinDelta, MAX_SPIN);

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        // ── [SPIN-DIAG] — emitted on every validated hit in dev builds ──────────
        // Compare forehand vs backhand lines to diagnose spin asymmetry:
        //   • faceSide          which rubber/wood face was active
        //   • paddleForward/Right/Up  full Rigidbody-authoritative frame for this hit
        //   • contactNormal     face-snapped normal (always ± rbFwd toward ball)
        //   • spinNormal        stabilized normal used for cross-product decomposition
        //   • effectiveVel      linear + angular contribution at contact point
        //   • tangential        brushing component (drives spin magnitude and axis)
        //   • spinDelta         raw spin vector before clamp (rad/s)
        //   • finalAngVel       angular velocity written to ball Rigidbody
        // If tangential.magnitude is small for backhand → swing is mostly along
        // spinNormal (forward push) with little brushing → increase wrist snap.
        // If faceSide=back when you expect front → SnapToPaddleFaceNormal flipped.
        {
            bool    faceFront = Vector3.Dot(contactNormal, _paddleRb.rotation * Vector3.forward) >= 0f;
            string  faceSide  = faceFront ? "front" : "back";
            Vector3 rbRight   = _paddleRb.rotation * Vector3.right;
            Vector3 rbUp      = _paddleRb.rotation * Vector3.up;
            Debug.Log($"[SPIN-DIAG] source={_diagLayer}  faceSide={faceSide}  frame={Time.frameCount}" +
                      $"\n  paddleForward={rbFwdForSpin:F3}  paddleRight={rbRight:F3}  paddleUp={rbUp:F3}" +
                      $"\n  contactNormal={contactNormal:F3}  spinNormal={spinNormal:F3}" +
                      $"\n  effectiveVel={effectiveVel:F3} ({effectiveSpd:F3} m/s)" +
                      $"\n  tangential={tangential:F3} ({tangential.magnitude:F3} m/s)" +
                      $"\n  spinDelta={spinDelta:F3} ({spinDelta.magnitude:F3} rad/s)" +
                      $"\n  finalAngVel={_ballRb.angularVelocity:F3} ({_ballRb.angularVelocity.magnitude:F3} rad/s)");
        }
#endif

        // ── Hit telemetry event ────────────────────────────────────────────────
        // Fires on every validated hit after all physics writes are complete.
        // outSpin reflects the value actually written to _ballRb.angularVelocity.
        // Consumed by RealHitVarianceLogger; safe to leave unsubscribed.
        OnHitTelemetry?.Invoke(new HitTelemetryData
        {
            hitSource            = _diagLayer,
            contactPoint         = contactPt,
            contactNormal        = contactNormal,
            leverArmMag          = leverArm.magnitude,
            paddleLinearVelocity = _paddleVelocity,
            paddleAngVelocity    = _paddleAngularVelocity,
            effectiveSpd         = effectiveSpd,
            outSpeed             = spd,
            outSpin              = _ballRb.angularVelocity.magnitude
        });

        _ballRb.useGravity = true;
        _ballSeparated     = false;  // lock out further hits until ball exits paddle zone

        // Restore visibility in case a floor/net collision hid the ball.
        SetBallVisible(true);

        _lastHitTime          = Time.time;
        _lastHitFrame         = Time.frameCount;
        playerPaddleCollision = 1;

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        float diagApplyMs = (Time.realtimeSinceStartup - _diagApplyHitStart) * 1000f;
        if (diagApplyMs > 2f)
            Debug.Log($"[PADDLE-FREEZE][TIMING] ApplyPaddleHit took {diagApplyMs:F3} ms (threshold 2 ms)");
#endif

        PlayPaddleHitSound(spd);

        // PaddleTiltDeg: raw angle from vertical (0°=flat, 90°=vertical spin stance).
        float paddleTiltDeg = Vector3.Angle(contactNormal, Vector3.up);

        // PaddleAngleErrorDeg: deviation from ideal topspin normal.
        // IDEAL_SPIN_NORMAL = Vector3(0, 0.707, 0.707) — 45° closed toward opponent.
        // 0° = perfect topspin orientation. 90° = flat hit.
        //
        // Use the nearer ideal face so backhand contacts (where contactNormal may
        // point away from the forehand ideal) are measured against the correct
        // reference and never receive an artificially inflated angle error in the
        // research data.  This keeps the metric meaningful for both hit faces.
        Vector3 idealForFace       = Vector3.Dot(contactNormal, IDEAL_SPIN_NORMAL) >= 0f
                                   ? IDEAL_SPIN_NORMAL : -IDEAL_SPIN_NORMAL;
        float paddleAngleErrorDeg  = Vector3.Angle(contactNormal, idealForFace);

        // MinPaddleTableDistM: closest the paddle came to the table this serve.
        // Cap at 9.999 if never within measurable range (player never approached table).
        float minDist = _minPaddleTableDistThisServe < 9f
                      ? _minPaddleTableDistThisServe : 9.999f;

        ExperimentLogger.Instance?.LogPlayerHit(
            spd, tangential.magnitude,
            _ballRb.angularVelocity.magnitude,
            paddleAngleErrorDeg, paddleTiltDeg,
            minDist);

        // ── Haptic event — fires after every validated paddle hit ─────────────
        OnValidPaddleHit?.Invoke(effectiveSpd, _ballRb.angularVelocity.magnitude);

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        Debug.Log($"[gameplay] HIT spd={spd:F1} effVel={effectiveSpd:F1} linVel={_paddleVelocity.magnitude:F1} " +
                  $"spin={_ballRb.angularVelocity.magnitude:F0} " +
                  $"tilt={paddleTiltDeg:F1}° angleErr={paddleAngleErrorDeg:F1}° " +
                  $"minTableDist={minDist:F3}m");

        // ── HIT-CAL: per-hit calibration log ──────────────────────────────────
        // Records the full energy breakdown for every hit.
        // Use this to quantify whether angular velocity is genuinely responsible
        // for excess energy on low-speed and edge contacts.
        // Format is one compact line to keep the log scannable.
        {
            float calLinear   = _paddleVelocity.magnitude;
            float calAngular  = _paddleAngularVelocity.magnitude;
            float calAngContrib = Vector3.Cross(
                _paddleAngularVelocity * effectiveAngularWeight, leverArm).magnitude;
            float calLeverArm = leverArm.magnitude;
            float calSpin     = _ballRb.angularVelocity.magnitude;
            Vector3 calContactLocal = paddleBox != null
                ? paddleBox.transform.InverseTransformPoint(contactPt)
                : contactPt;
            Debug.Log(
                $"[HIT-CAL] " +
                $"linear={calLinear:F3} " +
                $"angular={calAngular:F3} " +
                $"angContrib={calAngContrib:F3} " +
                $"effectiveSpd={effectiveSpd:F3} " +
                $"leverArm={calLeverArm:F4} " +
                $"outSpeed={spd:F3} " +
                $"spin={calSpin:F1} " +
                $"contact=({calContactLocal.x:F3},{calContactLocal.y:F3},{calContactLocal.z:F3}) " +
                $"normal=({contactNormal.x:F3},{contactNormal.y:F3},{contactNormal.z:F3}) " +
                $"lsBlend={lsBlend:F3} " +
                $"angWeight={effectiveAngularWeight:F3}");
        }

        // ── [PHYSICS-AUDIT] — structured per-hit calibration log ─────────────
        // Fires on every validated paddle hit (dev/editor builds).
        // Use these fields to audit realism, consistency, and energy transfer:
        //   PaddleVelocity  — EMA-smoothed world-space linear velocity (m/s).
        //                     If consistently low (<1.5) despite hard swings,
        //                     raise velEmaAlpha in the Inspector (default 0.35→0.5).
        //   PaddleForward   — Rigidbody-authoritative face direction at contact.
        //   ContactPoint    — World-space contact point (local coords in HIT-CAL).
        //   OutgoingVelocity— Final velocity written to the ball Rigidbody (m/s).
        //   OutgoingSpeed   — |OutgoingVelocity| (m/s). Should be ≥ 3 m/s for
        //                     any intentional swing (effectiveSpd ≥ 1.0 m/s).
        //   GeneratedSpin   — |angularVelocity| after hit (rad/s).
        //                     Topspin/backspin ~80–200 rad/s is realistic.
        //   BounceCount     — Table contacts since the PREVIOUS paddle hit.
        //                     High values (>4) indicate long rally with energy decay.
        {
            Vector3 auditPaddleFwd  = _paddleRb.rotation * Vector3.forward;
            Vector3 auditOutVel     = _ballRb.linearVelocity;   // already written above
            float   auditOutSpd     = auditOutVel.magnitude;
            float   auditSpin       = _ballRb.angularVelocity.magnitude;
            Debug.Log(
                $"[PHYSICS-AUDIT] frame={Time.frameCount}  layer={_diagLayer}\n" +
                $"  PaddleVelocity={_paddleVelocity:F3} ({_paddleVelocity.magnitude:F2} m/s)\n" +
                $"  PaddleForward={auditPaddleFwd:F3}\n" +
                $"  ContactPoint={contactPt:F3}\n" +
                $"  OutgoingVelocity={auditOutVel:F3}\n" +
                $"  OutgoingSpeed={auditOutSpd:F2} m/s\n" +
                $"  GeneratedSpin={auditSpin:F1} rad/s\n" +
                $"  BounceCount={hitBounceCount}");
        }
#endif
    }

    // Sharp percussive click matching real table tennis paddle contact.
    // Multi-harmonic layering (2400 + 4200 + 6800 Hz) with very fast decay (90×)
    // gives the "click" character rather than a sustained ping.
    static AudioClip CreatePaddleClickClip()
    {
        const int sampleRate = 44100;
        const float duration = 0.090f;   // 90ms: louder/easier to hear on Quest speakers

        int samples = Mathf.RoundToInt(sampleRate * duration);
        float[] data = new float[samples];

        for (int i = 0; i < samples; i++)
        {
            float t = (float)i / sampleRate;
            float env = Mathf.Exp(-t * 45f);

            float sig =
                0.55f * Mathf.Sin(2f * Mathf.PI * 1800f * t) +
                0.30f * Mathf.Sin(2f * Mathf.PI * 3200f * t) +
                0.15f * Mathf.Sin(2f * Mathf.PI * 5200f * t);

            data[i] = Mathf.Clamp(env * sig * 1.8f, -1f, 1f);
        }

        AudioClip clip = AudioClip.Create("PaddleClick_LOUD", samples, 1, sampleRate, false);
        clip.SetData(data, 0);
        return clip;
    }

    void PlayPaddleHitSound(float speed)
    {
        Debug.Log($"[SOUND-DIAG] PlayPaddleHitSound called speed={speed:F3} audioNull={_gameplayAudio == null} clipNull={_paddleHitClip == null}");

        if (_gameplayAudio == null || _paddleHitClip == null)
            return;

        // AUDIO-ONLY PATCH: force reliable 2D playback. Do not touch physics.
        _gameplayAudio.enabled = true;
        _gameplayAudio.mute = false;
        _gameplayAudio.spatialBlend = 0f;
        _gameplayAudio.volume = 1f;
        _gameplayAudio.priority = 0;
        _gameplayAudio.outputAudioMixerGroup = null;

        _gameplayAudio.pitch = Mathf.Clamp(0.95f + speed * 0.03f, 0.9f, 1.25f);
        _gameplayAudio.PlayOneShot(_paddleHitClip, 1f);

        Debug.Log($"[SOUND-DIAG] PlayOneShot executed FULL VOLUME clip={_paddleHitClip.name} pitch={_gameplayAudio.pitch:F3} frame={Time.frameCount}");
    }

    // ── Serve system ──────────────────────────────────────────────────────────
    //
    //  Serve flow (one press/release only):
    //
    //    HIDDEN  — ball invisible/underground.
    //              HOLD left trigger → ball appears at palm → HELD.
    //
    //    HELD    — trigger is held → ball teleports to palm every frame.
    //              RELEASE trigger → ball tosses with hand velocity → DROPPED.
    //              Releasing trigger is the ONLY action needed. No prior press
    //              history is required because we check !triggerHeld directly.
    //
    //    DROPPED — ball in free flight under gravity.
    //              HOLD trigger → re-grab → HELD.
    //              5-second timeout auto-resets if ball gets stuck on floor.
    //
    void HandlePlayerServe()
    {
        bool inPlayerStart = ballbounce.state == ballbounce.gameState.playerStart;

        // Compute these here so they are available in the !inPlayerStart block.
        Vector3 palmOffset = _leftValid ? _leftRot * new Vector3(0f, 0.04f, 0.06f)
                                        : Vector3.zero;
        Vector3 ballInHand = _leftPos + palmOffset;
        bool    triggerHeld = GetLeftTriggerHeld();

        // ── Trigger-held diagnostic (once per second) ─────────────────────────
        if (triggerHeld && Time.time > _serveDiagLastLog + 1f)
        {
            _serveDiagLastLog = Time.time;
            Vector3 ballPos = (ball != null) ? ball.transform.position : Vector3.one * -999f;
            Debug.Log($"[SERVE-DIAG] state={ballbounce.state} phase={_servePhase} " +
                      $"leftValid={_leftValid} triggerHeld={triggerHeld} " +
                      $"ballPos=({ballPos.x:F2},{ballPos.y:F2},{ballPos.z:F2})");
        }

        // ── World-escape fallback ─────────────────────────────────────────────
        // If the ball has fallen off the scene geometry (through a gap between
        // chair legs, off the table edge, etc.) it sinks to very negative Y.
        // Without this guard, _servePhase stays Dropped forever — pointJustScored
        // was already consumed and there is no other reset path.
        // Threshold: -10 m.  The table top is ~0.76 m; the floor is 0 m; anything
        // below -10 m is definitively off the world.  We do NOT call
        // ParkBallUnderground here — just arm Hidden so the player can grab again.
        if (_servePhase == ServePhase.Dropped && _ballRb != null && _ballRb.position.y < -10f)
        {
            Debug.Log($"[SERVE→Hidden] src=worldEscape y={_ballRb.position.y:F1}");
            _servePhase        = ServePhase.Hidden;
            _triggerWasPressed = false;
            serveReady         = false;
        }

        if (!inPlayerStart)
        {
            // ── Serve-reset arming ────────────────────────────────────────────
            // The player grabs the ball by holding the left trigger, then releases
            // to toss.  In VR they naturally keep a loose grip — GetLeftTriggerHeld()
            // stays true through the toss and into the rally.  If we fire the
            // serve-reset on any triggerHeld the ball is immediately parked
            // underground the moment the state advances to playerPaddle/enemyPaddle.
            //
            // Fix: the serve-reset only arms (becomes eligible to fire) once the
            // trigger has been fully released at least once since the last toss.
            // A deliberate re-press of SERVE_RESET_HOLD_SEC seconds then resets.
            if (!triggerHeld)
            {
                _serveResetArmed     = true;   // trigger released → allow next press to reset
                _serveResetHoldStart = -1f;    // clear hold timer on any release
            }

            // ── Serve-reset: ARMED + left trigger held ────────────────────────
            // Fires whenever the trigger is held for SERVE_RESET_HOLD_SEC (0.4 s)
            // after it was released at least once since the last toss.
            // Training mode: no scoring, so the player resets manually at any time.
            if (triggerHeld && _leftValid && _serveResetArmed)
            {
                if (_serveResetHoldStart < 0f)
                    _serveResetHoldStart = Time.time;

                float holdDur = Time.time - _serveResetHoldStart;

                if (holdDur < SERVE_RESET_HOLD_SEC)
                {
                    // Still within hold window — keep ball in flight (Dropped),
                    // or preserve Hidden if it was already waiting.
                    if (_servePhase != ServePhase.Dropped)
                    {
                        _servePhase        = ServePhase.Hidden;
                        _triggerWasPressed = false;
                        serveReady         = false;
                    }
                    return;
                }

                // ── Hold confirmed: fire the serve-reset ──────────────────────
                _serveResetHoldStart = -1f;
                Debug.Log($"[SERVE] Serve-reset — state={ballbounce.state} phase={_servePhase} " +
                          $"sinceHit={Time.time - _lastHitTime:F1}s holdDur={holdDur:F2}s");
                ParkBallUnderground();            // immediately hide the current ball
                ballbounce.state           = ballbounce.gameState.playerStart;
                playerPaddleCollision      = 0;
                _lastHitTime               = -10f;
                _servePhase                = ServePhase.Hidden;
                serveReady                 = false;
                _serveResetArmed           = false; // consume the arm; re-arm on next release
                // Fall through to Hidden→Held below — ball appears this frame.
            }
            else
            {
                // Trigger not held — preserve Dropped if ball is in flight;
                // clear Hidden/Held which are only meaningful in playerStart.
                if (_servePhase != ServePhase.Dropped)
                {
                    if (_servePhase != ServePhase.Hidden)
                        Debug.Log($"[SERVE→Hidden] src=earlyReturn phase={_servePhase} state={ballbounce.state}");
                    _servePhase        = ServePhase.Hidden;
                    _triggerWasPressed = false;
                    serveReady         = false;
                }
                return;
            }
        }

        if (_ballRb == null || ball == null) return;

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        if (_debugFrame % 60 == 0)
            Debug.Log($"[SERVE] phase={_servePhase} held={triggerHeld} leftValid={_leftValid}");
#endif

        // ── Timeout reset: ball stuck on floor after a weak toss ─────────
        // Only fires when the ball is in a scoring/reset state (playerStart or
        // enemyStart), not during an active rally (playerPaddle, enemyPaddle,
        // playerSide, enemySide).  This prevents the timeout from converting
        // _servePhase=Dropped→Hidden mid-rally, which would otherwise let a
        // 0.4 s trigger hold vanish the ball while it is still in play.
        // In training mode only playerPaddle means "ball in play".
        bool inActiveRally = ballbounce.state == ballbounce.gameState.playerPaddle;
        if (_servePhase == ServePhase.Dropped && !inActiveRally && Time.time > _dropTime + 5f)
        {
            Debug.Log("[SERVE→Hidden] src=timeout5s");
            _servePhase        = ServePhase.Hidden;
            _triggerWasPressed = false;
            serveReady         = false;
        }

        // ── HIDDEN → HELD: grab when trigger is held ─────────────────────
        // Ball only becomes visible when the player actively holds the trigger.
        // This prevents auto-grab without trigger (which caused the double-press
        // bug: _triggerWasPressed was false when ball first appeared, so the
        // first release never fired).
        if (_servePhase == ServePhase.Hidden && _leftValid && triggerHeld)
        {
            // Keep ball KINEMATIC during the entire Held phase.
            //
            // Previously this set isKinematic=false, which made the ball subject to
            // the physics solver while pinned to the hand.  The playerside BoxCollider
            // is 1 unit thick — at normal serving height the hand (and ball) sits
            // INSIDE that collider body.  The solver fires depenetration forces at
            // 120 Hz to push the ball out; the serve code fights back by zeroing
            // velocity and teleporting position every Update.  The result is 120 Hz
            // oscillation that looks like two vibrating balls, and accumulated solver
            // velocity that causes sideways drift on toss.
            //
            // A kinematic Rigidbody teleports exactly to wherever _ballRb.position
            // is written — the solver never pushes it.  isKinematic switches back to
            // false at toss so physics takes over cleanly.
            // Zero velocity BEFORE setting kinematic — Unity 6 rejects velocity
            // writes on kinematic bodies with a hard warning.
            if (!_ballRb.isKinematic)
            {
                _ballRb.linearVelocity  = Vector3.zero;
                _ballRb.angularVelocity = Vector3.zero;
            }
            _ballRb.isKinematic     = true;
            _ballRb.useGravity      = false;
            _ballRb.position        = ballInHand;
            _prevBallPosValid       = false;   // invalidate sweep until ball is live
            SetBallVisible(true);
            _leftAnchorPrev   = _leftPos;
            _leftHandVelocity = Vector3.zero;
            _servePhase       = ServePhase.Held;
            // Reset per-serve metrics — new serve attempt begins now.
            _serveGrabTime               = Time.time;
            _minPaddleTableDistThisServe = float.MaxValue;

            // Diagnostic: log every collider overlapping the grab point so we
            // can confirm what geometry the ball was fighting during hold.
            var hits = Physics.OverlapSphere(ballInHand, 0.025f);
            foreach (var h in hits)
                Debug.Log($"[SERVE] OverlapSphere at grab: collider='{h.name}' " +
                          $"isTrigger={h.isTrigger} layer={h.gameObject.layer}");
            Debug.Log($"[SERVE] Grabbed (kinematic) — ball.y={_ballRb.position.y:F2}  " +
                      $"release trigger to toss.  overlaps={hits.Length}");
        }

        // ── HELD: track hand, toss on release ────────────────────────────
        if (_servePhase == ServePhase.Held)
        {
            if (_leftValid)
            {
                // Sample hand velocity every frame while ball is held.
                Vector3 ovrLVel = Vector3.zero;
                try { ovrLVel = OVRInput.GetLocalControllerVelocity(OVRInput.Controller.LTouch); } catch { }
                _leftHandVelocity = ovrLVel.sqrMagnitude > 0.0001f
                    ? Vector3.Lerp(_leftHandVelocity, ovrLVel, 0.5f)
                    : Vector3.Lerp(_leftHandVelocity,
                                   (_leftPos - _leftAnchorPrev) / Mathf.Max(Time.deltaTime, 0.001f),
                                   0.5f);
                _leftAnchorPrev = _leftPos;

                // Pin ball to palm each frame via kinematic teleport.
                // Ball is kinematic during Held — no depenetration forces, no solver
                // fights.  Do NOT write velocity while kinematic: Unity 6 rejects
                // those writes with a hard warning every frame.
                _ballRb.useGravity      = false;
                _ballRb.position        = ballInHand;
            }

            // Toss fires the INSTANT trigger is no longer held.
            // No edge detection needed — we just check the live boolean.
            // This is why one press+release is sufficient.
            if (!triggerHeld)
            {
                Vector3 tossVel = _leftHandVelocity;
                if (tossVel.y < 1.0f) tossVel.y = 1.0f;   // always rise at least 1 m/s

                // ── RELEASE DIAGNOSTIC ───────────────────────────────────────
                // Purpose: confirm whether the ball overlaps scene geometry at
                // the exact frame isKinematic flips true→false.
                // Read these logs via ADB logcat tag "Unity" after a toss.
                {
                    const float BALL_R = 0.02f;   // matches ballbounce.BALL_RADIUS

                    Debug.Log($"[TOSS-DIAG] ── release frame ──────────────────────────");
                    Debug.Log($"[TOSS-DIAG] ballInHand world pos = {ballInHand:F4}");
                    Debug.Log($"[TOSS-DIAG] tossVel assigned     = {tossVel:F4}  (magnitude={tossVel.magnitude:F3})");

                    // ── Named suspects ──────────────────────────────────────
                    string[] suspectNames = { "playerside", "net", "Net", "TableNet", "table_net" };
                    foreach (string sn in suspectNames)
                    {
                        GameObject sgo = GameObject.Find(sn);
                        if (sgo == null) continue;
                        Collider sc = sgo.GetComponent<Collider>();
                        if (sc == null) continue;
                        Debug.Log($"[TOSS-DIAG] suspect '{sn}' bounds={sc.bounds}  layer={sgo.layer}  isTrigger={sc.isTrigger}");
                        if (_ballCollider != null)
                        {
                            try
                            {
                                bool pen = Physics.ComputePenetration(
                                    _ballCollider, ballInHand, Quaternion.identity,
                                    sc, sc.transform.position, sc.transform.rotation,
                                    out Vector3 penDir, out float penDist);
                                Debug.Log($"[TOSS-DIAG]   ComputePenetration → overlap={pen}  depth={penDist:F5} m  pushDir={penDir:F3}");
                            }
                            catch (System.Exception ex)
                            {
                                Debug.Log($"[TOSS-DIAG]   ComputePenetration threw: {ex.Message}");
                            }
                        }
                    }

                    // ── Paddle ───────────────────────────────────────────────
                    if (paddleBox != null)
                    {
                        Debug.Log($"[TOSS-DIAG] paddle bounds={paddleBox.bounds}  layer={paddle?.gameObject.layer}");
                        if (_ballCollider != null)
                        {
                            try
                            {
                                bool pen = Physics.ComputePenetration(
                                    _ballCollider, ballInHand, Quaternion.identity,
                                    paddleBox, paddleBox.transform.position, paddleBox.transform.rotation,
                                    out Vector3 penDir, out float penDist);
                                Debug.Log($"[TOSS-DIAG]   ComputePenetration → overlap={pen}  depth={penDist:F5} m  pushDir={penDir:F3}");
                            }
                            catch (System.Exception ex)
                            {
                                Debug.Log($"[TOSS-DIAG]   ComputePenetration threw: {ex.Message}");
                            }
                        }
                    }

                    // ── OverlapSphere sweep: any solid geometry within BALL_R + 1 cm ──
                    // Query BEFORE isKinematic flips so the ball's own collider is
                    // still kinematic and excluded from physics — only external
                    // colliders appear in the result.
                    Collider[] releaseOverlaps = Physics.OverlapSphere(
                        ballInHand, BALL_R + 0.01f, ~0, QueryTriggerInteraction.Ignore);
                    Debug.Log($"[TOSS-DIAG] OverlapSphere r={BALL_R + 0.01f:F3} — {releaseOverlaps.Length} solid collider(s) found");
                    foreach (Collider oc in releaseOverlaps)
                    {
                        Debug.Log($"[TOSS-DIAG]   collider='{oc.name}' go='{oc.gameObject.name}' layer={oc.gameObject.layer}  bounds={oc.bounds}");
                        if (_ballCollider != null)
                        {
                            try
                            {
                                bool pen = Physics.ComputePenetration(
                                    _ballCollider, ballInHand, Quaternion.identity,
                                    oc, oc.transform.position, oc.transform.rotation,
                                    out Vector3 penDir, out float penDist);
                                if (pen)
                                    Debug.Log($"[TOSS-DIAG]     PENETRATING  depth={penDist:F5} m  pushDir={penDir:F3}");
                                else
                                    Debug.Log($"[TOSS-DIAG]     overlap sphere but ComputePenetration = no penetration");
                            }
                            catch (System.Exception ex)
                            {
                                Debug.Log($"[TOSS-DIAG]     ComputePenetration threw: {ex.Message}");
                            }
                        }
                    }
                }
                // ── END RELEASE DIAGNOSTIC ───────────────────────────────────

                // Stamp position, then switch kinematic→non-kinematic BEFORE
                // assigning velocity.  linearVelocity writes are silently ignored
                // on kinematic bodies — the order matters.
                _ballRb.position                   = ballInHand;
                _ballRb.isKinematic                = false;
                _ballRb.collisionDetectionMode     = CollisionDetectionMode.ContinuousDynamic;
                _ballRb.useGravity                 = true;
                _ballRb.linearVelocity             = tossVel;
                _ballRb.angularVelocity            = Vector3.zero;

                Debug.Log($"[TOSS-DIAG] POST-RELEASE linearVelocity={_ballRb.linearVelocity:F4}  " +
                          $"isKinematic={_ballRb.isKinematic}  useGravity={_ballRb.useGravity}");

                _servePhase           = ServePhase.Dropped;
                _dropTime             = Time.time;
                serveReady            = false;
                playerPaddleCollision = 0;
                _serveResetArmed      = false;  // must release trigger before serve-reset can fire
                _ballSeparated        = true;   // ball just left hand — reset contact lockout

                // Log serve event: distance to net and prep time.
                // serveDistToNet: |ballZ - netZ|. Smaller = closer to net (illegally forward).
                // prepTime: seconds from ball appearing in hand to this toss.
                float serveDistToNet = Mathf.Abs(ballInHand.z - _netZ);
                float prepTime       = _serveGrabTime >= 0f
                                     ? Time.time - _serveGrabTime : 0f;
                ExperimentLogger.Instance?.LogServeStart();

                Debug.Log($"[SERVE] Tossed vel={tossVel:F2}  ball.y={_ballRb.position.y:F2} " +
                          $"distToNet={serveDistToNet:F3} prep={prepTime:F2}s");
            }
        }

        // ── DROPPED → HELD: re-grab on trigger hold (0.4 s debounce) ────
        if (_servePhase == ServePhase.Dropped && triggerHeld && _leftValid
            && Time.time > _dropTime + 0.4f)
        {
            // Same kinematic-hold approach as Hidden→Held — prevents depenetration
            // fights with the playerside collider during re-grab.
            // Zero velocity BEFORE setting kinematic (Unity 6 rejects writes after).
            if (!_ballRb.isKinematic)
            {
                _ballRb.linearVelocity  = Vector3.zero;
                _ballRb.angularVelocity = Vector3.zero;
            }
            _ballRb.isKinematic     = true;
            _ballRb.useGravity      = false;
            _ballRb.position        = ballInHand;
            _prevBallPosValid       = false;
            SetBallVisible(true);
            _leftAnchorPrev   = _leftPos;
            _leftHandVelocity = Vector3.zero;
            _servePhase       = ServePhase.Held;
            // Reset per-serve metrics on re-grab too.
            _serveGrabTime               = Time.time;
            _minPaddleTableDistThisServe = float.MaxValue;
            Debug.Log($"[SERVE] Re-grabbed — ball.y={_ballRb.position.y:F2}");
        }
    }

    void ParkBallUnderground()
    {
        if (_ballRb != null)
        {
            // Only zero velocity while non-kinematic — kinematic bodies reject velocity writes.
            if (!_ballRb.isKinematic)
            {
                _ballRb.linearVelocity  = Vector3.zero;
                _ballRb.angularVelocity = Vector3.zero;
            }
            _ballRb.isKinematic = true;
            _ballRb.useGravity  = false;
        }
        if (ball != null)
            ball.transform.position = new Vector3(0f, -100f, 0f);
    }

    // ── AI ────────────────────────────────────────────────────────────────────
    public void FollowBall()
    {
        return;  // AI disabled
        if (enemyPaddle == null || ball == null || _ballRb == null) return;

        Vector3 ballPos = ball.transform.position;

        Vector3 target = new Vector3(
            ballPos.x,
            Mathf.Clamp(ballPos.y, 0.65f, 1.15f),
            Mathf.Clamp(ballPos.z, 0.15f, 1.6f));

        float dist3D = Vector3.Distance(
            new Vector3(enemyPaddle.transform.position.x, 0f, enemyPaddle.transform.position.z),
            new Vector3(target.x, 0f, target.z));

        float aiSpd = Mathf.Lerp(AI_SPEED_MIN, AI_SPEED_MAX, Mathf.Clamp01(dist3D / 0.5f));
        enemyPaddle.transform.position = Vector3.MoveTowards(
            enemyPaddle.transform.position, target, aiSpd * Time.deltaTime);

        if (enemypaddleBox == null)
            enemypaddleBox = enemyPaddle.GetComponent<BoxCollider>();

        if (enemypaddleBox != null && Time.time > _aiLastHitTime + AI_HIT_COOLDOWN)
        {
            float distToCenter = Vector3.Distance(
                ball.transform.position, enemyPaddle.transform.position);
            if (distToCenter < 0.28f)
            {
                Vector3 aimSpot = new Vector3(
                    Random.Range(-0.5f, 0.5f), 0.76f,
                    Random.Range(-0.6f, -0.1f));

                Vector3 dir = (aimSpot - ball.transform.position).normalized;
                float   spd = Random.Range(4.5f, 7.0f);

                _ballRb.linearVelocity  = dir * spd + Vector3.up * 1.8f;
                _ballRb.useGravity      = true;

                float t = Random.value;
                _ballRb.angularVelocity = t < 0.60f
                    ? new Vector3(Random.Range(-40f, -15f), 0f, Random.Range(-8f, 8f))
                    : t < 0.85f
                        ? new Vector3(Random.Range(-8f, 8f), 0f, Random.Range(-5f, 5f))
                        : new Vector3(Random.Range(15f, 40f), 0f, Random.Range(-10f, 10f));

                enemyPaddleCollision = 1;
                _aiLastHitTime       = Time.time;
                _lastHitTime         = Time.time;  // keep juggle-lockout window alive during AI rally

                // AI disabled in training mode — logging removed.

                Debug.Log($"[AI] Return vel={_ballRb.linearVelocity:F1}");
            }
        }
    }

    public void ResetEnemyPaddle()
    {
        if (enemyPaddle != null)
            enemyPaddle.transform.position = new Vector3(0f, 0.86f, 1.1f);
    }


    // ── Controller pose ───────────────────────────────────────────────────────
    void UpdateControllerPoses()
    {
        _rightValid = false;
        _leftValid  = false;

        Vector3    rp = InputTracking.GetLocalPosition(XRNode.RightHand);
        Quaternion rr = InputTracking.GetLocalRotation(XRNode.RightHand);
        if (rp.sqrMagnitude > 0.0001f || rr != Quaternion.identity)
        { _rightPos = rp; _rightRot = rr; _rightValid = true; }

        Vector3    lp = InputTracking.GetLocalPosition(XRNode.LeftHand);
        Quaternion lr = InputTracking.GetLocalRotation(XRNode.LeftHand);
        if (lp.sqrMagnitude > 0.0001f || lr != Quaternion.identity)
        { _leftPos = lp; _leftRot = lr; _leftValid = true; }

        if (_rightDevices.Count == 0 || _leftDevices.Count == 0) RefreshDeviceLists();

        if (!_rightValid)
            foreach (var d in _rightDevices)
            {
                if (d.TryGetFeatureValue(XRCommonUsages.devicePosition, out rp) &&
                    d.TryGetFeatureValue(XRCommonUsages.deviceRotation, out rr) &&
                    rp != Vector3.zero)
                { _rightPos = rp; _rightRot = rr; _rightValid = true; break; }
            }

        if (!_leftValid)
            foreach (var d in _leftDevices)
            {
                if (d.TryGetFeatureValue(XRCommonUsages.devicePosition, out lp) &&
                    d.TryGetFeatureValue(XRCommonUsages.deviceRotation, out lr) &&
                    lp != Vector3.zero)
                { _leftPos = lp; _leftRot = lr; _leftValid = true; break; }
            }

#if ENABLE_INPUT_SYSTEM
        if (!_rightValid || !_leftValid) ScanNewInputDevices();
#endif

        if (!_rightValid || !_leftValid)
            try
            {
                if (!_rightValid)
                {
                    var p = OVRInput.GetLocalControllerPosition(OVRInput.Controller.RTouch);
                    var r = OVRInput.GetLocalControllerRotation(OVRInput.Controller.RTouch);
                    if (p.sqrMagnitude > 0.0001f)
                    { _rightPos = p; _rightRot = r; _rightValid = true; }
                }
                if (!_leftValid)
                {
                    var p = OVRInput.GetLocalControllerPosition(OVRInput.Controller.LTouch);
                    var r = OVRInput.GetLocalControllerRotation(OVRInput.Controller.LTouch);
                    if (p.sqrMagnitude > 0.0001f)
                    { _leftPos = p; _leftRot = r; _leftValid = true; }
                }
            }
            catch { }
    }

#if ENABLE_INPUT_SYSTEM
    void ScanNewInputDevices()
    {
        foreach (var dev in UnityEngine.InputSystem.InputSystem.devices)
        {
            bool isRight = false, isLeft = false;
            foreach (var u in dev.usages)
            {
                string us = u.ToString();
                if (us.Contains("Right")) isRight = true;
                if (us.Contains("Left"))  isLeft  = true;
            }
            if (!isRight && !isLeft)
            {
                string dn = dev.name.ToLower();
                if      (dn.Contains("right")) isRight = true;
                else if (dn.Contains("left"))  isLeft  = true;
            }
            if (!isRight && !isLeft) continue;

            var xrc = dev as UnityEngine.InputSystem.XR.XRController;
            if (xrc != null)
            {
                Vector3    p  = xrc.devicePosition.ReadValue();
                Quaternion r  = xrc.deviceRotation.ReadValue();
                bool ok = p.sqrMagnitude > 0.0001f;
                if (isRight && !_rightValid && ok) { _rightPos = p; _rightRot = r; _rightValid = true; }
                if (isLeft  && !_leftValid  && ok) { _leftPos  = p; _leftRot  = r; _leftValid  = true; }
            }
            else
            {
                var pc = dev.TryGetChildControl("devicePosition")
                         as UnityEngine.InputSystem.Controls.Vector3Control;
                var rc = dev.TryGetChildControl("deviceRotation")
                         as UnityEngine.InputSystem.Controls.QuaternionControl;
                if (pc != null && rc != null)
                {
                    Vector3    p  = pc.ReadValue();
                    Quaternion r  = rc.ReadValue();
                    bool ok = p.sqrMagnitude > 0.0001f;
                    if (isRight && !_rightValid && ok) { _rightPos = p; _rightRot = r; _rightValid = true; }
                    if (isLeft  && !_leftValid  && ok) { _leftPos  = p; _leftRot  = r; _leftValid  = true; }
                }
            }
        }
    }
#endif

    // ── Right-hand input helpers ───────────────────────────────────────────────
    // Digital left index trigger — true while squeezed past Meta's threshold.
    // Using boolean state avoids the analog timing mismatch that caused:
    //   - double-trigger (grip lingers above threshold after index releases)
    //   - stuck-at-zero (analog axis unavailable in some SDK configurations)
    bool GetLeftTriggerHeld()
    {
        // OVRInput digital button — most reliable on Quest hardware.
        try { if (OVRInput.Get(OVRInput.RawButton.LIndexTrigger)) return true; } catch {}
        try { if (OVRInput.Get(OVRInput.Button.PrimaryIndexTrigger,
                               OVRInput.Controller.LTouch)) return true; } catch {}
        // XR Input System digital button fallback.
        foreach (var d in _leftDevices)
        {
            bool pressed = false;
            if (d.TryGetFeatureValue(XRCommonUsages.triggerButton, out pressed) && pressed)
                return true;
        }
        // Last resort: analog axis with a firm threshold.
        float v = 0f;
        try { v = OVRInput.Get(OVRInput.RawAxis1D.LIndexTrigger); } catch {}
        if (v > 0.5f) return true;
        foreach (var d in _leftDevices)
            if (d.TryGetFeatureValue(XRCommonUsages.trigger, out v) && v > 0.5f) return true;
        return false;
    }

    float GetRightGrip()
    {
        float v = 0f;
        try { v = OVRInput.Get(OVRInput.RawAxis1D.RHandTrigger); } catch {}
        if (v > 0.01f) return v;
        foreach (var d in _rightDevices)
            if (d.TryGetFeatureValue(XRCommonUsages.grip, out v) && v > 0.01f) return v;
        return 0f;
    }

    float GetRightIndexTrigger()
    {
        float v = 0f;
        try { v = OVRInput.Get(OVRInput.RawAxis1D.RIndexTrigger); } catch {}
        if (v > 0.01f) return v;
        foreach (var d in _rightDevices)
            if (d.TryGetFeatureValue(XRCommonUsages.trigger, out v) && v > 0.01f) return v;
        return 0f;
    }

    bool GetRightThumb()
    {
        try
        {
            return OVRInput.Get(OVRInput.RawTouch.A) ||
                   OVRInput.Get(OVRInput.RawTouch.B) ||
                   OVRInput.Get(OVRInput.RawTouch.RThumbstick);
        }
        catch {}
        bool a = false, b = false, s = false;
        foreach (var d in _rightDevices)
        {
            d.TryGetFeatureValue(XRCommonUsages.primaryTouch,       out a);
            d.TryGetFeatureValue(XRCommonUsages.secondaryTouch,     out b);
            d.TryGetFeatureValue(XRCommonUsages.primary2DAxisTouch, out s);
            break;
        }
        return a || b || s;
    }

    // ── Legacy aliases ────────────────────────────────────────────────────────
    public void follow_ball()        => FollowBall();
    public void reset_enemy_paddle() => ResetEnemyPaddle();
}

// ── PaddleHitForwarder ────────────────────────────────────────────────────────
// Attached to the paddle GameObject at runtime by gameplay.Start().
// Routes OnCollisionEnter / OnCollisionStay physics events back to gameplay.cs.
//
// Why this is needed:
//   Our proximity-based hit detection checks the current ball/paddle positions
//   each FixedUpdate step.  When the paddle swings very fast (padVel > 4 m/s),
//   it can move 6+ cm in one step, carrying the face completely through the ball
//   between two consecutive frames.  In that case the ball is never within the
//   4 cm hit-radius in ANY frame, so proximity detection misses the contact.
//
//   ContinuousSpeculative on the kinematic paddle DOES detect this contact
//   internally (that is its purpose), but it only sends the result as physics
//   collision messages — which we weren't listening to.  This forwarder listens
//   and routes the event to gameplay so we can apply our custom Eleven-TT-style
//   response (full 3D velocity + spin), replacing the plain physics bounce.
//
public class PaddleHitForwarder : MonoBehaviour
{
    public gameplay owner;

    void OnCollisionEnter(Collision col)
    {
        owner?.OnPaddlePhysicsHit(col);
    }
    // OnCollisionStay intentionally removed.
    // It caused repeated hits during slow contact / juggling rests and
    // produced the "double contact" artefact.  OnCollisionEnter alone is
    // sufficient — it fires the moment ContinuousSpeculative detects the sweep.
}
