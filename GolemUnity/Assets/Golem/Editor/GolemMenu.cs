using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Golem.EditorTools
{
    /// <summary>
    /// GOLEM > Import Split Props: brings every prop from ../assets/split/&lt;name&gt;/ (the cutter's
    /// &lt;name&gt;_split.glb + &lt;name&gt;_joints.json) into the open scene as an articulated object.
    ///
    /// Command line (builds SampleScene, runs the hinge self-test, saves, exits 0 on success):
    ///   Unity.exe -batchmode -projectPath GolemUnity -executeMethod Golem.EditorTools.GolemMenu.BuildFromCommandLine
    /// </summary>
    public static class GolemMenu
    {
        const string ScenePath = "Assets/Scenes/SampleScene.unity";
        const string PropsFolder = "Assets/GolemProps";
        const string Suffix = " (GOLEM)";
        const float Spacing = 3.5f;

        static string SplitRoot => Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "assets", "split"));

        [MenuItem("GOLEM/Import Split Props")]
        public static void ImportSplitPropsMenu()
        {
            var count = ImportAll();
            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
            EditorUtility.DisplayDialog("GOLEM", $"Imported {count} prop(s) from\n{SplitRoot}", "OK");
        }

        [MenuItem("GOLEM/Run Hinge Self-Test")]
        public static void SelfTestMenu()
        {
            var passed = SelfTest();
            EditorUtility.DisplayDialog("GOLEM", passed ? "All joints open the right way." : "A joint failed: see the Console.", "OK");
        }

        /// <summary>For an agent driving the live Editor: import, self-test and save the open scene,
        /// with no dialogs and without quitting Unity.</summary>
        public static string ImportAndTest()
        {
            var count = ImportAll();
            var passed = count > 0 && SelfTest();
            EditorSceneManager.SaveScene(SceneManager.GetActiveScene());
            return $"{count} prop(s), self-test {(passed ? "PASS" : "FAIL")}";
        }

        public static void BuildFromCommandLine()
        {
            EditorSceneManager.OpenScene(ScenePath);
            var count = ImportAll();
            var passed = count > 0 && SelfTest();
            EditorSceneManager.SaveScene(SceneManager.GetActiveScene());
            Debug.Log($"[GOLEM] BUILD {(passed ? "PASS" : "FAIL")}: {count} prop(s)");
            EditorApplication.Exit(passed ? 0 : 1);
        }

        static int ImportAll()
        {
            if (!Directory.Exists(SplitRoot))
            {
                Debug.LogError($"[GOLEM] no split props folder: {SplitRoot}");
                return 0;
            }
            var count = 0;
            foreach (var dir in Directory.GetDirectories(SplitRoot).OrderBy(d => d))
            {
                var name = Path.GetFileName(dir);
                var glb = Path.Combine(dir, $"{name}_split.glb");
                var json = Path.Combine(dir, $"{name}_joints.json");
                if (!File.Exists(glb) || !File.Exists(json))
                    continue;
                if (ImportProp(name, glb, json, new Vector3(count * Spacing, 0, 0)))
                    count++;
            }
            EnsureStage();
            return count;
        }

        static bool ImportProp(string name, string glb, string json, Vector3 position)
        {
            var folder = $"{PropsFolder}/{name}";
            Directory.CreateDirectory(folder);
            var glbAsset = $"{folder}/{name}_split.glb";
            File.Copy(glb, glbAsset, true);
            File.Copy(json, $"{folder}/{name}_joints.json", true);
            AssetDatabase.ImportAsset(glbAsset, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);

            var model = AssetDatabase.LoadAssetAtPath<GameObject>(glbAsset);
            if (model == null)
            {
                Debug.LogError($"[GOLEM] {name}: glTFast did not import {glbAsset} (is com.unity.cloud.gltfast installed?)");
                return false;
            }
            var old = GameObject.Find(name + Suffix);
            if (old != null)
                Object.DestroyImmediate(old);

            // The wrapper stands in for the glTF scene root, so the spec's coordinates mean the same
            // thing here. (glTFast folds a glTF's single root node into the imported object and
            // renames it after the file, so that object is the root part, keeping its own offset.)
            var wrapper = new GameObject(name + Suffix).transform;
            wrapper.position = position;
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(model);
            PrefabUtility.UnpackPrefabInstance(instance, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
            instance.transform.SetParent(wrapper, false);

            var renderers = instance.GetComponentsInChildren<Renderer>();
            if (renderers.Length > 0)  // stand the prop on the ground
                wrapper.position += Vector3.up * -renderers.Min(r => r.bounds.min.y);

            var spec = JsonUtility.FromJson<GolemSpec>(File.ReadAllText(json));
            GolemArticulator.Build(wrapper, spec);
            Debug.Log($"[GOLEM] {name}: {spec.joints.Length} joint(s) built");
            return true;
        }

        /// <summary>Ground, drag controller, and a camera looking at the props' fronts (+Z in Unity, as in glTF).</summary>
        static void EnsureStage()
        {
            if (GameObject.Find("GOLEM Ground") == null)
            {
                var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
                ground.name = "GOLEM Ground";
                ground.transform.localScale = new Vector3(5, 1, 5);
            }
            var cam = Camera.main;
            if (cam == null)
                return;
            if (!cam.TryGetComponent(out GolemDragController _))
                cam.gameObject.AddComponent<GolemDragController>();

            var renderers = Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None)
                .Where(r => r.transform.root.name.EndsWith(Suffix)).ToArray();
            if (renderers.Length == 0)
                return;
            var bounds = renderers[0].bounds;
            foreach (var r in renderers)
                bounds.Encapsulate(r.bounds);
            var size = Mathf.Max(bounds.size.x, bounds.size.y, 2f);
            cam.transform.position = bounds.center + new Vector3(-0.35f * size, 0.55f * size, 1.35f * size);
            cam.transform.LookAt(bounds.center);
        }

        /// <summary>
        /// Simulates each prop headlessly: drive every joint 70 degrees (toward whichever limit has
        /// room: open for a lid, closed for a standing laptop screen) and check that it turned that
        /// far and moved the right way: positive rotation must move the part toward its hinge side
        /// (a lid opening swings back over its hinge; a screen closing tips forward). That catches a
        /// wrong axis sign after the glTF -> Unity conversion. Runs on copies, so the scene keeps its pose.
        /// </summary>
        public static bool SelfTest()
        {
            var passed = true;
            var tested = 0;
            var previous = Physics.simulationMode;
            Physics.simulationMode = SimulationMode.Script;
            foreach (var original in Object.FindObjectsByType<Transform>(FindObjectsSortMode.None).Where(t => t.parent == null && t.name.EndsWith(Suffix)))
            {
                var copy = Object.Instantiate(original.gameObject, original.position + new Vector3(0, 0, -50), original.rotation);
                var parts = copy.GetComponentsInChildren<ArticulationBody>().Where(b => !b.isRoot).ToArray();
                foreach (var link in copy.GetComponentsInChildren<GolemJointLink>())
                    GolemJointLink.Apply(link.transform);
                var before = parts.Select(p => (p.transform.rotation, Center(p))).ToArray();
                var moves = new float[parts.Length];
                for (var i = 0; i < parts.Length; i++)
                {
                    var drive = parts[i].xDrive;
                    var upRoom = drive.upperLimit - drive.target;
                    moves[i] = upRoom >= 45f ? Mathf.Min(70f, upRoom) : -Mathf.Min(70f, drive.target - drive.lowerLimit);
                    drive.target += moves[i];
                    parts[i].xDrive = drive;
                }
                for (var step = 0; step < 200; step++)
                    Physics.Simulate(0.02f);

                for (var i = 0; i < parts.Length; i++)
                {
                    var angle = Quaternion.Angle(before[i].rotation, parts[i].transform.rotation);
                    var shift = Center(parts[i]) - before[i].Item2;
                    var outward = parts[i].TryGetComponent(out GolemJointLink link) ? link.outward : Vector3.zero;
                    // Positive rotation moves the part toward its hinge side; without that data, a lid must rise.
                    var rightWay = outward != Vector3.zero ? Mathf.Sign(Vector3.Dot(shift, outward)) == Mathf.Sign(moves[i]) : shift.y > 0;
                    var ok = rightWay && Mathf.Abs(angle - Mathf.Abs(moves[i])) < 10f;
                    passed &= ok;
                    tested++;
                    Debug.Log($"[GOLEM] SELFTEST {original.name}/{parts[i].name}: drove {moves[i]:+0;-0} deg, turned {angle:F1}, " +
                              $"moved {(rightWay ? "the right way" : "THE WRONG WAY")} -> {(ok ? "PASS" : "FAIL")}");
                }
                Object.DestroyImmediate(copy);
            }
            Physics.simulationMode = previous;
            if (tested == 0)
                Debug.LogError("[GOLEM] SELFTEST found no moving parts to test");
            return passed && tested > 0;
        }

        static Vector3 Center(ArticulationBody part)
        {
            var renderers = part.GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0)
                return part.transform.position;
            var bounds = renderers[0].bounds;
            foreach (var r in renderers)
                bounds.Encapsulate(r.bounds);
            return bounds.center;
        }
    }
}
