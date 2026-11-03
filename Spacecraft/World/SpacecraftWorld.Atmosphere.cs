// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using HdFog = UnityEngine.Rendering.HighDefinition.Fog;

namespace Modstoke.Client;

internal sealed partial class ClientSpacecraftWorld
{
    private GameObject? _fogRoot;
    private Volume? _fogVolume;
    private VolumeProfile? _fogProfile;
    private HdFog? _fog;
    private GameObject? _spaceAtmosphere;
    private Volume? _spaceVolume;
    private VolumeProfile? _spaceProfile;
    private HdFog? _spaceFog;
    private VisualEnvironment? _spaceEnvironment;
    private VolumetricClouds? _spaceClouds;
    private CloudLayer? _spaceCloudLayer;

    private void BuildSpaceAtmosphere()
    {
        _spaceAtmosphere = Child("Space camera atmosphere", _root!.transform);
        _spaceAtmosphere.SetActive(false);
        var box = _spaceAtmosphere.AddComponent<BoxCollider>();
        box.isTrigger = true;
        box.size = Vector3.one * 40;
        _spaceProfile = ScriptableObject.CreateInstance<VolumeProfile>();
        _spaceProfile.name = "Owned vacuum environment";
        _spaceFog = _spaceProfile.Add<HdFog>(false);
        _spaceFog.active = true;
        _spaceFog.enabled.Override(false);
        _spaceFog.enableVolumetricFog.Override(false);
        _spaceEnvironment = _spaceProfile.Add<VisualEnvironment>(false);
        _spaceEnvironment.active = true;
        // Zero is HDRP's None selection. The camera-local shell supplies the sky.
        _spaceEnvironment.skyType.Override(0);
        _spaceEnvironment.cloudType.Override(0);
        _spaceClouds = _spaceProfile.Add<VolumetricClouds>(false);
        _spaceClouds.active = true;
        _spaceClouds.enable.Override(false);
        _spaceCloudLayer = _spaceProfile.Add<CloudLayer>(false);
        _spaceCloudLayer.active = true;
        _spaceCloudLayer.opacity.Override(0);
        _spaceVolume = _spaceAtmosphere.AddComponent<Volume>();
        _spaceVolume.isGlobal = false;
        _spaceVolume.priority = 2000;
        _spaceVolume.weight = 1;
        _spaceVolume.blendDistance = 0;
        _spaceVolume.sharedProfile = _spaceProfile;
    }

    private void UpdateSpaceAtmosphere(SpacecraftRegion region, Camera? camera)
    {
        if (_spaceAtmosphere == null) return;
        if (region is not (SpacecraftRegion.Space or SpacecraftRegion.Moon))
        {
            if (_spaceAtmosphere.activeSelf) _spaceAtmosphere.SetActive(false);
            return;
        }
        var enabled = TryVolumeAnchor(_spaceAtmosphere, camera, out var anchor);
        if (enabled) _spaceAtmosphere.transform.position = anchor;
        if (_spaceAtmosphere.activeSelf != enabled) _spaceAtmosphere.SetActive(enabled);
    }

    private static bool TryVolumeAnchor(GameObject volume, Camera? camera, out Vector3 position)
    {
        position = default;
        if (camera == null) return false;
        var hd = camera.GetComponent<HDAdditionalCameraData>();
        var mask = hd != null ? hd.volumeLayerMask.value : 1;
        if (mask == 0) return false;
        for (var layer = 0; layer < 32; layer++)
        {
            if ((mask & (1 << layer)) == 0) continue;
            volume.layer = layer;
            break;
        }
        // Native cameras can evaluate volumes at a player anchor instead of the
        // camera position. Follow that anchor without replacing its reference.
        var anchor = hd != null ? hd.volumeAnchorOverride : null;
        position = anchor != null ? anchor.position : camera.transform.position;
        return true;
    }

    private void ReleaseSpaceAtmosphere()
    {
        if (_spaceAtmosphere != null) _spaceAtmosphere.SetActive(false);
        if (_spaceVolume != null) _spaceVolume.sharedProfile = null;
        if (_spaceProfile != null) _spaceProfile.components.Clear();
        if (_spaceFog != null) UnityEngine.Object.Destroy(_spaceFog);
        if (_spaceEnvironment != null) UnityEngine.Object.Destroy(_spaceEnvironment);
        if (_spaceClouds != null) UnityEngine.Object.Destroy(_spaceClouds);
        if (_spaceCloudLayer != null) UnityEngine.Object.Destroy(_spaceCloudLayer);
        if (_spaceProfile != null) UnityEngine.Object.Destroy(_spaceProfile);
        _spaceFog = null;
        _spaceEnvironment = null;
        _spaceClouds = null;
        _spaceCloudLayer = null;
        _spaceProfile = null;
        _spaceVolume = null;
        _spaceAtmosphere = null;
    }

    private void BuildFog()
    {
        _fogRoot = Child("Local wetland haze", _root!.transform);
        _fogRoot.SetActive(false);
        _fogRoot.transform.localPosition = new Vector3(0, 100, 0);
        var box = _fogRoot.AddComponent<BoxCollider>();
        box.isTrigger = true;
        box.size = new Vector3(480, 440, 480);
        _fogProfile = ScriptableObject.CreateInstance<VolumeProfile>();
        _fogProfile.name = "Owned expedition haze";
        _fog = _fogProfile.Add<HdFog>(false);
        _fog.active = true;
        _fog.enabled.Override(true);
        _fog.colorMode.Override(FogColorMode.ConstantColor);
        _fog.color.Override(new Color(.20f, .29f, .28f));
        _fog.meanFreePath.Override(145f);
        _fog.baseHeight.Override(SpacecraftLayout.WorldOrigin.y - 4);
        _fog.maximumHeight.Override(SpacecraftLayout.WorldOrigin.y + 115);
        _fog.maxFogDistance.Override(650f);
        _fog.enableVolumetricFog.Override(false);
        _fogVolume = _fogRoot.AddComponent<Volume>();
        _fogVolume.isGlobal = false;
        _fogVolume.priority = 1000;
        _fogVolume.weight = 1;
        _fogVolume.blendDistance = 20;
        _fogVolume.sharedProfile = _fogProfile;
    }

    private void UpdateFog(SpacecraftRegion region, Camera? camera)
    {
        if (_fogRoot == null) return;
        var enabled = region == SpacecraftRegion.TestWorld && TryVolumeAnchor(_fogRoot, camera, out _);
        if (_fogRoot.activeSelf != enabled) _fogRoot.SetActive(enabled);
    }

    private void ReleaseFog()
    {
        if (_fogRoot != null) _fogRoot.SetActive(false);
        if (_fogVolume != null) _fogVolume.sharedProfile = null;
        if (_fogProfile != null) _fogProfile.components.Clear();
        if (_fog != null) UnityEngine.Object.Destroy(_fog);
        if (_fogProfile != null) UnityEngine.Object.Destroy(_fogProfile);
        _fog = null;
        _fogProfile = null;
        _fogVolume = null;
        _fogRoot = null;
    }
}
