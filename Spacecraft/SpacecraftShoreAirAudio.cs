// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.IO;
using FMOD;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Modstoke.Sons;
using UnityEngine;

namespace Modstoke.Client;

// The compressor transform and FMOD resources belong to one Unity main-thread owner.
internal sealed class SpacecraftShoreAirAudio : IDisposable
{
    private const int Motor = 0, Flow = 1;
    private readonly Sound[] _sounds = new Sound[2];
    private readonly Channel[] _channels = new Channel[2];
    private readonly Transform _compressor;
    private readonly int _thread = Environment.CurrentManagedThreadId;
    private IntPtr _systemHandle;
    private bool _failed, _disposed;

    internal SpacecraftShoreAirAudio(Transform compressor) => _compressor = compressor;

    // The caller supplies current coupled readings and source/listener atmosphere attenuation.
    internal void Update(ushort watts, float molesPerSecond, float atmosphereGain,
        ClientTabletPreferencesSnapshot? preferences)
    {
        if (_disposed || _failed || Environment.CurrentManagedThreadId != _thread) return;
        try
        {
            if (!_compressor || !_compressor.gameObject.activeInHierarchy || preferences == null
                || GameRuntime.IsTitleScene || GameRuntime.State.PauseMenuOpen || Time.timeScale <= 0
                || watts == 0 || watts > SpacecraftShoreAir.MaximumWatts
                || !float.IsFinite(molesPerSecond) || molesPerSecond < 0
                || molesPerSecond > SpacecraftShoreAir.MaximumMolesPerSecond
                || !float.IsFinite(atmosphereGain) || atmosphereGain <= 0 || atmosphereGain > 1)
            { Suspend(); return; }
            var volume = ClientTabletAlerts.DeviceVolume(preferences) * atmosphereGain;
            if (volume <= 0) { Suspend(); return; }
            var point = _compressor.TransformPoint(new Vector3(0, .46f, 0));
            if (!float.IsFinite(point.x) || !float.IsFinite(point.y) || !float.IsFinite(point.z))
            { Suspend(); return; }
            if (!ClientTabletAlerts.TryGetSystem(out var system)) { ClearHandles(); return; }
            if (_systemHandle != system.handle)
            {
                // Never dereference resources belonging to a retired FMOD system.
                ClearHandles();
                _systemHandle = system.handle;
            }
            var load = Mathf.Sqrt((float)watts / SpacecraftShoreAir.MaximumWatts);
            var flow = Mathf.Sqrt(molesPerSecond / SpacecraftShoreAir.MaximumMolesPerSecond);
            var duty = Mathf.Min(1, (float)watts / SpacecraftShoreAir.MotorWatts);
            Loop(system, Motor, point, (.15f + .13f * load) * duty * volume, .94f + .10f * load);
            Loop(system, Flow, point, .18f * flow * volume, .96f + .08f * flow);
        }
        catch (Exception error) { Fail(error); }
    }

    private void Loop(FMOD.System system, int index, Vector3 point, float gain, float pitch)
    {
        if (gain <= 0) { Stop(index); return; }
        if (!Playing(index)) Begin(system, index);
        var channel = _channels[index];
        var position = new VECTOR { x = point.x, y = point.y, z = point.z };
        var velocity = default(VECTOR);
        ClientTabletAlerts.Check(channel.set3DAttributes(ref position, ref velocity), "Positioning shore compressor audio");
        ClientTabletAlerts.Check(channel.setPitch(pitch), "Setting shore compressor audio pitch");
        ClientTabletAlerts.Check(channel.setVolume(gain), "Setting shore compressor audio volume");
        ClientTabletAlerts.Check(channel.setPaused(false), "Playing shore compressor audio");
    }

    private void Begin(FMOD.System system, int index)
    {
        if (_sounds[index].handle == IntPtr.Zero) _sounds[index] = LoadSound(system, index);
        ClientTabletAlerts.Check(system.getMasterChannelGroup(out var output), "Finding shore compressor audio output");
        var result = system.playSound(_sounds[index], output, true, out var channel);
        _channels[index] = channel;
        ClientTabletAlerts.Check(result, "Starting shore compressor audio");
        ClientTabletAlerts.Check(channel.set3DMinMaxDistance(1.2f, 18), "Setting shore compressor audio range");
        ClientTabletAlerts.Check(channel.setVolumeRamp(true), "Smoothing shore compressor audio");
    }

    private static Sound LoadSound(FMOD.System system, int index)
    {
        var name = index == Motor ? "motor" : "flow";
        using var stream = typeof(SpacecraftShoreAirAudio).Assembly.GetManifestResourceStream(
            "Modstoke.Client.Assets.spacecraft-shore-" + name + ".wav")
            ?? throw new InvalidDataException("Missing shore compressor " + name + " audio.");
        if (stream.Length is < 44 or > 384044)
            throw new InvalidDataException("Invalid shore compressor " + name + " audio length.");
        var bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        var data = new Il2CppStructArray<byte>(bytes);
        var info = new CREATESOUNDEXINFO
        {
            cbsize = Il2CppSystem.Runtime.InteropServices.Marshal.SizeOf(Il2CppType.Of<CREATESOUNDEXINFO>()),
            length = (uint)bytes.Length
        };
        // OPENMEMORY copies before returning; no managed buffer escapes as a native pointer.
        var result = system.createSound(data, MODE.OPENMEMORY | MODE.CREATESAMPLE | MODE.LOOP_NORMAL
            | MODE._3D | MODE._3D_WORLDRELATIVE | MODE._3D_INVERSETAPEREDROLLOFF, ref info, out var sound);
        GC.KeepAlive(data);
        if (result != RESULT.OK)
        {
            if (sound.handle != IntPtr.Zero) sound.release();
            ClientTabletAlerts.Check(result, "Creating shore compressor " + name + " audio");
        }
        return sound;
    }

    private bool Playing(int index)
    {
        var channel = _channels[index];
        if (channel.handle == IntPtr.Zero) return false;
        var result = channel.isPlaying(out var playing);
        if (result == RESULT.ERR_INVALID_HANDLE || result == RESULT.ERR_CHANNEL_STOLEN || result == RESULT.OK && !playing)
        { _channels[index] = default; return false; }
        ClientTabletAlerts.Check(result, "Checking shore compressor audio");
        return true;
    }

    internal void Suspend()
    {
        if (_disposed || Environment.CurrentManagedThreadId != _thread) return;
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
    }

    private void Stop(int index)
    {
        var channel = _channels[index];
        _channels[index] = default;
        if (channel.handle == IntPtr.Zero) return;
        var result = channel.stop();
        if (result != RESULT.ERR_INVALID_HANDLE && result != RESULT.ERR_CHANNEL_STOLEN)
            ClientTabletAlerts.Check(result, "Stopping shore compressor audio");
    }

    private void Fail(Exception error)
    {
        if (!_failed) UnityEngine.Debug.LogWarning("Modstoke Client: shore compressor audio is unavailable: " + error);
        _failed = true;
        Release();
    }

    private void ClearHandles()
    {
        Array.Clear(_channels);
        Array.Clear(_sounds);
        _systemHandle = IntPtr.Zero;
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
                    ClientTabletAlerts.Check(sound.release(), "Releasing shore compressor audio");
                }
                catch (Exception error) { failure ??= error; }
            }
            if (failure != null) UnityEngine.Debug.LogWarning("Modstoke Client: shore compressor audio cleanup: " + failure);
        }
        catch (Exception error) { UnityEngine.Debug.LogWarning("Modstoke Client: shore compressor audio cleanup: " + error); }
        finally { ClearHandles(); }
    }

    public void Dispose()
    {
        if (_disposed || Environment.CurrentManagedThreadId != _thread) return;
        _disposed = true;
        Release();
    }
}
