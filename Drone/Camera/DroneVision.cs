// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using Il2CppInterop.Runtime.Attributes;
using Il2CppInterop.Runtime.Injection;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace Modstoke.Client;

// The camera owner calls this object only on Unity's main thread.
internal sealed class ClientDroneVision : IDisposable
{
    private static readonly ManualLogSource Log = BepInEx.Logging.Logger.CreateLogSource("Modstoke drone vision");
    private static readonly Dictionary<IntPtr, ClientDroneVision> Owners = new();
    private static readonly FrameSettingsField[] EnabledEffects =
    {
        FrameSettingsField.CustomPass, FrameSettingsField.Postprocess,
        FrameSettingsField.ColorGrading, FrameSettingsField.Tonemapping,
        FrameSettingsField.ExposureControl, FrameSettingsField.FilmGrain,
        FrameSettingsField.Vignette
    };
    private static readonly FrameSettingsField[] DisabledEffects =
    {
        FrameSettingsField.DepthOfField, FrameSettingsField.MotionBlur,
        FrameSettingsField.ChromaticAberration, FrameSettingsField.Bloom,
        FrameSettingsField.LensDistortion, FrameSettingsField.PaniniProjection,
        FrameSettingsField.CustomPostProcess
    };
    private static Harmony? _patches;
    private static MethodInfo? _volumeUpdate, _volumePostfix;
    private static bool _hooked, _passRegistered;
    private readonly Camera _camera;
    private readonly ClientDroneThermalTargets _targets = new();
    private readonly HashSet<string> _warnedOperations = new();
    private HDAdditionalCameraData? _data;
    private HDCamera? _hdCamera;
    private FrameSettings _savedSettings;
    private FrameSettingsOverrideMask _savedMask;
    private bool _savedCustomSettings, _settingsSaved;
    private bool _active, _disposed, _registered, _passReady;
    private IntPtr _cameraPointer;
    private DroneCameraVision _requested;
    private Texture3D? _nightLut, _thermalLut;
    private Material? _heatMaterial;
    private GameObject? _passRoot;
    private CustomPassVolume? _volume;
    private ClientDroneThermalPass? _pass;
    private int _heatShaderPass, _volumeFrame = -1;
    private float _lastVisionFrameTime;

    internal DroneCameraVision Mode { get; private set; }
    internal string Status { get; private set; } = "Camera feed off.";

    internal ClientDroneVision(Camera camera) => _camera = camera;

    internal void SetMode(DroneCameraVision mode)
    {
        if (_disposed || _requested == mode) return;
        _requested = mode;
        if (_active) ConfigureMode();
    }

    internal void SetActive(bool active)
    {
        if (_disposed || _active == active) return;
        _active = active;
        if (active) ConfigureMode();
        else
        {
            Mode = DroneCameraVision.Color;
            Status = "Camera feed off.";
            RestoreColor();
        }
    }

    internal void Update()
    {
        if (_disposed || !_active || _requested == DroneCameraVision.Color) return;
        try
        {
            if (_requested == DroneCameraVision.Thermal) _targets.Refresh(_camera);
            // A stripped native callback must not leave an apparently working mode.
            if (Time.realtimeSinceStartup - _lastVisionFrameTime > 5)
                throw new InvalidOperationException("The drone camera vision callback did not run.");
        }
        catch (Exception error) { Fail(error); }
    }

    private void ConfigureMode()
    {
        Mode = DroneCameraVision.Color;
        _volumeFrame = -1;
        _warnedOperations.Clear();
        try
        {
            if (_pass != null) _pass.enabled = false;
            _targets.Clear();
            if (_requested == DroneCameraVision.Color)
            {
                RestoreColor();
                Status = "Color camera.";
                return;
            }
            if (_requested is not (DroneCameraVision.Night or DroneCameraVision.Thermal))
                throw new InvalidOperationException("The requested drone vision mode is unsupported.");
            ConfigureCamera();
            InstallVolumeHook();
            CreatePalettes();
            if (_requested == DroneCameraVision.Thermal)
            {
                CreateThermalPass();
                _targets.Refresh(_camera);
                _pass!.enabled = true;
            }
            _lastVisionFrameTime = Time.realtimeSinceStartup;
            Status = _requested == DroneCameraVision.Night ? "Starting night camera." : "Starting thermal camera.";
        }
        catch (Exception error) { Fail(error); }
    }

    private void ConfigureCamera()
    {
        _data = _camera.GetComponent<HDAdditionalCameraData>();
        if (!_data) throw new InvalidOperationException("The drone camera has no HDRP camera data.");
        if (!_settingsSaved)
        {
            // Feed reactivation can copy fresh game settings into this camera.
            _savedSettings = _data!.m_RenderingPathCustomFrameSettings;
            _savedMask = _data.renderingPathCustomFrameSettingsOverrideMask;
            _savedCustomSettings = _data.customRenderingSettings;
            _settingsSaved = true;
        }
        var settings = _savedSettings;
        var mask = _savedMask;
        foreach (var field in EnabledEffects)
        {
            settings.SetEnabled(field, true);
            mask.mask[(uint)field] = true;
        }
        foreach (var field in DisabledEffects)
        {
            settings.SetEnabled(field, false);
            mask.mask[(uint)field] = true;
        }
        _data!.m_RenderingPathCustomFrameSettings = settings;
        _data.renderingPathCustomFrameSettingsOverrideMask = mask;
        _data.customRenderingSettings = true;
    }

    private void InstallVolumeHook()
    {
        if (_registered) return;
        if (!_hooked)
        {
            _volumeUpdate = AccessTools.DeclaredMethod(typeof(HDCamera), nameof(HDCamera.UpdateVolumeAndPhysicalParameters), Type.EmptyTypes)
                ?? throw new MissingMethodException(nameof(HDCamera.UpdateVolumeAndPhysicalParameters));
            _volumePostfix = AccessTools.DeclaredMethod(typeof(ClientDroneVision), nameof(AfterVolumeUpdate))
                ?? throw new MissingMethodException(nameof(AfterVolumeUpdate));
            _patches ??= new Harmony("modstoke.client.drone.vision");
            _patches.Patch(_volumeUpdate, postfix: new HarmonyMethod(_volumePostfix) { priority = Priority.Last });
            _hooked = true;
        }
        _cameraPointer = _camera.Pointer;
        Owners.Add(_cameraPointer, this);
        _registered = true;
    }

    private static void AfterVolumeUpdate(HDCamera __instance)
    {
        ClientDroneVision? owner = null;
        try
        {
            var camera = __instance.camera;
            if (camera == null || !Owners.TryGetValue(camera.Pointer, out owner)
                || owner._disposed || !owner._active || owner._requested == DroneCameraVision.Color) return;
            owner.ApplyVolume(__instance);
        }
        catch (Exception error) { owner?.Fail(error); }
    }

    private void ApplyVolume(HDCamera camera)
    {
        _hdCamera = camera;
        var stack = camera.volumeStack;
        var exposure = stack.GetComponent<Exposure>();
        var colors = stack.GetComponent<ColorAdjustments>();
        var tonemapping = stack.GetComponent<Tonemapping>();
        var grain = stack.GetComponent<FilmGrain>();
        var vignette = stack.GetComponent<Vignette>();
        if (!exposure || !colors || !tonemapping || !grain || !vignette)
            throw new InvalidOperationException("The drone camera volume stack lacks a required vision effect.");
        var night = _requested == DroneCameraVision.Night;
        // Native HDRP has just blended this camera's stack. Its shared profiles
        // remain untouched and the next native update replaces these overrides.
        tonemapping.active = true;
        tonemapping.mode.Override(TonemappingMode.External);
        tonemapping.lutTexture.Override(night ? _nightLut! : _thermalLut!);
        tonemapping.lutContribution.Override(1);
        if (!tonemapping.ValidateLUT())
            throw new InvalidOperationException("HDRP rejected the drone camera lookup texture.");
        exposure.active = true;
        exposure.mode.Override(ExposureMode.Automatic);
        exposure.compensation.Override(night ? 3.5f : 0);
        exposure.limitMin.Override(night ? -10 : -5);
        exposure.limitMax.Override(16);
        exposure.adaptationMode.Override(AdaptationMode.Fixed);
        colors.active = true;
        colors.postExposure.Override(night ? 1.5f : 0);
        colors.contrast.Override(0);
        colors.colorFilter.Override(Color.white);
        colors.hueShift.Override(0);
        colors.saturation.Override(0);
        grain.active = true;
        grain.type.Override(FilmGrainLookup.Thin1);
        grain.intensity.Override(night ? .24f : .055f);
        grain.response.Override(.75f);
        vignette.active = true;
        vignette.mode.Override(VignetteMode.Procedural);
        vignette.color.Override(Color.black);
        vignette.center.Override(new Vector2(.5f, .5f));
        vignette.intensity.Override(night ? .26f : .12f);
        vignette.smoothness.Override(.6f);
        vignette.roundness.Override(1);
        vignette.rounded.Override(false);
        if (_volumeFrame == -1)
            Log.LogInfo("Drone " + _requested + " volume overrides applied.");
        _volumeFrame = Time.frameCount;
        if (night)
        {
            _lastVisionFrameTime = Time.realtimeSinceStartup;
            Mode = DroneCameraVision.Night;
            Status = "Night camera · amplified green image.";
        }
    }

    private void CreatePalettes()
    {
        var asset = HDRenderPipeline.currentAsset;
        if (!asset) throw new InvalidOperationException("The HDRP pipeline is unavailable.");
        var size = asset.currentPlatformRenderPipelineSettings.postProcessSettings.lutSize;
        if (_nightLut && _thermalLut && _nightLut!.width == size && _thermalLut!.width == size) return;
        ReleasePalettes();
        _nightLut = CreatePalette(size, true);
        _thermalLut = CreatePalette(size, false);
    }

    private static Texture3D CreatePalette(int size, bool night)
    {
        var texture = new Texture3D(size, size, size, TextureFormat.RGBAHalf, false)
        {
            name = night ? "Drone night palette" : "Drone thermal background palette",
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear,
            hideFlags = HideFlags.HideAndDontSave
        };
        try
        {
            var linear = new float[size];
            // HDRP samples external tonemapping LUTs in Alexa LogC EI 1000.
            for (var i = 0; i < size; i++)
                linear[i] = Mathf.Max(0, (Mathf.Pow(10, (i / (float)(size - 1) - .386036f) / .244161f) - .047996f) / 5.555556f);
            var pixels = new Il2CppStructArray<Color>(size * size * size);
            for (var blue = 0; blue < size; blue++)
            for (var green = 0; green < size; green++)
            for (var red = 0; red < size; red++)
            {
                var luminance = .2126729f * linear[red] + .7151522f * linear[green] + .072175f * linear[blue];
                var level = luminance / (1 + luminance);
                var color = night
                    ? new Color(.015f + level * .13f, .025f + level * .93f, .012f + level * .20f, 1)
                    : new Color(.03f + level * .21f, .03f + level * .21f, .03f + level * .21f, 1);
                pixels[red + size * (green + size * blue)] = color;
            }
            texture.SetPixels(pixels);
            texture.Apply(false, true);
            return texture;
        }
        catch
        {
            UnityEngine.Object.Destroy(texture);
            throw;
        }
    }

    private void CreateThermalPass()
    {
        if (_passReady) return;
        ReleaseThermalPass();
        if (!HDRenderPipeline.currentAsset.currentPlatformRenderPipelineSettings.supportCustomPass)
            throw new InvalidOperationException("The HDRP pipeline does not support custom passes.");
        var shader = Shader.Find("HDRP/Unlit");
        if (!shader || !shader.isSupported) throw new InvalidOperationException("The HDRP thermal shader is unavailable.");
        _heatMaterial = new Material(shader) { name = "Drone heat silhouettes", hideFlags = HideFlags.HideAndDontSave };
        foreach (var property in new[] { "_UnlitColor", "_EmissiveColor", "_EmissiveExposureWeight", "_ZTestDepthEqualForOpaque", "_ZWrite" })
            if (!_heatMaterial.HasProperty(property)) throw new InvalidOperationException("The HDRP thermal shader lacks " + property + ".");
        ClientDeviceScreenMaterial.SetDisplay(_heatMaterial, new Color(.95f, .95f, .95f, 1), null);
        _heatMaterial.SetFloat("_ZTestDepthEqualForOpaque", (float)CompareFunction.LessEqual);
        _heatMaterial.SetFloat("_ZWrite", 0);
        _heatMaterial.SetFloat("_StencilWriteMask", 0);
        _heatShaderPass = _heatMaterial.FindPass("ForwardOnly");
        if (_heatShaderPass < 0) throw new InvalidOperationException("The HDRP thermal forward pass is unavailable.");
        if (!_passRegistered)
        {
            ClassInjector.RegisterTypeInIl2Cpp<ClientDroneThermalPass>();
            _passRegistered = true;
        }
        _passRoot = new GameObject("Drone thermal camera pass") { hideFlags = HideFlags.HideAndDontSave };
        _passRoot.SetActive(false);
        _passRoot.transform.SetParent(_camera.transform, false);
        _volume = _passRoot.AddComponent<CustomPassVolume>();
        _volume.isGlobal = true;
        _volume.targetCamera = _camera;
        _volume.useTargetCamera = true;
        _volume.priority = 1000;
        _volume.injectionPoint = CustomPassInjectionPoint.AfterPostProcess;
        _pass = new ClientDroneThermalPass
        {
            name = "Drone heat silhouettes",
            enabled = false,
            targetColorBuffer = CustomPass.TargetBuffer.Camera,
            targetDepthBuffer = CustomPass.TargetBuffer.Camera,
            clearFlags = ClearFlag.None
        };
        _pass.Attach(this);
        _volume.customPasses.Add(_pass);
        _passRoot.SetActive(true);
        _passReady = true;
    }

    internal void DrawThermal(ClientDroneThermalPass pass, CustomPassContext context)
    {
        if (_disposed || !_active || _requested != DroneCameraVision.Thermal || _volumeFrame != Time.frameCount) return;
        try
        {
            var camera = context.hdCamera.camera;
            if (camera == null || camera.Pointer != _cameraPointer) return;
            if (_heatMaterial == null || context.cameraDepthBuffer == null)
                throw new InvalidOperationException("The drone thermal render resources are unavailable.");
            // Reuse the camera depth without writing it; terrain and buildings
            // continue to occlude warm bodies in the post-processed image.
            pass.SetCameraRenderTarget(context.cmd, true);
            _targets.Draw(context.cmd, _heatMaterial, _heatShaderPass);
            _lastVisionFrameTime = Time.realtimeSinceStartup;
            if (Mode != DroneCameraVision.Thermal)
                Log.LogInfo("Drone thermal draw commands submitted.");
            Mode = DroneCameraVision.Thermal;
            Status = "Thermal camera · white-hot living targets.";
        }
        catch (Exception error) { Fail(error); }
    }

    private void Fail(Exception error)
    {
        var failed = _requested;
        _requested = DroneCameraVision.Color;
        Mode = DroneCameraVision.Color;
        var cause = error.GetBaseException();
        var reason = (cause.GetType().Name + ": " + cause.Message).Replace('\r', ' ').Replace('\n', ' ');
        if (reason.Length > 140) reason = reason[..137] + "...";
        Status = failed + " camera unavailable; using color. " + reason;
        Warn(failed + " vision unavailable; using color", error);
        RestoreColor();
    }

    private void RestoreColor()
    {
        try { if (_pass != null) _pass.enabled = false; }
        catch (Exception error) { Warn("thermal pass could not be disabled", error); }
        _targets.Clear();
        try
        {
            if (_settingsSaved && _data)
            {
                _data!.m_RenderingPathCustomFrameSettings = _savedSettings;
                _data.renderingPathCustomFrameSettingsOverrideMask = _savedMask;
                _data.customRenderingSettings = _savedCustomSettings;
            }
            _settingsSaved = false;
            // The postfix is inactive for Color, feed-off and disposal. Reblend
            // now so a retained HDCamera cannot keep a stale LUT on reactivation.
            if (_hdCamera != null && _camera) _hdCamera.UpdateVolumeAndPhysicalParameters();
        }
        catch (Exception error) { Warn("camera color settings could not be restored", error); }
    }

    private void Warn(string operation, Exception error)
    {
        if (!_warnedOperations.Add(operation)) return;
        Log.LogWarning("Drone " + operation + ": " + error);
    }

    private void ReleasePalettes()
    {
        if (_nightLut) UnityEngine.Object.Destroy(_nightLut);
        if (_thermalLut) UnityEngine.Object.Destroy(_thermalLut);
        _nightLut = _thermalLut = null;
    }

    private void ReleaseThermalPass()
    {
        _pass?.Attach(null);
        try
        {
            if (_passRoot) _passRoot!.SetActive(false);
            if (_volume) _volume!.customPasses.Clear();
        }
        catch (Exception error) { Warn("thermal pass cleanup failed", error); }
        try { if (_passRoot) UnityEngine.Object.Destroy(_passRoot); }
        catch (Exception error) { Warn("thermal volume cleanup failed", error); }
        try { if (_heatMaterial) UnityEngine.Object.Destroy(_heatMaterial); }
        catch (Exception error) { Warn("thermal material cleanup failed", error); }
        _passReady = false;
        _pass = null;
        _volume = null;
        _passRoot = null;
        _heatMaterial = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _active = false;
        Mode = DroneCameraVision.Color;
        Status = "Camera feed off.";
        RestoreColor();
        if (_registered) Owners.Remove(_cameraPointer);
        _registered = false;
        try
        {
            if (Owners.Count == 0 && _volumeUpdate != null && _volumePostfix != null)
            {
                _patches?.Unpatch(_volumeUpdate, _volumePostfix);
                _hooked = false;
            }
        }
        catch (Exception error) { Warn("vision callback could not be removed", error); }
        ReleaseThermalPass();
        try { ReleasePalettes(); }
        catch (Exception error) { Warn("vision palette cleanup failed", error); }
        _hdCamera = null;
    }
}

public sealed class ClientDroneThermalPass : CustomPass
{
    private ClientDroneVision? _owner;

    public ClientDroneThermalPass(IntPtr pointer) : base(pointer) { }

    public ClientDroneThermalPass() : base(ClassInjector.DerivedConstructorPointer<ClientDroneThermalPass>()) =>
        ClassInjector.DerivedConstructorBody(this);

    [HideFromIl2Cpp]
    internal void Attach(ClientDroneVision? owner) => _owner = owner;

    public override void Execute(CustomPassContext context) => _owner?.DrawThermal(this, context);
}
