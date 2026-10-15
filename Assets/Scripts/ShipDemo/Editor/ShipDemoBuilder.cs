using FishNet.Component.Spawning;
using FishNet.Object;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Builds the ship demo: a copy of SampleScene with a flying ship, some asteroids,
/// and a ShipPlayer prefab hooked up to the PlayerSpawner.
/// </summary>
public static class ShipDemoBuilder
{
    private const string Folder = "Assets/ShipDemo";
    private const string SourceScene = "Assets/Scenes/SampleScene.unity";
    private const string ScenePath = Folder + "/ShipDemo.unity";
    private const string PrefabPath = Folder + "/ShipPlayer.prefab";

    [MenuItem("Tools/Ship Demo/Build Demo Scene")]
    private static void Build()
    {
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            return;

        if (!AssetDatabase.IsValidFolder(Folder))
            AssetDatabase.CreateFolder("Assets", "ShipDemo");

        if (AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath) != null)
        {
            if (!EditorUtility.DisplayDialog("Ship Demo", $"{ScenePath} already exists. Rebuild it?", "Rebuild", "Cancel"))
                return;
            AssetDatabase.DeleteAsset(ScenePath);
        }

        NetworkObject playerPrefab = BuildPlayerPrefab();

        if (!AssetDatabase.CopyAsset(SourceScene, ScenePath))
        {
            Debug.LogError($"Could not copy {SourceScene}. The demo needs its NetworkManager and PlayerSpawner.");
            return;
        }
        var scene = EditorSceneManager.OpenScene(ScenePath);

        foreach (GameObject root in scene.GetRootGameObjects())
        {
            if (root.name == "Plane")
                Object.DestroyImmediate(root);
        }

        PlayerSpawner spawner = Object.FindAnyObjectByType<PlayerSpawner>();
        if (spawner != null)
        {
            SerializedObject so = new(spawner);
            so.FindProperty("_playerPrefab").objectReferenceValue = playerPrefab;
            so.ApplyModifiedPropertiesWithoutUndo();

            // Lets a build start as server/client from the command line.
            if (spawner.GetComponent<DemoAutoStart>() == null)
                spawner.gameObject.AddComponent<DemoAutoStart>();
        }
        else
        {
            Debug.LogWarning("No PlayerSpawner found in the scene; assign ShipPlayer as the player prefab yourself.");
        }

        Camera cam = Camera.main;
        if (cam != null)
            cam.nearClipPlane = 0.05f;

        BuildShip();
        BuildAsteroids();

        EditorSceneManager.SaveScene(scene);
        AssetDatabase.SaveAssets();
        Debug.Log($"Ship demo built at {ScenePath}. Press Play and start Server + Client from the NetworkManager HUD.");
    }

    private static NetworkObject BuildPlayerPrefab()
    {
        GameObject root = new("ShipPlayer");
        NetworkObject nob = root.AddComponent<NetworkObject>();
        root.AddComponent<ShipPlayer>();

        // Prediction on, type Other: ShipPlayer drives its own body and smoothing.
        SerializedObject so = new(nob);
        so.FindProperty("_enablePrediction").boolValue = true;
        so.FindProperty("_predictionType").intValue = 0;
        so.ApplyModifiedPropertiesWithoutUndo();

        Material bodyMat = GetMaterial("Player", new Color(0.95f, 0.55f, 0.15f));
        Material visorMat = GetMaterial("Visor", new Color(0.1f, 0.15f, 0.2f));

        GameObject visual = Primitive(PrimitiveType.Capsule, "Visual", root.transform, Vector3.zero, new Vector3(0.8f, 0.9f, 0.8f), bodyMat);
        Primitive(PrimitiveType.Cube, "Visor", visual.transform, new Vector3(0f, 0.6f, 0.4f), new Vector3(0.6f, 0.2f, 0.25f), visorMat);

        GameObject prefab = PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
        Object.DestroyImmediate(root);
        return prefab.GetComponent<NetworkObject>();
    }

    private static void BuildShip()
    {
        GameObject ship = new("Ship");
        ship.transform.position = new Vector3(60f, 20f, 0f);
        ShipFrame frame = ship.AddComponent<ShipFrame>();

        Transform hull = new GameObject("Hull").transform;
        hull.SetParent(ship.transform, false);

        Material hullMat = GetMaterial("Hull", new Color(0.55f, 0.6f, 0.68f));
        Material floorMat = GetMaterial("Floor", new Color(0.25f, 0.28f, 0.32f));
        Material propMat = GetMaterial("Prop", new Color(0.3f, 0.6f, 0.45f));

        // Interior is x -6..6, y 0..4, z -10..10. Walls are 0.5 thick.
        Box(hull, "Floor", new Vector3(0f, -0.25f, 0f), new Vector3(13f, 0.5f, 21f), floorMat);
        Box(hull, "Ceiling", new Vector3(0f, 4.25f, 0f), new Vector3(13f, 0.5f, 21f), hullMat);

        // Side and front walls have a window band (an invisible collider acts as glass).
        WindowedWall(hull, "Left", new Vector3(-6.25f, 0f, 0f), new Vector3(0.5f, 0f, 21f), hullMat);
        WindowedWall(hull, "Right", new Vector3(6.25f, 0f, 0f), new Vector3(0.5f, 0f, 21f), hullMat);
        WindowedWall(hull, "Front", new Vector3(0f, 0f, 10.25f), new Vector3(13f, 0f, 0.5f), hullMat);

        // Back wall with an open 3m-wide doorway into space.
        Box(hull, "Back_L", new Vector3(-4f, 2f, -10.25f), new Vector3(5f, 4.5f, 0.5f), hullMat);
        Box(hull, "Back_R", new Vector3(4f, 2f, -10.25f), new Vector3(5f, 4.5f, 0.5f), hullMat);
        Box(hull, "Back_Top", new Vector3(0f, 3.75f, -10.25f), new Vector3(3f, 1f, 0.5f), hullMat);

        // Something to walk on and around.
        Box(hull, "Platform", new Vector3(-3.5f, 0.5f, 3f), new Vector3(3f, 1f, 4f), propMat);
        GameObject ramp = Box(hull, "Ramp", new Vector3(-3.5f, 0.45f, -0.6f), new Vector3(3f, 0.2f, 3.3f), propMat);
        ramp.transform.localRotation = Quaternion.Euler(-17f, 0f, 0f);
        Box(hull, "Crate_A", new Vector3(3.5f, 0.5f, 6f), Vector3.one, propMat);
        Box(hull, "Crate_B", new Vector3(4.6f, 0.5f, 6.2f), Vector3.one, propMat);
        Box(hull, "Crate_C", new Vector3(4f, 1.5f, 6.1f), Vector3.one, propMat);

        SerializedObject so = new(frame);
        so.FindProperty("_hull").objectReferenceValue = hull;
        so.ApplyModifiedPropertiesWithoutUndo();
    }

    private static void WindowedWall(Transform parent, string name, Vector3 center, Vector3 size, Material mat)
    {
        const float bottom = 1.2f;
        const float top = 3f;
        Box(parent, name + "_Low", new Vector3(center.x, (bottom - 0.25f) * 0.5f, center.z), new Vector3(size.x, bottom + 0.25f, size.z), mat);
        Box(parent, name + "_High", new Vector3(center.x, (top + 4.25f) * 0.5f, center.z), new Vector3(size.x, 4.25f - top, size.z), mat);

        GameObject glass = new(name + "_Glass");
        glass.transform.SetParent(parent, false);
        glass.transform.localPosition = new Vector3(center.x, (bottom + top) * 0.5f, center.z);
        glass.AddComponent<BoxCollider>().size = new Vector3(size.x, top - bottom, size.z);
    }

    private static void BuildAsteroids()
    {
        Transform root = new GameObject("Asteroids").transform;
        Material mat = GetMaterial("Asteroid", new Color(0.4f, 0.37f, 0.35f));

        Random.State old = Random.state;
        Random.InitState(1234);
        for (int i = 0; i < 40; i++)
        {
            Vector2 flat = Random.insideUnitCircle * 140f;
            Vector3 pos = new(flat.x, Random.Range(-10f, 50f), flat.y);
            // Keep the ship's flight path clear.
            float ring = new Vector2(pos.x, pos.z).magnitude;
            if (Mathf.Abs(ring - 60f) < 18f)
                pos += new Vector3(pos.x, 0f, pos.z).normalized * 30f;

            GameObject rock = Primitive(PrimitiveType.Cube, $"Asteroid_{i}", root, pos, Vector3.one * Random.Range(2f, 9f), mat);
            rock.transform.rotation = Random.rotation;
        }
        Random.state = old;

        Primitive(PrimitiveType.Cube, "Station", root, new Vector3(0f, 0f, 0f), new Vector3(30f, 2f, 30f), GetMaterial("Station", new Color(0.7f, 0.7f, 0.75f)));
    }

    private static GameObject Box(Transform parent, string name, Vector3 localPos, Vector3 size, Material mat) =>
        Primitive(PrimitiveType.Cube, name, parent, localPos, size, mat);

    private static GameObject Primitive(PrimitiveType type, string name, Transform parent, Vector3 localPos, Vector3 scale, Material mat)
    {
        GameObject go = GameObject.CreatePrimitive(type);
        go.name = name;
        go.transform.SetParent(parent, false);
        go.transform.localPosition = localPos;
        go.transform.localScale = scale;
        go.GetComponent<Renderer>().sharedMaterial = mat;
        // Prefab visuals shouldn't collide; ShipPlayer creates its own body.
        if (parent != null && parent.GetComponentInParent<ShipPlayer>() != null)
            Object.DestroyImmediate(go.GetComponent<Collider>());
        return go;
    }

    private static Material GetMaterial(string name, Color color)
    {
        string path = $"{Folder}/{name}.mat";
        Material mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (mat != null)
            return mat;

        Shader shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
        mat = new Material(shader);
        mat.SetColor("_BaseColor", color);
        mat.color = color;
        AssetDatabase.CreateAsset(mat, path);
        return mat;
    }
}
