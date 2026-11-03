// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Il2CppInterop.Runtime.InteropTypes;
using Sons.Gameplay.GPS;
using UnityEngine;
using UnityEngine.UI;

namespace Modstoke.Client;

// Owns the tablet presentation. Native GPS objects, zoom, discovery and assets stay untouched.
internal sealed partial class ClientTabletNavigation : IDisposable
{
    private const float MinimumZoom = .1f;
    private const float MaximumZoom = 8f;
    private readonly GPSTrackerSystem _system;
    private readonly Canvas _canvas;
    private readonly Transform _held;
    private readonly Camera _camera;
    private RectTransform? _viewport;
    private RectTransform? _root;
    private Image? _source;
    private bool _visible;
    private bool _following = true;
    private bool _headingUp;
    private bool _showMarkers = true;
    private bool _navigable;
    private bool _headingAvailable;
    private bool _dragging;
    private bool _panStarted;
    private bool _zoomAnchorActive;
    private int _lastFrame = -1;
    private int _lastInputFrame = -1;
    private float _zoom = 1.6f;
    private float _targetZoom = 1.6f;
    private float _rotation;
    private float _heading;
    private float _nextRefresh;
    private Vector2 _size;
    private Vector2 _mapSize;
    private Vector2 _center = new(.5f, .5f);
    private Vector2 _playerUv;
    private Vector2 _lastPointer;
    private Vector2 _zoomAnchorUv;
    private Vector2 _zoomAnchorLocal;
    private Vector2 _worldSpan;
    private Vector3 _position;

    internal ClientTabletNavigation(GPSTrackerSystem system, Canvas canvas, Transform held,
        Camera camera, int sortingLayer, int sortingOrder)
    {
        _system = system;
        _canvas = canvas;
        _held = held;
        _camera = camera;
        // Graphics inherit the owned screen canvas's camera, layer and sorting.
    }

    internal bool Available => _root != null && _viewport != null && _source != null
        && _source.sprite != null && _system != null && _held != null && _canvas != null
        && _system._mapRoot != null && Same(_root.parent, _viewport)
        && Same(_system._rootCanvas, _canvas) && Same(_system._surfaceMap, _source.transform);

    internal bool Attach(RectTransform viewport)
    {
        if (Available && Same(_viewport, viewport)) return true;
        Detach();
        if (viewport == null || _system == null || _canvas == null || _held == null
            || !Same(_system._rootCanvas, _canvas) || !_canvas.transform.IsChildOf(_held)
            || _system._mapRoot == null || _system._surfaceMap == null) return false;
        var source = _system._surfaceMap.GetComponent<Image>();
        if (source == null || source.sprite == null) return false;
        viewport.ForceUpdateRectTransforms();
        if (!PositiveSize(viewport.rect.size) || !PositiveSize(_system._mapRoot.rect.size)) return false;
        _viewport = viewport;
        _source = source;
        try
        {
            BuildUi();
            _root!.gameObject.SetActive(false);
            return true;
        }
        catch
        {
            Detach();
            throw;
        }
    }

    internal void SetVisible(bool visible)
    {
        if (_visible != visible)
        {
            _visible = visible;
            if (!visible)
            {
                _placeName?.DeactivateInputField();
                DeactivateNotebookInputs();
                ClearFieldConfirmation();
            }
            CancelPointer();
            _zoomAnchorActive = false;
            _nextRefresh = 0f;
            if (_root != null) _root.gameObject.SetActive(visible);
        }
        Update();
    }

    internal void Update()
    {
        if (_root == null) return;
        if (!Available) { Detach(); return; }
        if (!_visible || !_root.gameObject.activeInHierarchy)
        {
            CancelPointer();
            return;
        }
        if (!Application.isFocused) CancelPointer();
        if (_lastFrame == Time.frameCount) return;
        _lastFrame = Time.frameCount;
        _size = _root.rect.size;
        if (!PositiveSize(_size)) return;
        LayoutChrome();
        var inCaves = _system.IsInCaves();
        var signal = _system.HasSignal();
        _navigable = !inCaves && signal;
        _headingAvailable = false;
        if (_navigable)
        {
            _position = _system.GetTrackerPosition();
            var forward = _system.GetTrackerForward();
            _navigable = Finite(_position) && TryWorldUv(_position, out _playerUv)
                && CalibrateWorldSpan();
            _headingAvailable = _navigable && Finite(forward) && forward.x * forward.x + forward.z * forward.z > .0001f;
            if (_headingAvailable)
                _heading = Mathf.Repeat(Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg, 360f);
        }
        _terrain!.gameObject.SetActive(_navigable);
        _fieldLayer!.gameObject.SetActive(_navigable);
        _markerLayer!.gameObject.SetActive(_navigable && _showMarkers);
        _player!.gameObject.SetActive(_navigable);
        _unavailable!.gameObject.SetActive(!_navigable);
        _scaleGroup!.gameObject.SetActive(_navigable);
        _compass!.gameObject.SetActive(_navigable);
        if (!_navigable)
        {
            if (_destinationEdge != null) _destinationEdge.gameObject.SetActive(false);
            CancelPointer();
            _zoomAnchorActive = false;
            HideMarkers();
            _unavailableTitle!.text = inCaves ? "UNDERGROUND" : signal ? "POSITION UNAVAILABLE" : "SIGNAL LOST";
            _unavailableDetail!.text = inCaves
                ? "Surface navigation is unavailable here.\nReturn above ground to resume tracking."
                : "Waiting for a live GPS position.";
            _positionText!.text = inCaves ? "No underground map available" : "GPS location unavailable";
            _headingText!.text = "";
            _nextRefresh = 0f;
            UpdateControlLabels();
            RefreshFieldStatus();
            return;
        }
        _terrain.sprite = _source!.sprite;
        var blend = 1f - Mathf.Exp(-12f * Mathf.Min(Time.unscaledDeltaTime, .1f));
        _zoom = Mathf.Lerp(_zoom, _targetZoom, blend);
        if (Mathf.Abs(_zoom - _targetZoom) < .001f) _zoom = _targetZoom;
        _rotation = Mathf.LerpAngle(_rotation, _headingUp && _headingAvailable ? _heading : 0f, blend);
        // Unit zoom covers the viewport diagonal; smaller zooms expose the island overview.
        var side = _size.magnitude * _zoom;
        _mapSize = new Vector2(side, side);
        ApplyPendingFieldFocus();
        if (_following) _center = _playerUv;
        else if (_zoomAnchorActive) _center = _zoomAnchorUv - ScreenToUvDelta(_zoomAnchorLocal);
        if (_zoom == _targetZoom) _zoomAnchorActive = false;
        ClampCenter();
        PresentMap();
    }

    internal void UpdateInput()
    {
        // Only the terminal's post-shell input pass calls this. Native presentation
        // callbacks keep the map current without consuming a press, release or wheel.
        Update();
        if (!_visible || _root == null || !_root.gameObject.activeInHierarchy
            || !_navigable || !PositiveSize(_size) || !PositiveSize(_mapSize) || !Application.isFocused)
        {
            CancelPointer();
            return;
        }
        if (_lastInputFrame == Time.frameCount) return;
        _lastInputFrame = Time.frameCount;
        if (!ReadPointer()) return;
        ClampCenter();
        // Apply the gesture immediately without advancing zoom/heading interpolation twice.
        PresentMap();
    }

    private void PresentMap()
    {
        _terrain!.rectTransform.sizeDelta = _mapSize;
        _terrain.rectTransform.localRotation = Quaternion.Euler(0f, 0f, _rotation);
        _terrain.rectTransform.anchoredPosition = Rotate(Vector2.Scale(new Vector2(.5f, .5f) - _center, _mapSize), _rotation);
        var playerPoint = UvToScreen(_playerUv);
        _player!.anchoredPosition = playerPoint;
        _player.localRotation = Quaternion.Euler(0f, 0f, _rotation - _heading);
        _playerHeading!.gameObject.SetActive(_headingAvailable);
        _player.gameObject.SetActive(InCoverage(_playerUv) && InView(playerPoint, 26f));
        _northArrow!.localRotation = Quaternion.Euler(0f, 0f, _rotation);
        if (Time.unscaledTime >= _nextRefresh)
        {
            _nextRefresh = Time.unscaledTime + .2f;
            RefreshMarkers();
            RefreshStatus();
            RefreshFieldStatus();
        }
        PositionMarkers();
        PositionFieldOverlays();
        PositionDestinationEdge();
        DeclutterMapLabels();
        UpdateControlLabels();
        SaveView();
    }

    internal void Zoom(int delta)
    {
        if (!_visible || !_navigable || !Available || delta == 0) return;
        _targetZoom = Mathf.Clamp(_targetZoom * Mathf.Pow(1.35f, Math.Sign(delta)), MinimumZoom, MaximumZoom);
        _zoomAnchorActive = false;
    }

    private void Recenter()
    {
        _following = true;
        _dragging = false;
        _zoomAnchorActive = false;
    }

    private void ToggleHeading()
    {
        _headingUp = !_headingUp;
        _zoomAnchorActive = false;
    }

    private void ToggleMarkers()
    {
        _showMarkers = !_showMarkers;
        if (!_showMarkers) HideMarkers();
    }

    private bool ReadPointer()
    {
        if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(_root, Input.mousePosition, _camera, out var point))
        { CancelPointer(); return false; }
        var changed = false;
        var inside = _root!.rect.Contains(point);
        var top = _size.y * .5f - point.y;
        var left = point.x + _size.x * .5f;
        // Toolbar, compass, footer and shell zoom buttons never initiate a pan or wheel zoom.
        var mapHit = inside && top > 100f && top < _size.y - 64f
            && !(left > _size.x - 180f && top < 188f) && !FieldChromeHit(point);
        if (Input.GetMouseButtonDown(0) && mapHit)
        {
            _dragging = true;
            _panStarted = false;
            _lastPointer = Input.mousePosition;
            _pressTime = Time.unscaledTime;
            _pressUv = _center + ScreenToUvDelta(point);
            _longPressHandled = false;
            _zoomAnchorActive = false;
        }
        if (_dragging)
        {
            if (!mapHit) { CancelPointer(); return false; }
            Vector2 screenPointer = Input.mousePosition;
            if ((screenPointer - _lastPointer).sqrMagnitude >= 16f) _panStarted = true;
            // Use one current map plane for both samples; tablet sway alone must not pan the map.
            if (_panStarted && RectTransformUtility.ScreenPointToLocalPointInRectangle(_root, _lastPointer, _camera, out var previous))
            {
                var movement = point - previous;
                _following = false;
                _center -= ScreenToUvDelta(movement);
                _lastPointer = screenPointer;
                changed = true;
            }
            if (!_panStarted && !_longPressHandled && Input.GetMouseButton(0)
                && Time.unscaledTime - _pressTime >= .65f)
            {
                _longPressHandled = true;
                CreateMapPin(_pressUv);
                changed = true;
            }
            if (Input.GetMouseButtonUp(0))
            {
                if (!_panStarted && !_longPressHandled)
                {
                    SelectFieldPin(point);
                    changed = true;
                }
                _dragging = false;
            }
        }
        if (!Input.GetMouseButton(0)) _dragging = false;
        var wheel = Input.mouseScrollDelta.y;
        if (!mapHit || Mathf.Abs(wheel) < .001f) return changed;
        if (!_following)
        {
            _zoomAnchorUv = _center + ScreenToUvDelta(point);
            _zoomAnchorLocal = point;
            _zoomAnchorActive = true;
        }
        _targetZoom = Mathf.Clamp(_targetZoom * Mathf.Pow(1.18f, Mathf.Clamp(wheel, -4f, 4f)), MinimumZoom, MaximumZoom);
        return true;
    }

    private void CancelPointer()
    {
        _dragging = false;
        _panStarted = false;
        _longPressHandled = false;
    }

    private void ClampCenter()
    {
        // Allow background margins so boundary pins can be centered and entire routes fitted.
        // The view center still stays inside the native map's coverage.
        _center = new Vector2(Mathf.Clamp01(_center.x), Mathf.Clamp01(_center.y));
    }

    private bool TryWorldUv(Vector3 world, out Vector2 uv)
    {
        uv = default;
        var nativeSize = _system._mapRoot.rect.size;
        if (!PositiveSize(nativeSize) || !Finite(world)) return false;
        var point = Vector2.zero;
        _system.World2DToMap2D(new Vector2(world.x, world.z), ref point);
        uv = new Vector2(.5f + point.x / nativeSize.x, .5f + point.y / nativeSize.y);
        return float.IsFinite(uv.x) && float.IsFinite(uv.y);
    }

    private bool CalibrateWorldSpan()
    {
        if (!TryWorldUv(Vector3.zero, out var origin) || !TryWorldUv(new Vector3(100f, 0f, 0f), out var east)
            || !TryWorldUv(new Vector3(0f, 0f, 100f), out var north)) return false;
        _worldSpan = new Vector2(100f / Mathf.Abs(east.x - origin.x), 100f / Mathf.Abs(north.y - origin.y));
        _fieldOriginUv = origin;
        _fieldEastUv = (east - origin) / 100f;
        _fieldNorthUv = (north - origin) / 100f;
        return PositiveSize(_worldSpan);
    }

    private Vector2 ScreenToUvDelta(Vector2 point)
    {
        var unrotated = Rotate(point, -_rotation);
        return new Vector2(unrotated.x / _mapSize.x, unrotated.y / _mapSize.y);
    }
    private Vector2 UvToScreen(Vector2 uv) => Rotate(Vector2.Scale(uv - _center, _mapSize), _rotation);
    private bool InView(Vector2 point, float margin) => Mathf.Abs(point.x) <= _size.x * .5f - margin
        && point.y <= _size.y * .5f - margin && point.y >= -_size.y * .5f + 64f + margin;
    private static bool InCoverage(Vector2 uv) => uv.x >= 0f && uv.x <= 1f && uv.y >= 0f && uv.y <= 1f;
    private static Vector2 Rotate(Vector2 value, float angle)
    {
        var radians = angle * Mathf.Deg2Rad;
        var c = Mathf.Cos(radians);
        var s = Mathf.Sin(radians);
        return new Vector2(value.x * c - value.y * s, value.x * s + value.y * c);
    }

    internal void Detach()
    {
        SaveView();
        if (_root != null)
        {
            _root.gameObject.SetActive(false);
            UnityEngine.Object.Destroy(_root.gameObject);
        }
        DisposeUiResources();
        _markers.Clear();
        ClearFieldUi();
        _activeMarkers = 0;
        _root = null;
        _viewport = null;
        _source = null;
        _visible = false;
        _navigable = false;
        CancelPointer();
        _zoomAnchorActive = false;
        _lastFrame = -1;
    }

    private static bool PositiveSize(Vector2 size) => float.IsFinite(size.x) && float.IsFinite(size.y) && size.x > 0f && size.y > 0f;
    private static bool Finite(Vector3 value) => float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);
    private static bool Same(Il2CppObjectBase? first, Il2CppObjectBase? second) => first is not null
        && second is not null && !first.WasCollected && !second.WasCollected && first.Pointer == second.Pointer;

    public void Dispose()
    {
        _pendingFieldFocus = null;
        Detach();
    }
}
