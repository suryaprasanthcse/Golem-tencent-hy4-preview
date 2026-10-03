using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace Golem
{
    /// <summary>
    /// Play-mode interaction: grab any hinged or sliding part with the left mouse button and drag
    /// it the way it moves; press O to open every part and C to close them.
    ///
    /// The part follows the mouse along its own path on screen: each frame we project how far, and
    /// which way, the grabbed point would move on screen for one unit of joint travel (one degree, or
    /// one metre for a slide), and convert the mouse movement along that direction into joint travel.
    /// So a side-hinged door follows horizontal drags, a lid vertical ones, and a drawer slides
    /// toward the camera, from any viewpoint. Parts move through their joint drives, so physics
    /// stays stable and the limits hold.
    /// </summary>
    [RequireComponent(typeof(Camera))]
    public class GolemDragController : MonoBehaviour
    {
        Camera cam;
        ArticulationBody held;
        Vector3 grabLocal;        // grabbed point in the part's local space
        Vector2 lastMouse;

        void Awake() => cam = GetComponent<Camera>();

        void Update()
        {
            if (PressedThisFrame(out var mouse))
            {
                held = null;
                if (Physics.Raycast(cam.ScreenPointToRay(mouse), out var hit) && hit.collider.attachedArticulationBody is { } body && !body.isRoot)
                {
                    held = body;
                    grabLocal = body.transform.InverseTransformPoint(hit.point);
                    lastMouse = mouse;
                }
            }
            if (held != null && Held(out mouse))
            {
                var perUnit = ScreenMotionPerUnit(held, held.transform.TransformPoint(grabLocal));
                if (perUnit.sqrMagnitude > 1e-6f)
                    SetTarget(held, held.xDrive.target + Vector2.Dot(mouse - lastMouse, perUnit) / perUnit.sqrMagnitude);
                lastMouse = mouse;
            }
            else
                held = null;

            if (KeyPressed('o'))
                foreach (var body in FindObjectsByType<ArticulationBody>(FindObjectsSortMode.None))
                    if (!body.isRoot) SetTarget(body, body.xDrive.upperLimit);
            if (KeyPressed('c'))
                foreach (var body in FindObjectsByType<ArticulationBody>(FindObjectsSortMode.None))
                    if (!body.isRoot) SetTarget(body, body.xDrive.lowerLimit);
        }

        /// <summary>Screen pixels the point moves for +1 unit of joint travel (degree or metre).</summary>
        Vector2 ScreenMotionPerUnit(ArticulationBody body, Vector3 point)
        {
            var anchorRotation = body.transform.rotation * body.anchorRotation;
            var axis = anchorRotation * Vector3.right;  // the joint axis in Unity is the anchor's X
            Vector3 moved;
            float step;
            if (body.jointType == ArticulationJointType.PrismaticJoint)
            {
                step = 0.01f;
                moved = point + axis * step;
            }
            else
            {
                step = 1f;
                var pivot = body.transform.TransformPoint(body.anchorPosition);
                moved = pivot + Quaternion.AngleAxis(step, axis) * (point - pivot);
            }
            return ((Vector2)cam.WorldToScreenPoint(moved) - (Vector2)cam.WorldToScreenPoint(point)) / step;
        }

        static void SetTarget(ArticulationBody body, float target)
        {
            var drive = body.xDrive;
            drive.target = Mathf.Clamp(target, drive.lowerLimit, drive.upperLimit);
            body.xDrive = drive;
        }

#if ENABLE_INPUT_SYSTEM
        static bool PressedThisFrame(out Vector2 position)
        {
            position = Mouse.current != null ? Mouse.current.position.ReadValue() : Vector2.zero;
            return Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame;
        }

        static bool Held(out Vector2 position)
        {
            position = Mouse.current != null ? Mouse.current.position.ReadValue() : Vector2.zero;
            return Mouse.current != null && Mouse.current.leftButton.isPressed;
        }

        static bool KeyPressed(char key) =>
            Keyboard.current != null && (key == 'o' ? Keyboard.current.oKey : Keyboard.current.cKey).wasPressedThisFrame;
#else
        static bool PressedThisFrame(out Vector2 position) { position = Input.mousePosition; return Input.GetMouseButtonDown(0); }
        static bool Held(out Vector2 position) { position = Input.mousePosition; return Input.GetMouseButton(0); }
        static bool KeyPressed(char key) => Input.GetKeyDown(key == 'o' ? KeyCode.O : KeyCode.C);
#endif
    }
}
