// ControllerHapticManager.cs
// Right Quest controller vibration for Multimodal condition (Group 3) only.
//
// MECHANISM
//   Unity XR InputDevice.SendHapticImpulse(channel, amplitude, duration)
//   This is the correct API for Quest 3 + OpenXR.  The legacy
//   OVRInput.SetControllerVibration is not reliable with the OpenXR backend.
//   SendHapticImpulse manages its own duration — no per-frame stop timer needed.
//
// EVENTS
//   gameplay.OnValidPaddleHit      → short amplitude-scaled pulse on right controller
//   gameplay.OnPaddleTableContact  → 0.10 s pulse; amplitude 0.55–1.0 by depth
//
// CONDITION GATE
//   Only Group 3 (Multimodal) receives controller vibration.
//   Groups 1 and 2 are silently skipped.

using UnityEngine;
using UnityEngine.XR;
using System.Collections.Generic;

[DisallowMultipleComponent]
public class ControllerHapticManager : MonoBehaviour
{
    // ── Inspector — Settings ───────────────────────────────────────────────────

    [Header("Haptic Settings")]
    [Tooltip("Duration of the paddle-hit vibration pulse (seconds). " +
             "0.03–0.06 s recommended for a crisp tap.")]
    public float pulseDuration = 0.04f;

    [Tooltip("Duration of the paddle-table contact pulse (seconds).")]
    public float paddleTablePulseDuration = 0.10f;

    [Tooltip("Master enable toggle.  Turning this off prevents all controller haptics.")]
    public bool enableControllerHaptics = true;

    [Header("Inspector Diagnostics (read-only)")]
    [SerializeField] private int   _diagGroup;
    [SerializeField] private bool  _diagSessionActive;
    [SerializeField] private bool  _diagHapticActive;
    [SerializeField] private float _diagLastAmp;
    [SerializeField] private bool  _diagDeviceValid;

    // ── Condition gate ─────────────────────────────────────────────────────────
    private bool HapticActive =>
        enableControllerHaptics &&
        ExperimentLogger.SessionActive &&
        ExperimentLogger.GroupNumber == 3;

    // ── XR device lookup ───────────────────────────────────────────────────────
    private static InputDevice GetRightController()
    {
        var devices = new List<InputDevice>();
        InputDevices.GetDevicesAtXRNode(XRNode.RightHand, devices);
        return devices.Count > 0 ? devices[0] : default;
    }

    // ── Lifecycle ──────────────────────────────────────────────────────────────

    void OnEnable()
    {
        gameplay.OnValidPaddleHit     += HandlePaddleHit;
        gameplay.OnPaddleTableContact += HandlePaddleTableContact;
    }

    void OnDisable()
    {
        gameplay.OnValidPaddleHit     -= HandlePaddleHit;
        gameplay.OnPaddleTableContact -= HandlePaddleTableContact;
        StopVibration();
    }

    // ── Event handlers ─────────────────────────────────────────────────────────

    void HandlePaddleHit(float effectiveSpd, float spinMag)
    {
        if (!HapticActive) return;

        float t         = Mathf.InverseLerp(1f, 10f, effectiveSpd);
        float amplitude = Mathf.Clamp01(Mathf.Lerp(0.2f, 0.8f, t) + spinMag * 0.001f);

        var device = GetRightController();
        if (device.isValid)
            device.SendHapticImpulse(0, amplitude, pulseDuration);

        _diagLastAmp   = amplitude;
        _diagDeviceValid = device.isValid;

        ExperimentLogger.Instance?.LogHapticEvent("Controller_PaddleHit");
        Debug.Log(
            $"[CTRL-HAPTIC] event=PaddleHit" +
            $" amp={amplitude:F2}" +
            $" duration={pulseDuration:F3}" +
            $" group={ExperimentLogger.GroupNumber}" +
            $" deviceValid={device.isValid}"
        );
    }

    void HandlePaddleTableContact(float depth)
    {
        float amplitude = Mathf.Lerp(0.55f, 1.0f, Mathf.Clamp01(depth / 0.01f));
        float duration  = paddleTablePulseDuration;

        var device = GetRightController();

        // Log BEFORE early return so the chain is always visible in adb logcat.
        Debug.Log(
            $"[PADDLE-TABLE-HAPTIC] receiver=Controller" +
            $" depth={depth:F4}" +
            $" active={HapticActive}" +
            $" group={ExperimentLogger.GroupNumber}" +
            $" session={ExperimentLogger.SessionActive}" +
            $" amp={amplitude:F2}" +
            $" duration={duration:F2}" +
            $" deviceValid={device.isValid}"
        );

        if (!HapticActive) return;

        if (device.isValid)
            device.SendHapticImpulse(0, amplitude, duration);

        _diagLastAmp     = amplitude;
        _diagDeviceValid = device.isValid;

        ExperimentLogger.Instance?.LogHapticEvent("Controller_PaddleTable");
    }

    // ── Per-frame: update inspector diagnostics ────────────────────────────────

    void Update()
    {
        _diagGroup         = ExperimentLogger.GroupNumber;
        _diagSessionActive = ExperimentLogger.SessionActive;
        _diagHapticActive  = HapticActive;
    }

    // ── Helper ─────────────────────────────────────────────────────────────────

    void StopVibration()
    {
        var device = GetRightController();
        if (device.isValid)
            device.StopHaptics();
    }
}
