// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using FMOD;
using UnityEngine;

namespace Modstoke.Client;

// Original synthesized piston, slipstream and warning sounds, owned on the main thread.
internal sealed class ClientAircraftAudio : IDisposable
{
    private const int SampleRate = 24000;
    private static readonly short[]?[] Samples = new short[4][];
    private readonly Sound[] _sounds = new Sound[4];
    private readonly Channel[] _channels = new Channel[4];
    private readonly int _ownerThread = Environment.CurrentManagedThreadId;
    private IntPtr _systemHandle;
    private float _volume;
    private float _lastImpact = -1;
    private float _previousVerticalSpeed;
    private bool _previousGrounded;
    private bool _observed;
    private bool _failed;
    private bool _disposed;

    internal void Update(AircraftFlightState state, ClientTabletPreferencesSnapshot preferences)
    {
        if (_disposed || _failed || Environment.CurrentManagedThreadId != _ownerThread) return;
        try
        {
            if (!GetSystem(out var system)) return;
            _volume = ClientTabletAlerts.DeviceVolume(preferences);
            var speed = (state.Velocity - state.WindVelocity).magnitude;
            var engineVolume = state.Engine ? .30f + .60f * state.Throttle : 0;
            Loop(system, 0, state, engineVolume * _volume * .72f, Math.Clamp(state.Rpm / 1800, .40f, 1.65f), 3, 260);
            Loop(system, 1, state, Math.Clamp((speed - 12) / 55, 0, 1) * _volume * .28f,
                .75f + Math.Clamp(speed / 110, 0, .65f), 2, 32);
            Loop(system, 2, state, state.Stall && !state.Grounded ? _volume * .15f : 0, 1, 1, 12);
            if (_observed && state.Grounded && !_previousGrounded && _previousVerticalSpeed < -1)
                Impact(state, Math.Clamp(-_previousVerticalSpeed / 8, .12f, .7f));
            _previousGrounded = state.Grounded;
            _previousVerticalSpeed = state.Velocity.y;
            _observed = true;
        }
        catch (Exception error) { Fail(error); }
    }

    private void Loop(FMOD.System system, int index, AircraftFlightState state, float volume, float pitch,
        float minimumDistance, float maximumDistance)
    {
        if (volume <= .001f) { Stop(index); return; }
        var channel = _channels[index];
        if (channel.handle != IntPtr.Zero)
        {
            var result = channel.isPlaying(out var playing);
            if (result == RESULT.ERR_INVALID_HANDLE || result == RESULT.ERR_CHANNEL_STOLEN || result == RESULT.OK && !playing)
                _channels[index] = channel = default;
            else ClientTabletAlerts.Check(result, "Checking aircraft audio");
        }
        if (channel.handle == IntPtr.Zero)
        {
            EnsureSound(system, index, true);
            ClientTabletAlerts.Check(system.getMasterChannelGroup(out var output), "Finding aircraft audio output");
            ClientTabletAlerts.Check(system.playSound(_sounds[index], output, true, out channel), "Starting aircraft audio");
            _channels[index] = channel;
            ClientTabletAlerts.Check(channel.set3DMinMaxDistance(minimumDistance, maximumDistance), "Setting aircraft audio range");
            ClientTabletAlerts.Check(channel.setVolumeRamp(true), "Smoothing aircraft audio");
        }
        Position(channel, state);
        ClientTabletAlerts.Check(channel.setPitch(pitch), "Setting aircraft audio pitch");
        ClientTabletAlerts.Check(channel.setVolume(volume), "Setting aircraft audio volume");
        ClientTabletAlerts.Check(channel.setPaused(false), "Playing aircraft audio");
    }

    internal void Impact(AircraftFlightState state, float strength)
    {
        if (_disposed || _failed || _volume <= 0 || Environment.CurrentManagedThreadId != _ownerThread) return;
        if (Time.realtimeSinceStartup - _lastImpact < .15f) return;
        try
        {
            if (!GetSystem(out var system)) return;
            _lastImpact = Time.realtimeSinceStartup;
            EnsureSound(system, 3, false);
            Stop(3);
            ClientTabletAlerts.Check(system.getMasterChannelGroup(out var output), "Finding aircraft impact output");
            ClientTabletAlerts.Check(system.playSound(_sounds[3], output, true, out var channel), "Starting aircraft impact");
            _channels[3] = channel;
            Position(channel, state);
            ClientTabletAlerts.Check(channel.set3DMinMaxDistance(2, 75), "Setting aircraft impact range");
            ClientTabletAlerts.Check(channel.setVolume(_volume * Mathf.Clamp01(strength) * .55f), "Setting aircraft impact volume");
            ClientTabletAlerts.Check(channel.setPaused(false), "Playing aircraft impact");
        }
        catch (Exception error) { Fail(error); }
    }

    private bool GetSystem(out FMOD.System system)
    {
        if (!ClientTabletAlerts.TryGetSystem(out system)) return false;
        if (_systemHandle != system.handle)
        {
            // Handles from a retired FMOD system must not be dereferenced.
            ClearHandles();
            _systemHandle = system.handle;
        }
        return true;
    }

    private void EnsureSound(FMOD.System system, int index, bool loop)
    {
        if (_sounds[index].handle != IntPtr.Zero) return;
        _sounds[index] = ClientTabletAlerts.CreatePcmSound(system, Samples[index] ??= CreateSamples(index),
            MODE._3D | MODE._3D_WORLDRELATIVE | MODE._3D_INVERSETAPEREDROLLOFF | (loop ? MODE.LOOP_NORMAL : MODE.LOOP_OFF));
    }

    private static void Position(Channel channel, AircraftFlightState state)
    {
        var position = new VECTOR { x = state.Position.x, y = state.Position.y, z = state.Position.z };
        var velocity = new VECTOR { x = state.Velocity.x, y = state.Velocity.y, z = state.Velocity.z };
        ClientTabletAlerts.Check(channel.set3DAttributes(ref position, ref velocity), "Positioning aircraft audio");
    }

    private static short[] CreateSamples(int kind)
    {
        var samples = new short[kind == 3 ? SampleRate / 2 : SampleRate];
        for (var i = 0; i < samples.Length; i++)
        {
            var t = (double)i / SampleRate;
            var phase = 2 * Math.PI * t;
            double value;
            if (kind == 0)
            {
                // A four-cylinder four-stroke at 1800 rpm has 60 firing pulses/s.
                // Integer partials keep the one-second loop continuous.
                var firing = .34 * Math.Sin(phase * 60) + .22 * Math.Sin(phase * 120 + .35)
                    + .12 * Math.Sin(phase * 180 + .8) + .065 * Math.Sin(phase * 240 + 1.1);
                var crank = .11 * Math.Sin(phase * 30) + .045 * Math.Sin(phase * 90 + .3);
                var exhaust = .035 * Math.Sin(phase * 421 + .8 * Math.Sin(phase * 60))
                    + .022 * Math.Sin(phase * 733) + .018 * Math.Sin(phase * 1187);
                value = (firing + crank) * (.89 + .07 * Math.Sin(phase * 7)) + exhaust;
            }
            else if (kind == 1)
            {
                value = 0;
                for (var partial = 1; partial <= 36; partial++)
                    value += Math.Sin(phase * (83 + partial * 97 + partial * partial) + partial * 2.39996)
                        * .045 / (1 + partial * .025);
                value *= .8 + .15 * Math.Sin(phase * 3);
            }
            else if (kind == 2)
            {
                var gate = Math.Pow(.5 + .5 * Math.Sin(phase * 3), 5);
                value = gate * (.45 * Math.Sin(phase * 920) + .12 * Math.Sin(phase * 1840));
            }
            else
            {
                var body = Math.Sin(phase * 73) * Math.Exp(-17 * t)
                    + .35 * Math.Sin(phase * 137) * Math.Exp(-12 * t);
                var rattle = (.3 * Math.Sin(phase * 967) + .2 * Math.Sin(phase * 1723)) * Math.Exp(-28 * t);
                value = (body + rattle) * Math.Min(1, t / .003) * Math.Min(1, (.5 - t) / .025) * .65;
            }
            samples[i] = (short)Math.Round(Math.Clamp(value * .60, -1d, 1d) * short.MaxValue);
        }
        return samples;
    }

    private void Stop(int index)
    {
        var channel = _channels[index];
        _channels[index] = default;
        if (channel.handle == IntPtr.Zero) return;
        var result = channel.stop();
        if (result != RESULT.ERR_INVALID_HANDLE && result != RESULT.ERR_CHANNEL_STOLEN)
            ClientTabletAlerts.Check(result, "Stopping aircraft audio");
    }

    private void Fail(Exception error)
    {
        _failed = true;
        UnityEngine.Debug.LogWarning("Modstoke Client: aircraft audio is unavailable: " + error.Message);
        for (var i = 0; i < _channels.Length; i++)
            try { Stop(i); } catch { }
    }

    private void ClearHandles()
    {
        Array.Clear(_channels);
        Array.Clear(_sounds);
        _systemHandle = IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed || Environment.CurrentManagedThreadId != _ownerThread) return;
        _disposed = true;
        try
        {
            if (_systemHandle == IntPtr.Zero || !ClientTabletAlerts.TryGetSystem(out var system) || system.handle != _systemHandle)
                return;
            Exception? failure = null;
            for (var i = 0; i < _channels.Length; i++)
            {
                try { Stop(i); }
                catch (Exception error) { failure ??= error; }
                try
                {
                    if (_sounds[i].handle != IntPtr.Zero)
                    {
                        var sound = _sounds[i];
                        _sounds[i] = default;
                        ClientTabletAlerts.Check(sound.release(), "Releasing aircraft audio");
                    }
                }
                catch (Exception error) { failure ??= error; }
            }
            if (failure != null) UnityEngine.Debug.LogWarning("Modstoke Client: aircraft audio cleanup: " + failure.Message);
        }
        catch (Exception error) { UnityEngine.Debug.LogWarning("Modstoke Client: aircraft audio cleanup: " + error.Message); }
        finally { ClearHandles(); }
    }
}
