using System;
using System.Collections.Generic;
using UnityEngine;
using CodeBase.Components.Potions;

namespace PotionCoop;

// «Тень» зелья: только картинка (те же меши, материалы и цвета жидкости), без физики и логики игры.
public static class Shadow
{
    // пары «настоящий рендерер → рендерер тени», чтобы потом обновлять цвета жидкости
    static readonly Dictionary<IntPtr, List<(MeshRenderer src, MeshRenderer dst)>> Pairs = new();

    // снять текущие цвета с настоящего зелья (вызывается в момент события)
    public static List<MaterialPropertyBlock> Snapshot(GameObject shadow)
    {
        if (shadow == null || !Pairs.TryGetValue(shadow.Pointer, out var pairs)) return null;
        var res = new List<MaterialPropertyBlock>();
        foreach (var (src, _) in pairs)
        {
            var b = new MaterialPropertyBlock();
            if (src != null) src.GetPropertyBlock(b);
            res.Add(b);
        }
        return res;
    }
    // применить снятые цвета к тени (вызывается с задержкой манекена)
    public static void Apply(GameObject shadow, List<MaterialPropertyBlock> snap)
    {
        if (shadow == null || snap == null || !Pairs.TryGetValue(shadow.Pointer, out var pairs)) return;
        for (int i = 0; i < pairs.Count && i < snap.Count; i++) if (pairs[i].dst != null) pairs[i].dst.SetPropertyBlock(snap[i]);
    }
    public static void Forget(GameObject shadow) { if (shadow != null) Pairs.Remove(shadow.Pointer); }

    public static GameObject Make(PotionFacade p)
    {
        var root = new GameObject("PotionCoop_Shadow_" + p.Uid);
        var src = p.transform;
        root.transform.position = src.position;
        root.transform.rotation = src.rotation;
        root.transform.localScale = src.lossyScale;
        var inv = Quaternion.Inverse(src.rotation);
        var mpb = new MaterialPropertyBlock();
        int copied = 0;
        var pairs = new List<(MeshRenderer, MeshRenderer)>();

        foreach (var r in p.GetComponentsInChildren<MeshRenderer>(true))
        {
            if (r == null || !r.enabled) continue;
            var mf = r.GetComponent<MeshFilter>();
            if (mf == null || mf.sharedMesh == null) continue;

            var go = new GameObject(r.name);
            go.layer = 0; // обычный слой, чтобы его видела основная камера
            go.transform.SetParent(root.transform, false);
            go.transform.localPosition = src.InverseTransformPoint(r.transform.position);
            go.transform.localRotation = inv * r.transform.rotation;
            var ls = r.transform.lossyScale; var ps = src.lossyScale;
            go.transform.localScale = new Vector3(ls.x / ps.x, ls.y / ps.y, ls.z / ps.z);

            go.AddComponent<MeshFilter>().sharedMesh = mf.sharedMesh;
            var nr = go.AddComponent<MeshRenderer>();
            nr.sharedMaterials = r.sharedMaterials;
            r.GetPropertyBlock(mpb);           // цвета жидкости, слои, этикетка — через property block
            nr.SetPropertyBlock(mpb);
            nr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
            pairs.Add((r, nr));
            copied++;
        }
        if (copied == 0) Plugin.L.LogWarning($"SHADOW: у зелья {p.Uid} не нашлось мешей для копии");
        Pairs[root.Pointer] = pairs;
        root.SetActive(false);
        return root;
    }
}
