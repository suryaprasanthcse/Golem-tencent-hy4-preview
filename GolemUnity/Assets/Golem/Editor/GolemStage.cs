using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Golem.EditorTools
{
    /// <summary>
    /// GOLEM > Build Demo Stage: lays the imported props out in a presentation line (smallest to
    /// largest, left to right on screen), turns them into free physics bodies standing on a floor,
    /// adds a backdrop, key/fill lighting and a framed camera, and sets up the physics-proof ball.
    /// Run after Import Split Props.
    /// </summary>
    public static class GolemStage
    {
        const string Suffix = " (GOLEM)";
        static readonly string[] Order = { "laptop", "toolbox", "chest", "filing_cabinet", "vault_door" };
        const float Gap = 0.6f;          // metres between props
        const float VerticalFov = 30f;   // a longer lens flattens perspective and reads as cinematic

        [MenuItem("GOLEM/Build Demo Stage")]
        public static void BuildMenu() => Build();

        /// <summary>For an agent driving the live Editor: build the stage and save the scene.</summary>
        public static string Build()
        {
            var props = Object.FindObjectsByType<Transform>(FindObjectsSortMode.None)
                .Where(t => t.parent == null && t.name.EndsWith(Suffix))
                .OrderBy(t => Rank(t.name.Replace(Suffix, "")))
                .ToList();
            if (props.Count == 0)
                return "no GOLEM props in the scene: run Import Split Props first";

            LayOut(props);
            foreach (var body in props.Select(p => p.GetComponentInChildren<ArticulationBody>()).Where(b => b != null && b.isRoot))
                body.immovable = false;  // free bodies, so an impact can move them by their real mass

            var all = BoundsOf(props);
            var surface = Material(new Color(0.16f, 0.16f, 0.17f), 0.25f);
            Floor(surface);
            Backdrop(Material(new Color(0.11f, 0.11f, 0.12f), 0.1f), all);
            Lights();
            Frame(all);
            Ball(props);

            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
            EditorSceneManager.SaveScene(SceneManager.GetActiveScene());
            return $"stage built: {props.Count} props, {all.size.x:F2} m wide";
        }

        static int Rank(string name)
        {
            var index = System.Array.IndexOf(Order, name);
            return index >= 0 ? index : Order.Length;  // props not in the list go at the end
        }

        // TryGetComponent, not "??": the Editor's fake-null objects would fool the ?? operator.
        static T Ensure<T>(GameObject owner) where T : Component =>
            owner.TryGetComponent(out T existing) ? existing : owner.AddComponent<T>();

        /// <summary>One line along X, centred on the origin. The camera looks along -Z, so +X is screen left.</summary>
        static void LayOut(List<Transform> props)
        {
            var cursor = 0f;
            var centres = new List<float>();
            foreach (var prop in props)
            {
                var b = BoundsOf(new[] { prop });
                var width = b.size.x;
                var centre = cursor - width / 2;
                prop.position += new Vector3(centre - b.center.x, 0, -b.center.z);
                centres.Add(centre);
                cursor -= width + Gap;
            }
            var shift = -(centres.First() + centres.Last()) / 2;
            foreach (var prop in props)
                prop.position += new Vector3(shift, 0, 0);
        }

        static void Floor(Material material)
        {
            var floor = GameObject.Find("GOLEM Ground") ?? GameObject.CreatePrimitive(PrimitiveType.Plane);
            floor.name = "GOLEM Ground";
            floor.transform.position = Vector3.zero;
            floor.transform.localScale = new Vector3(10, 1, 10);
            floor.GetComponent<Renderer>().sharedMaterial = material;
        }

        static void Backdrop(Material material, Bounds all)
        {
            var wall = GameObject.Find("GOLEM Backdrop") ?? GameObject.CreatePrimitive(PrimitiveType.Quad);
            wall.name = "GOLEM Backdrop";
            wall.transform.position = new Vector3(all.center.x, 6f, all.min.z - 3f);
            // A Quad is visible from -Z only; the camera looks along -Z from +Z, so turn it around.
            wall.transform.rotation = Quaternion.Euler(0f, 180f, 0f);
            wall.transform.localScale = new Vector3(40, 12, 1);
            Object.DestroyImmediate(wall.GetComponent<Collider>());
            wall.GetComponent<Renderer>().sharedMaterial = material;
        }

        static void Lights()
        {
            var key = Object.FindObjectsByType<Light>(FindObjectsSortMode.None).FirstOrDefault(l => l.type == LightType.Directional && l.name != "GOLEM Fill");
            if (key == null)
                key = new GameObject("Directional Light").AddComponent<Light>();
            key.type = LightType.Directional;
            key.transform.rotation = Quaternion.Euler(42f, 150f, 0f);  // from front-right, above
            key.intensity = 1.3f;
            key.shadows = LightShadows.Soft;
            key.shadowStrength = 0.75f;
            RenderSettings.sun = key;

            var fill = Ensure<Light>(GameObject.Find("GOLEM Fill") ?? new GameObject("GOLEM Fill"));
            fill.type = LightType.Directional;
            fill.transform.rotation = Quaternion.Euler(20f, -140f, 0f);  // softer, from front-left
            fill.intensity = 0.35f;
            fill.shadows = LightShadows.None;
        }

        /// <summary>Fit the whole line in a 16:9 frame with a little margin, from slightly above.</summary>
        static void Frame(Bounds all)
        {
            var cam = Camera.main;
            if (cam == null)
                return;
            cam.fieldOfView = VerticalFov;
            cam.nearClipPlane = 0.05f;
            var halfV = VerticalFov * 0.5f * Mathf.Deg2Rad;
            var halfH = Mathf.Atan(Mathf.Tan(halfV) * 16f / 9f);
            var distance = Mathf.Max(all.size.x * 0.56f / Mathf.Tan(halfH), all.size.y * 0.7f / Mathf.Tan(halfV)) + all.extents.z;
            var target = all.center + Vector3.down * 0.08f * all.size.y;
            cam.transform.position = target + new Vector3(0, 0.25f * distance, distance);
            cam.transform.LookAt(target);
        }

        static void Ball(List<Transform> props)
        {
            var ball = GameObject.Find("GOLEM Ball") ?? GameObject.CreatePrimitive(PrimitiveType.Sphere);
            ball.name = "GOLEM Ball";
            const float radius = 0.2f;
            ball.transform.localScale = Vector3.one * radius * 2;
            ball.GetComponent<Renderer>().sharedMaterial = Material(new Color(0.75f, 0.12f, 0.08f), 0.7f);
            var rb = Ensure<Rigidbody>(ball);
            rb.mass = 30f;  // a heavy ball, so the difference between light and heavy props shows
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;

            // Same ball, same speed: it bounces off the 4.5 t vault door but knocks the 17 kg toolbox over.
            // The first shot fires by itself shortly after Play; each can be repeated with its key.
            var launcherObject = GameObject.Find("GOLEM Ball Launcher") ?? new GameObject("GOLEM Ball Launcher");
            var launcher = Ensure<GolemBallLauncher>(launcherObject);
            launcher.ball = rb;
            var shots = new List<GolemBallLauncher.Shot>();
            foreach (var (name, key, side) in new[] { ("vault_door", "V", 1.4f), ("toolbox", "T", -1.2f), ("filing_cabinet", "B", -1.4f) })
            {
                var target = props.FirstOrDefault(p => p.name == name + Suffix);
                if (target == null)
                    continue;
                var c = BoundsOf(new[] { target }).center;
                shots.Add(new GolemBallLauncher.Shot { key = key, target = target, spawn = new Vector3(c.x + side, radius, 3f), speed = 7f });
            }
            launcher.shots = shots.ToArray();
            if (shots.Count > 0)
                ball.transform.position = shots[0].spawn;
        }

        /// <summary>Render the main camera straight into a PNG at the given size, with the camera's
        /// aspect matched to it (the Editor's tiled screenshot misaligns when the Game view's aspect differs).</summary>
        public static string Capture(string path, int width = 1920, int height = 1080)
        {
            var cam = Camera.main;
            if (cam == null)
                return "no main camera";
            var target = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4 };
            var previousTarget = cam.targetTexture;
            var previousAspect = cam.aspect;
            cam.targetTexture = target;
            cam.aspect = (float)width / height;
            cam.Render();
            var active = RenderTexture.active;
            RenderTexture.active = target;
            var image = new Texture2D(width, height, TextureFormat.RGB24, false);
            image.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            image.Apply();
            RenderTexture.active = active;
            cam.targetTexture = previousTarget;
            cam.aspect = previousAspect;
            cam.ResetAspect();
            System.IO.File.WriteAllBytes(path, image.EncodeToPNG());
            Object.DestroyImmediate(image);
            target.Release();
            Object.DestroyImmediate(target);
            return $"captured {width}x{height} to {path}";
        }

        static Material Material(Color colour, float smoothness)
        {
            var material = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            material.SetColor("_BaseColor", colour);
            material.SetFloat("_Smoothness", smoothness);
            return material;
        }

        static Bounds BoundsOf(IEnumerable<Transform> roots)
        {
            var renderers = roots.SelectMany(r => r.GetComponentsInChildren<Renderer>()).ToArray();
            if (renderers.Length == 0)
                return new Bounds();
            var b = renderers[0].bounds;
            foreach (var r in renderers)
                b.Encapsulate(r.bounds);
            return b;
        }
    }
}
