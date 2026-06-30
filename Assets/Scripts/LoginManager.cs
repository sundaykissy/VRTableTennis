using UnityEngine;
using UnityEngine.XR;
using UnityEngine.UI;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using System.Collections.Generic;
using XRInputDevice   = UnityEngine.XR.InputDevice;
using XRCommonUsages  = UnityEngine.XR.CommonUsages;

/// <summary>
/// VR login screen for the table tennis experiment.
/// Auto-instantiates at runtime — no scene attachment needed.
///
/// INPUT — keyboard (editor) and Quest 3 controller:
///   Keyboard:      Arrow Up/Down = change value | Tab/Arrow Left/Right = next field
///                  Enter = confirm | Space = instant start (debug)
///   Right controller: Thumbstick ↑↓ = change value | Thumbstick ←→ = next field
///                     A = next field / start session
///
/// Fields: Participant ID (1-999) | Group (1-3) | Session (1-20)
/// </summary>
public class LoginManager : MonoBehaviour
{
    // ── Auto-instantiation (once per play session) ────────────────────────────
    private static bool _created = false;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void AutoCreate()
    {
        if (_created) return;
        _created = true;
        var go = new GameObject("[LoginManager]");
        go.AddComponent<LoginManager>();
    }

    // ── UI elements ───────────────────────────────────────────────────────────
    private GameObject _canvasGO;
    private Text       _titleText;
    private Text       _bodyText;
    private Text       _instructionText;
    private Text       _logPathText;

    // ── State ─────────────────────────────────────────────────────────────────
    private int  _participantID = 1;
    private int  _group         = 1;
    private int  _session       = 1;
    private int  _selectedField = 0;   // 0=ID  1=Group  2=Session  3=START

    // Separate flags so LateUpdate keeps repositioning during the 4-s confirmation
    private bool _inputDone  = false;  // stop reading controller / keyboard
    private bool _canvasDone = false;  // stop repositioning canvas (set in HideCanvas)

    private float _thumbCooldown = 0f;
    private float _aCooldown     = 0f;
    private float _keyCooldown   = 0f;
    private const float COOLDOWN = 0.22f;

    // ── XR device fallback ────────────────────────────────────────────────────
    private List<XRInputDevice> _rightDevices = new List<XRInputDevice>();

    // ── Canvas constants ──────────────────────────────────────────────────────
    private readonly Vector2 CANVAS_SIZE = new Vector2(800f, 500f);
    private const float CANVAS_DIST   = 2.0f;   // metres ahead of camera
    private const float CANVAS_Y_OFF  = -0.15f; // slightly below eye level

    // ── Lifecycle ─────────────────────────────────────────────────────────────
    void Start()
    {
        InputDevices.GetDevicesWithCharacteristics(
            InputDeviceCharacteristics.Controller | InputDeviceCharacteristics.Right,
            _rightDevices);

        BuildCanvas();
        RefreshUI();
        Debug.Log("[LoginManager] Login screen active. Space = quick-start (debug).");
    }

    // ── LateUpdate: keep canvas 2 m in front of the camera ───────────────────
    // Uses _canvasDone (not _inputDone) so the panel keeps following
    // the player during the 4-second session-started confirmation.
    void LateUpdate()
    {
        if (_canvasDone || _canvasGO == null) return;

        Camera cam = Camera.main;
        if (cam == null) return;

        Vector3 fwd = cam.transform.forward;
        fwd.y = 0f;
        if (fwd.sqrMagnitude < 0.001f) fwd = cam.transform.right;
        fwd.Normalize();

        _canvasGO.transform.position = cam.transform.position
                                      + fwd * CANVAS_DIST
                                      + Vector3.up * CANVAS_Y_OFF;
        _canvasGO.transform.rotation = Quaternion.LookRotation(fwd);
    }

    // ── Update: input ─────────────────────────────────────────────────────────
    void Update()
    {
        if (_inputDone) return;

        // Guarantee OVRInput data is fresh for this frame regardless of which
        // script ran OVRInput.Update() first (avoids stale reads when LoginManager
        // executes before gameplay.cs in the same frame).
        try { OVRInput.Update(); } catch { }

        _thumbCooldown -= Time.deltaTime;
        _aCooldown     -= Time.deltaTime;
        _keyCooldown   -= Time.deltaTime;

        ReadKeyboard();
        ReadThumbstick();
        ReadThumbstickLR();
        ReadAButton();
        ReadLeftTriggerQuickStart();
    }

    // ── Keyboard input (new Input System) ────────────────────────────────────
    void ReadKeyboard()
    {
        if (_keyCooldown > 0f) return;

        var kb = Keyboard.current;
        if (kb == null) return;

        if (kb.spaceKey.wasPressedThisFrame) { BeginSession(); return; }

        int delta = 0;
        if (kb.upArrowKey.isPressed)   delta =  1;
        if (kb.downArrowKey.isPressed) delta = -1;
        if (delta != 0 && _selectedField < 3)
        {
            ApplyValueDelta(delta);
            _keyCooldown = COOLDOWN;
            RefreshUI();
            return;
        }

        bool shift = kb.leftShiftKey.isPressed || kb.rightShiftKey.isPressed;
        if (kb.rightArrowKey.wasPressedThisFrame ||
            (kb.tabKey.wasPressedThisFrame && !shift))
        {
            _selectedField = (_selectedField + 1) % 4;
            _keyCooldown   = COOLDOWN;
            RefreshUI();
            return;
        }

        if (kb.leftArrowKey.wasPressedThisFrame ||
            (kb.tabKey.wasPressedThisFrame && shift))
        {
            _selectedField = (_selectedField + 3) % 4;
            _keyCooldown   = COOLDOWN;
            RefreshUI();
            return;
        }

        if (kb.enterKey.wasPressedThisFrame || kb.numpadEnterKey.wasPressedThisFrame)
        {
            _keyCooldown = COOLDOWN;
            if (_selectedField < 3) { _selectedField = (_selectedField + 1) % 4; RefreshUI(); }
            else BeginSession();
        }
    }

    // ── Thumbstick Y — change selected field value ────────────────────────────
    void ReadThumbstick()
    {
        if (_thumbCooldown > 0f) return;

        float y = 0f;
        try { y = OVRInput.Get(OVRInput.RawAxis2D.RThumbstick).y; } catch {}

        // XR fallback
        if (Mathf.Abs(y) < 0.5f)
        {
            if (_rightDevices.Count == 0)
                InputDevices.GetDevicesWithCharacteristics(
                    InputDeviceCharacteristics.Controller | InputDeviceCharacteristics.Right,
                    _rightDevices);
            foreach (var d in _rightDevices)
                if (d.TryGetFeatureValue(XRCommonUsages.primary2DAxis, out Vector2 ax))
                { y = ax.y; break; }
        }

        if (Mathf.Abs(y) < 0.5f || _selectedField >= 3) return;
        ApplyValueDelta(y > 0f ? 1 : -1);
        _thumbCooldown = COOLDOWN;
        RefreshUI();
    }

    // ── Thumbstick X — cycle between fields ──────────────────────────────────
    void ReadThumbstickLR()
    {
        if (_thumbCooldown > 0f) return;

        float x = 0f;
        try { x = OVRInput.Get(OVRInput.RawAxis2D.RThumbstick).x; } catch {}

        if (Mathf.Abs(x) < 0.5f)
            foreach (var d in _rightDevices)
                if (d.TryGetFeatureValue(XRCommonUsages.primary2DAxis, out Vector2 ax))
                { x = ax.x; break; }

        if (Mathf.Abs(x) < 0.5f) return;
        _selectedField = x > 0f ? (_selectedField + 1) % 4 : (_selectedField + 3) % 4;
        _thumbCooldown = COOLDOWN;
        RefreshUI();
    }

    // ── A button — confirm / start ────────────────────────────────────────────
    void ReadAButton()
    {
        if (_aCooldown > 0f) return;

        bool pressed = false;
        try { pressed = OVRInput.GetDown(OVRInput.RawButton.A); } catch {}

        // XR fallback
        if (!pressed)
            foreach (var d in _rightDevices)
                if (d.TryGetFeatureValue(XRCommonUsages.primaryButton, out bool b) && b)
                { pressed = true; break; }

        if (!pressed) return;
        _aCooldown = COOLDOWN;

        if (_selectedField < 3) { _selectedField = (_selectedField + 1) % 4; RefreshUI(); }
        else BeginSession();
    }

    // ── Left trigger quick-start (same as Space bar on keyboard) ─────────────
    // Pressing the left grip or index trigger > 0.5 while the login screen is
    // visible immediately starts a session with whatever values are currently
    // shown — identical behaviour to the Space key in the Editor.
    private float _leftTriggerHeld = 0f;
    void ReadLeftTriggerQuickStart()
    {
        float v = 0f;
        try { v = OVRInput.Get(OVRInput.RawAxis1D.LHandTrigger);  } catch {}
        if (v < 0.1f)
            try { v = OVRInput.Get(OVRInput.RawAxis1D.LIndexTrigger); } catch {}

        // XR InputDevice fallback
        if (v < 0.1f)
        {
            var leftDevs = new System.Collections.Generic.List<XRInputDevice>();
            InputDevices.GetDevicesAtXRNode(XRNode.LeftHand, leftDevs);
            foreach (var d in leftDevs)
                if (d.TryGetFeatureValue(XRCommonUsages.trigger, out float t) && t > v) v = t;
        }

        if (v > 0.5f)
        {
            _leftTriggerHeld += Time.deltaTime;
            if (_leftTriggerHeld >= 1.0f)   // hold for 1 second to quick-start
            {
                Debug.Log("[LoginManager] Left trigger held — quick-starting session.");
                BeginSession();
            }
        }
        else
        {
            _leftTriggerHeld = 0f;
        }
    }

    // ── Field delta ───────────────────────────────────────────────────────────
    void ApplyValueDelta(int delta)
    {
        switch (_selectedField)
        {
            case 0: _participantID = Mathf.Clamp(_participantID + delta, 1, 999); break;
            case 1: _group         = Mathf.Clamp(_group         + delta, 1, 3);   break;
            case 2: _session       = Mathf.Clamp(_session       + delta, 1, 20);  break;
        }
    }

    // ── Session start ─────────────────────────────────────────────────────────
    void BeginSession()
    {
        // Always propagate the group — must happen before scene load in both paths.
        VisualFeedbackManager.Instance?.SetCondition(_group);

        if (ExperimentLogger.Instance == null)
        {
            // Fallback: set the flags directly so the game proceeds even without a logger.
            // GroupNumber must be set here too — ForwardBoundarySystem and
            // WoojerHapticManager both read it and default to 1 (Standard) otherwise.
            ExperimentLogger.GroupNumber  = _group;
            ExperimentLogger.SessionActive = true;
            _inputDone = true;
            Invoke(nameof(HideCanvas), 2f);   // HideCanvas loads the scene
            return;
        }

        ExperimentLogger.Instance.StartSession(_participantID, _group, _session);

        string groupName = _group == 1 ? "Standard VR (control)"
                         : _group == 2 ? "Visual Feedback"
                         : "Multimodal (Visual + Haptic)";

        if (_bodyText != null)
            _bodyText.text =
                $"<color=#00FF88><b>Session Started!</b></color>\n\n" +
                $"Participant:  P{_participantID:D3}\n" +
                $"Group:        {_group} — {groupName}\n" +
                $"Session:      {_session}\n\n" +
                "<size=18>Swing the paddle to serve.\n" +
                "Or press the LEFT trigger to launch the ball.</size>";

        if (_instructionText != null) _instructionText.text = "";
        if (_logPathText     != null) _logPathText.text     = "Log saved to /ExperimentLogs/";

        // Lock input now, but let LateUpdate keep repositioning for 4 more seconds
        _inputDone = true;
        Invoke(nameof(HideCanvas), 4f);

        Debug.Log($"[LoginManager] Session — P{_participantID:D3} G{_group} S{_session}");
    }

    void HideCanvas()
    {
        if (_canvasGO != null) _canvasGO.SetActive(false);
        _canvasDone = true;
        Debug.Log("[LoginManager] Loading game scene: game__room");
        SceneManager.LoadScene("game__room");
    }

    // ── UI refresh ────────────────────────────────────────────────────────────
    void RefreshUI()
    {
        if (_bodyText == null) return;

        string groupName = _group == 1 ? "Standard VR (control)"
                         : _group == 2 ? "Visual Feedback"
                         : "Multimodal (Visual + Haptic)";

        string Mark(int f) => _selectedField == f ? "  <color=#FFD700>◄</color>" : "";
        string startColor  = _selectedField == 3 ? "#00FF88" : "#666666";

        _bodyText.text =
            $"<b>Participant ID:</b>  {_participantID:D3}{Mark(0)}\n\n" +
            $"<b>Group:</b>           {_group} — {groupName}{Mark(1)}\n\n" +
            $"<b>Session:</b>         {_session}{Mark(2)}\n\n" +
            $"<color={startColor}><b>[ START SESSION ]{Mark(3)}</b></color>";
    }

    // ── Canvas construction ───────────────────────────────────────────────────
    void BuildCanvas()
    {
        _canvasGO = new GameObject("LoginCanvas");
        var canvas = _canvasGO.AddComponent<Canvas>();
        canvas.renderMode   = RenderMode.WorldSpace;
        canvas.sortingOrder = 100;
        _canvasGO.AddComponent<CanvasScaler>();
        _canvasGO.AddComponent<GraphicRaycaster>();

        var rt = _canvasGO.GetComponent<RectTransform>();
        rt.sizeDelta  = CANVAS_SIZE;
        rt.localScale = Vector3.one * 0.002f;

        var bg = MakePanel(_canvasGO, "BG",
            new Color(0.05f, 0.05f, 0.15f, 0.93f),
            new Vector2(780f, 480f), Vector2.zero);

        _titleText = MakeText(bg, "Title",
            "TABLE TENNIS VR EXPERIMENT",
            28, Color.cyan,
            new Vector2(0f, 190f), new Vector2(760f, 50f));

        _bodyText = MakeText(bg, "Body",
            "", 22, Color.white,
            new Vector2(0f, 25f), new Vector2(720f, 280f));

        _instructionText = MakeText(bg, "Instructions",
            "Keyboard: ↑↓ change  |  ←→/Tab next  |  Enter confirm  |  Space start\n" +
            "Quest: Right thumbstick ↑↓ change  |  ←→ next  |  A confirm/start",
            13, new Color(0.75f, 0.75f, 0.75f),
            new Vector2(0f, -180f), new Vector2(760f, 50f));

        _logPathText = MakeText(bg, "LogPath",
            "", 13, new Color(0.5f, 1f, 0.5f),
            new Vector2(0f, -215f), new Vector2(760f, 28f));
    }

    static GameObject MakePanel(GameObject parent, string name, Color color,
                                 Vector2 size, Vector2 pos)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(Image));
        go.transform.SetParent(parent.transform, false);
        go.GetComponent<Image>().color = color;
        var rt = go.GetComponent<RectTransform>();
        rt.sizeDelta = size; rt.anchoredPosition = pos;
        return go;
    }

    static Text MakeText(GameObject parent, string name, string content,
                          int size, Color color, Vector2 pos, Vector2 rectSize)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(Text));
        go.transform.SetParent(parent.transform, false);
        var t = go.GetComponent<Text>();
        t.text            = content;
        t.fontSize        = size;
        t.color           = color;
        t.alignment       = TextAnchor.MiddleCenter;
        t.font            = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        t.supportRichText = true;
        var rt = go.GetComponent<RectTransform>();
        rt.sizeDelta = rectSize; rt.anchoredPosition = pos;
        return t;
    }
}
