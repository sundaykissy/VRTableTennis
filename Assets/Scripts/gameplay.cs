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
    private Vector3 _prevWorldPos   = Vector3.zero;
    private Vector3 _paddleVelocity = Vector3.zero;
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

    // ── Table / serve tracking ────────────────────────────────────────────────
    private float _tableTopY = 0.76f;   // world-space Y of table surface (cached in Start)
    private float _netZ      = 0f;      // world-space Z of net centre (cached in Start)
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
    private const float SPIN_COEFF    = 60f;     // rad/s per m/s of tangential brush velocity
    private const float MAX_SPIN      = 300f;    // rad/s cap — prevents physics instability over long sessions

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

    // ── Audio ─────────────────────────────────────────────────────────────────
    private AudioSource _paddleAudio;
    private AudioClip   _paddleHitClip;

    // ── Events ────────────────────────────────────────────────────────────────
    void OnEnable()
    {
        InputDevices.deviceConnected    += OnDeviceChange;
        InputDevices.deviceDisconnected += OnDeviceChange;
    }
    void OnDisable()
    {
        InputDevices.deviceConnected    -= OnDeviceChange;
        InputDevices.deviceDisconnected -= OnDeviceChange;
    }
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

            _paddleHitClip = CreatePaddleClickClip();
            _paddleAudio   = paddle.GetComponent<AudioSource>();
            if (_paddleAudio == null) _paddleAudio = paddle.AddComponent<AudioSource>();
            _paddleAudio.clip         = _paddleHitClip;
            _paddleAudio.spatialBlend = 1f;
            _paddleAudio.volume       = 0.9f;
            _paddleAudio.playOnAwake  = false;
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
        }
        Debug.Log($"[gameplay] Table top Y={_tableTopY:F3} m  Net Z={_netZ:F3}");
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

        if (++_debugFrame % 90 == 0)
        {
            Debug.Log($"[gameplay] rightValid={_rightValid} rightPos={_rightPos:F2} " +
                      $"leftValid={_leftValid} leftPos={_leftPos:F2} " +
                      $"state={ballbounce.state} phase={_servePhase} session={ExperimentLogger.SessionActive}");

            // Per-request: paddle tracking diagnostic (every ~1.5 s at 60 fps)
            if (paddle != null)
                Debug.Log($"[RIGHT PADDLE] tracking={_trackingReady} " +
                          $"rightValid={_rightValid} " +
                          $"pos={paddle.transform.position.ToString("F2")} " +
                          $"anchor={(_rightAnchor != null ? _rightAnchor.name : "none")}");
        }

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

        if (_rightValid)
        {
            Vector3    fp = _rightPos + _rightRot * paddlePositionOffset;
            Quaternion fr = _rightRot  * Quaternion.Euler(paddleRotationOffset);

            // MovePosition/MoveRotation on a kinematic Rigidbody: the physics
            // engine sweeps the collider from old→new position every step.
            _prevPaddlePos      = _paddleRb.position;   // record BEFORE MovePosition
            _prevPaddleRot      = _paddleRb.rotation;   // record BEFORE MoveRotation
            _currentPaddlePos   = fp;
            _currentPaddleRot   = fr;
            _prevPaddlePosValid = true;
            _paddleRb.MovePosition(fp);
            _paddleRb.MoveRotation(fr);

            // Paddle velocity — OVR hardware velocity preferred; position delta fallback.
            Vector3 ovrVel = Vector3.zero;
            try { ovrVel = OVRInput.GetLocalControllerVelocity(OVRInput.Controller.RTouch); } catch { }

            // Guard against tracking-dropout velocity spikes.
            // If tracking was lost last step (_prevRightValid=false) or if the
            // position delta is larger than a realistic maximum swing (0.2 m per
            // step = 24 m/s at 120 Hz), _prevWorldPos is stale.  Using the raw
            // delta would produce an inflated velocity and fire a ghost paddle hit.
            // In that case prefer OVR velocity (measured by the hardware IMU) or
            // zero; never the stale positional delta.
            Vector3 posDelta = fp - _prevWorldPos;
            const float MAX_STEP_M = 0.2f;   // 0.2 m / (1/120 s) = 24 m/s — generous cap
            bool  deltaValid = _prevRightValid && posDelta.magnitude <= MAX_STEP_M;
            _paddleVelocity = ovrVel.sqrMagnitude > 0.0001f
                ? ovrVel
                : (deltaValid ? posDelta / Mathf.Max(Time.fixedDeltaTime, 0.0001f) : Vector3.zero);
            _prevWorldPos    = fp;
            _prevRightValid  = true;
        }
        else
        {
            _prevRightValid = false;
        }

        // ── Hit detection ─────────────────────────────────────────────────────
        if (!_trackingReady || !_rightValid || ball == null || _ballRb == null)
        {
            _prevBallPos = (_ballRb != null) ? _ballRb.position : Vector3.zero;
            return;
        }
        if (!ExperimentLogger.SessionActive || !_hitWindowOpen || paddleBox == null)
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

            // ── PaddleTableContact: edge-detect — only on entering edge ───────
            bool nowBelow = distToTable < 0f;
            if (nowBelow && _paddleWasAboveTable)
            {
                float penetration = -distToTable;
                ExperimentLogger.Instance?.LogPaddleTableContact(penetration);
                Debug.Log($"[gameplay] PaddleTableContact penetration={penetration:F4} m");
            }
            _paddleWasAboveTable = !nowBelow;
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

        // ClosestPoint returns the ball center itself when the ball is fully inside
        // the box (dist = 0).  Each layer below re-evaluates the cooldown directly
        // from _lastHitTime so a hit registered by an earlier layer is immediately
        // visible to the later layers in the same FixedUpdate call.
        Vector3 closest = paddleBox.ClosestPoint(ballPos);
        float   dist    = Vector3.Distance(closest, ballPos);

        // ── Hit detection: four layers, each re-checks cooldown from _lastHitTime ──
        //
        // ApplyPaddleHit updates _lastHitTime immediately, so every layer after
        // the first that fires will see a fresh cooldown and skip — naturally
        // preventing double-fires without any shared bool.

        // ── Layer 0 — OverlapBox (current frame) ─────────────────────────────────
        // Checks whether the ball is physically INSIDE the paddle volume right now.
        // This catches ALL tunneling scenarios that Layers 1-3 can miss:
        //   • Relative-motion tunneling: ball and paddle both flying fast toward each
        //     other so neither center sweep covers the crossing.
        //   • Pure rotational swing: wrist flick moves the paddle face through a large
        //     arc while the center barely translates → Layer 3 BoxCastAll sweep is tiny.
        // Run this before every other layer so tunneled balls get fixed up immediately.
        // _ballSeparated: only fire if ball has left the paddle zone since the last hit.
        if (Time.time > _lastHitTime + HIT_COOLDOWN && _ballSeparated)
        {
            // Use Rigidbody position/rotation — NOT the Transform — as the single
            // source of truth for where the physics collider actually is.
            // Transform can lag the Rigidbody when interpolation is enabled, and
            // any stale transform write from outside FixedUpdate would cause a mismatch.
            Vector3    rbBoxCtr = _paddleRb.position + _paddleRb.rotation * paddleBox.center;
            Quaternion rbRot    = _paddleRb.rotation;
            // Half extents: paddle box size × lossyScale, then add 1 cm margin on
            // every axis.  The margin catches balls whose centres are just outside
            // a corner/edge by a few mm — enough to prevent rare missed contacts
            // without creating false ghost hits (those are already gated by
            // _ballSeparated and HIT_COOLDOWN).
            const float OVERLAP_MARGIN = 0.01f;   // 1 cm — ball radius is ~2 cm
            Vector3 halfExt = Vector3.Scale(paddleBox.size * 0.5f,
                                  new Vector3(
                                      Mathf.Abs(paddleBox.transform.lossyScale.x),
                                      Mathf.Abs(paddleBox.transform.lossyScale.y),
                                      Mathf.Abs(paddleBox.transform.lossyScale.z)))
                              + Vector3.one * OVERLAP_MARGIN;
            Collider[] overlaps = Physics.OverlapBox(
                rbBoxCtr, halfExt, rbRot,
                ~0, QueryTriggerInteraction.Ignore);
            foreach (var oc in overlaps)
            {
                if (oc.attachedRigidbody == _ballRb)
                {
                    ApplyPaddleHit(paddleBox.ClosestPoint(ballPos));
                    Debug.Log($"[gameplay] HIT via OverlapBox padVel={_paddleVelocity.magnitude:F1}");
                    break;
                }
            }
        }

        // ── Layer 1 — proximity + surface-velocity approach guard ─────────────────
        // Distance alone causes ghost hits (ball jumps away before the paddle face
        // visually arrives).  The approach guard prevents this by requiring that
        // either the ball or paddle is genuinely moving toward contact.
        //
        // IMPORTANT: use SURFACE velocity, not just center velocity.
        // During a fast wrist flick, the paddle center barely moves (~0 m/s) but the
        // blade face sweeps at 2–5 m/s.  v_surface = v_center + ω × r, where r is
        // the lever arm from the paddle box center to the contact point.
        if (dist < HIT_RADIUS && Time.time > _lastHitTime + HIT_COOLDOWN && _ballSeparated)
        {
            Vector3 towardPaddle = closest - ballPos;
            float   tpLen        = towardPaddle.magnitude;
            Vector3 tpDir        = tpLen > 1e-5f ? towardPaddle / tpLen : Vector3.zero;

            // Angular velocity in world space (OVR gives local-controller space).
            Vector3 ovrAngVel = Vector3.zero;
            try { ovrAngVel = _rightRot * OVRInput.GetLocalControllerAngularVelocity(OVRInput.Controller.RTouch); } catch { }

            // Lever arm from the paddle box world-centre to the contact point.
            // Use Rigidbody position/rotation — not Transform — for consistency with Layer 0.
            Vector3 boxCtr      = _paddleRb.position + _paddleRb.rotation * paddleBox.center;
            Vector3 leverArm    = closest - boxCtr;
            Vector3 surfaceVel  = _paddleVelocity + Vector3.Cross(ovrAngVel, leverArm);

            float padApproach  = Vector3.Dot(surfaceVel, tpDir);               // >0: surface moving toward ball
            float ballApproach = Vector3.Dot(_ballRb.linearVelocity, tpDir);   // >0: ball moving toward paddle

            // Threshold lowered from 0.3 → 0.05 m/s.
            // The original 0.3 f guard was over-conservative: for a pure wrist flick
            // the linear centre velocity is near-zero and Cross(ovrAngVel, leverArm)
            // can be near-perpendicular to tpDir for edge contacts, giving padApproach
            // ≈ 0 even though the face is sweeping at 4–5 m/s.
            // _ballSeparated (set false in ApplyPaddleHit until ball exits 8 cm) is
            // already the primary multi-fire guard, so this can safely be very loose.
            if (padApproach > 0.05f || ballApproach > 0.05f)
                ApplyPaddleHit(closest);
        }

        // ── Layer 2 — ball-path SphereCast ───────────────────────────────────────
        // Catches fast-moving BALL that travels entirely through the paddle in one
        // physics step.  Uses hit.point (the actual surface crossing point) rather
        // than ClosestPoint(ballPos) which returns the wrong face when the ball is
        // already on the far side.
        if (Time.time > _lastHitTime + HIT_COOLDOWN && _prevBallPosValid && _ballSeparated)
        {
            Vector3 sweepDir = ballPos - _prevBallPos;
            float   sweepLen = sweepDir.magnitude;
            if (sweepLen > 0.001f &&
                Physics.SphereCast(_prevBallPos, 0.02f, sweepDir / sweepLen,
                    out RaycastHit hit, sweepLen) &&
                hit.collider == paddleBox)
            {
                ApplyPaddleHit(hit.point);
                Debug.Log($"[gameplay] HIT via ball-sweep padVel={_paddleVelocity.magnitude:F1}");
            }
        }

        // ── Layer 3 — paddle-sweep BoxCastAll ────────────────────────────────────
        // Catches fast-swinging PADDLE translating through a slow/stationary ball.
        // Uses the BoxCollider's actual world-centre (not the Rigidbody pivot) and
        // BoxCastAll so a table or net hit earlier in the sweep doesn't hide the ball.
        // Note: this layer handles TRANSLATIONAL sweeps.  ROTATIONAL fast swings are
        // covered by Layer 0 (OverlapBox) which runs every frame regardless.
        if (Time.time > _lastHitTime + HIT_COOLDOWN && _prevPaddlePosValid && _ballSeparated)
        {
            Vector3 paddleDelta   = _currentPaddlePos - _prevPaddlePos;
            float   paddleMoveLen = paddleDelta.magnitude;
            if (paddleMoveLen > 0.001f)
            {
                Vector3      halfExt3    = paddleBox.size * 0.5f;
                Vector3      prevBoxCtr  = _prevPaddlePos + _prevPaddleRot * paddleBox.center;
                RaycastHit[] allHits     = Physics.BoxCastAll(
                    prevBoxCtr, halfExt3, paddleDelta.normalized,
                    _prevPaddleRot, paddleMoveLen,
                    ~0, QueryTriggerInteraction.Ignore);
                foreach (var bHit in allHits)
                {
                    if (bHit.rigidbody == _ballRb)
                    {
                        ApplyPaddleHit(bHit.point);
                        Debug.Log($"[gameplay] HIT via BoxCast padVel={paddleMoveLen / Time.fixedDeltaTime:F1}");
                        break;
                    }
                }
            }
        }

        // ── Separation update ─────────────────────────────────────────────────
        // Once the ball moves > SEPARATION_DIST from the paddle surface after a
        // hit, unlock the next contact session.  dist was computed above.
        if (!_ballSeparated && dist > SEPARATION_DIST)
            _ballSeparated = true;

        _prevBallPos      = ballPos;
        _prevBallPosValid = !_ballRb.isKinematic;
    }

    // Called by PaddleHitForwarder when the physics engine detects a collision
    // between the paddle and ball via ContinuousSpeculative sweep.
    // This is Layer 4 — catches any fast swing that slips past all three FixedUpdate layers.
    public void OnPaddlePhysicsHit(Collision col)
    {
        if (!_hitWindowOpen || !ExperimentLogger.SessionActive) return;
        if (ball == null || col.gameObject != ball) return;
        if (_ballRb == null || Time.time < _lastHitTime + HIT_COOLDOWN) return;
        if (!_ballSeparated) return;   // still in same contact session — separation required

        // Layer 4 — physics engine contact (ContinuousSpeculative).
        // OnCollisionEnter fires AFTER FixedUpdate in the same physics step.
        // This means Time.time is identical to when a FixedUpdate layer just set
        // _lastHitTime, so the time-based cooldown alone cannot block a same-step
        // double-fire (Time.time < _lastHitTime + 0.08 evaluates to 0 < 0.08 → true,
        // but the subtraction yields exactly 0, so the guard fails).
        // The frame check closes this gap: if FixedUpdate already registered a hit
        // in this physics step, _lastHitFrame == Time.frameCount and we skip.
        if (Time.frameCount == _lastHitFrame) return;
        Vector3 contactPt = col.contactCount > 0
            ? col.GetContact(0).point
            : (paddleBox != null ? paddleBox.ClosestPoint(_ballRb.position) : paddle.transform.position);

        ApplyPaddleHit(contactPt);
        Debug.Log($"[gameplay] HIT via physics collision padVel={_paddleVelocity.magnitude:F1}");
    }

    // ── Paddle hit physics ────────────────────────────────────────────────────
    //
    //  Physics reflection — matches Eleven Table Tennis feel:
    //
    //    1. Reflect the ball's incoming velocity off the paddle surface normal.
    //       (Ball falling at 3 m/s, paddle stationary → ball leaves at 3 m/s upward.)
    //    2. Add the paddle's velocity projected onto the normal, scaled by HIT_PAD_SCALE.
    //       (Swinging the paddle hard → ball goes faster in that direction.)
    //    3. Enforce a minimum outgoing speed (HIT_BASE_SPD) so even a dead-still
    //       tap sends the ball somewhere playable.
    //    4. Hard-cap total speed at HIT_MAX_SPD.
    //
    //  Result: juggling feels natural (gentle taps = low arc, hard flicks = fast shots).
    //
    // closestSurfacePoint — nearest point on the paddle BoxCollider to the ball center,
    // computed with ClosestPoint() before calling.  Used to derive a contact normal that
    // is correct for ANY paddle rotation without relying on transform.up/forward/right.
    void ApplyPaddleHit(Vector3 closestSurfacePoint)
    {
        // ── Contact normal ────────────────────────────────────────────────────
        // Primary: vector from the closest surface point to the ball center.
        // When tunneling has occurred, the ball is already past the paddle and
        // ClosestPoint() returns the far face — diff then points the WRONG way
        // (away from the player, toward opponent).  We detect this by checking
        // if diff is pointing roughly the same direction the ball is moving, and
        // if so we flip it.  This ensures the reflection is always physically
        // correct regardless of which side of the paddle the ball ends up on.
        Vector3 diff = _ballRb.position - closestSurfacePoint;
        Vector3 contactNormal;
        if (diff.sqrMagnitude > 1e-6f)
        {
            contactNormal = diff.normalized;
            // If the ball is already moving in the same direction as the outward
            // normal (i.e. ball passed through and is now on the far side), flip
            // the normal so the reflection pushes the ball back the correct way.
            Vector3 bv = _ballRb.linearVelocity;
            if (bv.sqrMagnitude > 0.01f && Vector3.Dot(bv.normalized, contactNormal) > 0.7f)
                contactNormal = -contactNormal;
        }
        else
        {
            // Ball center is exactly on the surface (or inside the box).
            // Use the paddle face that the ball approached from — determined by
            // which face normal most opposes the ball's incoming velocity.
            Vector3 inVel = _ballRb.linearVelocity;
            Vector3 pFwd  = paddle.transform.forward;
            contactNormal = Vector3.Dot(inVel, pFwd) > 0f ? -pFwd : pFwd;
        }
        if (contactNormal == Vector3.zero) contactNormal = Vector3.up;

        // ── Snap contact normal to nearest paddle face ────────────────────────
        // Edge/corner contacts produce diagonal normals from ClosestPoint() that
        // cause unpredictable reflections.  Projecting onto the three face axes
        // and picking the dominant one gives a physically plausible result for
        // all contact positions without relying on approximate threshold logic.
        //
        // IMPORTANT: use _paddleRb.rotation, NOT paddle.transform.right/up/forward.
        // MoveRotation() updates _paddleRb.rotation immediately but the Transform is
        // only synced AFTER the physics step completes.  Reading transform.forward
        // here gives axes from the previous step — on a fast wrist flick that lag is
        // 15–30° which is the exact cause of the systematic rightward drift at speed.
        {
            Vector3 pRight   = _paddleRb.rotation * Vector3.right;
            Vector3 pUp      = _paddleRb.rotation * Vector3.up;
            Vector3 pForward = _paddleRb.rotation * Vector3.forward;
            float   dotR     = Mathf.Abs(Vector3.Dot(contactNormal, pRight));
            float   dotU     = Mathf.Abs(Vector3.Dot(contactNormal, pUp));
            float   dotF     = Mathf.Abs(Vector3.Dot(contactNormal, pForward));
            Vector3 dominant = dotF >= dotR && dotF >= dotU ? pForward
                             : dotU >= dotR                 ? pUp
                             :                               pRight;
            float   sign     = Mathf.Sign(Vector3.Dot(contactNormal, dominant));
            if (sign == 0f) sign = 1f;
            contactNormal = dominant * sign;
        }

        Vector3 ballVel = _ballRb.linearVelocity;
        // Note: no approach guard here — the if/else chain in FixedUpdate and the
        // HIT_COOLDOWN gate prevent double-fires.  A guard based on relApproach would
        // incorrectly reject tunneled-ball detections where the normal was already
        // flipped above (the ball IS moving away, but that's the whole point — we
        // need to reverse it).

        // Step 1 — reflect incoming ball velocity off the contact surface.
        float   normalIn  = Mathf.Max(0f, Vector3.Dot(ballVel, -contactNormal));
        Vector3 reflected = ballVel + 2f * normalIn * contactNormal;

        // Step 2 — compute outgoing DIRECTION.
        //
        // Problem with pure reflection: if the ball falls onto a horizontal paddle,
        // reflected = straight up — the player's forward swing barely changes direction.
        // Fix: blend the reflected direction toward the actual paddle SWING direction
        // at higher swing speeds.  This makes "ball goes where you swing" rather than
        // "ball bounces off the face".  At low swing speeds (gentle taps) the face
        // normal still dominates, which is correct.
        float   padSpd    = _paddleVelocity.magnitude;
        Vector3 swingDir  = padSpd > 0.25f ? _paddleVelocity.normalized : contactNormal;

        // Blend 0→50% toward swing direction as speed goes from 0.25 → 3.0 m/s.
        float   swingBlend   = Mathf.Clamp01((padSpd - 0.25f) / 2.75f) * 0.5f;
        Vector3 outDir       = Vector3.Slerp(reflected.normalized, swingDir, swingBlend).normalized;

        // ── Speed — paddle-velocity dominant ─────────────────────────────────
        // OLD additive formula (reflected.magnitude + padSpd * scale) had a fatal
        // flaw: when the ball falls under gravity for 1-3 s, normalIn grows to
        // 15-28 m/s → reflected.magnitude = 2×normalIn = 30-56 → always clamped
        // to 12 regardless of swing intensity.  Every non-trivial hit sounded and
        // felt identical.
        //
        // NEW formula: paddle speed is the primary driver; incoming ball speed
        // contributes only 10% (adds natural "loaded return" feel without the cap
        // domination).  Mapping:
        //   gentle tap  padSpd=0.5 m/s → ≈1.0 m/s ball
        //   normal swing padSpd=3.0 m/s → ≈5.4 m/s ball
        //   hard swing  padSpd=7.0 m/s → ≈12.6 → capped to 12 m/s
        float spd = padSpd * 1.8f + normalIn * 0.10f;

        // Step 3 — clamp upward launch angle to ≤ 35° above horizontal.
        // Use a normalized directional clamp (not a raw Y chop) so the direction
        // vector stays valid.  sin(35°) ≈ 0.574.
        // This replaces the old "if (finalVel.y > 3.0f) finalVel.y = 3.0f" which
        // silently broke the direction on fast upward shots.
        const float MAX_Y_FRACTION = 0.574f;  // sin(35°)
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

        // Soften speed slightly when contact normal is mostly upward (gentle tap).
        float verticalBias = Mathf.Clamp01(Vector3.Dot(contactNormal, Vector3.up));
        spd = Mathf.Lerp(spd, spd * 0.65f, verticalBias);

        // Adaptive minimum speed.
        float minSpd = padSpd >= 0.8f ? 1.2f : padSpd * 1.5f;
        spd = Mathf.Clamp(spd, minSpd, HIT_MAX_SPD);

        _ballRb.linearVelocity = outDir * spd;

        // Push ball clear of the paddle face so the physics solver has no
        // residual penetration to correct on the next step.  Without this,
        // ContinuousSpeculative can generate a separation impulse that fights
        // our velocity assignment, producing the "sticky paddle" feel.
        // Move to contact point + 2.5 cm along the contact normal (ball radius
        // is ~2 cm, so this guarantees no overlap even after position correction).
        _ballRb.position = closestSurfacePoint + contactNormal * 0.025f;

        // Step 4 — spin from tangential (brushing) paddle-surface velocity.
        // Decompose paddle velocity into:
        //   normal component  = push-through the face (no spin contribution)
        //   tangential component = brushing motion → generates spin
        // Cross(normal, tangential) gives the correct axis:
        //   upward brush on upward-facing normal → topspin ✓
        //   downward brush                        → backspin ✓
        //   sideways brush                        → sidespin ✓
        // Blend with existing spin so juggling topspin accumulates naturally.
        // Clamp to MAX_SPIN to prevent physics instability during long sessions —
        // without a cap, repeated hits can push angularVelocity into values that
        // destabilise the physics solver and cause erratic bounces.
        Vector3 nComp      = Vector3.Dot(_paddleVelocity, contactNormal) * contactNormal;
        Vector3 tangential = _paddleVelocity - nComp;
        Vector3 spinDelta  = Vector3.Cross(contactNormal, tangential) * SPIN_COEFF;
        _ballRb.angularVelocity = Vector3.ClampMagnitude(
            _ballRb.angularVelocity * 0.3f + spinDelta, MAX_SPIN);

        _ballRb.useGravity = true;
        _ballSeparated     = false;  // lock out further hits until ball exits paddle zone

        // Restore visibility in case a floor/net collision hid the ball.
        SetBallVisible(true);

        _lastHitTime          = Time.time;
        _lastHitFrame         = Time.frameCount;
        playerPaddleCollision = 1;

        PlayPaddleHitSound(spd);

        // PaddleTiltDeg: raw angle from vertical (0°=flat, 90°=vertical spin stance).
        float paddleTiltDeg = Vector3.Angle(contactNormal, Vector3.up);

        // PaddleAngleErrorDeg: deviation from ideal topspin normal.
        // IDEAL_SPIN_NORMAL = Vector3(0, 0.707, 0.707) — 45° closed toward opponent.
        // 0° = perfect topspin orientation. 90° = flat hit. 180° = opposite face.
        float paddleAngleErrorDeg = Vector3.Angle(contactNormal, IDEAL_SPIN_NORMAL);

        // MinPaddleTableDistM: closest the paddle came to the table this serve.
        // Cap at 9.999 if never within measurable range (player never approached table).
        float minDist = _minPaddleTableDistThisServe < 9f
                      ? _minPaddleTableDistThisServe : 9.999f;

        ExperimentLogger.Instance?.LogPlayerHit(
            spd, tangential.magnitude,
            paddleAngleErrorDeg, paddleTiltDeg,
            minDist);

        Debug.Log($"[gameplay] HIT spd={spd:F1} spin={_ballRb.angularVelocity.magnitude:F0} " +
                  $"tilt={paddleTiltDeg:F1}° angleErr={paddleAngleErrorDeg:F1}° " +
                  $"minTableDist={minDist:F3}m");
    }

    // Sharp percussive click matching real table tennis paddle contact.
    // Multi-harmonic layering (2400 + 4200 + 6800 Hz) with very fast decay (90×)
    // gives the "click" character rather than a sustained ping.
    static AudioClip CreatePaddleClickClip()
    {
        const int   sampleRate = 44100;
        const float duration   = 0.040f;   // 40ms — short percussive attack
        int     samples = Mathf.RoundToInt(sampleRate * duration);
        float[] data    = new float[samples];
        for (int i = 0; i < samples; i++)
        {
            float t   = (float)i / sampleRate;
            float env = Mathf.Exp(-t * 90f);   // very fast decay → click, not ping
            float sig = 0.65f * Mathf.Sin(2f * Mathf.PI * 2400f * t)
                      + 0.25f * Mathf.Sin(2f * Mathf.PI * 4200f * t)
                      + 0.10f * Mathf.Sin(2f * Mathf.PI * 6800f * t);
            data[i] = env * sig;
        }
        var clip = AudioClip.Create("PaddleClick", samples, 1, sampleRate, false);
        clip.SetData(data, 0);
        return clip;
    }

    void PlayPaddleHitSound(float speed)
    {
        if (_paddleAudio == null || _paddleHitClip == null) return;
        // Pitch rises slightly with speed — faster shots sound crisper.
        _paddleAudio.pitch = Mathf.Clamp(0.9f + speed * 0.04f, 0.85f, 1.5f);
        // PlayOneShot allows overlapping audio during fast juggling sequences.
        // Play() would cut the previous click short on every consecutive hit.
        _paddleAudio.PlayOneShot(_paddleHitClip,
            Mathf.Clamp(0.65f + speed * 0.03f, 0.65f, 1.0f));
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

        if (_debugFrame % 60 == 0)
            Debug.Log($"[SERVE] phase={_servePhase} held={triggerHeld} leftValid={_leftValid}");

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
            _ballRb.isKinematic     = true;
            _ballRb.useGravity      = false;
            _ballRb.linearVelocity  = Vector3.zero;   // cleared so toss starts from zero
            _ballRb.angularVelocity = Vector3.zero;
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
                // fights.  Velocity writes are no-ops on kinematic bodies but kept
                // here so the stored state is clean at the moment isKinematic→false.
                _ballRb.linearVelocity  = Vector3.zero;
                _ballRb.angularVelocity = Vector3.zero;
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

                // Stamp position, then switch kinematic→non-kinematic BEFORE
                // assigning velocity.  linearVelocity writes are silently ignored
                // on kinematic bodies — the order matters.
                _ballRb.position                   = ballInHand;
                _ballRb.isKinematic                = false;
                _ballRb.collisionDetectionMode     = CollisionDetectionMode.ContinuousDynamic;
                _ballRb.useGravity                 = true;
                _ballRb.linearVelocity             = tossVel;
                _ballRb.angularVelocity            = Vector3.zero;
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
                ExperimentLogger.Instance?.LogServe(serveDistToNet, prepTime);

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
            _ballRb.isKinematic     = true;
            _ballRb.useGravity      = false;
            _ballRb.linearVelocity  = Vector3.zero;
            _ballRb.angularVelocity = Vector3.zero;
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
