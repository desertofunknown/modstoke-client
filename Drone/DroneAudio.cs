// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using FMOD;
using UnityEngine;

namespace Modstoke.Client;

// One main-thread owner per physical airframe. No Unity AudioSource or audio callbacks.
internal sealed class ClientDroneAudio : IDisposable
{
    private const int SampleRate = 24000;
    private static short[]? _rotorSamples;
    private readonly int _ownerThread = Environment.CurrentManagedThreadId;
    private IntPtr _systemHandle;
    private Sound _sound;
    private Channel _channel;
    private float _load;
    private float _lastUpdate = -1f;
    private bool _failed;
    private bool _disposed;

    internal string Status { get; private set; } = "Rotor audio is ready.";

    internal void Update(Vector3 position, Vector3 velocity, float normalizedMotorLoad,
        ClientTabletPreferencesSnapshot preferences)
    {
        if (_disposed || _failed || Environment.CurrentManagedThreadId != _ownerThread) return;
        try
        {
            var now = Time.realtimeSinceStartup;
            // Account for time away so a stopped motor cannot replay an old tail on resume.
            var elapsed = _lastUpdate < 0 ? 1f / 60f : Math.Clamp(now - _lastUpdate, 0f, 1f);
            _lastUpdate = now;
            if (!ClientTabletAlerts.TryGetSystem(out var system)) return;
            if (_systemHandle != system.handle)
            {
                // Old resources belong to the replaced system; never dereference stale handles.
                ClearHandles();
                _systemHandle = system.handle;
            }
            var volume = ClientTabletAlerts.DeviceVolume(preferences);
            if (volume <= 0 || !Finite(position) || !Finite(velocity) || !float.IsFinite(normalizedMotorLoad))
            {
                _load = 0;
                StopChannel();
                return;
            }
            var target = Math.Clamp(normalizedMotorLoad, 0f, 1f);
            // Spin down gently, then release the voice instead of leaving an inaudible loop alive.
            var response = target > _load ? 9f : 12f;
            _load += (target - _load) * (1f - MathF.Exp(-response * elapsed));
            if (target <= 0 && _load < .005f)
            {
                _load = 0;
                StopChannel();
                return;
            }
            if (_load <= 0) return;
            if (_channel.handle != IntPtr.Zero)
            {
                var result = _channel.isPlaying(out var playing);
                if (result == RESULT.ERR_INVALID_HANDLE || result == RESULT.ERR_CHANNEL_STOLEN || result == RESULT.OK && !playing)
                    _channel = default;
                else
                    ClientTabletAlerts.Check(result, "Checking rotor voice");
            }
            // Never restart a completed/stolen voice just to play an old spin-down tail.
            if (_channel.handle == IntPtr.Zero && target <= 0) { _load = 0; return; }
            var pos = new VECTOR { x = position.x, y = position.y, z = position.z };
            var vel = new VECTOR { x = velocity.x, y = velocity.y, z = velocity.z };
            if (_channel.handle == IntPtr.Zero)
            {
                if (_sound.handle == IntPtr.Zero)
                    _sound = ClientTabletAlerts.CreatePcmSound(system, _rotorSamples ??= CreateRotorSamples(),
                        MODE._3D | MODE._3D_WORLDRELATIVE | MODE._3D_INVERSETAPEREDROLLOFF | MODE.LOOP_NORMAL);
                ClientTabletAlerts.Check(system.getMasterChannelGroup(out var output), "Finding rotor audio output");
                ClientTabletAlerts.Check(system.playSound(_sound, output, true, out var channel), "Starting rotor loop");
                _channel = channel;
                // Inverse attenuation near the drone, with a tapered end at 70 metres.
                ClientTabletAlerts.Check(_channel.set3DMinMaxDistance(1.5f, 70f), "Setting rotor audio range");
                ClientTabletAlerts.Check(_channel.setVolumeRamp(true), "Smoothing rotor volume");
            }
            ClientTabletAlerts.Check(_channel.set3DAttributes(ref pos, ref vel), "Positioning rotor audio");
            ClientTabletAlerts.Check(_channel.setPitch(.62f + _load * .74f), "Setting rotor speed");
            ClientTabletAlerts.Check(_channel.setVolume(volume * .30f * _load), "Setting rotor volume");
            ClientTabletAlerts.Check(_channel.setPaused(false), "Playing rotor loop");
        }
        catch (Exception error)
        {
            _failed = true;
            Status = "Rotor audio is unavailable: " + error.Message;
            UnityEngine.Debug.LogWarning("Modstoke Client: " + Status);
            // Failure stays local to audio; body simulation and device controls continue.
            try { StopChannel(); } catch { }
        }
    }

    private static bool Finite(Vector3 value) =>
        float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);

    private static short[] CreateRotorSamples()
    {
        var samples = new short[SampleRate];
        for (var i = 0; i < samples.Length; i++)
        {
            var phase = 2 * Math.PI * i / SampleRate;
            // Whole-cycle partials and modulation wrap at one second, including their slopes.
            // Slightly detuned blade harmonics provide air texture without random loop seams.
            var blades = .32 * Math.Sin(phase * 92 + .32 * Math.Sin(phase * 7))
                + .24 * Math.Sin(phase * 188 + .24 * Math.Sin(phase * 11))
                + .13 * Math.Sin(phase * 281) + .08 * Math.Sin(phase * 377);
            var air = .045 * Math.Sin(phase * 733) + .035 * Math.Sin(phase * 941)
                + .025 * Math.Sin(phase * 1237) + .018 * Math.Sin(phase * 1693);
            var sample = (blades + air) * (.8 + .12 * Math.Sin(phase * 23));
            samples[i] = (short)Math.Round(sample * .45 * short.MaxValue);
        }
        return samples;
    }

    private void StopChannel()
    {
        var channel = _channel;
        _channel = default;
        if (channel.handle == IntPtr.Zero) return;
        var result = channel.stop();
        if (result != RESULT.ERR_INVALID_HANDLE && result != RESULT.ERR_CHANNEL_STOLEN)
            ClientTabletAlerts.Check(result, "Stopping rotor audio");
    }

    private void ClearHandles()
    {
        _channel = default;
        _sound = default;
        _systemHandle = IntPtr.Zero;
        _load = 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        if (Environment.CurrentManagedThreadId != _ownerThread)
        {
            Status = "Rotor audio must be released on the Unity main thread.";
            return;
        }
        _disposed = true;
        try
        {
            if (_systemHandle == IntPtr.Zero || !ClientTabletAlerts.TryGetSystem(out var system) || system.handle != _systemHandle)
                return;
            try { StopChannel(); }
            finally
            {
                if (_sound.handle != IntPtr.Zero)
                    ClientTabletAlerts.Check(_sound.release(), "Releasing rotor audio");
            }
        }
        catch (Exception error) { Status = "Rotor audio cleanup: " + error.Message; }
        finally { ClearHandles(); }
    }
}
