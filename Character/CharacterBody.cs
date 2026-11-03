// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Sons.Wearable.Clothing;
using Sons.Wearable.Race;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace Modstoke.Client;

// One player's custom body on the game's own skeleton. The native head and arms stay
// loaded underneath with their renderers off, so the game's race sync, expressions and
// clothing all keep working and the body can be removed at any time.
internal sealed class CharacterBody : IDisposable
{
    private const int PlayerLayer = 18;
    private const int Head = 1, Torso = 2, Legs = 4, Hands = 8, Feet = 16, Overcoat = 32;
    private static readonly int BaseColor = Shader.PropertyToID("_BaseColor");
    private static readonly int BaseColorMap = Shader.PropertyToID("_BaseColorMap");
    private static readonly int NormalMap = Shader.PropertyToID("_NormalMap");
    private static readonly int MaskMap = Shader.PropertyToID("_MaskMap");
    private static readonly int ThicknessMap = Shader.PropertyToID("_ThicknessMap");

    private readonly PlayerRaceSystem _system;
    private readonly bool _local;
    private readonly List<(BodyPart Part, SkinnedMeshRenderer Renderer)> _parts = new();
    private readonly List<GameObject> _extras = new();
    private readonly Dictionary<BodyMaterial, Material> _materials = new();
    private readonly List<Renderer> _hidden = new();
    private readonly List<Renderer> _hiddenClothing = new();
    private Renderer? _headRenderer;
    private GameObject? _root;
    private IntPtr _hiddenHead;
    private IntPtr _hiddenArms;
    private readonly HashSet<string> _created = new(StringComparer.Ordinal);
    private int _covered = -1;
    private bool? _firstPerson;
    private bool _nude;

    internal CharacterBody(PlayerRaceSystem system, bool local)
    {
        _system = system;
        _local = local;
    }

    internal BodyModel? Model { get; private set; }
    internal Action<int, bool>? Coverage { get; set; }
    internal bool AdultView { get; private set; }
    internal CharacterAppearance? Appearance { get; private set; }
    internal bool Live => _system != null && _system && _root != null && _root;

    // SwitchToFirstPerson and SwitchToThirdPerson keep writing the shadow mode of the hidden head.
    internal bool NativeFirstPerson => _headRenderer != null && _headRenderer
        && _headRenderer.shadowCastingMode == ShadowCastingMode.ShadowsOnly;

    // adultView: this viewer installed the adult add-on and confirmed their age. The wearer's
    // choice of no underwear only takes effect for such viewers.
    internal bool TryAttach(BodyLibrary library, CharacterAppearance appearance, int lod, bool adultView, out string status)
    {
        Detach();
        try
        {
            if (!library.TryGet(appearance.Sex, lod, out var model))
            {
                status = library.Status;
                return false;
            }
            var head = _system._head;
            var arms = _system._arms;
            var animation = _system._animationRoot;
            if (head == null || !head || arms == null || !arms || animation == null || !animation)
            {
                status = "Waiting for the player model to load.";
                return false;
            }
            var hips = Find(animation, "Hips");
            if (hips == null)
            {
                status = "The player skeleton has no Hips bone.";
                return false;
            }
            var rig = new Dictionary<string, Transform>(StringComparer.Ordinal);
            Index(hips, rig);
            CreateMaterials(library, appearance.Sex, head, arms, adultView);
            _root = new GameObject("Modstoke Character");
            _root.layer = PlayerLayer;
            _root.transform.SetParent(animation.parent, false);
            AddParts(model, rig, hips);
            // A missing or broken adult add-on leaves the covered body in place.
            if (adultView && library.TryGet(appearance.Sex, lod, true, out var adult))
            {
                _materials[BodyMaterial.AdultSkin] = Skin(Clone(_materials[BodyMaterial.SkinBody], library, appearance.Sex, "adult"));
                AddParts(adult, rig, hips);
                AdultView = true;
            }
            Model = model;
            Apply(appearance);
            status = "Character body attached.";
            return true;
        }
        catch (Exception error)
        {
            Detach();
            status = "The character body could not be attached: " + error.Message;
            return false;
        }
    }

    private void AddParts(BodyModel model, Dictionary<string, Transform> rig, Transform hips)
    {
        foreach (var extra in model.Extras)
        {
            if (_created.Contains(extra.Name)) continue;
            if (rig.ContainsKey(extra.Name))
                throw new InvalidOperationException("Extra bone " + extra.Name + " already exists on the rig.");
            if (!rig.TryGetValue(extra.Parent, out var parent))
                throw new InvalidOperationException("Missing parent bone " + extra.Parent + ".");
            var bone = new GameObject(extra.Name);
            bone.transform.SetParent(parent, false);
            bone.transform.localPosition = extra.Position;
            bone.transform.localRotation = extra.Rotation;
            _extras.Add(bone);
            _created.Add(extra.Name);
            rig[extra.Name] = bone.transform;
        }
        var bones = new Transform[model.Bones.Count];
        for (var i = 0; i < bones.Length; i++)
            if (!rig.TryGetValue(model.Bones[i].Name, out bones[i]!))
                throw new InvalidOperationException("Missing bone " + model.Bones[i].Name + ".");
        var boneArray = new Il2CppReferenceArray<Transform>(bones);
        foreach (var part in model.Parts)
        {
            var child = new GameObject(part.Region.ToString());
            child.layer = PlayerLayer;
            child.transform.SetParent(_root!.transform, false);
            var renderer = child.AddComponent<SkinnedMeshRenderer>();
            renderer.sharedMesh = part.Mesh;
            renderer.bones = boneArray;
            renderer.rootBone = hips;
            renderer.updateWhenOffscreen = _local;
            // Runtime meshes keep model-space bounds, but the renderer reads them in Hips space,
            // and the game scales Hips by 0.4: six units here cover 2.4 m around the hips.
            renderer.localBounds = new Bounds(Vector3.zero, new Vector3(6, 6, 6));
            _parts.Add((part, renderer));
        }
    }

    // Underwear faces show bare skin, and adult parts appear, only for an adult view of a
    // player who chose no underwear.
    private void AssignMaterials()
    {
        foreach (var (part, renderer) in _parts)
        {
            var slots = new Material[part.Materials.Length];
            for (var i = 0; i < slots.Length; i++)
            {
                var kind = part.Materials[i];
                slots[i] = part.Region == BodyRegion.Adult && kind == BodyMaterial.SkinBody ? _materials[BodyMaterial.AdultSkin]
                    : _nude && kind == BodyMaterial.Underwear ? _materials[BodyMaterial.SkinBody]
                    : _materials[kind];
            }
            renderer.sharedMaterials = slots;
        }
    }

    internal void Apply(CharacterAppearance appearance)
    {
        Appearance = appearance;
        var nude = AdultView && appearance.NoUnderwear;
        if (nude != _nude || _covered < 0)
        {
            _nude = nude;
            _covered = -1;
            AssignMaterials();
        }
        if (_materials.TryGetValue(BodyMaterial.AdultSkin, out var adultSkin) && adultSkin && adultSkin.HasProperty(BaseColor))
            adultSkin.SetColor(BaseColor, appearance.Skin);
        Tint(BodyMaterial.SkinBody, appearance.Skin);
        Tint(BodyMaterial.SkinHead, appearance.Skin);
        Tint(BodyMaterial.Eye, appearance.Eyes);
        Tint(BodyMaterial.Hair, appearance.HairColor);
        Tint(BodyMaterial.Underwear, appearance.Underwear);
        foreach (var (_, renderer) in _parts)
        {
            var mesh = renderer.sharedMesh;
            foreach (var name in appearance.ShapeNames)
            {
                var index = mesh.GetBlendShapeIndex(name);
                if (index >= 0) renderer.SetBlendShapeWeight(index, Mathf.Clamp01(appearance.Shape(name)) * 100);
            }
        }
    }

    // Called every frame the player exists: the game replaces the head and arms on every
    // race change and clothing changes at any time.
    internal void Refresh(PlayerClothingSystem? clothing, bool preview)
    {
        if (!Live) return;
        HideNative();
        var firstPerson = _local && !preview && NativeFirstPerson;
        if (preview && _local) HideClothing(clothing);
        else RestoreClothing();
        var covered = preview && _local ? 0 : Covered(clothing);
        if (covered == _covered && firstPerson == _firstPerson) return;
        if (_local && covered != _covered) Coverage?.Invoke(covered, preview);
        _covered = covered;
        _firstPerson = firstPerson;
        foreach (var (part, renderer) in _parts)
        {
            renderer.enabled = Visible(part.Region, covered) && (part.Region != BodyRegion.Adult || _nude);
            // In first person only the forearms and hands are seen; the rest still casts shadows.
            renderer.shadowCastingMode = firstPerson && part.Region is not (BodyRegion.Forearms or BodyRegion.Hands)
                ? ShadowCastingMode.ShadowsOnly : ShadowCastingMode.On;
        }
    }

    private static bool Visible(BodyRegion region, int covered) => region switch
    {
        BodyRegion.Torso or BodyRegion.UpperArms or BodyRegion.Forearms => (covered & (Torso | Overcoat)) == 0,
        BodyRegion.Hips or BodyRegion.UpperLegs or BodyRegion.LowerLegs or BodyRegion.Underwear => (covered & Legs) == 0,
        BodyRegion.Feet => (covered & Feet) == 0,
        BodyRegion.Hands => (covered & Hands) == 0,
        BodyRegion.Hair => (covered & Head) == 0,
        BodyRegion.Adult => (covered & Legs) == 0,
        _ => true
    };

    // The game keeps a hookup for every slot, so only hookups with a loaded garment count;
    // the worn item IDs also cover a garment whose model is still loading.
    private static int Covered(PlayerClothingSystem? clothing)
    {
        if (clothing == null || !clothing) return 0;
        var covered = 0;
        var hookups = clothing._clothingHookupData;
        if (hookups != null)
            foreach (var hookup in hookups)
                if (hookup != null && hookup.Instance != null && hookup.Instance) covered |= (int)hookup.Slot;
        var worn = clothing.GetCurrentClothingIds();
        if (worn != null)
            foreach (var id in worn)
            {
                var piece = clothing.GetClothingPiece(id);
                if (piece != null && piece) covered |= (int)piece.Slot;
            }
        return covered;
    }

    private void HideClothing(PlayerClothingSystem? clothing)
    {
        if (clothing == null || !clothing || clothing._clothingHookupData == null) return;
        foreach (var hookup in clothing._clothingHookupData)
        {
            if (hookup == null || hookup.Instance == null || !hookup.Instance) continue;
            foreach (var renderer in hookup.Instance.GetComponentsInChildren<Renderer>(true))
            {
                if (!renderer.enabled) continue;
                renderer.enabled = false;
                _hiddenClothing.Add(renderer);
            }
        }
    }

    private void RestoreClothing()
    {
        if (_hiddenClothing.Count == 0) return;
        foreach (var renderer in _hiddenClothing)
            if (renderer != null && renderer) renderer.enabled = true;
        _hiddenClothing.Clear();
    }

    private void HideNative()
    {
        var head = _system._head;
        var arms = _system._arms;
        var headPointer = head != null && head ? head.Pointer : IntPtr.Zero;
        var armsPointer = arms != null && arms ? arms.Pointer : IntPtr.Zero;
        if (headPointer == _hiddenHead && armsPointer == _hiddenArms) return;
        RestoreNative();
        foreach (var owner in new[] { head, arms })
        {
            if (owner == null || !owner) continue;
            foreach (var renderer in owner.GetComponentsInChildren<Renderer>(true))
            {
                if (!renderer.enabled) continue;
                if (ReferenceEquals(owner, head)) _headRenderer ??= renderer;
                renderer.enabled = false;
                _hidden.Add(renderer);
            }
        }
        _hiddenHead = headPointer;
        _hiddenArms = armsPointer;
    }

    private void RestoreNative()
    {
        foreach (var renderer in _hidden)
            if (renderer != null && renderer) renderer.enabled = true;
        _hidden.Clear();
        _headRenderer = null;
        _hiddenHead = _hiddenArms = IntPtr.Zero;
    }

    // Clone the game's own materials so skin, eyes and hair keep its shaders, subsurface
    // profiles and wetness, then swap in the package textures.
    private void CreateMaterials(BodyLibrary library, CharacterSex sex, GameObject head, GameObject arms, bool adultView)
    {
        Material? skin = null, eye = null, lens = null, teeth = null, brows = null, hair = null;
        foreach (var renderer in arms.GetComponentsInChildren<Renderer>(true))
            foreach (var material in renderer.sharedMaterials)
                if (material != null && material && material.shader != null
                    && material.shader.name.Contains("Skin", StringComparison.Ordinal)) skin ??= material;
        foreach (var renderer in head.GetComponentsInChildren<Renderer>(true))
            foreach (var material in renderer.sharedMaterials)
            {
                if (material == null || !material) continue;
                var name = material.name;
                var shader = material.shader != null ? material.shader.name : "";
                if (shader.Contains("Skin", StringComparison.Ordinal)) skin ??= material;
                else if (name.StartsWith("EyeLensAndTear", StringComparison.Ordinal)) lens ??= material;
                else if (name.StartsWith("Eye", StringComparison.Ordinal) && !name.StartsWith("EyeCaruncle", StringComparison.Ordinal)) eye ??= material;
                else if (name.StartsWith("Teeth", StringComparison.Ordinal)) teeth ??= material;
                else if (name.StartsWith("BrowLashes", StringComparison.Ordinal)) brows ??= material;
                else if (shader.Contains("Hair", StringComparison.Ordinal)) hair ??= material;
            }
        if (skin == null || eye == null || lens == null || teeth == null || brows == null)
            throw new InvalidOperationException("The player head has no skin, eye or teeth material to copy.");
        _materials[BodyMaterial.SkinBody] = Skin(Clone(skin, library, sex, "body", adultView));
        _materials[BodyMaterial.SkinHead] = Skin(Clone(skin, library, sex, "head"));
        _materials[BodyMaterial.Eye] = Clone(eye, library, sex, "eye");
        _materials[BodyMaterial.EyeLens] = Clone(lens, null, sex, "");
        _materials[BodyMaterial.Teeth] = Clone(teeth, library, sex, "teeth");
        _materials[BodyMaterial.Brows] = Clone(brows, library, sex, "lashes");
        _materials[BodyMaterial.Hair] = Clone(hair ?? brows, library, sex, "hair");
        _materials[BodyMaterial.Underwear] = Fabric(Clone(teeth, library, sex, "underwear"));
    }

    // The native blood and reveal masks are laid out for the source mesh's UVs.
    private static Material Skin(Material material)
    {
        foreach (var name in new[] { "_BloodMask", "_BloodRevealMask" })
            if (material.HasProperty(name)) material.SetTexture(name, Texture2D.blackTexture);
        return material;
    }

    // Underwear starts from the teeth material, which carries teeth subsurface scattering.
    private static Material Fabric(Material material)
    {
        foreach (var keyword in new[] { "_MATERIAL_FEATURE_SUBSURFACE_SCATTERING", "_MATERIAL_FEATURE_TRANSMISSION",
            "_SUBSURFACE_MASK_MAP", "_TRANSMISSION_MASK_MAP", "_THICKNESSMAP" })
            material.DisableKeyword(keyword);
        if (material.HasProperty("_MaterialID")) material.SetFloat("_MaterialID", 1);
        foreach (var name in new[] { "_SubsurfaceMaskMap", "_TransmissionMaskMap", "_ThicknessMap" })
            if (material.HasProperty(name)) material.SetTexture(name, null);
        HDMaterial.ValidateMaterial(material);
        return material;
    }

    private static Material Clone(Material source, BodyLibrary? library, CharacterSex sex, string part, bool adultView = false)
    {
        var material = new Material(source) { name = "Modstoke " + (part.Length == 0 ? source.name : part) };
        if (library == null) return material;
        Set(material, BaseColorMap, library.Texture(sex, part, "albedo", adultView));
        Set(material, NormalMap, library.Texture(sex, part, "normal", adultView));
        Set(material, MaskMap, library.Texture(sex, part, "mask", adultView));
        Set(material, ThicknessMap, library.Texture(sex, part, "thickness", adultView));
        if (material.HasProperty(BaseColor)) material.SetColor(BaseColor, Color.white);
        return material;
    }

    private static void Set(Material material, int property, Texture2D? texture)
    {
        if (texture != null && material.HasProperty(property)) material.SetTexture(property, texture);
    }

    private void Tint(BodyMaterial kind, Color32 color)
    {
        if (_materials.TryGetValue(kind, out var material) && material && material.HasProperty(BaseColor))
            material.SetColor(BaseColor, color);
    }

    private static Transform? Find(Transform parent, string name)
    {
        for (var i = 0; i < parent.childCount; i++)
        {
            var child = parent.GetChild(i);
            if (child.name == name) return child;
            var found = Find(child, name);
            if (found != null) return found;
        }
        return null;
    }

    private static void Index(Transform bone, Dictionary<string, Transform> rig)
    {
        rig.TryAdd(bone.name, bone);
        for (var i = 0; i < bone.childCount; i++) Index(bone.GetChild(i), rig);
    }

    private void Detach()
    {
        RestoreNative();
        RestoreClothing();
        foreach (var (_, renderer) in _parts)
            if (renderer != null && renderer) renderer.sharedMaterials = new Il2CppReferenceArray<Material>(0);
        _parts.Clear();
        if (_root != null && _root) UnityEngine.Object.Destroy(_root);
        _root = null;
        foreach (var extra in _extras)
            if (extra != null && extra) UnityEngine.Object.Destroy(extra);
        _extras.Clear();
        foreach (var material in _materials.Values)
            if (material != null && material) UnityEngine.Object.Destroy(material);
        _materials.Clear();
        _created.Clear();
        Model = null;
        AdultView = false;
        _nude = false;
        _covered = -1;
        _firstPerson = null;
    }

    public void Dispose() => Detach();
}
