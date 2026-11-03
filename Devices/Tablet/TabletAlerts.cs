// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.IO;
using FMOD;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using SonsSdk;
using UnityEngine;

namespace Modstoke.Client;

internal enum ClientDeviceCue { Touch, SolarOpen, SolarClose, BatteryLatch, Arm, Disarm }

// Construct and use on the Unity main thread. No Unity objects enter the tablet OS worker.
internal sealed class ClientTabletAlerts : IDisposable
{
    private const int SampleRate = 24000;
    private readonly int _ownerThread = Environment.CurrentManagedThreadId;
    private readonly Sound[] _sounds = new Sound[3];
    private static readonly short[]?[] DeviceSamples = new short[6][];
    private readonly Sound[] _deviceSounds = new Sound[6];
    private readonly Channel[] _deviceChannels = new Channel[2];
    private readonly float[] _nextDeviceCue = new float[6];
    private IntPtr _systemHandle;
    private Channel _channel;
    private float _nextNotification;
    private float _nextPreview;
    private bool _failed;
    private bool _deviceFailed;
    private bool _disposed;

    internal string Status { get; private set; } = "Notification tones are ready.";

    internal bool PlayDeviceCue(ClientTabletPreferencesSnapshot preferences, ClientDeviceCue cue)
    {
        if (_disposed || _deviceFailed || Environment.CurrentManagedThreadId != _ownerThread || !Enum.IsDefined(cue))
            return false;
        try
        {
            var volume = DeviceVolume(preferences);
            var index = (int)cue;
            var now = Time.realtimeSinceStartup;
            if (volume <= 0 || now < _nextDeviceCue[index] || !TryGetSystem(out var system)) return false;
            if (_systemHandle != system.handle)
            {
                ClearHandles();
                _systemHandle = system.handle;
            }
            if (_deviceSounds[index].handle == IntPtr.Zero)
                _deviceSounds[index] = CreatePcmSound(system, DeviceSamples[index] ??= CreateDeviceSamples(cue),
                    MODE._2D | MODE.LOOP_OFF);
            // Touch feedback cannot interrupt a latch, and neither voice interrupts notifications.
            var voice = cue == ClientDeviceCue.Touch ? 0 : 1;
            StopDeviceChannel(voice);
            Check(system.getMasterChannelGroup(out var output), "Finding device audio output");
            Check(system.playSound(_deviceSounds[index], output, true, out var channel), "Starting device cue");
            try
            {
                Check(channel.setVolume(volume * (cue == ClientDeviceCue.Touch ? .30f : .55f)), "Setting device volume");
                Check(channel.setPaused(false), "Playing device cue");
                _deviceChannels[voice] = channel;
            }
            catch
            {
                channel.stop();
                throw;
            }
            _nextDeviceCue[index] = now + (cue == ClientDeviceCue.Touch ? .06f : .18f);
            return true;
        }
        catch (Exception error)
        {
            _deviceFailed = true;
            Status = "Device audio is unavailable: " + error.Message;
            return false;
        }
    }

    internal static float DeviceVolume(ClientTabletPreferencesSnapshot preferences)
    {
        var volume = preferences.EffectiveVolume;
        var gameVolume = SoundTools.SfxVolume;
        return float.IsFinite(volume) && float.IsFinite(gameVolume)
            ? Math.Clamp(volume, 0f, 1f) * Math.Clamp(gameVolume, 0f, 1f) : 0f;
    }

    internal void UpdateDeviceVolume(ClientTabletPreferencesSnapshot preferences)
    {
        if (_disposed || _deviceFailed || Environment.CurrentManagedThreadId != _ownerThread
            || _deviceChannels[0].handle == IntPtr.Zero && _deviceChannels[1].handle == IntPtr.Zero) return;
        try
        {
            if (!TryGetSystem(out var system)) return;
            if (_systemHandle != system.handle) { ClearHandles(); _systemHandle = system.handle; return; }
            var volume = DeviceVolume(preferences);
            for (var i = 0; i < _deviceChannels.Length; i++)
            {
                if (_deviceChannels[i].handle == IntPtr.Zero) continue;
                if (volume <= 0) { StopDeviceChannel(i); continue; }
                var result = _deviceChannels[i].setVolume(volume * (i == 0 ? .30f : .55f));
                if (result == RESULT.ERR_INVALID_HANDLE || result == RESULT.ERR_CHANNEL_STOLEN)
                    _deviceChannels[i] = default;
                else Check(result, "Updating device volume");
            }
        }
        catch (Exception error)
        {
            _deviceFailed = true;
            Status = "Device audio is unavailable: " + error.Message;
            for (var i = 0; i < _deviceChannels.Length; i++)
                try { StopDeviceChannel(i); } catch { }
        }
    }

    internal bool Play(ClientTabletPreferencesSnapshot preferences, string? channel = null)
    {
        if (!Ready() || !preferences.Notifications || preferences.EffectiveVolume <= 0) return false;
        var channelEnabled = channel switch
        {
            "expedition" => preferences.ExpeditionAlerts,
            "tribe" => preferences.TribeAlerts,
            "direct" => preferences.DirectAlerts,
            _ => true
        };
        if (!channelEnabled) return false;
        if (Time.realtimeSinceStartup < _nextNotification) return false;
        return PlayTone(preferences, false);
    }

    // Explicit previews use the manual volume even when notifications or Silent mode mute alerts.
    internal bool Preview(ClientTabletPreferencesSnapshot preferences)
    {
        if (!Ready()) return false;
        if (preferences.Volume <= 0) { Status = "Notification volume is muted."; return false; }
        if (Time.realtimeSinceStartup < _nextPreview) return false;
        return PlayTone(preferences, true);
    }

    private bool Ready()
    {
        if (Environment.CurrentManagedThreadId != _ownerThread)
        {
            Status = "Notification audio requires the Unity main thread.";
            return false;
        }
        return !_disposed && !_failed;
    }

    private bool PlayTone(ClientTabletPreferencesSnapshot preferences, bool preview)
    {
        try
        {
            if (!Enum.IsDefined(preferences.Tone) || !float.IsFinite(preferences.Volume))
            {
                Status = "The notification tone or volume is invalid.";
                return false;
            }
            var volume = Math.Clamp(preview ? preferences.Volume : preferences.EffectiveVolume, 0f, 1f) * SoundTools.SfxVolume;
            if (!float.IsFinite(volume) || volume <= 0)
            {
                Status = "Game master or sound-effects volume is muted.";
                return false;
            }
            if (!TryGetSystem(out var system))
            {
                Status = "Game audio is not ready.";
                return false;
            }
            if (_systemHandle != system.handle)
            {
                // A replaced FMOD system owns the old resources; never release its stale handles.
                ClearHandles();
                _systemHandle = system.handle;
            }
            var index = (int)preferences.Tone;
            if (_sounds[index].handle == IntPtr.Zero)
                _sounds[index] = CreateTone(system, preferences.Tone);
            StopChannel();
            Check(system.getMasterChannelGroup(out var output), "Finding game audio output");
            Check(system.playSound(_sounds[index], output, true, out var channel), "Starting notification tone");
            try
            {
                Check(channel.setVolume(Math.Clamp(volume, 0f, 1f)), "Setting notification volume");
                Check(channel.setPaused(false), "Playing notification tone");
                _channel = channel;
            }
            catch
            {
                channel.stop();
                throw;
            }
            var now = Time.realtimeSinceStartup;
            _nextNotification = now + 1f;
            _nextPreview = now + .3f;
            Status = preview ? "Notification tone preview requested." : "Notification tone requested.";
            return true;
        }
        catch (Exception error)
        {
            _failed = true;
            Status = "Notification audio is unavailable: " + error.Message;
            return false;
        }
    }

    internal static bool TryGetSystem(out FMOD.System system)
    {
        system = default;
        if (!FMOD_StudioSystem.TryGetInstance(out var host, false) || host == null || !host.isInitialized)
            return false;
        var studio = host.System;
        return studio.isValid() && studio.getCoreSystem(out system) == RESULT.OK && system.handle != IntPtr.Zero;
    }

    private static Sound CreateTone(FMOD.System system, ClientTabletTone tone)
    {
        var duration = tone switch { ClientTabletTone.Chime => .48, ClientTabletTone.Radio => .20, _ => .30 };
        var length = (int)(duration * SampleRate);
        // The game disables Unity audio. Give its FMOD mixer a complete, copied PCM sample.
        using var wave = new MemoryStream(44 + length * sizeof(short));
        using var writer = new BinaryWriter(wave);
        writer.Write("RIFF"u8);
        writer.Write(36 + length * sizeof(short));
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1); // PCM
        writer.Write((short)1); // Mono
        writer.Write(SampleRate);
        writer.Write(SampleRate * sizeof(short));
        writer.Write((short)sizeof(short));
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(length * sizeof(short));
        for (var i = 0; i < length; i++)
        {
            var time = (double)i / SampleRate;
            // Original, quietly normalized sine envelopes with soft attacks and releases.
            var sample = tone switch
            {
                ClientTabletTone.Chime => Note(time, 660, .46, 7) * .55 + Note(time - .07, 990, .39, 9) * .35,
                ClientTabletTone.Radio => Note(time, 1250, .065, 8) * .55 + Note(time - .10, 1600, .09, 12) * .45,
                _ => Note(time, 740, .11, 5) * .6 + Note(time - .15, 880, .14, 7) * .6
            };
            writer.Write((short)Math.Round(Math.Clamp(sample * .45, -1d, 1d) * short.MaxValue));
        }
        var data = new Il2CppStructArray<byte>(wave.ToArray());
        var info = new CREATESOUNDEXINFO
        {
            cbsize = Il2CppSystem.Runtime.InteropServices.Marshal.SizeOf(Il2CppType.Of<CREATESOUNDEXINFO>()),
            length = (uint)wave.Length
        };
        // OPENMEMORY copies synchronously; no retained sample pointer or audio-thread callback.
        var result = system.createSound(data, MODE.OPENMEMORY | MODE.CREATESAMPLE | MODE._2D | MODE.LOOP_OFF,
            ref info, out var sound);
        GC.KeepAlive(data);
        if (result != RESULT.OK)
        {
            if (sound.handle != IntPtr.Zero) sound.release();
            Check(result, "Creating notification tone");
        }
        return sound;
    }

    private static double Note(double time, double frequency, double duration, double decay)
    {
        if (time <= 0 || time >= duration) return 0;
        var envelope = Math.Min(1, time / .008) * Math.Min(1, (duration - time) / .018) * Math.Exp(-decay * time);
        return Math.Sin(2 * Math.PI * frequency * time) * envelope;
    }

    private static short[] CreateDeviceSamples(ClientDeviceCue cue)
    {
        var duration = cue switch
        {
            ClientDeviceCue.Touch => .024,
            ClientDeviceCue.SolarOpen => (double)ClientDroneModel.SolarMotionSeconds,
            ClientDeviceCue.SolarClose => (double)ClientDroneModel.SolarMotionSeconds,
            ClientDeviceCue.BatteryLatch => .10,
            _ => .14
        };
        var samples = new short[(int)(duration * SampleRate)];
        uint noise = 0x48A53E19;
        for (var i = 0; i < samples.Length; i++)
        {
            var time = (double)i / SampleRate;
            noise = unchecked(noise * 1664525 + 1013904223);
            var grit = ((noise >> 8) / 8388607.5 - 1) * .25;
            var progress = time / duration;
            var envelope = Math.Min(1, time / .002) * Math.Min(1, (duration - time) / .009);
            var tick = (Math.Sin(2 * Math.PI * 1800 * time) * .35 + grit) * Math.Exp(-180 * time);
            var sample = cue switch
            {
                ClientDeviceCue.Touch => tick,
                ClientDeviceCue.SolarOpen => .10 * Math.Sin(2 * Math.PI * (260 * time + 24 * time * time))
                    * Math.Sin(Math.PI * progress) + tick,
                ClientDeviceCue.SolarClose => .10 * Math.Sin(2 * Math.PI * (320 * time - 24 * time * time))
                    * Math.Sin(Math.PI * progress) + (grit + .3 * Math.Sin(2 * Math.PI * 760 * time)) * Math.Exp(-100 * Math.Abs(time - duration + .04)),
                ClientDeviceCue.BatteryLatch => tick + (grit + .35 * Math.Sin(2 * Math.PI * 580 * time)) * Math.Exp(-110 * Math.Abs(time - .04)),
                ClientDeviceCue.Arm => tick + .2 * Math.Sin(2 * Math.PI * (390 * time + 650 * time * time)) * Math.Sin(Math.PI * progress),
                _ => tick + .2 * Math.Sin(2 * Math.PI * (570 * time - 650 * time * time)) * Math.Sin(Math.PI * progress)
            };
            samples[i] = (short)Math.Round(Math.Clamp(sample * envelope * .55, -1d, 1d) * short.MaxValue);
        }
        return samples;
    }

    internal static Sound CreatePcmSound(FMOD.System system, short[] samples, MODE mode)
    {
        using var wave = new MemoryStream(44 + samples.Length * sizeof(short));
        using var writer = new BinaryWriter(wave);
        writer.Write("RIFF"u8);
        writer.Write(36 + samples.Length * sizeof(short));
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(SampleRate);
        writer.Write(SampleRate * sizeof(short));
        writer.Write((short)sizeof(short));
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(samples.Length * sizeof(short));
        foreach (var sample in samples) writer.Write(sample);
        var data = new Il2CppStructArray<byte>(wave.ToArray());
        var info = new CREATESOUNDEXINFO
        {
            cbsize = Il2CppSystem.Runtime.InteropServices.Marshal.SizeOf(Il2CppType.Of<CREATESOUNDEXINFO>()),
            length = (uint)wave.Length
        };
        // OPENMEMORY copies before returning; the managed sample never escapes as a pointer.
        var result = system.createSound(data, MODE.OPENMEMORY | MODE.CREATESAMPLE | mode, ref info, out var sound);
        GC.KeepAlive(data);
        if (result != RESULT.OK)
        {
            if (sound.handle != IntPtr.Zero) sound.release();
            Check(result, "Creating device audio");
        }
        return sound;
    }

    private void StopDeviceChannel(int index)
    {
        var channel = _deviceChannels[index];
        _deviceChannels[index] = default;
        if (channel.handle == IntPtr.Zero) return;
        var result = channel.stop();
        if (result != RESULT.ERR_INVALID_HANDLE && result != RESULT.ERR_CHANNEL_STOLEN)
            Check(result, "Stopping device cue");
    }

    private void StopChannel()
    {
        if (_channel.handle == IntPtr.Zero) return;
        var channel = _channel;
        _channel = default;
        var result = channel.stop();
        // Completed or stolen one-shot channels no longer have a live handle.
        if (result != RESULT.ERR_INVALID_HANDLE && result != RESULT.ERR_CHANNEL_STOLEN)
            Check(result, "Stopping notification tone");
    }

    private void ClearHandles()
    {
        _channel = default;
        Array.Clear(_sounds);
        Array.Clear(_deviceSounds);
        Array.Clear(_deviceChannels);
        _systemHandle = IntPtr.Zero;
    }

    internal static void Check(RESULT result, string operation)
    {
        if (result != RESULT.OK) throw new InvalidOperationException(operation + ": " + result);
    }

    public void Dispose()
    {
        if (_disposed) return;
        if (Environment.CurrentManagedThreadId != _ownerThread)
        {
            Status = "Notification audio must be released on the Unity main thread.";
            return;
        }
        _disposed = true;
        try
        {
            if (_systemHandle == IntPtr.Zero || !TryGetSystem(out var system) || system.handle != _systemHandle)
                return;
            try { StopChannel(); }
            catch (Exception error) { Status = "Notification audio cleanup: " + error.Message; }
            for (var i = 0; i < _deviceChannels.Length; i++)
            {
                try { StopDeviceChannel(i); }
                catch (Exception error) { Status = "Device audio cleanup: " + error.Message; }
            }
            for (var i = 0; i < _deviceSounds.Length; i++)
            {
                if (_deviceSounds[i].handle == IntPtr.Zero) continue;
                try { Check(_deviceSounds[i].release(), "Releasing device cue"); }
                catch (Exception error) { Status = "Device audio cleanup: " + error.Message; }
            }
            for (var i = 0; i < _sounds.Length; i++)
            {
                if (_sounds[i].handle == IntPtr.Zero) continue;
                try { Check(_sounds[i].release(), "Releasing notification tone"); }
                catch (Exception error) { Status = "Notification audio cleanup: " + error.Message; }
            }
        }
        catch (Exception error) { Status = "Notification audio cleanup: " + error.Message; }
        finally
        {
            ClearHandles();
        }
    }
}
