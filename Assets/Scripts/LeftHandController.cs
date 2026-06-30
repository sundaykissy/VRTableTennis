using UnityEngine;
#pragma warning disable CS0618

/// <summary>
/// Positions the left hand at the tracked controller every frame.
/// Reads tracking data from gameplay.cs shared static fields.
///
/// The ball already renders at the left hand position during playerStart,
/// so NO sphere marker is created (it was being mistaken for a second ball).
/// </summary>
public class LeftHandController : MonoBehaviour
{
    [Header("Hand mesh — drag the CustomHandLeft prefab instance here")]
    [Tooltip("Optional. If empty, the hand is invisible (the ball acts as the visual cue during serve).")]
    public GameObject handMesh;

    [Header("Frames of stable tracking before revealing the hand")]
    public int stableFramesRequired = 10;

    private int        _stableFrames  = 0;
    private bool       _trackingReady = false;
    private Renderer[] _renderers;

    // ── Lifecycle ─────────────────────────────────────────────────────────────
    void Start()
    {
        // Only show a hand mesh if one was explicitly assigned in the Inspector.
        // Do NOT create a sphere — it looks identical to the ball and confuses players.
        if (handMesh != null)
        {
            _renderers = handMesh.GetComponentsInChildren<Renderer>(true);
            SetVisible(false);
            Debug.Log("[LeftHand] Hand mesh assigned — waiting for stable tracking.");
        }
        else
        {
            _renderers = new Renderer[0];
            Debug.Log("[LeftHand] No hand mesh assigned — left hand is invisible. " +
                      "The ball serves as the visual cue during serve.");
        }

        Debug.Log("[LeftHand] Started — waiting for stable left controller tracking.");
    }

    void SetVisible(bool v)
    {
        foreach (var r in _renderers) r.enabled = v;
    }

    // ── Every frame ───────────────────────────────────────────────────────────
    void Update()
    {
        Vector3    pos   = gameplay.sharedLeftPos;
        Quaternion rot   = gameplay.sharedLeftRot;
        bool       valid = gameplay.sharedLeftValid;

        if (valid && pos.sqrMagnitude > 0.01f)
            _stableFrames++;
        else
            _stableFrames = 0;

        if (!_trackingReady && _stableFrames >= stableFramesRequired)
        {
            _trackingReady = true;
            if (_renderers.Length > 0)
            {
                SetVisible(true);
                Debug.Log("[LeftHand] Tracking stable — left hand revealed.");
            }
        }

        if (_trackingReady && valid)
        {
            transform.position = pos;
            transform.rotation = rot;
        }
        else if (!_trackingReady)
        {
            transform.position = Vector3.down * 100f;
        }
    }
}
