// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using FMOD;
using Modstoke.Sons;
using UnityEngine;

namespace Modstoke.Client;

// FMOD resources and the ship transform belong to one Unity main-thread owner.
internal sealed class ClientSpacecraftAudio : IDisposable
{
    private const int SampleRate = 24000;
    private const int Engine = 0, Thrusters = 1, Warp = 2, Door = 3, Decompression = 4;
    private const int Start = 5, Shutdown = 6, Latch = 7, Gear = 8, Transition = 9;
    private const int SoundCount = Transition + 1;
    private static readonly short[]?[] Samples = new short[SoundCount][];
    private readonly Sound[] _sounds = new Sound[SoundCount];
    private readonly Channel[] _channels = new Channel[SoundCount];
    private readonly float[] _gains = new float[SoundCount];
    private readonly Transform _ship;
    private readonly ClientSpacecraftComputerAudio _computer;
    private readonly int _thread = Environment.CurrentManagedThreadId;
    private IntPtr _systemHandle;
    private SpacecraftFlightState _previous;
    private float _load, _decompression, _lastUpdate = -1, _doorMovingUntil;
    private bool _observed, _doorMoving, _failed, _disposed;

    internal ClientSpacecraftAudio(Transform ship)
    {
        _ship = ship;
        _computer = new ClientSpacecraftComputerAudio(ship);
    }

    internal void Update(SpacecraftFlightState state, ClientTabletPreferencesSnapshot? preferences)
    {
        if (_disposed || _failed || Environment.CurrentManagedThreadId != _thread) return;
        try
        {
            if (!_ship || !_ship.gameObject.activeInHierarchy || preferences == null
                || GameRuntime.IsTitleScene || GameRuntime.State.PauseMenuOpen || Time.timeScale <= 0
                || !Finite(state.Velocity) || !Finite(state.AngularVelocity))
            { Suspend(); return; }
            var volume = ClientTabletAlerts.DeviceVolume(preferences);
            if (volume <= 0) { Suspend(); return; }
            if (!ClientTabletAlerts.TryGetSystem(out var system)) { _computer.Suspend(); ClearHandles(); return; }
            if (_systemHandle != system.handle)
            {
                // Never dereference resources belonging to a retired FMOD system.
                ClearHandles();
                _systemHandle = system.handle;
            }
            _computer.Update(state, preferences);
            if (_computer.Speaking) volume *= .45f;
            var now = Time.realtimeSinceStartup;
            var dt = _lastUpdate < 0 ? 1f / 60 : Math.Clamp(now - _lastUpdate, .001f, .1f);
            if (_lastUpdate >= 0 && now - _lastUpdate > .5f) _observed = false;
            _lastUpdate = now;
            var travel = state.Travel != SpacecraftTravel.None;
            var powered = state.Powered || travel;
            var acceleration = _observed ? (state.Velocity - _previous.Velocity).magnitude / dt : 0;
            var demand = powered ? travel ? 1f : Mathf.Clamp01(acceleration / 18
                + state.AngularVelocity.magnitude * .25f + (!state.Grounded ? .12f : 0)) : 0;
            _load = Mathf.MoveTowards(_load, demand, dt * 2.5f);
            var progress = float.IsFinite(state.TravelProgress) ? Mathf.Clamp01(state.TravelProgress) : 0;
            var outflow = ClientSpacecraftSystems.Outflow(state);
            _decompression = outflow > .05f
                ? Mathf.MoveTowards(_decompression, Mathf.Sqrt(Mathf.Clamp01(outflow / 80)), dt * 6) : 0;

            if (_observed)
            {
                if (state.Powered != _previous.Powered)
                {
                    Stop(state.Powered ? Shutdown : Start);
                    Cue(system, state.Powered ? Start : Shutdown, state, .43f, 1, volume);
                }
                if (state.GearDeployed != _previous.GearDeployed)
                    Cue(system, Gear, state, .36f, state.GearDeployed ? .85f : 1.1f, volume);
                if (travel != (_previous.Travel != SpacecraftTravel.None))
                    Cue(system, Transition, state, .55f, travel ? .75f : 1.2f, volume);
                if (Mathf.Abs(state.DoorAmount - _previous.DoorAmount) > .0001f)
                    _doorMovingUntil = now + .18f;
            }
            var moving = now < _doorMovingUntil;
            if (_observed && _doorMoving && !moving)
                Cue(system, Latch, state, .26f, state.DoorOpen ? .85f : 1.05f, volume);
            _doorMoving = moving;

            Loop(system, Engine, state, powered ? .20f + _load * .24f : 0, .72f + _load * .38f, volume);
            Loop(system, Thrusters, state, powered ? _load * .38f : 0, .8f + _load * .5f, volume);
            Loop(system, Warp, state, travel ? .18f + progress * .32f : 0, .65f + progress * 1.1f, volume);
            Loop(system, Door, state, moving ? .23f : 0, state.DoorOpen ? .86f : 1, volume);
            Loop(system, Decompression, state, _decompression * .55f, .9f + _decompression * .18f, volume);
            // Moving one-shots track the ship and respond immediately to volume changes.
            for (var i = Start; i < _channels.Length; i++)
            {
                if (!Playing(i)) continue;
                Position(_channels[i], i, state);
                ClientTabletAlerts.Check(_channels[i].setVolume(_gains[i] * volume), "Adjusting spacecraft cue volume");
            }
            _previous = state;
            _observed = true;
        }
        catch (Exception error) { Fail(error); }
    }

    private void Loop(FMOD.System system, int index, SpacecraftFlightState state, float gain, float pitch, float volume)
    {
        if (gain <= .001f) { Stop(index); return; }
        if (!Playing(index)) Begin(system, index, true);
        Configure(index, state, gain, pitch, volume);
    }

    private void Cue(FMOD.System system, int index, SpacecraftFlightState state, float gain, float pitch, float volume)
    {
        Stop(index);
        Begin(system, index, false);
        Configure(index, state, gain, pitch, volume);
    }

    private void Begin(FMOD.System system, int index, bool loop)
    {
        if (_sounds[index].handle == IntPtr.Zero)
            _sounds[index] = ClientTabletAlerts.CreatePcmSound(system, Samples[index] ??= CreateSamples(index),
                MODE._3D | MODE._3D_WORLDRELATIVE | MODE._3D_INVERSETAPEREDROLLOFF
                | (loop ? MODE.LOOP_NORMAL : MODE.LOOP_OFF));
        ClientTabletAlerts.Check(system.getMasterChannelGroup(out var output), "Finding spacecraft audio output");
        ClientTabletAlerts.Check(system.playSound(_sounds[index], output, true, out var channel), "Starting spacecraft audio");
        _channels[index] = channel;
        var mechanical = index is Door or Latch or Gear or Decompression;
        ClientTabletAlerts.Check(channel.set3DMinMaxDistance(mechanical ? 3 : 9,
            index == Decompression ? 35 : mechanical ? 55 : index is Warp or Transition ? 350 : 180), "Setting spacecraft audio range");
        ClientTabletAlerts.Check(channel.setVolumeRamp(true), "Smoothing spacecraft audio");
    }

    private void Configure(int index, SpacecraftFlightState state, float gain, float pitch, float volume)
    {
        var channel = _channels[index];
        _gains[index] = gain;
        Position(channel, index, state);
        ClientTabletAlerts.Check(channel.setPitch(pitch), "Setting spacecraft audio pitch");
        ClientTabletAlerts.Check(channel.setVolume(gain * volume), "Setting spacecraft audio volume");
        ClientTabletAlerts.Check(channel.setPaused(false), "Playing spacecraft audio");
    }

    private void Position(Channel channel, int index, SpacecraftFlightState state)
    {
        var local = index == Decompression && state.DoorAmount <= .01f ? ClientSpacecraftEffects.VentOutlet
            : index is Door or Latch or Decompression ? ClientSpacecraftEffects.HatchOutlet
            : index == Gear ? new Vector3(0, -3, -2) : new Vector3(0, 1.5f, -12);
        var point = _ship.TransformPoint(local);
        var velocity = Vector3.ClampMagnitude(state.Velocity, 100);
        var pos = new VECTOR { x = point.x, y = point.y, z = point.z };
        var vel = new VECTOR { x = velocity.x, y = velocity.y, z = velocity.z };
        ClientTabletAlerts.Check(channel.set3DAttributes(ref pos, ref vel), "Positioning spacecraft audio");
    }

    private bool Playing(int index)
    {
        var channel = _channels[index];
        if (channel.handle == IntPtr.Zero) return false;
        var result = channel.isPlaying(out var playing);
        if (result == RESULT.ERR_INVALID_HANDLE || result == RESULT.ERR_CHANNEL_STOLEN || result == RESULT.OK && !playing)
        { _channels[index] = default; return false; }
        ClientTabletAlerts.Check(result, "Checking spacecraft audio");
        return true;
    }

    internal void Suspend()
    {
        if (_disposed || Environment.CurrentManagedThreadId != _thread) return;
        _computer.Suspend();
        try
        {
            if (_systemHandle != IntPtr.Zero && ClientTabletAlerts.TryGetSystem(out var system) && system.handle == _systemHandle)
            {
                Exception? failure = null;
                for (var i = 0; i < _channels.Length; i++)
                    try { Stop(i); } catch (Exception error) { failure ??= error; }
                if (failure != null) Fail(failure);
            }
            else ClearHandles();
        }
        catch (Exception error) { Fail(error); }
        _observed = _doorMoving = false;
        _load = _decompression = _doorMovingUntil = 0;
        _lastUpdate = -1;
    }

    private void Stop(int index)
    {
        var channel = _channels[index];
        _channels[index] = default;
        _gains[index] = 0;
        if (channel.handle == IntPtr.Zero) return;
        var result = channel.stop();
        if (result != RESULT.ERR_INVALID_HANDLE && result != RESULT.ERR_CHANNEL_STOLEN)
            ClientTabletAlerts.Check(result, "Stopping spacecraft audio");
    }

    private void Fail(Exception error)
    {
        if (!_failed) UnityEngine.Debug.LogWarning("Modstoke Client: spacecraft audio is unavailable: " + error);
        _failed = true;
        _computer.Suspend();
        Release();
    }

    private void ClearHandles()
    {
        Array.Clear(_channels);
        Array.Clear(_sounds);
        Array.Clear(_gains);
        _systemHandle = IntPtr.Zero;
        _observed = _doorMoving = false;
        _load = _decompression = _doorMovingUntil = 0;
        _lastUpdate = -1;
    }

    private void Release()
    {
        try
        {
            if (_systemHandle == IntPtr.Zero || !ClientTabletAlerts.TryGetSystem(out var system) || system.handle != _systemHandle)
                return;
            Exception? failure = null;
            for (var i = 0; i < _channels.Length; i++)
            {
                try { Stop(i); } catch (Exception error) { failure ??= error; }
                try
                {
                    if (_sounds[i].handle == IntPtr.Zero) continue;
                    var sound = _sounds[i];
                    _sounds[i] = default;
                    ClientTabletAlerts.Check(sound.release(), "Releasing spacecraft audio");
                }
                catch (Exception error) { failure ??= error; }
            }
            if (failure != null) UnityEngine.Debug.LogWarning("Modstoke Client: spacecraft audio cleanup: " + failure);
        }
        catch (Exception error) { UnityEngine.Debug.LogWarning("Modstoke Client: spacecraft audio cleanup: " + error); }
        finally { ClearHandles(); }
    }

    private static bool Finite(Vector3 value) => float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);

    private static short[] CreateSamples(int kind)
    {
        var duration = kind == Decompression ? 2 : kind is Start or Shutdown or Gear ? 1.25
            : kind == Latch ? .35 : kind == Transition ? 1.5 : 1;
        var samples = new short[(int)(SampleRate * duration)];
        if (kind == Decompression)
        {
            const int overlap = SampleRate / 50;
            var noise = new double[samples.Length + overlap];
            var random = new System.Random(7141);
            double filtered = 0, rumble = 0;
            for (var i = -256; i < noise.Length; i++)
            {
                filtered += (random.NextDouble() * 2 - 1 - filtered) * .68;
                rumble += (filtered - rumble) * .08;
                if (i >= 0) noise[i] = filtered - rumble;
            }
            for (var i = 0; i < samples.Length; i++)
            {
                var value = noise[i];
                if (i < overlap)
                {
                    // Blend the continuation into the start so the loop has no seam.
                    var blend = (double)i / overlap * Math.PI * .5;
                    value = noise[samples.Length + i] * Math.Cos(blend) + value * Math.Sin(blend);
                }
                samples[i] = (short)Math.Round(Math.Clamp(value * .65, -1, 1) * short.MaxValue);
            }
            return samples;
        }
        for (var i = 0; i < samples.Length; i++)
        {
            var t = (double)i / SampleRate;
            var phase = 2 * Math.PI * t;
            double value;
            if (kind == Engine)
                value = .3 * Math.Sin(phase * 48) + .18 * Math.Sin(phase * 96 + .18 * Math.Sin(phase * 3))
                    + .11 * Math.Sin(phase * 147) + .06 * Math.Sin(phase * 293);
            else if (kind == Thrusters)
            {
                value = 0;
                // Integer partials close at one second without a discontinuous noise seam.
                for (var partial = 1; partial <= 32; partial++)
                    value += Math.Sin(phase * (57 + partial * 81 + partial * partial) + partial * 2.39996)
                        * .045 / (1 + partial * .05);
                value *= .8 + .14 * Math.Sin(phase * 7);
            }
            else if (kind == Warp)
                value = .24 * Math.Sin(phase * 62 + .9 * Math.Sin(phase * 2))
                    + .16 * Math.Sin(phase * 127) + .10 * Math.Sin(phase * 251 + .4 * Math.Sin(phase * 5))
                    + .04 * Math.Sin(phase * 503);
            else if (kind == Door)
                value = .18 * Math.Sin(phase * 117) + .12 * Math.Sin(phase * 234)
                    + .06 * Math.Sin(phase * 467 + .3 * Math.Sin(phase * 11));
            else if (kind is Start or Shutdown)
            {
                var sweep = kind == Start ? 45 * t + 85 * t * t : 230 * t - 65 * t * t;
                value = (.3 * Math.Sin(2 * Math.PI * sweep) + .13 * Math.Sin(4 * Math.PI * sweep)
                    + .08 * Math.Sin(phase * 63)) * Math.Pow(Math.Sin(Math.PI * t / duration), 1.2);
            }
            else if (kind == Gear)
                value = (.20 * Math.Sin(phase * 89) + .10 * Math.Sin(phase * 178)
                    + .04 * Math.Sin(phase * 713)) * Math.Sin(Math.PI * t / duration)
                    + .23 * Math.Sin(phase * 61) * Math.Exp(-30 * Math.Abs(t - 1.03));
            else if (kind == Latch)
                value = (.48 * Math.Sin(phase * 71) + .21 * Math.Sin(phase * 151)
                    + .10 * Math.Sin(phase * 937)) * Math.Exp(-20 * t);
            else
                value = (.28 * Math.Sin(2 * Math.PI * (95 * t + 170 * t * t))
                    + .16 * Math.Sin(phase * 51) + .08 * Math.Sin(phase * 607))
                    * Math.Pow(Math.Sin(Math.PI * t / duration), 2);
            if (kind >= Start) value *= Math.Min(1, t / .012) * Math.Min(1, (duration - t) / .05);
            samples[i] = (short)Math.Round(Math.Clamp(value * .65, -1, 1) * short.MaxValue);
        }
        return samples;
    }

    public void Dispose()
    {
        if (_disposed || Environment.CurrentManagedThreadId != _thread) return;
        _disposed = true;
        _computer.Dispose();
        Release();
    }
}
