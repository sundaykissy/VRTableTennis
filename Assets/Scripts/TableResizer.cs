using UnityEngine;
using UnityEngine.XR;
using System.Collections.Generic;
#pragma warning disable CS0618

// Attach to the table root GameObject.
// Hold the RIGHT grip button to enter height-adjust mode.
// Move the right controller up/down to move the table up/down.
// Release grip to lock the table at the current height.
// No scaling. No X/Z movement. Physics and colliders are unaffected.
public class TableResizer : MonoBehaviour
{
    [Tooltip("How much the table moves per metre of controller movement.")]
    public float verticalSensitivity = 1.0f;

    [Tooltip("Lowest allowed table Y position (world space).")]
    public float minY = 0.3f;

    [Tooltip("Highest allowed table Y position (world space).")]
    public float maxY = 1.6f;

    // ── Private state ─────────────────────────────────────────────────────────
    private List<InputDevice> _rightDevices = new List<InputDevice>();

    private bool    _adjusting        = false;
    private float   _startControllerY = 0f;
    private float   _startTableY      = 0f;

    private Renderer[] _renderers;

    // ── Lifecycle ─────────────────────────────────────────────────────────────
    void Start()
    {
        _renderers = GetComponentsInChildren<Renderer>();
    }

    void Update()
    {
        // ── Refresh device list ───────────────────────────────────────────────
        _rightDevices.Clear();
        InputDevices.GetDevicesWithCharacteristics(
            InputDeviceCharacteristics.Controller | InputDeviceCharacteristics.Right,
            _rightDevices);

        // ── Right grip value ──────────────────────────────────────────────────
        float rightGrip = 0f;
        if (_rightDevices.Count > 0)
            _rightDevices[0].TryGetFeatureValue(CommonUsages.grip, out rightGrip);

        bool gripHeld = rightGrip > 0.5f;

        // ── Right controller Y position ───────────────────────────────────────
        Vector3 rightPos = Vector3.zero;
        bool    gotRight = false;

        if (_rightDevices.Count > 0)
            gotRight = _rightDevices[0].TryGetFeatureValue(
                CommonUsages.devicePosition, out rightPos);

        // XRNode fallback (Oculus Link / OpenXR on PC)
        if (!gotRight || rightPos.sqrMagnitude < 0.0001f)
        {
            rightPos = InputTracking.GetLocalPosition(XRNode.RightHand);
            gotRight = rightPos.sqrMagnitude > 0.0001f;
        }

        if (!gotRight) return;

        // ── Grip state transitions ────────────────────────────────────────────
        if (gripHeld && !_adjusting)
        {
            _adjusting        = true;
            _startControllerY = rightPos.y;
            _startTableY      = transform.position.y;
            SetHighlight(true);
            Debug.Log($"[TableResizer] Adjust started — controller Y={_startControllerY:F3} " +
                      $"table Y={_startTableY:F3}");
        }
        else if (!gripHeld && _adjusting)
        {
            _adjusting = false;
            SetHighlight(false);
            Debug.Log($"[TableResizer] Locked table at Y={transform.position.y:F3}");
        }

        // ── Height adjustment ─────────────────────────────────────────────────
        if (_adjusting)
        {
            float delta   = (rightPos.y - _startControllerY) * verticalSensitivity;
            float targetY = Mathf.Clamp(_startTableY + delta, minY, maxY);

            Vector3 pos = transform.position;
            pos.y             = targetY;
            transform.position = pos;
        }
    }

    // ── Visual feedback ───────────────────────────────────────────────────────
    void SetHighlight(bool on)
    {
        foreach (var r in _renderers)
        {
            if (r == null) continue;
            foreach (var mat in r.materials)
            {
                Color c = mat.color;
                c.a       = on ? 0.55f : 1.0f;
                mat.color = c;
            }
        }
    }
}
