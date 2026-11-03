// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Threading;

namespace Modstoke.Client;

// The simulated flight computer never receives Unity objects. Each computer has one
// replaceable sensor sample and one output; a single bounded worker services them all.
internal sealed class ClientDroneFirmware : IDisposable
{
    internal const float Mass = 1.65f;
    internal const float ArmX = .34f;
    internal const float ArmZ = .28f;
    internal const float CenterOfMassZ = -.025f;
    internal const float MaximumMotorForce = 10f;
    internal const float ReactionTorque = .035f;
    internal const float MinimumArmUp = .85f;
    private const float MaximumTilt = .38f;
    private readonly object _gate = new();
    private Sample _pending;
    private Output _output;
    private long _submitted;
    private long _processed;
    private bool _armed;
    private bool _launched;
    private LandingReason _landing;
    private bool _holding;
    private bool _disposed;
    private float _controlTime;
    private float _heading;
    private float _forwardTrim;
    private float _rightTrim;
    private float _holdX, _holdY, _holdZ, _holdHeading;
    private string _mode = "Disarmed";

    internal readonly record struct Sample(long At, float Time, bool Grounded, float Up, float Heading,
        float Pitch, float Roll, float PitchRate, float YawRate, float RollRate,
        float ForwardSpeed, float RightSpeed, float VerticalSpeed, float Gravity,
        float Charge, float Health, float Forward, float Right, float Climb, float Yaw, bool Fresh,
        float PositionX, float PositionY, float PositionZ);

    internal enum LandingReason { None, Manual, LinkLost, LowBattery, Damage, Computer }

    internal readonly record struct Output(long At, bool Armed, string Mode,
        float FrontLeft, float FrontRight, float RearLeft, float RearRight)
    {
        internal static Output Off(long at, string mode) => new(at, false, mode, 0, 0, 0, 0);
    }

    internal ClientDroneFirmware() => Worker.Add(this);

    internal bool Armed { get { lock (_gate) return _armed && !_disposed; } }
    internal string Mode { get { lock (_gate) return _mode; } }

    internal bool Arm(Sample sample)
    {
        lock (_gate)
        {
            if (_disposed || _armed || !sample.Grounded || sample.Up < MinimumArmUp ||
                sample.Charge < .15f || sample.Health < .35f) return false;
            _armed = true;
            _landing = LandingReason.None;
            _holding = _launched = false;
            _controlTime = sample.Time;
            _heading = sample.Heading;
            _forwardTrim = _rightTrim = 0;
            _mode = "Ready";
            _output = new Output(sample.At, true, _mode, 1.2f, 1.2f, 1.2f, 1.2f);
            _pending = sample;
            _processed = _submitted;
            return true;
        }
    }

    internal void Hold(Sample sample)
    {
        lock (_gate)
        {
            if (!_armed || _disposed || _holding || _landing != LandingReason.None) return;
            _holding = true;
            _holdX = sample.PositionX;
            _holdY = sample.PositionY;
            _holdZ = sample.PositionZ;
            _holdHeading = sample.Heading;
            _mode = _launched ? "Hovering" : "Ready";
            _pending = sample;
            _processed = _submitted;
            _output = default;
        }
    }

    internal bool ResumeControl(Sample sample)
    {
        lock (_gate)
        {
            if (_disposed) return false;
            if (!_armed) return true;
            if (_landing is not (LandingReason.None or LandingReason.Manual)
                || !sample.Fresh || sample.Charge < .12f || sample.Health < .35f || sample.Up < .15f)
                return false;
            _holding = false;
            _landing = LandingReason.None;
            _mode = _launched ? "Flying" : "Ready";
            _pending = sample;
            _processed = _submitted;
            _output = default;
            return true;
        }
    }

    internal void Land(LandingReason reason = LandingReason.Manual)
    {
        lock (_gate)
        {
            if (!_armed || _disposed) return;
            StartLanding(reason);
            // Invalidate a previously computed climb command immediately.
            _output = default;
        }
    }

    private void StartLanding(LandingReason reason)
    {
        // A manual request cannot clear a latched safety landing.
        if (reason == LandingReason.Manual && _landing is not (LandingReason.None or LandingReason.Manual)) return;
        _landing = reason;
        _holding = false;
        _mode = reason switch
        {
            LandingReason.LowBattery => "Low battery - landing",
            LandingReason.Damage => "Damage - landing",
            LandingReason.LinkLost => "Link lost - landing",
            LandingReason.Computer => "Computer stale - landing",
            _ => "Landing"
        };
    }

    internal void Stop()
    {
        lock (_gate)
        {
            _armed = _launched = _holding = false;
            _landing = LandingReason.None;
            _forwardTrim = _rightTrim = 0;
            _mode = "Disarmed";
            _output = Output.Off(Environment.TickCount64, _mode);
        }
    }

    internal Output Tick(Sample sample)
    {
        Output result;
        lock (_gate)
        {
            if (_disposed) return Output.Off(sample.At, "Offline");
            _pending = sample;
            _submitted++;
            var outputStale = _output.At != 0 && sample.At - _output.At > 200;
            // A stalled worker cannot leave a climb or bank command latched. The
            // same controller runs locally in landing mode until touchdown.
            if (_output.At == 0 || outputStale || !sample.Fresh ||
                sample.Charge < .12f || sample.Health < .35f)
            {
                if (_armed && (outputStale || !sample.Fresh))
                    StartLanding(!sample.Fresh ? LandingReason.LinkLost : LandingReason.Computer);
                _output = Calculate(sample);
                _processed = _submitted;
            }
            result = _output;
        }
        Worker.Wake();
        return result;
    }

    private void Process()
    {
        lock (_gate)
        {
            if (_disposed || _submitted == _processed) return;
            _output = Calculate(_pending);
            _processed = _submitted;
        }
    }

    private Output Calculate(Sample s)
    {
        if (!_armed) return Output.Off(s.At, _mode);
        if (s.Health <= 0 || s.Charge <= .0001f || s.Up < .15f)
        {
            _armed = false;
            _mode = s.Health <= 0 ? "Damaged" : s.Charge <= .0001f ? "Battery empty" : "Overturned";
            return Output.Off(s.At, _mode);
        }
        if (s.Charge < .12f || s.Health < .35f)
            StartLanding(s.Charge < .12f ? LandingReason.LowBattery : LandingReason.Damage);
        if (!s.Fresh || Environment.TickCount64 - s.At > 200)
            StartLanding(!s.Fresh ? LandingReason.LinkLost : LandingReason.Computer);
        var landing = _landing != LandingReason.None;
        if (landing && s.Grounded && MathF.Abs(s.VerticalSpeed) < .9f)
        {
            _armed = false;
            _mode = "Landed";
            return Output.Off(s.At, _mode);
        }
        if (!_launched && s.Grounded && (_holding || s.Climb <= .08f) && !landing)
        {
            _mode = "Ready";
            return new Output(s.At, true, _mode, 1.2f, 1.2f, 1.2f, 1.2f);
        }

        _launched = true;
        if (!landing) _mode = _holding ? "Hovering" : "Flying";
        // The worker may replace intermediate samples; integrate elapsed physics time.
        var dt = Math.Clamp(s.Time - _controlTime, 0, .1f);
        _controlTime = s.Time;
        // Keep wind compensation fixed in world space while the pilot turns.
        var headingChange = s.Heading - _heading;
        _heading = s.Heading;
        var cosine = MathF.Cos(headingChange);
        var sine = MathF.Sin(headingChange);
        var forwardTrim = _forwardTrim * cosine + _rightTrim * sine;
        _rightTrim = _rightTrim * cosine - _forwardTrim * sine;
        _forwardTrim = forwardTrim;
        if (s.Grounded) _forwardTrim = _rightTrim = 0;
        var forwardTarget = landing || _holding ? 0 : Unit(s.Forward) * 7;
        var rightTarget = landing || _holding ? 0 : Unit(s.Right) * 7;
        if (_holding)
        {
            var dx = _holdX - s.PositionX;
            var dz = _holdZ - s.PositionZ;
            var headingSine = MathF.Sin(s.Heading);
            var headingCosine = MathF.Cos(s.Heading);
            forwardTarget = Math.Clamp((dx * headingSine + dz * headingCosine) * .8f, -3, 3);
            rightTarget = Math.Clamp((dx * headingCosine - dz * headingSine) * .8f, -3, 3);
        }
        var forwardError = forwardTarget - s.ForwardSpeed;
        var rightError = rightTarget - s.RightSpeed;
        if (!s.Grounded)
        {
            IntegrateSpeed(ref _forwardTrim, forwardError, dt);
            IntegrateSpeed(ref _rightTrim, rightError, dt);
        }
        var pitch = Math.Clamp(forwardError * .09f + _forwardTrim, -MaximumTilt, MaximumTilt);
        var roll = -Math.Clamp(rightError * .09f + _rightTrim, -MaximumTilt, MaximumTilt);
        var pitchRate = Math.Clamp((pitch - s.Pitch) * 4.5f, -1.5f, 1.5f);
        var rollRate = Math.Clamp((roll - s.Roll) * 4.5f, -1.5f, 1.5f);
        var yawRate = landing ? 0 : _holding
            ? Math.Clamp(MathF.IEEERemainder(_holdHeading - s.Heading, MathF.Tau) * 1.6f, -.6f, .6f)
            : Unit(s.Yaw) * 1.1f;
        var pitchTorque = Math.Clamp((pitchRate - s.PitchRate) * .72f, -1.1f, 1.1f);
        var rollTorque = Math.Clamp((rollRate - s.RollRate) * .72f, -1.1f, 1.1f);
        var yawTorque = Math.Clamp((yawRate - s.YawRate) * .28f, -.30f, .30f);
        var verticalTarget = landing ? -.65f : _holding
            ? Math.Clamp((_holdY - s.PositionY) * .8f, -1.5f, 1.5f) : Unit(s.Climb) * 3;
        var acceleration = Math.Clamp((verticalTarget - s.VerticalSpeed) * 3.5f, -5, 6);
        var lift = Mass * (Math.Clamp(s.Gravity, 1, 20) + acceleration) / Math.Max(.55f, s.Up);
        var collective = Math.Clamp(lift / 4, 0, MaximumMotorForce - 1.8f);
        // Collective thrust acts ahead of the battery-shifted centre of mass.
        // Cancel that moment before mixing the requested attitude torque.
        var pitchMix = (pitchTorque - CenterOfMassZ * 4 * collective) / (4 * ArmZ);
        var rollMix = rollTorque / (4 * ArmX);
        var yawMix = yawTorque / (4 * ReactionTorque);
        // r x F: front motors give negative X torque; left motors give negative Z.
        // Opposite diagonals counter-rotate, supplying physical reaction torque.
        return new Output(s.At, true, _mode,
            Motor(collective - pitchMix - rollMix + yawMix),
            Motor(collective - pitchMix + rollMix - yawMix),
            Motor(collective + pitchMix - rollMix - yawMix),
            Motor(collective + pitchMix + rollMix + yawMix));
    }

    private static void IntegrateSpeed(ref float trim, float error, float dt)
    {
        var tilt = error * .09f + trim;
        // Persistent wind needs trim, but an unreachable speed must not wind it up.
        if (tilt >= MaximumTilt && error > 0 || tilt <= -MaximumTilt && error < 0) return;
        trim = Math.Clamp(trim + error * .03f * dt, -MaximumTilt, MaximumTilt);
    }

    private static float Unit(float value) => float.IsFinite(value) ? Math.Clamp(value, -1, 1) : 0;
    private static float Motor(float value) => Math.Clamp(value, 0, MaximumMotorForce);

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _armed = false;
            _mode = "Offline";
        }
        Worker.Remove(this);
    }

    private static class Worker
    {
        private static readonly object Gate = new();
        private static readonly List<ClientDroneFirmware> Computers = new();
        private static readonly AutoResetEvent Signal = new(false);
        private static Thread? _thread;

        internal static void Add(ClientDroneFirmware firmware)
        {
            lock (Gate)
            {
                if (Computers.Count >= 32) throw new InvalidOperationException("The drone flight computer limit has been reached.");
                Computers.Add(firmware);
                if (_thread == null)
                {
                    _thread = new Thread(Run) { IsBackground = true, Name = "Modstoke drone computers" };
                    _thread.Start();
                }
            }
            Signal.Set();
        }

        internal static void Wake() => Signal.Set();
        internal static void Remove(ClientDroneFirmware firmware)
        {
            lock (Gate) Computers.Remove(firmware);
            Signal.Set();
        }

        private static void Run()
        {
            var batch = new ClientDroneFirmware[32];
            while (true)
            {
                Signal.WaitOne(20);
                int count;
                lock (Gate)
                {
                    count = Computers.Count;
                    if (count == 0) { _thread = null; return; }
                    Computers.CopyTo(batch);
                }
                for (var i = 0; i < count; i++)
                {
                    try { batch[i].Process(); }
                    catch { batch[i].Land(LandingReason.Computer); }
                    batch[i] = null!;
                }
            }
        }
    }
}
