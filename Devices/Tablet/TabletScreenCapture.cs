// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace Modstoke.Client;

// Owns only an auxiliary camera and its buffers. The live UI, its camera binding,
// layers and input components remain owned by the terminal.
internal sealed class ClientTabletScreenCapture : IDisposable
{
    private const int Width = 512;
    private const int Height = 320;
    private const int MaximumFrameBytes = 24576;
    private const float Interval = .5f;
    private const float CameraDistance = .02f;
    private const float PlaneTolerance = .0005f;
    private static readonly int[] JpegQualities = { 70, 50, 30, 15 };
    private static readonly FrameSettingsField[] DisabledEffects =
    {
        FrameSettingsField.OpaqueObjects, FrameSettingsField.Postprocess,
        FrameSettingsField.CustomPostProcess, FrameSettingsField.CustomPass,
        FrameSettingsField.ExposureControl, FrameSettingsField.AtmosphericScattering,
        FrameSettingsField.Volumetrics, FrameSettingsField.VolumetricClouds,
        FrameSettingsField.ShadowMaps, FrameSettingsField.ContactShadows,
        FrameSettingsField.ScreenSpaceShadows, FrameSettingsField.MotionVectors,
        FrameSettingsField.ObjectMotionVectors, FrameSettingsField.RayTracing,
        FrameSettingsField.ReflectionProbe, FrameSettingsField.PlanarProbe,
        FrameSettingsField.Water, FrameSettingsField.Decals,
        FrameSettingsField.Refraction, FrameSettingsField.Distortion
    };

    private readonly GameObject _canvas;
    private readonly RectTransform _rect;
    private readonly Il2CppSystem.Action<ScriptableRenderContext, Camera> _afterCamera;
    private GameObject? _cameraObject;
    private Camera? _camera;
    private RenderTexture? _target;
    private Texture2D? _pixels;
    private bool _subscriptionAttempted;
    private bool _disposed;
    private bool _pending;
    private bool _rendered;
    private int _scheduledFrame;
    private int _renderedFrame;
    private int _jpegQualityIndex;
    private int _smallFrames;
    private float _nextCapture;
    private byte[]? _frame;
    private Exception? _renderFailure;

    internal ClientTabletScreenCapture(GameObject canvas)
    {
        _canvas = canvas;
        _rect = canvas.GetComponent<RectTransform>()
            ?? throw new InvalidOperationException("The tablet capture has no screen rectangle.");
        _afterCamera = (Action<ScriptableRenderContext, Camera>)AfterCamera;
        try
        {
            var ui = canvas.GetComponent<Canvas>();
            if (ui == null || ui.renderMode != RenderMode.WorldSpace)
                throw new InvalidOperationException("The tablet capture requires its live world-space canvas.");

            _cameraObject = new GameObject("Tablet screen sharing camera");
            _cameraObject.SetActive(false);
            // Parenting preserves the canvas pose through its later native render callbacks.
            _cameraObject.transform.SetParent(_rect, false);
            _camera = _cameraObject.AddComponent<Camera>();
            _camera.enabled = false;
            _camera.orthographic = true;
            _camera.nearClipPlane = CameraDistance - PlaneTolerance;
            _camera.farClipPlane = CameraDistance + PlaneTolerance;
            _camera.cullingMask = 1 << canvas.layer;
            _camera.clearFlags = CameraClearFlags.SolidColor;
            _camera.backgroundColor = Color.black;
            _camera.allowHDR = false;
            _camera.allowMSAA = false;
            _camera.allowDynamicResolution = false;
            _camera.useOcclusionCulling = false;
            _camera.stereoTargetEye = StereoTargetEyeMask.None;
            // The drone's camera renders at -20, so its live texture is ready first.
            _camera.depth = -10;
            ConfigurePipeline(_cameraObject.GetComponent<HDAdditionalCameraData>()
                ?? _cameraObject.AddComponent<HDAdditionalCameraData>());

            _target = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32,
                RenderTextureReadWrite.sRGB)
            {
                name = "Shared tablet screen",
                antiAliasing = 1,
                useMipMap = false,
                autoGenerateMips = false
            };
            if (!_target.Create())
                throw new InvalidOperationException("The tablet capture render target could not be created.");
            _camera.targetTexture = _target;
            _pixels = new Texture2D(Width, Height, TextureFormat.RGB24, false, false)
                { name = "Shared tablet screen pixels" };

            FrameCanvas();
            _subscriptionAttempted = true;
            RenderPipelineManager.add_endCameraRendering(_afterCamera);
            _cameraObject.SetActive(true);
        }
        catch
        {
            try { Dispose(); }
            catch { }
            throw;
        }
    }

    internal bool Matches(GameObject canvas) => !_disposed && _canvas && _canvas == canvas;
    internal float NextCaptureAt => _nextCapture;

    internal void Update()
    {
        if (_disposed) return;
        if (_renderFailure != null) throw new InvalidOperationException("Tablet capture rendering failed.", _renderFailure);
        if (!_canvas || !_rect || !_camera || !_target || !_pixels)
            throw new InvalidOperationException("The tablet capture lost its screen or render resources.");

        if (_pending)
        {
            // The callback records actual HDRP completion; an elapsed frame alone
            // is not proof that the render target contains a new image.
            if (Time.frameCount <= _scheduledFrame) return;
            _camera!.enabled = false;
            if (!_rendered)
                throw new InvalidOperationException("The tablet camera did not report render completion.");
            if (Time.frameCount <= _renderedFrame) return;
            _pending = false;
            _rendered = false;
            _frame = ReadFrame();
        }

        var now = Time.unscaledTime;
        if (now < _nextCapture) return;
        FrameCanvas();
        _scheduledFrame = Time.frameCount;
        _nextCapture = now + Interval;
        _pending = true;
        _camera!.enabled = true;
    }

    internal byte[]? TakeFrame()
    {
        var frame = _frame;
        _frame = null;
        return frame;
    }

    private void FrameCanvas()
    {
        var bounds = _rect.rect;
        // Transform vectors retain the world dimensions without subtracting two
        // distant world positions. The current tablet mount has positive scale.
        var x = _rect.TransformVector(Vector3.right).magnitude;
        var y = _rect.TransformVector(Vector3.up).magnitude;
        var z = _rect.TransformVector(Vector3.forward).magnitude;
        var width = bounds.width * x;
        var height = bounds.height * y;
        if (!float.IsFinite(width) || !float.IsFinite(height) || !float.IsFinite(z)
            || width <= 0 || height <= 0 || z <= 0)
            throw new InvalidOperationException("The tablet capture rectangle has invalid world dimensions.");
        var transform = _cameraObject!.transform;
        transform.localPosition = new Vector3(bounds.center.x, bounds.center.y, -CameraDistance / z);
        transform.localRotation = Quaternion.identity;
        _camera!.orthographicSize = height * .5f;
        _camera.aspect = width / height;
    }

    private static void ConfigurePipeline(HDAdditionalCameraData data)
    {
        data.clearColorMode = HDAdditionalCameraData.ClearColorMode.Color;
        data.backgroundColorHDR = Color.black;
        data.clearDepth = true;
        data.antialiasing = HDAdditionalCameraData.AntialiasingMode.None;
        data.volumeLayerMask = (LayerMask)0;
        data.allowDynamicResolution = false;
        data.hasPersistentHistory = false;
        var settings = FrameSettings.NewDefaultCamera();
        var mask = new FrameSettingsOverrideMask();
        foreach (var field in DisabledEffects)
        {
            settings.SetEnabled(field, false);
            mask.mask[(uint)field] = true;
        }
        settings.SetEnabled(FrameSettingsField.TransparentObjects, true);
        mask.mask[(uint)FrameSettingsField.TransparentObjects] = true;
        data.m_RenderingPathCustomFrameSettings = settings;
        data.renderingPathCustomFrameSettingsOverrideMask = mask;
        data.customRenderingSettings = true;
    }

    private void AfterCamera(ScriptableRenderContext context, Camera camera)
    {
        if (_disposed || !_pending) return;
        try
        {
            if (!_camera || camera != _camera) return;
            _camera!.enabled = false;
            _renderedFrame = Time.frameCount;
            _rendered = true;
        }
        catch (Exception error)
        {
            _renderFailure = error;
            try { if (_camera) _camera!.enabled = false; }
            catch { }
        }
    }

    private byte[]? ReadFrame()
    {
        var previous = RenderTexture.active;
        try
        {
            RenderTexture.active = _target;
            // Unity ReadPixels starts at the lower left; the receiver's UVs do too.
            _pixels!.ReadPixels(new Rect(0, 0, Width, Height), 0, 0, false);
        }
        finally { RenderTexture.active = previous; }

        // Encode at most once per capture. Adapt the next frame instead of
        // repeatedly compressing the same pixels on the game thread.
        var encoded = ImageConversion.EncodeToJPG(_pixels!, JpegQualities[_jpegQualityIndex]);
        if (encoded == null || encoded.Length == 0) return null;
        if (encoded.Length > MaximumFrameBytes)
        {
            _jpegQualityIndex = Math.Min(_jpegQualityIndex + 1, JpegQualities.Length - 1);
            _smallFrames = 0;
            return null;
        }
        _smallFrames = encoded.Length < MaximumFrameBytes / 2 ? _smallFrames + 1 : 0;
        if (_smallFrames >= 10)
        {
            _jpegQualityIndex = Math.Max(0, _jpegQualityIndex - 1);
            _smallFrames = 0;
        }
        return encoded.ToArray();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _frame = null;
        _pending = false;
        Exception? failure = null;
        void Release(Action action)
        {
            try { action(); }
            catch (Exception error) { failure ??= error; }
        }
        if (_camera)
        {
            Release(() => _camera!.enabled = false);
            Release(() => _camera!.targetTexture = null);
        }
        if (_subscriptionAttempted)
        {
            Release(() => RenderPipelineManager.remove_endCameraRendering(_afterCamera));
            _subscriptionAttempted = false;
        }
        if (_cameraObject) Release(() => UnityEngine.Object.Destroy(_cameraObject));
        if (_target)
        {
            Release(() => _target!.Release());
            Release(() => UnityEngine.Object.Destroy(_target));
        }
        if (_pixels) Release(() => UnityEngine.Object.Destroy(_pixels));
        _camera = null;
        _cameraObject = null;
        _target = null;
        _pixels = null;
        if (failure != null) throw new InvalidOperationException("Tablet capture cleanup failed.", failure);
    }
}
