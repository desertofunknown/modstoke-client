// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace Modstoke.Client;

internal sealed class ClientDronePlacementPreview : IDisposable
{
    private const float MarkerHeight = ClientDroneModel.GroundY + .015f;
    private GameObject? _root;
    private GameObject? _cameraObject;
    private Camera? _camera;
    private RenderTexture? _feed;
    private Mesh? _mesh;
    private Material? _outline;
    private Material? _status;
    private bool _visible;
    private bool _disposed;

    internal Texture? Feed => !_disposed && _visible ? _feed : null;

    internal ClientDronePlacementPreview()
    {
        try
        {
            _root = new GameObject("Drone placement preview");
            _root.SetActive(false);
            var shader = Shader.Find("HDRP/Lit");
            if (!shader) throw new InvalidOperationException("The drone placement material is unavailable.");
            _outline = new Material(shader) { name = "Drone placement guide outline" };
            _status = new Material(shader) { name = "Drone placement guide status" };
            SetColor(_outline, new Color(.008f, .012f, .015f), false);
            SetColor(_status, new Color(.05f, .9f, .22f), true);
            CreateGuides();
            CreateCamera();
        }
        catch { Dispose(); throw; }
    }

    internal void Show(Vector3 position, Quaternion rotation, bool clear)
    {
        if (_disposed) return;
        _root!.transform.SetPositionAndRotation(position, rotation);
        SetColor(_status!, clear ? new Color(.05f, .9f, .22f) : new Color(1, .065f, .025f), true);
        _visible = true;
        _root.SetActive(true);
        _camera!.enabled = true;
    }

    internal void Hide()
    {
        _visible = false;
        if (_camera) _camera!.enabled = false;
        if (_root) _root!.SetActive(false);
    }

    private void CreateCamera()
    {
        _cameraObject = new GameObject("Drone placement ground camera");
        var node = _cameraObject;
        node.transform.SetParent(_root!.transform, false);
        var target = new Vector3(0, MarkerHeight, 0);
        node.transform.localPosition = target + new Vector3(0, 2, -1.5f);
        node.transform.localRotation = Quaternion.LookRotation(target - node.transform.localPosition, Vector3.up);
        _camera = node.AddComponent<Camera>();
        _camera.enabled = false;
        _camera.fieldOfView = 60;
        _camera.aspect = 640f / 360;
        _camera.nearClipPlane = .05f;
        _camera.farClipPlane = 100;
        _camera.depth = -20;
        _camera.allowMSAA = false;
        _camera.allowHDR = true;
        _camera.clearFlags = CameraClearFlags.Skybox;
        var main = Camera.main;
        _camera.cullingMask = (main ? main.cullingMask : -1) | (1 << _root.layer);
        var uiLayer = LayerMask.NameToLayer("UI");
        if (uiLayer >= 0) _camera.cullingMask &= ~(1 << uiLayer);
        var data = node.GetComponent<HDAdditionalCameraData>() ?? node.AddComponent<HDAdditionalCameraData>();
        data.clearColorMode = HDAdditionalCameraData.ClearColorMode.Sky;
        data.clearDepth = true;
        data.antialiasing = HDAdditionalCameraData.AntialiasingMode.FastApproximateAntialiasing;
        data.volumeAnchorOverride = node.transform;
        var mainData = main ? main.GetComponent<HDAdditionalCameraData>() : null;
        data.volumeLayerMask = mainData != null ? mainData.volumeLayerMask : (LayerMask)(-1);
        _feed = new RenderTexture(640, 360, 24, RenderTextureFormat.ARGB32)
        {
            name = "Drone placement ground feed",
            antiAliasing = 1,
            useMipMap = false,
            autoGenerateMips = false
        };
        if (!_feed.Create()) throw new InvalidOperationException("The drone placement render target could not be created.");
        _camera.targetTexture = _feed;
    }

    private void CreateGuides()
    {
        var vertices = new List<Vector3>();
        var normals = new List<Vector3>();
        var triangles = new[] { new List<int>(), new List<int>() };

        void Strip(Vector2 start, Vector2 end, float width, float height, int material)
        {
            var a = new Vector3(start.x, height, start.y);
            var b = new Vector3(end.x, height, end.y);
            var side = Vector3.Cross(Vector3.up, (b - a).normalized) * (width * .5f);
            var index = vertices.Count;
            vertices.AddRange(new[] { a - side, b - side, b + side, a + side });
            normals.AddRange(new[] { Vector3.up, Vector3.up, Vector3.up, Vector3.up });
            triangles[material].AddRange(new[] { index, index + 1, index + 2, index, index + 2, index + 3 });
        }

        void Guide(Vector2 start, Vector2 end, float width)
        {
            Strip(start, end, width + .02f, MarkerHeight, 0);
            Strip(start, end, width, MarkerHeight + .002f, 1);
        }

        // The open center keeps the actual ground visible. The perimeter covers
        // the full clearance envelope, including the open propellers.
        const float edge = .55f;
        Guide(new Vector2(-edge, -edge), new Vector2(-edge, edge), .028f);
        Guide(new Vector2(-edge, edge), new Vector2(edge, edge), .028f);
        Guide(new Vector2(edge, edge), new Vector2(edge, -edge), .028f);
        Guide(new Vector2(edge, -edge), new Vector2(-edge, -edge), .028f);
        for (var i = 0; i < 4; i++)
        {
            var foot = ClientDroneModel.MotorPosition(i);
            Guide(new Vector2(foot.x - .025f, foot.z), new Vector2(foot.x + .025f, foot.z), .05f);
        }
        // A nose chevron distinguishes the airframe's heading from the square.
        Guide(new Vector2(-.09f, .32f), new Vector2(0, .43f), .025f);
        Guide(new Vector2(0, .43f), new Vector2(.09f, .32f), .025f);

        _mesh = new Mesh { name = "Drone placement footprint mesh" };
        _mesh.vertices = vertices.ToArray();
        _mesh.normals = normals.ToArray();
        _mesh.subMeshCount = 2;
        _mesh.SetTriangles(triangles[0].ToArray(), 0);
        _mesh.SetTriangles(triangles[1].ToArray(), 1);
        _mesh.RecalculateBounds();
        _root!.AddComponent<MeshFilter>().sharedMesh = _mesh;
        var renderer = _root.AddComponent<MeshRenderer>();
        renderer.sharedMaterials = new[] { _outline!, _status! };
        renderer.shadowCastingMode = ShadowCastingMode.Off;
        renderer.receiveShadows = false;
    }

    private static void SetColor(Material material, Color color, bool emissive)
    {
        if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
        if (material.HasProperty("_Color")) material.SetColor("_Color", color);
        if (material.HasProperty("_Metallic")) material.SetFloat("_Metallic", 0);
        if (material.HasProperty("_Smoothness")) material.SetFloat("_Smoothness", 0);
        if (material.HasProperty("_EmissiveColor")) material.SetColor("_EmissiveColor", emissive ? color * 2 : Color.black);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Hide();
        if (_camera) _camera!.targetTexture = null;
        if (_cameraObject) UnityEngine.Object.Destroy(_cameraObject);
        _cameraObject = null;
        if (_root) UnityEngine.Object.Destroy(_root);
        _root = null;
        _camera = null;
        if (_feed != null) { _feed.Release(); UnityEngine.Object.Destroy(_feed); }
        _feed = null;
        if (_mesh) UnityEngine.Object.Destroy(_mesh);
        _mesh = null;
        if (_outline) UnityEngine.Object.Destroy(_outline);
        _outline = null;
        if (_status) UnityEngine.Object.Destroy(_status);
        _status = null;
    }
}
