using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;

// Editor-only tooling. Exported levels contain exclusively stock HFF behaviours.
public static class LastGripCli
{
    const string ScenePath = "Assets/LastGrip/LastGripTower.unity";
    const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

    static Type FindType(string name)
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            var result = assembly.GetType(name, false);
            if (result != null) return result;
        }
        throw new InvalidOperationException("Required official Workshop type was not loaded: " + name);
    }

    static void Set(object instance, string name, object value)
    {
        var type = instance as Type ?? instance.GetType();
        var field = type.GetField(name, Flags);
        if (field == null) throw new MissingFieldException(type.FullName, name);
        field.SetValue(instance is Type ? null : instance, value);
    }

    static object Get(object instance, string name)
    {
        var type = instance as Type ?? instance.GetType();
        var field = type.GetField(name, Flags);
        if (field == null) throw new MissingFieldException(type.FullName, name);
        return field.GetValue(instance is Type ? null : instance);
    }

    static object Invoke(object instance, string name, params object[] args)
    {
        var type = instance as Type ?? instance.GetType();
        var method = type.GetMethods(Flags).Single(m => m.Name == name && m.GetParameters().Length == args.Length);
        return method.Invoke(instance is Type ? null : instance, args);
    }

    [MenuItem("LAST GRIP/Validate Tower")]
    public static void Validate()
    {
        EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        var root = GameObject.Find("LAST GRIP - Human Workshop Tower");
        if (!root) throw new InvalidOperationException("Tower root missing.");
        var objects = root.GetComponentsInChildren<Transform>(true);
        int scripts = 0;
        foreach (var tr in objects)
        {
            foreach (var component in tr.GetComponents<MonoBehaviour>())
            {
                if (component == null) throw new InvalidOperationException("Missing script: " + tr.name);
                ++scripts;
                if (component.GetType().Assembly.GetName().Name != "HFF-Code-CSharp")
                    throw new InvalidOperationException("Unexpected runtime assembly: " + component.GetType().FullName);
            }
        }
        var netBody = FindType("Multiplayer.NetBody");
        var bodies = root.GetComponentsInChildren<Rigidbody>(true);
        if (bodies.Length != 35) throw new InvalidOperationException("Expected 35 physics bodies; got " + bodies.Length);
        foreach (var body in bodies)
            if (!body.GetComponent(netBody)) throw new InvalidOperationException("Rigidbody has no NetBody: " + body.name);
        foreach (var hinge in root.GetComponentsInChildren<HingeJoint>(true))
        {
            var expected = hinge.transform.TransformPoint(hinge.anchor);
            var actual = hinge.connectedBody ? hinge.connectedBody.transform.TransformPoint(hinge.connectedAnchor) : hinge.connectedAnchor;
            if ((expected - actual).sqrMagnitude > .001f)
                throw new InvalidOperationException("Hinge anchor mismatch: " + hinge.name);
        }
        var checkpointType = FindType("HumanAPI.Checkpoint");
        var checkpoints = root.GetComponentsInChildren(checkpointType, true);
        var ids = checkpoints.Select(c => (int)Get(c,"number")).OrderBy(n => n).ToArray();
        if (!ids.SequenceEqual(new[] {0,1,2,3})) throw new InvalidOperationException("Invalid checkpoint sequence.");
        if (UnityEngine.Object.FindObjectsOfType<Camera>().Length != 0)
            throw new InvalidOperationException("Exportable level must contain no cameras.");
        var passType = FindType("HumanAPI.LevelPassTrigger");
        if (root.GetComponentsInChildren(passType, true).Length != 1)
            throw new InvalidOperationException("Expected one finish trigger.");
        EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene());
        Directory.CreateDirectory("Build");
        File.WriteAllText("Build/unity-validation.txt", "Unity import validation passed.\nObjects: " + objects.Length + "\nStock SDK behaviours: " + scripts + "\nNetworked bodies: " + bodies.Length + "\nCheckpoints: 0, 1, 2, 3\nGameplay clearance, difficulty, and multiplayer playtest remain manual checks.\n");
        Debug.Log("LAST_GRIP_VALIDATION_PASSED");
    }

    [MenuItem("LAST GRIP/Export Local Map")]
    public static void ExportLocal()
    {
        Validate();
        var exporterType = FindType("HumanExport");
        var exporter = ScriptableObject.CreateInstance(exporterType);
        try
        {
            Invoke(exporter, "InitStatics");
            Invoke(exporter, "MakeEmptyMetadata", "LAST GRIP");
            var metadata = Get(exporter,"metadata");
            Set(metadata,"title","LAST GRIP");
            Set(metadata,"description","12-stage, 72m physics tower. Precision jumps, seesaws, carousels, hanging swings, ice, loose planks, moving platforms, sweepers, elevators and a final combination. Checkpoints every three stages. Designed for solo practice or friends. Initial build: gameplay and multiplayer balancing still required.");
            Set(exporterType,"sTagLevelTypeSelected",1);
            Set(exporterType,"sTagNumberPlayersSelected",1);
            var target = Path.GetFullPath("Build/LastGrip");
            var thumb = Path.GetFullPath("Previews/overview.png");
            if (!File.Exists(thumb)) throw new FileNotFoundException("Run Tools/preview_tower.py first",thumb);
            var texture = new Texture2D(2,2,TextureFormat.RGB24,false);
            texture.LoadImage(File.ReadAllBytes(thumb));
            Set(exporter,"thumbnail",texture);
            Set(exporterType,"sModPath",target);
            Set(exporterType,"sThumbPath",thumb);
            var iconProp = exporterType.GetProperty("sIconSource",Flags);
            if (iconProp != null) iconProp.SetValue(null,Enum.ToObject(iconProp.PropertyType,0),null);
            if (!(bool)Invoke(exporter,"Validate"))
                throw new InvalidOperationException("Official exporter rejected the scene: " + Get(exporter,"validationString"));
            // The official exporter also patches script references from the SDK
            // assembly to the game's assembly; a raw AssetBundle is insufficient.
            Invoke(exporter,"Export",EditorSceneManager.GetActiveScene());
            foreach (var filename in new[] {"data","metadata.json","thumbnail.png"})
                if (!File.Exists(Path.Combine(target,filename)))
                    throw new FileNotFoundException("Official export did not produce " + filename);
            Debug.Log("LAST_GRIP_EXPORT_PASSED: " + target);
        }
        finally { UnityEngine.Object.DestroyImmediate(exporter); }
    }

    [MenuItem("LAST GRIP/Render Previews")]
    public static void RenderPreviews()
    {
        Validate();
        Directory.CreateDirectory("Previews");
        var go = new GameObject("Temporary_Editor_Preview_Camera");
        var camera = go.AddComponent<Camera>();
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = new Color(.035f,.055f,.10f);
        camera.farClipPlane = 500;
        camera.transform.position = new Vector3(91,86,-110);
        camera.transform.LookAt(new Vector3(0,35,0));
        camera.fieldOfView = 38;
        var rt = new RenderTexture(1600,1200,24);
        var texture = new Texture2D(1600,1200,TextureFormat.RGB24,false);
        try
        {
            camera.targetTexture = rt;
            camera.Render();
            RenderTexture.active = rt;
            texture.ReadPixels(new Rect(0,0,1600,1200),0,0);
            texture.Apply();
            File.WriteAllBytes("Previews/unity-overview.png",texture.EncodeToPNG());
        }
        finally
        {
            RenderTexture.active = null;
            camera.targetTexture = null;
            UnityEngine.Object.DestroyImmediate(texture);
            UnityEngine.Object.DestroyImmediate(rt);
            UnityEngine.Object.DestroyImmediate(go);
        }
        Debug.Log("LAST_GRIP_PREVIEW_PASSED");
    }
}
