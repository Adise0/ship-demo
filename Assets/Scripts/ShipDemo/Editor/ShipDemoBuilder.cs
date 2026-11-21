using System.Reflection;
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

        AssignSceneIds(scene);
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

    /// <summary>
    /// Replaces just the ship and fighter in the open scene, keeping everything else (network
    /// settings, lighting, edits) as it is.
    /// </summary>
    [MenuItem("Tools/Ship Demo/Rebuild Ship")]
    private static void RebuildShip()
    {
        var scene = EditorSceneManager.GetActiveScene();
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            if (root.name == "Ship" || root.name == "Fighter")
                Object.DestroyImmediate(root);
        }

        BuildShip();
        AssignSceneIds(scene);
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        Debug.Log($"Rebuilt the ship and fighter in {scene.path}.");
    }

    /// <summary>
    /// FishNet gives new scene NetworkObjects their ids from OnValidate, but at most once per
    /// 250ms, so of several objects created at once only the first gets one. Run its generator
    /// directly (it's internal) so every new object has an id.
    /// </summary>
    private static void AssignSceneIds(UnityEngine.SceneManagement.Scene scene)
    {
        MethodInfo createSceneIds = typeof(NetworkObject).GetMethod("CreateSceneId", BindingFlags.NonPublic | BindingFlags.Static,
            null, new[] { typeof(UnityEngine.SceneManagement.Scene), typeof(bool), typeof(int).MakeByRefType() }, null);
        if (createSceneIds == null)
        {
            Debug.LogWarning("Couldn't find FishNet's scene id generator. Use Tools > Fish-Networking > Utility > Reserialize NetworkObjects.");
            return;
        }
        createSceneIds.Invoke(null, new object[] { scene, false, 0 });
    }

    // Ship-local layout. x -7..7, z -16..16 (forward is +z). Walls and floors are 0.5 thick.
    //   Lower deck (hangar): floor top y 0, ceiling y 5. Floor hatch x -3..3, z -2..6 for the fighter.
    //   Upper deck (bridge): floor top y 5.5, ceiling y 9.5. Helm at the front.
    //   A ramp along the right wall joins them. The back of the hangar is open to space (a 3m door).
    private const float DeckHeight = 5.5f;
    private static readonly Vector3 HatchMin = new(-3f, 0f, -2f);
    private static readonly Vector3 HatchMax = new(3f, 0f, 6f);
    private static readonly Vector3 DockPosition = new(0f, 1f, 2f);
    private static readonly Vector3 HelmSeat = new(0f, 6.4f, 13f);

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
        Material guideMat = GetMaterial("Guide", new Color(1f, 0.75f, 0.1f));

        // Lower deck floor, around the hatch.
        Box(hull, "Floor_Back", new Vector3(0f, -0.25f, -9.25f), new Vector3(15f, 0.5f, 14.5f), floorMat);
        Box(hull, "Floor_Front", new Vector3(0f, -0.25f, 11.25f), new Vector3(15f, 0.5f, 10.5f), floorMat);
        Box(hull, "Floor_Left", new Vector3(-5.25f, -0.25f, 2f), new Vector3(4.5f, 0.5f, 8f), floorMat);
        Box(hull, "Floor_Right", new Vector3(5.25f, -0.25f, 2f), new Vector3(4.5f, 0.5f, 8f), floorMat);

        // Hatch: railing inside so nobody walks into space by accident, and bright markings
        // underneath so the pilot can find it.
        Vector3 hatchCenter = (HatchMin + HatchMax) * 0.5f;
        Vector3 hatchSize = HatchMax - HatchMin;
        Box(hull, "Rail_Left", new Vector3(HatchMin.x - 0.2f, 0.55f, hatchCenter.z), new Vector3(0.1f, 1.1f, hatchSize.z), propMat);
        Box(hull, "Rail_Right", new Vector3(HatchMax.x + 0.2f, 0.55f, hatchCenter.z), new Vector3(0.1f, 1.1f, hatchSize.z), propMat);
        Box(hull, "Rail_Back", new Vector3(0f, 0.55f, HatchMin.z - 0.2f), new Vector3(hatchSize.x + 0.5f, 1.1f, 0.1f), propMat);
        Box(hull, "Rail_Front", new Vector3(0f, 0.55f, HatchMax.z + 0.2f), new Vector3(hatchSize.x + 0.5f, 1.1f, 0.1f), propMat);
        Box(hull, "Guide_Left", new Vector3(HatchMin.x - 0.4f, -0.55f, hatchCenter.z), new Vector3(0.8f, 0.1f, hatchSize.z + 1.6f), guideMat);
        Box(hull, "Guide_Right", new Vector3(HatchMax.x + 0.4f, -0.55f, hatchCenter.z), new Vector3(0.8f, 0.1f, hatchSize.z + 1.6f), guideMat);
        Box(hull, "Guide_Back", new Vector3(0f, -0.55f, HatchMin.z - 0.4f), new Vector3(hatchSize.x, 0.1f, 0.8f), guideMat);
        Box(hull, "Guide_Front", new Vector3(0f, -0.55f, HatchMax.z + 0.4f), new Vector3(hatchSize.x, 0.1f, 0.8f), guideMat);

        // Deck between the levels, open above the ramp (x 4..7, z -14..-1).
        Box(hull, "Deck_Main", new Vector3(-1.75f, 5.25f, 0f), new Vector3(11.5f, 0.5f, 33f), floorMat);
        Box(hull, "Deck_RightFront", new Vector3(5.75f, 5.25f, 7.75f), new Vector3(3.5f, 0.5f, 17.5f), floorMat);
        Box(hull, "Deck_RightBack", new Vector3(5.75f, 5.25f, -15.25f), new Vector3(3.5f, 0.5f, 2.5f), floorMat);
        Box(hull, "Ceiling", new Vector3(0f, 9.75f, 0f), new Vector3(15f, 0.5f, 33f), hullMat);

        // Ramp up the right side: from the hangar floor at z -14 to the upper deck at z -1.
        float rise = DeckHeight;
        float run = 13f;
        float angle = Mathf.Atan2(rise, run) * Mathf.Rad2Deg;
        float length = Mathf.Sqrt(rise * rise + run * run);
        GameObject ramp = Box(hull, "Ramp", Vector3.zero, new Vector3(2.6f, 0.2f, length), propMat);
        ramp.transform.localRotation = Quaternion.Euler(-angle, 0f, 0f);
        // Top surface through the line from (z -14, y 0) to (z -1, y 5.5).
        ramp.transform.localPosition = new Vector3(5.5f, rise * 0.5f, -14f + run * 0.5f) - ramp.transform.localRotation * new Vector3(0f, 0.1f, 0f);
        // Railing on the upper deck along the ramp opening.
        Box(hull, "Rail_Ramp_Side", new Vector3(3.95f, DeckHeight + 0.55f, -7.5f), new Vector3(0.1f, 1.1f, 13f), propMat);
        Box(hull, "Rail_Ramp_Back", new Vector3(5.5f, DeckHeight + 0.55f, -14.05f), new Vector3(3f, 1.1f, 0.1f), propMat);

        // Walls: solid around the hangar, windows around the bridge deck. An invisible collider acts as glass.
        Box(hull, "Left_Lower", new Vector3(-7.25f, 2.5f, 0f), new Vector3(0.5f, 6f, 33f), hullMat);
        Box(hull, "Right_Lower", new Vector3(7.25f, 2.5f, 0f), new Vector3(0.5f, 6f, 33f), hullMat);
        Box(hull, "Front_Lower", new Vector3(0f, 2.5f, 16.25f), new Vector3(15f, 6f, 0.5f), hullMat);
        WindowedWall(hull, "Left_Upper", new Vector3(-7.25f, 0f, 0f), new Vector3(0.5f, 0f, 33f), 6.7f, 8.5f, hullMat);
        WindowedWall(hull, "Right_Upper", new Vector3(7.25f, 0f, 0f), new Vector3(0.5f, 0f, 33f), 6.7f, 8.5f, hullMat);
        WindowedWall(hull, "Front_Upper", new Vector3(0f, 0f, 16.25f), new Vector3(15f, 0f, 0.5f), 6.3f, 9f, hullMat);

        // Back wall: a 3m doorway out of the hangar, solid behind the bridge deck.
        Box(hull, "Back_L", new Vector3(-4.5f, 2.5f, -16.25f), new Vector3(6f, 6f, 0.5f), hullMat);
        Box(hull, "Back_R", new Vector3(4.5f, 2.5f, -16.25f), new Vector3(6f, 6f, 0.5f), hullMat);
        Box(hull, "Back_Top", new Vector3(0f, 4.25f, -16.25f), new Vector3(3f, 2.5f, 0.5f), hullMat);
        Box(hull, "Back_Upper", new Vector3(0f, 7.75f, -16.25f), new Vector3(15f, 4.5f, 0.5f), hullMat);

        // Bridge: the helm console in front of the seat.
        Box(hull, "Helm_Console", new Vector3(0f, DeckHeight + 0.5f, HelmSeat.z + 1.5f), new Vector3(3f, 1f, 0.8f), propMat);

        // Something to walk around.
        Box(hull, "Crate_A", new Vector3(-5f, 0.5f, -12f), Vector3.one, propMat);
        Box(hull, "Crate_B", new Vector3(-3.9f, 0.5f, -12.2f), Vector3.one, propMat);
        Box(hull, "Crate_C", new Vector3(-4.5f, 1.5f, -12.1f), Vector3.one, propMat);
        Box(hull, "Crate_D", new Vector3(-5f, DeckHeight + 0.5f, -10f), Vector3.one, propMat);

        SerializedObject so = new(frame);
        so.FindProperty("_hull").objectReferenceValue = hull;
        so.FindProperty("_interior").boundsValue = new Bounds(new Vector3(0f, 4.75f, 0f), new Vector3(14f, 10.5f, 32f));
        so.FindProperty("_spawnLocal").vector3Value = new Vector3(0f, 1f, -9f);
        SerializedProperty zones = so.FindProperty("_gravityZones");
        zones.arraySize = 2;
        zones.GetArrayElementAtIndex(0).boundsValue = new Bounds(new Vector3(0f, 2.75f, 0f), new Vector3(14f, 5.5f, 32f));
        zones.GetArrayElementAtIndex(1).boundsValue = new Bounds(new Vector3(0f, 7.5f, 0f), new Vector3(14f, 4f, 32f));
        so.ApplyModifiedPropertiesWithoutUndo();

        // The helm is networked, so it's its own object (see ShipHelm).
        GameObject helmObject = new("Helm");
        helmObject.transform.SetParent(ship.transform, false);
        helmObject.AddComponent<NetworkObject>();
        ShipHelm helm = helmObject.AddComponent<ShipHelm>();
        SerializedObject helmSo = new(helm);
        helmSo.FindProperty("_ship").objectReferenceValue = frame;
        helmSo.FindProperty("_seatLocal").vector3Value = HelmSeat;
        helmSo.ApplyModifiedPropertiesWithoutUndo();

        BuildFighter(frame);
    }

    /// <summary>
    /// The fighter, docked in the hatch. Its visuals only render; Fighter makes its own body.
    /// </summary>
    private static void BuildFighter(ShipFrame carrier)
    {
        GameObject root = new("Fighter");
        root.transform.SetPositionAndRotation(carrier.transform.TransformPoint(DockPosition), carrier.transform.rotation);
        root.AddComponent<NetworkObject>();
        Fighter fighter = root.AddComponent<Fighter>();
        SerializedObject so = new(fighter);
        so.FindProperty("_carrierId").intValue = carrier.Id;
        so.FindProperty("_dockPosition").vector3Value = DockPosition;
        so.ApplyModifiedPropertiesWithoutUndo();

        Material bodyMat = GetMaterial("Fighter", new Color(0.8f, 0.25f, 0.2f));
        Material canopyMat = GetMaterial("Visor", new Color(0.1f, 0.15f, 0.2f));
        Material engineMat = GetMaterial("Prop", new Color(0.3f, 0.6f, 0.45f));

        // Forward is +z. The canopy is a box the pilot's eye sits inside; its faces don't render
        // from within, so the pilot sees out all around, including up through the hatch.
        Visual(root.transform, "Fuselage", new Vector3(0f, 0f, 0f), new Vector3(1.4f, 1f, 5.2f), bodyMat);
        Visual(root.transform, "Canopy", new Vector3(0f, 0.6f, 1f), new Vector3(1f, 0.6f, 1.6f), canopyMat);
        Visual(root.transform, "Wings", new Vector3(0f, -0.1f, -0.8f), new Vector3(4.4f, 0.15f, 1.8f), bodyMat);
        Visual(root.transform, "Engine", new Vector3(0f, 0f, -2.85f), new Vector3(0.9f, 0.7f, 0.6f), engineMat);
    }

    private static void Visual(Transform parent, string name, Vector3 localPos, Vector3 scale, Material mat)
    {
        GameObject go = Primitive(PrimitiveType.Cube, name, parent, localPos, scale, mat);
        Object.DestroyImmediate(go.GetComponent<Collider>());
    }

    /// <summary>
    /// Wall with a window band between windowBottom and windowTop (ship-local heights), spanning
    /// the upper deck. An invisible collider fills the window as glass.
    /// </summary>
    private static void WindowedWall(Transform parent, string name, Vector3 center, Vector3 size, float windowBottom, float windowTop, Material mat)
    {
        const float bottom = DeckHeight;
        const float top = 10f;
        Box(parent, name + "_Low", new Vector3(center.x, (bottom + windowBottom) * 0.5f, center.z), new Vector3(size.x, windowBottom - bottom, size.z), mat);
        Box(parent, name + "_High", new Vector3(center.x, (windowTop + top) * 0.5f, center.z), new Vector3(size.x, top - windowTop, size.z), mat);

        GameObject glass = new(name + "_Glass");
        glass.transform.SetParent(parent, false);
        glass.transform.localPosition = new Vector3(center.x, (windowBottom + windowTop) * 0.5f, center.z);
        glass.AddComponent<BoxCollider>().size = new Vector3(size.x, windowTop - windowBottom, size.z);
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
