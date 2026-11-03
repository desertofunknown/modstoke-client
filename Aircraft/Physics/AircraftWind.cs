// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Modstoke.Sons;
using UnityEngine;

namespace Modstoke.Client;

// Authored atmospheric velocity, in world metres/second and moving-to direction.
// Native precipitation modulates this field; the SDK does not supply measured wind.
// Only the authoritative aircraft advances it, on the Unity physics thread.
internal sealed class ClientAircraftWind
{
    private readonly Eddy[] _eddies =
    {
        new(new(.8f, .1f, .6f), Vector3.up, 170, .50f, .071f, .4),
        new(new(-.3f, .2f, .93f), Vector3.right, 62, .28f, -.113f, 2.7),
        new(new(.6f, -.5f, -.62f), Vector3.forward, 24, .15f, .193f, 4.3),
        new(new(-.7f, .4f, .59f), Vector3.right, 11, .07f, -.271f, 1.2)
    };
    private double _time;
    private float _surfaceAltitude;
    private float _targetSurfaceAltitude;
    private float _terrainDelay;
    private Il2CppStructArray<RaycastHit>? _surfaceRaycastHits;
    private bool _terrainInitialized;
    private long _nextWeatherRead;
    private float _weatherTarget;
    private float _weather;
    private float _fade;
    private float _gust;
    private Vector3 _direction;
    private float _speedAtTenMetres;

    internal void Advance(float dt, Rigidbody aircraft)
    {
        _time += dt;
        ReadWeather();
        _weather += (_weatherTarget - _weather) * (1 - MathF.Exp(-dt / 12));
        // Deployment and weather changes must not kick a stationary airframe.
        _fade = (float)(1 - Math.Exp(-_time / 5));
        var heading = .7 + .28 * Math.Sin(_time / 190) + .15 * Math.Sin(_time / 61 + 1.3);
        _direction = new Vector3((float)Math.Sin(heading), 0, (float)Math.Cos(heading));
        _speedAtTenMetres = 3.5f + 5.5f * _weather;
        _gust = (.8f + 1.8f * _weather) * (float)(.65 * Math.Sin(_time * .17 + .6)
            + .35 * Math.Sin(_time * .43 + 2.1));

        UpdateSurface(dt, aircraft);
        var advection = _direction * (_speedAtTenMetres * _fade);
        for (var i = 0; i < _eddies.Length; i++)
            _eddies[i].Advance(advection, dt);
    }

    internal Vector3 Sample(Vector3 position)
    {
        var height = MathF.Max(0, position.y - _surfaceAltitude);
        // A bounded 1/7-power surface-layer approximation, referenced at 10 m
        // AGL. Above 200 m retain the free-stream value instead of unlimited shear.
        var shear = MathF.Pow(Math.Clamp(height, .2f, 200) / 10, 1f / 7);
        var turbulence = Vector3.zero;
        for (var i = 0; i < _eddies.Length; i++) turbulence += _eddies[i].Sample(position);
        turbulence *= .9f + 1.6f * _weather;
        // Vertical flow tends to zero at the sampled surface, without switching
        // the wind off on touchdown or making it depend on aircraft attitude.
        var vertical = Math.Clamp(height / 10, 0, 1);
        turbulence.y *= vertical * vertical * (3 - 2 * vertical);
        return (_direction * ((_speedAtTenMetres + _gust) * shear) + turbulence) * _fade;
    }

    private void ReadWeather()
    {
        var now = Environment.TickCount64;
        if (now < _nextWeatherRead) return;
        _nextWeatherRead = now + 1000;
        // Missing or unknown observations relax to the fair-weather preset;
        // they are not evidence that the game's weather is actually calm.
        _weatherTarget = 0;
        if (!GameRuntime.State.IsInWorld
            || !EnvironmentReads.TryReadWeather(out var weather, out _) || weather == null) return;
        _weatherTarget = weather.Precipitation switch
        {
            Precipitation.Light => .25f,
            Precipitation.Medium => .6f,
            Precipitation.Heavy => 1,
            _ => 0
        };
    }

    private void UpdateSurface(float dt, Rigidbody aircraft)
    {
        _terrainDelay -= dt;
        if (_terrainDelay <= 0)
        {
            _terrainDelay = .5f;
            var origin = aircraft.worldCenterOfMass;
            var nearest = 1000f;
            var hits = _surfaceRaycastHits ??= new Il2CppStructArray<RaycastHit>(size: 32);
            var hitCount = Physics.RaycastNonAlloc(origin, Vector3.down, hits, nearest, ~0, QueryTriggerInteraction.Ignore);
            if (hitCount == hits.Length)
            {
                // A full buffer can omit the nearest eligible hit. Preserve the complete query.
                hits = Physics.RaycastAll(origin, Vector3.down, nearest, ~0, QueryTriggerInteraction.Ignore);
                hitCount = hits.Length;
            }
            var root = aircraft.transform;
            for (var i = 0; i < hitCount; i++)
            {
                var hit = hits[i];
                var collider = hit.collider;
                if (!collider || collider.attachedRigidbody == aircraft
                    || collider.transform.IsChildOf(root) || hit.normal.y < .35f) continue;
                // Moving occupants/vehicles must not become the atmospheric ground.
                if (collider.attachedRigidbody) continue;
                nearest = MathF.Min(nearest, hit.distance);
            }
            _targetSurfaceAltitude = origin.y - nearest;
            if (!_terrainInitialized)
            {
                _surfaceAltitude = _targetSurfaceAltitude;
                _terrainInitialized = true;
            }
        }
        _surfaceAltitude += (_targetSurfaceAltitude - _surfaceAltitude) * (1 - MathF.Exp(-dt / 1.5f));
    }

    private struct Eddy
    {
        private readonly Vector3 _wave;
        private readonly Vector3 _polarization;
        private readonly float _evolution;
        private double _phase;

        internal Eddy(Vector3 direction, Vector3 reference, float wavelength, float weight,
            float evolution, double phase)
        {
            _wave = direction.normalized * (2 * MathF.PI / wavelength);
            // Perpendicular wave and velocity directions produce a solenoidal
            // eddy before the near-ground envelope. Weights sum to one, bounding
            // the gust field without clipping samples or generating impulses.
            _polarization = Vector3.Cross(direction, reference).normalized * weight;
            _evolution = evolution;
            _phase = phase;
        }

        internal void Advance(Vector3 advection, float dt) =>
            _phase = (_phase + (_evolution - Vector3.Dot(_wave, advection)) * dt) % (2 * Math.PI);

        internal readonly Vector3 Sample(Vector3 position) =>
            _polarization * (float)Math.Sin(Vector3.Dot(_wave, position) + _phase);
    }
}
