using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;
using XRInputDevice  = UnityEngine.XR.InputDevice;
using XRCommonUsages = UnityEngine.XR.CommonUsages;
// Do NOT add "using UnityEngine.InputSystem" — causes XR type ambiguities.
// New Input System types are accessed via fully-qualified names only.
#pragma warning disable CS0618

public class gameplay : MonoBehaviour {

    public static int playerPaddleCollision = 0;
    public static int enemyPaddleCollision = 0;

    public GameObject playerside;
    public GameObject enemyside;
    public GameObject avatar;

    public GameObject paddle;
    public GameObject ball;

    public BoxCollider paddleBox;
    public BoxCollider enemypaddleBox;

    public GameObject text_score;
    public GameObject enemyPaddle;

    public float speed;

    [Header("Paddle Handle Calibration")]
    [Tooltip("Offset applied in the controller's LOCAL space. Z- = toward wrist, Z+ = toward blade tip.")]
    public Vector3 paddlePositionOffset = new Vector3(0f, 0f, 0f);
    [Tooltip("Rotation applied in the controller's LOCAL space. Y=180 flips blade to face opponent.")]
    public Vector3 paddleRotationOffset = new Vector3(0f, 180f, 0f);

    private List<XRInputDevice> rightHandDevices = new List<XRInputDevice>();
    private List<XRInputDevice> leftHandDevices  = new List<XRInputDevice>();

    private Vector3    rightPos = Vector3.zero;
    private Quaternion rightRot = Quaternion.identity;
    private Vector3    leftPos  = Vector3.zero;
    private Quaternion leftRot  = Quaternion.identity;
    private bool rightValid = false;
    private bool leftValid  = false;

    private Vector3 _prevWorldPos    = Vector3.zero;
    private Vector3 _paddleVelocity  = Vector3.zero;
    private float   _lastHitTime     = -1f;
    private int     _debugFrame      = 0;

    // Tracking stability gate — paddle is hidden until we see this many
    // consecutive frames with a real non-zero controller position.
    // Prevents the paddle from snapping from (0,0,0) to your hand on launch.
    private const int STABLE_FRAMES_REQUIRED = 10;
    private int  _stableFrames  = 0;
    private bool _trackingReady = false;

    private Rigidbody _paddleRb;
    private Rigidbody _ballRb;
    private Renderer[] _paddleRenderers;

    void OnEnable()
    {
        InputDevices.deviceConnected    += OnDeviceConnected;
        InputDevices.deviceDisconnected += OnDeviceDisconnected;
    }

    void OnDisable()
    {
        InputDevices.deviceConnected    -= OnDeviceConnected;
        InputDevices.deviceDisconnected -= OnDeviceDisconnected;
    }

    private void OnDeviceConnected(XRInputDevice d)    { RefreshDeviceLists(); }
    private void OnDeviceDisconnected(XRInputDevice d) { RefreshDeviceLists(); }

    private void RefreshDeviceLists()
    {
        rightHandDevices.Clear();
        InputDevices.GetDevicesWithCharacteristics(
            InputDeviceCharacteristics.Controller | InputDeviceCharacteristics.Right,
            rightHandDevices);
        leftHandDevices.Clear();
        InputDevices.GetDevicesWithCharacteristics(
            InputDeviceCharacteristics.Controller | InputDeviceCharacteristics.Left,
            leftHandDevices);
    }

    void Start()
    {
        if (paddle != null)
        {
            _paddleRb = paddle.GetComponent<Rigidbody>();
            if (_paddleRb != null)
            {
                _paddleRb.isKinematic = false;
                _paddleRb.useGravity  = false;
            }
            if (paddleBox == null) paddleBox = paddle.GetComponent<BoxCollider>();

            // Hide all renderers until tracking is stable — prevents the
            // paddle from flashing at the world origin on launch.
            _paddleRenderers = paddle.GetComponentsInChildren<Renderer>(true);
            SetPaddleVisible(false);
        }
        if (ball != null)
        {
            _ballRb = ball.GetComponent<Rigidbody>();
            if (_ballRb != null)
            {
                // Real table tennis ball: 2.7 g, very low drag, gravity on
                _ballRb.mass           = 0.0027f;
                _ballRb.linearDamping  = 0.04f;   // light air resistance
                _ballRb.angularDamping = 0.5f;    // spin decay
                _ballRb.useGravity     = true;
            }
            // High-bounciness physics material — mimics celluloid/ABS ball
            // on table and rubber paddle (coefficient of restitution ~0.85)
            var ballCol = ball.GetComponent<Collider>();
            if (ballCol != null)
            {
                var mat = new PhysicsMaterial("Ball");
                mat.bounciness      = 0.85f;
                mat.dynamicFriction = 0.1f;
                mat.staticFriction  = 0.1f;
                mat.bounceCombine   = PhysicsMaterialCombine.Maximum;
                mat.frictionCombine = PhysicsMaterialCombine.Minimum;
                ballCol.material    = mat;
            }
        }

        RefreshDeviceLists();
    }

    private void SetPaddleVisible(bool visible)
    {
        if (_paddleRenderers == null) return;
        foreach (var r in _paddleRenderers) r.enabled = visible;
    }

    // ── Controller pose resolution ─────────────────────────────────────────
    private void UpdateControllerPoses()
    {
        rightValid = false;
        leftValid  = false;

        // 1. XRNode
        Vector3    rp = InputTracking.GetLocalPosition(XRNode.RightHand);
        Quaternion rr = InputTracking.GetLocalRotation(XRNode.RightHand);
        if (rp.sqrMagnitude > 0.0001f || rr != Quaternion.identity)
        { rightPos = rp; rightRot = rr; rightValid = true; }

        Vector3    lp = InputTracking.GetLocalPosition(XRNode.LeftHand);
        Quaternion lr = InputTracking.GetLocalRotation(XRNode.LeftHand);
        if (lp.sqrMagnitude > 0.0001f || lr != Quaternion.identity)
        { leftPos = lp; leftRot = lr; leftValid = true; }

        // 2. Legacy InputDevices
        if (rightHandDevices.Count == 0 || leftHandDevices.Count == 0) RefreshDeviceLists();
        if (!rightValid)
            foreach (var d in rightHandDevices)
            {
                bool gp = d.TryGetFeatureValue(XRCommonUsages.devicePosition, out rightPos);
                bool gr = d.TryGetFeatureValue(XRCommonUsages.deviceRotation, out rightRot);
                if (gp && gr && rightPos != Vector3.zero) { rightValid = true; break; }
            }
        if (!leftValid)
            foreach (var d in leftHandDevices)
            {
                bool gp = d.TryGetFeatureValue(XRCommonUsages.devicePosition, out leftPos);
                bool gr = d.TryGetFeatureValue(XRCommonUsages.deviceRotation, out leftRot);
                if (gp && gr && leftPos != Vector3.zero) { leftValid = true; break; }
            }

        // 3. New Input System scan
#if ENABLE_INPUT_SYSTEM
        if (!rightValid || !leftValid) ScanNewInputDevices();
#endif

        // 4. OVRInput (Android APK)
        if (!rightValid || !leftValid)
            try
            {
                if (!rightValid)
                {
                    Vector3    p = OVRInput.GetLocalControllerPosition(OVRInput.Controller.RTouch);
                    Quaternion r = OVRInput.GetLocalControllerRotation(OVRInput.Controller.RTouch);
                    if (p.sqrMagnitude > 0.0001f) { rightPos = p; rightRot = r; rightValid = true; }
                }
                if (!leftValid)
                {
                    Vector3    p = OVRInput.GetLocalControllerPosition(OVRInput.Controller.LTouch);
                    Quaternion r = OVRInput.GetLocalControllerRotation(OVRInput.Controller.LTouch);
                    if (p.sqrMagnitude > 0.0001f) { leftPos = p; leftRot = r; leftValid = true; }
                }
            }
            catch { }
    }

#if ENABLE_INPUT_SYSTEM
    private void ScanNewInputDevices()
    {
        foreach (var dev in UnityEngine.InputSystem.InputSystem.devices)
        {
            bool looksRight = false, looksLeft = false;
            foreach (var u in dev.usages)
            {
                string us = u.ToString();
                if (us.Contains("Right")) looksRight = true;
                if (us.Contains("Left"))  looksLeft  = true;
            }
            if (!looksRight && !looksLeft)
            {
                string dn = dev.name.ToLower();
                if (dn.Contains("right"))     looksRight = true;
                else if (dn.Contains("left")) looksLeft  = true;
            }
            if (!looksRight && !looksLeft) continue;

            var xrc = dev as UnityEngine.InputSystem.XR.XRController;
            if (xrc != null)
            {
                Vector3    p = xrc.devicePosition.ReadValue();
                Quaternion r = xrc.deviceRotation.ReadValue();
                bool ok = p.sqrMagnitude > 0.0001f;
                if (looksRight && !rightValid && ok) { rightPos = p; rightRot = r; rightValid = true; }
                if (looksLeft  && !leftValid  && ok) { leftPos  = p; leftRot  = r; leftValid  = true; }
                continue;
            }
            var pc = dev["devicePosition"] as UnityEngine.InputSystem.Controls.Vector3Control;
            var rc = dev["deviceRotation"] as UnityEngine.InputSystem.Controls.QuaternionControl;
            if (pc != null && rc != null)
            {
                Vector3    p = pc.ReadValue();
                Quaternion r = rc.ReadValue();
                bool ok = p.sqrMagnitude > 0.0001f;
                if (looksRight && !rightValid && ok) { rightPos = p; rightRot = r; rightValid = true; }
                if (looksLeft  && !leftValid  && ok) { leftPos  = p; leftRot  = r; leftValid  = true; }
            }
        }
    }
#endif

    private float GetRightTrigger()
    {
        float v = 0f;
        try { v = OVRInput.Get(OVRInput.RawAxis1D.RHandTrigger); } catch {}
        if (v > 0.01f) return v;
        foreach (var d in rightHandDevices)
            if (d.TryGetFeatureValue(XRCommonUsages.trigger, out v)) return v;
        return 0f;
    }

    private float GetLeftTrigger()
    {
        float v = 0f;
        try { v = OVRInput.Get(OVRInput.RawAxis1D.LHandTrigger); } catch {}
        if (v > 0.01f) return v;
        foreach (var d in leftHandDevices)
            if (d.TryGetFeatureValue(XRCommonUsages.trigger, out v)) return v;
        return 0f;
    }

    void Update()
    {
        try { OVRInput.Update(); } catch { }

        UpdateControllerPoses();

        if (ballbounce.enemyFlag) follow_ball();
        if (ballbounce.resetFlag) reset_enemy_paddle();

        float leftTrigger  = GetLeftTrigger();
        float rightTrigger = GetRightTrigger();

        // ── Stability gate — count consecutive frames of real tracking data ──
        // rightPos must be clearly non-zero (hand at least ~10 cm from origin).
        if (rightValid && rightPos.sqrMagnitude > 0.01f)   // 0.01 = 10 cm squared
            _stableFrames++;
        else
            _stableFrames = 0;   // reset on any bad frame

        if (!_trackingReady && _stableFrames >= STABLE_FRAMES_REQUIRED)
        {
            _trackingReady = true;
            SetPaddleVisible(true);
            // Initialise prevWorldPos so velocity starts at zero, not garbage
            _prevWorldPos = rightPos + rightRot * paddlePositionOffset;
            Debug.Log("[gameplay] Tracking stable — paddle revealed.");
        }

        // ── Place paddle at controller position + local-space offset ──────
        if (_trackingReady && rightValid && paddle != null)
        {
            Vector3    finalPos = rightPos + rightRot * paddlePositionOffset;
            Quaternion finalRot = rightRot * Quaternion.Euler(paddleRotationOffset);

            paddle.transform.position = finalPos;
            paddle.transform.rotation = finalRot;

            // Velocity for physics (world-space delta)
            _paddleVelocity = (finalPos - _prevWorldPos) / Time.deltaTime;
            _prevWorldPos   = finalPos;
        }
        else if (!_trackingReady && paddle != null)
        {
            // Tracking not yet stable — keep paddle hidden, park it off-screen
            paddle.transform.position = Vector3.down * 100f;
        }

        // Debug line every ~1.5 s
        if (++_debugFrame % 90 == 0)
            Debug.Log($"[gameplay] valid={rightValid} pos={rightPos:F2}  rightHand={rightHandDevices.Count}");

        // ── Physics & collision ────────────────────────────────────────────
        if (_trackingReady && rightValid && paddle != null)
        {
            if (_paddleRb != null && !_paddleRb.isKinematic)
                _paddleRb.linearVelocity = _paddleVelocity;

            if (ballbounce.followPlayer == 1 && enemyPaddle != null)
            {
                enemyPaddle.transform.position = new Vector3(rightPos.x, rightPos.y, -1.5f * rightPos.z);
                enemyPaddle.transform.localRotation = new Quaternion(
                    rightRot.x, rightRot.y * -1f, rightRot.z * -1f, rightRot.w);
                Rigidbody eRb = enemyPaddle.GetComponent<Rigidbody>();
                if (eRb != null && !eRb.isKinematic)
                    eRb.linearVelocity = _paddleVelocity;
            }

            if (paddleBox != null && ball != null)
            {
                Vector3 closest  = paddleBox.ClosestPointOnBounds(ball.transform.position);
                float   distance = Vector3.Distance(closest, ball.transform.position);

                // 0.15 s cooldown prevents the same swing triggering twice
                if (distance < 0.05f && _ballRb != null && Time.time > _lastHitTime + 0.15f)
                {
                    // ── Physics-correct moving-wall collision ─────────────────
                    // Models rubber-on-ball with restitution 0.78 (ITT standard).
                    // Swing speed drives exit speed, just like Eleven Table Tennis.
                    const float restitution = 0.78f;
                    Vector3 paddleNormal  = paddle.transform.up;

                    Vector3 ballNormComp   = Vector3.Project(_ballRb.linearVelocity, paddleNormal);
                    Vector3 ballTangComp   = _ballRb.linearVelocity - ballNormComp;
                    Vector3 paddleNormComp = Vector3.Project(_paddleVelocity, paddleNormal);

                    // Relative velocity reflected + paddle velocity imparted
                    Vector3 newNorm = paddleNormComp
                                    - restitution * (ballNormComp - paddleNormComp);
                    Vector3 newVel  = newNorm + ballTangComp;

                    // Minimum speed — a gentle tap still clears the net
                    if (newVel.magnitude < 1.5f)
                        newVel = paddleNormal * 1.5f;

                    // Maximum speed — caps hard smashes at a playable level
                    if (newVel.magnitude > 14f)
                        newVel = newVel.normalized * 14f;

                    _ballRb.linearVelocity = newVel;
                    _lastHitTime           = Time.time;
                    playerPaddleCollision  = 1;
                }
            }
        }

        // ── Ball serve ─────────────────────────────────────────────────────
        if (leftTrigger > 0.2f && leftValid && ball != null)
        {
            if (ballbounce.state == ballbounce.gameState.playerStart)
            {
                if (_ballRb != null) _ballRb.linearVelocity = Vector3.zero;
                ball.transform.position = leftPos;
            }
        }
    }

    public void follow_ball()
    {
        if (ballbounce.followPlayer == 0 && enemyPaddle != null)
        {
            speed = 5.0f;
            enemyPaddle.transform.position = Vector3.MoveTowards(
                enemyPaddle.transform.localPosition,
                ball.transform.localPosition,
                speed * Time.deltaTime);

            if (enemypaddleBox == null)
                enemypaddleBox = enemyPaddle.GetComponent<BoxCollider>();

            if (enemypaddleBox != null && ball != null)
            {
                Vector3 closest = enemypaddleBox.ClosestPointOnBounds(ball.transform.position);
                if (Vector3.Distance(closest, ball.transform.position) < 0.2f && _ballRb != null)
                {
                    _ballRb.linearVelocity =
                        enemyPaddle.transform.up      * 3f +
                        enemyPaddle.transform.forward * 3f;
                    enemyPaddleCollision = 1;
                }
            }
        }
    }

    public void reset_enemy_paddle()
    {
        if (enemyPaddle != null)
            enemyPaddle.transform.position = new Vector3(0.7f, 0.7f, 1.2f);
    }
}
