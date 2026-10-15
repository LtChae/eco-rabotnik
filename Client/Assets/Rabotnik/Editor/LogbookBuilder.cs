// Builds the Rabotnik client bundle from simple_book.glb, RabotnikMaskIcon.png and images/rabotnik_imprint.png (the cover
// imprint): the RabotnikLogbookObject prefab, the
// RabotnikLogbookItem and RabotnikMaskItem icons, the mod scene and rabotnik.unity3d. Run headless:
//   Unity.exe -batchmode -projectPath Client -executeMethod LogbookBuilder.Build   (no -nographics: the icon is rendered)
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class LogbookBuilder
{
    const string Dir = "Assets/Rabotnik";
    const string ModelPath = Dir + "/simple_book.glb";
    const float ModelScale = 1.2f; // the glTFast import already applies 0.01; 1.2x gives a ~0.30 x 0.22 x 0.34 m tome
    const string BundleName = "rabotnik";
    // The Rabotnik mask icon ("Android mask" by Lorc, CC BY 4.0, see CREDITS.md), made transparent from images/rabotnik_mask.png.
    // Shipped as the icon of the hidden server item RabotnikMaskItem (RabotnikIcons.cs): the client ignores an icon with no item of
    // the same name. The Rabotnik tab (RabotnikComponent.IconName) and Ecopedia pages (icon="RabotnikMaskItem") use it.
    const string MaskIconPath = Dir + "/RabotnikMaskIcon.png";
    const string MaskIconName = "RabotnikMaskItem";
    // The same mask, white on transparent, stamped in gold foil on the front cover (BuildImprint). Read straight from the repo.
    const string ImprintPath = "../images/rabotnik_imprint.png";
    const int ImprintGrid = 128;        // sampling resolution: ~1 mm steps on the cover
    const float ImprintWidth = 0.55f;   // fraction of the cover's width
    const float ImprintLift = 0.0005f;  // above the cover face, against z-fighting
    static readonly Color FoilColor = new Color(0.85f, 0.67f, 0.28f);
    // The worker's name on the spine (BuildSpineText): fractions of the book's height (along) and thickness (across).
    const float SpineTextLength = 0.8f;
    const float SpineTextWidth = 0.6f;
    const float SpineLift = 0.001f;     // in front of the spine's outermost point
    const float SpineTextScale = 0.01f; // SpineAnchor's scale: the text is laid out in centimetres

    public static void Build()
    {
        AssetDatabase.DeleteAsset(Dir + "/Materials"); // regenerated below; drops leftovers from earlier runs
        AssetDatabase.CreateFolder(Dir, "Materials");
        if (!File.Exists(ModelPath) && File.Exists("Assets/simple_book.glb"))
            AssetDatabase.MoveAsset("Assets/simple_book.glb", ModelPath);
        AssetDatabase.Refresh();

        var model = AssetDatabase.LoadAssetAtPath<GameObject>(ModelPath);
        if (model == null) { Debug.LogError("Could not load " + ModelPath); EditorApplication.Exit(1); return; }

        var mesh = BakeMesh(model, out var materials);
        var objectPrefab = BuildObjectPrefab(mesh, materials);
        var iconSprite = RenderIcon(mesh, materials);
        var maskSprite = ImportSprite(MaskIconPath);
        if (maskSprite == null) { Debug.LogError("Could not load " + MaskIconPath); EditorApplication.Exit(1); return; }
        BuildScene(objectPrefab, iconSprite, maskSprite);
        BuildBundle();
        Debug.Log("LogbookBuilder: done");
    }

    // Source material -> Curved Standard material (one per distinct source), keeping colour and base texture.
    static Material Convert(Material s, Shader shader)
    {
        var path = $"{Dir}/Materials/{Sanitize(s != null ? s.name : "mat")}.mat";
        var m = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (m == null) { m = new Material(shader); AssetDatabase.CreateAsset(m, path); }
        m.shader = shader;
        if (s != null)
        {
            var tex = Tex(s, "baseColorTexture") ?? Tex(s, "_MainTex") ?? Tex(s, "_BaseMap");
            if (tex != null) m.SetTexture("_MainTex", tex);
            if (s.HasProperty("baseColorFactor")) m.SetColor("_Color", s.GetColor("baseColorFactor"));
            else if (s.HasProperty("_BaseColor")) m.SetColor("_Color", s.GetColor("_BaseColor"));
            else if (s.HasProperty("_Color")) m.SetColor("_Color", s.GetColor("_Color"));
        }
        EditorUtility.SetDirty(m);
        return m;
    }

    static string Sanitize(string n) { foreach (var c in Path.GetInvalidFileNameChars()) n = n.Replace(c, '_'); return n.Replace(' ', '_'); }

    static Texture Tex(Material m, string prop) => m.HasProperty(prop) ? m.GetTexture(prop) : null;

    static Shader FindCurvedShader()
    {
        foreach (var guid in AssetDatabase.FindAssets("CurvedStandard t:Shader"))
        {
            var s = AssetDatabase.LoadAssetAtPath<Shader>(AssetDatabase.GUIDToAssetPath(guid));
            if (s != null && s.name.Contains("Standard") && !s.name.Contains("Fade")) return s;
        }
        Debug.LogError("CurvedStandard shader not found");
        return Shader.Find("Standard");
    }

    // Bakes the glb's ~90 page meshes into ONE mesh (one submesh per material), scaled to metres with the origin at the
    // bottom-centre, so it rests on the table surface.
    static Mesh BakeMesh(GameObject model, out Material[] materials)
    {
        var shader = FindCurvedShader();
        var inst = (GameObject)PrefabUtility.InstantiatePrefab(model);
        var groups = new Dictionary<Material, List<CombineInstance>>();
        var points = new Dictionary<Material, List<Vector3>>();
        var scale = Matrix4x4.Scale(Vector3.one * ModelScale);
        foreach (var mf in inst.GetComponentsInChildren<MeshFilter>())
        {
            var mr = mf.GetComponent<MeshRenderer>();
            if (mr == null || mf.sharedMesh == null) continue;
            var m = scale * mf.transform.localToWorldMatrix;
            var verts = mf.sharedMesh.vertices;
            for (int sub = 0; sub < mf.sharedMesh.subMeshCount; sub++)
            {
                var src = sub < mr.sharedMaterials.Length ? mr.sharedMaterials[sub] : null;
                var key = Convert(src, shader);
                if (!groups.ContainsKey(key)) { groups[key] = new List<CombineInstance>(); points[key] = new List<Vector3>(); }
                groups[key].Add(new CombineInstance { mesh = mf.sharedMesh, subMeshIndex = sub, transform = m });
                // submesh vertices (all of the mesh's vertices when it has a single submesh)
                if (mf.sharedMesh.subMeshCount == 1) foreach (var v in verts) points[key].Add(m.MultiplyPoint3x4(v));
                else foreach (var i in mf.sharedMesh.GetIndices(sub)) points[key].Add(m.MultiplyPoint3x4(verts[i]));
            }
        }
        Object.DestroyImmediate(inst);

        // The glb's node rotations leave the book at an arbitrary angle, so find its axes from the vertices: thinnest = cover
        // thickness, longest = height, middle = width. Stand it upright on its bottom edge, spine towards +Z.
        var all = new List<Vector3>();
        foreach (var kv in points) all.AddRange(kv.Value);
        PrincipalAxes(all, out var thick, out var height, out var width);
        // The spine is the end of the width axis where the cover has vertices in the middle of its thickness (the plate joining
        // the two boards); at the fore-edge the cover is just two separate boards.
        Material cover = null;
        foreach (var k in groups.Keys) if (k.name.Contains("Plastic")) cover = k;
        if (cover != null)
        {
            Range(points[cover], width, out var wLo, out var wHi);
            Range(points[cover], thick, out var tLo, out var tHi);
            float tMid = (tLo + tHi) / 2, tHalf = (tHi - tLo) / 2, wSpan = wHi - wLo;
            int lowMid = 0, highMid = 0;
            foreach (var pt in points[cover])
            {
                if (Mathf.Abs(Vector3.Dot(pt, thick) - tMid) > tHalf * 0.4f) continue;
                var w = Vector3.Dot(pt, width);
                if (w < wLo + wSpan * 0.15f) lowMid++; else if (w > wHi - wSpan * 0.15f) highMid++;
            }
            Debug.Log($"Spine test: cover verts mid-thickness at low end {lowMid}, high end {highMid}");
            if (lowMid > highMid) width = -width; // spine was on the low side; flip so it is on +width
        }        if (Vector3.Dot(Vector3.Cross(thick, height), width) < 0) thick = -thick; // keep a proper rotation, no mirroring
        var upright = new Matrix4x4();
        upright.SetRow(0, new Vector4(thick.x, thick.y, thick.z, 0));  // X = thickness
        upright.SetRow(1, new Vector4(height.x, height.y, height.z, 0)); // Y = height (up)
        upright.SetRow(2, new Vector4(width.x, width.y, width.z, 0));   // Z = width, spine at +Z
        upright.SetRow(3, new Vector4(0, 0, 0, 1));

        var bounds = new Bounds(upright.MultiplyPoint3x4(all[0]), Vector3.zero);
        foreach (var v in all) bounds.Encapsulate(upright.MultiplyPoint3x4(v));
        var shift = Matrix4x4.Translate(new Vector3(-bounds.center.x, -bounds.min.y, -bounds.center.z)) * upright;

        var perMaterial = new List<CombineInstance>();
        var mats = new List<Material>();
        foreach (var kv in groups)
        {
            var list = kv.Value;
            for (int i = 0; i < list.Count; i++) { var ci = list[i]; ci.transform = shift * ci.transform; list[i] = ci; }
            var part = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            part.CombineMeshes(list.ToArray(), true, true);
            perMaterial.Add(new CombineInstance { mesh = part, subMeshIndex = 0, transform = Matrix4x4.identity });
            mats.Add(kv.Key);
        }
        var coverPts = new List<Vector3>();
        foreach (var p in cover != null ? points[cover] : all) coverPts.Add(shift.MultiplyPoint3x4(p));
        var imprint = BuildImprint(coverPts);
        if (imprint != null)
        {
            perMaterial.Add(new CombineInstance { mesh = imprint, subMeshIndex = 0, transform = Matrix4x4.identity });
            mats.Add(FoilMaterial(shader));
        }
        var mesh = new Mesh { name = "RabotnikLogbookMesh", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
        mesh.CombineMeshes(perMaterial.ToArray(), false, false);
        mesh.RecalculateBounds();
        mesh.RecalculateTangents();
        var meshPath = Dir + "/RabotnikLogbookMesh.asset";
        AssetDatabase.DeleteAsset(meshPath);
        AssetDatabase.CreateAsset(mesh, meshPath);
        materials = mats.ToArray();
        Debug.Log($"Book mesh: {mesh.vertexCount} verts, {mesh.subMeshCount} submeshes, bounds {mesh.bounds.size} m");
        return mesh;
    }

    // The Rabotnik mask stamped on the front cover as a thin layer of geometry: one quad per run of opaque pixels on an
    // ImprintGrid x ImprintGrid sampling of the image. Geometry rather than a decal, because the Curved shader's forward pass
    // has no alpha cutout. The front cover is the -X face (seen from -X, the spine at +Z is on the left), image up is +Y.
    static Mesh BuildImprint(List<Vector3> cover)
    {
        var tex = new Texture2D(2, 2);
        if (!File.Exists(ImprintPath) || !tex.LoadImage(File.ReadAllBytes(ImprintPath))) { Debug.LogError("Could not load " + ImprintPath); return null; }
        var b = new Bounds(cover[0], Vector3.zero);
        foreach (var p in cover) b.Encapsulate(p);
        // the board's outer face, measured in the middle of the cover (a rounded spine can bulge further out)
        float face = float.MaxValue;
        foreach (var p in cover)
            if (Mathf.Abs(p.z - b.center.z) < b.size.z * 0.25f && Mathf.Abs(p.y - b.center.y) < b.size.y * 0.25f) face = Mathf.Min(face, p.x);
        if (face == float.MaxValue) face = b.min.x;

        float size = b.size.z * ImprintWidth, cell = size / ImprintGrid, x = face - ImprintLift;
        float top = b.center.y + b.size.y * 0.04f + size / 2; // a little above the middle of the cover
        float left = b.center.z + size / 2;                   // image left edge, towards the spine (+Z)
        var verts = new List<Vector3>(); var uvs = new List<Vector2>(); var tris = new List<int>();
        for (int r = 0; r < ImprintGrid; r++)
            for (int c = 0; c < ImprintGrid; )
            {
                if (!Opaque(tex, c, r)) { c++; continue; }
                int c0 = c; while (c < ImprintGrid && Opaque(tex, c, r)) c++;
                float y0 = top - r * cell, y1 = top - (r + 1) * cell, z0 = left - c0 * cell, z1 = left - c * cell;
                int i = verts.Count; // TL, TR, BR, BL, clockwise as seen from -X
                verts.Add(new Vector3(x, y0, z0)); verts.Add(new Vector3(x, y0, z1)); verts.Add(new Vector3(x, y1, z1)); verts.Add(new Vector3(x, y1, z0));
                float u0 = (float)c0 / ImprintGrid, u1 = (float)c / ImprintGrid, v0 = 1 - (float)r / ImprintGrid, v1 = 1 - (float)(r + 1) / ImprintGrid;
                uvs.Add(new Vector2(u0, v0)); uvs.Add(new Vector2(u1, v0)); uvs.Add(new Vector2(u1, v1)); uvs.Add(new Vector2(u0, v1));
                tris.AddRange(new[] { i, i + 1, i + 2, i, i + 2, i + 3 });
            }
        Object.DestroyImmediate(tex);
        var mesh = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
        mesh.SetVertices(verts); mesh.SetUVs(0, uvs); mesh.SetTriangles(tris, 0);
        var normals = new Vector3[verts.Count];
        for (int i = 0; i < normals.Length; i++) normals[i] = Vector3.left;
        mesh.normals = normals;
        Debug.Log($"Imprint: {verts.Count / 4} quads, {size:F3} m wide, on the cover face at x={face:F4}");
        return mesh;
    }

    // Texture2D rows run bottom-up; r counts down from the top of the image.
    static bool Opaque(Texture2D tex, int c, int r) => tex.GetPixelBilinear((c + 0.5f) / ImprintGrid, 1 - (r + 0.5f) / ImprintGrid).a >= 0.5f;

    static Material FoilMaterial(Shader shader)
    {
        var m = new Material(shader) { name = "Imprint_Foil" };
        m.SetColor("_Color", FoilColor);
        m.SetFloat("_Metallic", 0.5f);
        m.SetFloat("_Glossiness", 0.6f);
        AssetDatabase.CreateAsset(m, $"{Dir}/Materials/Imprint_Foil.mat");
        return m;
    }

    static void Range(List<Vector3> pts, Vector3 axis, out float lo, out float hi)
    {
        lo = float.MaxValue; hi = float.MinValue;
        foreach (var p in pts) { var d = Vector3.Dot(p, axis); lo = Mathf.Min(lo, d); hi = Mathf.Max(hi, d); }
    }

    // Axes of the point cloud from its covariance (Jacobi eigen-decomposition): smallest, largest, middle spread.
    static void PrincipalAxes(List<Vector3> pts, out Vector3 smallest, out Vector3 largest, out Vector3 middle)
    {
        var mean = Vector3.zero; foreach (var p in pts) mean += p; mean /= pts.Count;
        var c = new double[3, 3];
        foreach (var p in pts)
        {
            var d = p - mean; var a = new double[] { d.x, d.y, d.z };
            for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) c[i, j] += a[i] * a[j];
        }
        var v = new double[3, 3] { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };
        for (int sweep = 0; sweep < 30; sweep++)
            for (int pI = 0; pI < 2; pI++) for (int q = pI + 1; q < 3; q++)
            {
                if (System.Math.Abs(c[pI, q]) < 1e-14) continue;
                double theta = (c[q, q] - c[pI, pI]) / (2 * c[pI, q]);
                double tt = System.Math.Sign(theta == 0 ? 1 : theta) / (System.Math.Abs(theta) + System.Math.Sqrt(theta * theta + 1));
                double cs = 1 / System.Math.Sqrt(tt * tt + 1), sn = tt * cs;
                for (int k = 0; k < 3; k++) { double a = c[k, pI], b = c[k, q]; c[k, pI] = cs * a - sn * b; c[k, q] = sn * a + cs * b; }
                for (int k = 0; k < 3; k++) { double a = c[pI, k], b = c[q, k]; c[pI, k] = cs * a - sn * b; c[q, k] = sn * a + cs * b; }
                for (int k = 0; k < 3; k++) { double a = v[k, pI], b = v[k, q]; v[k, pI] = cs * a - sn * b; v[k, q] = sn * a + cs * b; }
            }
        var order = new[] { 0, 1, 2 };
        System.Array.Sort(order, (a, b) => c[a, a].CompareTo(c[b, b]));
        Vector3 Col(int i) => new Vector3((float)v[0, i], (float)v[1, i], (float)v[2, i]).normalized;
        smallest = Col(order[0]); middle = Col(order[1]); largest = Col(order[2]);
    }
    static GameObject MakeModelObject(string name, Mesh mesh, Material[] materials, Transform parent)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        go.AddComponent<MeshRenderer>().sharedMaterials = materials;
        return go;
    }

    // Root (ModObject, Sign) > Model, SpineAnchor > SpineText. Origin at the bottom-centre of the book.
    static GameObject BuildObjectPrefab(Mesh mesh, Material[] materials)
    {
        var root = new GameObject("RabotnikLogbookObject") { tag = "ModObject" };
        MakeModelObject("Model", mesh, materials, root.transform);

        // Sign rather than plain WorldObject: the client fills SignText with the server's CustomTextComponent (the worker's name).
        var sign = root.AddComponent<Sign>();
        sign.SignText = BuildSpineText(mesh.bounds, root.transform);
        sign.ExtraSignTexts = new TMPro.TextMeshPro[0];
        root.AddComponent<HighlightableObject>();
        var box = root.AddComponent<BoxCollider>();
        box.center = mesh.bounds.center;
        box.size = mesh.bounds.size;
        sign.size = Vector3.one;

        var prefab = PrefabUtility.SaveAsPrefabAsset(root, Dir + "/RabotnikLogbookObject.prefab");
        Object.DestroyImmediate(root);
        return prefab;
    }

    // Gold text along the spine (the +Z face), reading top to bottom with the letters' tops towards the front cover (-X), as on
    // an English book. Font left empty: ReuseVanillaFont gives it the vanilla sign font at runtime, so none is bundled.
    // SpineAnchor holds the placement and a 1/100 scale; the text under it has an identity transform and sizes in centimetres.
    // Needed: with both on the text itself, in game it came out lying flat and ~50x too big, so the client resets the text's
    // own transform (the IL2CPP client can't be decompiled to check). Verified in game that this layout sits on the spine.
    static TMPro.TextMeshPro BuildSpineText(Bounds b, Transform parent)
    {
        var anchor = new GameObject("SpineAnchor");
        anchor.transform.SetParent(parent, false);
        anchor.transform.localPosition = new Vector3(b.center.x, b.center.y, b.max.z + SpineLift);
        anchor.transform.localRotation = Quaternion.LookRotation(Vector3.back, Vector3.left); // text faces +Z, reads along -Y
        anchor.transform.localScale = Vector3.one * SpineTextScale;

        var go = new GameObject("SpineText");
        go.transform.SetParent(anchor.transform, false);
        var tmp = go.AddComponent<TMPro.TextMeshPro>();
        var so = new SerializedObject(tmp);
        so.FindProperty("m_fontAsset").objectReferenceValue = null;
        so.FindProperty("m_sharedMaterial").objectReferenceValue = null;
        so.ApplyModifiedPropertiesWithoutUndo();
        tmp.rectTransform.sizeDelta = new Vector2(b.size.y * SpineTextLength, b.size.x * SpineTextWidth) / SpineTextScale;
        tmp.text = "";
        tmp.color = FoilColor;
        tmp.fontStyle = TMPro.FontStyles.UpperCase; // no Bold: the sign font is heavy already, and bold blurred at spine size
        tmp.characterSpacing = 4f;
        tmp.alignment = TMPro.TextAlignmentOptions.Center;
        tmp.textWrappingMode = TMPro.TextWrappingModes.NoWrap;
        tmp.enableAutoSizing = true; // long names shrink to fit the spine
        tmp.fontSizeMin = 1f;
        tmp.fontSizeMax = 26f;       // leaves a margin either side on the spine
        tmp.fontSize = 26f;
        tmp.margin = Vector4.zero;
        go.AddComponent<ReuseVanillaFont>().FontName = "mainfont SDF Worldspace";
        Debug.Log($"Spine text: {tmp.rectTransform.sizeDelta.x:F1} x {tmp.rectTransform.sizeDelta.y:F1} cm at {anchor.transform.localPosition}");
        return tmp;
    }

    static Sprite RenderIcon(Mesh mesh, Material[] materials)
    {
        var pngPath = Dir + "/RabotnikLogbookIcon.png";
        var stage = new GameObject("IconStage");
        // The Curved shaders depend on globals the game sets, so the icon is rendered with Standard copies of the materials.
        var plain = new Material[materials.Length];
        for (int i = 0; i < plain.Length; i++)
        {
            plain[i] = new Material(Shader.Find("Standard"));
            plain[i].color = materials[i].HasProperty("_Color") ? materials[i].GetColor("_Color") : Color.white;
            if (materials[i].HasProperty("_MainTex") && materials[i].GetTexture("_MainTex") != null) plain[i].mainTexture = materials[i].GetTexture("_MainTex");
            plain[i].SetFloat("_Glossiness", 0.2f);
        }
        MakeModelObject("Model", mesh, plain, stage.transform);
        var b = mesh.bounds;

        var camGo = new GameObject("IconCam");
        var cam = camGo.AddComponent<Camera>();
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0, 0, 0, 0);
        cam.orthographic = true;
        cam.orthographicSize = Mathf.Max(b.size.x, b.size.y, b.size.z) * 0.7f;
        cam.nearClipPlane = 0.01f; cam.farClipPlane = 10f;
        var look = b.center;
        // three-quarter view showing the front cover (-X, with the imprint) and the spine (+Z)
        cam.transform.position = look + new Vector3(-1.0f, 0.5f, 0.8f).normalized * 3f;
        cam.transform.LookAt(look);
        var lightGo = new GameObject("IconLight");
        var light = lightGo.AddComponent<Light>();
        light.type = LightType.Directional; light.intensity = 1.2f;
        lightGo.transform.rotation = Quaternion.Euler(40, 30, 0);
        RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
        RenderSettings.ambientLight = new Color(0.6f, 0.6f, 0.6f);

        var rt = new RenderTexture(256, 256, 24, RenderTextureFormat.ARGB32);
        cam.targetTexture = rt;
        cam.Render();
        RenderTexture.active = rt;
        var tex = new Texture2D(256, 256, TextureFormat.RGBA32, false);
        tex.ReadPixels(new Rect(0, 0, 256, 256), 0, 0);
        tex.Apply();
        File.WriteAllBytes(pngPath, tex.EncodeToPNG());
        RenderTexture.active = null;
        Object.DestroyImmediate(rt); Object.DestroyImmediate(tex);
        Object.DestroyImmediate(stage); Object.DestroyImmediate(camGo); Object.DestroyImmediate(lightGo);

        return ImportSprite(pngPath);
    }

    static Sprite ImportSprite(string pngPath)
    {
        AssetDatabase.ImportAsset(pngPath);
        var imp = (TextureImporter)AssetImporter.GetAtPath(pngPath);
        if (imp == null) return null;
        imp.textureType = TextureImporterType.Sprite;
        imp.spriteImportMode = SpriteImportMode.Single; // the default is Multiple, which has no sprite to load
        imp.alphaIsTransparency = true;
        imp.SaveAndReimport();
        return AssetDatabase.LoadAssetAtPath<Sprite>(pngPath);
    }

    static void BuildScene(GameObject objectPrefab, Sprite icon, Sprite mask)
    {
        var scenePath = Dir + "/Rabotnik.unity";
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        var objects = new GameObject("Objects");
        objects.AddComponent<ModkitPrefabContainer>().Prefabs = new[] { objectPrefab };

        var items = new GameObject("Items");
        AddIcon(items.transform, "RabotnikLogbookItem", icon);
        // Not an item: an icon looked up by name (component tab, Ecopedia). Same ItemTemplate setup as an item icon.
        AddIcon(items.transform, MaskIconName, mask);

        EditorSceneManager.SaveScene(scene, scenePath);
    }

    // An ItemTemplate under Items: the client knows the icon by the GameObject's name.
    static void AddIcon(Transform items, string name, Sprite sprite)
    {
        var template = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/EcoModKit/Prefabs/ItemTemplate.prefab");
        var item = (GameObject)PrefabUtility.InstantiatePrefab(template, items);
        PrefabUtility.UnpackPrefabInstance(item, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
        item.name = name;
        var it = item.GetComponent<ItemTemplate>();
        // via SerializedObject so the change is recorded and saved with the scene
        var fg = new SerializedObject(it.foreground); fg.FindProperty("m_Sprite").objectReferenceValue = sprite; fg.ApplyModifiedPropertiesWithoutUndo();
        var bg = new SerializedObject(it.background); bg.FindProperty("m_Sprite").objectReferenceValue = null; bg.ApplyModifiedPropertiesWithoutUndo(); // vanilla UI_ICONS atlas reference would bundle the whole atlas
        Debug.Log($"Icon {name}: sprite {(sprite != null ? sprite.name : "NULL")}, foreground now: {(it.foreground.sprite != null ? it.foreground.sprite.name : "NULL")}");
        var iconName = item.transform.Find("IconName");
        if (iconName != null) Object.DestroyImmediate(iconName.gameObject); // references mainfont
    }

    static void BuildBundle()
    {
        PlayerSettings.stripUnusedMeshComponents = false;
        var scenePath = Dir + "/Rabotnik.unity";
        var scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
        foreach (var root in scene.GetRootGameObjects()) root.SetActive(false);
        EditorSceneManager.SaveScene(scene);

        var importer = AssetImporter.GetAtPath(scenePath);
        importer.SetAssetBundleNameAndVariant(BundleName, "");

        Directory.CreateDirectory("AssetBundles");
        var manifest = BuildPipeline.BuildAssetBundles("AssetBundles", BuildAssetBundleOptions.ChunkBasedCompression, EditorUserBuildSettings.activeBuildTarget);
        if (manifest == null) { Debug.LogError("Bundle build failed"); EditorApplication.Exit(1); return; }
        var outDir = Path.GetFullPath("../Mods/UserCode/Rabotnik");
        Directory.CreateDirectory(outDir);
        File.Copy(Path.Combine("AssetBundles", BundleName), Path.Combine(outDir, BundleName + ".unity3d"), true);
        Debug.Log("Bundle written to " + Path.Combine(outDir, BundleName + ".unity3d"));

        foreach (var root in scene.GetRootGameObjects()) root.SetActive(true);
        EditorSceneManager.SaveScene(scene);
    }
}
