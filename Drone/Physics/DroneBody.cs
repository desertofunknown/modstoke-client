// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.Attributes;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

namespace Modstoke.Client;

internal sealed class ClientDroneBody : IDisposable
{
    private readonly bool _authority;
    private readonly float[] _motors = new float[4];
    private readonly ClientAircraftWind _wind = new();
    private readonly Vector3[] _airPoints = new Vector3[5];
    private readonly Vector3[] _airForces = new Vector3[5];
    private Rigidbody _body = null!;
    private ClientDroneModel? _model;
    private ClientDroneAudio? _audio;
    private ClientDroneFirmware? _firmware;
    private GameObject? _cameraObject;
    private Camera? _camera;
    private RenderTexture? _feed;
    private ClientDroneVision? _vision;
    private DroneCameraVision _requestedVision;
    private bool _immersiveCamera;
    private float _targetCameraPitch = 12;
    private float _cameraPitch = 12;
    private float _cameraZoom = 1;
    private float _charge = 1;
    private float _health = 1;
    private float _groundedUntil = -1;
    private Vector3 _groundNormal = Vector3.up;
    private float _lastImpact = -1;
    private bool _disposed;
    private bool _replicaArmed;
    private string _replicaMode = "Disarmed";
    private Vector3 _replicaPosition;
    private Quaternion _replicaRotation;
    private Vector3 _replicaVelocity;
    private long _replicaAt;
    private bool _solarDeployed;

    internal GameObject Root { get; private set; } = null!;
    internal Vector3 Position => Root ? Root.transform.position : Vector3.zero;
    internal Quaternion Rotation => Root ? Root.transform.rotation : Quaternion.identity;
    internal float Heading
    {
        get
        {
            if (_disposed || !Root) return 0;
            var forward = Root.transform.forward;
            return (MathF.Atan2(forward.x, forward.z) * Mathf.Rad2Deg + 360) % 360;
        }
    }
    internal float CameraPitch => _cameraPitch;
    internal float CameraZoom => _cameraZoom;
    internal DroneCameraVision CameraVision => _vision?.Mode ?? DroneCameraVision.Color;
    internal string CameraVisionStatus => _vision?.Status ?? "Color camera · sensor effects initialize with the live feed.";
    internal Vector3 Velocity => _disposed || !_body ? Vector3.zero : _authority ? _body.velocity : _replicaVelocity;
    internal float BatteryCharge
    {
        get => _charge;
        set { _charge = Fraction(value); if (_charge <= .0001f && _authority) StopMotors(); }
    }
    internal float Health
    {
        get => _health;
        set { _health = Fraction(value); if (_health <= 0 && _authority) StopMotors(); }
    }
    // Unity does not send OnCollisionStay while a resting Rigidbody is asleep.
    internal bool Grounded => !_disposed && _body &&
        (Time.fixedTime <= _groundedUntil || _groundedUntil >= 0 && _body.IsSleeping()) && MathF.Abs(Velocity.y) < .9f;
    internal bool Armed => !_disposed && (_authority ? _firmware?.Armed == true : _replicaArmed);
    internal bool SolarMoving => _model?.SolarMoving == true;
    internal bool SolarFolded => _model?.SolarFolded == true;
    internal bool SolarReady => _model?.SolarReady == true;
    internal string SolarStatus => SolarMoving ? _solarDeployed ? "Solar unfolding" : "Solar folding" : "";
    internal string Mode => _disposed ? "Offline" : _health <= 0 ? "Damaged" : _charge <= .0001f ? "Battery empty"
        : _authority ? _firmware?.Mode ?? "Offline" : _replicaMode;

    internal ClientDroneBody(string id, Vector3 position, Quaternion rotation, bool authority)
    {
        _authority = authority;
        try
        {
            Root = new GameObject("Modstoke survey drone " + id);
            Root.transform.SetPositionAndRotation(position, rotation);
            _body = Root.AddComponent<Rigidbody>();
            _body.mass = ClientDroneFirmware.Mass;
            _body.drag = 0;
            _body.angularDrag = .18f;
            _body.useGravity = authority;
            _body.isKinematic = !authority;
            _body.interpolation = RigidbodyInterpolation.Interpolate;
            _body.collisionDetectionMode = authority ? CollisionDetectionMode.ContinuousDynamic : CollisionDetectionMode.Discrete;
            _body.maxAngularVelocity = 3.2f;
            var hull = Root.AddComponent<BoxCollider>();
            hull.center = new Vector3(0, -.145f, -.02f);
            hull.size = new Vector3(.21f, .07f, .39f);
            var battery = Root.AddComponent<BoxCollider>();
            battery.center = new Vector3(0, -.1965f, -.081f);
            battery.size = new Vector3(.164f, .053f, .243f);
            for (var i = 0; i < 4; i++)
            {
                var motor = ClientDroneModel.MotorPosition(i);
                var pod = Root.AddComponent<SphereCollider>();
                pod.center = motor;
                pod.radius = .037f;
                var foot = Root.AddComponent<CapsuleCollider>();
                foot.direction = 1;
                foot.center = new Vector3(motor.x, -.161f, motor.z);
                foot.radius = .012f;
                foot.height = .128f;
                var root = new Vector3(Mathf.Sign(motor.x) * .095f, -.142f, Mathf.Sign(motor.z) * .120f);
                var elbow = new Vector3(Mathf.Sign(motor.x) * .205f, -.083f, Mathf.Sign(motor.z) * .195f);
                ArmCollider(root, elbow);
                ArmCollider(elbow, motor);
            }
            var sensor = Root.AddComponent<SphereCollider>();
            sensor.center = ClientDroneModel.CameraPivot;
            sensor.radius = .062f;
            var charger = Root.AddComponent<BoxCollider>();
            charger.center = new Vector3(0, -.0745f, 0);
            charger.size = new Vector3(.198f, .073f, .24f);
            // A consistent inertia tensor keeps the controller tied to this airframe,
            // independent of Unity's compound-collider inertia approximation.
            _body.centerOfMass = new Vector3(0, -.125f, ClientDroneFirmware.CenterOfMassZ);
            _body.inertiaTensor = new Vector3(.09f, .16f, .09f);
            _body.inertiaTensorRotation = Quaternion.identity;
            _model = new ClientDroneModel(Root.transform);
            if (authority)
            {
                _firmware = new ClientDroneFirmware();
                ClientDroneCollisionRelay.Attach(Root, this);
            }
            _replicaPosition = position;
            _replicaRotation = rotation;
        }
        catch { Dispose(); throw; }
    }

    private void ArmCollider(Vector3 start, Vector3 end)
    {
        var node = new GameObject("Drone arm collision") { layer = Root.layer };
        node.transform.SetParent(Root.transform, false);
        node.transform.localPosition = (start + end) * .5f;
        node.transform.localRotation = Quaternion.LookRotation(end - start);
        node.AddComponent<BoxCollider>().size = new Vector3(.045f, .027f, (end - start).magnitude);
    }

    internal bool Arm()
    {
        if (_disposed || !_authority || _firmware == null || !_body || !SolarFolded) return false;
        if (!_firmware.Arm(Sensors(DronePilotInput.Neutral, true))) return false;
        SetSolarDeployed(false);
        _body.WakeUp();
        return true;
    }

    internal void Hold()
    {
        if (!_disposed && _authority && _body && Root)
            _firmware?.Hold(Sensors(DronePilotInput.Neutral, true));
    }

    internal bool ResumeControl() => !_disposed && _authority && _body && Root
        && _firmware?.ResumeControl(Sensors(DronePilotInput.Neutral, true)) == true;

    internal void Land(ClientDroneFirmware.LandingReason reason = ClientDroneFirmware.LandingReason.Manual)
    {
        if (!_disposed && _authority) _firmware?.Land(reason);
    }

    internal void StopMotors()
    {
        _firmware?.Stop();
        Array.Clear(_motors);
    }

    internal void FixedUpdate(DronePilotInput input, bool fresh)
    {
        if (_disposed || !_body || !Root) return;
        if (!_authority)
        {
            if (_replicaAt == 0) return;
            var age = Math.Min(.15f, Math.Max(0, (Environment.TickCount64 - _replicaAt) / 1000f));
            var target = _replicaPosition + _replicaVelocity * age;
            var blend = 1 - Mathf.Exp(-16 * Time.fixedDeltaTime);
            _body.MovePosition(Vector3.Lerp(_body.position, target, blend));
            _body.MoveRotation(Quaternion.Slerp(_body.rotation, _replicaRotation, blend));
            return;
        }
        var snapshot = Sensors(input, fresh);
        var output = _firmware!.Tick(snapshot);
        var dt = Math.Clamp(Time.fixedDeltaTime, .001f, .1f);
        _wind.Advance(dt, _body);
        var lift = 0f;
        var yaw = 0f;
        var rotation = _body.rotation;
        var position = _body.position;
        var up = rotation * Vector3.up;
        for (var i = 0; i < 4; i++)
        {
            var target = i == 0 ? output.FrontLeft : i == 1 ? output.FrontRight : i == 2 ? output.RearLeft : output.RearRight;
            _motors[i] = output.Armed ? Mathf.MoveTowards(_motors[i], target, dt * 60) : 0;
            if (_motors[i] <= 0) continue;
            _body.AddForceAtPosition(up * _motors[i], position + rotation * ClientDroneModel.MotorPosition(i), ForceMode.Force);
            lift += _motors[i];
            yaw += _motors[i] * (i == 0 || i == 3 ? 1 : -1);
        }
        if (output.Armed)
            _body.AddRelativeTorque(new Vector3(0, yaw * ClientDroneFirmware.ReactionTorque, 0), ForceMode.Force);
        ApplyAerodynamics(output.Armed);
        var load = lift / (ClientDroneFirmware.Mass * 9.81f);
        // Dedicated pack: roughly eight minutes of hovering, faster under heavy lift.
        BatteryCharge -= dt * (1f / 21600 + load * load / 480);
        if (_solarDeployed && (Armed || !Grounded)) SetSolarDeployed(false);
    }

    private void ApplyAerodynamics(bool motorsArmed)
    {
        var density = ClientAircraftAerodynamics.Density(_body.position.y);
        if (!float.IsFinite(density)) return;
        var rotation = _body.rotation;
        var inverseRotation = Quaternion.Inverse(rotation);
        var position = _body.position;
        var centre = _body.worldCenterOfMass;
        var totalForce = Vector3.zero;
        var totalTorque = Vector3.zero;
        for (var i = 0; i < _airPoints.Length; i++)
        {
            var point = position + rotation * (i == 0 ? new Vector3(0, -.13f, -.02f) : ClientDroneModel.MotorPosition(i - 1));
            var air = inverseRotation * (_body.GetPointVelocity(point) - _wind.Sample(point));
            // Estimated Cd*A in local right/up/forward axes, in square metres.
            // Narrow hull, belly battery and folded solar use an estimated
            // combined projection. Each arm/motor/blade group uses (.01, .0125,
            // .01) at Cd .8; rotor discs are not treated as solid plates.
            // Total (.082, .115, .067) is an authored small-drone approximation,
            // not measured airframe data. Rotor lift remains the motor model.
            var area = i == 0 ? new Vector3(.050f, .075f, .035f) : new Vector3(.008f, .010f, .008f);
            var force = rotation * new Vector3(
                -area.x * air.x * MathF.Abs(air.x),
                -area.y * air.y * MathF.Abs(air.y),
                -area.z * air.z * MathF.Abs(air.z)) * (.5f * density);
            if (!Finite(point) || !Finite(force)) return;
            _airPoints[i] = point;
            _airForces[i] = force;
            totalForce += force;
            totalTorque += Vector3.Cross(point - centre, force);
        }
        if (!Finite(totalForce) || !Finite(totalTorque)) return;
        var resting = !motorsArmed && !Armed && Grounded &&
            (_body.IsSleeping() || _body.velocity.sqrMagnitude < .0004f && _body.angularVelocity.sqrMagnitude < .0004f);
        if (resting)
        {
            if (GroundSupportsWind(totalForce, totalTorque)) return;
            // A sleeping body has no fresh OnCollisionStay. Retain its known
            // contact for the normal grace period while force wakes the solver.
            _groundedUntil = Time.fixedTime + .12f;
        }
        for (var i = 0; i < _airPoints.Length; i++)
            _body.AddForceAtPosition(_airForces[i], _airPoints[i], ForceMode.Force);
    }

    private bool GroundSupportsWind(Vector3 force, Vector3 torque)
    {
        var transform = Root.transform;
        if (Vector3.Dot(transform.up, _groundNormal) < .85f) return false;
        // Approximate dry foot static friction and the motor-foot support
        // footprint. Only resting, disarmed contact can absorb this load;
        // armed takeoff, sliding and unpowered flight receive full air forces.
        var load = force + Physics.gravity * _body.mass;
        var normalLoad = -Vector3.Dot(load, _groundNormal);
        if (normalLoad <= 0) return false;
        var tangent = load + _groundNormal * normalLoad;
        var right = Vector3.ProjectOnPlane(transform.right, _groundNormal).normalized;
        var forward = Vector3.Cross(right, _groundNormal);
        var support = transform.TransformPoint(new Vector3(0, ClientDroneModel.GroundY, 0));
        var moment = torque + Vector3.Cross(_body.worldCenterOfMass - support, load);
        const float friction = .55f;
        // Translation and yaw share the available friction; pitch and roll
        // must also remain within the footprint's restoring moment.
        return tangent.magnitude + MathF.Abs(Vector3.Dot(moment, _groundNormal)) / ClientDroneFirmware.ArmZ <= friction * normalLoad
            && MathF.Abs(Vector3.Dot(moment, right)) <= ClientDroneFirmware.ArmZ * normalLoad
            && MathF.Abs(Vector3.Dot(moment, forward)) <= ClientDroneFirmware.ArmX * normalLoad;
    }

    private ClientDroneFirmware.Sample Sensors(DronePilotInput input, bool fresh)
    {
        // Interpolated render transforms can lag the rigidbody by a physics step.
        var rotation = _body.rotation;
        var up = rotation * Vector3.up;
        var forward = rotation * Vector3.forward;
        var right = rotation * Vector3.right;
        var position = _body.position;
        var velocity = _body.velocity;
        var rates = Quaternion.Inverse(rotation) * _body.angularVelocity;
        // Heading-relative horizontal speed, without projecting a slope into climb.
        var heading = new Vector3(forward.x, 0, forward.z).normalized;
        var lateral = new Vector3(heading.z, 0, -heading.x);
        return new ClientDroneFirmware.Sample(Environment.TickCount64, Time.fixedTime, Grounded, up.y,
            MathF.Atan2(heading.x, heading.z),
            MathF.Atan2(-forward.y, up.y), MathF.Atan2(right.y, up.y), rates.x, rates.y, rates.z,
            Vector3.Dot(velocity, heading), Vector3.Dot(velocity, lateral), velocity.y, MathF.Abs(Physics.gravity.y),
            _charge, _health, input.Forward, input.Right, input.Climb, input.Yaw, fresh,
            position.x, position.y, position.z);
    }

    internal void Update(float deltaTime, ClientTabletPreferencesSnapshot? audioPreferences)
    {
        if (_disposed || !Root) return;
        if (!_authority)
        {
            var thrust = _replicaArmed ? ClientDroneFirmware.Mass * 9.81f / 4 : 0;
            for (var i = 0; i < 4; i++) _motors[i] = thrust;
        }
        _model?.Update(deltaTime, _motors, Armed, _health, _charge);
        if (audioPreferences != null)
        {
            _audio ??= new ClientDroneAudio();
            _audio.Update(Position, Velocity, _model?.MotorActivity ?? 0, audioPreferences);
        }
        else if (_audio != null)
        {
            _audio.Dispose();
            _audio = null;
        }
        var cameraDelta = float.IsFinite(deltaTime) ? Math.Clamp(deltaTime, 0, .1f) : 0;
        _cameraPitch = Mathf.Lerp(_cameraPitch, _targetCameraPitch, 1 - Mathf.Exp(-35 * cameraDelta));
        _model?.SetCameraTilt(_cameraPitch);
        ApplyCameraView();
        if (_camera != null && (_charge <= .0001f || _health <= 0))
        {
            _camera.enabled = false;
            _vision?.SetActive(false);
        }
        if (_camera != null && _camera.enabled) _vision?.Update();
    }

    internal void SetReplica(Vector3 position, Quaternion rotation, Vector3 velocity, float charge, float health, bool armed, string mode)
    {
        if (_authority || _disposed || !Root) return;
        _replicaPosition = position;
        _replicaRotation = rotation;
        _replicaVelocity = velocity;
        _replicaAt = Environment.TickCount64;
        _charge = Fraction(charge);
        _health = Fraction(health);
        _replicaArmed = armed && _charge > .0001f && _health > 0;
        _replicaMode = mode;
        if ((_body.position - position).sqrMagnitude > 64)
        {
            _body.position = position;
            _body.rotation = rotation;
        }
    }

    internal void SetSolarDeployed(bool deployed)
    {
        if (_disposed) return;
        _solarDeployed = deployed && !Armed && (!_authority || Grounded);
        _model?.SetSolarDeployed(_solarDeployed);
    }

    internal void SetSolarCharging(bool charging) => _model?.SetSolarCharging(charging);

    internal void SetCameraVision(DroneCameraVision mode)
    {
        if (_disposed || !Enum.IsDefined(mode)) return;
        _requestedVision = mode;
        _vision?.SetMode(mode);
    }

    internal void SetCameraView(bool immersive, float pitch, float zoom)
    {
        if (_disposed) return;
        immersive &= _charge > .0001f && _health > 0;
        _targetCameraPitch = float.IsFinite(pitch) ? Math.Clamp(pitch, -20, 80) : 12;
        _cameraZoom = float.IsFinite(zoom) ? Math.Clamp(zoom, 1, 3) : 1;
        if (_immersiveCamera != immersive)
        {
            if (_camera) ReplaceCameraFeed(immersive);
            _immersiveCamera = immersive;
        }
        ApplyCameraView();
    }

    private void ApplyCameraView()
    {
        if (_camera == null || !_camera || _cameraObject == null || !_cameraObject) return;
        var tilt = Quaternion.Euler(_cameraPitch, 0, 0);
        // Follow the physical gimbal lens, just outside its front glass. Keep the
        // environment's normal depth and culling so nearby obstacles remain visible.
        _cameraObject.transform.localPosition = ClientDroneModel.CameraPivot + tilt * ClientDroneModel.CameraLensOffset;
        _cameraObject.transform.localRotation = tilt;
        _camera.fieldOfView = 2 * MathF.Atan(MathF.Tan(39 * Mathf.Deg2Rad) / _cameraZoom) * Mathf.Rad2Deg;
    }

    internal Texture? CameraFeed(bool active)
    {
        if (_disposed || !Root) return null;
        active &= _charge > .0001f && _health > 0;
        if (!active)
        {
            if (_camera != null) _camera.enabled = false;
            _vision?.SetActive(false);
            SetCameraView(false, _targetCameraPitch, _cameraZoom);
            return null;
        }
        if (!_camera) CreateCamera();
        else if (!_camera!.enabled) ConfigureCameraRendering();
        _camera!.enabled = true;
        _vision?.SetActive(true);
        return _feed;
    }

    private void CreateCamera()
    {
        try
        {
            _cameraObject = new GameObject("Drone onboard camera");
            _cameraObject.SetActive(false);
            _cameraObject.transform.SetParent(Root.transform, false);
            _camera = _cameraObject.AddComponent<Camera>();
            _camera.enabled = false;
            ApplyCameraView();
            _camera.nearClipPlane = .05f;
            _camera.depth = -20;
            _camera.allowMSAA = false;
            _camera.allowHDR = true;
            _camera.allowDynamicResolution = false;
            _camera.stereoTargetEye = StereoTargetEyeMask.None;
            _camera.clearFlags = CameraClearFlags.Skybox;
            ConfigureCameraRendering();
            ReplaceCameraFeed(_immersiveCamera);
            _vision = new ClientDroneVision(_camera);
            _vision.SetMode(_requestedVision);
            _cameraObject.SetActive(true);
        }
        catch { DisposeCamera(); throw; }
    }

    private void ConfigureCameraRendering()
    {
        var camera = _camera!;
        var main = Camera.main;
        camera.farClipPlane = main ? Mathf.Max(600, main.farClipPlane) : 600;
        camera.cullingMask = main ? main.cullingMask : -1;
        camera.depthTextureMode = (main ? main.depthTextureMode : DepthTextureMode.None) | DepthTextureMode.Depth;
        var uiLayer = LayerMask.NameToLayer("UI");
        if (uiLayer >= 0) camera.cullingMask &= ~(1 << uiLayer);
        var data = camera.GetComponent<HDAdditionalCameraData>() ?? camera.gameObject.AddComponent<HDAdditionalCameraData>();
        var mainData = main ? main.GetComponent<HDAdditionalCameraData>() : null;
        // Preserve the game's frame overrides without sharing its camera callbacks,
        // render history or player exposure target with the onboard camera.
        data.defaultFrameSettings = mainData != null ? mainData.defaultFrameSettings : FrameSettingsRenderType.Camera;
        data.m_RenderingPathCustomFrameSettings = mainData != null
            ? mainData.m_RenderingPathCustomFrameSettings : FrameSettings.NewDefaultCamera();
        data.renderingPathCustomFrameSettingsOverrideMask = mainData != null
            ? mainData.renderingPathCustomFrameSettingsOverrideMask : new FrameSettingsOverrideMask();
        data.customRenderingSettings = mainData != null && mainData.customRenderingSettings;
        data.volumeLayerMask = mainData != null ? mainData.volumeLayerMask : (LayerMask)(-1);
        data.probeLayerMask = mainData != null ? mainData.probeLayerMask : (LayerMask)(-1);
        data.volumeAnchorOverride = camera.transform;
        data.clearColorMode = HDAdditionalCameraData.ClearColorMode.Sky;
        data.clearDepth = true;
        data.antialiasing = HDAdditionalCameraData.AntialiasingMode.FastApproximateAntialiasing;
        data.allowDynamicResolution = false;
        data.xrRendering = false;
    }

    private void ReplaceCameraFeed(bool immersive)
    {
        RenderTexture? replacement = null;
        try
        {
            replacement = new RenderTexture(immersive ? 1600 : 640, immersive ? 900 : 360, 24, RenderTextureFormat.ARGB32)
            {
                name = "Drone camera feed",
                antiAliasing = 1,
                useMipMap = false,
                autoGenerateMips = false
            };
            if (!replacement.Create()) throw new InvalidOperationException("The drone camera render target could not be created.");
            _camera!.targetTexture = replacement;
        }
        catch
        {
            DestroyCameraFeed(replacement);
            throw;
        }
        var previous = _feed;
        _feed = replacement;
        DestroyCameraFeed(previous);
    }

    internal void Contact(Collision collision, bool impact)
    {
        if (_disposed || !_authority || !Root) return;
        for (var i = 0; i < Math.Min(collision.contactCount, 6); i++)
        {
            var normal = collision.GetContact(i).normal;
            if (normal.y > .55f)
            {
                _groundNormal = normal;
                _groundedUntil = Time.fixedTime + .12f;
            }
        }
        if (!impact || Time.fixedTime - _lastImpact < .18f) return;
        var speed = collision.relativeVelocity.magnitude;
        if (!float.IsFinite(speed) || speed < 3.8f) return;
        _lastImpact = Time.fixedTime;
        Health -= Math.Clamp((speed - 3.8f) * .065f, 0, .65f);
        if (_health < .35f) Land();
    }

    private static float Fraction(float value) => float.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;
    private static bool Finite(Vector3 value) => float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);

    private static void DestroyCameraFeed(RenderTexture? feed)
    {
        if (feed != null)
        {
            feed.Release();
            UnityEngine.Object.Destroy(feed);
        }
    }

    private void DisposeCamera()
    {
        _vision?.Dispose();
        _vision = null;
        if (_camera != null) { _camera.enabled = false; _camera.targetTexture = null; }
        if (_cameraObject) UnityEngine.Object.Destroy(_cameraObject);
        _camera = null;
        _cameraObject = null;
        DestroyCameraFeed(_feed);
        _feed = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _firmware?.Dispose();
        _firmware = null;
        _audio?.Dispose();
        _audio = null;
        if (Root) ClientDroneCollisionRelay.Detach(Root);
        DisposeCamera();
        _model?.Dispose();
        _model = null;
        if (Root) { Root.SetActive(false); UnityEngine.Object.Destroy(Root); }
    }
}

public sealed class ClientDroneCollisionRelay : MonoBehaviour
{
    private static readonly Dictionary<int, ClientDroneBody> Owners = new();
    private static bool _registered;

    public ClientDroneCollisionRelay(IntPtr pointer) : base(pointer) { }

    [HideFromIl2Cpp]
    internal static void Attach(GameObject root, ClientDroneBody owner)
    {
        if (!_registered)
        {
            ClassInjector.RegisterTypeInIl2Cpp<ClientDroneCollisionRelay>();
            _registered = true;
        }
        Owners[root.GetInstanceID()] = owner;
        root.AddComponent<ClientDroneCollisionRelay>();
    }

    [HideFromIl2Cpp]
    internal static void Detach(GameObject root) => Owners.Remove(root.GetInstanceID());

    private void OnCollisionEnter(Collision collision)
    {
        if (Owners.TryGetValue(gameObject.GetInstanceID(), out var body)) body.Contact(collision, true);
    }

    private void OnCollisionStay(Collision collision)
    {
        if (Owners.TryGetValue(gameObject.GetInstanceID(), out var body)) body.Contact(collision, false);
    }

    private void OnDestroy() => Owners.Remove(gameObject.GetInstanceID());
}
