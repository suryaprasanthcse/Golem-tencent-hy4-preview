using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace Golem
{
    /// <summary>
    /// Play-mode interaction: drag any hinged or sliding part with the left mouse button
    /// (up opens, down closes), or press O to open every part and C to close them.
    /// Parts move through their joint drives, so physics stays stable and limits hold.
    /// </summary>
    [RequireComponent(typeof(Camera))]
    public class GolemDragController : MonoBehaviour
    {
        [Tooltip("Degrees (or metres for slides) per pixel of vertical mouse movement.")]
        public float sensitivity = 0.35f;

        Camera cam;
        ArticulationBody held;
        float grabMouseY, grabTarget;

        void Awake() => cam = GetComponent<Camera>();

        void Update()
        {
            if (PressedThisFrame(out var mouse))
            {
                held = null;
                if (Physics.Raycast(cam.ScreenPointToRay(mouse), out var hit) && hit.collider.attachedArticulationBody is { } body && !body.isRoot)
                {
                    held = body;
                    grabMouseY = mouse.y;
                    grabTarget = body.xDrive.target;
                }
            }
            if (held != null && Held(out mouse))
                SetTarget(held, grabTarget + (mouse.y - grabMouseY) * sensitivity);
            else
                held = null;

            if (KeyPressed('o'))
                foreach (var body in FindObjectsByType<ArticulationBody>(FindObjectsSortMode.None))
                    if (!body.isRoot) SetTarget(body, body.xDrive.upperLimit);
            if (KeyPressed('c'))
                foreach (var body in FindObjectsByType<ArticulationBody>(FindObjectsSortMode.None))
                    if (!body.isRoot) SetTarget(body, body.xDrive.lowerLimit);
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
